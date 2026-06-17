using System.Buffers.Binary;

namespace Starling.Js.Bytecode;

/// <summary>
/// A bytecode chunk — bytes + constant pool + local-slot count.
/// </summary>
/// <remarks>
/// <para>
/// One chunk per compilation unit (script top-level body, function body,
/// etc.). The chunk is immutable from the VM's perspective; the compiler
/// builds it via <see cref="ChunkBuilder"/> and then freezes it.
/// </para>
/// <para>
/// Constants are boxed as object: doubles for NumericLiteral, strings for
/// StringLiteral, the singleton <see cref="JsBigIntPlaceholder"/> for
/// BigIntLiteral (decoded by the runtime when the type is needed).
/// </para>
/// </remarks>
public sealed class Chunk
{
    public byte[] Code { get; }
    public IReadOnlyList<object?> Constants { get; }
    public int LocalCount { get; }
    public string? Name { get; }
    /// <summary>wp:M3-63 — the source path/URL of the script or module this
    /// chunk lexically belongs to. Unlike <see cref="Name"/> (which is the
    /// running function's own name, e.g. <c>"foo"</c> for a nested function),
    /// this is the SAME value for the top-level chunk and every nested function
    /// chunk compiled within it. It is the referrer used to resolve dynamic
    /// <c>import()</c> relative specifiers and to back <c>import.meta.url</c>, so
    /// a relative import inside a nested async / arrow / generator function
    /// resolves against the active script/module (matching real engines, where
    /// the referrer is the active script/module record — not the running
    /// function). Falls back to <see cref="Name"/> when unset (e.g. the
    /// top-level script chunk whose <see cref="Name"/> already is the path).</summary>
    public string? SourcePath { get; init; }
    /// <summary>ES strict mode — true when the code in this chunk runs as strict
    /// mode code (§11.2.2). Read by the VM to select strict semantics for
    /// this-binding (§10.2.1.2), assignment to undeclared globals (§9.1.1.4.16),
    /// and strict property/delete failures (§10.1.9 / §13.5.1.2).</summary>
    public bool IsStrict { get; init; }
    /// <summary>§10.2.1.3 — true when this chunk is a generator / async /
    /// async-generator body whose parameter-binding prologue ends at a
    /// <see cref="Opcode.PrologueEnd"/> marker. The runtime dispatcher
    /// (<c>Start{Generator,Async,AsyncGenerator}Body</c>) runs that prologue
    /// synchronously at call time, so a destructuring/default throw surfaces
    /// before the generator object / promise is produced. Synthetic async bodies
    /// without a marker (e.g. top-level-await module wrappers) leave this false
    /// so the dispatcher does not consume an extra resume looking for one.</summary>
    public bool HasPrologue { get; init; }
    /// <summary>§14.11 / §10.2.1 — true when this chunk's body was compiled
    /// lexically inside one or more <c>with</c> statements, so a function
    /// instance built from it must snapshot the creating frame's with-stack
    /// (stored on <see cref="Starling.Js.Runtime.JsFunction.CapturedWith"/>)
    /// and the VM must seed the callee frame's with-stack from it.</summary>
    public bool CapturesWith { get; init; }
    /// <summary>wp:M3-73 — true when this function's body or parameter list
    /// lexically contains a direct eval call (not crossing nested function
    /// boundaries). A non-strict such function's frame eagerly allocates an
    /// <see cref="Starling.Js.Runtime.EvalVarStore"/> at entry so the §19.2.1.3
    /// var/function bindings that a direct eval injects into this function's
    /// variable environment are visible both to the rest of this frame and to
    /// closures it creates (which snapshot the store onto
    /// <see cref="Starling.Js.Runtime.JsFunction.CapturedEvalVarStore"/>).</summary>
    public bool HasDirectEval { get; init; }
    /// <summary>wp:M3-64 — true when this chunk is an arrow-function body. Arrows
    /// inherit <c>super</c> / <c>[[HomeObject]]</c> lexically (§14.2 / §13.2.5
    /// note): they are never made into methods themselves, so the VM stamps the
    /// enclosing frame's <see cref="Starling.Js.Runtime.JsFunction.HomeObject"/>
    /// onto an arrow closure when it is created, letting <c>super.x</c> inside the
    /// arrow resolve against the enclosing method's home object.</summary>
    public bool IsArrow { get; init; }
    /// <summary>wp:M3-81 — true when this chunk is a class field initializer or
    /// static-block thunk (an "initializer" per §sec-performeval-rules-in-initializer).
    /// At frame entry the VM seeds a non-zero initializer depth so a direct eval
    /// running at this thunk's top level (and inside arrows it lexically creates,
    /// which carry <see cref="Starling.Js.Runtime.JsFunction.InInitializer"/>) is
    /// subject to the ContainsArguments early-error rule.</summary>
    public bool IsInitializer { get; init; }
    /// <summary>Local-slot indices promoted to <c>Cell</c> storage because at
    /// least one nested function references the binding. Empty for functions
    /// with no captured locals.</summary>
    public IReadOnlySet<int> CapturedSlots { get; }

