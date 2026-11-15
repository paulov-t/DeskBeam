using System.Diagnostics;
using DeskBeam.Core.Models;
using DeskBeam.Core.Video;
using DeskBeam.Engine.Capture;
using static DeskBeam.Engine.Capture.ScreenCaptureFactory;

namespace DeskBeam.Engine.Streaming;

/// <summary>
/// Owns the capture thread and a single sequential encoder. Delta frames describe changes since the
/// previously encoded frame, so encoding must stay ordered; the mailbox drops stale captures instead,
/// which keeps latency bounded. A cached keyframe lets a viewer joining an idle desktop get a full
/// picture immediately instead of waiting for screen motion.
/// </summary>
public sealed class EncodingPipeline : IAsyncDisposable
{
    private readonly FrameBroadcaster _hub;
    private readonly Action<string>? _log;
    private readonly RawFrameMailbox _mailbox = new();
    private readonly PipelineMetrics _metrics = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly JpegTileEncoder _encoder = new();
    private readonly object _publishGate = new();
    private readonly SemaphoreSlim _keyframeSignal = new(0, 1);

    private Task? _captureTask;
    private Task? _encodeTask;
    private Task? _keyframeTask;
    private volatile ICapturer? _capturer;
    private int _activeMonitorIndex = -1;
    private bool _activeIncludeCursor;
    private CaptureApi _captureApi = CaptureApi.Auto;
    private CaptureApi _activeCaptureApi = CaptureApi.Auto;
    private volatile bool _hasViewers;

    private volatile int _maxWidth = 1280;
    private volatile int _maxHeight = 720;
    private volatile int _fps = 60;
    private volatile int _quality = 65;
    private volatile bool _includeCursor = true;
    private volatile int _monitorIndex;
    private int _sequence;
    private int _forceKeyframe = 1;
    private ulong _lastFingerprint;
    private long _lastPublishedMs;
    private volatile bool _forcePublish = true;

    public EncodingPipeline(FrameBroadcaster hub, Action<string>? log)
    {
        _hub = hub;
        _log = log;
    }

    public PipelineMetrics Metrics => _metrics;

    public MonitorDescriptor? CurrentMonitor => _capturer?.Monitor;

    public bool HasViewers
    {
        get => _hasViewers;
        set
        {
            if (value && !_hasViewers)
                _forcePublish = true;
            _hasViewers = value;
        }
    }

    public void Configure(HostOptions options)
    {
        _monitorIndex = options.MonitorIndex;
        _maxWidth = options.MaxWidth;
        _maxHeight = options.MaxHeight;
        _fps = options.Fps;
        _quality = options.Quality;
        _includeCursor = options.IncludeCursor;
        _captureApi = options.CaptureApi;
    }

    public void ConfigureStream(int width, int height, int fps, int quality)
    {
        _maxWidth = Math.Clamp(width, 320, 3840);
        _maxHeight = Math.Clamp(height, 180, 2160);
        _fps = Math.Clamp(fps, 5, 120);
        _quality = Math.Clamp(quality, 20, 90);
        _forcePublish = true;
        Interlocked.Exchange(ref _forceKeyframe, 1);

        // Re-emit the cached canvas only when its size still matches; otherwise the next captured
        // frame becomes the keyframe at the new size.
        var (outputWidth, outputHeight) = GetOutputSize();
        if (_encoder.CanvasWidth == outputWidth && _encoder.CanvasHeight == outputHeight)
            RequestKeyframe();
    }

