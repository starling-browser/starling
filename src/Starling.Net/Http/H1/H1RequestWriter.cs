using System.Buffers;
using System.Globalization;
using System.Text;
using StarlingUrl = global::Starling.Url.Url;

namespace Starling.Net.Http.H1;

/// <summary>
/// Serializes a <see cref="HttpRequest"/> into a wire-format HTTP/1.1 message
/// (request-line + headers + body) and writes it to a <see cref="Stream"/>.
/// </summary>
/// <remarks>
/// The writer fills in the spec-required headers (Host, User-Agent, Accept,
/// Accept-Encoding, Connection, Content-Length) only when the caller has not
/// already supplied them. Header names from <see cref="HttpRequest.Headers"/>
/// always win — this lets callers force a header that conflicts with our
/// defaults (e.g. Accept-Encoding: identity for testing).
/// </remarks>
public sealed class H1RequestWriter
{
    public string UserAgent { get; init; } = "Starling/0.1 (https://github.com/anthropic-starling)";

    public string AcceptHeader { get; init; } =
        "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8";

    public string AcceptEncodingHeader { get; init; } = "gzip, br, deflate";

    public string ConnectionHeader { get; init; } = "keep-alive";

    public async ValueTask WriteAsync(HttpRequest req, Stream output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(output);

        var rent = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            var bytes = Serialize(req, rent);
            await output.WriteAsync(bytes, ct).ConfigureAwait(false);

            if (!req.Body.IsEmpty)
            {
                await output.WriteAsync(req.Body, ct).ConfigureAwait(false);
            }

            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rent);
        }
    }

    /// <summary>
    /// Test-friendly synchronous serialization. Returns the wire bytes for the
    /// request-line + header block (without the body) so unit tests can assert
    /// the textual form without standing up a stream.
    /// </summary>
    public byte[] SerializeHead(HttpRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        var ms = new MemoryStream();
        var head = Serialize(req, scratch: null);
        ms.Write(head.Span);
        return ms.ToArray();
    }

    private ReadOnlyMemory<byte> Serialize(HttpRequest req, byte[]? scratch)
    {
        var sb = new StringBuilder(512);

        sb.Append(req.Method).Append(' ')
          .Append(BuildRequestTarget(req.Url)).Append(' ')
          .Append("HTTP/1.1\r\n");

        AppendDefaultHeader(sb, req.Headers, "Host", BuildHostHeader(req.Url));
        AppendDefaultHeader(sb, req.Headers, "User-Agent", UserAgent);
        AppendDefaultHeader(sb, req.Headers, "Accept", AcceptHeader);
        AppendDefaultHeader(sb, req.Headers, "Accept-Encoding", AcceptEncodingHeader);
        AppendDefaultHeader(sb, req.Headers, "Connection", ConnectionHeader);

        // Emit Content-Length when there is a body, OR when the method is one
        // that carries a request body (POST/PUT/PATCH) even if that body is
        // empty. An empty-body POST with no Content-Length is rejected by many
        // servers with 411 Length Required — XHR/fetch always send "0" here, so
        // we must too (e.g. McMaster's token-authorization POST).
        if ((!req.Body.IsEmpty || MethodCarriesBody(req.Method))
            && !req.Headers.Contains("Content-Length")
            && !req.Headers.Contains("Transfer-Encoding"))
        {
            sb.Append("Content-Length: ")
              .Append(req.Body.Length.ToString(CultureInfo.InvariantCulture))
              .Append("\r\n");
        }

        foreach (var kv in req.Headers)
        {
            sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
        }

        sb.Append("\r\n");

        var s = sb.ToString();
        var len = Encoding.ASCII.GetByteCount(s);
        if (scratch is null || scratch.Length < len)
        {
            scratch = new byte[len];
        }

        var written = Encoding.ASCII.GetBytes(s, scratch);
        return new ReadOnlyMemory<byte>(scratch, 0, written);
    }

    /// <summary>True for request methods that carry a body (so an empty body
    /// still warrants <c>Content-Length: 0</c>). GET/HEAD/OPTIONS/etc. do not.</summary>
    internal static bool MethodCarriesBody(string method) =>
        method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
        method.Equals("PUT", StringComparison.OrdinalIgnoreCase) ||
        method.Equals("PATCH", StringComparison.OrdinalIgnoreCase);

    private static void AppendDefaultHeader(StringBuilder sb, HttpHeaders user, string name, string value)
    {
        if (user.Contains(name))
        {
            return;
        }

        sb.Append(name).Append(": ").Append(value).Append("\r\n");
    }

    /// <summary>
    /// Build the request-target per RFC 9112 §3.2. For origin-form (the only
    /// form we emit for direct connections, not proxies) that is the path
    /// plus the query, with the path defaulting to "/" if empty.
    /// </summary>
    internal static string BuildRequestTarget(StarlingUrl url)
    {
        var path = string.IsNullOrEmpty(url.Path) ? "/" : url.Path;
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        return url.Query is { Length: > 0 } q ? path + "?" + q : path;
    }

    /// <summary>
    /// Build the Host header per RFC 9112 §3.2 / §7.2. Includes the explicit
    /// port if the URL specifies one that differs from the scheme default.
    /// </summary>
    internal static string BuildHostHeader(StarlingUrl url)
    {
        if (string.IsNullOrEmpty(url.Host))
        {
            throw new ArgumentException("URL has no host — cannot build Host header.", nameof(url));
        }

        var defaultPort = url.DefaultPort;
        if (url.Port is int p && p != defaultPort)
        {
            return url.Host + ":" + p.ToString(CultureInfo.InvariantCulture);
        }

        return url.Host;
    }
}
