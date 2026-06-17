using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;
using WasmConfig = Wasmtime.Config;
using WasmEngine = Wasmtime.Engine;
using WasmFunction = Wasmtime.Function;
using WasmFunctionImport = Wasmtime.FunctionImport;
using WasmInstance = Wasmtime.Instance;
using WasmLinker = Wasmtime.Linker;
using WasmMemory = Wasmtime.Memory;
using WasmMemoryImport = Wasmtime.MemoryImport;
using WasmModule = Wasmtime.Module;
using WasmStore = Wasmtime.Store;
using WasmTable = Wasmtime.Table;
using WasmTableImport = Wasmtime.TableImport;
using WasmTableKind = Wasmtime.TableKind;
using WasmTrapException = Wasmtime.TrapException;
using WasmValueBox = Wasmtime.ValueBox;
using WasmValueKind = Wasmtime.ValueKind;
using WasmWasmtimeException = Wasmtime.WasmtimeException;

namespace Starling.Bindings.Jint;

/// <summary>
/// WebAssembly JavaScript API for the Jint backend, mirroring
/// <c>Starling.Bindings/WebAssemblyBinding.cs</c>: a real Wasmtime-backed
/// <c>WebAssembly</c> global (Module/Instance/Memory/Table + CompileError/
/// LinkError/RuntimeError, compile/validate/instantiate + streaming variants).
/// Enough for self-contained numeric modules and a real object for
/// <c>typeof WebAssembly</c> feature detection.
/// </summary>
/// <remarks>
/// One intentional divergence from the canonical backend: <c>Memory.buffer</c>
/// returns a fresh snapshot ArrayBuffer of the wasm linear memory on each access
/// (refreshed after grow/calls) rather than a zero-copy live-aliased buffer. Jint's
/// ArrayBuffer has no public storage hook equivalent to the canonical engine's
/// <c>JsArrayBuffer.Wrap(IJsArrayBufferStorage)</c>.
/// </remarks>
internal static class WebAssemblyBinding
{
    private const int WasmExecutionStackSize = 16 * 1024 * 1024;
    private const int WasmtimeMaximumStackSize = 2 * 1024 * 1024;

    private static readonly WasmEngine SharedEngine = new(CreateEngineConfig());
    private static readonly ConditionalWeakTable<ObjectInstance, WasmFunctionReference> WasmFunctionReferences = new();

    [ThreadStatic]
    private static bool t_onWasmExecutionStack;

    private static WasmConfig CreateEngineConfig() => new WasmConfig()
        .WithReferenceTypes(true)
        .WithSIMD(true)
        .WithRelaxedSIMD(enable: true, deterministic: true)
        .WithBulkMemory(true)
        .WithMultiValue(true)
        .WithMaximumStackSize(WasmtimeMaximumStackSize);

