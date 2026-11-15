using DeskBeam.Core.Video;

namespace DeskBeam.Engine.Streaming;

/// <summary>
/// Holds the newest encoded frame and wakes every subscribed video connection. Subscribers only
/// ever send the newest frame, which keeps latency bounded when a client cannot keep up.
/// </summary>
public sealed class FrameBroadcaster
{
    private readonly object _gate = new();
    private readonly List<Subscriber> _subscribers = [];
    private volatile EncodedFrame? _latest;

    public EncodedFrame? Latest => _latest;

    /// <summary>
    /// Drops the last frame and its sequence. Called when a host starts a fresh pipeline in the same
    /// process so the new sequence space (starting at 1) is not rejected as older than the old one.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
            _latest = null;
    }

    public Subscriber Subscribe()
    {
        var subscriber = new Subscriber(this);
        lock (_gate)
            _subscribers.Add(subscriber);
        return subscriber;
    }

    internal void Unsubscribe(Subscriber subscriber)
    {
        lock (_gate)
            _subscribers.Remove(subscriber);
    }

    public void Publish(EncodedFrame frame)
    {
        Subscriber[] subscribers;
        lock (_gate)
        {
            if (_latest is not null && unchecked(frame.Sequence - _latest.Sequence) <= 0)
                return; // A newer frame was already published.

            _latest = frame;
            subscribers = _subscribers.ToArray();
        }

        foreach (var subscriber in subscribers)
            subscriber.Signal();
    }

    public sealed class Subscriber : IDisposable
    {
        private readonly FrameBroadcaster _owner;
        private readonly SemaphoreSlim _signal = new(0, 1);
        private bool _disposed;

        internal Subscriber(FrameBroadcaster owner) => _owner = owner;

        public int LastSequence { get; set; } = int.MinValue;

        internal void Signal()
        {
            if (_disposed)
                return;

            try
            {
                if (_signal.CurrentCount == 0)
                    _signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already signalled.
            }
            catch (ObjectDisposedException)
            {
                // Raced with disposal.
            }
        }

        public async ValueTask<EncodedFrame?> WaitAsync(CancellationToken ct)
        {
            await _signal.WaitAsync(ct).ConfigureAwait(false);
            return _owner.Latest;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _owner.Unsubscribe(this);
            try
            {
                _signal.Release();
            }
            catch (Exception)
            {
                // ignored
            }

            _signal.Dispose();
        }
    }
}