    /// <summary>wp:M3-23 — sparse source-position table, sorted ascending by
    /// bytecode offset. Each entry maps the start offset of a
    /// throw-prone opcode (Call / CallMethod / New / *Apply / member loads)
    /// to the 1-based line/column of the originating AST node. Empty unless
    /// the compiler recorded positions. Lookup via <see cref="PositionAt"/>.</summary>
    public IReadOnlyList<(int Offset, int Line, int Col)> Positions { get; }

    /// <summary>§16.1.7 / §19.2.1.1 — the private-name environment in scope for
    /// this chunk's body: a map from each visible private name (e.g.
    /// <c>"#m"</c>) to its mangled own-property key. A direct <c>eval</c> running
    /// in this function's context inherits it so eval'd code can resolve
    /// <c>this.#m</c> against the enclosing class's private names (the spec's
    /// PrivateEnvironment threaded into the eval). Null when no private names are
    /// in scope (the common case).</summary>
    public IReadOnlyDictionary<string, string>? PrivateNameScope { get; init; }

    public Chunk(byte[] code, IReadOnlyList<object?> constants, int localCount, string? name = null)
        : this(code, constants, localCount, name, capturedSlots: null)
    {
    }

    public Chunk(byte[] code, IReadOnlyList<object?> constants, int localCount, string? name, IReadOnlySet<int>? capturedSlots)
        : this(code, constants, localCount, name, capturedSlots, positions: null)
    {
    }