    public static void Install(JintBackendContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var engine = ctx.Engine;
        if (engine.Global.HasOwnProperty("WebAssembly"))
        {
            return;
        }

        var state = new WasmRealmState(SharedEngine);
        var errProtoBase = (ObjectInstance)engine.Intrinsics.Error.Get("prototype");
        var moduleProto = new JsObject(engine);
        var instanceProto = new JsObject(engine);
        var memoryProto = new JsObject(engine);
        var tableProto = new JsObject(engine);
        var compileErrorProto = new JsObject(engine) { Prototype = errProtoBase };
        var linkErrorProto = new JsObject(engine) { Prototype = errProtoBase };
        var runtimeErrorProto = new JsObject(engine) { Prototype = errProtoBase };

        var moduleCtor = new NativeConstructor(engine, "Module", 1, (args, _) =>
            new WasmModuleObject(engine, moduleProto, DecodeModule(ctx, compileErrorProto, Arg(args, 0))));
        WireConstructor(moduleCtor, moduleProto, "Module");

        var instanceCtor = new NativeConstructor(engine, "Instance", 1, (args, _) =>
        {
            var module = RequireModule(ctx, Arg(args, 0));
            var importObject = args.Length > 1 ? args[1] : JsValue.Undefined;
            return BuildInstanceObject(ctx, state, instanceProto, memoryProto, tableProto, linkErrorProto, runtimeErrorProto, module, importObject);
        });
        WireConstructor(instanceCtor, instanceProto, "Instance");

        var memoryCtor = new NativeConstructor(engine, "Memory", 1, (args, _) =>
        {
            var descriptor = Arg(args, 0);
            if (!descriptor.IsObject())
            {
                throw TypeErr(ctx, "WebAssembly.Memory requires a descriptor object");
            }

            var obj = descriptor.AsObject();
            var initial = ToNonNegativeInt(ctx, obj.Get("initial"), "initial");
            var maximum = obj.Get("maximum");
            var memory = new WasmMemory(state.Store, initial, maximum.IsUndefined() ? null : ToNonNegativeInt(ctx, maximum, "maximum"), is64Bit: false);
            var wrapper = new WasmMemoryObject(engine, memoryProto, memory);
            state.RegisterMemory(wrapper);
            return wrapper;
        });
        WireConstructor(memoryCtor, memoryProto, "Memory");
        JintInterop.DefineAccessor(engine, memoryProto, "buffer", (t, _) => RequireMemory(ctx, t).GetBuffer(engine));
        JintInterop.DefineMethod(engine, memoryProto, "grow", (t, a) =>
        {
            var memoryObject = RequireMemory(ctx, t);
            var oldPages = memoryObject.Memory.Grow(ToNonNegativeInt(ctx, Arg(a, 0), "delta"));
            memoryObject.SyncFromWasm();
            return JintInterop.Num(oldPages);
        }, 1);

        var tableCtor = new NativeConstructor(engine, "Table", 1, (args, _) =>
        {
            var descriptor = Arg(args, 0);
            if (!descriptor.IsObject())
            {
                throw TypeErr(ctx, "WebAssembly.Table requires a descriptor object");
            }

            var obj = descriptor.AsObject();
            var element = TypeConverter.ToString(obj.Get("element"));
            if (!StringComparer.Ordinal.Equals(element, "anyfunc") && !StringComparer.Ordinal.Equals(element, "funcref"))
            {
                throw TypeErr(ctx, "WebAssembly.Table descriptor 'element' must be 'funcref'");
            }

            var initial = (uint)ToNonNegativeInt(ctx, obj.Get("initial"), "initial", "WebAssembly.Table descriptor");
            var maximum = obj.Get("maximum");
            var maximumElements = maximum.IsUndefined() ? uint.MaxValue : (uint)ToNonNegativeInt(ctx, maximum, "maximum", "WebAssembly.Table descriptor");
            var table = new WasmTable(state.Store, WasmTableKind.FuncRef, WasmFunction.Null, initial, maximumElements);
            return new WasmTableObject(engine, tableProto, table);
        });
        WireConstructor(tableCtor, tableProto, "Table");
        JintInterop.DefineAccessor(engine, tableProto, "length", (t, _) => JintInterop.Num(RequireTable(ctx, t).Table.GetSize()));
        JintInterop.DefineMethod(engine, tableProto, "get", (t, a) =>
        {
            var table = RequireTable(ctx, t);
            var index = ToTableIndex(ctx, Arg(a, 0), table.Table.GetSize());
            return FromTableValue(ctx, state, table, table.Table.GetElement(index));
        }, 1);
        JintInterop.DefineMethod(engine, tableProto, "set", (t, a) =>
        {
            var table = RequireTable(ctx, t);
            var index = ToTableIndex(ctx, Arg(a, 0), table.Table.GetSize());
            table.Table.SetElement(index, ToTableValue(ctx, Arg(a, 1), defaultToNull: false));
            return JsValue.Undefined;
        }, 2);
        JintInterop.DefineMethod(engine, tableProto, "grow", (t, a) =>
        {
            var table = RequireTable(ctx, t);
            var delta = (uint)ToNonNegativeInt(ctx, Arg(a, 0), "delta", "WebAssembly.Table grow");
            var initial = ToTableValue(ctx, Arg(a, 1), defaultToNull: true);
            return JintInterop.Num(table.Table.Grow(delta, initial));
        }, 1);

        var wasm = new JsObject(engine);
        Data(wasm, "Module", moduleCtor);
        Data(wasm, "Instance", instanceCtor);
        Data(wasm, "Memory", memoryCtor);
        Data(wasm, "Table", tableCtor);

        InstallWasmErrorConstructor(ctx, wasm, "CompileError", compileErrorProto);
        InstallWasmErrorConstructor(ctx, wasm, "LinkError", linkErrorProto);
        InstallWasmErrorConstructor(ctx, wasm, "RuntimeError", runtimeErrorProto);

        JintInterop.DefineMethod(engine, wasm, "compile", (_, a) =>
        {
            try { return ResolvedPromise(engine, new WasmModuleObject(engine, moduleProto, DecodeModule(ctx, compileErrorProto, Arg(a, 0)))); }
            catch (JavaScriptException ex) { return RejectedPromise(engine, ex.Error); }
            catch (Exception ex) { return RejectedPromise(engine, TypeErr(ctx, ex.Message).Error); }
        }, 1);

        JintInterop.DefineMethod(engine, wasm, "validate", (_, a) =>
        {
            try { return JintInterop.Bool(WasmModule.Validate(SharedEngine, BytesFromBufferSource(ctx, Arg(a, 0))) is null); }
            catch { return JsBoolean.False; }
        }, 1);

        JintInterop.DefineMethod(engine, wasm, "instantiate", (_, a) =>
        {
            try
            {
                var importObject = a.Length > 1 ? a[1] : JsValue.Undefined;
                if (Arg(a, 0) is WasmModuleObject moduleObject)
                {
                    var instance = BuildInstanceObject(ctx, state, instanceProto, memoryProto, tableProto, linkErrorProto, runtimeErrorProto, moduleObject.Module, importObject);
                    return ResolvedPromise(engine, instance);
                }
                var module = DecodeModule(ctx, compileErrorProto, Arg(a, 0));
                var moduleWrapper = new WasmModuleObject(engine, moduleProto, module);
                var instanceWrapper = BuildInstanceObject(ctx, state, instanceProto, memoryProto, tableProto, linkErrorProto, runtimeErrorProto, module, importObject);
                return ResolvedPromise(engine, BuildInstantiateResult(engine, moduleWrapper, instanceWrapper));
            }
            catch (JavaScriptException ex) { return RejectedPromise(engine, ex.Error); }
            catch (Exception ex) { return RejectedPromise(engine, TypeErr(ctx, ex.Message).Error); }
        }, 1);

        JintInterop.DefineMethod(engine, wasm, "compileStreaming", (_, a) =>
            StreamBytes(ctx, Arg(a, 0), bytes => new WasmModuleObject(engine, moduleProto, DecodeModule(ctx, compileErrorProto, bytes))), 1);

        JintInterop.DefineMethod(engine, wasm, "instantiateStreaming", (_, a) =>
        {
            var importObject = a.Length > 1 ? a[1] : JsValue.Undefined;
            return StreamBytes(ctx, Arg(a, 0), bytes =>
            {
                var module = DecodeModule(ctx, compileErrorProto, bytes);
                var moduleWrapper = new WasmModuleObject(engine, moduleProto, module);
                var instanceWrapper = BuildInstanceObject(ctx, state, instanceProto, memoryProto, tableProto, linkErrorProto, runtimeErrorProto, module, importObject);
                return BuildInstantiateResult(engine, moduleWrapper, instanceWrapper);
            });
        }, 1);

        JintInterop.DefineDataProp(engine.Global, "WebAssembly", wasm, writable: true, enumerable: false, configurable: true);
    }

