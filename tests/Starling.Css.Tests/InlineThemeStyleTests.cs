using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Css.Parser;
using Starling.Css.Properties;
using Starling.Css.Values;
using Starling.Dom;

namespace Starling.Css.Tests;

/// <summary>
/// Pins the inline-style theming patterns x.com relies on (Tier 2 item 6,
/// tasks/SITE_STYLING_PLAN.md). The dark theme there is applied entirely via
/// inline longhands — <c>color</c>, <c>background-color</c>, the four
/// per-side border colors — while border widths/styles and the four
/// per-corner radius longhands come from atomic classes. Every markup shape
/// and color value below is lifted from
/// <c>testdata/sites/xcom-nasa/index.html</c>.
/// </summary>
[TestClass]
public sealed class InlineThemeStyleTests
{
    /// <summary>The atomic classes the fixture's pill buttons wear.</summary>
    private const string AtomicButtonCss = """
        .r-sdzlij{border-bottom-left-radius:9999px;border-bottom-right-radius:9999px;border-top-left-radius:9999px;border-top-right-radius:9999px;}
        .r-1phboty{border-bottom-style:solid;border-left-style:solid;border-right-style:solid;border-top-style:solid;}
        .r-rs99b7{border-bottom-width:1px;border-left-width:1px;border-right-width:1px;border-top-width:1px;}
        """;

    private static (Document doc, StyleEngine engine) Setup(string css = "")
    {
        var doc = new Document();
        var engine = new StyleEngine(includeUserAgentStyleSheet: false);
        if (css.Length > 0)
        {
            engine.AddStyleSheet(CssParser.ParseStyleSheet(css));
        }

        return (doc, engine);
    }

    private static (byte R, byte G, byte B, byte A) Rgba(ComputedStyle style, PropertyId id)
    {
        var c = style.GetColor(id);
        return (c.R, c.G, c.B, c.A);
    }

    [TestMethod]
    public void Login_pill_translucent_border_color_longhands_apply_per_side()
    {
        // <div role="link" data-testid="login" style="background-color:
        // rgba(0,0,0,0.00); border-top-color: rgba(255,255,255,0.35); ...">
        var (doc, engine) = Setup();
        var div = doc.CreateElement("div");
        div.SetAttribute("style",
            "background-color: rgba(0,0,0,0.00); " +
            "border-top-color: rgba(255,255,255,0.35); " +
            "border-right-color: rgba(255,255,255,0.35); " +
            "border-bottom-color: rgba(255,255,255,0.35); " +
            "border-left-color: rgba(255,255,255,0.35)");
        doc.AppendChild(div);

        var style = engine.Compute(div);

        style.GetColor(PropertyId.BackgroundColor).A.Should().Be(0,
            "rgba(0,0,0,0.00) is a fully transparent background");
        foreach (var side in new[]
        {
            PropertyId.BorderTopColor, PropertyId.BorderRightColor,
            PropertyId.BorderBottomColor, PropertyId.BorderLeftColor,
        })
        {
            var c = style.GetColor(side);
            (c.R, c.G, c.B).Should().Be(((byte)255, (byte)255, (byte)255),
                $"{side} carries the inline white border tint");
            ((int)c.A).Should().BeInRange(88, 90, "0.35 alpha rounds to ~89/255");
        }
    }

    [TestMethod]
    public void Signup_button_combines_class_radius_longhands_with_inline_colors()
    {
        // <button data-testid="signup" class="... r-sdzlij r-1phboty r-rs99b7 ..."
        //   style="background-color: rgba(239,243,244,1.00); border-top-color:
        //   rgba(0,0,0,0.00); ..."><span style="color: rgba(15,20,25,1.00)">
        var (doc, engine) = Setup(AtomicButtonCss);
        var button = doc.CreateElement("button");
        button.SetAttribute("class", "r-sdzlij r-1phboty r-rs99b7");
        button.SetAttribute("style",
            "background-color: rgba(239,243,244,1.00); " +
            "border-top-color: rgba(0,0,0,0.00); " +
            "border-right-color: rgba(0,0,0,0.00); " +
            "border-bottom-color: rgba(0,0,0,0.00); " +
            "border-left-color: rgba(0,0,0,0.00)");
        var span = doc.CreateElement("span");
        span.SetAttribute("style", "color: rgba(15,20,25,1.00)");
        doc.AppendChild(button);
        button.AppendChild(span);

        var buttonStyle = engine.Compute(button);
        var spanStyle = engine.Compute(span);

        var pill = new CssLength(9999, CssLengthUnit.Px);
        buttonStyle.GetLength(PropertyId.BorderTopLeftRadius).Should().Be(pill);
        buttonStyle.GetLength(PropertyId.BorderTopRightRadius).Should().Be(pill);
        buttonStyle.GetLength(PropertyId.BorderBottomRightRadius).Should().Be(pill);
        buttonStyle.GetLength(PropertyId.BorderBottomLeftRadius).Should().Be(pill);
        buttonStyle.GetLength(PropertyId.BorderTopWidth)
            .Should().Be(new CssLength(1, CssLengthUnit.Px));

        Rgba(buttonStyle, PropertyId.BackgroundColor)
            .Should().Be(((byte)239, (byte)243, (byte)244, (byte)255));
        buttonStyle.GetColor(PropertyId.BorderTopColor).A.Should().Be(0,
            "the dark theme hides the signup button border via transparent side colors");
        Rgba(spanStyle, PropertyId.Color)
            .Should().Be(((byte)15, (byte)20, (byte)25, (byte)255));
    }

