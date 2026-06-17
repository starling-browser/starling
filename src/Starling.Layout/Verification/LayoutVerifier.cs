using System.Globalization;
using Starling.Dom;
using Starling.Layout.Box;

namespace Starling.Layout.Verification;

/// <summary>
/// One geometry or structure mismatch found between two layout outputs that are
/// expected to be identical. <see cref="Path"/> locates the box in the tree
/// (e.g. <c>html &gt; body &gt; div[2]</c>), <see cref="Element"/> is the DOM
/// element it came from (null for anonymous/text boxes), and <see cref="Field"/>
/// names exactly what differs (e.g. <c>Frame</c>, <c>Margin.Left</c>,
/// <c>Fragments[1].X</c>, <c>Kind</c>, <c>ChildCount</c>).
/// </summary>
public readonly record struct LayoutDivergence(
    string Path,
    Element? Element,
    string Field,
    string Expected,
    string Actual)
{
    public override string ToString()
        => $"{Path}: {Field} expected {Expected} but was {Actual}";
}

/// <summary>
/// Phase 0d safety net for incremental layout. Runs two layout outputs that
/// should be byte-for-byte equivalent — today a full rebuild vs. a second full
/// rebuild (an identity check that proves the harness itself), and later the
/// incremental path vs. a full rebuild — and reports the <em>first</em> place
/// their fragment geometry diverges. This is how silent mispositioning (the
/// worst failure mode of incremental layout, because a stale-but-plausible
/// frame passes a glance) is caught before it reaches a user.
/// </summary>
/// <remarks>
/// The comparison walks both trees in lockstep, short-circuiting on the first
/// difference so the report points at the root cause rather than every
/// downstream box knocked out of place by it. It is read-only: it never mutates
/// either tree.
/// </remarks>
public static class LayoutVerifier
{
    /// <summary>Set to <c>1</c> to make <see cref="LayoutEngine"/> dual-run and
    /// verify every layout. Off by default — the dual run doubles layout cost,
    /// so it is a debug/CI tool, not a production path.</summary>
    public const string EnvVar = "STARLING_LAYOUT_VERIFY";

    /// <summary>Whether <see cref="EnvVar"/> requests verification.</summary>
    public static bool Enabled =>
        string.Equals(
            Environment.GetEnvironmentVariable(EnvVar), "1", StringComparison.Ordinal);

    /// <summary>
    /// Layout geometry is produced by deterministic <see cref="double"/>
    /// arithmetic, so two runs of the same algorithm match exactly and the
    /// incremental path — which <em>reuses</em> a cached fragment rather than
    /// recomputing it — matches even more trivially. This epsilon absorbs only
    /// representational noise (summation reordering across a parallel cascade,
    /// say) while still catching real mispositioning, which is never sub-pixel.
    /// </summary>
    public const double DefaultEpsilon = 1e-4;

