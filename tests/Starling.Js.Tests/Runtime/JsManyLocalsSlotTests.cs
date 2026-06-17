using System.Text;
using AwesomeAssertions;
using Starling.Js.Bytecode;
using Starling.Js.Parse;
using Starling.Js.Runtime;
namespace Starling.Js.Tests.Runtime;

/// <summary>
/// Local-slot operands are u16, not u8 (see <c>ChunkBuilder.EmitSlot</c>).
/// Large minified bundles (Google Tag Manager, TodoMVC, webpack output)
/// routinely declare more than 255 locals in a single function — gtag's
/// top-level IIFE has ~2100. With a u8 slot operand, slot indices ≥ 256
/// silently wrapped modulo 256 and aliased an unrelated low slot. When one
/// of the colliding slots was a captured <see cref="Cell"/> and the other a
/// plain local, a plain <c>StoreLocal</c> overwrote the cell with a raw
/// value, and a later <c>StoreCellLocal</c> on the same byte operand threw
/// <c>InvalidCastException: JsFunction → Cell</c>. These tests pin correct
/// behavior for functions whose slot count crosses the old byte boundary.
/// </summary>
[TestClass]
public class JsManyLocalsSlotTests
{
    /// <summary>The exact failure mode that broke gtag/GTM: a captured var
    /// (cell-backed, read by a nested closure) lives at a low slot while
    /// hundreds of later plain locals push the slot count past 256. Under
    /// the u8 bug a high slot aliased the captured low slot and clobbered
    /// its cell, throwing on the next captured write.</summary>
    [TestMethod]
    public void Captured_local_survives_when_function_has_over_256_slots()
    {
        var sb = new StringBuilder();
        sb.Append("function outer(){\n");
        sb.Append("  var captured = 7;\n");
        sb.Append("  function read(){ return captured; }\n");
        // Push the function past 256 locals with plain function-expr vars.
        for (var i = 0; i < 300; i++)
        {
            sb.Append($"  var v{i} = function(){{ return {i}; }};\n");
        }
        // A second captured var declared AFTER the slot count crosses 256.
        sb.Append("  var late = 11;\n");
        sb.Append("  function readLate(){ return late; }\n");
        for (var i = 0; i < 300; i++)
        {
            sb.Append($"  var w{i} = function(){{ return {i}; }};\n");
        }

        sb.Append("  captured = 42;\n");
        sb.Append("  late = 99;\n");
        sb.Append("  return read() + readLate();\n");
        sb.Append("}\n");
        sb.Append("outer();\n");

        Eval(sb.ToString()).AsNumber.Should().Be(141); // 42 + 99
    }

    /// <summary>A captured parameter (PromoteParamCell) at a low slot must
    /// also survive >256 locals — the param cell must not be aliased by a
    /// high plain slot.</summary>
    [TestMethod]
    public void Captured_param_survives_over_256_slots()
    {
        var sb = new StringBuilder();
        sb.Append("function outer(p){\n");
        sb.Append("  function read(){ return p; }\n");
        for (var i = 0; i < 300; i++)
        {
            sb.Append($"  var v{i} = {i};\n");
        }

        sb.Append("  p = 1234;\n");
        sb.Append("  return read();\n");
        sb.Append("}\n");
        sb.Append("outer(5);\n");

        Eval(sb.ToString()).AsNumber.Should().Be(1234);
    }

    /// <summary>Plain (non-captured) locals beyond slot 255 read/write their
    /// own value and don't alias each other.</summary>
    [TestMethod]
    public void Plain_locals_beyond_slot_255_are_distinct()
    {
        var sb = new StringBuilder();
        sb.Append("function outer(){\n");
        for (var i = 0; i < 400; i++)
        {
            sb.Append($"  var v{i} = {i};\n");
        }
        // Sum the last few high-slot locals — if they aliased, the sum is wrong.
        sb.Append("  return v300 + v350 + v399;\n");
        sb.Append("}\n");
        sb.Append("outer();\n");

        Eval(sb.ToString()).AsNumber.Should().Be(300 + 350 + 399);
    }

    private static JsValue Eval(string src)
    {
        var program = new JsParser(src).ParseProgram();
        var chunk = JsCompiler.CompileForEval(program);
        return new JsVm(new JsRuntime()).Run(chunk);
    }
}