    private static void Data(ObjectInstance o, string name, JsValue v)
        => o.FastSetProperty(name, new PropertyDescriptor(v, writable: true, enumerable: false, configurable: true));

    private static JsValue Arg(JsValue[] args, int index) => index < args.Length ? args[index] : JsValue.Undefined;

    private static JavaScriptException TypeErr(JintBackendContext ctx, string message)
        => new(ctx.Engine.Intrinsics.TypeError, message);

    private static JavaScriptException RangeErr(JintBackendContext ctx, string message)
    {
        var proto = (ObjectInstance)ctx.Engine.Global.Get("RangeError").AsObject().Get("prototype");
        var err = new JsObject(ctx.Engine) { Prototype = proto };
        err.FastSetProperty("message", new PropertyDescriptor(JintInterop.Str(message), writable: true, enumerable: false, configurable: true));
        return new JavaScriptException(err);
    }

    private static void WireConstructor(ObjectInstance ctor, ObjectInstance proto, string name)
    {
        ctor.DefineOwnProperty("prototype", new PropertyDescriptor(proto, writable: false, enumerable: false, configurable: false));
        proto.FastSetProperty("constructor", new PropertyDescriptor(ctor, writable: true, enumerable: false, configurable: true));
        ctor.FastSetProperty("name", new PropertyDescriptor(JintInterop.Str(name), writable: false, enumerable: false, configurable: true));
    }

    private static void InstallWasmErrorConstructor(JintBackendContext ctx, ObjectInstance wasm, string name, ObjectInstance prototype)
    {
        var engine = ctx.Engine;
        var ctor = new NativeConstructor(engine, name, 1, (args, newTarget) =>
        {
            var proto = NewTargetPrototype(newTarget, prototype);
            var instance = new JsObject(engine) { Prototype = proto };
            ApplyMessageAndCause(instance, args);
            return instance;
        });
        WireConstructor(ctor, prototype, name);
        prototype.FastSetProperty("name", new PropertyDescriptor(JintInterop.Str(name), writable: true, enumerable: false, configurable: true));
        prototype.FastSetProperty("message", new PropertyDescriptor(JintInterop.Str(""), writable: true, enumerable: false, configurable: true));
        Data(wasm, name, ctor);
    }

    private static ObjectInstance NewTargetPrototype(JsValue newTarget, ObjectInstance defaultPrototype)
    {
        if (newTarget is ObjectInstance nt)
        {
            var proto = nt.Get("prototype");
            if (proto is ObjectInstance p)
            {
                return p;
            }
        }
        return defaultPrototype;
    }

    private static void ApplyMessageAndCause(ObjectInstance instance, JsValue[] args)
    {
        if (args.Length > 0 && !args[0].IsUndefined())
        {
            instance.FastSetProperty("message", new PropertyDescriptor(JintInterop.Str(TypeConverter.ToString(args[0])), writable: true, enumerable: false, configurable: true));
        }

        if (args.Length > 1 && args[1].IsObject())
        {
            var options = args[1].AsObject();
            if (options.HasOwnProperty("cause"))
            {
                instance.FastSetProperty("cause", new PropertyDescriptor(options.Get("cause"), writable: true, enumerable: false, configurable: true));
            }
        }
    }

    private static JsObject NewWasmError(global::Jint.Engine engine, ObjectInstance prototype, string message)
    {
        var error = new JsObject(engine) { Prototype = prototype };
        error.FastSetProperty("message", new PropertyDescriptor(JintInterop.Str(message), writable: true, enumerable: false, configurable: true));
        return error;
    }

