using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Starling.Common.Diagnostics;
using Starling.Common.Encoding;
using Starling.Dom;
using Starling.Net;
using Starling.Url;
using StarlingUrl = global::Starling.Url.Url;

namespace Starling.Engine;

/// <summary>
/// Collects every classic <c>&lt;script&gt;</c> element in <see cref="Document"/>
/// order, resolves its source (inline <c>TextContent</c> or a
/// fetched <c>src</c>), and exposes the result list. The engine's
/// <see cref="StarlingEngine"/> then feeds each entry through a single
/// <c>JsRuntime</c> so DOM mutations from earlier scripts are visible to later
/// ones.
/// </summary>
/// <remarks>
/// Scope-down for first wiring:
/// <list type="bullet">
///   <item>Only classic scripts run. <c>type="module"</c>, <c>importmap</c>,
///   and anything with a non-empty unrecognised <c>type</c> attribute is
///   skipped (HTML §4.12.1 "script type").</item>
///   <item><c>async</c> and <c>defer</c> are honoured per HTML §4.12.1
///   "execute the script element": a <c>defer</c> (no <c>async</c>) script runs
///   after parsing in document order; an <c>async</c> script runs as soon as it
///   is available, order-independent; a script with neither attribute runs in
///   document order. (An external <c>async</c> <i>and</i> <c>defer</c> script is
///   treated as <c>async</c>, matching the spec precedence.)</item>
///   <item>Fetch failures are fail-soft (mirroring <see cref="StylesheetFetcher"/>):
///   the script is dropped with a diag warning and the rest of the run
///   continues.</item>
/// </list>
/// </remarks>
internal sealed class ScriptFetcher : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _log;
    private readonly Func<StarlingHttpClient> _httpFactory;
    // Concurrent because FetchAllAsync starts multiple external fetches in
    // parallel; their cache writes (on the network continuation) can race.
    private readonly ConcurrentDictionary<string, string> _byUrl = new(StringComparer.Ordinal);
    private readonly List<LoadedScript> _scripts = new();
    private readonly List<LoadedScript> _moduleScripts = new();
    private readonly HashSet<Element> _loadedElements = new(ReferenceEqualityComparer.Instance);
    private StarlingHttpClient? _sharedHttp;
    private readonly bool _ownsHttp;

    public ScriptFetcher(ILoggerFactory loggerFactory, Func<StarlingHttpClient> httpFactory)
    {
        _loggerFactory = loggerFactory;
        _log = _loggerFactory.CreateLogger<ScriptFetcher>();
        _httpFactory = httpFactory;
        _ownsHttp = true;
    }

    /// <summary>
    /// Use a caller-owned <see cref="StarlingHttpClient"/> so resource fetches
    /// share one connection pool — same-origin requests reuse the keep-alive
    /// transport instead of paying a fresh DNS+TCP+TLS handshake each time. The
    /// shared client is owned by the caller and is not disposed by this fetcher.
    /// </summary>
    public ScriptFetcher(ILoggerFactory loggerFactory, StarlingHttpClient sharedHttp)
    {
        _loggerFactory = loggerFactory;
        _log = _loggerFactory.CreateLogger<ScriptFetcher>();
        _sharedHttp = sharedHttp;
        _httpFactory = () => sharedHttp;
        _ownsHttp = false;
    }

    /// <summary>Scripts collected in document order. Inline entries carry the
    /// document URL as their base; external entries carry the URL they were
    /// fetched from (so syntax errors can be located precisely).</summary>
    public IReadOnlyList<LoadedScript> Scripts => _scripts;

    /// <summary>Module scripts (<c>&lt;script type="module"&gt;</c>) collected in
    /// document order. Per HTML §4.12.1 these execute deferred — after the
    /// document is parsed, in document order — so the engine runs them through a
    /// <c>ModuleLoader</c> after the classic scripts. Inline modules carry their
    /// source text directly; external modules carry their fetched <c>src</c> URL
    /// (the loader fetches the body itself so the import graph is resolved
    /// through one mechanism).</summary>
    public IReadOnlyList<LoadedScript> ModuleScripts => _moduleScripts;

    public async Task FetchAllAsync(Document document, StarlingUrl? baseUrl, CancellationToken ct)
        => await FetchMatchingAsync(document, baseUrl, ScriptFetchPhase.All, ct).ConfigureAwait(false);

    public async Task FetchBlockingAsync(Document document, StarlingUrl? baseUrl, CancellationToken ct)
        => await FetchMatchingAsync(document, baseUrl, ScriptFetchPhase.Blocking, ct).ConfigureAwait(false);

    public async Task FetchDeferredAsync(Document document, StarlingUrl? baseUrl, CancellationToken ct)
        => await FetchMatchingAsync(document, baseUrl, ScriptFetchPhase.Deferred, ct).ConfigureAwait(false);

    public static bool HasRunnableScripts(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);

        foreach (var script in document.GetElementsByTagName("script"))
        {
            if (IsModuleScript(script) || IsClassicJavascript(script))
            {
                return true;
            }
        }

        return false;
    }

    private async Task FetchMatchingAsync(
        Document document,
        StarlingUrl? baseUrl,
        ScriptFetchPhase phase,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);

        // Two-pass fetch so external scripts download in parallel while their
        // results stay bucketed in document order. HTML §4.12.1 constrains
        // execution order (classic non-async scripts run in document order), not
        // fetch order — browsers fetch speculatively in parallel. Pass 1 walks
        // in document order and kicks off each fetch without awaiting (the
        // synchronous prefix — DOM reads, cache probe — still runs sequentially
        // here; only the network I/O overlaps). Pass 2 awaits in order and
        // appends, preserving per-list document order.
        var pending = new List<(Element Element, bool IsModule, Task<LoadedScript?> Task)>();
        foreach (var script in document.GetElementsByTagName("script"))
        {
            ct.ThrowIfCancellationRequested();
            if (_loadedElements.Contains(script))
            {
                continue;
            }

            // Module scripts take a separate branch: their src/inline source is
            // collected but evaluated via the ModuleLoader, not the classic VM.
            if (IsModuleScript(script))
            {
                if (phase is ScriptFetchPhase.All or ScriptFetchPhase.Deferred)
                {
                    pending.Add((script, IsModule: true, LoadModuleAsync(script, baseUrl, ct)));
                }

                continue;
            }

            if (!IsClassicJavascript(script))
            {
                continue;
            }

            var disposition = ClassifyDisposition(script);
            var isBlocking = disposition == ScriptDisposition.None;
            if (phase == ScriptFetchPhase.Blocking && !isBlocking)
            {
                continue;
            }

            if (phase == ScriptFetchPhase.Deferred && isBlocking)
            {
                continue;
            }

            pending.Add((script, IsModule: false, LoadAsync(script, baseUrl, ct)));
        }

        if (pending.Count == 0)
        {
            return;
        }

        // Await all fetches before bucketing. Task.WhenAll observes every task,
        // so a cancellation can't leave an unobserved faulted task behind; it
        // also surfaces cancellation the same way the old sequential loop did.
        await Task.WhenAll(pending.Select(static p => p.Task)).ConfigureAwait(false);

        foreach (var (element, isModule, task) in pending)
        {
            _loadedElements.Add(element);
            var loaded = task.Result; // completed; null = not runnable / fetch failed
            if (loaded is null)
            {
                continue;
            }

            if (isModule)
            {
                _moduleScripts.Add(loaded);
            }
            else
            {
                _scripts.Add(loaded);
            }
        }
    }

    /// <summary>
    /// Resolve and (for an external <c>src</c>) fetch a single classic
    /// <c>&lt;script&gt;</c> into a <see cref="LoadedScript"/>, or return
    /// <c>null</c> if it is not runnable (non-classic type, empty inline, or a
    /// fetch failure). Shared by the document-order collection pass and the
    /// runtime-injection path so both flow through the same resolve/fetch logic.
    /// </summary>
    public async Task<LoadedScript?> LoadAsync(Element script, StarlingUrl? baseUrl, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (!IsClassicJavascript(script))
        {
            return null;
        }

        var disposition = ClassifyDisposition(script);
        var src = script.GetAttribute("src");
        if (string.IsNullOrWhiteSpace(src))
        {
            var inline = script.TextContent;
            if (string.IsNullOrWhiteSpace(inline))
            {
                return null;
            }
            // Inline scripts have no fetch, so async/defer never delay them.
            return new LoadedScript(script, inline, baseUrl, IsInline: true, ScriptDisposition.None);
        }

        var absolute = ResolveAbsolute(src, baseUrl);
        if (absolute is null)
        {
            ScriptFetcherLog.CannotResolveScriptSrc(_log, src);
            return null;
        }

        var source = await FetchAsync(absolute, ct).ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        ScriptFetcherLog.ExternalScriptLoaded(_log, disposition.ToString(), source.Length);

        // Large bundles dominate critical-path wall time on real pages
        // (google.com's xjs bundle is ~1 MB Defer / ~2 s of compile+exec and
        // errors out before any visible effect). Promote any oversize external
        // script to Async — both <em>None</em> (parser-blocking) and
        // <em>Defer</em> (runs before DOMContentLoaded) become Async (runs in
        // the deferred phase, after first paint). Small scripts (jQuery
        // ~30 KB, React ~40 KB, typical analytics shims) stay in their
        // declared phase. The threshold trades correctness-vs-speed:
        //   - None→Async: violates parser-blocking ordering. Scripts that
        //     append inline DOM (rare for large bundles) would render late.
        //   - Defer→Async: violates "before DOMContentLoaded" ordering.
        //     Scripts that wire DOMContentLoaded handlers may miss them, but
        //     most large defer bundles are interactivity-only.
        // 200 KB is a heuristic — measure before raising/lowering.
        const int LargeScriptDeferThresholdBytes = 200 * 1024;
        if ((disposition == ScriptDisposition.None || disposition == ScriptDisposition.Defer)
            && source.Length > LargeScriptDeferThresholdBytes)
        {
            ScriptFetcherLog.DeferringLargeScript(_log, source.Length, disposition.ToString(), absolute.ToString());
            disposition = ScriptDisposition.Async;
        }
        return new LoadedScript(script, source, absolute, IsInline: false, disposition);
    }

    /// <summary>HTML §4.12.1: <c>async</c> takes precedence over <c>defer</c>;
    /// an external script with only <c>defer</c> runs after parse in document
    /// order. Both flags are ignored for inline scripts (no fetch to gate on),
    /// which is handled by <see cref="LoadAsync"/>.</summary>
    private static ScriptDisposition ClassifyDisposition(Element script)
    {
        if (script.HasAttribute("async"))
        {
            return ScriptDisposition.Async;
        }

        if (script.HasAttribute("defer"))
        {
            return ScriptDisposition.Defer;
        }

        return ScriptDisposition.None;
    }

    /// <summary>HTML §4.12.1: <c>type="module"</c> (case-insensitively) selects
    /// a module script. Module type takes precedence over the classic-script
    /// MIME check.</summary>
    private static bool IsModuleScript(Element script)
    {
        var type = script.GetAttribute("type");
        return type is not null && type.Trim().Equals("module", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Collect a <c>&lt;script type="module"&gt;</c>. Inline modules
    /// keep their source and the document base URL; external modules record
    /// their resolved <c>src</c> URL so the loader can fetch + parse the import
    /// graph rooted there. Module scripts are deferred (run after parse via the
    /// <c>ModuleLoader</c>), so they carry <see cref="ScriptDisposition.Defer"/>.</summary>
    private async Task<LoadedScript?> LoadModuleAsync(Element script, StarlingUrl? baseUrl, CancellationToken ct)
    {
        var src = script.GetAttribute("src");
        if (string.IsNullOrWhiteSpace(src))
        {
            var inline = script.TextContent;
            if (string.IsNullOrWhiteSpace(inline))
            {
                return null;
            }

            return new LoadedScript(script, inline, baseUrl, IsInline: true, ScriptDisposition.Defer);
        }

        var absolute = ResolveAbsolute(src, baseUrl);
        if (absolute is null)
        {
            ScriptFetcherLog.CannotResolveModuleScriptSrc(_log, src);
            return null;
        }

        // Pre-warm the fetch cache so the loader's host can reuse the body. We
        // still record the URL (not the body) so the loader treats it as the
        // entry module and resolves its imports relative to it.
        var source = await FetchAsync(absolute, ct).ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        return new LoadedScript(script, source, absolute, IsInline: false, ScriptDisposition.Defer);
    }

    /// <summary>Fetch a module's source through the engine's shared script-fetch
    /// path (file:// + http), reusing the URL cache. Exposed for the engine's
    /// <c>ModuleLoader</c> host so the whole import graph flows through one
    /// mechanism.</summary>
    public Task<string?> FetchModuleSourceAsync(StarlingUrl url, CancellationToken ct) => FetchAsync(url, ct);

    /// <summary>HTML §4.12.1: an empty / absent <c>type</c> (or <c>"text/javascript"</c>
    /// case-insensitively, plus the historical aliases) is a classic script.
    /// Anything else — including <c>"module"</c>, MIME types we don't know,
    /// and explicit non-JS like <c>"application/ld+json"</c> — is a non-running
    /// data block. Returns <c>true</c> only for classic JS.</summary>
    private static bool IsClassicJavascript(Element script)
    {
        var type = script.GetAttribute("type");
        if (string.IsNullOrWhiteSpace(type))
        {
            return true;
        }

        var trimmed = type.Trim();
        // Strip MIME parameters (e.g. "text/javascript; charset=utf-8").
        var semi = trimmed.IndexOf(';');
        if (semi >= 0)
        {
            trimmed = trimmed[..semi].Trim();
        }

        return trimmed.Equals("text/javascript", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("application/javascript", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("application/ecmascript", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("text/ecmascript", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("application/x-javascript", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Fetch a script's source for the dynamic (src-set-from-JS) path.
    /// Shares the same scheme handling, decode, and per-URL cache as the static
    /// parser-batch path so a bundle requested both ways is only fetched once.
    /// Exposed for <c>DynamicScriptRunner</c>.</summary>
    public Task<string?> FetchSourceAsync(StarlingUrl url, CancellationToken ct)
        => FetchAsync(url, ct);

    private async Task<string?> FetchAsync(StarlingUrl url, CancellationToken ct)
    {
        var key = url.ToString();
        if (_byUrl.TryGetValue(key, out var cached))
        {
            return cached;
        }

        using var _ = StarlingTelemetry.Span("engine", "fetch_script");
        Activity.Current?.SetTag("url", key);

        // data: URLs decode locally — no network. The deferred-bundle repro
        // uses `data:text/javascript,…` so the core behavior is testable
        // offline; real loaders point at http(s) bundles handled below.
        if (url.IsData)
        {
            if (DataUrl.TryDecode(url, out var payload))
            {
                var decoded = Decode(payload.MediaType, payload.Bytes);
                _byUrl[key] = decoded;
                StarlingTelemetry.Counter("engine.fetch.script", 1);
                return decoded;
            }
            ScriptFetcherLog.MalformedDataScriptUrl(_log);
            StarlingTelemetry.Counter("engine.fetch.script.failed", 1);
            return null;
        }

        try
        {
            byte[] bytes;
            string? contentType = null;
            if (url.IsFile)
            {
                var path = url.ToFileSystemPath();
                if (!File.Exists(path))
                {
                    ScriptFetcherLog.MissingLocalScript(_log, path);
                    StarlingTelemetry.Counter("engine.fetch.script.failed", 1);
                    return null;
                }
                bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            }
            else if (url.IsHttp || url.IsHttps)
            {
                _sharedHttp ??= _httpFactory();
                var response = await _sharedHttp.GetAsync(url, ct).ConfigureAwait(false);
                if (response.IsErr)
                {
                    ScriptFetcherLog.ScriptFetchFailed(_log, url.ToString(), response.Error.ToString());
                    StarlingTelemetry.Counter("engine.fetch.script.failed", 1);
                    return null;
                }
                Activity.Current?.SetTag("http.status_code", response.Value.StatusCode);
                if (response.Value.StatusCode is < 200 or >= 400)
                {
                    ScriptFetcherLog.ScriptFetchHttpError(_log, response.Value.StatusCode, url.ToString());
                    StarlingTelemetry.Counter("engine.fetch.script.failed", 1);
                    return null;
                }
                bytes = response.Value.Body.ToArray();
                contentType = response.Value.Headers.GetFirst("Content-Type");
            }
            else
            {
                ScriptFetcherLog.UnsupportedScriptScheme(_log, url.Scheme, url.ToString());
                StarlingTelemetry.Counter("engine.fetch.script.failed", 1);
                return null;
            }

            Activity.Current?.SetTag("bytes", bytes.Length);
            var source = Decode(contentType, bytes);
            _byUrl[key] = source;
            StarlingTelemetry.Counter("engine.fetch.script", 1);
            return source;
        }
        catch (IOException ex)
        {
            ScriptFetcherLog.ScriptReadFailed(_log, ex, url.ToString());
            StarlingTelemetry.Counter("engine.fetch.script.failed", 1);
            return null;
        }
    }

    private static string Decode(string? contentType, byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (contentType is { Length: > 0 })
        {
            var charset = ExtractCharset(contentType);
            if (charset is not null && TryResolveEncoding(charset) is { } encoding)
            {
                return encoding.GetString(bytes);
            }
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private static string? ExtractCharset(string headerValue)
    {
        foreach (var raw in headerValue.Split(';'))
        {
            var part = raw.Trim();
            const string prefix = "charset=";
            if (part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return part[prefix.Length..].Trim().Trim('"', '\'');
            }
        }
        return null;
    }

    private static Encoding? TryResolveEncoding(string name)
    {
        var canonical = WhatwgEncodingLabels.TryGetCanonicalName(name);
        if (canonical is null)
        {
            return null;
        }

        return canonical switch
        {
            "UTF-8" => Encoding.UTF8,
            "UTF-16LE" => Encoding.Unicode,
            "UTF-16BE" => Encoding.BigEndianUnicode,
            _ => TryGetEncodingByName(canonical),
        };
    }

    private static Encoding? TryGetEncodingByName(string name)
    {
        try { return Encoding.GetEncoding(name); }
        catch (ArgumentException) { return null; }
    }

    private static StarlingUrl? ResolveAbsolute(string href, StarlingUrl? baseUrl)
    {
        var parsed = baseUrl is null
            ? UrlParser.Parse(href)
            : UrlParser.Parse(href, baseUrl);
        return parsed.IsOk ? parsed.Value : null;
    }

    public void Dispose()
    {
        _byUrl.Clear();
        _scripts.Clear();
        _moduleScripts.Clear();
        _loadedElements.Clear();
        if (_ownsHttp)
        {
            _sharedHttp?.Dispose();
        }

        _sharedHttp = null;
    }
}

internal enum ScriptFetchPhase
{
    All,
    Blocking,
    Deferred,
}

/// <summary>One <c>&lt;script&gt;</c>'s source text plus the URL syntax errors
/// should be reported against, and how its execution is ordered relative to
/// document parsing (<see cref="ScriptDisposition"/>).</summary>
internal sealed record LoadedScript(
    Element Element, string Source, StarlingUrl? BaseUrl, bool IsInline, ScriptDisposition Disposition);

/// <summary>HTML §4.12.1 classic-script execution ordering.</summary>
internal enum ScriptDisposition
{
    /// <summary>Neither <c>async</c> nor <c>defer</c>: runs in document order.</summary>
    None,

    /// <summary><c>async</c>: runs as soon as it is fetched, order-independent.</summary>
    Async,

    /// <summary><c>defer</c> (without <c>async</c>): runs after parsing
    /// completes, in document order, before <c>DOMContentLoaded</c>.</summary>
    Defer,
}

internal static partial class ScriptFetcherLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not resolve <script src='{Src}'>")]
    public static partial void CannotResolveScriptSrc(ILogger logger, string src);

    [LoggerMessage(Level = LogLevel.Information, Message = "external script disposition={Disposition} bytes={Bytes}")]
    public static partial void ExternalScriptLoaded(ILogger logger, string disposition, int bytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "deferring large external script ({Bytes} bytes, was {Disposition}): {Url}")]
    public static partial void DeferringLargeScript(ILogger logger, int bytes, string disposition, string url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not resolve <script type=module src='{Src}'>")]
    public static partial void CannotResolveModuleScriptSrc(ILogger logger, string src);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Malformed data: script URL")]
    public static partial void MalformedDataScriptUrl(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Missing local script: {Path}")]
    public static partial void MissingLocalScript(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Script fetch failed {Url}: {Error}")]
    public static partial void ScriptFetchFailed(ILogger logger, string url, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Script fetch HTTP {StatusCode} from {Url}")]
    public static partial void ScriptFetchHttpError(ILogger logger, int statusCode, string url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unsupported script scheme '{Scheme}' for {Url}")]
    public static partial void UnsupportedScriptScheme(ILogger logger, string scheme, string url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Script read failed {Url}")]
    public static partial void ScriptReadFailed(ILogger logger, Exception ex, string url);
}
