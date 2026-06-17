using AwesomeAssertions;
using Starling.Js.Bytecode;
using Starling.Js.Modules;
using Starling.Js.Parse;
using Starling.Js.Runtime;

namespace Starling.Js.Tests;

/// <summary>
/// wp:M3-03c — dynamic <c>import()</c> + <c>import.meta</c>.
/// <para>
/// <c>import(specifier)</c> is a call-like expression (valid in modules AND
/// classic scripts) that returns a Promise of the imported module's namespace
/// object. A dynamically-imported module with top-level await resolves only
/// after it settles. <c>import.meta</c> exposes the running module's host meta
/// object (at least <c>import.meta.url</c>).
/// </para>
/// </summary>
/// <remarks>
/// Uses the same in-memory <see cref="IModuleHost"/> + <c>report()</c> harness as
/// <c>JsModuleTests</c> / <c>TopLevelAwaitTests</c>. Tests are deterministic:
/// <c>WithActiveVm</c> / <c>LoadAndEvaluate</c> drain the microtask queue, so the
/// chained <c>.then</c> the import promise rides settles before assertions — no
/// sleeps.
/// </remarks>
[TestClass]
public class DynamicImportTests
{
    /// <summary>In-memory module host: identity resolution over a flat map of
    /// specifiers to source text (mirrors <c>JsModuleTests.MapHost</c>).</summary>
    private sealed class MapHost(Dictionary<string, string> modules) : IModuleHost
    {
        public string? Resolve(string specifier, string? referrer) =>
            modules.ContainsKey(specifier) ? specifier : null;

        public string? FetchSource(string resolvedUrl) =>
            modules.TryGetValue(resolvedUrl, out var src) ? src : null;
    }

    /// <summary>Evaluate the entry module and return the single value the entry
    /// passed to <c>report(...)</c>.</summary>
    private static JsValue RunGraph(Dictionary<string, string> modules, string entry)
    {
        JsValue captured = JsValue.Undefined;
        var runtime = new JsRuntime();
        runtime.RegisterGlobal("report", args =>
        {
            captured = args.Length > 0 ? args[0] : JsValue.Undefined;
            return JsValue.Undefined;
        });
        var loader = new ModuleLoader(runtime, new MapHost(modules));
        runtime.WithActiveVm(() => loader.LoadAndEvaluate(entry));
        return captured;
    }

    /// <summary>Evaluate the entry module, collecting every <c>report(...)</c> call
    /// in order so a test can assert ordering (e.g. that a TLA module's body runs
    /// before the import resolution observes its exports).</summary>
    private static List<string> RunGraphLog(Dictionary<string, string> modules, string entry)
    {
        var log = new List<string>();
        var runtime = new JsRuntime();
        runtime.RegisterGlobal("report", args =>
        {
            log.Add(args.Length > 0 ? JsValue.ToStringValue(args[0]) : "undefined");
            return JsValue.Undefined;
        });
        var loader = new ModuleLoader(runtime, new MapHost(modules));
        runtime.WithActiveVm(() => loader.LoadAndEvaluate(entry));
        return log;
    }

    /// <summary>Run a classic (non-module) script that uses <c>import()</c>. A
    /// loader is registered on the realm (so <c>import()</c> can reach it) but the
    /// top-level code itself is compiled and run as an ordinary script.</summary>
    private static JsValue RunClassicScript(string source, Dictionary<string, string> modules)
    {
        JsValue captured = JsValue.Undefined;
        var runtime = new JsRuntime();
        runtime.RegisterGlobal("report", args =>
        {
            captured = args.Length > 0 ? args[0] : JsValue.Undefined;
            return JsValue.Undefined;
        });
        // Construction registers the loader on the realm; the script never calls
        // LoadAndEvaluate — it's a classic script that triggers import() at runtime.
        _ = new ModuleLoader(runtime, new MapHost(modules));
        var program = new JsParser(source).ParseProgram();
        var chunk = JsCompiler.Compile(program);
        runtime.WithActiveVm(() => new JsVm(runtime).Run(chunk));
        return captured;
    }

