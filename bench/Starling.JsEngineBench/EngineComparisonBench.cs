using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using Jint;
using Starling.Js.Bytecode;
using Starling.Js.Parse;
using Starling.Js.Runtime;

namespace Starling.JsEngineBench;

// Where does the Starling JS engine land against Jint? This replicates Jint's
// official "EngineComparison" suite locally: the same 19 vendored scripts (see
// Scripts/), every engine in global strict mode, ranked on THIS machine so the
// Starling-vs-Jint numbers are true apples-to-apples (Jint's published table is
// different hardware — that lives in bench/engine-comparison.md as reference).
//
// Two variants per engine mirror Jint's own split:
//   cold      — parse + compile + run every iteration
//   prepared  — reuse the compiled artifact, run on a fresh runtime each time
// So Starling <-> Jint and Starling_Prepared <-> Jint_ParsedScript are the
// pairs to read across.
//
// Several scripts lean on the Dromaeo harness globals (startTest/test/prep/
// endTest/log/assert); we inject the same stubs Jint's benchmark does, right
// after the "use strict" directive. A (script, engine) pair the engine cannot
// run is detected once in GlobalSetup and skipped — the measured methods stay
// free of try/catch so timing is never distorted. Skipped pairs are logged and
// surface as near-zero rows; bench/engine-comparison.md lists them explicitly.
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[RankColumn]
public class EngineComparisonBench
{
    // Strict-mode parity for every engine, plus the harness stubs the dromaeo /
    // linq scripts expect. The directive must stay the first statement.
    private const string Prelude =
        "\"use strict\";\n" +
        "var startTest = function () {};\n" +
        "var test = function (name, fn) { fn(); };\n" +
        "var endTest = function () {};\n" +
        "var prep = function (fn) { fn(); };\n" +
        "var log = function () {};\n" +
        "var assert = function () {};\n";

    [ParamsSource(nameof(ScriptFiles))]
    public string FileName = "";

    public static IEnumerable<string> ScriptFiles =>
    [
        "array-stress.js",
        "dromaeo-3d-cube.js",
        "dromaeo-3d-cube-modern.js",
        "dromaeo-core-eval.js",
        "dromaeo-core-eval-modern.js",
        "dromaeo-object-array.js",
        "dromaeo-object-array-modern.js",
        "dromaeo-object-regexp.js",
        "dromaeo-object-regexp-modern.js",
        "dromaeo-object-string.js",
        "dromaeo-object-string-modern.js",
        "dromaeo-string-base64.js",
        "dromaeo-string-base64-modern.js",
        "evaluation.js",
        "evaluation-modern.js",
        "linq-js.js",
        "minimal.js",
        "stopwatch.js",
        "stopwatch-modern.js",
    ];

    private string _src = "";

    // Prepared artifacts, built once per script in GlobalSetup.
    private Chunk? _starlingChunk;
    private Prepared<Acornima.Ast.Script> _jintPrepared;

    // Per-engine "this script is unsupported here" flags, set once per script.
    private bool _skipStarling;
    private bool _skipStarlingPrepared;
    private bool _skipJint;
    private bool _skipJintPrepared;

    [GlobalSetup]
    public void Setup()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Scripts", FileName);
        _src = Prelude + File.ReadAllText(path);

        // Build the reusable artifacts. If either fails, its prepared variant is
        // skipped (the cold variant may still run, and vice-versa).
        try { _starlingChunk = JsCompiler.CompileForEval(new JsParser(_src).ParseProgram()); }
        catch (Exception ex) { _starlingChunk = null; Skip("Starling/compile", ex); }

        try { _jintPrepared = Engine.PrepareScript(_src, strict: true); }
        catch (Exception ex) { _jintPrepared = default; Skip("Jint/prepare", ex); }

        // Validate one run of each engine; mark unsupported pairs to skip.
        _skipStarling = !Validate("Starling", RunStarling);
        _skipStarlingPrepared = _starlingChunk is null || !Validate("Starling_Prepared", RunStarlingPrepared);
        _skipJint = !Validate("Jint", RunJint);
        _skipJintPrepared = !_jintPrepared.IsValid || !Validate("Jint_ParsedScript", RunJintPrepared);
    }

    private bool Validate<T>(string label, Func<T> run)
    {
        try { run(); return true; }
        catch (Exception ex) { Skip(label, ex); return false; }
    }

    private void Skip(string label, Exception ex)
    {
        var msg = ex.Message.ReplaceLineEndings(" ");
        if (msg.Length > 120)
        {
            msg = msg[..120];
        }

        Console.WriteLine($"[skip] {label} / {FileName}: {ex.GetType().Name}: {msg}");
    }

    // ---- measured methods: one skip branch, then the raw call. No try/catch. ----

    [Benchmark]
    public JsValue Starling() => _skipStarling ? default : RunStarling();

    [Benchmark]
    public JsValue Starling_Prepared() => _skipStarlingPrepared ? default : RunStarlingPrepared();

    [Benchmark(Baseline = true)]
    public Engine? Jint() => _skipJint ? null : RunJint();

    [Benchmark]
    public Engine? Jint_ParsedScript() => _skipJintPrepared ? null : RunJintPrepared();

    // ---- raw runners ----

    private JsValue RunStarling() =>
        new JsVm(new JsRuntime()).Run(JsCompiler.CompileForEval(new JsParser(_src).ParseProgram()));

    private JsValue RunStarlingPrepared() =>
        new JsVm(new JsRuntime()).Run(_starlingChunk!);

    private Engine RunJint() =>
        new Engine(o => o.Strict = true).Execute(_src);

    private Engine RunJintPrepared() =>
        new Engine(o => o.Strict = true).Execute(_jintPrepared);
}
