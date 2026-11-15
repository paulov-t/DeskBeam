using System.Net;
using System.Net.Sockets;
using DeskBeam.Core.Models;
using DeskBeam.Core.Net;
using DeskBeam.Core.Protocol;
using DeskBeam.Core.Video;
using DeskBeam.Engine.Audio;
using DeskBeam.Engine.Capture;
using DeskBeam.Engine.Input;

namespace DeskBeam.Engine.Streaming;

/// <summary>
/// Runs the DeskBeam host: control + video TCP listeners, screen capture pipeline, virtual
/// gamepad injection, optional UPnP mapping, and client bookkeeping.
/// </summary>
public sealed class HostServer : IAsyncDisposable
{
    private readonly FrameBroadcaster _hub = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, ClientSession> _sessions = [];

    private HostOptions _options = new();
    private TcpListener? _controlListener;
    private TcpListener? _videoListener;
    private CancellationTokenSource? _cts;
    private EncodingPipeline? _pipeline;
    private AudioStreamer? _audio;
    private string? _audioStatus;
    private Task? _controlAcceptTask;
    private Task? _videoAcceptTask;
    private Task? _statsTask;
    private UpnpPortMapper? _upnp;
    private string? _virtualPadStatus;
    private string? _lastError;
    private bool _running;

