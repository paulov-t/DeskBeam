namespace DeskBeam.Client;

public sealed class ClientSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = Core.Protocol.ProtocolConstants.DefaultControlPort;
    public string Pin { get; set; } = string.Empty;
    public string ClientName { get; set; } = Environment.MachineName;
    public bool ForwardMouse { get; set; } = true;
    public bool ForwardKeyboard { get; set; } = true;
    public bool ShowStats { get; set; } = true;
    public bool MuteAudio { get; set; }
}