    public Chunk(byte[] code, IReadOnlyList<object?> constants, int localCount, string? name,
        IReadOnlySet<int>? capturedSlots, IReadOnlyList<(int Offset, int Line, int Col)>? positions,
        int inlineCacheCount = 0, int regexLiteralCount = 0)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
        Constants = constants ?? throw new ArgumentNullException(nameof(constants));
        LocalCount = localCount;
        Name = name;
        CapturedSlots = capturedSlots ?? EmptyCaptured;
        Positions = positions ?? EmptyPositions;
        Caches = inlineCacheCount == 0 ? EmptyCaches : new Runtime.InlineCache[inlineCacheCount];
        RegexLiterals = regexLiteralCount == 0 ? EmptyRegexLiterals : new Runtime.Regex.IRegexMatcher[regexLiteralCount];
    }

    private static readonly IReadOnlySet<int> EmptyCaptured = new HashSet<int>();
    private static readonly IReadOnlyList<(int, int, int)> EmptyPositions = [];
    private static readonly Runtime.InlineCache[] EmptyCaches = System.Array.Empty<Runtime.InlineCache>();
    private static readonly Runtime.Regex.IRegexMatcher[] EmptyRegexLiterals = System.Array.Empty<Runtime.Regex.IRegexMatcher>();

    /// <summary>Per-site compiled regex literals, indexed by the <c>u16</c> cache
    /// id operand on <see cref="Opcode.LoadRegExp"/>. A regex literal compiles
    /// once and is reused across re-evaluations (e.g. in a loop); a fresh
    /// <c>JsRegExp</c> wrapper is still created each time per spec. Write-once
    /// from the VM; the matcher is immutable and realm-independent so sharing
    /// across realms/threads is safe.</summary>
    internal Runtime.Regex.IRegexMatcher?[] RegexLiterals { get; }

    /// <summary>Per-property-site inline caches, indexed by the <c>u16</c> cache
    /// id operand the compiler emits on <see cref="Opcode.LoadProperty"/> /
    /// <see cref="Opcode.StoreProperty"/>. Mutated by the VM on a cache miss;
    /// the array reference is fixed for the chunk's life. A cache id of
    /// <c>0xFFFF</c> means "uncacheable site" (allocated past the per-chunk
    /// limit) and indexes nothing.</summary>
    internal Runtime.InlineCache[] Caches { get; }

    /// <summary>wp:M3-23 — find the nearest recorded source position at or
    /// before <paramref name="ip"/>. The VM's <c>ip</c> at a throw site has
    /// already advanced past the offending opcode's operand bytes, so the
    /// recorded entry (keyed by the opcode's start offset) is the greatest
    /// entry with <c>Offset &lt;= ip</c>. Returns <c>null</c> when no position
    /// was recorded (empty table or the throw precedes the first entry).</summary>
    public (int Line, int Col)? PositionAt(int ip)
    {
        var positions = Positions;
        if (positions.Count == 0)
        {
            return null;
        }
        // Binary search for the greatest Offset <= ip.
        int lo = 0, hi = positions.Count - 1, best = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (positions[mid].Offset <= ip) { best = mid; lo = mid + 1; }
            else
            {
                hi = mid - 1;
            }
        }
        if (best < 0)
        {
            return null;
        }

        var e = positions[best];
        return (e.Line, e.Col);
    }

    public override string ToString() => Disassembler.Disassemble(this);
}

/// <summary>
/// Constant-pool wrapper for a BigInt literal. The parser turns the lexeme
/// (decimal / 0x / 0b / 0o, with the trailing <c>n</c> stripped) into a
/// <see cref="System.Numerics.BigInteger"/> at AST-build time; the VM unboxes
/// it via <see cref="Starling.Js.Runtime.JsValue.BigInt(System.Numerics.BigInteger)"/>
/// when <see cref="Opcode.LoadConst"/> dispatches on this record.
/// </summary>
public sealed record JsBigIntPlaceholder(System.Numerics.BigInteger Value)
{
    /// <summary>Back-compat ctor — parse a decimal digits string.</summary>
    public JsBigIntPlaceholder(string digits)
        : this(System.Numerics.BigInteger.Parse(digits,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture))
    { }

    /// <summary>Back-compat decimal-string view of <see cref="Value"/>.
    /// Used by the disassembler.</summary>
    public string Digits => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Constant-pool wrapper describing a tagged template's strings array
/// (§13.2.8.4 GetTemplateObject). <see cref="Cooked"/> holds the cooked
/// segments (a <c>null</c> entry means an illegal escape, surfaced as
/// <c>undefined</c>); <see cref="Raw"/> holds the matching raw segments for
/// <c>strings.raw</c>. One instance is emitted per tagged-template call site,
/// and its reference identity keys the per-realm cache so the VM hands the same
/// frozen object back on every evaluation of that site (the spec's call-site
/// caching that user tag functions rely on as a Map/WeakMap key).
/// </summary>
public sealed class TemplateObjectTemplate(IReadOnlyList<string?> cooked, IReadOnlyList<string> raw)
{
    public IReadOnlyList<string?> Cooked { get; } = cooked;
    public IReadOnlyList<string> Raw { get; } = raw;
}

/// <summary>
/// Mutable builder used by <c>JsCompiler</c>. Tracks the constant pool,
/// the byte stream, and the active local-slot count.
/// </summary>
public sealed class ChunkBuilder
{
    private readonly List<byte> _code = [];
    private readonly List<object?> _constants = [];
    private readonly Dictionary<string, int> _stringPool = new(StringComparer.Ordinal);
    private HashSet<int>? _capturedSlots;
    private List<(int Offset, int Line, int Col)>? _positions;
    public int LocalCount { get; private set; }

