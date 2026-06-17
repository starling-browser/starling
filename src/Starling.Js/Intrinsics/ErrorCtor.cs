using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>
/// §20.5 The Error intrinsic family — installs the eight Error constructors
/// (<c>Error</c>, <c>TypeError</c>, <c>RangeError</c>, <c>ReferenceError</c>,
/// <c>SyntaxError</c>, <c>URIError</c>, <c>EvalError</c>,
/// <c>AggregateError</c>) on the realm's global object and populates the
/// corresponding prototype slots with <c>name</c>, <c>message</c>, and
/// <c>toString</c>. The realm pre-wires the prototype chain
/// (TypeError.prototype → Error.prototype → Object.prototype, etc.); this
/// install only fills in own properties + the constructor back-references.
/// </summary>
/// <remarks>
/// <para>
/// Error instances carry a <c>message</c> own property when the constructor is
/// invoked with a defined first argument. The §20.5.1.1 options-bag
/// <c>cause</c> is honored: <c>new Error("m", { cause: x }).cause === x</c>.
/// </para>
/// <para>
/// <c>AggregateError</c>'s <c>errors</c> argument is currently accepted as an
/// array-like object with an own <c>length</c> and integer-indexed slots. It
/// does not consume general iterables yet.
/// </para>
/// <para>
/// Error objects do not capture <c>stack</c> yet. The prototype methods and
/// descriptor flags below are independent of stack capture.
/// </para>
/// </remarks>
public static class ErrorCtor
{
    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);

        // Install Error itself first so subclass constructors can use it as
        // their [[Prototype]] (matches `class TypeError extends Error`).
        var errorCtor = InstallError(realm);

        InstallSubclass(realm, "TypeError", realm.TypeErrorPrototype, errorCtor);
        InstallSubclass(realm, "RangeError", realm.RangeErrorPrototype, errorCtor);
        InstallSubclass(realm, "ReferenceError", realm.ReferenceErrorPrototype, errorCtor);
        InstallSubclass(realm, "SyntaxError", realm.SyntaxErrorPrototype, errorCtor);
        InstallSubclass(realm, "URIError", realm.UriErrorPrototype, errorCtor);
        InstallSubclass(realm, "EvalError", realm.EvalErrorPrototype, errorCtor);
        InstallAggregateError(realm, errorCtor);
    }

    // ====================================================================
    //                          Per-constructor install
    // ====================================================================

    /// <summary>§20.5.1 The Error constructor — root of the family. Returns a
    /// fresh ordinary object whose [[Prototype]] is <c>Error.prototype</c>.</summary>
    private static JsNativeFunction InstallError(JsRealm realm)
    {
        var proto = realm.ErrorPrototype;
        var ctor = new JsNativeFunction("Error", (newTarget, args) =>
        {
            // §20.5.1.1 / OrdinaryCreateFromConstructor: when constructed via a
            // derived class (`class E extends Error {}`), super() threads E as
            // new.target so the instance's [[Prototype]] is E.prototype.
            var instProto = IntrinsicHelpers.NewTargetPrototype(realm.ActiveVm, newTarget, proto);
            var instance = new JsObject(instProto);
            ApplyMessageAndCause(instance, args);
            return JsValue.Object(instance);
        }, isConstructor: true);

        WireConstructor(realm, ctor, proto, "Error", parentCtor: null);
        PopulatePrototype(realm, proto, "Error");

        realm.GlobalObject.DefineOwnProperty("Error",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));

        return ctor;
    }

    /// <summary>Installs a homogeneous Error subclass (TypeError, RangeError,
    /// …). Same shape as <see cref="InstallError"/> but the subclass
    /// constructor's [[Prototype]] is the Error constructor object, mirroring
    /// `class Foo extends Error` semantics.</summary>
    private static void InstallSubclass(JsRealm realm, string name, JsObject proto, JsNativeFunction parentCtor)
    {
        var ctor = new JsNativeFunction(name, (newTarget, args) =>
        {
            var instProto = IntrinsicHelpers.NewTargetPrototype(realm.ActiveVm, newTarget, proto);
            var instance = new JsObject(instProto);
            ApplyMessageAndCause(instance, args);
            return JsValue.Object(instance);
        }, isConstructor: true);

        WireConstructor(realm, ctor, proto, name, parentCtor);
        PopulatePrototype(realm, proto, name);

        realm.GlobalObject.DefineOwnProperty(name,
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
    }

    /// <summary>§20.5.7 AggregateError — adds an own <c>errors</c> array-like
    /// of its iterable first argument. Throws TypeError if the first argument
    /// is not array-like.</summary>
    private static void InstallAggregateError(JsRealm realm, JsNativeFunction parentCtor)
    {
        var proto = realm.AggregateErrorPrototype;
        var ctor = new JsNativeFunction("AggregateError", (newTarget, args) =>
        {
            var errorsArg = args.Length > 0 ? args[0] : JsValue.Undefined;
            var errorsValue = CopyErrorsArrayLike(realm, errorsArg);

            // §20.5.7.1: message + options come at args[1] / args[2], not [0] / [1].
            var instProto = IntrinsicHelpers.NewTargetPrototype(realm.ActiveVm, newTarget, proto);
            var instance = new JsObject(instProto);
            if (args.Length > 1 && !args[1].IsUndefined)
            {
                instance.DefineOwnProperty("message",
                    PropertyDescriptor.Data(JsValue.String(JsValue.ToStringValue(args[1])),
                        writable: true, enumerable: false, configurable: true));
            }
            if (args.Length > 2 && args[2].IsObject)
            {
                var opts = args[2].AsObject;
                if (opts.HasOwn("cause"))
                {
                    instance.DefineOwnProperty("cause",
                        PropertyDescriptor.Data(opts.Get("cause"),
                            writable: true, enumerable: false, configurable: true));
                }
            }
            instance.DefineOwnProperty("errors",
                PropertyDescriptor.Data(errorsValue, writable: true, enumerable: false, configurable: true));
            return JsValue.Object(instance);
        }, isConstructor: true);

        WireConstructor(realm, ctor, proto, "AggregateError", parentCtor);
        PopulatePrototype(realm, proto, "AggregateError");

        realm.GlobalObject.DefineOwnProperty("AggregateError",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
    }

    // ====================================================================
    //                                Helpers
    // ====================================================================

    /// <summary>Wire common constructor-object slots: <c>length</c>,
    /// <c>name</c>, <c>prototype</c>, and the prototype's back-reference
    /// <c>constructor</c>. For Error subclasses, also chain the constructor
    /// object's [[Prototype]] to the Error constructor.</summary>
    private static void WireConstructor(JsRealm realm, JsNativeFunction ctor, JsObject proto, string name, JsNativeFunction? parentCtor)
    {
        // §20.5.1.1 Error.length === 1; subclasses inherit length=1 too. We
        // set it explicitly per ECMA-262 default attributes.
        ctor.DefineOwnProperty("length",
            PropertyDescriptor.Data(JsValue.Number(name == "AggregateError" ? 2 : 1),
                writable: false, enumerable: false, configurable: true));
        ctor.DefineOwnProperty("name",
            PropertyDescriptor.Data(JsValue.String(name),
                writable: false, enumerable: false, configurable: true));
        // §20.5.2.1 Error.prototype is non-writable / non-enumerable / non-configurable.
        ctor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(proto),
                writable: false, enumerable: false, configurable: false));

        // Subclass.[[Prototype]] = Error (so `class TE extends Error` chaining
        // sees Error as the parent constructor). For Error itself fall back
        // to Function.prototype.
        ctor.SetPrototypeOf((JsObject?)parentCtor ?? realm.FunctionPrototype);

        // Prototype.constructor back-reference (writable, non-enumerable, configurable).
        proto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(ctor),
                writable: true, enumerable: false, configurable: true));
    }

    /// <summary>Populate an Error-family prototype with <c>name</c>,
    /// <c>message</c>, and a shared-shape <c>toString</c>.</summary>
    private static void PopulatePrototype(JsRealm realm, JsObject proto, string name)
    {
        proto.DefineOwnProperty("name",
            PropertyDescriptor.Data(JsValue.String(name),
                writable: true, enumerable: false, configurable: true));
        proto.DefineOwnProperty("message",
            PropertyDescriptor.Data(JsValue.String(""),
                writable: true, enumerable: false, configurable: true));

        // Define toString only on Error.prototype itself — subclasses inherit
        // via the prototype chain. Skip re-installing for subclasses.
        if (name == "Error")
        {
            var toStringFn = new JsNativeFunction("toString", (thisV, _) => ToStringImpl(thisV), isConstructor: false);
            toStringFn.DefineOwnProperty("name",
                PropertyDescriptor.Data(JsValue.String("toString"), writable: false, enumerable: false, configurable: true));
            toStringFn.DefineOwnProperty("length",
                PropertyDescriptor.Data(JsValue.Number(0), writable: false, enumerable: false, configurable: true));
            proto.DefineOwnProperty("toString", PropertyDescriptor.BuiltinMethod(JsValue.Object(toStringFn)));
            var stackGetter = new JsNativeFunction(realm, "get stack", 0,
                (thisV, _) => ToStringImpl(thisV), isConstructor: false);
            proto.DefineOwnProperty("stack",
                PropertyDescriptor.Accessor(stackGetter, null, enumerable: false, configurable: true));
        }
    }

    /// <summary>§20.5.3.4 Error.prototype.toString. Read <c>this.name</c>
    /// (default <c>"Error"</c>) and <c>this.message</c> (default <c>""</c>),
    /// then join with <c>": "</c> unless one side is empty.</summary>
    private static JsValue ToStringImpl(JsValue thisV)
    {
        if (!thisV.IsObject)
        {
            return JsValue.String("Error");
        }

        var o = thisV.AsObject;

        var nameV = o.Get("name");
        var name = nameV.IsUndefined ? "Error" : JsValue.ToStringValue(nameV);

        var msgV = o.Get("message");
        var msg = msgV.IsUndefined ? "" : JsValue.ToStringValue(msgV);

        if (name.Length == 0)
        {
            return JsValue.String(msg);
        }

        if (msg.Length == 0)
        {
            return JsValue.String(name);
        }

        return JsValue.String(name + ": " + msg);
    }

    /// <summary>§20.5.1.1 step 3 — if the first arg is not undefined, set
    /// <c>message</c> on the instance. Also honors the options-bag
    /// <c>{ cause }</c> at position 1.</summary>
    private static void ApplyMessageAndCause(JsObject instance, JsValue[] args)
    {
        if (args.Length > 0 && !args[0].IsUndefined)
        {
            var msg = JsValue.ToStringValue(args[0]);
            instance.DefineOwnProperty("message",
                PropertyDescriptor.Data(JsValue.String(msg),
                    writable: true, enumerable: false, configurable: true));
        }
        if (args.Length > 1 && args[1].IsObject)
        {
            var opts = args[1].AsObject;
            if (opts.HasOwn("cause"))
            {
                instance.DefineOwnProperty("cause",
                    PropertyDescriptor.Data(opts.Get("cause"),
                        writable: true, enumerable: false, configurable: true));
            }
        }
    }

    /// <summary>Shallow-copy an array-like (own <c>length</c> + integer-indexed
    /// slots) into a fresh array-like ordinary object. Throws TypeError if
    /// <paramref name="value"/> is not array-like.</summary>
    private static JsValue CopyErrorsArrayLike(JsRealm realm, JsValue value)
    {
        if (!value.IsObject)
        {
            throw new JsThrow(realm.NewTypeError("AggregateError: errors must be an iterable / array-like"));
        }

        var src = value.AsObject;
        var lengthV = src.Get("length");
        if (!lengthV.IsNumber)
        {
            throw new JsThrow(realm.NewTypeError("AggregateError: errors must be an iterable / array-like"));
        }

        var len = (int)lengthV.AsNumber;
        if (len < 0)
        {
            len = 0;
        }

        var dst = realm.NewOrdinaryObject();
        for (var i = 0; i < len; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var v = src.Get(key);
            dst.DefineOwnProperty(key,
                PropertyDescriptor.Data(v, writable: true, enumerable: true, configurable: true));
        }
        dst.DefineOwnProperty("length",
            PropertyDescriptor.Data(JsValue.Number(len), writable: true, enumerable: false, configurable: false));
        return JsValue.Object(dst);
    }
}
