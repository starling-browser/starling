using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout;
using Starling.Layout.Box;
namespace Starling.Gui.Tests;

/// <summary>
/// Tests for per-character selection. The model works against the same
/// <c>PlacedFragment</c> list the painter consumes, so we drive it from a
/// real layout to keep the test data honest about glyph positions, line
/// breaks, and whitespace fragments.
/// </summary>
[TestClass]
public sealed class SelectionModelTests
{
    private static BlockBox Layout(string html, Size viewport)
        => new LayoutEngine(new StyleEngine()).LayoutDocument(HtmlParser.Parse(html), viewport);

    private static List<BoxHitTester.PlacedFragment> Place(string html, Size viewport)
        => BoxHitTester.CollectFragments(Layout(html, viewport));

    // ---- CaretFromPoint ----------------------------------------------------

    [TestMethod]
    public void CaretFromPoint_at_fragment_start_returns_offset_zero()
    {
        var placed = Place("<body><p>hello world</p></body>", new Size(800, 600));
        var hello = placed.First(f => f.Text == "hello");

        var caret = SelectionModel.CaretFromPoint(placed, hello.X + 0.1, hello.Y + 1);

        caret.FragmentIndex.Should().Be(placed.IndexOf(hello));
        caret.CharOffset.Should().Be(0);
    }

    [TestMethod]
    public void CaretFromPoint_past_fragment_end_returns_text_length()
    {
        var placed = Place("<body><p>hello world</p></body>", new Size(800, 600));
        var hello = placed.First(f => f.Text == "hello");

        var caret = SelectionModel.CaretFromPoint(placed, hello.X + hello.Width + 0.01, hello.Y + 1);

        // Just past the right edge of "hello" snaps to either offset 5 of
        // "hello" or offset 0 of the next fragment (the space) — both are
        // the same caret position; assert whichever the model picks is
        // canonically at "end of hello".
        if (caret.FragmentIndex == placed.IndexOf(hello))
        {
            caret.CharOffset.Should().Be(5);
        }
        else
        {
            caret.FragmentIndex.Should().Be(placed.IndexOf(hello) + 1);
            caret.CharOffset.Should().Be(0);
        }
    }

    [TestMethod]
    public void CaretFromPoint_in_middle_of_word_lands_between_characters()
    {
        var placed = Place("<body><p>hello world</p></body>", new Size(800, 600));
        var hello = placed.First(f => f.Text == "hello");

        // Roughly the visual center of the word should produce a caret
        // somewhere strictly inside the word, not pinned to either edge.
        var caret = SelectionModel.CaretFromPoint(placed, hello.X + (hello.Width / 2), hello.Y + 1);

        caret.FragmentIndex.Should().Be(placed.IndexOf(hello));
        caret.CharOffset.Should().BeInRange(1, 4);
    }

    [TestMethod]
    public void CaretFromPoint_off_the_text_line_clamps_to_a_caret_on_that_line()
    {
        var placed = Place("<body><p>hello world</p></body>", new Size(800, 600));
        var firstY = placed[0].Y;

        // A point well below the line should still return a caret on that
        // line — selection should never silently jump to a different line
        // just because the pointer drifted vertically.
        var caret = SelectionModel.CaretFromPoint(placed, placed[0].X + 1, firstY + 200);

        caret.IsValid.Should().BeTrue();
        placed[caret.FragmentIndex].Y.Should().Be(firstY);
    }

    // ---- Order -------------------------------------------------------------

    [TestMethod]
    public void Order_swaps_carets_when_the_end_is_before_the_start()
    {
        var a = new SelectionModel.Caret(2, 3);
        var b = new SelectionModel.Caret(0, 5);

        var range = SelectionModel.Order(a, b);

        range.Start.Should().Be(b);
        range.End.Should().Be(a);
    }

    [TestMethod]
    public void Order_handles_same_fragment_swap()
    {
        var a = new SelectionModel.Caret(1, 4);
        var b = new SelectionModel.Caret(1, 1);

        var range = SelectionModel.Order(a, b);

        range.Start.CharOffset.Should().Be(1);
        range.End.CharOffset.Should().Be(4);
    }

