using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>
/// Spec section 20.2 — the Function intrinsic. Populates
/// <see cref="JsRealm.FunctionConstructor"/> and <see cref="JsRealm.FunctionPrototype"/>,
/// then registers the constructor on the realm's global object.
/// </summary>
/// <remarks>
/// <para>
/// The constructor itself, called as <c>Function(src)</c> or
/// <c>new Function(src)</c>, assembles the parameter list and body into a
/// global-scope function expression, then compiles it through the shared eval
/// path.
/// </para>
/// <para><b>Callable shape guarantees:</b></para>
/// <list type="number">
///   <item><c>Function.prototype</c> is now populated with
///   <c>call</c>/<c>apply</c>/<c>bind</c>/<c>toString</c>, so every callable
///   that inherits from it picks the methods up automatically.</item>
///   <item><c>JsFunction</c> instances built at <c>LoadFunction</c> /
///   <c>MakeClosure</c> time are wired to <c>realm.FunctionPrototype</c>
///   (see <c>JsFunction.CreateInstance</c>) and carry their own
///   <c>prototype</c>/<c>name</c>/<c>length</c> data slots.</item>
/// </list>
/// </remarks>
public static class FunctionCtor
{
    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var funcProto = realm.FunctionPrototype;

        // ----------------------------------------------------------- Constructor
        // Spec 20.2.1 — `Function(p1, …, pN, body)` builds a function from source:
        // the last argument is the body, the rest are comma-joined into the
        // parameter list. We synthesize `(function anonymous(<params>){<body>})`,
        // compile it in global (eval) scope, and return the resulting function.
        var ctor = new JsNativeFunction("Function", (newTarget, args) =>
        {
            var fn = BuildDynamicFunction(realm, args);
            // §20.2.1.1 CreateDynamicFunction uses OrdinaryCreateFromConstructor
            // for the new function's [[Prototype]]. When subclassed
            // (`class S extends Function {}`), super()'s new.target is S, so the
            // produced function is re-parented to S.prototype for instanceof.
            if (fn.IsObject)
            {
                var instProto = IntrinsicHelpers.NewTargetPrototype(realm.ActiveVm, newTarget, funcProto);
                if (!ReferenceEquals(instProto, funcProto))
                {
                    fn.AsObject.SetPrototypeOf(instProto);
                }
            }
            return fn;
        }, isConstructor: true);
        // The constructor inherits from its own prototype object.
        ctor.SetPrototypeOf(funcProto);
        ctor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(funcProto), writable: false, enumerable: false, configurable: false));
        ctor.DefineOwnProperty("name",
            PropertyDescriptor.Data(JsValue.String("Function"), writable: false, enumerable: false, configurable: true));
        ctor.DefineOwnProperty("length",
            PropertyDescriptor.Data(JsValue.Number(1), writable: false, enumerable: false, configurable: true));

        // Function.prototype.[[Prototype]] is already wired in JsRealm's
        // bootstrap (chain to Object.prototype). Stamp `constructor`, a length of
        // 0, a name of "" (spec 20.2.3), and the prototype methods.
        // Bulk-install in the same creation order as the prior sequential install:
        // constructor (W=true/C=true), then the non-writable length + name data
        // properties (C=true only), then the four prototype methods. Mixed flags
        // are carried per-member; the result is byte-identical and
        // getOwnPropertyNames order is unchanged.
        IntrinsicHelpers.BulkInstallBuiltins(realm, funcProto, new[]
        {
            new IntrinsicHelpers.BulkMember("constructor", 0, null, JsValue.Object(ctor)),
            new IntrinsicHelpers.BulkMember("length", 0, null, JsValue.Number(0), Shape.Configurable),
            new IntrinsicHelpers.BulkMember("name", 0, null, JsValue.String(""), Shape.Configurable),
            new IntrinsicHelpers.BulkMember("call", 1, (thisV, args) => ProtoCall(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("apply", 2, (thisV, args) => ProtoApply(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("bind", 1, (thisV, args) => ProtoBind(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("toString", 0, ProtoToString),
        });

        realm.FunctionConstructor = ctor;
        realm.GlobalObject.DefineOwnProperty("Function",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
    }

    /// <summary>Spec 20.2.1.1 CreateDynamicFunction — assemble source from the
    /// argument list (last arg = body, the rest = parameters) and compile it as a
    /// function expression in global scope. A malformed parameter list or body
    /// surfaces as a SyntaxError via the shared eval path.</summary>
    private static JsValue BuildDynamicFunction(JsRealm realm, JsValue[] args)
    {
        var body = args.Length > 0 ? JsValue.ToStringValue(args[^1]) : "";
        var paramList = args.Length > 1
            ? string.Join(",", args[..^1].Select(JsValue.ToStringValue))
            : "";
        // The trailing newline before ')' guards against a `//`-comment in the
        // last parameter chunk; the newline before '}' likewise guards the body.
        var source = "(function anonymous(" + paramList + "\n) {\n" + body + "\n})";
        return Globals.RunGlobalSource(realm, source, "<anonymous>");
    }

    // ====================================================================
    //                          Prototype implementations
    // ====================================================================

    /// <summary>Spec 20.2.3.3 — Function.prototype.call(thisArg, ...args).</summary>
    private static JsValue ProtoCall(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        if (!AbstractOperations.IsCallable(thisV))
        {
            throw new JsThrow(realm.NewTypeError("Function.prototype.call called on non-callable"));
        }

        var thisArg = args.Length > 0 ? args[0] : JsValue.Undefined;
        var rest = args.Length > 1 ? args[1..] : Array.Empty<JsValue>();
        return AbstractOperations.Call(realm.ActiveVm, thisV, thisArg, rest);
    }

    /// <summary>Spec 20.2.3.1 — Function.prototype.apply(thisArg, argsArray).</summary>
    private static JsValue ProtoApply(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        if (!AbstractOperations.IsCallable(thisV))
        {
            throw new JsThrow(realm.NewTypeError("Function.prototype.apply called on non-callable"));
        }

        var thisArg = args.Length > 0 ? args[0] : JsValue.Undefined;
        var argsArrayV = args.Length > 1 ? args[1] : JsValue.Undefined;

        JsValue[] callArgs;
        if (argsArrayV.IsNullish)
        {
            callArgs = Array.Empty<JsValue>();
        }
        else if (argsArrayV.IsObject)
        {
            var obj = argsArrayV.AsObject;
            var lenV = obj.Get("length");
            var len = (int)JsValue.ToNumber(lenV);
            if (len < 0)
            {
                len = 0;
            }

            callArgs = new JsValue[len];
            for (var i = 0; i < len; i++)
            {
                callArgs[i] = obj.Get(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        else
        {
            // Per spec, non-object non-nullish argsArray is a TypeError.
            throw new JsThrow(realm.NewTypeError("Function.prototype.apply: argsArray must be an object or null/undefined"));
        }

        return AbstractOperations.Call(realm.ActiveVm, thisV, thisArg, callArgs);
    }

    /// <summary>Spec 20.2.3.2 — Function.prototype.bind(thisArg, ...boundArgs).
    /// Returns a fresh bound function whose <c>[[Prototype]]</c> chains to
    /// <c>Function.prototype</c> so subsequent <c>.bind()</c> calls work.</summary>
    private static JsValue ProtoBind(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        if (!thisV.IsObject || !AbstractOperations.IsCallable(thisV))
        {
            throw new JsThrow(realm.NewTypeError("Function.prototype.bind called on non-callable"));
        }

        var target = thisV.AsObject;
        var boundThis = args.Length > 0 ? args[0] : JsValue.Undefined;
        var boundArgs = args.Length > 1 ? args[1..] : Array.Empty<JsValue>();
        var bound = new JsBoundFunction(realm, target, boundThis, boundArgs);
        // Per spec 20.2.3.2, the bound function has its own `length` and
        // `name` derived from the target. We give sensible defaults for our
        // current surface (Google's bundles rely on `.length` existing).
        var targetLen = (int)JsValue.ToNumber(target.Get("length"));
        var lengthMinusBound = Math.Max(0, targetLen - boundArgs.Length);
        bound.DefineOwnProperty("length",
            PropertyDescriptor.Data(JsValue.Number(lengthMinusBound), writable: false, enumerable: false, configurable: true));
        var targetName = target.Get("name");
        var nameStr = targetName.IsString ? targetName.AsString : "";
        bound.DefineOwnProperty("name",
            PropertyDescriptor.Data(JsValue.String("bound " + nameStr), writable: false, enumerable: false, configurable: true));
        return JsValue.Object(bound);
    }

    /// <summary>Spec 20.2.3.5 — Function.prototype.toString.</summary>
    private static JsValue ProtoToString(JsValue thisV, JsValue[] args)
    {
        if (!thisV.IsObject)
        {
            return JsValue.String("function () { [native code] }");
        }

        return thisV.AsObject switch
        {
            JsNativeFunction nat => JsValue.String($"function {nat.Name}() {{ [native code] }}"),
            JsFunction fn => JsValue.String(fn.SourceText ?? $"function {fn.Name}() {{ [bytecode] }}"),
            JsBoundFunction => JsValue.String("function () { [native code] }"),
            _ => JsValue.String("function () { [native code] }"),
        };
    }
}
