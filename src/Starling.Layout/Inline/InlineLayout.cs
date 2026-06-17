using Starling.Css.Cascade;
using Starling.Css.Properties;
using Starling.Css.Values;
using Starling.Dom;
using Starling.Layout.Box;
using Starling.Layout.Text;

namespace Starling.Layout.Inline;

/// <summary>
/// Lays out an anonymous block's inline children into line boxes. Produces
/// <see cref="TextFragment"/> entries on each <see cref="TextBox"/> describing
/// where each piece appears on its parent's line.
/// </summary>
internal sealed class InlineLayout
{
    private readonly ITextMeasurer _measurer;
    private readonly Size? _viewport;

    public InlineLayout(ITextMeasurer measurer, Size? viewport = null)
    {
        _measurer = measurer;
        _viewport = viewport;
    }

    public double Layout(Box.Box container, double availableWidth)
        => Layout(container, availableWidth, measure: false);

    /// <summary>
    /// Internal overload used by the inline-block shrink-to-fit measurement
    /// pass. When <paramref name="measure"/> is <c>true</c>, alignment shifts
    /// (<see cref="AlignLines"/>) are skipped so callers walking the resulting
    /// frames (via <see cref="MeasureUsedWidth"/>) see the natural pre-shift
    /// positions. Otherwise a text-align:center under a huge measurement
    /// width would shift everything right by ~availableWidth/2, defeating
    /// shrink-to-fit.
    /// </summary>
    internal double Layout(Box.Box container, double availableWidth, bool measure)
    {
        var fontSize = ResolveFontSize(container.Style);
        var containerSpec = ResolveFontSpec(container.Style);
        var lineHeight = ResolveLineHeight(container.Style, fontSize, containerSpec);
        var baseline = _measurer.Baseline(fontSize, containerSpec);

        // Collect a flat sequence of inline-formatting items by walking the
        // container's inline subtree. Regular `<span>` wrappers flatten so
        // their text contributes directly; `inline-block` boxes (form
        // controls, explicit display:inline-block) stay atomic and lay out
        // as a single unit with their own frame + box model.
        var runs = new List<InlineRun>();
        Flatten(container, runs);

        // No content. Editable text fields (text-family <input>, <textarea>)
        // still reserve one empty line box so an empty field keeps a text
        // line's height — and a place for the caret — instead of collapsing to
        // padding+border the moment its value and placeholder are both gone.
        // Every other empty container stays zero-height (an empty <div> is 0).
        if (runs.Count == 0)
        {
            return ReservesEmptyTextLine(container.Element) ? lineHeight : 0;
        }

        double cursorX = 0, cursorY = 0;
        double currentLineHeight = lineHeight;
        var fragments = new List<(TextBox Owner, int Index)>();
        var placedImages = new List<ImageBox>();
        var placedAtomics = new List<InlineBox>();

        // CSS Text 3 §9.1 — `text-indent` offsets the start edge of the first
        // line of a block container. Percentages resolve against the available
        // inline-axis width. We apply it as the initial pen-X.
        var indent = ResolveTextIndent(container.Style, fontSize, availableWidth);
        if (indent != 0)
        {
            cursorX = indent;
        }

        var firstLine = true;

        foreach (var run in runs)
        {
            switch (run)
            {
                case TextRun text:
                    LayoutText(text, container.Style, availableWidth, baseline,
                        fragments, ref cursorX, ref cursorY, ref currentLineHeight, ref firstLine, indent);
                    break;
                case ImageRun image:
                    LayoutImage(image.Box, availableWidth,
                        placedImages, ref cursorX, ref cursorY, ref currentLineHeight);
                    break;
                case AtomicRun atomic:
                    LayoutAtomic(atomic.Box, availableWidth,
                        placedAtomics, ref cursorX, ref cursorY, ref currentLineHeight);
                    break;
                case LineBreakRun:
                    // <br>: a forced line break. Close the current line and
                    // start a fresh one at the container's line height.
                    cursorY += currentLineHeight;
                    cursorX = 0;
                    currentLineHeight = lineHeight;
                    firstLine = false;
                    break;
            }
        }

        var contentHeight = cursorY + currentLineHeight;

        // CSS UI 4 §7 (text-overflow: ellipsis) + the legacy -webkit-line-clamp
        // pattern: both are fragment surgery on the finished lines, applied
        // before alignment so paint just draws the kept fragments. The block's
        // own style lives on the anonymous wrapper's parent (the wrapper only
        // inherits text properties).
        var blockStyle = container.Kind == BoxKind.AnonymousBlock
            ? container.Parent?.Style
            : container.Style;
        if (blockStyle is not null && fragments.Count > 0)
        {
            contentHeight = ApplyEllipsisAndClamp(
                blockStyle, availableWidth, contentHeight,
                fragments, placedImages, placedAtomics, measure);
        }

        if (!measure)
        {
            AlignLines(container.Style, availableWidth, fragments, placedImages, placedAtomics);
        }

        return contentHeight;
    }

    private void LayoutText(
        TextRun run,
        ComputedStyle? containerStyle,
        double availableWidth,
        double containerBaseline,
        List<(TextBox Owner, int Index)> fragments,
        ref double cursorX,
        ref double cursorY,
        ref double currentLineHeight,
        ref bool firstLine,
        double indent)
    {
        var effectiveStyle = run.Style ?? containerStyle;
        var localFontSize = ResolveFontSize(effectiveStyle);
        var localSpec = ResolveFontSpec(effectiveStyle);
        var localLineHeight = ResolveLineHeight(effectiveStyle, localFontSize, localSpec);
        // CSS 2.2 §10.8.1 — the difference between the used line-height and the
        // font's content area ("leading") is split half above and half below the
        // glyphs. Push the baseline down by that half-leading so text is centred
        // in its line box rather than pinned to the top (which made every box
        // with line-height > normal — pills, buttons, search fields — sit too
        // high). The term is zero when line-height resolves to `normal`, so the
        // common case is unchanged.
        var localBaseline = _measurer.Baseline(localFontSize, localSpec)
            + (localLineHeight - _measurer.NormalLineHeight(localFontSize, localSpec)) / 2;

        // CSS Text 3 §3 / §4 — resolve the white-space behaviour and the
        // spacing/tab/transform controls for this run from its effective style.
        var ws = WhiteSpaceMode.Resolve(effectiveStyle);
        var letterSpacing = ResolveSpacing(effectiveStyle, PropertyId.LetterSpacing, localFontSize);
        var wordSpacing = ResolveSpacing(effectiveStyle, PropertyId.WordSpacing, localFontSize);
        var tabSize = ResolveTabSize(effectiveStyle, localFontSize, localSpec);
        var breakMode = ResolveBreakMode(effectiveStyle);

        // CSS Text 3 §2.1 — `text-transform` rewrites the rendered text before
        // measuring/shaping so the produced glyphs and advances match the
        // transformed string the painter will draw.
        var text = TextTransformer.Apply(run.Text, TextTransformer.Resolve(effectiveStyle));
        if (text.Length == 0)
        {
            return;
        }

        // Segment the run into "lines" on forced breaks (preserved newlines),
        // then lay each segment out as a sequence of break-separated tokens.
        // For collapsing modes the whole run is a single segment with whitespace
        // collapsed to single spaces.
        var segments = ws.PreserveNewlines
            ? SplitOnNewlines(text)
            : new[] { ws.CollapseSpaces ? NormalizeWhitespace(text) : text };

        for (var s = 0; s < segments.Length; s++)
        {
            if (s > 0)
            {
                // Preserved forced line break between segments.
                cursorY += currentLineHeight;
                cursorX = 0;
                currentLineHeight = localLineHeight;
                firstLine = false;
            }

            var segment = segments[s];
            currentLineHeight = Math.Max(currentLineHeight, localLineHeight);

            foreach (var token in Tokenize(segment, ws, tabSize, localFontSize, localSpec))
            {
                LayoutToken(run.Owner, token, ws, breakMode, availableWidth,
                    localFontSize, localSpec, localLineHeight, localBaseline,
                    letterSpacing, wordSpacing,
                    fragments, ref cursorX, ref cursorY, ref currentLineHeight, ref firstLine, indent);
            }
        }
    }

