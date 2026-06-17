using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Starling.Gui.Theme;

namespace Starling.Gui.Chrome;

/// <summary>
/// One row of timing bars positioned proportionally across a total duration —
/// Avalonia port of Starling.Gui's Chrome/FlameRowDrawable.cs. Shared by the
/// DevTools Performance panel and the URL-bar <see cref="MiniLoadChart"/>.
/// </summary>
public sealed class FlameRow : Control
{
    public IReadOnlyList<TimingBar> Bars { get; set; } = Array.Empty<TimingBar>();
    public double Total { get; set; } = 1;
    public ThemeTokens Tokens { get; set; } = ThemeTokens.Dark;

    public bool ShowLabels { get; set; }
    public bool ShowCursor { get; set; }
    public double CursorFraction { get; set; }

    public double CornerRadius { get; set; } = 2;
    public double BarOpacity { get; set; } = 1;
    public string? MonoFontFamily { get; set; }

    /// <summary>
    /// Force a repaint after mutating any of the public state. Avalonia's
    /// custom-render path requires an explicit InvalidateVisual; there are no
    /// observable properties on this class.
    /// </summary>
    public void Refresh() => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        if (Total <= 0 || rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        double w = rect.Width, h = rect.Height;

        foreach (var bar in Bars)
        {
            var x = bar.T / Total * w;
            var bw = bar.D / Total * w;
            if (bw < 0.5)
            {
                continue;
            }

            var catColor = Tokens[bar.Cat];
            var fill = new SolidColorBrush(catColor, BarOpacity);
            var barRect = new Rect(x, 0, bw, h);
            context.DrawRectangle(fill, null, barRect, CornerRadius, CornerRadius);

            // Inner hairline for adjacent same-hue bar separation.
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(64, 0, 0, 0)), 1);
            var innerRect = new Rect(x + 0.5, 0.5, Math.Max(0, bw - 1), Math.Max(0, h - 1));
            context.DrawRectangle(null, pen, innerRect, CornerRadius, CornerRadius);

            if (ShowLabels && !string.IsNullOrEmpty(bar.Label) && bw > w * 0.04)
            {
                var typeface = MonoFontFamily is null
                    ? Typeface.Default
                    : new Typeface(new FontFamily(MonoFontFamily));
                var ft = new FormattedText(
                    bar.Label, System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, 10,
                    new SolidColorBrush(Tokens.BarInk))
                {
                    MaxTextWidth = Math.Max(0, bw - 8),
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                var ty = (h - ft.Height) / 2;
                context.DrawText(ft, new Point(x + 4, ty));
            }
        }

        if (ShowCursor)
        {
            var cx = Math.Clamp(CursorFraction, 0, 1) * w;
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(178, Tokens.Text.R, Tokens.Text.G, Tokens.Text.B)), 1);
            context.DrawLine(pen, new Point(cx, -2), new Point(cx, h + 2));
        }
    }
}
