using AwesomeAssertions;
using Starling.Css.Parser;
using Starling.Css.Properties;
using Starling.Css.Values;
using Starling.Spec;

namespace Starling.Css.Tests;

/// <summary>
/// CSS Backgrounds and Borders 3 §3 — background-image, -position, -size,
/// -repeat. These are the inputs the sprite-sheet pattern that drives
/// mcmaster.com (and most "icon grid" sites) depends on.
/// </summary>
[TestClass]
[Spec("css-images-4", "https://www.w3.org/TR/css-images-4/")]
public sealed class BackgroundImageTests
{
    private static List<PropertyDeclaration> Expand(string css)
    {
        var sheet = CssParser.ParseStyleSheet($"x {{ {css} }}");
        var rule = (StyleRule)sheet.Rules[0];
        return rule.Declarations.SelectMany(PropertyRegistry.Parse).ToList();
    }

    [TestMethod]
    public void Background_image_url_becomes_css_url()
    {
        var decls = Expand("background-image: url(\"sprite.png\");");

        var value = decls.Single(d => d.Id == PropertyId.BackgroundImage).Value;
        value.Should().Be(new CssUrl("sprite.png"));
    }

    [TestMethod]
    public void Background_image_none_keyword()
    {
        var decls = Expand("background-image: none;");

        decls.Single(d => d.Id == PropertyId.BackgroundImage).Value.Should().Be(new CssKeyword("none"));
    }

    [TestMethod]
    public void Background_position_single_length_resolves_to_x_offset()
    {
        // McMaster's sprite math: `background-position: -60px` is shorthand
        // for `-60px center` and picks the second slice out of a horizontal
        // sprite sheet. We require the value to round-trip as a CssLength so
        // the painter can compute a negative source-rect offset.
        var decls = Expand("background-position: -60px;");

        var value = decls.Single(d => d.Id == PropertyId.BackgroundPosition).Value;
        // Either a bare length (-60px → CssLength) or a list that begins
        // with one. Both are acceptable; the painter must consume both.
        if (value is CssValueList list)
        {
            list.Values[0].Should().Be(new CssLength(-60, CssLengthUnit.Px));
        }
        else
        {
            value.Should().Be(new CssLength(-60, CssLengthUnit.Px));
        }
    }

    [TestMethod]
    public void Background_position_two_lengths()
    {
        var decls = Expand("background-position: -120px -60px;");

        var value = decls.Single(d => d.Id == PropertyId.BackgroundPosition).Value;
        value.Should().BeOfType<CssValueList>();
        var list = (CssValueList)value;
        list.Values[0].Should().Be(new CssLength(-120, CssLengthUnit.Px));
        list.Values[^1].Should().Be(new CssLength(-60, CssLengthUnit.Px));
    }

    [TestMethod]
    public void Background_size_keywords_contain_and_cover()
    {
        Expand("background-size: contain;")
            .Single(d => d.Id == PropertyId.BackgroundSize).Value
            .Should().Be(new CssKeyword("contain"));

        Expand("background-size: cover;")
            .Single(d => d.Id == PropertyId.BackgroundSize).Value
            .Should().Be(new CssKeyword("cover"));
    }

    [TestMethod]
    public void Background_size_explicit_length()
    {
        var decls = Expand("background-size: 1320px;");

        var value = decls.Single(d => d.Id == PropertyId.BackgroundSize).Value;
        value.Should().Be(new CssLength(1320, CssLengthUnit.Px));
    }

    [TestMethod]
    public void Background_repeat_no_repeat()
    {
        var decls = Expand("background-repeat: no-repeat;");

        decls.Single(d => d.Id == PropertyId.BackgroundRepeat).Value
            .Should().Be(new CssKeyword("no-repeat"));
    }

    [TestMethod]
    public void Background_shorthand_carries_url_alongside_color()
    {
        // `background: url("x.png") #fff no-repeat center;` mixes image + color
        // + repeat + position. The shorthand should expand into all four
        // longhands without losing the image.
        var decls = Expand("background: url(\"x.png\") #ffffff no-repeat;");

        var image = decls.Single(d => d.Id == PropertyId.BackgroundImage);
        image.Value.Should().Be(new CssUrl("x.png"));
        decls.Should().Contain(d => d.Id == PropertyId.BackgroundColor);
        var repeat = decls.Single(d => d.Id == PropertyId.BackgroundRepeat);
        repeat.Value.Should().Be(new CssKeyword("no-repeat"));
    }
}
