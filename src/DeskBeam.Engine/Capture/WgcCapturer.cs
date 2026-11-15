using System.Buffers;
using System.Runtime.InteropServices;
using DeskBeam.Core.Models;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace DeskBeam.Engine.Capture;

/// <summary>
/// Windows.Graphics.Capture (WGC) screen capture. WGC is composed by the desktop window manager,
/// so it keeps producing frames when a game switches resolution, refresh rate, HDR, or goes
/// fullscreen. That is the case where Desktop Duplication tends to return no frames at all.
/// </summary>
public sealed class WgcCapturer : ICapturer
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid CaptureItemInteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid DxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid ActivationFactoryIid = new("00000035-0000-0000-C000-000000000046");

    private static readonly object RoGate = new();
    private static bool _roInitialized;

    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _frameSignal = new(0, 1);
    private readonly bool _includeCursor;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private Windows.Graphics.SizeInt32 _poolSize;

    private Direct3D11CaptureFrame? _pendingFrame;
    private ID3D11Texture2D? _pendingTexture;

    private ID3D11Texture2D? _staging;
    private int _stagingWidth;
    private int _stagingHeight;
    private Format _stagingFormat;

    private bool _closed;
    private bool _disposed;

    public WgcCapturer(MonitorDescriptor monitor, bool includeCursor, Action<string>? log = null)
    {
        Monitor = monitor;
        _includeCursor = includeCursor;
        _log = log;

        EnsureWinRt();
        Initialize();
    }

    public MonitorDescriptor Monitor { get; private set; }

    public CaptureApi Backend => CaptureApi.WindowsGraphicsCapture;

    private static void EnsureWinRt()
    {
        lock (RoGate)
        {
            if (_roInitialized)
                return;

            _roInitialized = true;
            try
            {
                // S_FALSE means COM was already initialized on this thread. RPC_E_CHANGED_MODE is
                // harmless here: WinRT still works when the caller lives in an STA.
                RoInitialize(1);
            }
            catch (Exception)
            {
                // COM/WinRT will report a useful error if capture is really unavailable.
            }
        }
    }

    private void Initialize()
    {
        var flags = DeviceCreationFlags.BgraSupport;
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags,
            [FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context);
        _device = device;
        _context = context;

        using (var dxgiDevice = device.QueryInterface<IDXGIDevice>())
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var inspectable));
            try
            {
                _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            }
            finally
            {
                Marshal.Release(inspectable);
            }
        }

        var hmonitor = FindMonitor(Monitor);
        if (hmonitor == IntPtr.Zero)
            throw new InvalidOperationException($"Could not find a monitor matching {Monitor.DeviceName}.");

        _item = CreateItemForMonitor(hmonitor);
        _item.Closed += OnItemClosed;

        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            3,
            _poolSize);
        _pool.FrameArrived += OnFrameArrived;

        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = _includeCursor;
        try
        {
            _session.IsBorderRequired = false;
        }
        catch (Exception)
        {
            // Older Windows 10 builds always draw the capture border.
        }

        _session.StartCapture();

        Monitor = Monitor with { Width = _item.Size.Width, Height = _item.Size.Height };
        _log?.Invoke($"Windows Graphics Capture ready: {Monitor.Display} ({Monitor.Width}×{Monitor.Height})" +
                     (_includeCursor ? ", cursor included." : "."));
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        lock (_gate)
        {
            _closed = true;
        }
        SignalFrame();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Direct3D11CaptureFrame? frame = null;
        ID3D11Texture2D? texture = null;
        Direct3D11CaptureFrame? previousFrame = null;
        ID3D11Texture2D? previousTexture = null;

        try
        {
            frame = sender.TryGetNextFrame();
            if (frame is null)
                return;

            using var surface = frame.Surface;
            texture = GetTexture(surface);

            var content = frame.ContentSize;
            if (content.Width > 0 && content.Height > 0 &&
                (content.Width != _poolSize.Width || content.Height != _poolSize.Height))
            {
                // A resolution/mode change gives the item a new surface size. The frame pool
                // is bound to the old size, so ask the pipeline to build a fresh capturer.
                _log?.Invoke(
                    $"Windows Graphics Capture size changed from {_poolSize.Width}×{_poolSize.Height} " +
                    $"to {content.Width}×{content.Height}; recreating capture.");
                texture.Dispose();
                frame.Dispose();
                lock (_gate)
                {
                    _closed = true;
                }
                SignalFrame();
                return;
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke("Windows Graphics Capture frame error: " + ex.Message);
            texture?.Dispose();
            frame?.Dispose();
            lock (_gate)
            {
                _closed = true;
            }
            SignalFrame();
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                texture.Dispose();
                frame.Dispose();
                return;
            }

            previousFrame = _pendingFrame;
            previousTexture = _pendingTexture;
            _pendingFrame = frame;
            _pendingTexture = texture;
        }

        previousTexture?.Dispose();
        previousFrame?.Dispose();
        SignalFrame();
    }

    private void SignalFrame()
    {
        try
        {
            _frameSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A frame is already waiting; the newer one replaced it.
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    public unsafe RawFrame? Capture(int timeoutMs, int maxWidth, int maxHeight)
    {
        if (_disposed)
            return null;

        if (_closed)
            throw new InvalidOperationException("The Windows Graphics Capture item was closed (display changed or capture stopped).");

        // A display-mode change gives the item a new size. Recreate the whole capturer so the
        // frame pool matches the new surface instead of mixing sizes.
        var itemSize = _item?.Size ?? _poolSize;
        if (itemSize.Width != _poolSize.Width || itemSize.Height != _poolSize.Height)
            throw new InvalidOperationException(
                $"Display size changed from {_poolSize.Width}×{_poolSize.Height} to {itemSize.Width}×{itemSize.Height}.");

        if (!_frameSignal.Wait(Math.Clamp(timeoutMs, 0, 2000)))
            return null;

        Direct3D11CaptureFrame? frame;
        ID3D11Texture2D? texture;
        lock (_gate)
        {
            frame = _pendingFrame;
            texture = _pendingTexture;
            _pendingFrame = null;
            _pendingTexture = null;
            if (_closed)
            {
                texture?.Dispose();
                frame?.Dispose();
                throw new InvalidOperationException("The Windows Graphics Capture item was closed (display changed or capture stopped).");
            }
        }

        if (frame is null || texture is null)
            return null;

        try
        {
            var desc = texture.Description;
            var width = (int)desc.Width;
            var height = (int)desc.Height;
            if (width <= 0 || height <= 0)
                return null;

            var content = frame.ContentSize;
            if (content.Width > 0 && content.Height > 0 && content.Width < width)
                width = content.Width;
            if (content.Height > 0 && content.Height < height)
                height = content.Height;
            if (width <= 0 || height <= 0)
                return null;

            if (desc.Format != Format.B8G8R8A8_UNorm)
            {
                _log?.Invoke($"Windows Graphics Capture returned {desc.Format}; expected BGRA8.");
                SetClosed();
                return null;
            }

            EnsureStaging(width, height, desc.Format);
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

                var rawFrame = new RawFrame(buffer, width, height, stride, 0, b => ArrayPool<byte>.Shared.Return(b));
                if (width != Monitor.Width || height != Monitor.Height)
                    Monitor = Monitor with { Width = width, Height = height };
                return rawFrame;
            }
            finally
            {
                _context.Unmap(_staging!, 0);
            }
        }
        finally
        {
            texture.Dispose();
            frame.Dispose();
        }
    }

    private void SetClosed()
    {
        lock (_gate)
        {
            _closed = true;
        }
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

    private static IntPtr FindMonitor(MonitorDescriptor monitor)
    {
        var found = IntPtr.Zero;
        var fallback = IntPtr.Zero;
        var index = 0;

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hmonitor, IntPtr hdc, ref NativeMethods.Rect rect, IntPtr data) =>
        {
            if (index == monitor.Index && fallback == IntPtr.Zero)
                fallback = hmonitor;

            var info = new NativeMethods.MonitorInfoEx
            {
                Size = Marshal.SizeOf<NativeMethods.MonitorInfoEx>(),
                DeviceName = string.Empty,
            };
            if (NativeMethods.GetMonitorInfo(hmonitor, ref info) &&
                string.Equals(info.DeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                found = hmonitor;
                return false;
            }

            index++;
            return true;
        }, IntPtr.Zero);

        return found != IntPtr.Zero ? found : fallback;
    }

    private static unsafe GraphicsCaptureItem CreateItemForMonitor(IntPtr monitor)
    {
        var classId = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(classId, classId.Length, out var hstring));
        try
        {
            var activationIid = ActivationFactoryIid;
            var hr = RoGetActivationFactory(hstring, ref activationIid, out var factoryPtr);
            Marshal.ThrowExceptionForHR(hr);

            try
            {
                var qi = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)factoryPtr)[0];
                var interopIid = CaptureItemInteropIid;
                IntPtr interopPtr;
                hr = qi(factoryPtr, &interopIid, &interopPtr);
                Marshal.ThrowExceptionForHR(hr);

                try
                {
                    // IGraphicsCaptureItemInterop: IUnknown (0-2), CreateForWindow (3), CreateForMonitor (4).
                    var createForMonitor = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)interopPtr)[4];
                    var itemIid = GraphicsCaptureItemIid;
                    IntPtr itemPtr;
                    hr = createForMonitor(interopPtr, monitor, &itemIid, &itemPtr);
                    Marshal.ThrowExceptionForHR(hr);

                    try
                    {
                        return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
                    }
                    finally
                    {
                        Marshal.Release(itemPtr);
                    }
                }
                finally
                {
                    Marshal.Release(interopPtr);
                }
            }
            finally
            {
                Marshal.Release(factoryPtr);
            }
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    private static unsafe ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var surfacePtr = ((IWinRTObject)surface).NativeObject.ThisPtr;
        var qi = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)surfacePtr)[0];

        var accessIid = DxgiInterfaceAccessIid;
        IntPtr accessPtr;
        Marshal.ThrowExceptionForHR(qi(surfacePtr, &accessIid, &accessPtr));
        try
        {
            var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)accessPtr)[3];
            var textureIid = typeof(ID3D11Texture2D).GUID;
            IntPtr texturePtr;
            Marshal.ThrowExceptionForHR(getInterface(accessPtr, &textureIid, &texturePtr));
            return new ID3D11Texture2D(texturePtr);
        }
        finally
        {
            Marshal.Release(accessPtr);
        }
    }

    public void Dispose()
    {
        Direct3D11CaptureFrame? frame;
        ID3D11Texture2D? texture;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            frame = _pendingFrame;
            texture = _pendingTexture;
            _pendingFrame = null;
            _pendingTexture = null;
        }

        try
        {
            if (_pool is not null)
                _pool.FrameArrived -= OnFrameArrived;
            if (_item is not null)
                _item.Closed -= OnItemClosed;
            _session?.Dispose();
            _pool?.Dispose();
            frame?.Dispose();
            texture?.Dispose();
            _staging?.Dispose();
            _winrtDevice?.Dispose();
            _context?.Dispose();
            _device?.Dispose();
        }
        catch (Exception)
        {
            // Best-effort teardown; a display change can invalidate objects mid-dispose.
        }
        finally
        {
            _session = null;
            _pool = null;
            _item = null;
            _staging = null;
            _winrtDevice = null;
            _context = null;
            _device = null;
            _frameSignal.Dispose();
        }
    }

    [DllImport("combase.dll")]
    private static extern int RoInitialize(uint initType);

    [DllImport("combase.dll", EntryPoint = "RoGetActivationFactory")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll")]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length,
        out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}
