using System.Text;
using AwesomeAssertions;
using Starling.Net.Http.H2.Hpack;

namespace Starling.Net.Tests.Http.H2;

/// <summary>
/// HPACK conformance tests (RFC 7541). Integer and request examples use the
/// exact byte vectors from Appendix C; the encoder is exercised by round-trip.
/// </summary>
[TestClass]
public class HpackTests
{
    private static byte[] Hex(string hex)
    {
        hex = hex.Replace(" ", "", StringComparison.Ordinal);
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        return bytes;
    }

    [TestMethod]
    public void Integer_encodes_per_appendix_C1()
    {
        Span<byte> buf = stackalloc byte[8];

        // C.1.1: 10 with a 5-bit prefix.
        HpackInteger.Encode(buf, 10, 5, 0).Should().Be(1);
        buf[0].Should().Be(0x0a);

        // C.1.2: 1337 with a 5-bit prefix.
        var n = HpackInteger.Encode(buf, 1337, 5, 0);
        buf[..n].ToArray().Should().Equal(0x1f, 0x9a, 0x0a);

        // C.1.3: 42 at an octet boundary (8-bit prefix).
        HpackInteger.Encode(buf, 42, 8, 0).Should().Be(1);
        buf[0].Should().Be(0x2a);
    }

    [TestMethod]
    public void Integer_round_trips()
    {
        Span<byte> buf = stackalloc byte[8];
        foreach (var value in new[] { 0, 1, 30, 31, 32, 127, 128, 1337, 16_383, 1_000_000, int.MaxValue })
        {
            var n = HpackInteger.Encode(buf, value, 5, 0);
            var offset = 0;
            HpackInteger.TryDecode(buf, ref offset, 5, out var decoded).Should().BeTrue();
            decoded.Should().Be(value);
            offset.Should().Be(n);
        }
    }

    [TestMethod]
    public void Huffman_decodes_appendix_C4_authority()
    {
        // The Huffman-coded value of "www.example.com" from C.4.1.
        var encoded = Hex("f1e3 c2e5 f23a 6ba0 ab90 f4ff");
        HpackHuffman.TryDecode(encoded, out var decoded).Should().BeTrue();
        Encoding.ASCII.GetString(decoded).Should().Be("www.example.com");
    }

    [TestMethod]
    public void Huffman_round_trips_arbitrary_text()
    {
        foreach (var text in new[] { "", "a", "GET", "/index.html?q=1", "Mon, 21 Oct 2013 20:13:21 GMT" })
        {
            var src = Encoding.ASCII.GetBytes(text);
            var buf = new byte[HpackHuffman.EncodedLength(src)];
            var n = HpackHuffman.Encode(src, buf);
            n.Should().Be(buf.Length);
            HpackHuffman.TryDecode(buf, out var back).Should().BeTrue();
            Encoding.ASCII.GetString(back).Should().Be(text);
        }
    }

    [TestMethod]
    public void Huffman_rejects_oversized_padding()
    {
        // A whole 0xff byte is 8 padding bits — padding must be < 8 bits.
        HpackHuffman.TryDecode(new byte[] { 0xff }, out _).Should().BeFalse();
    }

    [TestMethod]
    public void Decoder_decodes_appendix_C41_request_with_huffman()
    {
        // C.4.1 First Request: indexed fields + literal-indexed name + Huffman value.
        var block = Hex("8286 8441 8cf1 e3c2 e5f2 3a6b a0ab 90f4 ff");
        var decoder = new HpackDecoder(4096);

        decoder.TryDecode(block, out var fields).Should().BeTrue();

        fields.Select(f => (f.Name, f.Value)).Should().Equal(
            (":method", "GET"),
            (":scheme", "http"),
            (":path", "/"),
            (":authority", "www.example.com"));
    }

    [TestMethod]
    public void Decoder_tracks_dynamic_table_across_two_requests()
    {
        // C.4.1 then C.4.2: the second request references the dynamic-table
        // entry inserted by the first, so a single decoder must carry state.
        var decoder = new HpackDecoder(4096);

        decoder.TryDecode(Hex("8286 8441 8cf1 e3c2 e5f2 3a6b a0ab 90f4 ff"), out _).Should().BeTrue();

        // C.4.2 Second Request.
        decoder.TryDecode(Hex("8286 84be 5886 a8eb 1064 9cbf"), out var second).Should().BeTrue();
        second.Select(f => (f.Name, f.Value)).Should().Equal(
            (":method", "GET"),
            (":scheme", "http"),
            (":path", "/"),
            (":authority", "www.example.com"),
            ("cache-control", "no-cache"));
    }

    [TestMethod]
    public void Encoder_output_round_trips_through_decoder()
    {
        var encoder = new HpackEncoder();
        var request = new (string, string)[]
        {
            (":method", "GET"),
            (":scheme", "https"),
            (":authority", "example.com"),
            (":path", "/some/path?x=1"),
            ("user-agent", "Starling/0.1"),
            ("accept", "text/html"),
            ("cookie", "sid=abc123; theme=dark"),
        };

        var block = encoder.Encode(request);

        var decoder = new HpackDecoder(4096);
        decoder.TryDecode(block, out var fields).Should().BeTrue();
        fields.Select(f => (f.Name, f.Value)).Should().Equal(request);
    }

    [TestMethod]
    public void Encoder_uses_static_index_for_exact_match()
    {
        // ":method: GET" is static entry 2 → a single indexed byte 0x82.
        var block = new HpackEncoder().Encode([(":method", "GET")]);
        block.Should().Equal(0x82);
    }

    [TestMethod]
    public void Decoder_rejects_zero_index()
    {
        // An indexed header field with index 0 is a decoding error (§6.1).
        new HpackDecoder(4096).TryDecode(new byte[] { 0x80 }, out _).Should().BeFalse();
    }
}
