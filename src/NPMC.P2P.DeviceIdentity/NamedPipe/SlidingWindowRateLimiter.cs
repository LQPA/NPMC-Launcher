namespace NPMC.P2P.DeviceIdentity.NamedPipe;

/// <summary>
/// 滑动窗口限流：跨连接累计，避免“断线重连绕过每分钟上限”。
/// </summary>
public sealed class SlidingWindowRateLimiter
{
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly Queue<long> _events = new();
    private readonly object _gate = new();

    public SlidingWindowRateLimiter(int limit, TimeSpan window)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }

        _limit = limit;
        _window = window;
    }

    public bool TryAcquire(DateTimeOffset now)
    {
        var nowTicks = now.UtcTicks;
        var threshold = nowTicks - _window.Ticks;

        lock (_gate)
        {
            while (_events.Count > 0 && _events.Peek() <= threshold)
            {
                _events.Dequeue();
            }

            if (_events.Count >= _limit)
            {
                return false;
            }

            _events.Enqueue(nowTicks);
            return true;
        }
    }
}
