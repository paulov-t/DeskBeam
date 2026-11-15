using SkiaSharp;

namespace DeskBeam.Core.Video;

/// <summary>Scales and JPEG-encodes BGRA frames using SkiaSharp, and decodes them back.</summary>
public static unsafe class JpegCodec
{
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    /// <summary>Scales (if needed) and JPEG-encodes a native BGRA buffer. Returns null on failure.</summary>
    public static byte[]? Encode(IntPtr bgra, int width, int height, int stride, int outWidth, int outHeight, int quality)
    {
        if (width <= 0 || height <= 0 || outWidth <= 0 || outHeight <= 0)
            return null;

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var source = new SKBitmap();
        if (!source.InstallPixels(info, bgra, stride))
            return null;

        SKBitmap? scaled = null;
        try
        {
            var target = source;
            if (outWidth != width || outHeight != height)
            {
                scaled = new SKBitmap(new SKImageInfo(outWidth, outHeight, SKColorType.Bgra8888, SKAlphaType.Opaque));
                if (!source.ScalePixels(scaled, Sampling))
                    return null;
                target = scaled;
            }

            using var data = target.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 10, 95));
            return data?.ToArray();
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    public static byte[]? Encode(byte[] bgra, int width, int height, int stride, int outWidth, int outHeight, int quality)
    {
        fixed (byte* p = bgra)
        {
            return Encode((IntPtr)p, width, height, stride, outWidth, outHeight, quality);
        }
    }

    /// <summary>Computes an even-dimensioned output size that fits within the maximum bounds.</summary>
    public static (int Width, int Height) FitSize(int width, int height, int maxWidth, int maxHeight)
    {
        if (maxWidth <= 0) maxWidth = width;
        if (maxHeight <= 0) maxHeight = height;

        var scale = Math.Min(1.0, Math.Min(maxWidth / (double)width, maxHeight / (double)height));
        var w = Math.Max(2, (int)Math.Round(width * scale));
        var h = Math.Max(2, (int)Math.Round(height * scale));
        if ((w & 1) != 0) w--;
        if ((h & 1) != 0) h--;
        return (w, h);
    }
}

/// <summary>
/// Reusable JPEG decoder that keeps a single BGRA bitmap buffer alive between frames.
/// Not thread-safe; create one instance per decoding consumer.
/// </summary>
public sealed class JpegDecoder : IDisposable
{
    private SKBitmap? _bitmap;
    private byte[] _buffer = [];

    public bool TryDecode(byte[] jpeg, out int width, out int height, out byte[] bgra)
    {
        width = 0;
        height = 0;
        bgra = _buffer;

        try
        {
            using var data = SKData.CreateCopy(jpeg);
            using var codec = SKCodec.Create(data);
            if (codec is null)
                return false;

            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            if (info.Width <= 0 || info.Height <= 0)
                return false;

            if (_bitmap is null || _bitmap.Width != info.Width || _bitmap.Height != info.Height)
            {
                _bitmap?.Dispose();
                _bitmap = new SKBitmap(info);
                _buffer = new byte[(int)_bitmap.ByteCount];
            }

            // A truncated JPEG decodes "successfully" but with missing chroma, which shows up as
            // pink blocks, so only fully decoded frames are accepted.
            var result = codec.GetPixels(info, _bitmap.GetPixels());
            if (result != SKCodecResult.Success)
                return false;

            _bitmap.GetPixelSpan().CopyTo(_buffer);
            width = info.Width;
            height = info.Height;
            bgra = _buffer;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose() => _bitmap?.Dispose();
}
