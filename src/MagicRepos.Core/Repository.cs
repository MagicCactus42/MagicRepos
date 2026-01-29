using System.Text;
using MagicRepos.Core.Config;
using MagicRepos.Core.Diff;
using MagicRepos.Core.Ignore;
using MagicRepos.Core.Objects;
using MagicRepos.Core.Refs;
using MagicRepos.Core.Storage;
using FileMode = MagicRepos.Core.Objects.FileMode;

namespace MagicRepos.Core;

public enum ResetMode { Soft, Mixed, Hard }

public enum FileStatusType { Added, Modified, Deleted, Untracked }

public record FileStatus(string Path, FileStatusType Status);

public record RepositoryStatus(
    IReadOnlyList<FileStatus> StagedChanges,
    IReadOnlyList<FileStatus> UnstagedChanges,
    IReadOnlyList<string> UntrackedFiles);

public class Repository
{
    private const string MagicReposDirName = ".magicrepos";
    private const string HeadDefaultContent = "ref: refs/heads/main";
    private const string IndexFileName = "index";

    public string WorkingDirectory { get; }
    public string MagicReposDirectory { get; }
    public ObjectStore ObjectStore { get; }
    public RefStore Refs { get; }
    public RepoConfig Config { get; }

    private Repository(string workingDir)
    {
        WorkingDirectory = Path.GetFullPath(workingDir);
        MagicReposDirectory = Path.Combine(WorkingDirectory, MagicReposDirName);
        ObjectStore = new ObjectStore(MagicReposDirectory);
        Refs = new RefStore(MagicReposDirectory);
        Config = new RepoConfig(Path.Combine(MagicReposDirectory, "config"));
        Config.Load();
    }

    // ══════════════════════════════════════════════════════════════
    //  Static factory methods
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Initializes a new repository at the given path (or the current directory).
    /// Creates the <c>.magicrepos</c> directory structure and returns the opened repository.
    /// </summary>
    public static Repository Init(string? path = null)
    {
        string workDir = Path.GetFullPath(path ?? Directory.GetCurrentDirectory());

        string magicDir = Path.Combine(workDir, MagicReposDirName);
        if (Directory.Exists(magicDir))
            throw new InvalidOperationException($"Repository already exists at '{workDir}'.");

        // Create directory structure
        Directory.CreateDirectory(Path.Combine(magicDir, "objects"));
        Directory.CreateDirectory(Path.Combine(magicDir, "refs", "heads"));
        Directory.CreateDirectory(Path.Combine(magicDir, "refs", "tags"));
        Directory.CreateDirectory(Path.Combine(magicDir, "refs", "remotes"));

        // Write initial HEAD pointing to main branch
        File.WriteAllText(Path.Combine(magicDir, "HEAD"), HeadDefaultContent + "\n");

        // Write default config
        var config = new RepoConfig(Path.Combine(magicDir, "config"));
        config.Set("core", "repositoryformatversion", "0");
        config.Set("core", "bare", "false");
        config.Save();

        return new Repository(workDir);
    }

    /// <summary>
    /// Opens an existing repository by searching up the directory tree from the given
    /// path (or the current directory) for a <c>.magicrepos</c> folder.
    /// </summary>
    public static Repository Open(string? path = null)
    {
        string startDir = Path.GetFullPath(path ?? Directory.GetCurrentDirectory());
        string? current = startDir;

        while (current is not null)
        {
            string candidate = Path.Combine(current, MagicReposDirName);
            if (Directory.Exists(candidate))
                return new Repository(current);

            current = Path.GetDirectoryName(current);
        }

        throw new InvalidOperationException(
            $"Not a MagicRepos repository (or any parent up to root): '{startDir}'");
    }

    /// <summary>
    /// Returns <see langword="true"/> if the given path contains a <c>.magicrepos</c> directory.
    /// </summary>
    public static bool IsRepository(string path)
    {
        return Directory.Exists(Path.Combine(Path.GetFullPath(path), MagicReposDirName));
    }

