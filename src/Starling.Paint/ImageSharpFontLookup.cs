using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.Fonts;
using Starling.Layout.Text;

namespace Starling.Paint;

/// <summary>
/// Shared font lookup for the ImageSharp backend and text measurer. Walks the
/// CSS <c>font-family</c> list (expanding generic keywords) against a font
/// collection that bundles both the embedded fonts in <c>Starling.Paint.dll</c>
/// and the host's installed system fonts, so paragraph widths and bold/italic
/// faces line up between layout and raster.
/// </summary>
internal static class ImageSharpFontLookup
{
    /// <summary>
    /// Loads every embedded TTF/OTF resource from <c>Starling.Paint.dll</c>,
    /// every system-installed family, and every <c>@font-face</c>-registered
    /// font from <paramref name="webFonts"/> into a single
    /// <see cref="FontCollection"/>. Registration order is bundled →
    /// web-fonts → system, so an author's <c>@font-face</c> stylesheet wins
    /// over a same-named system face but cannot override the bundled
    /// deterministic-fallback OpenSans (which is loaded first under its own
    /// "Open Sans" family). <paramref name="webFonts"/> may be <c>null</c>
    /// when the caller has no document context (e.g. the standalone text
    /// measurer unit tests).
    /// </summary>
    public static FontCollection LoadCollection(FontFaceRegistry? webFonts = null)
    {
        var log = NullLoggerFactory.Instance.CreateLogger(typeof(ImageSharpFontLookup));
        var collection = new FontCollection();
        AddEmbeddedFonts(collection, log);
        var aliases = AddRegisteredWebFonts(collection, webFonts);
        TryAddSystemFonts(collection, log);
        if (aliases.Count > 0)
        {
            s_webFontAliases.AddOrUpdate(collection, aliases);
        }

        return collection;
    }

    // @font-face faces are folded into the SixLabors FontCollection under the
    // font file's own internal name, which Google Fonts' per-weight instances
    // mangle ("Inter Tight Medium"), so the collection can't be queried by the
    // declared family the CSS uses. This side table carries the declared-family
    // → loaded-face alias built by FontFaceRegistry.AddTo, scoped to the exact
    // collection it was built for; it lives as long as the collection does.
    private static readonly ConditionalWeakTable<FontCollection, IReadOnlyDictionary<string, IReadOnlyList<FontFamily>>>
        s_webFontAliases = new();

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<FontFamily>> s_noAliases =
        new Dictionary<string, IReadOnlyList<FontFamily>>();

    /// <summary>
    /// Resolves <paramref name="spec"/> against <paramref name="collection"/>,
    /// returning a <see cref="Font"/> at the requested <paramref name="size"/>.
    /// Walks the family list, expanding CSS generics (<c>sans-serif</c>,
    /// <c>serif</c>, etc.) the same way <see cref="FontResolver"/> does, then
    /// falls back to the first family in the collection (always the bundled
    /// OpenSans). Returns the requested <see cref="FontStyle"/> only if the
    /// matched family carries that face — callers should not assume
    /// synthesised bold/italic.
    /// </summary>
    public static Font CreateFont(FontCollection collection, FontSpec spec, float size)
    {
        var style = (spec.Bold, spec.Italic) switch
        {
            (true, true) => FontStyle.BoldItalic,
            (true, false) => FontStyle.Bold,
            (false, true) => FontStyle.Italic,
            _ => FontStyle.Regular,
        };

        var aliases = s_webFontAliases.TryGetValue(collection, out var map) ? map : s_noAliases;

        // CSS font matching is per-family: walk the font-family list in order
        // and take the first family that exists, then pick the nearest style
        // *within* that family. A later family in the list must never win just
        // because it happens to carry the exact requested style — otherwise a
        // bold heading in a web font that ships only a medium face (Inter Tight)
        // skips to bold Times from the `serif` fallback instead of staying in
        // the author's family.
        foreach (var candidate in EnumerateCandidates(spec.Families))
        {
            if (TryResolveFamily(collection, aliases, candidate, style, out var family))
            {
                return family.CreateFont(size, ResolveStyle(family, style));
            }
        }

        // Last-resort fallback: prefer the bundled Open Sans (the documented
        // terminal fallback), then the first available family as a true last resort.
        if (TryResolveFamily(collection, aliases, "Open Sans", style, out var openSans))
        {
            return openSans.CreateFont(size, ResolveStyle(openSans, style));
        }

        foreach (var family in collection.Families)
        {
            return family.CreateFont(size, ResolveStyle(family, style));
        }

        throw new InvalidOperationException(
            "ImageSharpFontLookup: no fonts available. Ensure Starling.Paint.dll bundles at least one TTF/OTF embedded resource.");
    }

    /// <summary>
    /// Resolves a single CSS family name to a <see cref="FontFamily"/>. A
    /// declared <c>@font-face</c> family (via <paramref name="aliases"/>) wins
    /// over a same-named bundled/system face, matching the cascade. When several
    /// faces are registered under one declared family — Google Fonts splits each
    /// weight into its own file with its own internal name — the one carrying the
    /// requested style is preferred.
    /// </summary>
    private static bool TryResolveFamily(
        FontCollection collection,
        IReadOnlyDictionary<string, IReadOnlyList<FontFamily>> aliases,
        string name,
        FontStyle style,
        out FontFamily family)
    {
        if (aliases.TryGetValue(name, out var aliased) && aliased.Count > 0)
        {
            family = PickByStyle(aliased, style);
            return true;
        }
        return collection.TryGet(name, out family);
    }

