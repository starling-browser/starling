namespace Starling.Dom;

/// <summary>
/// Root of a DOM tree. Covers node creation, tree lookup helpers, document
/// convenience accessors, and live-collection invalidation.
/// </summary>
public sealed class Document : Node
{
    public override NodeKind Kind => NodeKind.Document;

    public override string NodeName => "#document";

    public override Document? OwnerDocument
    {
        get => this;
        internal set { /* documents own themselves; setter is a no-op for tree machinery. */ }
    }

    public int MutationVersion { get; private set; }

    internal void BumpMutationVersion() => MutationVersion++;

    /// <summary>Subset of <see cref="MutationVersion"/> that only advances on
    /// mutations a built-in style/layout pass would actually care about
    /// (structural changes, text edits, and value changes to attributes
    /// flagged by <see cref="IsLayoutRelevantAttribute"/>). Used by the
    /// layout-cache invalidation check so a hot-path analytics burst that
    /// only writes <c>data-*</c> / <c>aria-*</c> / framework attributes
    /// doesn't tear down the cached layout.</summary>
    public int LayoutInvalidationVersion { get; private set; }

    internal void BumpLayoutInvalidationVersion() => LayoutInvalidationVersion++;

    // ---- Per-frame layout-mutation batch (incremental layout) ----------------
    //
    // When RecordLayoutMutations is on, every layout-relevant DOM change appends
    // a (target, kind) record here. The incremental layout engine drains the
    // batch each frame to reconcile only the changed subtrees. Off by default,
    // so non-incremental pages pay nothing.

    private List<LayoutMutation>? _layoutMutations;

    /// <summary>Enables per-frame recording of layout-relevant mutations into a
    /// batch the incremental layout engine drains. The engine turns this on for
    /// the live page; it stays off for one-shot renders.</summary>
    public bool RecordLayoutMutations { get; set; }

    internal void RecordLayoutMutation(Node target, LayoutChangeKind kind)
    {
        if (!RecordLayoutMutations)
        {
            return;
        }
        // Only batch mutations on the live tree. Edits to a detached subtree
        // (built before insertion) are subsumed when its connected ancestor is
        // reconciled, and recording them would name unmapped nodes.
        if (!target.IsConnectedToDocument)
        {
            return;
        }

        (_layoutMutations ??= new List<LayoutMutation>()).Add(new LayoutMutation(target, kind));
        NoteRecentlyMutated(target);
    }

    // ---- Recently-mutated element tracker (compositor isolation) --------------
    //
    // Independent of the layout-mutation batch above (which the incremental
    // layout engine drains each frame). The live compositor reads this to promote
    // a just-mutated subtree to its own layer, so the base layer's slice content
    // hash stays stable and serves from cache instead of re-rasterizing the whole
    // viewport. A small per-frame countdown (hysteresis) keeps an element promoted
    // for a few rendered frames after its last mutation, so promotion does not
    // churn as mutations come and go.
    private Dictionary<Element, int>? _recentlyMutated;
    private const int RecentMutationFrames = 3;

    private void NoteRecentlyMutated(Node target)
    {
        var el = target as Element ?? NearestElement(target);
        if (el is null)
        {
            return;
        }

        (_recentlyMutated ??= new Dictionary<Element, int>())[el] = RecentMutationFrames;
    }

