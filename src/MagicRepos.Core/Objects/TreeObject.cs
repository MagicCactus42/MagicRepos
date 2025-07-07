using System.Text;

namespace MagicRepos.Core.Objects;

public sealed class TreeObject
{
    public ObjectId Id { get; }
    public IReadOnlyList<TreeEntry> Entries { get; }

    public TreeObject(IEnumerable<TreeEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        // Sort entries by name.
        var sorted = entries.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        Entries = sorted.AsReadOnly();
        Id = ComputeId(sorted);
    }

    /// <summary>
    /// Serializes tree entries as raw content (without the header).
    /// Each entry: "{mode_octal} {name}\0{hash_bytes_32}"
    /// Entries are sorted by name.
    /// </summary>
    private static byte[] SerializeContent(IReadOnlyList<TreeEntry> entries)
    {
        using var ms = new MemoryStream();
        foreach (var entry in entries)
        {
            var modeAndName = Encoding.UTF8.GetBytes($"{entry.Mode.ToOctalString()} {entry.Name}\0");
            ms.Write(modeAndName);
            ms.Write(entry.Id.Bytes);
        }
        return ms.ToArray();
    }

    private static ObjectId ComputeId(IReadOnlyList<TreeEntry> entries)
    {
        var content = SerializeContent(entries);
        var header = Encoding.UTF8.GetBytes($"{ObjectType.Tree.ToTypeString()} {content.Length}\0");
        var full = new byte[header.Length + content.Length];
        header.CopyTo(full, 0);
        content.CopyTo(full, header.Length);
        return ObjectId.Hash(full);
    }

    /// <summary>
    /// Returns the full serialized tree including the header: "tree {length}\0{entries...}".
    /// </summary>
    public byte[] Serialize()
    {
        var content = SerializeContent(Entries);
        var header = Encoding.UTF8.GetBytes($"{ObjectType.Tree.ToTypeString()} {content.Length}\0");
        var full = new byte[header.Length + content.Length];
        header.CopyTo(full, 0);
        content.CopyTo(full, header.Length);
        return full;
    }
}
