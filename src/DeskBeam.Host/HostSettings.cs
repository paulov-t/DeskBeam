using DeskBeam.Core.Models;
using DeskBeam.Core.Protocol;

namespace DeskBeam.Host;

/// <summary>Persisted host preferences. Converted to <see cref="HostOptions"/> when streaming starts.</summary>
public sealed class HostSettings
{
    public string ServerName { get; set; } = Environment.MachineName;
    public int Port { get; set; } = ProtocolConstants.DefaultControlPort;
    public string Pin { get; set; } = string.Empty;
    public int MonitorIndex { get; set; }
    public int MaxWidth { get; set; } = 1280;
    public int MaxHeight { get; set; } = 720;
    public int Fps { get; set; } = 60;
    public int Quality { get; set; } = 55;
    public bool AllowInput { get; set; } = true;
    public bool EnableVirtualGamepad { get; set; } = true;
    public bool IncludeCursor { get; set; } = true;
    public CaptureApi CaptureApi { get; set; } = CaptureApi.Auto;
    public bool EnableAudio { get; set; } = true;
    public int AudioBitrateKbps { get; set; } = Core.Audio.AudioConstants.DefaultBitrateKbps;
    public string AudioExcludedProcesses { get; set; } =
        "Discord, DiscordPTB, DiscordCanary, DiscordDevelopment";
    public bool MapPortsOnStart { get; set; }

    public HostOptions ToOptions() => new()
    {
        ServerName = string.IsNullOrWhiteSpace(ServerName) ? Environment.MachineName : ServerName.Trim(),
        Port = Port,
        Pin = Pin,
        MonitorIndex = MonitorIndex,
        MaxWidth = MaxWidth,
        MaxHeight = MaxHeight,
        Fps = Fps,
        Quality = Quality,
        AllowInput = AllowInput,
        EnableVirtualGamepad = EnableVirtualGamepad,
        IncludeCursor = IncludeCursor,
        CaptureApi = CaptureApi,
        EnableAudio = EnableAudio,
        AudioBitrateKbps = AudioBitrateKbps,
        AudioExcludedProcesses = AudioExcludedProcesses,
    };
}
