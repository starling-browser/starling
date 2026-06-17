using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Common.Diagnostics;
using Starling.Dom;
using Starling.Dom.Events;
using Starling.Js.Bytecode;
using Starling.Js.Modules;
using Starling.Js.Parse;
using Starling.Js.Runtime;
using Starling.Js.Hosting;
using Starling.Loop;
using StarlingUrl = global::Starling.Url.Url;
using ConsoleLevel = global::Starling.Js.Hosting.ConsoleLevel;
using JsConsoleLevel = global::Starling.Js.Runtime.ConsoleLevel;

namespace Starling.Bindings.Backend;

/// <summary>
/// The default JS backend: a thin <see cref="IScriptSession"/> wrapper over the
/// from-scratch <c>Starling.Js</c> engine. It encapsulates exactly the wiring
/// that previously lived inline in <c>StarlingEngine.BeginScripts</c> /
/// <c>ExecuteScript</c> / <c>RunModuleScripts</c> / <c>Fire*</c> / the pump
/// methods: it owns the <see cref="JsRuntime"/> and the simulated
/// <see cref="WebEventLoop"/>, installs the Window/Timers/AnimationFrame
/// bindings, owns the dynamic <c>&lt;script src&gt;</c> runner + the
/// <see cref="ScriptSrcHook"/> registration + the runtime-injection routing,
/// runs classic and module scripts, fires DOMContentLoaded/load, drains
/// microtasks, and advances the loop one pump iteration at a time.
/// </summary>
/// <remarks>
/// All JS-engine knowledge stays inside this assembly. The engine talks only to
/// <see cref="IScriptSession"/>, so swapping in the Jint backend never touches
/// the engine's script orchestration. Removing this backend is a single-file
/// delete plus one selector arm.
/// </remarks>
internal sealed class StarlingScriptSession : IScriptSession
{
    private readonly JsRuntime _runtime;
    private readonly WebEventLoop _loop;
    private readonly Document _document;
    private readonly StarlingUrl _baseUrl;
    private readonly ScriptFetcherDelegate _fetcher;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _log;
    private readonly StarlingDynamicScriptRunner _dynamicRunner;
    private readonly StarlingModuleHost _moduleHost;
    private readonly ModuleLoader _moduleLoader;
    private bool _disposed;

    // Live-phase (post-load) clock tracking. The shell drives PumpFrame with a
    // cumulative "ms since it began driving" stopwatch; we advance the loop's
    // monotonic clock by the per-tick delta so timers scheduled past load fire.
    private bool _liveStarted;
    private long _livePrevElapsedMs;

    private Action<ConsoleLevel, string> _consoleSink = static (_, _) => { };

    public StarlingScriptSession(ScriptSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _document = options.Document;
        _baseUrl = options.BaseUrl;
        _fetcher = options.Fetcher;
        _loggerFactory = options.LoggerFactory ?? NullLoggerFactory.Instance;
        _log = _loggerFactory.CreateLogger<StarlingScriptSession>();
        _loop = new WebEventLoop();

        _runtime = new JsRuntime();
        // wp:M3-68 — page JS reads many host globals this engine doesn't
        // implement yet (and pages routinely probe `someGlobal` directly). Keep
        // unresolved global reads lenient (yield undefined, the legacy behavior)
        // for the page realm so a missing host global degrades gracefully instead
        // of throwing a ReferenceError and breaking the page. (The strict,
        // spec-correct default stays on for Test262 / the default realm.)
        _runtime.Realm.ThrowOnUnresolvedGlobalRead = false;

        // Stop button / supersede signal. The VM's dispatch loop polls this
        // every ~1024 opcodes and throws OperationCanceledException, which the
        // engine's navigation catch unwinds without treating as a script error.
        _runtime.AbortToken = options.AbortToken;

        // Route the realm's console through the host sink. The realm sink uses
        // Starling.Js.ConsoleLevel; map to the hosting-neutral enum here so the
        // engine never sees an engine-specific type.
        _runtime.Realm.ConsoleSink = (level, message) => _consoleSink(MapLevel(level), message);

        WindowBinding.Install(_runtime, _document, new WindowInstallOptions(
            DocumentUrl: _baseUrl.ToString(),
            InnerWidth: options.ViewportWidth,
            InnerHeight: options.ViewportHeight,
            HttpClient: options.Http,
            LayoutHost: options.LayoutHost,
            AnimationHost: options.AnimationHost,
            // Back performance.now() with the simulated loop clock so it shares a
            // timeline with rAF timestamps / setTimeout — page animations that ease
            // on (rafTimestamp - performance.now()) then progress correctly.
            MonotonicTimeMs: () => _loop.NowMilliseconds));

        // setTimeout / setInterval / rAF ride the same simulated WebEventLoop
        // clock; PumpOnce advances it.
        TimersBinding.Install(_runtime, _loop);
        AnimationFrameBinding.Install(_runtime, _loop);

        // Dynamic <script src=…> path (HTML §4.12.1 "prepare a script"). The
        // runner shares the session's fetch delegate; it is registered before
        // the NodeConnected hook so the hook can route script-inserted async /
        // external scripts to it.
        _dynamicRunner = new StarlingDynamicScriptRunner(
            _loggerFactory, _runtime, _baseUrl,
            (url, token) => _fetcher(url, token));
        ScriptSrcHook.Register(_runtime.Realm, _dynamicRunner.OnSrcSet);

        _moduleHost = new StarlingModuleHost(_baseUrl, (url, token) => _fetcher(url, token), options.AbortToken);
        _moduleLoader = new ModuleLoader(_runtime, _moduleHost);
    }