    // ══════════════════════════════════════════════════════════════
    //  Staging
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Stages a single file by reading its contents, creating a blob object,
    /// and updating the index entry with current file metadata.
    /// </summary>
    public void StageFile(string relativePath)
    {
        // Normalize to forward slashes for internal storage
        string normalizedPath = relativePath.Replace('\\', '/');
        string fullPath = Path.Combine(WorkingDirectory, normalizedPath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(fullPath))
        {
            // File was deleted -- remove from index
            IndexFile index = LoadIndex();
            index.Remove(normalizedPath);
            SaveIndex(index);
            return;
        }

        byte[] content = File.ReadAllBytes(fullPath);
        ObjectId blobId = StoreBlob(content);

        var fileInfo = new FileInfo(fullPath);
        var modTime = fileInfo.LastWriteTimeUtc;
        long mtimeSeconds = new DateTimeOffset(modTime).ToUnixTimeSeconds();

        var entry = new IndexEntry
        {
            ModifiedTimeSeconds = mtimeSeconds,
            ModifiedTimeNanoseconds = 0,
            FileSize = (int)fileInfo.Length,
            ObjectId = blobId,
            Flags = (ushort)Math.Min(normalizedPath.Length, 0xFFF),
            Path = normalizedPath
        };

        IndexFile idx = LoadIndex();
        idx.AddOrUpdate(entry);
        SaveIndex(idx);
    }

    /// <summary>
    /// Stages all non-ignored files in the working tree. Also removes index entries
    /// for files that have been deleted from the working tree.
    /// </summary>
    public void StageAll()
    {
        IgnoreRuleSet ignoreRules = LoadIgnoreRules();
        var workingTree = new WorkingTree(WorkingDirectory, ignoreRules);

        IReadOnlyList<string> files = workingTree.GetFiles();
        IndexFile index = LoadIndex();

        // Track which paths are still present in the working tree
        var presentPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (string relativePath in files)
        {
            string normalizedPath = relativePath.Replace('\\', '/');
            presentPaths.Add(normalizedPath);

            string fullPath = Path.Combine(WorkingDirectory,
                normalizedPath.Replace('/', Path.DirectorySeparatorChar));

            byte[] content = File.ReadAllBytes(fullPath);
            ObjectId blobId = StoreBlob(content);

            var fileInfo = new FileInfo(fullPath);
            var modTime = fileInfo.LastWriteTimeUtc;
            long mtimeSeconds = new DateTimeOffset(modTime).ToUnixTimeSeconds();

            var entry = new IndexEntry
            {
                ModifiedTimeSeconds = mtimeSeconds,
                ModifiedTimeNanoseconds = 0,
                FileSize = (int)fileInfo.Length,
                ObjectId = blobId,
                Flags = (ushort)Math.Min(normalizedPath.Length, 0xFFF),
                Path = normalizedPath
            };

            index.AddOrUpdate(entry);
        }

        // Remove entries for deleted files
        var toRemove = index.Entries
            .Where(e => !presentPaths.Contains(e.Path))
            .Select(e => e.Path)
            .ToList();

        foreach (string path in toRemove)
        {
            index.Remove(path);
        }

        SaveIndex(index);
    }

    // ══════════════════════════════════════════════════════════════
    //  Commit
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a commit from the current index. Builds a tree hierarchy from the
    /// flat index entries, creates the commit object, stores everything in the
    /// object store, and updates the HEAD reference.
    /// </summary>
    public ObjectId CreateCommit(string message, Signature? author = null)
    {
        IndexFile index = LoadIndex();
        if (index.Entries.Count == 0)
            throw new InvalidOperationException("Nothing to commit: the index is empty.");

        // Build tree from index entries
        ObjectId treeId = BuildTree(index.Entries);

        // Determine author/committer
        Signature sig = author ?? GetDefaultSignature();

        // Determine parents
        var parents = new List<ObjectId>();
        ObjectId? headCommit = Refs.ResolveHead();
        if (headCommit is not null)
            parents.Add(headCommit.Value);

        // Create commit object
        var commit = new CommitObject(treeId, parents, sig, sig, message);
        ObjectId commitId = StoreCommit(commit);

        // Update HEAD ref
        string? branchName = Refs.GetCurrentBranchName();
        if (branchName is not null)
        {
            Refs.CreateBranch(branchName, commitId);
        }
        else
        {
            // Detached HEAD -- write raw hash
            Refs.WriteHead(commitId.ToHexString());
        }

        return commitId;
    }

    /// <summary>
    /// Builds a tree hierarchy from a flat list of index entries. Handles nested
    /// directories by recursively building subtrees for each directory level.
    /// </summary>
    private ObjectId BuildTree(IReadOnlyList<IndexEntry> entries)
    {
        return BuildTreeFromEntries(entries, "");
    }

