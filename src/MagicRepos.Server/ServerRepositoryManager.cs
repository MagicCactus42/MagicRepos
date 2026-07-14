namespace MagicRepos.Server;

/// <summary>
/// Manages bare repositories on the server filesystem.
/// Repositories are stored under <c>{baseDir}/{username}/{repoName}.mr</c>.
/// </summary>
public class ServerRepositoryManager
{
    private readonly string _baseDir;

    public ServerRepositoryManager(string baseDir)
    {
        _baseDir = baseDir;
    }

    /// <summary>
    /// Returns the filesystem path for the repository owned by
    /// <paramref name="username"/> with the given <paramref name="repoName"/>.
    /// Both segments are validated so a caller cannot escape the data directory.
    /// </summary>
    public string GetRepoPath(string username, string repoName)
    {
        string path = Path.Combine(_baseDir, ValidateSegment(username, nameof(username)),
            ValidateSegment(repoName, nameof(repoName)) + ".mr");

        // Defence in depth: ensure the resolved path really is under the data directory.
        string fullBase = Path.GetFullPath(_baseDir);
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullBase + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && fullPath != fullBase)
        {
            throw new ArgumentException($"Repository path '{path}' escapes the data directory.");
        }

        return path;
    }

    private static string ValidateSegment(string segment, string paramName)
    {
        if (string.IsNullOrEmpty(segment) || segment is "." or ".."
            || segment.Contains('/') || segment.Contains('\\')
            || segment.Contains('\0') || segment.Any(char.IsControl))
        {
            throw new ArgumentException($"Invalid path segment: '{segment}'.", paramName);
        }

        return segment;
    }

    /// <summary>
    /// Returns the bare repository for the given user/repo, creating it if it does not exist.
    /// </summary>
    public BareRepository GetOrCreate(string username, string repoName)
    {
        string path = GetRepoPath(username, repoName);
        if (Directory.Exists(path) && File.Exists(Path.Combine(path, "HEAD")))
            return BareRepository.Open(path);

        return BareRepository.Init(path);
    }

    /// <summary>
    /// Returns <see langword="true"/> if the repository exists on disk.
    /// </summary>
    public bool Exists(string username, string repoName)
    {
        string path = GetRepoPath(username, repoName);
        return Directory.Exists(path) && File.Exists(Path.Combine(path, "HEAD"));
    }
}
