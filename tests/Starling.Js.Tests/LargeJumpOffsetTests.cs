using System.Text;
using AwesomeAssertions;
using Starling.Js.Bytecode;
using Starling.Js.Parse;
using Starling.Js.Runtime;

namespace Starling.Js.Tests;

/// <summary>
/// Jump/branch operands are i32, not i16. A single function in a large minified
/// bundle (e.g. mcmaster.com's ScriptCombiner output) can compile to far more
/// than 32 KB of bytecode, so a forward/backward jump's distance overflows the
/// old ±32767 i16 range. These exercise jumps whose distance exceeds that.
/// </summary>
[TestClass]
public class LargeJumpOffsetTests
{
    private static JsValue Eval(string src)
    {
        var program = new JsParser(src).ParseProgram();
        var chunk = JsCompiler.CompileForEval(program);
        return new JsVm(new JsRuntime()).Run(chunk);
    }

    // ~8000 `x+=1;` statements compile to well over 32 KB of bytecode, forcing
    // any jump spanning the block past the old i16 limit.
    private static string Filler(int n)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < n; i++)
        {
            sb.Append("x+=1;");
        }

        return sb.ToString();
    }

    [TestMethod]
    public void Forward_jump_skips_over_32kb_block()
    {
        // JumpIfFalse must skip the entire (huge) consequent.
        Eval("var x=0; if(false){" + Filler(8000) + "} x;")
            .AsNumber.Should().Be(0);
    }

    [TestMethod]
    public void Backward_jump_loops_over_32kb_body()
    {
        // The loop's backward Jump spans the whole body.
        Eval("var x=0; for(var i=0;i<2;i++){" + Filler(8000) + "} x;")
            .AsNumber.Should().Be(16000);
    }

    [TestMethod]
    public void Try_finally_offsets_span_32kb()
    {
        // EnterTry's catch/finally i32 offsets must reach past a huge try body;
        // the finally still runs and its result is observable.
        Eval("var x=0; try{" + Filler(8000) + "} finally { x+=5; } x;")
            .AsNumber.Should().Be(8005);
    }
}