    private static WasmModule DecodeModule(JintBackendContext ctx, ObjectInstance compileErrorProto, JsValue value)
    {
        try { return WasmModule.FromBytes(SharedEngine, "starling", BytesFromBufferSource(ctx, value)); }
        catch (JavaScriptException) { throw; }
        catch (Exception ex) { throw new JavaScriptException(NewWasmError(ctx.Engine, compileErrorProto, ex.Message)); }
    }

    private static WasmModule DecodeModule(JintBackendContext ctx, ObjectInstance compileErrorProto, byte[] bytes)
    {
        try { return WasmModule.FromBytes(SharedEngine, "starling", bytes); }
        catch (Exception ex) { throw new JavaScriptException(NewWasmError(ctx.Engine, compileErrorProto, ex.Message)); }
    }

    private static WasmModule RequireModule(JintBackendContext ctx, JsValue value)
        => value is WasmModuleObject m ? m.Module : throw TypeErr(ctx, "Expected WebAssembly.Module");

    private static WasmMemoryObject RequireMemory(JintBackendContext ctx, JsValue value)
        => value as WasmMemoryObject ?? throw TypeErr(ctx, "Expected WebAssembly.Memory");

    private static WasmTableObject RequireTable(JintBackendContext ctx, JsValue value)
        => value as WasmTableObject ?? throw TypeErr(ctx, "Expected WebAssembly.Table");

    private static WasmInstanceObject BuildInstanceObject(
        JintBackendContext ctx, WasmRealmState state, ObjectInstance instanceProto, ObjectInstance memoryProto,
        ObjectInstance tableProto, ObjectInstance linkErrorProto, ObjectInstance runtimeErrorProto, WasmModule module, JsValue importObject)
    {
        var engine = ctx.Engine;
        using var linker = new WasmLinker(SharedEngine);
        RegisterImports(ctx, state, linker, module, importObject, linkErrorProto);
        WasmInstance instance;
        try { instance = linker.Instantiate(state.Store, module); }
        catch (JavaScriptException) { throw; }
        catch (WasmTrapException ex) { throw new JavaScriptException(NewWasmError(engine, runtimeErrorProto, ex.Message)); }
        catch (Exception ex) { throw new JavaScriptException(NewWasmError(engine, linkErrorProto, ex.Message)); }
        state.SyncMemoryObjectsFromWasm();
        var exports = BuildExports(ctx, state, memoryProto, tableProto, runtimeErrorProto, instance);
        return new WasmInstanceObject(engine, instanceProto, instance, exports);
    }

    private static void RegisterImports(JintBackendContext ctx, WasmRealmState state, WasmLinker linker, WasmModule module, JsValue importObject, ObjectInstance linkErrorProto)
    {
        foreach (var import in module.Imports)
        {
            var value = ResolveImport(ctx, importObject, import, linkErrorProto);
            switch (import)
            {
                case WasmFunctionImport functionImport:
                    linker.Define(import.ModuleName, import.Name, BuildHostFunction(ctx, state, functionImport, value, linkErrorProto));
                    break;
                case WasmMemoryImport memoryImport:
                    linker.Define(import.ModuleName, import.Name, RequireImportedMemory(ctx, memoryImport, value, linkErrorProto));
                    break;
                case WasmTableImport:
                    linker.Define(import.ModuleName, import.Name, RequireTable(ctx, value).Table);
                    break;
                default:
                    throw new JavaScriptException(NewWasmError(ctx.Engine, linkErrorProto,
                        $"Unsupported WebAssembly import {import.ModuleName}.{import.Name} ({import.GetType().Name})"));
            }
        }
    }

    private static JsValue ResolveImport(JintBackendContext ctx, JsValue importObject, Wasmtime.Import import, ObjectInstance linkErrorProto)
    {
        if (!importObject.IsObject())
        {
            throw MissingImport(ctx, import, linkErrorProto);
        }

        var moduleValue = importObject.AsObject().Get(import.ModuleName);
        if (!moduleValue.IsObject())
        {
            throw MissingImport(ctx, import, linkErrorProto);
        }

        var value = moduleValue.AsObject().Get(import.Name);
        if (value.IsUndefined())
        {
            throw MissingImport(ctx, import, linkErrorProto);
        }

        return value;
    }

    private static JavaScriptException MissingImport(JintBackendContext ctx, Wasmtime.Import import, ObjectInstance linkErrorProto)
        => new(NewWasmError(ctx.Engine, linkErrorProto, $"Missing WebAssembly import {import.ModuleName}.{import.Name}"));

