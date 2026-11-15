using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using DeskBeam.Core.Audio;
using NAudio.Wave;

namespace DeskBeam.Client.Audio;

public readonly record struct AudioMetrics(
    double ReceivedFps,
    double LossPercent,
    double JitterMs,
    double BufferMs);

/// <summary>
/// Receives the host's UDP audio channel, reorders packets in a small jitter buffer, decodes Opus
/// and feeds WASAPI. A lost packet becomes a concealed frame, so audio glitches stay short while
/// the video path is never throttled by the audio socket.
/// </summary>
public sealed class AudioReceiver : IAsyncDisposable
{
    private const int JitterDepth = 2;
    private const int PrebufferPackets = JitterDepth + 1;
    private const int MaxPlcFrames = 20;
    private const int MaxConcealGap = 5;
    private const int MaxQueuedPackets = 6;
    private static readonly TimeSpan TargetPlaybackBuffer = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan ConcealBelowBuffer = TimeSpan.FromMilliseconds(10);

    private readonly string _host;
    private readonly int _port;
    private readonly uint _sessionId;
    private readonly string _token;
    private readonly Action<string>? _log;
    private readonly object _bufferGate = new();
    private readonly object _decodeGate = new();
    private readonly SortedDictionary<uint, byte[]> _packets = [];
    private readonly OpusAudioDecoder _decoder = new();
    private readonly byte[] _pcm = new byte[AudioConstants.PcmBytesPerFrame];
    private readonly BufferedWaveProvider _playbackBuffer;
    private readonly WasapiPlayer? _output;

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _playbackTask;
    private Task? _keepaliveTask;
    private uint _lastPlayed;
    private uint _highestArrived;
    private uint _expectedSequence;
    private bool _started;
    private volatile bool _muted;
    private int _plcFrames;
    private long _lastTransit;
    private double _jitter;
    private int _windowReceived;
    private int _windowLost;
    private long _windowStart = Stopwatch.GetTimestamp();
    private readonly object _metricsGate = new();
    private AudioMetrics _lastMetrics;

    public AudioReceiver(string host, int port, string audioId, string token, bool startMuted = false,
        Action<string>? log = null)
    {
        _host = host;
        _port = port;
        _token = token;
        _log = log;
        _muted = startMuted;

        if (!uint.TryParse(audioId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _sessionId))
            _sessionId = 0;

        _playbackBuffer = new BufferedWaveProvider(
            new WaveFormat(AudioConstants.SampleRate, 16, AudioConstants.Channels),
            TimeSpan.FromMilliseconds(50))
        {
            ReadFully = true,
            DiscardOnBufferOverflow = true,
        };

        try
        {
            // A short event-driven buffer keeps latency around 50-100 ms end to end. Low latency
            // mode is opportunistic: when the endpoint cannot do it the player falls back cleanly.
            _output = new WasapiPlayerBuilder()
                .WithSharedMode()
                .WithEventSync()
                .WithLatency(50)
                .WithLowLatency(required: false)
                .WithMmcssThreadPriority("Audio")
                .Build();
            _output.Init(_playbackBuffer);
            _output.Play();
        }
        catch (Exception ex)
        {
            _output = null;
            LastError = "Audio playback unavailable: " + ex.Message;
            _log?.Invoke(LastError);
        }
    }

    public bool IsAvailable => _udp is not null && _output is not null && _sessionId != 0;

    public string? LastError { get; private set; }

    public bool Muted
    {
        get => _muted;
        set
        {
            if (_muted == value)
                return;

            _muted = value;

            // Both directions reset the pipeline: muting drops the backlog, and unmuting must
            // resynchronise against the live packet stream instead of replaying stale frames.
            ResetPlayback(clearBuffer: true);

            _log?.Invoke(value ? "Audio muted." : "Audio unmuted.");
        }
    }

