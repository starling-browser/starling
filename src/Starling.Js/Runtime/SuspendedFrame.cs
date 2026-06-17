using Starling.Js.Bytecode;

namespace Starling.Js.Runtime;

/// <summary>
/// Runtime handle for a generator, async function, or async generator body that
/// is parked at a <c>yield</c>, <c>await</c>, or prologue boundary.
/// </summary>
public sealed class SuspendedFrame
{
    private Action? _body;

    public JsValue ResumeValue { get; private set; } = JsValue.Undefined;
    public bool ResumeWithThrow { get; private set; }
    public bool ResumeWithReturn { get; private set; }
    public JsValue YieldedValue { get; private set; } = JsValue.Undefined;
    public int SuspendKind { get; private set; }
    public bool Completed { get; private set; }
    public JsValue ReturnValue { get; private set; } = JsValue.Undefined;
    public bool ThrewUncaught { get; private set; }
    public JsVm Vm { get; }

    internal bool Suspended { get; private set; }
    internal ContinuationFrameState? State { get; private set; }
    internal ContinuationResumeAction ResumeAction { get; private set; }
    internal YieldDelegateContinuation? YieldDelegate { get; set; }

    public SuspendedFrame(JsVm vm)
    {
        Vm = vm ?? throw new ArgumentNullException(nameof(vm));
    }

    public void Start(Action body)
    {
        if (_body is not null)
        {
            throw new InvalidOperationException("SuspendedFrame already started");
        }

        _body = body ?? throw new ArgumentNullException(nameof(body));
    }

    internal void Suspend(
        ContinuationFrameState state,
        JsValue yieldedValue,
        int suspendKind,
        ContinuationResumeAction resumeAction)
    {
        if (Completed)
        {
            return;
        }

        State = state ?? throw new ArgumentNullException(nameof(state));
        YieldedValue = yieldedValue;
        SuspendKind = suspendKind;
        ResumeAction = resumeAction;
        Suspended = true;
    }

    public void ClearContinuation()
    {
        State = null;
        YieldDelegate = null;
        ResumeAction = ContinuationResumeAction.None;
        Suspended = false;
    }

    internal void ClearResumeAction() => ResumeAction = ContinuationResumeAction.None;

    internal ResumeCompletion ConsumeResume()
    {
        var result = ResumeWithThrow
            ? new ResumeCompletion(ResumeCompletionKind.Throw, ResumeValue)
            : ResumeWithReturn
                ? new ResumeCompletion(ResumeCompletionKind.Return, ResumeValue)
                : new ResumeCompletion(ResumeCompletionKind.Normal, ResumeValue);

        ResumeWithThrow = false;
        ResumeWithReturn = false;
        return result;
    }

    internal void SetReturnValue(JsValue value)
    {
        ReturnValue = value;
        Completed = true;
        ClearContinuation();
    }

    internal void SetThrew(JsValue value)
    {
        ThrewUncaught = true;
        ReturnValue = value;
        Completed = true;
        ClearContinuation();
    }

    public void Resume(JsValue value, bool withThrow = false, bool withReturn = false)
    {
        if (Completed)
        {
            return;
        }

        if (_body is null)
        {
            throw new InvalidOperationException("SuspendedFrame has not been started");
        }

        ResumeValue = value;
        ResumeWithThrow = withThrow;
        ResumeWithReturn = withReturn;
        Suspended = false;

        try
        {
            _body();
        }
        catch (JsThrow ex)
        {
            SetThrew(ex.Value);
        }
        catch (Exception ex)
        {
            SetThrew(JsValue.String("internal VM error: " + ex.Message));
        }
    }
}

internal enum ContinuationResumeAction
{
    None,
    IgnoreResume,
    PushResume,
    AsyncGeneratorYieldAwait,
    YieldDelegate,
}

internal enum ResumeCompletionKind
{
    Normal,
    Throw,
    Return,
}

internal readonly record struct ResumeCompletion(ResumeCompletionKind Kind, JsValue Value);

internal sealed class ContinuationFrameState
{
    public required Chunk Chunk { get; init; }
    public required JsValue[] Stack { get; init; }
    public required JsValue[] Locals { get; init; }
    /// <summary>Carries <see cref="CallFrame.LocalsEscaped"/> across the
    /// suspension so the resumed frame still skips returning an escaped
    /// locals array to the pool at completion.</summary>
    public required bool LocalsEscaped { get; init; }
    public required IReadOnlyList<JsValue> Upvalues { get; init; }
    /// <summary>Null when the frame never entered a try (lazily created).</summary>
    public required Stack<TryFrame>? TryStack { get; init; }
    public JsFunction? CurrentFunction { get; init; }
    public JsObject? NewTarget { get; init; }
    public EvalScope? EvalScope { get; init; }
    public EvalVarStore? FrameVarStore { get; set; }
    public List<JsObject>? WithStack { get; init; }
    public JsValue ThisValue { get; init; }
    public int Ip { get; init; }
    public int Sp { get; init; }
    public int MaxSp { get; init; }
    public int InitDepth { get; init; }
}

internal sealed class YieldDelegateContinuation
{
    public required bool IsAsync { get; init; }
    public required bool SyncWrapped { get; init; }
    public required IteratorRecord Record { get; init; }
    public JsValue InnerIterator { get; init; }
    public JsValue NextMethod { get; init; }
    public JsValue Received { get; set; } = JsValue.Undefined;
    public int ReceivedKind { get; set; }
    public YieldDelegatePhase Phase { get; set; }
    public bool ProcessingReturnResult { get; set; }
    public bool PendingOuterReturn { get; set; }
}

internal enum YieldDelegatePhase
{
    CallInner,
    AwaitInnerResult,
    AfterOuterYield,
}

internal readonly record struct YieldDelegateStep(bool Suspended, bool Completed, JsValue Value)
{
    public static YieldDelegateStep Parked() => new(Suspended: true, Completed: false, JsValue.Undefined);
    public static YieldDelegateStep Done(JsValue value) => new(Suspended: false, Completed: true, value);
}
