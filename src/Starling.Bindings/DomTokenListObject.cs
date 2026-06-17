using System.Globalization;
using Starling.Dom;
using Starling.Js.Runtime;

namespace Starling.Bindings;

/// <summary>
/// Exotic JS object backing a <c>DOMTokenList</c> (e.g. <c>element.classList</c>).
/// Adds integer-indexed access (<c>classList[0]</c>) over the token set on top of
/// the methods/accessors installed by <c>NodeBindings.BuildDomTokenList</c>; the
/// VM resolves index reads through <see cref="GetOwnPropertyDescriptor"/>.
/// </summary>
internal sealed class DomTokenListObject : JsObject
{
    private readonly DomTokenList _list;

    public DomTokenListObject(JsObject proto, DomTokenList list) : base(proto)
    {
        _list = list;
    }

    // WebIDL "array index": a canonical numeric string in the range
    // [0, 2^32-2]. uint covers the full range (2^32-1 is reserved and not a
    // valid array index), so parse as uint rather than int — otherwise valid
    // large keys like "4294967294" would be mis-treated as ordinary properties.
    private static bool TryIndex(string name, out uint index)
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

        return uint.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out index)
            && index != uint.MaxValue;
    }

    public override JsValue Get(string name)
    {
        if (TryIndex(name, out var i))
        {
            return i < (uint)_list.Count ? JsValue.String(_list[(int)i]) : JsValue.Undefined;
        }

        return base.Get(name);
    }

    public override bool HasOwn(string name)
    {
        if (TryIndex(name, out var i))
        {
            return i < (uint)_list.Count;
        }

        return base.HasOwn(name);
    }

    public override PropertyDescriptor? GetOwnPropertyDescriptor(string name)
    {
        if (TryIndex(name, out var i))
        {
            return i < (uint)_list.Count
                ? PropertyDescriptor.Data(JsValue.String(_list[(int)i]), writable: false, enumerable: true, configurable: true)
                : null;
        }

        return base.GetOwnPropertyDescriptor(name);
    }

    public override IEnumerable<JsPropertyKey> OwnPropertyKeys
    {
        get
        {
            for (var i = 0; i < _list.Count; i++)
            {
                yield return JsPropertyKey.String(i.ToString(CultureInfo.InvariantCulture));
            }

            foreach (var k in base.OwnPropertyKeys)
            {
                yield return k;
            }
        }
    }
}