    private static Element? NearestElement(Node node)
    {
        for (var n = node.ParentNode; n is not null; n = n.ParentNode)
        {
            if (n is Element e)
            {
                return e;
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="element"/>'s subtree was mutated within
    /// the last few rendered frames (the compositor isolation hysteresis window). The live
    /// compositor promotes such elements so their repaint does not re-raster the
    /// base layer.</summary>
    public bool WasRecentlyMutated(Element element)
        => _recentlyMutated is { } m && m.ContainsKey(element);

    /// <summary>Advances the recently-mutated hysteresis countdown by one rendered
    /// frame, dropping elements whose window has elapsed. The live shell calls
    /// this once per painted frame.</summary>
    public void DecayRecentMutations()
    {
        if (_recentlyMutated is not { Count: > 0 } m)
        {
            return;
        }

        var keys = new List<Element>(m.Keys);
        foreach (var el in keys)
        {
            var ttl = m[el] - 1;
            if (ttl <= 0)
            {
                m.Remove(el);
            }
            else
            {
                m[el] = ttl;
            }
        }
    }

    /// <summary>The mutations recorded since the last drain, WITHOUT draining
    /// them. The live compositor inspects the pending batch before triggering
    /// the relayout that drains it, to decide whether this frame's changes are
    /// confined to subtrees already promoted to their own compositor layers.
    /// Callers must not hold the list across the drain.</summary>
    public IReadOnlyList<LayoutMutation> PeekLayoutMutations()
        => _layoutMutations is { Count: > 0 } pending ? pending : Array.Empty<LayoutMutation>();

    /// <summary>Adds every element currently inside the recently-mutated
    /// promotion window (see <see cref="WasRecentlyMutated"/>) to
    /// <paramref name="into"/>. The live compositor snapshots this when it
    /// rebuilds a layer tree in full, as part of the baseline of elements that
    /// own their own layer in the cached tree.</summary>
    public void CollectRecentlyMutated(ISet<Element> into)
    {
        if (_recentlyMutated is not { Count: > 0 } m)
        {
            return;
        }

        foreach (var el in m.Keys)
        {
            into.Add(el);
        }
    }

    /// <summary>Removes and returns the mutations recorded since the last drain.
    /// Empty when nothing layout-relevant changed (or recording is off).</summary>
    public IReadOnlyList<LayoutMutation> DrainLayoutMutations()
    {
        if (_layoutMutations is null || _layoutMutations.Count == 0)
        {
            return Array.Empty<LayoutMutation>();
        }

        var snapshot = _layoutMutations.ToArray();
        _layoutMutations.Clear();
        return snapshot;
    }

    /// <summary>Per-attribute-name bump counter (diagnostic). Engine reads
    /// this after script execution to surface "which attribute caused all the
    /// re-layouts" without needing a full mutation log.</summary>
    public readonly Dictionary<string, int> AttributeMutationCounts =
        new(StringComparer.OrdinalIgnoreCase);

    internal void NoteAttributeMutation(string attrName)
    {
        AttributeMutationCounts.TryGetValue(attrName, out var n);
        AttributeMutationCounts[attrName] = n + 1;
    }

    /// <summary>
    /// Attribute names that some active stylesheet selects on
    /// (e.g. <c>[data-state="open"]</c> → <c>data-state</c>). Set by the layout
    /// pass from the style engine. Selector-aware invalidation (plan §7): a write
    /// to one of these is layout-relevant even when the static heuristic in
    /// <see cref="IsLayoutRelevantAttribute"/> would treat it as cosmetic, so
    /// author CSS keyed on a <c>data-*</c>/<c>aria-*</c> attribute no longer
    /// misses a recompute when a script flips that attribute. Empty until layout
    /// runs (the default heuristic still applies).
    /// </summary>
    public IReadOnlySet<string> StyleReferencedAttributes { get; set; } = EmptyAttributeSet;

    private static readonly IReadOnlySet<string> EmptyAttributeSet =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a value change on <paramref name="attrName"/> can shift
    /// cascade or layout for this document — the static heuristic
    /// (<see cref="IsLayoutRelevantAttribute"/>) widened by the attributes the
    /// page's own stylesheets actually select on (<see cref="StyleReferencedAttributes"/>).</summary>
    public bool IsAttributeLayoutRelevant(string attrName)
        => IsLayoutRelevantAttribute(attrName) || StyleReferencedAttributes.Contains(attrName);

    /// <summary>True iff a value change on <paramref name="attrName"/> can
    /// shift cascade or layout for an ordinary HTML page.</summary>
    /// <remarks>
    /// <para>Used to suppress <see cref="BumpMutationVersion"/> for
    /// accessibility / metadata attributes that real pages mutate frequently
    /// during their analytics passes but that no built-in style targets —
    /// <c>data-*</c>, <c>aria-*</c>, <c>role</c>, <c>jsname</c>, etc. On
    /// Google's homepage this turns ~350 of ~500 attribute mutations into
    /// no-op bumps, which keeps the layout cache valid through the analytics
    /// noise and skips redundant reflows.</para>
    /// <para>This is only the static half. A page whose author CSS uses an
    /// attribute selector (e.g. <c>[role="button"] { … }</c>) AND mutates that
    /// attribute via script is handled by selector-aware invalidation:
    /// <see cref="IsAttributeLayoutRelevant"/> also consults
    /// <see cref="StyleReferencedAttributes"/>, the attributes the page's own
    /// stylesheets select on. Callers on the invalidation path should use that
    /// instance method, not this static heuristic alone.</para>
    /// </remarks>
    public static bool IsLayoutRelevantAttribute(string attrName)
    {
        if (string.IsNullOrEmpty(attrName))
        {
            return true;
        }

        if (attrName.StartsWith("data-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (attrName.StartsWith("aria-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        // js* covers Google's framework attributes (jsname, jscontroller,
        // jsaction, jsslot, jsl, etc.) — present on most modern Google pages
        // and never targeted by built-in styles.
        if (attrName.StartsWith("js", StringComparison.OrdinalIgnoreCase)
            && attrName.Length > 2 && char.IsLower(attrName[2]))
        {
            return false;
        }

        return attrName switch
        {
            "role" or "tabindex" or "title" or "alt"
                or "href" or "target" or "rel" or "download" or "ping"
                or "for" or "form" or "list" or "autocomplete"
                or "accesskey" or "contenteditable" or "draggable" or "spellcheck"
                or "translate" or "autocapitalize" or "enterkeyhint" or "inputmode"
                or "is" or "slot" or "part" or "exportparts"
                or "itemid" or "itemprop" or "itemref" or "itemscope" or "itemtype"
                => false,
            _ => true,
        };
    }

    /// <summary>
    /// The element with keyboard focus — HTML's <c>document.activeElement</c>.
    /// Set by the shell when the user clicks a focusable control and by
    /// <c>element.focus()</c>/<c>.blur()</c>; read by the <c>:focus</c> cascade,
    /// the text-editing pipeline, and the <c>activeElement</c> JS accessor.
    /// Null when nothing is focused.
    /// </summary>
    public Element? FocusedElement { get; set; }

    private readonly Dictionary<string, List<string>> _autocompleteValues =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> GetAutocompleteValues(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return Array.Empty<string>();
        }

        return _autocompleteValues.TryGetValue(fieldName, out var values)
            ? values.ToArray()
            : Array.Empty<string>();
    }

    internal void RecordAutocompleteValue(string fieldName, string value)
    {
        if (string.IsNullOrWhiteSpace(fieldName) || string.IsNullOrEmpty(value))
        {
            return;
        }

        if (!_autocompleteValues.TryGetValue(fieldName, out var values))
        {
            values = new List<string>();
            _autocompleteValues[fieldName] = values;
        }
        if (!values.Contains(value, StringComparer.Ordinal))
        {
            values.Add(value);
        }
    }

    /// <summary>
    /// Host hook fired when a node is connected into this document's tree (via
    /// <see cref="Node.InsertBefore"/>). The engine subscribes to this so that
    /// <c>&lt;script&gt;</c> elements created and appended at runtime by JS are
    /// fetched and executed through the same compile+run path as parser-found
    /// scripts. Null when no host is attached (pure-DOM usage). Kept off the
    /// public surface — only the engine wires it during script execution.
    /// </summary>
    internal Action<Node>? NodeConnected { get; set; }

    /// <summary>Fired after an attribute mutation on an element in this document:
    /// (element, qualifiedName, oldValue). Subscribed by MutationObserverBinding
    /// to queue attribute MutationRecords. Null when no observer is installed.</summary>
    internal Action<Element, string, string?>? AttributeMutated { get; set; }

    /// <summary>Fired after a childList mutation: (target parent, addedNodes,
    /// removedNodes, previousSibling, nextSibling) — one of added/removed is
    /// non-empty. A DocumentFragment insertion reports all moved children in a
    /// single call (one MutationRecord per DOM §4.3). Subscribed by
    /// MutationObserverBinding to queue childList MutationRecords.</summary>
    internal Action<Node, IReadOnlyList<Node>?, IReadOnlyList<Node>?, Node?, Node?>? ChildListMutated { get; set; }

    /// <summary>Fired after a CharacterData (Text/Comment/CDATA) data change:
    /// (target, oldData). Subscribed by MutationObserverBinding for characterData
    /// MutationRecords.</summary>
    internal Action<Node, string>? CharacterDataMutated { get; set; }

    /// <summary>Raise <see cref="NodeConnected"/> for <paramref name="node"/>
    /// if a host has subscribed. Called from the tree-mutation path.</summary>
    internal void NotifyNodeConnected(Node node) => NodeConnected?.Invoke(node);

    public DocumentType? DocType
    {
        get
        {
            for (var child = FirstChild; child is not null; child = child.NextSibling)
            {
                if (child is DocumentType type)
                {
                    return type;
                }
            }

            return null;
        }
    }

    public QuirksMode Mode { get; internal set; } = QuirksMode.NoQuirks;

    /// <summary>True when this is an HTML document (created by the HTML parser
    /// or <c>createHTMLDocument</c>). False for XML documents created via
    /// <c>implementation.createDocument</c>. Affects name-casing in
    /// <c>createAttribute</c> (HTML lower-cases, XML preserves).</summary>
    public bool IsHtml { get; set; } = true;

    /// <summary>DOM §4.5 — the document's content type. Null defers to the
    /// <see cref="IsHtml"/> default ("text/html" vs "application/xml").
    /// <c>createDocument</c> sets it from the root element's namespace
    /// (HTML → application/xhtml+xml, SVG → image/svg+xml, else application/xml).</summary>
    public string? ContentType { get; set; }

    // ---- DOM §4.9 — createAttribute / createAttributeNS --------------------

    /// <summary>DOM §4.9 createAttribute — creates a new detached Attr node
    /// with the given local name and no namespace. Case is preserved by this
    /// method; the JS binding is responsible for lower-casing on HTML documents.</summary>
    public AttrNode CreateAttribute(string localName)
    {
        ArgumentException.ThrowIfNullOrEmpty(localName);
        return new AttrNode(localName) { OwnerDocument = this };
    }

    /// <summary>DOM §4.9 createAttributeNS — creates a new detached Attr node
    /// with a namespace URI and qualified name. Name validation and
    /// namespace/prefix consistency checks are enforced by the JS binding layer
    /// (ValidateQualifiedName) before calling this method.</summary>
    public AttrNode CreateAttributeNS(string? @namespace, string qualifiedName)
    {
        ArgumentException.ThrowIfNullOrEmpty(qualifiedName);
        var attr = AttrNode.CreateNamespaced(qualifiedName, @namespace);
        attr.OwnerDocument = this;
        return attr;
    }

    public Element CreateElement(string tagName, string? @namespace = null)
    {
        Element e = IsHtmlTemplate(tagName, @namespace)
            ? new HtmlTemplateElement(tagName, @namespace)
            : new Element(tagName, @namespace);
        e.OwnerDocument = this;
        return e;
    }

    /// <summary>DOM §4.5 createElementNS — preserves the qualified name's case and
    /// splits the prefix (unlike <see cref="CreateElement(string,string?)"/>).</summary>
    public Element CreateElementNS(string? @namespace, string qualifiedName)
    {
        // <template> is HTML-only, so the rare createElementNS("…/xhtml",
        // "template") still needs the specialized type for template.content.
        var local = LocalNameOf(qualifiedName);
        Element e = IsHtmlTemplate(local, @namespace)
            ? new HtmlTemplateElement(local, @namespace)
            : Element.CreateNamespaced(@namespace, qualifiedName);
        e.OwnerDocument = this;
        return e;
    }

    // True when (tagName, namespace) names the HTML <template> element. A null or
    // empty namespace is the HTML namespace in this engine's element model.
    private static bool IsHtmlTemplate(string tagName, string? @namespace)
        => string.Equals(tagName, "template", StringComparison.OrdinalIgnoreCase)
           && (string.IsNullOrEmpty(@namespace) || @namespace == Element.HtmlNamespace);

    private static string LocalNameOf(string qualifiedName)
    {
        var i = qualifiedName.IndexOf(':', StringComparison.Ordinal);
        return i >= 0 ? qualifiedName[(i + 1)..] : qualifiedName;
    }

    public Text CreateText(string data) => CreateTextNode(data);

    public Text CreateTextNode(string data)
        => new(data) { OwnerDocument = this };

    public Comment CreateComment(string data)
        => new(data) { OwnerDocument = this };

    public CData CreateCDataSection(string data)
        => new(data) { OwnerDocument = this };

    public ProcessingInstruction CreateProcessingInstruction(string target, string data)
        => new(target, data) { OwnerDocument = this };

    public DocumentFragment CreateDocumentFragment()
        => new() { OwnerDocument = this };

    public DocumentType CreateDocumentType(string name, string publicId = "", string systemId = "")
        => new(name, publicId, systemId) { OwnerDocument = this };

    public Element? DocumentElement
    {
        get
        {
            for (var child = FirstChild; child is not null; child = child.NextSibling)
            {
                if (child is Element element)
                {
                    return element;
                }
            }

            return null;
        }
    }

    public Element? Head
    {
        get
        {
            foreach (var node in Descendants())
            {
                if (node is Element { LocalName: "head" } element)
                {
                    return element;
                }
            }

            return null;
        }
    }

    public Element? Body
    {
        get
        {
            foreach (var node in Descendants())
            {
                if (node is Element { LocalName: "body" } element)
                {
                    return element;
                }
            }

            return null;
        }
    }

    public Element? GetElementById(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        foreach (var node in Descendants())
        {
            if (node is Element element && element.Id == id)
            {
                return element;
            }
        }

        return null;
    }

    /// <summary>The document's doctype child, or null. DOM §4.5 <c>doctype</c>.</summary>
    public DocumentType? Doctype
    {
        get
        {
            for (var n = FirstChild; n is not null; n = n.NextSibling)
            {
                if (n is DocumentType dt)
                {
                    return dt;
                }
            }

            return null;
        }
    }

    public IReadOnlyList<Element> GetElementsByTagName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new LiveElementCollection(this, element =>
            name == "*" || element.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<Element> GetElementsByClassName(string names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var classes = names.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new LiveElementCollection(this, element =>
            classes.Length > 0 && classes.All(element.ClassList.Contains));
    }

    public IReadOnlyList<Element> GetElementsByTagNameNS(string? @namespace, string localName)
    {
        ArgumentNullException.ThrowIfNull(localName);
        var anyNs = @namespace == "*";
        var anyLocal = localName == "*";
        var ns = string.IsNullOrEmpty(@namespace) ? null : @namespace;
        return new LiveElementCollection(this, element =>
            (anyNs || string.Equals(element.Namespace, ns, StringComparison.Ordinal))
            && (anyLocal || element.LocalName.Equals(localName, StringComparison.Ordinal)));
    }

    /// <summary>
    /// DOM §4.5.1 — create and initialize a new Document with a minimal HTML
    /// skeleton: a doctype, an &lt;html&gt; root, a &lt;head&gt; (optionally with
    /// a &lt;title&gt; child whose text is <paramref name="title"/>), and a
    /// &lt;body&gt;. Returns the document ready for use as an off-screen context
    /// for fragment parsing (e.g. DOMImplementation.createHTMLDocument).
    /// </summary>
    /// <param name="title">
    /// The text content for the &lt;title&gt; element. When <c>null</c> the spec
    /// says no &lt;title&gt; element is created; an empty string creates a
    /// &lt;title&gt; with empty text (matching jQuery's
    /// <c>createHTMLDocument("")</c> call).
    /// </param>
    public static Document CreateHtmlDocument(string? title = null)
    {
        var doc = new Document();

        // 1. DOCTYPE
        var doctype = doc.CreateDocumentType("html");
        doc.AppendChild(doctype);

        // 2. <html>
        var html = doc.CreateElement("html");
        doc.AppendChild(html);

        // 3. <head> (+ optional <title>)
        var head = doc.CreateElement("head");
        html.AppendChild(head);
        if (title is not null)
        {
            var titleEl = doc.CreateElement("title");
            titleEl.AppendChild(doc.CreateTextNode(title));
            head.AppendChild(titleEl);
        }

        // 4. <body>
        var body = doc.CreateElement("body");
        html.AppendChild(body);

        return doc;
    }
}

public enum QuirksMode
{
    NoQuirks,
    LimitedQuirks,
    Quirks,
}