    /// <summary>
    /// Place one token (a word, a run of preserved spaces, or a tab) onto the
    /// current line, soft-wrapping per the resolved white-space wrap policy and
    /// applying letter-/word-spacing to the advance.
    /// </summary>
    private void LayoutToken(
        TextBox owner,
        Token token,
        WhiteSpaceMode ws,
        BreakMode breakMode,
        double availableWidth,
        double fontSize,
        FontSpec spec,
        double lineHeight,
        double baseline,
        double letterSpacing,
        double wordSpacing,
        List<(TextBox Owner, int Index)> fragments,
        ref double cursorX,
        ref double cursorY,
        ref double currentLineHeight,
        ref bool firstLine,
        double indent)
    {
        if (token.Text.Length == 0)
        {
            return;
        }

        var lineOrigin = firstLine ? indent : 0;
        var atLineStart = cursorX <= lineOrigin + 0.0001;

        if (token.IsSpace)
        {
            // In collapsing modes (white-space: normal/nowrap/pre-line) a
            // collapsed space that would lead a line contributes nothing
            // visible, so drop it (CSS Text 3 §4.1.1 trimming).
            if (!ws.PreserveSpaces && atLineStart)
            {
                return;
            }

            var spaceWidth = token.Advance;
            // word-spacing adds to the advance of each space (CSS Text 3 §8.1).
            // Tabs do not receive word-spacing.
            if (!token.IsTab)
            {
                spaceWidth += wordSpacing * token.SpaceCount;
            }

            AddFragment(owner, fragments,
                new TextFragment(token.Text, cursorX, cursorY, spaceWidth, currentLineHeight, baseline, token.Shaped));
            cursorX += spaceWidth;
            return;
        }

        var width = token.Advance + letterSpacing * token.LetterCount;

        // Sub-pixel tolerance: when a box is sized to its own max-content width
        // (e.g. a shrink-to-fit flex item), the sum of word advances equals the
        // available width exactly in theory, but float accumulation can push the
        // final word a hair over and trigger a spurious wrap. Allow a fraction
        // of a pixel of slack so exact-fit content stays on one line.
        const double wrapTolerance = 0.05;
        if (ws.Wrap && cursorX > lineOrigin && cursorX + width > availableWidth + wrapTolerance)
        {
            // Soft-wrap: move to a new line before this word.
            cursorY += currentLineHeight;
            cursorX = 0;
            currentLineHeight = lineHeight;
            firstLine = false;
        }

        // If a single word still overflows the line and breaking inside words is
        // allowed (overflow-wrap: anywhere/break-word or word-break: break-all),
        // split it character-by-character at the available width.
        if (ws.Wrap && breakMode != BreakMode.None && width > availableWidth && cursorX <= 0.0001)
        {
            LayoutBrokenWord(owner, token.Text, availableWidth, fontSize, spec,
                lineHeight, baseline, letterSpacing,
                fragments, ref cursorX, ref cursorY, ref currentLineHeight, ref firstLine);
            return;
        }

        AddFragment(owner, fragments,
            new TextFragment(token.Text, cursorX, cursorY, width, currentLineHeight, baseline, token.Shaped));
        cursorX += width;
    }

    /// <summary>
    /// Break an over-long unbreakable token across lines one character at a time
    /// (CSS Text 3 §6.2 emergency breaking via <c>overflow-wrap</c>/<c>word-break</c>).
    /// Each emitted slice is re-shaped from its substring so the painter gets a
    /// faithful glyph run.
    /// </summary>
    private void LayoutBrokenWord(
        TextBox owner,
        string word,
        double availableWidth,
        double fontSize,
        FontSpec spec,
        double lineHeight,
        double baseline,
        double letterSpacing,
        List<(TextBox Owner, int Index)> fragments,
        ref double cursorX,
        ref double cursorY,
        ref double currentLineHeight,
        ref bool firstLine)
    {
        var start = 0;
        while (start < word.Length)
        {
            var count = 0;
            double sliceWidth = 0;
            // Accumulate characters until adding one more would overflow.
            while (start + count < word.Length)
            {
                var ch = word[start + count].ToString();
                var chW = _measurer.MeasureWidth(ch, fontSize, spec) + letterSpacing;
                if (count > 0 && cursorX + sliceWidth + chW > availableWidth)
                {
                    break;
                }

                sliceWidth += chW;
                count++;
            }
            if (count == 0)
            {
                count = 1; // always make progress
            }

            var slice = word.Substring(start, count);
            var shaped = ShapeWord(slice, fontSize, spec);
            var w = shaped.Advance + letterSpacing * CountLetters(slice);
            AddFragment(owner, fragments,
                new TextFragment(slice, cursorX, cursorY, w, currentLineHeight, baseline, shaped));
            cursorX += w;
            start += count;

            if (start < word.Length)
            {
                cursorY += currentLineHeight;
                cursorX = 0;
                currentLineHeight = lineHeight;
                firstLine = false;
            }
        }
    }

    private ShapedRun ShapeWord(string word, double fontSize, FontSpec spec)
    {
        return _measurer.Shape(word, fontSize, spec);
    }

    // ---- CSS Text 3 helpers -------------------------------------------------

    /// <summary>
    /// One layout token produced by <see cref="Tokenize"/>: either a word (a
    /// maximal run of non-space characters), a run of preserved spaces, or a
    /// single tab. Carries the shaped run + base advance so callers add only
    /// the letter-/word-spacing extras.
    /// </summary>
    private readonly record struct Token(
        string Text,
        ShapedRun Shaped,
        double Advance,
        bool IsSpace,
        bool IsTab,
        int SpaceCount,
        int LetterCount);

    /// <summary>
    /// Split a run into tokens honouring the resolved white-space mode, shaping
    /// the whole segment <em>once</em> and slicing each word out of it (the
    /// per-run shape optimisation: a 9-word paragraph fires the shaper once, not
    /// 10×). Slicing relies on a 1:1 glyph-to-character mapping; when that does
    /// not hold (ligatures, combining marks, surrogate pairs) we fall back to
    /// shaping each word on its own. Tabs expand to the next tab-stop; runs of
    /// spaces are kept verbatim in preserving modes and collapsed to a single
    /// space in collapsing modes (e.g. pre-line).
    /// </summary>
    private IEnumerable<Token> Tokenize(
        string segment,
        WhiteSpaceMode ws,
        double tabSize,
        double fontSize,
        FontSpec spec)
    {
        if (segment.Length == 0)
        {
            yield break;
        }

        // Shape the whole segment once; slice words from it when 1:1.
        var whole = _measurer.Shape(segment, fontSize, spec);
        var canSlice = whole.Glyphs.Length == segment.Length;

        var i = 0;
        while (i < segment.Length)
        {
            var c = segment[i];
            if (ws.PreserveSpaces && c == '\t')
            {
                // Tab: advance to the next multiple of tab-size (CSS Text 3 §6.4).
                var tabShape = canSlice ? whole.Slice(i, i + 1) : _measurer.Shape("\t", fontSize, spec);
                yield return new Token("\t", tabShape, tabSize, IsSpace: true, IsTab: true, SpaceCount: 0, LetterCount: 0);
                i++;
                continue;
            }
            if (c == ' ' || c == '\t')
            {
                // Run of whitespace. In collapsing modes (e.g. pre-line) a run
                // of spaces becomes a single space; in preserving modes the run
                // is kept verbatim.
                var start = i;
                while (i < segment.Length && (segment[i] == ' ' || segment[i] == '\t'))
                {
                    i++;
                }

                if (ws.PreserveSpaces)
                {
                    var spaces = segment[start..i];
                    var shaped = canSlice ? whole.Slice(start, i) : _measurer.Shape(spaces, fontSize, spec);
                    yield return new Token(spaces, shaped, shaped.Advance, IsSpace: true, IsTab: false, SpaceCount: spaces.Length, LetterCount: 0);
                }
                else
                {
                    // Collapsed: a single space; its exact glyph data is unused
                    // by the painter (whitespace-only fragments are skipped).
                    var shaped = _measurer.Shape(" ", fontSize, spec);
                    yield return new Token(" ", shaped, shaped.Advance, IsSpace: true, IsTab: false, SpaceCount: 1, LetterCount: 0);
                }
                continue;
            }

            // Word: maximal run of non-space characters.
            var wordStart = i;
            while (i < segment.Length && segment[i] != ' ' && segment[i] != '\t')
            {
                i++;
            }

            var word = segment[wordStart..i];
            var wordShaped = canSlice ? whole.Slice(wordStart, i) : _measurer.Shape(word, fontSize, spec);
            yield return new Token(word, wordShaped, wordShaped.Advance, IsSpace: false, IsTab: false, SpaceCount: 0, LetterCount: CountLetters(word));
        }
    }