    // ---- RectsFor: sub-fragment slicing -----------------------------------

    [TestMethod]
    public void RectsFor_sub_range_within_a_single_fragment_produces_one_sliced_rect()
    {
        var placed = Place("<body><p>hello world</p></body>", new Size(800, 600));
        var helloIdx = placed.FindIndex(f => f.Text == "hello");
        var hello = placed[helloIdx];

        // Select "ell" — the inner three characters of "hello".
        var range = new SelectionModel.Range(
            new SelectionModel.Caret(helloIdx, 1),
            new SelectionModel.Caret(helloIdx, 4));

        var rects = SelectionModel.RectsFor(placed, range);

        rects.Should().ContainSingle();
        var r = rects[0];
        r.X.Should().BeGreaterThan(hello.X,
            "the highlight should start past the first character of 'hello'");
        (r.X + r.Width).Should().BeLessThan(hello.X + hello.Width,
            "the highlight should end before the last character of 'hello'");
        r.Y.Should().Be(hello.Y);
        r.Height.Should().Be(hello.Height);
    }

    [TestMethod]
    public void RectsFor_sub_range_widths_sum_to_full_fragment_when_split_in_two()
    {
        var placed = Place("<body><p>hello world</p></body>", new Size(800, 600));
        var helloIdx = placed.FindIndex(f => f.Text == "hello");
        var hello = placed[helloIdx];

        var left = SelectionModel.RectsFor(placed, new SelectionModel.Range(
            new SelectionModel.Caret(helloIdx, 0),
            new SelectionModel.Caret(helloIdx, 2)));
        var right = SelectionModel.RectsFor(placed, new SelectionModel.Range(
            new SelectionModel.Caret(helloIdx, 2),
            new SelectionModel.Caret(helloIdx, 5)));

        left.Should().ContainSingle();
        right.Should().ContainSingle();
        // The two pieces should tile the original fragment with no overlap
        // and no gap (within fp tolerance).
        left[0].X.Should().BeApproximately(hello.X, 0.5);
        (left[0].X + left[0].Width).Should().BeApproximately(right[0].X, 0.5);
        (right[0].X + right[0].Width).Should().BeApproximately(hello.X + hello.Width, 0.5);
    }

    [TestMethod]
    public void RectsFor_range_across_three_fragments_produces_three_rects()
    {
        // hello / space / world — selection from middle of hello to middle
        // of world should produce: sliced rect on hello, full rect on the
        // space fragment, sliced rect on world.
        var placed = Place("<body><p>hello world</p></body>", new Size(800, 600));
        var helloIdx = placed.FindIndex(f => f.Text == "hello");
        var worldIdx = placed.FindIndex(f => f.Text == "world");
        var spaceIdx = placed.FindIndex(f => f.Text == " ");
        spaceIdx.Should().BeGreaterThan(helloIdx);
        spaceIdx.Should().BeLessThan(worldIdx);

        var range = new SelectionModel.Range(
            new SelectionModel.Caret(helloIdx, 2),
            new SelectionModel.Caret(worldIdx, 3));

        var rects = SelectionModel.RectsFor(placed, range);
        rects.Should().HaveCount(3);

        // Middle rect should match the whole space fragment.
        var space = placed[spaceIdx];
        rects[1].X.Should().BeApproximately(space.X, 0.5);
        rects[1].Width.Should().BeApproximately(space.Width, 0.5);
    }

    [TestMethod]
    public void RectsFor_empty_range_produces_no_rects()
    {
        var placed = Place("<body><p>hello world</p></body>", new Size(800, 600));
        var caret = new SelectionModel.Caret(0, 2);

        SelectionModel.RectsFor(placed, new SelectionModel.Range(caret, caret))
            .Should().BeEmpty();
    }

    // ---- TextFor: substring across fragments ------------------------------

    [TestMethod]
    public void TextFor_sub_range_within_a_single_fragment_returns_the_substring()
    {
        var placed = Place("<body><p>hello world</p></body>", new Size(800, 600));
        var helloIdx = placed.FindIndex(f => f.Text == "hello");

        var range = new SelectionModel.Range(
            new SelectionModel.Caret(helloIdx, 1),
            new SelectionModel.Caret(helloIdx, 4));

        SelectionModel.TextFor(placed, range).Should().Be("ell");
    }

