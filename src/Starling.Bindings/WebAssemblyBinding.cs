// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Starling.Js.Runtime;
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

namespace Starling.Bindings;

/// <summary>
/// Minimal WebAssembly JavaScript API backed by Wasmtime.NET. This first slice is
/// enough for self-contained numeric modules and gives Blazor WASM a real
/// <c>WebAssembly</c> object to probe before the next browser-runtime gaps.
/// </summary>
public static class WebAssemblyBinding
{
    private const int WasmExecutionStackSize = 16 * 1024 * 1024;
    private const int WasmtimeMaximumStackSize = 2 * 1024 * 1024;

    private static readonly WasmEngine SharedEngine = new(CreateEngineConfig());
    private static readonly ConditionalWeakTable<JsObject, WasmFunctionReference> WasmFunctionReferences = new();

    [ThreadStatic]
    private static bool t_onWasmExecutionStack;

    private static WasmConfig CreateEngineConfig()
    {
        var config = new WasmConfig();
        return config
            .WithReferenceTypes(true)
            .WithSIMD(true)
            .WithRelaxedSIMD(enable: true, deterministic: true)
            .WithBulkMemory(true)
            .WithMultiValue(true)
            .WithMaximumStackSize(WasmtimeMaximumStackSize);
    }

    public static void Install(JsRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        var realm = runtime.Realm;
        if (realm.GlobalObject.GetOwnPropertyDescriptor("WebAssembly") is not null)
        {
            return;
        }

        var state = new WasmRealmState(SharedEngine);
        var moduleProto = new JsObject(realm.ObjectPrototype);
        var instanceProto = new JsObject(realm.ObjectPrototype);
        var memoryProto = new JsObject(realm.ObjectPrototype);
        var tableProto = new JsObject(realm.ObjectPrototype);
        var compileErrorProto = new JsObject(realm.ErrorPrototype);
        var linkErrorProto = new JsObject(realm.ErrorPrototype);
        var runtimeErrorProto = new JsObject(realm.ErrorPrototype);

        var moduleCtor = new JsNativeFunction(realm, "Module", 1, (_, args) =>
            JsValue.Object(new WasmModuleObject(
                moduleProto,
                DecodeModule(realm, compileErrorProto, Arg(args, 0)))),
            isConstructor: true);
        WireConstructor(moduleCtor, moduleProto, "Module");

        var instanceCtor = new JsNativeFunction(realm, "Instance", 1, (_, args) =>
        {
            var module = RequireModule(realm, Arg(args, 0));
            var importObject = args.Length > 1 ? args[1] : JsValue.Undefined;
            return JsValue.Object(BuildInstanceObject(
                state,
                realm,
                instanceProto,
                memoryProto,
                tableProto,
                linkErrorProto,
                runtimeErrorProto,
                module,
                importObject));
        }, isConstructor: true);
        WireConstructor(instanceCtor, instanceProto, "Instance");

        var memoryCtor = new JsNativeFunction(realm, "Memory", 1, (_, args) =>
        {
            var descriptor = Arg(args, 0);
            if (!descriptor.IsObject)
            {
                throw new JsThrow(realm.NewTypeError("WebAssembly.Memory requires a descriptor object"));
            }

            var obj = descriptor.AsObject;
            var initial = ToNonNegativeInt(realm, obj.Get("initial"), "initial");
            var maximum = obj.Get("maximum");
            var memory = new WasmMemory(
                state.Store,
                initial,
                maximum.IsUndefined ? null : ToNonNegativeInt(realm, maximum, "maximum"),
                is64Bit: false);
            var wrapper = new WasmMemoryObject(memoryProto, memory);
            state.RegisterMemory(wrapper);
            return JsValue.Object(wrapper);
        }, isConstructor: true);
        WireConstructor(memoryCtor, memoryProto, "Memory");
        EventTargetBinding.DefineAccessor(realm, memoryProto, "buffer", (thisV, _) =>
        {
            var memory = RequireMemory(realm, thisV);
            return JsValue.Object(memory.GetBuffer(realm.ArrayBufferPrototype));
        });
        EventTargetBinding.DefineMethod(realm, memoryProto, "grow", (thisV, args) =>
        {
            var memoryObject = RequireMemory(realm, thisV);
            var oldPages = memoryObject.Memory.Grow(ToNonNegativeInt(realm, Arg(args, 0), "delta"));
            memoryObject.SyncFromWasm();
            return JsValue.Number(oldPages);
        }, length: 1);

        var tableCtor = new JsNativeFunction(realm, "Table", 1, (_, args) =>
        {
            var descriptor = Arg(args, 0);
            if (!descriptor.IsObject)
            {
                throw new JsThrow(realm.NewTypeError("WebAssembly.Table requires a descriptor object"));
            }

            var obj = descriptor.AsObject;
            var element = JsValue.ToStringValue(obj.Get("element"));
            if (!StringComparer.Ordinal.Equals(element, "anyfunc") &&
                !StringComparer.Ordinal.Equals(element, "funcref"))
            {
                throw new JsThrow(realm.NewTypeError("WebAssembly.Table descriptor 'element' must be 'funcref'"));
            }

            var initial = (uint)ToNonNegativeInt(
                realm, obj.Get("initial"), "initial", "WebAssembly.Table descriptor");
            var maximum = obj.Get("maximum");
            var maximumElements = maximum.IsUndefined
                ? uint.MaxValue
                : (uint)ToNonNegativeInt(realm, maximum, "maximum", "WebAssembly.Table descriptor");
            var table = new WasmTable(state.Store, WasmTableKind.FuncRef, WasmFunction.Null, initial, maximumElements);
            return JsValue.Object(new WasmTableObject(tableProto, table));
        }, isConstructor: true);
        WireConstructor(tableCtor, tableProto, "Table");
        EventTargetBinding.DefineAccessor(realm, tableProto, "length", (thisV, _) =>
            JsValue.Number(RequireTable(realm, thisV).Table.GetSize()));
        EventTargetBinding.DefineMethod(realm, tableProto, "get", (thisV, args) =>
        {
            var table = RequireTable(realm, thisV);
            var index = ToTableIndex(realm, Arg(args, 0), table.Table.GetSize());
            return FromTableValue(state, realm, table, table.Table.GetElement(index));
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, tableProto, "set", (thisV, args) =>
        {
            var table = RequireTable(realm, thisV);
            var index = ToTableIndex(realm, Arg(args, 0), table.Table.GetSize());
            table.Table.SetElement(index, ToTableValue(realm, Arg(args, 1), defaultToNull: false));
            return JsValue.Undefined;
        }, length: 2);
        EventTargetBinding.DefineMethod(realm, tableProto, "grow", (thisV, args) =>
        {
            var table = RequireTable(realm, thisV);
            var delta = (uint)ToNonNegativeInt(realm, Arg(args, 0), "delta", "WebAssembly.Table grow");
            var initial = ToTableValue(realm, Arg(args, 1), defaultToNull: true);
            var oldLength = table.Table.Grow(delta, initial);
            return JsValue.Number(oldLength);
        }, length: 1);

        var wasm = new JsObject(realm.ObjectPrototype);
        wasm.DefineOwnProperty("Module",
            PropertyDescriptor.Data(JsValue.Object(moduleCtor), writable: true, enumerable: false, configurable: true));
        wasm.DefineOwnProperty("Instance",
            PropertyDescriptor.Data(JsValue.Object(instanceCtor), writable: true, enumerable: false, configurable: true));
        wasm.DefineOwnProperty("Memory",
            PropertyDescriptor.Data(JsValue.Object(memoryCtor), writable: true, enumerable: false, configurable: true));
        wasm.DefineOwnProperty("Table",
            PropertyDescriptor.Data(JsValue.Object(tableCtor), writable: true, enumerable: false, configurable: true));

        InstallWasmErrorConstructor(realm, wasm, "CompileError", compileErrorProto);
        InstallWasmErrorConstructor(realm, wasm, "LinkError", linkErrorProto);
        InstallWasmErrorConstructor(realm, wasm, "RuntimeError", runtimeErrorProto);

        EventTargetBinding.DefineMethod(realm, wasm, "compile", (_, args) =>
        {
            try
            {
                return FetchBinding.ResolvedPromise(realm,
                    JsValue.Object(new WasmModuleObject(
                        moduleProto,
                        DecodeModule(realm, compileErrorProto, Arg(args, 0)))));
            }
            catch (JsThrow ex)
            {
                return FetchBinding.RejectedPromise(realm, ex.Value);
            }
            catch (Exception ex)
            {
                return FetchBinding.RejectedPromise(realm, realm.NewTypeError(ex.Message));
            }
        }, length: 1);

        EventTargetBinding.DefineMethod(realm, wasm, "validate", (_, args) =>
        {
            try
            {
                var bytes = CoreWebApiBinding.BytesFromBufferSource(realm, Arg(args, 0));
                return JsValue.Boolean(WasmModule.Validate(SharedEngine, bytes) is null);
            }
            catch
            {
                return JsValue.False;
            }
        }, length: 1);

        EventTargetBinding.DefineMethod(realm, wasm, "instantiate", (_, args) =>
        {
            try
            {
                var importObject = args.Length > 1 ? args[1] : JsValue.Undefined;
                if (Arg(args, 0).IsObject && Arg(args, 0).AsObject is WasmModuleObject moduleObject)
                {
                    var instance = BuildInstanceObject(
                        state,
                        realm,
                        instanceProto,
                        memoryProto,
                        tableProto,
                        linkErrorProto,
                        runtimeErrorProto,
                        moduleObject.Module,
                        importObject);
                    return FetchBinding.ResolvedPromise(realm, JsValue.Object(instance));
                }

                var module = DecodeModule(realm, compileErrorProto, Arg(args, 0));
                var moduleWrapper = new WasmModuleObject(moduleProto, module);
                var instanceWrapper = BuildInstanceObject(
                    state,
                    realm,
                    instanceProto,
                    memoryProto,
                    tableProto,
                    linkErrorProto,
                    runtimeErrorProto,
                    module,
                    importObject);
                return FetchBinding.ResolvedPromise(realm,
                    JsValue.Object(BuildInstantiateResult(realm, moduleWrapper, instanceWrapper)));
            }
            catch (JsThrow ex)
            {
                return FetchBinding.RejectedPromise(realm, ex.Value);
            }
            catch (Exception ex)
            {
                return FetchBinding.RejectedPromise(realm, realm.NewTypeError(ex.Message));
            }
        }, length: 1);

        EventTargetBinding.DefineMethod(realm, wasm, "compileStreaming", (_, args) =>
            StreamBytes(realm, Arg(args, 0), bytes =>
                JsValue.Object(new WasmModuleObject(
                    moduleProto,
                    DecodeModule(realm, compileErrorProto, bytes)))), length: 1);

        EventTargetBinding.DefineMethod(realm, wasm, "instantiateStreaming", (_, args) =>
        {
            var importObject = args.Length > 1 ? args[1] : JsValue.Undefined;
            return StreamBytes(realm, Arg(args, 0), bytes =>
            {
                var module = DecodeModule(realm, compileErrorProto, bytes);
                var moduleWrapper = new WasmModuleObject(moduleProto, module);
                var instanceWrapper = BuildInstanceObject(
                    state,
                    realm,
                    instanceProto,
                    memoryProto,
                    tableProto,
                    linkErrorProto,
                    runtimeErrorProto,
                    module,
                    importObject);
                return JsValue.Object(BuildInstantiateResult(realm, moduleWrapper, instanceWrapper));
            });
        }, length: 1);

        realm.GlobalObject.DefineOwnProperty("WebAssembly",
            PropertyDescriptor.Data(JsValue.Object(wasm), writable: true, enumerable: false, configurable: true));
    }

    private static JsValue Arg(JsValue[] args, int index)
        => index < args.Length ? args[index] : JsValue.Undefined;

    private static void WireConstructor(JsObject ctor, JsObject proto, string name)
    {
        ctor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(proto), writable: false, enumerable: false, configurable: false));
        proto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
        ctor.DefineOwnProperty("name",
            PropertyDescriptor.Data(JsValue.String(name), writable: false, enumerable: false, configurable: true));
    }

    private static void InstallWasmErrorConstructor(
        JsRealm realm,
        JsObject wasm,
        string name,
        JsObject prototype)
    {
        var ctor = new JsNativeFunction(realm, name, 1, (newTarget, args) =>
        {
            var instanceProto = NewTargetPrototype(realm, newTarget, prototype);
            var instance = new JsObject(instanceProto);
            ApplyMessageAndCause(instance, args);
            return JsValue.Object(instance);
        }, isConstructor: true);

        WireConstructor(ctor, prototype, name);
        ctor.SetPrototypeOf(realm.GlobalObject.Get("Error").IsObject
            ? realm.GlobalObject.Get("Error").AsObject
            : realm.FunctionPrototype);
        prototype.DefineOwnProperty("name",
            PropertyDescriptor.Data(JsValue.String(name), writable: true, enumerable: false, configurable: true));
        prototype.DefineOwnProperty("message",
            PropertyDescriptor.Data(JsValue.String(""), writable: true, enumerable: false, configurable: true));
        wasm.DefineOwnProperty(name,
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
    }

    private static JsObject NewTargetPrototype(JsRealm realm, JsValue newTarget, JsObject defaultPrototype)
    {
        if (newTarget.IsObject && AbstractOperations.IsConstructor(newTarget))
        {
            var prototype = AbstractOperations.Get(realm.ActiveVm, newTarget.AsObject, "prototype");
            if (prototype.IsObject)
            {
                return prototype.AsObject;
            }
        }

        return defaultPrototype;
    }

    private static void ApplyMessageAndCause(JsObject instance, JsValue[] args)
    {
        if (args.Length > 0 && !args[0].IsUndefined)
        {
            instance.DefineOwnProperty("message",
                PropertyDescriptor.Data(
                    JsValue.String(JsValue.ToStringValue(args[0])),
                    writable: true,
                    enumerable: false,
                    configurable: true));
        }

        if (args.Length > 1 && args[1].IsObject)
        {
            var options = args[1].AsObject;
            if (options.HasOwn("cause"))
            {
                instance.DefineOwnProperty("cause",
                    PropertyDescriptor.Data(
                        options.Get("cause"),
                        writable: true,
                        enumerable: false,
                        configurable: true));
            }
        }
    }

    private static JsValue NewWasmError(JsObject prototype, string message)
    {
        var error = new JsObject(prototype);
        error.DefineOwnProperty("message",
            PropertyDescriptor.Data(JsValue.String(message), writable: true, enumerable: false, configurable: true));
        return JsValue.Object(error);
    }

    private static WasmModule DecodeModule(JsRealm realm, JsObject compileErrorProto, JsValue value)
    {
        try
        {
            return WasmModule.FromBytes(SharedEngine, "starling", CoreWebApiBinding.BytesFromBufferSource(realm, value));
        }
        catch (JsThrow)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new JsThrow(NewWasmError(compileErrorProto, ex.Message));
        }
    }

    private static WasmModule DecodeModule(JsRealm realm, JsObject compileErrorProto, byte[] bytes)
    {
        try
        {
            return WasmModule.FromBytes(SharedEngine, "starling", bytes);
        }
        catch (Exception ex)
        {
            throw new JsThrow(NewWasmError(compileErrorProto, ex.Message));
        }
    }

    private static WasmModule RequireModule(JsRealm realm, JsValue value)
    {
        if (value.IsObject && value.AsObject is WasmModuleObject module)
        {
            return module.Module;
        }

        throw new JsThrow(realm.NewTypeError("Expected WebAssembly.Module"));
    }

    private static WasmMemoryObject RequireMemory(JsRealm realm, JsValue value)
    {
        if (value.IsObject && value.AsObject is WasmMemoryObject memory)
        {
            return memory;
        }

        throw new JsThrow(realm.NewTypeError("Expected WebAssembly.Memory"));
    }

    private static WasmTableObject RequireTable(JsRealm realm, JsValue value)
    {
        if (value.IsObject && value.AsObject is WasmTableObject table)
        {
            return table;
        }

        throw new JsThrow(realm.NewTypeError("Expected WebAssembly.Table"));
    }

    private static WasmInstanceObject BuildInstanceObject(
        WasmRealmState state,
        JsRealm realm,
        JsObject instanceProto,
        JsObject memoryProto,
        JsObject tableProto,
        JsObject linkErrorProto,
        JsObject runtimeErrorProto,
        WasmModule module,
        JsValue importObject)
    {
        using var linker = new WasmLinker(SharedEngine);
        RegisterImports(state, realm, linker, module, importObject, linkErrorProto);
        WasmInstance instance;
        try
        {
            instance = linker.Instantiate(state.Store, module);
        }
        catch (JsThrow)
        {
            throw;
        }
        catch (WasmTrapException ex)
        {
            throw new JsThrow(NewWasmError(runtimeErrorProto, ex.Message));
        }
        catch (Exception ex)
        {
            throw new JsThrow(NewWasmError(linkErrorProto, ex.Message));
        }
        state.SyncMemoryObjectsFromWasm();
        var exports = BuildExports(state, realm, memoryProto, tableProto, runtimeErrorProto, instance);
        return new WasmInstanceObject(instanceProto, instance, exports);
    }

    private static void RegisterImports(
        WasmRealmState state,
        JsRealm realm,
        WasmLinker linker,
        WasmModule module,
        JsValue importObject,
        JsObject linkErrorProto)
    {
        foreach (var import in module.Imports)
        {
            var value = ResolveImport(importObject, import, linkErrorProto);
            switch (import)
            {
                case WasmFunctionImport functionImport:
                    linker.Define(import.ModuleName, import.Name,
                        BuildHostFunction(state, realm, functionImport, value, linkErrorProto));
                    break;
                case WasmMemoryImport memoryImport:
                    linker.Define(import.ModuleName, import.Name,
                        RequireImportedMemory(memoryImport, value, linkErrorProto));
                    break;
                case WasmTableImport:
                    linker.Define(import.ModuleName, import.Name, RequireTable(realm, value).Table);
                    break;
                default:
                    throw new JsThrow(NewWasmError(
                        linkErrorProto,
                        $"Unsupported WebAssembly import {import.ModuleName}.{import.Name} ({import.GetType().Name})"));
            }
        }
    }

    private static JsValue ResolveImport(JsValue importObject, Wasmtime.Import import, JsObject linkErrorProto)
    {
        if (!importObject.IsObject)
        {
            throw MissingImport(import, linkErrorProto);
        }

        var moduleValue = importObject.AsObject.Get(import.ModuleName);
        if (!moduleValue.IsObject)
        {
            throw MissingImport(import, linkErrorProto);
        }

        var value = moduleValue.AsObject.Get(import.Name);
        if (value.IsUndefined)
        {
            throw MissingImport(import, linkErrorProto);
        }

        return value;
    }

    private static JsThrow MissingImport(Wasmtime.Import import, JsObject linkErrorProto) =>
        new(NewWasmError(linkErrorProto, $"Missing WebAssembly import {import.ModuleName}.{import.Name}"));

    private static WasmFunction BuildHostFunction(
        WasmRealmState state,
        JsRealm realm,
        WasmFunctionImport import,
        JsValue value,
        JsObject linkErrorProto)
    {
        if (!AbstractOperations.IsCallable(value))
        {
            throw new JsThrow(NewWasmError(
                linkErrorProto,
                $"WebAssembly import {import.ModuleName}.{import.Name} must be a function"));
        }

        return WasmFunction.FromCallback(state.Store, (_, args, results) =>
        {
            state.SyncMemoryObjectsFromWasm();
            try
            {
                var jsArgs = new JsValue[args.Length];
                for (var i = 0; i < jsArgs.Length; i++)
                {
                    jsArgs[i] = FromWasmValue(realm, args[i], import.Parameters[i]);
                }

                JsValue result;
                try
                {
                    result = AbstractOperations.Call(realm.ActiveVm, value, JsValue.Undefined, jsArgs);
                }
                catch (JsThrow ex)
                {
                    throw new WasmImportException(
                        $"WebAssembly import {import.ModuleName}.{import.Name} threw: {DescribeThrown(ex.Value)}",
                        ex);
                }

                if (results.Length == 1)
                {
                    results[0] = ToWasmValue(realm, result, import.Results[0]);
                }
                else if (results.Length > 1)
                {
                    if (!result.IsObject)
                    {
                        throw new JsThrow(realm.NewTypeError(
                            $"WebAssembly import {import.ModuleName}.{import.Name} must return an array"));
                    }

                    var obj = result.AsObject;
                    for (var i = 0; i < results.Length; i++)
                    {
                        results[i] = ToWasmValue(
                            realm,
                            obj.Get(i.ToString(CultureInfo.InvariantCulture)),
                            import.Results[i]);
                    }
                }
            }
            finally
            {
                state.SyncMemoryObjectsFromWasm();
            }
        }, import.Parameters, import.Results);
    }

    private static WasmMemory RequireImportedMemory(
        WasmMemoryImport import,
        JsValue value,
        JsObject linkErrorProto)
    {
        if (!value.IsObject || value.AsObject is not WasmMemoryObject memoryObject)
        {
            throw new JsThrow(NewWasmError(
                linkErrorProto,
                $"WebAssembly import {import.ModuleName}.{import.Name} must be a memory"));
        }

        var memory = memoryObject.Memory;
        if (memory.GetSize() < import.Minimum)
        {
            throw new JsThrow(NewWasmError(
                linkErrorProto,
                $"WebAssembly import {import.ModuleName}.{import.Name} memory is smaller than required"));
        }

        return memory;
    }

    private static JsObject BuildExports(
        WasmRealmState state,
        JsRealm realm,
        JsObject memoryProto,
        JsObject tableProto,
        JsObject runtimeErrorProto,
        WasmInstance instance)
    {
        var exports = new JsObject(realm.ObjectPrototype);
        foreach (var (name, function) in instance.GetFunctions())
        {
            exports.DefineOwnProperty(name,
                PropertyDescriptor.Data(
                    BuildExportedFunction(state, realm, runtimeErrorProto, function, name),
                    writable: true, enumerable: true, configurable: true));
        }

        foreach (var (name, memory) in instance.GetMemories())
        {
            var memoryObject = new WasmMemoryObject(memoryProto, memory);
            state.RegisterMemory(memoryObject);
            exports.DefineOwnProperty(name,
                PropertyDescriptor.Data(JsValue.Object(memoryObject),
                    writable: true, enumerable: true, configurable: true));
        }

        foreach (var (name, table) in instance.GetTables())
        {
            exports.DefineOwnProperty(name,
                PropertyDescriptor.Data(
                    JsValue.Object(new WasmTableObject(tableProto, table, runtimeErrorProto, instance)),
                    writable: true,
                    enumerable: true,
                    configurable: true));
        }

        return exports;
    }

    private static JsObject BuildInstantiateResult(
        JsRealm realm,
        WasmModuleObject moduleWrapper,
        WasmInstanceObject instanceWrapper)
    {
        var result = new JsObject(realm.ObjectPrototype);
        result.DefineOwnProperty("module",
            PropertyDescriptor.Data(JsValue.Object(moduleWrapper), writable: true, enumerable: true, configurable: true));
        result.DefineOwnProperty("instance",
            PropertyDescriptor.Data(JsValue.Object(instanceWrapper), writable: true, enumerable: true, configurable: true));
        return result;
    }

    private static JsValue BuildExportedFunction(
        WasmRealmState state,
        JsRealm realm,
        JsObject runtimeErrorProto,
        WasmFunction function,
        string exportName)
    {
        var fn = new JsNativeFunction(realm, exportName, function.Parameters.Count, (_, args) =>
            InvokeWasmFunction(state, realm, runtimeErrorProto, function, exportName, args), isConstructor: false);
        WasmFunctionReferences.Add(fn, new WasmFunctionReference(function));
        return JsValue.Object(fn);
    }

    private static JsValue InvokeWasmFunction(
        WasmRealmState state,
        JsRealm realm,
        JsObject runtimeErrorProto,
        WasmFunction function,
        string exportName,
        JsValue[] args)
    {
        if (t_onWasmExecutionStack)
        {
            if (NeedsFreshReentrantWasmStack(exportName))
            {
                return InvokeOnWasmExecutionStack(() =>
                    InvokeWasmFunctionCore(state, realm, runtimeErrorProto, function, exportName, args));
            }

            return InvokeWasmFunctionCore(state, realm, runtimeErrorProto, function, exportName, args);
        }

        return InvokeOnWasmExecutionStack(() =>
            InvokeWasmFunctionCore(state, realm, runtimeErrorProto, function, exportName, args));
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
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                exception = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                t_onWasmExecutionStack = false;
            }
        }, WasmExecutionStackSize)
        {
            Name = "Starling Wasmtime invocation",
        };

        thread.Start();
        thread.Join();
        exception?.Throw();
        return result;
    }

    private static JsValue InvokeWasmFunctionCore(
        WasmRealmState state,
        JsRealm realm,
        JsObject runtimeErrorProto,
        WasmFunction function,
        string exportName,
        JsValue[] args)
    {
        var parameters = new WasmValueBox[function.Parameters.Count];
        for (var i = 0; i < parameters.Length; i++)
        {
            parameters[i] = ToWasmValue(realm, Arg(args, i), function.Parameters[i]);
        }

        object? result;
        try
        {
            result = function.Invoke(parameters);
        }
        catch (WasmTrapException ex) when (ex.InnerException is WasmImportException importException)
        {
            throw new JsThrow(NewWasmError(
                runtimeErrorProto,
                $"WebAssembly function {exportName} failed: {importException.Message}"));
        }
        catch (WasmWasmtimeException ex) when (ex.InnerException is WasmImportException importException)
        {
            throw new JsThrow(NewWasmError(
                runtimeErrorProto,
                $"WebAssembly function {exportName} failed: {importException.Message}"));
        }
        catch (WasmTrapException ex)
        {
            throw new JsThrow(NewWasmError(
                runtimeErrorProto,
                $"WebAssembly function {exportName} trapped: {ex.Message}"));
        }
        catch (WasmWasmtimeException ex)
        {
            throw new JsThrow(NewWasmError(
                runtimeErrorProto,
                $"WebAssembly function {exportName} failed: {ex.Message}"));
        }
        finally
        {
            state.SyncMemoryObjectsFromWasm();
        }

        return FromWasmResult(realm, result, function.Results);
    }

    private static string DescribeThrown(JsValue value)
    {
        if (value.IsObject)
        {
            var message = value.AsObject.Get("message");
            if (!message.IsUndefined)
            {
                return JsValue.ToStringValue(message);
            }
        }

        return JsValue.ToStringValue(value);
    }

    private static WasmValueBox ToWasmValue(JsRealm realm, JsValue value, WasmValueKind type)
    {
        return type switch
        {
            WasmValueKind.Int32 => (int)JsValue.ToNumber(value),
            WasmValueKind.Int64 => value.Kind == JsValueKind.BigInt
                ? (long)value.AsBigInt
                : (long)JsValue.ToNumber(value),
            WasmValueKind.Float32 => (float)JsValue.ToNumber(value),
            WasmValueKind.Float64 => JsValue.ToNumber(value),
            _ => throw new JsThrow(realm.NewTypeError($"Unsupported WebAssembly value type '{type}'")),
        };
    }

    private static JsValue FromWasmValue(JsRealm realm, WasmValueBox value, WasmValueKind type)
    {
        return type switch
        {
            WasmValueKind.Int32 => JsValue.Number(value.AsInt32()),
            WasmValueKind.Int64 => JsValue.BigInt(new BigInteger(value.AsInt64())),
            WasmValueKind.Float32 => JsValue.Number(value.AsSingle()),
            WasmValueKind.Float64 => JsValue.Number(value.AsDouble()),
            _ => throw new JsThrow(realm.NewTypeError($"Unsupported WebAssembly value type '{type}'")),
        };
    }

    private static JsValue FromWasmResult(JsRealm realm, object? result, IReadOnlyList<WasmValueKind> types)
    {
        return types.Count switch
        {
            0 => JsValue.Undefined,
            1 => FromWasmObject(realm, result, types[0]),
            _ => MultiValueResult(realm, result, types),
        };
    }

    private static JsValue FromWasmObject(JsRealm realm, object? result, WasmValueKind type)
    {
        return type switch
        {
            WasmValueKind.Int32 => JsValue.Number(result is int i ? i : Convert.ToInt32(result, CultureInfo.InvariantCulture)),
            WasmValueKind.Int64 => JsValue.BigInt(new BigInteger(result is long l
                ? l
                : Convert.ToInt64(result, CultureInfo.InvariantCulture))),
            WasmValueKind.Float32 => JsValue.Number(result is float f
                ? f
                : Convert.ToSingle(result, CultureInfo.InvariantCulture)),
            WasmValueKind.Float64 => JsValue.Number(result is double d
                ? d
                : Convert.ToDouble(result, CultureInfo.InvariantCulture)),
            _ => throw new JsThrow(realm.NewTypeError($"Unsupported WebAssembly result type '{type}'")),
        };
    }

    private static JsValue FromTableValue(
        WasmRealmState state,
        JsRealm realm,
        WasmTableObject table,
        object? value)
    {
        if (value is null)
        {
            return JsValue.Null;
        }

        if (value is not WasmFunction function)
        {
            throw new JsThrow(realm.NewTypeError("Unsupported WebAssembly.Table value"));
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
        var fn = new JsNativeFunction(realm, exportName ?? "wasm table function",
            function.Parameters.Count, (_, args) =>
                InvokeWasmFunction(
                    state,
                    realm,
                    table.RuntimeErrorPrototype ?? realm.TypeErrorPrototype,
                    function,
                    exportName ?? "table",
                    args),
            isConstructor: false);
        WasmFunctionReferences.Add(fn, new WasmFunctionReference(function));

        var result = JsValue.Object(fn);
        table.FunctionCache[function] = result;
        return result;
    }

    private static WasmFunction ToTableValue(
        JsRealm realm,
        JsValue value,
        bool defaultToNull)
    {
        if (value.IsNull || (defaultToNull && value.IsUndefined))
        {
            return WasmFunction.Null;
        }

        if (!value.IsObject ||
            !WasmFunctionReferences.TryGetValue(value.AsObject, out var reference))
        {
            throw new JsThrow(realm.NewTypeError("WebAssembly.Table value must be a wasm function or null"));
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

    private static JsValue MultiValueResult(JsRealm realm, object? result, IReadOnlyList<WasmValueKind> types)
    {
        object?[] values = result switch
        {
            object?[] array => array,
            ITuple tuple => TupleValues(tuple),
            _ => throw new JsThrow(realm.NewTypeError("Unsupported WebAssembly multi-value result")),
        };

        var arrayObject = new JsArray(realm);
        for (var i = 0; i < values.Length && i < types.Count; i++)
        {
            arrayObject.Push(FromWasmObject(realm, values[i], types[i]));
        }

        return JsValue.Object(arrayObject);
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

    private static int ToNonNegativeInt(
        JsRealm realm,
        JsValue value,
        string name,
        string owner = "WebAssembly.Memory descriptor")
    {
        var number = JsValue.ToNumber(value);
        if (double.IsNaN(number) || number < 0 || number > int.MaxValue)
        {
            throw new JsThrow(realm.NewTypeError($"{owner} '{name}' must be a non-negative integer"));
        }

        return (int)number;
    }

    private static uint ToTableIndex(JsRealm realm, JsValue value, ulong length)
    {
        var number = JsValue.ToNumber(value);
        if (double.IsNaN(number) ||
            number < 0 ||
            number >= length ||
            number > uint.MaxValue ||
            Math.Truncate(number) != number)
        {
            throw new JsThrow(realm.NewRangeError("WebAssembly.Table index out of bounds"));
        }

        return (uint)number;
    }

    private static JsValue StreamBytes(JsRealm realm, JsValue source, Func<byte[], JsValue> onBytes)
    {
        var sourcePromise = PromiseResolve(realm, source);
        var bytesPromise = PromiseThen(realm, sourcePromise, new JsNativeFunction(realm, "", 1, (_, args) =>
        {
            var response = Arg(args, 0);
            if (!response.IsObject)
            {
                throw new JsThrow(realm.NewTypeError("WebAssembly streaming source must resolve to a Response"));
            }

            var arrayBuffer = response.AsObject.Get("arrayBuffer");
            if (!AbstractOperations.IsCallable(arrayBuffer))
            {
                throw new JsThrow(realm.NewTypeError("WebAssembly streaming source has no arrayBuffer method"));
            }

            return AbstractOperations.Call(realm.ActiveVm, arrayBuffer, response, Array.Empty<JsValue>());
        }, isConstructor: false));

        return PromiseThen(realm, bytesPromise, new JsNativeFunction(realm, "", 1, (_, args) =>
        {
            var bytes = CoreWebApiBinding.BytesFromBufferSource(realm, Arg(args, 0));
            return onBytes(bytes);
        }, isConstructor: false));
    }

    private static JsValue PromiseResolve(JsRealm realm, JsValue value)
    {
        if (realm.PromiseConstructor is null)
        {
            throw new InvalidOperationException("Promise not installed");
        }

        var resolve = realm.PromiseConstructor.Get("resolve");
        return AbstractOperations.Call(realm.ActiveVm, resolve,
            JsValue.Object(realm.PromiseConstructor), new[] { value });
    }

    private static JsValue PromiseThen(JsRealm realm, JsValue promise, JsObject onFulfilled)
    {
        if (!promise.IsObject)
        {
            throw new JsThrow(realm.NewTypeError("Expected Promise"));
        }

        var then = promise.AsObject.Get("then");
        if (!AbstractOperations.IsCallable(then))
        {
            throw new JsThrow(realm.NewTypeError("Expected thenable Promise"));
        }

        return AbstractOperations.Call(realm.ActiveVm, then, promise,
            new[] { JsValue.Object(onFulfilled) });
    }
}

internal sealed class WasmRealmState
{
    private readonly List<WasmMemoryObject> _memories = new();

    public WasmRealmState(WasmEngine engine)
        => Store = new WasmStore(engine);

    public WasmStore Store { get; }

    public void RegisterMemory(WasmMemoryObject memory)
    {
        if (!_memories.Contains(memory))
        {
            _memories.Add(memory);
        }
    }

    public void SyncMemoryObjectsFromWasm()
    {
        foreach (var memory in _memories)
        {
            memory.SyncFromWasm();
        }
    }
}

internal sealed class WasmModuleObject : JsObject
{
    public WasmModuleObject(JsObject? prototype, WasmModule module) : base(prototype)
        => Module = module;

    public WasmModule Module { get; }
}

internal sealed class WasmInstanceObject : JsObject
{
    public WasmInstanceObject(JsObject? prototype, WasmInstance instance, JsObject exports) : base(prototype)
    {
        Instance = instance;
        DefineOwnProperty("exports",
            PropertyDescriptor.Data(JsValue.Object(exports), writable: false, enumerable: true, configurable: true));
    }

    public WasmInstance Instance { get; }
}

internal sealed class WasmMemoryObject : JsObject
{
    public WasmMemoryObject(JsObject? prototype, WasmMemory memory) : base(prototype)
        => Memory = memory;

    public WasmMemory Memory { get; }

    private JsArrayBuffer? Buffer { get; set; }

    public JsArrayBuffer GetBuffer(JsObject? prototype)
    {
        if (Buffer is null)
        {
            Buffer = JsArrayBuffer.Wrap(prototype, new WasmMemoryBufferStorage(Memory));
        }
        else
        {
            Buffer.RefreshByteLength();
        }

        return Buffer;
    }

    public void SyncFromWasm()
    {
        Buffer?.RefreshByteLength();
    }
}

internal sealed class WasmMemoryBufferStorage : IJsArrayBufferStorage
{
    private readonly WasmMemory _memory;

    public WasmMemoryBufferStorage(WasmMemory memory)
        => _memory = memory;

    public int ByteLength => checked((int)_memory.GetLength());

    public Span<byte> GetSpan()
    {
        var length = ByteLength;
        return _memory.GetSpan(0, length);
    }
}

internal sealed class WasmTableObject : JsObject
{
    public WasmTableObject(
        JsObject? prototype,
        WasmTable table,
        JsObject? runtimeErrorPrototype = null,
        WasmInstance? owner = null) : base(prototype)
    {
        Table = table;
        RuntimeErrorPrototype = runtimeErrorPrototype;
        Owner = owner;
    }

    public WasmTable Table { get; }

    public JsObject? RuntimeErrorPrototype { get; }

    public WasmInstance? Owner { get; }

    public Dictionary<WasmFunction, JsValue> FunctionCache { get; } = new();
}

internal sealed class WasmFunctionReference
{
    public WasmFunctionReference(WasmFunction function)
        => Function = function;

    public WasmFunction Function { get; }
}

internal sealed class WasmImportException : Exception
{
    public WasmImportException()
    {
    }

    public WasmImportException(string message)
        : base(message)
    {
    }

    public WasmImportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
