using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout.Box;
using Starling.Spec;
namespace Starling.Layout.Tests.Flex;

/// <summary>
/// CSS Flexbox 1 §9.7 — resolving flexible lengths must clamp each item by its
/// used max main size, FREEZE the violator at the clamp, and redistribute the
/// freed space among the remaining unfrozen items, iterating until stable. A
/// single clamp pass without redistribution leaves the freed space unassigned.
///
/// The user-visible bug: x.com's primary column (`width:100%; flex-grow:1;
/// max-width:600px`) inside a 920px flex slot kept the full 920px reserved, so
/// the right sidebar was pushed ~300px off-screen instead of sitting at x≈837.
/// </summary>
[TestClass]
[Spec("css-flexbox-1", "https://drafts.csswg.org/css-flexbox-1/#resolve-flexible-lengths", section: "9.7")]
public sealed class FlexMaxMainSizeTests
{
    private static LayoutEngine NewEngine() => new(new StyleEngine());

    private static BlockBox Layout(string html, Size viewport)
        => NewEngine().LayoutDocument(HtmlParser.Parse(html), viewport);

    private static List<Box.Box> FlexChildren(Box.Box root)
    {
        var container = FindBox(root, b => b.Element?.GetAttribute("id") == "c")!;
        return container.Children.OfType<BlockBox>().Cast<Box.Box>().ToList();
    }

    private static Box.Box? FindBox(Box.Box root, Func<Box.Box, bool> pred)
    {
        if (pred(root))
        {
            return root;
        }

        foreach (var child in root.Children)
        {
            var hit = FindBox(child, pred);
            if (hit is not null)
            {
                return hit;
            }
        }
        return null;
    }

    [SpecFact]
    public void Max_width_caps_a_grown_item_and_hands_the_slack_to_its_sibling()
    {
        // The x.com shape: 920px row; the primary column wants 100% + grow but
        // is capped at 600. Its sibling (grow:1, basis 200) takes ALL the freed
        // space: 920 - 600 - 200 = 120 → sibling = 320.
        var root = Layout("""
            <body style="margin:0"><div id="c" style="display:flex; width:920px; height:40px">
              <div style="width:100%; flex-grow:1; max-width:600px; height:40px"></div>
              <div style="width:200px; flex-grow:1; height:40px"></div>
            </div></body>
            """, new Size(1280, 900));

        var items = FlexChildren(root);
        items.Should().HaveCount(2);
        items[0].Frame.X.Should().BeApproximately(0, 0.5);
        items[0].Frame.Width.Should().BeApproximately(600, 0.5);
        items[1].Frame.X.Should().BeApproximately(600, 0.5);
        items[1].Frame.Width.Should().BeApproximately(320, 0.5);
    }

    [SpecFact]
    public void Freed_space_from_a_max_violator_is_redistributed_iteratively()
    {
        // Three flex:1 items in 600px. Unconstrained each would get 200. The
        // first is capped at 100; §9.7 freezes it and re-runs distribution, so
        // the other two get (600 - 100) / 2 = 250 each. A one-shot clamp pass
        // would leave them at 200 and strand 100px.
        var root = Layout("""
            <body style="margin:0"><div id="c" style="display:flex; width:600px; height:40px">
              <div style="flex:1 1 0; max-width:100px; height:40px"></div>
              <div style="flex:1 1 0; height:40px"></div>
              <div style="flex:1 1 0; height:40px"></div>
            </div></body>
            """, new Size(1280, 900));

        var items = FlexChildren(root);
        items[0].Frame.Width.Should().BeApproximately(100, 0.5);
        items[1].Frame.X.Should().BeApproximately(100, 0.5);
        items[1].Frame.Width.Should().BeApproximately(250, 0.5);
        items[2].Frame.X.Should().BeApproximately(350, 0.5);
        items[2].Frame.Width.Should().BeApproximately(250, 0.5);
    }

    [SpecFact]
    public void Min_clamped_shrink_violator_freezes_and_the_rest_absorb_the_overflow()
    {
        // Two 200px items shrink into 300px. Equal shrink would target 150
        // each, but the first's min-width:180 wins; it freezes at 180 and the
        // second absorbs the rest: 300 - 180 = 120. Without redistribution the
        // second would stop at 150 and the line would overflow to 330.
        var root = Layout("""
            <body style="margin:0"><div id="c" style="display:flex; width:300px; height:40px">
              <div style="width:200px; min-width:180px; height:40px"></div>
              <div style="width:200px; height:40px"></div>
            </div></body>
            """, new Size(1280, 900));

        var items = FlexChildren(root);
        items[0].Frame.Width.Should().BeApproximately(180, 0.5);
        items[1].Frame.X.Should().BeApproximately(180, 0.5);
        items[1].Frame.Width.Should().BeApproximately(120, 0.5);
    }

    [SpecFact]
    public void Max_width_clamps_the_hypothetical_size_of_an_inflexible_item()
    {
        // No flex factors at all: the hypothetical main size itself is the
        // base size clamped by max (§9.7 step 1), so width:100% + max-width
        // behaves like width:600.
        var root = Layout("""
            <body style="margin:0"><div id="c" style="display:flex; width:920px; height:40px">
              <div style="width:100%; max-width:600px; height:40px"></div>
              <div style="width:200px; height:40px"></div>
            </div></body>
            """, new Size(1280, 900));

        var items = FlexChildren(root);
        items[0].Frame.Width.Should().BeApproximately(600, 0.5);
        items[1].Frame.X.Should().BeApproximately(600, 0.5);
    }

    [SpecFact]
    public void Stretch_in_a_row_is_clamped_by_the_item_max_height()
    {
        // align-items defaults to stretch: the auto-height item fills the
        // 100px line, but the capped one stops at its max-height.
        var root = Layout("""
            <body style="margin:0"><div id="c" style="display:flex; width:600px; height:100px">
              <div id="capped" style="width:100px; max-height:40px"></div>
              <div id="full" style="width:100px"></div>
            </div></body>
            """, new Size(1280, 900));

        var capped = FindBox(root, b => b.Element?.GetAttribute("id") == "capped")!;
        var full = FindBox(root, b => b.Element?.GetAttribute("id") == "full")!;
        capped.Frame.Height.Should().BeApproximately(40, 0.5);
        full.Frame.Height.Should().BeApproximately(100, 0.5);
    }

    [SpecFact]
    public void Stretch_in_a_column_is_clamped_by_the_item_max_width()
    {
        var root = Layout("""
            <body style="margin:0"><div id="c" style="display:flex; flex-direction:column; width:300px">
              <div id="capped" style="height:40px; max-width:120px"></div>
              <div id="full" style="height:40px"></div>
            </div></body>
            """, new Size(1280, 900));

        var capped = FindBox(root, b => b.Element?.GetAttribute("id") == "capped")!;
        var full = FindBox(root, b => b.Element?.GetAttribute("id") == "full")!;
        capped.Frame.Width.Should().BeApproximately(120, 0.5);
        full.Frame.Width.Should().BeApproximately(300, 0.5);
    }
}