    /// <summary>
    /// Returns the first place <paramref name="expected"/> and
    /// <paramref name="actual"/> diverge, or <c>null</c> when the two trees are
    /// geometrically identical within <paramref name="epsilon"/>.
    /// </summary>
    public static LayoutDivergence? FindFirstDivergence(
        Box.Box expected, Box.Box actual, double epsilon = DefaultEpsilon)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        return Compare(expected, actual, RootPath(expected), epsilon);
    }

    private static LayoutDivergence? Compare(Box.Box e, Box.Box a, string path, double eps)
    {
        if (e.Kind != a.Kind)
        {
            return new LayoutDivergence(path, e.Element ?? a.Element, "Kind", e.Kind.ToString(), a.Kind.ToString());
        }

        if (RectDiff(e.Frame, a.Frame, eps) is { } rf)
        {
            return new LayoutDivergence(path, e.Element, "Frame." + rf, Fmt(e.Frame), Fmt(a.Frame));
        }

        if (EdgeDiff(e.Margin, a.Margin, eps) is { } mf)
        {
            return new LayoutDivergence(path, e.Element, "Margin." + mf, Fmt(e.Margin), Fmt(a.Margin));
        }

        if (EdgeDiff(e.Padding, a.Padding, eps) is { } pf)
        {
            return new LayoutDivergence(path, e.Element, "Padding." + pf, Fmt(e.Padding), Fmt(a.Padding));
        }

        if (EdgeDiff(e.Border, a.Border, eps) is { } bf)
        {
            return new LayoutDivergence(path, e.Element, "Border." + bf, Fmt(e.Border), Fmt(a.Border));
        }

        // Text leaves carry their own value-typed line fragments — the glyph-run
        // payload incremental layout reuses wholesale — so compare them too.
        if (e is TextBox et && a is TextBox at)
        {
            if (!string.Equals(et.Text, at.Text, StringComparison.Ordinal))
            {
                return new LayoutDivergence(path, null, "Text", Quote(et.Text), Quote(at.Text));
            }

            if (et.Fragments.Count != at.Fragments.Count)
            {
                return new LayoutDivergence(path, null, "Fragments.Count",
                    et.Fragments.Count.ToString(CultureInfo.InvariantCulture),
                    at.Fragments.Count.ToString(CultureInfo.InvariantCulture));
            }

            for (var i = 0; i < et.Fragments.Count; i++)
            {
                if (FragmentDiff(et.Fragments[i], at.Fragments[i], eps) is { } ff)
                {
                    return new LayoutDivergence(path, null, $"Fragments[{i}].{ff.Field}", ff.Expected, ff.Actual);
                }
            }
        }

        if (e.Children.Count != a.Children.Count)
        {
            return new LayoutDivergence(path, e.Element, "ChildCount",
                e.Children.Count.ToString(CultureInfo.InvariantCulture),
                a.Children.Count.ToString(CultureInfo.InvariantCulture));
        }

        for (var i = 0; i < e.Children.Count; i++)
        {
            var child = e.Children[i];
            var childPath = path + " > " + ChildLabel(child, i);
            if (Compare(child, a.Children[i], childPath, eps) is { } d)
            {
                return d;
            }
        }

        return null;
    }

    // --- field-level diffs (return the offending sub-field name, or null) -----

    private static string? RectDiff(Rect e, Rect a, double eps)
    {
        if (!Near(e.X, a.X, eps))
        {
            return "X";
        }

        if (!Near(e.Y, a.Y, eps))
        {
            return "Y";
        }

        if (!Near(e.Width, a.Width, eps))
        {
            return "Width";
        }

        if (!Near(e.Height, a.Height, eps))
        {
            return "Height";
        }

        return null;
    }

    private static string? EdgeDiff(Edges e, Edges a, double eps)
    {
        if (!Near(e.Top, a.Top, eps))
        {
            return "Top";
        }

        if (!Near(e.Right, a.Right, eps))
        {
            return "Right";
        }

        if (!Near(e.Bottom, a.Bottom, eps))
        {
            return "Bottom";
        }

        if (!Near(e.Left, a.Left, eps))
        {
            return "Left";
        }

        return null;
    }

    private static (string Field, string Expected, string Actual)? FragmentDiff(
        TextFragment e, TextFragment a, double eps)
    {
        if (!string.Equals(e.Text, a.Text, StringComparison.Ordinal))
        {
            return ("Text", Quote(e.Text), Quote(a.Text));
        }

        if (!Near(e.X, a.X, eps))
        {
            return ("X", Num(e.X), Num(a.X));
        }

        if (!Near(e.Y, a.Y, eps))
        {
            return ("Y", Num(e.Y), Num(a.Y));
        }

        if (!Near(e.Width, a.Width, eps))
        {
            return ("Width", Num(e.Width), Num(a.Width));
        }

        if (!Near(e.Height, a.Height, eps))
        {
            return ("Height", Num(e.Height), Num(a.Height));
        }

        if (!Near(e.Baseline, a.Baseline, eps))
        {
            return ("Baseline", Num(e.Baseline), Num(a.Baseline));
        }

        return null;
    }

    private static bool Near(double a, double b, double eps) => Math.Abs(a - b) <= eps;

    // --- path + value formatting ----------------------------------------------

    private static string RootPath(Box.Box root) => ChildLabel(root, 0);

    private static string ChildLabel(Box.Box b, int index)
    {
        if (b.Element is { } el)
        {
            var id = el.Id;
            return id.Length > 0 ? $"{el.LocalName}#{id}" : $"{el.LocalName}[{index}]";
        }
        return b.Kind switch
        {
            BoxKind.Text => $"#text[{index}]",
            BoxKind.AnonymousBlock => $"#anon[{index}]",
            _ => $"{b.Kind}[{index}]",
        };
    }

    private static string Fmt(Rect r)
        => $"({Num(r.X)},{Num(r.Y)} {Num(r.Width)}x{Num(r.Height)})";

    private static string Fmt(Edges e)
        => $"[{Num(e.Top)},{Num(e.Right)},{Num(e.Bottom)},{Num(e.Left)}]";

    private static string Num(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Quote(string s) => "\"" + s + "\"";
}