    /// <summary>Wakes the encoder so it re-emits the last canvas as a full keyframe.</summary>
    public void RequestKeyframe()
    {
        try
        {
            if (_keyframeSignal.CurrentCount == 0)
                _keyframeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A keyframe is already pending.
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    public (int Width, int Height) GetOutputSize()
    {
        var monitor = _capturer?.Monitor;
        return monitor is null
            ? (Math.Max(2, _maxWidth), Math.Max(2, _maxHeight))
            : JpegCodec.FitSize(monitor.Width, monitor.Height, _maxWidth, _maxHeight);
    }

    public void Start()
    {
        _captureTask = Task.Run(CaptureLoopAsync);
        _encodeTask = Task.Run(EncodeLoopAsync);
        _keyframeTask = Task.Run(KeyframeLoopAsync);
    }

    private async Task CaptureLoopAsync()
    {
        var clock = Stopwatch.StartNew();
        var nextDueMs = 0.0;

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (!_hasViewers)
                {
                    nextDueMs = 0;
                    await Task.Delay(80, _cts.Token).ConfigureAwait(false);
                    continue;
                }

                if (_capturer is null || _activeMonitorIndex != _monitorIndex || _activeIncludeCursor != _includeCursor ||
                    _activeCaptureApi != _captureApi)
                {
                    RecreateCapturer();
                    if (_capturer is null)
                    {
                        await Task.Delay(1000, _cts.Token).ConfigureAwait(false);
                        continue;
                    }
                }

                var interval = 1000.0 / Math.Clamp(_fps, 5, 120);
                var now = clock.Elapsed.TotalMilliseconds;
                if (now < nextDueMs)
                {
                    var wait = (int)Math.Clamp(nextDueMs - now, 1, 50);
                    await Task.Delay(wait, _cts.Token).ConfigureAwait(false);
                    continue;
                }

                RawFrame? frame;
                try
                {
                    frame = _capturer!.Capture((int)Math.Clamp(interval, 1, 50), _maxWidth, _maxHeight);
                }
                catch (Exception ex)
                {
                    _log?.Invoke("Capture error: " + ex.Message);
                    _capturer?.Dispose();
                    _capturer = null;
                    await Task.Delay(1000, _cts.Token).ConfigureAwait(false);
                    continue;
                }

                if (frame is null)
                {
                    nextDueMs = Math.Max(nextDueMs, now);

                    // A change-driven backend (WGC, Desktop Duplication) legitimately returns no
                    // frame while the desktop is static, so do not treat a gap as a failure. Real
                    // failures surface as an exception from Capture, which recreates the capturer.
                    await Task.Delay(1, _cts.Token).ConfigureAwait(false);
                    continue;
                }

                // Only GDI repeats identical frames at the requested rate. WGC and Desktop
                // Duplication are change-driven, and skipping their frames by a sparse hash can
                // lose small updates such as a moving cursor.
                if (_capturer!.Backend == CaptureApi.Gdi)
                {
                    var fingerprint = Fingerprint(frame);
                    var publishAge = Environment.TickCount64 - _lastPublishedMs;
                    if (!_forcePublish && fingerprint == _lastFingerprint && publishAge < 1000)
                    {
                        frame.Dispose();
                        nextDueMs += interval;
                        if (now - nextDueMs > 250)
                            nextDueMs = now;
                        continue;
                    }

                    _lastFingerprint = fingerprint;
                }

                _forcePublish = false;
                _lastPublishedMs = Environment.TickCount64;
                frame.TimestampMs = Environment.TickCount64;
                _mailbox.Publish(frame);

                nextDueMs += interval;
                if (now - nextDueMs > 250)
                    nextDueMs = now;
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _log?.Invoke("Capture loop stopped: " + ex.Message);
        }
    }

    private static unsafe ulong Fingerprint(RawFrame frame)
    {
        const int columns = 96;
        const int rows = 54;
        var stepX = Math.Max(1, frame.Width / columns);
        var stepY = Math.Max(1, frame.Height / rows);
        var hash = 14695981039346656037UL;

        fixed (byte* basePtr = frame.Pixels)
        {
            for (var y = 0; y < frame.Height; y += stepY)
            {
                var row = basePtr + (long)y * frame.Stride;
                for (var x = 0; x < frame.Width; x += stepX)
                {
                    var pixel = *(uint*)(row + x * 4);
                    hash = (hash ^ pixel) * 1099511628211UL;
                }
            }
        }

        return hash;
    }

    private void RecreateCapturer()
    {
        try
        {
            _capturer?.Dispose();
            _capturer = null;

            var monitors = EnumerateMonitors();
            var monitor = monitors.FirstOrDefault(m => m.Index == _monitorIndex) ?? monitors.FirstOrDefault();
            if (monitor is null)
            {
                _log?.Invoke("No monitor is available for capture.");
                return;
            }

            var capturer = Create(monitor, _captureApi, _includeCursor, _log);
            _capturer = capturer;
            _activeMonitorIndex = monitor.Index;
            _activeIncludeCursor = _includeCursor;
            _activeCaptureApi = _captureApi;
            _forcePublish = true;
            Interlocked.Exchange(ref _forceKeyframe, 1);
            _log?.Invoke($"Capturing {monitor.Display} using {Describe(capturer.Backend)}.");
        }
        catch (Exception ex)
        {
            _log?.Invoke("Capture initialization failed: " + ex.Message);
            _capturer = null;
        }
    }

    private async Task EncodeLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = await _mailbox.TakeAsync(_cts.Token).ConfigureAwait(false);
                if (frame is null)
                    continue;

                try
                {
                    var (outputWidth, outputHeight) = JpegCodec.FitSize(
                        frame.Width, frame.Height, _maxWidth, _maxHeight);
                    var options = new VideoEncoderOptions(outputWidth, outputHeight, _quality);
                    var forceKeyframe = Interlocked.Exchange(ref _forceKeyframe, 0) != 0;

                    var started = Stopwatch.GetTimestamp();
                    EncodedFrame? encoded;
                    lock (_publishGate)
                    {
                        encoded = _encoder.Encode(frame.Pixels, frame.Width, frame.Height, frame.Stride,
                            options, Environment.TickCount64, forceKeyframe);
                        if (encoded is not null)
                        {
                            encoded = encoded with { Sequence = ++_sequence };
                            _hub.Publish(encoded);
                        }
                    }

                    if (encoded is not null)
                    {
                        var elapsed = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                        _metrics.AddFrame(encoded.ByteCount, elapsed);
                    }
                }
                catch (Exception ex)
                {
                    _log?.Invoke("Encode error: " + ex.Message);
                }
                finally
                {
                    frame.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _log?.Invoke("Encoder stopped: " + ex.Message);
        }
    }

