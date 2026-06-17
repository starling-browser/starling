namespace Starling.Dom;

/// <summary>
/// W3C Selection API §3 — Selection. Each Document has at most one Selection
/// (per the spec, exposed via <c>document.getSelection()</c> /
/// <c>window.getSelection()</c>; both return the same instance). The selection
/// has an associated single Range (modern spec; the historical multi-range API
/// from Gecko is no longer in scope — <c>addRange</c> after the first is a no-op).
/// </summary>
/// <remarks>
/// The "direction" tracks which boundary point is the anchor (initial) and which
/// is the focus (movable). <c>collapse</c> creates a directionless caret;
/// <c>addRange</c> sets the direction forwards (anchor=start, focus=end);
/// <c>extend</c> / <c>setBaseAndExtent</c> can produce backwards selections
/// (anchor>focus in tree order), and the underlying Range is then anchored at
/// the lower of the two points.
/// </remarks>
public sealed class DomSelection
{
    public Document Document { get; }

    private DomRange? _range;
    private SelectionDirection _direction = SelectionDirection.Directionless;

    public DomSelection(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Document = document;
    }

    /// <summary>The selection's range (the actual live Range — same identity
    /// returned by <c>getRangeAt(0)</c>), or null when the selection is empty.</summary>
    public DomRange? Range => _range;

    public SelectionDirection Direction => _direction;

    public bool IsCollapsed => _range is null || _range.Collapsed;
    public int RangeCount => _range is null ? 0 : 1;

    public string Type
    {
        get
        {
            if (_range is null)
            {
                return "None";
            }

            return _range.Collapsed ? "Caret" : "Range";
        }
    }

    public Node? AnchorNode
    {
        get
        {
            if (_range is null)
            {
                return null;
            }

            return _direction == SelectionDirection.Backwards ? _range.EndContainer : _range.StartContainer;
        }
    }

    public int AnchorOffset
    {
        get
        {
            if (_range is null)
            {
                return 0;
            }

            return _direction == SelectionDirection.Backwards ? _range.EndOffset : _range.StartOffset;
        }
    }

    public Node? FocusNode
    {
        get
        {
            if (_range is null)
            {
                return null;
            }

            return _direction == SelectionDirection.Backwards ? _range.StartContainer : _range.EndContainer;
        }
    }

    public int FocusOffset
    {
        get
        {
            if (_range is null)
            {
                return 0;
            }

            return _direction == SelectionDirection.Backwards ? _range.StartOffset : _range.EndOffset;
        }
    }

    // -----------------------------------------------------------------------
    // Mutation methods

    /// <summary>Selection API §3.2 addRange. Per the modern spec, only the
    /// first range survives — subsequent calls are a no-op.</summary>
    public void AddRange(DomRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        // Spec: if range's root is not the document, abort.
        if (!ReferenceEquals(Root(range.StartContainer), Document))
        {
            return;
        }

        if (_range is not null)
        {
            return;
        }

        _range = range;
        _direction = SelectionDirection.Directionless;
    }

    /// <summary>Selection API §3.2 removeRange. Throws NotFoundError if range
    /// isn't the current range.</summary>
    public void RemoveRange(DomRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        if (!ReferenceEquals(_range, range))
        {
            throw DomRangeException.Create("NotFoundError", "removeRange: range is not in the selection");
        }

        _range = null;
        _direction = SelectionDirection.Directionless;
    }

    public void RemoveAllRanges()
    {
        _range = null;
        _direction = SelectionDirection.Directionless;
    }

    /// <summary>Selection API §3.2 getRangeAt. The returned Range is the
    /// SAME live object the selection holds — mutating it mutates the selection.</summary>
    public DomRange GetRangeAt(int index)
    {
        if (_range is null || index != 0)
        {
            throw DomRangeException.Create("IndexSizeError", $"getRangeAt: no range at index {index}");
        }

        return _range;
    }

    /// <summary>Selection API §3.2 collapse(node, offset). If node is null,
    /// equivalent to removeAllRanges(). Otherwise validates and creates a
    /// new collapsed Range at (node, offset).</summary>
    public void Collapse(Node? node, int offset)
    {
        if (node is null) { RemoveAllRanges(); return; }
        if (node is DocumentType)
        {
            throw DomRangeException.Create("InvalidNodeTypeError", "collapse: doctype nodes cannot be boundary points");
        }

        ValidateOffset(node, offset, "collapse");
        if (!ReferenceEquals(Root(node), Document))
        {
            return; // spec: silently abort
        }

        _range = new DomRange(Document);
        _range.SetStart(node, offset);
        _range.SetEnd(node, offset);
        _direction = SelectionDirection.Directionless;
    }

