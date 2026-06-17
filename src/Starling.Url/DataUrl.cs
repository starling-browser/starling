using System.Text;

namespace Starling.Url;

/// <summary>
/// Decode <c>data:</c> URLs into their MIME type + payload bytes per
/// <see href="https://datatracker.ietf.org/doc/html/rfc2397">RFC 2397</see>.
/// </summary>
/// <remarks>
/// Form: <c>data:[&lt;mediatype&gt;][;base64],&lt;data&gt;</c>. When base64
/// is absent the payload is URL-encoded text; we percent-decode it to bytes.
/// Used by <c>ImageFetcher</c> so inline <c>&lt;img src="data:..."&gt;</c>
/// (very common on Google et al. for icons) decodes locally instead of
/// hitting the network.
/// </remarks>
public static class DataUrl
{
    public readonly record struct Payload(string MediaType, byte[] Bytes);
    public const int MaxPayloadBytes = 32 * 1024 * 1024; // 32 MiB

    /// <summary>
    /// Attempt to decode a <see cref="Url"/> whose scheme is <c>data</c>.
    /// Returns <c>false</c> for any other scheme, malformed data URLs, or
    /// payloads that exceed <paramref name="maxBytes"/>.
    /// </summary>
    public static bool TryDecode(Url url, out Payload payload, int maxBytes = MaxPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        if (!url.IsData)
        {
            payload = default;
            return false;
        }

        // UrlParser stores everything after `data:` in Path as the opaque
        // path. We re-attach Query/Fragment because base64 strings can
        // legitimately contain `?`/`#` only after percent-encoding — the
        // parser already handled escaping — but for simplicity the typical
        // image data URL never carries either component.
        var raw = url.Path;
        if (string.IsNullOrEmpty(raw))
        {
            payload = default;
            return false;
        }
        if (url.Query is not null)
        {
            raw = raw + "?" + url.Query;
        }

        if (url.Fragment is not null)
        {
            raw = raw + "#" + url.Fragment;
        }

        var comma = raw.IndexOf(',');
        if (comma < 0)
        {
            payload = default;
            return false;
        }

        var meta = raw[..comma];
        var body = raw[(comma + 1)..];

        var isBase64 = false;
        string mediaType;
        if (meta.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            isBase64 = true;
            mediaType = meta[..^";base64".Length];
        }
        else
        {
            mediaType = meta;
        }
        if (string.IsNullOrEmpty(mediaType))
        {
            mediaType = "text/plain;charset=US-ASCII";
        }

        try
        {
            byte[] bytes;
            if (isBase64)
            {
                // Base64 payloads in data URLs are passed through OpaquePath
                // unchanged (alphanumerics, +, /, = are all safe). Strip any
                // whitespace that may have been embedded.
                var clean = body.AsSpan().Trim();
                if (EstimateBase64DecodedUpperBound(clean.Length) > maxBytes)
                {
                    payload = default;
                    return false;
                }
                bytes = Convert.FromBase64String(new string(clean));
                if (bytes.Length > maxBytes)
                {
                    payload = default;
                    return false;
                }
            }
            else
            {
                // Percent-decode the body to bytes.
                if (!TryPercentDecode(body, maxBytes, out bytes))
                {
                    payload = default;
                    return false;
                }
            }
            payload = new Payload(mediaType, bytes);
            return true;
        }
        catch (FormatException)
        {
            payload = default;
            return false;
        }
    }

    private static long EstimateBase64DecodedUpperBound(int encodedLength)
        => ((long)encodedLength + 3) / 4 * 3;

    private static bool TryPercentDecode(string input, int maxBytes, out byte[] bytes)
    {
        var output = new List<byte>(input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c == '%' && i + 2 < input.Length
                && IsHex(input[i + 1]) && IsHex(input[i + 2]))
            {
                output.Add((byte)((FromHex(input[i + 1]) << 4) | FromHex(input[i + 2])));
                if (output.Count > maxBytes)
                {
                    bytes = [];
                    return false;
                }
                i += 2;
            }
            else
            {
                // Non-ASCII characters get UTF-8 encoded.
                if (c < 0x80)
                {
                    output.Add((byte)c);
                    if (output.Count > maxBytes)
                    {
                        bytes = [];
                        return false;
                    }
                }
                else
                {
                    var enc = Encoding.UTF8.GetBytes(new[] { c });
                    output.AddRange(enc);
                    if (output.Count > maxBytes)
                    {
                        bytes = [];
                        return false;
                    }
                }
            }
        }
        bytes = output.ToArray();
        return true;
    }

    private static bool IsHex(char c)
        => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static int FromHex(char c)
        => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;
}
