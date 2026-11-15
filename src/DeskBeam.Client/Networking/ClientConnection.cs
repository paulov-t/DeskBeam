using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using DeskBeam.Client.Audio;
using DeskBeam.Core.Net;
using DeskBeam.Core.Protocol;
using DeskBeam.Core.Security;
using DeskBeam.Core.Video;

namespace DeskBeam.Client.Networking;

public sealed record ClientMetrics(
    double Fps,
    double Mbps,
    double DecodeMs,
    long RttMs,
    int Width,
    int Height);

/// <summary>
/// One remote session on the client: control handshake, PIN proof, separate video socket, JPEG
/// decoding, and a serialized outbox for input messages.
/// </summary>
public sealed class ClientConnection : IAsyncDisposable
{
    private TcpClient? _control;
    private TcpClient? _video;
    private NetworkStream? _controlStream;
    private NetworkStream? _videoStream;
    private CancellationTokenSource? _cts;
    private Channel<ControlMessage>? _outbox;
    private Task? _writerTask;
    private Task? _readerTask;
    private Task? _videoTask;
    private Task? _pingTask;
    private Task? _audioReportTask;
    private AudioReceiver? _audio;
    private bool _muteAudio;
    private Func<int, int, byte[], CancellationToken, ValueTask>? _present;
    private readonly FrameCompositor _compositor = new();
    private long _lastKeyframeRequestTicks;

    private int _disconnectRaised;
    private long _frames;
    private long _bytes;
    private double _decodeTotal;
    private int _decodeSamples;
    private long _windowStart = Stopwatch.GetTimestamp();
    private long _rtt;
    private int _minRtt = int.MaxValue;

    // Separate counters for the adaptive controller so UI sampling does not disturb it.
    private long _adaptiveFrames;
    private long _adaptiveSkipped;
    private long _adaptiveBytes;
    private long _adaptiveWindowStart = Stopwatch.GetTimestamp();
    private long _connectedAt = Stopwatch.GetTimestamp();
    private int _lastSequence = int.MinValue;
    private int _requestedWidth = 1280;
    private int _requestedHeight = 720;
    private int _requestedFps = 60;
    private int _requestedQuality = 60;
    private int _initialWidth = 1280;
    private int _initialHeight = 720;
    private int _initialFps = 60;
    private int _initialQuality = 65;
    private int _level;
    private int _badWindows;
    private int _goodWindows;
    private long _lastAdaptChange = Stopwatch.GetTimestamp();
    private long _lastDowngrade;
    private long _probeAfter;
    private double _probeBackoffSeconds = 10;
    private int _hostCaptureFps;
    private string _adaptiveStatus = string.Empty;
    private Task? _adaptiveTask;

    /// <summary>
    /// Decreasing quality/resolution ladder. Text stays readable because quality only drops after
    /// a modest downscale, and the last steps also halve the frame rate.
    /// </summary>
    private static readonly (double Scale, int Quality, int Fps)[] Ladder =
    [
        (1.00, 0, 0),   // 0: host defaults, filled in at connect time
        (1.00, 55, 0),
        (0.90, 50, 0),
        (0.80, 45, 0),
        (0.70, 45, 0),
        (0.60, 40, 0),
        (0.50, 38, 0),
        (0.42, 35, 0),
        (0.36, 32, 30),
        (0.30, 28, 30),
    ];

    public string ServerName { get; private set; } = string.Empty;
    public string MonitorName { get; private set; } = string.Empty;
    public int StreamFps { get; private set; }
    public int StreamQuality { get; private set; }
    /// <summary>Current size requested from the host (may be lower than the original stream size).</summary>
    public int RequestedWidth => _requestedWidth;
    public int RequestedHeight => _requestedHeight;
    public int RequestedFps => _requestedFps;
    public int RequestedQuality => _requestedQuality;
    public int AdaptiveLevel => _level;
    /// <summary>Set to false to always keep the host's original quality settings.</summary>
    public bool AdaptiveQuality { get; set; } = true;
    public string AdaptiveStatus => _adaptiveStatus;
    public string? VideoWarning { get; private set; }
    public string? AudioWarning { get; private set; }
    public bool AudioAvailable { get; private set; }
    public int AudioPort { get; private set; }
    public bool IsConnected { get; private set; }

