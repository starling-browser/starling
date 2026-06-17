using Starling.Url;
using StarlingUrl = Starling.Url.Url;

namespace Starling.Gui;

/// <summary>
/// Resolves an anchor's <c>href</c> attribute against the current page URL
/// for navigation. Goes through the WHATWG URL parser used elsewhere in the
/// engine — falling back to <see cref="System.Uri.TryCreate(string,System.UriKind,out System.Uri)"/>
/// is unsafe on Unix, where a path-only href like <c>/products/foo</c> is
/// recognized as an absolute Unix file path and parsed as
/// <c>file:///products/foo</c>, bypassing the base URL.
/// </summary>
public static class LinkResolver
{
    /// <summary>
    /// Resolves <paramref name="href"/> against <paramref name="baseUrl"/>.
    /// Returns the serialized absolute URL on success, or null if the href
    /// cannot be parsed even with the base.
    /// </summary>
    public static string? Resolve(string href, string? baseUrl)
    {
        StarlingUrl? parsedBase = null;
        if (!string.IsNullOrEmpty(baseUrl))
        {
            var baseParsed = UrlParser.Parse(baseUrl);
            if (baseParsed.IsOk)
            {
                parsedBase = baseParsed.Value;
            }
        }

        var parsed = parsedBase is null
            ? UrlParser.Parse(href)
            : UrlParser.Parse(href, parsedBase);
        return parsed.IsOk ? parsed.Value.ToString() : null;
    }
}