    public event Action<string>? Log;
    public event Action? ClientsChanged;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _running;
        }
    }

    public HostOptions CurrentOptions => _options.Clone();

    internal AudioStreamer? Audio => _audio;

    public string? VirtualPadStatus => _virtualPadStatus;

    public string? LastError => _lastError;

    public async Task StartAsync(HostOptions options)
    {
        await StopAsync().ConfigureAwait(false);

        _options = options.Clone();
        _audioStatus = null;
        _hub.Reset();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            _controlListener = new TcpListener(IPAddress.Any, _options.Port);
            _controlListener.Start();

            _videoListener = new TcpListener(IPAddress.Any, _options.VideoPort);
            try
            {
                _videoListener.Start();
            }
            catch
            {
                _controlListener.Stop();
                _controlListener = null;
                throw;
            }

            _pipeline = new EncodingPipeline(_hub, LogMessage);
            _pipeline.Configure(_options);
            _pipeline.Start();

            if (_options.EnableAudio)
            {
                var audio = new AudioStreamer(LogMessage);
                if (await audio.StartAsync(_options, ct).ConfigureAwait(false))
                {
                    _audio = audio;
                    _audioStatus = null;
                }
                else
                {
                    _audioStatus = audio.Status;
                    await audio.DisposeAsync().ConfigureAwait(false);
                }
            }
            else
            {
                _audioStatus = "Audio disabled in the host settings.";
                LogMessage("Audio is disabled in the host settings; streaming video only.");
            }

            ProbeVirtualGamepad();

            lock (_gate)
                _running = true;

            _controlAcceptTask = Task.Run(() => AcceptLoopAsync(_controlListener, HandleControlClientAsync, ct), ct);
            _videoAcceptTask = Task.Run(() => AcceptLoopAsync(_videoListener, HandleVideoClientAsync, ct), ct);
            _statsTask = Task.Run(() => StatsLoopAsync(ct), ct);

            LogMessage($"Host started. Control port {_options.Port}, video port {_options.VideoPort} (TCP).");
            LogMessage(_audio is null
                ? $"Audio channel is unavailable: {_audioStatus} Video is unaffected."
                : $"Audio port {_options.AudioPort} (UDP).");
            LogMessage($"Gamepad: {_virtualPadStatus}");
        }
        catch (SocketException ex)
        {
            _lastError = $"Could not listen on TCP {_options.Port}/{_options.VideoPort}: {ex.Message}";
            LogMessage(_lastError);
            await StopAsync().ConfigureAwait(false);
            throw new InvalidOperationException(_lastError, ex);
        }
    }

    private void ProbeVirtualGamepad()
    {
        if (!_options.EnableVirtualGamepad)
        {
            _virtualPadStatus = "Disabled in settings.";
            return;
        }

        _virtualPadStatus = VirtualGamepad.Probe(out var error)
            ? "ViGEmBus ready. A virtual Xbox 360 pad appears while a client is connected."
            : $"Unavailable: {error}. Install ViGEmBus from https://github.com/nefarius/ViGEmBus/releases.";
    }

    private async Task AcceptLoopAsync(TcpListener listener, Func<TcpClient, CancellationToken, Task> handler,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                LogMessage("Accept failed: " + ex.Message);
                try
                {
                    await Task.Delay(200, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            try
            {
                client.NoDelay = true;
            }
            catch (Exception)
            {
                // ignored
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await handler(client, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogMessage("Connection handler failed: " + ex.Message);
                    try
                    {
                        client.Dispose();
                    }
                    catch (Exception)
                    {
                        // ignored
                    }
                }
            }, CancellationToken.None);
        }
    }

    private async Task HandleControlClientAsync(TcpClient client, CancellationToken ct)
    {
        var session = new ClientSession(Guid.NewGuid().ToString("N")[..8], client, this);

        try
        {
            if (!await session.HandshakeAsync(ct).ConfigureAwait(false))
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return;
            }
        }
        catch (Exception ex)
        {
            LogMessage("Handshake failed: " + ex.Message);
            await session.DisposeAsync().ConfigureAwait(false);
            return;
        }

        lock (_gate)
            _sessions[session.Id] = session;

        UpdateViewerState();
        ClientsChanged?.Invoke();
        LogMessage($"{session.Name} connected from {session.RemoteAddress}.");

        try
        {
            await session.RunAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
                _sessions.Remove(session.Id);

            UpdateViewerState();
            ClientsChanged?.Invoke();
            await session.DisposeAsync().ConfigureAwait(false);
            LogMessage($"{session.Name} disconnected.");
        }
    }

    private async Task HandleVideoClientAsync(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();

        try
        {
            var token = await Wire.ReceiveStringAsync(stream, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
            {
                client.Dispose();
                return;
            }

            var session = await FindSessionByTokenAsync(token, ct).ConfigureAwait(false);
            if (session is null)
            {
                await Wire.SendStringAsync(stream, "DENY", ct).ConfigureAwait(false);
                LogMessage("Rejected a video connection with an unknown token.");
                client.Dispose();
                return;
            }

            if (!session.TryAttachVideo())
            {
                await Wire.SendStringAsync(stream, "DENY", ct).ConfigureAwait(false);
                client.Dispose();
                return;
            }

            await Wire.SendStringAsync(stream, "OK", ct).ConfigureAwait(false);

            // Bound how much stale video can queue inside the kernel. Without this, a slow
            // upstream lets seconds of old frames pile up before the adaptive controller reacts.
            try
            {
                client.SendBufferSize = 1024 * 1024;
            }
            catch (SocketException)
            {
                // Keep the OS default.
            }

            LogMessage($"{session.Name}: video channel open.");

            try
            {
                await StreamVideoAsync(stream, session, ct).ConfigureAwait(false);
            }
            finally
            {
                session.DetachVideo();
                client.Dispose();
                LogMessage($"{session.Name}: video channel closed.");
            }
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
        }
        catch (IOException)
        {
            client.Dispose();
        }
        catch (Exception ex)
        {
            LogMessage("Video connection error: " + ex.Message);
            client.Dispose();
        }
    }

    private async Task<ClientSession?> FindSessionByTokenAsync(string token, CancellationToken ct)
    {
        // The client may open the video socket a few milliseconds before the control handler
        // registers the session, so retry briefly.
        for (var attempt = 0; attempt < 60; attempt++)
        {
            lock (_gate)
            {
                foreach (var session in _sessions.Values)
                {
                    if (session.IsAuthenticated &&
                        string.Equals(session.Token, token, StringComparison.Ordinal))
                        return session;
                }
            }

            try
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        return null;
    }

    private async Task StreamVideoAsync(NetworkStream stream, ClientSession session, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, session.SessionToken);
        using var subscriber = _hub.Subscribe();

        var needKeyframe = true;
        var lastSequence = int.MinValue;

        var latest = _hub.Latest;
        if (latest is not null)
        {
            if (latest.IsKeyframe)
            {
                await Wire.SendVideoAsync(stream, latest, linked.Token).ConfigureAwait(false);
                subscriber.LastSequence = latest.Sequence;
                lastSequence = latest.Sequence;
                needKeyframe = false;
            }
            else
            {
                // A viewer cannot apply a delta before it has a canvas. Ask the encoder for the
                // cached keyframe instead of waiting for the desktop to change.
                RequestKeyframe();
            }
        }

        while (!linked.IsCancellationRequested)
        {
            var frame = await subscriber.WaitAsync(linked.Token).ConfigureAwait(false);
            if (frame is null || frame.Sequence == lastSequence)
                continue;

            if (needKeyframe)
            {
                if (!frame.IsKeyframe)
                {
                    RequestKeyframe();
                    continue;
                }
            }
            else if (frame.Sequence != lastSequence + 1)
            {
                // A delta only applies to the immediately previous frame. If this viewer missed one
                // and the frame is not already a keyframe, wait for a keyframe rather than drawing
                // a corrupt composite.
                if (!frame.IsKeyframe)
                {
                    needKeyframe = true;
                    RequestKeyframe();
                    continue;
                }
            }

            await Wire.SendVideoAsync(stream, frame, linked.Token).ConfigureAwait(false);
            subscriber.LastSequence = frame.Sequence;
            lastSequence = frame.Sequence;
            needKeyframe = false;
        }
    }

    internal void RequestKeyframe() => _pipeline?.RequestKeyframe();

    private async Task StatsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            List<ClientSession> sessions;
            lock (_gate)
                sessions = _sessions.Values.ToList();

            var metrics = _pipeline?.Metrics;
            metrics?.Sample();

            var size = GetOutputSize();
            var audio = _audio?.GetStats() ?? default;
            var stats = new HostStats(
                metrics?.Fps ?? 0,
                metrics?.AverageEncodeMs ?? 0,
                metrics?.Kbps ?? 0,
                sessions.Count,
                size.Width,
                size.Height,
                _virtualPadStatus,
                audio.Kbps,
                audio.Capturing,
                _audio?.Status ?? _audioStatus);

            var now = Environment.TickCount64;
            foreach (var session in sessions)
            {
                if (now - session.LastSeenTicks > 15000)
                {
                    session.Abort("no heartbeat for 15 seconds");
                    continue;
                }

                session.PostMessage(stats);
            }
        }
    }

    private void UpdateViewerState()
    {
        int count;
        lock (_gate)
            count = _sessions.Count;

        if (_pipeline is not null)
        {
            _pipeline.HasViewers = count > 0;

            // Do not let an adaptive downgrade from one session become the starting quality of
            // the next one; restore the host's configured stream when the last viewer leaves.
            if (count == 0)
                _pipeline.ConfigureStream(_options.MaxWidth, _options.MaxHeight, _options.Fps, _options.Quality);
        }
    }

    internal HostOptions GetOptionsSnapshot() => _options.Clone();

    internal MonitorDescriptor? GetSelectedMonitor()
    {
        var current = _pipeline?.CurrentMonitor;
        if (current is not null)
            return current;

        try
        {
            var monitors = ScreenCaptureFactory.EnumerateMonitors();
            return monitors.FirstOrDefault(m => m.Index == _options.MonitorIndex) ?? monitors.FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal (int Width, int Height) GetOutputSize()
    {
        var size = _pipeline?.GetOutputSize();
        if (size is not null)
            return size.Value;

        var monitor = GetSelectedMonitor();
        return monitor is null
            ? (Math.Max(2, _options.MaxWidth), Math.Max(2, _options.MaxHeight))
            : JpegCodec.FitSize(monitor.Width, monitor.Height, _options.MaxWidth, _options.MaxHeight);
    }

    internal void ApplyStreamRequest(RequestStream request)
    {
        if (_pipeline is null)
            return;

        // Clamp to what the host was configured to offer so a viewer cannot force a bigger or
        // more expensive stream than the operator chose.
        var width = Math.Clamp(request.Width, 320, Math.Max(320, _options.MaxWidth));
        var height = Math.Clamp(request.Height, 180, Math.Max(180, _options.MaxHeight));
        var fps = Math.Clamp(request.Fps, 15, Math.Max(15, _options.Fps));
        var quality = Math.Clamp(request.Quality, 20, Math.Max(20, _options.Quality));
        _pipeline.ConfigureStream(width, height, fps, quality);
    }

    internal void NotifyGamepadCreated()
    {
        _virtualPadStatus = "Virtual Xbox 360 pad active.";
    }

    internal void ApplyAudioReport(string sessionToken, AudioReport report)
    {
        _audio?.ApplyReport(sessionToken, report);
    }

    public void UpdateOptions(HostOptions options)
    {
        // Quality and size changes should be visible immediately even on a static desktop; other
        // options either take effect on the next capture or are audio-only.
        var visualChanged = _options.Quality != options.Quality ||
                            _options.MaxWidth != options.MaxWidth ||
                            _options.MaxHeight != options.MaxHeight;

        _options = options.Clone();
        _pipeline?.Configure(_options);
        if (visualChanged)
            _pipeline?.RequestKeyframe();
        _audio?.UpdateOptions(_options);
        if (_options.EnableVirtualGamepad)
            ProbeVirtualGamepad();
        else
            _virtualPadStatus = "Disabled in settings.";
    }

    public HostSnapshot GetSnapshot()
    {
        int clientCount;
        lock (_gate)
            clientCount = _sessions.Count;

        var metrics = _pipeline?.Metrics;
        metrics?.Sample();
        var size = GetOutputSize();
        var audio = _audio?.GetStats() ?? default;

        return new HostSnapshot(
            IsRunning,
            clientCount,
            metrics?.Fps ?? 0,
            metrics?.AverageEncodeMs ?? 0,
            metrics?.Kbps ?? 0,
            size.Width,
            size.Height,
            _virtualPadStatus,
            _lastError,
            audio.Kbps,
            audio.Capturing,
            _audio?.Status ?? _audioStatus);
    }

    public IReadOnlyList<ClientSnapshot> GetClients()
    {
        lock (_gate)
            return _sessions.Values.Select(s => s.ToSnapshot()).ToList();
    }

    public async Task<UpnpMappingResult> MapPortsAsync(CancellationToken ct = default)
    {
        var mapper = new UpnpPortMapper();
        mapper.Log += LogMessage;

        if (_upnp is not null)
        {
            // Refresh: drop the previous lease before creating new mappings so routers that
            // reject duplicates still accept the request.
            try
            {
                await _upnp.UnmapAsync(ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort.
            }

            _upnp.Dispose();
        }

        _upnp = mapper;

        var local = NetworkUtil.GetPrimaryIPv4() ?? NetworkUtil.GetLocalIPv4().FirstOrDefault();
        if (local is null)
            return new UpnpMappingResult(false, "No LAN IPv4 address was found.", null, 0, 0, null);

        var audioPort = _audio?.IsAvailable == true ? _options.AudioPort : 0;
        var result = await mapper.MapAsync(_options.Port, _options.VideoPort, audioPort, local, ct)
            .ConfigureAwait(false);
        LogMessage(result.Message);
        return result;
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        EncodingPipeline? pipeline;
        AudioStreamer? audio;
        UpnpPortMapper? upnp;

        lock (_gate)
        {
            if (!_running && _cts is null)
                return;

            _running = false;
            cts = _cts;
            _cts = null;
            pipeline = _pipeline;
            _pipeline = null;
            audio = _audio;
            _audio = null;
            upnp = _upnp;
            _upnp = null;
        }

        try
        {
            cts?.Cancel();
        }
        catch (Exception)
        {
            // ignored
        }

        try
        {
            _controlListener?.Stop();
            _videoListener?.Stop();
        }
        catch (Exception)
        {
            // ignored
        }

        _controlListener = null;
        _videoListener = null;

        List<ClientSession> sessions;
        lock (_gate)
        {
            sessions = _sessions.Values.ToList();
            _sessions.Clear();
        }

        foreach (var session in sessions)
            session.Abort("host stopped");

        foreach (var session in sessions)
            await session.DisposeAsync().ConfigureAwait(false);

        if (audio is not null)
            await audio.DisposeAsync().ConfigureAwait(false);

        if (pipeline is not null)
            await pipeline.DisposeAsync().ConfigureAwait(false);

        if (upnp is not null)
        {
            try
            {
                await upnp.UnmapAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // ignored
            }

            upnp.Dispose();
        }

        var tasks = new[] { _controlAcceptTask, _videoAcceptTask, _statsTask }.Where(t => t is not null).ToArray();
        _controlAcceptTask = null;
        _videoAcceptTask = null;
        _statsTask = null;

        try
        {
            await Task.WhenAll(tasks!).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // ignored
        }

        cts?.Dispose();
        _lastError = null;
        LogMessage("Host stopped.");
        ClientsChanged?.Invoke();
    }

    internal void LogMessage(string message)
    {
        try
        {
            Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
        }
        catch (Exception)
        {
            // Never let logging take down the server.
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
