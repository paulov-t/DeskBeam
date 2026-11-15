using System.Runtime.InteropServices;
using Concentus.Enums;
using Concentus.Structs;

namespace DeskBeam.Core.Audio;

/// <summary>Thin wrapper around Concentus so the host and viewer share one codec configuration.</summary>
public sealed class OpusAudioEncoder
{
    private readonly OpusEncoder _encoder;
    private readonly short[] _samples = new short[AudioConstants.SamplesPerFramePerChannel * AudioConstants.Channels];
    private readonly byte[] _packet = new byte[AudioConstants.MaxOpusPacketBytes];
    private int _bitrateKbps;

    public OpusAudioEncoder(int bitrateKbps = AudioConstants.DefaultBitrateKbps)
    {
        _encoder = OpusEncoder.Create(AudioConstants.SampleRate, AudioConstants.Channels,
            OpusApplication.OPUS_APPLICATION_AUDIO);
        _encoder.Complexity = 5;
        _encoder.UseVBR = true;
        _encoder.UseInbandFEC = true;
        _encoder.PacketLossPercent = 10;
        _encoder.ExpertFrameDuration = OpusFramesize.OPUS_FRAMESIZE_10_MS;
        BitrateKbps = bitrateKbps;
    }

    public int BitrateKbps
    {
        get => _bitrateKbps;
        set
        {
            _bitrateKbps = Math.Clamp(value, 8, 256);
            _encoder.Bitrate = _bitrateKbps * 1000;
        }
    }

    /// <summary>Encodes exactly one 10 ms, 48 kHz stereo 16-bit PCM frame.</summary>
    public int Encode(ReadOnlySpan<byte> pcm16, Span<byte> destination)
    {
        if (pcm16.Length != AudioConstants.PcmBytesPerFrame)
            throw new ArgumentException($"Expected {AudioConstants.PcmBytesPerFrame} PCM bytes.", nameof(pcm16));

        if (destination.Length < AudioConstants.MaxOpusPacketBytes)
            throw new ArgumentException("Destination is too small for an Opus packet.", nameof(destination));

        MemoryMarshal.Cast<byte, short>(pcm16).CopyTo(_samples);
        var length = _encoder.Encode(_samples, 0, AudioConstants.SamplesPerFramePerChannel, _packet, 0,
            _packet.Length);
        _packet.AsSpan(0, length).CopyTo(destination);
        return length;
    }

    public void Reset() => _encoder.ResetState();
}

public sealed class OpusAudioDecoder
{
    private readonly OpusDecoder _decoder;
    private readonly short[] _samples = new short[AudioConstants.SamplesPerFramePerChannel * AudioConstants.Channels];

    public OpusAudioDecoder()
    {
        _decoder = OpusDecoder.Create(AudioConstants.SampleRate, AudioConstants.Channels);
    }

    public bool TryDecode(byte[]? packet, int offset, int length, Span<byte> pcm16) =>
        packet is null || length <= 0
            ? Decode(null, 0, 0, false, pcm16)
            : Decode(packet, offset, length, false, pcm16);

    /// <summary>
    /// Recovers the missing frame from the in-band FEC carried by the following packet. Opus falls
    /// back to normal concealment when that packet has no FEC data, so this is always safe to call.
    /// </summary>
    public bool TryDecodeFec(byte[] packet, Span<byte> pcm16) =>
        Decode(packet, 0, packet.Length, true, pcm16);

    private bool Decode(byte[]? packet, int offset, int length, bool decodeFec, Span<byte> pcm16)
    {
        if (pcm16.Length < AudioConstants.PcmBytesPerFrame)
            throw new ArgumentException("Destination is too small for a PCM frame.", nameof(pcm16));

        try
        {
            var samples = _decoder.Decode(packet, offset, length, _samples, 0,
                AudioConstants.SamplesPerFramePerChannel, decodeFec);

            if (samples <= 0)
                return false;

            MemoryMarshal.AsBytes(_samples.AsSpan(0, AudioConstants.SamplesPerFramePerChannel *
                                                          AudioConstants.Channels))
                .CopyTo(pcm16);
            return true;
        }
        catch (Exception)
        {
            // Corrupt packets must not take the audio loop down; the caller inserts PLC instead.
            return false;
        }
    }

    /// <summary>Packet-loss concealment for one missing 10 ms frame.</summary>
    public bool TryDecodeLost(Span<byte> pcm16) => TryDecode(null, 0, 0, pcm16);

    public void Reset() => _decoder.ResetState();
}
