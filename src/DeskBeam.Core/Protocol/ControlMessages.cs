using DeskBeam.Core.Audio;

namespace DeskBeam.Core.Protocol;

/// <summary>Base type for every JSON control message exchanged on the control socket.</summary>
public abstract record ControlMessage;

public sealed record ServerHello(
    int ProtocolVersion,
    bool RequiresPassword,
    string ServerName,
    string Nonce) : ControlMessage;

public sealed record AuthRequest(
    string ClientName,
    string Proof) : ControlMessage;

public sealed record AuthResult(
    bool Ok,
    string? Error,
    string? Warning) : ControlMessage;

public sealed record StreamInfo(
    int VideoPort,
    string SessionToken,
    int Width,
    int Height,
    int Fps,
    int Quality,
    string MonitorName,
    int AudioPort = 0,
    bool AudioAvailable = false,
    int AudioSampleRate = AudioConstants.SampleRate,
    int AudioChannels = AudioConstants.Channels,
    int AudioFrameMs = AudioConstants.FrameMilliseconds,
    int AudioBitrateKbps = AudioConstants.DefaultBitrateKbps,
    string AudioId = "") : ControlMessage;

public enum PointerAction
{
    Move,
    Down,
    Up,
    Wheel,
}

/// <summary>Mouse input in normalized coordinates (0..1) of the captured monitor.</summary>
public sealed record PointerInput(
    PointerAction Action,
    double X,
    double Y,
    int Button,
    int Wheel) : ControlMessage;

public sealed record KeyInput(
    int VirtualKey,
    bool Pressed) : ControlMessage;

public sealed record GamepadState(
    uint PacketNumber,
    ushort Buttons,
    byte LeftTrigger,
    byte RightTrigger,
    short LeftThumbX,
    short LeftThumbY,
    short RightThumbX,
    short RightThumbY,
    bool Connected) : ControlMessage;

public sealed record RumbleState(
    byte LeftMotor,
    byte RightMotor) : ControlMessage;

public sealed record RequestStream(
    int Width,
    int Height,
    int Fps,
    int Quality) : ControlMessage;

/// <summary>
/// Sent when the viewer cannot apply a delta (missing frame or first connect) and needs a full
/// keyframe. The host answers with a cached keyframe instead of waiting for screen motion.
/// </summary>
public sealed record KeyframeRequest : ControlMessage;

public sealed record Ping(
    long ClientTicks) : ControlMessage;

public sealed record Pong(
    long ClientTicks,
    long HostTicks) : ControlMessage;

/// <summary>Viewer's view of the UDP audio channel, sent about once per second.</summary>
public sealed record AudioReport(
    double ReceivedFps,
    double LossPercent,
    double JitterMs,
    bool Muted) : ControlMessage;

public sealed record HostStats(
    double CaptureFps,
    double EncodeMs,
    double Kbps,
    int Clients,
    int Width,
    int Height,
    string? Note,
    double AudioKbps = 0,
    bool AudioActive = false,
    string? AudioStatus = null) : ControlMessage;

public sealed record Disconnect(
    string Reason) : ControlMessage;
