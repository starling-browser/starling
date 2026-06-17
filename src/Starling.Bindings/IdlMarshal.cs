using Starling.Dom;
using Starling.Dom.Events;
using Starling.Js.Runtime;

namespace Starling.Bindings;

/// <summary>
/// Reusable marshalling for the generated bindings — the analogue of Chromium's
/// NativeValueTraits (arguments) and ToV8Traits (return values). The generated
/// binding body stays thin: it converts arguments and the receiver through these
/// helpers, dispatches to the Starling DOM impl method, wraps the return, and
/// translates a <see cref="DomException"/> to a JS DOMException. No spec logic
/// lives in the generated body or here — it lives in the DOM impl.
/// </summary>
internal static class IdlMarshal
{
    /// <summary>Unwrap the receiver to <typeparamref name="T"/>, or throw a JS
    /// TypeError (illegal invocation).</summary>
    public static T Receiver<T>(JsRealm realm, JsValue thisV, string iface, string op) where T : class
    {
        if (DomWrappers.UnwrapAs<T>(thisV) is { } host)
        {
            return host;
        }

        throw new JsThrow(realm.NewTypeError(
            $"Failed to execute '{op}' on '{iface}': Illegal invocation"));
    }

    /// <summary>Convert a required interface argument to a non-null
    /// <typeparamref name="T"/>, or throw a JS TypeError.</summary>
    public static T RequireInterface<T>(JsRealm realm, JsValue[] args, int index, string op, string typeName, int requiredCount)
        where T : class
    {
        if (args.Length < requiredCount)
        {
            throw new JsThrow(realm.NewTypeError(
                $"Failed to execute '{op}': {requiredCount} argument{(requiredCount == 1 ? "" : "s")} required, but only {args.Length} present."));
        }

        if (index < args.Length && DomWrappers.UnwrapAs<T>(args[index]) is { } value)
        {
            return value;
        }

        throw new JsThrow(realm.NewTypeError(
            $"Failed to execute '{op}': parameter {index + 1} is not of type '{typeName}'."));
    }

    /// <summary>Convert a nullable interface argument: a wrapped
    /// <typeparamref name="T"/>, or null when the argument is null/undefined, or a
    /// JS TypeError when it is some other non-null value.</summary>
    public static T? NullableInterface<T>(JsRealm realm, JsValue[] args, int index, string op, string typeName)
        where T : class
    {
        if (index >= args.Length || args[index].IsNull || args[index].IsUndefined)
        {
            return null;
        }

        if (DomWrappers.UnwrapAs<T>(args[index]) is { } value)
        {
            return value;
        }

        throw new JsThrow(realm.NewTypeError(
            $"Failed to execute '{op}': parameter {index + 1} is not of type '{typeName}'."));
    }

    /// <summary>Wrap a DOM object as a JS value, preserving wrapper identity, or
    /// null.</summary>
    public static JsValue Wrap(JsRealm realm, EventTarget? target) =>
        target is { } t ? JsValue.Object(DomWrappers.Wrap(realm, t)) : JsValue.Null;

    /// <summary>Translate a DOM-layer exception to a thrown JS DOMException.</summary>
    public static JsThrow Translate(JsRealm realm, DomException ex) =>
        DomExceptionBinding.Throw(realm, ex.Name, ex.Message);

    /// <summary>Convert a required string argument via the spec ToString.</summary>
    public static string RequireString(JsRealm realm, JsValue[] args, int index, string op, int requiredCount)
    {
        if (args.Length < requiredCount)
        {
            throw new JsThrow(realm.NewTypeError(
                $"Failed to execute '{op}': {requiredCount} argument{(requiredCount == 1 ? "" : "s")} required, but only {args.Length} present."));
        }

        return JsValue.ToStringValue(index < args.Length ? args[index] : JsValue.Undefined);
    }

    /// <summary>Convert a required nullable <c>DOMString?</c> argument: the
    /// argument must be present, but a null or undefined value becomes a C# null
    /// rather than the string "null". Everything else goes through the spec
    /// ToString.</summary>
    public static string? RequireNullableString(JsRealm realm, JsValue[] args, int index, string op, int requiredCount)
    {
        if (args.Length < requiredCount)
        {
            throw new JsThrow(realm.NewTypeError(
                $"Failed to execute '{op}': {requiredCount} argument{(requiredCount == 1 ? "" : "s")} required, but only {args.Length} present."));
        }

        if (index >= args.Length || args[index].IsNullish)
        {
            return null;
        }

        return JsValue.ToStringValue(args[index]);
    }

    /// <summary>Convert a required boolean argument via the spec ToBoolean.</summary>
    public static bool RequireBool(JsRealm realm, JsValue[] args, int index, string op, int requiredCount)
    {
        if (args.Length < requiredCount)
        {
            throw new JsThrow(realm.NewTypeError(
                $"Failed to execute '{op}': {requiredCount} argument{(requiredCount == 1 ? "" : "s")} required, but only {args.Length} present."));
        }

        return JsValue.ToBoolean(index < args.Length ? args[index] : JsValue.Undefined);
    }

