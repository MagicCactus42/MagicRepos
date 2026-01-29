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
    /// </summary>
    public string GetRepoPath(string username, string repoName) =>
        Path.Combine(_baseDir, username, repoName + ".mr");

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
    /// Returns the bare repository for the given user/repo, or <see langword="null"/> if it does not exist.
    /// </summary>
    public BareRepository? Get(string username, string repoName)
    {
        string path = GetRepoPath(username, repoName);
        if (!Directory.Exists(path) || !File.Exists(Path.Combine(path, "HEAD")))
            return null;

        return BareRepository.Open(path);
    }

    /// <summary>
    /// Returns <see langword="true"/> if the repository exists on disk.
    /// </summary>
    public bool Exists(string username, string repoName)
    {
        string path = GetRepoPath(username, repoName);
        return Directory.Exists(path) && File.Exists(Path.Combine(path, "HEAD"));
    }

    /// <summary>
    /// Lists all repository names (without the <c>.mr</c> suffix) owned by <paramref name="username"/>.
    /// </summary>
    public IReadOnlyList<string> ListRepositories(string username)
    {
        string userDir = Path.Combine(_baseDir, username);
        if (!Directory.Exists(userDir))
            return Array.Empty<string>();

        return Directory.GetDirectories(userDir, "*.mr", SearchOption.TopDirectoryOnly)
            .Where(d => File.Exists(Path.Combine(d, "HEAD")))
            .Select(d => Path.GetFileNameWithoutExtension(d))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }
}