    private static WasmFunction BuildHostFunction(JintBackendContext ctx, WasmRealmState state, WasmFunctionImport import, JsValue value, ObjectInstance linkErrorProto)
    {
        if (!value.IsCallable())
        {
            throw new JavaScriptException(NewWasmError(ctx.Engine, linkErrorProto, $"WebAssembly import {import.ModuleName}.{import.Name} must be a function"));
        }

        return WasmFunction.FromCallback(state.Store, (_, args, results) =>
        {
            state.SyncMemoryObjectsFromWasm();
            try
            {
                var jsArgs = new JsValue[args.Length];
                for (var i = 0; i < jsArgs.Length; i++)
                {
                    jsArgs[i] = FromWasmValue(ctx, args[i], import.Parameters[i]);
                }

                JsValue result;
                try { result = value.Call(JsValue.Undefined, jsArgs); }
                catch (JavaScriptException ex) { throw new WasmImportException($"WebAssembly import {import.ModuleName}.{import.Name} threw: {DescribeThrown(ex.Error)}", ex); }

                if (results.Length == 1)
                {
                    results[0] = ToWasmValue(ctx, result, import.Results[0]);
                }
                else if (results.Length > 1)
                {
                    if (!result.IsObject())
                    {
                        throw TypeErr(ctx, $"WebAssembly import {import.ModuleName}.{import.Name} must return an array");
                    }

                    var obj = result.AsObject();
                    for (var i = 0; i < results.Length; i++)
                    {
                        results[i] = ToWasmValue(ctx, obj.Get(i.ToString(CultureInfo.InvariantCulture)), import.Results[i]);
                    }
                }
            }
            finally { state.SyncMemoryObjectsFromWasm(); }
        }, import.Parameters, import.Results);
    }

    private static WasmMemory RequireImportedMemory(JintBackendContext ctx, WasmMemoryImport import, JsValue value, ObjectInstance linkErrorProto)
    {
        if (value is not WasmMemoryObject memoryObject)
        {
            throw new JavaScriptException(NewWasmError(ctx.Engine, linkErrorProto, $"WebAssembly import {import.ModuleName}.{import.Name} must be a memory"));
        }

        var memory = memoryObject.Memory;
        if (memory.GetSize() < import.Minimum)
        {
            throw new JavaScriptException(NewWasmError(ctx.Engine, linkErrorProto, $"WebAssembly import {import.ModuleName}.{import.Name} memory is smaller than required"));
        }

        return memory;
    }

    private static JsObject BuildExports(JintBackendContext ctx, WasmRealmState state, ObjectInstance memoryProto, ObjectInstance tableProto, ObjectInstance runtimeErrorProto, WasmInstance instance)
    {
        var engine = ctx.Engine;
        var exports = new JsObject(engine);
        foreach (var (name, function) in instance.GetFunctions())
        {
            exports.FastSetProperty(name, new PropertyDescriptor(BuildExportedFunction(ctx, state, runtimeErrorProto, function, name), writable: true, enumerable: true, configurable: true));
        }

        foreach (var (name, memory) in instance.GetMemories())
        {
            var memoryObject = new WasmMemoryObject(engine, memoryProto, memory);
            state.RegisterMemory(memoryObject);
            exports.FastSetProperty(name, new PropertyDescriptor(memoryObject, writable: true, enumerable: true, configurable: true));
        }
        foreach (var (name, table) in instance.GetTables())
        {
            exports.FastSetProperty(name, new PropertyDescriptor(new WasmTableObject(engine, tableProto, table, runtimeErrorProto, instance), writable: true, enumerable: true, configurable: true));
        }

        return exports;
    }

    private static JsObject BuildInstantiateResult(global::Jint.Engine engine, WasmModuleObject moduleWrapper, WasmInstanceObject instanceWrapper)
    {
        var result = new JsObject(engine);
        result.FastSetProperty("module", new PropertyDescriptor(moduleWrapper, writable: true, enumerable: true, configurable: true));
        result.FastSetProperty("instance", new PropertyDescriptor(instanceWrapper, writable: true, enumerable: true, configurable: true));
        return result;
    }

    private static ClrFunction BuildExportedFunction(JintBackendContext ctx, WasmRealmState state, ObjectInstance runtimeErrorProto, WasmFunction function, string exportName)
    {
        var fn = new ClrFunction(ctx.Engine, exportName, (_, args) =>
            InvokeWasmFunction(ctx, state, runtimeErrorProto, function, exportName, args), function.Parameters.Count, PropertyFlag.Configurable);
        WasmFunctionReferences.Add(fn, new WasmFunctionReference(function));
        return fn;
    }

    private static JsValue InvokeWasmFunction(JintBackendContext ctx, WasmRealmState state, ObjectInstance runtimeErrorProto, WasmFunction function, string exportName, JsValue[] args)
    {
        if (t_onWasmExecutionStack)
        {
            if (NeedsFreshReentrantWasmStack(exportName))
            {
                return InvokeOnWasmExecutionStack(() => InvokeWasmFunctionCore(ctx, state, runtimeErrorProto, function, exportName, args));
            }

            return InvokeWasmFunctionCore(ctx, state, runtimeErrorProto, function, exportName, args);
        }
        return InvokeOnWasmExecutionStack(() => InvokeWasmFunctionCore(ctx, state, runtimeErrorProto, function, exportName, args));
    }

