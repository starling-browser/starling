using System.Globalization;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Native.Symbol;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;
using Starling.Dom;

namespace Starling.Bindings.Jint;

/// <summary>
/// DOM §4.9 Attr / NamedNodeMap for the Jint backend, mirroring the canonical
/// backend's <c>JsNamedNodeMapObject</c> + Element Attr-node methods. Before this,
/// <c>element.attributes</c> returned a plain snapshot array of <c>{name,value}</c>
/// and there were no Attr-node methods. This installs a real <c>NamedNodeMap</c>
/// interface, makes <c>element.attributes</c> a live exotic map of wrapped
/// <see cref="AttrNode"/>s, and adds <c>getAttributeNode</c>/<c>setAttributeNode</c>/
/// … plus <c>document.createAttribute(NS)</c>. The <c>Attr</c> interface itself is
/// installed by <see cref="NodeBindings"/> (its prototype slot + global).
/// </summary>
internal static class AttrBinding
{
    public static void Install(JintBackendContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var engine = ctx.Engine;
        var elProto = ctx.Wrappers.ElementPrototype;
        var docProto = ctx.Wrappers.DocumentPrototype;
        if (elProto is null || docProto is null)
        {
            return;
        }

        // ---- NamedNodeMap.prototype --------------------------------------------
        var proto = new JsObject(engine);
        ctx.Wrappers.NamedNodeMapPrototype = proto;
        JintInterop.DefineAccessor(engine, proto, "length",
            (t, _) => JintInterop.Num(t is JintNamedNodeMapObject m ? m.Length : 0));
        JintInterop.DefineMethod(engine, proto, "item",
            (t, a) => t is JintNamedNodeMapObject m && a.Length > 0
                ? m.WrapAttr(m.GetItem((int)TypeConverter.ToNumber(a[0]))) : JsValue.Null, 1);
        JintInterop.DefineMethod(engine, proto, "getNamedItem",
            (t, a) => t is JintNamedNodeMapObject m && a.Length > 0
                ? m.WrapAttr(m.Element.Attributes.GetNamedItem(TypeConverter.ToString(a[0]))) : JsValue.Null, 1);
        JintInterop.DefineMethod(engine, proto, "getNamedItemNS",
            (t, a) => t is JintNamedNodeMapObject m && a.Length >= 2
                ? m.WrapAttr(m.Element.Attributes.GetNamedItemNS(a[0].IsNull() || a[0].IsUndefined() ? null : TypeConverter.ToString(a[0]), TypeConverter.ToString(a[1])))
                : JsValue.Null, 2);
        JintInterop.DefineMethod(engine, proto, "setNamedItem", (t, a) =>
        {
            if (t is not JintNamedNodeMapObject m)
            {
                return JsValue.Null;
            }

            if (a.Length == 0 || ctx.Wrappers.Unwrap(a[0]) is not AttrNode attr)
            {
                throw new JavaScriptException(engine.Intrinsics.TypeError, "setNamedItem requires an Attr argument");
            }

            return m.WrapAttr(m.Element.Attributes.SetNamedItem(attr));
        }, 1);
        JintInterop.DefineMethod(engine, proto, "setNamedItemNS", (t, a) =>
        {
            if (t is not JintNamedNodeMapObject m)
            {
                return JsValue.Null;
            }

            if (a.Length == 0 || ctx.Wrappers.Unwrap(a[0]) is not AttrNode attr)
            {
                throw new JavaScriptException(engine.Intrinsics.TypeError, "setNamedItemNS requires an Attr argument");
            }

            return m.WrapAttr(m.Element.Attributes.SetNamedItemNS(attr));
        }, 1);
        JintInterop.DefineMethod(engine, proto, "removeNamedItem", (t, a) =>
        {
            if (t is not JintNamedNodeMapObject m)
            {
                return JsValue.Null;
            }

            var name = a.Length > 0 ? TypeConverter.ToString(a[0]) : "";
            var removed = m.Element.Attributes.GetNamedItem(name)
                ?? throw DomExceptionBinding.Throw(ctx, "NotFoundError", "The node was not found.");
            m.Element.Attributes.RemoveNamedItem(name);
            return m.WrapAttr(removed);
        }, 1);
        JintInterop.DefineMethod(engine, proto, "removeNamedItemNS", (t, a) =>
        {
            if (t is not JintNamedNodeMapObject m)
            {
                return JsValue.Null;
            }

            var ns = a.Length > 0 && !a[0].IsNull() && !a[0].IsUndefined() ? TypeConverter.ToString(a[0]) : null;
            var local = a.Length > 1 ? TypeConverter.ToString(a[1]) : "";
            var removed = m.Element.Attributes.GetNamedItemNS(ns, local)
                ?? throw DomExceptionBinding.Throw(ctx, "NotFoundError", "The node was not found.");
            m.Element.Attributes.RemoveNamedItemNS(ns, local);
            return m.WrapAttr(removed);
        }, 2);
        WireInterface(engine, proto, "NamedNodeMap");

        // ---- Element Attr-node methods -----------------------------------------
        JintInterop.DefineMethod(engine, elProto, "getAttributeNode", (t, a) =>
        {
            if (ctx.Wrappers.UnwrapElement(t) is not { } e || a.Length == 0)
            {
                return JsValue.Null;
            }

            var attr = e.Attributes.GetNamedItem(TypeConverter.ToString(a[0]));
            return attr is null ? JsValue.Null : ctx.Wrappers.Wrap(attr);
        }, 1);
        JintInterop.DefineMethod(engine, elProto, "getAttributeNodeNS", (t, a) =>
        {
            if (ctx.Wrappers.UnwrapElement(t) is not { } e || a.Length < 2)
            {
                return JsValue.Null;
            }

            var ns = a[0].IsNull() || a[0].IsUndefined() ? null : TypeConverter.ToString(a[0]);
            var attr = e.Attributes.GetNamedItemNS(ns, TypeConverter.ToString(a[1]));
            return attr is null ? JsValue.Null : ctx.Wrappers.Wrap(attr);
        }, 2);
        JintInterop.DefineMethod(engine, elProto, "setAttributeNode", (t, a) =>
        {
            if (ctx.Wrappers.UnwrapElement(t) is not { } e || a.Length == 0)
            {
                return JsValue.Null;
            }

            if (ctx.Wrappers.Unwrap(a[0]) is not AttrNode attr)
            {
                throw new JavaScriptException(engine.Intrinsics.TypeError, "setAttributeNode requires an Attr argument");
            }

            var old = e.Attributes.SetNamedItem(attr);
            return old is null ? JsValue.Null : ctx.Wrappers.Wrap(old);
        }, 1);
        JintInterop.DefineMethod(engine, elProto, "setAttributeNodeNS", (t, a) =>
        {
            if (ctx.Wrappers.UnwrapElement(t) is not { } e || a.Length == 0)
            {
                return JsValue.Null;
            }

            if (ctx.Wrappers.Unwrap(a[0]) is not AttrNode attr)
            {
                throw new JavaScriptException(engine.Intrinsics.TypeError, "setAttributeNodeNS requires an Attr argument");
            }

            var old = e.Attributes.SetNamedItemNS(attr);
            return old is null ? JsValue.Null : ctx.Wrappers.Wrap(old);
        }, 1);
        JintInterop.DefineMethod(engine, elProto, "removeAttributeNode", (t, a) =>
        {
            if (ctx.Wrappers.UnwrapElement(t) is not { } e || a.Length == 0)
            {
                return JsValue.Null;
            }

            if (ctx.Wrappers.Unwrap(a[0]) is not AttrNode attr)
            {
                throw new JavaScriptException(engine.Intrinsics.TypeError, "removeAttributeNode requires an Attr argument");
            }

            var found = e.Attributes.GetNamedItem(attr.Name);
            if (found is null || !ReferenceEquals(found, attr))
            {
                throw DomExceptionBinding.Throw(ctx, "NotFoundError", "The node was not found.");
            }

            e.Attributes.RemoveNamedItem(attr.Name);
            return ctx.Wrappers.Wrap(attr);
        }, 1);

        // ---- document.createAttribute ------------------------------------------
        // NOTE: createAttributeNS needs AttrNode.CreateNamespaced, which is internal
        // to Starling.Dom and not visible to this assembly — tracked under Tier 4.
        JintInterop.DefineMethod(engine, docProto, "createAttribute", (_, a) =>
        {
            var name = a.Length > 0 ? TypeConverter.ToString(a[0]) : "";
            if (name.Length == 0)
            {
                throw DomExceptionBinding.Throw(ctx, "InvalidCharacterError", "createAttribute: empty name");
            }

            return ctx.Wrappers.Wrap(new AttrNode(name.ToLowerInvariant()));
        }, 1);
    }

