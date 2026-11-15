namespace DeskBeam.Core.Protocol;

public static class ProtocolConstants
{
    public const int Version = 3;

    /// <summary>Default TCP port for the control channel.</summary>
    public const int DefaultControlPort = 47777;

    /// <summary>The video channel always listens on the control port plus this offset.</summary>
    public const int VideoPortOffset = 1;

    /// <summary>The audio channel is UDP and always listens on the control port plus this offset.</summary>
    public const int AudioPortOffset = 2;

    public const int MaxControlMessageBytes = 1 * 1024 * 1024;
    public const int MaxVideoFrameBytes = 32 * 1024 * 1024;

    /// <summary>Token length (hex characters) used to authorize the video socket.</summary>
    public const int SessionTokenLength = 32;
}
