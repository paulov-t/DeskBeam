using System.Buffers.Binary;
using System.Text;

namespace DeskBeam.Core.Audio;

public enum AudioPacketKind : byte
{
    /// <summary>Client -> host: claims/refreshes the UDP endpoint for a session token.</summary>
    Bind = 1,

    /// <summary>Host -> client: one Opus frame.</summary>
    Audio = 2,
}

/// <summary>
/// Header of a UDP audio datagram:
/// <code>
/// 0..3   magic "DBAU"
/// 4      packet version
/// 5      kind (Bind / Audio)
/// 6..7   reserved
/// 8..11  session id (host-generated AudioId)
/// 12..15 sequence
/// 16..19 sender timestamp in milliseconds
/// 20..   payload (UTF-8 session token for Bind, Opus packet for Audio)
/// </code>
/// The payload length is the datagram length minus <see cref="AudioConstants.HeaderBytes"/>.
/// </summary>
public readonly record struct AudioPacketHeader(
    AudioPacketKind Kind,
    uint SessionId,
    uint Sequence,
    uint TimestampMs,
    int PayloadLength)
{
    public ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> datagram) =>
        datagram.Slice(AudioConstants.HeaderBytes, PayloadLength);
}

public static class AudioPacket
{
    private static ReadOnlySpan<byte> Magic => "DBAU"u8;

    public static byte[] CreateBind(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("A session token is required.", nameof(token));

        var tokenBytes = Encoding.UTF8.GetBytes(token);
        if (tokenBytes.Length > 1024)
            throw new ArgumentException("Session token is too long.", nameof(token));

        var datagram = new byte[AudioConstants.HeaderBytes + tokenBytes.Length];
        WriteHeader(datagram, AudioPacketKind.Bind, 0, 0, 0);
        tokenBytes.CopyTo(datagram.AsSpan(AudioConstants.HeaderBytes));
        return datagram;
    }

    /// <summary>Writes an audio packet into <paramref name="destination"/> and returns its length.</summary>
    public static int WriteAudio(Span<byte> destination, uint sessionId, uint sequence, uint timestampMs,
        ReadOnlySpan<byte> opus)
    {
        if (opus.Length is 0 or > AudioConstants.MaxOpusPacketBytes)
            throw new ArgumentOutOfRangeException(nameof(opus), "Invalid Opus packet length.");

        var length = AudioConstants.HeaderBytes + opus.Length;
        if (destination.Length < length)
            throw new ArgumentException("Destination is too small for the audio packet.", nameof(destination));

        WriteHeader(destination, AudioPacketKind.Audio, sessionId, sequence, timestampMs);
        opus.CopyTo(destination[AudioConstants.HeaderBytes..]);
        return length;
    }

    public static void WriteHeader(Span<byte> destination, AudioPacketKind kind, uint sessionId, uint sequence,
        uint timestampMs)
    {
        if (destination.Length < AudioConstants.HeaderBytes)
            throw new ArgumentException("Destination is too small for the audio header.", nameof(destination));

        Magic.CopyTo(destination);
        destination[4] = AudioConstants.PacketVersion;
        destination[5] = (byte)kind;
        destination[6] = 0;
        destination[7] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..12], sessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..16], sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..20], timestampMs);
    }

    public static bool TryParse(ReadOnlySpan<byte> datagram, out AudioPacketHeader header)
    {
        header = default;
        if (datagram.Length is <= AudioConstants.HeaderBytes or > AudioConstants.MaxDatagramBytes)
            return false;

        if (!datagram[..4].SequenceEqual(Magic))
            return false;

        if (datagram[4] != AudioConstants.PacketVersion)
            return false;

        var kind = (AudioPacketKind)datagram[5];
        if (kind is not (AudioPacketKind.Bind or AudioPacketKind.Audio))
            return false;

        header = new AudioPacketHeader(
            kind,
            BinaryPrimitives.ReadUInt32LittleEndian(datagram[8..12]),
            BinaryPrimitives.ReadUInt32LittleEndian(datagram[12..16]),
            BinaryPrimitives.ReadUInt32LittleEndian(datagram[16..20]),
            datagram.Length - AudioConstants.HeaderBytes);
        return true;
    }

    public static bool TryReadBindToken(ReadOnlySpan<byte> datagram, out string token)
    {
        token = string.Empty;
        if (!TryParse(datagram, out var header) || header.Kind != AudioPacketKind.Bind)
            return false;

        if (header.PayloadLength is <= 0 or > 1024)
            return false;

        try
        {
            token = Encoding.UTF8.GetString(datagram.Slice(AudioConstants.HeaderBytes, header.PayloadLength));
            return !string.IsNullOrWhiteSpace(token);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
