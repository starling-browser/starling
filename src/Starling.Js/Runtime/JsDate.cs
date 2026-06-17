namespace Starling.Js.Runtime;

/// <summary>
/// §21.4 Date instance — wraps a millisecond timestamp (UTC, since the Unix
/// epoch) with NaN representing an invalid date. Backed by .NET's
/// <see cref="System.DateTimeOffset"/> for arithmetic + formatting; locale
/// handling is intentionally invariant (en-US, UTC) since Starling targets
/// reproducible rendering, not human-locale UX.
/// </summary>
/// <remarks>
/// The time-value uses <c>double</c> (not <c>long</c>) so we can represent
/// NaN per §21.4.1.1 — every getter on an invalid date is required to return
/// NaN, and every setter on an invalid date keeps it invalid.
/// </remarks>
public sealed class JsDate : JsObject
{
    /// <summary>The underlying [[DateValue]] internal slot — milliseconds
    /// since the Unix epoch, or NaN for an invalid date.</summary>
    public double TimeValueMs { get; private set; }

    public bool IsValid => !double.IsNaN(TimeValueMs);

    public JsDate(JsRealm realm, double ms) : base(realm.DatePrototype)
    {
        TimeValueMs = ms;
    }

    /// <summary>Convert the stored ms timestamp to a UTC
    /// <see cref="System.DateTimeOffset"/>, or <c>null</c> if invalid /
    /// out-of-range.</summary>
    public System.DateTimeOffset? ToDto()
    {
        if (!IsValid)
        {
            return null;
        }

        var ms = TimeValueMs;
        // DateTimeOffset.FromUnixTimeMilliseconds rejects values outside
        // [-62135596800000, 253402300799999]. Clamp via try/catch so we mirror
        // the JS notion of an invalid date for absurd inputs rather than
        // throwing into user code.
        if (ms < -62135596800000d || ms > 253402300799999d)
        {
            return null;
        }

        try
        {
            return System.DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
        }
        catch (System.ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public void SetTimeMs(double ms) => TimeValueMs = ms;
}
