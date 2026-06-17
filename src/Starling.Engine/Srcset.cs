using System.Globalization;

namespace Starling.Engine;

/// <summary>
/// Minimal HTML <c>srcset</c> + <c>sizes</c> parser and source selector.
/// Implements the subset of <see href="https://html.spec.whatwg.org/multipage/images.html#image-candidate-string">
/// "Parse a srcset attribute"</see> and "Parse a sizes attribute" that the
/// engine needs for picking a candidate URL and computing the
/// density-corrected intrinsic width/height of the resulting image.
/// </summary>
/// <remarks>
/// <para>Supported:
/// <list type="bullet">
///   <item><c>srcset</c> candidates with bare URLs and <c>Nw</c> width descriptors.</item>
///   <item><c>x</c> pixel-density descriptors (treated as candidate density).</item>
///   <item><c>sizes</c> as a comma-separated list of <c>(media-query) length</c>
///     pairs plus an optional bare default length. Only
///     <c>(min-width: Nx)</c> and <c>(max-width: Nx)</c> media queries are
///     evaluated against the viewport width; unknown queries are skipped.</item>
///   <item>Length units: px, em, rem, vw (relative to viewport width), pt.</item>
/// </list>
/// </para>
/// <para>The result drives both the URL to fetch and the
/// <c>density-corrected natural width/height</c> per the HTML spec: when a
/// <c>w</c>-descriptor candidate is selected via <c>sizes</c>, the displayed
/// intrinsic width is the source-size length in CSS pixels (not the actual
/// pixel dimensions of the bitmap). Without this, an image authored with
/// <c>sizes="400px"</c> but backed by a 1200×800 source paints three times
/// too big.</para>
/// </remarks>
internal static class Srcset
{
    internal readonly record struct Candidate(string Url, double Width, double Density);

    /// <summary>
    /// Pick an image source for the element. Returns the URL to fetch and
    /// the density-corrected intrinsic (CSS-pixel) width &amp; height to use
    /// once decoded. <paramref name="fallbackSrc"/> is the verbatim <c>src</c>
    /// attribute; it is returned with <c>correctedWidth = 0</c> (meaning
    /// "use the source's pixel dimensions") when no usable srcset is present.
    /// </summary>
    public static (string Url, double CorrectedWidth, double CorrectedHeight) Select(
        string? srcset, string? sizes, string? fallbackSrc, double viewportWidthCssPx, double fontSizeCssPx)
    {
        var candidates = Parse(srcset);
        if (candidates.Count == 0)
        {
            return (fallbackSrc ?? string.Empty, 0, 0);
        }

        var sourceSize = ParseSourceSize(sizes, viewportWidthCssPx, fontSizeCssPx);
        var (picked, density) = PickCandidate(candidates, sourceSize);
        if (picked is null)
        {
            return (fallbackSrc ?? string.Empty, 0, 0);
        }

        // Density-corrected width: source-size in CSS px when a w-candidate
        // was picked, or 0 (= use source pixel dims) for pure x-candidates
        // with no sizes hint. (`density` is implicit in the corrected width;
        // callers don't need it separately.)
        _ = density;
        var correctedWidth = picked.Value.Width > 0 && sourceSize > 0
            ? sourceSize
            : 0;
        return (picked.Value.Url, correctedWidth, 0);
    }

    /// <summary>
    /// Parse a srcset attribute into candidate descriptors. Each candidate is
    /// a URL plus either a width descriptor (<c>Nw</c>, in source-bitmap
    /// pixels) or a density descriptor (<c>Nx</c>). Defaults to density 1.0
    /// when neither is provided.
    /// </summary>
    public static List<Candidate> Parse(string? srcset)
    {
        var list = new List<Candidate>();
        if (string.IsNullOrWhiteSpace(srcset))
        {
            return list;
        }

        // Split on commas, but respect URLs that may contain commas — the
        // HTML spec's tokenizer is whitespace-driven; we approximate by
        // splitting on ", " (comma followed by whitespace) which works for
        // virtually all real-world srcset values including the Cloudinary-
        // style ones we care about.
        foreach (var raw in SplitCandidates(srcset))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            // Last whitespace separates URL from descriptor (if any).
            var lastWs = LastWhitespace(trimmed);
            string url; string? descriptor;
            if (lastWs < 0 || IsDescriptor(trimmed.AsSpan(lastWs + 1)) is false)
            {
                url = trimmed;
                descriptor = null;
            }
            else
            {
                url = trimmed[..lastWs].TrimEnd();
                descriptor = trimmed[(lastWs + 1)..];
            }

            if (url.Length == 0)
            {
                continue;
            }

            double w = 0, d = 1.0;
            if (descriptor is { Length: > 0 })
            {
                var unit = descriptor[^1];
                var numText = descriptor[..^1];
                if (!double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                {
                    continue;
                }

                if (unit is 'w' or 'W')
                {
                    w = n;
                }
                else if (unit is 'x' or 'X')
                {
                    d = n;
                }
                else
                {
                    continue;
                }
            }
            list.Add(new Candidate(url, w, d));
        }
        return list;
    }

