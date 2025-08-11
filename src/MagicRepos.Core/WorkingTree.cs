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
    /// Reads the full contents of a file given its path relative to the working directory.
    /// </summary>
    public byte[] ReadFile(string relativePath)
    {
        var fullPath = GetFullPath(relativePath);
        return File.ReadAllBytes(fullPath);
    }

    /// <summary>
    /// Gets the last modification time of a file as Unix-style seconds and nanoseconds.
    /// </summary>
    public (long seconds, int nanoseconds) GetModifiedTime(string relativePath)
    {
        var fullPath = GetFullPath(relativePath);
        var lastWrite = File.GetLastWriteTimeUtc(fullPath);
        var epoch = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var elapsed = lastWrite - epoch;
        var totalSeconds = (long)elapsed.TotalSeconds;
        var fractionalTicks = elapsed.Ticks - totalSeconds * TimeSpan.TicksPerSecond;
        var nanoseconds = (int)(fractionalTicks * 100); // 1 tick = 100 ns
        return (totalSeconds, nanoseconds);
    }

    /// <summary>
    /// Gets the size of a file in bytes.
    /// </summary>
    public int GetFileSize(string relativePath)
    {
        var fullPath = GetFullPath(relativePath);
        var info = new FileInfo(fullPath);
        return (int)info.Length;
    }

    /// <summary>
    /// Checks whether a file exists at the given relative path.
    /// </summary>
    public bool FileExists(string relativePath)
    {
        var fullPath = GetFullPath(relativePath);
        return File.Exists(fullPath);
    }

    /// <summary>
    /// Recursively enumerates a directory, collecting non-ignored file paths.
    /// Directories that are ignored are pruned entirely.
    /// </summary>
    private void EnumerateDirectory(string directory, List<string> results)
    {
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(directory);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        Array.Sort(entries, StringComparer.Ordinal);

        foreach (var entry in entries)
        {
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
    /// Converts an absolute path to a path relative to the working directory, using forward slashes.
    /// </summary>
    private string ToRelativePath(string absolutePath)
    {
        var relative = Path.GetRelativePath(_workingDir, absolutePath);
        return relative.Replace('\\', '/');
    }

    /// <summary>
    /// Resolves a relative path (with forward slashes) to an absolute filesystem path.
    /// </summary>
    private string GetFullPath(string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(_workingDir, normalized);
    }
}
