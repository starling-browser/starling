// SPDX-License-Identifier: Apache-2.0
using Starling.Css.Properties;
using Starling.Css.Values;
using Starling.Layout.Scroll;
using DomElement = Starling.Dom.Element;
using LayoutBox = Starling.Layout.Box.Box;

namespace Starling.Gui;

/// <summary>
/// Shared wheel-scroll routing for every shell (browser-plan/scroll-model.md,
/// "Input and events"). Takes the hit-tested box under the pointer and a wheel
/// delta, walks up the box tree for the deepest scroll container with room to
/// move — latched per axis, so a diagonal delta can scroll one container
/// vertically and an ancestor horizontally — writes the offsets into the
/// page's <see cref="ScrollStateStore"/>, and reports whether anything
/// consumed the delta. The caller falls through to its own root scroller
/// (the Avalonia <c>ScrollViewer</c> / the native shell's <c>scrollY</c>)
/// when nothing did.
/// </summary>
/// <remarks>
/// <para>This replaces the per-panel offset dictionary and its direct-children
/// content-extent guess: room-to-move now comes from the store's measured
/// scrollport + scrollable overflow, so deep and positioned descendants count.</para>
/// <para>Writes only set store state (offset + pending-event flag). No event
/// dispatch, no relayout — the caller schedules a repaint. Runs per wheel
/// tick: the walk allocates nothing.</para>
/// </remarks>
public static class ScrollController
{
    /// <summary>CSS px per wheel line for non-precision ticks — the constant
    /// both shells already used (scroll-model.md, Decision 3).</summary>
    public const double LinePixels = 40d;

    /// <summary>
    /// Route a wheel delta starting at <paramref name="hitBox"/>. Deltas are
    /// content-space: positive <paramref name="deltaY"/> scrolls down
    /// (offset grows), positive <paramref name="deltaX"/> scrolls right.
    /// With <paramref name="precise"/> unset the deltas are wheel lines and
    /// are converted at <see cref="LinePixels"/> px per line; a precision
    /// (trackpad pixel) delta passes through unscaled when the input event
    /// flags it (Decision 3). Returns true when any scroll container
    /// consumed any of the delta.
    /// </summary>
    public static bool TryScroll(ScrollStateStore store, LayoutBox? hitBox, double deltaX, double deltaY, bool precise)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (hitBox is null)
        {
            return false;
        }

        var dx = precise ? deltaX : deltaX * LinePixels;
        var dy = precise ? deltaY : deltaY * LinePixels;
        if (dx == 0 && dy == 0)
        {
            return false;
        }

        // Latch each axis to its own deepest scroller with room in the delta's
        // direction. The axes can land on different containers (an inner
        // vertical feed inside an outer horizontal strip).
        var targetX = dx != 0 ? FindScroller(store, hitBox, dx, vertical: false) : null;
        var targetY = dy != 0 ? FindScroller(store, hitBox, dy, vertical: true) : null;
        if (targetX is null && targetY is null)
        {
            return false;
        }

        if (targetX is not null && ReferenceEquals(targetX, targetY))
        {
            Apply(store, targetX, dx, dy);
            return true;
        }
        if (targetX is not null)
        {
            Apply(store, targetX, dx, 0);
        }

        if (targetY is not null)
        {
            Apply(store, targetY, 0, dy);
        }

        return true;
    }

    /// <summary>
    /// Walk from <paramref name="hitBox"/> up the parent chain for the first
    /// (deepest) box that scrolls on the requested axis per its computed
    /// <c>overflow-x</c>/<c>overflow-y</c> AND has room to move in the
    /// delta's direction per the store's measured geometry. Style gates the
    /// axis — a wide <c>overflow-y</c>-only box must not pan sideways even
    /// though its measured overflow would allow it.
    /// </summary>
    private static DomElement? FindScroller(ScrollStateStore store, LayoutBox hitBox, double delta, bool vertical)
    {
        for (var node = (LayoutBox?)hitBox; node is not null; node = node.Parent)
        {
            if (node.Style is not { } style || node.Element is not { } el)
            {
                continue;
            }

            var axisValue = style.Get(vertical ? PropertyId.OverflowY : PropertyId.OverflowX);
            if (!IsScrollKeyword(axisValue))
            {
                continue;
            }

            if (!store.TryGet(el, out var state))
            {
                continue;
            }

            var offset = vertical ? state.OffsetY : state.OffsetX;
            var max = vertical ? state.MaxOffsetY : state.MaxOffsetX;
            // Room in the delta's direction; a scroller pinned at the rail
            // lets the delta chain to an ancestor.
            if (delta > 0 ? offset < max : offset > 0)
            {
                return el;
            }
        }
        return null;
    }

    private static void Apply(ScrollStateStore store, DomElement element, double dx, double dy)
    {
        store.TryGet(element, out var state); // found by FindScroller, so present
        store.Write(element, state.OffsetX + dx, state.OffsetY + dy);
    }

    private static bool IsScrollKeyword(CssValue? value)
        => value is CssKeyword { Name: var n }
           && (n.Equals("scroll", StringComparison.OrdinalIgnoreCase)
            || n.Equals("auto", StringComparison.OrdinalIgnoreCase));
}
