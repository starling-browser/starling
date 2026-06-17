using AwesomeAssertions;
using Starling.Css.Properties;
using Starling.Css.Values;
using Starling.Dom;
using Starling.Html;
using Starling.Paint.DisplayList;

namespace Starling.Paint.Tests;

/// <summary>
/// Tier 5 item 21 downstream check — a transition's interpolated mid-value
/// must actually paint. The interpolator emits typed <see cref="CssBoxShadow"/>
/// and <see cref="CssGradient"/> intermediates; this drives a real
/// <c>box-shadow</c> + <c>background-image</c> transition to half duration
/// through the style engine's compositor and asserts the display list carries
/// the lerped shadow and gradient, not an endpoint snap.
/// </summary>
[TestClass]
public sealed class TransitionMidValuePaintTests
{
    private const string Html =
        "<html><head><style>" +
        "  div { width: 60px; height: 60px;" +
        "        box-shadow: 0px 0px 0px 0px rgb(0,0,0);" +
        "        background-image: linear-gradient(0deg, rgb(255,0,0), rgb(0,0,0));" +
        "        transition-property: box-shadow, background-image;" +
        "        transition-duration: 200ms;" +
        "        transition-timing-function: linear; }" +
        "</style></head>" +
        "<body><div></div></body></html>";

    [TestMethod]
    public void Mid_transition_box_shadow_and_gradient_paint_lerped_values()
    {
        var doc = HtmlParser.Parse(Html);
        var div = FindFirstDiv(doc)
            ?? throw new InvalidOperationException("Test fixture has no <div> element");

        var painter = new Painter();
        var (root, style) = painter.LayoutDocumentWithStyle(doc, new Starling.Layout.Size(400, 200), defaultFontSize: 16f);

        // Prime the compositor snapshot with the initial cascade (transitions
        // only fire on *changes*, never on the first observed value).
        style.ComputeWithAnimations(div, nowMs: 0);

        // Retarget via inline style — the stylesheet rule keeps the transition
        // longhands; inline wins on the two animated properties.
        div.SetAttribute("style",
            "box-shadow: 16px 16px 8px 0px rgb(0,0,0);" +
            "background-image: linear-gradient(90deg, rgb(0,0,255), rgb(0,0,0))");
        style.ComputeWithAnimations(div, nowMs: 0); // starts both transitions at t=0

        style.TransitionEngine.Tick(100); // half of 200ms, linear easing
        var mid = style.ComputeWithAnimations(div, nowMs: 100);

        // The overlaid computed style must hold typed interpolator output.
        var midShadow = mid.Get(PropertyId.BoxShadow).Should().BeOfType<CssBoxShadow>().Subject;
        midShadow.Layers[0].OffsetX.Value.Should().BeApproximately(8, 1e-6);
        var midGradient = mid.Get(PropertyId.BackgroundImage).Should().BeOfType<CssGradient>().Subject;
        midGradient.Line!.AngleDegrees.Should().BeApproximately(45, 1e-6);

        // And that style must paint: the builder consumes the typed mid-values
        // through the same parser entry points as static styles.
        var list = new DisplayListBuilder().Build(root, box =>
            ReferenceEquals(box.Element, div) ? mid : null);

        var shadow = list.Items.OfType<DrawBoxShadow>().Should().ContainSingle().Subject;
        shadow.OffsetX.Should().BeApproximately(8, 1e-6);
        shadow.OffsetY.Should().BeApproximately(8, 1e-6);
        shadow.Blur.Should().BeApproximately(4, 1e-6);
        shadow.Inset.Should().BeFalse();

        var fill = list.Items.OfType<FillGradient>().Should().ContainSingle().Subject;
        fill.Gradient.Kind.Should().Be(CssGradientKind.Linear);
        fill.Gradient.Line!.AngleDegrees.Should().BeApproximately(45, 1e-6);
        // Premultiplied sRGB midpoint of red → blue keeps both channels live.
        fill.Gradient.Stops[0].Color.R.Should().BeInRange(110, 145);
        fill.Gradient.Stops[0].Color.B.Should().BeInRange(110, 145);
    }

    private static Element? FindFirstDiv(Document document)
    {
        foreach (var d in document.GetElementsByTagName("div"))
        {
            return d;
        }

        return null;
    }
}
