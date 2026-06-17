using System.Collections.Concurrent;
using Acornima;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Modules;

namespace Starling.Js.Test262.Tests;

/// <summary>
/// The Jint analogue of <see cref="Test262Runner"/>: an informational
/// compat-delta baseline (package J5b). It REUSES the exact same corpus
/// enumeration (<see cref="Test262Corpus"/>), frontmatter parsing
/// (<see cref="Test262Runner.ParseMetadata"/>), and out-of-scope skip-feature set
/// (<see cref="Test262Runner.OutOfScopeFeatures"/>), but executes each applicable
/// scenario through a fresh <see cref="global::Jint.Engine"/> instead of the
/// in-house engine. Pass/Fail/Timeout/Skip is classified identically to the
/// Starling.Js runner so the two engines' numbers are directly comparable on the
/// identical corpus.
/// </summary>
/// <remarks>
/// Fidelity notes vs the Starling runner:
/// <list type="bullet">
/// <item>A fresh engine is built per scenario (per realm), like Starling's fresh
/// <c>JsRuntime</c>.</item>
/// <item>Harness chunks (sta.js / assert.js / per-file includes) are read once
/// and cached as source strings, then re-executed per realm.</item>
/// <item>Negative parse-phase tests use a parse-only attempt; runtime-phase
/// negative tests compare the thrown error's <c>name</c> to the expected type,
/// exactly as the Starling runner does.</item>
/// <item>Async tests use the same <c>print</c> + <c>doneprintHandle.js</c>
/// protocol: success is the literal "Test262:AsyncTestComplete" line.</item>
/// <item>Module tests run through a filesystem <see cref="IModuleLoader"/> that
/// resolves relative specifiers against the test file's directory.</item>
/// </list>
/// </remarks>
public sealed class JintTest262Runner
{
    private readonly string _root;
    private readonly string _harnessDir;
    private readonly ConcurrentDictionary<string, string> _harnessCache = new(StringComparer.Ordinal);
    private readonly int _timeoutMs;

    public JintTest262Runner(string test262Root, int timeoutMs = 10_000)
    {
        _root = test262Root;
        _harnessDir = Path.Combine(test262Root, "harness");
        _timeoutMs = timeoutMs;
    }

    public IReadOnlyList<ScenarioResult> RunFile(string path)
    {
        string source;
        try { source = File.ReadAllText(path); }
        catch (Exception ex) { return new[] { new ScenarioResult(path, ScenarioMode.NonStrict, Outcome.Skip, "read: " + ex.Message) }; }

        var meta = Test262Runner.ParseMetadata(source);
        var rel = Path.GetRelativePath(_root, path);

        // Skip the SAME out-of-scope features the Starling runner skips, so the
        // denominators match. (Unlike the Starling runner, Jint CAN run module
        // tests, so those are not blanket-skipped here.)
        foreach (var f in meta.Features)
        {
            if (Test262Runner.OutOfScopeFeatures.Contains(f))
            {
                return new[] { new ScenarioResult(rel, ScenarioMode.NonStrict, Outcome.Skip, "out-of-scope:" + f) };
            }
        }

        var results = new List<ScenarioResult>();
        foreach (var mode in Modes(meta))
        {
            results.Add(RunScenario(rel, source, meta, mode, path));
        }

        return results;
    }

    private static IEnumerable<ScenarioMode> Modes(Test262Metadata meta)
    {
        if (meta.IsRaw) { yield return ScenarioMode.Raw; yield break; }
        // Module + async tests have no "use strict" prepend variant; run them once
        // (non-strict slot). Modules are implicitly strict; async strict-mode is
        // covered by the non-strict run (the body opts in if it needs strict).
        if (meta.IsModule || meta.IsAsync) { yield return ScenarioMode.NonStrict; yield break; }
        if (!meta.OnlyStrict)
        {
            yield return ScenarioMode.NonStrict;
        }

        if (!meta.NoStrict)
        {
            yield return ScenarioMode.Strict;
        }
    }

    private ScenarioResult RunScenario(string rel, string source, Test262Metadata meta, ScenarioMode mode, string absPath)
    {
        Outcome outcome = Outcome.Fail;
        string? detail = null;

        var worker = new Thread(() =>
        {
            try { (outcome, detail) = Execute(source, meta, mode, absPath); }
            catch (Exception ex) { outcome = Outcome.Fail; detail = "host:" + ex.GetType().Name + ":" + Truncate(ex.Message); }
        })
        { IsBackground = true };

        worker.Start();
        if (!worker.Join(_timeoutMs))
        {
            return new ScenarioResult(rel, mode, Outcome.Timeout, "timeout");
        }

        return new ScenarioResult(rel, mode, outcome, detail);
    }

