using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MagicRepos.Core.Objects;

namespace MagicRepos.Core.Storage;

/// <summary>
/// Represents a single entry in the staging area index.
/// </summary>
public sealed class IndexEntry
{
    public long ModifiedTimeSeconds { get; set; }
    public int ModifiedTimeNanoseconds { get; set; }
    public int FileSize { get; set; }
    public ObjectId ObjectId { get; set; }
    public ushort Flags { get; set; }
    public string Path { get; set; } = string.Empty;
}

/// <summary>
/// Binary staging area file at .magicrepos/index.
///
/// Format:
///   "MRIX" (4B magic) | uint32 version=1 | uint32 entryCount
///   Per entry:
///     uint64 mtime_s | uint32 mtime_ns | uint32 fileSize |
///     byte[32] objectId | uint16 flags |
///     name (null-terminated, then null-padded to 8-byte alignment)
///   Footer: byte[32] SHA-256 checksum of everything before it
///
/// All multi-byte integers are big-endian.
/// </summary>
public sealed class IndexFile
{
    private static readonly byte[] Magic = "MRIX"u8.ToArray();
    private const uint Version = 1;

    private readonly List<IndexEntry> _entries = new();

    /// <summary>
    /// The list of index entries, sorted by <see cref="IndexEntry.Path"/>.
    /// </summary>
    public IReadOnlyList<IndexEntry> Entries => _entries;

    private IndexFile()
    {
    }

    /// <summary>
    /// Creates a new empty index.
    /// </summary>
    public static IndexFile CreateEmpty() => new();

    /// <summary>
    /// Adds or updates an entry by its path. Maintains sorted order by path.
    /// </summary>
    public void AddOrUpdate(IndexEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        int index = _entries.FindIndex(e =>
            string.Equals(e.Path, entry.Path, StringComparison.Ordinal));

        if (index >= 0)
        {
            _entries[index] = entry;
        }
        else
        {
            _entries.Add(entry);
            _entries.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Removes the entry with the given path, if present.
    /// </summary>
    public void Remove(string path)
    {
        _entries.RemoveAll(e => string.Equals(e.Path, path, StringComparison.Ordinal));
    }

    /// <summary>
    /// Serializes the index to disk in binary format with big-endian byte order.
    /// </summary>
    public void WriteTo(string filePath)
    {
        using var ms = new MemoryStream();

        // Magic
        ms.Write(Magic, 0, Magic.Length);

        // Version (big-endian uint32)
        WriteUInt32(ms, Version);

        // Entry count (big-endian uint32)
        WriteUInt32(ms, (uint)_entries.Count);

        // Entries
        foreach (IndexEntry entry in _entries)
        {
            long entryStart = ms.Position;

            // mtime_s (big-endian uint64)
            WriteUInt64(ms, (ulong)entry.ModifiedTimeSeconds);

            // mtime_ns (big-endian uint32)
            WriteUInt32(ms, (uint)entry.ModifiedTimeNanoseconds);

            // fileSize (big-endian uint32)
            WriteUInt32(ms, (uint)entry.FileSize);

            // objectId (32 bytes)
            ms.Write(entry.ObjectId.Bytes);

            // flags (big-endian uint16)
            WriteUInt16(ms, entry.Flags);

            // name (null-terminated)
            byte[] nameBytes = Encoding.UTF8.GetBytes(entry.Path);
            ms.Write(nameBytes, 0, nameBytes.Length);
            ms.WriteByte(0); // null terminator

            // Pad to 8-byte alignment from the start of this entry
            long written = ms.Position - entryStart;
            int padding = (int)((8 - (written % 8)) % 8);
            for (int i = 0; i < padding; i++)
                ms.WriteByte(0);
        }

        // Compute SHA-256 checksum over everything written so far
        byte[] dataBytes = ms.ToArray();
        byte[] checksum = SHA256.HashData(dataBytes);

        // Write data + checksum to file
        string? directory = System.IO.Path.GetDirectoryName(filePath);
        if (directory is not null)
            Directory.CreateDirectory(directory);

        using var fs = new FileStream(filePath, System.IO.FileMode.Create, FileAccess.Write);
        fs.Write(dataBytes, 0, dataBytes.Length);
        fs.Write(checksum, 0, checksum.Length);
    }

    /// <summary>
    /// Deserializes an index file from disk.
    /// </summary>
    public static IndexFile ReadFrom(string filePath)
    {
        byte[] allBytes = File.ReadAllBytes(filePath);

        if (allBytes.Length < Magic.Length + 4 + 4 + ObjectId.ByteLength)
            throw new InvalidDataException("Index file is too small.");

        // Verify checksum: last 32 bytes are SHA-256 of everything before
        ReadOnlySpan<byte> data = allBytes.AsSpan(0, allBytes.Length - ObjectId.ByteLength);
        ReadOnlySpan<byte> storedChecksum = allBytes.AsSpan(allBytes.Length - ObjectId.ByteLength);

        byte[] computedChecksum = SHA256.HashData(data);
        if (!storedChecksum.SequenceEqual(computedChecksum))
            throw new InvalidDataException("Index file checksum mismatch.");

        int offset = 0;

        // Magic
        if (!data.Slice(offset, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Invalid index file magic bytes.");
        offset += Magic.Length;

        // Version
        uint version = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
        if (version != Version)
            throw new InvalidDataException($"Unsupported index version {version}.");
        offset += 4;

        // Entry count
        uint entryCount = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
        offset += 4;

        var indexFile = new IndexFile();

        for (uint i = 0; i < entryCount; i++)
        {
            int entryStart = offset;

            // mtime_s
            ulong mtimeS = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(offset, 8));
            offset += 8;

            // mtime_ns
            uint mtimeNs = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            offset += 4;

            // fileSize
            uint fileSize = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            offset += 4;

            // objectId (32 bytes)
            byte[] objectIdBytes = data.Slice(offset, ObjectId.ByteLength).ToArray();
            offset += ObjectId.ByteLength;

            // flags
            ushort flags = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
            offset += 2;

            // name (null-terminated)
            int nameStart = offset;
            while (offset < data.Length && data[offset] != 0)
                offset++;
            string path = Encoding.UTF8.GetString(data.Slice(nameStart, offset - nameStart));
            offset++; // skip null terminator

            // Skip padding to reach 8-byte alignment from entry start
            int written = offset - entryStart;
            int padding = (8 - (written % 8)) % 8;
            offset += padding;

            indexFile._entries.Add(new IndexEntry
            {
                ModifiedTimeSeconds = (long)mtimeS,
                ModifiedTimeNanoseconds = (int)mtimeNs,
                FileSize = (int)fileSize,
                ObjectId = new ObjectId(objectIdBytes),
                Flags = flags,
                Path = path
            });
        }

        return indexFile;
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, value);
        stream.Write(buf);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        stream.Write(buf);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buf, value);
        stream.Write(buf);
    }
}
