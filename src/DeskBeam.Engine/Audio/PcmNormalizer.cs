using System.Buffers.Binary;
using DeskBeam.Core.Audio;
using NAudio.Wave;

namespace DeskBeam.Engine.Audio;

/// <summary>
/// Converts arbitrary WASAPI capture output into the fixed wire format: 48 kHz, stereo, 16-bit
/// PCM, split into 10 ms frames. The capture format is requested up front, but devices are free
/// to return float or mono, so the conversion is still defensive.
/// </summary>
public sealed class PcmNormalizer
{
    private readonly WaveFormat _input;
    private readonly Action<byte[]> _onFrame;
    private readonly byte[] _frame = new byte[AudioConstants.PcmBytesPerFrame];
    private readonly int _inputChannels;
    private readonly int _inputBlockAlign;
    private int _writeOffset;

    public PcmNormalizer(WaveFormat input, Action<byte[]> onFrame)
    {
        if (input is WaveFormatExtensible extensible)
            input = extensible.ToStandardWaveFormat();

        _input = input;
        _onFrame = onFrame;
        _inputChannels = Math.Max(1, input.Channels);
        _inputBlockAlign = Math.Max(1, input.BlockAlign);

        if (input.SampleRate != AudioConstants.SampleRate)
        {
            throw new NotSupportedException(
                $"The audio device produced {input.SampleRate} Hz; DeskBeam needs 48000 Hz. " +
                "Disable audio or change the Windows playback device format to 48 kHz.");
        }

        if (input.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat))
        {
            throw new NotSupportedException(
                $"The audio device uses unsupported {input.Encoding} samples. DeskBeam needs PCM or IEEE float.");
        }

        if (input.Encoding == WaveFormatEncoding.Pcm && input.BitsPerSample is not (8 or 16 or 24 or 32))
        {
            throw new NotSupportedException(
                $"The audio device uses unsupported {input.BitsPerSample}-bit PCM.");
        }

        if (input.Encoding == WaveFormatEncoding.IeeeFloat && input.BitsPerSample != 32)
        {
            throw new NotSupportedException(
                $"The audio device uses unsupported {input.BitsPerSample}-bit float samples.");
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        var frames = data.Length / _inputBlockAlign;
        for (var i = 0; i < frames; i++)
        {
            var frame = data.Slice(i * _inputBlockAlign, _inputBlockAlign);
            var left = ReadSample(frame, 0);
            var right = _inputChannels > 1 ? ReadSample(frame, 1) : left;

            BinaryPrimitives.WriteInt16LittleEndian(_frame.AsSpan(_writeOffset), left);
            BinaryPrimitives.WriteInt16LittleEndian(_frame.AsSpan(_writeOffset + 2), right);
            _writeOffset += 4;

            if (_writeOffset < AudioConstants.PcmBytesPerFrame)
                continue;

            _onFrame(_frame.ToArray());
            _writeOffset = 0;
        }
    }

    private short ReadSample(ReadOnlySpan<byte> frame, int channel)
    {
        if (_input.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            var bits = BinaryPrimitives.ReadInt32LittleEndian(frame[(channel * 4)..]);
            var value = BitConverter.Int32BitsToSingle(bits);
            if (float.IsNaN(value))
                return 0;
            return (short)Math.Clamp(Math.Round(value * short.MaxValue), short.MinValue, short.MaxValue);
        }

        var offset = channel * (_input.BitsPerSample / 8);
        return _input.BitsPerSample switch
        {
            8 => (short)((frame[offset] - 128) << 8),
            16 => BinaryPrimitives.ReadInt16LittleEndian(frame[offset..]),
            24 => (short)(ReadInt24(frame[offset..]) >> 8),
            32 => (short)(BinaryPrimitives.ReadInt32LittleEndian(frame[offset..]) >> 16),
            _ => 0,
        };
    }

    private static int ReadInt24(ReadOnlySpan<byte> data)
    {
        var value = data[0] | (data[1] << 8) | (data[2] << 16);
        return (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;
    }
}
