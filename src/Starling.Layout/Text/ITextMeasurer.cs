using Starling.Css.Cascade;
using Starling.Css.Properties;
using Starling.Css.Values;

namespace Starling.Layout.Text;

/// <summary>
/// One OpenType variation-axis setting. <see cref="Tag"/> is the four-letter
/// axis identifier ("wght", "wdth", "opsz", "ital", "slnt", or any font-
/// defined custom axis), <see cref="Value"/> the numeric coordinate on that
/// axis. Sorted by tag in <see cref="FontSpec"/> so two specs reach the same
/// cache entry regardless of authored order.
/// </summary>
public readonly record struct FontVariation(string Tag, float Value);

/// <summary>
/// The font selection layout needs for measurement: a prioritised family list,
/// bold/italic flags, and a (possibly empty) set of OpenType variation-axis
/// settings. Layout doesn't resolve this to a concrete typeface — that's the
/// paint module's job. The measurer resolves and caches per spec so each call
/// doesn't re-walk the family list.
/// <para>
/// <see cref="Equals(FontSpec?)"/> compares families element-wise so two
/// instances built from the same cascade compare equal (the default record
/// <c>Equals</c> for a reference-typed list would use reference equality and
/// blow caches keyed on FontSpec).
/// </para>
/// </summary>
public sealed record FontSpec(
    IReadOnlyList<string> Families,
    bool Bold,
    bool Italic,
    IReadOnlyList<FontVariation> Variations)
{
    public FontSpec(IReadOnlyList<string> families, bool bold, bool italic)
        : this(families, bold, italic, Array.Empty<FontVariation>())
    {
    }

    public static readonly FontSpec Default = new(["sans-serif"], false, false, Array.Empty<FontVariation>());

    public bool Equals(FontSpec? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (Bold != other.Bold || Italic != other.Italic)
        {
            return false;
        }

        if (Families.Count != other.Families.Count)
        {
            return false;
        }

        for (var i = 0; i < Families.Count; i++)
        {
            if (!string.Equals(Families[i], other.Families[i], StringComparison.Ordinal))
            {
                return false;
            }
        }
        if (Variations.Count != other.Variations.Count)
        {
            return false;
        }

        for (var i = 0; i < Variations.Count; i++)
        {
            if (Variations[i] != other.Variations[i])
            {
                return false;
            }
        }
        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Bold);
        hash.Add(Italic);
        foreach (var f in Families)
        {
            hash.Add(f, StringComparer.Ordinal);
        }

        foreach (var v in Variations)
        {
            hash.Add(v);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Builds a <see cref="FontSpec"/> from a computed style. Used by both
    /// inline layout (to call the measurer) and the display-list builder (to
    /// stamp DrawText records) so the two see the same families/bold/italic.
    /// </summary>
    public static FontSpec FromStyle(ComputedStyle? style)
    {
        if (style is null)
        {
            return Default;
        }

        var bold = IsBold(style);
        var italic = IsItalic(style);
        var variations = ExtractVariations(style);
        return new FontSpec(ExtractFamilies(style), bold, italic, variations);
    }

    /// <summary>
    /// Builds the variation-axis list. We layer three sources, last-write-wins:
    /// <list type="number">
    ///   <item>The <c>wght</c> axis derived from numeric <c>font-weight</c>
    ///   (so a variable font reaches the requested weight even when no
    ///   discrete bold face is loaded).</item>
    ///   <item>The <c>wdth</c> axis from <c>font-stretch</c> (percentage or
    ///   keyword), and <c>slnt</c> from oblique <c>font-style</c>.</item>
    ///   <item>Explicit <c>font-variation-settings</c> overrides — the
    ///   author's last word per CSS Fonts 4 §6.</item>
    /// </list>
    /// Output is sorted by tag so equality and caching are order-independent.
    /// </summary>
    private static FontVariation[] ExtractVariations(ComputedStyle style)
    {
        var axes = new Dictionary<string, float>(StringComparer.Ordinal);

        // wght: 1–1000 per CSS Fonts 4. We feed the raw numeric value so a
        // variable face delivers 350/550/etc. exactly. A keyword font-weight
        // resolves to a number upstream (normal=400, bold=700).
        if (style.Get(PropertyId.FontWeight) is CssNumber n && n.Value > 0)
        {
            axes["wght"] = (float)n.Value;
        }

        // wdth: 50%–200%. We accept either a percentage or the named keywords.
        switch (style.Get(PropertyId.FontStretch))
        {
            case CssPercentage pct when pct.Value > 0:
                axes["wdth"] = (float)pct.Value;
                break;
            case CssKeyword kw:
                if (StretchKeyword(kw.Name) is { } stretchValue)
                {
                    axes["wdth"] = stretchValue;
                }

                break;
        }

        // slnt: degrees of slant, negative = forward. Activated by an oblique
        // font-style with an angle, or the bare "oblique" keyword.
        if (style.Get(PropertyId.FontStyle) is CssValueList styleList &&
            styleList.Values.Count >= 2 &&
            styleList.Values[0] is CssKeyword { Name: "oblique" } &&
            styleList.Values[1] is CssDimension { Unit: "deg" } slnt)
        {
            axes["slnt"] = (float)-slnt.Value;
        }

        // Explicit overrides last.
        if (style.Get(PropertyId.FontVariationSettings) is CssValueList settings)
        {
            ApplyExplicitSettings(settings.Values, axes);
        }
        else if (style.Get(PropertyId.FontVariationSettings) is { } single)
        {
            ApplyExplicitSettings(new[] { single }, axes);
        }

        if (axes.Count == 0)
        {
            return Array.Empty<FontVariation>();
        }

        var result = new FontVariation[axes.Count];
        var i = 0;
        foreach (var kv in axes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            result[i++] = new FontVariation(kv.Key, kv.Value);
        }

        return result;
    }

    private static void ApplyExplicitSettings(IReadOnlyList<CssValue> values, Dictionary<string, float> axes)
    {
        // The generic value parser leaves commas in the list as empty-name
        // CssKeywords (the tokenizer's Comma token has Value=""). Walk pairs
        // of (CssString tag, CssNumber value), skipping empty separators.
        string? pendingTag = null;
        foreach (var v in values)
        {
            switch (v)
            {
                case CssString s when s.Value.Length == 4:
                    pendingTag = s.Value;
                    break;
                case CssNumber num when pendingTag is not null:
                    axes[pendingTag] = (float)num.Value;
                    pendingTag = null;
                    break;
                case CssKeyword kw when kw.Name.Length == 0:
                    // comma — reset.
                    pendingTag = null;
                    break;
                case CssKeyword kw when kw.Name == "normal":
                    // `font-variation-settings: normal` — clear explicit axes.
                    // Auto-derived wght/wdth survive since they live in `axes`
                    // from earlier; explicit "normal" just means no overrides.
                    return;
            }
        }
    }

    private static float? StretchKeyword(string name) => name switch
    {
        "ultra-condensed" => 50f,
        "extra-condensed" => 62.5f,
        "condensed" => 75f,
        "semi-condensed" => 87.5f,
        "normal" => 100f,
        "semi-expanded" => 112.5f,
        "expanded" => 125f,
        "extra-expanded" => 150f,
        "ultra-expanded" => 200f,
        _ => null,
    };

    private static IReadOnlyList<string> ExtractFamilies(ComputedStyle style)
    {
        // The CSS value parser leaves commas in CssValueList as empty-name
        // CssKeywords (the tokenizer emits a Comma token with Value=""); skip
        // those. Falls back to "sans-serif" when nothing usable parses.
        var value = style.Get(PropertyId.FontFamily);
        switch (value)
        {
            case CssKeyword kw when kw.Name.Length > 0: return new[] { kw.Name };
            case CssString s when s.Value.Length > 0: return new[] { s.Value };
            case CssValueList vl:
                var families = new List<string>(vl.Values.Count);
                foreach (var v in vl.Values)
                {
                    switch (v)
                    {
                        case CssKeyword k when k.Name.Length > 0: families.Add(k.Name); break;
                        case CssString str when str.Value.Length > 0: families.Add(str.Value); break;
                    }
                }
                if (families.Count > 0)
                {
                    return families;
                }

                break;
        }
        return Default.Families;
    }

    private static bool IsBold(ComputedStyle style)
        => style.Get(PropertyId.FontWeight) switch
        {
            CssKeyword { Name: "bold" } => true,
            CssNumber n => n.Value >= 600,
            _ => false,
        };

    private static bool IsItalic(ComputedStyle style)
        => style.Get(PropertyId.FontStyle) is CssKeyword { Name: "italic" or "oblique" };
}

/// <summary>
/// Layout's text-measurement seam. The paint module supplies the real
/// implementation backed by font metrics; layout keeps a coarse fallback so
/// it can produce a usable tree without a paint dependency.
/// </summary>
public interface ITextMeasurer
{
    /// <summary>Advance width in CSS px for <paramref name="text"/> at the given font size and spec.</summary>
    double MeasureWidth(string text, double fontSize, FontSpec spec);

    /// <summary>
    /// Shape <paramref name="text"/> into a positioned glyph run plus its
    /// total advance. Implementations without real font support (e.g.
    /// <see cref="DefaultTextMeasurer"/>) may return an empty
    /// <see cref="ShapedRun.Glyphs"/> array with a heuristic advance — the
    /// paint backend treats that as "shape at paint time".
    /// </summary>
    ShapedRun Shape(string text, double fontSize, FontSpec spec);

    /// <summary>Line-height for the given font at the given size when CSS <c>line-height: normal</c> applies.</summary>
    double NormalLineHeight(double fontSize, FontSpec spec);

    /// <summary>Distance from the top of the line box to the alphabetic baseline.</summary>
    double Baseline(double fontSize, FontSpec spec);
}

/// <summary>
/// Default measurer: assumes a roughly proportional sans-serif with an average
/// glyph advance of ~0.5em. Good enough for layout to wrap text and choose
/// line counts; precise paint metrics come from the paint module. Ignores the
/// FontSpec — the heuristic is family-agnostic.
/// </summary>
public sealed class DefaultTextMeasurer : ITextMeasurer
{
    public static readonly DefaultTextMeasurer Instance = new();

    public double MeasureWidth(string text, double fontSize, FontSpec spec)
    {
        ArgumentNullException.ThrowIfNull(text);
        double total = 0;
        foreach (var c in text)
        {
            total += AverageAdvanceFactor(c) * fontSize;
        }
        return total;
    }

    /// <summary>
    /// The heuristic has no real glyphs to emit; return an empty
    /// <see cref="ShapedRun.Glyphs"/> array so the paint backend shapes at
    /// paint time. The advance is the heuristic width.
    /// </summary>
    public ShapedRun Shape(string text, double fontSize, FontSpec spec)
        => new GlyphShapedRun(Array.Empty<ShapedGlyph>(), MeasureWidth(text, fontSize, spec));

    public double NormalLineHeight(double fontSize, FontSpec spec) => fontSize * 1.2;

    public double Baseline(double fontSize, FontSpec spec) => fontSize * 0.8;

    private static double AverageAdvanceFactor(char c) => c switch
    {
        ' ' or '\t' => 0.28,
        'i' or 'l' or 'I' or '|' or '!' or '.' or ',' or ';' or ':' or '\'' or '`' => 0.28,
        'm' or 'M' or 'w' or 'W' => 0.85,
        _ when c >= '0' && c <= '9' => 0.55,
        _ when char.IsUpper(c) => 0.65,
        _ when char.IsLower(c) => 0.52,
        _ => 0.5,
    };
}
