using System.Collections.Concurrent;
using DeskBeam.Engine.Capture;

namespace DeskBeam.Engine.Streaming;

/// <summary>
/// Small bounded queue between the capture thread and the encoder. When encoding falls behind,
/// the oldest frame is dropped so the stream always stays close to real time.
/// </summary>
internal sealed class RawFrameMailbox : IDisposable
{
    private readonly ConcurrentQueue<RawFrame> _queue = new();
    private readonly SemaphoreSlim _signal = new(0, 8);
    private bool _disposed;

    public void Publish(RawFrame frame)
    {
        if (_disposed)
        {
            frame.Dispose();
            return;
        }

        _queue.Enqueue(frame);

        while (_queue.Count > 2 && _queue.TryDequeue(out var stale))
            stale.Dispose();

        try
        {
            if (_signal.CurrentCount < 8)
                _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // ignored
        }
        catch (ObjectDisposedException)
        {
            frame.Dispose();
        }
    }

    public async ValueTask<RawFrame?> TakeAsync(CancellationToken ct)
    {
        while (!_disposed)
        {
            await _signal.WaitAsync(ct).ConfigureAwait(false);
            if (_queue.TryDequeue(out var frame))
                return frame;
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        while (_queue.TryDequeue(out var frame))
            frame.Dispose();

        _signal.Dispose();
    }
}
