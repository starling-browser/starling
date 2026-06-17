using System.Text;
using BenchmarkDotNet.Attributes;
using Starling.Net.Http.H1;

namespace Starling.Bench;

// HTTP/1.1 response parsing — every fetch path hits this. The keep-alive
// connection pool (wp:M2-07c) calls `H1ResponseParser` per response, so any
// regression here multiplies across the subresource graph. Body is read off
// an in-memory `MemoryStream`, so the bench is socket-free.
[MemoryDiagnoser]
public class H1ResponseBench
{
    private byte[] _smallBody = null!;       // Content-Length, ~1 KB
    private byte[] _largeBody = null!;       // Content-Length, ~64 KB
    private byte[] _chunkedBody = null!;     // chunked, ~16 KB across 8 chunks

    [GlobalSetup]
    public void Setup()
    {
        _smallBody = BuildContentLengthResponse(payloadBytes: 1024);
        _largeBody = BuildContentLengthResponse(payloadBytes: 64 * 1024);
        _chunkedBody = BuildChunkedResponse(chunks: 8, perChunk: 2048);
    }

    [Benchmark]
    public int ContentLength_1KB()
    {
        var stream = new MemoryStream(_smallBody);
        var result = new H1ResponseParser().ParseAsync(stream, default).GetAwaiter().GetResult();
        return result.Value.Body.Length;
    }

    [Benchmark]
    public int ContentLength_64KB()
    {
        var stream = new MemoryStream(_largeBody);
        var result = new H1ResponseParser().ParseAsync(stream, default).GetAwaiter().GetResult();
        return result.Value.Body.Length;
    }

    [Benchmark]
    public int Chunked_16KB_8Chunks()
    {
        var stream = new MemoryStream(_chunkedBody);
        var result = new H1ResponseParser().ParseAsync(stream, default).GetAwaiter().GetResult();
        return result.Value.Body.Length;
    }

    private static byte[] BuildContentLengthResponse(int payloadBytes)
    {
        var head = $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {payloadBytes}\r\nConnection: keep-alive\r\n\r\n";
        var bytes = new byte[Encoding.ASCII.GetByteCount(head) + payloadBytes];
        var off = Encoding.ASCII.GetBytes(head, bytes);
        for (var i = 0; i < payloadBytes; i++)
        {
            bytes[off + i] = (byte)('a' + (i % 26));
        }

        return bytes;
    }

    private static byte[] BuildChunkedResponse(int chunks, int perChunk)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nTransfer-Encoding: chunked\r\n\r\n");
        for (var c = 0; c < chunks; c++)
        {
            sb.Append(perChunk.ToString("X", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append("\r\n");
            for (var i = 0; i < perChunk; i++)
            {
                sb.Append((char)('a' + (i % 26)));
            }

            sb.Append("\r\n");
        }
        sb.Append("0\r\n\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
