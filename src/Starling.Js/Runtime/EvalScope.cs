namespace Starling.Js.Runtime;

/// <summary>
/// wp:M3-72 — direct-eval caller-scope handle. Captures the live variable
/// environment of the frame that performed a direct eval(...) so the
/// eval'd code can read and write the caller's local bindings (params, vars,
/// lets, consts) instead of going straight to the global object (§19.2.1.1
/// PerformEval, §9.1.2.1 ResolveBinding through the caller's LexicalEnvironment).
/// </summary>
/// <remarks>
/// <para>
/// Each in-scope caller binding is represented by an <see cref="Entry"/> that
/// is either backed by a shared <see cref="Cell"/> (for caller locals the
/// compiler promoted to cell storage because some nested function — including
/// this eval — captures them) or by a direct (array, slot) reference into the
/// caller frame's live locals array (for plain, non-captured locals).
/// Reads/writes go through the live storage so mutations are observed by the
/// caller after eval returns, matching live-binding semantics.
/// </para>
/// <para>
/// This is never reachable from user JS; it is consulted only by the
/// eval-scope-aware opcodes the compiler emits for free identifiers that match
/// a caller binding name when compiling direct-eval source.
/// </para>
/// </remarks>
internal sealed class EvalScope
{
    /// <summary>One caller binding visible to the eval'd code. Exactly one of
    /// <see cref="Cell"/> / <see cref="Locals"/> is non-null.</summary>
    internal sealed class Entry
    {
        public required string Name { get; init; }
        /// <summary>Shared cell storage (captured caller local), or null.</summary>
        public Cell? Cell { get; init; }
        /// <summary>Live caller locals array (plain caller local), or null.</summary>
        public JsValue[]? Locals { get; init; }
        /// <summary>Slot index into <see cref="Locals"/> when array-backed.</summary>
        public int Slot { get; init; }
        /// <summary>True when this is a lexical binding (let/const/class) and so
        /// is subject to the Temporal Dead Zone in the caller.</summary>
        public bool IsLexical { get; init; }
        /// <summary>True when this is an immutable caller binding.</summary>
        public bool IsConst { get; init; }

        public JsValue Read()
            => Cell is { } c ? c.Value : Locals![Slot];

        public void Write(JsValue v)
        {
            if (Cell is { } c)
            {
                c.Value = v;
            }
            else
            {
                Locals![Slot] = v;
            }
        }
    }

    private readonly Dictionary<string, Entry> _byName;

    public EvalScope(IReadOnlyList<Entry> entries)
    {
        _byName = new Dictionary<string, Entry>(StringComparer.Ordinal);
        // Innermost-first wins: entries are supplied innermost scope first, so
        // only add the first occurrence of each name (shadowing).
        foreach (var e in entries)
        {
            _byName.TryAdd(e.Name, e);
        }
    }

    /// <summary>The full set of caller binding names visible to the eval'd code
    /// (used at compile time so a free identifier matching one routes through
    /// the eval-scope-aware opcode instead of going to the global object).</summary>
    public IReadOnlyCollection<string> Names => _byName.Keys;

    public bool TryGet(string name, out Entry entry) => _byName.TryGetValue(name, out entry!);
}