    [TestMethod]
    public void TextFor_range_across_fragments_concatenates_substrings_including_whitespace()
    {
        // Selecting "llo wo" from "hello world" — start inside "hello",
        // crosses the space fragment, ends inside "world".
        var placed = Place("<body><p>hello world</p></body>", new Size(800, 600));
        var helloIdx = placed.FindIndex(f => f.Text == "hello");
        var worldIdx = placed.FindIndex(f => f.Text == "world");

        var range = new SelectionModel.Range(
            new SelectionModel.Caret(helloIdx, 2),
            new SelectionModel.Caret(worldIdx, 2));

        SelectionModel.TextFor(placed, range).Should().Be("llo wo");
    }

    [TestMethod]
    public void TextFor_full_word_to_full_word_produces_exact_three_fragment_join()
    {
        var placed = Place("<body><p>hello world</p></body>", new Size(800, 600));
        var helloIdx = placed.FindIndex(f => f.Text == "hello");
        var worldIdx = placed.FindIndex(f => f.Text == "world");
        var world = placed[worldIdx];

        var range = new SelectionModel.Range(
            new SelectionModel.Caret(helloIdx, 0),
            new SelectionModel.Caret(worldIdx, world.Text.Length));

        SelectionModel.TextFor(placed, range).Should().Be("hello world");
    }

    [TestMethod]
    public void TextFor_inserts_space_when_line_wrap_dropped_an_implicit_one()
    {
        // Very narrow viewport forces "hello world" to wrap; if the inline
        // formatter drops the space at the wrap boundary there will be no
        // explicit whitespace fragment between the two words. The model
        // must still produce "hello world" when the user selects across.
        var placed = Place("<body><p>hello world</p></body>", new Size(40, 600));
        var helloIdx = placed.FindIndex(f => f.Text == "hello");
        var worldIdx = placed.FindIndex(f => f.Text == "world");
        helloIdx.Should().BeGreaterThanOrEqualTo(0);
        worldIdx.Should().BeGreaterThan(helloIdx);
        placed[helloIdx].Y.Should().NotBe(placed[worldIdx].Y,
            "the test only exercises the wrap case if the two words ended up on different lines");

        var range = new SelectionModel.Range(
            new SelectionModel.Caret(helloIdx, 0),
            new SelectionModel.Caret(worldIdx, placed[worldIdx].Text.Length));

        SelectionModel.TextFor(placed, range).Should().Be("hello world");
    }

    [TestMethod]
    public void TextFor_empty_range_returns_empty_string()
    {
        var placed = Place("<body><p>hello world</p></body>", new Size(800, 600));
        var caret = new SelectionModel.Caret(0, 0);

        SelectionModel.TextFor(placed, new SelectionModel.Range(caret, caret))
            .Should().BeEmpty();
    }

    // ---- End-to-end: round-trip through CaretFromPoint --------------------

    [TestMethod]
    public void CaretFromPoint_round_trip_selects_a_sub_word_substring()
    {
        // Drive the model end-to-end: drag from the left edge of one
        // letter in "hello" to the left edge of one letter in "world",
        // ask the model for the resulting selection text, and verify
        // the substring matches what the visual range describes.
        //
        // We deliberately stay in the model's uniform-width fallback
        // (the default test measurer emits no real glyph positions) so
        // the assertions stay measurer-independent.
        var placed = Place("<body><p>hello world</p></body>", new Size(800, 600));
        var hello = placed.First(f => f.Text == "hello");
        var world = placed.First(f => f.Text == "world");

        var perHello = hello.Width / hello.Text.Length;
        var perWorld = world.Width / world.Text.Length;

        // Offset 2 of "hello" sits at the left edge of 'l'; offset 2 of
        // "world" sits at the left edge of 'r'. Selection should be the
        // characters in between: "llo wo".
        var anchor = SelectionModel.CaretFromPoint(placed, hello.X + (2 * perHello), hello.Y + 1);
        var cursor = SelectionModel.CaretFromPoint(placed, world.X + (2 * perWorld), world.Y + 1);
        var range = SelectionModel.Order(anchor, cursor);

        SelectionModel.TextFor(placed, range).Should().Be("llo wo");
    }

