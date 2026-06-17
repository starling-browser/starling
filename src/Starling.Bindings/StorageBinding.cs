using System.Collections.Concurrent;
using Starling.Dom;
using Starling.Js.Runtime;

namespace Starling.Bindings;

/// <summary>
/// B5-5 — HTML §12.3 Web Storage. Installs <c>localStorage</c> and
/// <c>sessionStorage</c> as <see cref="JsStorage"/> exotic objects whose
/// string bracket-access (<c>storage.foo = 'bar'</c>) is equivalent to
/// <c>storage.setItem('foo', 'bar')</c>.
/// </summary>
/// <remarks>
/// <para><b>Scoping:</b> <see cref="JsStorage"/> instances for
/// <c>localStorage</c> are keyed per origin (<c>scheme://host[:port]</c>)
/// inside a process-wide map, so two realms loaded against the same origin
/// see the same store. <c>sessionStorage</c> is per-realm — a fresh
/// <see cref="JsRuntime"/> always gets a fresh session store, mirroring the
/// "session history of a top-level browsing context" semantics for v1.</para>
/// <para><b>Persistence:</b> both stores are in-memory only. The HTML spec
/// asks <c>localStorage</c> to persist across process restarts; that lands
/// when we wire a per-profile on-disk file (the entries are flat
/// <c>(key, value)</c> strings, so JSON is sufficient). Until then, this
/// binding gives session-grade lifetime — enough for SPA route state, login
/// hints staged before navigation, etc.</para>
/// <para><b>Storage events:</b> the cross-realm <c>storage</c> event isn't
/// dispatched yet — there's currently no concept of "same-origin sibling
/// realms" to notify. Tracked as a follow-up if a real-world site depends on
/// it.</para>
/// </remarks>
public static class StorageBinding
{
    private static readonly ConcurrentDictionary<string, JsStorageStore> LocalStores = new(StringComparer.Ordinal);
    private static readonly HashSet<string> InterfaceNames = new(StringComparer.Ordinal)
    {
        "length", "key", "getItem", "setItem", "removeItem", "clear", "constructor",
    };

    /// <summary>Install <c>localStorage</c> and <c>sessionStorage</c> on the
    /// realm. Idempotent.</summary>
    public static void Install(JsRuntime runtime, Document document, string documentUrl)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var realm = runtime.Realm;
        if (realm.GlobalObject.HasOwn("localStorage"))
        {
            return;
        }

        var storageProto = BuildStorageProto(realm);

        var origin = OriginFor(documentUrl);
        var localStore = LocalStores.GetOrAdd(origin, _ => new JsStorageStore());
        var local = new JsStorage(storageProto, localStore);
        var session = new JsStorage(storageProto, new JsStorageStore());

        realm.GlobalObject.DefineOwnProperty("localStorage",
            PropertyDescriptor.Data(JsValue.Object(local), writable: true, enumerable: true, configurable: true));
        realm.GlobalObject.DefineOwnProperty("sessionStorage",
            PropertyDescriptor.Data(JsValue.Object(session), writable: true, enumerable: true, configurable: true));
    }

    /// <summary>Test-only hook so suites starting from a fresh process never
    /// inherit state from a previous run.</summary>
    public static void ResetForTests() => LocalStores.Clear();

    internal static bool IsInterfaceName(string name) => InterfaceNames.Contains(name);

    private static JsObject BuildStorageProto(JsRealm realm)
    {
        var proto = new JsObject(realm.ObjectPrototype);

        EventTargetBinding.DefineAccessor(realm, proto, "length",
            (thisV, _) => thisV.IsObject && thisV.AsObject is JsStorage s ? JsValue.Number(s.Store.Count) : JsValue.Number(0));

        EventTargetBinding.DefineMethod(realm, proto, "key", (thisV, args) =>
        {
            if (thisV.IsObject && thisV.AsObject is JsStorage s && args.Length > 0)
            {
                var idx = (int)JsValue.ToNumber(args[0]);
                var key = s.Store.KeyAt(idx);
                return key is null ? JsValue.Null : JsValue.String(key);
            }
            return JsValue.Null;
        }, length: 1);

        EventTargetBinding.DefineMethod(realm, proto, "getItem", (thisV, args) =>
        {
            if (thisV.IsObject && thisV.AsObject is JsStorage s && args.Length > 0)
            {
                var k = JsValue.ToStringValue(args[0]);
                return s.Store.TryGet(k, out var v) ? JsValue.String(v) : JsValue.Null;
            }
            return JsValue.Null;
        }, length: 1);

        EventTargetBinding.DefineMethod(realm, proto, "setItem", (thisV, args) =>
        {
            if (thisV.IsObject && thisV.AsObject is JsStorage s && args.Length >= 1)
            {
                var k = JsValue.ToStringValue(args[0]);
                var v = args.Length >= 2 ? JsValue.ToStringValue(args[1]) : "undefined";
                s.Store.Set(k, v);
            }
            return JsValue.Undefined;
        }, length: 2);

        EventTargetBinding.DefineMethod(realm, proto, "removeItem", (thisV, args) =>
        {
            if (thisV.IsObject && thisV.AsObject is JsStorage s && args.Length > 0)
            {
                var k = JsValue.ToStringValue(args[0]);
                s.Store.Remove(k);
            }
            return JsValue.Undefined;
        }, length: 1);

        EventTargetBinding.DefineMethod(realm, proto, "clear", (thisV, _) =>
        {
            if (thisV.IsObject && thisV.AsObject is JsStorage s)
            {
                s.Store.Clear();
            }

            return JsValue.Undefined;
        }, length: 0);

        return proto;
    }

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