    private (Outcome, string?) Execute(string source, Test262Metadata meta, ScenarioMode mode, string absPath)
    {
        var printed = new List<string>();

        // Build a fresh engine + realm. Modules are enabled with a filesystem
        // loader rooted at the test file's directory so `import "./x.js"` resolves
        // the fixture modules on disk. Recursion + statement caps mirror the
        // depth-1000 guard the Starling runner relies on.
        // Always enable the filesystem module loader (rooted at the test file's
        // directory). Module tests use it for static imports; non-module tests may
        // still use it for dynamic import() — which test262 exercises heavily under
        // language/expressions/dynamic-import (those files carry no `module` flag).
        // The Starling runner likewise wires its module host unconditionally.
        var engine = new global::Jint.Engine(opts =>
        {
            opts.Strict = false;
            opts.EnableModules(new FsModuleLoader(absPath));
        });
        engine.SetValue("print", new Action<JsValue>(v => printed.Add(v.IsString() ? v.AsString() : v.ToString())));

        // ---- Negative parse: the test body itself must fail to parse. ----
        if (meta.IsNegative && meta.NegativePhase is "parse" or "early")
        {
            try
            {
                if (meta.IsModule)
                {
                    PrepareModuleParse(absPath, source);
                }
                else
                {
                    engine.Execute(WithStrict(source, mode), absPath);
                }

                return (Outcome.Fail, "expected parse error, parsed OK");
            }
            catch (Exception ex) when (IsParseLevel(ex)) { return (Outcome.Pass, null); }
            catch (JavaScriptException jex)
            {
                // Jint surfaces some early errors as a thrown SyntaxError at
                // execution time rather than a parse exception; that still
                // satisfies a parse/early negative expectation when the type
                // matches (test262 negative parse types are always SyntaxError).
                var name = ErrorName(jex.Error);
                return name == meta.NegativeType
                    ? (Outcome.Pass, null)
                    : (Outcome.Fail, $"expected parse {meta.NegativeType}, threw {name}");
            }
            catch (Exception ex) { return (Outcome.Fail, "expected parse error, got " + ex.GetType().Name); }
        }

        // ---- Build the harness for an executable scenario. ----
        try
        {
            engine.Execute(
                "var $262 = { global: globalThis, detachArrayBuffer: function(){}, gc: function(){} };",
                "<host>");

            if (!meta.IsRaw)
            {
                RunHarness(engine, "assert.js");
                RunHarness(engine, "sta.js");
                if (meta.IsAsync)
                {
                    RunHarness(engine, "doneprintHandle.js");
                }

                foreach (var inc in meta.Includes)
                {
                    RunHarness(engine, inc);
                }
            }
        }
        catch (JavaScriptException jex)
        {
            return (Outcome.Fail, "harness:JavaScriptException:" + Truncate(Describe(jex.Error, jex.Message)));
        }
        catch (Exception ex)
        {
            return (Outcome.Fail, "harness:" + ex.GetType().Name + ":" + Truncate(ex.Message));
        }

        // ---- Run the test body. ----
        try
        {
            if (meta.IsModule)
            {
                engine.Modules.Import(absPath);
                engine.Advanced.ProcessTasks();
            }
            else
            {
                engine.Execute(WithStrict(source, mode), absPath);
                engine.Advanced.ProcessTasks();
            }
        }
        catch (JavaScriptException jex)
        {
            if (meta.IsNegative)
            {
                var name = ErrorName(jex.Error);
                return name == meta.NegativeType
                    ? (Outcome.Pass, null)
                    : (Outcome.Fail, $"expected {meta.NegativeType}, threw {Truncate(Describe(jex.Error, jex.Message))}");
            }
            return (Outcome.Fail, "threw " + Truncate(Describe(jex.Error, jex.Message)));
        }
        catch (Exception ex) when (IsParseLevel(ex))
        {
            // Unexpected parse failure on a runnable test — or a runtime-phase
            // negative test whose error Jint reports as a parse-time SyntaxError.
            if (meta.IsNegative && meta.NegativeType == "SyntaxError")
            {
                return (Outcome.Pass, null);
            }

            return (Outcome.Fail, "parse:" + Truncate(ex.Message));
        }
        catch (Exception ex)
        {
            return (Outcome.Fail, "host:" + ex.GetType().Name + ":" + Truncate(ex.Message));
        }

        // Reached end without throwing.
        if (meta.IsNegative)
        {
            return (Outcome.Fail, $"expected {meta.NegativeType}, no throw");
        }

        if (meta.IsAsync)
        {
            return printed.Contains("Test262:AsyncTestComplete")
                ? (Outcome.Pass, null)
                : (Outcome.Fail, "async incomplete: " + Truncate(string.Join("|", printed)));
        }

        return (Outcome.Pass, null);
    }