    /// <summary>Number of typographic letter units a letter-spacing extra is added after.</summary>
    private static int CountLetters(string word) => word.Length;

    /// <summary>Split text on \n into forced-line segments, dropping the newlines.</summary>
    private static string[] SplitOnNewlines(string text)
    {
        // Normalize CRLF / CR to LF first so a forced break is a single split.
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                             .Replace('\r', '\n');
        return normalized.Split('\n');
    }

    /// <summary>
    /// Resolve <c>letter-spacing</c> / <c>word-spacing</c> to px. The initial
    /// keyword <c>normal</c> is zero extra spacing; lengths resolve against the
    /// element font size for em (CSS Text 3 §8).
    /// </summary>
    private double ResolveSpacing(ComputedStyle? style, PropertyId id, double fontSize)
    {
        if (style is null)
        {
            return 0;
        }

        return style.Get(id) switch
        {
            CssKeyword { Name: "normal" } => 0,
            CssLength len => ToPxRelative(len, fontSize),
            CssNumber n => n.Value,
            CssPercentage pct => fontSize * pct.Value / 100d, // word-spacing % is font-relative
            _ => 0,
        };
    }

    /// <summary>
    /// Resolve <c>text-indent</c> to px. Percentages resolve against the
    /// containing block's available inline-axis width (CSS Text 3 §9.1).
    /// </summary>
    private double ResolveTextIndent(ComputedStyle? style, double fontSize, double availableWidth)
    {
        if (style is null)
        {
            return 0;
        }

        return style.Get(PropertyId.TextIndent) switch
        {
            CssLength len => ToPxRelative(len, fontSize),
            CssPercentage pct => availableWidth * pct.Value / 100d,
            CssNumber n => n.Value,
            _ => 0,
        };
    }

    /// <summary>
    /// Resolve <c>tab-size</c> to px. A bare number is that many advance widths
    /// of the space (U+0020) glyph; a length is used directly (CSS Text 3 §6.4).
    /// </summary>
    private double ResolveTabSize(ComputedStyle? style, double fontSize, FontSpec spec)
    {
        var spaceWidth = _measurer.MeasureWidth(" ", fontSize, spec);
        if (spaceWidth <= 0)
        {
            spaceWidth = fontSize * 0.28;
        }

        if (style is null)
        {
            return 8 * spaceWidth;
        }

        return style.Get(PropertyId.TabSize) switch
        {
            CssNumber n => n.Value * spaceWidth,
            CssLength len => ToPxRelative(len, fontSize),
            _ => 8 * spaceWidth,
        };
    }

    /// <summary>
    /// Length-to-px that resolves <c>em</c> against the actual element font size
    /// (the shared <see cref="Block.BlockLayout.ToPx(CssLength, Size?)"/> assumes
    /// a 16px em). Other units defer to the shared helper.
    /// </summary>
    private double ToPxRelative(CssLength len, double fontSize)
        => len.Unit switch
        {
            CssLengthUnit.Em => len.Value * fontSize,
            CssLengthUnit.Rem => len.Value * 16d,
            _ => Block.BlockLayout.ToPx(len, _viewport),
        };

    /// <summary>How an over-long word may break inside itself.</summary>
    private enum BreakMode { None, Anywhere }

    /// <summary>
    /// Resolve the in-word emergency break policy from <c>overflow-wrap</c> and
    /// <c>word-break</c> (CSS Text 3 §6.2 / §6.4). <c>overflow-wrap: anywhere |
    /// break-word</c> and <c>word-break: break-all</c> all permit breaking a
    /// word that would otherwise overflow; <c>word-break: keep-all</c> and the
    /// initial values do not.
    /// </summary>
    private static BreakMode ResolveBreakMode(ComputedStyle? style)
    {
        if (style is null)
        {
            return BreakMode.None;
        }

        if (style.Get(PropertyId.OverflowWrap) is CssKeyword { Name: "anywhere" or "break-word" })
        {
            return BreakMode.Anywhere;
        }

        if (style.Get(PropertyId.WordBreak) is CssKeyword { Name: "break-all" })
        {
            return BreakMode.Anywhere;
        }

        return BreakMode.None;
    }

    /// <summary>
    /// Resolved CSS Text white-space behaviour. Bundles the three axes the
    /// inline engine needs: whether to collapse runs of spaces, whether to
    /// preserve segment-breaking newlines, and whether soft-wrapping is allowed.
    /// Built from the legacy <c>white-space</c> keyword or the modern
    /// <c>white-space-collapse</c> + <c>text-wrap</c> longhands (CSS Text 4 §3).
    /// </summary>
    private readonly record struct WhiteSpaceMode(bool CollapseSpaces, bool PreserveNewlines, bool Wrap)
    {
        public bool PreserveSpaces => !CollapseSpaces;

        public static WhiteSpaceMode Resolve(ComputedStyle? style)
        {
            if (style is null)
            {
                return new WhiteSpaceMode(CollapseSpaces: true, PreserveNewlines: false, Wrap: true);
            }

            // Legacy shorthand keyword first — it is what authors and the UA
            // sheet (e.g. `pre { white-space: pre }`) overwhelmingly set.
            if (style.Get(PropertyId.WhiteSpace) is CssKeyword { Name: var ws } && ws != "normal")
            {
                switch (ws)
                {
                    case "nowrap":
                        return new WhiteSpaceMode(CollapseSpaces: true, PreserveNewlines: false, Wrap: false);
                    case "pre":
                        return new WhiteSpaceMode(CollapseSpaces: false, PreserveNewlines: true, Wrap: false);
                    case "pre-wrap":
                        return new WhiteSpaceMode(CollapseSpaces: false, PreserveNewlines: true, Wrap: true);
                    case "pre-line":
                        return new WhiteSpaceMode(CollapseSpaces: true, PreserveNewlines: true, Wrap: true);
                }
            }

            // Modern longhands: white-space-collapse {collapse|preserve|
            // preserve-breaks|...} + text-wrap {wrap|nowrap|...}.
            var collapseKw = (style.Get(PropertyId.WhiteSpaceCollapse) as CssKeyword)?.Name ?? "collapse";
            var wrapKw = (style.Get(PropertyId.TextWrap) as CssKeyword)?.Name ?? "wrap";

            var collapse = collapseKw is "collapse" or "preserve-breaks";
            var preserveNewlines = collapseKw is "preserve" or "preserve-breaks";
            var wrap = wrapKw != "nowrap";
            return new WhiteSpaceMode(collapse, preserveNewlines, wrap);
        }
    }

    /// <summary>CSS Text 3 §2.1 — <c>text-transform</c> kinds.</summary>
    private static class TextTransformer
    {
        public enum Kind { None, Uppercase, Lowercase, Capitalize }

        public static Kind Resolve(ComputedStyle? style)
            => style?.Get(PropertyId.TextTransform) is CssKeyword k
                ? k.Name switch
                {
                    "uppercase" => Kind.Uppercase,
                    "lowercase" => Kind.Lowercase,
                    "capitalize" => Kind.Capitalize,
                    _ => Kind.None,
                }
                : Kind.None;

        public static string Apply(string text, Kind kind)
        {
            if (kind == Kind.None || text.Length == 0)
            {
                return text;
            }

            switch (kind)
            {
                case Kind.Uppercase:
                    return text.ToUpperInvariant();
                case Kind.Lowercase:
                    return text.ToLowerInvariant();
                case Kind.Capitalize:
                    var chars = text.ToCharArray();
                    var atWordStart = true;
                    for (var i = 0; i < chars.Length; i++)
                    {
                        if (char.IsWhiteSpace(chars[i]))
                        {
                            atWordStart = true;
                        }
                        else
                        {
                            if (atWordStart && char.IsLetter(chars[i]))
                            {
                                chars[i] = char.ToUpperInvariant(chars[i]);
                            }

                            atWordStart = false;
                        }
                    }
                    return new string(chars);
                default:
                    return text;
            }
        }
    }

