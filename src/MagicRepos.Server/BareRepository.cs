namespace MagicRepos.Server;

using MagicRepos.Core.Objects;
using MagicRepos.Core.Storage;
using MagicRepos.Core.Refs;

/// <summary>
/// A bare repository (no working tree) stored on the server.
/// The repository root itself acts as the "magicrepos directory" –
/// it contains objects/, refs/heads/, refs/tags/, and HEAD directly.
/// </summary>
public class BareRepository
{
    public string Path { get; }
    public ObjectStore ObjectStore { get; }
    public RefStore Refs { get; }

    private BareRepository(string path)
    {
        Path = path;
        ObjectStore = new ObjectStore(path);
        Refs = new RefStore(path);
    }

    /// <summary>
    /// Opens an existing bare repository at <paramref name="path"/>.
    /// Throws if the directory does not exist or is missing required structure.
    /// </summary>
    public static BareRepository Open(string path)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Repository not found at '{path}'.");

        string headPath = System.IO.Path.Combine(path, "HEAD");
        if (!File.Exists(headPath))
            throw new InvalidOperationException($"Not a valid bare repository: HEAD not found at '{headPath}'.");

        return new BareRepository(path);
    }

    /// <summary>
    /// Initializes a new bare repository at <paramref name="path"/>.
    /// Creates the directory structure: objects/, refs/heads/, refs/tags/, and HEAD.
    /// </summary>
    public static BareRepository Init(string path)
    {
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(System.IO.Path.Combine(path, "objects"));
        Directory.CreateDirectory(System.IO.Path.Combine(path, "refs", "heads"));
        Directory.CreateDirectory(System.IO.Path.Combine(path, "refs", "tags"));

        string headPath = System.IO.Path.Combine(path, "HEAD");
        if (!File.Exists(headPath))
        {
            File.WriteAllText(headPath, "ref: refs/heads/main\n");
        }

        return new BareRepository(path);
    }

    /// <summary>
    /// Checks whether an object with the given <paramref name="id"/> exists in the object store.
    /// </summary>
    public bool Exists(ObjectId id) => ObjectStore.Exists(id);
}
