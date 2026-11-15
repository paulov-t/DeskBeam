namespace DeskBeam.Core.Video;

/// <summary>Output shape and quality for one encoded frame.</summary>
public readonly record struct VideoEncoderOptions(int Width, int Height, int Quality);

/// <summary>
/// Encodes captured BGRA frames into wire-ready video frames. An implementation may emit a
/// keyframe or a delta; a delta always describes changes since the previously returned frame, so
/// a caller must feed one encoder instance sequentially. This is the seam where an interframe
/// codec (H.264/HEVC) can later replace the JPEG tile encoder without changing the transport.
/// </summary>
public interface IVideoEncoder : IDisposable
{
    string Name { get; }

    int CanvasWidth { get; }

    int CanvasHeight { get; }

    /// <summary>
    /// Encodes one captured frame. Returns null when nothing changed.
    /// </summary>
    EncodedFrame? Encode(ReadOnlySpan<byte> bgra, int width, int height, int stride,
        VideoEncoderOptions options, long timestampMs, bool forceKeyframe);

    /// <summary>
    /// Re-emits the last encoded canvas as a keyframe. Lets a viewer that joins an idle desktop get
    /// a full picture without waiting for the next screen change. Returns null before any encode or
    /// when the requested output size differs from the cached canvas.
    /// </summary>
    EncodedFrame? EncodeCachedKeyframe(VideoEncoderOptions options, long timestampMs);
}