    public async Task<bool> StartAsync(CancellationToken ct)
    {
        if (_sessionId == 0 || _output is null)
        {
            LastError ??= "The host did not provide a usable audio channel.";
            return false;
        }

        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            var addresses = await Dns.GetHostAddressesAsync(_host, ct).ConfigureAwait(false);
            var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                          ?? addresses.FirstOrDefault()
                          ?? throw new SocketException((int)SocketError.HostNotFound);
            _udp.Connect(address, _port);

            // Connected UDP binds an ephemeral port; the host learns it from this bind datagram.
            SendBind();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _cts.Token;
            _receiveTask = Task.Run(() => ReceiveLoopAsync(token), CancellationToken.None);
            _playbackTask = Task.Run(() => PlaybackLoopAsync(token), CancellationToken.None);
            _keepaliveTask = Task.Run(() => KeepaliveLoopAsync(token), CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            LastError = "Audio channel unavailable: " + ex.Message;
            _log?.Invoke(LastError);
            await DisposeAsync().ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// Samples and resets the loss window. This is the authoritative report source, so it must be
    /// called from a single loop; use <see cref="PeekMetrics"/> for display.
    /// </summary>
    public AudioMetrics SampleMetrics()
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = (now - _windowStart) / (double)Stopwatch.Frequency;
        if (seconds <= 0)
            seconds = 1;

        var received = Interlocked.Exchange(ref _windowReceived, 0);
        var lost = Interlocked.Exchange(ref _windowLost, 0);
        _windowStart = now;

        var total = received + lost;
        var loss = total > 0 ? lost * 100.0 / total : 0;

        var metrics = new AudioMetrics(
            received / seconds,
            loss,
            _jitter,
            _playbackBuffer.BufferedDuration.TotalMilliseconds);

        lock (_metricsGate)
            _lastMetrics = metrics;

        return metrics;
    }

    /// <summary>
    /// Last sampled loss figures with a live buffer level. Reading this must never reset the
    /// counters, otherwise a UI refresh would starve the report the host uses to mute.
    /// </summary>
    public AudioMetrics PeekMetrics()
    {
        lock (_metricsGate)
        {
            return _lastMetrics with
            {
                BufferMs = _playbackBuffer.BufferedDuration.TotalMilliseconds,
            };
        }
    }

    private void SendBind()
    {
        try
        {
            var datagram = AudioPacket.CreateBind(_token);
            _udp?.Send(datagram, datagram.Length);
        }
        catch (Exception ex)
        {
            _log?.Invoke("Audio keepalive failed: " + ex.Message);
        }
    }

    private async Task KeepaliveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(2000, ct).ConfigureAwait(false);
                SendBind();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var socket = _udp;
        if (socket is null)
            return;

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
                if (ex.SocketErrorCode is not (SocketError.ConnectionReset or SocketError.MessageSize))
                    _log?.Invoke("Audio receive failed: " + ex.Message);
                continue;
            }

            var datagram = result.Buffer;
            if (!AudioPacket.TryParse(datagram, out var header) ||
                header.Kind != AudioPacketKind.Audio ||
                header.SessionId != _sessionId ||
                header.PayloadLength <= 0)
                continue;

