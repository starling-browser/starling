using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;

namespace Starling.Bindings.Jint;

/// <summary>
/// Installs the JS-visible <c>DOMException</c> (DOM §4.4) on the Jint backend —
/// the error type DOM methods throw and that <c>assert_throws_dom</c> checks.
/// Mirrors <c>Starling.Bindings/DomExceptionBinding.cs</c>: an instance carries
/// own <c>name</c>/<c>message</c>; the prototype computes the legacy <c>code</c>
/// from the name and exposes the legacy numeric constants (also on the
/// constructor). Exposed as a global. The <see cref="Make"/>/<see cref="Throw"/>
/// helpers let other binding families produce a real DOMException value instead of
/// falling back to a plain <c>TypeError</c>.
/// </summary>
internal static class DomExceptionBinding
{
    // DOM §4.4 "Error names" → legacy code. Names not listed have code 0.
    private static readonly (string Name, int Code)[] NameCodes =
    {
        ("IndexSizeError", 1), ("HierarchyRequestError", 3), ("WrongDocumentError", 4),
        ("InvalidCharacterError", 5), ("NoModificationAllowedError", 7), ("NotFoundError", 8),
        ("NotSupportedError", 9), ("InUseAttributeError", 10), ("InvalidStateError", 11),
        ("SyntaxError", 12), ("InvalidModificationError", 13), ("NamespaceError", 14),
        ("InvalidAccessError", 15), ("SecurityError", 18), ("NetworkError", 19),
        ("AbortError", 20), ("URLMismatchError", 21), ("QuotaExceededError", 22),
        ("TimeoutError", 23), ("InvalidNodeTypeError", 24), ("DataCloneError", 25),
    };

    // Legacy code constants (incl. ones with no modern name) — on ctor + prototype.
    private static readonly (string Const, int Val)[] Constants =
    {
        ("INDEX_SIZE_ERR", 1), ("DOMSTRING_SIZE_ERR", 2), ("HIERARCHY_REQUEST_ERR", 3),
        ("WRONG_DOCUMENT_ERR", 4), ("INVALID_CHARACTER_ERR", 5), ("NO_DATA_ALLOWED_ERR", 6),
        ("NO_MODIFICATION_ALLOWED_ERR", 7), ("NOT_FOUND_ERR", 8), ("NOT_SUPPORTED_ERR", 9),
        ("INUSE_ATTRIBUTE_ERR", 10), ("INVALID_STATE_ERR", 11), ("SYNTAX_ERR", 12),
        ("INVALID_MODIFICATION_ERR", 13), ("NAMESPACE_ERR", 14), ("INVALID_ACCESS_ERR", 15),
        ("VALIDATION_ERR", 16), ("TYPE_MISMATCH_ERR", 17), ("SECURITY_ERR", 18),
        ("NETWORK_ERR", 19), ("ABORT_ERR", 20), ("URL_MISMATCH_ERR", 21),
        ("QUOTA_EXCEEDED_ERR", 22), ("TIMEOUT_ERR", 23), ("INVALID_NODE_TYPE_ERR", 24),
        ("DATA_CLONE_ERR", 25),
    };

    public static void Install(JintBackendContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (ctx.Wrappers.DomExceptionPrototype is not null)
        {
            return; // idempotent
        }

        var engine = ctx.Engine;

        var proto = new JsObject(engine);
        ctx.Wrappers.DomExceptionPrototype = proto;

        JintInterop.DefineAccessor(engine, proto, "code", (thisV, _) =>
        {
            var n = thisV is ObjectInstance o ? o.Get("name") : JsValue.Undefined;
            var name = n.IsString() ? n.AsString() : "";
            foreach (var (cn, code) in NameCodes)
            {
                if (cn == name)
                {
                    return JintInterop.Num(code);
                }
            }

            return JintInterop.Num(0);
        });
        JintInterop.DefineMethod(engine, proto, "toString", (thisV, _) =>
        {
            if (thisV is not ObjectInstance o)
            {
                return JintInterop.Str("Error");
            }

            var name = o.Get("name");
            var msg = o.Get("message");
            var ns = name.IsString() ? name.AsString() : "Error";
            var ms = msg.IsString() ? msg.AsString() : "";
            return JintInterop.Str(ms.Length > 0 ? ns + ": " + ms : ns);
        }, length: 0);
        foreach (var (c, v) in Constants)
        {
            proto.FastSetProperty(c, new PropertyDescriptor(JintInterop.Num(v), writable: false, enumerable: true, configurable: false));
        }

        var ctor = new NativeConstructor(engine, "DOMException", 0, (args, _) =>
        {
            var message = args.Length > 0 && !args[0].IsUndefined() ? TypeConverter.ToString(args[0]) : "";
            var name = args.Length > 1 && !args[1].IsUndefined() ? TypeConverter.ToString(args[1]) : "Error";
            return MakeInstance(ctx, name, message);
        });
        ctor.DefineOwnProperty("prototype",
            new PropertyDescriptor(proto, writable: false, enumerable: false, configurable: false));
        proto.FastSetProperty("constructor",
            new PropertyDescriptor(ctor, writable: true, enumerable: false, configurable: true));
        foreach (var (c, v) in Constants)
        {
            ctor.FastSetProperty(c, new PropertyDescriptor(JintInterop.Num(v), writable: false, enumerable: true, configurable: false));
        }

        JintInterop.DefineDataProp(engine.Global, "DOMException", ctor,
            writable: true, enumerable: false, configurable: true);
    }

    private static JsObject MakeInstance(JintBackendContext ctx, string name, string message)
    {
        var o = new JsObject(ctx.Engine) { Prototype = ctx.Wrappers.DomExceptionPrototype };
        o.FastSetProperty("name", new PropertyDescriptor(JintInterop.Str(name), writable: true, enumerable: false, configurable: true));
        o.FastSetProperty("message", new PropertyDescriptor(JintInterop.Str(message), writable: true, enumerable: false, configurable: true));
        return o;
    }

    /// <summary>Build a real DOMException value (not thrown) with the given DOM
    /// error name (§4.4) — for bindings that need a DOMException as a value (e.g.
    /// AbortSignal.reason). Falls back to a plain object prototype if install has
    /// not run yet (bare contexts).</summary>
    public static ObjectInstance Make(JintBackendContext ctx, string name, string message = "")
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return MakeInstance(ctx, name, message);
    }

    /// <summary>Build a <see cref="JavaScriptException"/> carrying a DOMException
    /// with the given DOM error name (§4.4) — for bindings to throw.</summary>
    public static JavaScriptException Throw(JintBackendContext ctx, string name, string message = "")
        => new(Make(ctx, name, message));
}
