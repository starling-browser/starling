using System.Runtime.CompilerServices;
using Starling.Dom;
using Starling.Js.Runtime;

namespace Starling.Bindings;

/// <summary>
/// WPT-02 — DOM §4.6 Range + §4.6(StaticRange stub): installs the
/// <c>Range</c> constructor and prototype onto a realm, and adds
/// <c>document.createRange()</c> to the Document prototype.
/// </summary>
/// <remarks>
/// <para><b>Identity:</b> each host <see cref="DomRange"/> gets exactly one JS
/// wrapper per realm, cached in a <see cref="ConditionalWeakTable{TKey,TValue}"/>.
/// This ensures that <c>range.startContainer === range.startContainer</c> (the
/// wrapper is returned from the same accessor, but the node itself is wrapped by
/// <see cref="DomWrappers.Wrap"/>).</para>
/// <para><b>Out of scope:</b> <c>extractContents</c>, <c>cloneContents</c>,
/// <c>insertNode</c>, <c>surroundContents</c> — not exercised by the WPT subtests
/// targeted here. <c>Selection</c> and <c>StaticRange</c> constructor are
/// provided as minimal stubs to stop the test-harness crashing on their absence.</para>
/// </remarks>
public static class RangeBinding
{
    // Per-realm wrapper caches for DomRange objects.
    private static readonly ConditionalWeakTable<JsRealm, ConditionalWeakTable<DomRange, JsObject>> RangeCachesPerRealm = new();

    /// <summary>Install Range, StaticRange, and Selection on the realm.
    /// Also installs <c>document.createRange()</c> on the Document prototype.
    /// Must be called after <see cref="NodeBindings.Install"/> so that
    /// <c>realm.DocumentPrototype</c> is already set.</summary>
    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        // Idempotent: check for the Range global already defined.
        if (!realm.GlobalObject.Get("Range").IsUndefined)
        {
            return;
        }

        if (realm.DocumentPrototype is null)
        {
            throw new InvalidOperationException("NodeBindings.Install must run before RangeBinding.Install");
        }

        // ----- Range prototype
        var rangeProto = new JsObject(realm.ObjectPrototype);

        // §4.6 attributes
        EventTargetBinding.DefineAccessor(realm, rangeProto, "startContainer",
            (thisV, _) => UnwrapRange(thisV) is { } r
                ? JsValue.Object(DomWrappers.Wrap(realm, r.StartContainer)) : JsValue.Null);
        EventTargetBinding.DefineAccessor(realm, rangeProto, "startOffset",
            (thisV, _) => UnwrapRange(thisV) is { } r ? JsValue.Number(r.StartOffset) : JsValue.Number(0));
        EventTargetBinding.DefineAccessor(realm, rangeProto, "endContainer",
            (thisV, _) => UnwrapRange(thisV) is { } r
                ? JsValue.Object(DomWrappers.Wrap(realm, r.EndContainer)) : JsValue.Null);
        EventTargetBinding.DefineAccessor(realm, rangeProto, "endOffset",
            (thisV, _) => UnwrapRange(thisV) is { } r ? JsValue.Number(r.EndOffset) : JsValue.Number(0));
        EventTargetBinding.DefineAccessor(realm, rangeProto, "collapsed",
            (thisV, _) => UnwrapRange(thisV) is { } r ? JsValue.Boolean(r.Collapsed) : JsValue.True);
        EventTargetBinding.DefineAccessor(realm, rangeProto, "commonAncestorContainer",
            (thisV, _) => UnwrapRange(thisV) is { } r
                ? JsValue.Object(DomWrappers.Wrap(realm, r.CommonAncestorContainer)) : JsValue.Null);

