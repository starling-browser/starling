using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>
/// B1b-2c — install <c>next</c>/<c>return</c>/<c>throw</c> on
/// %GeneratorPrototype% and %AsyncGeneratorPrototype%. The actual frame
/// driving lives on <see cref="JsGenerator"/> + <see cref="SuspendedFrame"/>.
/// </summary>
public static class GeneratorIntrinsics
{
    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var proto = realm.GeneratorPrototype;

        var next = new JsNativeFunction(realm, "next", 1,
            (thisV, args) => GeneratorNext(realm, thisV, args.Length > 0 ? args[0] : JsValue.Undefined),
            isConstructor: false);
        proto.DefineOwnProperty("next", PropertyDescriptor.BuiltinMethod(JsValue.Object(next)));

        var ret = new JsNativeFunction(realm, "return", 1,
            (thisV, args) => GeneratorReturn(realm, thisV, args.Length > 0 ? args[0] : JsValue.Undefined),
            isConstructor: false);
        proto.DefineOwnProperty("return", PropertyDescriptor.BuiltinMethod(JsValue.Object(ret)));

        var thr = new JsNativeFunction(realm, "throw", 1,
            (thisV, args) => GeneratorThrow(realm, thisV, args.Length > 0 ? args[0] : JsValue.Undefined),
            isConstructor: false);
        proto.DefineOwnProperty("throw", PropertyDescriptor.BuiltinMethod(JsValue.Object(thr)));

        // [Symbol.iterator]() { return this }
        var iter = new JsNativeFunction(realm, "[Symbol.iterator]", 0,
            (thisV, _) => thisV, isConstructor: false);
        proto.DefineOwnProperty(SymbolCtor.Iterator,
            PropertyDescriptor.BuiltinMethod(JsValue.Object(iter)));

        proto.DefineOwnProperty(SymbolCtor.ToStringTag,
            PropertyDescriptor.Data(JsValue.String("Generator"), writable: false, enumerable: false, configurable: true));