    public bool MuteAudio
    {
        get => _muteAudio;
        set
        {
            _muteAudio = value;
            if (_audio is not null)
                _audio.Muted = value;
        }
    }

    public AudioMetrics SampleAudioMetrics() => _audio?.PeekMetrics() ?? default;
    public int FrameWidth { get; private set; }
    public int FrameHeight { get; private set; }
    public string? LastError { get; private set; }

    public event Action<HostStats>? HostStatistics;
    public event Action<RumbleState>? RumbleReceived;
    public event Action<string>? Disconnected;
    public event Action<string>? Log;

    public async Task ConnectAsync(
        string host,
        int port,
        string pin,
        string clientName,
        Func<int, int, byte[], CancellationToken, ValueTask> present,
        CancellationToken ct)
    {
        if (IsConnected)
            throw new InvalidOperationException("Already connected.");

        LastError = null;
        Interlocked.Exchange(ref _disconnectRaised, 0);
        _present = present;

        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshake.CancelAfter(TimeSpan.FromSeconds(15));
        var hct = handshake.Token;

        try
        {
            _control = new TcpClient { NoDelay = true };
            await _control.ConnectAsync(host, port, hct).ConfigureAwait(false);
            _controlStream = _control.GetStream();

            var hello = await Wire.ReceiveControlAsync(_controlStream, hct).ConfigureAwait(false) as ServerHello
                        ?? throw new InvalidOperationException("The host did not send a DeskBeam greeting.");
            if (hello.ProtocolVersion != ProtocolConstants.Version)
                throw new InvalidOperationException($"Protocol mismatch (host {hello.ProtocolVersion}, client {ProtocolConstants.Version}).");

            ServerName = hello.ServerName;

            var proof = hello.RequiresPassword
                ? Auth.ComputeProof(pin, hello.Nonce, clientName, ProtocolConstants.Version)
                : string.Empty;

            await Wire.SendControlAsync(_controlStream, new AuthRequest(clientName, proof), hct).ConfigureAwait(false);

            var auth = await Wire.ReceiveControlAsync(_controlStream, hct).ConfigureAwait(false) as AuthResult
                       ?? throw new InvalidOperationException("The host did not answer the authentication request.");
            if (!auth.Ok)
                throw new InvalidOperationException(auth.Error ?? "Authentication failed.");

            VideoWarning = auth.Warning;

            var info = await Wire.ReceiveControlAsync(_controlStream, hct).ConfigureAwait(false) as StreamInfo
                       ?? throw new InvalidOperationException("The host did not send stream information.");

            FrameWidth = info.Width;
            FrameHeight = info.Height;
            StreamFps = info.Fps;
            StreamQuality = info.Quality;
            MonitorName = info.MonitorName;
            AudioAvailable = info.AudioAvailable && info.AudioPort > 0 &&
                              !string.IsNullOrWhiteSpace(info.AudioId);
            AudioPort = info.AudioPort;

            _initialWidth = Math.Max(320, info.Width);
            _initialHeight = Math.Max(180, info.Height);
            _initialFps = Math.Clamp(info.Fps, 15, 120);
            _initialQuality = Math.Clamp(info.Quality, 20, 90);
            _requestedWidth = _initialWidth;
            _requestedHeight = _initialHeight;
            _requestedFps = _initialFps;
            _requestedQuality = _initialQuality;
            _level = 0;

            _video = new TcpClient { NoDelay = true };
            await _video.ConnectAsync(host, info.VideoPort, hct).ConfigureAwait(false);
            _videoStream = _video.GetStream();
            await Wire.SendStringAsync(_videoStream, info.SessionToken, hct).ConfigureAwait(false);
            var accepted = await Wire.ReceiveStringAsync(_videoStream, hct).ConfigureAwait(false);
            if (accepted != "OK")
                throw new InvalidOperationException("The host rejected the video channel.");

            _cts = new CancellationTokenSource();
            _outbox = Channel.CreateUnbounded<ControlMessage>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });

