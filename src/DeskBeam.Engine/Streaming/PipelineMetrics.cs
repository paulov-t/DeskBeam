using System.Diagnostics;

namespace DeskBeam.Engine.Streaming;

/// <summary>Rolling one-second view of the streaming pipeline.</summary>
public sealed class PipelineMetrics
{
    private readonly object _gate = new();
    private long _frames;
    private long _bytes;
    private double _encodeMsTotal;
    private int _encodeSamples;
    private long _windowStart = Stopwatch.GetTimestamp();

    public double Fps { get; private set; }
    public double AverageEncodeMs { get; private set; }
    public double Kbps { get; private set; }

    public void AddFrame(int bytes, double encodeMs)
    {
        lock (_gate)
        {
            _frames++;
            _bytes += bytes;
            _encodeMsTotal += encodeMs;
            _encodeSamples++;
        }
    }

    public void Sample()
    {
        var now = Stopwatch.GetTimestamp();
        double seconds;
        long frames;
        long bytes;
        double encodeTotal;
        int samples;

        lock (_gate)
        {
            seconds = (now - _windowStart) / (double)Stopwatch.Frequency;
            if (seconds < 0.75)
                return;

            frames = _frames;
            bytes = _bytes;
            encodeTotal = _encodeMsTotal;
            samples = _encodeSamples;

            _frames = 0;
            _bytes = 0;
            _encodeMsTotal = 0;
            _encodeSamples = 0;
            _windowStart = now;
        }

        Fps = frames / seconds;
        Kbps = bytes * 8.0 / 1000.0 / seconds;
        AverageEncodeMs = samples > 0 ? encodeTotal / samples : 0;
    }

    public void Reset()
    {
        lock (_gate)
        {
            _frames = 0;
            _bytes = 0;
            _encodeMsTotal = 0;
            _encodeSamples = 0;
            _windowStart = Stopwatch.GetTimestamp();
        }

        Fps = 0;
        Kbps = 0;
        AverageEncodeMs = 0;
    }
}