    // ---- Grapheme cluster behaviour ---------------------------------------
    //
    // These tests bypass the layout pipeline and construct PlacedFragment
    // records directly so the input glyph data is exactly what the test
    // describes. They exercise the model's uniform-width fallback path
    // (Shaped == null), distributing Width by cluster count rather than by
    // code unit — so per-cluster width is always `Width / clusterCount`.

    private static BoxHitTester.PlacedFragment SyntheticFragment(string text, double width)
        => new(X: 0, Y: 0, Width: width, Height: 20, Text: text, Shaped: null);

    [TestMethod]
    public void CaretFromPoint_in_emoji_surrogate_returns_grapheme_boundary()
    {
        // "😀" is one cluster spanning a surrogate pair (2 UTF-16 units).
        // No matter where the user clicks, the caret must never land at
        // offset 1, which would orphan a surrogate.
        var f = SyntheticFragment("😀", width: 10);
        var fragments = new List<BoxHitTester.PlacedFragment> { f };

        foreach (var probe in new[] { 0.5, 2.5, 4.99, 5.0, 5.01, 7.5, 9.5 })
        {
            var caret = SelectionModel.CaretFromPoint(fragments, probe, 1);
            caret.CharOffset.Should().BeOneOf([0, 2],
                $"caret must snap to a grapheme boundary at x={probe}, got {caret.CharOffset}");
        }
    }

    [TestMethod]
    public void CaretFromPoint_in_combining_mark_returns_grapheme_boundary()
    {
        // "éo" — 'e' + combining acute (forms é) + 'o'. Three UTF-16
        // units, two clusters at offsets 0 and 2. Offset 1 (between the
        // base 'e' and its combining acute) must never appear.
        var f = SyntheticFragment("éo", width: 20);
        var fragments = new List<BoxHitTester.PlacedFragment> { f };

        foreach (var probe in new[] { 0.5, 4.99, 5.01, 9.99, 10.01, 14.99, 15.01, 19.5 })
        {
            var caret = SelectionModel.CaretFromPoint(fragments, probe, 1);
            caret.CharOffset.Should().BeOneOf([0, 2, 3],
                $"offset must skip the dangling combining-mark code unit at x={probe}, got {caret.CharOffset}");
        }
    }

    [TestMethod]
    public void CaretFromPoint_in_zwj_family_treats_sequence_as_one_cluster()
    {
        // "👨‍👩‍👧" — three person emoji glued by zero-width
        // joiners. One cluster, eight UTF-16 units. A click anywhere
        // inside must collapse to a boundary (0 or text.Length).
        var text = "👨‍👩‍👧";
        var f = SyntheticFragment(text, width: 10);
        var fragments = new List<BoxHitTester.PlacedFragment> { f };

        foreach (var probe in new[] { 1.0, 3.0, 5.0, 7.0, 9.0 })
        {
            var caret = SelectionModel.CaretFromPoint(fragments, probe, 1);
            caret.CharOffset.Should().BeOneOf([0, text.Length],
                $"ZWJ family must remain indivisible at x={probe}, got {caret.CharOffset}");
        }
    }

    [TestMethod]
    public void CaretFromPoint_in_regional_indicator_flag_treats_pair_as_one_cluster()
    {
        // Two regional-indicator code points form one flag cluster.
        var text = "🇺🇸"; // four UTF-16 units, one cluster
        var f = SyntheticFragment(text, width: 10);
        var fragments = new List<BoxHitTester.PlacedFragment> { f };

        foreach (var probe in new[] { 1.0, 3.0, 5.0, 7.0, 9.0 })
        {
            var caret = SelectionModel.CaretFromPoint(fragments, probe, 1);
            caret.CharOffset.Should().BeOneOf(0, text.Length);
        }
    }