    /// <summary>Selection API §3.2 collapseToStart.</summary>
    public void CollapseToStart()
    {
        if (_range is null)
        {
            throw DomRangeException.Create("InvalidStateError", "collapseToStart: no range");
        }

        var n = _range.StartContainer;
        var o = _range.StartOffset;
        _range = new DomRange(Document);
        _range.SetStart(n, o);
        _range.SetEnd(n, o);
        _direction = SelectionDirection.Directionless;
    }

    /// <summary>Selection API §3.2 collapseToEnd.</summary>
    public void CollapseToEnd()
    {
        if (_range is null)
        {
            throw DomRangeException.Create("InvalidStateError", "collapseToEnd: no range");
        }

        var n = _range.EndContainer;
        var o = _range.EndOffset;
        _range = new DomRange(Document);
        _range.SetStart(n, o);
        _range.SetEnd(n, o);
        _direction = SelectionDirection.Directionless;
    }

    /// <summary>Selection API §3.2 extend(node, offset). Per spec:
    /// 1) If node's root isn't the associated Document, silently abort.
    /// 2) Else if range is null, throw InvalidStateError.
    /// 3) Doctype → InvalidNodeTypeError; offset OOB → IndexSizeError.
    /// 4) If anchor's root differs from node's root, the new range is
    ///    just (node, offset)..(node, offset) and direction follows.
    /// 5) Else the new range is anchor..focus (direction set by the order).</summary>
    public void Extend(Node node, int offset)
    {
        ArgumentNullException.ThrowIfNull(node);
        // Step 1: silent no-op when node isn't in our document.
        if (!ReferenceEquals(Root(node), Document))
        {
            return;
        }
        // Step 2: range required.
        if (_range is null)
        {
            throw DomRangeException.Create("InvalidStateError", "extend: no range");
        }
        // Step 3: validate the new focus.
        if (node is DocumentType)
        {
            throw DomRangeException.Create("InvalidNodeTypeError", "extend: doctype nodes cannot be boundary points");
        }

        ValidateOffset(node, offset, "extend");

        var anchorNode = AnchorNode!;
        var anchorOffset = AnchorOffset;

        var newRange = new DomRange(Document);
        if (!ReferenceEquals(Root(anchorNode), Root(node)))
        {
            // Anchor is in a different tree (anchor was detached after selection was set).
            newRange.SetStart(node, offset);
            newRange.SetEnd(node, offset);
            _direction = SelectionDirection.Forwards;
        }
        else
        {
            var cmp = ComparePoints(anchorNode, anchorOffset, node, offset);
            if (cmp <= 0)
            {
                newRange.SetStart(anchorNode, anchorOffset);
                newRange.SetEnd(node, offset);
                _direction = SelectionDirection.Forwards;
            }
            else
            {
                newRange.SetStart(node, offset);
                newRange.SetEnd(anchorNode, anchorOffset);
                _direction = SelectionDirection.Backwards;
            }
        }
        _range = newRange;
    }

    /// <summary>Selection API §3.2 setBaseAndExtent(anchorNode, anchorOffset,
    /// focusNode, focusOffset).</summary>
    public void SetBaseAndExtent(Node anchorNode, int anchorOffset, Node focusNode, int focusOffset)
    {
        ArgumentNullException.ThrowIfNull(anchorNode);
        ArgumentNullException.ThrowIfNull(focusNode);
        ValidateOffset(anchorNode, anchorOffset, "setBaseAndExtent");
        ValidateOffset(focusNode, focusOffset, "setBaseAndExtent");
        if (!ReferenceEquals(Root(anchorNode), Document) || !ReferenceEquals(Root(focusNode), Document))
        {
            return;
        }

        var newRange = new DomRange(Document);
        var cmp = ComparePoints(anchorNode, anchorOffset, focusNode, focusOffset);
        if (cmp <= 0)
        {
            newRange.SetStart(anchorNode, anchorOffset);
            newRange.SetEnd(focusNode, focusOffset);
            _direction = cmp == 0 ? SelectionDirection.Directionless : SelectionDirection.Forwards;
        }
        else
        {
            newRange.SetStart(focusNode, focusOffset);
            newRange.SetEnd(anchorNode, anchorOffset);
            _direction = SelectionDirection.Backwards;
        }
        _range = newRange;
    }

    /// <summary>Selection API §3.2 selectAllChildren. Replaces selection with
    /// a range covering all children of node.</summary>
    public void SelectAllChildren(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node is DocumentType)
        {
            throw DomRangeException.Create("InvalidNodeTypeError", "selectAllChildren: doctype nodes cannot be boundary points");
        }

