using AwesomeAssertions;
using Starling.Css.Cssom;
using Starling.Css.Selectors;
using Starling.Spec;

namespace Starling.Css.Tests;

/// <summary>
/// An+B microsyntax parsing + serialization (CSS Syntax 3 §9). Mirrors the WPT
/// cases in css/css-syntax/anb-parsing.html and anb-serialization.html.
/// </summary>
[Spec("css-syntax-3", "https://drafts.csswg.org/css-syntax/#the-anb-type", "§9")]
[TestClass]
public sealed class AnbParsingTests
{
    /// <summary>Round-trips An+B text the same way the WPT test does: set
    /// selectorText to ":nth-child(str)", read it back, slice out the An+B.
    /// Returns "parse error" when the An+B is rejected.</summary>
    private static string RoundTrip(string anb)
    {
        var rule = new CssomStyleRule("foo", new CssomDeclarationBlock());
        rule.TrySetSelectorText("foo");
        rule.TrySetSelectorText($":nth-child({anb})");
        var text = rule.SelectorTextRaw;
        if (text == "foo")
        {
            return "parse error";
        }
        // text is ":nth-child(<X>)"; slice(11,-1) extracts <X>.
        return text.Substring(11, text.Length - 12);
    }

    [TestMethod]
    // odd | even
    [DataRow("odd", "2n+1")]
    [DataRow("even", "2n")]
    // <integer>
    [DataRow("1", "1")]
    [DataRow("+1", "1")]
    [DataRow("-1", "-1")]
    [DataRow("0n + 0", "0")]
    [DataRow("0n + 1", "1")]
    [DataRow("0n - 1", "-1")]
    // A is 1
    [DataRow("1n", "n")]
    [DataRow("1n - 0", "n")]
    [DataRow("1n + 1", "n+1")]
    [DataRow("1n - 1", "n-1")]
    // A is -1
    [DataRow("-1n", "-n")]
    [DataRow("-1n - 0", "-n")]
    [DataRow("-1n + 1", "-n+1")]
    [DataRow("-1n - 1", "-n-1")]
    // implied via + or -
    [DataRow("+n+1", "n+1")]
    [DataRow("-n-1", "-n-1")]
    // B is 0
    [DataRow("n + 0", "n")]
    [DataRow("n - 0", "n")]
    // both nonzero
    [DataRow("2n + 2", "2n+2")]
    [DataRow("-2n - 2", "-2n-2")]
    public void Serializes_anb(string input, string expected)
        => RoundTrip(input).Should().Be(expected);

    [TestMethod]
    // n-dimension forms
    [DataRow("5n", "5n")]
    [DataRow("5N", "5n")]
    [DataRow("+n", "n")]
    [DataRow("n", "n")]
    [DataRow("N", "n")]
    [DataRow("-n", "-n")]
    [DataRow("-N", "-n")]
    [DataRow("5n-5", "5n-5")]
    [DataRow("+n-5", "n-5")]
    [DataRow("n-5", "n-5")]
    [DataRow("-n-5", "-n-5")]
    [DataRow("5n +5", "5n+5")]
    [DataRow("5n -5", "5n-5")]
    [DataRow("+n +5", "n+5")]
    [DataRow("n +5", "n+5")]
    [DataRow("+n -5", "n-5")]
    [DataRow("-n +5", "-n+5")]
    [DataRow("-n -5", "-n-5")]
    [DataRow("5n- 5", "5n-5")]
    [DataRow("-5n- 5", "-5n-5")]
    [DataRow("+n- 5", "n-5")]
    [DataRow("n- 5", "n-5")]
    [DataRow("-n- 5", "-n-5")]
    [DataRow("5n + 5", "5n+5")]
    [DataRow("5n - 5", "5n-5")]
    [DataRow("+n + 5", "n+5")]
    [DataRow("n + 5", "n+5")]
    [DataRow("+n - 5", "n-5")]
    [DataRow("-n + 5", "-n+5")]
    [DataRow("-n - 5", "-n-5")]
    public void Parses_valid_anb(string input, string expected)
        => RoundTrip(input).Should().Be(expected);

    [TestMethod]
    [DataRow("+ n")]
    [DataRow("+ n-5")]
    [DataRow("n 5")]
    [DataRow("+ n +5")]
    [DataRow("-n 5")]
    [DataRow("5n- -5")]
    [DataRow("5n- +5")]
    [DataRow("+ n- 5")]
    [DataRow("n- +5")]
    [DataRow("n- -5")]
    [DataRow("-n- +5")]
    [DataRow("-n- -5")]
    [DataRow("5n + +5")]
    [DataRow("5n + -5")]
    [DataRow("5n - +5")]
    [DataRow("5n - -5")]
    [DataRow("+ n + 5")]
    [DataRow("+n + +5")]
    [DataRow("+n + -5")]
    [DataRow("+n - +5")]
    [DataRow("+n - -5")]
    [DataRow("-n + +5")]
    [DataRow("-n + -5")]
    [DataRow("-n - +5")]
    [DataRow("-n - -5")]
    [DataRow("1 - n")]
    [DataRow("0 - n")]
    [DataRow("-1 + n")]
    [DataRow("2 n + 2")]
    [DataRow("- 2n")]
    [DataRow("+ 2n")]
    [DataRow("+2 n")]
    public void Rejects_invalid_anb(string input)
        => RoundTrip(input).Should().Be("parse error");

    [TestMethod]
    [DataRow(2, 1, "2n+1")]
    [DataRow(2, 0, "2n")]
    [DataRow(0, 1, "1")]
    [DataRow(0, -1, "-1")]
    [DataRow(1, 0, "n")]
    [DataRow(-1, 0, "-n")]
    [DataRow(1, 1, "n+1")]
    [DataRow(-1, -1, "-n-1")]
    [DataRow(5, -5, "5n-5")]
    public void SerializeAnb_direct(int a, int b, string expected)
        => SelectorSerializer.SerializeAnb(new NthPattern(a, b)).Should().Be(expected);
}
