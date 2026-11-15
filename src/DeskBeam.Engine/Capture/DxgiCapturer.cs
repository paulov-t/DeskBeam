using System.Buffers;
using System.Diagnostics;
using DeskBeam.Core.Models;
using DeskBeam.Core.Video;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DeskBeam.Engine.Capture;

/// <summary>
/// DXGI Desktop Duplication capture. This is the primary path: frames stay on the GPU until
/// they are copied into a reusable CPU buffer, so it scales from 720p up to 4K at high refresh.
/// </summary>
public sealed class DxgiCapturer : ICapturer
{
    private const int WaitTimeout = unchecked((int)0x887A0027);
    private const int AccessLost = unchecked((int)0x887A0022);
    private const int DeviceRemoved = unchecked((int)0x887A0005);

    private readonly int _monitorIndex;
    private readonly string? _deviceName;
    private readonly bool _includeCursor;
    private readonly Action<string>? _log;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIAdapter? _adapter;
    private IDXGIOutput1? _output;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _staging;
    private CursorOverlay? _cursor;
    private HdrConverter? _hdrConverter;
    private int _stagingWidth;
    private int _stagingHeight;
    private Format _stagingFormat;
    private long _retryAfterTicks;
    private bool _disposed;
    private bool _pqOutput;
    private float _sdrWhiteNits = 200f;
    private readonly HashSet<Format> _loggedFormats = [];

    /// <summary>Format of the most recently captured desktop surface (diagnostics).</summary>
    public Format LastSurfaceFormat { get; private set; } = Format.Unknown;

    public DxgiCapturer(int monitorIndex, string? deviceName, bool includeCursor, Action<string>? log = null)
    {
        _monitorIndex = monitorIndex;
        _deviceName = deviceName;
        _includeCursor = includeCursor;
        _log = log;
        Initialize();
    }

    public MonitorDescriptor Monitor { get; private set; } = new(0, string.Empty, string.Empty, 0, 0, 0, 0);

    public CaptureApi Backend => CaptureApi.DesktopDuplication;

    public static IReadOnlyList<MonitorDescriptor> EnumerateMonitors()
    {
        var result = new List<MonitorDescriptor>();

        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        IDXGIAdapter? adapter = null;
        try
        {
            ID3D11Device createdDevice;
            ID3D11DeviceContext createdContext;
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0], out createdDevice, out createdContext);
            device = createdDevice;
            context = createdContext;

            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            adapter = dxgiDevice.GetAdapter();

