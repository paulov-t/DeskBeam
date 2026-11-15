using DeskBeam.Core.Models;

namespace DeskBeam.Engine.Capture;

/// <summary>A captured frame backed by a pooled buffer. Dispose returns the buffer to the pool.</summary>
public sealed class RawFrame : IDisposable
{
    private readonly Action<byte[]>? _return;

    public RawFrame(byte[] pixels, int width, int height, int stride, int sequence, Action<byte[]>? returnBuffer = null)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
        Sequence = sequence;
        _return = returnBuffer;
    }

    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public int Sequence { get; set; }
    public long TimestampMs { get; set; } = Environment.TickCount64;

    public void Dispose() => _return?.Invoke(Pixels);
}

public interface ICapturer : IDisposable
{
    MonitorDescriptor Monitor { get; }

    /// <summary>The concrete API this capturer is using (not <see cref="CaptureApi.Auto"/>).</summary>
    CaptureApi Backend { get; }

    /// <summary>
    /// Captures one frame. <paramref name="maxWidth"/>/<paramref name="maxHeight"/> let a capturer
    /// that converts on the GPU produce the streaming size directly; capturers that cannot do that
    /// may return a larger frame and let the encoder scale it.
    /// </summary>
    RawFrame? Capture(int timeoutMs, int maxWidth, int maxHeight);
}