/// <summary>Insertion-ordered string→string map backing a single
/// <see cref="JsStorage"/> instance. Separated from <see cref="JsStorage"/>
/// so the per-origin <c>localStorage</c> share a backing store while each
/// realm gets a distinct JS wrapper.</summary>
internal sealed class JsStorageStore
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

    public void Clear()
    {
        _map.Clear();
        _order.Clear();
    }

    public string? KeyAt(int index) => index >= 0 && index < _order.Count ? _order[index] : null;

    public IEnumerable<string> Keys => _order;
}

/// <summary>JS-side <c>Storage</c> exotic object. String property access is
/// routed through the underlying <see cref="JsStorageStore"/> except for the
/// reserved interface names (<c>length</c>, <c>getItem</c>, …) which fall
/// through to the prototype.</summary>
internal sealed class JsStorage : JsObject
{
    public JsStorageStore Store { get; }

    public JsStorage(JsObject proto, JsStorageStore store) : base(proto)
    {
        Store = store;
    }

    public override JsValue Get(string name)
    {
        if (StorageBinding.IsInterfaceName(name))
        {
            return base.Get(name);
        }

        return Store.TryGet(name, out var v) ? JsValue.String(v) : base.Get(name);
    }

    public override void Set(string name, JsValue value)
    {
        if (StorageBinding.IsInterfaceName(name)) { base.Set(name, value); return; }
        Store.Set(name, JsValue.ToStringValue(value));
    }

    // AbstractOperations.Set falls through to DefineOwnProperty when no own
    // property exists yet, so first-write bracket access lands here. Route
    // data writes to the named-setter store; accessor descriptors and
    // interface names go to the prototype/own slot path.
    public override bool DefineOwnProperty(string name, PropertyDescriptor desc)
    {
        if (StorageBinding.IsInterfaceName(name) || desc.IsAccessor)
        {
            return base.DefineOwnProperty(name, desc);
        }

        Store.Set(name, JsValue.ToStringValue(desc.Value));
        return true;
    }

    public override PropertyDescriptor? GetOwnPropertyDescriptor(string name)
    {
        if (!StorageBinding.IsInterfaceName(name) && Store.TryGet(name, out var v))
        {
            return PropertyDescriptor.Data(JsValue.String(v), writable: true, enumerable: true, configurable: true);
        }

        return base.GetOwnPropertyDescriptor(name);
    }

    public override bool Has(string name)
    {
        if (Store.TryGet(name, out _))
        {
            return true;
        }

        return base.Has(name);
    }

    public override bool HasOwn(string name)
    {
        if (Store.TryGet(name, out _))
        {
            return true;
        }

        return base.HasOwn(name);
    }

    public override bool Delete(string name)
    {
        if (Store.Remove(name))
        {
            return true;
        }

        return base.Delete(name);
    }

    public override IEnumerable<string> EnumerableKeys() => Store.Keys;

    public override IEnumerable<string> Keys => Store.Keys.Concat(base.Keys);
}
