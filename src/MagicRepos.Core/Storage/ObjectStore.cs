using MagicRepos.Core.Objects;

namespace MagicRepos.Core.Storage;

/// <summary>
/// Manages loose objects on disk in the .magicrepos/objects/ directory.
/// Path structure: objects/{first2hexchars}/{remaining62hexchars}
/// </summary>
public sealed class ObjectStore
{
    private readonly string _objectsDirectory;

    /// <summary>
    /// Initializes a new <see cref="ObjectStore"/> rooted at the given .magicrepos directory.
    /// </summary>
    /// <param name="magicReposDirectory">
    /// The path to the .magicrepos directory (e.g. "/repo/.magicrepos").
    /// </param>
    public ObjectStore(string magicReposDirectory)
    {
        ArgumentNullException.ThrowIfNull(magicReposDirectory);
        _objectsDirectory = Path.Combine(magicReposDirectory, "objects");
    }

    /// <summary>
    /// Writes compressed object data to disk. If the object already exists, the write is skipped.
    /// </summary>
    public void Write(ObjectId id, byte[] compressedData)
    {
        ArgumentNullException.ThrowIfNull(compressedData);

        string path = GetObjectPath(id);

        if (File.Exists(path))
            return;

        string? directory = Path.GetDirectoryName(path);
        if (directory is not null)
            Directory.CreateDirectory(directory);

        File.WriteAllBytes(path, compressedData);
    }

    /// <summary>
    /// Reads compressed bytes from disk for the given object ID.
    /// </summary>
    public byte[] Read(ObjectId id)
    {
        string path = GetObjectPath(id);

        if (!File.Exists(path))
            throw new FileNotFoundException($"Object {id} not found at '{path}'.");

        return File.ReadAllBytes(path);
    }

    /// <summary>
    /// Checks whether the object file exists on disk.
    /// </summary>
    public bool Exists(ObjectId id)
    {
        return File.Exists(GetObjectPath(id));
    }

    private string GetObjectPath(ObjectId id)
    {
        return Path.Combine(_objectsDirectory, id.Prefix, id.Suffix);
    }
}