            var payload = datagram.AsSpan(AudioConstants.HeaderBytes, header.PayloadLength).ToArray();
            TrackArrival(header);
            Enqueue(header.Sequence, payload);
        }
    }

    private void TrackArrival(AudioPacketHeader header)
    {
        // RFC 3550-style interarrival jitter. Both clocks are reduced to their low 32 bits, so the
        // constant offset cancels and the signed difference survives a 49.7-day TickCount wrap.
        var transit = unchecked((int)((uint)Environment.TickCount64 - header.TimestampMs));
        if (_lastTransit != 0)
        {
            var delta = Math.Abs(transit - _lastTransit);
            _jitter += (delta - _jitter) / 16.0;
        }

        _lastTransit = transit;

        if (_expectedSequence != 0)
        {
            var gap = unchecked((int)(header.Sequence - _expectedSequence));

            // A gap longer than half a second is a host-side mute/resync, not packet loss.
            // Counting it as loss would immediately make the host mute us again.
            if (gap is > 0 and <= 50)
                Interlocked.Add(ref _windowLost, gap);
        }

        _expectedSequence = header.Sequence + 1;
        Interlocked.Increment(ref _windowReceived);
    }

    private void Enqueue(uint sequence, byte[] payload)
    {
        lock (_bufferGate)
        {
            if (!_started)
            {
                _packets[sequence] = payload;
                _highestArrived = _packets.Keys.Last();

                // Keep only a fresh tail so a restart never replays stale audio.
                while (_packets.Count > PrebufferPackets)
                    _packets.Remove(_packets.Keys.First());
                return;
            }

            var delta = unchecked((int)(sequence - _lastPlayed));
            if (delta <= 0)
                return;

            if (delta > 1000)
            {
                // The sender restarted (or we were paused for a long time); resynchronise.
                _packets.Clear();
                _packets[sequence] = payload;
                _highestArrived = sequence;
                _started = false;
                return;
            }

            _packets[sequence] = payload;
            _highestArrived = _packets.Keys.Last();

            // Bound buffered latency. Extra packets are a network burst or clock skew, and playing
            // them all would push audio progressively further behind the video.
            while (_packets.Count > MaxQueuedPackets)
                _packets.Remove(_packets.Keys.First());
        }
    }

    private async Task PlaybackLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_muted)
                {
                    await Task.Delay(10, ct).ConfigureAwait(false);
                    continue;
                }

                // Top up to a small target instead of running a wall-clock timer: WASAPI consumes
                // at exactly real time, so the buffer level is the most reliable frame clock.
                if (_playbackBuffer.BufferedDuration >= TargetPlaybackBuffer)
                {
                    await Task.Delay(3, ct).ConfigureAwait(false);
                    continue;
                }

                byte[]? packet = null;
                byte[]? fecPacket = null;
                var playConcealment = false;
                var stalled = false;

                lock (_bufferGate)
                {
                    if (!_started)
                    {
                        if (_packets.Count >= PrebufferPackets)
                        {
                            ResetDecoder();
                            _lastPlayed = _packets.Keys.First() - 1;
                            _started = true;
                            _plcFrames = 0;
                        }
                    }
                    else
                    {
                        var next = _lastPlayed + 1;
                        if (_packets.TryGetValue(next, out packet))
                        {
                            _packets.Remove(next);
                            _lastPlayed = next;
                            _plcFrames = 0;
                        }
                        else if (_packets.Count > 0)
                        {
                            var oldest = _packets.Keys.First();
                            var gap = unchecked((int)(oldest - next));
                            if (gap > MaxConcealGap)
                            {
                                // Too far behind to conceal; skip to the newest buffered packet.
                                ResetDecoder();
                                _lastPlayed = oldest - 1;
                                _plcFrames = 0;
                            }
                            else if (unchecked((int)(_highestArrived - next)) >= JitterDepth ||
                                     _playbackBuffer.BufferedDuration <= ConcealBelowBuffer)
                            {
                                // The frame is older than the jitter window (or playback is about to
                                // run dry), so it will not arrive in time. Conceal right away: pausing
                                // for a late packet drains the cushion and adds latency after losses.
                                _packets.TryGetValue(next + 1, out fecPacket);
                                playConcealment = true;
                                _lastPlayed = next;
                                _plcFrames = 0;
                            }
                            // Otherwise the frame is only just due; it still has the jitter window
                            // (JitterDepth frames) to arrive before we give up on it.
                        }
                        else if (_plcFrames < MaxPlcFrames)
                        {
                            // Nothing buffered: keep the 10 ms cadence with concealment for a
                            // moment, then wait for a fresh prebuffer instead of drifting ahead.
                            playConcealment = true;
                            _lastPlayed++;
                            _plcFrames++;
                        }
                        else
                        {
                            _started = false;
                        }
                    }
                }

                if (packet is null && !playConcealment)
                    stalled = true;

                if (packet is not null)
                {
                    lock (_decodeGate)
                    {
                        if (!_decoder.TryDecode(packet, 0, packet.Length, _pcm))
                            _decoder.TryDecodeLost(_pcm);
                    }

                    _playbackBuffer.AddSamples(_pcm, 0, _pcm.Length);
                    _plcFrames = 0;
                }
                else if (playConcealment)
                {
                    lock (_decodeGate)
                    {
                        // Prefer the redundant copy in the next packet over a synthetic concealment.
                        var decoded = fecPacket is not null && _decoder.TryDecodeFec(fecPacket, _pcm);
                        if (!decoded)
                            decoded = _decoder.TryDecodeLost(_pcm);
                        if (decoded)
                            _playbackBuffer.AddSamples(_pcm, 0, _pcm.Length);
                    }
                }
                else if (stalled)
                {
                    await Task.Delay(2, ct).ConfigureAwait(false);
                }

            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            LastError = "Audio playback error: " + ex.Message;
            _log?.Invoke(LastError);
        }
    }

    private void ResetPlayback(bool clearBuffer)
    {
        lock (_bufferGate)
        {
            _packets.Clear();
            _started = false;
            _lastPlayed = 0;
            _highestArrived = 0;
            _expectedSequence = 0;
            _plcFrames = 0;
            ResetDecoder();
        }

        if (clearBuffer)
            _playbackBuffer.ClearBuffer();

        _lastTransit = 0;
        _jitter = 0;
    }

    private void ResetDecoder()
    {
        lock (_decodeGate)
            _decoder.Reset();
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

        var udp = _udp;
        _udp = null;
        try
        {
            udp?.Dispose();
        }
        catch (Exception)
        {
            // ignored
        }

        var tasks = new[] { _receiveTask, _playbackTask, _keepaliveTask }
            .Where(t => t is not null)
            .Select(t => t!)
            .ToArray();
        _receiveTask = null;
        _playbackTask = null;
        _keepaliveTask = null;

        try
        {
            if (tasks.Length > 0)
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Shutdown races are expected.
        }

        try
        {
            _output?.Stop();
        }
        catch (Exception)
        {
            // ignored
        }

        try
        {
            _output?.Dispose();
        }
        catch (Exception)
        {
            // ignored
        }

        cts?.Dispose();
    }
}
