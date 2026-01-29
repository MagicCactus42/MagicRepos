using System.Text;

namespace MagicRepos.Core.Objects;

public sealed class BlobObject
{
    public ObjectId Id { get; }
    public byte[] Data { get; }

    public BlobObject(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        Data = data;
        Id = ComputeId(data);
    }

    /// <summary>
    /// Creates a BlobObject from raw byte content.
    /// </summary>
    public static BlobObject FromBytes(byte[] data) => new(data);

    private static ObjectId ComputeId(byte[] data)
    {
        var header = Encoding.UTF8.GetBytes($"{ObjectType.Blob.ToTypeString()} {data.Length}\0");
        var full = new byte[header.Length + data.Length];
        header.CopyTo(full, 0);
        data.CopyTo(full, header.Length);
        return ObjectId.Hash(full);
    }

    /// <summary>
    /// Returns the full serialized content including the header: "blob {length}\0{data}".
    /// </summary>
    public byte[] Serialize()
    {
        var header = Encoding.UTF8.GetBytes($"{ObjectType.Blob.ToTypeString()} {Data.Length}\0");
        var full = new byte[header.Length + Data.Length];
        header.CopyTo(full, 0);
        Data.CopyTo(full, header.Length);
        return full;
    }
}
