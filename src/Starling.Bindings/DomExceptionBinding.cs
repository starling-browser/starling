using Starling.Js.Runtime;

namespace Starling.Bindings;

/// <summary>
/// Installs the JS-visible <c>DOMException</c> (DOM §4.4) — the error type DOM
/// methods throw and that <c>assert_throws_dom</c> checks. An instance carries
/// own <c>name</c>/<c>message</c>; the prototype computes the legacy <c>code</c>
/// from the name and exposes the legacy numeric constants (also on the
/// constructor). Exposed as a global and on the window.
/// </summary>
public static class DomExceptionBinding
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

    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        if (realm.DomExceptionPrototype is not null)
        {
            return; // idempotent
        }

        var proto = new JsObject(realm.ObjectPrototype);
        realm.DomExceptionPrototype = proto;

        EventTargetBinding.DefineAccessor(realm, proto, "code", (thisV, _) =>
        {
            var n = thisV.IsObject ? thisV.AsObject.Get("name") : JsValue.Undefined;
            var name = n.IsString ? n.AsString : "";
            foreach (var (cn, code) in NameCodes)
            {
                if (cn == name)
                {
                    return JsValue.Number(code);
                }
            }

            return JsValue.Number(0);
        });
        EventTargetBinding.DefineMethod(realm, proto, "toString", (thisV, _) =>
        {
            if (!thisV.IsObject)
            {
                return JsValue.String("Error");
            }

            var name = thisV.AsObject.Get("name");
            var msg = thisV.AsObject.Get("message");
            var ns = name.IsString ? name.AsString : "Error";
            var ms = msg.IsString ? msg.AsString : "";
            return JsValue.String(ms.Length > 0 ? ns + ": " + ms : ns);
        }, length: 0);
        foreach (var (c, v) in Constants)
        {
            proto.DefineOwnProperty(c, PropertyDescriptor.Data(JsValue.Number(v), writable: false, enumerable: true, configurable: false));
        }

        var ctor = new JsNativeFunction(realm, "DOMException", 0, (_, args) =>
        {
            var message = args.Length > 0 && !args[0].IsUndefined ? JsValue.ToStringValue(args[0]) : "";
            var name = args.Length > 1 && !args[1].IsUndefined ? JsValue.ToStringValue(args[1]) : "Error";
            return JsValue.Object(MakeInstance(realm, name, message));
        }, isConstructor: true);
        ctor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(proto), writable: false, enumerable: false, configurable: false));
        proto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
        foreach (var (c, v) in Constants)
        {
            ctor.DefineOwnProperty(c, PropertyDescriptor.Data(JsValue.Number(v), writable: false, enumerable: true, configurable: false));
        }

        realm.GlobalObject.DefineOwnProperty("DOMException",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
    }

    private static JsObject MakeInstance(JsRealm realm, string name, string message)
    {
        var o = new JsObject(realm.DomExceptionPrototype!);
        o.DefineOwnProperty("name", PropertyDescriptor.Data(JsValue.String(name), writable: true, enumerable: false, configurable: true));
        o.DefineOwnProperty("message", PropertyDescriptor.Data(JsValue.String(message), writable: true, enumerable: false, configurable: true));
        return o;
    }

    /// <summary>Build a <see cref="JsThrow"/> carrying a DOMException with the
    /// given DOM error name (§4.4) — for bindings to throw.</summary>
    public static JsThrow Throw(JsRealm realm, string name, string message = "")
        => new(JsValue.Object(MakeInstance(realm, name, message)));

    /// <summary>Build a real DOMException value (not thrown) with the given DOM
    /// error name, so its <c>constructor</c> is the realm's DOMException — for
    /// bindings that need a DOMException as a value (e.g. AbortSignal.reason).</summary>
    public static JsValue Make(JsRealm realm, string name, string message = "")
        => JsValue.Object(MakeInstance(realm, name, message));
}
