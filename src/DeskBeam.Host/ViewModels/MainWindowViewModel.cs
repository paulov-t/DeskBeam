using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskBeam.Core.Models;
using DeskBeam.Core.Net;
using DeskBeam.Core.Settings;
using DeskBeam.Engine.Capture;
using DeskBeam.Engine.Streaming;

namespace DeskBeam.Host.ViewModels;

public sealed record ClientRow(string Name, string Remote, string Summary);

public partial class MainWindowViewModel : ObservableObject
{
    private sealed record ResolutionPreset(string Label, int Width, int Height, bool Native = false);

    private static readonly ResolutionPreset[] Presets =
    [
        new("1280 × 720 (720p)", 1280, 720),
        new("1600 × 900", 1600, 900),
        new("1920 × 1080 (1080p)", 1920, 1080),
        new("2560 × 1440 (1440p)", 2560, 1440),
        new("Match monitor resolution", 0, 0, Native: true),
    ];

    private static readonly int[] FpsChoices = [30, 60, 90, 120];

    private static readonly (string Label, CaptureApi Api)[] CaptureChoices =
    [
        ("Auto (recommended)", CaptureApi.Auto),
        ("Windows Graphics Capture (games)", CaptureApi.WindowsGraphicsCapture),
        ("DXGI Desktop Duplication", CaptureApi.DesktopDuplication),
        ("GDI (slow fallback)", CaptureApi.Gdi),
    ];

    private readonly HostServer _server = new();
    private readonly DispatcherTimer _timer;
    private readonly HostSettings _settings;

    private MonitorDescriptor[] _monitors = [];
    private UpnpMappingResult? _mapping;