    private ObjectId BuildTreeFromEntries(IReadOnlyList<IndexEntry> entries, string prefix)
    {
        // Group entries by their top-level component under this prefix
        var directChildren = new List<TreeEntry>();
        var subdirectories = new Dictionary<string, List<IndexEntry>>(StringComparer.Ordinal);

        foreach (IndexEntry entry in entries)
        {
            string relativePath;
            if (prefix.Length == 0)
            {
                relativePath = entry.Path;
            }
            else if (entry.Path.StartsWith(prefix, StringComparison.Ordinal))
            {
                relativePath = entry.Path[prefix.Length..];
            }
            else
            {
                continue; // not under this prefix
            }

            int slashIndex = relativePath.IndexOf('/');
            if (slashIndex < 0)
            {
                // Direct child file
                directChildren.Add(new TreeEntry(FileMode.Regular, relativePath, entry.ObjectId));
            }
            else
            {
                // Belongs in a subdirectory
                string dirName = relativePath[..slashIndex];
                if (!subdirectories.ContainsKey(dirName))
                    subdirectories[dirName] = new List<IndexEntry>();

                subdirectories[dirName].Add(entry);
            }
        }

        // Recursively build subtrees
        foreach ((string dirName, List<IndexEntry> subEntries) in subdirectories)
        {
            string subPrefix = prefix.Length == 0 ? dirName + "/" : prefix + dirName + "/";
            ObjectId subTreeId = BuildTreeFromEntries(subEntries, subPrefix);
            directChildren.Add(new TreeEntry(FileMode.Directory, dirName, subTreeId));
        }

        var tree = new TreeObject(directChildren);
        StoreTree(tree);
        return tree.Id;
    }

    // ══════════════════════════════════════════════════════════════
    //  Status
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Compares the index against HEAD (staged changes), the working tree against
    /// the index (unstaged changes), and detects untracked files.
    /// </summary>
    public RepositoryStatus GetStatus()
    {
        IndexFile index = LoadIndex();
        IgnoreRuleSet ignoreRules = LoadIgnoreRules();
        var workingTree = new WorkingTree(WorkingDirectory, ignoreRules);

        // Build lookup of index entries by path
        var indexByPath = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
        foreach (IndexEntry entry in index.Entries)
        {
            indexByPath[entry.Path] = entry;
        }

        // === Staged changes: compare index vs HEAD tree ===
        var stagedChanges = new List<FileStatus>();
        var headEntries = new Dictionary<string, ObjectId>(StringComparer.Ordinal);

        ObjectId? headTreeId = GetHeadTreeId();
        if (headTreeId is not null)
        {
            foreach ((string path, ObjectId id) in ReadTreeRecursive(headTreeId.Value))
            {
                headEntries[path] = id;
            }
        }

        // Files in index but not in HEAD -> staged added
        // Files in index and HEAD but different -> staged modified
        foreach (IndexEntry entry in index.Entries)
        {
            if (!headEntries.TryGetValue(entry.Path, out ObjectId headId))
            {
                stagedChanges.Add(new FileStatus(entry.Path, FileStatusType.Added));
            }
            else if (entry.ObjectId != headId)
            {
                stagedChanges.Add(new FileStatus(entry.Path, FileStatusType.Modified));
            }
        }

        // Files in HEAD but not in index -> staged deleted
        foreach (string headPath in headEntries.Keys)
        {
            if (!indexByPath.ContainsKey(headPath))
            {
                stagedChanges.Add(new FileStatus(headPath, FileStatusType.Deleted));
            }
        }

        // === Unstaged changes: compare working tree vs index ===
        var unstagedChanges = new List<FileStatus>();
        var untrackedFiles = new List<string>();

        IReadOnlyList<string> workingFiles = workingTree.GetFiles();
        var workingFilesSet = new HashSet<string>(StringComparer.Ordinal);

        foreach (string file in workingFiles)
        {
            string normalizedPath = file.Replace('\\', '/');
            workingFilesSet.Add(normalizedPath);

            if (!indexByPath.TryGetValue(normalizedPath, out IndexEntry? indexEntry))
            {
                untrackedFiles.Add(normalizedPath);
            }
            else
            {
                // Check if file content differs from index
                string fullPath = Path.Combine(WorkingDirectory,
                    normalizedPath.Replace('/', Path.DirectorySeparatorChar));
                byte[] currentContent = File.ReadAllBytes(fullPath);
                ObjectId currentBlobId = ObjectSerializer.ComputeId(ObjectType.Blob, currentContent);

                if (currentBlobId != indexEntry.ObjectId)
                {
                    unstagedChanges.Add(new FileStatus(normalizedPath, FileStatusType.Modified));
                }
            }
        }

        // Files in index but not in working tree -> unstaged deleted
        foreach (IndexEntry entry in index.Entries)
        {
            if (!workingFilesSet.Contains(entry.Path))
            {
                unstagedChanges.Add(new FileStatus(entry.Path, FileStatusType.Deleted));
            }
        }

        return new RepositoryStatus(stagedChanges, unstagedChanges, untrackedFiles);
    }

