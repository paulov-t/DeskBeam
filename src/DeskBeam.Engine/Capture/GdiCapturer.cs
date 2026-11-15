using System.Buffers;
using System.Runtime.InteropServices;
using DeskBeam.Core.Models;

namespace DeskBeam.Engine.Capture;

/// <summary>
/// GDI BitBlt fallback for machines where Desktop Duplication is unavailable.
/// Slower than DXGI, so it is only used when necessary.
/// </summary>
public sealed class GdiCapturer : ICapturer
{
    private readonly IntPtr _screenDc;
    private readonly IntPtr _memoryDc;
    private readonly IntPtr _dib;
    private readonly IntPtr _oldObject;
    private readonly IntPtr _dibBits;
    private readonly int _stride;
    private readonly CursorOverlay? _cursor;
    private bool _disposed;

    public GdiCapturer(MonitorDescriptor monitor, bool includeCursor)
    {
        Monitor = monitor;
        var width = Math.Max(1, monitor.Width);
        var height = Math.Max(1, monitor.Height);
        _stride = width * 4;

        _screenDc = NativeMethods.GetDC(IntPtr.Zero);
        _memoryDc = NativeMethods.CreateCompatibleDC(_screenDc);
        var bmi = NativeMethods.CreateTopDownBitmapInfo(width, height);
        _dib = NativeMethods.CreateDIBSection(_screenDc, ref bmi, 0, out _dibBits, IntPtr.Zero, 0);
        _oldObject = NativeMethods.SelectObject(_memoryDc, _dib);

        if (includeCursor)
            _cursor = new CursorOverlay(monitor.X, monitor.Y, width, height);
    }

    public MonitorDescriptor Monitor { get; }

    public CaptureApi Backend => CaptureApi.Gdi;

    public static IReadOnlyList<MonitorDescriptor> EnumerateMonitors()
    {
        var result = new List<MonitorDescriptor>();
        var index = 0;
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc,
            ref NativeMethods.Rect rect, IntPtr data) =>
        {
            var info = new NativeMethods.MonitorInfoEx
            {
                Size = Marshal.SizeOf<NativeMethods.MonitorInfoEx>(),
                DeviceName = string.Empty,
            };

            if (NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                result.Add(new MonitorDescriptor(
                    index++,
                    info.DeviceName,
                    NativeMethods.GetFriendlyMonitorName(info.DeviceName),
                    info.Monitor.Left,
                    info.Monitor.Top,
                    info.Monitor.Width,
                    info.Monitor.Height));
            }

            return true;
        }, IntPtr.Zero);

        return result;
    }

    public RawFrame? Capture(int timeoutMs, int maxWidth, int maxHeight)
    {
        if (_disposed)
            return null;

        var width = Monitor.Width;
        var height = Monitor.Height;
        if (width <= 0 || height <= 0)
            return null;

        if (!NativeMethods.BitBlt(_memoryDc, 0, 0, width, height, _screenDc, Monitor.X, Monitor.Y, 0x00CC0020))
            return null;

        var buffer = ArrayPool<byte>.Shared.Rent(_stride * height);
        var span = buffer.AsSpan(0, _stride * height);

        unsafe
        {
            var source = new ReadOnlySpan<byte>((void*)_dibBits, _stride * height);
            source.CopyTo(span);
        }

        var frame = new RawFrame(buffer, width, height, _stride, 0, b => ArrayPool<byte>.Shared.Return(b));
        _cursor?.Draw(frame.Pixels, width, height, _stride);
        return frame;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _cursor?.Dispose();
        if (_oldObject != IntPtr.Zero)
            NativeMethods.SelectObject(_memoryDc, _oldObject);
        if (_dib != IntPtr.Zero)
            NativeMethods.DeleteObject(_dib);
        if (_memoryDc != IntPtr.Zero)
            NativeMethods.DeleteDC(_memoryDc);
        if (_screenDc != IntPtr.Zero)
            NativeMethods.ReleaseDC(IntPtr.Zero, _screenDc);
    }
}
