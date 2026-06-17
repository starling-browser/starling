using System.Text;

namespace Starling.Net.Http.H2.Hpack;

/// <summary>
/// HPACK encoder (RFC 7541 §6). Deliberately simple and stateless: it indexes
/// the static table for exact and name matches but never inserts into a dynamic
/// table, so it carries no per-connection state and cannot drift. String
/// literals are Huffman-coded only when that is strictly shorter. This is a
/// fully conformant encoding — the peer decodes it the same regardless of the
/// indexing strategy we choose.
/// </summary>
internal sealed class HpackEncoder
{
    /// <summary>Encode a header field list into a single HPACK header block.</summary>
    public byte[] Encode(IReadOnlyList<(string Name, string Value)> fields)
    {
        var dst = new List<byte>(64);
        foreach (var (name, value) in fields)
        {
            var index = HpackStaticTable.FindIndex(name, value, out var exact);
            if (exact)
            {
                // §6.1 Indexed Header Field.
                WriteInteger(dst, index, 7, 0x80);
                continue;
            }

            if (index > 0)
            {
                // §6.2.2 Literal without indexing, name from the static table.
                WriteInteger(dst, index, 4, 0x00);
            }
            else
            {
                // §6.2.2 Literal without indexing, new name.
                dst.Add(0x00);
                WriteString(dst, name);
            }
            WriteString(dst, value);
        }
        return [.. dst];
    }

    private static void WriteInteger(List<byte> dst, int value, int prefixBits, byte firstByteHigh)
    {
        Span<byte> tmp = stackalloc byte[HpackInteger.MaxEncodedLength];
        var n = HpackInteger.Encode(tmp, value, prefixBits, firstByteHigh);
        for (var i = 0; i < n; i++)
        {
            dst.Add(tmp[i]);
        }
    }

    private static void WriteString(List<byte> dst, string s)
    {
        var bytes = Encoding.Latin1.GetBytes(s);
        var huffLen = HpackHuffman.EncodedLength(bytes);

        if (huffLen < bytes.Length)
        {
            WriteInteger(dst, huffLen, 7, 0x80); // H flag set
            var buf = new byte[huffLen];
            HpackHuffman.Encode(bytes, buf);
            dst.AddRange(buf);
        }
        else
        {
            WriteInteger(dst, bytes.Length, 7, 0x00);
            dst.AddRange(bytes);
        }
    }
}
