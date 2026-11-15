using SkiaSharp;

namespace DeskBeam.Core.Video;

/// <summary>
/// Intra-only JPEG encoder that sends a full keyframe only when needed and otherwise emits the
/// 64x64 tiles whose pixels changed since the last encoded frame. Desktop content is mostly static,
/// so this removes almost all redundant bytes while keeping the existing JPEG decode path.
/// </summary>
public sealed unsafe class JpegTileEncoder : IVideoEncoder
{
    private const int TileSize = 64;

    // Once a large part of the canvas changes, per-tile JPEG headers cost more than one full frame.
    private const double KeyframeDirtyFraction = 0.5;

    // Safety net for viewers that somehow lost a delta; explicit keyframe requests are the fast path.
    private const int KeyframeInterval = 240;

    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    private byte[]? _previous;
    private byte[] _current = [];
    private int _width;
    private int _height;
    private int _framesSinceKeyframe;
    private SKBitmap? _scaleTarget;

    public string Name => "JPEG tiles";

    public int CanvasWidth => _width;

    public int CanvasHeight => _height;

    public EncodedFrame? Encode(ReadOnlySpan<byte> bgra, int width, int height, int stride,
        VideoEncoderOptions options, long timestampMs, bool forceKeyframe)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || options.Width <= 0 || options.Height <= 0)
            return null;

        if (_previous is null || _width != options.Width || _height != options.Height)
        {
            _width = options.Width;
            _height = options.Height;
            _current = GC.AllocateUninitializedArray<byte>(_width * _height * 4);

            // Allocate the baseline only once the first conversion succeeds. Until then there is
            // nothing valid to diff against, and a cached keyframe must not expose uninitialized
            // memory.
            _previous = null;
            _framesSinceKeyframe = 0;
            forceKeyframe = true;
        }

        if (!PrepareCurrent(bgra, width, height, stride))
            return null;

        if (_previous is null)
        {
            _previous = GC.AllocateUninitializedArray<byte>(_current.Length);
            forceKeyframe = true;
        }

        if (forceKeyframe || _framesSinceKeyframe >= KeyframeInterval)
            return BuildKeyframe(options.Quality, timestampMs);

        var tiles = CollectDirtyTiles(options.Quality, out var needsKeyframe);
        if (needsKeyframe)
            return BuildKeyframe(options.Quality, timestampMs);

        if (tiles.Count == 0)
            return null;

        CopyTilesToPrevious(tiles);
        _framesSinceKeyframe++;
        return EncodedFrame.Delta(_width, _height, 0, timestampMs, tiles);
    }

    public EncodedFrame? EncodeCachedKeyframe(VideoEncoderOptions options, long timestampMs)
    {
        if (_previous is null || options.Width != _width || options.Height != _height)
            return null;

        var jpeg = JpegCodec.Encode(_previous, _width, _height, _width * 4, _width, _height, options.Quality);
        if (jpeg is null)
            return null;

        _framesSinceKeyframe = 0;
        return EncodedFrame.Keyframe(jpeg, _width, _height, 0, timestampMs);
    }

    private bool PrepareCurrent(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        var rowBytes = _width * 4;

        if (width == _width && height == _height)
        {
            if (stride == rowBytes)
            {
                bgra[.._current.Length].CopyTo(_current);
                return true;
            }

            for (var y = 0; y < _height; y++)
                bgra.Slice(y * stride, rowBytes).CopyTo(_current.AsSpan(y * rowBytes));
            return true;
        }

        if (_scaleTarget is null || _scaleTarget.Width != _width || _scaleTarget.Height != _height)
        {
            _scaleTarget?.Dispose();
            _scaleTarget = new SKBitmap(new SKImageInfo(_width, _height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        }

        fixed (byte* p = bgra)
        {
            using var source = new SKBitmap();
            if (!source.InstallPixels(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque),
                    (IntPtr)p, stride))
                return false;

            if (!source.ScalePixels(_scaleTarget, Sampling))
                return false;
        }

        var pixels = _scaleTarget.GetPixelSpan();
        if (_scaleTarget.RowBytes == rowBytes)
        {
            pixels[.._current.Length].CopyTo(_current);
            return true;
        }

        for (var y = 0; y < _height; y++)
            pixels.Slice(y * _scaleTarget.RowBytes, rowBytes).CopyTo(_current.AsSpan(y * rowBytes));
        return true;
    }

    /// <summary>
    /// Returns changed tiles, an empty list when nothing changed, or a non-empty list plus a
    /// keyframe recommendation when so much of the canvas changed that a full frame is smaller.
    /// </summary>
    private List<VideoTile> CollectDirtyTiles(int quality, out bool needsKeyframe)
    {
        needsKeyframe = false;
        var tiles = new List<VideoTile>();
        var dirty = new List<(int X, int Y, int Width, int Height)>();
        var totalTiles = ((_width + TileSize - 1) / TileSize) * ((_height + TileSize - 1) / TileSize);
        var limit = Math.Max(1, (int)(totalTiles * KeyframeDirtyFraction));
        var rowBytes = _width * 4;

        fixed (byte* previous = _previous, current = _current)
        {
            for (var y = 0; y < _height; y += TileSize)
            {
                var tileHeight = Math.Min(TileSize, _height - y);
                for (var x = 0; x < _width; x += TileSize)
                {
                    var tileWidth = Math.Min(TileSize, _width - x);
                    if (!RegionDiffers(previous, current, rowBytes, x, y, tileWidth, tileHeight))
                        continue;

                    dirty.Add((x, y, tileWidth, tileHeight));
                    if (dirty.Count > limit)
                    {
                        needsKeyframe = true;
                        return tiles;
                    }
                }
            }

            foreach (var (x, y, tileWidth, tileHeight) in dirty)
            {
                var jpeg = JpegCodec.Encode((IntPtr)(current + (long)y * rowBytes + x * 4),
                    tileWidth, tileHeight, rowBytes, tileWidth, tileHeight, quality);
                if (jpeg is null)
                {
                    needsKeyframe = true;
                    return tiles;
                }

                tiles.Add(new VideoTile(x, y, tileWidth, tileHeight, jpeg));
            }
        }

        return tiles;
    }

    private static bool RegionDiffers(byte* previous, byte* current, int rowBytes, int x, int y, int width, int height)
    {
        for (var row = 0; row < height; row++)
        {
            var previousRow = (uint*)(previous + (long)(y + row) * rowBytes + x * 4);
            var currentRow = (uint*)(current + (long)(y + row) * rowBytes + x * 4);
            for (var column = 0; column < width; column++)
            {
                // Alpha never reaches the JPEG, so only compare BGR to avoid repainting a tile
                // when a capture backend changes alpha only.
                if ((previousRow[column] & 0x00FFFFFF) != (currentRow[column] & 0x00FFFFFF))
                    return true;
            }
        }

        return false;
    }

    private void CopyTilesToPrevious(List<VideoTile> tiles)
    {
        var rowBytes = _width * 4;
        fixed (byte* previous = _previous, current = _current)
        {
            foreach (var tile in tiles)
            {
                var bytes = tile.Width * 4;
                for (var row = 0; row < tile.Height; row++)
                {
                    var offset = (long)(tile.Y + row) * rowBytes + tile.X * 4;
                    Buffer.MemoryCopy(current + offset, previous + offset, bytes, bytes);
                }
            }
        }
    }

    private EncodedFrame? BuildKeyframe(int quality, long timestampMs)
    {
        var jpeg = JpegCodec.Encode(_current, _width, _height, _width * 4, _width, _height, quality);
        if (jpeg is null || _previous is null)
            return null;

        Buffer.BlockCopy(_current, 0, _previous, 0, _current.Length);
        _framesSinceKeyframe = 0;
        return EncodedFrame.Keyframe(jpeg, _width, _height, 0, timestampMs);
    }

    public void Dispose()
    {
        _scaleTarget?.Dispose();
        _scaleTarget = null;
    }
}
