using System.Buffers;
using System.Buffers.Binary;
using DeskBeam.Core.Video;

namespace DeskBeam.Core.Protocol;

/// <summary>
/// A decoded video frame: either a full keyframe or a set of changed tiles from the previous frame.
/// </summary>
public sealed record VideoFrame(
    int Sequence,
    int Width,
    int Height,
    bool IsKeyframe,
    byte[]? KeyframeJpeg,
    IReadOnlyList<VideoTile> Tiles)
{
    public int ByteCount
    {
        get
        {
            var total = KeyframeJpeg?.Length ?? 0;
            foreach (var tile in Tiles)
                total += tile.Jpeg.Length;
            return total;
        }
    }
}

/// <summary>
/// Length-prefixed framing for both channels. Control frame: [int32 length][utf8 json].
/// Video frame: [int32 length][int32 sequence][uint16 width][uint16 height][byte kind][body],
/// where kind 1 is a whole-canvas JPEG and kind 2 is a list of changed tiles.
/// </summary>
public static class Wire
{
    private const byte VideoKindKeyframe = 1;
    private const byte VideoKindTiles = 2;
    private const int MaxTilesPerFrame = 4096;

    public static async ValueTask SendControlAsync(Stream stream, ControlMessage message, CancellationToken ct)
    {
        var payload = ControlCodec.Encode(message);
        if (payload.Length > ProtocolConstants.MaxControlMessageBytes)
            throw new InvalidOperationException("Control message too large.");

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async ValueTask<ControlMessage?> ReceiveControlAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        try
        {
            await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > ProtocolConstants.MaxControlMessageBytes)
            throw new InvalidDataException($"Invalid control frame length: {length}");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return ControlCodec.Decode(payload);
    }

    public static async ValueTask SendVideoAsync(Stream stream, EncodedFrame frame, CancellationToken ct)
    {
        var bodyLength = 5;
        if (frame.IsKeyframe)
            bodyLength += 4 + (frame.KeyframeJpeg?.Length ?? 0);
        else
        {
            bodyLength += 2;
            foreach (var tile in frame.Tiles)
                bodyLength += 12 + tile.Jpeg.Length;
        }

        var total = 8 + bodyLength;
        if (total > ProtocolConstants.MaxVideoFrameBytes)
            throw new InvalidOperationException("Video frame too large.");

        var buffer = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            var span = buffer.AsSpan(0, total);
            BinaryPrimitives.WriteInt32LittleEndian(span[..4], bodyLength);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4, 4), frame.Sequence);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(8, 2), checked((ushort)frame.Width));
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(10, 2), checked((ushort)frame.Height));

            var offset = 12;
            if (frame.IsKeyframe)
            {
                span[offset++] = VideoKindKeyframe;
                var jpeg = frame.KeyframeJpeg ?? [];
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), jpeg.Length);
                offset += 4;
                jpeg.CopyTo(span[offset..]);
            }
            else
            {
                span[offset++] = VideoKindTiles;
                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset, 2), checked((ushort)frame.Tiles.Count));
                offset += 2;

                foreach (var tile in frame.Tiles)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset, 2), checked((ushort)tile.X));
                    BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset + 2, 2), checked((ushort)tile.Y));
                    BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset + 4, 2), checked((ushort)tile.Width));
                    BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset + 6, 2), checked((ushort)tile.Height));
                    BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset + 8, 4), tile.Jpeg.Length);
                    offset += 12;
                    tile.Jpeg.CopyTo(span[offset..]);
                    offset += tile.Jpeg.Length;
                }
            }

            await stream.WriteAsync(buffer.AsMemory(0, total), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async ValueTask<VideoFrame?> ReceiveVideoAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[8];
        try
        {
            await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        var sequence = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (length is < 6 or > ProtocolConstants.MaxVideoFrameBytes)
            throw new InvalidDataException($"Invalid video frame length: {length}");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);

        var width = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
        var height = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2, 2));
        var kind = payload[4];
        var offset = 5;

        if (width == 0 || height == 0)
            throw new InvalidDataException("Invalid video frame dimensions.");

        if (kind == VideoKindKeyframe)
        {
            if (length < offset + 4)
                throw new InvalidDataException("Truncated video keyframe header.");

            var jpegLength = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4));
            offset += 4;
            if (jpegLength <= 0 || jpegLength > length - offset)
                throw new InvalidDataException("Invalid video keyframe length.");

            return new VideoFrame(sequence, width, height, true,
                payload.AsSpan(offset, jpegLength).ToArray(), Array.Empty<VideoTile>());
        }

        if (kind == VideoKindTiles)
        {
            if (length < offset + 2)
                throw new InvalidDataException("Truncated video tile header.");

            var count = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset, 2));
            offset += 2;
            if (count == 0 || count > MaxTilesPerFrame)
                throw new InvalidDataException($"Invalid video tile count: {count}");

            var tiles = new List<VideoTile>(count);
            for (var i = 0; i < count; i++)
            {
                if (length < offset + 12)
                    throw new InvalidDataException("Truncated video tile.");

                var x = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset, 2));
                var y = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset + 2, 2));
                var tileWidth = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset + 4, 2));
                var tileHeight = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset + 6, 2));
                var jpegLength = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset + 8, 4));
                offset += 12;

                if (tileWidth == 0 || tileHeight == 0 ||
                    x + tileWidth > width || y + tileHeight > height ||
                    jpegLength <= 0 || jpegLength > length - offset)
                    throw new InvalidDataException("Invalid video tile.");

                tiles.Add(new VideoTile(x, y, tileWidth, tileHeight,
                    payload.AsSpan(offset, jpegLength).ToArray()));
                offset += jpegLength;
            }

            return new VideoFrame(sequence, width, height, false, null, tiles);
        }

        throw new InvalidDataException($"Unknown video frame kind: {kind}");
    }

    /// <summary>Reads a length-prefixed UTF-8 string (used by the video socket handshake).</summary>
    public static async ValueTask<string?> ReceiveStringAsync(Stream stream, CancellationToken ct, int maxBytes = 4096)
    {
        var header = new byte[4];
        try
        {
            await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > maxBytes)
            throw new InvalidDataException("Invalid string frame length.");
        if (length == 0)
            return string.Empty;

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetString(payload);
    }

    public static async ValueTask SendStringAsync(Stream stream, string value, CancellationToken ct)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(value);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
}
