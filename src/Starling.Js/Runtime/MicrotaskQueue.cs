namespace Starling.Js.Runtime;

/// <summary>
/// Host-agnostic FIFO of Promise reaction jobs (HTML §8.1.5.1 "queue a
/// microtask"). Lives on <see cref="JsRealm"/> so the Promise impl can
/// schedule work without dragging in a hard dependency on
/// <c>Starling.Loop.WebEventLoop</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two modes:
/// </para>
/// <list type="bullet">
///   <item>
///     <description><b>Internal drain (default).</b> Jobs are enqueued into
///     the in-process queue and run when <see cref="DrainAll"/> is invoked.
///     <see cref="JsVm.Run(Starling.Js.Bytecode.Chunk)"/> drains automatically
///     after each top-level script returns, so unit tests that don't wire up
///     a host loop still see promise reactions settle.</description>
///   </item>
///   <item>
///     <description><b>Host-scheduled.</b> A host (e.g. the renderer
///     wired against <c>WebEventLoop</c>) installs a scheduler via
///     <see cref="JsRuntime.SetMicrotaskScheduler"/>. Subsequent calls to
///     <see cref="Enqueue"/> delegate to the host instead of touching the
///     local queue; <see cref="DrainAll"/> becomes a no-op (the host pumps).
///     </description>
///   </item>
/// </list>
/// <para>
/// <see cref="DrainAll"/> follows §9.4.1: jobs may enqueue more jobs while
/// draining, so it keeps pulling until empty rather than snapshotting.
/// Exceptions thrown by a job are reported through
/// <see cref="UncaughtHandler"/> (default: rethrow). The Promise impl wires
/// this to surface unhandledrejection-style diagnostics through the realm's
/// console sink.
/// </para>
/// </remarks>
public sealed class MicrotaskQueue
{
    // B5-3: thread-safe so off-thread completions (fetch / XHR) can enqueue
    // resolve jobs from a thread-pool callback. Enqueue takes the lock; drain
    // takes the lock only around TryDequeue, releasing it while the job runs
    // so jobs can recursively enqueue without deadlocking.
    private readonly object _lock = new();
    private readonly Queue<Action> _queue = new();
    private Action<Action>? _hostScheduler;

    /// <summary>Invoked when a microtask job throws an unhandled exception.
    /// Defaults to rethrow so test failures surface fast; <see cref="JsRuntime"/>
    /// installs a friendlier reporter once a console sink is available.</summary>
    public Action<Exception> UncaughtHandler { get; set; } = static ex => throw ex;

    /// <summary>Count of jobs queued in the internal buffer. Host-scheduled
    /// jobs aren't reflected here — they live on the host loop.</summary>
    public int PendingCount
    {
        get { lock (_lock)
            {
                return _queue.Count;
            }
        }
    }

    /// <summary>Enqueue a job. Delegates to the host scheduler if one was
    /// installed; otherwise pushes to the internal queue. Per §9.4.1 the
    /// job runs at the next clean point on the JS event-loop spine.</summary>
    public void Enqueue(Action job)
    {
        ArgumentNullException.ThrowIfNull(job);
        Action<Action>? scheduler;
        lock (_lock)
        {
            scheduler = _hostScheduler;
            if (scheduler is null)
            {
                _queue.Enqueue(job);
                return;
            }
        }
        scheduler(job);
    }

    /// <summary>Drain the internal queue until empty. Jobs that enqueue more
    /// jobs are picked up in the same drain. No-op when a host scheduler is
    /// installed (the host pumps its own queue).</summary>
    public void DrainAll()
    {
        while (true)
        {
            Action? job;
            lock (_lock)
            {
                if (_hostScheduler is not null)
                {
                    return;
                }

                if (!_queue.TryDequeue(out job))
                {
                    return;
                }
            }
            try { job(); }
            catch (Exception ex) { UncaughtHandler(ex); }
        }
    }

    /// <summary>Install a host bridge so subsequent <see cref="Enqueue"/>
    /// calls feed into the host's loop instead of the internal queue. Pass
    /// <c>null</c> to revert to internal drain.</summary>
    /// <remarks>
    /// Contract: the host scheduler MUST eventually invoke each delegated
    /// job exactly once, on the microtask-checkpoint slot of its loop. Jobs
    /// must not be re-entered while a prior job is running. The host is
    /// responsible for capturing and surfacing exceptions — the engine has
    /// no opportunity to inspect what happens after the handoff.
    /// </remarks>
    public void SetHostScheduler(Action<Action>? scheduler)
    {
        lock (_lock)
        {
            _hostScheduler = scheduler;
        }
    }

    /// <summary>True when a host scheduler is currently installed.</summary>
    public bool HasHostScheduler
    {
        get { lock (_lock)
            {
                return _hostScheduler is not null;
            }
        }
    }
}
