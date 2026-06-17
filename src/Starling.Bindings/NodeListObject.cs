using System.Globalization;
using Starling.Dom;
using Starling.Js.Runtime;

namespace Starling.Bindings;

/// <summary>
/// A live NodeList (DOM §4.2.10.2) as a legacy platform object that supports
/// indexed properties only — no named properties (unlike HTMLCollection).
/// Integer indices resolve to nodes and <c>length</c> reflects the current match
/// set; backed by a snapshot function so it tracks the live tree. Used by
/// <c>document.getElementsByName</c>.
/// </summary>
internal sealed class NodeListObject : JsObject
{
    private readonly JsRealm _realm;
    private readonly Func<IReadOnlyList<Node>> _source;

    public NodeListObject(JsRealm realm, JsObject? prototype, Func<IReadOnlyList<Node>> source)
        : base(prototype)
    {
        _realm = realm;
        _source = source;
    }

    private IReadOnlyList<Node> Items => _source();

    public int Count => Items.Count;

    public JsValue Item(int index)
    {
        var items = Items;
        return index >= 0 && index < items.Count
            ? JsValue.Object(DomWrappers.Wrap(_realm, items[index])) : JsValue.Null;
    }

    public IEnumerable<JsValue> Values()
    {
        foreach (var n in Items)
        {
            yield return JsValue.Object(DomWrappers.Wrap(_realm, n));
        }
    }

    // WebIDL "array index": a canonical numeric string in [0, 2^32-2]. 2^32-1 and
    // above are not array indices; a value past int range is always past the end
    // of any real list, so clamp to int.MaxValue (=> out of range => undefined).
    private static bool TryIndex(string name, out int index)
    {
        index = 0;
        if (name.Length == 0)
        {
            return false;
        }

        if (name.Length > 1 && name[0] == '0')
        {
            return false;
        }

        if (!ulong.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var v))
        {
            return false;
        }

        if (v > 4294967294UL)
        {
            return false;
        }

        index = v > int.MaxValue ? int.MaxValue : (int)v;
        return true;
    }

    public override JsValue Get(string name)
    {
        if (TryIndex(name, out var i))
        {
            var items = Items;
            return i < items.Count ? JsValue.Object(DomWrappers.Wrap(_realm, items[i])) : JsValue.Undefined;
        }
        return base.Get(name); // length / item / iterators live on the prototype
    }

    public override bool HasOwn(string name)
    {
        if (TryIndex(name, out var i))
        {
            return i < Items.Count;
        }

        return base.HasOwn(name);
    }

    public override PropertyDescriptor? GetOwnPropertyDescriptor(string name)
    {
        if (TryIndex(name, out var i))
        {
            var items = Items;
            // Indexed properties are read-only (NodeList has no indexed setter), so
            // writable:false — consistent with the Set/Delete/DefineOwnProperty
            // overrides and with WebIDL (a strict-mode list[0] = x must fail).
            return i < items.Count
                ? PropertyDescriptor.Data(JsValue.Object(DomWrappers.Wrap(_realm, items[i])), writable: false, enumerable: true, configurable: true)
                : null;
        }
        return base.GetOwnPropertyDescriptor(name);
    }

    public override IEnumerable<string> Keys
    {
        get
        {
            var count = Items.Count;
            for (var i = 0; i < count; i++)
            {
                yield return i.ToString(CultureInfo.InvariantCulture);
            }

            foreach (var k in base.Keys)
            {
                yield return k; // expando properties
            }
        }
    }

    public override IEnumerable<JsPropertyKey> OwnPropertyKeys
    {
        get
        {
            foreach (var k in Keys)
            {
                yield return JsPropertyKey.String(k);
            }

            foreach (var s in SymbolKeys)
            {
                yield return JsPropertyKey.Symbol(s);
            }
        }
    }

    // Legacy platform object: indexed properties have no setter, so assigning to,
    // deleting, or defining over an array index fails; any other key is an
    // ordinary expando.
    public override void Set(string name, JsValue value)
    {
        if (TryIndex(name, out _))
        {
            return;
        }

        base.Set(name, value);
    }

    public override bool Delete(string name)
    {
        if (TryIndex(name, out _))
        {
            return false;
        }

        return base.Delete(name);
    }

    public override bool DefineOwnProperty(string name, PropertyDescriptor desc)
    {
        if (TryIndex(name, out _))
        {
            return false;
        }

        return base.DefineOwnProperty(name, desc);
    }
}
