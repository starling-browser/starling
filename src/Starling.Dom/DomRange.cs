using System.Runtime.CompilerServices;

namespace Starling.Dom;

/// <summary>
/// DOM §4.6 — Live Range. Represents a contiguous portion of the DOM tree
/// defined by two boundary points (start, end), each a (node, offset) pair.
/// Offsets into CharacterData nodes are code-unit offsets into the character
/// data; offsets into other nodes count child nodes.
/// </summary>
/// <remarks>
/// Named <c>DomRange</c> (not <c>Range</c>) to avoid a collision with
/// <see cref="System.Range"/> and the JS engine's RangeErrorPrototype slot.
/// The JS-visible name is "Range" — the binding layer installs it under that key.
/// </remarks>
public sealed class DomRange
{
    // §4.6: A range has a start and an end, both boundary points.
    // A boundary point is a (node, offset) tuple.

    public Node StartContainer { get; private set; }
    public int StartOffset { get; private set; }
    public Node EndContainer { get; private set; }
    public int EndOffset { get; private set; }

    /// <summary>§4.6 — collapsed: start and end are the same boundary point.</summary>
    public bool Collapsed => ReferenceEquals(StartContainer, EndContainer) && StartOffset == EndOffset;

    /// <summary>§4.6 — commonAncestorContainer: the deepest node that contains
    /// both boundary points.</summary>
    public Node CommonAncestorContainer
    {
        get
        {
            // Walk from start container; stop when we reach an inclusive ancestor of end.
            var container = StartContainer;
            while (!IsInclusiveAncestor(container, EndContainer))
            {
                container = container.ParentNode!;
            }

            return container;
        }
    }

    // -----------------------------------------------------------------------
    // Constructors

    /// <summary>Create a collapsed range at (document, 0) — the §4.6
    /// "new Range()" initializer. The document is the associated document
    /// for the "new Range()" constructor; it is the owner document of the
    /// current page.</summary>
    public DomRange(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        StartContainer = document;
        StartOffset = 0;
        EndContainer = document;
        EndOffset = 0;
        Register(document);
    }

    private DomRange(Node startContainer, int startOffset, Node endContainer, int endOffset)
    {
        StartContainer = startContainer;
        StartOffset = startOffset;
        EndContainer = endContainer;
        EndOffset = endOffset;
        if ((startContainer.OwnerDocument ?? startContainer as Document) is { } d)
        {
            Register(d);
        }
    }

    // ---- Live-range registry (§5.3.4 mutation algorithms) -------------------
    //
    // Per-document weak list of ranges. CharacterData mutations call
    // OnReplaceData on the modified node, and each registered range whose
    // boundary points into that node is shifted per the spec.

    private static readonly ConditionalWeakTable<Document, List<WeakReference<DomRange>>> LiveRanges = new();

    private void Register(Document doc)
    {
        var list = LiveRanges.GetValue(doc, _ => new List<WeakReference<DomRange>>());
        lock (list)
        {
            list.Add(new WeakReference<DomRange>(this));
        }
    }

    /// <summary>DOM §5.3.4 — when a CharacterData node has data replaced at
    /// <paramref name="offset"/> (deleting <paramref name="count"/> code
    /// units, inserting <paramref name="insertedLength"/>), adjust any live
    /// Range whose boundary points into the node.</summary>
    public static void OnReplaceData(CharacterData node, int offset, int count, int insertedLength)
    {
        ArgumentNullException.ThrowIfNull(node);
        var doc = node.OwnerDocument;
        if (doc is null || !LiveRanges.TryGetValue(doc, out var list))
        {
            return;
        }

        List<WeakReference<DomRange>>? snapshot;
        lock (list)
        {
            snapshot = new List<WeakReference<DomRange>>(list);
        }

        foreach (var wr in snapshot)
        {
            if (!wr.TryGetTarget(out var range))
            {
                continue;
            }

            range.ShiftForCharacterDataReplace(node, offset, count, insertedLength);
        }
        // Compact dead refs occasionally.
        lock (list)
        {
            list.RemoveAll(w => !w.TryGetTarget(out _));
        }
    }

    /// <summary>DOM §5.3.4 "split a Text node" — after splitting
    /// <paramref name="original"/> at <paramref name="offset"/>, half of the
    /// data is now in <paramref name="newText"/>. Adjust ranges whose
    /// boundary points fell past the split point.</summary>
    public static void OnSplitText(Text original, Text newText, int offset)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(newText);
        var doc = original.OwnerDocument;
        if (doc is null || !LiveRanges.TryGetValue(doc, out var list))
        {
            return;
        }

