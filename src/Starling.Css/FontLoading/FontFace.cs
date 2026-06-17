using Starling.Css.FontFace;

namespace Starling.Css.FontLoading;

/// <summary>
/// Represents a single font face as described by CSS Font Loading Level 3 §3
/// (<see href="https://www.w3.org/TR/css-font-loading-3/#fontface-interface"/>).
/// </summary>
/// <remarks>
/// <para>
/// This is the pure-managed model slice. It covers the descriptor properties
/// (<see cref="Family"/>, <see cref="Style"/>, <see cref="Weight"/>,
/// <see cref="Stretch"/>, <see cref="UnicodeRange"/>) and the
/// <see cref="Status"/> state machine (§3.1). The following are
/// intentionally out of scope for this slice:
/// </para>
/// <list type="bullet">
///   <item><description>
///     Network loading — <see cref="Load"/> transitions status to
///     <see cref="FontFaceLoadStatus.Loaded"/> synchronously without fetching
///     any data. Real async fetch and parse lives in <c>Starling.Engine</c>'s
///     font-face fetcher.
///   </description></item>
///   <item><description>
///     JavaScript API bindings — <c>document.fonts</c> and the <c>FontFace</c>
///     constructor exposed via the Starling JS engine are a later step.
///   </description></item>
/// </list>
/// </remarks>
public sealed class FontFace
{
    /// <summary>
    /// The raw serialised value of the <c>src</c> descriptor as provided to
    /// the constructor. Stored for round-trip; not re-parsed at the model layer.
    /// CSS Font Loading 3 §3 — <c>source</c> argument.
    /// </summary>
    public string Source { get; }

    /// <summary>
    /// The font-family name. CSS Font Loading 3 §3 — <c>family</c> descriptor.
    /// </summary>
    public string Family { get; }

    /// <summary>
    /// The <c>font-style</c> descriptor value, e.g. <c>"normal"</c> or
    /// <c>"italic"</c>. Defaults to <c>"normal"</c>.
    /// CSS Font Loading 3 §3.
    /// </summary>
    public string Style { get; }

    /// <summary>
    /// The <c>font-weight</c> descriptor value, e.g. <c>"normal"</c> or
    /// <c>"bold"</c>. Defaults to <c>"normal"</c>.
    /// CSS Font Loading 3 §3.
    /// </summary>
    public string Weight { get; }

    /// <summary>
    /// The <c>font-stretch</c> descriptor value, e.g. <c>"normal"</c>.
    /// Defaults to <c>"normal"</c>.
    /// CSS Font Loading 3 §3.
    /// </summary>
    public string Stretch { get; }

    /// <summary>
    /// The <c>unicode-range</c> descriptor value serialised as a string, e.g.
    /// <c>"U+0-10FFFF"</c>. Defaults to <c>"U+0-10FFFF"</c> (full range).
    /// CSS Font Loading 3 §3.
    /// </summary>
    public string UnicodeRange { get; }

    /// <summary>
    /// The current load status of this font face.
    /// CSS Font Loading 3 §3.1
    /// (<see href="https://www.w3.org/TR/css-font-loading-3/#dom-fontface-status"/>).
    /// </summary>
    public FontFaceLoadStatus Status { get; private set; }

