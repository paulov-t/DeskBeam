using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using DeskBeam.Core.Audio;
using DeskBeam.Core.Models;
using DeskBeam.Core.Protocol;
using DeskBeam.Core.Security;
using DeskBeam.Engine.Input;

namespace DeskBeam.Engine.Streaming;

internal sealed class ClientSession : IAsyncDisposable
{
    private readonly HostServer _server;
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly string _nonce;

    private VirtualGamepad? _pad;
    private DesktopInjector? _injector;
    private long _lastSeenTicks;
    private long _lastKeyframeRequestTicks;
    private bool _disposed;
    private string _lastStreamRequest = string.Empty;

    public ClientSession(string id, TcpClient client, HostServer server)
    {
        Id = id;
        _client = client;
        _server = server;
        _stream = client.GetStream();
        _nonce = Auth.CreateNonce();
        RemoteAddress = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        _lastSeenTicks = Environment.TickCount64;
    }

    public string Id { get; }
    public string Name { get; private set; } = "Remote player";
    public string RemoteAddress { get; }
    public string Token { get; private set; } = string.Empty;
    public string AudioId { get; } = RandomNumberGenerator.GetInt32(1, int.MaxValue).ToString("x8");
    public bool IsAuthenticated { get; private set; }
    public bool VideoConnected { get; private set; }
    private bool _audioRegistered;
    public bool GamepadConnected => _pad is not null;
    public long LastSeenTicks => _lastSeenTicks;
    public CancellationToken SessionToken => _cts.Token;

    public async Task<bool> HandshakeAsync(CancellationToken ct)
    {
        var options = _server.CurrentOptions;

        await SendAsync(new ServerHello(
            ProtocolConstants.Version,
            !string.IsNullOrEmpty(options.Pin),
            options.ServerName,
            _nonce), ct).ConfigureAwait(false);

        var message = await Wire.ReceiveControlAsync(_stream, ct).ConfigureAwait(false);
        if (message is not AuthRequest auth)
        {
            await SendAsync(new AuthResult(false, "Expected an authentication request.", null), ct).ConfigureAwait(false);
            return false;
        }

        if (!string.IsNullOrEmpty(options.Pin))
        {
            var expected = Auth.ComputeProof(options.Pin, _nonce, auth.ClientName ?? string.Empty,
                ProtocolConstants.Version);
            if (!Auth.VerifyProof(expected, auth.Proof ?? string.Empty))
            {
                await SendAsync(new AuthResult(false, "Incorrect PIN.", null), ct).ConfigureAwait(false);
                _server.LogMessage($"Rejected {RemoteAddress}: incorrect PIN.");
                return false;
            }
        }

        Name = Sanitize(auth.ClientName);
        Token = Auth.CreateSessionToken();

        string? warning = null;

        // Mouse/keyboard injection and the virtual pad are independent settings. The host UI has
        // separate checkboxes, so a host that only wants a controller (AllowInput = false,
        // EnableVirtualGamepad = true) must still get the virtual pad.
        if (options.AllowInput)
        {
            var monitor = _server.GetSelectedMonitor();
            if (monitor is not null)
                _injector = new DesktopInjector(monitor);
        }

        if (options.EnableVirtualGamepad)
            warning = TryCreateGamepad();

        if (!options.AllowInput)
        {
            var mouseNote = _pad is not null
                ? "Remote mouse and keyboard are disabled; the virtual Xbox pad is active."
                : "Remote mouse and keyboard are disabled by the host.";
            warning = warning is null ? mouseNote : warning + " " + mouseNote;
        }

        var size = _server.GetOutputSize();
        var monitorName = _server.GetSelectedMonitor()?.Display ?? "Desktop";

        IsAuthenticated = true;
        _lastSeenTicks = Environment.TickCount64;

        var audio = _server.Audio;
        var audioAvailable = audio is { IsAvailable: true } && audio.Register(Token, AudioId);
        _audioRegistered = audioAvailable;

        await SendAsync(new AuthResult(true, null, warning), ct).ConfigureAwait(false);
        await SendAsync(new StreamInfo(
            options.VideoPort,
            Token,
            size.Width,
            size.Height,
            options.Fps,
            options.Quality,
            monitorName,
            audioAvailable ? options.AudioPort : 0,
            audioAvailable,
            AudioConstants.SampleRate,
            AudioConstants.Channels,
            AudioConstants.FrameMilliseconds,
            Math.Clamp(options.AudioBitrateKbps, 8, 256),
            audioAvailable ? AudioId : string.Empty), ct).ConfigureAwait(false);

        // Subscribe only after the handshake messages have been sent: the ViGEm pad raises a
        // feedback event as soon as it connects, and a rumble message written before AuthResult
        // would desynchronize the client's fixed handshake sequence.
        if (_pad is not null)
            _pad.RumbleRequested += OnPadRumble;

        return true;
    }

    private void OnPadRumble(byte left, byte right) => PostMessage(new RumbleState(left, right));

