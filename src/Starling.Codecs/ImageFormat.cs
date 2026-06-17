namespace Starling.Codecs;

/// <summary>The container formats <see cref="NativeImageDecoder"/> recognises.</summary>
internal enum ImageFormat
{
    Unknown = 0,
    Png,
    Jpeg,
    Webp,
    Gif,
    Bmp,
    /// <summary>
    /// Scalable Vector Graphics — an XML document, not a raster container.
    /// Detected from a text prefix (see <see cref="ImageFormatSniffer"/>) rather
    /// than magic bytes. The OS-native backends cannot decode it; the engine
    /// routes SVG to the pure-managed rasterizer in <c>Starling.Paint</c>.
    /// </summary>
    Svg,
}

/// <summary>
/// Classifies an encoded image by its leading magic bytes. The platform
/// backends (especially Linux, which binds a separate library per codec) use
/// this to pick the right decode path without trial-and-error.
/// </summary>
internal static class ImageFormatSniffer
{
    /// <summary>
    /// Inspect the header of <paramref name="bytes"/> and return the detected
    /// <see cref="ImageFormat"/>, or <see cref="ImageFormat.Unknown"/> if no
    /// signature matches.
    /// </summary>
    public static ImageFormat Detect(ReadOnlySpan<byte> bytes)
    {
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return ImageFormat.Png;
        }

        // JPEG: FF D8 FF
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return ImageFormat.Jpeg;
        }

        // WebP: "RIFF" .... "WEBP"
        if (bytes.Length >= 12 &&
            bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
            bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            return ImageFormat.Webp;
        }

        // GIF: "GIF87a" / "GIF89a"
        if (bytes.Length >= 6 &&
            bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' &&
            bytes[3] == (byte)'8' && (bytes[4] == (byte)'7' || bytes[4] == (byte)'9') && bytes[5] == (byte)'a')
        {
            return ImageFormat.Gif;
        }

        // BMP: "BM"
        if (bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
        {
            return ImageFormat.Bmp;
        }

        // SVG: an XML document. Checked last because — unlike the raster
        // formats above — it has no single magic number; it is text that, after
        // an optional UTF-8 BOM and leading whitespace, opens with "<?xml",
        // "<svg", or "<!DOCTYPE svg" (case-insensitive on the element name).
        if (LooksLikeSvg(bytes))
        {
            return ImageFormat.Svg;
        }

        return ImageFormat.Unknown;
    }

    /// <summary>
    /// True when <paramref name="bytes"/> appears to be an SVG document. Public
    /// so the engine — which cannot see the internal <see cref="ImageFormat"/>
    /// enum — can route SVG bytes to the managed rasterizer instead of the
    /// OS-native raster codecs (which would reject XML).
    /// </summary>
    public static bool LooksLikeSvg(ReadOnlySpan<byte> bytes)
    {
        // Skip a UTF-8 BOM (EF BB BF) if present.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bytes = bytes[3..];
        }

        // Skip ASCII leading whitespace (space, tab, CR, LF, FF).
        int i = 0;
        while (i < bytes.Length &&
               (bytes[i] == (byte)' ' || bytes[i] == (byte)'\t' ||
                bytes[i] == (byte)'\r' || bytes[i] == (byte)'\n' || bytes[i] == 0x0C))
        {
            i++;
        }
        var rest = bytes[i..];
        if (rest.IsEmpty || rest[0] != (byte)'<')
        {
            return false;
        }

        // Decode an ASCII prefix for the prologue/root-element check. SVG markup
        // is ASCII at the top regardless of the declared encoding, so a bounded
        // prefix is enough and never throws on multi-byte tails. The window must
        // be large enough to clear an XML declaration *and* a leading generator
        // comment before the root element — Adobe Illustrator emits
        // "<?xml …?>\r\n<!-- Generator: Adobe Illustrator …, SVG Export … -->"
        // which pushes "<svg" well past 100 bytes. 1 KiB covers real-world
        // generator preambles with margin.
        Span<char> prefix = stackalloc char[1024];
        int n = 0;
        for (int j = 0; j < rest.Length && n < prefix.Length; j++)
        {
            byte b = rest[j];
            prefix[n++] = b < 0x80 ? (char)b : '?';
        }
        var head = prefix[..n];

        // "<?xml" prologue — accept only if the document is plausibly SVG.
        if (StartsWith(head, "<?xml"))
        {
            return ContainsSvgRoot(head);
        }

        // "<!DOCTYPE svg" or "<!doctype svg".
        if (StartsWith(head, "<!DOCTYPE") || StartsWith(head, "<!doctype"))
        {
            return ContainsToken(head, "svg");
        }

        // "<svg" possibly followed by whitespace or '>' or ':' (namespaced).
        if (StartsWith(head, "<svg"))
        {
            char after = head.Length > 4 ? head[4] : '>';
            return after is ' ' or '\t' or '\r' or '\n' or '>' or '/' or ':';
        }

        // A leading comment ("<!--") before <svg> is common (Illustrator emits
        // a generator comment). If the prefix is just a comment opener, accept
        // when "svg" appears nearby.
        if (StartsWith(head, "<!--"))
        {
            return ContainsToken(head, "svg");
        }

        return false;
    }

    private static bool ContainsSvgRoot(ReadOnlySpan<char> head)
        // After an XML prologue the root may not fit in the prefix window; the
        // common case (xml decl then "<svg" on the same/next line) does. Accept
        // when "<svg" or "svg" appears in the window; the prologue itself is a
        // strong signal that this is XML, and the managed decoder fails soft if
        // it turns out not to be SVG.
        => ContainsToken(head, "<svg") || ContainsToken(head, "svg");

    private static bool StartsWith(ReadOnlySpan<char> span, string value)
        => span.StartsWith(value.AsSpan(), StringComparison.Ordinal);

    private static bool ContainsToken(ReadOnlySpan<char> span, string token)
        => span.ToString().Contains(token, StringComparison.OrdinalIgnoreCase);
}
