using Starling.RegExp;
using Starling.Js.Runtime.Regex;

namespace Starling.Js.Runtime;

/// <summary>
/// ES2024 §22.2.7 RegExp instance — an exotic object holding a compiled regex
/// plus the mutable <c>lastIndex</c> slot. Inherits from
/// <see cref="JsRealm.RegExpPrototype"/>.
/// </summary>
public sealed class JsRegExp : JsObject
{
    // §22.2.7.1: every RegExp has exactly one own property, lastIndex
    // (writable, non-enumerable, non-configurable). Precompute that shape once
    // so constructing a regex — frequent for a literal re-evaluated in a loop —
    // adopts it with a single-slot array instead of running DefineOwnProperty
    // (which would grow a fresh capacity-4 slot array per instance). The shape
    // is byte-identical to the incremental one (same Root→{lastIndex} transition
    // and flags), so inline caches and migration are unaffected.
    internal static readonly Shape LastIndexShape = Shape.Root.Transition("lastIndex", Shape.Writable);

    public IRegexMatcher Compiled { get; }
    public string Source => EscapeSource(Compiled.Source);
    public RegexFlags Flags => Compiled.Flags;

    public JsRegExp(JsRealm realm, IRegexMatcher compiled) : base(realm.RegExpPrototype)
    {
        Compiled = compiled;
        AdoptShape(LastIndexShape, new[] { JsValue.Number(0) });
    }

    public double LastIndex
    {
        get
        {
            var v = Get("lastIndex");
            return v.IsNumber ? v.AsNumber : JsValue.ToNumber(v);
        }
        set => Set("lastIndex", JsValue.Number(value));
    }

    /// <summary>True iff this regexp still carries the canonical single-slot
    /// <c>lastIndex</c> shape — i.e. no own <c>exec</c>/<c>global</c>/<c>unicode</c>
    /// (or any other) own property shadows the prototype, and <c>lastIndex</c>
    /// lives at slot 0. The @@replace fast path uses this to skip the
    /// instance-level guard walk and to write <c>lastIndex</c> directly.</summary>
    internal bool HasPristineShape => ReferenceEquals(Shape, LastIndexShape);

    /// <summary>Set <c>lastIndex</c> to 0 via a direct slot write, valid only when
    /// <see cref="HasPristineShape"/> holds (lastIndex is slot 0). Avoids the
    /// name lookup of the generic setter on the global-replace hot path.</summary>
    internal void ResetLastIndexFast() => WriteSlot(0, JsValue.Number(0));

    public override string ToString() => $"/{Source}/{RegexFlagParser.ToFlagString(Flags)}";

    private static string EscapeSource(string source)
    {
        if (source.Length == 0)
        {
            return "(?:)";
        }

        var sb = new System.Text.StringBuilder(source.Length);
        var inClass = false;
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '\\')
            {
                sb.Append(c);
                if (i + 1 < source.Length)
                {
                    sb.Append(source[++i]);
                }

                continue;
            }

            if (c == '[')
            {
                inClass = true;
            }
            else if (c == ']')
            {
                inClass = false;
            }

            switch (c)
            {
                case '/' when !inClass:
                    sb.Append("\\/");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\u2028':
                    sb.Append("\\u2028");
                    break;
                case '\u2029':
                    sb.Append("\\u2029");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
