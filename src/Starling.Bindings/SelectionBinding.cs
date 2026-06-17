using System.Runtime.CompilerServices;
using Starling.Dom;
using Starling.Js.Runtime;

namespace Starling.Bindings;

/// <summary>
/// Selection API (W3C, formerly DOM §5) — installs the global
/// <c>Selection</c> constructor + prototype and wires
/// <c>window.getSelection()</c> / <c>document.getSelection()</c> to return the
/// realm's single per-document Selection. The Selection holds a live Range
/// (modern spec: at most one range; <c>addRange</c> is a no-op when one already
/// exists). Boundary points and direction are derived from that range, so
/// mutating the Range from JS automatically reflects in <c>anchorNode</c> /
/// <c>focusOffset</c> / etc.
/// </summary>
public static class SelectionBinding
{
    // Per-realm Selection prototype, used to assemble wrappers and to answer
    // `instanceof Selection`.
    private static readonly ConditionalWeakTable<JsRealm, JsObject> SelProtoPerRealm = new();
    // Per-realm cache of (Document → JsSelectionWrapper) so the same Selection
    // wrapper is returned every call. The host DomSelection is also stable —
    // we hang it off the Document via DocumentSelectionTable.
    private static readonly ConditionalWeakTable<JsRealm, ConditionalWeakTable<Document, JsObject>> SelWrappersPerRealm = new();
    // Per-Document host Selection (shared across realms — there's only one).
    private static readonly ConditionalWeakTable<Document, DomSelection> DocumentSelectionTable = new();

    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        if (!realm.GlobalObject.Get("Selection").IsUndefined)
        {
            return;
        }

        if (realm.DocumentPrototype is null)
        {
            throw new InvalidOperationException("NodeBindings.Install must run before SelectionBinding.Install");
        }

        var selProto = new JsObject(realm.ObjectPrototype);
        SelProtoPerRealm.Add(realm, selProto);

        // ----- attributes
        EventTargetBinding.DefineAccessor(realm, selProto, "anchorNode", (thisV, _) =>
            Sel(thisV) is { } s && s.AnchorNode is { } n ? JsValue.Object(DomWrappers.Wrap(realm, n)) : JsValue.Null);
        EventTargetBinding.DefineAccessor(realm, selProto, "anchorOffset", (thisV, _) =>
            Sel(thisV) is { } s ? JsValue.Number(s.AnchorOffset) : JsValue.Number(0));
        EventTargetBinding.DefineAccessor(realm, selProto, "focusNode", (thisV, _) =>
            Sel(thisV) is { } s && s.FocusNode is { } n ? JsValue.Object(DomWrappers.Wrap(realm, n)) : JsValue.Null);
        EventTargetBinding.DefineAccessor(realm, selProto, "focusOffset", (thisV, _) =>
            Sel(thisV) is { } s ? JsValue.Number(s.FocusOffset) : JsValue.Number(0));
        EventTargetBinding.DefineAccessor(realm, selProto, "isCollapsed", (thisV, _) =>
            Sel(thisV) is { } s ? JsValue.Boolean(s.IsCollapsed) : JsValue.True);
        EventTargetBinding.DefineAccessor(realm, selProto, "rangeCount", (thisV, _) =>
            Sel(thisV) is { } s ? JsValue.Number(s.RangeCount) : JsValue.Number(0));
        EventTargetBinding.DefineAccessor(realm, selProto, "type", (thisV, _) =>
            Sel(thisV) is { } s ? JsValue.String(s.Type) : JsValue.String("None"));
        EventTargetBinding.DefineAccessor(realm, selProto, "direction", (thisV, _) =>
            JsValue.String(Sel(thisV) is { } s ? s.Direction switch
            {
                SelectionDirection.Forwards => "forward",
                SelectionDirection.Backwards => "backward",
                _ => "none",
            } : "none"));

