using Starling.Layout.Text;

namespace Starling.Bench.Replay;

/// <summary>
/// Wraps a real <see cref="ITextMeasurer"/> and counts how often layout asks it
/// to measure and shape text, plus an approximate shape-cache hit rate. The hit
/// rate is judged the way the inner measurer's own cache works: a
/// <c>(text, size, spec)</c> key seen before is a hit, a new key is a miss. The
/// seen-key set lives for the whole run (the real cache survives across frames
/// too), so a node re-shaped on a later frame counts as a hit. The harness reads
/// and resets the per-frame call counts at each frame boundary.
/// </summary>
public sealed class CountingTextMeasurer : ITextMeasurer, IDisposable
{
    private readonly ITextMeasurer _inner;
    private readonly HashSet<(string Text, double Size, FontSpec Spec)> _seen = new();

    private long _measureWidthCalls;
    private long _shapeCalls;
    private long _shapeHits;
    private long _shapeMisses;

    public CountingTextMeasurer(ITextMeasurer inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public double MeasureWidth(string text, double fontSize, FontSpec spec)
    {
        _measureWidthCalls++;
        return _inner.MeasureWidth(text, fontSize, spec);
    }

    public ShapedRun Shape(string text, double fontSize, FontSpec spec)
    {
        _shapeCalls++;
        if (_seen.Add((text, fontSize, spec)))
        {
            _shapeMisses++;
        }
        else
        {
            _shapeHits++;
        }

        return _inner.Shape(text, fontSize, spec);
    }

    public double NormalLineHeight(double fontSize, FontSpec spec) => _inner.NormalLineHeight(fontSize, spec);

    public double Baseline(double fontSize, FontSpec spec) => _inner.Baseline(fontSize, spec);

    /// <summary>Read the call counts accumulated since the last snapshot and reset them.</summary>
    public FrameMeasureCounters TakeFrameSnapshot()
    {
        var snap = new FrameMeasureCounters(_measureWidthCalls, _shapeCalls, _shapeHits, _shapeMisses);
        _measureWidthCalls = 0;
        _shapeCalls = 0;
        _shapeHits = 0;
        _shapeMisses = 0;
        return snap;
    }

    public void Dispose() => (_inner as IDisposable)?.Dispose();
}

public readonly record struct FrameMeasureCounters(
    long MeasureWidthCalls, long ShapeCalls, long ShapeHits, long ShapeMisses);
