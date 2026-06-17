using System.Collections.Concurrent;
using Jint;
using Jint.Native;
using Jint.Native.Object;

namespace Starling.Bindings.Jint;

/// <summary>
/// HTML §12.3 Web Storage on the Jint backend.
/// Mirrors <c>Starling.Bindings/StorageBinding.cs</c>: per-origin
/// <c>localStorage</c> (shared across realms targeting the same scheme://host:port),
/// per-session <c>sessionStorage</c>, insertion-ordered keys, all interface
/// methods (length / key / getItem / setItem / removeItem / clear).
/// </summary>
/// <remarks>
/// In-memory only; the spec asks <c>localStorage</c> to persist across process
/// restarts but per-profile on-disk wiring isn't here yet. Cross-realm
/// <c>storage</c> events are not dispatched.
/// </remarks>
internal static class StorageBinding
{
    private static readonly ConcurrentDictionary<string, StorageStore> LocalStores =
        new(StringComparer.Ordinal);
    private static readonly HashSet<string> InterfaceNames = new(StringComparer.Ordinal)
    {
        "length", "key", "getItem", "setItem", "removeItem", "clear", "constructor",
    };

    public static void Install(JintBackendContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var engine = ctx.Engine;

        var storageProto = BuildStorageProto(engine);
        var origin = OriginFor(WindowBinding.UrlFor(ctx));
        var localStore = LocalStores.GetOrAdd(origin, _ => new StorageStore());

        var local = BuildStorageObject(engine, storageProto, localStore);
        var session = BuildStorageObject(engine, storageProto, new StorageStore());

        JintInterop.DefineDataProp(engine.Global, "localStorage", local,
            writable: true, enumerable: true, configurable: true);
        JintInterop.DefineDataProp(engine.Global, "sessionStorage", session,
            writable: true, enumerable: true, configurable: true);
    }

    /// <summary>Test-only: drop the per-origin localStorage map so suites can
    /// start from a clean slate.</summary>
    public static void ResetForTests() => LocalStores.Clear();

    private static JsObject BuildStorageProto(global::Jint.Engine engine)
    {
        var proto = new JsObject(engine);

        JintInterop.DefineAccessor(engine, proto, "length",
            (thisV, _) => TryStore(thisV) is { } s ? JintInterop.Num(s.Count) : JintInterop.Num(0));

        JintInterop.DefineMethod(engine, proto, "key", (thisV, args) =>
        {
            if (TryStore(thisV) is { } s && args.Length > 0)
            {
                var idx = (int)global::Jint.Runtime.TypeConverter.ToNumber(args[0]);
                var key = s.KeyAt(idx);
                return key is null ? JsValue.Null : JintInterop.Str(key);
            }
            return JsValue.Null;
        }, length: 1);

        JintInterop.DefineMethod(engine, proto, "getItem", (thisV, args) =>
        {
            if (TryStore(thisV) is { } s && args.Length > 0)
            {
                var k = args[0].ToString();
                return s.TryGet(k, out var v) ? JintInterop.Str(v) : JsValue.Null;
            }
            return JsValue.Null;
        }, length: 1);

        JintInterop.DefineMethod(engine, proto, "setItem", (thisV, args) =>
        {
            if (TryStore(thisV) is { } s && args.Length >= 1)
            {
                var k = args[0].ToString();
                var v = args.Length >= 2 ? args[1].ToString() : "undefined";
                s.Set(k, v);
            }
            return JsValue.Undefined;
        }, length: 2);

        JintInterop.DefineMethod(engine, proto, "removeItem", (thisV, args) =>
        {
            if (TryStore(thisV) is { } s && args.Length > 0)
            {
                s.Remove(args[0].ToString());
            }

            return JsValue.Undefined;
        }, length: 1);

        JintInterop.DefineMethod(engine, proto, "clear", (thisV, _) =>
        {
            TryStore(thisV)?.Clear();
            return JsValue.Undefined;
        }, length: 0);

        return proto;
    }

