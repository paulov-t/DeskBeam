namespace DeskBeam.Core.Audio;

/// <summary>
/// Fixed audio format used on the wire. Desktop audio is captured, converted to 48 kHz stereo
/// 16-bit PCM, split into 10 ms Opus frames and sent over its own UDP channel.
/// </summary>
public static class AudioConstants
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int FrameMilliseconds = 10;
    public const int SamplesPerFramePerChannel = SampleRate * FrameMilliseconds / 1000;
    public const int PcmBytesPerFrame = SamplesPerFramePerChannel * Channels * sizeof(short);

    /// <summary>One-byte packet format version; independent of the control protocol version.</summary>
    public const byte PacketVersion = 1;

    /// <summary>Magic bytes at the start of every Datagram ("DBAU").</summary>
    public const uint Magic = 0x55414244;

    public const int HeaderBytes = 20;

    /// <summary>Upper bound for a single Opus packet. 10 ms stereo frames are far below this.</summary>
    public const int MaxOpusPacketBytes = 1275;

    /// <summary>Maximum datagram we are willing to parse (header + packet).</summary>
    public const int MaxDatagramBytes = HeaderBytes + MaxOpusPacketBytes;

    public const int DefaultBitrateKbps = 64;
}
