using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Common.Diagnostics;
using Starling.Css.Cascade;
using Starling.Dom;
using Starling.Layout.Block;
using Starling.Layout.Box;
using Starling.Layout.Text;
using Starling.Layout.Tree;
using Starling.Layout.Verification;

namespace Starling.Layout;

/// <summary>
/// Top-level layout façade. Consumes a parsed <see cref="Document"/>, runs the
/// style engine, builds a box tree, then performs layout.
/// </summary>
/// <remarks>
/// Current scope covers block and inline layout, floats, positioning, flexbox,
/// grid, word wrap, and simple adjacent-sibling margin collapse. Table
/// formatting is still approximated through the user-agent stylesheet. The box
/// tree's <c>Frame</c> values are CSS px in the document's coordinate space.
/// </remarks>
public sealed class LayoutEngine
{
    private readonly StyleEngine _style;
    private readonly ITextMeasurer _measurer;
    private readonly IImageResolver _images;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _log;
    private readonly CancellationToken _abort;

    public LayoutEngine(
        StyleEngine style,
        ITextMeasurer? measurer = null,
        IImageResolver? images = null,
        ILoggerFactory? loggerFactory = null,
        CancellationToken abort = default)
    {
        ArgumentNullException.ThrowIfNull(style);
        _style = style;
        _measurer = measurer ?? DefaultTextMeasurer.Instance;
        _images = images ?? NullImageResolver.Instance;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _log = _loggerFactory.CreateLogger<LayoutEngine>();
        _abort = abort;
    }

    /// <summary>
    /// Dual-run layout verification. When set, every <c>LayoutDocument</c>
    /// call lays the document out a second time and checks that the two outputs are
    /// geometrically identical, logging the first divergence. Defaults to the
    /// <see cref="LayoutVerifier.EnvVar"/> env switch; tests set it directly.
    /// Doubles layout cost, so it is a debug/CI tool only.
    /// </summary>
    public bool VerifyLayout { get; init; } = LayoutVerifier.Enabled;

    /// <summary>
    /// Optional per-document scroll store (browser-plan/scroll-model.md WP1).
    /// When set, every <c>LayoutDocument</c> call runs under the store's
    /// layout gate (offset writes mid-pass throw in DEBUG), measures each
    /// scroll container's scrollport + scrollable overflow after the position
    /// pass, and re-clamps every stored offset against the fresh geometry.
    /// Null (the default) leaves layout byte-for-byte unchanged.
    /// </summary>
    public Scroll.ScrollStateStore? ScrollState { get; init; }

    public BlockBox LayoutDocument(Document document, Size viewport)
        => LayoutDocument(document, viewport, nowMs: null);

    /// <summary>
    /// Layout overload that threads a monotonic frame clock into the cascade.
    /// When <paramref name="nowMs"/> is non-null, each element is computed
    /// via <see cref="StyleEngine.ComputeWithAnimations"/> so the box tree
    /// reflects any in-flight CSS animations + transitions at that instant.
    /// </summary>
    public BlockBox LayoutDocument(Document document, Size viewport, double? nowMs)
    {
        ArgumentNullException.ThrowIfNull(document);

        var scroll = ScrollState;
        scroll?.BeginLayoutPass();
        try
        {
            var root = LayoutOnce(document, viewport, nowMs);

            if (VerifyLayout)
            {
                Verify(root, document, viewport, nowMs);
            }

            if (scroll is not null)
            {
                // Measure after the position pass (LayoutOnce ran it): the
                // overflow union includes positioned descendants, whose
                // frames only exist now. Then re-clamp every stored offset
                // against the fresh geometry.
                Scroll.ScrollOverflowMeasurer.Measure(root, viewport, scroll);
                scroll.ReconcileAfterLayout();
            }

            return root;
        }
        finally
        {
            scroll?.EndLayoutPass();
        }
    }

    /// <summary>
    /// Re-lays the document out a second time (full rebuild) and compares it to
    /// <paramref name="produced"/>. Today both sides are a full rebuild, so this
    /// is an identity check that proves the harness itself; once the incremental
    /// path lands, <paramref name="produced"/> becomes the incremental output and
    /// this stays the always-correct reference it is checked against.
    /// </summary>
    private void Verify(BlockBox produced, Document document, Size viewport, double? nowMs)
    {
        using var _ = StarlingTelemetry.Span("layout", "verify");
        var reference = LayoutOnce(document, viewport, nowMs);
        var divergence = LayoutVerifier.FindFirstDivergence(produced, reference);
        if (divergence is { } d)
        {
            StarlingTelemetry.Counter("layout.verify.divergent", 1);
            LayoutEngineLog.LayoutDivergence(_log, d.ToString());
        }
        else
        {
            StarlingTelemetry.Counter("layout.verify.ok", 1);
        }
    }

    private BlockBox LayoutOnce(Document document, Size viewport, double? nowMs)
    {
        StarlingTelemetry.Counter("layout.runs", 1);
        using var span = StarlingTelemetry.Span("layout", "run");
        Activity.Current?.SetTag("layout.viewport_width", viewport.Width);
        Activity.Current?.SetTag("layout.viewport_height", viewport.Height);

        BlockBox root;
        using (StarlingTelemetry.Span("layout", "box_tree_build"))
        {
            var builder = new BoxTreeBuilder(_style, _images, nowMs);
            root = builder.Build(document);
        }

        BlockLayout block;
        using (StarlingTelemetry.Span("layout", "block"))
        {
            block = new BlockLayout(_measurer, viewport, _abort);
            block.Layout(root);
        }

        // Second pass: place position:absolute / fixed descendants and apply
        // position:relative offsets. The viewport rect doubles as the
        // initial containing block and as the fixed-positioning anchor.
        using (StarlingTelemetry.Span("layout", "position"))
        {
            var positioning = new Starling.Layout.Position.PositionLayout(block, viewport, ScrollState);
            positioning.LayoutPositioned(root);
        }

        return root;
    }
}

internal static partial class LayoutEngineLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "layout divergence: {Divergence}")]
    public static partial void LayoutDivergence(ILogger logger, string divergence);
}
