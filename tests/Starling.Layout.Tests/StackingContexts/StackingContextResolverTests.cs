using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Dom;
using Starling.Html;
using Starling.Layout.Box;
using Starling.Layout.Compositor;
using Starling.Spec;

namespace Starling.Layout.Tests.StackingContexts;

/// <summary>
/// Unit-level tests that exercise <see cref="StackingContextResolver.Resolve"/>
/// directly, one CSS stacking-context rule per test. Styles are produced by the
/// real cascade (the resolver's only input contract is a
/// <see cref="ComputedStyle"/>); a throwaway <see cref="BlockBox"/> stands in
/// for the box, which the resolver only uses for null-checking.
/// </summary>
[TestClass]
public sealed class StackingContextResolverTests
{
    /// <summary>Compute the style of the single element with the given id and run
    /// the resolver against it.</summary>
    private static LayerHint ResolveFor(string elementHtml, string id, bool isRoot = false)
    {
        var document = HtmlParser.Parse($"<body>{elementHtml}</body>");
        var engine = new StyleEngine();
        var element = FindElementById(document.DocumentElement!, id)!;
        var style = engine.Compute(element);
        var box = new BlockBox(style, element);
        return StackingContextResolver.Resolve(box, style, isRoot);
    }

    [TestMethod]
    public void Null_style_yields_no_hints()
    {
        var box = new AnonymousBlockBox(parentStyle: null);
        StackingContextResolver.Resolve(box, style: null).Should().Be(LayerHint.None);
    }

    [TestMethod]
    public void Plain_box_yields_no_hints()
    {
        ResolveFor("""<div id="t">x</div>""", "t").Should().Be(LayerHint.None);
    }

    [TestMethod]
    public void Root_flag_sets_root_bit()
    {
        ResolveFor("""<div id="t">x</div>""", "t", isRoot: true)
            .Should().HaveFlag(LayerHint.Root);
    }

    [TestMethod]
    [Spec("css-position-3", "https://www.w3.org/TR/css-position-3/#stacking-context", section: "9")]
    public void Relative_with_z_index_is_promoted()
    {
        ResolveFor("""<div id="t" style="position:relative; z-index:1">x</div>""", "t")
            .Should().HaveFlag(LayerHint.Promoted);
    }

    [TestMethod]
    [Spec("css-position-3", "https://www.w3.org/TR/css-position-3/#stacking-context", section: "9")]
    public void Absolute_with_z_index_is_promoted()
    {
        ResolveFor("""<div id="t" style="position:absolute; z-index:3">x</div>""", "t")
            .Should().HaveFlag(LayerHint.Promoted);
    }

    [TestMethod]
    [Spec("css-position-3", "https://www.w3.org/TR/css-position-3/#stacking-context", section: "9")]
    public void Relative_with_auto_z_index_is_not_promoted()
    {
        ResolveFor("""<div id="t" style="position:relative; z-index:auto">x</div>""", "t")
            .Should().Be(LayerHint.None);
    }

    [TestMethod]
    [Spec("css-position-3", "https://www.w3.org/TR/css-position-3/#stacking-context", section: "9")]
    public void Static_with_z_index_is_not_promoted()
    {
        // z-index only takes effect on positioned boxes.
        ResolveFor("""<div id="t" style="z-index:5">x</div>""", "t")
            .Should().Be(LayerHint.None);
    }

    [TestMethod]
    [Spec("css-position-3", "https://www.w3.org/TR/css-position-3/#stacking-context", section: "9")]
    public void Fixed_position_sets_fixed_bit()
    {
        ResolveFor("""<div id="t" style="position:fixed">x</div>""", "t")
            .Should().HaveFlag(LayerHint.Fixed);
    }

    [TestMethod]
    [Spec("css-position-3", "https://www.w3.org/TR/css-position-3/#stacking-context", section: "9")]
    public void Fixed_with_z_index_sets_fixed_and_promoted()
    {
        var hints = ResolveFor("""<div id="t" style="position:fixed; z-index:2">x</div>""", "t");
        hints.Should().HaveFlag(LayerHint.Fixed);
        hints.Should().HaveFlag(LayerHint.Promoted);
    }

    [TestMethod]
    [Spec("css-position-3", "https://www.w3.org/TR/css-position-3/#stacking-context", section: "9")]
    public void Sticky_position_sets_sticky_bit_unconditionally()
    {
        ResolveFor("""<div id="t" style="position:sticky">x</div>""", "t")
            .Should().HaveFlag(LayerHint.Sticky);
    }