    [TestMethod]
    public void CaretFromPoint_in_variation_selector_does_not_split_base()
    {
        // U+2764 ❤ + U+FE0F (VS-16) presents as a red heart emoji. VS-16
        // attaches to the preceding base; selection must not split the
        // base from its variation selector.
        var text = "❤️";
        var f = SyntheticFragment(text, width: 10);
        var fragments = new List<BoxHitTester.PlacedFragment> { f };

        foreach (var probe in new[] { 1.0, 3.0, 5.0, 7.0, 9.0 })
        {
            var caret = SelectionModel.CaretFromPoint(fragments, probe, 1);
            caret.CharOffset.Should().BeOneOf(0, text.Length);
        }
    }

    [TestMethod]
    public void CaretFromPoint_in_skin_tone_modifier_keeps_emoji_intact()
    {
        // 👋🏽 = waving hand + medium skin tone modifier. Four UTF-16 units,
        // one cluster.
        var text = "👋🏽";
        var f = SyntheticFragment(text, width: 10);
        var fragments = new List<BoxHitTester.PlacedFragment> { f };

        foreach (var probe in new[] { 1.0, 5.0, 9.0 })
        {
            var caret = SelectionModel.CaretFromPoint(fragments, probe, 1);
            caret.CharOffset.Should().BeOneOf(0, text.Length);
        }
    }

    [TestMethod]
    public void CaretFromPoint_in_mixed_ascii_and_emoji_places_caret_at_each_cluster_boundary()
    {
        // "hi 😀 yo" — eight UTF-16 units, seven clusters
        // (h, i, ' ', 😀, ' ', y, o). With Width = 70 the per-cluster
        // width is exactly 10 and boundaries are [0,1,2,3,5,6,7,8].
        // Crucially, no probe — wherever it lands — produces offset 4
        // (mid-surrogate-pair inside the emoji).
        var f = SyntheticFragment("hi 😀 yo", width: 70);
        var fragments = new List<BoxHitTester.PlacedFragment> { f };
        var validBoundaries = new[] { 0, 1, 2, 3, 5, 6, 7, 8 };

        foreach (var probe in new[] { 0.5, 9.0, 19.0, 29.0, 34.0, 36.0, 44.0, 49.0, 59.0, 69.5 })
        {
            var caret = SelectionModel.CaretFromPoint(fragments, probe, 1);
            caret.CharOffset.Should().BeOneOf(validBoundaries,
                $"x={probe} produced offset {caret.CharOffset}, which is not a grapheme boundary of \"hi 😀 yo\"");
        }
    }

    [TestMethod]
    public void RectsFor_through_emoji_slices_on_grapheme_boundaries()
    {
        // Range covering just the emoji cluster of "hi 😀 yo": offsets
        // 3 → 5 (one cluster wide). With 7 clusters in 70px, expect a
        // single rect ≈ 10px wide, ≈ 30px from the fragment's left edge.
        var f = SyntheticFragment("hi 😀 yo", width: 70);
        var fragments = new List<BoxHitTester.PlacedFragment> { f };

        var range = new SelectionModel.Range(
            new SelectionModel.Caret(0, 3),
            new SelectionModel.Caret(0, 5));

        var rects = SelectionModel.RectsFor(fragments, range);

        rects.Should().ContainSingle();
        rects[0].X.Should().BeApproximately(30, 0.001);
        rects[0].Width.Should().BeApproximately(10, 0.001);
    }

    [TestMethod]
    public void TextFor_across_emoji_returns_well_formed_substring()
    {
        // "hi 😀 yo" range (0,1) → (0,5) → "i 😀". Exactly four UTF-16
        // units copied; the surrogate pair stays intact.
        var f = SyntheticFragment("hi 😀 yo", width: 70);
        var fragments = new List<BoxHitTester.PlacedFragment> { f };

        var range = new SelectionModel.Range(
            new SelectionModel.Caret(0, 1),
            new SelectionModel.Caret(0, 5));

        var text = SelectionModel.TextFor(fragments, range);

        text.Should().Be("i 😀");
        text.Length.Should().Be(4);
        // The trailing surrogate pair is well-formed: the low surrogate at
        // [^1] must be preceded by its high surrogate.
        char.IsLowSurrogate(text, text.Length - 1).Should().BeTrue();
        char.IsSurrogatePair(text, text.Length - 2).Should().BeTrue();
    }
}
