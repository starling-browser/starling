using StarlingUrl = global::Starling.Url.Url;

namespace Starling.Net.Http;

/// <summary>
/// HTTP request value object. The <see cref="Body"/> is sent verbatim;
/// chunked-transfer encoding is not used on the request side in v1 (we
/// always know the body length up front).
/// </summary>
public sealed class HttpRequest
{
    public string Method { get; }
    public StarlingUrl Url { get; }
    public HttpHeaders Headers { get; }
    public ReadOnlyMemory<byte> Body { get; }

    public HttpRequest(
        string method,
        StarlingUrl url,
        HttpHeaders? headers = null,
        ReadOnlyMemory<byte> body = default)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            throw new ArgumentException("Method required.", nameof(method));
        }

        ArgumentNullException.ThrowIfNull(url);

        Method = method;
        Url = url;
        Headers = headers ?? new HttpHeaders();
        Body = body;
    }

    public static HttpRequest Get(StarlingUrl url, HttpHeaders? headers = null)
        => new("GET", url, headers);
}
