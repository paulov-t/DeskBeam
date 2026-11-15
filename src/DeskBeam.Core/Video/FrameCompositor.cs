using DeskBeam.Core.Protocol;

namespace DeskBeam.Core.Video;

/// <summary>
/// Applies keyframes and delta tiles to a persistent BGRA canvas. Not thread-safe; use one instance
/// per video consumer.
/// </summary>
public sealed class FrameCompositor : IDisposable
{
    private readonly JpegDecoder _decoder = new();
    private byte[] _canvas = [];
    private int _width;
    private int _height;
    private bool _hasCanvas;

    public int Width => _width;

    public int Height => _height;

    /// <summary>
    /// Applies a frame. Returns false when a delta arrives before any keyframe (or when a tile is
    /// malformed), which tells the caller to request a fresh keyframe.
    /// </summary>
    public bool TryApply(VideoFrame frame, out int width, out int height, out byte[] bgra)
    {
        width = _width;
        height = _height;
        bgra = _canvas;

        if (frame.IsKeyframe)
        {
            if (frame.KeyframeJpeg is null)
                return false;

            if (!_decoder.TryDecode(frame.KeyframeJpeg, out var decodedWidth, out var decodedHeight, out var pixels))
                return false;

            var length = decodedWidth * decodedHeight * 4;
            if (length <= 0 || pixels.Length < length)
                return false;

            if (_canvas.Length != length)
                _canvas = new byte[length];

            Buffer.BlockCopy(pixels, 0, _canvas, 0, length);
            _width = width = decodedWidth;
            _height = height = decodedHeight;
            _hasCanvas = true;
            bgra = _canvas;
            return true;
        }

        if (!_hasCanvas)
            return false;

        // A delta is only meaningful at the canvas size it was encoded against. The host sends a
        // keyframe when the stream size changes, but reject a mismatch defensively.
        if (frame.Width != _width || frame.Height != _height)
            return false;

        foreach (var tile in frame.Tiles)
        {
            if (tile.Width <= 0 || tile.Height <= 0 ||
                tile.X < 0 || tile.Y < 0 ||
                tile.X + tile.Width > _width || tile.Y + tile.Height > _height)
                return false;

            if (!_decoder.TryDecode(tile.Jpeg, out var tileWidth, out var tileHeight, out var pixels) ||
                tileWidth != tile.Width || tileHeight != tile.Height)
                return false;

            var rowBytes = tileWidth * 4;
            for (var y = 0; y < tileHeight; y++)
            {
                var destination = ((tile.Y + y) * _width + tile.X) * 4;
                Buffer.BlockCopy(pixels, y * rowBytes, _canvas, destination, rowBytes);
            }
        }

        width = _width;
        height = _height;
        bgra = _canvas;
        return true;
    }

    /// <summary>Drops the canvas so the next frame must be a keyframe.</summary>
    public void Reset()
    {
        _hasCanvas = false;
        _width = 0;
        _height = 0;
        _canvas = [];
    }

    public void Dispose() => _decoder.Dispose();
}