    /// <summary>Convert a required <c>unsigned long</c> argument via the Web IDL
    /// integer conversion (ToUint32: ToNumber, then truncate toward zero modulo
    /// 2^32, with NaN and the infinities mapping to 0).</summary>
    public static uint RequireUnsignedLong(JsRealm realm, JsValue[] args, int index, string op, int requiredCount)
    {
        if (args.Length < requiredCount)
        {
            throw new JsThrow(realm.NewTypeError(
                $"Failed to execute '{op}': {requiredCount} argument{(requiredCount == 1 ? "" : "s")} required, but only {args.Length} present."));
        }

        return ToUint32(realm, index < args.Length ? args[index] : JsValue.Undefined, op);
    }

    // Web IDL ToUint32. Symbol and BigInt have no Number conversion and raise a
    // TypeError; everything else goes through ToNumber.
    private static uint ToUint32(JsRealm realm, JsValue v, string op)
    {
        if (v.IsSymbol || v.IsBigInt)
        {
            throw new JsThrow(realm.NewTypeError(
                $"Failed to execute '{op}': Cannot convert a {(v.IsSymbol ? "Symbol" : "BigInt")} value to a number."));
        }

        double n = JsValue.ToNumber(v);
        if (double.IsNaN(n) || double.IsInfinity(n))
        {
            return 0;
        }

        double mod = System.Math.Truncate(n) % 4294967296.0;   // 2^32
        if (mod < 0)
        {
            mod += 4294967296.0;
        }

        return (uint)mod;
    }

    /// <summary>Wrap a nullable string return as a JS string or null.</summary>
    public static JsValue WrapString(string? value) =>
        value is { } v ? JsValue.String(v) : JsValue.Null;

    /// <summary>Wrap a boolean return.</summary>
    public static JsValue WrapBool(bool value) => JsValue.Boolean(value);

    /// <summary>Wrap a number return.</summary>
    public static JsValue WrapNumber(double value) => JsValue.Number(value);

    /// <summary>Wrap a nullable boolean return.</summary>
    public static JsValue WrapNullableBool(bool? value) =>
        value is { } v ? JsValue.Boolean(v) : JsValue.Null;

    /// <summary>Wrap a nullable number return.</summary>
    public static JsValue WrapNullableNumber(double? value) =>
        value is { } v ? JsValue.Number(v) : JsValue.Null;

    /// <summary>Return Web IDL undefined for void operations.</summary>
    public static JsValue Void() => JsValue.Undefined;

    /// <summary>Wrap a node sequence as a static NodeList (a snapshot).</summary>
    public static JsValue WrapNodeList(JsRealm realm, IEnumerable<Node> nodes)
    {
        var snapshot = nodes.Cast<Node>().ToList();
        return NodeBindings.BuildNodeList(realm, () => snapshot);
    }

    /// <summary>Wrap a node sequence as a real JS Array of wrapped nodes. Starling
    /// returns these from querySelectorAll for .map/.filter ergonomics.</summary>
    public static JsValue WrapNodeArray(JsRealm realm, IEnumerable<Node> nodes)
    {
        var items = nodes.Select(n => JsValue.Object(DomWrappers.Wrap(realm, n))).Cast<JsValue>().ToList();
        return NodeBindings.MakeArray(realm, items);
    }

    /// <summary>Wrap a live element source as an HTMLCollection (re-evaluated on
    /// access).</summary>
    public static JsValue WrapHtmlCollection(JsRealm realm, System.Func<IEnumerable<Element>> source) =>
        NodeBindings.BuildHtmlCollection(realm, () => source().ToList());

    /// <summary>Wrap a string sequence as a real JS Array of strings.</summary>
    public static JsValue WrapStringArray(JsRealm realm, IEnumerable<string> strings)
    {
        var items = strings.Select(JsValue.String).Cast<JsValue>().ToList();
        return NodeBindings.MakeArray(realm, items);
    }

    /// <summary>An optional boolean argument: null when absent or undefined.</summary>
    public static bool? NullableBool(JsValue[] args, int index) =>
        index >= args.Length || args[index].IsUndefined ? null : JsValue.ToBoolean(args[index]);

    /// <summary>A nullable string argument: null when null or undefined, else the
    /// spec ToString. Used for nullable DOMString? args like NS namespaces.</summary>
    public static string? NullableString(JsValue[] args, int index) =>
        index >= args.Length || args[index].IsNullish ? null : JsValue.ToStringValue(args[index]);

    /// <summary>Coerce a variadic (Node or DOMString)... argument list to nodes:
    /// a wrapped node stays a node, a string becomes a Text node in the context's
    /// document. Used by the ParentNode / ChildNode methods (append, before, ...).</summary>
    public static List<Node> NodeOrStringArgs(JsRealm realm, Node context, JsValue[] args, int start)
    {
        var doc = context.OwnerDocument ?? context as Document ?? new Document();
        var nodes = new List<Node>(System.Math.Max(0, args.Length - start));
        for (int i = start; i < args.Length; i++)
        {
            nodes.Add(DomWrappers.UnwrapNode(args[i]) is { } n ? n : doc.CreateTextNode(JsValue.ToStringValue(args[i])));
        }

        return nodes;
    }
}
