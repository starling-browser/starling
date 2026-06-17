using System.Collections.Concurrent;
using Jint;
using Jint.Native;
using Jint.Runtime.Modules;
using Starling.Url;
using StarlingUrl = global::Starling.Url.Url;

namespace Starling.Bindings.Jint;

// ES module loader (static import, top-level await, dynamic import()).
// Mirrors the Starling backend's StarlingModuleHost: specifiers resolve via
// Starling.Url against the importing module's URL (or the document base for the
// entry / inline modules), and module bodies load through the session's shared
// fetch path (file/data/http). Top-level await + dynamic import() ride Jint's
// own module machinery once EnableModules is wired at engine construction.
//
// Unlike other binding families, the module surface cannot be installed onto
// an already-constructed engine: Jint requires the IModuleLoader (and the
// import.meta host) to be supplied to the Engine constructor via
// options.EnableModules(...) / options.UseHostFactory(...). JintScriptSession
// therefore constructs the loader first and passes it into the engine factory;
// ModuleLoader.Install stays a no-op so JintBindings.InstallAll keeps its stable
// shape.
internal static class ModuleLoader
{
    public static void Install(JintBackendContext ctx)
    {
        // Intentionally empty. The module loader + import.meta host are wired at
        // engine construction (see JintScriptSession + StarlingJintModuleLoader),
        // not onto a live engine. This keeps the InstallAll dispatcher order
        // intact while satisfying Jint's requirement that
        // module support be enabled before the realm is built.
        _ = ctx;
    }
}

/// <summary>
/// The Jint backend's <see cref="IModuleLoader"/>. Resolves import specifiers
/// relative to the importing module's URL (or the document <c>BaseUrl</c> for the
/// entry / inline modules) via <c>Starling.Url</c>, and loads module source
/// through the session's shared fetch delegate — the same file/data/http path
/// classic scripts and the dynamic-script runner use.
/// </summary>
/// <remarks>
/// Jint's <see cref="IModuleLoader"/> is synchronous-facing, so HTTP(S) fetches
/// block on the async fetch delegate (matching the Starling backend's
/// <c>StarlingModuleHost.FetchSource</c>). Entry + inline module bodies are
/// primed before evaluation so the loader never re-fetches them; an inline
/// <c>&lt;script type="module"&gt;</c> body has no URL of its own, so it is keyed
/// under a synthetic <c>about:inline-N</c> specifier whose import base is the
/// document URL. <c>data:</c> URLs and bare/relative specifiers are handled by
/// <c>Starling.Url</c>; bare specifiers (no <c>./</c>, <c>../</c>, or scheme)
/// resolve against the base like every other relative reference (there is no
/// import-map support yet — see the Wave-3 gap note).
/// </remarks>
internal sealed class StarlingJintModuleLoader : IModuleLoader
{
    private readonly StarlingUrl _baseUrl;
    private readonly Func<StarlingUrl, CancellationToken, Task<string?>> _fetch;

    // Primed/inline bodies keyed by their resolved specifier (Key). Both maps are
    // touched only on the JS thread during module evaluation, but a concurrent
    // dictionary keeps things safe should a fetch completion ever prime late.
    private readonly ConcurrentDictionary<string, string> _primed =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StarlingUrl> _inlineBases =
        new(StringComparer.Ordinal);
    private int _inlineCounter;

    public StarlingJintModuleLoader(
        StarlingUrl baseUrl,
        Func<StarlingUrl, CancellationToken, Task<string?>> fetch)
    {
        _baseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
    }

    /// <summary>Prime an already-fetched entry-module body so the loader reuses
    /// it instead of re-fetching the entry URL. Returns the resolved specifier
    /// the engine should import.</summary>
    public string RegisterEntry(StarlingUrl url, string source)
    {
        var key = url.ToString();
        _primed[key] = source;
        return key;
    }

