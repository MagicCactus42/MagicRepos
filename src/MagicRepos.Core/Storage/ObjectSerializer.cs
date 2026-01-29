using System.IO.Compression;
using System.Text;
using MagicRepos.Core.Objects;

namespace MagicRepos.Core.Storage;

/// <summary>
/// Handles serialization of repository objects using the format:
/// deflate("{type} {content_length}\0{content_bytes}")
/// SHA-256 is computed from the uncompressed bytes (header + content).
/// </summary>
public static class ObjectSerializer
{
    /// <summary>
    /// Creates header+content, computes SHA-256 over the uncompressed data,
    /// and returns the object ID along with the deflate-compressed bytes.
    /// </summary>
    public static (ObjectId Id, byte[] Compressed) Serialize(ObjectType type, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        byte[] raw = BuildRawBytes(type, content);
        ObjectId id = ObjectId.Hash(raw);
        byte[] compressed = Compress(raw);

        return (id, compressed);
    }

    /// <summary>
    /// Decompresses the data, parses the header, and returns the object type and content.
    /// </summary>
    public static (ObjectType Type, byte[] Content) Deserialize(byte[] compressedData)
    {
        ArgumentNullException.ThrowIfNull(compressedData);

        byte[] raw = Decompress(compressedData);

        // Find the null byte separating header from content.
        int nullIndex = Array.IndexOf(raw, (byte)0);
        if (nullIndex < 0)
            throw new InvalidDataException("Object data does not contain a null byte header separator.");

        // Parse header: "{type} {content_length}"
        string header = Encoding.ASCII.GetString(raw, 0, nullIndex);
        int spaceIndex = header.IndexOf(' ');
        if (spaceIndex < 0)
            throw new InvalidDataException("Object header does not contain a space between type and size.");

        string typeString = header[..spaceIndex];
        string sizeString = header[(spaceIndex + 1)..];

        ObjectType objectType = ObjectTypeExtensions.ParseObjectType(typeString);
        int contentLength = int.Parse(sizeString);

        byte[] content = new byte[contentLength];
        Buffer.BlockCopy(raw, nullIndex + 1, content, 0, contentLength);

        return (objectType, content);
    }

    /// <summary>
    /// Computes the SHA-256 of header+content without compressing.
    /// </summary>
    public static ObjectId ComputeId(ObjectType type, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        byte[] raw = BuildRawBytes(type, content);
        return ObjectId.Hash(raw);
    }

    /// <summary>
    /// Builds the raw uncompressed byte array: "{type} {content_length}\0{content_bytes}".
    /// </summary>
    private static byte[] BuildRawBytes(ObjectType type, byte[] content)
    {
        string header = $"{type.ToTypeString()} {content.Length}";
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);

        // header + null byte + content
        byte[] raw = new byte[headerBytes.Length + 1 + content.Length];
        Buffer.BlockCopy(headerBytes, 0, raw, 0, headerBytes.Length);
        raw[headerBytes.Length] = 0; // null separator
        Buffer.BlockCopy(content, 0, raw, headerBytes.Length + 1, content.Length);

        return raw;
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    private static byte[] Decompress(byte[] compressedData)
    {
        using var input = new MemoryStream(compressedData);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }
}
