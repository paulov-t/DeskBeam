using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using DeskBeam.Core.Audio;
using DeskBeam.Core.Models;
using DeskBeam.Core.Protocol;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DeskBeam.Engine.Audio;

public readonly record struct AudioStreamerStats(
    double Kbps,
    double PacketsPerSecond,
    int Clients,
    bool Capturing,
    string Status);

/// <summary>
/// Captures desktop audio once, encodes it to 10 ms Opus frames and fans the same packets out to
/// every viewer over a dedicated UDP socket. Video stays on its own TCP channel: audio is always
/// droppable and a congested viewer is muted instead of changing video settings.
/// </summary>
public sealed class AudioStreamer : IAsyncDisposable
{
    private sealed class AudioClientState
    {
        public required string Token { get; init; }
        public required uint SessionId { get; init; }
        public IPEndPoint? Endpoint { get; set; }
        public long LastSeenTicks { get; set; }
        public long LastReportTicks { get; set; }
        public long MuteUntilTicks { get; set; }
        public bool ClientMuted { get; set; }
        public int BadReports { get; set; }
        public double LossPercent { get; set; }
        public double JitterMs { get; set; }
        public double ReceivedFps { get; set; }
    }

    private readonly Action<string> _log;
    private readonly object _clientsGate = new();
    private readonly Dictionary<string, AudioClientState> _clients = new(StringComparer.Ordinal);
    private readonly Channel<byte[]> _frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true,
    });
    private readonly SemaphoreSlim _sessionChanged = new(0, 1);

    private UdpClient? _udp;
    private Socket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _supervisorTask;
    private Thread? _encoderThread;
    private WasapiRecorder? _recorder;
    private PcmNormalizer? _normalizer;
    private int _capturePid = -1;
    private int _bitrateKbps = AudioConstants.DefaultBitrateKbps;
    private string _excludedProcesses = string.Empty;
    private int _ownPid;

    private long _sentPackets;
    private long _sentBytes;
    private long _statsWindowTicks = Environment.TickCount64;
    private double _kbps;
    private double _packetsPerSecond;
    private volatile bool _capturing;
    private volatile string _status = "Audio has not started.";
    private int _lastErrorLogTicks;

    public AudioStreamer(Action<string> log)
    {
        _log = log;
    }

    public bool IsAvailable => _socket is not null;

    public int Port { get; private set; }

    public int BitrateKbps => _bitrateKbps;

    public string Status => _status;

    public void UpdateOptions(HostOptions options)
    {
        _bitrateKbps = Math.Clamp(options.AudioBitrateKbps, 8, 256);
        _excludedProcesses = options.AudioExcludedProcesses ?? string.Empty;
        try
        {
            _sessionChanged.Release();
        }
        catch (SemaphoreFullException)
        {
            // A supervisor pass is already pending.
        }
    }

    /// <summary>Binds the UDP socket. Returns false when audio cannot start; video can still run.</summary>
    public async Task<bool> StartAsync(HostOptions options, CancellationToken hostToken)
    {
        _ownPid = Environment.ProcessId;
        _bitrateKbps = Math.Clamp(options.AudioBitrateKbps, 8, 256);
        _excludedProcesses = options.AudioExcludedProcesses ?? string.Empty;
        Port = options.AudioPort;

        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
            _udp.Client.ReceiveBufferSize = 64 * 1024;
            _udp.Client.SendBufferSize = 64 * 1024;
            DisableUdpConnectionReset(_udp.Client);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
            _socket = _udp.Client;
        }
        catch (Exception ex)
        {
            _status = $"Audio unavailable: {ex.Message}";
            _log("Audio disabled: " + ex.Message);
            await DisposeSocketAsync().ConfigureAwait(false);
            return false;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
        var ct = _cts.Token;

        _receiveTask = Task.Run(() => ReceiveLoopAsync(ct), CancellationToken.None);
        _supervisorTask = Task.Run(() => CaptureSupervisorAsync(ct), CancellationToken.None);

        _encoderThread = new Thread(() => EncodeLoop(ct))
        {
            IsBackground = true,
            Name = "DeskBeam audio encoder",
            Priority = ThreadPriority.BelowNormal,
        };
        _encoderThread.Start();

        _status = "Waiting for a viewer to bind the audio channel.";
        return true;
    }

    public bool Register(string token, string audioId)
    {
        if (_socket is null || !uint.TryParse(audioId, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var sessionId))
            return false;

        lock (_clientsGate)
        {
            if (_clients.ContainsKey(token))
                return true;

            _clients[token] = new AudioClientState
            {
                Token = token,
                SessionId = sessionId,
            };
        }

        try
        {
            // Wake the supervisor immediately so the capture device starts before the first bind.
            _sessionChanged.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is already pending.
        }

        return true;
    }

    public void Unregister(string token)
    {
        lock (_clientsGate)
            _clients.Remove(token);
    }

    public bool IsClientAudioConnected(string token)
    {
        lock (_clientsGate)
        {
            return _clients.TryGetValue(token, out var client) &&
                   client.Endpoint is not null &&
                   Environment.TickCount64 - client.LastSeenTicks < 10000;
        }
    }

    public bool IsClientMuted(string token)
    {
        lock (_clientsGate)
            return _clients.TryGetValue(token, out var client) && (client.ClientMuted || IsHostMuted(client));
    }

    public void ApplyReport(string token, AudioReport report)
    {
        AudioClientState? client;
        lock (_clientsGate)
        {
            if (!_clients.TryGetValue(token, out client))
                return;

            client.LastReportTicks = Environment.TickCount64;
            client.ReceivedFps = Math.Max(0, report.ReceivedFps);
            client.LossPercent = Math.Clamp(report.LossPercent, 0, 100);
            client.JitterMs = Math.Max(0, report.JitterMs);
            client.ClientMuted = report.Muted;

            // Only a clearly unusable audio path mutes the viewer. Mild Wi-Fi loss is left to
            // Opus PLC, because muting for five seconds is more disruptive than the glitches.
            var congested = client.ReceivedFps > 0 &&
                            (client.LossPercent > 25 || client.JitterMs > 150);

            if (congested)
            {
                client.BadReports++;
                if (client.BadReports >= 2 && client.MuteUntilTicks < Environment.TickCount64)
                {
                    client.MuteUntilTicks = Environment.TickCount64 + 5000;
                    client.BadReports = 0;
                    _log($"{token}: audio muted for 5 s ({client.LossPercent:F0}% loss, " +
                         $"{client.JitterMs:F0} ms jitter). Video is unaffected.");
                }
            }
            else
            {
                client.BadReports = 0;
            }
        }
    }

    public AudioStreamerStats GetStats()
    {
        var now = Environment.TickCount64;
        var elapsed = (now - _statsWindowTicks) / 1000.0;
        if (elapsed >= 0.5)
        {
            var bytes = Interlocked.Exchange(ref _sentBytes, 0);
            var packets = Interlocked.Exchange(ref _sentPackets, 0);
            _kbps = bytes * 8.0 / 1000.0 / elapsed;
            _packetsPerSecond = packets / elapsed;
            _statsWindowTicks = now;
        }

        int clients;
        lock (_clientsGate)
            clients = _clients.Values.Count(c => c.Endpoint is not null);

        return new AudioStreamerStats(_kbps, _packetsPerSecond, clients, _capturing, _status);
    }

    private bool IsHostMuted(AudioClientState client) => Environment.TickCount64 < client.MuteUntilTicks;

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var socket = _udp!;
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(ct).ConfigureAwait(false);
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
                if (ct.IsCancellationRequested)
                    break;

                // ICMP port-unreachable wakes a UDP receive with ConnectionReset on Windows;
                // ignore it instead of taking the audio channel down.
                if (ex.SocketErrorCode is not (SocketError.ConnectionReset or SocketError.MessageSize))
                    LogError("Audio receive failed: " + ex.Message);
                continue;
            }

            if (!AudioPacket.TryReadBindToken(result.Buffer, out var token))
                continue;

            AudioClientState? bound = null;
            lock (_clientsGate)
            {
                if (!_clients.TryGetValue(token, out var client))
                    continue;

                var changed = client.Endpoint is null || !client.Endpoint.Equals(result.RemoteEndPoint);
                client.Endpoint = result.RemoteEndPoint;
                client.LastSeenTicks = Environment.TickCount64;
                if (changed)
                    bound = client;
            }

            if (bound is not null)
                _log($"Audio channel bound from {result.RemoteEndPoint}.");
        }
    }

    private async Task CaptureSupervisorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _sessionChanged.WaitAsync(1000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var hasClients = false;
            lock (_clientsGate)
                hasClients = _clients.Count > 0;

            if (!hasClients)
            {
                StopRecorder();
                _capturing = false;
                _status = "Waiting for a viewer to bind the audio channel.";
                continue;
            }

            var target = AudioProcessResolver.FindExclusionTarget(_excludedProcesses, _ownPid);
            if (_recorder is not null && _capturing && _capturePid == target.ProcessId)
                continue;

            try
            {
                await StartRecorderAsync(target, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                StopRecorder();
                _status = "Audio capture failed: " + ex.Message;
                LogError("Audio capture failed: " + ex.Message);
                await DelayAsync(2000, ct).ConfigureAwait(false);
            }
        }

        StopRecorder();
    }

    private async Task StartRecorderAsync(AudioProcessResolver.ExclusionTarget target, CancellationToken ct)
    {
        StopRecorder();

        var format = new WaveFormat(AudioConstants.SampleRate, 16, AudioConstants.Channels);
        var builder = new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)target.ProcessId, ProcessLoopbackMode.ExcludeTargetProcessTree)
            .WithFormat(format)
            .WithBufferLength(20)
            .WithSharedMode()
            .WithEventSync()
            .WithMmcssThreadPriority("Pro Audio");

        var recorder = await builder.BuildAsync().ConfigureAwait(false);
        try
        {
            if (recorder.WaveFormat.SampleRate != AudioConstants.SampleRate)
            {
                throw new NotSupportedException(
                    $"The audio device runs at {recorder.WaveFormat.SampleRate} Hz; DeskBeam needs 48000 Hz.");
            }

            var normalizer = new PcmNormalizer(recorder.WaveFormat, OnPcmFrame);
            recorder.DataAvailable += OnDataAvailable;
            recorder.RecordingStopped += OnRecordingStopped;
            recorder.StartRecording();

            _normalizer = normalizer;
            _recorder = recorder;
            _capturePid = target.ProcessId;
            _capturing = true;
            _status = target.IsFallback
                ? AudioProcessResolver.ParseNames(_excludedProcesses).Count == 0
                    ? "Capturing desktop audio. No exclusions configured."
                    : "Capturing desktop audio. No excluded app is running."
                : $"Capturing desktop audio, excluding {target.Name}.";
            _log(_status);
        }
        catch
        {
            recorder.DataAvailable -= OnDataAvailable;
            recorder.RecordingStopped -= OnRecordingStopped;
            recorder.Dispose();
            throw;
        }
    }

    private void OnDataAvailable(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long devicePosition,
        long qpcPosition)
    {
        try
        {
            _normalizer?.Write(data);
        }
        catch (Exception ex)
        {
            LogError("Audio conversion failed: " + ex.Message);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _capturing = false;
        if (e.Exception is not null)
        {
            _status = "Audio capture stopped: " + e.Exception.Message;
            LogError("Audio capture stopped: " + e.Exception.Message);
        }

        // The supervisor sees _capturing == false and rebuilds the recorder on its next pass.
        try
        {
            _sessionChanged.Release();
        }
        catch (SemaphoreFullException)
        {
            // ignore
        }
    }

    private void OnPcmFrame(byte[] frame)
    {
        _frames.Writer.TryWrite(frame);
    }

    private void EncodeLoop(CancellationToken ct)
    {
        var encoder = new OpusAudioEncoder(_bitrateKbps);
        var packet = new byte[AudioConstants.MaxOpusPacketBytes];
        var sendBuffer = new byte[AudioConstants.MaxDatagramBytes];
        uint sequence = 0;
        var appliedBitrate = _bitrateKbps;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!_frames.Reader.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult())
                    break;

                while (_frames.Reader.TryRead(out var frame))
                {
                    if (ct.IsCancellationRequested)
                        break;

                    if (appliedBitrate != _bitrateKbps)
                    {
                        appliedBitrate = _bitrateKbps;
                        encoder.BitrateKbps = appliedBitrate;
                    }

                    try
                    {
                        var length = encoder.Encode(frame, packet);
                        sequence++;
                        SendFrame(packet.AsSpan(0, length), sequence, sendBuffer);
                    }
                    catch (Exception ex)
                    {
                        LogError("Audio encode failed: " + ex.Message);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            LogError("Audio encoder stopped: " + ex.Message);
        }
    }

    private void SendFrame(ReadOnlySpan<byte> opus, uint sequence, byte[] buffer)
    {
        if (_socket is null)
            return;

        var now = Environment.TickCount64;
        var timestamp = unchecked((uint)now);
        List<AudioClientState> clients;
        lock (_clientsGate)
        {
            clients = _clients.Values
                .Where(c => c.Endpoint is not null &&
                            !c.ClientMuted &&
                            !IsHostMuted(c) &&
                            now - c.LastSeenTicks < 10000)
                .ToList();
        }

        foreach (var client in clients)
        {
            try
            {
                var length = AudioPacket.WriteAudio(buffer, client.SessionId, sequence, timestamp, opus);
                _socket.SendTo(buffer.AsSpan(0, length), SocketFlags.None, client.Endpoint!);
                Interlocked.Increment(ref _sentPackets);
                Interlocked.Add(ref _sentBytes, length);
            }
            catch (SocketException)
            {
                // Dropping audio is always preferable to slowing the video path.
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    private void StopRecorder()
    {
        var recorder = _recorder;
        _recorder = null;
        _normalizer = null;
        _capturing = false;
        _capturePid = -1;

        if (recorder is null)
            return;

        recorder.DataAvailable -= OnDataAvailable;
        recorder.RecordingStopped -= OnRecordingStopped;
        try
        {
            recorder.StopRecording();
        }
        catch (Exception)
        {
            // Best effort.
        }

        try
        {
            recorder.Dispose();
        }
        catch (Exception)
        {
            // Best effort.
        }
    }

    private void LogError(string message)
    {
        var now = Environment.TickCount;
        var last = Volatile.Read(ref _lastErrorLogTicks);
        if (unchecked(now - last) < 2000)
            return;

        Interlocked.Exchange(ref _lastErrorLogTicks, now);
        _log(message);
    }

    private static async Task DelayAsync(int milliseconds, CancellationToken ct)
    {
        try
        {
            await Task.Delay(milliseconds, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private static void DisableUdpConnectionReset(Socket socket)
    {
        try
        {
            // SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12
            socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
        }
        catch (Exception)
        {
            // Not supported everywhere; the receive loop also handles ConnectionReset.
        }
    }

    private async Task DisposeSocketAsync()
    {
        var udp = _udp;
        _udp = null;
        _socket = null;
        if (udp is null)
            return;

        try
        {
            udp.Dispose();
        }
        catch (Exception)
        {
            // Best effort.
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
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

        try
        {
            _sessionChanged.Release();
        }
        catch (SemaphoreFullException)
        {
            // ignore
        }

        _frames.Writer.TryComplete();
        StopRecorder();

        try
        {
            var tasks = new[] { _receiveTask, _supervisorTask }.Where(t => t is not null).Select(t => t!).ToArray();
            if (tasks.Length > 0)
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Shutdown races are expected.
        }

        if (_encoderThread is not null && _encoderThread.IsAlive)
        {
            try
            {
                _encoderThread.Join(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // ignored
            }
        }

        _encoderThread = null;
        _receiveTask = null;
        _supervisorTask = null;

        // A supervisor pass that was already building a recorder when cancellation arrived can
        // finish after the first StopRecorder; make sure nothing keeps capturing after shutdown.
        StopRecorder();

        await DisposeSocketAsync().ConfigureAwait(false);
        cts?.Dispose();
        _sessionChanged.Dispose();
        _status = "Audio stopped.";
    }
}
