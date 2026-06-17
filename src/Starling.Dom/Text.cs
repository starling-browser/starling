namespace Starling.Dom;

public abstract class CharacterData : Node
{
    private string _data;

    protected CharacterData(string data)
    {
        _data = data;
    }

    public string Data
    {
        get => _data;
        set
        {
            var normalized = value ?? string.Empty;
            if (_data == normalized)
            {
                return;
            }

            var oldData = _data;
            _data = normalized;
            OnTreeMutated();
            OwnerDocument?.RecordLayoutMutation(this, LayoutChangeKind.TextChanged);
            OwnerDocument?.CharacterDataMutated?.Invoke(this, oldData);
        }
    }

    public override string? NodeValue
    {
        get => Data;
        set => Data = value ?? string.Empty;
    }

    public override string TextContent
    {
        get => Data;
        set => Data = value ?? string.Empty;
    }

    /// <summary>The number of UTF-16 code units in the data.
    /// <a href="https://dom.spec.whatwg.org/#dom-characterdata-length">DOM §4.10</a></summary>
    public int Length => _data.Length;

    /// <summary>
    /// <a href="https://dom.spec.whatwg.org/#concept-cd-substring">Substring data</a>:
    /// returns count code units starting at offset, clamped to the end. Throws
    /// IndexSizeError when offset is past the end.
    /// </summary>
    public string SubstringData(uint offset, uint count)
    {
        if (offset > (uint)_data.Length)
        {
            throw DomException.Create("IndexSizeError",
                $"substringData: offset {offset} is past the data length {_data.Length}.");
        }

        uint available = (uint)_data.Length - offset;
        uint take = count > available ? available : count;
        return _data.Substring((int)offset, (int)take);
    }

    /// <summary>Append data to the end. <a href="https://dom.spec.whatwg.org/#dom-characterdata-appenddata">DOM §4.10</a></summary>
    public void AppendData(string data) => ReplaceData((uint)_data.Length, 0, data);

    /// <summary>Insert data at offset. Throws IndexSizeError when offset is past the end.</summary>
    public void InsertData(uint offset, string data) => ReplaceData(offset, 0, data);

    /// <summary>Delete count code units at offset, clamped to the end. Throws IndexSizeError when offset is past the end.</summary>
    public void DeleteData(uint offset, uint count) => ReplaceData(offset, count, string.Empty);

    /// <summary>
    /// <a href="https://dom.spec.whatwg.org/#concept-cd-replace">Replace data</a>:
    /// the primitive the other mutators defer to. Removes count code units at
    /// offset (clamped to the end) and inserts data there. Throws IndexSizeError
    /// when offset is past the end. Writing through Data fires the mutation hooks.
    /// </summary>
    public void ReplaceData(uint offset, uint count, string data)
    {
        if (offset > (uint)_data.Length)
        {
            throw DomException.Create("IndexSizeError",
                $"replaceData: offset {offset} is past the data length {_data.Length}.");
        }

        uint available = (uint)_data.Length - offset;
        uint remove = count > available ? available : count;
        Data = string.Concat(
            _data.AsSpan(0, (int)offset),
            data ?? string.Empty,
            _data.AsSpan((int)(offset + remove)));
    }
}

public sealed class Text : CharacterData
{
    public Text(string data) : base(data)
    {
    }

    public override NodeKind Kind => NodeKind.Text;

    public override string NodeName => "#text";

    public override string ToString() => $"Text({Data.Length} chars)";

    /// <summary>
    /// The concatenated data of this node and its contiguous Text-node siblings,
    /// in tree order. <a href="https://dom.spec.whatwg.org/#dom-text-wholetext">DOM §4.11</a>
    /// </summary>
    public string WholeText
    {
        get
        {
            Node start = this;
            while (start.PreviousSibling is Text prev)
            {
                start = prev;
            }

            var sb = new System.Text.StringBuilder();
            for (Node? n = start; n is Text t; n = n.NextSibling)
            {
                sb.Append(t.Data);
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Split this node at offset: the data after offset moves into a new Text node
    /// inserted as the next sibling, which is returned. Throws IndexSizeError when
    /// offset is past the end.
    /// <a href="https://dom.spec.whatwg.org/#dom-text-splittext">DOM §4.11</a>
    /// </summary>
    public Text SplitText(uint offset)
    {
        if (offset > (uint)Length)
        {
            throw DomException.Create("IndexSizeError",
                $"splitText: offset {offset} is past the data length {Length}.");
        }

        uint count = (uint)Length - offset;
        var newNode = new Text(SubstringData(offset, count)) { OwnerDocument = OwnerDocument };

        ParentNode?.InsertBefore(newNode, NextSibling);
        DeleteData(offset, count);
        return newNode;
    }
}

public sealed class Comment : CharacterData
{
    public Comment(string data) : base(data)
    {
    }

    public override NodeKind Kind => NodeKind.Comment;

    public override string NodeName => "#comment";
}

public sealed class CData : CharacterData
{
    public CData(string data) : base(data)
    {
    }

    public override NodeKind Kind => NodeKind.CDataSection;

    public override string NodeName => "#cdata-section";
}

public sealed class ProcessingInstruction : CharacterData
{
    public ProcessingInstruction(string target, string data) : base(data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        Target = target;
    }

    public string Target { get; }

    public override NodeKind Kind => NodeKind.ProcessingInstruction;

    public override string NodeName => Target;
}