    private static bool NeedsFreshReentrantWasmStack(string exportName) =>
        StringComparer.Ordinal.Equals(exportName, "mono_wasm_write_managed_pointer_unsafe")
        || StringComparer.Ordinal.Equals(exportName, "mono_wasm_copy_managed_pointer");

    private static T InvokeOnWasmExecutionStack<T>(Func<T> action)
    {
        T result = default!;
        ExceptionDispatchInfo? exception = null;
        var thread = new Thread(() =>
        {
            t_onWasmExecutionStack = true;
            try { result = action(); }
            catch (Exception ex) { exception = ExceptionDispatchInfo.Capture(ex); }
            finally { t_onWasmExecutionStack = false; }
        }, WasmExecutionStackSize)
        { Name = "Starling Wasmtime invocation (Jint)" };
        thread.Start();
        thread.Join();
        exception?.Throw();
        return result;
    }

    private static JsValue InvokeWasmFunctionCore(JintBackendContext ctx, WasmRealmState state, ObjectInstance runtimeErrorProto, WasmFunction function, string exportName, JsValue[] args)
    {
        var engine = ctx.Engine;
        var parameters = new WasmValueBox[function.Parameters.Count];
        for (var i = 0; i < parameters.Length; i++)
        {
            parameters[i] = ToWasmValue(ctx, Arg(args, i), function.Parameters[i]);
        }

        object? result;
        try { result = function.Invoke(parameters); }
        catch (WasmTrapException ex) when (ex.InnerException is WasmImportException ie)
        { throw new JavaScriptException(NewWasmError(engine, runtimeErrorProto, $"WebAssembly function {exportName} failed: {ie.Message}")); }
        catch (WasmWasmtimeException ex) when (ex.InnerException is WasmImportException ie)
        { throw new JavaScriptException(NewWasmError(engine, runtimeErrorProto, $"WebAssembly function {exportName} failed: {ie.Message}")); }
        catch (WasmTrapException ex) { throw new JavaScriptException(NewWasmError(engine, runtimeErrorProto, $"WebAssembly function {exportName} trapped: {ex.Message}")); }
        catch (WasmWasmtimeException ex) { throw new JavaScriptException(NewWasmError(engine, runtimeErrorProto, $"WebAssembly function {exportName} failed: {ex.Message}")); }
        finally { state.SyncMemoryObjectsFromWasm(); }

        return FromWasmResult(ctx, result, function.Results);
    }

    private static string DescribeThrown(JsValue value)
    {
        if (value.IsObject())
        {
            var message = value.AsObject().Get("message");
            if (!message.IsUndefined())
            {
                return TypeConverter.ToString(message);
            }
        }
        return TypeConverter.ToString(value);
    }

    private static WasmValueBox ToWasmValue(JintBackendContext ctx, JsValue value, WasmValueKind type) => type switch
    {
        WasmValueKind.Int32 => (int)TypeConverter.ToNumber(value),
        WasmValueKind.Int64 => value is JsBigInt ? (long)TypeConverter.ToBigInt(value) : (long)TypeConverter.ToNumber(value),
        WasmValueKind.Float32 => (float)TypeConverter.ToNumber(value),
        WasmValueKind.Float64 => TypeConverter.ToNumber(value),
        _ => throw TypeErr(ctx, $"Unsupported WebAssembly value type '{type}'"),
    };

    private static JsValue FromWasmValue(JintBackendContext ctx, WasmValueBox value, WasmValueKind type) => type switch
    {
        WasmValueKind.Int32 => JintInterop.Num(value.AsInt32()),
        WasmValueKind.Int64 => new JsBigInt(new BigInteger(value.AsInt64())),
        WasmValueKind.Float32 => JintInterop.Num(value.AsSingle()),
        WasmValueKind.Float64 => JintInterop.Num(value.AsDouble()),
        _ => throw TypeErr(ctx, $"Unsupported WebAssembly value type '{type}'"),
    };

    private static JsValue FromWasmResult(JintBackendContext ctx, object? result, IReadOnlyList<WasmValueKind> types) => types.Count switch
    {
        0 => JsValue.Undefined,
        1 => FromWasmObject(ctx, result, types[0]),
        _ => MultiValueResult(ctx, result, types),
    };

    private static JsValue FromWasmObject(JintBackendContext ctx, object? result, WasmValueKind type) => type switch
    {
        WasmValueKind.Int32 => JintInterop.Num(result is int i ? i : Convert.ToInt32(result, CultureInfo.InvariantCulture)),
        WasmValueKind.Int64 => new JsBigInt(new BigInteger(result is long l ? l : Convert.ToInt64(result, CultureInfo.InvariantCulture))),
        WasmValueKind.Float32 => JintInterop.Num(result is float f ? f : Convert.ToSingle(result, CultureInfo.InvariantCulture)),
        WasmValueKind.Float64 => JintInterop.Num(result is double d ? d : Convert.ToDouble(result, CultureInfo.InvariantCulture)),
        _ => throw TypeErr(ctx, $"Unsupported WebAssembly result type '{type}'"),
    };