    private static FontFamily PickByStyle(IReadOnlyList<FontFamily> families, FontStyle style)
    {
        foreach (var f in families)
        {
            if (HasStyle(f, style))
            {
                return f;
            }
        }

        return families[0];
    }

    /// <summary>
    /// The face to render once a family is chosen: the requested style if the
    /// family has it, else Regular, else whatever single style the family ships.
    /// Never asks SixLabors for a style the family lacks (it would throw), and
    /// never synthesises bold/italic.
    /// </summary>
    private static FontStyle ResolveStyle(FontFamily family, FontStyle requested)
    {
        if (HasStyle(family, requested))
        {
            return requested;
        }

        if (HasStyle(family, FontStyle.Regular))
        {
            return FontStyle.Regular;
        }

        var styles = family.GetAvailableStyles().Span;
        return styles.Length > 0 ? styles[0] : FontStyle.Regular;
    }

    private static bool HasStyle(FontFamily family, FontStyle style)
    {
        var styles = family.GetAvailableStyles().Span;
        for (var i = 0; i < styles.Length; i++)
        {
            if (styles[i] == style)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateCandidates(IReadOnlyList<string> families)
    {
        foreach (var family in families)
        {
            // Family keying is quote/whitespace-insensitive: canonicalise once
            // per candidate (allocation-free when the name is already clean) so
            // a quote-laden name still hits the @font-face alias map and the
            // collection. Case-insensitivity comes from the dictionaries.
            var name = FontFamilyKey.Normalize(family);
            yield return name;
            foreach (var sub in ExpandGeneric(name))
            {
                yield return sub;
            }
        }
    }

    /// <summary>
    /// CSS generic-family expansions, mirroring <see cref="FontResolver.ExpandGeneric"/>.
    /// macOS names come first because osx-arm64 is the only shipped RID today.
    /// Keyed OrdinalIgnoreCase so the resolve path never lowercases candidates.
    /// </summary>
    private static readonly FrozenDictionary<string, string[]> s_genericExpansions = BuildGenericExpansions();

    private static string[] ExpandGeneric(string family)
        => s_genericExpansions.TryGetValue(family, out var expansion) ? expansion : [];

    private static FrozenDictionary<string, string[]> BuildGenericExpansions()
    {
        string[] systemUi = ["-apple-system", "system-ui", "SF Pro Text", "Helvetica Neue", "Segoe UI", "Roboto"];
        return new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["serif"] = ["Times New Roman", "Times", "Georgia", "Liberation Serif", "DejaVu Serif", "Noto Serif"],
            ["sans-serif"] = ["Helvetica Neue", "Helvetica", "Arial", "Inter", "Liberation Sans", "DejaVu Sans", "Segoe UI", "Noto Sans", "Verdana"],
            ["monospace"] = ["Menlo", "Monaco", "SF Mono", "Courier New", "Courier", "Liberation Mono", "DejaVu Sans Mono", "Consolas", "Noto Sans Mono"],
            ["cursive"] = ["Snell Roundhand", "Apple Chancery", "Comic Sans MS", "Brush Script MT"],
            ["fantasy"] = ["Papyrus", "Impact", "Herculanum"],
            ["system-ui"] = systemUi,
            ["ui-sans-serif"] = systemUi,
            ["ui-serif"] = ["-apple-system", "Times New Roman", "Times"],
            ["ui-monospace"] = ["Menlo", "SF Mono", "Consolas"],
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static void AddEmbeddedFonts(FontCollection collection, ILogger log)
    {
        var asm = typeof(ImageSharpFontLookup).Assembly;
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            try { collection.Add(stream); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // skip malformed embedded font
                ImageSharpFontLookupLog.EmbeddedFontSkipped(log, ex, name);
            }
        }
    }

    private static void TryAddSystemFonts(FontCollection collection, ILogger log)
    {
        try
        {
            collection.AddSystemFonts();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // System fonts unavailable (sandbox, unsupported platform, etc.).
            // Bundled fonts still serve as the fallback.
            ImageSharpFontLookupLog.SystemFontsUnavailable(log, ex);
        }
    }

    /// <summary>
    /// Folds every font registered via <c>@font-face</c> into
    /// <paramref name="collection"/>. Validation happens at registration time
    /// so CSS Fonts fallback can continue to later <c>src</c> entries when a
    /// downloaded font is unreadable.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<FontFamily>> AddRegisteredWebFonts(
        FontCollection collection, FontFaceRegistry? webFonts)
    {
        if (webFonts is null)
        {
            return s_noAliases;
        }

        return webFonts.AddTo(collection);
    }
}

internal static partial class ImageSharpFontLookupLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "skipping malformed embedded font resource '{ResourceName}'")]
    public static partial void EmbeddedFontSkipped(ILogger logger, Exception ex, string resourceName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "system fonts unavailable; bundled fonts will be used as fallback")]
    public static partial void SystemFontsUnavailable(ILogger logger, Exception ex);
}