    /// <summary>wp:M3-63 — a referrer-aware host that resolves a relative
    /// specifier (<c>./x</c> / <c>../x</c>) against the importing referrer's
    /// directory (POSIX-style), mirroring what <c>FsModuleHost</c> does on disk.
    /// This is the resolution the engine relies on: a relative <c>import()</c>
    /// must be handed the ACTIVE SCRIPT/MODULE path as its referrer, so it lands
    /// in the right directory. The flat <see cref="MapHost"/> can't catch the
    /// bug (it ignores the referrer); this host requires the correct referrer.</summary>
    private sealed class DirHost(Dictionary<string, string> modules) : IModuleHost
    {
        public string? Resolve(string specifier, string? referrer)
        {
            // Bare specifiers resolve by identity (the entry module's URL).
            if (!specifier.StartsWith("./", StringComparison.Ordinal)
                && !specifier.StartsWith("../", StringComparison.Ordinal))
            {
                return modules.ContainsKey(specifier) ? specifier : null;
            }

            // Relative specifier: join against the referrer's directory.
            var baseDir = referrer is null ? "" : DirOf(referrer);
            var resolved = NormalizeJoin(baseDir, specifier);
            return modules.ContainsKey(resolved) ? resolved : null;
        }

        public string? FetchSource(string resolvedUrl) =>
            modules.TryGetValue(resolvedUrl, out var src) ? src : null;

        private static string DirOf(string url)
        {
            var slash = url.LastIndexOf('/');
            return slash < 0 ? "" : url[..slash];
        }