    private async Task KeyframeLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _keyframeSignal.WaitAsync(_cts.Token).ConfigureAwait(false);

                var (outputWidth, outputHeight) = GetOutputSize();
                var options = new VideoEncoderOptions(outputWidth, outputHeight, _quality);
                var started = Stopwatch.GetTimestamp();
                EncodedFrame? encoded;
                lock (_publishGate)
                {
                    encoded = _encoder.EncodeCachedKeyframe(options, Environment.TickCount64);
                    if (encoded is not null)
                    {
                        encoded = encoded with { Sequence = ++_sequence };
                        _hub.Publish(encoded);
                    }
                }

                if (encoded is not null)
                {
                    var elapsed = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                    _metrics.AddFrame(encoded.ByteCount, elapsed);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _log?.Invoke("Keyframe error: " + ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }

        try
        {
            _keyframeSignal.Release();
        }
        catch (Exception)
        {
            // ignored
        }

        var tasks = new List<Task>();
        if (_captureTask is not null)
            tasks.Add(_captureTask);
        if (_encodeTask is not null)
            tasks.Add(_encodeTask);
        if (_keyframeTask is not null)
            tasks.Add(_keyframeTask);

        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Ignore shutdown races.
        }

        _mailbox.Dispose();
        _capturer?.Dispose();
        _capturer = null;
        _encoder.Dispose();
        _keyframeSignal.Dispose();
        _cts.Dispose();
    }
}
