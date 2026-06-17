using System.Runtime.CompilerServices;
using Jint.Native;
using Jint.Native.Object;
using Starling.Dom;
using Starling.Dom.Events;

namespace Starling.Bindings.Jint;

/// <summary>
/// Per-engine identity map between real DOM <see cref="EventTarget"/>s and their
/// Jint <see cref="ObjectInstance"/> wrappers, plus the per-engine prototype
/// slots binding families populate and consult. Mirrors
/// <c>Starling.Bindings/DomWrappers.cs</c>: one wrapper per node per engine, so
/// <c>document.body === document.body</c> holds and listener bookkeeping is
/// stable.
/// </summary>
/// <remarks>
/// Stable wrapper contract for binding families:
/// <list type="bullet">
/// <item>set their prototype slot once in their <c>Install</c> (e.g.
/// <see cref="NodePrototype"/>), then</item>
/// <item>call <see cref="GetOrCreate"/> to mint a wrapper whose prototype is the
/// most-derived slot for the node's type, registering identity automatically.</item>
/// </list>
/// The wrapper carries the backing CLR object so <see cref="Unwrap"/> /
/// <see cref="UnwrapNode"/> recover it. Until a family installs its prototype,
/// <see cref="GetOrCreate"/> falls back to the next slot up the chain (Element →
/// Node → Object), so partial binding installs still produce usable wrappers.
/// </remarks>
public sealed class JintDomWrapper
{
    private readonly JintBackendContext _ctx;

    // object → wrapper. The key is the EventTarget (Node/Document/Window host);
    // ConditionalWeakTable keeps identity without rooting the DOM.
    private readonly ConditionalWeakTable<object, ObjectInstance> _wrappers = new();
    // wrapper → backing object, for Unwrap. Stored as a hidden own field is
    // brittle across engines, so we keep a side table here.
    private readonly ConditionalWeakTable<ObjectInstance, object> _backing = new();

    public JintDomWrapper(JintBackendContext ctx)
    {
        _ctx = ctx;
    }

    // ---- prototype slots --------------------------------------------------

    /// <summary>%EventTargetPrototype%.</summary>
    public ObjectInstance? EventTargetPrototype { get; set; }

    /// <summary>%NodePrototype%. Inherits EventTarget.prototype.</summary>
    public ObjectInstance? NodePrototype { get; set; }

    /// <summary>%ElementPrototype%. Inherits Node.prototype.</summary>
    public ObjectInstance? ElementPrototype { get; set; }

    /// <summary>%DocumentPrototype%. Inherits Node.prototype.</summary>
    public ObjectInstance? DocumentPrototype { get; set; }

    /// <summary>%CharacterDataPrototype%. Inherits Node.prototype.</summary>
    public ObjectInstance? CharacterDataPrototype { get; set; }

    /// <summary>%TextPrototype%. Inherits CharacterData.prototype.</summary>
    public ObjectInstance? TextPrototype { get; set; }

    /// <summary>%CommentPrototype%. Inherits CharacterData.prototype.</summary>
    public ObjectInstance? CommentPrototype { get; set; }

    /// <summary>%CDATASectionPrototype%. Inherits Text.prototype.</summary>
    public ObjectInstance? CDataPrototype { get; set; }

    /// <summary>%ProcessingInstructionPrototype%. Inherits CharacterData.prototype.</summary>
    public ObjectInstance? ProcessingInstructionPrototype { get; set; }

    /// <summary>%DocumentFragmentPrototype%. Inherits Node.prototype.</summary>
    public ObjectInstance? DocumentFragmentPrototype { get; set; }

    /// <summary>%DocumentTypePrototype%. Inherits Node.prototype.</summary>
    public ObjectInstance? DocumentTypePrototype { get; set; }

    /// <summary>%AttrPrototype%. Inherits Node.prototype.</summary>
    public ObjectInstance? AttrPrototype { get; set; }

    /// <summary>%NodeListPrototype% — set by <see cref="CollectionsBinding"/>.</summary>
    public ObjectInstance? NodeListPrototype { get; set; }

    /// <summary>%HTMLCollectionPrototype% — set by <see cref="CollectionsBinding"/>.</summary>
    public ObjectInstance? HtmlCollectionPrototype { get; set; }

    /// <summary>%DOMTokenListPrototype% — set by <see cref="CollectionsBinding"/>.</summary>
    public ObjectInstance? DomTokenListPrototype { get; set; }

    /// <summary>%NamedNodeMapPrototype% — set by <see cref="AttrBinding"/>.</summary>
    public ObjectInstance? NamedNodeMapPrototype { get; set; }

    /// <summary>%MutationRecordPrototype% — set by <see cref="MutationObserverBinding"/>.</summary>
    public ObjectInstance? MutationRecordPrototype { get; set; }

    /// <summary>%WindowPrototype%. Inherits EventTarget.prototype.</summary>
    public ObjectInstance? WindowPrototype { get; set; }

    /// <summary>%EventPrototype%.</summary>
    public ObjectInstance? EventPrototype { get; set; }