        // ----- methods
        EventTargetBinding.DefineMethod(realm, selProto, "getRangeAt", (thisV, args) =>
        {
            var s = RequireSel(realm, thisV, "getRangeAt");
            var idx = args.Length > 0 ? (int)JsValue.ToNumber(args[0]) : 0;
            DomRange r = WrapDomCall(realm, () => s.GetRangeAt(idx));
            return JsValue.Object(RangeBinding.WrapRange(realm, r));
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, selProto, "addRange", (thisV, args) =>
        {
            var s = RequireSel(realm, thisV, "addRange");
            if (args.Length < 1 || RangeBinding.UnwrapRange(args[0]) is not { } r)
            {
                throw new JsThrow(realm.NewTypeError("addRange: argument must be a Range"));
            }

            s.AddRange(r);
            return JsValue.Undefined;
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, selProto, "removeRange", (thisV, args) =>
        {
            var s = RequireSel(realm, thisV, "removeRange");
            if (args.Length < 1 || RangeBinding.UnwrapRange(args[0]) is not { } r)
            {
                throw new JsThrow(realm.NewTypeError("removeRange: argument must be a Range"));
            }

            WrapDomCall(realm, () => { s.RemoveRange(r); return 0; });
            return JsValue.Undefined;
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, selProto, "removeAllRanges", (thisV, _) =>
        {
            RequireSel(realm, thisV, "removeAllRanges").RemoveAllRanges();
            return JsValue.Undefined;
        }, length: 0);
        EventTargetBinding.DefineMethod(realm, selProto, "empty", (thisV, _) =>
        {
            RequireSel(realm, thisV, "empty").RemoveAllRanges();
            return JsValue.Undefined;
        }, length: 0);
        EventTargetBinding.DefineMethod(realm, selProto, "collapse", (thisV, args) =>
        {
            var s = RequireSel(realm, thisV, "collapse");
            // Spec: if node is null, removeAllRanges.
            if (args.Length == 0)
            {
                throw new JsThrow(realm.NewTypeError("collapse: requires a Node or null"));
            }

            Node? node = args[0].IsNull ? null : DomWrappers.UnwrapNode(args[0]);
            if (!args[0].IsNull && node is null)
            {
                throw new JsThrow(realm.NewTypeError("collapse: argument 0 must be a Node or null"));
            }

            var offset = args.Length > 1 ? (int)JsValue.ToNumber(args[1]) : 0;
            WrapDomCall(realm, () => { s.Collapse(node, offset); return 0; });
            return JsValue.Undefined;
        }, length: 1);
        // setPosition is an alias for collapse per spec.
        EventTargetBinding.DefineMethod(realm, selProto, "setPosition", (thisV, args) =>
        {
            var s = RequireSel(realm, thisV, "setPosition");
            if (args.Length == 0)
            {
                throw new JsThrow(realm.NewTypeError("setPosition: requires a Node or null"));
            }

            Node? node = args[0].IsNull ? null : DomWrappers.UnwrapNode(args[0]);
            if (!args[0].IsNull && node is null)
            {
                throw new JsThrow(realm.NewTypeError("setPosition: argument 0 must be a Node or null"));
            }

            var offset = args.Length > 1 ? (int)JsValue.ToNumber(args[1]) : 0;
            WrapDomCall(realm, () => { s.Collapse(node, offset); return 0; });
            return JsValue.Undefined;
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, selProto, "collapseToStart", (thisV, _) =>
        {
            var s = RequireSel(realm, thisV, "collapseToStart");
            WrapDomCall(realm, () => { s.CollapseToStart(); return 0; });
            return JsValue.Undefined;
        }, length: 0);
        EventTargetBinding.DefineMethod(realm, selProto, "collapseToEnd", (thisV, _) =>
        {
            var s = RequireSel(realm, thisV, "collapseToEnd");
            WrapDomCall(realm, () => { s.CollapseToEnd(); return 0; });
            return JsValue.Undefined;
        }, length: 0);
        EventTargetBinding.DefineMethod(realm, selProto, "extend", (thisV, args) =>
        {
            var s = RequireSel(realm, thisV, "extend");
            if (args.Length < 1 || DomWrappers.UnwrapNode(args[0]) is not { } node)
            {
                throw new JsThrow(realm.NewTypeError("extend: argument 0 must be a Node"));
            }

            var offset = args.Length > 1 ? (int)JsValue.ToNumber(args[1]) : 0;
            WrapDomCall(realm, () => { s.Extend(node, offset); return 0; });
            return JsValue.Undefined;
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, selProto, "setBaseAndExtent", (thisV, args) =>
        {
            var s = RequireSel(realm, thisV, "setBaseAndExtent");
            if (args.Length < 4)
            {
                throw new JsThrow(realm.NewTypeError("setBaseAndExtent: requires 4 arguments"));
            }

            var aNode = DomWrappers.UnwrapNode(args[0]) ?? throw new JsThrow(realm.NewTypeError("setBaseAndExtent: anchorNode must be a Node"));
            var aOff = (int)JsValue.ToNumber(args[1]);
            var fNode = DomWrappers.UnwrapNode(args[2]) ?? throw new JsThrow(realm.NewTypeError("setBaseAndExtent: focusNode must be a Node"));
            var fOff = (int)JsValue.ToNumber(args[3]);
            WrapDomCall(realm, () => { s.SetBaseAndExtent(aNode, aOff, fNode, fOff); return 0; });
            return JsValue.Undefined;
        }, length: 4);
        EventTargetBinding.DefineMethod(realm, selProto, "selectAllChildren", (thisV, args) =>
        {
            var s = RequireSel(realm, thisV, "selectAllChildren");
            if (args.Length < 1 || DomWrappers.UnwrapNode(args[0]) is not { } node)
            {
                throw new JsThrow(realm.NewTypeError("selectAllChildren: argument must be a Node"));
            }

            WrapDomCall(realm, () => { s.SelectAllChildren(node); return 0; });
            return JsValue.Undefined;
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, selProto, "containsNode", (thisV, args) =>
        {
            var s = RequireSel(realm, thisV, "containsNode");
            if (args.Length < 1 || DomWrappers.UnwrapNode(args[0]) is not { } node)
            {
                throw new JsThrow(realm.NewTypeError("containsNode: argument 0 must be a Node"));
            }

            var allowPartial = args.Length > 1 && JsValue.ToBoolean(args[1]);
            return JsValue.Boolean(s.ContainsNode(node, allowPartial));
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, selProto, "deleteFromDocument", (thisV, _) =>
        {
            RequireSel(realm, thisV, "deleteFromDocument").DeleteFromDocument();
            return JsValue.Undefined;
        }, length: 0);
        EventTargetBinding.DefineMethod(realm, selProto, "toString", (thisV, _) =>
        {
            var s = Sel(thisV);
            return JsValue.String(s?.Stringify() ?? string.Empty);
        }, length: 0);
        // modify(alter, direction, granularity) is non-standard; expose as no-op
        // so tests that probe its existence don't trip.
        EventTargetBinding.DefineMethod(realm, selProto, "modify", (_, _) => JsValue.Undefined, length: 3);
        // getComposedRanges is from the proposed Shadow DOM extension — return
        // an empty array (we have no shadow-DOM-aware ranges yet).
        EventTargetBinding.DefineMethod(realm, selProto, "getComposedRanges", (_, _) => JsValue.Object(new JsArray(realm, Array.Empty<JsValue>())), length: 0);

        // ----- Selection constructor (not user-constructible per spec)
        var selCtor = new JsNativeFunction(realm, "Selection", 0, (_, _) =>
            throw new JsThrow(realm.NewTypeError("Selection is not user-constructible")), isConstructor: true);
        selCtor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(selProto), writable: false, enumerable: false, configurable: false));
        selProto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(selCtor), writable: true, enumerable: false, configurable: true));
        realm.GlobalObject.DefineOwnProperty("Selection",
            PropertyDescriptor.Data(JsValue.Object(selCtor), writable: true, enumerable: false, configurable: true));

        // ----- window.getSelection() — returns the realm document's Selection.
        EventTargetBinding.DefineMethod(realm, realm.GlobalObject, "getSelection", (_, _) =>
        {
            var doc = GetRealmDocument(realm);
            if (doc is null)
            {
                return JsValue.Null;
            }

            return JsValue.Object(WrapSelection(realm, doc));
        }, length: 0);

        // ----- document.getSelection() — must return same instance as window.getSelection
        // for the realm's document. For Documents without a defaultView (e.g.
        // createHTMLDocument/createDocument results), it must return null.
        EventTargetBinding.DefineMethod(realm, realm.DocumentPrototype, "getSelection", (thisV, _) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is not { } doc)
            {
                return JsValue.Null;
            }
            // Spec: getSelection returns null when the document has no defaultView.
            if (!IsRealmDocument(realm, doc))
            {
                return JsValue.Null;
            }

            return JsValue.Object(WrapSelection(realm, doc));
        }, length: 0);
    }