    // ══════════════════════════════════════════════════════════════
    //  Log
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Walks the commit chain from HEAD backwards and returns up to
    /// <paramref name="maxCount"/> commits.
    /// </summary>
    public IReadOnlyList<CommitObject> GetLog(int maxCount = int.MaxValue)
    {
        var commits = new List<CommitObject>();

        ObjectId? current = Refs.ResolveHead();
        int count = 0;

        while (current is not null && count < maxCount)
        {
            CommitObject commit = ReadCommit(current.Value);
            commits.Add(commit);
            count++;

            // Follow first parent (linear history)
            current = commit.Parents.Count > 0 ? commit.Parents[0] : null;
        }

        return commits;
    }

    // ══════════════════════════════════════════════════════════════
    //  Diff
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Computes diffs between the working tree and the index (unstaged changes).
    /// </summary>
    public IReadOnlyList<DiffResult> DiffWorkingTree()
    {
        IndexFile index = LoadIndex();
        IgnoreRuleSet ignoreRules = LoadIgnoreRules();
        var workingTree = new WorkingTree(WorkingDirectory, ignoreRules);
        var results = new List<DiffResult>();

        foreach (IndexEntry entry in index.Entries)
        {
            string fullPath = Path.Combine(WorkingDirectory,
                entry.Path.Replace('/', Path.DirectorySeparatorChar));

            string oldText;
            string newText;

            if (!File.Exists(fullPath))
            {
                // File deleted from working tree
                byte[] indexContent = ReadBlobContent(entry.ObjectId);
                oldText = Encoding.UTF8.GetString(indexContent);
                newText = string.Empty;
            }
            else
            {
                byte[] currentContent = File.ReadAllBytes(fullPath);
                ObjectId currentBlobId = ObjectSerializer.ComputeId(ObjectType.Blob, currentContent);

                if (currentBlobId == entry.ObjectId)
                    continue; // No changes

                byte[] indexContent = ReadBlobContent(entry.ObjectId);
                oldText = Encoding.UTF8.GetString(indexContent);
                newText = Encoding.UTF8.GetString(currentContent);
            }

            DiffResult diff = DiffEngine.Diff(oldText, newText, entry.Path, entry.Path);
            if (diff.HasChanges)
                results.Add(diff);
        }

        return results;
    }

    /// <summary>
    /// Computes diffs between the index and the HEAD commit (staged changes).
    /// </summary>
    public IReadOnlyList<DiffResult> DiffIndex()
    {
        IndexFile index = LoadIndex();
        var results = new List<DiffResult>();

        var headEntries = new Dictionary<string, ObjectId>(StringComparer.Ordinal);
        ObjectId? headTreeId = GetHeadTreeId();
        if (headTreeId is not null)
        {
            foreach ((string path, ObjectId id) in ReadTreeRecursive(headTreeId.Value))
            {
                headEntries[path] = id;
            }
        }

        var indexByPath = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
        foreach (IndexEntry entry in index.Entries)
        {
            indexByPath[entry.Path] = entry;
        }

        // Files in index: compare against HEAD
        foreach (IndexEntry entry in index.Entries)
        {
            string oldText;
            string newText;

            if (!headEntries.TryGetValue(entry.Path, out ObjectId headBlobId))
            {
                // New file (added)
                oldText = string.Empty;
                byte[] indexContent = ReadBlobContent(entry.ObjectId);
                newText = Encoding.UTF8.GetString(indexContent);
            }
            else if (entry.ObjectId != headBlobId)
            {
                // Modified
                byte[] headContent = ReadBlobContent(headBlobId);
                byte[] indexContent = ReadBlobContent(entry.ObjectId);
                oldText = Encoding.UTF8.GetString(headContent);
                newText = Encoding.UTF8.GetString(indexContent);
            }
            else
            {
                continue; // Unchanged
            }

            DiffResult diff = DiffEngine.Diff(oldText, newText, entry.Path, entry.Path);
            if (diff.HasChanges)
                results.Add(diff);
        }

        // Files in HEAD but not in index (deleted)
        foreach ((string path, ObjectId headBlobId) in headEntries)
        {
            if (indexByPath.ContainsKey(path))
                continue;

            byte[] headContent = ReadBlobContent(headBlobId);
            string oldText = Encoding.UTF8.GetString(headContent);

            DiffResult diff = DiffEngine.Diff(oldText, string.Empty, path, path);
            if (diff.HasChanges)
                results.Add(diff);
        }

        return results;
    }