    public MainWindowViewModel()
    {
        _settings = JsonStore.Load(AppPaths.HostSettingsFile, () => new HostSettings());
        try
        {
            File.AppendAllText(AppPaths.LogFile,
                $"{Environment.NewLine}=== DeskBeam host {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
        }
        catch (Exception)
        {
            // Optional diagnostic log.
        }

        if (string.IsNullOrWhiteSpace(_settings.Pin))
        {
            // First run: generate a PIN so the host is never accidentally open.
            _settings.Pin = GeneratePin();
        }

        RefreshMonitorList();

        _serverName = _settings.ServerName;
        _portText = _settings.Port.ToString();
        _pin = _settings.Pin;
        _selectedMonitorIndex = ClampMonitor(_settings.MonitorIndex);
        _resolutionIndex = FindResolutionIndex(_settings.MaxWidth, _settings.MaxHeight);
        _fpsIndex = Array.IndexOf(FpsChoices, _settings.Fps) is var fps and >= 0 ? fps : 1;
        _quality = Math.Clamp(_settings.Quality, 40, 90);
        _allowInput = _settings.AllowInput;
        _enableVirtualGamepad = _settings.EnableVirtualGamepad;
        _includeCursor = _settings.IncludeCursor;
        _captureApiIndex = Array.FindIndex(CaptureChoices, c => c.Api == _settings.CaptureApi) is var api and >= 0 ? api : 0;
        _mapPortsOnStart = _settings.MapPortsOnStart;
        _qualityLabel = $"JPEG quality {_quality:F0}";
        _enableAudio = _settings.EnableAudio;
        _audioBitrate = Math.Clamp(_settings.AudioBitrateKbps, 32, 128);
        _audioExcludedProcesses = _settings.AudioExcludedProcesses;
        _audioBitrateLabel = $"Opus bitrate {_audioBitrate:F0} kbps";

        _server.Log += message => Dispatcher.UIThread.Post(() => AppendLog(message));
        _server.ClientsChanged += () => Dispatcher.UIThread.Post(RefreshClients);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        UpdateShareAddress();
    }

    [ObservableProperty] private string _serverName;
    [ObservableProperty] private string _portText;
    [ObservableProperty] private string _pin;
    [ObservableProperty] private int _selectedMonitorIndex;
    [ObservableProperty] private int _resolutionIndex;
    [ObservableProperty] private int _fpsIndex;
    [ObservableProperty] private double _quality;
    [ObservableProperty] private string _qualityLabel;
    [ObservableProperty] private bool _allowInput;
    [ObservableProperty] private bool _enableVirtualGamepad;
    [ObservableProperty] private bool _includeCursor;
    [ObservableProperty] private int _captureApiIndex;
    [ObservableProperty] private bool _mapPortsOnStart;
    [ObservableProperty] private bool _enableAudio;
    [ObservableProperty] private double _audioBitrate;
    [ObservableProperty] private string _audioBitrateLabel;
    [ObservableProperty] private string _audioExcludedProcesses;
    [ObservableProperty] private string _audioStatus = "Audio status unknown until the host starts.";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "Stopped. Configure the host and press Start.";
    [ObservableProperty] private string _statsText = "No stream yet.";
    [ObservableProperty] private string _padStatus = "Gamepad status unknown until the host starts.";
    [ObservableProperty] private string _shareText = string.Empty;
    [ObservableProperty] private string _mapButtonText = "Map ports with UPnP";
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _hasClients;

    public ObservableCollection<string> LogLines { get; } = [];
    public ObservableCollection<ClientRow> Clients { get; } = [];

    public IReadOnlyList<string> MonitorChoices { get; private set; } = [];
    public IReadOnlyList<string> ResolutionChoices { get; } = Presets.Select(p => p.Label).ToArray();
    public IReadOnlyList<string> FpsChoicesLabels { get; } = FpsChoices.Select(f => $"{f} fps").ToArray();
    public IReadOnlyList<string> CaptureApiChoices { get; } = CaptureChoices.Select(c => c.Label).ToArray();

    public bool StartEnabled => !IsRunning && !IsBusy;
    public bool StopEnabled => IsRunning && !IsBusy;
    public bool MapEnabled => IsRunning && !IsBusy;

    public string RequiredPorts => int.TryParse(PortText, out var port)
        ? $"TCP {port}, TCP {port + 1} and UDP {port + 2}"
        : "TCP (control), TCP (video) and UDP (audio)";

    public string? SelectedMonitorName =>
        SelectedMonitorIndex >= 0 && SelectedMonitorIndex < _monitors.Length
            ? _monitors[SelectedMonitorIndex].Display
            : "No monitor detected";

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(StartEnabled));
        OnPropertyChanged(nameof(StopEnabled));
        OnPropertyChanged(nameof(MapEnabled));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(StartEnabled));
        OnPropertyChanged(nameof(StopEnabled));
        OnPropertyChanged(nameof(MapEnabled));
    }

    partial void OnQualityChanged(double value)
    {
        QualityLabel = $"JPEG quality {value:F0}";
        ApplyLiveOptions();
    }

    partial void OnSelectedMonitorIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedMonitorName));
        ApplyLiveOptions();
    }

    partial void OnResolutionIndexChanged(int value) => ApplyLiveOptions();

    partial void OnFpsIndexChanged(int value) => ApplyLiveOptions();

    partial void OnAllowInputChanged(bool value) => ApplyLiveOptions();

    partial void OnEnableVirtualGamepadChanged(bool value) => ApplyLiveOptions();

    partial void OnIncludeCursorChanged(bool value) => ApplyLiveOptions();

    partial void OnCaptureApiIndexChanged(int value) => ApplyLiveOptions();

    partial void OnEnableAudioChanged(bool value) => ApplyLiveOptions();

    partial void OnAudioBitrateChanged(double value)
    {
        AudioBitrateLabel = $"Opus bitrate {value:F0} kbps";
        ApplyLiveOptions();
    }

    partial void OnAudioExcludedProcessesChanged(string value) => ApplyLiveOptions();

    partial void OnPortTextChanged(string value)
    {
        OnPropertyChanged(nameof(RequiredPorts));
        UpdateShareAddress();
    }

    [RelayCommand]
    private void RegeneratePin()
    {
        Pin = GeneratePin();
        SaveSettings();
    }

    [RelayCommand]
    private void RefreshMonitors()
    {
        var index = SelectedMonitorIndex;
        RefreshMonitorList();
        SelectedMonitorIndex = ClampMonitor(index);
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning || IsBusy)
            return;

        if (!TryBuildOptions(out var options, out var error))
        {
            SetError(error);
            return;
        }

        IsBusy = true;
        HasError = false;
        StatusText = "Starting host...";

        try
        {
            await _server.StartAsync(options);
            IsRunning = true;
            StatusText = "Host is live. Waiting for a remote player to connect.";
            SaveSettings();
            UpdateShareAddress();
            Tick();

            if (MapPortsOnStart)
                await MapPortsCoreAsync();
        }
        catch (Exception ex)
        {
            SetError("Could not start: " + ex.Message);
            IsRunning = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (!IsRunning || IsBusy)
            return;

        IsBusy = true;
        StatusText = "Stopping host...";

        try
        {
            await _server.StopAsync();
            IsRunning = false;
            Clients.Clear();
            HasClients = false;
            StatsText = "No stream yet.";
            PadStatus = "Gamepad status unknown until the host starts.";
            _mapping = null;
            MapButtonText = "Map ports with UPnP";
            StatusText = "Stopped.";
            UpdateShareAddress();
        }
        catch (Exception ex)
        {
            SetError("Stop failed: " + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task MapPortsAsync()
    {
        if (!IsRunning || IsBusy)
            return;

        IsBusy = true;
        StatusText = "Asking the router for a UPnP port mapping...";

        try
        {
            await MapPortsCoreAsync();
        }
        catch (Exception ex)
        {
            SetError("UPnP failed: " + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task MapPortsCoreAsync()
    {
        var result = await _server.MapPortsAsync();
        _mapping = result.Success ? result : null;
        MapButtonText = result.Success ? "Refresh UPnP mapping" : "Retry UPnP mapping";
        StatusText = result.Message;
        HasError = !result.Success;
        UpdateShareAddress();
    }

    private void ApplyLiveOptions()
    {
        if (!IsRunning || !TryBuildOptions(out var options, out _))
            return;

        try
        {
            _server.UpdateOptions(options);
        }
        catch (Exception)
        {
            // Live tuning is best-effort; a restart applies everything.
        }

        SaveSettings();
    }

    private bool TryBuildOptions(out HostOptions options, out string error)
    {
        options = _settings.ToOptions();
        error = string.Empty;

        if (!int.TryParse(PortText, out var port) || port is < 1 or > 65533)
        {
            error = "Enter a control port between 1 and 65533 (video uses port + 1, audio UDP uses port + 2).";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Pin) || Pin.Trim().Length < 3)
        {
            error = "Set a PIN with at least 3 characters so random clients cannot connect.";
            return false;
        }

        var preset = Presets[Math.Clamp(ResolutionIndex, 0, Presets.Length - 1)];
        var monitor = SelectedMonitorIndex >= 0 && SelectedMonitorIndex < _monitors.Length
            ? _monitors[SelectedMonitorIndex]
            : _monitors.FirstOrDefault();

        var width = preset.Native ? monitor?.Width ?? 1920 : preset.Width;
        var height = preset.Native ? monitor?.Height ?? 1080 : preset.Height;

        options.ServerName = string.IsNullOrWhiteSpace(ServerName) ? Environment.MachineName : ServerName.Trim();
        options.Port = port;
        options.Pin = Pin.Trim();
        options.MonitorIndex = monitor?.Index ?? 0;
        options.MaxWidth = width;
        options.MaxHeight = height;
        options.Fps = FpsChoices[Math.Clamp(FpsIndex, 0, FpsChoices.Length - 1)];
        options.Quality = (int)Math.Round(Quality);
        options.AllowInput = AllowInput;
        options.EnableVirtualGamepad = EnableVirtualGamepad;
        options.IncludeCursor = IncludeCursor;
        options.CaptureApi = CaptureChoices[Math.Clamp(CaptureApiIndex, 0, CaptureChoices.Length - 1)].Api;
        options.EnableAudio = EnableAudio;
        options.AudioBitrateKbps = (int)Math.Round(Math.Clamp(AudioBitrate, 32, 128));
        options.AudioExcludedProcesses = AudioExcludedProcesses;
        return true;
    }

    private void Tick()
    {
        var snapshot = _server.GetSnapshot();

        // WGC and Desktop Duplication are change-driven: a quiet desktop legitimately delivers no
        // new frames. Say so instead of showing a bare 0 fps that looks like a capture failure.
        var fpsText = snapshot.Clients > 0 && snapshot.CaptureFps < 0.5
            ? "0 fps (screen unchanged)"
            : $"{snapshot.CaptureFps:F0} fps";

        var audioText = snapshot.AudioActive ? $" · audio {snapshot.AudioKbps:F0} kbps" : string.Empty;
        StatsText = snapshot.Running
            ? $"{fpsText} · {snapshot.EncodeMs:F1} ms encode · {snapshot.Kbps:F0} kbps{audioText} · " +
              $"{snapshot.Width}×{snapshot.Height} · {snapshot.Clients} client(s)"
            : "No stream yet.";

        AudioStatus = snapshot.Running
            ? snapshot.AudioStatus ?? (EnableAudio ? "Audio starting..." : "Audio disabled.")
            : EnableAudio
                ? "Audio idle (host stopped)."
                : "Audio disabled.";

        if (!string.IsNullOrWhiteSpace(snapshot.VirtualPadStatus))
            PadStatus = snapshot.VirtualPadStatus!;

        if (IsRunning)
            RefreshClients();

        if (snapshot.LastError is not null)
            SetError(snapshot.LastError);
    }

    private void RefreshClients()
    {
        var snapshots = _server.GetClients();
        Clients.Clear();
        foreach (var client in snapshots)
        {
            var audio = client.AudioConnected ? (client.AudioMuted ? "muted" : "on") : "off";
            var summary =
                $"video {(client.VideoConnected ? "on" : "off")} · audio {audio} · pad {(client.GamepadConnected ? "on" : "off")}";
            Clients.Add(new ClientRow(client.Name, client.RemoteAddress, summary));
        }

        HasClients = Clients.Count > 0;
    }

    private void RefreshMonitorList()
    {
        try
        {
            _monitors = ScreenCaptureFactory.EnumerateMonitors().ToArray();
        }
        catch (Exception)
        {
            _monitors = [];
        }

        MonitorChoices = _monitors.Length == 0
            ? ["No monitors detected"]
            : _monitors.Select(m => m.Display).ToArray();
        OnPropertyChanged(nameof(MonitorChoices));
        OnPropertyChanged(nameof(SelectedMonitorName));
    }

    private int ClampMonitor(int index) =>
        _monitors.Length == 0 ? 0 : Math.Clamp(index, 0, _monitors.Length - 1);

    private static int FindResolutionIndex(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return Array.FindIndex(Presets, p => p.Native);

        var index = Array.FindIndex(Presets, p => !p.Native && p.Width == width && p.Height == height);
        return index >= 0 ? index : 0;
    }

    private void UpdateShareAddress()
    {
        if (!IsRunning)
        {
            ShareText = "Start the host to get a share address.";
            return;
        }

        if (_mapping is { Success: true, ExternalIp: not null })
        {
            ShareText = $"{_mapping.ExternalIp}:{_mapping.ExternalControlPort}  (internet, via UPnP)";
            return;
        }

        var local = NetworkUtil.GetPrimaryIPv4() ?? NetworkUtil.GetLocalIPv4().FirstOrDefault();
        var port = int.TryParse(PortText, out var parsed) ? parsed : 0;
        ShareText = local is null
            ? "No LAN IPv4 address found."
            : $"{local}:{port}  (same network — map ports for internet play)";
    }

    private void SetError(string message)
    {
        HasError = true;
        StatusText = message;
        AppendLog("ERROR: " + message);
    }

    private static readonly object LogFileGate = new();

    private void AppendLog(string message)
    {
        LogLines.Add(message);
        while (LogLines.Count > 400)
            LogLines.RemoveAt(0);

        try
        {
            lock (LogFileGate)
            {
                var path = AppPaths.LogFile;
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 4 * 1024 * 1024)
                    File.Move(path, path + ".old", overwrite: true);

                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Logging must never take the host down.
        }
    }

    public void SaveSettings()
    {
        _settings.ServerName = ServerName;
        _settings.Pin = Pin;
        _settings.MonitorIndex = SelectedMonitorIndex >= 0 && SelectedMonitorIndex < _monitors.Length
            ? _monitors[SelectedMonitorIndex].Index
            : 0;
        if (int.TryParse(PortText, out var port))
            _settings.Port = port;

        var preset = Presets[Math.Clamp(ResolutionIndex, 0, Presets.Length - 1)];
        _settings.MaxWidth = preset.Native ? 0 : preset.Width;
        _settings.MaxHeight = preset.Native ? 0 : preset.Height;
        _settings.Fps = FpsChoices[Math.Clamp(FpsIndex, 0, FpsChoices.Length - 1)];
        _settings.Quality = (int)Math.Round(Quality);
        _settings.AllowInput = AllowInput;
        _settings.EnableVirtualGamepad = EnableVirtualGamepad;
        _settings.IncludeCursor = IncludeCursor;
        _settings.CaptureApi = CaptureChoices[Math.Clamp(CaptureApiIndex, 0, CaptureChoices.Length - 1)].Api;
        _settings.EnableAudio = EnableAudio;
        _settings.AudioBitrateKbps = (int)Math.Round(Math.Clamp(AudioBitrate, 32, 128));
        _settings.AudioExcludedProcesses = AudioExcludedProcesses;
        _settings.MapPortsOnStart = MapPortsOnStart;

        JsonStore.Save(AppPaths.HostSettingsFile, _settings);
    }

    public async ValueTask ShutdownAsync()
    {
        _timer.Stop();
        SaveSettings();
        await _server.DisposeAsync();
    }

    private static string GeneratePin()
    {
        var value = System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 1_000_000);
        return value.ToString("D6");
    }
}