    /// <summary>ES strict mode — set by the compiler from the AST node's
    /// effective strictness before <see cref="Build"/>. Stamped onto the
    /// produced <see cref="Chunk.IsStrict"/>.</summary>
    public bool IsStrict { get; set; }

    /// <summary>§10.2.1.3 — set when a <see cref="Opcode.PrologueEnd"/> marker is
    /// emitted; stamped onto <see cref="Chunk.HasPrologue"/>.</summary>
    public bool HasPrologue { get; private set; }

    /// <summary>wp:M3-23 — record that the opcode about to be emitted at the
    /// current <see cref="Position"/> originates from the given 1-based source
    /// line/column. Call immediately BEFORE emitting a throw-prone opcode so
    /// the recorded offset equals the opcode's start byte. Entries are kept in
    /// emission order, which is monotonically non-decreasing in offset, so the
    /// table is already sorted for <see cref="Chunk.PositionAt"/>'s binary
    /// search. Duplicate offsets are coalesced (last write wins).</summary>
    public void RecordPosition(int line, int col)
    {
        _positions ??= [];
        var offset = _code.Count;
        if (_positions.Count > 0 && _positions[^1].Offset == offset)
        {
            _positions[^1] = (offset, line, col);
        }
        else
        {
            _positions.Add((offset, line, col));
        }
    }

    /// <summary>Record that <paramref name="slot"/> is captured by a nested
    /// function and must use <see cref="Starling.Js.Runtime.Cell"/> storage.
    /// Reads and writes against the slot go through the cell-aware opcodes.</summary>
    public void MarkCaptured(int slot)
    {
        _capturedSlots ??= [];
        _capturedSlots.Add(slot);
    }

    public bool IsCaptured(int slot) => _capturedSlots is not null && _capturedSlots.Contains(slot);

    public int Position => _code.Count;

    public int AddConstant(object? value)
    {
        if (value is string s && _stringPool.TryGetValue(s, out var idx))
        {
            return idx;
        }

        var pos = _constants.Count;
        _constants.Add(value);
        if (value is string ss)
        {
            _stringPool[ss] = pos;
        }

        return pos;
    }

    public int ReserveLocal()
    {
        var slot = LocalCount;
        LocalCount++;
        return slot;
    }

    public void Emit(Opcode op)
    {
        if (op == Opcode.PrologueEnd)
        {
            HasPrologue = true;
        }

        _code.Add((byte)op);
    }

    public void Emit(Opcode op, byte arg)
    {
        _code.Add((byte)op);
        _code.Add(arg);
    }

    public void EmitU16(Opcode op, int arg)
    {
        if (arg is < 0 or > 0xFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(arg), arg, "operand exceeds u16 range");
        }

        _code.Add((byte)op);
        var b1 = (byte)(arg & 0xFF);
        var b2 = (byte)((arg >> 8) & 0xFF);
        _code.Add(b1);
        _code.Add(b2);
    }

    /// <summary>Number of inline-cache sites allocated so far; becomes the size
    /// of <see cref="Chunk.Caches"/>. Capped at 0xFFFF (see
    /// <see cref="EmitProperty"/>).</summary>
    private int _cacheCount;

    /// <summary>Emit a named property access opcode (<see cref="Opcode.LoadProperty"/>
    /// / <see cref="Opcode.StoreProperty"/>) as <c>[op][u16 nameIdx][u16
    /// cacheId]</c>. Each site gets its own dense cache id so the VM can index
    /// <see cref="Chunk.Caches"/> directly. Past 0xFFFF sites (huge functions)
    /// the site is emitted with the 0xFFFF "uncacheable" sentinel and always
    /// takes the slow path.</summary>
    public void EmitProperty(Opcode op, int nameIdx)
    {
        EmitU16(op, nameIdx);
        if (_cacheCount < 0xFFFF) { EmitU16Raw(_cacheCount); _cacheCount++; }
        else
        {
            EmitU16Raw(0xFFFF);
        }
    }

