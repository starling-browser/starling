using System.Text;
using AwesomeAssertions;
using Starling.Js.Bytecode;
using Starling.Js.Parse;
using Starling.Js.Runtime;
namespace Starling.Js.Tests.Runtime;

/// <summary>
/// Upvalue-index operands are u16, not u8 (see <c>ChunkBuilder.EmitUpvalue</c>),
/// and <c>MakeClosure</c>'s captured-count operand is u16. A function that
/// captures more than 255 outer bindings was previously rejected at compile
/// time with <c>"more than 255 upvalues per function not supported"</c>. Large
/// minified bundles (Google Analytics / gtag, GTM) contain inner functions
/// that reference hundreds of hoisted outer vars, so they failed to compile —
/// leaving the enclosing declarations (e.g. <c>Nf</c>) undefined and surfacing
/// as <c>"not a function: undefined"</c> at runtime. These tests pin correct
/// behavior for closures whose upvalue count crosses the old byte boundary.
/// </summary>
[TestClass]
public class JsManyUpvaluesTests
{
    /// <summary>An inner function that captures ~300 outer locals must compile
    /// (no 255 cap) and sum every captured upvalue correctly — including the
    /// ones past index 255. Under the u8 cap this threw at compile time; once
    /// the cap is lifted, a u8 index operand would have wrapped high captures
    /// modulo 256 and double-counted/aliased low cells, giving a wrong sum.</summary>
    [TestMethod]
    public void Inner_function_capturing_over_255_upvalues_sums_all()
    {
        var sb = new StringBuilder();
        sb.Append("function outer(){\n");
        for (var i = 0; i < 300; i++)
        {
            sb.Append($"  var v{i} = {i};\n");
        }
        // Inner captures all 300 outer vars and sums them. Capturing every
        // binding forces 300 distinct upvalue indices (well past the old 255).
        sb.Append("  function inner(){\n");
        sb.Append("    return ");
        for (var i = 0; i < 300; i++)
        {
            sb.Append($"v{i}{(i < 299 ? " + " : "")}");
        }

        sb.Append(";\n");
        sb.Append("  }\n");
        sb.Append("  return inner();\n");
        sb.Append("}\n");
        sb.Append("outer();\n");

        // Sum of 0..299 = 299*300/2 = 44850.
        Eval(sb.ToString()).AsNumber.Should().Be(44850);
    }

    /// <summary>A direct, unambiguous read of the 299th captured upvalue: the
    /// inner closure captures 300 outer bindings and returns one specific
    /// high-index one. Pins that the high u16 upvalue index resolves to the
    /// right cell rather than aliasing a low index.</summary>
    [TestMethod]
    public void High_index_upvalue_resolves_to_correct_binding()
    {
        var sb = new StringBuilder();
        sb.Append("function outer(){\n");
        for (var i = 0; i < 300; i++)
        {
            sb.Append($"  var v{i} = {i * 2};\n");
        }

        sb.Append("  function inner(){\n");
        // Force capture of all 300, then return the 299th specifically.
        sb.Append("    var sum = 0;\n");
        for (var i = 0; i < 300; i++)
        {
            sb.Append($"    if (v{i} < 0) sum += v{i};\n");
        }

        sb.Append("    return v299;\n");
        sb.Append("  }\n");
        sb.Append("  return inner();\n");
        sb.Append("}\n");
        sb.Append("outer();\n");

        Eval(sb.ToString()).AsNumber.Should().Be(299 * 2);
    }

    /// <summary>An inner closure that captures >255 outer bindings and writes
    /// back through a high-index captured cell. The write must mutate the
    /// shared cell so the outer function (and other closures) observe it —
    /// exercising StoreUpvalue with a u16 index past the old byte boundary.</summary>
    [TestMethod]
    public void Write_back_through_high_index_captured_cell()
    {
        var sb = new StringBuilder();
        sb.Append("function outer(){\n");
        for (var i = 0; i < 300; i++)
        {
            sb.Append($"  var v{i} = {i};\n");
        }

        sb.Append("  function writer(){\n");
        // Reference every var so all 300 are captured; then write back to the
        // high-index 299th binding through its shared cell.
        sb.Append("    var t = 0;\n");
        for (var i = 0; i < 300; i++)
        {
            sb.Append($"    if (v{i} < 0) t += v{i};\n");
        }

        sb.Append("    v299 = 5000;\n");
        sb.Append("  }\n");
        sb.Append("  writer();\n");
        // outer reads v299 after the closure mutated the shared cell.
        sb.Append("  return v299;\n");
        sb.Append("}\n");
        sb.Append("outer();\n");

        Eval(sb.ToString()).AsNumber.Should().Be(5000);
    }

    private static JsValue Eval(string src)
    {
        var program = new JsParser(src).ParseProgram();
        var chunk = JsCompiler.CompileForEval(program);
        return new JsVm(new JsRuntime()).Run(chunk);
    }
}
