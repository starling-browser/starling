using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using Starling.Net.Http.Decoding;
namespace Starling.Net.Tests.Http;

[TestClass]
public class ChunkedReaderTests
{
    private static InboundBuffer FromString(string data) =>
        new(new MemoryStream(Encoding.ASCII.GetBytes(data)));

    [TestMethod]
    public async Task Reads_single_chunk()
    {
        var src = FromString("5\r\nhello\r\n0\r\n\r\n");
        var bytes = await ChunkedReader.ReadAllAsync(src, 1024, CancellationToken.None);
        Encoding.ASCII.GetString(bytes).Should().Be("hello");
    }

    [TestMethod]
    public async Task Reads_multiple_chunks_in_order()
    {
        var src = FromString("5\r\nhello\r\n6\r\n world\r\n1\r\n!\r\n0\r\n\r\n");
        var bytes = await ChunkedReader.ReadAllAsync(src, 1024, CancellationToken.None);
        Encoding.ASCII.GetString(bytes).Should().Be("hello world!");
    }

    [TestMethod]
    public async Task Handles_chunk_extensions()
    {
        var src = FromString("5;name=value\r\nhello\r\n0;final=1\r\n\r\n");
        var bytes = await ChunkedReader.ReadAllAsync(src, 1024, CancellationToken.None);
        Encoding.ASCII.GetString(bytes).Should().Be("hello");
    }

    [TestMethod]
    public async Task Skips_trailers()
    {
        var src = FromString("5\r\nhello\r\n0\r\nX-Trailer-One: value\r\nX-Trailer-Two: more\r\n\r\n");
        var bytes = await ChunkedReader.ReadAllAsync(src, 1024, CancellationToken.None);
        Encoding.ASCII.GetString(bytes).Should().Be("hello");
    }

    [TestMethod]
    public async Task Accepts_uppercase_hex_chunk_size()
    {
        var src = FromString("FF\r\n" + new string('x', 0xFF) + "\r\n0\r\n\r\n");
        var bytes = await ChunkedReader.ReadAllAsync(src, 1024, CancellationToken.None);
        bytes.Length.Should().Be(0xFF);
    }

    [TestMethod]
    public async Task Rejects_truncated_stream()
    {
        var src = FromString("5\r\nhel"); // not enough data
        var act = async () => await ChunkedReader.ReadAllAsync(src, 1024, CancellationToken.None);
        await act.Should().ThrowAsync<EndOfStreamException>();
    }

    [TestMethod]
    public async Task Rejects_missing_crlf_after_chunk_data()
    {
        var src = FromString("5\r\nhellobad");
        var act = async () => await ChunkedReader.ReadAllAsync(src, 1024, CancellationToken.None);
        await act.Should().ThrowAsync<Exception>();
    }

    [TestMethod]
    public async Task Enforces_body_size_cap()
    {
        var src = FromString("a\r\n0123456789\r\n0\r\n\r\n");
        var act = async () => await ChunkedReader.ReadAllAsync(src, 5, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*exceeded cap*");
    }

    [TestMethod]
    public void ParseChunkSize_handles_extensions()
    {
        var line = Encoding.ASCII.GetBytes("a;ext=1");
        ChunkedReader.ParseChunkSize(line).Should().Be(10);
    }

    [TestMethod]
    public void ParseChunkSize_rejects_empty()
    {
        var act = () => ChunkedReader.ParseChunkSize(Array.Empty<byte>());
        act.Should().Throw<InvalidDataException>();
    }

    [TestMethod]
    public void ParseChunkSize_rejects_non_hex()
    {
        var act = () => ChunkedReader.ParseChunkSize(Encoding.ASCII.GetBytes("xyz"));
        act.Should().Throw<InvalidDataException>();
    }
}

[TestClass]
public class BodyDecoderTests
{
    [TestMethod]
    public void Identity_passes_through()
    {
        var input = Encoding.UTF8.GetBytes("hello");
        BodyDecoder.Decode(input, Array.Empty<string>())
            .Should().Equal(input);
    }

    [TestMethod]
    public void Decodes_gzip()
    {
        var payload = Encoding.UTF8.GetBytes("the quick brown fox jumps over the lazy dog");
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            gz.Write(payload);
        }

        BodyDecoder.Decode(ms.ToArray(), new[] { "gzip" })
            .Should().Equal(payload);
    }

    [TestMethod]
    public void Decodes_brotli()
    {
        var payload = Encoding.UTF8.GetBytes("brotli compressed text payload, repeated. " + new string('a', 200));
        using var ms = new MemoryStream();
        using (var br = new BrotliStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            br.Write(payload);
        }

        BodyDecoder.Decode(ms.ToArray(), new[] { "br" })
            .Should().Equal(payload);
    }

    [TestMethod]
    public void Decodes_deflate_with_zlib_wrapping()
    {
        var payload = Encoding.UTF8.GetBytes("zlib wrapped deflate payload");
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(payload);
        }

        BodyDecoder.Decode(ms.ToArray(), new[] { "deflate" })
            .Should().Equal(payload);
    }

    [TestMethod]
    public void Decodes_raw_deflate_when_no_zlib_wrapping()
    {
        var payload = Encoding.UTF8.GetBytes("raw deflate payload, no header");
        using var ms = new MemoryStream();
        using (var d = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            d.Write(payload);
        }

        BodyDecoder.Decode(ms.ToArray(), new[] { "deflate" })
            .Should().Equal(payload);
    }

    [TestMethod]
    public void Decodes_stacked_encodings_in_reverse_order()
    {
        // Server applied gzip first, then brotli. To recover identity we must
        // peel brotli first, then gzip — i.e. iterate the list in reverse.
        var payload = Encoding.UTF8.GetBytes("stacked encoding payload");
        byte[] gz, brOverGz;
        using (var ms = new MemoryStream())
        {
            using (var z = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                z.Write(payload);
            }

            gz = ms.ToArray();
        }
        using (var ms = new MemoryStream())
        {
            using (var b = new BrotliStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                b.Write(gz);
            }

            brOverGz = ms.ToArray();
        }

        BodyDecoder.Decode(brOverGz, new[] { "gzip", "br" })
            .Should().Equal(payload);
    }

    [TestMethod]
    [DataRow("gzip, br", new[] { "gzip", "br" })]
    [DataRow("  gzip  , identity, br ", new[] { "gzip", "br" })]
    [DataRow("identity", new string[0])]
    [DataRow("", new string[0])]
    [DataRow(null, new string[0])]
    public void ParseEncodings_handles_common_inputs(string? header, string[] expected)
    {
        BodyDecoder.ParseEncodings(header).Should().Equal(expected);
    }

    [TestMethod]
    public void Rejects_unknown_encoding()
    {
        var act = () => BodyDecoder.Decode(new byte[] { 1, 2, 3 }, new[] { "compress" });
        act.Should().Throw<NotSupportedException>();
    }
}
