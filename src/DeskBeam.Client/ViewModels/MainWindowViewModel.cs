using System.ComponentModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskBeam.Client.Input;
using DeskBeam.Client.Networking;
using DeskBeam.Client.Rendering;
using DeskBeam.Core.Protocol;
using DeskBeam.Core.Settings;

namespace DeskBeam.Client.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly XInputGamepad _xinput = new();
    private readonly FramePresenter _presenter = new();
    private readonly ClientSettings _settings;

    private ClientConnection? _connection;
    private CancellationTokenSource? _gamepadCts;
    private Task? _gamepadTask;
    private DispatcherTimer? _statsTimer;
    private string _hostStatsLine = string.Empty;
    private string _lastStatus = "Not connected.";
    private bool _warningDismissed;
    private int _generation;
    private int _frameWidth;
    private int _frameHeight;

    public MainWindowViewModel()
    {
        _settings = JsonStore.Load(AppPaths.ClientSettingsFile, () => new ClientSettings());
        if (string.IsNullOrWhiteSpace(_settings.ClientName))
            _settings.ClientName = Environment.MachineName;

        _host = _settings.Host;
        _portText = _settings.Port.ToString();
        _pin = _settings.Pin;
        _clientName = _settings.ClientName;
        _forwardMouse = _settings.ForwardMouse;
        _forwardKeyboard = _settings.ForwardKeyboard;
        _showStats = _settings.ShowStats;
        _muteAudio = _settings.MuteAudio;
        _controllerText = _xinput.IsAvailable ? _xinput.Status : "XInput unavailable on this PC.";
    }

    [ObservableProperty] private string _host;
    [ObservableProperty] private string _portText;
    [ObservableProperty] private string _pin;
    [ObservableProperty] private string _clientName;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _showPlaceholder = true;
    [ObservableProperty] private string _statusText = "Not connected.";
    [ObservableProperty] private string _statsText = string.Empty;
    [ObservableProperty] private string _controllerText;
    [ObservableProperty] private bool _forwardMouse;
    [ObservableProperty] private bool _forwardKeyboard;
    [ObservableProperty] private bool _showStats;
    [ObservableProperty] private bool _muteAudio;
    [ObservableProperty] private string _warningText = string.Empty;
    [ObservableProperty] private bool _hasWarning;
    [ObservableProperty] private WriteableBitmap? _frame;

    public bool ConnectEnabled => !IsConnected && !IsBusy;
    public bool DisconnectEnabled => IsConnected && !IsBusy;

    public int FrameWidth
    {
        get => _frameWidth;
        private set
        {
            if (_frameWidth == value)
                return;
            _frameWidth = value;
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(FrameWidth)));
        }
    }

    public int FrameHeight
    {
        get => _frameHeight;
        private set
        {
            if (_frameHeight == value)
                return;
            _frameHeight = value;
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(FrameHeight)));
        }
    }

    public ClientConnection? Connection => _connection;

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectEnabled));
        OnPropertyChanged(nameof(DisconnectEnabled));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectEnabled));
        OnPropertyChanged(nameof(DisconnectEnabled));
    }

    partial void OnMuteAudioChanged(bool value)
    {
        if (_connection is not null)
            _connection.MuteAudio = value;
        SaveSettings();
    }

    private async ValueTask PresentFrameAsync(int width, int height, byte[] bgra, int generation, CancellationToken ct)
    {
        // WriteableBitmap must be touched on the UI thread. Awaiting here also applies natural
        // backpressure: the host's frame mailbox drops frames we cannot present in time.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != _generation)
                return;

            Frame = _presenter.Present(width, height, bgra);
            FrameWidth = width;
            FrameHeight = height;
            ShowPlaceholder = false;
        });
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsConnected || IsBusy)
            return;

        if (string.IsNullOrWhiteSpace(Host))
        {
            StatusText = "Enter the host address.";
            return;
        }

        if (!int.TryParse(PortText, out var port) || port is < 1 or > 65535)
        {
            StatusText = "Enter a valid TCP port (1-65535).";
            return;
        }

        IsBusy = true;
        HasWarning = false;
        WarningText = string.Empty;
        _warningDismissed = false;
        StatusText = $"Connecting to {Host.Trim()}:{port}...";

        var connection = new ClientConnection { MuteAudio = MuteAudio };
        var generation = ++_generation;
        connection.Log += message => Dispatcher.UIThread.Post(() => StatusText = message);
        connection.Disconnected += reason => Dispatcher.UIThread.Post(() => _ = TeardownAsync(reason));
        connection.HostStatistics += stats => Dispatcher.UIThread.Post(() => ApplyHostStats(stats));
        connection.RumbleReceived += rumble => _xinput.SetVibration(rumble.LeftMotor, rumble.RightMotor);

        try
        {
            await connection.ConnectAsync(
                Host.Trim(),
                port,
                Pin,
                string.IsNullOrWhiteSpace(ClientName) ? Environment.MachineName : ClientName,
                (width, height, bgra, ct) => PresentFrameAsync(width, height, bgra, generation, ct),
                CancellationToken.None);

            _connection = connection;
            IsConnected = true;
            var warning = string.Join(" ", new[] { connection.VideoWarning, connection.AudioWarning }
                .Where(w => !string.IsNullOrWhiteSpace(w)));
            if (!string.IsNullOrWhiteSpace(warning))
            {
                HasWarning = true;
                WarningText = warning;
            }

            StatusText = $"Connected to {connection.ServerName}.";
            ControllerText = _xinput.Status;

            StartGamepadLoop(connection);
            StartStatsTimer();
            SaveSettings();
        }
        catch (Exception ex)
        {
            _connection = null;
            IsConnected = false;
            _generation++;
            ShowPlaceholder = true;
            StatusText = "Connection failed: " + ex.Message;
            await connection.DisposeAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await TeardownAsync("Disconnected.");
    }

    [RelayCommand]
    private void DismissWarning()
    {
        _warningDismissed = true;
        HasWarning = false;
        WarningText = string.Empty;
    }

    public void SendPointer(PointerAction action, double x, double y, int button = 0, int wheel = 0)
    {
        if (!ForwardMouse || _connection is null || !IsConnected)
            return;

        _connection.SendPointer(new PointerInput(action, x, y, button, wheel));
    }

    public void SendKey(int virtualKey, bool pressed)
    {
        if (!ForwardKeyboard || virtualKey == 0 || _connection is null || !IsConnected)
            return;

        _connection.SendKey(virtualKey, pressed);
    }

    private void ApplyHostStats(HostStats stats)
    {
        var audio = stats.AudioActive ? $" · audio {stats.AudioKbps:F0} kbps" : string.Empty;
        _hostStatsLine =
            $"host {stats.CaptureFps:F0} fps · {stats.EncodeMs:F1} ms encode · {stats.Kbps:F0} kbps{audio}";
        if (!_warningDismissed &&
            !string.IsNullOrWhiteSpace(stats.Note) &&
            stats.Note.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
        {
            HasWarning = true;
            WarningText = stats.Note;
        }
    }

    private void StartStatsTimer()
    {
        StopStatsTimer();
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statsTimer.Tick += (_, _) => UpdateStats();
        _statsTimer.Start();
    }

    private void UpdateStats()
    {
        var connection = _connection;
        if (connection is null)
            return;

        var metrics = connection.SampleMetrics();
        var audio = connection.SampleAudioMetrics();
        var adaptive = connection.AdaptiveLevel > 0
            ? $" · adaptive {connection.RequestedWidth}x{connection.RequestedHeight} q{connection.RequestedQuality}"
            : string.Empty;
        var audioLine = connection.AudioAvailable
            ? $"audio {audio.ReceivedFps:F0} pkt/s · {audio.LossPercent:F1}% loss · {audio.JitterMs:F0} ms jitter · " +
              $"{audio.BufferMs:F0} ms buffer" + (MuteAudio ? " · muted" : string.Empty)
            : "audio off";
        StatsText =
            $"{metrics.Fps:F0} fps · {metrics.Mbps:F1} Mbps · {metrics.DecodeMs:F1} ms decode · {metrics.RttMs} ms RTT{adaptive}\n" +
            $"{metrics.Width}x{metrics.Height} · {audioLine}" +
            (string.IsNullOrEmpty(_hostStatsLine) ? string.Empty : "\n" + _hostStatsLine);
    }

    private void StopStatsTimer()
    {
        _statsTimer?.Stop();
        _statsTimer = null;
    }

    private void StartGamepadLoop(ClientConnection connection)
    {
        StopGamepadLoop();
        _gamepadCts = new CancellationTokenSource();
        var ct = _gamepadCts.Token;

        _gamepadTask = Task.Run(async () =>
        {
            var wasConnected = false;
            var lastPacket = uint.MaxValue;

            while (!ct.IsCancellationRequested && connection.IsConnected)
            {
                try
                {
                    if (_xinput.TryPoll(out var state))
                    {
                        if (state.PacketNumber != lastPacket)
                        {
                            connection.SendGamepad(state);
                            lastPacket = state.PacketNumber;
                        }

                        if (!wasConnected)
                        {
                            wasConnected = true;
                            var status = _xinput.Status;
                            Dispatcher.UIThread.Post(() => ControllerText = status);
                        }
                    }
                    else if (wasConnected)
                    {
                        wasConnected = false;
                        lastPacket = uint.MaxValue;
                        connection.SendGamepad(new GamepadState(0, 0, 0, 0, 0, 0, 0, 0, false));
                        var status = _xinput.Status;
                        Dispatcher.UIThread.Post(() => ControllerText = status);
                    }

                    await Task.Delay(10, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }, ct);
    }

    private void StopGamepadLoop()
    {
        try
        {
            _gamepadCts?.Cancel();
        }
        catch (Exception)
        {
            // ignored
        }

        _gamepadCts?.Dispose();
        _gamepadCts = null;
        _gamepadTask = null;
    }

    private async Task TeardownAsync(string status)
    {
        var connection = _connection;
        _connection = null;

        if (connection is null && !IsConnected)
        {
            StatusText = status;
            return;
        }

        StopGamepadLoop();
        StopStatsTimer();

        _generation++;
        IsConnected = false;
        ShowPlaceholder = true;
        Frame = null;
        FrameWidth = 0;
        FrameHeight = 0;
        HasWarning = false;
        WarningText = string.Empty;
        StatsText = string.Empty;
        _hostStatsLine = string.Empty;
        StatusText = status;
        ControllerText = _xinput.IsAvailable ? _xinput.Status : "XInput unavailable on this PC.";
        _lastStatus = status;

        _xinput.SetVibration(0, 0);
        SaveSettings();

        if (connection is not null)
            await connection.DisposeAsync();
    }

    public void SaveSettings()
    {
        _settings.Host = Host;
        _settings.Pin = Pin;
        _settings.ClientName = ClientName;
        _settings.ForwardMouse = ForwardMouse;
        _settings.ForwardKeyboard = ForwardKeyboard;
        _settings.ShowStats = ShowStats;
        _settings.MuteAudio = MuteAudio;
        if (int.TryParse(PortText, out var port))
            _settings.Port = port;

        JsonStore.Save(AppPaths.ClientSettingsFile, _settings);
    }

    public async ValueTask DisposeAsync()
    {
        await TeardownAsync(_lastStatus);
        StopGamepadLoop();
        _xinput.Dispose();
        SaveSettings();
    }
}
