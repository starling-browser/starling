namespace Starling.Loop;

/// <summary>
/// Deterministic event loop core for tests/headless hosts. Microtasks drain
/// before timers; timers run once their due time is reached;
/// requestAnimationFrame callbacks fire once per <see cref="RunFrame(long)"/>.
/// </summary>
public sealed class WebEventLoop
{
    private readonly PriorityQueue<TimerTask, TimerKey> _timers = new();
    private readonly Queue<Action> _microtasks = new();
    // rAF queue (CSS Animations 1 §3.5 / HTML §"run the animation frame callbacks"):
    // snapshotted at the top of RunFrame so callbacks scheduled *during* the
    // drain land in the queue for the *next* frame, not the current one.
    private readonly List<RafCallback> _animationCallbacks = new();
    private long _nowMs;
    private int _nextTimerId = 1;
    private int _nextRafId = 1;
    private long _sequence;

    public long NowMilliseconds => _nowMs;
    public int PendingTimerCount => _timers.Count;
    public int PendingMicrotaskCount => _microtasks.Count;
    public int PendingAnimationFrameCount => _animationCallbacks.Count;

    public void QueueMicrotask(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _microtasks.Enqueue(callback);
    }

    public int SetTimeout(Action callback, int delayMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (delayMilliseconds < 0)
        {
            delayMilliseconds = 0;
        }

        var task = new TimerTask(_nextTimerId++, callback, Cancelled: false);
        _timers.Enqueue(task, new TimerKey(_nowMs + delayMilliseconds, _sequence++));
        return task.Id;
    }

    public bool ClearTimeout(int id)
    {
        var removed = false;
        var kept = new List<(TimerTask Task, TimerKey Key)>(_timers.Count);
        while (_timers.TryDequeue(out var task, out var key))
        {
            if (task.Id == id)
            {
                removed = true;
                continue;
            }
            kept.Add((task, key));
        }

        foreach (var (task, key) in kept)
        {
            _timers.Enqueue(task, key);
        }

        return removed;
    }

    /// <summary>Schedule <paramref name="callback"/> to fire on the next
    /// <see cref="RunFrame(long)"/>, receiving that frame's timestamp.
    /// Returns a handle usable with <see cref="CancelAnimationFrame(int)"/>.</summary>
    public int RequestAnimationFrame(Action<double> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var id = _nextRafId++;
        _animationCallbacks.Add(new RafCallback(id, callback, Cancelled: false));
        return id;
    }

    /// <summary>Cancel a pending rAF callback by handle. Returns true if a
    /// pending callback was found and removed (or marked cancelled).</summary>
    public bool CancelAnimationFrame(int handle)
    {
        for (var i = 0; i < _animationCallbacks.Count; i++)
        {
            if (_animationCallbacks[i].Id == handle)
            {
                _animationCallbacks[i] = _animationCallbacks[i] with { Cancelled = true };
                return true;
            }
        }
        return false;
    }

    public void AdvanceBy(int milliseconds)
    {
        if (milliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(milliseconds), "Cannot move event-loop time backwards.");
        }

        RunFrame(_nowMs + milliseconds);
    }

    /// <summary>
    /// Advance the loop's monotonic clock to <paramref name="nowMs"/>, then
    /// run one "task + frame" cycle: drain microtasks, fire due timers (each
    /// followed by a microtask drain), drain the rAF queue (snapshotted, so
    /// nested rAFs land on the *next* frame), and drain microtasks once more.
    /// All rAF callbacks in a single frame observe the same
    /// <paramref name="nowMs"/> timestamp — required by CSS Animations 1 §3.5
    /// for synchronized samples across animations in one frame.
    /// </summary>
    public void RunFrame(long nowMs)
    {
        if (nowMs < _nowMs)
        {
            throw new ArgumentOutOfRangeException(nameof(nowMs), "Cannot move event-loop time backwards.");
        }

        _nowMs = nowMs;
        RunUntilIdle();

        if (_animationCallbacks.Count > 0)
        {
            var snapshot = _animationCallbacks.ToArray();
            _animationCallbacks.Clear();
            var timestamp = (double)nowMs;
            foreach (var cb in snapshot)
            {
                if (cb.Cancelled)
                {
                    continue;
                }

                cb.Callback(timestamp);
                DrainMicrotasks();
            }
        }

        DrainMicrotasks();
    }

    public void RunUntilIdle()
    {
        DrainMicrotasks();
        while (_timers.TryPeek(out _, out var key) && key.DueMilliseconds <= _nowMs)
        {
            var task = _timers.Dequeue();
            task.Callback();
            DrainMicrotasks();
        }
    }

    private void DrainMicrotasks()
    {
        while (_microtasks.TryDequeue(out var callback))
        {
            callback();
        }
    }

    private readonly record struct TimerTask(int Id, Action Callback, bool Cancelled);

    private readonly record struct RafCallback(int Id, Action<double> Callback, bool Cancelled);

    private readonly record struct TimerKey(long DueMilliseconds, long Sequence) : IComparable<TimerKey>
    {
        public int CompareTo(TimerKey other)
        {
            var byDue = DueMilliseconds.CompareTo(other.DueMilliseconds);
            return byDue != 0 ? byDue : Sequence.CompareTo(other.Sequence);
        }
    }
}