    private static JintStorageObject BuildStorageObject(global::Jint.Engine engine,
        ObjectInstance proto, StorageStore store)
    {
        // HTML §12.3.2 — Storage is a named-property exotic: arbitrary string keys
        // read/write storage items (storage.foo = x persists, delete storage.foo,
        // for-in over keys). Interface members (length/getItem/…) on the prototype
        // are never shadowed.
        var obj = new JintStorageObject(engine, store, InterfaceNames) { Prototype = proto };
        StorageBackings.Add(obj, store);
        return obj;
    }

    private static StorageStore? TryStore(JsValue thisV)
    {
        if (thisV is ObjectInstance oi && StorageBackings.TryGetValue(oi, out var s))
        {
            return s;
        }

        return null;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ObjectInstance, StorageStore>
        StorageBackings = new();

    private static string OriginFor(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return "about:blank";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "about:blank";
        }

        var port = uri.IsDefaultPort ? "" : $":{uri.Port}";
        return $"{uri.Scheme}://{uri.Host}{port}".ToLowerInvariant();
    }
}

/// <summary>HTML §12.3.2 named-property exotic for localStorage/sessionStorage:
/// string keys that are not interface members resolve to storage items.</summary>
internal sealed class JintStorageObject : ObjectInstance
{
    private readonly StorageStore _store;
    private readonly HashSet<string> _interfaceNames;

    public JintStorageObject(global::Jint.Engine engine, StorageStore store, HashSet<string> interfaceNames) : base(engine)
    {
        _store = store;
        _interfaceNames = interfaceNames;
    }

    private bool IsItemKey(JsValue p, out string key)
    {
        key = "";
        if (!p.IsString())
        {
            return false;
        }

        key = p.AsString();
        return !_interfaceNames.Contains(key);
    }

    public override JsValue Get(JsValue property, JsValue receiver)
    {
        if (IsItemKey(property, out var key) && _store.TryGet(key, out var v))
        {
            return JintInterop.Str(v);
        }

        return base.Get(property, receiver);
    }

    public override bool Set(JsValue property, JsValue value, JsValue receiver)
    {
        if (IsItemKey(property, out var key))
        {
            _store.Set(key, value.IsUndefined() ? "undefined" : value.ToString());
            return true;
        }
        return base.Set(property, value, receiver);
    }

    public override global::Jint.Runtime.Descriptors.PropertyDescriptor GetOwnProperty(JsValue property)
    {
        if (IsItemKey(property, out var key) && _store.TryGet(key, out var v))
        {
            return new global::Jint.Runtime.Descriptors.PropertyDescriptor(JintInterop.Str(v), writable: true, enumerable: true, configurable: true);
        }

        return base.GetOwnProperty(property);
    }

    public override bool HasProperty(JsValue property)
    {
        if (IsItemKey(property, out var key) && _store.TryGet(key, out _))
        {
            return true;
        }

        return base.HasProperty(property);
    }

    public override bool Delete(JsValue property)
    {
        if (IsItemKey(property, out var key) && _store.Remove(key))
        {
            return true;
        }

        return base.Delete(property);
    }

    public override List<JsValue> GetOwnPropertyKeys(global::Jint.Runtime.Types types = global::Jint.Runtime.Types.String | global::Jint.Runtime.Types.Symbol)
    {
        var keys = new List<JsValue>();
        if ((types & global::Jint.Runtime.Types.String) != 0)
        {
            foreach (var k in _store.Keys)
            {
                keys.Add(JintInterop.Str(k));
            }
        }

        keys.AddRange(base.GetOwnPropertyKeys(types));
        return keys;
    }
}

internal sealed class StorageStore
{
    private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();

    public int Count => _map.Count;
    public bool TryGet(string key, out string value) => _map.TryGetValue(key, out value!);

    public void Set(string key, string value)
    {
        if (!_map.ContainsKey(key))
        {
            _order.Add(key);
        }

        _map[key] = value;
    }

    public bool Remove(string key)
    {
        if (!_map.Remove(key))
        {
            return false;
        }

        _order.Remove(key);
        return true;
    }

    public void Clear() { _map.Clear(); _order.Clear(); }
    public string? KeyAt(int index) => index >= 0 && index < _order.Count ? _order[index] : null;
    public IReadOnlyList<string> Keys => _order;
}