    /// <summary>Number of regex-literal sites; becomes the size of
    /// <see cref="Chunk.RegexLiterals"/>.</summary>
    private int _regexCacheCount;

    /// <summary>Allocate a per-site cache id for a <see cref="Opcode.LoadRegExp"/>
    /// site so its compiled matcher is reused across re-evaluations. The id is
    /// emitted as a u16 operand, so past 65535 regex-literal sites we throw an
    /// explicit, located error rather than letting <see cref="EmitU16Raw"/> raise
    /// a bare "operand exceeds u16 range" — consistent with the local-slot and
    /// upvalue limits below.</summary>
    public int AllocateRegexCache()
    {
        if (_regexCacheCount > 0xFFFF)
        {
            throw new InvalidOperationException(
                "regex-literal site count exceeds the u16 limit (65535); function has too many regex literals");
        }

        return _regexCacheCount++;
    }

    /// <summary>Emit a local-slot opcode with a 16-bit slot operand. Local
    /// slots are addressed with a u16 (not u8) so functions with more than
    /// 255 locals — common in large minified bundles such as Google Tag
    /// Manager, whose top-level IIFE has thousands of hoisted closures and
    /// captured vars — address every slot uniquely. A u8 operand silently
    /// wrapped slot indices modulo 256, aliasing an unrelated low slot; if
    /// one of the colliding slots was a captured <c>Cell</c> and the other a
    /// plain local, a <c>StoreLocal</c> could overwrite the cell with a raw
    /// value, and a later <c>StoreCellLocal</c> on the same byte operand then
    /// failed casting the raw value to <c>Cell</c>.</summary>
    public void EmitSlot(Opcode op, int slot)
    {
        if (slot is < 0 or > 0xFFFF)
        {
            throw new InvalidOperationException(
                $"local slot {slot} exceeds the u16 limit (65535); function has too many locals");
        }

        EmitU16(op, slot);
    }

    /// <summary>Emit an upvalue opcode (<see cref="Opcode.LoadUpvalue"/>,
    /// <see cref="Opcode.StoreUpvalue"/>, <see cref="Opcode.LoadUpvalueCell"/>)
    /// with a 16-bit upvalue-index operand. Upvalue indices are addressed with
    /// a u16 (not u8) so functions that capture more than 255 outer bindings —
    /// common in large minified bundles such as Google Tag Manager / gtag,
    /// whose inner closures reference hundreds of hoisted vars — address every
    /// captured binding uniquely. A u8 operand previously hard-capped captures
    /// at 255 (throwing at compile time), leaving large bundles' declarations
    /// undefined.</summary>
    public void EmitUpvalue(Opcode op, int idx)
    {
        if (idx is < 0 or > 0xFFFF)
        {
            throw new InvalidOperationException(
                $"upvalue index {idx} exceeds the u16 limit (65535); function captures too many bindings");
        }

        EmitU16(op, idx);
    }

    /// <summary>
    /// Emit a jump opcode with a placeholder offset; returns the position of
    /// the offset bytes for later patching via <see cref="PatchJump"/>.
    /// </summary>
    public int EmitJump(Opcode op)
    {
        _code.Add((byte)op);
        var pos = _code.Count;
        // i32 offset (4 bytes). A u16 cap (±32767) overflowed on large minified
        // bundles whose single functions compile to >32 KB of bytecode.
        _code.Add(0);
        _code.Add(0);
        _code.Add(0);
        _code.Add(0);
        return pos;
    }

    /// <summary>Patch a previously-emitted jump to land at the current position.</summary>
    public void PatchJump(int operandPos)
    {
        var jumpFrom = operandPos + 4; // jump is measured from end-of-operand
        var target = _code.Count;
        var delta = target - jumpFrom;
        PatchI32(operandPos, delta);
    }

