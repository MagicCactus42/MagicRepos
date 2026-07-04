using System.Security.Cryptography;

namespace MagicRepos.Core.Objects;

public readonly struct ObjectId : IEquatable<ObjectId>
{
    public static readonly ObjectId Zero = new(new byte[32]);

    public const int ByteLength = 32;
    public const int HexLength = 64;

    private static readonly byte[] ZeroBytes = new byte[ByteLength];

    private readonly byte[] _bytes;

    // A default(ObjectId) has a null backing array; treat it as the all-zero hash so that
    // Bytes/ToHexString/GetHashCode/Equals never throw and behave consistently.
    private byte[] SafeBytes => _bytes ?? ZeroBytes;

    public ReadOnlySpan<byte> Bytes => SafeBytes;

    public ObjectId(byte[] bytes)
    {
        if (bytes is null || bytes.Length != ByteLength)
            throw new ArgumentException($"SHA-256 hash must be {ByteLength} bytes.", nameof(bytes));

        // Defensive copy so later mutation of the caller's array cannot change this id.
        _bytes = (byte[])bytes.Clone();
    }

    public ObjectId(ReadOnlySpan<byte> bytes) : this(bytes.ToArray())
    {
    }

    public static ObjectId Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);

        if (!TryParse(hex, out ObjectId id))
            throw new ArgumentException($"Invalid object id: must be {HexLength} hexadecimal characters.", nameof(hex));

        return id;
    }

    /// <summary>
    /// Attempts to parse a 64-character lowercase/uppercase hex string into an <see cref="ObjectId"/>.
    /// Returns <see langword="false"/> for null, wrong-length, or non-hex input instead of throwing,
    /// so it is safe to call on untrusted network input.
    /// </summary>
    public static bool TryParse(string? hex, out ObjectId id)
    {
        id = default;

        if (hex is null || hex.Length != HexLength)
            return false;

        var bytes = new byte[ByteLength];
        for (var i = 0; i < ByteLength; i++)
        {
            int hi = FromHexDigit(hex[i * 2]);
            int lo = FromHexDigit(hex[i * 2 + 1]);
            if (hi < 0 || lo < 0)
                return false;

            bytes[i] = (byte)((hi << 4) | lo);
        }

        id = new ObjectId(bytes);
        return true;
    }

    private static int FromHexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1
    };

    public static ObjectId Hash(ReadOnlySpan<byte> data)
    {
        var hash = SHA256.HashData(data);
        return new ObjectId(hash);
    }

    public string ToHexString()
    {
        return Convert.ToHexStringLower(SafeBytes);
    }

    /// <summary>First 2 hex characters, used as directory prefix in the object store.</summary>
    public string Prefix => ToHexString()[..2];

    /// <summary>Remaining 62 hex characters, used as filename in the object store.</summary>
    public string Suffix => ToHexString()[2..];

    public bool Equals(ObjectId other)
    {
        return SafeBytes.AsSpan().SequenceEqual(other.SafeBytes);
    }

    public override bool Equals(object? obj)
    {
        return obj is ObjectId other && Equals(other);
    }

    public override int GetHashCode()
    {
        // Use the first 4 bytes as a hash code for performance.
        return BitConverter.ToInt32(SafeBytes, 0);
    }

    public override string ToString() => ToHexString();

    public static bool operator ==(ObjectId left, ObjectId right) => left.Equals(right);
    public static bool operator !=(ObjectId left, ObjectId right) => !left.Equals(right);
}
