using System.IO.Compression;

namespace Starling.Net.Http.Decoding;

/// <summary>
/// Applies HTTP <c>Content-Encoding</c> decoding stages to a buffered body.
/// </summary>
/// <remarks>
/// Per RFC 9110 §8.4, encodings in <c>Content-Encoding</c> are listed in the
/// order they were applied. To recover the identity representation we apply
/// the inverse codings in reverse order — i.e. the last listed coding is
/// peeled first.
/// </remarks>
public static class BodyDecoder
{
    /// <summary>
    /// Decode <paramref name="body"/> through every stage named in
    /// <paramref name="contentEncodings"/>. Unknown or empty encoding tokens
    /// are rejected.
    /// </summary>
    public static byte[] Decode(ReadOnlyMemory<byte> body, IReadOnlyList<string> contentEncodings)
    {
        ArgumentNullException.ThrowIfNull(contentEncodings);

        if (contentEncodings.Count == 0)
        {
            return body.ToArray();
        }

        var current = body.ToArray();
        for (var i = contentEncodings.Count - 1; i >= 0; i--)
        {
            current = DecodeStage(current, contentEncodings[i]);
        }
        return current;
    }

    /// <summary>
    /// Parse a comma-separated <c>Content-Encoding</c> header value into a
    /// list of lowercase coding tokens. Whitespace and empty entries are
    /// stripped. <c>identity</c> is filtered out (it's a no-op coding).
    /// </summary>
    public static IReadOnlyList<string> ParseEncodings(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return Array.Empty<string>();
        }

        var parts = headerValue.Split(',');
        List<string>? result = null;
        foreach (var raw in parts)
        {
            var token = raw.Trim().ToLowerInvariant();
            if (token.Length == 0 || token == "identity")
            {
                continue;
            }

            (result ??= []).Add(token);
        }
        return result ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    private static byte[] DecodeStage(byte[] input, string encoding)
    {
        var token = (encoding ?? string.Empty).Trim().ToLowerInvariant();
        return token switch
        {
            "gzip" or "x-gzip" => Decompress(input, raw => new GZipStream(raw, CompressionMode.Decompress)),
            "br" => Decompress(input, raw => new BrotliStream(raw, CompressionMode.Decompress)),
            "deflate" => DecodeDeflate(input),
            "identity" or "" => input,
            _ => throw new NotSupportedException($"Content-Encoding '{encoding}' is not supported."),
        };
    }

    private static byte[] Decompress(byte[] input, Func<MemoryStream, Stream> wrap)
    {
        using var raw = new MemoryStream(input, writable: false);
        using var decompressor = wrap(raw);
        using var output = new MemoryStream();
        decompressor.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>
    /// "deflate" in HTTP is historically zlib-wrapped DEFLATE (RFC 1950),
    /// but real servers send raw DEFLATE about as often. Try zlib first; if
    /// the header fails, fall back to raw <see cref="DeflateStream"/>.
    /// </summary>
    private static byte[] DecodeDeflate(byte[] input)
    {
        try
        {
            return Decompress(input, raw => new ZLibStream(raw, CompressionMode.Decompress));
        }
        catch (InvalidDataException)
        {
            return Decompress(input, raw => new DeflateStream(raw, CompressionMode.Decompress));
        }
    }
}