    /// <summary>Write a signed 32-bit value at an arbitrary previously-reserved
    /// position (used for backward jumps and composite-operand offsets).</summary>
    public void PatchI32(int pos, int value)
        => BinaryPrimitives.WriteInt32LittleEndian(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_code).Slice(pos, 4),
            value);

    /// <summary>Emit two raw bytes for an unsigned 16-bit operand. Useful
    /// after a manually-emitted opcode that doesn't have a typed
    /// <see cref="EmitU16"/> overload.</summary>
    public void EmitU16Raw(int value)
    {
        if (value is < 0 or > 0xFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        _code.Add((byte)(value & 0xFF));
        _code.Add((byte)((value >> 8) & 0xFF));
    }

    /// <summary>Emit four raw bytes for a signed 32-bit operand. Used for
    /// jump/branch offset placeholders that are patched later via
    /// <see cref="PatchJump"/> / <see cref="PatchI32"/>.</summary>
    public void EmitI32Raw(int value)
    {
        _code.Add((byte)(value & 0xFF));
        _code.Add((byte)((value >> 8) & 0xFF));
        _code.Add((byte)((value >> 16) & 0xFF));
        _code.Add((byte)((value >> 24) & 0xFF));
    }

    /// <summary>Emit a raw unsigned-8-bit byte. Used for opcodes with
    /// composite operand layouts (e.g. <see cref="Opcode.MakeClosure"/>
    /// = <c>[u16 idx][u8 count]</c>) where the typed
    /// <see cref="Emit(Opcode, byte)"/> doesn't apply.</summary>
    public void EmitU8Raw(int value)
    {
        if (value is < 0 or > 0xFF)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        _code.Add((byte)value);
    }

    public bool CapturesWith { get; set; }

    /// <summary>wp:M3-73 — set by the compiler when this function's body/params
    /// lexically contain a direct eval call; stamped onto
    /// <see cref="Chunk.HasDirectEval"/>.</summary>
    public bool HasDirectEval { get; set; }

    /// <summary>wp:M3-64 — set true while building an arrow-function body so the
    /// produced <see cref="Chunk.IsArrow"/> is stamped. The VM uses it to copy
    /// the enclosing frame's [[HomeObject]] onto the arrow closure for lexical
    /// <c>super</c>.</summary>
    public bool IsArrow { get; set; }

    /// <summary>wp:M3-81 — set true while building a class field initializer or
    /// static-block thunk so the produced <see cref="Chunk.IsInitializer"/> is
    /// stamped (drives the eval-inside-initializer ContainsArguments rule).</summary>
    public bool IsInitializer { get; set; }

    /// <summary>§16.1.7 — the private-name environment in scope for this body;
    /// stamped onto <see cref="Chunk.PrivateNameScope"/> so a direct eval can
    /// recover the enclosing class's private names. Null when none are in scope.</summary>
    public IReadOnlyDictionary<string, string>? PrivateNameScope { get; set; }

    /// <summary>wp:M3-63 — the source path/URL of the script or module being
    /// compiled. Set on the top-level compiler's builder from the compile
    /// entry-point's <c>name</c> (the script/module URL) and inherited by every
    /// nested function's builder, so the stamped <see cref="Chunk.SourcePath"/>
    /// is identical across the whole compilation unit.</summary>
    public string? SourcePath { get; set; }

    public Chunk Build(string? name = null)
        => new(_code.ToArray(), _constants.ToArray(), LocalCount, name, _capturedSlots,
            _positions is null ? null : _positions.ToArray(), _cacheCount, _regexCacheCount)
        { IsStrict = IsStrict, HasPrologue = HasPrologue, CapturesWith = CapturesWith, SourcePath = SourcePath, IsArrow = IsArrow, IsInitializer = IsInitializer, HasDirectEval = HasDirectEval, PrivateNameScope = PrivateNameScope };
}
