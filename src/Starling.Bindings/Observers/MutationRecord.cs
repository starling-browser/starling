using Starling.Dom;
using Starling.Js.Runtime;

namespace Starling.Bindings.Observers;

/// <summary>
/// B5-4 — Helpers for building <c>MutationRecord</c> / <c>IntersectionObserverEntry</c>
/// / <c>ResizeObserverEntry</c> JS objects that the spec's observer callbacks
/// receive. The records are ordinary <see cref="JsObject"/>s with data
/// properties — we don't expose them as constructable classes (browsers don't
/// either; per spec only the platform synthesizes them).
/// </summary>
internal static class ObserverRecords
{
    /// <summary>Build a single <c>MutationRecord</c> (DOM §4.3.3).</summary>
    public static JsObject BuildMutationRecord(
        JsRealm realm,
        string type,
        Node target,
        IReadOnlyList<Node>? addedNodes = null,
        IReadOnlyList<Node>? removedNodes = null,
        Node? previousSibling = null,
        Node? nextSibling = null,
        string? attributeName = null,
        string? attributeNamespace = null,
        string? oldValue = null)
    {
        var rec = new JsObject(realm.MutationRecordPrototype ?? realm.ObjectPrototype);
        SetData(rec, "type", JsValue.String(type));
        SetData(rec, "target", JsValue.Object(DomWrappers.Wrap(realm, target)));
        SetData(rec, "addedNodes", JsValue.Object(BuildNodeArray(realm, addedNodes)));
        SetData(rec, "removedNodes", JsValue.Object(BuildNodeArray(realm, removedNodes)));
        SetData(rec, "previousSibling", previousSibling is null
            ? JsValue.Null
            : JsValue.Object(DomWrappers.Wrap(realm, previousSibling)));
        SetData(rec, "nextSibling", nextSibling is null
            ? JsValue.Null
            : JsValue.Object(DomWrappers.Wrap(realm, nextSibling)));
        SetData(rec, "attributeName", attributeName is null ? JsValue.Null : JsValue.String(attributeName));
        SetData(rec, "attributeNamespace", attributeNamespace is null ? JsValue.Null : JsValue.String(attributeNamespace));
        SetData(rec, "oldValue", oldValue is null ? JsValue.Null : JsValue.String(oldValue));
        return rec;
    }

    /// <summary>Build an <c>IntersectionObserverEntry</c>.</summary>
    public static JsObject BuildIntersectionEntry(
        JsRealm realm,
        Element target,
        double intersectionRatio = 0,
        bool isIntersecting = false,
        double time = 0)
    {
        var entry = new JsObject(realm.IntersectionObserverEntryPrototype ?? realm.ObjectPrototype);
        SetData(entry, "target", JsValue.Object(DomWrappers.Wrap(realm, target)));
        SetData(entry, "intersectionRatio", JsValue.Number(intersectionRatio));
        SetData(entry, "isIntersecting", JsValue.Boolean(isIntersecting));
        SetData(entry, "time", JsValue.Number(time));
        SetData(entry, "boundingClientRect", JsValue.Null);
        SetData(entry, "intersectionRect", JsValue.Null);
        SetData(entry, "rootBounds", JsValue.Null);
        return entry;
    }

    /// <summary>Build a <c>ResizeObserverEntry</c>.</summary>
    public static JsObject BuildResizeEntry(
        JsRealm realm,
        Element target,
        double contentBoxWidth = 0,
        double contentBoxHeight = 0,
        double borderBoxWidth = 0,
        double borderBoxHeight = 0)
    {
        var entry = new JsObject(realm.ResizeObserverEntryPrototype ?? realm.ObjectPrototype);
        SetData(entry, "target", JsValue.Object(DomWrappers.Wrap(realm, target)));
        SetData(entry, "contentRect", JsValue.Null);
        SetData(entry, "contentBoxSize", JsValue.Object(BuildBoxSizeArray(realm, contentBoxWidth, contentBoxHeight)));
        SetData(entry, "borderBoxSize", JsValue.Object(BuildBoxSizeArray(realm, borderBoxWidth, borderBoxHeight)));
        SetData(entry, "devicePixelContentBoxSize", JsValue.Object(BuildBoxSizeArray(realm, contentBoxWidth, contentBoxHeight)));
        return entry;
    }

    private static JsArray BuildNodeArray(JsRealm realm, IReadOnlyList<Node>? nodes)
    {
        if (nodes is null || nodes.Count == 0)
        {
            return new JsArray(realm);
        }

        var items = new List<JsValue>(nodes.Count);
        foreach (var n in nodes)
        {
            items.Add(JsValue.Object(DomWrappers.Wrap(realm, n)));
        }

        return new JsArray(realm, items);
    }

    private static JsArray BuildBoxSizeArray(JsRealm realm, double inlineSize, double blockSize)
    {
        var box = new JsObject(realm.ObjectPrototype);
        SetData(box, "inlineSize", JsValue.Number(inlineSize));
        SetData(box, "blockSize", JsValue.Number(blockSize));
        return new JsArray(realm, new[] { JsValue.Object(box) });
    }

    private static void SetData(JsObject target, string name, JsValue value)
        => target.DefineOwnProperty(name,
            PropertyDescriptor.Data(value, writable: true, enumerable: true, configurable: true));
}