        // §4.6.3 setStart / setEnd / collapse / select* methods
        EventTargetBinding.DefineMethod(realm, rangeProto, "setStart", (thisV, args) =>
        {
            var r = RequireRange(realm, thisV, "setStart");
            var node = RequireNodeArg(realm, args, 0, "setStart");
            var offset = RequireIntArg(args, 1);
            WrapDomException(realm, () => r.SetStart(node, offset));
            return JsValue.Undefined;
        }, length: 2);
        EventTargetBinding.DefineMethod(realm, rangeProto, "setEnd", (thisV, args) =>
        {
            var r = RequireRange(realm, thisV, "setEnd");
            var node = RequireNodeArg(realm, args, 0, "setEnd");
            var offset = RequireIntArg(args, 1);
            WrapDomException(realm, () => r.SetEnd(node, offset));
            return JsValue.Undefined;
        }, length: 2);
        EventTargetBinding.DefineMethod(realm, rangeProto, "setStartBefore", (thisV, args) =>
        {
            var r = RequireRange(realm, thisV, "setStartBefore");
            var node = RequireNodeArg(realm, args, 0, "setStartBefore");
            WrapDomException(realm, () => r.SetStartBefore(node));
            return JsValue.Undefined;
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, rangeProto, "setStartAfter", (thisV, args) =>
        {
            var r = RequireRange(realm, thisV, "setStartAfter");
            var node = RequireNodeArg(realm, args, 0, "setStartAfter");
            WrapDomException(realm, () => r.SetStartAfter(node));
            return JsValue.Undefined;
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, rangeProto, "setEndBefore", (thisV, args) =>
        {
            var r = RequireRange(realm, thisV, "setEndBefore");
            var node = RequireNodeArg(realm, args, 0, "setEndBefore");
            WrapDomException(realm, () => r.SetEndBefore(node));
            return JsValue.Undefined;
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, rangeProto, "setEndAfter", (thisV, args) =>
        {
            var r = RequireRange(realm, thisV, "setEndAfter");
            var node = RequireNodeArg(realm, args, 0, "setEndAfter");
            WrapDomException(realm, () => r.SetEndAfter(node));
            return JsValue.Undefined;
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, rangeProto, "collapse", (thisV, args) =>
        {
            var r = RequireRange(realm, thisV, "collapse");
            var toStart = args.Length > 0 && JsValue.ToBoolean(args[0]);
            r.Collapse(toStart);
            return JsValue.Undefined;
        }, length: 0);
        EventTargetBinding.DefineMethod(realm, rangeProto, "selectNode", (thisV, args) =>
        {
            var r = RequireRange(realm, thisV, "selectNode");
            var node = RequireNodeArg(realm, args, 0, "selectNode");
            WrapDomException(realm, () => r.SelectNode(node));
            return JsValue.Undefined;
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, rangeProto, "selectNodeContents", (thisV, args) =>
        {
            var r = RequireRange(realm, thisV, "selectNodeContents");
            var node = RequireNodeArg(realm, args, 0, "selectNodeContents");
            WrapDomException(realm, () => r.SelectNodeContents(node));
            return JsValue.Undefined;
        }, length: 1);

        // §4.6.4 comparison
        EventTargetBinding.DefineMethod(realm, rangeProto, "compareBoundaryPoints", (thisV, args) =>
        {
            var r = RequireRange(realm, thisV, "compareBoundaryPoints");
            // WebIDL: `how` is an `unsigned short` — convert via ToUint16 (mod 65536).
            var howRaw = args.Length > 0 ? JsValue.ToNumber(args[0]) : 0.0;
            var how = (int)(ushort)(double.IsNaN(howRaw) || double.IsInfinity(howRaw)
                ? 0 : (uint)(long)howRaw);
            DomRange other;
            if (args.Length < 2 || UnwrapRange(args[1]) is not { } o)
            {
                throw new JsThrow(realm.NewTypeError("compareBoundaryPoints: second argument must be a Range"));
            }

            other = o;
            return WrapDomExceptionReturn(realm, () => JsValue.Number(r.CompareBoundaryPoints(how, other)));
        }, length: 2);
        EventTargetBinding.DefineMethod(realm, rangeProto, "comparePoint", (thisV, args) =>
        {
            var r = RequireRange(realm, thisV, "comparePoint");
            var node = RequireNodeArg(realm, args, 0, "comparePoint");
            var offset = RequireIntArg(args, 1);
            return WrapDomExceptionReturn(realm, () => JsValue.Number(r.ComparePoint(node, offset)));
        }, length: 2);
        EventTargetBinding.DefineMethod(realm, rangeProto, "isPointInRange", (thisV, args) =>
        {
            var r = RequireRange(realm, thisV, "isPointInRange");
            var node = RequireNodeArg(realm, args, 0, "isPointInRange");
            var offset = RequireIntArg(args, 1);
            return WrapDomExceptionReturn(realm, () => JsValue.Boolean(r.IsPointInRange(node, offset)));
        }, length: 2);
        EventTargetBinding.DefineMethod(realm, rangeProto, "intersectsNode", (thisV, args) =>
        {
            var r = RequireRange(realm, thisV, "intersectsNode");
            var node = RequireNodeArg(realm, args, 0, "intersectsNode");
            return WrapDomExceptionReturn(realm, () => JsValue.Boolean(r.IntersectsNode(node)));
        }, length: 1);

        // §4.6.5 cloning
        EventTargetBinding.DefineMethod(realm, rangeProto, "cloneRange", (thisV, _) =>
        {
            var r = RequireRange(realm, thisV, "cloneRange");
            return JsValue.Object(WrapRange(realm, r.CloneRange()));
        }, length: 0);
        EventTargetBinding.DefineMethod(realm, rangeProto, "detach", (thisV, _) =>
        {
            // Spec: detach is a no-op. Still resolve `this` to detect bad receivers.
            UnwrapRange(thisV);
            return JsValue.Undefined;
        }, length: 0);

        // §4.6.6 stringify
        EventTargetBinding.DefineMethod(realm, rangeProto, "toString", (thisV, _) =>
        {
            var r = RequireRange(realm, thisV, "toString");
            return JsValue.String(r.Stringify());
        }, length: 0);

        // §4.6.7 mutation stubs (keep the harness from crashing)
        EventTargetBinding.DefineMethod(realm, rangeProto, "deleteContents", (thisV, _) =>
        {
            var r = RequireRange(realm, thisV, "deleteContents");
            r.DeleteContents();
            return JsValue.Undefined;
        }, length: 0);
        EventTargetBinding.DefineMethod(realm, rangeProto, "cloneContents", (thisV, _) =>
        {
            var r = RequireRange(realm, thisV, "cloneContents");
            try { return JsValue.Object(DomWrappers.Wrap(realm, r.CloneContents())); }
            catch (DomRangeException ex) { throw DomExceptionBinding.Throw(realm, ex.DomName, ex.Message); }
        }, length: 0);
        EventTargetBinding.DefineMethod(realm, rangeProto, "extractContents", (thisV, _) =>
        {
            var r = RequireRange(realm, thisV, "extractContents");
            try { return JsValue.Object(DomWrappers.Wrap(realm, r.ExtractContents())); }
            catch (DomRangeException ex) { throw DomExceptionBinding.Throw(realm, ex.DomName, ex.Message); }
        }, length: 0);
        EventTargetBinding.DefineMethod(realm, rangeProto, "insertNode", (thisV, args) =>
        {
            var r = RequireRange(realm, thisV, "insertNode");
            if (args.Length == 0 || DomWrappers.UnwrapNode(args[0]) is not { } node)
            {
                throw new JsThrow(realm.NewTypeError("insertNode: requires a Node argument"));
            }

            try { r.InsertNode(node); return JsValue.Undefined; }
            catch (DomRangeException ex) { throw DomExceptionBinding.Throw(realm, ex.DomName, ex.Message); }
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, rangeProto, "surroundContents", (thisV, args) =>
        {
            var r = RequireRange(realm, thisV, "surroundContents");
            if (args.Length == 0 || DomWrappers.UnwrapNode(args[0]) is not { } node)
            {
                throw new JsThrow(realm.NewTypeError("surroundContents: requires a Node argument"));
            }

            try { r.SurroundContents(node); return JsValue.Undefined; }
            catch (DomRangeException ex) { throw DomExceptionBinding.Throw(realm, ex.DomName, ex.Message); }
        }, length: 1);

        // ----- Range constructor (§4.6: new Range())
        // The constructor takes the document for the current realm from the global.
        var rangeCtor = new JsNativeFunction(realm, "Range", 0, (_, _) =>
        {
            var doc = GetRealmDocument(realm)
                ?? throw new JsThrow(realm.NewTypeError("Range: no document associated with realm"));
            return JsValue.Object(WrapRange(realm, new DomRange(doc)));
        }, isConstructor: true);
        rangeCtor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(rangeProto), writable: false, enumerable: false, configurable: false));
        rangeProto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(rangeCtor), writable: true, enumerable: false, configurable: true));

        // §4.6 constants on Range constructor + prototype
        foreach (var (name, value) in new[] {
            ("START_TO_START", 0), ("START_TO_END", 1), ("END_TO_END", 2), ("END_TO_START", 3) })
        {
            var v = JsValue.Number(value);
            rangeCtor.DefineOwnProperty(name, PropertyDescriptor.Data(v, writable: false, enumerable: true, configurable: false));
            rangeProto.DefineOwnProperty(name, PropertyDescriptor.Data(v, writable: false, enumerable: true, configurable: false));
        }

        realm.GlobalObject.DefineOwnProperty("Range",
            PropertyDescriptor.Data(JsValue.Object(rangeCtor), writable: true, enumerable: false, configurable: true));

        // ----- document.createRange() on Document prototype
        EventTargetBinding.DefineMethod(realm, realm.DocumentPrototype, "createRange", (thisV, _) =>
        {
            var doc = DomWrappers.UnwrapDocument(thisV)
                ?? throw new JsThrow(realm.NewTypeError("createRange: called on non-Document"));
            return JsValue.Object(WrapRange(realm, new DomRange(doc)));
        }, length: 0);
        // document.getSelection() + window.getSelection() are installed by
        // SelectionBinding.Install — keep RangeBinding focused on Range/StaticRange.

        // ----- StaticRange constructor (DOM §StaticRange) — minimal stub
        InstallStaticRange(realm, rangeProto);
    }

    // -----------------------------------------------------------------------
    // StaticRange stub

    private static void InstallStaticRange(JsRealm realm, JsObject rangeProto)
    {
        // StaticRange shares the same read-only attribute surface as Range,
        // but is backed by a plain JS object with own properties. The
        // WPT-02 scope covers only Range; StaticRange is a stub to prevent crashes.
        var staticRangeProto = new JsObject(rangeProto); // inherits Range.prototype

        // StaticRange constructor: new StaticRange({startContainer, startOffset, endContainer, endOffset})
        var staticRangeCtor = new JsNativeFunction(realm, "StaticRange", 1, (_, args) =>
        {
            var init = args.Length > 0 && args[0].IsObject ? args[0].AsObject : null;
            if (init is null)
            {
                throw new JsThrow(realm.NewTypeError("StaticRange: argument must be an object"));
            }

            var sc = DomWrappers.UnwrapNode(init.Get("startContainer"));
            var ec = DomWrappers.UnwrapNode(init.Get("endContainer"));
            if (sc is null || ec is null)
            {
                throw DomExceptionBinding.Throw(realm, "InvalidNodeTypeError",
                    "StaticRange: startContainer and endContainer must be nodes");
            }

            var so = (int)JsValue.ToNumber(init.Get("startOffset"));
            var eo = (int)JsValue.ToNumber(init.Get("endOffset"));

            // Build a plain object with the read-only attributes as own properties.
            var obj = new JsObject(staticRangeProto);
            obj.DefineOwnProperty("startContainer", PropertyDescriptor.Data(JsValue.Object(DomWrappers.Wrap(realm, sc)), writable: false, enumerable: true, configurable: false));
            obj.DefineOwnProperty("startOffset", PropertyDescriptor.Data(JsValue.Number(so), writable: false, enumerable: true, configurable: false));
            obj.DefineOwnProperty("endContainer", PropertyDescriptor.Data(JsValue.Object(DomWrappers.Wrap(realm, ec)), writable: false, enumerable: true, configurable: false));
            obj.DefineOwnProperty("endOffset", PropertyDescriptor.Data(JsValue.Number(eo), writable: false, enumerable: true, configurable: false));
            obj.DefineOwnProperty("collapsed", PropertyDescriptor.Data(
                JsValue.Boolean(ReferenceEquals(sc, ec) && so == eo), writable: false, enumerable: true, configurable: false));
            return JsValue.Object(obj);
        }, isConstructor: true);
        staticRangeCtor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(staticRangeProto), writable: false, enumerable: false, configurable: false));
        staticRangeProto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(staticRangeCtor), writable: true, enumerable: false, configurable: true));

        realm.GlobalObject.DefineOwnProperty("StaticRange",
            PropertyDescriptor.Data(JsValue.Object(staticRangeCtor), writable: true, enumerable: false, configurable: true));
    }

    // -----------------------------------------------------------------------
    // Wrapper identity

    /// <summary>Return the JS wrapper for <paramref name="range"/> in
    /// <paramref name="realm"/>, allocating on first use.</summary>
    internal static JsObject WrapRange(JsRealm realm, DomRange range)
    {
        var cache = RangeCachesPerRealm.GetValue(realm, _ => new ConditionalWeakTable<DomRange, JsObject>());
        if (cache.TryGetValue(range, out var existing))
        {
            return existing;
        }

        var proto = (JsObject?)realm.GlobalObject.Get("Range").AsObject?.Get("prototype").AsObject
            ?? realm.ObjectPrototype;
        var wrapper = new JsRangeWrapper(proto, range);
        cache.Add(range, wrapper);
        return wrapper;
    }

    /// <summary>Resolve the host <see cref="DomRange"/> from a JS wrapper,
    /// or null if <paramref name="v"/> is not a Range wrapper.</summary>
    internal static DomRange? UnwrapRange(JsValue v)
        => v.IsObject && v.AsObject is JsRangeWrapper w ? w.HostRange : null;

    // -----------------------------------------------------------------------
    // Helper utilities

    private static DomRange RequireRange(JsRealm realm, JsValue thisV, string op)
        => UnwrapRange(thisV) ?? throw new JsThrow(realm.NewTypeError($"{op}: 'this' is not a Range"));

    private static Node RequireNodeArg(JsRealm realm, JsValue[] args, int idx, string op)
        => (idx < args.Length ? DomWrappers.UnwrapNode(args[idx]) : null)
            ?? throw new JsThrow(realm.NewTypeError($"{op}: argument {idx} must be a Node"));

    private static int RequireIntArg(JsValue[] args, int idx)
    {
        if (idx >= args.Length)
        {
            return 0;
        }
        // Per WebIDL, treat as unsigned long (wraps modulo 2^32).
        var n = JsValue.ToNumber(args[idx]);
        if (double.IsNaN(n) || double.IsInfinity(n))
        {
            return 0;
        }

        return (int)(uint)(long)n;
    }

    private static void WrapDomException(JsRealm realm, Action action)
    {
        try { action(); }
        catch (DomRangeException ex) { throw DomExceptionBinding.Throw(realm, ex.DomName, ex.Message); }
    }

    private static JsValue WrapDomExceptionReturn(JsRealm realm, Func<JsValue> fn)
    {
        try { return fn(); }
        catch (DomRangeException ex) { throw DomExceptionBinding.Throw(realm, ex.DomName, ex.Message); }
    }

    /// <summary>Find the document associated with the realm (the one passed to
    /// WindowBinding.Install). We look for the <c>document</c> global property.</summary>
    private static Document? GetRealmDocument(JsRealm realm)
    {
        var docVal = realm.GlobalObject.Get("document");
        return DomWrappers.UnwrapDocument(docVal);
    }
}

/// <summary>JS wrapper that carries a host <see cref="DomRange"/> slot.
/// Identified by type-check in <see cref="RangeBinding.UnwrapRange"/>.</summary>
internal sealed class JsRangeWrapper : JsObject
{
    public DomRange HostRange { get; }
    public JsRangeWrapper(JsObject proto, DomRange range) : base(proto) { HostRange = range; }
}
