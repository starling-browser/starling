using Starling.Js.Modules;
using Starling.Url;
using StarlingUrl = global::Starling.Url.Url;

namespace Starling.Bindings.Backend;

/// <summary>
/// <see cref="IModuleHost"/> for the Starling backend, backed by the session's
/// <c>ScriptFetcherDelegate</c>. Resolves specifiers via <c>Starling.Url</c> and
/// fetches module bodies through the same file/data/http path classic scripts
/// use. Mirrors the engine's previous <c>EngineModuleHost</c>; it lives in the
/// backend now so the engine never references the module loader directly.
/// </summary>
/// <remarks>
/// The <see cref="ModuleLoader"/> contract is synchronous; HTTP(S) fetches block
/// on the async delegate. Inline <c>&lt;script type="module"&gt;</c> bodies have
/// no URL of their own, so they register under a synthetic <c>about:inline-N</c>
/// key whose import base is the document URL. The entry module's source is
/// primed (so the loader does not re-fetch it).
/// </remarks>
internal sealed class StarlingModuleHost : IModuleHost
{
    private readonly StarlingUrl _documentUrl;
    private readonly Func<StarlingUrl, CancellationToken, Task<string?>> _fetch;
    private readonly CancellationToken _ct;

    private readonly Dictionary<string, string> _inlineSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StarlingUrl> _inlineBases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _primed = new(StringComparer.Ordinal);
    private int _inlineCounter;

    public StarlingModuleHost(
        StarlingUrl documentUrl,
        Func<StarlingUrl, CancellationToken, Task<string?>> fetch,
        CancellationToken ct)
    {
        _documentUrl = documentUrl;
        _fetch = fetch;
        _ct = ct;
    }

    /// <summary>Register an inline module body, returning its synthetic key.</summary>
    public string RegisterInlineModule(string source)
    {
        var key = $"about:inline-{_inlineCounter++}";
        _inlineSources[key] = source;
        _inlineBases[key] = _documentUrl;
        return key;
    }

    /// <summary>Prime an already-fetched module body so the loader reuses it
    /// instead of re-fetching the entry URL.</summary>
    public void PrimeSource(string resolvedUrl, string source) => _primed[resolvedUrl] = source;

    public string? Resolve(string specifier, string? referrer)
    {
        if (_inlineSources.ContainsKey(specifier))
        {
            return specifier;
        }

        StarlingUrl baseUrl = _documentUrl;
        if (referrer is not null)
        {
            if (_inlineBases.TryGetValue(referrer, out var inlineBase))
            {
                baseUrl = inlineBase;
            }
            else
            {
                var parsedReferrer = UrlParser.Parse(referrer);
                if (parsedReferrer.IsOk)
                {
                    baseUrl = parsedReferrer.Value;
                }
            }
        }

        var parsed = UrlParser.Parse(specifier, baseUrl);
        return parsed.IsOk ? parsed.Value.ToString() : null;
    }

    public string? FetchSource(string resolvedUrl)
    {
        if (_inlineSources.TryGetValue(resolvedUrl, out var inline))
        {
            return inline;
        }

        if (_primed.TryGetValue(resolvedUrl, out var primed))
        {
            return primed;
        }

        var parsed = UrlParser.Parse(resolvedUrl);
        if (parsed.IsErr)
        {
            return null;
        }

        return _fetch(parsed.Value, _ct).GetAwaiter().GetResult();
    }
}
