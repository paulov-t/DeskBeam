using System.Runtime.InteropServices;

namespace DeskBeam.Engine.Capture;

/// <summary>
/// Draws the real Windows cursor into the captured BGRA frame, because DXGI Desktop Duplication
/// (like BitBlt) excludes the cursor. The cursor is rendered once per shape into a magenta-keyed
/// DIB and blended at the current position each frame.
/// </summary>
internal sealed unsafe class CursorOverlay : IDisposable
{
    private const int Size = 64;
    private const uint Magenta = 0xFFFF00FF;

    private readonly IntPtr _screenDc;
    private readonly IntPtr _memDc;
    private readonly IntPtr _dib;
    private readonly IntPtr _oldObject;
    private readonly IntPtr _dibBits;
    private readonly byte[] _pixels = new byte[Size * Size * 4];
    private readonly int _monitorX;
    private readonly int _monitorY;
    private readonly int _monitorWidth;
    private readonly int _monitorHeight;

    private IntPtr _cachedCursor;
    private int _cursorWidth;
    private int _cursorHeight;
    private int _hotspotX;
    private int _hotspotY;

    public CursorOverlay(int monitorX, int monitorY, int monitorWidth, int monitorHeight)
    {
        _monitorX = monitorX;
        _monitorY = monitorY;
        _monitorWidth = monitorWidth;
        _monitorHeight = monitorHeight;

        _screenDc = NativeMethods.GetDC(IntPtr.Zero);
        _memDc = NativeMethods.CreateCompatibleDC(_screenDc);

        var bmi = NativeMethods.CreateTopDownBitmapInfo(Size, Size);
        _dib = NativeMethods.CreateDIBSection(_screenDc, ref bmi, 0, out _dibBits, IntPtr.Zero, 0);
        _oldObject = NativeMethods.SelectObject(_memDc, _dib);
    }

    public void Draw(byte[] frame, int width, int height, int stride, float scale = 1f)
    {
        var info = new NativeMethods.CursorInfo { Size = Marshal.SizeOf<NativeMethods.CursorInfo>() };
        if (!NativeMethods.GetCursorInfo(ref info) || (info.Flags & NativeMethods.CursorShowing) == 0)
            return;

        var relativeX = info.ScreenPosition.X - _monitorX;
        var relativeY = info.ScreenPosition.Y - _monitorY;
        if (relativeX < -Size || relativeY < -Size || relativeX > _monitorWidth + Size || relativeY > _monitorHeight + Size)
            return;

        if (info.Cursor != _cachedCursor)
        {
            if (!RefreshCursor(info.Cursor))
                return;
        }

        if (_cursorWidth <= 0 || _cursorHeight <= 0)
            return;

        scale = Math.Clamp(scale, 0.01f, 8f);
        var destX = (int)MathF.Round(relativeX * scale) - (int)MathF.Round(_hotspotX * scale);
        var destY = (int)MathF.Round(relativeY * scale) - (int)MathF.Round(_hotspotY * scale);
        var targetWidth = Math.Max(1, (int)MathF.Round(_cursorWidth * scale));
        var targetHeight = Math.Max(1, (int)MathF.Round(_cursorHeight * scale));

        fixed (byte* framePtr = frame)
        {
            fixed (byte* pixelsPtr = _pixels)
            {
                for (var y = 0; y < targetHeight; y++)
                {
                    var py = destY + y;
                    if (py < 0 || py >= height)
                        continue;

                    var sourceY = Math.Min(_cursorHeight - 1, (int)(y / scale));
                    var sourceRow = pixelsPtr + sourceY * Size * 4;
                    var destRow = framePtr + py * stride;

                    for (var x = 0; x < targetWidth; x++)
                    {
                        var px = destX + x;
                        if (px < 0 || px >= width)
                            continue;

                        var sourceX = Math.Min(_cursorWidth - 1, (int)(x / scale));
                        var source = (uint*)(sourceRow + sourceX * 4);
                        if (*source == Magenta)
                            continue;

                        var destination = (uint*)(destRow + px * 4);
                        *destination = *source | 0xFF000000;
                    }
                }
            }
        }
    }

    private bool RefreshCursor(IntPtr cursor)
    {
        _cachedCursor = cursor;
        _cursorWidth = 0;
        _cursorHeight = 0;

        var info = new NativeMethods.IconInfo();
        if (!NativeMethods.GetIconInfo(cursor, ref info))
            return false;

        try
        {
            var iconWidth = 0;
            var iconHeight = 0;

            if (info.ColorBitmap != IntPtr.Zero)
            {
                var bitmap = new NativeMethods.Bitmap();
                if (NativeMethods.GetObject(info.ColorBitmap, Marshal.SizeOf<NativeMethods.Bitmap>(), ref bitmap) > 0)
                {
                    iconWidth = bitmap.Width;
                    iconHeight = bitmap.Height;
                }
            }

            if (iconWidth <= 0 || iconHeight <= 0)
            {
                iconWidth = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SmCxCursor));
                iconHeight = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SmCyCursor));
            }

            iconWidth = Math.Min(iconWidth, Size);
            iconHeight = Math.Min(iconHeight, Size);

            var span = new Span<uint>((void*)_dibBits, Size * Size);
            span.Fill(Magenta);

            if (!NativeMethods.DrawIconEx(_memDc, 0, 0, cursor, iconWidth, iconHeight, 0, IntPtr.Zero,
                    NativeMethods.DiNormal))
                return false;

            var source = new ReadOnlySpan<byte>((void*)_dibBits, Size * Size * 4);
            source.CopyTo(_pixels);

            _cursorWidth = iconWidth;
            _cursorHeight = iconHeight;
            _hotspotX = Math.Clamp(info.HotspotX, 0, iconWidth);
            _hotspotY = Math.Clamp(info.HotspotY, 0, iconHeight);
            return true;
        }
        finally
        {
            if (info.ColorBitmap != IntPtr.Zero)
                NativeMethods.DeleteObject(info.ColorBitmap);
            if (info.MaskBitmap != IntPtr.Zero)
                NativeMethods.DeleteObject(info.MaskBitmap);
        }
    }

    public void Dispose()
    {
        if (_oldObject != IntPtr.Zero)
            NativeMethods.SelectObject(_memDc, _oldObject);
        if (_dib != IntPtr.Zero)
            NativeMethods.DeleteObject(_dib);
        if (_memDc != IntPtr.Zero)
            NativeMethods.DeleteDC(_memDc);
        if (_screenDc != IntPtr.Zero)
            NativeMethods.ReleaseDC(IntPtr.Zero, _screenDc);
    }
}
