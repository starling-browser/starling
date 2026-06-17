using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Common.Diagnostics;
using Starling.Dom;
using Starling.Dom.Events;
using Starling.Js.Bytecode;
using Starling.Js.Parse;
using Starling.Js.Runtime;
using Starling.Url;
using StarlingUrl = global::Starling.Url.Url;

namespace Starling.Bindings.Backend;

/// <summary>
/// Starling-backend half of HTML §4.12.1 "prepare a script" for the dynamic
/// (<c>src</c>-set-from-JS / runtime-injected) path. Moved here from
/// <c>Starling.Engine.DynamicScriptRunner</c> so the engine no longer touches
/// the JS realm — the dynamic-script + <see cref="ScriptSrcHook"/> coupling now
/// lives entirely inside the backend, as the seam contract requires.
/// </summary>
/// <remarks>
/// Behavior is unchanged from the engine's previous implementation: when JS
/// sets <c>src</c> on a not-yet-started <c>&lt;script&gt;</c>, queue it; the
/// session's pump fetches it, executes it on the shared realm, and fires
/// <c>load</c>/<c>error</c>. The "already started" flag is tracked per element
/// so the parser-batch and any dynamically-run script never run twice.
/// </remarks>
internal sealed class StarlingDynamicScriptRunner
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _log;
    private readonly ILogger _jsConsoleLog;
    private readonly JsRuntime _runtime;
    private readonly StarlingUrl _baseUrl;
    private readonly Func<StarlingUrl, CancellationToken, Task<string?>> _fetch;

    private readonly ConditionalWeakTable<Element, object> _started = new();
    private readonly Queue<Element> _pending = new();
    private static readonly object Marker = new();

    public StarlingDynamicScriptRunner(
        ILoggerFactory loggerFactory, JsRuntime runtime, StarlingUrl baseUrl,
        Func<StarlingUrl, CancellationToken, Task<string?>> fetch)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _log = _loggerFactory.CreateLogger<StarlingDynamicScriptRunner>();
        _jsConsoleLog = _loggerFactory.CreateLogger("Starling.engine.js");
        _runtime = runtime;
        _baseUrl = baseUrl;
        _fetch = fetch;
    }

    /// <summary>Mark a script element as "already started" so a later
    /// <c>src</c> write does not re-run it. Called for every script the parser
    /// batch executed.</summary>
    public void MarkStarted(Element script) => _started.AddOrUpdate(script, Marker);

    /// <summary>True when at least one src-triggered script is waiting to be
    /// fetched+executed.</summary>
    public bool HasPending => _pending.Count > 0;

    /// <summary>Hook target wired into <see cref="ScriptSrcHook"/>: queue a
    /// script whose <c>src</c> was just set, unless it already started. Runs
    /// synchronously on the JS thread, so it only enqueues.</summary>
    public void OnSrcSet(Element script)
    {
        if (_started.TryGetValue(script, out _))
        {
            return;
        }

        _started.AddOrUpdate(script, Marker);
        _pending.Enqueue(script);
    }

    /// <summary>Queue a script-inserted external <c>&lt;script&gt;</c> for the
    /// deferred phase. Idempotent.</summary>
    public void EnqueueInjectedExternal(Element script) => OnSrcSet(script);

    /// <summary>Drain every queued script: fetch, execute on the realm, then
    /// fire load/error. Each execution can enqueue the next script, so we loop
    /// until the queue empties. Returns the count processed.</summary>
    public async Task<int> DrainAsync(CancellationToken ct)
    {
        var processed = 0;
        while (_pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var script = _pending.Dequeue();
            await RunOneAsync(script, ct).ConfigureAwait(false);
            processed++;
        }
        return processed;
    }

    private async Task RunOneAsync(Element script, CancellationToken ct)
    {
        var src = script.GetAttribute("src");
        if (string.IsNullOrWhiteSpace(src))
        {
            FireEvent(script, "error");
            return;
        }

        var absolute = ResolveAbsolute(src, _baseUrl);
        if (absolute is null)
        {
            StarlingDynamicScriptRunnerLog.UnresolvableSrc(_log, src);
            FireEvent(script, "error");
            return;
        }

        string? source;
        try
        {
            source = await _fetch(absolute, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StarlingDynamicScriptRunnerLog.DynamicScriptFetchFailed(_log, ex, absolute);
            source = null;
        }

        if (source is null)
        {
            FireEvent(script, "error");
            return;
        }

        var label = absolute.ToString();
        var ranOk = false;
        _runtime.WithActiveVm(() =>
        {
            try
            {
                var program = new JsParser(source).ParseProgram();
                var chunk = JsCompiler.Compile(program);
                new JsVm(_runtime).Run(chunk);
                StarlingTelemetry.Counter("engine.script.dynamic.ok", 1);
                ranOk = true;
            }
            catch (JsThrow ex)
            {
                StarlingTelemetry.Counter("engine.script.dynamic.failed", 1);
                StarlingDynamicScriptRunnerLog.UncaughtDynamicScriptError(
                    _jsConsoleLog, label, StarlingScriptSession.DescribeThrow(ex.Value));
            }
            catch (Exception ex)
            {
                StarlingTelemetry.Counter("engine.script.dynamic.failed", 1);
                StarlingDynamicScriptRunnerLog.DynamicScriptRunFailed(_jsConsoleLog, ex, label);
            }
        });

        FireEvent(script, ranOk ? "load" : "error");
    }

    private void FireEvent(Element script, string type)
    {
        _runtime.WithActiveVm(() =>
        {
            try
            {
                script.DispatchEvent(new Event(type));
            }
            catch (Exception ex)
            {
                StarlingDynamicScriptRunnerLog.DynamicScriptEventHandlerThrew(_jsConsoleLog, ex, type);
            }
        });
    }

    private static StarlingUrl? ResolveAbsolute(string href, StarlingUrl? baseUrl)
    {
        var parsed = baseUrl is null
            ? UrlParser.Parse(href)
            : UrlParser.Parse(href, baseUrl);
        return parsed.IsOk ? parsed.Value : null;
    }
}

internal static partial class StarlingDynamicScriptRunnerLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "could not resolve dynamic <script src='{Src}'>")]
    public static partial void UnresolvableSrc(ILogger logger, string src);

    [LoggerMessage(Level = LogLevel.Warning, Message = "dynamic script fetch failed: {Absolute}")]
    public static partial void DynamicScriptFetchFailed(ILogger logger, Exception ex, object absolute);

    [LoggerMessage(Level = LogLevel.Warning, Message = "uncaught dynamic script error ({Label}): {JsMessage}")]
    public static partial void UncaughtDynamicScriptError(ILogger logger, string label, string jsMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "dynamic script compile/run failure ({Label})")]
    public static partial void DynamicScriptRunFailed(ILogger logger, Exception ex, string label);

    [LoggerMessage(Level = LogLevel.Warning, Message = "dynamic script '{EventType}' handler threw")]
    public static partial void DynamicScriptEventHandlerThrew(ILogger logger, Exception ex, string eventType);
}