        // ----- AsyncGeneratorPrototype (wp:M3-04g): next/return/throw each
        // return a Promise of {value, done}. The body interleaves yield and
        // await; requests are serialized through a per-generator queue
        // (§27.6.1 %AsyncGeneratorPrototype%).
        var asyncProto = realm.AsyncGeneratorPrototype;
        var asyncNext = new JsNativeFunction(realm, "next", 1,
            (thisV, args) => AsyncGeneratorNext(realm, thisV, args.Length > 0 ? args[0] : JsValue.Undefined),
            isConstructor: false);
        asyncProto.DefineOwnProperty("next", PropertyDescriptor.BuiltinMethod(JsValue.Object(asyncNext)));
        var asyncReturn = new JsNativeFunction(realm, "return", 1,
            (thisV, args) => AsyncGeneratorReturn(realm, thisV, args.Length > 0 ? args[0] : JsValue.Undefined),
            isConstructor: false);
        asyncProto.DefineOwnProperty("return", PropertyDescriptor.BuiltinMethod(JsValue.Object(asyncReturn)));
        var asyncThrow = new JsNativeFunction(realm, "throw", 1,
            (thisV, args) => AsyncGeneratorThrow(realm, thisV, args.Length > 0 ? args[0] : JsValue.Undefined),
            isConstructor: false);
        asyncProto.DefineOwnProperty("throw", PropertyDescriptor.BuiltinMethod(JsValue.Object(asyncThrow)));
        var asyncIter = new JsNativeFunction(realm, "[Symbol.asyncIterator]", 0,
            (thisV, _) => thisV, isConstructor: false);
        asyncProto.DefineOwnProperty(SymbolCtor.AsyncIterator,
            PropertyDescriptor.BuiltinMethod(JsValue.Object(asyncIter)));
        asyncProto.DefineOwnProperty(SymbolCtor.ToStringTag,
            PropertyDescriptor.Data(JsValue.String("AsyncGenerator"), writable: false, enumerable: false, configurable: true));
    }

    private static JsValue GeneratorNext(JsRealm realm, JsValue thisV, JsValue sent)
    {
        if (!thisV.IsObject || thisV.AsObject is not JsGenerator gen)
        {
            throw new JsThrow(realm.NewTypeError("Generator.prototype.next called on non-Generator"));
        }

        if (gen.Done)
        {
            return IteratorIntrinsics.MakeResult(realm, JsValue.Undefined, done: true);
        }

        if (!gen.TryEnterResume())
        {
            throw new JsThrow(realm.NewTypeError("Generator is already running"));
        }

        try
        {
            if (!gen.Started)
            {
                gen.Started = true;
                gen.Frame.Resume(JsValue.Undefined); // sent value on first call is ignored
            }
            else
            {
                gen.Frame.Resume(sent);
            }
            if (gen.Frame.Completed)
            {
                gen.Done = true;
                if (gen.Frame.ThrewUncaught)
                {
                    throw new JsThrow(gen.Frame.ReturnValue);
                }

                return IteratorIntrinsics.MakeResult(realm, gen.Frame.ReturnValue, done: true);
            }
            return IteratorIntrinsics.MakeResult(realm, gen.Frame.YieldedValue, done: false);
        }
        finally
        {
            gen.ExitResume();
        }
    }

    private static JsValue GeneratorReturn(JsRealm realm, JsValue thisV, JsValue sent)
    {
        if (!thisV.IsObject || thisV.AsObject is not JsGenerator gen)
        {
            throw new JsThrow(realm.NewTypeError("Generator.prototype.return called on non-Generator"));
        }

        if (gen.Done || !gen.Started)
        {
            gen.Done = true;
            return IteratorIntrinsics.MakeResult(realm, sent, done: true);
        }
        if (!gen.TryEnterResume())
        {
            throw new JsThrow(realm.NewTypeError("Generator is already running"));
        }
        // Signal the suspended frame to perform an early Return at the current
        // suspension point. The Suspend opcode handler raises a
        // JsReturnSentinel which walks any enclosing try/finally frames
        // before the body returns sent (or, if a finally overrides the
        // completion via throw, the frame reports ThrewUncaught).
        try
        {
            gen.Frame.Resume(sent, withReturn: true);
            if (gen.Frame.ThrewUncaught)
            {
                throw new JsThrow(gen.Frame.ReturnValue);
            }

            if (gen.Frame.Completed)
            {
                gen.Done = true;
                return IteratorIntrinsics.MakeResult(realm, gen.Frame.ReturnValue, done: true);
            }
            return IteratorIntrinsics.MakeResult(realm, gen.Frame.YieldedValue, done: false);
        }
        finally
        {
            gen.ExitResume();
        }
    }

    private static JsValue GeneratorThrow(JsRealm realm, JsValue thisV, JsValue err)
    {
        if (!thisV.IsObject || thisV.AsObject is not JsGenerator gen)
        {
            throw new JsThrow(realm.NewTypeError("Generator.prototype.throw called on non-Generator"));
        }

        if (gen.Done || !gen.Started)
        {
            gen.Done = true;
            throw new JsThrow(err);
        }
        if (!gen.TryEnterResume())
        {
            throw new JsThrow(realm.NewTypeError("Generator is already running"));
        }

        try
        {
            gen.Frame.Resume(err, withThrow: true);
            if (gen.Frame.Completed)
            {
                gen.Done = true;
                if (gen.Frame.ThrewUncaught)
                {
                    throw new JsThrow(gen.Frame.ReturnValue);
                }

                return IteratorIntrinsics.MakeResult(realm, gen.Frame.ReturnValue, done: true);
            }
            return IteratorIntrinsics.MakeResult(realm, gen.Frame.YieldedValue, done: false);
        }
        finally
        {
            gen.ExitResume();
        }
    }

    // ---- wp:M3-04g — async-generator next/return/throw -----------------------
    // Each call enqueues an AsyncGeneratorRequest and returns its promise. The
    // VM driver (JsVm.AsyncGeneratorEnqueue / DrainQueue) serializes them so
    // the body never runs two requests at once, and rides yield+await
    // suspensions to settle each request with a Promise of {value, done}.

    private static JsValue AsyncGeneratorEnqueue(
        JsRealm realm, JsValue thisV, AsyncGeneratorRequestKind kind, JsValue value, string method)
    {
        // §27.6.3.1 step 1–3: a brand check failure must REJECT the returned
        // promise rather than throw synchronously.
        if (!thisV.IsObject || thisV.AsObject is not JsAsyncGenerator gen)
        {
            var p = new JsPromise(realm.PromisePrototype);
            PromiseCtor.Reject(realm, p, realm.NewTypeError(
                $"AsyncGenerator.prototype.{method} called on non-AsyncGenerator"));
            return JsValue.Object(p);
        }
        var vm = realm.ActiveVm
            ?? throw new JsThrow(realm.NewTypeError("no active VM to drive async generator"));
        return vm.AsyncGeneratorEnqueue(gen, kind, value);
    }

    private static JsValue AsyncGeneratorNext(JsRealm realm, JsValue thisV, JsValue sent)
        => AsyncGeneratorEnqueue(realm, thisV, AsyncGeneratorRequestKind.Next, sent, "next");

    private static JsValue AsyncGeneratorReturn(JsRealm realm, JsValue thisV, JsValue value)
        => AsyncGeneratorEnqueue(realm, thisV, AsyncGeneratorRequestKind.Return, value, "return");

    private static JsValue AsyncGeneratorThrow(JsRealm realm, JsValue thisV, JsValue err)
        => AsyncGeneratorEnqueue(realm, thisV, AsyncGeneratorRequestKind.Throw, err, "throw");
}
