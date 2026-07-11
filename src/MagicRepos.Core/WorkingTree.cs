using MagicRepos.Core.Ignore;

namespace MagicRepos.Core;

public class WorkingTree
{
    private readonly string _workingDir;
    private readonly IgnoreRuleSet _ignoreRules;

    public WorkingTree(string workingDir, IgnoreRuleSet ignoreRules)
    {
        _workingDir = Path.GetFullPath(workingDir);
        _ignoreRules = ignoreRules;
    }

    /// <summary>
    /// Gets all non-ignored files relative to the working directory.
    /// Paths use forward slashes as separators.
    /// </summary>
    public IReadOnlyList<string> GetFiles()
    {
        var results = new List<string>();
        EnumerateDirectory(_workingDir, results);
        results.Sort(StringComparer.Ordinal);
        return results;
    }

    /// <summary>
    /// Recursively enumerates a directory, collecting non-ignored file paths.
    /// Directories that are ignored are pruned entirely. Symlinks (reparse points) are
    /// skipped so directory-symlink cycles cannot expand into an unbounded / duplicated
    /// listing, and a symlink pointing at the repository root cannot expose internals.
    /// </summary>
    private void EnumerateDirectory(string directory, List<string> results)
    {
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(directory);
        }
        catch (DirectoryNotFoundException)
        {
            // The directory disappeared during enumeration (benign race).
            return;
        }

        Array.Sort(entries, StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (IsSymlink(entry))
                continue;

            var relativePath = ToRelativePath(entry);
            var isDir = Directory.Exists(entry);

            if (_ignoreRules.IsIgnored(relativePath, isDir))
                continue;

            if (isDir)
            {
                EnumerateDirectory(entry, results);
            }
            else
            {
                results.Add(relativePath);
            }
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> if the entry is a symbolic link / reparse point.
    /// </summary>
    private static bool IsSymlink(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // If we cannot even read the attributes, do not traverse it.
            return true;
        }
    }

    /// <summary>
    /// Converts an absolute path to a path relative to the working directory, using forward slashes.
    /// </summary>
    private string ToRelativePath(string absolutePath)
    {
        var relative = Path.GetRelativePath(_workingDir, absolutePath);
        return relative.Replace('\\', '/');
    }
}
