namespace Starling.Net.Http.H2.Hpack;

/// <summary>
/// HPACK Huffman codec (RFC 7541 Appendix B). Decode walks a bit-trie built
/// once at static init; encode emits MSB-first codes. The canonical 257-symbol
/// table (256 octet symbols + EOS) is embedded verbatim from the RFC.
/// </summary>
internal static class HpackHuffman
{
    /// <summary>Symbol index of the End-Of-String code; never appears decoded.</summary>
    private const int Eos = 256;

    // (code right-aligned in a uint, bit length). Index = symbol value.
    private static readonly (uint Code, int Bits)[] Table =
    [
        (0x00001ff8u, 13), (0x007fffd8u, 23), (0x0fffffe2u, 28), (0x0fffffe3u, 28), (0x0fffffe4u, 28),
        (0x0fffffe5u, 28), (0x0fffffe6u, 28), (0x0fffffe7u, 28), (0x0fffffe8u, 28), (0x00ffffeau, 24),
        (0x3ffffffcu, 30), (0x0fffffe9u, 28), (0x0fffffeau, 28), (0x3ffffffdu, 30), (0x0fffffebu, 28),
        (0x0fffffecu, 28), (0x0fffffedu, 28), (0x0fffffeeu, 28), (0x0fffffefu, 28), (0x0ffffff0u, 28),
        (0x0ffffff1u, 28), (0x0ffffff2u, 28), (0x3ffffffeu, 30), (0x0ffffff3u, 28), (0x0ffffff4u, 28),
        (0x0ffffff5u, 28), (0x0ffffff6u, 28), (0x0ffffff7u, 28), (0x0ffffff8u, 28), (0x0ffffff9u, 28),
        (0x0ffffffau, 28), (0x0ffffffbu, 28), (0x00000014u, 6), (0x000003f8u, 10), (0x000003f9u, 10),
        (0x00000ffau, 12), (0x00001ff9u, 13), (0x00000015u, 6), (0x000000f8u, 8), (0x000007fau, 11),
        (0x000003fau, 10), (0x000003fbu, 10), (0x000000f9u, 8), (0x000007fbu, 11), (0x000000fau, 8),
        (0x00000016u, 6), (0x00000017u, 6), (0x00000018u, 6), (0x00000000u, 5), (0x00000001u, 5),
        (0x00000002u, 5), (0x00000019u, 6), (0x0000001au, 6), (0x0000001bu, 6), (0x0000001cu, 6),
        (0x0000001du, 6), (0x0000001eu, 6), (0x0000001fu, 6), (0x0000005cu, 7), (0x000000fbu, 8),
        (0x00007ffcu, 15), (0x00000020u, 6), (0x00000ffbu, 12), (0x000003fcu, 10), (0x00001ffau, 13),
        (0x00000021u, 6), (0x0000005du, 7), (0x0000005eu, 7), (0x0000005fu, 7), (0x00000060u, 7),
        (0x00000061u, 7), (0x00000062u, 7), (0x00000063u, 7), (0x00000064u, 7), (0x00000065u, 7),
        (0x00000066u, 7), (0x00000067u, 7), (0x00000068u, 7), (0x00000069u, 7), (0x0000006au, 7),
        (0x0000006bu, 7), (0x0000006cu, 7), (0x0000006du, 7), (0x0000006eu, 7), (0x0000006fu, 7),
        (0x00000070u, 7), (0x00000071u, 7), (0x00000072u, 7), (0x000000fcu, 8), (0x00000073u, 7),
        (0x000000fdu, 8), (0x00001ffbu, 13), (0x0007fff0u, 19), (0x00001ffcu, 13), (0x00003ffcu, 14),
        (0x00000022u, 6), (0x00007ffdu, 15), (0x00000003u, 5), (0x00000023u, 6), (0x00000004u, 5),
        (0x00000024u, 6), (0x00000005u, 5), (0x00000025u, 6), (0x00000026u, 6), (0x00000027u, 6),
        (0x00000006u, 5), (0x00000074u, 7), (0x00000075u, 7), (0x00000028u, 6), (0x00000029u, 6),
        (0x0000002au, 6), (0x00000007u, 5), (0x0000002bu, 6), (0x00000076u, 7), (0x0000002cu, 6),
        (0x00000008u, 5), (0x00000009u, 5), (0x0000002du, 6), (0x00000077u, 7), (0x00000078u, 7),
        (0x00000079u, 7), (0x0000007au, 7), (0x0000007bu, 7), (0x00007ffeu, 15), (0x000007fcu, 11),
        (0x00003ffdu, 14), (0x00001ffdu, 13), (0x0ffffffcu, 28), (0x000fffe6u, 20), (0x003fffd2u, 22),
        (0x000fffe7u, 20), (0x000fffe8u, 20), (0x003fffd3u, 22), (0x003fffd4u, 22), (0x003fffd5u, 22),
        (0x007fffd9u, 23), (0x003fffd6u, 22), (0x007fffdau, 23), (0x007fffdbu, 23), (0x007fffdcu, 23),
        (0x007fffddu, 23), (0x007fffdeu, 23), (0x00ffffebu, 24), (0x007fffdfu, 23), (0x00ffffecu, 24),
        (0x00ffffedu, 24), (0x003fffd7u, 22), (0x007fffe0u, 23), (0x00ffffeeu, 24), (0x007fffe1u, 23),
        (0x007fffe2u, 23), (0x007fffe3u, 23), (0x007fffe4u, 23), (0x001fffdcu, 21), (0x003fffd8u, 22),
        (0x007fffe5u, 23), (0x003fffd9u, 22), (0x007fffe6u, 23), (0x007fffe7u, 23), (0x00ffffefu, 24),
        (0x003fffdau, 22), (0x001fffddu, 21), (0x000fffe9u, 20), (0x003fffdbu, 22), (0x003fffdcu, 22),
        (0x007fffe8u, 23), (0x007fffe9u, 23), (0x001fffdeu, 21), (0x007fffeau, 23), (0x003fffddu, 22),
        (0x003fffdeu, 22), (0x00fffff0u, 24), (0x001fffdfu, 21), (0x003fffdfu, 22), (0x007fffebu, 23),
        (0x007fffecu, 23), (0x001fffe0u, 21), (0x001fffe1u, 21), (0x003fffe0u, 22), (0x001fffe2u, 21),
        (0x007fffedu, 23), (0x003fffe1u, 22), (0x007fffeeu, 23), (0x007fffefu, 23), (0x000fffeau, 20),
        (0x003fffe2u, 22), (0x003fffe3u, 22), (0x003fffe4u, 22), (0x007ffff0u, 23), (0x003fffe5u, 22),
        (0x003fffe6u, 22), (0x007ffff1u, 23), (0x03ffffe0u, 26), (0x03ffffe1u, 26), (0x000fffebu, 20),
        (0x0007fff1u, 19), (0x003fffe7u, 22), (0x007ffff2u, 23), (0x003fffe8u, 22), (0x01ffffecu, 25),
        (0x03ffffe2u, 26), (0x03ffffe3u, 26), (0x03ffffe4u, 26), (0x07ffffdeu, 27), (0x07ffffdfu, 27),
        (0x03ffffe5u, 26), (0x00fffff1u, 24), (0x01ffffedu, 25), (0x0007fff2u, 19), (0x001fffe3u, 21),
        (0x03ffffe6u, 26), (0x07ffffe0u, 27), (0x07ffffe1u, 27), (0x03ffffe7u, 26), (0x07ffffe2u, 27),
        (0x00fffff2u, 24), (0x001fffe4u, 21), (0x001fffe5u, 21), (0x03ffffe8u, 26), (0x03ffffe9u, 26),
        (0x0ffffffdu, 28), (0x07ffffe3u, 27), (0x07ffffe4u, 27), (0x07ffffe5u, 27), (0x000fffecu, 20),
        (0x00fffff3u, 24), (0x000fffedu, 20), (0x001fffe6u, 21), (0x003fffe9u, 22), (0x001fffe7u, 21),
        (0x001fffe8u, 21), (0x007ffff3u, 23), (0x003fffeau, 22), (0x003fffebu, 22), (0x01ffffeeu, 25),
        (0x01ffffefu, 25), (0x00fffff4u, 24), (0x00fffff5u, 24), (0x03ffffeau, 26), (0x007ffff4u, 23),
        (0x03ffffebu, 26), (0x07ffffe6u, 27), (0x03ffffecu, 26), (0x03ffffedu, 26), (0x07ffffe7u, 27),
        (0x07ffffe8u, 27), (0x07ffffe9u, 27), (0x07ffffeau, 27), (0x07ffffebu, 27), (0x0ffffffeu, 28),
        (0x07ffffecu, 27), (0x07ffffedu, 27), (0x07ffffeeu, 27), (0x07ffffefu, 27), (0x07fffff0u, 27),
        (0x03ffffeeu, 26), (0x3fffffffu, 30),
    ];

