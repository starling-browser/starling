using AwesomeAssertions;
using Starling.Css.Parser;
using Starling.Css.Values;
using Starling.Spec;

namespace Starling.Css.Tests;

[Spec("css-color-5", "https://www.w3.org/TR/css-color-5/")]

[TestClass]
public class ColorMixHueHintTests
{
    private static CssColor ParseColor(string text)
    {
        var sheet = CssParser.ParseStyleSheet("a{x:" + text + "}");
        var rule = (StyleRule)sheet.Rules.Single();
        var decl = rule.Declarations.Single();
        return (CssColor)CssValueParser.Parse(decl.Value);
    }

    [TestMethod]
    public void Default_hue_strategy_is_shorter()
    {
        // Red (hue 0) and a hue near 350 in oklch: shorter path goes backwards through 0/360.
        var c = ParseColor("color-mix(in oklch, oklch(60% 0.15 350), oklch(60% 0.15 10))");
        c.Should().NotBeNull();
        // shorter halfway between 350 and 10 ≈ 0
        var hue = c.C3;
        hue.Should().BeApproximately(0, 1.5);
    }

    [TestMethod]
    public void Shorter_hue_explicit()
    {
        var c = ParseColor("color-mix(in oklch shorter hue, oklch(60% 0.15 350), oklch(60% 0.15 10))");
        c.C3.Should().BeApproximately(0, 1.5);
    }

    [TestMethod]
    public void Longer_hue_takes_the_other_way_around()
    {
        var c = ParseColor("color-mix(in oklch longer hue, oklch(60% 0.15 350), oklch(60% 0.15 10))");
        // longer halfway from 350 wrapping the long way to 10 → ~180
        c.C3.Should().BeApproximately(180, 1.5);
    }

    [TestMethod]
    public void Increasing_hue_always_forward()
    {
        // From 350 to 10 forward goes 350 -> 360 -> 10. Halfway = 0.
        var c = ParseColor("color-mix(in oklch increasing hue, oklch(60% 0.15 350), oklch(60% 0.15 10))");
        c.C3.Should().BeApproximately(0, 1.5);
    }

    [TestMethod]
    public void Decreasing_hue_always_backward()
    {
        // From 350 to 10 backward goes 350 -> 180 -> 10. Halfway = 180.
        var c = ParseColor("color-mix(in oklch decreasing hue, oklch(60% 0.15 350), oklch(60% 0.15 10))");
        c.C3.Should().BeApproximately(180, 1.5);
    }

    [TestMethod]
    public void Hue_hint_works_for_hsl()
    {
        var c = ParseColor("color-mix(in hsl longer hue, hsl(20 100% 50%), hsl(40 100% 50%))");
        // hsl hue is first component (C1). Halfway from 20→40 long way ≈ 210.
        c.C1.Should().BeApproximately(210, 2);
    }

    [TestMethod]
    public void Hue_hint_works_for_lch()
    {
        var c = ParseColor("color-mix(in lch longer hue, lch(50% 50 350), lch(50% 50 10))");
        c.C3.Should().BeApproximately(180, 2);
    }

    [TestMethod]
    public void Hue_hint_in_non_polar_space_fails()
    {
        // longer hue on srgb space is invalid per spec.
        // Parse must not crash; result is the color-mix function held as-is or transparent fallback.
        var sheet = CssParser.ParseStyleSheet("a{x:color-mix(in srgb longer hue, red, blue)}");
        var rule = (StyleRule)sheet.Rules.Single();
        var decl = rule.Declarations.Single();
        var v = CssValueParser.Parse(decl.Value);
        // Either no CssColor produced (fallback to a function/list value) or transparent.
        if (v is CssColor c)
        {
            c.Should().Be(CssColor.Transparent);
        }
    }
}
