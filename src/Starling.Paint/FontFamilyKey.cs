namespace Starling.Paint;

/// <summary>
/// Canonicalises a CSS font-family name for keying. CSS family matching is
/// ASCII case-insensitive (CSS Fonts 3 §5.1) and quote-insensitive — the
/// quotes around <c>"TwitterChirp"</c> are syntax, not part of the name. The
/// CSS tokenizer strips quotes for values that flow through the parser, but
/// names can also arrive as raw strings (the CSS Font Loading API's
/// <c>FontFace(family, …)</c> constructor, hand-built <c>FontSpec</c>s, tool
/// input), so both keying sides — <see cref="FontFaceRegistry"/> registration
/// and <see cref="ImageSharpFontLookup"/> candidate resolution — normalise
/// through this helper. Case-insensitivity itself is handled by the
/// <see cref="StringComparer.OrdinalIgnoreCase"/> dictionaries on those types;
/// this helper only removes surrounding whitespace and one matching pair of
/// ASCII quotes.
/// </summary>
internal static class FontFamilyKey
{
    /// <summary>
    /// Trims surrounding whitespace and one matching pair of ASCII quotes
    /// (<c>"…"</c> or <c>'…'</c>). Returns the original instance when the
    /// name is already canonical, so the per-candidate call in the resolve
    /// path allocates nothing for the common clean case.
    /// </summary>
    public static string Normalize(string family)
    {
        var span = family.AsSpan().Trim();
        if (span.Length >= 2 && (span[0] == '"' || span[0] == '\'') && span[^1] == span[0])
        {
            span = span[1..^1].Trim();
        }

        return span.Length == family.Length ? family : span.ToString();
    }
}
