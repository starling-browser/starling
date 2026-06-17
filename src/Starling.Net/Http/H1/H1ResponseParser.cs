using System.Globalization;
using System.Text;
using Starling.Common;
using Starling.Net.Http.Decoding;

namespace Starling.Net.Http.H1;

/// <summary>
/// Parser for an HTTP/1.1 response message off a byte-oriented
/// <see cref="Stream"/>. Returns a fully buffered <see cref="HttpResponse"/>
/// with body framing (Content-Length / chunked / EOF) and any
/// Content-Encoding stack removed.
/// </summary>
/// <remarks>
/// State machine — RFC 9112 §3:
///   status-line → header-section → body
/// Body framing decided per §6.3:
///   1. <c>Transfer-Encoding: chunked</c> takes priority over Content-Length.
///   2. <c>Content-Length: N</c> if present.
///   3. Otherwise read until EOF (legacy HTTP/1.0 close-delimited).
/// We do <em>not</em> attempt to handle 1xx informational responses except
/// to discard their head and re-enter the status-line state; v1 wires the
/// HTTP layer to a TLS transport that we don't drive in 100-Continue mode.
/// </remarks>
public sealed class H1ResponseParser
{
    /// <summary>Cap on the size of the status-line + header block.</summary>
    public int MaxHeaderBlockBytes { get; init; } = 64 * 1024;

    /// <summary>Cap on the decoded body (post Content-Encoding).</summary>
    public int MaxBodyBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>Cap on a single header line.</summary>
    public int MaxHeaderLineBytes { get; init; } = 16 * 1024;

    public async Task<Result<HttpResponse, HttpError>> ParseAsync(
        Stream input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var buf = new InboundBuffer(input);

        // 1. Discard 1xx informational responses (e.g. 100 Continue / 103 Early Hints).
        Headline headline;
        HttpHeaders headers;
        while (true)
        {
            var headBlock = await ReadHeaderBlockAsync(buf, ct).ConfigureAwait(false);
            if (headBlock.IsErr)
            {
                return Result<HttpResponse, HttpError>.Err(headBlock.Error);
            }

            var parsed = ParseHeadBlock(headBlock.Value);
            if (parsed.IsErr)
            {
                return Result<HttpResponse, HttpError>.Err(parsed.Error);
            }

            headline = parsed.Value.Headline;
            headers = parsed.Value.Headers;
            if (headline.StatusCode is < 100 or >= 200)
            {
                break;
            }
            // 1xx — keep reading.
        }

        // 2. Body framing.
        byte[] rawBody;
        try
        {
            rawBody = await ReadBodyAsync(buf, headline, headers, ct).ConfigureAwait(false);
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("exceeded", StringComparison.Ordinal))
        {
            return Result<HttpResponse, HttpError>.Err(HttpError.BodyTooLarge);
        }
        catch (InvalidDataException)
        {
            return Result<HttpResponse, HttpError>.Err(HttpError.BadChunkedFraming);
        }
        catch (EndOfStreamException)
        {
            return Result<HttpResponse, HttpError>.Err(HttpError.UnexpectedEof);
        }

        // 3. Content-Encoding.
        byte[] decoded;
        try
        {
            var encodings = BodyDecoder.ParseEncodings(headers.GetFirst("Content-Encoding"));
            decoded = encodings.Count == 0 ? rawBody : BodyDecoder.Decode(rawBody, encodings);
        }
        catch (NotSupportedException)
        {
            return Result<HttpResponse, HttpError>.Err(HttpError.UnsupportedEncoding);
        }
        catch (InvalidDataException)
        {
            return Result<HttpResponse, HttpError>.Err(HttpError.DecodeFailed);
        }

        return Result<HttpResponse, HttpError>.Ok(
            new HttpResponse(
                headline.HttpVersion,
                headline.StatusCode,
                headline.ReasonPhrase,
                headers,
                decoded));
    }

    private async Task<Result<byte[], HttpError>> ReadHeaderBlockAsync(
        InboundBuffer buf, CancellationToken ct)
    {
        while (true)
        {
            var idx = buf.IndexOfDoubleCrLf();
            if (idx >= 0)
            {
                var headBytes = buf.Peek().Slice(0, idx + 2).ToArray(); // include the first CRLF; not the empty line
                buf.Consume(idx + 4);
                return Result<byte[], HttpError>.Ok(headBytes);
            }
            if (buf.BufferedCount > MaxHeaderBlockBytes)
            {
                return Result<byte[], HttpError>.Err(HttpError.HeadersTooLarge);
            }

            if (!await buf.ReadMoreAsync(ct).ConfigureAwait(false))
            {
                if (buf.BufferedCount == 0)
                {
                    return Result<byte[], HttpError>.Err(HttpError.UnexpectedEof);
                }

                return Result<byte[], HttpError>.Err(HttpError.UnexpectedEof);
            }
        }
    }

