using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace DeskBeam.Client.Rendering;

/// <summary>
/// Rotates a small set of WriteableBitmaps so each frame invalidates the Image control without
/// allocating a new bitmap 60 times per second.
/// </summary>
public sealed class FramePresenter
{
    private readonly WriteableBitmap?[] _buffers = new WriteableBitmap?[3];
    private int _index;
    private int _width;
    private int _height;

    public WriteableBitmap Present(int width, int height, byte[] bgra)
    {
        if (width != _width || height != _height)
        {
            foreach (var buffer in _buffers)
                buffer?.Dispose();
            Array.Clear(_buffers);
            _width = width;
            _height = height;
            _index = 0;
        }

        var bitmap = _buffers[_index];
        if (bitmap is null)
        {
            bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _buffers[_index] = bitmap;
        }

        var rowBytes = width * 4;
        using (var framebuffer = bitmap.Lock())
        {
            if (framebuffer.RowBytes == rowBytes)
            {
                Marshal.Copy(bgra, 0, framebuffer.Address, rowBytes * height);
            }
            else
            {
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(bgra, y * rowBytes, framebuffer.Address + y * framebuffer.RowBytes, rowBytes);
                }
            }
        }

        _index = (_index + 1) % _buffers.Length;
        return bitmap;
    }
}