    /// <summary>Build the live NamedNodeMap exotic backing
    /// <c>element.attributes</c>.</summary>
    public static JsValue WrapAttributes(JintBackendContext ctx, Element element)
        => new JintNamedNodeMapObject(ctx, element);

    private static void WireInterface(global::Jint.Engine engine, ObjectInstance proto, string name)
    {
        var ctor = new ClrFunction(engine, name,
            (_, _) => throw new JavaScriptException(engine.Intrinsics.TypeError, "Illegal constructor"), 0, PropertyFlag.Configurable);
        ctor.Set("prototype", proto);
        JintInterop.DefineDataProp(proto, "constructor", ctor, writable: true, enumerable: false, configurable: true);
        proto.DefineOwnProperty(GlobalSymbolRegistry.ToStringTag,
            new PropertyDescriptor(JintInterop.Str(name), writable: false, enumerable: false, configurable: true));
        JintInterop.DefineDataProp(engine.Global, name, ctor, writable: true, enumerable: false, configurable: true);
    }
}

/// <summary>Live NamedNodeMap (DOM §4.9.1) backing <c>element.attributes</c>:
/// integer indices and attribute names resolve to wrapped <see cref="AttrNode"/>s
/// against the element's live attribute list. Methods (item/getNamedItem/…) are
/// inherited from %NamedNodeMapPrototype% and are never shadowed by an attribute
/// of the same name.</summary>
internal sealed class JintNamedNodeMapObject : ObjectInstance
{
    private readonly JintBackendContext _ctx;
    public Element Element { get; }