        if (!ReferenceEquals(Root(node), Document))
        {
            return;
        }

        var len = DomRange.NodeLength(node);
        var newRange = new DomRange(Document);
        newRange.SetStart(node, 0);
        newRange.SetEnd(node, len);
        _range = newRange;
        _direction = SelectionDirection.Directionless;
    }

    /// <summary>Selection API §3.2 containsNode. Returns true if the selection
    /// contains node (fully, or partially if allowPartialContainment).</summary>
    public bool ContainsNode(Node node, bool allowPartialContainment)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (_range is null)
        {
            return false;
        }

        if (!ReferenceEquals(Root(node), Document))
        {
            return false;
        }

        var len = DomRange.NodeLength(node);
        var parent = node.ParentNode;
        if (parent is null)
        {
            // Not in any tree — only contained if root is the selection's root and range covers everything.
            return false;
        }
        var idx = DomRange.IndexOf(node);

        if (allowPartialContainment)
        {
            // Spec: node is partially contained if either endpoint is inside it OR
            // it's strictly between the endpoints.
            // Equivalent: range_start < (node, len) AND range_end > (node, 0).
            // i.e. start comes before node-end AND end comes after node-start.
            var startBeforeNodeEnd = ComparePoints(_range.StartContainer, _range.StartOffset, parent, idx + 1) < 0;
            var endAfterNodeStart = ComparePoints(_range.EndContainer, _range.EndOffset, parent, idx) > 0;
            return startBeforeNodeEnd && endAfterNodeStart;
        }
        else
        {
            // Fully contained: range_start <= (parent, idx) AND range_end >= (parent, idx+1).
            var startBeforeNodeStart = ComparePoints(_range.StartContainer, _range.StartOffset, parent, idx) <= 0;
            var endAfterNodeEnd = ComparePoints(_range.EndContainer, _range.EndOffset, parent, idx + 1) >= 0;
            return startBeforeNodeStart && endAfterNodeEnd;
        }
    }

    /// <summary>Selection API §3.2 deleteFromDocument. Equivalent to calling
    /// deleteContents() on the selection's range.</summary>
    public void DeleteFromDocument()
    {
        _range?.DeleteContents();
    }

    /// <summary>Selection API §3.2 stringify: returns the text of the
    /// selection's range, or empty string when no range.</summary>
    public string Stringify() => _range?.Stringify() ?? string.Empty;

    // -----------------------------------------------------------------------
    // Helpers (duplicated from DomRange — they're private there).

    private static Node Root(Node n)
    {
        var cur = n;
        while (cur.ParentNode is not null)
        {
            cur = cur.ParentNode;
        }

        return cur;
    }

    private static void ValidateOffset(Node node, int offset, string op)
    {
        var len = DomRange.NodeLength(node);
        if (offset < 0 || offset > len)
        {
            throw DomRangeException.Create("IndexSizeError",
                $"{op}: offset {offset} is out of range [0, {len}]");
        }
    }

    private static int ComparePoints(Node nodeA, int offsetA, Node nodeB, int offsetB)
    {
        if (ReferenceEquals(nodeA, nodeB))
        {
            return offsetA.CompareTo(offsetB);
        }

        var ancestorsA = AncestorsFromRoot(nodeA);
        var ancestorsB = AncestorsFromRoot(nodeB);
        var minLen = Math.Min(ancestorsA.Count, ancestorsB.Count);
        int i;
        for (i = 0; i < minLen; i++)
        {
            if (!ReferenceEquals(ancestorsA[i], ancestorsB[i]))
            {
                break;
            }
        }

        if (i == minLen)
        {
            if (ancestorsA.Count < ancestorsB.Count)
            {
                var childInA = ancestorsB[i];
                var childIndex = DomRange.IndexOf(childInA);
                return offsetA <= childIndex ? -1 : 1;
            }
            else
            {
                var childInB = ancestorsA[i];
                var childIndex = DomRange.IndexOf(childInB);
                return childIndex < offsetB ? -1 : 1;
            }
        }

        var siblingA = ancestorsA[i];
        var siblingB = ancestorsB[i];
        for (var s = siblingA.NextSibling; s is not null; s = s.NextSibling)
        {
            if (ReferenceEquals(s, siblingB))
            {
                return -1;
            }
        }

        return 1;
    }

    private static List<Node> AncestorsFromRoot(Node node)
    {
        var chain = new List<Node>();
        for (var n = node; n is not null; n = n.ParentNode)
        {
            chain.Add(n);
        }

        chain.Reverse();
        return chain;
    }
}

public enum SelectionDirection
{
    Directionless,
    Forwards,
    Backwards,
}
