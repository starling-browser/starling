using AwesomeAssertions;
using Starling.Css.Animations;
using Starling.Css.Properties;
using Starling.Css.Values;
using Starling.Dom;

namespace Starling.Css.Tests;

/// <summary>
/// Covers the property layout-impact classifier and the engine queries that use
/// it. Incremental layout reads these every animation frame to decide whether an
/// in-flight animation needs a relayout (it does for width/margin/font-size, but
/// not for transform/opacity/color, which only repaint). Getting this wrong is
/// what made the animations page re-lay out the world every frame.
/// </summary>
[TestClass]
public sealed class PropertyLayoutImpactTests
{
    [TestMethod]
    public void Paint_and_composite_only_properties_do_not_affect_layout()
    {
        foreach (var id in new[]
        {
            PropertyId.Transform, PropertyId.TransformOrigin, PropertyId.Translate,
            PropertyId.Scale, PropertyId.Rotate, PropertyId.Opacity,
            PropertyId.Color, PropertyId.BackgroundColor, PropertyId.BorderTopColor,
            PropertyId.BoxShadow, PropertyId.Filter, PropertyId.ClipPath,
            PropertyId.BackgroundPosition, PropertyId.OutlineWidth, PropertyId.OutlineColor,
        })
        {
            PropertyRegistry.AffectsLayout(id).Should().BeFalse($"{id} is paint/composite-only");
        }
    }

    [TestMethod]
    public void Geometry_properties_affect_layout()
    {
        foreach (var id in new[]
        {
            PropertyId.Width, PropertyId.Height, PropertyId.MarginTop, PropertyId.PaddingLeft,
            PropertyId.FontSize, PropertyId.LineHeight, PropertyId.Display,
            PropertyId.BorderTopWidth, PropertyId.Top, PropertyId.FlexBasis, PropertyId.LetterSpacing,
        })
        {
            PropertyRegistry.AffectsLayout(id).Should().BeTrue($"{id} can move geometry");
        }
    }

    [TestMethod]
    public void Animation_of_transform_is_not_layout_affecting_but_width_is()
    {
        var el = new Element("div");

        var transform = new AnimationEngine();
        transform.RegisterKeyframes(new KeyframesRule("spin", new[]
        {
            new Keyframe(0.0, new[] { new KeyframeDeclaration("transform", new CssKeyword("none")) }),
            new Keyframe(1.0, new[] { new KeyframeDeclaration("transform", new CssKeyword("none")) }),
        }));
        transform.OnAnimationsCascaded(el, new[] { Decl("spin") });
        transform.HasLayoutAffectingProperty(el).Should().BeFalse(
            "a transform-only animation needs only a repaint, not a relayout");

        var width = new AnimationEngine();
        width.RegisterKeyframes(new KeyframesRule("grow", new[]
        {
            new Keyframe(0.0, new[] { new KeyframeDeclaration("width", CssLength.Zero) }),
            new Keyframe(1.0, new[] { new KeyframeDeclaration("width", new CssLength(100, CssLengthUnit.Px)) }),
        }));
        width.OnAnimationsCascaded(el, new[] { Decl("grow") });
        width.HasLayoutAffectingProperty(el).Should().BeTrue("a width animation moves geometry");
    }

    [TestMethod]
    public void Unknown_or_empty_animation_is_conservatively_layout_affecting()
    {
        var el = new Element("div");
        var engine = new AnimationEngine();
        // An animation whose keyframes are never registered surfaces no
        // recognised property; the conservative answer is "relayout".
        engine.OnAnimationsCascaded(el, new[] { Decl("missing") });
        engine.HasLayoutAffectingProperty(el).Should().BeTrue(
            "with no recognised animated property we relayout to stay correct");
    }

    private static AnimationDeclaration Decl(string name)
        => new(name, 1000, 0, TimingFunction.Linear, double.PositiveInfinity,
            AnimationDirection.Normal, AnimationFillMode.None, AnimationPlayState.Running);
}