    // ══════════════════════════════════════════════════════════════
    //  Branch operations
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a new branch pointing at the current HEAD commit.
    /// </summary>
    public void CreateBranch(string name)
    {
        ObjectId? headCommit = Refs.ResolveHead();
        if (headCommit is null)
            throw new InvalidOperationException("Cannot create branch: no commits exist yet.");

        if (Refs.ResolveBranch(name) is not null)
            throw new InvalidOperationException($"Branch '{name}' already exists.");

        Refs.CreateBranch(name, headCommit.Value);
    }

    /// <summary>
    /// Deletes a branch. Cannot delete the currently checked-out branch.
    /// </summary>
    public void DeleteBranch(string name)
    {
        string? currentBranch = Refs.GetCurrentBranchName();
        if (string.Equals(currentBranch, name, StringComparison.Ordinal))
            throw new InvalidOperationException($"Cannot delete the currently checked-out branch '{name}'.");

        if (Refs.ResolveBranch(name) is null)
            throw new InvalidOperationException($"Branch '{name}' not found.");

        Refs.DeleteBranch(name);
    }

    /// <summary>
    /// Lists all branch names.
    /// </summary>
    public IReadOnlyList<string> ListBranches()
    {
        return Refs.ListBranches();
    }

    // ══════════════════════════════════════════════════════════════
    //  Checkout
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Checks out a branch by resolving it to a commit, updating the working tree
    /// and index to match the commit's tree, and pointing HEAD at the branch.
    /// </summary>
    public void CheckoutBranch(string branchName)
    {
        ObjectId? commitId = Refs.ResolveBranch(branchName);
        if (commitId is null)
            throw new InvalidOperationException($"Branch '{branchName}' does not exist.");

        CommitObject commit = ReadCommit(commitId.Value);
        List<(string Path, ObjectId Id)> treeEntries = ReadTreeRecursive(commit.TreeId);

        // Clear existing tracked files from working tree
        IndexFile currentIndex = LoadIndex();
        foreach (IndexEntry entry in currentIndex.Entries)
        {
            string fullPath = Path.Combine(WorkingDirectory,
                entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath))
                File.Delete(fullPath);

            // Clean up empty parent directories
            CleanEmptyDirectories(Path.GetDirectoryName(fullPath));
        }

        // Write files from the commit's tree
        IndexFile newIndex = IndexFile.CreateEmpty();

        foreach ((string path, ObjectId blobId) in treeEntries)
        {
            string fullPath = Path.Combine(WorkingDirectory,
                path.Replace('/', Path.DirectorySeparatorChar));

            string? dir = Path.GetDirectoryName(fullPath);
            if (dir is not null)
                Directory.CreateDirectory(dir);

            byte[] content = ReadBlobContent(blobId);
            File.WriteAllBytes(fullPath, content);

            var fileInfo = new FileInfo(fullPath);
            long mtimeSeconds = new DateTimeOffset(fileInfo.LastWriteTimeUtc).ToUnixTimeSeconds();

            newIndex.AddOrUpdate(new IndexEntry
            {
                ModifiedTimeSeconds = mtimeSeconds,
                ModifiedTimeNanoseconds = 0,
                FileSize = (int)fileInfo.Length,
                ObjectId = blobId,
                Flags = (ushort)Math.Min(path.Length, 0xFFF),
                Path = path
            });
        }

        SaveIndex(newIndex);

