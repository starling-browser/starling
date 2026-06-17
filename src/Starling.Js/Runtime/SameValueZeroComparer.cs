namespace Starling.Js.Runtime;

/// <summary>
/// §7.2.11 SameValueZero — used as the key-equality predicate for Map/Set.
/// NaN compares equal to NaN, and +0 compares equal to -0. String/Object/
/// Symbol/BigInt use strict-equality semantics.
/// </summary>
/// <remarks>
/// <para><b>Hashing:</b> dictionaries demand that <c>Equals(a, b) =&gt;
/// GetHashCode(a) == GetHashCode(b)</c>. The tricky pieces are:</para>
/// <list type="bullet">
///   <item><description>All NaNs hash to the same bucket (the .NET runtime
///     also collapses NaN doubles in <see cref="double.GetHashCode"/>, but we
///     fold explicitly so any payload-NaN encoding still collides).</description></item>
///   <item><description><c>+0</c> and <c>-0</c> must hash equal — we hash on
///     the numeric value, and both compare equal to 0.0 in IEEE 754.</description></item>
///   <item><description>Objects/Symbols hash by reference; Strings/BigInts
///     hash by their underlying string bits.</description></item>
/// </list>
/// </remarks>
internal sealed class SameValueZeroComparer : IEqualityComparer<JsValue>
{
    public static readonly SameValueZeroComparer Instance = new();
    private SameValueZeroComparer() { }

    public bool Equals(JsValue x, JsValue y) => AbstractOperations.SameValueZero(x, y);

    public int GetHashCode(JsValue obj)
    {
        // Combine the discriminator with a kind-appropriate hash payload.
        return obj.Kind switch
        {
            JsValueKind.Undefined => HashCode.Combine(JsValueKind.Undefined),
            JsValueKind.Null => HashCode.Combine(JsValueKind.Null),
            JsValueKind.Boolean => HashCode.Combine(JsValueKind.Boolean, obj.AsBool),
            JsValueKind.Number => HashNumber(obj.AsNumber),
            JsValueKind.String => HashCode.Combine(JsValueKind.String, obj.AsString),
            JsValueKind.Object => HashCode.Combine(JsValueKind.Object,
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.AsObject)),
            JsValueKind.Symbol => HashCode.Combine(JsValueKind.Symbol,
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.AsSymbol)),
            JsValueKind.BigInt => HashCode.Combine(JsValueKind.BigInt, obj.AsBigInt),
            _ => 0,
        };
    }

    private static int HashNumber(double n)
    {
        // All NaNs collapse to one bucket so Map/Set treat NaN-keys as equal.
        if (double.IsNaN(n))
        {
            return HashCode.Combine(JsValueKind.Number, "NaN");
        }
        // +0 and -0 must hash equally per SameValueZero. Comparing to 0.0
        // returns true for both, so reuse a fixed bucket.
        if (n == 0.0)
        {
            return HashCode.Combine(JsValueKind.Number, 0.0);
        }

        return HashCode.Combine(JsValueKind.Number, n);
    }
}
