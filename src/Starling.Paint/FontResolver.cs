namespace Starling.Paint;

/// <summary>
/// Public seam for font resolution. Historically wrapped Skia's
/// <c>SkFontMgr</c> + <c>SkTypeface</c>; after the Skia/Graphite shim was
/// removed the engine paints through ImageSharp.Drawing 3, which owns its own
/// <see cref="SixLabors.Fonts.FontCollection"/>. This type is kept so the
/// public <see cref="Painter"/> constructor signature stays stable for
/// callers; the only state it tracks today is whether a custom instance has
/// been handed out so disposal still flows through.
/// </summary>
public sealed class FontResolver : IDisposable
{
    public static readonly FontResolver Default = new();

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }

    /// <summary>
    /// Iterates the candidate families for the given CSS font-family list,
    /// expanding generic keywords (<c>serif</c>, <c>sans-serif</c>,
    /// <c>monospace</c>, etc.) into ordered platform fallbacks. The ImageSharp
    /// text path consults <see cref="SixLabors.Fonts.FontCollection"/> family-by-family
    /// in this order before picking the bundled default.
    /// </summary>
    internal static IEnumerable<string> ExpandFamilies(IReadOnlyList<string> families)
    {
        foreach (var family in families)
        {
            yield return family;
            foreach (var sub in ExpandGeneric(family))
            {
                yield return sub;
            }
        }
    }

    private static IEnumerable<string> ExpandGeneric(string family) => family.ToLowerInvariant() switch
    {
        "serif" => ["Times New Roman", "Times", "Georgia", "Liberation Serif", "DejaVu Serif", "Noto Serif"],
        "sans-serif" => ["Helvetica Neue", "Helvetica", "Arial", "Inter", "Liberation Sans", "DejaVu Sans", "Segoe UI", "Noto Sans", "Verdana"],
        "monospace" => ["Menlo", "Monaco", "SF Mono", "Courier New", "Courier", "Liberation Mono", "DejaVu Sans Mono", "Consolas", "Noto Sans Mono"],
        "cursive" => ["Snell Roundhand", "Apple Chancery", "Comic Sans MS", "Brush Script MT"],
        "fantasy" => ["Papyrus", "Impact", "Herculanum"],
        "system-ui" or "ui-sans-serif" => ["-apple-system", "system-ui", "SF Pro Text", "Helvetica Neue", "Segoe UI", "Roboto"],
        "ui-serif" => ["-apple-system", "Times New Roman", "Times"],
        "ui-monospace" => ["Menlo", "SF Mono", "Consolas"],
        _ => [],
    };
}