    private static IEnumerable<string> SplitCandidates(string srcset)
    {
        var start = 0;
        for (var i = 0; i < srcset.Length; i++)
        {
            if (srcset[i] != ',')
            {
                continue;
            }
            // Treat as separator only when followed by whitespace or end.
            if (i + 1 < srcset.Length && !char.IsWhiteSpace(srcset[i + 1]))
            {
                continue;
            }

            yield return srcset[start..i];
            start = i + 1;
        }
        if (start < srcset.Length)
        {
            yield return srcset[start..];
        }
    }

    private static int LastWhitespace(string s)
    {
        for (var i = s.Length - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(s[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsDescriptor(ReadOnlySpan<char> token)
    {
        if (token.Length < 2)
        {
            return false;
        }

        var tail = token[^1];
        if (tail is not ('w' or 'W' or 'x' or 'X'))
        {
            return false;
        }

        return double.TryParse(token[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>
    /// Evaluate <paramref name="sizes"/> against the viewport, returning the
    /// resulting source-size length in CSS pixels. Returns 0 when sizes is
    /// empty or no clause matches and there is no default bare length.
    /// </summary>
    public static double ParseSourceSize(string? sizes, double viewportWidthCssPx, double fontSizeCssPx)
    {
        if (string.IsNullOrWhiteSpace(sizes))
        {
            return 0;
        }

        foreach (var raw in sizes.Split(','))
        {
            var clause = raw.Trim();
            if (clause.Length == 0)
            {
                continue;
            }

            // Either "<media-query> <length>" or bare "<length>".
            string lengthPart;
            if (clause[0] == '(')
            {
                var close = clause.IndexOf(')');
                if (close < 0)
                {
                    continue;
                }

                var query = clause[1..close].Trim();
                lengthPart = clause[(close + 1)..].Trim();
                if (!EvaluateMediaQuery(query, viewportWidthCssPx, fontSizeCssPx))
                {
                    continue;
                }
            }
            else
            {
                lengthPart = clause;
            }
            if (ResolveLengthCssPx(lengthPart, viewportWidthCssPx, fontSizeCssPx) is { } px && px > 0)
            {
                return px;
            }
        }
        return 0;
    }

    private static bool EvaluateMediaQuery(string query, double viewportWidth, double fontSize)
    {
        var colon = query.IndexOf(':');
        if (colon < 0)
        {
            return false;
        }

        var feature = query[..colon].Trim().ToLowerInvariant();
        var value = query[(colon + 1)..].Trim();
        if (ResolveLengthCssPx(value, viewportWidth, fontSize) is not { } px)
        {
            return false;
        }

        return feature switch
        {
            "min-width" => viewportWidth >= px,
            "max-width" => viewportWidth <= px,
            _ => false,
        };
    }

    private static double? ResolveLengthCssPx(string raw, double viewportWidth, double fontSize)
    {
        raw = raw.Trim();
        if (raw.Length == 0)
        {
            return null;
        }

        string num; string unit;
        var unitStart = raw.Length;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (!(char.IsDigit(c) || c == '.' || c == '-' || c == '+'))
            {
                unitStart = i;
                break;
            }
        }
        num = raw[..unitStart];
        unit = raw[unitStart..].Trim().ToLowerInvariant();

        if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
        {
            return null;
        }

        return unit switch
        {
            "" or "px" => n,
            "em" or "rem" => n * fontSize,
            "vw" => n * viewportWidth / 100.0,
            "pt" => n * 96.0 / 72.0,
            _ => null,
        };
    }

    private static (Candidate? Picked, double Density) PickCandidate(List<Candidate> candidates, double sourceSize)
    {
        // When sizes resolves a source size and we have w-described
        // candidates, pick the smallest w that satisfies w >= sourceSize.
        // Falls back to the largest w if none satisfy. This matches the
        // "fewest pixels at acceptable density" heuristic used by Chromium
        // and Firefox when dpr=1.
        if (sourceSize > 0)
        {
            Candidate? bestFit = null;
            Candidate? largest = null;
            foreach (var c in candidates)
            {
                if (c.Width <= 0)
                {
                    continue;
                }

                if (largest is null || c.Width > largest.Value.Width)
                {
                    largest = c;
                }

                if (c.Width >= sourceSize && (bestFit is null || c.Width < bestFit.Value.Width))
                {
                    bestFit = c;
                }
            }
            if (bestFit is { } b)
            {
                return (b, b.Width / sourceSize);
            }

            if (largest is { } l)
            {
                return (l, l.Width / sourceSize);
            }
        }

        // No sizes: prefer the highest density x-candidate; if only
        // w-candidates exist with no sizes, pick the largest as a sane
        // default (matches Chromium's behaviour at dpr=1).
        Candidate? bestX = null;
        Candidate? bestW = null;
        foreach (var c in candidates)
        {
            if (c.Width > 0)
            {
                if (bestW is null || c.Width > bestW.Value.Width)
                {
                    bestW = c;
                }
            }
            else
            {
                if (bestX is null || c.Density > bestX.Value.Density)
                {
                    bestX = c;
                }
            }
        }
        if (bestX is { } x)
        {
            return (x, x.Density);
        }

        if (bestW is { } w)
        {
            return (w, 1.0);
        }

        return (null, 1.0);
    }
}