    /// <summary>
    /// Place an inline-block box atomically on the current line. Resolves its
    /// own box model, lays its children out within its content box, and sets
    /// its <see cref="Box.Box.Frame"/> in the enclosing anonymous block's
    /// content-coordinate space. The painter walks the box like any other
    /// block: background + border first, then children translated by our
    /// padding+border edge.
    /// </summary>
    /// <remarks>
    /// Per CSS 2.1 §10.1, an inline-block establishes a new block formatting
    /// context. We dispatch on the child mix:
    /// <list type="bullet">
    ///   <item>Any block-level child → nested <see cref="Block.BlockLayout"/>
    ///   pass (true BFC sub-pass, with anonymous-block wrapping already done
    ///   by <see cref="Tree.BoxTreeBuilder"/>).</item>
    ///   <item>Only inline-level non-text children (nested inline-blocks,
    ///   images, <c>&lt;br&gt;</c>, spans) → a recursive inline-formatting
    ///   sub-pass via <see cref="Layout(Box.Box, double)"/>, sized via a max-content
    ///   approximation so the inline-block shrinks-to-fit (CSS 2.1 §10.3.5).</item>
    ///   <item>Only text children → the lightweight
    ///   <see cref="LayoutAtomicContent"/> path that measures glyph widths
    ///   directly.</item>
    /// </list>
    /// </remarks>
    private void LayoutAtomic(
        InlineBox box,
        double availableWidth,
        List<InlineBox> placedAtomics,
        ref double cursorX,
        ref double cursorY,
        ref double currentLineHeight)
    {
        ResolveAtomicBoxModel(box, availableWidth);

        var fontSize = ResolveFontSize(box.Style);
        var spec = ResolveFontSpec(box.Style);
        var lineHeight = ResolveLineHeight(box.Style, fontSize, spec);

        double contentWidth;
        double contentHeight;

        if (Block.BlockLayout.IsFlexContainer(box.Style))
        {
            // display:inline-flex — an atomic inline whose contents lay out as
            // a flex container, shrunk-to-fit. Mirror the two-pass approach of
            // the BFC path: lay flex out at "infinite" main size (grow=0 keeps
            // items at their flex base size) to read the max-content width from
            // the placed item frames, clamp to the space left on the line, then
            // re-run flex at that width so descendants settle.
            var viewport = _viewport ?? new Size(availableWidth, 0);
            var explicitInner = ResolveLength(box.Style, PropertyId.Width, availableWidth);
            double subWidth;
            if (explicitInner is { } iw)
            {
                subWidth = iw;
            }
            else
            {
                const double measureWidth = 1_000_000d;
                var measureBlock = new Block.BlockLayout(_measurer, viewport);
                var measureFlex = new Starling.Layout.Flex.FlexLayout(measureBlock, viewport);
                measureFlex.Layout(box, measureWidth, null);
                // Flex items are direct children placed along the main axis;
                // their frames carry the used main extent. (MeasureUsedWidth
                // can't be reused here: item text fragments are positioned
                // relative to each item, not the flex container.)
                double maxContent = 0;
                foreach (var item in box.Children)
                {
                    maxContent = Math.Max(maxContent, item.Frame.X + item.Frame.Width);
                }

                var available = Math.Max(0, availableWidth - cursorX);
                subWidth = Math.Min(maxContent, available);
            }

            var block = new Block.BlockLayout(_measurer, viewport);
            var flex = new Starling.Layout.Flex.FlexLayout(block, viewport);
            contentHeight = flex.Layout(box, subWidth, null);
            contentWidth = subWidth;
        }
        else if (HasBlockLevelChild(box))
        {
            // Inline-block with mixed/block children: run a BFC sub-pass.
            //
            // CSS 2.1 §10.3.5 shrink-to-fit:
            //     used-width = min(max(min-content, available), max-content).
            //
            // The hard problem here: block children sized at the available
            // width inflate their *frame* to that width even when their
            // content is narrow. A naive single-pass therefore paints the
            // block child's background across the whole row. We approximate
            // max-content with a measurement pass at "infinite" width, walk
            // text/atomic descendants to find the rightmost edge they
            // actually used (block frames after this pass are at the wide
            // width — we deliberately ignore them), then re-layout at the
            // shrunk width so block descendants' frames match too.
            //
            // Cost: two BFC passes per inline-block-with-block-child. At
            // this stage of the project that's fine; real engines compute
            // intrinsic sizes recursively without an extra placement pass.
            var viewport = _viewport ?? new Size(availableWidth, 0);
            var explicitInner = ResolveLength(box.Style, PropertyId.Width, availableWidth);

            double subWidth;
            if (explicitInner is { } iw)
            {
                subWidth = iw;
            }
            else
            {
                const double measureWidth = 1_000_000d;
                var measureLayout = new Block.BlockLayout(_measurer, viewport);
                measureLayout.LayoutChildren(box, measureWidth, measure: true);
                var maxContent = MeasureUsedWidth(box);
                var available = Math.Max(0, availableWidth - cursorX);
                subWidth = Math.Min(maxContent, available);
            }

            var sub = new Block.BlockLayout(_measurer, viewport);
            var consumed = sub.LayoutChildren(box, subWidth);
            contentWidth = subWidth;
            contentHeight = consumed;
        }
        else if (HasNonTextInlineChild(box))
        {
            // Inline-block whose children are inline-level non-text (nested
            // inline-blocks, <br>, <img>, spans). The text-only path would
            // silently drop these. Run an IFC sub-pass shrunk-to-fit via the
            // same two-pass approach used for BFC sub-pass above: measure at
            // huge width to find max-content, then re-lay at the constrained
            // width so atomic-inline frames sit at their final X.
            //
            // CSS 2.1 §10.3.5: used-width = min(max(min-content, available), max-content).
            var explicitInner = ResolveLength(box.Style, PropertyId.Width, availableWidth);
            double subWidth;
            if (explicitInner is { } iw)
            {
                subWidth = iw;
            }
            else
            {
                const double measureWidth = 1_000_000d;
                this.Layout(box, measureWidth, measure: true);
                var maxContent = MeasureUsedWidth(box);
                var available = Math.Max(0, availableWidth - cursorX);
                subWidth = Math.Min(maxContent, available);
            }

            var consumed = this.Layout(box, subWidth);
            contentHeight = consumed;
            contentWidth = subWidth;
        }
        else
        {
            contentWidth = LayoutAtomicContent(box, fontSize, spec);
            contentHeight = lineHeight;
        }

        // CSS width: prefer an explicit value if the cascade gave us one.
        // Percentages resolve against the enclosing block's content width.
        var explicitWidth = ResolveLength(box.Style, PropertyId.Width, availableWidth);
        if (explicitWidth is { } w)
        {
            contentWidth = w;
        }

        var explicitHeight = ResolveLength(box.Style, PropertyId.Height, lineHeight);
        if (explicitHeight is { } h)
        {
            contentHeight = h;
        }

        var outerWidth = contentWidth + box.Padding.Horizontal + box.Border.Horizontal;
        var outerHeight = contentHeight + box.Padding.Vertical + box.Border.Vertical;

        // Wrap to a new line if this atomic doesn't fit and the line already
        // has content. Atomic items never split.
        if (cursorX > 0 && cursorX + outerWidth + box.Margin.Horizontal > availableWidth)
        {
            cursorY += currentLineHeight;
            cursorX = 0;
            currentLineHeight = outerHeight + box.Margin.Vertical;
        }
        else
        {
            currentLineHeight = Math.Max(currentLineHeight, outerHeight + box.Margin.Vertical);
        }

        box.Frame = new Rect(
            cursorX + box.Margin.Left,
            cursorY + box.Margin.Top,
            outerWidth,
            outerHeight);
        placedAtomics.Add(box);
        cursorX += outerWidth + box.Margin.Horizontal;
    }

    /// <summary>
    /// Measure and place the inline-block's direct text children, returning
    /// the natural content width. The synthesized TextBox for an
    /// <c>&lt;input&gt;</c>'s value/placeholder lives here, as does a
    /// <c>&lt;button&gt;</c>'s label text.
    /// </summary>
    private double LayoutAtomicContent(InlineBox box, double fontSize, FontSpec spec)
    {
        var baseline = _measurer.Baseline(fontSize, spec);
        double width = 0;
        double height = 0;

        foreach (var child in box.Children)
        {
            if (child is TextBox tb)
            {
                tb.Fragments.Clear();
                var text = NormalizeWhitespace(tb.Text);
                if (text.Length == 0)
                {
                    continue;
                }

                var fragWidth = _measurer.MeasureWidth(text, fontSize, spec);
                tb.Fragments.Add(new TextFragment(text, width, 0, fragWidth, fontSize, baseline));
                width += fragWidth;
                height = Math.Max(height, fontSize);
            }
        }

        // <input size=N>: HTML attribute that hints at an N-character width
        // (used for text-like input types). Per HTML, the default size is 20
        // and the rendered width is N "average character widths" of the font.
        // We approximate "average character width" with the advance of the
        // "0" glyph (a fair proxy for the CSS `ch` unit), which keeps the
        // sizing in line with what browsers settle on for proportional fonts.
        var cols = ResolveInputSizeCols(box.Element);
        if (cols > 0)
        {
            var charWidth = _measurer.MeasureWidth("0", fontSize, spec);
            if (charWidth <= 0)
            {
                charWidth = fontSize * 0.5;
            }

            var minWidth = cols * charWidth;
            if (minWidth > width)
            {
                width = minWidth;
            }
        }

        return width;
    }