    [TestMethod]
    public void Border_side_color_longhands_land_on_their_own_sides()
    {
        // The fixture always sets all four side colors together; routing each
        // to a distinct longhand catches side cross-wiring the uniform pattern
        // would mask. Values are all colors the fixture themes with.
        var (doc, engine) = Setup();
        var div = doc.CreateElement("div");
        div.SetAttribute("style",
            "border-top-color: rgba(83,100,113,1.00); " +
            "border-right-color: rgba(29,155,240,1.00); " +
            "border-bottom-color: rgba(239,243,244,1.00); " +
            "border-left-color: rgba(15,20,25,0.75)");
        doc.AppendChild(div);

        var style = engine.Compute(div);

        Rgba(style, PropertyId.BorderTopColor)
            .Should().Be(((byte)83, (byte)100, (byte)113, (byte)255));
        Rgba(style, PropertyId.BorderRightColor)
            .Should().Be(((byte)29, (byte)155, (byte)240, (byte)255));
        Rgba(style, PropertyId.BorderBottomColor)
            .Should().Be(((byte)239, (byte)243, (byte)244, (byte)255));
        var left = style.GetColor(PropertyId.BorderLeftColor);
        (left.R, left.G, left.B).Should().Be(((byte)15, (byte)20, (byte)25));
        ((int)left.A).Should().BeInRange(190, 192, "0.75 alpha rounds to ~191/255");
    }

    [TestMethod]
    public void Single_corner_radius_longhand_touches_only_its_corner()
    {
        // .r-j3xhw6{border-top-left-radius:16px;} — same longhand inline.
        var (doc, engine) = Setup();
        var div = doc.CreateElement("div");
        div.SetAttribute("style", "border-top-left-radius: 16px");
        doc.AppendChild(div);

        var style = engine.Compute(div);

        style.GetLength(PropertyId.BorderTopLeftRadius)
            .Should().Be(new CssLength(16, CssLengthUnit.Px));
        style.GetLength(PropertyId.BorderTopRightRadius).Value.Should().Be(0);
        style.GetLength(PropertyId.BorderBottomRightRadius).Value.Should().Be(0);
        style.GetLength(PropertyId.BorderBottomLeftRadius).Value.Should().Be(0);
    }

    [TestMethod]
    public void Inline_theme_longhands_override_author_theme_rules()
    {
        // Theme flips only work if the inline longhands beat the stylesheet.
        var (doc, engine) = Setup("""
            div { background-color: #FFFFFF; border-top-color: #000000; color: #000000; }
            """);
        var div = doc.CreateElement("div");
        div.SetAttribute("style",
            "background-color: rgba(15,20,25,0.75); " +
            "border-top-color: rgba(83,100,113,1.00); " +
            "color: rgba(231,233,234,1.00)");
        doc.AppendChild(div);

        var style = engine.Compute(div);

        var bg = style.GetColor(PropertyId.BackgroundColor);
        (bg.R, bg.G, bg.B).Should().Be(((byte)15, (byte)20, (byte)25));
        ((int)bg.A).Should().BeInRange(190, 192);
        Rgba(style, PropertyId.BorderTopColor)
            .Should().Be(((byte)83, (byte)100, (byte)113, (byte)255));
        Rgba(style, PropertyId.Color)
            .Should().Be(((byte)231, (byte)233, (byte)234, (byte)255));
    }

    [TestMethod]
    public void Inline_text_color_inherits_until_a_nested_inline_color_overrides()
    {
        // Tweet text: a primary-color wrapper with secondary-color spans inside
        // (color: rgba(231,233,234,1.00) wrapping color: rgba(113,118,123,1.00)).
        var (doc, engine) = Setup();
        var wrapper = doc.CreateElement("div");
        wrapper.SetAttribute("style", "color: rgba(231,233,234,1.00)");
        var primary = doc.CreateElement("span");
        var secondary = doc.CreateElement("span");
        secondary.SetAttribute("style", "color: rgba(113,118,123,1.00)");
        doc.AppendChild(wrapper);
        wrapper.AppendChild(primary);
        wrapper.AppendChild(secondary);

        Rgba(engine.Compute(primary), PropertyId.Color)
            .Should().Be(((byte)231, (byte)233, (byte)234, (byte)255));
        Rgba(engine.Compute(secondary), PropertyId.Color)
            .Should().Be(((byte)113, (byte)118, (byte)123, (byte)255));
    }
}
