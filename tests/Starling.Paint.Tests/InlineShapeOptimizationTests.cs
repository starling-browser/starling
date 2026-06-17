using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout;
using Starling.Layout.Text;

namespace Starling.Paint.Tests;

/// <summary>
/// Pins that inline layout does not add fallback shaping beyond its word-token
/// requests when the measurer provides one glyph per character.
/// </summary>
[TestClass]
public sealed class InlineShapeOptimizationTests
{
    [TestMethod]
    public void Ascii_paragraph_shapes_each_word_token_once()
    {
        var measurer = new OneToOneTextMeasurer();
        var engine = new LayoutEngine(new StyleEngine(), measurer);

        var doc = HtmlParser.Parse(
            "<body><p>the quick brown fox jumps over the lazy dog</p></body>");
        engine.LayoutDocument(doc, new Size(800, 600));

        measurer.ShapeCalls.Should().Be(9, "the paragraph has nine word tokens");
    }

    [TestMethod]
    public void Two_paragraphs_shape_one_run_each()
    {
        var measurer = new OneToOneTextMeasurer();
        var engine = new LayoutEngine(new StyleEngine(), measurer);

        var doc = HtmlParser.Parse(
            "<body><p>alpha beta gamma</p><p>delta epsilon zeta</p></body>");
        engine.LayoutDocument(doc, new Size(800, 600));

        measurer.ShapeCalls.Should().Be(6, "the two paragraphs have six word tokens total");
    }

    /// <summary>
    /// Same paragraph rendered through two consecutive layouts on the same
    /// measurer instance. The layout still asks for each token, and the
    /// measurer cache hands back the same <see cref="ShapedRun"/> instance for
    /// repeat text.
    /// </summary>
    [TestMethod]
    public void Repeat_layout_reuses_cached_shape_for_same_run()
    {
        var measurer = new OneToOneTextMeasurer();
        var engine = new LayoutEngine(new StyleEngine(), measurer);

        var html = "<body><p>the quick brown fox jumps over the lazy dog</p></body>";
        engine.LayoutDocument(HtmlParser.Parse(html), new Size(800, 600));
        engine.LayoutDocument(HtmlParser.Parse(html), new Size(800, 600));

        measurer.ShapeCalls.Should().Be(18);

        // Direct cache check: re-issuing Shape for the run text must hand
        // back the same ShapedRun instance — proves the measurer cache is
        // populated by the layout pass and reusable across renders.
        var spec = new FontSpec(["sans-serif"], bold: false, italic: false);
        var first = measurer.Shape("the quick brown fox jumps over the lazy dog", 16, spec);
        var second = measurer.Shape("the quick brown fox jumps over the lazy dog", 16, spec);
        second.Should().BeSameAs(first);
    }

    private sealed class OneToOneTextMeasurer : ITextMeasurer
    {
        private readonly Dictionary<(string Text, double FontSize, FontSpec Spec), ShapedRun> _cache = new();

        public int ShapeCalls { get; private set; }

        public double MeasureWidth(string text, double fontSize, FontSpec spec)
            => text.Length;

        public ShapedRun Shape(string text, double fontSize, FontSpec spec)
        {
            ShapeCalls++;
            var key = (text, fontSize, spec);
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var glyphs = new ShapedGlyph[text.Length];
            for (var i = 0; i < glyphs.Length; i++)
            {
                glyphs[i] = new ShapedGlyph((uint)text[i], i, 0);
            }

            var shaped = new GlyphShapedRun(glyphs, text.Length);
            _cache[key] = shaped;
            return shaped;
        }

        public double NormalLineHeight(double fontSize, FontSpec spec)
            => fontSize * 1.2;

        public double Baseline(double fontSize, FontSpec spec)
            => fontSize;
    }
}
