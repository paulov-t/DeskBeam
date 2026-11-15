namespace DeskBeam.Core.Video;

/// <summary>A changed rectangle of the canvas, encoded as a standalone JPEG.</summary>
public sealed record VideoTile(int X, int Y, int Width, int Height, byte[] Jpeg);

/// <summary>
/// One encoded video frame. A keyframe carries the whole canvas; a delta carries only changed
/// tiles and must be applied on top of the previously received frame from the same stream.
/// </summary>
public sealed record EncodedFrame(
    int Width,
    int Height,
    int Sequence,
    long TimestampMs,
    bool IsKeyframe,
    byte[]? KeyframeJpeg,
    IReadOnlyList<VideoTile> Tiles)
{
    public static EncodedFrame Keyframe(byte[] jpeg, int width, int height, int sequence, long timestampMs) =>
        new(width, height, sequence, timestampMs, true, jpeg, Array.Empty<VideoTile>());

    public static EncodedFrame Delta(int width, int height, int sequence, long timestampMs,
        IReadOnlyList<VideoTile> tiles) =>
        new(width, height, sequence, timestampMs, false, null, tiles);

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
