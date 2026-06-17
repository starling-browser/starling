using BenchmarkDotNet.Attributes;
using Starling.Js.Bytecode;
using Starling.Js.Lex;
using Starling.Js.Parse;
using Starling.Js.Runtime;

namespace Starling.Bench;

// Backstops the M3 exit ("Hand-picked microbenchmarks within 10x of V8 on
// simple loops") and the §C.5 "0 allocations per bytecode dispatch" budget.
// The four stages (lex / parse / compile / run) are split so a regression
// shows up in the affected one. Scripts stay inside the implemented surface:
// no Math, no built-in array methods — only language features known to be
// shipped (functions, closures, arithmetic, control flow, plain objects).
[MemoryDiagnoser]
public class JsBench
{
    private const string SumLoopSrc = @"
        var s = 0;
        for (var i = 0; i < 1000; i++) { s = s + i; }
        s;
    ";

    private const string FibRecursiveSrc = @"
        function fib(n) {
            if (n < 2) return n;
            return fib(n - 1) + fib(n - 2);
        }
        fib(15);
    ";

    private const string ObjectAllocSrc = @"
        function make(i) { return { a: i, b: i + 1, c: i * 2 }; }
        var total = 0;
        for (var i = 0; i < 100; i++) {
            var o = make(i);
            total = total + o.a + o.b + o.c;
        }
        total;
    ";

    private Chunk _sumLoopChunk = null!;
    private Chunk _fibChunk = null!;
    private Chunk _objAllocChunk = null!;

    [GlobalSetup]
    public void Setup()
    {
        _sumLoopChunk = JsCompiler.CompileForEval(new JsParser(SumLoopSrc).ParseProgram());
        _fibChunk = JsCompiler.CompileForEval(new JsParser(FibRecursiveSrc).ParseProgram());
        _objAllocChunk = JsCompiler.CompileForEval(new JsParser(ObjectAllocSrc).ParseProgram());
    }

    [Benchmark]
    public int Lex_FibRecursive()
    {
        var lex = new JsLexer(FibRecursiveSrc);
        var count = 0;
        while (lex.Next().Kind != JsTokenKind.EndOfFile)
        {
            count++;
        }

        return count;
    }

    [Benchmark]
    public int Parse_FibRecursive()
        => new JsParser(FibRecursiveSrc).ParseProgram().Body.Count;

    [Benchmark]
    public int Compile_FibRecursive()
        => JsCompiler.CompileForEval(new JsParser(FibRecursiveSrc).ParseProgram()).Code.Length;

    [Benchmark]
    public double Run_SumLoop_1000()
        => new JsVm(new JsRuntime()).Run(_sumLoopChunk).AsNumber;

    [Benchmark(Baseline = true)]
    public double Run_FibRecursive_15()
        => new JsVm(new JsRuntime()).Run(_fibChunk).AsNumber;

    [Benchmark]
    public double Run_ObjectAlloc_100()
        => new JsVm(new JsRuntime()).Run(_objAllocChunk).AsNumber;
}