        // Update HEAD to point to the branch
        Refs.WriteHead($"ref: refs/heads/{branchName}");
    }

    // ══════════════════════════════════════════════════════════════
    //  Reset
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Resets the repository to the given target (a ref, branch name, or commit hash).
    /// <list type="bullet">
    ///   <item><see cref="ResetMode.Soft"/>: moves HEAD only.</item>
    ///   <item><see cref="ResetMode.Mixed"/>: moves HEAD and resets the index.</item>
    ///   <item><see cref="ResetMode.Hard"/>: moves HEAD, resets the index, and resets the working tree.</item>
    /// </list>
    /// </summary>
    public void Reset(string target, ResetMode mode)
    {
        ObjectId? commitId = Refs.Resolve(target);
        if (commitId is null)
            throw new InvalidOperationException($"Cannot resolve target '{target}' to a commit.");

        CommitObject commit = ReadCommit(commitId.Value);

        // Move HEAD
        string? branchName = Refs.GetCurrentBranchName();
        if (branchName is not null)
        {
            Refs.CreateBranch(branchName, commitId.Value);
        }
        else
        {
            Refs.WriteHead(commitId.Value.ToHexString());
        }

        if (mode == ResetMode.Soft)
            return;

        // Reset index from the commit's tree
        List<(string Path, ObjectId Id)> treeEntries = ReadTreeRecursive(commit.TreeId);
        IndexFile newIndex = IndexFile.CreateEmpty();

        foreach ((string path, ObjectId blobId) in treeEntries)
        {
            string fullPath = Path.Combine(WorkingDirectory,
                path.Replace('/', Path.DirectorySeparatorChar));

            long mtimeSeconds = 0;
            int fileSize = 0;

            if (File.Exists(fullPath))
            {
                var fileInfo = new FileInfo(fullPath);
                mtimeSeconds = new DateTimeOffset(fileInfo.LastWriteTimeUtc).ToUnixTimeSeconds();
                fileSize = (int)fileInfo.Length;
            }
            else
            {
                // File doesn't exist yet; if Hard mode, it will be written below
                byte[] content = ReadBlobContent(blobId);
                fileSize = content.Length;
            }

            newIndex.AddOrUpdate(new IndexEntry
            {
                ModifiedTimeSeconds = mtimeSeconds,
                ModifiedTimeNanoseconds = 0,
                FileSize = fileSize,
                ObjectId = blobId,
                Flags = (ushort)Math.Min(path.Length, 0xFFF),
                Path = path
            });
        }

        SaveIndex(newIndex);

        if (mode != ResetMode.Hard)
            return;

        // Reset working tree: remove tracked files, then write from tree
        IndexFile oldIndex = LoadIndex();
        // Use both old and new entries to determine which files to consider
        var allTrackedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (IndexEntry e in oldIndex.Entries)
            allTrackedPaths.Add(e.Path);
        foreach ((string path, _) in treeEntries)
            allTrackedPaths.Add(path);

        // Remove all currently tracked files
        foreach (string trackedPath in allTrackedPaths)
        {
            string fullPath = Path.Combine(WorkingDirectory,
                trackedPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath))
                File.Delete(fullPath);
        }

        // Write files from the commit's tree
        foreach ((string path, ObjectId blobId) in treeEntries)
        {
            string fullPath = Path.Combine(WorkingDirectory,
                path.Replace('/', Path.DirectorySeparatorChar));

            string? dir = Path.GetDirectoryName(fullPath);
            if (dir is not null)
                Directory.CreateDirectory(dir);

            byte[] content = ReadBlobContent(blobId);
            File.WriteAllBytes(fullPath, content);
        }

        // Rebuild index with correct file metadata after writing
        IndexFile freshIndex = IndexFile.CreateEmpty();
        foreach ((string path, ObjectId blobId) in treeEntries)
        {
            string fullPath = Path.Combine(WorkingDirectory,
                path.Replace('/', Path.DirectorySeparatorChar));

            var fileInfo = new FileInfo(fullPath);
            long mtimeSeconds = new DateTimeOffset(fileInfo.LastWriteTimeUtc).ToUnixTimeSeconds();

            freshIndex.AddOrUpdate(new IndexEntry
            {
                ModifiedTimeSeconds = mtimeSeconds,
                ModifiedTimeNanoseconds = 0,
                FileSize = (int)fileInfo.Length,
                ObjectId = blobId,
                Flags = (ushort)Math.Min(path.Length, 0xFFF),
                Path = path
            });
        }

        SaveIndex(freshIndex);
    }

    // ══════════════════════════════════════════════════════════════
    //  Tree reading helpers
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads a tree object recursively, flattening the hierarchy into a list of
    /// (relative-path, blob-object-id) pairs. Directories are expanded, not included.
    /// </summary>
    private List<(string Path, ObjectId Id)> ReadTreeRecursive(ObjectId treeId, string prefix = "")
    {
        var result = new List<(string Path, ObjectId Id)>();
        TreeObject tree = ReadTree(treeId);

        foreach (TreeEntry entry in tree.Entries)
        {
            string entryPath = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;

            if (entry.Mode == FileMode.Directory)
            {
                // Recurse into subtree
                result.AddRange(ReadTreeRecursive(entry.Id, entryPath));
            }
            else
            {
                result.Add((entryPath, entry.Id));
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the tree <see cref="ObjectId"/> from the current HEAD commit,
    /// or <see langword="null"/> if HEAD cannot be resolved (e.g. unborn branch).
    /// </summary>
    private ObjectId? GetHeadTreeId()
    {
        ObjectId? headCommitId = Refs.ResolveHead();
        if (headCommitId is null)
            return null;

        CommitObject commit = ReadCommit(headCommitId.Value);
        return commit.TreeId;
    }

    // ══════════════════════════════════════════════════════════════
    //  Object read/write helpers
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads compressed data from the object store and deserializes it.
    /// </summary>
    public (ObjectType Type, byte[] Content) ReadObject(ObjectId id)
    {
        byte[] compressed = ObjectStore.Read(id);
        return ObjectSerializer.Deserialize(compressed);
    }

    /// <summary>
    /// Reads and parses a commit object from the object store.
    /// </summary>
    public CommitObject ReadCommit(ObjectId id)
    {
        (ObjectType type, byte[] content) = ReadObject(id);
        if (type != ObjectType.Commit)
            throw new InvalidOperationException($"Object {id} is not a commit (found {type}).");

        return ParseCommit(id, content);
    }

    /// <summary>
    /// Reads and parses a tree object from the object store.
    /// </summary>
    public TreeObject ReadTree(ObjectId id)
    {
        (ObjectType type, byte[] content) = ReadObject(id);
        if (type != ObjectType.Tree)
            throw new InvalidOperationException($"Object {id} is not a tree (found {type}).");

        return ParseTree(content);
    }

    /// <summary>
    /// Reads and parses a blob object from the object store.
    /// </summary>
    public BlobObject ReadBlob(ObjectId id)
    {
        (ObjectType type, byte[] content) = ReadObject(id);
        if (type != ObjectType.Blob)
            throw new InvalidOperationException($"Object {id} is not a blob (found {type}).");

        return BlobObject.FromBytes(content);
    }

    /// <summary>
    /// Creates a blob from the given data, serializes and stores it, and returns its ID.
    /// </summary>
    public ObjectId StoreBlob(byte[] data)
    {
        (ObjectId id, byte[] compressed) = ObjectSerializer.Serialize(ObjectType.Blob, data);
        ObjectStore.Write(id, compressed);
        return id;
    }

    /// <summary>
    /// Serializes and stores a tree object, returning its ID.
    /// </summary>
    public ObjectId StoreTree(TreeObject tree)
    {
        byte[] content = SerializeTreeContent(tree);
        (ObjectId id, byte[] compressed) = ObjectSerializer.Serialize(ObjectType.Tree, content);
        ObjectStore.Write(id, compressed);
        return id;
    }

    /// <summary>
    /// Serializes and stores a commit object, returning its ID.
    /// </summary>
    public ObjectId StoreCommit(CommitObject commit)
    {
        byte[] content = SerializeCommitContent(commit);
        (ObjectId id, byte[] compressed) = ObjectSerializer.Serialize(ObjectType.Commit, content);
        ObjectStore.Write(id, compressed);
        return id;
    }

    // ══════════════════════════════════════════════════════════════
    //  Serialization / Parsing helpers
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Serializes tree entries as raw content bytes (without the "tree N\0" header).
    /// Format per entry: "{mode_octal} {name}\0{32_raw_hash_bytes}"
    /// </summary>
    private static byte[] SerializeTreeContent(TreeObject tree)
    {
        using var ms = new MemoryStream();
        foreach (TreeEntry entry in tree.Entries)
        {
            byte[] modeAndName = Encoding.UTF8.GetBytes($"{entry.Mode.ToOctalString()} {entry.Name}\0");
            ms.Write(modeAndName);
            ms.Write(entry.Id.Bytes);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Serializes commit fields as raw content bytes (without the "commit N\0" header).
    /// </summary>
    private static byte[] SerializeCommitContent(CommitObject commit)
    {
        var sb = new StringBuilder();
        sb.Append($"tree {commit.TreeId.ToHexString()}\n");

        foreach (ObjectId parent in commit.Parents)
        {
            sb.Append($"parent {parent.ToHexString()}\n");
        }

        sb.Append($"author {commit.Author}\n");
        sb.Append($"committer {commit.Committer}\n");
        sb.Append('\n');
        sb.Append(commit.Message);

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Parses raw tree content bytes into a <see cref="TreeObject"/>.
    /// </summary>
    private static TreeObject ParseTree(byte[] content)
    {
        var entries = new List<TreeEntry>();
        int offset = 0;

        while (offset < content.Length)
        {
            // Find the space separating mode from name
            int spaceIndex = Array.IndexOf(content, (byte)' ', offset);
            if (spaceIndex < 0)
                break;

            string modeStr = Encoding.UTF8.GetString(content, offset, spaceIndex - offset);
            FileMode mode = FileModeExtensions.ParseFileMode(modeStr);

            // Find the null byte after the name
            int nullIndex = Array.IndexOf(content, (byte)0, spaceIndex + 1);
            if (nullIndex < 0)
                break;

            string name = Encoding.UTF8.GetString(content, spaceIndex + 1, nullIndex - spaceIndex - 1);

            // Read 32 bytes of hash
            byte[] hashBytes = new byte[ObjectId.ByteLength];
            Buffer.BlockCopy(content, nullIndex + 1, hashBytes, 0, ObjectId.ByteLength);
            var objectId = new ObjectId(hashBytes);

            entries.Add(new TreeEntry(mode, name, objectId));
            offset = nullIndex + 1 + ObjectId.ByteLength;
        }

        return new TreeObject(entries);
    }

    /// <summary>
    /// Parses raw commit content bytes into a <see cref="CommitObject"/>.
    /// </summary>
    private static CommitObject ParseCommit(ObjectId expectedId, byte[] content)
    {
        string text = Encoding.UTF8.GetString(content);
        string[] lines = text.Split('\n');

        ObjectId? treeId = null;
        var parents = new List<ObjectId>();
        Signature? author = null;
        Signature? committer = null;
        int messageStartLine = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];

            if (line.Length == 0)
            {
                // Empty line marks the start of the message body
                messageStartLine = i + 1;
                break;
            }

            if (line.StartsWith("tree ", StringComparison.Ordinal))
            {
                treeId = ObjectId.Parse(line[5..]);
            }
            else if (line.StartsWith("parent ", StringComparison.Ordinal))
            {
                parents.Add(ObjectId.Parse(line[7..]));
            }
            else if (line.StartsWith("author ", StringComparison.Ordinal))
            {
                author = Signature.Parse(line[7..]);
            }
            else if (line.StartsWith("committer ", StringComparison.Ordinal))
            {
                committer = Signature.Parse(line[10..]);
            }
        }

        if (treeId is null)
            throw new InvalidDataException("Commit object is missing a tree field.");
        if (author is null)
            throw new InvalidDataException("Commit object is missing an author field.");
        if (committer is null)
            throw new InvalidDataException("Commit object is missing a committer field.");

        // Reconstruct message from remaining lines
        string message = messageStartLine < lines.Length
            ? string.Join('\n', lines[messageStartLine..])
            : string.Empty;

        return new CommitObject(treeId.Value, parents, author, committer, message);
    }

    /// <summary>
    /// Reads the raw blob content (uncompressed data bytes) for the given object ID.
    /// </summary>
    private byte[] ReadBlobContent(ObjectId id)
    {
        (ObjectType type, byte[] content) = ReadObject(id);
        if (type != ObjectType.Blob)
            throw new InvalidOperationException($"Object {id} is not a blob (found {type}).");

        return content;
    }

    // ══════════════════════════════════════════════════════════════
    //  Index helpers
    // ══════════════════════════════════════════════════════════════

    private string IndexPath => Path.Combine(MagicReposDirectory, IndexFileName);

    private IndexFile LoadIndex()
    {
        if (File.Exists(IndexPath))
            return IndexFile.ReadFrom(IndexPath);

        return IndexFile.CreateEmpty();
    }

    private void SaveIndex(IndexFile index)
    {
        index.WriteTo(IndexPath);
    }

    // ══════════════════════════════════════════════════════════════
    //  Ignore rules helper
    // ══════════════════════════════════════════════════════════════

    private IgnoreRuleSet LoadIgnoreRules()
    {
        string ignorePath = Path.Combine(WorkingDirectory, ".magicreposignore");
        if (File.Exists(ignorePath))
            return IgnoreRuleSet.Load(ignorePath);

        return IgnoreRuleSet.Empty();
    }

    // ══════════════════════════════════════════════════════════════
    //  Signature helper
    // ══════════════════════════════════════════════════════════════

    private Signature GetDefaultSignature()
    {
        string name = Config.GetUserName() ?? "Unknown";
        string email = Config.GetUserEmail() ?? "unknown@unknown";
        return new Signature(name, email, DateTimeOffset.Now);
    }

    // ══════════════════════════════════════════════════════════════
    //  File system helpers
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Removes empty directories walking up towards the working directory root.
    /// Stops at the working directory boundary.
    /// </summary>
    private void CleanEmptyDirectories(string? dirPath)
    {
        while (dirPath is not null
            && !string.Equals(Path.GetFullPath(dirPath), WorkingDirectory, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(dirPath)
            && !Directory.EnumerateFileSystemEntries(dirPath).Any())
        {
            Directory.Delete(dirPath);
            dirPath = Path.GetDirectoryName(dirPath);
        }
    }
}