    /// <summary>
    /// Resolve the effective character-column width for an <c>&lt;input&gt;</c>'s
    /// <c>size</c> attribute. Returns 0 for elements that don't honor <c>size</c>
    /// (non-input, or input types like checkbox/submit/file) so callers don't
    /// inflate their width. Text-like input types default to 20 columns when
    /// the attribute is missing, matching the HTML spec.
    /// </summary>
    /// <summary>
    /// Whether an empty element still reserves one line box of height. True for
    /// editable text fields — text-family <c>&lt;input&gt;</c>s and
    /// <c>&lt;textarea&gt;</c> — which always show a text line (and a caret)
    /// even with no value or placeholder, so their box must not collapse to
    /// padding+border. Everything else collapses to zero height when empty.
    /// </summary>
    private static bool ReservesEmptyTextLine(Element? element)
    {
        if (element is null)
        {
            return false;
        }

        if (string.Equals(element.LocalName, "textarea", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ResolveInputSizeCols(element) > 0;
    }

    private static int ResolveInputSizeCols(Element? element)
    {
        if (element is null)
        {
            return 0;
        }

        if (!string.Equals(element.LocalName, "input", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var sizeAttr = element.GetAttribute("size");
        if (!string.IsNullOrEmpty(sizeAttr) &&
            int.TryParse(sizeAttr, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var explicitCols) && explicitCols > 0)
        {
            return explicitCols;
        }

        // Default size=20 only for the "text"-family input types per the
        // HTML spec. Buttons, checkboxes, radios, etc. size to their content
        // (label/glyph) instead.
        var type = (element.GetAttribute("type") ?? "text").Trim().ToLowerInvariant();
        return type switch
        {
            "text" or "search" or "tel" or "url" or "email" or "password" or "" => 20,
            _ => 0,
        };
    }

    private void ResolveAtomicBoxModel(InlineBox box, double containerWidth)
    {
        box.Margin = new Edges(
            ResolveLength(box.Style, PropertyId.MarginTop, containerWidth) ?? 0,
            ResolveLength(box.Style, PropertyId.MarginRight, containerWidth) ?? 0,
            ResolveLength(box.Style, PropertyId.MarginBottom, containerWidth) ?? 0,
            ResolveLength(box.Style, PropertyId.MarginLeft, containerWidth) ?? 0);

        box.Padding = new Edges(
            ResolveLength(box.Style, PropertyId.PaddingTop, containerWidth) ?? 0,
            ResolveLength(box.Style, PropertyId.PaddingRight, containerWidth) ?? 0,
            ResolveLength(box.Style, PropertyId.PaddingBottom, containerWidth) ?? 0,
            ResolveLength(box.Style, PropertyId.PaddingLeft, containerWidth) ?? 0);

        box.Border = new Edges(
            ResolveBorderWidth(box.Style, PropertyId.BorderTopWidth, PropertyId.BorderTopStyle),
            ResolveBorderWidth(box.Style, PropertyId.BorderRightWidth, PropertyId.BorderRightStyle),
            ResolveBorderWidth(box.Style, PropertyId.BorderBottomWidth, PropertyId.BorderBottomStyle),
            ResolveBorderWidth(box.Style, PropertyId.BorderLeftWidth, PropertyId.BorderLeftStyle));
    }

    private double? ResolveLength(ComputedStyle? style, PropertyId property, double percentageBasis)
    {
        if (style is null)
        {
            return null;
        }

        return style.Get(property) switch
        {
            CssLength len => Block.BlockLayout.ToPx(len, _viewport),
            CssPercentage pct => percentageBasis * pct.Value / 100d,
            CssNumber n => n.Value,
            _ => null,
        };
    }

    private double ResolveBorderWidth(ComputedStyle? style, PropertyId widthId, PropertyId styleId)
    {
        if (style is null)
        {
            return 0;
        }

        if (style.Get(styleId) is CssKeyword k && k.Name == "none")
        {
            return 0;
        }

        return style.Get(widthId) is CssLength len ? Block.BlockLayout.ToPx(len, _viewport) : 0;
    }

    private void LayoutImage(
        ImageBox image,
        double availableWidth,
        List<ImageBox> placedImages,
        ref double cursorX,
        ref double cursorY,
        ref double currentLineHeight)
    {
        var (width, height) = ResolveReplacedSize(image, availableWidth);

        // Wrap to the next line if this image won't fit and the current line
        // already has content. An image wider than the container stays on its
        // own line and overflows — matching the simple v1 "no overflow break"
        // behaviour of text.
        if (cursorX > 0 && cursorX + width > availableWidth)
        {
            cursorY += currentLineHeight;
            cursorX = 0;
            currentLineHeight = height;
        }
        else
        {
            currentLineHeight = Math.Max(currentLineHeight, height);
        }

        // v1 places the image top-aligned within its line. Baseline alignment
        // ("bottom of replaced element sits on text baseline") is a follow-up.
        image.Frame = new Rect(cursorX, cursorY, width, height);
        placedImages.Add(image);
        cursorX += width;
    }

    /// <summary>
    /// CSS 2.1 §10.3.2 (used dimensions of a replaced element) + §10.4
    /// (min-/max-width/height clamps). Resolves the box's computed
    /// <c>width</c>/<c>height</c>/<c>min-*</c>/<c>max-*</c> against the
    /// available inline-axis width, preserving the intrinsic aspect ratio
    /// when one axis is <c>auto</c>. Height percentages resolve against an
    /// unknown containing-block height in inline context, so they collapse
    /// to <c>auto</c> here (matches the common Chromium behaviour).
    /// </summary>
    internal (double Width, double Height) ResolveReplacedSize(ImageBox image, double availableWidth)
    {
        var style = image.Style;
        var iw = image.IntrinsicWidth > 0 ? image.IntrinsicWidth : 1;
        var ih = image.IntrinsicHeight > 0 ? image.IntrinsicHeight : 1;
        var ratio = iw / ih;

        // css-sizing-4 §5.1 — the aspect-ratio property can override the
        // natural ratio: a bare <ratio> always wins; `auto && <ratio>` defers
        // to the natural ratio when the replaced element has one.
        var cssRatioOverrides = false;
        if (Block.IntrinsicSizes.TryGetAspectRatio(style, out var cssRatio, out var ratioHasAuto))
        {
            var hasNaturalRatio = image.IntrinsicWidth > 0 && image.IntrinsicHeight > 0;
            if (!ratioHasAuto || !hasNaturalRatio)
            {
                ratio = cssRatio;
                cssRatioOverrides = true;
            }
        }

        var specW = Block.BlockLayout.ResolveLength(style, PropertyId.Width, availableWidth, _viewport, allowAuto: true);
        // Height percentages need a known containing-block height; in inline
        // context we don't have one, so leave height as auto when authored
        // as a percentage. Lengths still resolve normally.
        var specH = style?.Get(PropertyId.Height) is CssPercentage
            ? (double?)null
            : Block.BlockLayout.ResolveLength(style, PropertyId.Height, 0, _viewport, allowAuto: true);

        double w, h;
        if (specW.HasValue && specH.HasValue)
        {
            w = specW.Value;
            h = specH.Value;
        }
        else if (specW.HasValue)
        {
            w = specW.Value;
            h = w / ratio;
        }
        else if (specH.HasValue)
        {
            h = specH.Value;
            w = h * ratio;
        }
        else if (image.IntrinsicSizeIsRatioOnly)
        {
            // Ratio-only replaced box (e.g. a viewBox-only <svg>) with both axes
            // auto: use the viewBox size but never overflow the available inline
            // size — a `viewBox="0 0 24 24"` icon keeps its 24px size, while a
            // `viewBox="0 -960 960 960"` Material Symbols icon shrinks to fit its
            // (e.g. 24px) box instead of rendering at 960px. Height follows the
            // ratio so the aspect is preserved when clamped.
            w = Math.Min(iw, availableWidth);
            h = w / ratio;
        }
        else
        {
            // Both axes auto: natural width, and the height follows the
            // (possibly css-overridden) preferred ratio.
            w = iw;
            h = cssRatioOverrides ? w / ratio : ih;
        }

        // §10.4 min/max constraints. When the constrained axis was derived
        // from the other axis (auto), keep aspect ratio while clamping; when
        // it was authored explicitly, clamp only that axis.
        var maxW = Block.BlockLayout.ResolveLength(style, PropertyId.MaxWidth, availableWidth, _viewport);
        if (style?.Get(PropertyId.MaxWidth) is CssKeyword mwk && mwk.Name == "none")
        {
            maxW = null;
        }

        var minW = Block.BlockLayout.ResolveLength(style, PropertyId.MinWidth, availableWidth, _viewport) ?? 0;

        var maxH = Block.BlockLayout.ResolveLength(style, PropertyId.MaxHeight, 0, _viewport);
        if (style?.Get(PropertyId.MaxHeight) is CssKeyword mhk && mhk.Name == "none")
        {
            maxH = null;
        }
        // Height percentages on min/max-height resolve against an unknown
        // containing-block height; ignore them in inline context.
        if (style?.Get(PropertyId.MaxHeight) is CssPercentage)
        {
            maxH = null;
        }

        var minHRaw = style?.Get(PropertyId.MinHeight) is CssPercentage
            ? (double?)null
            : Block.BlockLayout.ResolveLength(style, PropertyId.MinHeight, 0, _viewport);
        var minH = minHRaw ?? 0;

        if (maxW.HasValue && w > maxW.Value)
        {
            var scale = maxW.Value / w;
            w = maxW.Value;
            if (!specH.HasValue)
            {
                h *= scale;
            }
        }
        if (w < minW)
        {
            var scale = w > 0 ? minW / w : 1;
            w = minW;
            if (!specH.HasValue)
            {
                h *= scale;
            }
        }
        if (maxH.HasValue && h > maxH.Value)
        {
            var scale = maxH.Value / h;
            h = maxH.Value;
            if (!specW.HasValue)
            {
                w *= scale;
            }
        }
        if (h < minH)
        {
            var scale = h > 0 ? minH / h : 1;
            h = minH;
            if (!specW.HasValue)
            {
                w *= scale;
            }
        }

        return (w, h);
    }

    private static void AddFragment(TextBox owner, List<(TextBox Owner, int Index)> fragments, TextFragment fragment)
    {
        owner.Fragments.Add(fragment);
        fragments.Add((owner, owner.Fragments.Count - 1));
    }

    private static void AlignLines(
        ComputedStyle? style,
        double availableWidth,
        List<(TextBox Owner, int Index)> fragments,
        List<ImageBox> placedImages,
        List<InlineBox> placedAtomics)
    {
        var align = style?.Get(PropertyId.TextAlign) is CssKeyword keyword
            ? keyword.Name.ToLowerInvariant()
            : "start";
        if (align is not ("center" or "right" or "end") ||
            (fragments.Count == 0 && placedImages.Count == 0 && placedAtomics.Count == 0))
        {
            return;
        }

        // Group fragments, images, and atomic inline-blocks by their Y so
        // per-line alignment shifts apply uniformly to everything on the line.
        var lines = new Dictionary<double, (List<(TextBox Owner, int Index)> Texts, List<ImageBox> Images, List<InlineBox> Atomics, double RightEdge)>();
        foreach (var item in fragments)
        {
            var frag = item.Owner.Fragments[item.Index];
            var key = frag.Y;
            if (!lines.TryGetValue(key, out var line))
            {
                line = ([], [], [], 0);
            }

            line.Texts.Add(item);
            line.RightEdge = Math.Max(line.RightEdge, frag.X + frag.Width);
            lines[key] = line;
        }
        foreach (var image in placedImages)
        {
            var key = image.Frame.Y;
            if (!lines.TryGetValue(key, out var line))
            {
                line = ([], [], [], 0);
            }

            line.Images.Add(image);
            line.RightEdge = Math.Max(line.RightEdge, image.Frame.X + image.Frame.Width);
            lines[key] = line;
        }
        foreach (var atomic in placedAtomics)
        {
            var key = atomic.Frame.Y;
            if (!lines.TryGetValue(key, out var line))
            {
                line = ([], [], [], 0);
            }

            line.Atomics.Add(atomic);
            line.RightEdge = Math.Max(line.RightEdge, atomic.Frame.X + atomic.Frame.Width);
            lines[key] = line;
        }

        foreach (var (_, line) in lines)
        {
            var offset = align == "center"
                ? Math.Max(0, (availableWidth - line.RightEdge) / 2d)
                : Math.Max(0, availableWidth - line.RightEdge);
            if (offset == 0)
            {
                continue;
            }

            foreach (var item in line.Texts)
            {
                var fragment = item.Owner.Fragments[item.Index];
                item.Owner.Fragments[item.Index] = fragment with { X = fragment.X + offset };
            }
            foreach (var image in line.Images)
            {
                image.Frame = image.Frame with { X = image.Frame.X + offset };
            }
            foreach (var atomic in line.Atomics)
            {
                atomic.Frame = atomic.Frame with { X = atomic.Frame.X + offset };
            }
        }
    }

    // ---- text-overflow: ellipsis + -webkit-line-clamp ------------------------

    private const string EllipsisText = "\u2026";

    /// <summary>
    /// Post-finalization line surgery (CSS UI 4 §7 / CSS Overflow 4 §5). Two
    /// behaviours share the machinery:
    /// <list type="bullet">
    ///   <item><c>text-overflow: ellipsis</c> on a block whose
    ///   <c>overflow-x</c> is not <c>visible</c> and whose white-space
    ///   prevents wrapping: every line whose inline content overflows the
    ///   content box is truncated at the content edge and a single U+2026
    ///   glyph that fits inside the box is appended.</item>
    ///   <item>the legacy clamp pattern — <c>display:-webkit-box</c> +
    ///   <c>-webkit-box-orient:vertical</c> + <c>-webkit-line-clamp:N</c> —
    ///   caps the box at N line boxes, drops every later line's fragments
    ///   (so paint and overflow geometry exclude them), and ellipsizes
    ///   line N.</item>
    /// </list>
    /// Cold path: the common case exits on two style reads. Bidi/RTL ellipsis
    /// placement is out of scope — lines are truncated at their right edge.
    /// </summary>
    private double ApplyEllipsisAndClamp(
        ComputedStyle blockStyle,
        double availableWidth,
        double contentHeight,
        List<(TextBox Owner, int Index)> fragments,
        List<ImageBox> placedImages,
        List<InlineBox> placedAtomics,
        bool measure)
    {
        var clampLines = ResolveLineClamp(blockStyle);
        var ellipsisActive = clampLines == 0 && ResolveEllipsisActive(blockStyle);
        if (clampLines == 0 && !ellipsisActive)
        {
            return contentHeight;
        }

        // Distinct line tops, ascending. Every fragment on a line carries the
        // exact cursorY it was placed at, so exact-equality grouping is sound
        // (AlignLines groups the same way).
        var lineTops = new List<double>();
        foreach (var (owner, index) in fragments)
        {
            var y = owner.Fragments[index].Y;
            if (!lineTops.Contains(y))
            {
                lineTops.Add(y);
            }
        }
        lineTops.Sort();

        var dropped = new HashSet<(TextBox Owner, int Index)>();
        var clampCut = double.PositiveInfinity;
        var forcedLineTop = double.NaN;

        if (clampLines > 0 && lineTops.Count > clampLines)
        {
            // Lines stack contiguously, so the top of line N+1 is the bottom
            // of line N — the clamped content height.
            clampCut = lineTops[clampLines];
            forcedLineTop = lineTops[clampLines - 1];
            foreach (var fragRef in fragments)
            {
                if (fragRef.Owner.Fragments[fragRef.Index].Y >= clampCut)
                {
                    dropped.Add(fragRef);
                }
            }
            // Replaced / atomic inline boxes on clamped-away lines: zero the
            // frame so paint and overflow geometry exclude them.
            foreach (var image in placedImages)
            {
                if (image.Frame.Y >= clampCut - 0.0001)
                {
                    image.Frame = new Rect(image.Frame.X, clampCut, 0, 0);
                }
            }
            foreach (var atomic in placedAtomics)
            {
                if (atomic.Frame.Y >= clampCut - 0.0001)
                {
                    atomic.Frame = new Rect(atomic.Frame.X, clampCut, 0, 0);
                }
            }
            contentHeight = clampCut;
        }

        // Ellipsizing must never affect intrinsic sizing (CSS UI 4 §7.1.1):
        // a min-content probe at width 0 would otherwise drop every fragment
        // and report the ellipsis advance as the text's minimum width. The
        // clamp's height capping above IS real content height, so measure
        // passes keep it; only the fragment surgery is paint-affecting.
        if (measure)
        {
            return contentHeight;
        }

        const double overflowTolerance = 0.05;
        for (var i = 0; i < lineTops.Count; i++)
        {
            var top = lineTops[i];
            if (top >= clampCut)
            {
                continue; // line dropped by the clamp
            }

            var force = top.Equals(forcedLineTop); // the clamp's last visible line
            if (!force)
            {
                if (!ellipsisActive)
                {
                    continue;
                }
                // Ellipsize only lines that actually overflow the content box.
                double rightmost = 0;
                foreach (var (owner, index) in fragments)
                {
                    if (dropped.Contains((owner, index)))
                    {
                        continue;
                    }

                    var frag = owner.Fragments[index];
                    if (frag.Y != top)
                    {
                        continue;
                    }

                    rightmost = Math.Max(rightmost, frag.X + frag.Width);
                }
                if (rightmost <= availableWidth + overflowTolerance)
                {
                    continue;
                }
            }
            EllipsizeLine(blockStyle, top, availableWidth, fragments, dropped);
        }

        RemoveDroppedFragments(fragments, dropped);
        return contentHeight;
    }

    /// <summary>
    /// Truncate one finished line so its content plus a single U+2026 glyph
    /// fits inside the content box, then append that glyph as a regular
    /// fragment (so paint draws it like any other text). The ellipsis is
    /// measured with the block's own font per CSS UI 4 §7.1.1; it is attached
    /// to the truncated run's TextBox so it paints with that run's color.
    /// </summary>
    private void EllipsizeLine(
        ComputedStyle blockStyle,
        double lineTop,
        double availableWidth,
        List<(TextBox Owner, int Index)> fragments,
        HashSet<(TextBox Owner, int Index)> dropped)
    {
        // The line's surviving fragments in X order. The master list is
        // append-ordered (X-ascending within a line); sort defensively.
        var line = new List<(TextBox Owner, int Index)>();
        foreach (var fragRef in fragments)
        {
            if (dropped.Contains(fragRef))
            {
                continue;
            }

            if (fragRef.Owner.Fragments[fragRef.Index].Y.Equals(lineTop))
            {
                line.Add(fragRef);
            }
        }
        if (line.Count == 0)
        {
            return;
        }

        line.Sort(static (a, b) =>
            a.Owner.Fragments[a.Index].X.CompareTo(b.Owner.Fragments[b.Index].X));

        var blockFontSize = ResolveFontSize(blockStyle);
        var blockSpec = ResolveFontSpec(blockStyle);
        var ellipsisShaped = _measurer.Shape(EllipsisText, blockFontSize, blockSpec);
        var ellipsisWidth = ellipsisShaped.Advance;

        // Content must end by `limit` so the ellipsis itself stays inside the
        // box. If the box is narrower than the glyph the ellipsis is placed at
        // the line origin and overflow clipping crops it (CSS UI 4 §7.1.1).
        var limit = Math.Max(0, availableWidth - ellipsisWidth);
        const double tolerance = 0.05;

        double end = 0;
        var endSet = false;
        var attachAt = -1; // index in `line` of the last kept fragment

        for (var i = 0; i < line.Count; i++)
        {
            var (owner, index) = line[i];
            var frag = owner.Fragments[index];
            if (frag.X + frag.Width <= limit + tolerance)
            {
                end = frag.X + frag.Width;
                endSet = true;
                attachAt = i;
                continue;
            }

            // Boundary fragment: keep the longest prefix whose advance fits
            // between the fragment start and the limit; drop the remainder and
            // every later fragment on the line.
            var room = limit - frag.X;
            var keptChars = 0;
            if (room > tolerance)
            {
                var runStyle = owner.Style ?? blockStyle;
                var runFontSize = ResolveFontSize(runStyle);
                var runSpec = ResolveFontSpec(runStyle);
                double acc = 0;
                while (keptChars < frag.Text.Length)
                {
                    var w = _measurer.MeasureWidth(frag.Text[keptChars].ToString(), runFontSize, runSpec);
                    if (acc + w > room + tolerance)
                    {
                        break;
                    }

                    acc += w;
                    keptChars++;
                }
                if (keptChars > 0)
                {
                    var slice = frag.Text[..keptChars];
                    var shaped = _measurer.Shape(slice, runFontSize, runSpec);
                    // Re-shaping the slice can come out a hair wider than the
                    // per-char sum (kerning); back off until it fits.
                    while (keptChars > 1 && shaped.Advance > room + tolerance)
                    {
                        keptChars--;
                        slice = frag.Text[..keptChars];
                        shaped = _measurer.Shape(slice, runFontSize, runSpec);
                    }
                    owner.Fragments[index] = frag with { Text = slice, Width = shaped.Advance, Shaped = shaped };
                    end = frag.X + shaped.Advance;
                    endSet = true;
                    attachAt = i;
                }
            }
            if (keptChars == 0)
            {
                dropped.Add(line[i]);
            }

            for (var j = i + 1; j < line.Count; j++)
            {
                dropped.Add(line[j]);
            }

            break;
        }

        // Trim kept trailing whitespace so the ellipsis hugs the text.
        while (attachAt >= 0)
        {
            var (owner, index) = line[attachAt];
            if (!IsWhitespaceText(owner.Fragments[index].Text))
            {
                break;
            }

            dropped.Add(line[attachAt]);
            attachAt--;
            while (attachAt >= 0 && dropped.Contains(line[attachAt]))
            {
                attachAt--;
            }

            if (attachAt >= 0)
            {
                var prev = line[attachAt].Owner.Fragments[line[attachAt].Index];
                end = prev.X + prev.Width;
            }
            else
            {
                endSet = false;
            }
        }

        // Append the U+2026 fragment after the kept content (or at the line
        // origin when nothing survived).
        var anchor = attachAt >= 0 ? line[attachAt] : line[0];
        var anchorFrag = anchor.Owner.Fragments[anchor.Index];
        var x = endSet ? end : anchorFrag.X;
        anchor.Owner.Fragments.Add(new TextFragment(
            EllipsisText, x, lineTop, ellipsisWidth, anchorFrag.Height, anchorFrag.Baseline, ellipsisShaped));
        fragments.Add((anchor.Owner, anchor.Owner.Fragments.Count - 1));
    }

    private static bool IsWhitespaceText(string text)
    {
        foreach (var c in text)
        {
            if (c is not (' ' or '\t'))
            {
                return false;
            }
        }
        return text.Length > 0;
    }

    /// <summary>
    /// Physically remove dropped fragments from their owners and re-index the
    /// master list (surgery invalidated its (Owner, Index) pairs) so
    /// <see cref="AlignLines"/> sees a coherent view.
    /// </summary>
    private static void RemoveDroppedFragments(
        List<(TextBox Owner, int Index)> fragments,
        HashSet<(TextBox Owner, int Index)> dropped)
    {
        if (dropped.Count == 0)
        {
            return;
        }

        // Distinct owners in first-appearance order.
        var owners = new List<TextBox>();
        var seen = new HashSet<TextBox>();
        foreach (var (owner, _) in fragments)
        {
            if (seen.Add(owner))
            {
                owners.Add(owner);
            }
        }

        // Per-owner descending-index removal keeps earlier indices stable.
        var perOwner = new Dictionary<TextBox, List<int>>();
        foreach (var (owner, index) in dropped)
        {
            if (!perOwner.TryGetValue(owner, out var list))
            {
                list = [];
                perOwner[owner] = list;
            }
            list.Add(index);
        }
        foreach (var (owner, indices) in perOwner)
        {
            indices.Sort();
            for (var i = indices.Count - 1; i >= 0; i--)
            {
                owner.Fragments.RemoveAt(indices[i]);
            }
        }

        fragments.Clear();
        foreach (var owner in owners)
        {
            for (var i = 0; i < owner.Fragments.Count; i++)
            {
                fragments.Add((owner, i));
            }
        }
    }

    /// <summary>
    /// The classic ellipsis combo (CSS UI 4 §7): <c>text-overflow:ellipsis</c>
    /// + <c>overflow-x</c> not <c>visible</c> + white-space that prevents
    /// wrapping. Only the single-keyword form is recognized (the two-value /
    /// &lt;string&gt; forms are not supported).
    /// </summary>
    private static bool ResolveEllipsisActive(ComputedStyle style)
    {
        if (style.Get(PropertyId.TextOverflow) is not CssKeyword { Name: "ellipsis" })
        {
            return false;
        }

        if (style.Get(PropertyId.OverflowX) is not CssKeyword overflowX
            || overflowX.Name == "visible")
        {
            return false;
        }

        return !WhiteSpaceMode.Resolve(style).Wrap;
    }

    /// <summary>
    /// The legacy clamp trio: <c>display:-webkit-box</c> +
    /// <c>-webkit-box-orient:vertical</c> + <c>-webkit-line-clamp:N</c>.
    /// Returns N (≥ 1) when all three are present, else 0.
    /// </summary>
    private static int ResolveLineClamp(ComputedStyle style)
    {
        if (style.Get(PropertyId.LineClamp) is not CssNumber clamp || clamp.Value < 1)
        {
            return 0;
        }

        if (style.Get(PropertyId.Display) is not CssKeyword { Name: "-webkit-box" })
        {
            return 0;
        }

        if (style.Get(PropertyId.BoxOrient) is not CssKeyword { Name: "vertical" })
        {
            return 0;
        }

        return (int)clamp.Value;
    }

    private abstract record InlineRun;
    private sealed record TextRun(string Text, ComputedStyle? Style, TextBox Owner) : InlineRun;
    private sealed record ImageRun(ImageBox Box) : InlineRun;
    private sealed record AtomicRun(InlineBox Box) : InlineRun;
    private sealed record LineBreakRun : InlineRun
    {
        public static readonly LineBreakRun Instance = new();
    }

    private static void Flatten(Box.Box box, List<InlineRun> runs)
    {
        foreach (var child in box.Children)
        {
            switch (child)
            {
                case TextBox tb:
                    tb.Fragments.Clear();
                    runs.Add(new TextRun(tb.Text, tb.Style, tb));
                    break;
                case ImageBox img:
                    runs.Add(new ImageRun(img));
                    break;
                case InlineBox ib when IsLineBreak(ib):
                    runs.Add(LineBreakRun.Instance);
                    break;
                case InlineBox ib when IsAtomicInline(ib):
                    runs.Add(new AtomicRun(ib));
                    break;
                case InlineBox ib:
                    Flatten(ib, runs);
                    break;
                default:
                    // Block-in-inline would land here; in v1 we just walk it as a sub-tree.
                    Flatten(child, runs);
                    break;
            }
        }
    }

    /// <summary>
    /// An "atomic inline" is a box that participates in the inline formatting
    /// context as a single unit — it occupies one indivisible slot in a line
    /// and carries its own box model. Per [CSS 2.1 §9.2.4] this includes
    /// <c>display:inline-block</c>, <c>inline-table</c>, replaced inlines,
    /// and (legacy) <c>inline-flex</c>/<c>inline-grid</c>. We only handle
    /// <c>inline-block</c> in v1; replaced inlines go through <see cref="ImageRun"/>.
    /// </summary>
    /// <summary>An <c>&lt;br&gt;</c> element: a forced line break in the inline flow.</summary>
    private static bool IsLineBreak(InlineBox box)
        => string.Equals(box.Element?.LocalName, "br", StringComparison.OrdinalIgnoreCase);

    private static bool IsAtomicInline(InlineBox box)
    {
        if (box.Style is null)
        {
            return false;
        }

        return box.Style.Get(PropertyId.Display) is CssKeyword k
            && (k.Name.Equals("inline-block", StringComparison.OrdinalIgnoreCase)
                || k.Name.Equals("inline-flex", StringComparison.OrdinalIgnoreCase)
                || k.Name.Equals("inline-grid", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True if any direct child is block-level (BlockContainer or
    /// AnonymousBlock — the latter being the wrapper around inline runs that
    /// share scope with real block children).
    /// </summary>
    private static bool HasBlockLevelChild(Box.Box box)
    {
        foreach (var child in box.Children)
        {
            if (child.Kind is BoxKind.BlockContainer or BoxKind.AnonymousBlock)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True if any direct (or indirectly-nested via plain inline spans) child
    /// is something the text-only <see cref="LayoutAtomicContent"/> path
    /// cannot place: a nested inline-block, a replaced inline (<c>&lt;img&gt;</c>),
    /// or a <c>&lt;br&gt;</c>. Bare inline spans wrapping such children also
    /// count — those flatten into the same inline formatting context as their
    /// contents. We deliberately do NOT count plain inline <c>&lt;span&gt;</c>s
    /// that contain only text: the text-only path handles them via the
    /// container's direct TextBox children, and the box tree builder normally
    /// produces text children at the inline-block level for void content like
    /// <c>&lt;input&gt;</c>.
    /// </summary>
    private static bool HasNonTextInlineChild(Box.Box box)
    {
        foreach (var child in box.Children)
        {
            switch (child)
            {
                case ImageBox:
                    return true;
                case InlineBox ib when IsLineBreak(ib):
                    return true;
                case InlineBox ib when IsAtomicInline(ib):
                    return true;
                case InlineBox ib:
                    // A wrapper inline (e.g. <span>) recurses; treat its
                    // subtree as part of this inline-block's IFC.
                    if (HasNonTextInlineChild(ib))
                    {
                        return true;
                    }

                    break;
            }
        }
        return false;
    }

    /// <summary>
    /// After a recursive <see cref="Layout(Box.Box, double)"/> sub-pass on
    /// <paramref name="box"/>, measure the rightmost X reached by any placed
    /// descendant. This is the max-content extent of the inline-block —
    /// suitable as its content-box width for shrink-to-fit sizing (CSS 2.1
    /// §10.3.5). Descends through inline spans because their text fragments
    /// live on the spans themselves; atomic children carry their own
    /// <see cref="Box.Box.Frame"/>.
    /// </summary>
    private static double MeasureUsedWidth(Box.Box box)
    {
        double max = 0;
        Walk(box);
        return max;

        void Walk(Box.Box node)
        {
            switch (node)
            {
                case TextBox tb:
                    foreach (var frag in tb.Fragments)
                    {
                        max = Math.Max(max, frag.X + frag.Width);
                    }

                    return;
                case ImageBox img:
                    max = Math.Max(max, img.Frame.X + img.Frame.Width);
                    return;
                case InlineBox ib when IsAtomicInline(ib) && ib != box:
                    max = Math.Max(max, ib.Frame.X + ib.Frame.Width);
                    return;
            }
            foreach (var child in node.Children)
            {
                Walk(child);
            }
        }
    }

    private static string NormalizeWhitespace(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var sb = new System.Text.StringBuilder(text.Length);
        var prevSpace = false;
        foreach (var c in text)
        {
            if (c is ' ' or '\t' or '\n' or '\r' or '\f')
            {
                if (!prevSpace)
                {
                    sb.Append(' ');
                }

                prevSpace = true;
            }
            else
            {
                sb.Append(c);
                prevSpace = false;
            }
        }
        return sb.ToString();
    }

    private static IEnumerable<string> SplitToWords(string text)
    {
        // Each word is one or more non-space chars, possibly preceded by a space.
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == ' ')
            {
                if (i > start)
                {
                    yield return text[start..i];
                }

                yield return " ";
                start = i + 1;
            }
        }
        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    private double ResolveFontSize(ComputedStyle? style)
    {
        if (style is null)
        {
            return 16;
        }

        return style.Get(PropertyId.FontSize) switch
        {
            CssLength len => Block.BlockLayout.ToPx(len, _viewport),
            CssNumber n => n.Value,
            _ => 16,
        };
    }

    private double ResolveLineHeight(ComputedStyle? style, double fontSize, FontSpec spec)
    {
        if (style is null)
        {
            return _measurer.NormalLineHeight(fontSize, spec);
        }

        return style.Get(PropertyId.LineHeight) switch
        {
            CssNumber n => n.Value * fontSize,
            CssLength len => Block.BlockLayout.ToPx(len, _viewport),
            CssPercentage pct => fontSize * pct.Value / 100d,
            CssKeyword k when k.Name == "normal" => _measurer.NormalLineHeight(fontSize, spec),
            _ => _measurer.NormalLineHeight(fontSize, spec),
        };
    }

    private static FontSpec ResolveFontSpec(ComputedStyle? style) => FontSpec.FromStyle(style);
}
