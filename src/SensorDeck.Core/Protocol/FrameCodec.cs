using System.Buffers.Binary;
using MessagePack;

namespace SensorDeck.Core.Protocol;

/// <summary>
/// Length-prefixed framing: a 4-byte little-endian payload length, then a MessagePack <see cref="IProtocolMessage"/>.
/// </summary>
public static class FrameCodec
{
    /// <summary>Guards against a corrupt or hostile length prefix.</summary>
    public const int MaxFrameBytes = 16 * 1024 * 1024;

    private static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    public static byte[] Serialize(IProtocolMessage message) =>
        MessagePackSerializer.Serialize(message, Options);

    public static IProtocolMessage Deserialize(ReadOnlyMemory<byte> payload) =>
        MessagePackSerializer.Deserialize<IProtocolMessage>(payload, Options);

    public static async ValueTask WriteAsync(Stream stream, IProtocolMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var payload = Serialize(message);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);

        // Header and payload as two writes (the pipe is in byte mode, so the reader sees the same bytes): no second copy
        // of a large payload (history answers are MBs). Callers never interleave frames: ClientSession writes from one
        // loop, CollectorClient holds its write lock, and both drop the connection if a write is cancelled midway.
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one frame. Returns null on a clean end of stream (peer disconnected between frames), and
    /// <see cref="UnknownMessage.Instance"/> for a message type this version doesn't know (a newer peer), which
    /// readers should skip.
    /// </summary>
    /// <exception cref="InvalidDataException">The length prefix is negative or above <see cref="MaxFrameBytes"/>.</exception>
    public static ValueTask<IProtocolMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken = default) =>
        ReadAsync(stream, MaxFrameBytes, cancellationToken);

    /// <summary>Like <see cref="ReadAsync(Stream, CancellationToken)"/>, with a smaller size limit.</summary>
    /// <param name="maxFrameBytes">Largest payload accepted (capped at <see cref="MaxFrameBytes"/>). The collector uses a
    /// small limit for what clients send, so a client can't make it hold a large buffer per connection.</param>
    /// <exception cref="InvalidDataException">The length prefix is negative or above the limit.</exception>
    public static async ValueTask<IProtocolMessage?> ReadAsync(Stream stream, int maxFrameBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        maxFrameBytes = Math.Clamp(maxFrameBytes, 0, MaxFrameBytes);

        var header = new byte[4];
        if (!await ReadExactlyOrEndAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > maxFrameBytes)
        {
            throw new InvalidDataException($"Invalid frame length {length}.");
        }

        var payload = new byte[length];
        if (!await ReadExactlyOrEndAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException("Stream ended in the middle of a frame.");
        }

        try
        {
            return Deserialize(payload) ?? UnknownMessage.Instance;
        }
        catch (MessagePackSerializationException)
        {
            // The frame was read whole, so the stream is still in sync: skip what we can't understand.
            return UnknownMessage.Instance;
        }
    }

    private static async ValueTask<bool> ReadExactlyOrEndAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("Stream ended in the middle of a frame.");
            }

            offset += read;
        }

        return true;
    }
}

/// <summary>A frame with a message type this version doesn't know (sent by a newer peer). Never sent.</summary>
public sealed class UnknownMessage : IProtocolMessage
{
    public static UnknownMessage Instance { get; } = new();

    private UnknownMessage()
    {
    }
}