            for (uint i = 0; i < 16; i++)
            {
                if (adapter.EnumOutputs(i, out var output).Failure)
                    break;

                using (output)
                {
                    var desc = output.Description;
                    var rect = desc.DesktopCoordinates;
                    var width = rect.Right - rect.Left;
                    var height = rect.Bottom - rect.Top;
                    if (width <= 0 || height <= 0)
                        continue;

                    result.Add(new MonitorDescriptor(
                        (int)i,
                        desc.DeviceName,
                        NativeMethods.GetFriendlyMonitorName(desc.DeviceName),
                        rect.Left,
                        rect.Top,
                        width,
                        height));
                }
            }
        }
        finally
        {
            adapter?.Dispose();
            context?.Dispose();
            device?.Dispose();
        }

        return result;
    }

    private void Initialize()
    {
        ReleaseGraphics();

        ID3D11Device device;
        ID3D11DeviceContext context;
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0], out device, out context);

        _device = device;
        _context = context;

        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        _adapter = dxgiDevice.GetAdapter();

        IDXGIOutput? output = null;
        var outputIndex = _monitorIndex;

        // Prefer matching the output by device name so the selection survives ordering changes.
        for (uint i = 0; i < 16; i++)
        {
            if (_adapter.EnumOutputs(i, out var candidate).Failure)
                break;

            if (!string.IsNullOrEmpty(_deviceName) &&
                string.Equals(candidate.Description.DeviceName, _deviceName, StringComparison.OrdinalIgnoreCase))
            {
                output = candidate;
                outputIndex = (int)i;
                break;
            }

            candidate.Dispose();
        }

        if (output is null)
        {
            if (_adapter.EnumOutputs((uint)_monitorIndex, out output).Failure)
            {
                output?.Dispose();
                throw new InvalidOperationException($"Monitor index {_monitorIndex} is not available through DXGI.");
            }

            outputIndex = _monitorIndex;
        }

        _output = output.QueryInterface<IDXGIOutput1>();
        output.Dispose();

        _duplication = _output.DuplicateOutput(device);

        var desc = _output.Description;
        var bounds = desc.DesktopCoordinates;
        Monitor = new MonitorDescriptor(
            outputIndex,
            desc.DeviceName,
            NativeMethods.GetFriendlyMonitorName(desc.DeviceName),
            bounds.Left,
            bounds.Top,
            bounds.Right - bounds.Left,
            bounds.Bottom - bounds.Top);

        if (_includeCursor)
            _cursor = new CursorOverlay(Monitor.X, Monitor.Y, Monitor.Width, Monitor.Height);

        _sdrWhiteNits = DisplayColorInfo.GetSdrWhiteNits(Monitor.DeviceName);
        _pqOutput = false;
        using (var output6 = _output.QueryInterfaceOrNull<IDXGIOutput6>())
        {
            if (output6 is not null)
                _pqOutput = output6.Description1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
        }

        _log?.Invoke($"DXGI capture ready: {Monitor.Display} ({Monitor.Width}×{Monitor.Height}).");
    }

    public unsafe RawFrame? Capture(int timeoutMs, int maxWidth, int maxHeight)
    {
        if (_disposed)
            return null;

        if (_duplication is null || _context is null || _device is null)
        {
            if (Environment.TickCount64 < _retryAfterTicks)
                return null;

            try
            {
                Initialize();
            }
            catch (Exception ex)
            {
                LogReinitFailure(ex);
                _retryAfterTicks = Environment.TickCount64 + 1500;
                return null;
            }
        }

        Result result;
        OutduplFrameInfo frameInfo;
        IDXGIResource? resource = null;
        try
        {
            result = _duplication!.AcquireNextFrame((uint)Math.Clamp(timeoutMs, 0, 100), out frameInfo, out resource);
        }
        catch (SharpGenException ex)
        {
            HandleCaptureFailure(ex.HResult);
            return null;
        }

        if (result.Failure)
        {
            if (result.Code == WaitTimeout)
                return null;

            HandleCaptureFailure(result.Code);
            return null;
        }

        RawFrame? frame = null;
        try
        {
            using var texture = resource!.QueryInterface<ID3D11Texture2D>();
            var textureDesc = texture.Description;

            var width = (int)textureDesc.Width;
            var height = (int)textureDesc.Height;
            if (width <= 0 || height <= 0)
                return null;

            frame = ReadFrame(texture, width, height, textureDesc.Format, maxWidth, maxHeight);
        }
        finally
        {
            resource?.Dispose();
            try
            {
                _duplication?.ReleaseFrame();
            }
            catch (SharpGenException)
            {
                // Frame already released or lost.
            }
        }

        return frame;
    }

    private unsafe RawFrame? ReadFrame(ID3D11Texture2D texture, int width, int height, Format format,
        int maxWidth, int maxHeight)
    {
        LastSurfaceFormat = format;

        switch (format)
        {
            case Format.R16G16B16A16_Float:
                LogFormat(format, "HDR scRGB (FP16)");
                return ConvertHdr(texture, width, height, format, maxWidth, maxHeight, HdrMode.ScRgb);

            case Format.R10G10B10A2_UNorm:
                LogFormat(format, _pqOutput ? "HDR10 (PQ)" : "10-bit SDR");
                return ConvertHdr(texture, width, height, format, maxWidth, maxHeight,
                    _pqOutput ? HdrMode.Pq : HdrMode.PassThrough);

            default:
                LogFormat(format, "SDR BGRA");
                return ReadSdr(texture, width, height);
        }
    }

    private void LogFormat(Format format, string description)
    {
        if (!_loggedFormats.Add(format))
            return;

        var detail = description.Contains("HDR", StringComparison.Ordinal)
            ? $", tone-mapping to SDR (SDR white {_sdrWhiteNits:F0} nits)"
            : string.Empty;
        _log?.Invoke($"Desktop surface: {description} ({format}){detail}.");
    }

    private RawFrame? ConvertHdr(ID3D11Texture2D texture, int width, int height, Format format, int maxWidth,
        int maxHeight, HdrMode mode)
    {
        try
        {
            _hdrConverter ??= new HdrConverter(_device!, _log);
        }
        catch (Exception ex)
        {
            _log?.Invoke("HDR conversion unavailable: " + ex.Message);
            return null;
        }

        var (outputWidth, outputHeight) = JpegCodec.FitSize(width, height, maxWidth, maxHeight);
        var frame = _hdrConverter.Convert(_context!, texture, width, height, format, outputWidth, outputHeight,
            mode, _sdrWhiteNits);

        if (frame is not null)
            DrawCursor(frame, width);

        return frame;
    }

    private unsafe RawFrame? ReadSdr(ID3D11Texture2D texture, int width, int height)
    {
        EnsureStaging(width, height, texture.Description.Format);
        _context!.CopyResource(_staging!, texture);

        var mapped = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var stride = width * 4;
            var buffer = ArrayPool<byte>.Shared.Rent(stride * height);
            var destination = buffer.AsSpan(0, stride * height);

            for (var y = 0; y < height; y++)
            {
                var sourceRow = (byte*)mapped.DataPointer + (long)y * mapped.RowPitch;
                new ReadOnlySpan<byte>(sourceRow, stride).CopyTo(destination.Slice(y * stride, stride));
            }

            var frame = new RawFrame(buffer, width, height, stride, 0, b => ArrayPool<byte>.Shared.Return(b));
            DrawCursor(frame, width);
            return frame;
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }
    }

    private void DrawCursor(RawFrame frame, int sourceWidth)
    {
        if (_cursor is null || sourceWidth <= 0)
            return;

        var scale = frame.Width / (float)sourceWidth;
        _cursor.Draw(frame.Pixels, frame.Width, frame.Height, frame.Stride, scale);
    }

    private void EnsureStaging(int width, int height, Format format)
    {
        if (_staging is not null && _stagingWidth == width && _stagingHeight == height && _stagingFormat == format)
            return;

        _staging?.Dispose();
        _stagingWidth = width;
        _stagingHeight = height;
        _stagingFormat = format;

        _staging = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
    }

    private void HandleCaptureFailure(int code)
    {
        var message = DescribeHResult(code);
        if (code == WaitTimeout)
            return;

        _log?.Invoke($"DXGI capture lost ({message}); recreating duplication.");
        ReleaseGraphics();
        _retryAfterTicks = Environment.TickCount64 + 500;
    }

    private long _lastReinitLogTicks;

    private void LogReinitFailure(Exception ex)
    {
        var now = Environment.TickCount64;
        if (now - _lastReinitLogTicks < 5000)
            return;
        _lastReinitLogTicks = now;

        var detail = ex is SharpGenException sharp
            ? $"{DescribeHResult(sharp.HResult)}: {ex.Message}"
            : ex.Message;
        _log?.Invoke("DXGI re-initialization failed: " + detail);
    }

    private static string DescribeHResult(int code)
    {
        var name = code switch
        {
            AccessLost => "DXGI_ERROR_ACCESS_LOST",
            DeviceRemoved => "DXGI_ERROR_DEVICE_REMOVED",
            WaitTimeout => "DXGI_ERROR_WAIT_TIMEOUT",
            unchecked((int)0x887A0001) => "DXGI_ERROR_INVALID_CALL",
            unchecked((int)0x887A0002) => "DXGI_ERROR_NOT_FOUND",
            unchecked((int)0x887A0020) => "DXGI_ERROR_UNSUPPORTED",
            unchecked((int)0x887A002B) => "DXGI_ERROR_SESSION_DISCONNECTED",
            _ => "HRESULT",
        };
        return $"0x{code:X8} {name}";
    }

    private void ReleaseGraphics()
    {
        _cursor?.Dispose();
        _cursor = null;
        _hdrConverter?.Dispose();
        _hdrConverter = null;
        _staging?.Dispose();
        _staging = null;
        _stagingWidth = 0;
        _stagingHeight = 0;
        _duplication?.Dispose();
        _duplication = null;
        _output?.Dispose();
        _output = null;
        _adapter?.Dispose();
        _adapter = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ReleaseGraphics();
    }
}
