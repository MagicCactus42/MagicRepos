using MagicRepos.Server;

var baseDir = Environment.GetEnvironmentVariable("MAGICREPOS_DATA")
    ?? "/var/lib/magicrepos/repositories";

if (args.Length >= 1 && args[0] == "serve")
{
    var authenticatedUser = Environment.GetEnvironmentVariable("MAGICREPOS_USER");
    if (string.IsNullOrEmpty(authenticatedUser))
    {
        Console.Error.WriteLine("MAGICREPOS_USER environment variable is not set. " +
            "Each SSH key must have environment=\"MAGICREPOS_USER=username\" in authorized_keys.");
        Environment.Exit(1);
    }

    var repoManager = new ServerRepositoryManager(baseDir);
    var accessControl = new AccessControl(baseDir);
    var handler = new SessionHandler(repoManager, accessControl, authenticatedUser,
        Console.OpenStandardInput(), Console.OpenStandardOutput());
    await handler.HandleAsync();
}
else if (args.Length >= 1 && args[0] == "daemon")
{
    Console.WriteLine("Daemon mode not yet implemented.");
}
else if (args.Length >= 3 && args[0] == "add-key")
{
    // magicrepos-server add-key <username> <public-key-file>
    string username = args[1];
    string keyFile = args[2];

    if (!File.Exists(keyFile))
    {
        Console.Error.WriteLine($"Public key file not found: {keyFile}");
        Environment.Exit(1);
    }

    string publicKey = File.ReadAllText(keyFile).Trim();
    string authorizedKeysPath = "/var/lib/magicrepos/.ssh/authorized_keys";

    // Allow override via env var for flexibility
    string? keysPathOverride = Environment.GetEnvironmentVariable("MAGICREPOS_AUTHORIZED_KEYS");
    if (!string.IsNullOrEmpty(keysPathOverride))
        authorizedKeysPath = keysPathOverride;

    string entry = $"environment=\"MAGICREPOS_USER={username}\" {publicKey}";
    File.AppendAllText(authorizedKeysPath, entry + Environment.NewLine);
    Console.WriteLine($"Added key for user '{username}' to {authorizedKeysPath}");
}
else if (args.Length >= 3 && args[0] == "add-collab")
{
    // magicrepos-server add-collab <owner/repo> <username>
    string repoSpec = args[1];
    string username = args[2];

    if (!TryParseRepoSpec(repoSpec, out string? owner, out string? repo))
    {
        Console.Error.WriteLine("Invalid repo format. Expected: owner/repo");
        Environment.Exit(1);
    }

    var accessControl = new AccessControl(baseDir);
    accessControl.AddCollaborator(owner!, repo!, username);
    Console.WriteLine($"Added '{username}' as collaborator to {repoSpec}");
}
else if (args.Length >= 3 && args[0] == "remove-collab")
{
    // magicrepos-server remove-collab <owner/repo> <username>
    string repoSpec = args[1];
    string username = args[2];

    if (!TryParseRepoSpec(repoSpec, out string? owner, out string? repo))
    {
        Console.Error.WriteLine("Invalid repo format. Expected: owner/repo");
        Environment.Exit(1);
    }

    var accessControl = new AccessControl(baseDir);
    if (accessControl.RemoveCollaborator(owner!, repo!, username))
        Console.WriteLine($"Removed '{username}' from {repoSpec}");
    else
        Console.WriteLine($"'{username}' is not a collaborator of {repoSpec}");
}
else if (args.Length >= 2 && args[0] == "list-collabs")
{
    // magicrepos-server list-collabs <owner/repo>
    string repoSpec = args[1];

    if (!TryParseRepoSpec(repoSpec, out string? owner, out string? repo))
    {
        Console.Error.WriteLine("Invalid repo format. Expected: owner/repo");
        Environment.Exit(1);
    }

    var accessControl = new AccessControl(baseDir);
    var collabs = accessControl.ListCollaborators(owner!, repo!);

    if (collabs.Count == 0)
    {
        Console.WriteLine($"No collaborators for {repoSpec}");
    }
    else
    {
        Console.WriteLine($"Collaborators for {repoSpec}:");
        foreach (string collab in collabs)
            Console.WriteLine($"  {collab}");
    }
}
else
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  magicrepos-server serve --stdin        Start protocol handler (called by SSH ForceCommand)");
    Console.WriteLine("  magicrepos-server daemon               Start daemon mode (not yet implemented)");
    Console.WriteLine("  magicrepos-server add-key <user> <key-file>       Add SSH key for a user");
    Console.WriteLine("  magicrepos-server add-collab <owner/repo> <user>  Add collaborator");
    Console.WriteLine("  magicrepos-server remove-collab <owner/repo> <user>  Remove collaborator");
    Console.WriteLine("  magicrepos-server list-collabs <owner/repo>       List collaborators");
}

static bool TryParseRepoSpec(string spec, out string? owner, out string? repo)
{
    owner = null;
    repo = null;

    int slashIndex = spec.IndexOf('/');
    if (slashIndex <= 0 || slashIndex >= spec.Length - 1)
        return false;

    owner = spec[..slashIndex];
    repo = spec[(slashIndex + 1)..];
    return true;
}