    private string? TryCreateGamepad()
    {
        try
        {
            _pad = new VirtualGamepad();
            _server.NotifyGamepadCreated();
            return null;
        }
        catch (Exception ex)
        {
            _server.LogMessage("Virtual gamepad unavailable: " + ex.Message);
            return "Virtual Xbox controller unavailable on the host. Install ViGEmBus to add a controller.";
        }
    }

    private static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Remote player";

        var cleaned = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length == 0)
            return "Remote player";
        return cleaned.Length > 40 ? cleaned[..40] : cleaned;
    }

    public async Task RunAsync(CancellationToken serverToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken, _cts.Token);

        try
        {
            while (!linked.IsCancellationRequested)
            {
                var message = await Wire.ReceiveControlAsync(_stream, linked.Token).ConfigureAwait(false);
                if (message is null)
                    break;

                _lastSeenTicks = Environment.TickCount64;
                await HandleMessageAsync(message, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal disconnect.
        }
        catch (IOException)
        {
            // Socket closed by the peer.
        }
        catch (Exception ex)
        {
            _server.LogMessage($"{Name}: control channel error: {ex.Message}");
        }
    }

    private async ValueTask HandleMessageAsync(ControlMessage message, CancellationToken ct)
    {
        var options = _server.CurrentOptions;

        switch (message)
        {
            case Ping ping:
                await SendAsync(new Pong(ping.ClientTicks, Stopwatch.GetTimestamp()), ct).ConfigureAwait(false);
                break;

            case GamepadState state:
                if (options.EnableVirtualGamepad)
                {
                    EnsureGamepad(options);
                    _pad?.Update(state);
                }
                break;

            case PointerInput pointer:
                if (options.AllowInput)
                {
                    EnsureInjector();
                    _injector?.Apply(pointer);
                }
                break;

            case KeyInput key:
                if (options.AllowInput)
                {
                    EnsureInjector();
                    _injector?.Key(key.VirtualKey, key.Pressed);
                }
                break;

            case AudioReport report:
                _server.ApplyAudioReport(Token, report);
                break;

            case KeyframeRequest:
                var now = Environment.TickCount64;
                if (unchecked(now - _lastKeyframeRequestTicks) >= 250)
                {
                    _lastKeyframeRequestTicks = now;
                    _server.RequestKeyframe();
                }

                break;

            case RequestStream request:
                var signature = $"{request.Width}x{request.Height}@{request.Fps}q{request.Quality}";
                if (!string.Equals(signature, _lastStreamRequest, StringComparison.Ordinal))
                {
                    _lastStreamRequest = signature;
                    _server.LogMessage(
                        $"{Name} requested {request.Width}x{request.Height} @ {request.Fps} fps (q{request.Quality}).");
                }

                _server.ApplyStreamRequest(request);
                break;

            case Disconnect disconnect:
                _server.LogMessage($"{Name} disconnected: {disconnect.Reason}");
                _cts.Cancel();
                break;
        }
    }

    private void EnsureInjector()
    {
        if (_injector is null)
        {
            var monitor = _server.GetSelectedMonitor();
            if (monitor is not null)
                _injector = new DesktopInjector(monitor);
        }
    }

    private void EnsureGamepad(HostOptions options)
    {
        if (_pad is null && options.EnableVirtualGamepad)
            TryCreateGamepad();
    }

    public bool TryAttachVideo()
    {
        if (VideoConnected)
            return false;
        VideoConnected = true;
        return true;
    }

    public void DetachVideo() => VideoConnected = false;

    public async ValueTask SendAsync(ControlMessage message, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Wire.SendControlAsync(_stream, message, ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                _sendLock.Release();
            }
            catch (ObjectDisposedException)
            {
                // Session already disposed.
            }
        }
    }

    public void PostMessage(ControlMessage message)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await SendAsync(message, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort.
            }
        });
    }

    public void Abort(string reason)
    {
        _server.LogMessage($"{Name}: {reason}");
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // ignored
        }

        try
        {
            _client.Close();
        }
        catch (Exception)
        {
            // ignored
        }
    }

    public ClientSnapshot ToSnapshot()
    {
        var audio = _server.Audio;
        return new ClientSnapshot(
            Id,
            Name,
            RemoteAddress,
            0,
            VideoConnected,
            GamepadConnected,
            VideoConnected,
            _audioRegistered && audio?.IsClientAudioConnected(Token) == true,
            _audioRegistered && audio?.IsClientMuted(Token) == true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // ignored
        }

        if (_audioRegistered)
        {
            _server.Audio?.Unregister(Token);
            _audioRegistered = false;
        }

        _pad?.RumbleRequested -= OnPadRumble;
        _pad?.Dispose();
        _pad = null;

        // Never leave the local desktop with a key or mouse button held down.
        _injector?.ReleaseAll();
        _injector = null;

        _client.Dispose();
        _stream.Dispose();

        try
        {
            _sendLock.Dispose();
        }
        catch (Exception)
        {
            // ignored
        }

        _cts.Dispose();
    }
}
