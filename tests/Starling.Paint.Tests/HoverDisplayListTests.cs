using AwesomeAssertions;
using Starling.Css.Selectors;
using Starling.Css.Values;
using Starling.Dom;
using Starling.Html;
using Starling.Paint.DisplayList;
using PaintList = Starling.Paint.DisplayList.DisplayList;

namespace Starling.Paint.Tests;

/// <summary>
/// Locks down the <see cref="DisplayListBuilder.Build(Starling.Layout.Box.BlockBox, System.Func{Starling.Layout.Box.Box, Starling.Css.Cascade.ComputedStyle?}?, Starling.Layout.Tree.IImageResolver?)"/> style-override hook
/// the Avalonia GUI uses to re-paint <c>a:hover</c> rules without re-laying
/// out. Without the hook, hover repaint had to either ship a misleading
/// translucent tint overlay (the M2-era MAUI approach) or trigger a full
/// re-layout per pointer move.
/// </summary>
[TestClass]
public sealed class HoverDisplayListTests
{
    [TestMethod]
    public void Hover_style_override_replaces_text_color_in_display_list()
    {
        const string Html =
            "<html><head><style>" +
            "  a { color: blue; }" +
            "  a:hover { color: red; }" +
            "</style></head>" +
            "<body><a href=\"#\">click me</a></body></html>";

        var doc = HtmlParser.Parse(Html);
        var anchor = FindFirstAnchor(doc)
            ?? throw new InvalidOperationException("Test fixture has no <a> element");

        var painter = new Painter();
        var (root, style) = painter.LayoutDocumentWithStyle(doc, new Starling.Layout.Size(400, 200), defaultFontSize: 16f);

        // Baseline: no override → display list emits blue text.
        var baseline = new DisplayListBuilder().Build(root);
        var baselineColors = TextColors(baseline);
        baselineColors.Should().Contain(c => IsBlue(c),
            "the layout-time cascade resolves a { color: blue }");

        // Hover: override anchor's style with a :hover-active cascade,
        // builder must emit red text instead.
        var hoverContext = new SelectorMatchContext { HoveredElement = anchor };
        var hoverStyle = style.Compute(anchor, hoverContext);

        var hover = new DisplayListBuilder().Build(root, box =>
        {
            // Anchor's own element or any text-box descendant within its
            // subtree — text inherits color from its element ancestor.
            for (var p = box; p is not null; p = p.Parent)
            {
                if (ReferenceEquals(p.Element, anchor))
                {
                    return hoverStyle;
                }
            }
            return null;
        });

        var hoverColors = TextColors(hover);
        hoverColors.Should().Contain(c => IsRed(c),
            "a:hover { color: red } must paint red text when the override is supplied");
        hoverColors.Should().NotContain(c => IsBlue(c),
            "the hover override must replace, not duplicate, the layout-time color");
    }

    [TestMethod]
    public void Null_override_falls_through_to_layout_time_styles()
    {
        // Regression guard: when callers pass null override, the builder must
        // behave exactly as before — same display list shape, same colors.
        const string Html = "<body><p>plain text</p></body>";
        var doc = HtmlParser.Parse(Html);

        var painter = new Painter();
        var root = painter.LayoutDocument(doc, new Starling.Layout.Size(400, 200), defaultFontSize: 16f);

        var withoutHook = new DisplayListBuilder().Build(root);
        var withNullHook = new DisplayListBuilder().Build(root, styleOverride: _ => null);

        var withoutColors = TextColors(withoutHook).ToList();
        var withNullColors = TextColors(withNullHook).ToList();

        withNullColors.Should().BeEquivalentTo(withoutColors,
            "passing styleOverride: null (or a no-op callback) must reproduce the baseline display list");
    }

    private static IEnumerable<CssColor> TextColors(PaintList list)
    {
        foreach (var item in list.Items)
        {
            if (item is DrawText dt)
            {
                yield return dt.Color;
            }
        }
    }

    private static bool IsBlue(CssColor c) => c.B > 200 && c.R < 50 && c.G < 50;
    private static bool IsRed(CssColor c) => c.R > 200 && c.B < 50 && c.G < 50;

    private static Element? FindFirstAnchor(Document document)
    {
        foreach (var a in document.GetElementsByTagName("a"))
        {
            return a;
        }

        return null;
    }
}
