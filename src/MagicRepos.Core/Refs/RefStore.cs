namespace MagicRepos.Core.Refs;

using MagicRepos.Core.Objects;

/// <summary>
/// Manages references (branches, HEAD, tags) stored as files under <c>.magicrepos/refs/</c>.
/// Ref files contain a 64-char hex hash followed by a newline.
/// HEAD may contain either a symbolic ref (<c>ref: refs/heads/main\n</c>) or a raw 64-char hex hash.
/// </summary>
public class RefStore
{
    private readonly string _magicReposDir;

    public RefStore(string magicReposDir)
    {
        _magicReposDir = magicReposDir;
    }

    // ──────────────────────────── paths ────────────────────────────

    private string HeadPath => Path.Combine(_magicReposDir, "HEAD");
    private string RefsDir => Path.Combine(_magicReposDir, "refs");
    private string HeadsDir => Path.Combine(RefsDir, "heads");

    // ──────────────────────────── HEAD management ────────────────────────────

    /// <summary>
    /// Reads the raw content of <c>.magicrepos/HEAD</c>.
    /// Returns content such as <c>"ref: refs/heads/main"</c> or a raw 64-char hex hash.
    /// </summary>
    public string ReadHead()
    {
        return File.ReadAllText(HeadPath).TrimEnd('\n', '\r');
    }

    /// <summary>
    /// Overwrites <c>.magicrepos/HEAD</c> with the given content (a trailing newline is appended).
    /// </summary>
    public void WriteHead(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HeadPath)!);
        File.WriteAllText(HeadPath, content + "\n");
    }

    /// <summary>
    /// Returns <see langword="true"/> when HEAD contains a raw hash rather than a symbolic ref.
    /// </summary>
    public bool IsDetachedHead()
    {
        string head = ReadHead();
        return !head.StartsWith("ref: ", StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the short branch name (e.g. <c>"main"</c>) when HEAD is a symbolic ref,
    /// or <see langword="null"/> if HEAD is detached.
    /// </summary>
    public string? GetCurrentBranchName()
    {
        string head = ReadHead();
        if (!head.StartsWith("ref: ", StringComparison.Ordinal))
            return null;

        // "ref: refs/heads/main" -> "main"
        const string prefix = "ref: refs/heads/";
        if (head.StartsWith(prefix, StringComparison.Ordinal))
            return head[prefix.Length..];

        // Unexpected symbolic ref format – return the full ref path after "ref: "
        return head["ref: ".Length..];
    }

    /// <summary>
    /// Follows the symbolic ref (if any) and returns the commit <see cref="ObjectId"/> that HEAD
    /// ultimately points to, or <see langword="null"/> if the ref does not exist yet (e.g. on an
    /// un-born branch).
    /// </summary>
    public ObjectId? ResolveHead()
    {
        string head = ReadHead();

        if (head.StartsWith("ref: ", StringComparison.Ordinal))
        {
            // Symbolic ref – read the target file under .magicrepos/
            string refPath = head["ref: ".Length..]; // e.g. "refs/heads/main"
            string fullPath = Path.Combine(_magicReposDir, refPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
                return null;

            string hex = File.ReadAllText(fullPath).TrimEnd('\n', '\r');
            return ObjectId.Parse(hex);
        }

        // Detached HEAD – content is the raw hash
        return ObjectId.Parse(head);
    }

    // ──────────────────────────── Branch operations ────────────────────────────

    /// <summary>
    /// Creates (or overwrites) a branch ref at <c>refs/heads/{name}</c> pointing to <paramref name="commitId"/>.
    /// </summary>
    public void CreateBranch(string name, ObjectId commitId)
    {
        string branchPath = Path.Combine(HeadsDir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(branchPath)!);
        File.WriteAllText(branchPath, commitId.ToString() + "\n");
    }

    /// <summary>
    /// Deletes the branch ref at <c>refs/heads/{name}</c>.
    /// </summary>
    public void DeleteBranch(string name)
    {
        string branchPath = Path.Combine(HeadsDir, name);
        if (File.Exists(branchPath))
            File.Delete(branchPath);
    }

    /// <summary>
    /// Reads the commit hash stored in <c>refs/heads/{name}</c>,
    /// or returns <see langword="null"/> if the branch does not exist.
    /// </summary>
    public ObjectId? ResolveBranch(string name)
    {
        string branchPath = Path.Combine(HeadsDir, name);
        if (!File.Exists(branchPath))
            return null;

        string hex = File.ReadAllText(branchPath).TrimEnd('\n', '\r');
        return ObjectId.Parse(hex);
    }

    /// <summary>
    /// Lists the names of all branches found under <c>refs/heads/</c>.
    /// </summary>
    public IReadOnlyList<string> ListBranches()
    {
        if (!Directory.Exists(HeadsDir))
            return Array.Empty<string>();

        return Directory.GetFiles(HeadsDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(HeadsDir, f).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    // ──────────────────────────── Generic ref operations ────────────────────────────

    /// <summary>
    /// Writes a 64-char hex hash to <c>refs/{refPath}</c>.
    /// </summary>
    public void WriteRef(string refPath, ObjectId id)
    {
        string fullPath = Path.Combine(RefsDir, refPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, id.ToString() + "\n");
    }

    /// <summary>
    /// Reads the hex hash from <c>refs/{refPath}</c>,
    /// or returns <see langword="null"/> if the file does not exist.
    /// </summary>
    public ObjectId? ReadRef(string refPath)
    {
        string fullPath = Path.Combine(RefsDir, refPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
            return null;

        string hex = File.ReadAllText(fullPath).TrimEnd('\n', '\r');
        return ObjectId.Parse(hex);
    }

    // ──────────────────────────── Universal resolve ────────────────────────────

    /// <summary>
    /// Resolves any ref-like string to an <see cref="ObjectId"/>. Accepts:
    /// <list type="bullet">
    ///   <item><c>"HEAD"</c></item>
    ///   <item>A branch name (looked up under <c>refs/heads/</c>)</item>
    ///   <item>A full ref path (e.g. <c>"refs/heads/main"</c> or <c>"refs/tags/v1"</c>)</item>
    ///   <item>A raw 64-char hex hash</item>
    /// </list>
    /// Returns <see langword="null"/> when the ref cannot be resolved.
    /// </summary>
    public ObjectId? Resolve(string refOrHash)
    {
        if (string.IsNullOrWhiteSpace(refOrHash))
            return null;

        // 1. HEAD
        if (refOrHash.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            return ResolveHead();

        // 2. Explicit full ref path (starts with "refs/")
        if (refOrHash.StartsWith("refs/", StringComparison.Ordinal))
            return ReadRef(refOrHash);

        // 3. Short branch name
        ObjectId? branchResult = ResolveBranch(refOrHash);
        if (branchResult is not null)
            return branchResult;

        // 4. Raw 64-char hex hash
        if (refOrHash.Length == 64 && refOrHash.All(IsHexDigit))
            return ObjectId.Parse(refOrHash);

        return null;
    }

    private static bool IsHexDigit(char c) =>
        c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');
}
