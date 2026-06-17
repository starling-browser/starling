using System.Globalization;

namespace Starling.Js.Runtime;

/// <summary>§10.4.3 String exotic object. Wraps a primitive UTF-16 string and
/// synthesises one read-only own data property per code unit, plus
/// <c>"length"</c>, lazily on lookup. The pre-2026-05 implementation eagerly
/// materialised every indexed property at boxing time, which made each
/// implicit <c>ToObject</c> on a string primitive O(n) in the string's
/// length — for real-world pages with 100 KB+ minified bundles boxed inside
/// hot JS loops, this was a multi-minute livelock.</summary>
public sealed class JsStringObject : JsObject
{
    /// <summary>The wrapped primitive string. Spec [[StringData]] internal slot.</summary>
    public string Text { get; }

    public JsStringObject(JsObject? prototype, string text) : base(prototype)
    {
        DisableInlineCache();
        Text = text;
    }

    /// <summary>True iff <paramref name="name"/> is a canonical numeric string
    /// (no leading zeros, no sign) naming an in-range code unit of <see cref="Text"/>.</summary>
    private bool TryIndex(string name, out int index)
    {
        index = 0;
        if (!JsArray.IsArrayIndex(name, out var u))
        {
            return false;
        }

        if (u >= (uint)Text.Length)
        {
            return false;
        }

        index = (int)u;
        return true;
    }

    private static PropertyDescriptor IndexDescriptor(char c) =>
        PropertyDescriptor.Data(JsValue.String(c.ToString()),
            writable: false, enumerable: true, configurable: false);

    private PropertyDescriptor LengthDescriptor() =>
        PropertyDescriptor.Data(JsValue.Number(Text.Length),
            writable: false, enumerable: false, configurable: false);

    public override JsValue Get(string name)
    {
        if (name.Length > 0 && name[0] >= '0' && name[0] <= '9'
            && TryIndex(name, out var idx))
        {
            return JsValue.String(Text[idx].ToString());
        }

        if (name == "length")
        {
            return JsValue.Number(Text.Length);
        }

        return base.Get(name);
    }

    public override bool HasOwn(string name)
    {
        if (name == "length")
        {
            return true;
        }

        if (TryIndex(name, out _))
        {
            return true;
        }

        return base.HasOwn(name);
    }

    public override PropertyDescriptor? GetOwnPropertyDescriptor(string name)
    {
        if (TryIndex(name, out var idx))
        {
            return IndexDescriptor(Text[idx]);
        }

        if (name == "length")
        {
            return LengthDescriptor();
        }

        return base.GetOwnPropertyDescriptor(name);
    }

    public override void Set(string name, JsValue value)
    {
        // §10.4.3.4 [[Set]]: indices and length are non-writable; assignment
        // silently fails (strict-mode throw is enforced at the caller).
        if (name == "length")
        {
            return;
        }

        if (TryIndex(name, out _))
        {
            return;
        }

        base.Set(name, value);
    }

    public override bool DefineOwnProperty(string name, PropertyDescriptor desc)
    {
        // §10.4.3.5: a String exotic's indices and length are non-configurable
        // non-writable data props. The only legal redefinition is one that
        // changes nothing observable. We accept an exact match and reject the
        // rest — close enough to spec for the rare code that touches this and
        // avoids dragging the eager-allocation path back in.
        if (TryIndex(name, out var idx))
        {
            return desc.Equals(IndexDescriptor(Text[idx]));
        }

        if (name == "length")
        {
            return desc.Equals(LengthDescriptor());
        }

        return base.DefineOwnProperty(name, desc);
    }

    public override IEnumerable<string> Keys => OrderedOwnStringKeys();

    public override IEnumerable<string> EnumerableKeys()
    {
        for (var i = 0; i < Text.Length; i++)
        {
            yield return i.ToString(CultureInfo.InvariantCulture);
        }
        // "length" is non-enumerable, skip it.
        foreach (var s in base.EnumerableKeys())
        {
            if (s == "length")
            {
                continue;
            }

            if (TryIndex(s, out _))
            {
                continue;
            }

            yield return s;
        }
    }

    public override IEnumerable<JsPropertyKey> OwnPropertyKeys
    {
        get
        {
            foreach (var s in OrderedOwnStringKeys())
            {
                yield return JsPropertyKey.String(s);
            }

            foreach (var sym in SymbolKeys)
            {
                yield return JsPropertyKey.Symbol(sym);
            }
        }
    }

    private IEnumerable<string> OrderedOwnStringKeys()
    {
        // §10.1.11.1: array-index keys ascending first, then "length" and any
        // other strings added via DefineOwnProperty in creation order.
        for (var i = 0; i < Text.Length; i++)
        {
            yield return i.ToString(CultureInfo.InvariantCulture);
        }

        yield return "length";
        foreach (var s in base.Keys)
        {
            if (s == "length")
            {
                continue;
            }

            if (TryIndex(s, out _))
            {
                continue;
            }

            yield return s;
        }
    }
}