            IsConnected = true;
            _windowStart = Stopwatch.GetTimestamp();
            _adaptiveWindowStart = _windowStart;
            _connectedAt = _windowStart;
            _minRtt = int.MaxValue;
            _lastSequence = int.MinValue;
            _compositor.Reset();
            Interlocked.Exchange(ref _lastKeyframeRequestTicks, 0);
            _badWindows = 0;
            _goodWindows = 0;
            _lastAdaptChange = _windowStart;
            _lastDowngrade = 0;
            _probeAfter = 0;
            _probeBackoffSeconds = 10;
            Interlocked.Exchange(ref _adaptiveFrames, 0);
            Interlocked.Exchange(ref _adaptiveSkipped, 0);
            Interlocked.Exchange(ref _adaptiveBytes, 0);

            if (!IsLocalNetwork(host))
            {
                // Assume an internet link on first connect. The controller upgrades within seconds
                // when there is headroom, but never starts by saturating a slow upstream.
                _level = 6;
                ApplyLevel("conservative start for a remote host");
            }

            var ct2 = _cts.Token;
            var controlStream = _controlStream;
            var videoStream = _videoStream;
            _readerTask = Task.Run(() => ReadLoopAsync(controlStream, ct2), CancellationToken.None);
            _writerTask = Task.Run(() => WriterLoopAsync(controlStream, ct2), CancellationToken.None);
            _videoTask = Task.Run(() => VideoLoopAsync(videoStream, ct2), CancellationToken.None);
            _pingTask = Task.Run(() => PingLoopAsync(ct2), CancellationToken.None);
            _adaptiveTask = Task.Run(() => AdaptiveLoopAsync(ct2), CancellationToken.None);

            if (AudioAvailable)
            {
                var receiver = new AudioReceiver(host, info.AudioPort, info.AudioId, info.SessionToken, _muteAudio,
                    message => Log?.Invoke(message));
                if (await receiver.StartAsync(ct2).ConfigureAwait(false))
                {
                    _audio = receiver;
                    _audioReportTask = Task.Run(() => AudioReportLoopAsync(ct2), CancellationToken.None);
                }
                else
                {
                    AudioWarning = receiver.LastError ?? "Audio playback is unavailable on this PC.";
                    AudioAvailable = false;
                    await receiver.DisposeAsync().ConfigureAwait(false);
                }
            }