    private static JsValue FromTableValue(JintBackendContext ctx, WasmRealmState state, WasmTableObject table, object? value)
    {
        if (value is null)
        {
            return JsValue.Null;
        }

        if (value is not WasmFunction function)
        {
            throw TypeErr(ctx, "Unsupported WebAssembly.Table value");
        }

        if (function.IsNull)
        {
            return JsValue.Null;
        }

        if (table.FunctionCache.TryGetValue(function, out var cached))
        {
            return cached;
        }

        var exportName = table.Owner is null ? null : FindFunctionExportName(table.Owner, function);
        var fn = new ClrFunction(ctx.Engine, exportName ?? "wasm table function", (_, args) =>
            InvokeWasmFunction(ctx, state, table.RuntimeErrorPrototype ?? (ObjectInstance)ctx.Engine.Intrinsics.TypeError.Get("prototype"), function, exportName ?? "table", args),
            function.Parameters.Count, PropertyFlag.Configurable);
        WasmFunctionReferences.Add(fn, new WasmFunctionReference(function));
        JsValue result = fn;
        table.FunctionCache[function] = result;
        return result;
    }

    private static WasmFunction ToTableValue(JintBackendContext ctx, JsValue value, bool defaultToNull)
    {
        if (value.IsNull() || (defaultToNull && value.IsUndefined()))
        {
            return WasmFunction.Null;
        }

        if (value is not ObjectInstance oi || !WasmFunctionReferences.TryGetValue(oi, out var reference))
        {
            throw TypeErr(ctx, "WebAssembly.Table value must be a wasm function or null");
        }

        return reference.Function;
    }

    private static string? FindFunctionExportName(WasmInstance instance, WasmFunction function)
    {
        foreach (var (name, exported) in instance.GetFunctions())
        {
            if (ReferenceEquals(exported, function))
            {
                return name;
            }
        }

        return null;
    }

    private static JsArray MultiValueResult(JintBackendContext ctx, object? result, IReadOnlyList<WasmValueKind> types)
    {
        object?[] values = result switch
        {
            object?[] array => array,
            ITuple tuple => TupleValues(tuple),
            _ => throw TypeErr(ctx, "Unsupported WebAssembly multi-value result"),
        };
        var items = new List<JsValue>();
        for (var i = 0; i < values.Length && i < types.Count; i++)
        {
            items.Add(FromWasmObject(ctx, values[i], types[i]));
        }

        return new JsArray(ctx.Engine, items.ToArray());
    }

    private static object?[] TupleValues(ITuple tuple)
    {
        var values = new object?[tuple.Length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = tuple[i];
        }

        return values;
    }

    private static int ToNonNegativeInt(JintBackendContext ctx, JsValue value, string name, string owner = "WebAssembly.Memory descriptor")
    {
        var number = TypeConverter.ToNumber(value);
        if (double.IsNaN(number) || number < 0 || number > int.MaxValue)
        {
            throw TypeErr(ctx, $"{owner} '{name}' must be a non-negative integer");
        }

        return (int)number;
    }

    private static uint ToTableIndex(JintBackendContext ctx, JsValue value, ulong length)
    {
        var number = TypeConverter.ToNumber(value);
        if (double.IsNaN(number) || number < 0 || number >= length || number > uint.MaxValue || Math.Truncate(number) != number)
        {
            throw RangeErr(ctx, "WebAssembly.Table index out of bounds");
        }

        return (uint)number;
    }

    private static JsValue StreamBytes(JintBackendContext ctx, JsValue source, Func<byte[], JsValue> onBytes)
    {
        // source is a Promise<Response> or a Response. Resolve, call .arrayBuffer(),
        // then onBytes. Implemented via Promise.resolve(source).then(...).
        var engine = ctx.Engine;
        var resolved = PromiseResolve(engine, source);
        var bytesPromise = PromiseThen(ctx, resolved, new ClrFunction(engine, "", (_, a) =>
        {
            var response = Arg(a, 0);
            if (!response.IsObject())
            {
                throw TypeErr(ctx, "WebAssembly streaming source must resolve to a Response");
            }

            var arrayBuffer = response.AsObject().Get("arrayBuffer");
            if (!arrayBuffer.IsCallable())
            {
                throw TypeErr(ctx, "WebAssembly streaming source has no arrayBuffer method");
            }

            return arrayBuffer.Call(response, System.Array.Empty<JsValue>());
        }, 1, PropertyFlag.Configurable));
        return PromiseThen(ctx, bytesPromise, new ClrFunction(engine, "", (_, a) => onBytes(BytesFromBufferSource(ctx, Arg(a, 0))), 1, PropertyFlag.Configurable));
    }

    private static JsValue PromiseResolve(global::Jint.Engine engine, JsValue value)
    {
        var promiseCtor = engine.Global.Get("Promise");
        var resolve = promiseCtor.AsObject().Get("resolve");
        return resolve.Call(promiseCtor, new[] { value });
    }