    public Action<ConsoleLevel, string> ConsoleSink
    {
        get => _consoleSink;
        set => _consoleSink = value ?? throw new ArgumentNullException(nameof(value));
    }

    private static int s_scriptDumpSeq;

    public void RunClassicScript(string source, string label)
    {
        ArgumentNullException.ThrowIfNull(source);
        // Per-script span so `STARLING_DIAG_TRACE=1` reveals which inline tag
        // or external URL is responsible for the run_scripts wall time. The
        // label is the source url (or "<inline>") set by the engine.
        using var _ = StarlingTelemetry.Span("js", $"run {label} ({source.Length} chars)");
        // STARLING_DIAG_DUMP_SCRIPTS=<dir> writes every classic script to that
        // directory so the source of a slow inline can be inspected without
        // re-fetching the page. Numbered so order matches the trace stream.
        var dumpDir = Environment.GetEnvironmentVariable("STARLING_DIAG_DUMP_SCRIPTS");
        if (!string.IsNullOrEmpty(dumpDir))
        {
            var seq = System.Threading.Interlocked.Increment(ref s_scriptDumpSeq);
            try
            {
                System.IO.Directory.CreateDirectory(dumpDir);
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(dumpDir, $"script-{seq:D3}.js"),
                    "// label: " + label + "\n" + source);
            }
            catch (IOException ex)
            {
                // dumping is best-effort
                StarlingScriptSessionLog.ScriptDumpFailed(_log, ex, dumpDir);
            }
        }
        try
        {
            Starling.Js.Bytecode.Chunk chunk;
            using (StarlingTelemetry.Span("js", "parse+compile"))
            {
                var program = new JsParser(source).ParseProgram();
                chunk = JsCompiler.Compile(program, label);
            }
            var vm = new JsVm(_runtime);
            using (StarlingTelemetry.Span("js", "execute"))
            {
                vm.Run(chunk);
            }
        }
        catch (JsThrow ex)
        {
            throw new ScriptThrow(DescribeThrow(ex.Value), jsStack: ExtractJsStack(ex.Value), ex);
        }
        catch (Starling.Js.Parse.JsParseException ex)
        {
            // A source-level syntax error is a SyntaxError at the JS layer
            // (HTML §4.12.1 fires `error`); it must never escape as a native
            // exception that JS try/catch can't see.
            throw new ScriptThrow($"SyntaxError: {ex.Message}", jsStack: null, ex);
        }
    }

    /// <summary>Pull the JS-side <c>error.stack</c> string off a thrown Error
    /// object so it can ride along in <see cref="ScriptThrow.JsStack"/> and be
    /// logged at the engine's fail-soft path. The Starling VM builds this string
    /// via <c>FormatJsStack</c>. Mirrors the Jint backend's stack passthrough so
    /// uncaught-error diagnostics are identical across engines. Returns
    /// <see langword="null"/> when the thrown value is not an object or has no
    /// <c>stack</c>.</summary>
    internal static string? ExtractJsStack(JsValue v)
    {
        if (!v.IsObject)
        {
            return null;
        }

        try
        {
            var stack = v.AsObject.Get("stack");
            return stack.IsUndefined ? null : JsValue.ToStringValue(stack);
        }
        catch (Exception ex)
        {
            // best-effort: a getter on `stack` could throw; fall back to no stack
            StarlingScriptSessionLog.ExtractJsStackFailed(NullLogger<StarlingScriptSession>.Instance, ex);
            return null;
        }
    }

    public async Task RunModuleScriptAsync(StarlingUrl url, string source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(source);
        ct.ThrowIfCancellationRequested();

        // Entry modules with a real URL load from that URL; an inline module
        // (url == baseUrl with source provided) is registered under a synthetic
        // key so its imports resolve against the document base.
        var isInline = string.Equals(url.ToString(), _baseUrl.ToString(), StringComparison.Ordinal);

        Exception? captured = null;
        _runtime.WithActiveVm(() =>
        {
            try
            {
                if (isInline)
                {
                    var key = _moduleHost.RegisterInlineModule(source);
                    _moduleLoader.LoadAndEvaluate(key);
                }
                else
                {
                    _moduleHost.PrimeSource(url.ToString(), source);
                    _moduleLoader.LoadAndEvaluate(url.ToString());
                }
            }
            catch (JsThrow ex)
            {
                captured = new ScriptThrow(DescribeThrow(ex.Value), jsStack: ExtractJsStack(ex.Value), ex);
            }
        });

        if (captured is not null)
        {
            throw captured;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public void FireDomContentLoaded()
        => _runtime.WithActiveVm(() => WindowBinding.FireDomContentLoaded(_runtime));

    public void FireLoad()
        => _runtime.WithActiveVm(() => WindowBinding.FireLoad(_runtime));

    public void FireBeforeUnload()
        => _runtime.WithActiveVm(() => WindowBinding.FireBeforeUnload(_runtime));

    public void FireUnload()
        => _runtime.WithActiveVm(() => WindowBinding.FireUnload(_runtime));

    public void DrainMicrotasks() => _runtime.WithActiveVm(() => { });

    public bool PumpOnce()
    {
        // One pump iteration, factored out of the engine's old
        // PumpPendingAsync/PumpWithDynamicScriptsAsync inner bodies:
        //   1) drain pending microtasks/promise jobs;
        //   2) else advance the simulated clock so the next due timers + one rAF
        //      frame fire (their callbacks land back on the microtask queue);
        //   3) else run any due dynamic <script src> fetches.
        // Returns true while any front still has pending work, false when fully
        // idle. The engine's outer loop owns the wall-clock / idle budgets.
        const int SimulatedStepMs = 50;

        if (_runtime.Realm.Microtasks.PendingCount > 0)
        {
            _runtime.WithActiveVm(() => { });
            return true;
        }

        // "Run the update intersection observations steps" (IO spec §3.2.2)
        // modelled as a pump front: deliver any queued IntersectionObserver
        // notifications off the layout snapshot. Callbacks can enqueue more work
        // (class toggles, microtasks), picked up on the next iteration.
        //
        // This front sits ABOVE the rAF front on purpose: a self-rescheduling
        // requestAnimationFrame loop keeps PendingAnimationFrameCount > 0 forever,
        // so if the IO front were below it RunPending would never run during the
        // navigation settle, HasPending would stay true, and the rAF-frame-budget
        // early-exit (gated on OnlyAnimationFramePending) would never fire — the
        // settle would burn its full wall-clock cap at full CPU. Draining IO first
        // lets the bucket state converge, after which HasPending goes false and the
        // rAF front settles normally.
        if (Observers.IntersectionObserverBinding.HasPending(_runtime))
        {
            Observers.IntersectionObserverBinding.RunPending(_runtime);
            return true;
        }

        // Dynamic <script src> fetches run BEFORE the clock advances: a real
        // browser's network wins the race against watchdog timers (webpack's
        // chunk loader arms a 120 s timeout per injected script — advancing
        // the simulated clock first fires that timeout before the script ever
        // executes, and every chunk load "times out").
        if (_dynamicRunner.HasPending)
        {
            // Synchronous drain of the queued dynamic scripts. Each fetch+run
            // can enqueue more work (chained loaders) or kick more microtasks,
            // observed on the next PumpOnce iteration.
            _dynamicRunner.DrainAsync(CancellationToken.None).GetAwaiter().GetResult();
            return true;
        }

        if (_loop.PendingTimerCount > 0 || _loop.PendingAnimationFrameCount > 0)
        {
            _loop.AdvanceBy(SimulatedStepMs);
            return true;
        }

        return false;
    }

    public bool OnlyAnimationFramePending =>
        _runtime.Realm.Microtasks.PendingCount == 0
        && _loop.PendingTimerCount == 0
        && !_dynamicRunner.HasPending
        && !HasPendingHostAsyncWork
        && !Observers.IntersectionObserverBinding.HasPending(_runtime)
        && _loop.PendingAnimationFrameCount > 0;

    public bool HasPendingHostAsyncWork => FetchBinding.HasPendingFetches(_runtime);

    /// <summary>HTML §4.12.1 step 8 — only a classic-JavaScript <c>type</c>
    /// (empty/missing, or a JavaScript MIME essence) makes the element a
    /// runnable classic script. Data blocks (<c>application/ld+json</c> and
    /// friends, which React apps inject for structured data) and module
    /// scripts must NOT be fed to the classic parser.</summary>
    private static bool IsClassicJavascriptType(Element script)
    {
        var type = script.GetAttribute("type");
        if (string.IsNullOrWhiteSpace(type))
        {
            return string.IsNullOrWhiteSpace(script.GetAttribute("language"));
        }

        var essence = type.Trim();
        return essence.Equals("text/javascript", StringComparison.OrdinalIgnoreCase)
            || essence.Equals("application/javascript", StringComparison.OrdinalIgnoreCase)
            || essence.Equals("application/ecmascript", StringComparison.OrdinalIgnoreCase)
            || essence.Equals("text/ecmascript", StringComparison.OrdinalIgnoreCase);
    }

    public void OnScriptElementConnected(Node scriptEl)
    {
        ArgumentNullException.ThrowIfNull(scriptEl);
        if (scriptEl is not Element { LocalName: "script" } script)
        {
            return;
        }

        // Data blocks (ld+json etc.) and non-classic types never run here.
        if (!IsClassicJavascriptType(script))
        {
            return;
        }

        // Script-inserted external scripts (and any async-flagged script) are
        // async by default — defer them to the dynamic-script pump rather than
        // running them inline. EnqueueInjectedExternal is idempotent.
        var hasSrc = !string.IsNullOrWhiteSpace(script.GetAttribute("src"));
        if (hasSrc || script.HasAttribute("async"))
        {
            if (hasSrc)
            {
                _dynamicRunner.EnqueueInjectedExternal(script);
            }

            return;
        }

        // Inline non-async injected script: run synchronously on insertion so
        // its side effects are visible to the code that appended it.
        var inline = script.TextContent;
        if (string.IsNullOrWhiteSpace(inline))
        {
            return;
        }

        var label = "<injected inline>";
        try
        {
            RunClassicScript(inline, label);
        }
        catch (ScriptThrow ex)
        {
            StarlingTelemetry.Counter("engine.script.failed", 1);
            StarlingScriptSessionLog.InjectedScriptError(_log, ex, label);
        }
    }

    public void MarkScriptStarted(Node scriptEl)
    {
        ArgumentNullException.ThrowIfNull(scriptEl);
        if (scriptEl is Element script)
        {
            _dynamicRunner.MarkStarted(script);
        }
    }

    public bool DispatchEvent(EventTarget target, Event evt)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(evt);
        if (_disposed)
        {
            return false;
        }

        var before = _document.MutationVersion;
        _runtime.WithActiveVm(() => target.DispatchEvent(evt));
        return _document.MutationVersion != before;
    }

    public bool DispatchScrollEvents(IReadOnlyList<Element> scrolledElements, bool documentScrolled)
    {
        ArgumentNullException.ThrowIfNull(scrolledElements);
        if (_disposed)
        {
            return false;
        }

        if (scrolledElements.Count == 0 && !documentScrolled)
        {
            return false;
        }

        // Same shape as DispatchEvent: run with the VM active so the bridged
        // JS listeners execute, drain the microtasks they queue on exit, and
        // report whether any listener mutated the DOM. The dispatcher never
        // re-reads the scroll store, so a listener that writes an offset
        // re-flags it for the NEXT frame's drain — no same-frame recursion.
        var before = _document.MutationVersion;
        _runtime.WithActiveVm(() =>
            ScrollEventDispatcher.Dispatch(_runtime.Realm, _document, scrolledElements, documentScrolled));
        return _document.MutationVersion != before;
    }

    public bool PumpFrame(long elapsedMs)
    {
        if (_disposed)
        {
            return false;
        }

        if (!_liveStarted)
        {
            _liveStarted = true;
            // Anchor the cumulative stopwatch at zero so the FIRST tick advances
            // by its full elapsed (still clamped below), rather than swallowing
            // the first interval. The shell's first pump arrives at ~0ms anyway;
            // a pump that arrives late after a load stall is caught by the clamp.
            _livePrevElapsedMs = 0;
        }

        var before = _document.MutationVersion;
        // Clamp the per-tick clock advance. When the UI thread stalls (a heavy
        // relayout on a script-busy page), the next tick would otherwise
        // teleport the simulated clock by the whole stall — long watchdog
        // timers (webpack arms a 120s timeout per injected chunk script) then
        // fire before the async script drain ever runs, and every chunk load
        // "times out". Browsers clamp/throttle timers across stalls the same
        // way. Pending work catches up at up to 1s of simulated time per tick.
        const long MaxTickAdvanceMs = 1_000;
        var nowElapsed = Math.Max(0, elapsedMs);
        var advance = Math.Clamp(nowElapsed - _livePrevElapsedMs, 0, MaxTickAdvanceMs);
        _livePrevElapsedMs = nowElapsed;
        var target = _loop.NowMilliseconds + advance;
        _runtime.WithActiveVm(() =>
        {
            // RunFrame requires a non-decreasing clock; a zero-advance tick
            // still drains the realm microtask queue on WithActiveVm exit, so
            // promise/fetch completions are serviced regardless.
            if (advance > 0)
            {
                _loop.RunFrame(target);
            }
        });

        // A script that set `src` on a <script> queues an off-thread fetch+run;
        // kick the drain (fire-and-forget — its completion re-enters via jobs the
        // next pump catches).
        if (_dynamicRunner.HasPending)
        {
            _ = _dynamicRunner.DrainAsync(CancellationToken.None);
        }

        return _document.MutationVersion != before;
    }

    public bool UpdateIntersectionObservations(double viewportX, double viewportY, double viewportWidth, double viewportHeight)
    {
        if (_disposed)
        {
            return false;
        }

        var before = _document.MutationVersion;
        // WithActiveVm drains the microtask queue on exit, so the delivery
        // microtasks the update schedules run (observer callbacks fire) before
        // we read the mutation version back.
        _runtime.WithActiveVm(() =>
            Observers.IntersectionObserverBinding.UpdateForDocument(
                _document, new LayoutRect(viewportX, viewportY, viewportWidth, viewportHeight)));
        return _document.MutationVersion != before;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ScriptSrcHook.Register(_runtime.Realm, null);
    }

    /// <summary>Render a thrown JS value for diagnostics, pulling
    /// <c>name</c>/<c>message</c> out of Error objects so they read legibly
    /// instead of stringifying to "[object Object]".</summary>
    internal static string DescribeThrow(JsValue v)
    {
        if (v.IsObject)
        {
            try
            {
                var o = v.AsObject;
                var name = o.Get("name");
                var message = o.Get("message");
                if (!name.IsUndefined || !message.IsUndefined)
                {
                    var n = name.IsUndefined ? "Error" : JsValue.ToStringValue(name);
                    var m = message.IsUndefined ? "" : JsValue.ToStringValue(message);
                    return string.IsNullOrEmpty(m) ? n : $"{n}: {m}";
                }
            }
            catch (Exception ex)
            {
                // fall through to generic stringification
                StarlingScriptSessionLog.DescribeThrowFailed(NullLogger<StarlingScriptSession>.Instance, ex);
            }
        }
        return JsValue.ToStringValue(v);
    }

    private static ConsoleLevel MapLevel(JsConsoleLevel level) => level switch
    {
        JsConsoleLevel.Log => ConsoleLevel.Log,
        JsConsoleLevel.Info => ConsoleLevel.Info,
        JsConsoleLevel.Warn => ConsoleLevel.Warn,
        JsConsoleLevel.Error => ConsoleLevel.Error,
        JsConsoleLevel.Debug => ConsoleLevel.Debug,
        JsConsoleLevel.Dir => ConsoleLevel.Dir,
        JsConsoleLevel.Table => ConsoleLevel.Table,
        JsConsoleLevel.Trace => ConsoleLevel.Trace,
        _ => ConsoleLevel.Log,
    };
}

/// <summary>
/// The Starling backend's <see cref="IScriptEngineFactory"/>. Named
/// <c>"starling"</c>; this is the default engine selected when
/// <c>STARLING_JS_ENGINE</c> is unset.
/// </summary>
public sealed class StarlingScriptEngineFactory : IScriptEngineFactory
{
    public string Name => "starling";

    public IScriptSession CreateSession(ScriptSessionOptions options)
        => new StarlingScriptSession(options);
}

internal static partial class StarlingScriptSessionLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "script dump to '{DumpDir}' failed (best-effort, ignored)")]
    public static partial void ScriptDumpFailed(ILogger logger, Exception ex, string dumpDir);

    [LoggerMessage(Level = LogLevel.Warning, Message = "injected script error in {Label}")]
    public static partial void InjectedScriptError(ILogger logger, Exception ex, string label);

    [LoggerMessage(Level = LogLevel.Debug, Message = "failed to extract name/message from thrown JS object (falling back to ToString)")]
    public static partial void DescribeThrowFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "failed to read stack from thrown JS object (no JS stack attached)")]
    public static partial void ExtractJsStackFailed(ILogger logger, Exception ex);
}
