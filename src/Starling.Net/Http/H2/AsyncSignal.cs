namespace Starling.Net.Http.H2;

/// <summary>
/// Edge-triggered async notification: waiters capture <see cref="WaitAsync"/>
/// before re-checking their condition, so a <see cref="Pulse"/> that races the
/// check is never lost. Used to wake stream senders when flow-control windows
/// grow or a concurrency slot frees up.
/// </summary>
/// <remarks>
/// Correct usage:
/// <code>
/// while (true)
/// {
///     var wait = signal.WaitAsync();
///     if (ConditionMet()) break;
///     await wait;
/// }
/// </code>
/// </remarks>
internal sealed class AsyncSignal
{
    private readonly object _gate = new();
    private TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitAsync()
    {
        lock (_gate)
        {
            return _tcs.Task;
        }
    }

    /// <summary>Wake all current waiters and arm a fresh signal for future waits.</summary>
    public void Pulse()
    {
        lock (_gate)
        {
            _tcs.TrySetResult();
            _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
