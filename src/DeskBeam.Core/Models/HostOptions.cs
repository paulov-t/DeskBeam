using DeskBeam.Core.Audio;

namespace DeskBeam.Core.Models;

/// <summary>Everything the host needs to start streaming.</summary>
public sealed class HostOptions
{
    public int Port { get; set; } = Protocol.ProtocolConstants.DefaultControlPort;
    public string Pin { get; set; } = string.Empty;
    public string ServerName { get; set; } = Environment.MachineName;
    public int MonitorIndex { get; set; }
    public int MaxWidth { get; set; } = 1280;
    public int MaxHeight { get; set; } = 720;
    public int Fps { get; set; } = 60;
    public int Quality { get; set; } = 55;
    public bool AllowInput { get; set; } = true;
    public bool EnableVirtualGamepad { get; set; } = true;
    public bool IncludeCursor { get; set; } = true;
    public CaptureApi CaptureApi { get; set; } = CaptureApi.Auto;

    /// <summary>Stream desktop audio over the separate UDP channel.</summary>
    public bool EnableAudio { get; set; } = true;

    public int AudioBitrateKbps { get; set; } = AudioConstants.DefaultBitrateKbps;

    /// <summary>
    /// Comma/semicolon separated process names whose audio is never sent. Discord is excluded by
    /// default; the local Discord client keeps playing normally.
    /// </summary>
    public string AudioExcludedProcesses { get; set; } = "Discord, DiscordPTB, DiscordCanary, DiscordDevelopment";

    public HostOptions Clone() => (HostOptions)MemberwiseClone();

    public int VideoPort => Port + Protocol.ProtocolConstants.VideoPortOffset;

    public int AudioPort => Port + Protocol.ProtocolConstants.AudioPortOffset;
}

public sealed record MonitorDescriptor(
    int Index,
    string DeviceName,
    string FriendlyName,
    int X,
    int Y,
    int Width,
    int Height)
{
    public string Display => string.IsNullOrWhiteSpace(FriendlyName)
        ? $"{Index + 1}: {Width}x{Height} ({DeviceName})"
        : $"{Index + 1}: {FriendlyName} ({Width}x{Height})";
}

public sealed record HostSnapshot(
    bool Running,
    int Clients,
    double CaptureFps,
    double EncodeMs,
    double Kbps,
    int Width,
    int Height,
    string? VirtualPadStatus,
    string? LastError,
    double AudioKbps = 0,
    bool AudioActive = false,
    string? AudioStatus = null);

public sealed record ClientSnapshot(
    string Id,
    string Name,
    string RemoteAddress,
    long PingMs,
    bool VideoConnected,
    bool GamepadConnected,
    bool HasVideo,
    bool AudioConnected = false,
    bool AudioMuted = false);

public sealed record UpnpMappingResult(
    bool Success,
    string Message,
    string? ExternalIp,
    int ExternalControlPort,
    int ExternalVideoPort,
    string? GatewayName,
    int ExternalAudioPort = 0);