    // Bit-trie: two arrays of child indices (0-bit / 1-bit) plus a per-node
    // symbol (-1 for internal nodes). Node 0 is the root.
    private static readonly int[] Zero;
    private static readonly int[] One;
    private static readonly int[] Symbol;

    static HpackHuffman()
    {
        // Worst case one internal node per code bit; over-allocate then trim.
        var capacity = 1;
        foreach (var (_, bits) in Table)
        {
            capacity += bits;
        }

        Zero = new int[capacity];
        One = new int[capacity];
        Symbol = new int[capacity];
        Array.Fill(Zero, -1);
        Array.Fill(One, -1);
        Array.Fill(Symbol, -1);

        var next = 1; // node 0 is the root
        for (var sym = 0; sym < Table.Length; sym++)
        {
            var (code, bits) = Table[sym];
            var node = 0;
            for (var i = bits - 1; i >= 0; i--)
            {
                var bit = (code >> i) & 1;
                ref var edge = ref (bit == 0 ? ref Zero[node] : ref One[node]);
                if (edge < 0)
                {
                    edge = next++;
                }
                node = edge;
            }
            Symbol[node] = sym;
        }
    }

    /// <summary>
    /// Decode a Huffman-coded octet string. Returns false on any RFC 7541
    /// §5.2 violation: an EOS symbol appearing in the stream, padding longer
    /// than 7 bits, or padding that is not the MSBs of the all-ones EOS code.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> input, out byte[] output)
    {
        var sink = new List<byte>(input.Length * 8 / 5 + 4);
        var node = 0;
        var bitsInNode = 0;
        var allOnesSinceRoot = true;

        foreach (var b in input)
        {
            for (var i = 7; i >= 0; i--)
            {
                var bit = (b >> i) & 1;
                if (node == 0)
                {
                    bitsInNode = 0;
                    allOnesSinceRoot = true;
                }
                bitsInNode++;
                if (bit == 0)
                {
                    allOnesSinceRoot = false;
                }

                node = bit == 0 ? Zero[node] : One[node];
                if (node < 0)
                {
                    output = [];
                    return false; // no such code path
                }

                if (Symbol[node] >= 0)
                {
                    if (Symbol[node] == Eos)
                    {
                        output = [];
                        return false; // EOS must never be encoded
                    }
                    sink.Add((byte)Symbol[node]);
                    node = 0;
                }
            }
        }

        // Trailing bits must be a (<=7-bit) prefix of EOS — i.e. all ones.
        if (node != 0 && (bitsInNode > 7 || !allOnesSinceRoot))
        {
            output = [];
            return false;
        }

        output = [.. sink];
        return true;
    }

    /// <summary>Number of bytes the Huffman encoding of <paramref name="src"/> occupies.</summary>
    public static int EncodedLength(ReadOnlySpan<byte> src)
    {
        var bits = 0L;
        foreach (var b in src)
        {
            bits += Table[b].Bits;
        }

        return (int)((bits + 7) / 8);
    }

    /// <summary>
    /// Huffman-encode <paramref name="src"/> into <paramref name="dst"/>,
    /// padding the final byte with the MSBs of the EOS code (all ones).
    /// Returns the number of bytes written.
    /// </summary>
    public static int Encode(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        var pos = 0;
        ulong acc = 0;
        var accBits = 0;

        foreach (var b in src)
        {
            var (code, bits) = Table[b];
            acc = (acc << bits) | code;
            accBits += bits;
            while (accBits >= 8)
            {
                accBits -= 8;
                dst[pos++] = (byte)(acc >> accBits);
            }
        }

        if (accBits > 0)
        {
            // Pad the remaining bits with 1s (EOS prefix).
            var pad = 8 - accBits;
            acc = (acc << pad) | ((1UL << pad) - 1);
            dst[pos++] = (byte)acc;
        }

        return pos;
    }
}
