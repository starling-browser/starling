namespace Starling.Js.Runtime;

/// <summary>§27.2 Promise state.</summary>
public enum PromiseState : byte
{
    Pending = 0,
    Fulfilled = 1,
    Rejected = 2,
}

/// <summary>
/// §27.2.6 Promise Objects — exotic object carrying the internal slots
/// [[PromiseState]], [[PromiseResult]], [[PromiseFulfillReactions]], and
/// [[PromiseRejectReactions]]. Pending until <c>resolve</c> or <c>reject</c>
/// is called by the executor (or by the constructor on a thrown executor),
/// then immutable.
/// </summary>
/// <remarks>
/// Stored as a real subclass (rather than an internal-slot map on a plain
/// <see cref="JsObject"/>) so the VM and intrinsics can fast-path-check via
/// <c>is JsPromise</c> — useful for <c>Promise.resolve</c>'s identity
/// shortcut, async and await adoption, and callers that need Promise-specific
/// state without property lookups.
/// </remarks>
public sealed class JsPromise : JsObject
{
    public PromiseState State { get; private set; } = PromiseState.Pending;
    public JsValue Result { get; private set; } = JsValue.Undefined;

    /// <summary>§27.2.6 [[PromiseIsHandled]] — true once any reaction has been
    /// attached via PerformPromiseThen (await adopts through <c>then</c>, so
    /// async machinery is covered). Drives HostPromiseRejectionTracker: a
    /// rejection on an unhandled promise is reported through
    /// <see cref="JsRealm.OnUnhandledRejection"/>.</summary>
    public bool IsHandled { get; internal set; }

    /// <summary>Reactions queued while pending; flushed to the microtask
    /// queue at the moment of settlement (§27.2.1.6 TriggerPromiseReactions).</summary>
    internal List<PromiseReaction> FulfillReactions { get; } = new();
    internal List<PromiseReaction> RejectReactions { get; } = new();

    public JsPromise(JsObject? prototype) : base(prototype) { }

    /// <summary>Transition Pending → Fulfilled. No-op if already settled.
    /// Returns true on success.</summary>
    internal bool Fulfill(JsValue value)
    {
        if (State != PromiseState.Pending)
        {
            return false;
        }

        State = PromiseState.Fulfilled;
        Result = value;
        return true;
    }

    /// <summary>Transition Pending → Rejected. No-op if already settled.</summary>
    internal bool Reject(JsValue reason)
    {
        if (State != PromiseState.Pending)
        {
            return false;
        }

        State = PromiseState.Rejected;
        Result = reason;
        return true;
    }

    public override string ToString() => State switch
    {
        PromiseState.Pending => "Promise { <pending> }",
        PromiseState.Fulfilled => $"Promise {{ {JsValue.ToStringValue(Result)} }}",
        PromiseState.Rejected => $"Promise {{ <rejected> {JsValue.ToStringValue(Result)} }}",
        _ => "Promise",
    };
}

/// <summary>§27.2.1.2 PromiseCapability Record. Carries a promise and the
/// resolving functions that settle it. Returned by <c>NewPromiseCapability</c>
/// and consumed everywhere the spec spells "PromiseCapability".</summary>
internal sealed record PromiseCapability(JsPromise Promise, JsValue Resolve, JsValue Reject);

/// <summary>§27.2.1.1 PromiseReaction Record. One entry on a pending promise's
/// reaction list; copied to the microtask queue at settlement.</summary>
/// <param name="Capability">The chained promise's capability — the one that
/// the reaction's outcome settles.</param>
/// <param name="Type">Fulfill vs. Reject — picks which handler slot to use.</param>
/// <param name="Handler">User-supplied callback (or <c>undefined</c> for the
/// pass-through identity / thrower introduced by spec when omitted).</param>
internal sealed record PromiseReaction(
    PromiseCapability Capability,
    PromiseReactionType Type,
    JsValue Handler);

internal enum PromiseReactionType : byte
{
    Fulfill = 0,
    Reject = 1,
}