            var audioNote = AudioAvailable ? ", audio on" : string.Empty;
            Log?.Invoke(
                $"Connected to {ServerName}: {info.Width}x{info.Height} @ {info.Fps} fps, q{info.Quality}{audioNote}.");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            var audio = _audio;
            _audio = null;
            if (audio is not null)
                await audio.DisposeAsync().ConfigureAwait(false);
            CloseSockets();
            throw;
        }
    }

    public void SendGamepad(GamepadState state) => Queue(state);

    public void SendPointer(PointerInput input) => Queue(input);

    public void SendKey(int virtualKey, bool pressed) => Queue(new KeyInput(virtualKey, pressed));

    public void Queue(ControlMessage message)
    {
        if (!IsConnected)
            return;
        _outbox?.Writer.TryWrite(message);
    }

    private async Task WriterLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        try
        {
            var reader = _outbox!.Reader;
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var message))
                {
                    // Collapse backlogged high-frequency messages to the newest one.
                    if (message is GamepadState && reader.TryPeek(out var nextGamepad) && nextGamepad is GamepadState)
                        continue;
                    if (message is PointerInput { Action: PointerAction.Move } &&
                        reader.TryPeek(out var nextPointer) &&
                        nextPointer is PointerInput { Action: PointerAction.Move })
                        continue;

                    await Wire.SendControlAsync(stream, message, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (IOException)
        {
            RaiseDisconnected("Control channel closed.");
        }
        catch (Exception ex)
        {
            RaiseDisconnected("Control send failed: " + ex.Message);
        }
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var message = await Wire.ReceiveControlAsync(stream, ct).ConfigureAwait(false);
                if (message is null)
                    break;

                switch (message)
                {
                    case Pong pong:
                        _rtt = (long)Math.Round((Stopwatch.GetTimestamp() - pong.ClientTicks) * 1000.0 /
                                                Stopwatch.Frequency);
                        if (_rtt > 0 && _rtt < _minRtt)
                            _minRtt = (int)Math.Min(_rtt, int.MaxValue);
                        break;

                    case HostStats stats:
                        _hostCaptureFps = (int)Math.Round(stats.CaptureFps);
                        HostStatistics?.Invoke(stats);
                        break;

                    case RumbleState rumble:
                        RumbleReceived?.Invoke(rumble);
                        break;

                    case Disconnect disconnect:
                        RaiseDisconnected("Host closed the session: " + disconnect.Reason);
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (IOException)
        {
            // Socket closed.
        }
        catch (Exception ex)
        {
            RaiseDisconnected("Control channel error: " + ex.Message);
        }

        if (!ct.IsCancellationRequested)
            RaiseDisconnected("Control channel closed.");
    }

    private async Task VideoLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        // Set when the canvas is known to be stale (a skipped sequence or a rejected delta). While
        // set, deltas are dropped: applying them on a stale canvas would corrupt the picture until
        // the keyframe arrives, and deltas in flight behind a keyframe request are exactly that.
        var needKeyframe = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await Wire.ReceiveVideoAsync(stream, ct).ConfigureAwait(false);
                if (frame is null)
                    break;

                // Sequence gaps mean the host skipped frames because the link could not keep up. A
                // delta cannot be applied across a gap, so ask for a fresh keyframe.
                if (_lastSequence != int.MinValue)
                {
                    var delta = unchecked(frame.Sequence - _lastSequence);
                    if (delta > 1)
                    {
                        Interlocked.Add(ref _adaptiveSkipped, delta - 1);
                        needKeyframe = true;
                    }
                }

                _lastSequence = frame.Sequence;

                if (needKeyframe)
                {
                    if (!frame.IsKeyframe)
                    {
                        RequestKeyframe();
                        continue;
                    }

                    needKeyframe = false;
                }

                var started = Stopwatch.GetTimestamp();
                if (!_compositor.TryApply(frame, out var width, out var height, out var bgra))
                {
                    // A delta arrived before any keyframe (or a tile was rejected).
                    needKeyframe = true;
                    RequestKeyframe();
                    continue;
                }

                var decodeMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                _decodeTotal += decodeMs;
                _decodeSamples++;
                Interlocked.Increment(ref _frames);
                Interlocked.Add(ref _bytes, frame.ByteCount);
                Interlocked.Increment(ref _adaptiveFrames);
                Interlocked.Add(ref _adaptiveBytes, frame.ByteCount);

                FrameWidth = width;
                FrameHeight = height;

                if (_present is not null)
                    await _present(width, height, bgra, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (IOException)
        {
            // Socket closed.
        }
        catch (Exception ex)
        {
            RaiseDisconnected("Video channel error: " + ex.Message);
        }

        if (!ct.IsCancellationRequested)
            RaiseDisconnected("Video channel closed.");
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Queue(new Ping(Stopwatch.GetTimestamp()));
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task AudioReportLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                var audio = _audio;
                if (audio is null)
                    continue;

                var metrics = audio.SampleMetrics();
                Queue(new AudioReport(metrics.ReceivedFps, metrics.LossPercent, metrics.JitterMs, audio.Muted));
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Asks the host to re-send a full frame. Throttled because several video frames can fail
    /// before the host answers; the host may also be idle and respond from its cached canvas.
    /// </summary>
    private void RequestKeyframe()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastKeyframeRequestTicks);
        if (unchecked(now - last) < 500)
            return;

        if (Interlocked.CompareExchange(ref _lastKeyframeRequestTicks, now, last) != last)
            return;

        Queue(new KeyframeRequest());
    }

    private async Task AdaptiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                EvaluateAdaptive();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Trades stream quality for frame rate when the link is saturated. The host drops frames when
    /// a video socket cannot keep up, so gaps in the frame sequence plus rising RTT are reliable
    /// congestion signals even without access to router statistics.
    /// </summary>
    private void EvaluateAdaptive()
    {
        if (!AdaptiveQuality || !IsConnected)
            return;

        var now = Stopwatch.GetTimestamp();
        var seconds = (now - _adaptiveWindowStart) / (double)Stopwatch.Frequency;
        if (seconds <= 0)
            return;

        var frames = Interlocked.Exchange(ref _adaptiveFrames, 0);
        var skipped = Interlocked.Exchange(ref _adaptiveSkipped, 0);
        var bytes = Interlocked.Exchange(ref _adaptiveBytes, 0);
        _adaptiveWindowStart = now;

        if (frames == 0 && skipped == 0)
            return; // Idle desktop or capture starting; leave the settings alone.

        // Ignore the first two seconds while the host starts its capture pipeline.
        if ((now - _connectedAt) / (double)Stopwatch.Frequency < 2.0)
            return;

        var fps = frames / seconds;
        var total = frames + skipped;
        var skipRatio = total > 0 ? skipped / (double)total : 0;
        var target = Math.Min(_requestedFps, 60);

        // Compare what the host actually produced with what arrived. A static desktop or a slow
        // host encoder lowers the publish rate; the client still receives everything in that case.
        // A saturated link shows up as a large deficit plus sequence gaps.
        var hostFps = _hostCaptureFps;
        var deliveredRatio = hostFps > 0 && fps < hostFps ? fps / hostFps : 1.0;
        var rttRise = _minRtt != int.MaxValue && _rtt > Math.Max(120, _minRtt + 100);

        var congested = (skipRatio > 0.15 && deliveredRatio < 0.9) ||
                        (rttRise && skipRatio > 0.05);

        if (congested)
        {
            _badWindows++;
            _goodWindows = 0;
        }
        else
        {
            _goodWindows++;
            _badWindows = 0;
        }

        var cooldown = (now - _lastAdaptChange) / (double)Stopwatch.Frequency;
        if (_badWindows >= 2 && cooldown >= 1.5)
        {
            var severe = (target > 0 && fps < target * 0.4) || skipRatio > 0.5;
            _badWindows = 0;
            _lastAdaptChange = now;
            StepDown(severe ? 3 : 1, fps, skipRatio);
        }
        else if (_goodWindows >= 4 && cooldown >= 2.5 && now >= _probeAfter)
        {
            _goodWindows = 0;
            _lastAdaptChange = now;

            // A link that has been healthy for a while is allowed to probe upward faster again.
            if (_lastDowngrade != 0 &&
                now - _lastDowngrade > 30.0 * Stopwatch.Frequency)
            {
                _probeBackoffSeconds = 10;
            }

            StepUp();
        }
    }

    private static bool IsLocalNetwork(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!IPAddress.TryParse(host, out var address))
            return false; // A host name is most likely a DDNS address, so play it safe.

        return IPAddress.IsLoopback(address) || NetworkUtil.IsPrivate(address);
    }

    private void StepDown(int steps, double fps, double skipRatio)
    {
        var next = Math.Min(Ladder.Length - 1, _level + Math.Max(1, steps));
        if (next == _level)
            return;

        _level = next;

        // Back off the next upward probe so a saturated link stops oscillating between levels.
        var now = Stopwatch.GetTimestamp();
        _lastDowngrade = now;
        _probeAfter = now + (long)(_probeBackoffSeconds * Stopwatch.Frequency);
        _probeBackoffSeconds = Math.Min(60, _probeBackoffSeconds * 2);

        ApplyLevel($"link limited: {fps:F0} fps, {skipRatio * 100:F0}% frames dropped");
    }

    private void StepUp()
    {
        if (_level == 0)
            return;

        _level--;
        ApplyLevel("link has headroom");
    }

    private void ApplyLevel(string reason)
    {
        var (scale, quality, fps) = Ladder[_level];
        if (_level == 0)
        {
            _requestedQuality = _initialQuality;
            _requestedFps = _initialFps;
        }
        else
        {
            _requestedQuality = Math.Min(quality, _initialQuality);
            _requestedFps = fps > 0 ? Math.Min(fps, _initialFps) : _initialFps;
        }

        var (width, height) = JpegCodec.FitSize(
            _initialWidth,
            _initialHeight,
            Math.Max(320, (int)Math.Round(_initialWidth * scale)),
            Math.Max(180, (int)Math.Round(_initialHeight * scale)));
        _requestedWidth = width;
        _requestedHeight = height;
        _adaptiveStatus = $"{width}x{height} @ {_requestedFps} fps q{_requestedQuality}";
        Queue(new RequestStream(width, height, _requestedFps, _requestedQuality));
        Log?.Invoke($"Adaptive stream {_adaptiveStatus} ({reason}).");
    }

    public ClientMetrics SampleMetrics()
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = (now - _windowStart) / (double)Stopwatch.Frequency;
        if (seconds <= 0)
            seconds = 1;

        var frames = Interlocked.Exchange(ref _frames, 0);
        var bytes = Interlocked.Exchange(ref _bytes, 0);
        var decodeSamples = _decodeSamples;
        var decodeAverage = decodeSamples > 0 ? _decodeTotal / decodeSamples : 0;
        _decodeTotal = 0;
        _decodeSamples = 0;
        _windowStart = now;

        return new ClientMetrics(
            frames / seconds,
            bytes * 8.0 / 1_000_000.0 / seconds,
            decodeAverage,
            _rtt,
            FrameWidth,
            FrameHeight);
    }

    private void RaiseDisconnected(string reason)
    {
        if (Interlocked.Exchange(ref _disconnectRaised, 1) != 0)
            return;

        IsConnected = false;
        Disconnected?.Invoke(reason);
    }

    private void CloseSockets()
    {
        try
        {
            _control?.Dispose();
        }
        catch (Exception)
        {
            // ignored
        }

        try
        {
            _video?.Dispose();
        }
        catch (Exception)
        {
            // ignored
        }

        _control = null;
        _video = null;
        _controlStream = null;
        _videoStream = null;
    }

    public async Task DisconnectAsync()
    {
        IsConnected = false;
        Interlocked.Exchange(ref _disconnectRaised, 1);

        var cts = _cts;
        _cts = null;

        try
        {
            cts?.Cancel();
        }
        catch (Exception)
        {
            // ignored
        }

        _outbox?.Writer.TryComplete();
        CloseSockets();

        var audio = _audio;
        _audio = null;

        var tasks = new[] { _readerTask, _writerTask, _videoTask, _pingTask, _adaptiveTask, _audioReportTask }
            .Where(t => t is not null)
            .Select(t => t!)
            .ToArray();
        _readerTask = null;
        _writerTask = null;
        _videoTask = null;
        _pingTask = null;
        _adaptiveTask = null;
        _audioReportTask = null;

        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Ignore shutdown races.
        }

        if (audio is not null)
            await audio.DisposeAsync().ConfigureAwait(false);

        _outbox = null;
        cts?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _compositor.Dispose();
    }
}