        private static string NormalizeJoin(string baseDir, string rel)
        {
            var absolute = baseDir.StartsWith('/');
            var segments = new List<string>();
            if (baseDir.Length > 0)
            {
                segments.AddRange(baseDir.Split('/', StringSplitOptions.RemoveEmptyEntries));
            }

            foreach (var part in rel.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".")
                {
                    continue;
                }

                if (part == "..")
                {
                    if (segments.Count > 0)
                    {
                        segments.RemoveAt(segments.Count - 1);
                    }

                    continue;
                }
                segments.Add(part);
            }
            var joined = string.Join('/', segments);
            return absolute ? "/" + joined : joined;
        }
    }

    /// <summary>wp:M3-63 — run a classic script compiled WITH a source path (so
    /// its top-level chunk + every nested function chunk carry that path as
    /// their referrer). Returns the value passed to <c>report(...)</c>.</summary>
    private static JsValue RunClassicScriptAtPath(
        string scriptPath, string source, Dictionary<string, string> modules)
    {
        JsValue captured = JsValue.Undefined;
        var runtime = new JsRuntime();
        runtime.RegisterGlobal("report", args =>
        {
            captured = args.Length > 0 ? args[0] : JsValue.Undefined;
            return JsValue.Undefined;
        });
        _ = new ModuleLoader(runtime, new DirHost(modules));
        var program = new JsParser(source).ParseProgram();
        var chunk = JsCompiler.Compile(program, scriptPath);
        runtime.WithActiveVm(() => new JsVm(runtime).Run(chunk));
        return captured;
    }

    // -----------------------------------------------------------------------
    // wp:M3-63 — a relative import() resolves against the active script/module
    // path regardless of which (possibly nested) function it appears in.
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Relative_import_at_top_level_resolves_against_script_path()
    {
        var modules = new Dictionary<string, string>
        {
            ["/proj/pkg/dep.js"] = "export const v = 'top';",
        };
        var source = @"
            import('./dep.js').then(function(ns) { report(ns.v); });";
        RunClassicScriptAtPath("/proj/pkg/main.js", source, modules)
            .AsString.Should().Be("top");
    }

    [TestMethod]
    public void Relative_import_inside_async_function_resolves_against_script_path()
    {
        // The bug: `await import('./dep.js')` inside an async function compiled
        // to its OWN chunk whose Name was the function name (not the script
        // path), so the relative specifier resolved against the wrong dir.
        var modules = new Dictionary<string, string>
        {
            ["/proj/pkg/dep.js"] = "export const v = 'async';",
        };
        var source = @"
            async function load() {
                const ns = await import('./dep.js');
                report(ns.v);
            }
            load();";
        RunClassicScriptAtPath("/proj/pkg/main.js", source, modules)
            .AsString.Should().Be("async");
    }

    [TestMethod]
    public void Relative_import_inside_nested_arrow_resolves_against_script_path()
    {
        // Same active-referrer rule through a nested arrow (its own chunk too).
        var modules = new Dictionary<string, string>
        {
            ["/proj/pkg/dep.js"] = "export const v = 'arrow';",
        };
        var source = @"
            const go = () => {
                const inner = () => import('./dep.js').then(function(ns){ report(ns.v); });
                inner();
            };
            go();";
        RunClassicScriptAtPath("/proj/pkg/main.js", source, modules)
            .AsString.Should().Be("arrow");
    }

    [TestMethod]
    public void Relative_import_from_all_nesting_levels_resolves_identically()
    {
        // Top-level, async function, and nested arrow must ALL resolve the same
        // relative specifier to the SAME script-relative module.
        var modules = new Dictionary<string, string>
        {
            ["/a/b/dep.js"] = "export const v = 'same';",
        };

        var topSrc = "import('./dep.js').then(function(ns){ report(ns.v); });";
        var asyncSrc = @"
            async function f(){ const ns = await import('./dep.js'); report(ns.v); }
            f();";
        var arrowSrc = "(() => import('./dep.js').then(function(ns){ report(ns.v); }))();";

        RunClassicScriptAtPath("/a/b/main.js", topSrc, modules).AsString.Should().Be("same");
        RunClassicScriptAtPath("/a/b/main.js", asyncSrc, modules).AsString.Should().Be("same");
        RunClassicScriptAtPath("/a/b/main.js", arrowSrc, modules).AsString.Should().Be("same");
    }

    [TestMethod]
    public void Relative_import_in_module_nested_function_uses_module_referrer()
    {
        // A static module graph: the entry module does the relative import from
        // inside a nested async function. The referrer must be the module URL,
        // so '../lib/dep.js' resolves to the sibling-dir module.
        var modules = new Dictionary<string, string>
        {
            ["/src/app/main.js"] = @"
                async function load(){ const ns = await import('../lib/dep.js'); report(ns.v); }
                load();",
            ["/src/lib/dep.js"] = "export const v = 'mod-nested';",
        };
        RunGraphDir(modules, "/src/app/main.js").AsString.Should().Be("mod-nested");
    }

    /// <summary>wp:M3-63 — like <see cref="RunGraph"/> but with the
    /// referrer-aware <see cref="DirHost"/> so relative specifiers resolve
    /// against the importing module's directory.</summary>
    private static JsValue RunGraphDir(Dictionary<string, string> modules, string entry)
    {
        JsValue captured = JsValue.Undefined;
        var runtime = new JsRuntime();
        runtime.RegisterGlobal("report", args =>
        {
            captured = args.Length > 0 ? args[0] : JsValue.Undefined;
            return JsValue.Undefined;
        });
        var loader = new ModuleLoader(runtime, new DirHost(modules));
        runtime.WithActiveVm(() => loader.LoadAndEvaluate(entry));
        return captured;
    }

    // -----------------------------------------------------------------------
    // import() — namespace resolution
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Dynamic_import_resolves_named_export()
    {
        var modules = new Dictionary<string, string>
        {
            ["m"] = "export const answer = 42;",
            ["main"] = @"
                import('m').then(function(ns) { report(ns.answer); });",
        };
        RunGraph(modules, "main").AsNumber.Should().Be(42);
    }

    [TestMethod]
    public void Dynamic_import_resolves_default_export()
    {
        var modules = new Dictionary<string, string>
        {
            ["m"] = "export default 7; export const tag = 'x';",
            ["main"] = @"
                import('m').then(function(ns) { report(ns.default + ':' + ns.tag); });",
        };
        RunGraph(modules, "main").AsString.Should().Be("7:x");
    }

    [TestMethod]
    public void Dynamic_import_namespace_is_live()
    {
        // A dynamic import shares the same record/cells as static imports, so a
        // value mutated by the module body is visible through the namespace.
        var modules = new Dictionary<string, string>
        {
            ["m"] = "export let n = 1; export function bump(){ n++; }",
            ["main"] = @"
                import('m').then(function(ns) { ns.bump(); ns.bump(); report(ns.n); });",
        };
        RunGraph(modules, "main").AsNumber.Should().Be(3);
    }

    [TestMethod]
    public void Dynamic_import_caches_record_returns_same_namespace()
    {
        // Two imports of the same specifier yield the identical namespace object.
        var modules = new Dictionary<string, string>
        {
            ["m"] = "export const v = 5;",
            ["main"] = @"
                Promise.all([import('m'), import('m')]).then(function(both) {
                    report(both[0] === both[1]);
                });",
        };
        RunGraph(modules, "main").AsBool.Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // import() of a top-level-await module — settles only after it finishes
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Dynamic_import_of_tla_module_resolves_after_it_settles()
    {
        // The imported module awaits before publishing its export and logs as it
        // runs; the import's .then must observe the settled value AFTER the body.
        var modules = new Dictionary<string, string>
        {
            ["m"] = @"
                report('m:start');
                export const v = await Promise.resolve('ready');
                report('m:done');",
            ["main"] = @"
                report('main:before');
                import('m').then(function(ns) { report('main:got:' + ns.v); });
                report('main:after');",
        };

        var log = RunGraphLog(modules, "main");

        // import('m') synchronously begins loading + linking the graph and runs
        // the imported module body up to its first top-level await — so 'm:start'
        // logs before the entry's next statement ('main:after'). The body then
        // suspends; 'm:done' runs after the awaited promise settles. Crucially the
        // import's .then ('main:got:ready') fires only AFTER the module fully
        // settles ('m:done') — i.e. a dynamic import of a TLA module resolves
        // only once it has settled. The value it observes is the awaited export.
        log.Should().Equal(
            "main:before",
            "m:start",
            "main:after",
            "m:done",
            "main:got:ready");

        // The settled resolution must come strictly after the module finished.
        log.IndexOf("main:got:ready").Should().BeGreaterThan(log.IndexOf("m:done"));
    }

    // -----------------------------------------------------------------------
    // import.meta
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Import_meta_url_returns_module_url()
    {
        var modules = new Dictionary<string, string>
        {
            ["https://example.test/mod.js"] = "report(import.meta.url);",
        };
        RunGraph(modules, "https://example.test/mod.js")
            .AsString.Should().Be("https://example.test/mod.js");
    }

    [TestMethod]
    public void Import_meta_is_object_and_stable_within_a_module()
    {
        var modules = new Dictionary<string, string>
        {
            ["m"] = "report(typeof import.meta === 'object' && import.meta === import.meta);",
        };
        RunGraph(modules, "m").AsBool.Should().BeTrue();
    }

    [TestMethod]
    public void Import_meta_url_visible_through_dynamic_import()
    {
        var modules = new Dictionary<string, string>
        {
            ["dep"] = "export const myUrl = import.meta.url;",
            ["main"] = @"
                import('dep').then(function(ns) { report(ns.myUrl); });",
        };
        RunGraph(modules, "main").AsString.Should().Be("dep");
    }

    // -----------------------------------------------------------------------
    // failure paths — a bad specifier rejects the promise (no synchronous throw)
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Dynamic_import_of_missing_module_rejects()
    {
        var modules = new Dictionary<string, string>
        {
            ["main"] = @"
                import('does-not-exist').then(
                    function() { report('resolved'); },
                    function(err) { report('rejected'); });",
        };
        RunGraph(modules, "main").AsString.Should().Be("rejected");
    }

    [TestMethod]
    public void Dynamic_import_of_module_that_throws_rejects()
    {
        // Evaluation error in the imported module surfaces as a rejection.
        var modules = new Dictionary<string, string>
        {
            ["boom"] = "throw new Error('kaboom');",
            ["main"] = @"
                import('boom').catch(function(e) { report('caught:' + e.message); });",
        };
        RunGraph(modules, "main").AsString.Should().Be("caught:kaboom");
    }

    [TestMethod]
    public void Dynamic_import_failed_tla_module_rejects()
    {
        // A module that rejects its top-level await rejects the import promise.
        var modules = new Dictionary<string, string>
        {
            ["m"] = "await Promise.reject(new Error('async-fail'));",
            ["main"] = @"
                import('m').then(
                    function() { report('resolved'); },
                    function(e) { report('rejected:' + e.message); });",
        };
        RunGraph(modules, "main").AsString.Should().Be("rejected:async-fail");
    }

    // -----------------------------------------------------------------------
    // import() from a classic (non-module) script
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Dynamic_import_works_from_classic_script()
    {
        var modules = new Dictionary<string, string>
        {
            ["lib"] = "export const greeting = 'hello-from-module';",
        };
        var source = @"
            import('lib').then(function(ns) { report(ns.greeting); });";
        RunClassicScript(source, modules).AsString.Should().Be("hello-from-module");
    }

    [TestMethod]
    public void Dynamic_import_result_can_be_destructured_after_await()
    {
        var modules = new Dictionary<string, string>
        {
            ["lib"] = "export const dotnet = { create() { report('created'); } };",
        };
        var source = @"
            async function load() {
                const { dotnet: n } = await import('lib');
                n.create();
            }
            load();";
        RunClassicScript(source, modules).AsString.Should().Be("created");
    }

    [TestMethod]
    public void Dynamic_imported_module_promise_executor_writes_outer_lexical()
    {
        var modules = new Dictionary<string, string>
        {
            ["lib"] = """
                function makeController() {
                    let state = null;
                    const promise = new Promise(function (resolve, reject) {
                        state = {
                            isDone: false,
                            promise: null,
                            resolve: function (value) {
                                if (!state.isDone) {
                                    state.isDone = true;
                                    resolve(value);
                                }
                            },
                            reject: function (error) {
                                if (!state.isDone) {
                                    state.isDone = true;
                                    reject(error);
                                }
                            }
                        };
                    });
                    state.promise = promise;
                    state.resolve("ready");
                    promise.then(function (value) { report(value); });
                }
                makeController();
                export const marker = true;
                """,
        };
        var source = "import('lib').catch(function (error) { report('error:' + error.message); });";
        RunClassicScript(source, modules).AsString.Should().Be("ready");
    }

    [TestMethod]
    public void Dynamic_imported_module_minified_promise_controller_writes_outer_lexical()
    {
        var modules = new Dictionary<string, string>
        {
            ["lib"] = """
                const key = Symbol.for("promise_control");
                function makeController(onResolve, onReject) {
                    let state = null;
                    const promise = new Promise((function (resolve, reject) {
                        state = {
                            isDone: false,
                            promise: null,
                            resolve: value => {
                                state.isDone || (state.isDone = true, resolve(value), onResolve && onResolve());
                            },
                            reject: error => {
                                state.isDone || (state.isDone = true, reject(error), onReject && onReject());
                            }
                        };
                    }));
                    state.promise = promise;
                    const tagged = promise;
                    tagged[key] = state;
                    state.resolve("ready");
                    promise.then(function (value) { report(value); });
                }
                makeController();
                export const marker = true;
                """,
        };
        var source = "import('lib').catch(function (error) { report('error:' + error.message); });";
        RunClassicScript(source, modules).AsString.Should().Be("ready");
    }

    [TestMethod]
    public void Dynamic_imported_module_dotnet_promise_controller_shape_writes_outer_lexical()
    {
        var modules = new Dictionary<string, string>
        {
            ["lib"] = """
                const r = Symbol.for("wasm promise_control");
                function i(e,t) {
                    let o = null;
                    const n = new Promise((function(n,r) {
                        o = {
                            isDone: false,
                            promise: null,
                            resolve: t => {
                                o.isDone || (o.isDone = true, n(t), e && e());
                            },
                            reject: e => {
                                o.isDone || (o.isDone = true, r(e), t && t());
                            }
                        };
                    }));
                    o.promise = n;
                    const i = n;
                    return i[r] = o, { promise: i, promise_control: o };
                }
                const ctl = i();
                ctl.promise.then(function(value) { report(value); });
                ctl.promise_control.resolve("ready");
                export const marker = true;
                """,
        };
        var source = "import('lib').catch(function (error) { report('error:' + error.message); });";
        RunClassicScript(source, modules).AsString.Should().Be("ready");
    }

    [TestMethod]
    public void Dynamic_imported_module_dotnet_minified_promise_controller_writes_outer_lexical()
    {
        var modules = new Dictionary<string, string>
        {
            ["lib"] = """
                const r=Symbol.for("wasm promise_control");function i(e,t){let o=null;const n=new Promise((function(n,r){o={isDone:!1,promise:null,resolve:t=>{o.isDone||(o.isDone=!0,n(t),e&&e())},reject:e=>{o.isDone||(o.isDone=!0,r(e),t&&t())}}}));o.promise=n;const i=n;return i[r]=o,{promise:i,promise_control:o}}const ctl=i();ctl.promise.then((value=>report(value)));ctl.promise_control.resolve("ready");export const marker=true;
                """,
        };
        var source = "import('lib').catch(function (error) { report('error:' + error.message); });";
        RunClassicScript(source, modules).AsString.Should().Be("ready");
    }

    [TestMethod]
    public void Dynamic_imported_module_dotnet_shadowed_minified_promise_controller_writes_outer_lexical()
    {
        var modules = new Dictionary<string, string>
        {
            ["lib"] = """
                const t=async()=>true,o=async()=>true,n=async()=>true,r=Symbol.for("wasm promise_control");function i(e,t){let o=null;const n=new Promise((function(n,r){o={isDone:!1,promise:null,resolve:t=>{o.isDone||(o.isDone=!0,n(t),e&&e())},reject:e=>{o.isDone||(o.isDone=!0,r(e),t&&t())}}}));o.promise=n;const i=n;return i[r]=o,{promise:i,promise_control:o}}const ctl=i();ctl.promise.then((value=>report(value)));ctl.promise_control.resolve("ready");export const marker=true;
                """,
        };
        var source = "import('lib').catch(function (error) { report('error:' + error.message); });";
        RunClassicScript(source, modules).AsString.Should().Be("ready");
    }

    [TestMethod]
    public void Dynamic_imported_dotnet_runtime_globals_survive_async_emscripten_callback()
    {
        var modules = new Dictionary<string, string>
        {
            ["runtime"] = """
                let api = null;
                let registry = null;

                export function setRuntimeGlobals(globals) {
                    api = globals.api;
                }

                export function initializeExports() {
                    Object.assign(api, { marker: 1 });
                    registry = {
                        registerRuntime(runtimeApi) {
                            runtimeApi.runtimeId = 0;
                            report(runtimeApi.marker + ':' + runtimeApi.runtimeId);
                        }
                    };
                }

                export async function configureRuntimeStartup() {
                }

                export function configureEmscriptenStartup(module) {
                    module.onRuntimeInitialized = async () => {
                        registry.registerRuntime(api);
                    };
                }
                """,
        };
        var source = """
            async function boot() {
                const runtime = await import('runtime');
                const {
                    initializeExports,
                    configureRuntimeStartup,
                    configureEmscriptenStartup,
                    setRuntimeGlobals
                } = runtime;

                const globals = { api: {} };
                const module = {};
                setRuntimeGlobals(globals);
                initializeExports(globals);
                await configureRuntimeStartup(module);
                configureEmscriptenStartup(module);
                await module.onRuntimeInitialized();
            }

            boot().catch(function (error) {
                report('error:' + error.message);
            });
            """;
        RunClassicScript(source, modules).AsString.Should().Be("1:0");
    }

    [TestMethod]
    public void Dynamic_imported_class_method_reads_updated_module_binding()
    {
        var modules = new Dictionary<string, string>
        {
            ["runtime"] = """
                let helpers = null;
                class RuntimeList {
                    register() {
                        report(typeof helpers + ':' + typeof helpers.config);
                    }
                }
                const list = new RuntimeList();
                export function setHelpers(value) {
                    helpers = value;
                }
                export function register() {
                    list.register();
                }
                """,
        };
        var source = """
            import('runtime').then(function (runtime) {
                runtime.setHelpers({ config: {} });
                runtime.register();
            });
            """;
        RunClassicScript(source, modules).AsString.Should().Be("object:object");
    }

    [TestMethod]
    public void Dynamic_imported_dotnet_minified_runtime_shape_preserves_api_binding()
    {
        var modules = new Dictionary<string, string>
        {
            ["runtime"] = """
                let Ke,et,ct,lt,pt,it=null,ut=!1;
                function ft(e) {
                    if (ut) throw new Error("Runtime module already loaded");
                    ut=!0,Ke=e.module,et=e.internal,ct=e.runtimeHelpers,lt=e.loaderHelpers,pt=e.diagnosticHelpers,it=e.api;
                    const t={gitHash:"hash"};
                    Object.assign(ct,t),Object.assign(e.module.config,{}),Object.assign(e.api,{Module:e.module,...e.module}),Object.assign(e.api,{INTERNAL:e.internal});
                }

                let tl;
                function nl(r) {
                    const o=Ke,a=r,i=globalThis;
                    Object.assign(a.internal,{get_dotnet_instance:()=>it});
                    const l={entry:1};
                    return Object.assign(it,{INTERNAL:a.internal,Module:o,runtimeBuildInfo:{wasmEnableExceptionHandling:!0},...l}),i.getDotnetRuntime?tl=i.getDotnetRuntime.__list:(i.getDotnetRuntime=e=>i.getDotnetRuntime.__list.getRuntime(e),i.getDotnetRuntime.__list=tl=new rl),it;
                }

                function configureEmscriptenStartup(e) {
                    e.onRuntimeInitialized = async () => {
                        tl.registerRuntime(it);
                    };
                }

                class rl {
                    constructor(){this.list={}}
                    registerRuntime(e) {
                        return void 0===e.runtimeId&&(e.runtimeId=Object.keys(this.list).length),this.list[e.runtimeId]={deref:()=>e},lt.config.runtimeId=e.runtimeId,report(typeof e + ':' + e.entry + ':' + e.runtimeId),e.runtimeId;
                    }
                    getRuntime(e){const t=this.list[e];return t?t.deref():void 0}
                }

                export { configureEmscriptenStartup, nl as initializeExports, ft as setRuntimeGlobals };
                """,
        };
        var source = """
            async function boot(modules) {
                const {
                    initializeExports,
                    configureEmscriptenStartup,
                    setRuntimeGlobals
                } = modules[0];
                const Ue={},Pe={config:{}},Me={},Le={},Ne={},ze={},We={config:ze};
                const Fe={mono:{},binding:{},internal:Ne,module:We,loaderHelpers:Pe,runtimeHelpers:Ue,diagnosticHelpers:Me,api:Le};
                setRuntimeGlobals(Fe);
                initializeExports(Fe);
                await Promise.resolve();
                configureEmscriptenStartup(We);
                await We.onRuntimeInitialized();
            }

            Promise.all([import('runtime')]).then(boot).catch(function (error) {
                report('error:' + error.message);
            });
            """;
        RunClassicScript(source, modules).AsString.Should().Be("object:1:0");
    }

    [TestMethod]
    public void Dynamic_import_from_classic_script_rejects_on_missing()
    {
        var source = @"
            import('nope').catch(function() { report('rejected'); });";
        RunClassicScript(source, new Dictionary<string, string>())
            .AsString.Should().Be("rejected");
    }

    // -----------------------------------------------------------------------
    // regression — static import/export declarations still parse + run
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Static_import_export_still_works_alongside_dynamic_import()
    {
        var modules = new Dictionary<string, string>
        {
            ["lib"] = "export const a = 10;",
            ["lazy"] = "export const b = 20;",
            ["main"] = @"
                import { a } from 'lib';
                import('lazy').then(function(ns) { report(a + ns.b); });",
        };
        RunGraph(modules, "main").AsNumber.Should().Be(30);
    }
}