    /// <summary>%DOMExceptionPrototype% — set by <see cref="DomExceptionBinding"/>.
    /// Bindings reach it via <c>DomExceptionBinding.Make</c>/<c>Throw</c> to mint a
    /// real DOMException value.</summary>
    public ObjectInstance? DomExceptionPrototype { get; set; }

    // ---- identity-mapped wrapping ----

    /// <summary>Wrap an <see cref="EventTarget"/> (Node/Document/Element/…) in
    /// its per-engine JS object, creating + registering it on first use. Returns
    /// <see cref="JsValue.Null"/> for a null target.</summary>
    public JsValue Wrap(EventTarget? target)
    {
        if (target is null)
        {
            return JsValue.Null;
        }

        return GetOrCreate(target);
    }

    /// <summary>Get the existing wrapper for <paramref name="backing"/>, or mint
    /// one whose prototype is the most-derived installed slot for its DOM type.
    /// Use this from a family that wants a specific node wrapped (e.g.
    /// <c>document.documentElement</c>).</summary>
    public ObjectInstance GetOrCreate(EventTarget backing)
    {
        ArgumentNullException.ThrowIfNull(backing);
        if (_wrappers.TryGetValue(backing, out var existing))
        {
            return existing;
        }

        var wrapper = new JsObject(_ctx.Engine);
        var proto = SelectPrototype(backing);
        if (proto is not null)
        {
            wrapper.Prototype = proto;
        }

        _wrappers.Add(backing, wrapper);
        _backing.Add(wrapper, backing);
        return wrapper;
    }

    /// <summary>Bind an existing <paramref name="wrapper"/> (e.g. the realm's
    /// <c>globalThis</c>) to a host <paramref name="backing"/>, so that
    /// <see cref="Unwrap(JsValue)"/> recovers it just like a freshly minted
    /// wrapper. Used by WindowBinding to make <c>window.addEventListener</c>
    /// route through the same EventTarget plumbing as DOM nodes. Idempotent: if
    /// either side is already bound the call is a no-op.</summary>
    public void BindExisting(EventTarget backing, ObjectInstance wrapper)
    {
        ArgumentNullException.ThrowIfNull(backing);
        ArgumentNullException.ThrowIfNull(wrapper);
        if (_wrappers.TryGetValue(backing, out _))
        {
            return;
        }

        if (_backing.TryGetValue(wrapper, out _))
        {
            return;
        }

        _wrappers.Add(backing, wrapper);
        _backing.Add(wrapper, backing);
    }

    /// <summary>Recover the backing CLR object from a wrapper, or <c>null</c> if
    /// the value is not one of this engine's DOM wrappers.</summary>
    public object? Unwrap(JsValue value)
    {
        if (value is ObjectInstance oi && _backing.TryGetValue(oi, out var backing))
        {
            return backing;
        }

        return null;
    }

    /// <summary>Convenience: unwrap to a <see cref="Node"/>, or <c>null</c>.</summary>
    public Node? UnwrapNode(JsValue value) => Unwrap(value) as Node;

    /// <summary>Convenience: unwrap to an <see cref="Element"/>, or <c>null</c>.</summary>
    public Element? UnwrapElement(JsValue value) => Unwrap(value) as Element;

    /// <summary>Convenience: unwrap to a <see cref="Document"/>, or <c>null</c>.</summary>
    public Document? UnwrapDocument(JsValue value) => Unwrap(value) as Document;

    /// <summary>Pick the most-derived installed prototype for a backing object,
    /// falling back up the inheritance chain (Document/Element → Node →
    /// EventTarget) so partial installs still yield usable wrappers.</summary>
    // Order matters: switch arms are evaluated top-to-bottom, so the most-derived
    // host types must precede their bases (Text/Comment/… before CharacterData
    // before Node; Document/Element before Node). Each arm falls back up its
    // inheritance chain so a partial install still yields a usable wrapper.
    private ObjectInstance? SelectPrototype(EventTarget backing) => backing switch
    {
        Document => DocumentPrototype ?? NodePrototype ?? EventTargetPrototype,
        Element => ElementPrototype ?? NodePrototype ?? EventTargetPrototype,
        CData => CDataPrototype ?? TextPrototype ?? CharacterDataPrototype ?? NodePrototype ?? EventTargetPrototype,
        Text => TextPrototype ?? CharacterDataPrototype ?? NodePrototype ?? EventTargetPrototype,
        Comment => CommentPrototype ?? CharacterDataPrototype ?? NodePrototype ?? EventTargetPrototype,
        ProcessingInstruction => ProcessingInstructionPrototype ?? CharacterDataPrototype ?? NodePrototype ?? EventTargetPrototype,
        CharacterData => CharacterDataPrototype ?? NodePrototype ?? EventTargetPrototype,
        DocumentFragment => DocumentFragmentPrototype ?? NodePrototype ?? EventTargetPrototype,
        DocumentType => DocumentTypePrototype ?? NodePrototype ?? EventTargetPrototype,
        AttrNode => AttrPrototype ?? NodePrototype ?? EventTargetPrototype,
        Node => NodePrototype ?? EventTargetPrototype,
        _ => EventTargetPrototype,
    };
}