    /// <summary>Return the JS wrapper for the Selection associated with
    /// <paramref name="document"/> in <paramref name="realm"/>. The host
    /// <see cref="DomSelection"/> is shared across realms (one per Document);
    /// the wrapper is per-realm so prototype identity is correct.</summary>
    internal static JsObject WrapSelection(JsRealm realm, Document document)
    {
        var cache = SelWrappersPerRealm.GetValue(realm, _ => new ConditionalWeakTable<Document, JsObject>());
        if (cache.TryGetValue(document, out var existing))
        {
            return existing;
        }

        var host = DocumentSelectionTable.GetValue(document, d => new DomSelection(d));
        var proto = SelProtoPerRealm.TryGetValue(realm, out var p) ? p : realm.ObjectPrototype;
        var wrapper = new JsSelectionWrapper(proto, host);
        cache.Add(document, wrapper);
        return wrapper;
    }

    internal static DomSelection? Sel(JsValue v)
        => v.IsObject && v.AsObject is JsSelectionWrapper w ? w.Host : null;

    private static DomSelection RequireSel(JsRealm realm, JsValue thisV, string op)
        => Sel(thisV) ?? throw new JsThrow(realm.NewTypeError($"{op}: 'this' is not a Selection"));

    private static T WrapDomCall<T>(JsRealm realm, Func<T> fn)
    {
        try { return fn(); }
        catch (DomRangeException ex) { throw DomExceptionBinding.Throw(realm, ex.DomName, ex.Message); }
    }

    private static void WrapDomCall(JsRealm realm, Action fn)
    {
        try { fn(); }
        catch (DomRangeException ex) { throw DomExceptionBinding.Throw(realm, ex.DomName, ex.Message); }
    }

    private static Document? GetRealmDocument(JsRealm realm)
        => DomWrappers.UnwrapDocument(realm.GlobalObject.Get("document"));

    internal static bool IsRealmDocument(JsRealm realm, Document doc)
        => ReferenceEquals(GetRealmDocument(realm), doc);
}

/// <summary>JS wrapper carrying a host <see cref="DomSelection"/> slot.</summary>
internal sealed class JsSelectionWrapper : JsObject
{
    public DomSelection Host { get; }
    public JsSelectionWrapper(JsObject proto, DomSelection host) : base(proto) { Host = host; }
}
