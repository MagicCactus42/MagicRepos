using System.Text.Json;

namespace MagicRepos.Server;

/// <summary>
/// Namespace-based access control backed by a <c>permissions.json</c> file.
/// <para>
/// Rules:
/// <list type="bullet">
///   <item>Read (pull/clone): any authenticated user.</item>
///   <item>Write (push/create/merge): owner of the namespace, or a listed collaborator.</item>
/// </list>
/// </para>
/// </summary>
public class AccessControl
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _permissionsPath;

    public AccessControl(string baseDir)
    {
        _permissionsPath = Path.Combine(baseDir, "permissions.json");
    }

    /// <summary>
    /// Any non-empty authenticated user can read.
    /// </summary>
    public bool CanRead(string authenticatedUser)
    {
        return !string.IsNullOrEmpty(authenticatedUser);
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="authenticatedUser"/> is allowed
    /// to write to the repository <c><paramref name="repoOwner"/>/<paramref name="repoName"/></c>.
    /// The owner always has write access; other users need to be listed as collaborators.
    /// </summary>
    public bool CanWrite(string authenticatedUser, string repoOwner, string repoName)
    {
        if (string.IsNullOrEmpty(authenticatedUser))
            return false;

        // Owner always has write access
        if (string.Equals(authenticatedUser, repoOwner, StringComparison.OrdinalIgnoreCase))
            return true;

        // Check collaborator list
        var permissions = LoadPermissions();
        string repoKey = $"{repoOwner}/{repoName}";

        if (permissions.Repositories.TryGetValue(repoKey, out var repoPerms))
        {
            return repoPerms.Collaborators.Contains(authenticatedUser, StringComparer.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Adds a collaborator to the specified repository.
    /// </summary>
    public void AddCollaborator(string repoOwner, string repoName, string username)
    {
        var permissions = LoadPermissions();
        string repoKey = $"{repoOwner}/{repoName}";

        if (!permissions.Repositories.TryGetValue(repoKey, out var repoPerms))
        {
            repoPerms = new RepoPermissions();
            permissions.Repositories[repoKey] = repoPerms;
        }

        if (!repoPerms.Collaborators.Contains(username, StringComparer.OrdinalIgnoreCase))
        {
            repoPerms.Collaborators.Add(username);
        }

        SavePermissions(permissions);
    }

    /// <summary>
    /// Removes a collaborator from the specified repository.
    /// Returns <see langword="true"/> if the collaborator was found and removed.
    /// </summary>
    public bool RemoveCollaborator(string repoOwner, string repoName, string username)
    {
        var permissions = LoadPermissions();
        string repoKey = $"{repoOwner}/{repoName}";

        if (!permissions.Repositories.TryGetValue(repoKey, out var repoPerms))
            return false;

        int index = repoPerms.Collaborators.FindIndex(
            c => string.Equals(c, username, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
            return false;

        repoPerms.Collaborators.RemoveAt(index);

        // Clean up empty entries
        if (repoPerms.Collaborators.Count == 0)
            permissions.Repositories.Remove(repoKey);

        SavePermissions(permissions);
        return true;
    }

    /// <summary>
    /// Lists all collaborators for the specified repository.
    /// </summary>
    public IReadOnlyList<string> ListCollaborators(string repoOwner, string repoName)
    {
        var permissions = LoadPermissions();
        string repoKey = $"{repoOwner}/{repoName}";

        if (permissions.Repositories.TryGetValue(repoKey, out var repoPerms))
            return repoPerms.Collaborators;

        return Array.Empty<string>();
    }

    private PermissionsFile LoadPermissions()
    {
        if (!File.Exists(_permissionsPath))
            return new PermissionsFile();

        string json = File.ReadAllText(_permissionsPath);
        return JsonSerializer.Deserialize<PermissionsFile>(json, JsonOptions) ?? new PermissionsFile();
    }

    private void SavePermissions(PermissionsFile permissions)
    {
        string? dir = Path.GetDirectoryName(_permissionsPath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        string json = JsonSerializer.Serialize(permissions, JsonOptions);
        File.WriteAllText(_permissionsPath, json);
    }
}

public class PermissionsFile
{
    public Dictionary<string, RepoPermissions> Repositories { get; set; } = new();
}

public class RepoPermissions
{
    public List<string> Collaborators { get; set; } = new();
}