    private Result<(Headline Headline, HttpHeaders Headers), HttpError> ParseHeadBlock(byte[] head)
    {
        var text = Encoding.ASCII.GetString(head);
        // We included the trailing CRLF before the empty line, so the last
        // newline is the one ending the final header line.
        var lines = text.Split("\r\n");
        if (lines.Length == 0 || string.IsNullOrEmpty(lines[0]))
        {
            return Result<(Headline, HttpHeaders), HttpError>.Err(HttpError.BadStatusLine);
        }

        var headlineResult = ParseStatusLine(lines[0]);
        if (headlineResult.IsErr)
        {
            return Result<(Headline, HttpHeaders), HttpError>.Err(headlineResult.Error);
        }

        var headers = new HttpHeaders();
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] is ' ' or '\t')
            {
                // RFC 7230 §3.2.4: line folding is deprecated and MUST be rejected.
                return Result<(Headline, HttpHeaders), HttpError>.Err(HttpError.BadHeader);
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                return Result<(Headline, HttpHeaders), HttpError>.Err(HttpError.BadHeader);
            }

            var name = line[..colon];
            var value = line[(colon + 1)..].Trim(' ', '\t');

            try { headers.Add(name, value); }
            catch (ArgumentException) { return Result<(Headline, HttpHeaders), HttpError>.Err(HttpError.BadHeader); }
        }

        return Result<(Headline, HttpHeaders), HttpError>.Ok((headlineResult.Value, headers));
    }

    private static Result<Headline, HttpError> ParseStatusLine(string line)
    {
        // status-line = HTTP-version SP status-code SP [ reason-phrase ]
        var firstSp = line.IndexOf(' ', StringComparison.Ordinal);
        if (firstSp <= 0)
        {
            return Result<Headline, HttpError>.Err(HttpError.BadStatusLine);
        }

        var version = line[..firstSp];
        if (!version.StartsWith("HTTP/", StringComparison.Ordinal))
        {
            return Result<Headline, HttpError>.Err(HttpError.BadStatusLine);
        }

        var secondSp = line.IndexOf(' ', firstSp + 1);
        var codeStr = secondSp < 0 ? line[(firstSp + 1)..] : line[(firstSp + 1)..secondSp];
        if (!int.TryParse(codeStr, NumberStyles.None, CultureInfo.InvariantCulture, out var code)
            || code is < 100 or > 599)
        {
            return Result<Headline, HttpError>.Err(HttpError.BadStatusLine);
        }

        var reason = secondSp < 0 ? string.Empty : line[(secondSp + 1)..];

        return Result<Headline, HttpError>.Ok(new Headline(version, code, reason));
    }

    private async Task<byte[]> ReadBodyAsync(
        InboundBuffer buf,
        Headline headline,
        HttpHeaders headers,
        CancellationToken ct)
    {
        // 204/304 and HEAD responses must have an empty body, but we don't
        // know the request method here. RFC 9112 §6.3 step 1 covers status —
        // the rest is the caller's responsibility.
        if (headline.StatusCode is 204 or 304)
        {
            return Array.Empty<byte>();
        }

        var te = headers.GetFirst("Transfer-Encoding");
        if (te is not null && ContainsToken(te, "chunked"))
        {
            return await ChunkedReader.ReadAllAsync(buf, MaxBodyBytes, ct).ConfigureAwait(false);
        }

        var clText = headers.GetFirst("Content-Length");
        if (clText is not null)
        {
            if (!long.TryParse(clText, NumberStyles.None, CultureInfo.InvariantCulture, out var cl) || cl < 0)
            {
                throw new InvalidDataException("Bad Content-Length");
            }

            if (cl > MaxBodyBytes)
            {
                throw new InvalidDataException("Content-Length exceeded cap.");
            }

            return cl == 0 ? Array.Empty<byte>() : await buf.ReadExactAsync((int)cl, ct).ConfigureAwait(false);
        }

        return await buf.ReadToEndAsync(MaxBodyBytes, ct).ConfigureAwait(false);
    }

    private static bool ContainsToken(string headerValue, string token)
    {
        foreach (var raw in headerValue.Split(','))
        {
            if (string.Equals(raw.Trim(), token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Decide whether <paramref name="response"/> indicated the server is
    /// willing to keep the underlying transport open for another request on
    /// the same origin. Used by the connection pool to gate
    /// <c>ReleaseAsync</c> on a clean response.
    /// </summary>
    /// <remarks>
    /// RFC 9112 §9.3: HTTP/1.1 connections are keep-alive by default unless
    /// a <c>Connection: close</c> option is present. HTTP/1.0 connections are
    /// close-by-default unless the legacy <c>Connection: keep-alive</c>
    /// signal is present (RFC 7230 §A.1.2). A response framed by
    /// connection-close (no Content-Length / Transfer-Encoding on a non-empty
    /// body) cannot be pooled even if the headers say keep-alive — the
    /// caller is responsible for checking framing.
    /// </remarks>
    public static bool IndicatesKeepAlive(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var connection = response.Headers.GetFirst("Connection");
        var isHttp11 = string.Equals(response.HttpVersion, "HTTP/1.1", StringComparison.Ordinal);
        var isHttp10 = string.Equals(response.HttpVersion, "HTTP/1.0", StringComparison.Ordinal);

        if (connection is not null)
        {
            if (ContainsToken(connection, "close"))
            {
                return false;
            }

            if (ContainsToken(connection, "keep-alive"))
            {
                return true;
            }
        }

        // No explicit signal: HTTP/1.1 default keep-alive; HTTP/1.0 default close.
        if (isHttp11)
        {
            return true;
        }

        if (isHttp10)
        {
            return false;
        }
        // Anything unrecognised (e.g. a malformed version we still parsed) —
        // be conservative and close.
        return false;
    }

    /// <summary>
    /// True when the response has a well-defined body length (Content-Length
    /// or chunked Transfer-Encoding, or a status that mandates an empty body).
    /// Connection-close framing (no length, non-empty body) is not poolable
    /// because the parser had to read to EOF to know where the body ended.
    /// </summary>
    public static bool HasDefiniteBodyFraming(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.StatusCode is 204 or 304)
        {
            return true;
        }

        var te = response.Headers.GetFirst("Transfer-Encoding");
        if (te is not null && ContainsToken(te, "chunked"))
        {
            return true;
        }

        return response.Headers.GetFirst("Content-Length") is not null;
    }

    private readonly record struct Headline(string HttpVersion, int StatusCode, string ReasonPhrase);
}