    private static JsValue PromiseThen(JintBackendContext ctx, JsValue promise, JsValue onFulfilled)
    {
        if (!promise.IsObject())
        {
            throw TypeErr(ctx, "Expected Promise");
        }

        var then = promise.AsObject().Get("then");
        if (!then.IsCallable())
        {
            throw TypeErr(ctx, "Expected thenable Promise");
        }

        return then.Call(promise, new[] { onFulfilled });
    }

    private static JsValue ResolvedPromise(global::Jint.Engine engine, JsValue value)
    {
        var (promise, resolve, _) = engine.Advanced.RegisterPromise();
        resolve(value);
        return promise;
    }

    private static JsValue RejectedPromise(global::Jint.Engine engine, JsValue reason)
    {
        var (promise, _, reject) = engine.Advanced.RegisterPromise();
        reject(reason);
        return promise;
    }

    // BufferSource → bytes (ArrayBuffer or ArrayBuffer view).
    internal static byte[] BytesFromBufferSource(JintBackendContext ctx, JsValue v)
    {
        if (v.IsUndefined() || v.IsNull())
        {
            return System.Array.Empty<byte>();
        }

        if (v.IsArrayBuffer() && v.AsArrayBuffer() is { } ab)
        {
            return ab;
        }

        if (v is ObjectInstance oi)
        {
            var bufVal = oi.Get("buffer");
            if (bufVal.IsArrayBuffer() && bufVal.AsArrayBuffer() is { } backing)
            {
                var offset = oi.Get("byteOffset").IsNumber() ? (int)oi.Get("byteOffset").AsNumber() : 0;
                var length = oi.Get("byteLength").IsNumber() ? (int)oi.Get("byteLength").AsNumber() : backing.Length;
                if (offset == 0 && (length == 0 || length == backing.Length))
                {
                    return backing;
                }

                if (offset >= 0 && length >= 0 && offset + length <= backing.Length)
                {
                    var slice = new byte[length];
                    System.Array.Copy(backing, offset, slice, 0, length);
                    return slice;
                }
                return backing;
            }
        }
        throw TypeErr(ctx, "Expected a BufferSource (ArrayBuffer or ArrayBuffer view)");
    }
}

// ---- wrapper objects --------------------------------------------------------

internal sealed class WasmRealmState
{
    private readonly List<WasmMemoryObject> _memories = new();
    public WasmRealmState(WasmEngine engine) => Store = new WasmStore(engine);
    public WasmStore Store { get; }
    public void RegisterMemory(WasmMemoryObject memory) { if (!_memories.Contains(memory))
        {
            _memories.Add(memory);
        }
    }
    public void SyncMemoryObjectsFromWasm() { foreach (var m in _memories)
        {
            m.SyncFromWasm();
        }
    }
}

internal sealed class WasmModuleObject : ObjectInstance
{
    public WasmModuleObject(global::Jint.Engine engine, ObjectInstance proto, WasmModule module) : base(engine) { Prototype = proto; Module = module; }
    public WasmModule Module { get; }
}

internal sealed class WasmInstanceObject : ObjectInstance
{
    public WasmInstanceObject(global::Jint.Engine engine, ObjectInstance proto, WasmInstance instance, ObjectInstance exports) : base(engine)
    {
        Prototype = proto;
        Instance = instance;
        FastSetProperty("exports", new PropertyDescriptor(exports, writable: false, enumerable: true, configurable: true));
    }
    public WasmInstance Instance { get; }
}

internal sealed class WasmMemoryObject : ObjectInstance
{
    public WasmMemoryObject(global::Jint.Engine engine, ObjectInstance proto, WasmMemory memory) : base(engine) { Prototype = proto; Memory = memory; }
    public WasmMemory Memory { get; }

    // Snapshot ArrayBuffer of the wasm linear memory. Not zero-copy live-aliased
    // (see the class remarks on WebAssemblyBinding) — re-snapshotted each access.
    public JsValue GetBuffer(global::Jint.Engine engine)
    {
        var length = checked((int)Memory.GetLength());
        var bytes = Memory.GetSpan(0, length).ToArray();
        return engine.Intrinsics.ArrayBuffer.Construct(bytes);
    }

    public void SyncFromWasm() { /* snapshot model: nothing cached to refresh */ }
}

internal sealed class WasmTableObject : ObjectInstance
{
    public WasmTableObject(global::Jint.Engine engine, ObjectInstance proto, WasmTable table, ObjectInstance? runtimeErrorPrototype = null, WasmInstance? owner = null) : base(engine)
    {
        Prototype = proto;
        Table = table;
        RuntimeErrorPrototype = runtimeErrorPrototype;
        Owner = owner;
    }
    public WasmTable Table { get; }
    public ObjectInstance? RuntimeErrorPrototype { get; }
    public WasmInstance? Owner { get; }
    public Dictionary<WasmFunction, JsValue> FunctionCache { get; } = new();
}

internal sealed class WasmFunctionReference
{
    public WasmFunctionReference(WasmFunction function) => Function = function;
    public WasmFunction Function { get; }
}

internal sealed class WasmImportException : Exception
{
    public WasmImportException() { }
    public WasmImportException(string message) : base(message) { }
    public WasmImportException(string message, Exception innerException) : base(message, innerException) { }
}