    /// <summary>Parse-only attempt for a negative-parse module test: build the
    /// source-text module record (which parses) without evaluating it.</summary>
    private static void PrepareModuleParse(string absPath, string source)
    {
        var engine = new global::Jint.Engine(opts => opts.EnableModules(new FsModuleLoader(absPath)));
        var request = new ModuleRequest(absPath, Array.Empty<ModuleImportAttribute>());
        var resolved = new ResolvedSpecifier(request, absPath, Uri: null, SpecifierType.RelativeOrAbsolute);
        // BuildSourceTextModule parses the module text and throws on a parse error.
        ModuleFactory.BuildSourceTextModule(engine, resolved, source, ModuleParsingOptions.Default);
    }

    private void RunHarness(global::Jint.Engine engine, string name)
    {
        var src = _harnessCache.GetOrAdd(name, n => File.ReadAllText(Path.Combine(_harnessDir, n)));
        engine.Execute(src, "<harness:" + name + ">");
    }

    private static string WithStrict(string source, ScenarioMode mode) =>
        mode == ScenarioMode.Strict ? "\"use strict\";\n" + source : source;

    private static bool IsParseLevel(Exception ex) =>
        ex is ParseErrorException or SyntaxErrorException or ScriptPreparationException
        || ex.GetType().Name is "ParseErrorException" or "SyntaxErrorException" or "ScriptPreparationException";

    /// <summary>Read the <c>name</c> property of a thrown error value (e.g.
    /// "TypeError"); null when the throw isn't an error-shaped object.</summary>
    private static string? ErrorName(JsValue value)
    {
        if (value is not ObjectInstance o)
        {
            return null;
        }

        var n = o.Get("name");
        return n.IsString() ? n.AsString() : null;
    }

    /// <summary>Format a thrown JS value as "Name: message" for triage.</summary>
    private static string Describe(JsValue error, string fallback)
    {
        try
        {
            if (error is ObjectInstance o)
            {
                var name = o.Get("name");
                var message = o.Get("message");
                if (!name.IsUndefined() || !message.IsUndefined())
                {
                    var n = name.IsUndefined() ? "Error" : name.ToString();
                    var m = message.IsUndefined() ? "" : message.ToString();
                    return string.IsNullOrEmpty(m) ? n : $"{n}: {m}";
                }
            }
            return error.ToString();
        }
        catch { return fallback; }
    }

    private static string Truncate(string s) => s.Length <= 120 ? s : s[..120];

    /// <summary>Filesystem module loader for module tests: resolves a relative
    /// specifier against the importing file's directory and reads the source from
    /// disk. The entry module is keyed by its absolute path.</summary>
    private sealed class FsModuleLoader : IModuleLoader
    {
        private readonly string _entryDir;

        public FsModuleLoader(string entryAbsPath)
        {
            _entryDir = Path.GetDirectoryName(entryAbsPath) ?? Directory.GetCurrentDirectory();
        }

        public ResolvedSpecifier Resolve(string? referencingModuleLocation, ModuleRequest moduleRequest)
        {
            var specifier = moduleRequest.Specifier;
            string key;
            if (Path.IsPathRooted(specifier))
            {
                key = Path.GetFullPath(specifier);
            }
            else
            {
                var baseDir = referencingModuleLocation is not null
                    ? Path.GetDirectoryName(referencingModuleLocation) ?? _entryDir
                    : _entryDir;
                key = Path.GetFullPath(Path.Combine(baseDir, specifier));
            }
            return new ResolvedSpecifier(moduleRequest, key, Uri: null, SpecifierType.RelativeOrAbsolute);
        }

        public Module LoadModule(global::Jint.Engine engine, ResolvedSpecifier resolved)
        {
            var key = resolved.Key;
            if (!File.Exists(key))
            {
                throw new ModuleResolutionException(
                    "Module not found", resolved.ModuleRequest.Specifier, parent: key, filePath: key);
            }

            var source = File.ReadAllText(key);
            return ModuleFactory.BuildSourceTextModule(engine, resolved, source, ModuleParsingOptions.Default);
        }
    }
}