        List<WeakReference<DomRange>> snapshot;
        lock (list)
        {
            snapshot = new List<WeakReference<DomRange>>(list);
        }
        // newText is already linked into the tree (the binding inserts it
        // before calling here), so its index is original's index + 1.
        var parent = original.ParentNode;
        var newTextIndex = parent is null ? -1 : IndexOf(newText);
        foreach (var wr in snapshot)
        {
            if (!wr.TryGetTarget(out var range))
            {
                continue;
            }
            // Boundary on original past offset → move to newText with shifted offset.
            if (ReferenceEquals(range.StartContainer, original) && range.StartOffset > offset)
            {
                range.StartContainer = newText;
                range.StartOffset -= offset;
            }
            if (ReferenceEquals(range.EndContainer, original) && range.EndOffset > offset)
            {
                range.EndContainer = newText;
                range.EndOffset -= offset;
            }
            // §5.3.4 splitText special substep — a boundary on the parent whose
            // offset equals newText's index gains 1. (Boundaries strictly past
            // newText's index were already shifted by OnNodeInserted when the
            // binding linked newText into the tree.)
            if (parent is not null)
            {
                if (ReferenceEquals(range.StartContainer, parent) && range.StartOffset == newTextIndex)
                {
                    range.StartOffset++;
                }

                if (ReferenceEquals(range.EndContainer, parent) && range.EndOffset == newTextIndex)
                {
                    range.EndOffset++;
                }
            }
        }
    }

    /// <summary>DOM §5.3.4 "insert" — after a node has been
    /// inserted into <paramref name="parent"/> at <paramref name="index"/>
    /// (inserting <paramref name="count"/> child nodes), shift any live Range
    /// boundary that points at <paramref name="parent"/> past the insertion
    /// point. Called AFTER the node is linked into the tree.</summary>
    public static void OnNodeInserted(Node parent, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(parent);
        var doc = parent.OwnerDocument ?? parent as Document;
        if (doc is null || !LiveRanges.TryGetValue(doc, out var list))
        {
            return;
        }

        List<WeakReference<DomRange>> snapshot;
        lock (list)
        {
            snapshot = new List<WeakReference<DomRange>>(list);
        }

        foreach (var wr in snapshot)
        {
            if (!wr.TryGetTarget(out var range))
            {
                continue;
            }
            // §5.3.4 insert: "For each live range whose start node is parent and
            // start offset is greater than index, increase its start offset by
            // count." Same for end.
            if (ReferenceEquals(range.StartContainer, parent) && range.StartOffset > index)
            {
                range.StartOffset += count;
            }

            if (ReferenceEquals(range.EndContainer, parent) && range.EndOffset > index)
            {
                range.EndOffset += count;
            }
        }
        lock (list)
        {
            list.RemoveAll(w => !w.TryGetTarget(out _));
        }
    }

    /// <summary>DOM §5.3.4 — when <paramref name="node"/> is being removed
    /// from <paramref name="oldParent"/>, ranges whose boundary points were
    /// inside or just past the node collapse / shift accordingly.</summary>
    public static void OnNodeRemoved(Node node, Node oldParent, int oldIndex)
    {
        ArgumentNullException.ThrowIfNull(node);
        var doc = oldParent.OwnerDocument ?? oldParent as Document;
        if (doc is null || !LiveRanges.TryGetValue(doc, out var list))
        {
            return;
        }

        List<WeakReference<DomRange>> snapshot;
        lock (list)
        {
            snapshot = new List<WeakReference<DomRange>>(list);
        }

        foreach (var wr in snapshot)
        {
            if (!wr.TryGetTarget(out var range))
            {
                continue;
            }

            range.ShiftForNodeRemoval(node, oldParent, oldIndex);
        }
    }

    private void ShiftForCharacterDataReplace(CharacterData node, int offset, int count, int insertedLength)
    {
        // §5.3.4 "replace data": for each live range:
        // * start: if startContainer === node and offset < startOffset <= offset+count → startOffset = offset
        // * start: if startContainer === node and startOffset > offset+count → startOffset += insertedLength - count
        // * end: same with endOffset
        if (ReferenceEquals(StartContainer, node))
        {
            if (StartOffset > offset && StartOffset <= offset + count)
            {
                StartOffset = offset;
            }
            else if (StartOffset > offset + count)
            {
                StartOffset += insertedLength - count;
            }
        }
        if (ReferenceEquals(EndContainer, node))
        {
            if (EndOffset > offset && EndOffset <= offset + count)
            {
                EndOffset = offset;
            }
            else if (EndOffset > offset + count)
            {
                EndOffset += insertedLength - count;
            }
        }
    }

    private void ShiftForNodeRemoval(Node removed, Node parent, int oldIndex)
    {
        // §5.3.4 "remove": for each range whose container is an inclusive
        // descendant of removed, set container to parent, offset to oldIndex.
        if (IsInclusiveAncestor(removed, StartContainer))
        {
            StartContainer = parent;
            StartOffset = oldIndex;
        }
        if (IsInclusiveAncestor(removed, EndContainer))
        {
            EndContainer = parent;
            EndOffset = oldIndex;
        }
        // For ranges whose container is the parent and whose offset is greater
        // than oldIndex, decrease by 1 (the removed child shifted everything
        // after it down).
        if (ReferenceEquals(StartContainer, parent) && StartOffset > oldIndex)
        {
            StartOffset--;
        }

        if (ReferenceEquals(EndContainer, parent) && EndOffset > oldIndex)
        {
            EndOffset--;
        }
    }

    // -----------------------------------------------------------------------
    // Setters (§4.6.3)

    /// <summary>§4.6.3 set the start.</summary>
    public void SetStart(Node node, int offset)
    {
        ArgumentNullException.ThrowIfNull(node);
        ValidateNotDocumentType(node, "setStart");
        ValidateOffset(node, offset, "setStart");

        // If the new start is after the old end (or in a different tree),
        // collapse to the new start.
        if (!SameRoot(node, EndContainer) || ComparePoints(node, offset, EndContainer, EndOffset) > 0)
        {
            StartContainer = node;
            StartOffset = offset;
            EndContainer = node;
            EndOffset = offset;
        }
        else
        {
            StartContainer = node;
            StartOffset = offset;
        }
    }

    /// <summary>§4.6.3 set the end.</summary>
    public void SetEnd(Node node, int offset)
    {
        ArgumentNullException.ThrowIfNull(node);
        ValidateNotDocumentType(node, "setEnd");
        ValidateOffset(node, offset, "setEnd");

        // If the new end is before the old start (or in a different tree),
        // collapse to the new end.
        if (!SameRoot(node, StartContainer) || ComparePoints(node, offset, StartContainer, StartOffset) < 0)
        {
            StartContainer = node;
            StartOffset = offset;
            EndContainer = node;
            EndOffset = offset;
        }
        else
        {
            EndContainer = node;
            EndOffset = offset;
        }
    }

    /// <summary>§4.6.3 setStartBefore.</summary>
    public void SetStartBefore(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var parent = node.ParentNode ?? throw DomRangeException.Create("InvalidNodeTypeError",
            "setStartBefore: node has no parent");
        SetStart(parent, IndexOf(node));
    }

    /// <summary>§4.6.3 setStartAfter.</summary>
    public void SetStartAfter(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var parent = node.ParentNode ?? throw DomRangeException.Create("InvalidNodeTypeError",
            "setStartAfter: node has no parent");
        SetStart(parent, IndexOf(node) + 1);
    }

    /// <summary>§4.6.3 setEndBefore.</summary>
    public void SetEndBefore(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var parent = node.ParentNode ?? throw DomRangeException.Create("InvalidNodeTypeError",
            "setEndBefore: node has no parent");
        SetEnd(parent, IndexOf(node));
    }

    /// <summary>§4.6.3 setEndAfter.</summary>
    public void SetEndAfter(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var parent = node.ParentNode ?? throw DomRangeException.Create("InvalidNodeTypeError",
            "setEndAfter: node has no parent");
        SetEnd(parent, IndexOf(node) + 1);
    }

    /// <summary>§4.6.3 collapse(toStart?). Default is toStart=false (collapse to end).</summary>
    public void Collapse(bool toStart = false)
    {
        if (toStart)
        {
            EndContainer = StartContainer;
            EndOffset = StartOffset;
        }
        else
        {
            StartContainer = EndContainer;
            StartOffset = EndOffset;
        }
    }

    /// <summary>§4.6.3 selectNode.</summary>
    public void SelectNode(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var parent = node.ParentNode ?? throw DomRangeException.Create("InvalidNodeTypeError",
            "selectNode: node has no parent");
        var index = IndexOf(node);
        StartContainer = parent;
        StartOffset = index;
        EndContainer = parent;
        EndOffset = index + 1;
    }

    /// <summary>§4.6.3 selectNodeContents.</summary>
    public void SelectNodeContents(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node is DocumentType)
        {
            throw DomRangeException.Create("InvalidNodeTypeError", "selectNodeContents: cannot select a doctype node");
        }

        StartContainer = node;
        StartOffset = 0;
        EndContainer = node;
        EndOffset = NodeLength(node);
    }

    // -----------------------------------------------------------------------
    // Comparison (§4.6.4)

    public const int START_TO_START = 0;
    public const int START_TO_END = 1;
    public const int END_TO_END = 2;
    public const int END_TO_START = 3;

    /// <summary>§4.6.4 compareBoundaryPoints.</summary>
    public int CompareBoundaryPoints(int how, DomRange other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (how is < 0 or > 3)
        {
            throw DomRangeException.Create("NotSupportedError", $"compareBoundaryPoints: invalid 'how' argument {how}");
        }

        // If the two ranges are not in the same tree, throw WrongDocumentError.
        if (!SameRoot(StartContainer, other.StartContainer))
        {
            throw DomRangeException.Create("WrongDocumentError",
                "compareBoundaryPoints: ranges are not in the same tree");
        }

        Node thisPoint, otherPoint;
        int thisOffset, otherOffset;

        switch (how)
        {
            case START_TO_START:
                thisPoint = StartContainer; thisOffset = StartOffset;
                otherPoint = other.StartContainer; otherOffset = other.StartOffset;
                break;
            case START_TO_END:
                thisPoint = EndContainer; thisOffset = EndOffset;
                otherPoint = other.StartContainer; otherOffset = other.StartOffset;
                break;
            case END_TO_END:
                thisPoint = EndContainer; thisOffset = EndOffset;
                otherPoint = other.EndContainer; otherOffset = other.EndOffset;
                break;
            case END_TO_START:
                thisPoint = StartContainer; thisOffset = StartOffset;
                otherPoint = other.EndContainer; otherOffset = other.EndOffset;
                break;
            default:
                throw new InvalidOperationException("unreachable");
        }

        return ComparePoints(thisPoint, thisOffset, otherPoint, otherOffset);
    }

    /// <summary>§4.6.4 comparePoint(node, offset).</summary>
    public int ComparePoint(Node node, int offset)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!SameRoot(node, StartContainer))
        {
            throw DomRangeException.Create("WrongDocumentError",
                "comparePoint: node and range are not in the same tree");
        }

        if (node is DocumentType)
        {
            throw DomRangeException.Create("InvalidNodeTypeError", "comparePoint: doctype node is not allowed");
        }

        ValidateOffset(node, offset, "comparePoint");

        if (ComparePoints(node, offset, StartContainer, StartOffset) < 0)
        {
            return -1;
        }

        if (ComparePoints(node, offset, EndContainer, EndOffset) > 0)
        {
            return 1;
        }

        return 0;
    }

    /// <summary>§4.6.4 isPointInRange.</summary>
    public bool IsPointInRange(Node node, int offset)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!SameRoot(node, StartContainer))
        {
            return false;
        }

        if (node is DocumentType)
        {
            throw DomRangeException.Create("InvalidNodeTypeError", "isPointInRange: doctype node is not allowed");
        }

        ValidateOffset(node, offset, "isPointInRange");

        return ComparePoints(node, offset, StartContainer, StartOffset) >= 0
            && ComparePoints(node, offset, EndContainer, EndOffset) <= 0;
    }

    /// <summary>§4.6.4 intersectsNode.</summary>
    public bool IntersectsNode(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!SameRoot(node, StartContainer))
        {
            return false;
        }

        var parent = node.ParentNode;
        if (parent is null)
        {
            return true; // orphan root intersects any range in the same tree
        }

        var offset = IndexOf(node);
        // The node overlaps the range if [offset, offset+1) intersects [start, end).
        return ComparePoints(parent, offset + 1, StartContainer, StartOffset) > 0
            && ComparePoints(parent, offset, EndContainer, EndOffset) < 0;
    }

    // -----------------------------------------------------------------------
    // Cloning (§4.6.5)

    /// <summary>§4.6.5 cloneRange — returns an independent copy.</summary>
    public DomRange CloneRange() =>
        new(StartContainer, StartOffset, EndContainer, EndOffset);

    /// <summary>§4.6.5 detach — no-op per current spec (ranges no longer get detached).</summary>
    public void Detach() { /* spec says no-op */ }

    // -----------------------------------------------------------------------
    // String representation (§4.6.6)

    /// <summary>§4.6.6 stringify: concatenates all text covered by the range.
    /// Walks all text nodes (CharacterData) in document order between start
    /// and end, slicing the boundary containers as needed.</summary>
    public string Stringify()
    {
        var sb = new System.Text.StringBuilder();

        if (ReferenceEquals(StartContainer, EndContainer))
        {
            // Same container — slice character data, or collect child text.
            if (StartContainer is CharacterData cd)
            {
                sb.Append(cd.Data, StartOffset, EndOffset - StartOffset);
            }
            else
            {
                // Element/Document: concatenate text from children [start, end).
                var i = 0;
                for (var c = StartContainer.FirstChild; c is not null; c = c.NextSibling)
                {
                    if (i >= StartOffset && i < EndOffset)
                    {
                        CollectText(c, sb);
                    }

                    i++;
                }
            }
            return sb.ToString();
        }

        // Walk all text descendants of the common ancestor in pre-order.
        // For each Text/CharacterData node, determine which slice to include.
        foreach (var textNode in TextNodesInPreOrder(CommonAncestorContainer))
        {
            if (ReferenceEquals(textNode, StartContainer))
            {
                sb.Append(textNode.Data, StartOffset, textNode.Data.Length - StartOffset);
            }
            else if (ReferenceEquals(textNode, EndContainer))
            {
                sb.Append(textNode.Data, 0, EndOffset);
                break; // End container is the last one.
            }
            else
            {
                // Fully contained? Check it's after start and before end.
                if (IsAfterOrAt(textNode) && IsBeforeOrAt(textNode))
                {
                    sb.Append(textNode.Data);
                }
            }
        }

        return sb.ToString();
    }

    private static void CollectText(Node node, System.Text.StringBuilder sb)
    {
        if (node is CharacterData cd) { sb.Append(cd.Data); return; }
        for (var c = node.FirstChild; c is not null; c = c.NextSibling)
        {
            CollectText(c, sb);
        }
    }

    private IEnumerable<CharacterData> TextNodesInPreOrder(Node root)
    {
        if (root is CharacterData rootCd) { yield return rootCd; yield break; }
        var stack = new Stack<Node>();
        for (var c = root.LastChild; c is not null; c = c.PreviousSibling)
        {
            stack.Push(c);
        }

        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n is CharacterData cd) { yield return cd; continue; }
            for (var c = n.LastChild; c is not null; c = c.PreviousSibling)
            {
                stack.Push(c);
            }
        }
    }

    private bool IsAfterOrAt(Node node)
    {
        // Is node at or after StartContainer?
        if (ReferenceEquals(node, StartContainer))
        {
            return true;
        }

        return ComparePoints(StartContainer, StartOffset, node, 0) <= 0;
    }

    private bool IsBeforeOrAt(Node node)
    {
        // Is node at or before EndContainer?
        if (ReferenceEquals(node, EndContainer))
        {
            return true;
        }

        return ComparePoints(node, NodeLength(node), EndContainer, EndOffset) <= 0;
    }

    // -----------------------------------------------------------------------
    // Extraction / manipulation (§4.6.7) — minimal stubs for WPT coverage

    /// <summary>§4.6.7 deleteContents — remove all nodes/text covered by range;
    /// collapse to start. This is a structural operation; simplified implementation
    /// handles the most common cases (single-node and same-parent).</summary>
    public void DeleteContents()
    {
        if (Collapsed)
        {
            return;
        }

        // Clone boundary points to be stable during mutation.
        var sc = StartContainer; var so = StartOffset;
        var ec = EndContainer; var eo = EndOffset;

        if (ReferenceEquals(sc, ec))
        {
            // Same node — trim the character data or remove child range.
            if (sc is CharacterData cd)
            {
                cd.Data = cd.Data[..so] + cd.Data[eo..];
                Collapse(true);
                return;
            }
            // Element/Document — remove children [so, eo).
            RemoveChildrenInRange(sc, so, eo);
            Collapse(true);
            return;
        }

        // Collect fully-contained nodes and remove them.
        foreach (var node in NodesInRange().ToList())
        {
            node.RemoveFromParent();
        }

        // Trim the end container character data.
        if (ec is CharacterData ecd)
        {
            ecd.Data = ecd.Data[eo..];
        }

        // Trim the start container character data.
        if (sc is CharacterData scd)
        {
            scd.Data = scd.Data[..so];
        }

        Collapse(true);
    }

    // -----------------------------------------------------------------------
    // §4.6.13 cloneContents / §4.6.14 extractContents / §4.6.16 insertNode /
    // §4.6.17 surroundContents
    //
    // The spec algorithms are mechanical but long. Comments below name the
    // spec step each block implements so the implementation stays scrutable.

    /// <summary>DOM §4.6.13 — clone the range's contents into a new
    /// DocumentFragment without modifying the source tree.</summary>
    public DocumentFragment CloneContents()
    {
        var doc = StartContainer.OwnerDocument ?? (StartContainer as Document) ?? new Document();
        var fragment = doc.CreateDocumentFragment();
        if (Collapsed)
        {
            return fragment;
        }

        var sn = StartContainer; var so = StartOffset;
        var en = EndContainer; var eo = EndOffset;

        // §4.6.13 step 4 — same CharacterData container.
        if (ReferenceEquals(sn, en) && sn is CharacterData cd0)
        {
            var clone = (CharacterData)NodeClone.Shallow(cd0);
            clone.Data = SafeSubstring(cd0.Data, so, eo - so);
            fragment.AppendChild(clone);
            return fragment;
        }

        // §4.6.13 step 5–6 — common ancestor.
        var commonAncestor = sn;
        while (!IsInclusiveAncestor(commonAncestor, en))
        {
            commonAncestor = commonAncestor.ParentNode ?? throw new InvalidOperationException("range nodes have no common ancestor");
        }

        // §4.6.13 step 7–10 — partially contained children + contained children.
        Node? firstPartial = null;
        if (!IsInclusiveAncestor(sn, en))
        {
            firstPartial = FirstChildPartiallyContaining(commonAncestor, sn);
        }

        Node? lastPartial = null;
        if (!IsInclusiveAncestor(en, sn))
        {
            lastPartial = LastChildPartiallyContaining(commonAncestor, en);
        }

        var contained = new List<Node>();
        for (var c = commonAncestor.FirstChild; c is not null; c = c.NextSibling)
        {
            if (IsContainedInRange(c))
            {
                contained.Add(c);
            }
        }

        // §4.6.13 step 12 — doctype contained → HierarchyRequestError.
        foreach (var c in contained)
        {
            if (c is DocumentType)
            {
                throw DomRangeException.Create("HierarchyRequestError",
                    "cloneContents: doctype in range");
            }
        }

        // §4.6.13 step 13 — first partial is CharacterData → clone + substring.
        if (firstPartial is CharacterData fcd)
        {
            var clone = (CharacterData)NodeClone.Shallow(fcd);
            clone.Data = SafeSubstring(fcd.Data, so, fcd.Data.Length - so);
            fragment.AppendChild(clone);
        }
        else if (firstPartial is not null)
        {
            // §4.6.13 step 14 — first partial is element-ish; recurse on a subrange.
            var clone = NodeClone.Shallow(firstPartial);
            fragment.AppendChild(clone);
            var subrange = new DomRange(sn, so, firstPartial, NodeLength(firstPartial));
            var sub = subrange.CloneContents();
            // Move subfragment's children into clone.
            MoveAllChildren(sub, clone);
        }

        // §4.6.13 step 15 — fully-contained children deep-cloned.
        foreach (var c in contained)
        {
            fragment.AppendChild(NodeClone.Deep(c));
        }

        // §4.6.13 step 16 — last partial is CharacterData.
        if (lastPartial is CharacterData lcd)
        {
            var clone = (CharacterData)NodeClone.Shallow(lcd);
            clone.Data = SafeSubstring(lcd.Data, 0, eo);
            fragment.AppendChild(clone);
        }
        else if (lastPartial is not null)
        {
            // §4.6.13 step 17.
            var clone = NodeClone.Shallow(lastPartial);
            fragment.AppendChild(clone);
            var subrange = new DomRange(lastPartial, 0, en, eo);
            var sub = subrange.CloneContents();
            MoveAllChildren(sub, clone);
        }
        return fragment;
    }

    /// <summary>DOM §4.6.14 — extract the range's contents into a new
    /// DocumentFragment, mutating the source tree to remove what was
    /// extracted. After: the range is collapsed to (originalStart).</summary>
    public DocumentFragment ExtractContents()
    {
        var doc = StartContainer.OwnerDocument ?? (StartContainer as Document) ?? new Document();
        var fragment = doc.CreateDocumentFragment();
        if (Collapsed)
        {
            return fragment;
        }

        var sn = StartContainer; var so = StartOffset;
        var en = EndContainer; var eo = EndOffset;

        // §4.6.14 step 4 — same CharacterData container.
        if (ReferenceEquals(sn, en) && sn is CharacterData cd0)
        {
            var clone = (CharacterData)NodeClone.Shallow(cd0);
            clone.Data = SafeSubstring(cd0.Data, so, eo - so);
            fragment.AppendChild(clone);
            cd0.Data = SafeSubstring(cd0.Data, 0, so) + SafeSubstring(cd0.Data, eo, cd0.Data.Length - eo);
            Collapse(true);
            return fragment;
        }

        // §4.6.14 step 5–10 — common ancestor + partials + contained.
        var commonAncestor = sn;
        while (!IsInclusiveAncestor(commonAncestor, en))
        {
            commonAncestor = commonAncestor.ParentNode ?? throw new InvalidOperationException("range nodes have no common ancestor");
        }

        Node? firstPartial = null;
        if (!IsInclusiveAncestor(sn, en))
        {
            firstPartial = FirstChildPartiallyContaining(commonAncestor, sn);
        }

        Node? lastPartial = null;
        if (!IsInclusiveAncestor(en, sn))
        {
            lastPartial = LastChildPartiallyContaining(commonAncestor, en);
        }

        var contained = new List<Node>();
        for (var c = commonAncestor.FirstChild; c is not null; c = c.NextSibling)
        {
            if (IsContainedInRange(c))
            {
                contained.Add(c);
            }
        }

        foreach (var c in contained)
        {
            if (c is DocumentType)
            {
                throw DomRangeException.Create("HierarchyRequestError",
                    "extractContents: doctype in range");
            }
        }

        // §4.6.14 step 12 — compute new boundary post-extraction. With a
        // descendant boundary, the new start is the common ancestor at the
        // index of where the start "fell into" it.
        Node newNode; int newOffset;
        if (IsInclusiveAncestor(sn, en))
        {
            newNode = sn;
            newOffset = so;
        }
        else
        {
            // ancestor of sn in commonAncestor's children that contains sn
            var reference = sn;
            while (reference.ParentNode is { } p && !ReferenceEquals(p, commonAncestor))
            {
                reference = p;
            }

            newNode = commonAncestor;
            newOffset = IndexOf(reference) + 1;
        }

        // §4.6.14 step 13 — first partial is CharacterData.
        if (firstPartial is CharacterData fcd)
        {
            var clone = (CharacterData)NodeClone.Shallow(fcd);
            clone.Data = SafeSubstring(fcd.Data, so, fcd.Data.Length - so);
            fragment.AppendChild(clone);
            fcd.Data = SafeSubstring(fcd.Data, 0, so);
        }
        else if (firstPartial is not null)
        {
            // §4.6.14 step 14.
            var clone = NodeClone.Shallow(firstPartial);
            fragment.AppendChild(clone);
            var subrange = new DomRange(sn, so, firstPartial, NodeLength(firstPartial));
            var sub = subrange.ExtractContents();
            MoveAllChildren(sub, clone);
        }

        // §4.6.14 step 15 — append fully-contained nodes (no cloning).
        foreach (var c in contained)
        {
            fragment.AppendChild(c);
        }

        // §4.6.14 step 16 — last partial is CharacterData.
        if (lastPartial is CharacterData lcd)
        {
            var clone = (CharacterData)NodeClone.Shallow(lcd);
            clone.Data = SafeSubstring(lcd.Data, 0, eo);
            fragment.AppendChild(clone);
            lcd.Data = SafeSubstring(lcd.Data, eo, lcd.Data.Length - eo);
        }
        else if (lastPartial is not null)
        {
            var clone = NodeClone.Shallow(lastPartial);
            fragment.AppendChild(clone);
            var subrange = new DomRange(lastPartial, 0, en, eo);
            var sub = subrange.ExtractContents();
            MoveAllChildren(sub, clone);
        }

        // §4.6.14 step 18 — collapse.
        StartContainer = newNode; StartOffset = newOffset;
        EndContainer = newNode; EndOffset = newOffset;
        return fragment;
    }

    /// <summary>DOM §4.6.16 insertNode — insert <paramref name="node"/> at
    /// the range's start point. The range adjusts so it spans the inserted
    /// content's leading edge to its original end.</summary>
    public void InsertNode(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);

        // §4.6.16 step 1 — invalid insertion target.
        var reference = StartContainer;
        if (reference is ProcessingInstruction or Comment
            || (reference is Text t && t.ParentNode is null)
            || ReferenceEquals(node, reference))
        {
            throw DomRangeException.Create("HierarchyRequestError",
                "insertNode: invalid insertion point");
        }

        // Determine the eventual parent (Text container's parent, or the
        // start container itself).
        var prospectiveParent = reference is Text txt0 ? txt0.ParentNode! : reference;

        // §4.2.3 pre-insertion validity: inserting a Document or DocumentType
        // into a non-Document parent is forbidden; ditto inserting a
        // bare-Document anywhere.
        if (node is Document)
        {
            throw DomRangeException.Create("HierarchyRequestError",
                "insertNode: Document cannot be inserted as a child");
        }

        if (node is DocumentType && prospectiveParent is not Document)
        {
            throw DomRangeException.Create("HierarchyRequestError",
                "insertNode: DocumentType cannot be inserted outside a Document");
        }

        // Descendant guard: inserting an inclusive-ancestor would create a
        // cycle. (Spec defers this to the pre-insertion validity check; we
        // surface it as the spec-named throw so the runtime catch picks it
        // up as a DOMException rather than a host InvalidOperationException.)
        if (IsInclusiveAncestor(node, prospectiveParent))
        {
            throw DomRangeException.Create("HierarchyRequestError",
                "insertNode: node is an ancestor of the insertion point");
        }

        // §4.6.16 step 3 — figure out the parent we'll insert into.
        Node parent;
        Node? insertBefore;
        if (reference is Text txt)
        {
            parent = txt.ParentNode!;
            // Split the text node at the start offset so the original Text's
            // characters survive past the insertion (spec step 4).
            var doc = txt.OwnerDocument ?? (parent.OwnerDocument ?? (parent as Document))
                ?? throw new InvalidOperationException("text insertion without owner document");
            var tail = doc.CreateTextNode(SafeSubstring(txt.Data, StartOffset, txt.Data.Length - StartOffset));
            txt.Data = SafeSubstring(txt.Data, 0, StartOffset);
            if (txt.NextSibling is { } ns)
            {
                parent.InsertBefore(tail, ns);
            }
            else
            {
                parent.AppendChild(tail);
            }

            insertBefore = tail;
        }
        else
        {
            parent = reference;
            insertBefore = ChildAt(reference, StartOffset);
        }

        // §4.6.16 step 8 — ensure the reference sibling isn't node itself.
        if (ReferenceEquals(node, insertBefore))
        {
            insertBefore = node.NextSibling;
        }

        // Spec step 8.4 — detach if already in tree.
        if (node.ParentNode is not null)
        {
            node.RemoveFromParent();
        }

        // §4.6.16 step 9–10 — insert + adjust the range end if it was collapsed.
        // The collapsed flag must be read BEFORE the InsertBefore below, since
        // that mutation now fires the live-range "insert" hook which can move
        // the (still-collapsed) end off the start.
        var wasCollapsed = Collapsed;
        var newOffsetBase = insertBefore is not null
            ? IndexOf(insertBefore)
            : ChildCount(parent);

        int inserted = node is DocumentFragment df ? ChildCount(df) : 1;
        try
        {
            if (insertBefore is not null)
            {
                parent.InsertBefore(node, insertBefore);
            }
            else
            {
                parent.AppendChild(node);
            }
        }
        catch (InvalidOperationException ex)
        {
            // Re-route Node's host-level guards to a spec-named DOM error so
            // the binding catches it as a DOMException.
            throw DomRangeException.Create("HierarchyRequestError", ex.Message);
        }

        if (wasCollapsed)
        {
            EndContainer = parent;
            EndOffset = newOffsetBase + inserted;
        }
    }

    /// <summary>DOM §4.6.17 surroundContents — wrap the range's contents in
    /// <paramref name="newParent"/>. Equivalent to <c>extract → set children
    /// of newParent → insertNode(newParent) → selectNode(newParent)</c>.</summary>
    public void SurroundContents(Node newParent)
    {
        ArgumentNullException.ThrowIfNull(newParent);

        // §4.6.17 step 1 — partial CharacterData isn't surroundable; reject.
        foreach (var n in PartiallyContainedNodes())
        {
            if (n is not CharacterData)
            {
                throw DomRangeException.Create("InvalidStateError",
                    "surroundContents: range partially contains non-text nodes");
            }
        }

        // §4.6.17 step 2 — newParent must not be Document/DocumentType/DocumentFragment.
        if (newParent is Document or DocumentType or DocumentFragment)
        {
            throw DomRangeException.Create("InvalidNodeTypeError",
                "surroundContents: newParent is not a valid wrapper");
        }

        // §4.6.17 step 5 implicitly relies on pre-insertion validity, which
        // rejects parents that aren't {Document, DocumentFragment, Element}.
        // Document/DF were filtered above, so what's left is: must be Element.
        if (newParent is not Element)
        {
            throw DomRangeException.Create("HierarchyRequestError",
                "surroundContents: newParent cannot have children");
        }

        // §4.6.17 step 3 — extract contents.
        var fragment = ExtractContents();

        // §4.6.17 step 4 — clear newParent.
        while (newParent.FirstChild is { } c)
        {
            c.RemoveFromParent();
        }

        // §4.6.17 step 5 — insert newParent at the range start (which now
        // equals the range end since extract collapses).
        InsertNode(newParent);

        // §4.6.17 step 6 — move extracted contents into newParent.
        MoveAllChildren(fragment, newParent);

        // §4.6.17 step 7 — select newParent.
        SelectNode(newParent);
    }

    // -----------------------------------------------------------------------
    // Step helpers

    private static string SafeSubstring(string s, int start, int length)
    {
        if (length <= 0)
        {
            return string.Empty;
        }

        if (start < 0) { length += start; start = 0; }
        if (start >= s.Length)
        {
            return string.Empty;
        }

        var avail = s.Length - start;
        return s.Substring(start, Math.Min(length, avail));
    }

    private static Node? ChildAt(Node parent, int index)
    {
        var i = 0;
        for (var c = parent.FirstChild; c is not null; c = c.NextSibling)
        {
            if (i == index)
            {
                return c;
            }

            i++;
        }
        return null;
    }

    private static void MoveAllChildren(Node source, Node target)
    {
        while (source.FirstChild is { } c)
        {
            c.RemoveFromParent();
            target.AppendChild(c);
        }
    }

    /// <summary>The first child of <paramref name="ancestor"/> whose subtree
    /// contains <paramref name="boundary"/> (used for "first partially
    /// contained child" in §4.6.13/14).</summary>
    private static Node? FirstChildPartiallyContaining(Node ancestor, Node boundary)
    {
        for (var c = ancestor.FirstChild; c is not null; c = c.NextSibling)
        {
            if (IsInclusiveAncestor(c, boundary))
            {
                return c;
            }
        }

        return null;
    }

    private static Node? LastChildPartiallyContaining(Node ancestor, Node boundary)
    {
        for (var c = ancestor.LastChild; c is not null; c = c.PreviousSibling)
        {
            if (IsInclusiveAncestor(c, boundary))
            {
                return c;
            }
        }

        return null;
    }

    /// <summary>A node is "contained" in this range when both its boundary
    /// points fall strictly inside the range (DOM §4.6 "node containment").</summary>
    private bool IsContainedInRange(Node n)
    {
        // start-of-range &lt;= start-of-node AND end-of-node &lt;= end-of-range
        if (ComparePoints(StartContainer, StartOffset,
                n.ParentNode ?? n, n.ParentNode is null ? 0 : IndexOf(n)) > 0)
        {
            return false;
        }

        if (ComparePoints(n.ParentNode ?? n, n.ParentNode is null ? NodeLength(n) : IndexOf(n) + 1,
                EndContainer, EndOffset) > 0)
        {
            return false;
        }

        return true;
    }

    /// <summary>Nodes whose subtree crosses one of the range boundaries
    /// (used by surroundContents to reject partially-selected non-text
    /// nodes).</summary>
    private IEnumerable<Node> PartiallyContainedNodes()
    {
        // A node is partially contained iff it's an inclusive ancestor of
        // start or end but not both. Walk ancestors of start and end up to
        // the common ancestor.
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        var commonAncestor = StartContainer;
        while (!IsInclusiveAncestor(commonAncestor, EndContainer))
        {
            commonAncestor = commonAncestor.ParentNode ?? throw new InvalidOperationException("range has no common ancestor");
        }

        for (var n = StartContainer; n is not null && !ReferenceEquals(n, commonAncestor); n = n.ParentNode)
        {
            if (!IsInclusiveAncestor(n, EndContainer) && seen.Add(n))
            {
                yield return n;
            }
        }

        for (var n = EndContainer; n is not null && !ReferenceEquals(n, commonAncestor); n = n.ParentNode)
        {
            if (!IsInclusiveAncestor(n, StartContainer) && seen.Add(n))
            {
                yield return n;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Helpers

    /// <summary>DOM §4.1: node length. For CharacterData it's the number of
    /// code units; for DocumentType it's 0; for others it's childNode count.</summary>
    public static int NodeLength(Node node) => node switch
    {
        DocumentType => 0,
        CharacterData cd => cd.Data.Length,
        _ => ChildCount(node),
    };

    private static int ChildCount(Node node)
    {
        var count = 0;
        for (var c = node.FirstChild; c is not null; c = c.NextSibling)
        {
            count++;
        }

        return count;
    }

    /// <summary>Index of <paramref name="node"/> in its parent's child list (0-based).</summary>
    public static int IndexOf(Node node)
    {
        var index = 0;
        for (var c = node.ParentNode?.FirstChild; c is not null && !ReferenceEquals(c, node); c = c.NextSibling)
        {
            index++;
        }

        return index;
    }

    /// <summary>True when <paramref name="a"/> is an inclusive ancestor of <paramref name="b"/>.</summary>
    private static bool IsInclusiveAncestor(Node a, Node b)
    {
        for (var n = b; n is not null; n = n.ParentNode)
        {
            if (ReferenceEquals(n, a))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when <paramref name="a"/> and <paramref name="b"/> are
    /// in the same tree (have the same root node).</summary>
    private static bool SameRoot(Node a, Node b) =>
        ReferenceEquals(Root(a), Root(b));

    private static Node Root(Node n)
    {
        var cur = n;
        while (cur.ParentNode is not null)
        {
            cur = cur.ParentNode;
        }

        return cur;
    }

    /// <summary>DOM §4.4 tree order comparison of two boundary points.
    /// Returns &lt;0 if (nodeA, offsetA) is before (nodeB, offsetB),
    /// 0 if equal, &gt;0 if after.</summary>
    private static int ComparePoints(Node nodeA, int offsetA, Node nodeB, int offsetB)
    {
        if (ReferenceEquals(nodeA, nodeB))
        {
            return offsetA.CompareTo(offsetB);
        }

        // Find positions in a pre-order list.
        // Collect ancestors of both from root downward.
        var ancestorsA = AncestorsFromRoot(nodeA);
        var ancestorsB = AncestorsFromRoot(nodeB);

        // Find first divergence in ancestor chains.
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
            // One is ancestor of other. Ancestor comes first only when the
            // deeper node is past the offset in the ancestor.
            if (ancestorsA.Count < ancestorsB.Count)
            {
                // nodeA is an ancestor of nodeB. The question: is offsetA
                // before or after the descendant's position?
                var childInA = ancestorsB[i]; // this child of nodeA that contains nodeB
                var childIndex = IndexOf(childInA);
                return offsetA <= childIndex ? -1 : 1;
            }
            else
            {
                // nodeB is ancestor of nodeA.
                var childInB = ancestorsA[i];
                var childIndex = IndexOf(childInB);
                return childIndex < offsetB ? -1 : 1;
            }
        }

        // The two siblings are ancestorsA[i] and ancestorsB[i], siblings in ancestorsA[i-1].
        // Compare their positions.
        var siblingA = ancestorsA[i];
        var siblingB = ancestorsB[i];
        for (var s = siblingA.NextSibling; s is not null; s = s.NextSibling)
        {
            if (ReferenceEquals(s, siblingB))
            {
                return -1; // A is before B
            }
        }

        return 1; // B is before A
    }

    private static List<Node> AncestorsFromRoot(Node node)
    {
        var chain = new List<Node>();
        for (var n = node; n is not null; n = n.ParentNode)
        {
            chain.Add(n);
        }

        chain.Reverse(); // root first
        return chain;
    }

    private static void ValidateNotDocumentType(Node node, string op)
    {
        // setStart/setEnd: if node is a doctype, throw InvalidNodeTypeError.
        if (node is DocumentType)
        {
            throw DomRangeException.Create("InvalidNodeTypeError",
                $"{op}: doctype nodes cannot be boundary points");
        }
    }

    private static void ValidateOffset(Node node, int offset, string op)
    {
        var len = NodeLength(node);
        if (offset < 0 || offset > len)
        {
            throw DomRangeException.Create("IndexSizeError",
                $"{op}: offset {offset} is out of range [0, {len}]");
        }
    }

    /// <summary>Enumerate nodes that are <em>fully</em> between start and end
    /// (i.e. neither the start nor end container, but wholly contained within
    /// the range). Used by Stringify and DeleteContents.</summary>
    private IEnumerable<Node> NodesInRange()
    {
        // Walk the tree in pre-order starting from the root; emit nodes that
        // are after the start and before the end.
        var root = CommonAncestorContainer;

        // Collect all descendants of the common ancestor in pre-order.
        var stack = new Stack<Node>();
        for (var c = root.LastChild; c is not null; c = c.PreviousSibling)
        {
            stack.Push(c);
        }

        while (stack.Count > 0)
        {
            var n = stack.Pop();

            var afterStart = !IsInclusiveAncestor(n, StartContainer)
                && ComparePoints(StartContainer, StartOffset, n, 0) <= 0;
            var beforeEnd = !IsInclusiveAncestor(n, EndContainer)
                && ComparePoints(n, NodeLength(n), EndContainer, EndOffset) <= 0;

            if (afterStart && beforeEnd)
            {
                yield return n;
                // Don't recurse into it — we already yielded it whole.
                continue;
            }

            // Push children for further examination.
            for (var c = n.LastChild; c is not null; c = c.PreviousSibling)
            {
                stack.Push(c);
            }
        }
    }

    private static void RemoveChildrenInRange(Node parent, int fromIndex, int toIndex)
    {
        var toRemove = new List<Node>();
        var i = 0;
        for (var c = parent.FirstChild; c is not null; c = c.NextSibling)
        {
            if (i >= fromIndex && i < toIndex)
            {
                toRemove.Add(c);
            }

            i++;
        }
        foreach (var c in toRemove)
        {
            c.RemoveFromParent();
        }
    }
}

/// <summary>
/// Lightweight exception type used by DomRange internals to signal DOM
/// errors. The binding layer catches these and converts them to JS
/// <c>DOMException</c> objects. Only <see cref="DomRangeException.DomName"/> is used for routing;
/// the message is for debugging.
/// </summary>
public sealed class DomRangeException : Exception
{
    /// <summary>DOMException name, e.g. "IndexSizeError".</summary>
    public string DomName { get; }

    public DomRangeException() : base() { DomName = "Error"; }
    public DomRangeException(string message) : base(message) { DomName = "Error"; }
    public DomRangeException(string message, Exception inner) : base(message, inner) { DomName = "Error"; }

    /// <summary>Create with a DOM error name and detail message.</summary>
    public DomRangeException(string domName, string message, string? _unused) : base(message)
    {
        DomName = domName;
    }

    /// <summary>Factory: create with a DOM error name and detail message.</summary>
    public static DomRangeException Create(string domName, string message) =>
        new(domName, message, null);
}
