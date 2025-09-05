using System.Buffers.Binary;
using MessagePack;
using MagicRepos.Protocol.Messages;

namespace MagicRepos.Protocol;

public static class MessageSerializer
{
    /// <summary>
    /// Writes a framed message to the stream.
    /// Wire format: [4B uint32 BE: length of type+payload] [1B MessageType] [NB MessagePack payload].
    /// </summary>
    public static async Task WriteMessageAsync<T>(Stream stream, MessageType type, T message, CancellationToken ct = default)
    {
        byte[] payload = MessagePackSerializer.Serialize(message, cancellationToken: ct);

        // Length covers the 1-byte type + payload bytes
        uint length = (uint)(1 + payload.Length);

        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, length);

        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        stream.WriteByte((byte)type);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a single framed message from the stream.
    /// Returns the message type and the raw MessagePack payload.
    /// </summary>
    public static async Task<(MessageType Type, byte[] Payload)> ReadFrameAsync(Stream stream, CancellationToken ct = default)
    {
        byte[] header = new byte[4];
        await ReadExactAsync(stream, header, ct).ConfigureAwait(false);

        uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length < 1)
            throw new InvalidDataException("Frame length must be at least 1 byte for the message type.");

        byte[] frame = new byte[length];
        await ReadExactAsync(stream, frame, ct).ConfigureAwait(false);

        var type = (MessageType)frame[0];
        byte[] payload = new byte[length - 1];
        if (payload.Length > 0)
            Buffer.BlockCopy(frame, 1, payload, 0, payload.Length);

        return (type, payload);
    }

    /// <summary>
    /// Deserializes a MessagePack payload into the specified type.
    /// </summary>
    public static T Deserialize<T>(byte[] payload)
    {
        return MessagePackSerializer.Deserialize<T>(payload);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Unexpected end of stream while reading frame.");
            offset += read;
        }
    }
}
