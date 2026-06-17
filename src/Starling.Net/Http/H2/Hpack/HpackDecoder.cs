using System.Text;

namespace Starling.Net.Http.H2.Hpack;

/// <summary>A single decoded header field.</summary>
internal readonly record struct HpackHeaderField(string Name, string Value, bool NeverIndexed);

/// <summary>
/// HPACK decoder (RFC 7541 §3 / §6). Stateful: one decoder mirrors the peer
/// encoder's dynamic table across an entire connection, so header blocks must
/// be fed in the order they arrive on the wire. Header octets are interpreted
/// as Latin-1 so each octet maps to exactly one char — keeping the dynamic
/// table's size accounting (octet length) equal to <c>string.Length</c>.
/// </summary>
internal sealed class HpackDecoder
{
    private readonly HpackDynamicTable _dynamic;
    private readonly int _maxAllowedTableSize;

    public HpackDecoder(int maxDynamicTableSize)
    {
        _maxAllowedTableSize = maxDynamicTableSize;
        _dynamic = new HpackDynamicTable(maxDynamicTableSize);
    }

    /// <summary>
    /// Decode one complete header block into <paramref name="fields"/>. Returns
    /// false on any malformed input (RFC 7541 calls these COMPRESSION_ERROR):
    /// truncation, a zero or out-of-range index, an invalid Huffman string, or
    /// a dynamic-table size update above the negotiated maximum.
    /// </summary>
    public bool TryDecode(ReadOnlySpan<byte> block, out List<HpackHeaderField> fields)
    {
        fields = [];
        var offset = 0;

        while (offset < block.Length)
        {
            var b = block[offset];

            if ((b & 0x80) != 0)
            {
                // §6.1 Indexed Header Field.
                if (!HpackInteger.TryDecode(block, ref offset, 7, out var index) || index == 0)
                {
                    return false;
                }

                if (!Resolve(index, out var name, out var value))
                {
                    return false;
                }

                fields.Add(new HpackHeaderField(name, value, NeverIndexed: false));
            }
            else if ((b & 0x40) != 0)
            {
                // §6.2.1 Literal Header Field with Incremental Indexing.
                if (!ReadLiteral(block, ref offset, 6, out var name, out var value))
                {
                    return false;
                }

                _dynamic.Add(name, value, name.Length, value.Length);
                fields.Add(new HpackHeaderField(name, value, NeverIndexed: false));
            }
            else if ((b & 0x20) != 0)
            {
                // §6.3 Dynamic Table Size Update.
                if (!HpackInteger.TryDecode(block, ref offset, 5, out var newSize))
                {
                    return false;
                }

                if (newSize > _maxAllowedTableSize)
                {
                    return false;
                }

                _dynamic.Resize(newSize);
            }
            else
            {
                // §6.2.2 (0x00) without indexing, §6.2.3 (0x10) never indexed.
                var neverIndexed = (b & 0x10) != 0;
                if (!ReadLiteral(block, ref offset, 4, out var name, out var value))
                {
                    return false;
                }

                fields.Add(new HpackHeaderField(name, value, neverIndexed));
            }
        }

        return true;
    }

    /// <summary>Read a literal representation whose name is either an index or an inline string.</summary>
    private bool ReadLiteral(
        ReadOnlySpan<byte> block, ref int offset, int namePrefixBits, out string name, out string value)
    {
        name = string.Empty;
        value = string.Empty;

        if (!HpackInteger.TryDecode(block, ref offset, namePrefixBits, out var nameIndex))
        {
            return false;
        }

        if (nameIndex == 0)
        {
            if (!TryReadString(block, ref offset, out name))
            {
                return false;
            }
        }
        else if (!Resolve(nameIndex, out name, out _))
        {
            return false;
        }

        return TryReadString(block, ref offset, out value);
    }

    /// <summary>Resolve a combined (static + dynamic) index to a name/value pair.</summary>
    private bool Resolve(int index, out string name, out string value)
    {
        if (index <= HpackStaticTable.Count)
        {
            return HpackStaticTable.TryGet(index, out name, out value);
        }

        return _dynamic.TryGet(index - HpackStaticTable.Count, out name, out value);
    }

    /// <summary>Read a length-prefixed (optionally Huffman-coded) string literal (§5.2).</summary>
    private static bool TryReadString(ReadOnlySpan<byte> block, ref int offset, out string result)
    {
        result = string.Empty;
        if (offset >= block.Length)
        {
            return false;
        }

        var huffman = (block[offset] & 0x80) != 0;
        if (!HpackInteger.TryDecode(block, ref offset, 7, out var length))
        {
            return false;
        }

        if (length < 0 || offset + length > block.Length)
        {
            return false;
        }

        var raw = block.Slice(offset, length);
        offset += length;

        if (huffman)
        {
            if (!HpackHuffman.TryDecode(raw, out var decoded))
            {
                return false;
            }

            result = Encoding.Latin1.GetString(decoded);
        }
        else
        {
            result = Encoding.Latin1.GetString(raw);
        }
        return true;
    }
}