    public JintNamedNodeMapObject(JintBackendContext ctx, Element element) : base(ctx.Engine)
    {
        _ctx = ctx;
        Element = element;
        if (ctx.Wrappers.NamedNodeMapPrototype is { } p)
        {
            Prototype = p;
        }
    }

    public int Length => Element.Attributes.Count;

    public AttrNode? GetItem(int index)
        => index >= 0 && index < Element.Attributes.Count ? Element.Attributes[index] : null;

    public JsValue WrapAttr(AttrNode? attr) => attr is null ? JsValue.Null : _ctx.Wrappers.Wrap(attr);

    private bool IsOnPrototype(JsValue name)
    {
        for (var p = Prototype; p is not null; p = p.Prototype)
        {
            if (p.GetOwnProperty(name) != PropertyDescriptor.Undefined)
            {
                return true;
            }
        }

        return false;
    }

    public override JsValue Get(JsValue property, JsValue receiver)
    {
        if (property.IsString())
        {
            var name = property.AsString();
            if (CollectionIndex.TryIndex(name, out var i))
            {
                return GetItem(i) is { } a ? _ctx.Wrappers.Wrap(a) : JsValue.Undefined;
            }

            if (!IsOnPrototype(property) && Element.Attributes.GetNamedItem(name) is { } attr)
            {
                return _ctx.Wrappers.Wrap(attr);
            }
        }
        return base.Get(property, receiver);
    }

    public override PropertyDescriptor GetOwnProperty(JsValue property)
    {
        if (property.IsString())
        {
            var name = property.AsString();
            if (CollectionIndex.TryIndex(name, out var i))
            {
                if (GetItem(i) is { } a)
                {
                    return new PropertyDescriptor(_ctx.Wrappers.Wrap(a), writable: false, enumerable: true, configurable: true);
                }
            }
            else if (base.GetOwnProperty(property) == PropertyDescriptor.Undefined && !IsOnPrototype(property)
                     && Element.Attributes.GetNamedItem(name) is { } attr)
            {
                return new PropertyDescriptor(_ctx.Wrappers.Wrap(attr), writable: false, enumerable: true, configurable: true);
            }
        }
        return base.GetOwnProperty(property);
    }

    public override bool HasProperty(JsValue property)
    {
        if (property.IsString())
        {
            var name = property.AsString();
            if (CollectionIndex.TryIndex(name, out var i))
            {
                return i < Element.Attributes.Count;
            }

            if (!IsOnPrototype(property) && Element.Attributes.GetNamedItem(name) is not null)
            {
                return true;
            }
        }
        return base.HasProperty(property);
    }

    public override List<JsValue> GetOwnPropertyKeys(Types types = Types.String | Types.Symbol)
    {
        var keys = new List<JsValue>();
        if ((types & Types.String) != 0)
        {
            for (var i = 0; i < Element.Attributes.Count; i++)
            {
                keys.Add(JintInterop.Str(i.ToString(CultureInfo.InvariantCulture)));
            }
        }

        keys.AddRange(base.GetOwnPropertyKeys(types));
        return keys;
    }
}
