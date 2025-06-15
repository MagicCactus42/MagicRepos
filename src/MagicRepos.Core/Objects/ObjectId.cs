using System.Security.Cryptography;

namespace MagicRepos.Core.Objects;

public readonly struct ObjectId : IEquatable<ObjectId>
{
    public static readonly ObjectId Zero = new(new byte[32]);

    public const int ByteLength = 32;
    public const int HexLength = 64;

    private readonly byte[] _bytes;

    public ReadOnlySpan<byte> Bytes => _bytes;

    public ObjectId(byte[] bytes)
    {
        if (bytes is null || bytes.Length != ByteLength)
            throw new ArgumentException($"SHA-256 hash must be {ByteLength} bytes.", nameof(bytes));

        _bytes = bytes;
    }

    public ObjectId(ReadOnlySpan<byte> bytes) : this(bytes.ToArray())
    {
    }

    public static ObjectId Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);

        if (hex.Length != HexLength)
            throw new ArgumentException($"Hex string must be {HexLength} characters.", nameof(hex));

        var bytes = new byte[ByteLength];
        for (var i = 0; i < ByteLength; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        return new ObjectId(bytes);
    }

    public static ObjectId Hash(ReadOnlySpan<byte> data)
    {
        var hash = SHA256.HashData(data);
        return new ObjectId(hash);
    }

    public static ObjectId Hash(Stream stream)
    {
        var hash = new byte[ByteLength];
        SHA256.HashData(stream, hash);
        return new ObjectId(hash);
    }

    public string ToHexString()
    {
        return Convert.ToHexStringLower(_bytes);
    }

    /// <summary>First 2 hex characters, used as directory prefix in the object store.</summary>
    public string Prefix => ToHexString()[..2];

    /// <summary>Remaining 62 hex characters, used as filename in the object store.</summary>
    public string Suffix => ToHexString()[2..];

    public bool Equals(ObjectId other)
    {
        return _bytes.AsSpan().SequenceEqual(other._bytes);
    }

    public override bool Equals(object? obj)
    {
        return obj is ObjectId other && Equals(other);
    }

    public override int GetHashCode()
    {
        // Use the first 4 bytes as a hash code for performance.
        return BitConverter.ToInt32(_bytes, 0);
    }

    public override string ToString() => ToHexString();

    public static bool operator ==(ObjectId left, ObjectId right) => left.Equals(right);
    public static bool operator !=(ObjectId left, ObjectId right) => !left.Equals(right);
}