    /// <summary>
    /// Creates a new <see cref="FontFace"/> with the given family and source,
    /// and optional descriptor overrides.
    /// CSS Font Loading 3 §3 — mirrors the <c>FontFace(family, source, descriptors)</c>
    /// constructor algorithm.
    /// </summary>
    /// <param name="family">The font-family name (required).</param>
    /// <param name="source">The serialised <c>src</c> value (required).</param>
    /// <param name="style">Optional <c>font-style</c> descriptor; defaults to <c>"normal"</c>.</param>
    /// <param name="weight">Optional <c>font-weight</c> descriptor; defaults to <c>"normal"</c>.</param>
    /// <param name="stretch">Optional <c>font-stretch</c> descriptor; defaults to <c>"normal"</c>.</param>
    /// <param name="unicodeRange">Optional <c>unicode-range</c> descriptor; defaults to <c>"U+0-10FFFF"</c>.</param>
    public FontFace(
        string family,
        string source,
        string style = "normal",
        string weight = "normal",
        string stretch = "normal",
        string unicodeRange = "U+0-10FFFF")
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(source);
        Family = family;
        Source = source;
        Style = style;
        Weight = weight;
        Stretch = stretch;
        UnicodeRange = unicodeRange;
        Status = FontFaceLoadStatus.Unloaded;
    }

    /// <summary>
    /// Creates a <see cref="FontFace"/> from an already-parsed
    /// <see cref="FontFaceRule"/> produced by <c>FontFaceParser</c>.
    /// CSS Font Loading 3 §3 — constructing a <c>FontFace</c> object from a
    /// CSS <c>@font-face</c> rule (the "parse a font face" algorithm, step 2).
    /// </summary>
    /// <param name="rule">The parsed at-rule. Must not be <see langword="null"/>.</param>
    /// <returns>A new <see cref="FontFace"/> in the <see cref="FontFaceLoadStatus.Unloaded"/> state.</returns>
    public static FontFace FromRule(FontFaceRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        // Build a minimal serialised `src` string from the first source entry.
        // Full multi-source serialisation is not needed for this model slice.
        var src = rule.Sources.Count > 0
            ? SerialiseSource(rule.Sources[0])
            : string.Empty;

        var style = rule.Italic ? "italic" : "normal";
        var weight = rule.Bold ? "bold" : "normal";

        // Serialise the unicode-range back to a string for the descriptor
        // property. When no range was declared the spec default covers all codepoints.
        var unicodeRange = rule.UnicodeRange is not null
            ? SerialiseUnicodeRange(rule.UnicodeRange)
            : "U+0-10FFFF";

        return new FontFace(
            family: rule.FamilyName,
            source: src,
            style: style,
            weight: weight,
            unicodeRange: unicodeRange);
    }

    /// <summary>
    /// Transitions the font face to the <see cref="FontFaceLoadStatus.Loaded"/>
    /// state. CSS Font Loading 3 §3.3 — the <c>load()</c> method.
    /// </summary>
    /// <remarks>
    /// In this model slice the method is synchronous and performs no network
    /// I/O. Real async loading is handled by the
    /// <c>Starling.Engine.FontFaceFetcher</c>. Calling <c>Load()</c> on a face
    /// that is already <see cref="FontFaceLoadStatus.Loaded"/> or
    /// <see cref="FontFaceLoadStatus.Error"/> is a no-op, matching the spec
    /// algorithm (§3.3 step 1).
    /// </remarks>
    public void Load()
    {
        if (Status is FontFaceLoadStatus.Loaded or FontFaceLoadStatus.Error)
        {
            return;
        }

        Status = FontFaceLoadStatus.Loading;
        // No real fetch in this slice — transition immediately to Loaded.
        Status = FontFaceLoadStatus.Loaded;
    }

    // ---------------------------------------------------------------------------
    // Private helpers
    // ---------------------------------------------------------------------------

    private static string SerialiseSource(FontFaceSource source) =>
        source switch
        {
            UrlFontSource u => u.Format is not null
                ? $"url(\"{u.Url}\") format(\"{u.Format}\")"
                : $"url(\"{u.Url}\")",
            LocalFontSource l => $"local(\"{l.Name}\")",
            _ => string.Empty,
        };

    private static string SerialiseUnicodeRange(UnicodeRangeSet ranges)
    {
        var parts = ranges.Ranges.Select(r =>
            r.Start == r.End
                ? $"U+{r.Start:X}"
                : $"U+{r.Start:X}-{r.End:X}");
        return string.Join(", ", parts);
    }
}