    /// <summary>Register an inline module body (no URL of its own), returning the
    /// synthetic specifier to import. Its imports resolve against the document
    /// base.</summary>
    public string RegisterInline(string source)
    {
        var key = $"about:inline-{System.Threading.Interlocked.Increment(ref _inlineCounter)}";
        _primed[key] = source;
        _inlineBases[key] = _baseUrl;
        return key;
    }

    public ResolvedSpecifier Resolve(string? referencingModuleLocation, ModuleRequest moduleRequest)
    {
        var specifier = moduleRequest.Specifier;

        // An already-known synthetic/inline or primed key resolves to itself.
        // The Key is the module's full URL; Uri is left null so Jint reports the
        // Key (not Uri.LocalPath) as Module.Location — that drives import.meta.url.
        if (_primed.ContainsKey(specifier))
        {
            return new ResolvedSpecifier(
                moduleRequest, specifier, Uri: null, SpecifierType.RelativeOrAbsolute);
        }

        var baseUrl = ResolveBase(referencingModuleLocation);
        var parsed = UrlParser.Parse(specifier, baseUrl);
        if (parsed.IsErr)
        {
            // Surface as a bare/error specifier; LoadModule turns a miss into a
            // module-not-found throw the JS side observes.
            return new ResolvedSpecifier(moduleRequest, specifier, Uri: null, SpecifierType.Bare);
        }

        var key = parsed.Value.ToString();
        return new ResolvedSpecifier(
            moduleRequest, key, Uri: null, SpecifierType.RelativeOrAbsolute);
    }

    public Module LoadModule(Engine engine, ResolvedSpecifier resolved)
    {
        var key = resolved.Key;

        if (!_primed.TryGetValue(key, out var source))
        {
            source = FetchSource(key);
            if (source is null)
            {
                throw new ModuleResolutionException(
                    "Module not found", resolved.ModuleRequest.Specifier, parent: key, filePath: key);
            }
            // Cache so a re-import of the same specifier reuses the body and the
            // module identity stays stable.
            _primed[key] = source;
        }

        return ModuleFactory.BuildSourceTextModule(
            engine, resolved, source, ModuleParsingOptions.Default);
    }

    // ---- internals ----

    private StarlingUrl ResolveBase(string? referencingModuleLocation)
    {
        if (string.IsNullOrEmpty(referencingModuleLocation))
        {
            return _baseUrl;
        }

        // Inline modules carry the document base for their imports.
        if (_inlineBases.TryGetValue(referencingModuleLocation, out var inlineBase))
        {
            return inlineBase;
        }

        var parsed = UrlParser.Parse(referencingModuleLocation);
        return parsed.IsOk ? parsed.Value : _baseUrl;
    }

    private string? FetchSource(string resolvedUrl)
    {
        var parsed = UrlParser.Parse(resolvedUrl);
        if (parsed.IsErr)
        {
            return null;
        }
        // The IModuleLoader contract is synchronous; block on the async fetch the
        // same way the Starling backend's StarlingModuleHost does. The session's
        // ScriptFetcher cache is shared, so parser-discovered modules hit warm.
        return _fetch(parsed.Value, CancellationToken.None).GetAwaiter().GetResult();
    }
}

/// <summary>
/// Jint <see cref="global::Jint.Runtime.Host"/> override that populates
/// <c>import.meta.url</c> from the evaluating module's location (its resolved
/// specifier / URL). Wired via <c>options.UseHostFactory(...)</c> at engine
/// construction. Without this, <c>import.meta</c> is an empty object in Jint
/// 4.9.2.
/// </summary>
internal sealed class StarlingJintModuleMetaHost : global::Jint.Runtime.Host
{
    public override List<KeyValuePair<JsValue, JsValue>> GetImportMetaProperties(Module module)
    {
        var location = module.Location ?? string.Empty;
        return new List<KeyValuePair<JsValue, JsValue>>
        {
            new(new JsString("url"), new JsString(location)),
        };
    }
}