    [TestMethod]
    [Spec("css-position-3", "https://www.w3.org/TR/css-position-3/#stacking-context", section: "9")]
    public void Opacity_less_than_one_sets_opacity_bit()
    {
        ResolveFor("""<div id="t" style="opacity:0.5">x</div>""", "t")
            .Should().HaveFlag(LayerHint.OpacityLessThanOne);
    }

    [TestMethod]
    [Spec("css-position-3", "https://www.w3.org/TR/css-position-3/#stacking-context", section: "9")]
    public void Opacity_one_sets_no_bit()
    {
        ResolveFor("""<div id="t" style="opacity:1">x</div>""", "t")
            .Should().Be(LayerHint.None);
    }

    [TestMethod]
    [Spec("css-transforms-1", "https://www.w3.org/TR/css-transforms-1/#transform-rendering", section: "5")]
    public void Non_identity_transform_sets_transform_bit()
    {
        ResolveFor("""<div id="t" style="transform:rotate(45deg)">x</div>""", "t")
            .Should().HaveFlag(LayerHint.Transform3D);
    }

    [TestMethod]
    [Spec("css-transforms-1", "https://www.w3.org/TR/css-transforms-1/#transform-rendering", section: "5")]
    public void Transform_none_sets_no_bit()
    {
        ResolveFor("""<div id="t" style="transform:none">x</div>""", "t")
            .Should().Be(LayerHint.None);
    }

    [TestMethod]
    [Spec("css-position-3", "https://www.w3.org/TR/css-position-3/#stacking-context", section: "9")]
    public void Will_change_transform_sets_will_change_bit()
    {
        ResolveFor("""<div id="t" style="will-change:transform">x</div>""", "t")
            .Should().HaveFlag(LayerHint.WillChange);
    }

    [TestMethod]
    [Spec("css-position-3", "https://www.w3.org/TR/css-position-3/#stacking-context", section: "9")]
    public void Will_change_opacity_sets_will_change_bit()
    {
        ResolveFor("""<div id="t" style="will-change:opacity">x</div>""", "t")
            .Should().HaveFlag(LayerHint.WillChange);
    }

    [TestMethod]
    [Spec("css-position-3", "https://www.w3.org/TR/css-position-3/#stacking-context", section: "9")]
    public void Will_change_auto_sets_no_bit()
    {
        ResolveFor("""<div id="t" style="will-change:auto">x</div>""", "t")
            .Should().Be(LayerHint.None);
    }

    [TestMethod]
    [Spec("css-position-3", "https://www.w3.org/TR/css-position-3/#stacking-context", section: "9")]
    public void Filter_not_none_sets_filter_bit()
    {
        ResolveFor("""<div id="t" style="filter:blur(2px)">x</div>""", "t")
            .Should().HaveFlag(LayerHint.Filter);
    }

    [TestMethod]
    [Spec("css-position-3", "https://www.w3.org/TR/css-position-3/#stacking-context", section: "9")]
    public void Filter_none_sets_no_bit()
    {
        ResolveFor("""<div id="t" style="filter:none">x</div>""", "t")
            .Should().Be(LayerHint.None);
    }

    [TestMethod]
    [Spec("css-position-3", "https://www.w3.org/TR/css-position-3/#stacking-context", section: "9")]
    public void Isolation_isolate_sets_isolation_bit()
    {
        ResolveFor("""<div id="t" style="isolation:isolate">x</div>""", "t")
            .Should().HaveFlag(LayerHint.Isolation);
    }

    [TestMethod]
    [Spec("css-position-3", "https://www.w3.org/TR/css-position-3/#stacking-context", section: "9")]
    public void Isolation_auto_sets_no_bit()
    {
        ResolveFor("""<div id="t" style="isolation:auto">x</div>""", "t")
            .Should().Be(LayerHint.None);
    }

    [TestMethod]
    public void Multiple_conditions_or_together()
    {
        var hints = ResolveFor(
            """<div id="t" style="position:fixed; z-index:2; opacity:0.4; transform:rotate(10deg)">x</div>""",
            "t");
        hints.Should().HaveFlag(LayerHint.Fixed);
        hints.Should().HaveFlag(LayerHint.Promoted);
        hints.Should().HaveFlag(LayerHint.OpacityLessThanOne);
        hints.Should().HaveFlag(LayerHint.Transform3D);
    }

    private static Element? FindElementById(Element root, string id)
    {
        if (root.GetAttribute("id") == id)
        {
            return root;
        }

        for (var child = root.FirstChild; child is not null; child = child.NextSibling)
        {
            if (child is Element el)
            {
                var hit = FindElementById(el, id);
                if (hit is not null)
                {
                    return hit;
                }
            }
        }
        return null;
    }
}
