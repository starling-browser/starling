namespace Starling.Css.Values;

/// <summary>
/// Parses the <c>box-shadow</c> property value into a strongly-typed
/// <see cref="CssBoxShadow"/>. Input is the generic <see cref="CssValue"/>
/// produced by <see cref="CssValueParser"/>: the <c>none</c> keyword, a single
/// layer, or a comma-separated list of layers. Each layer is
/// <c>&lt;color&gt;? &amp;&amp; [&lt;length&gt;{2,4} &amp;&amp; inset?]</c> per
/// <see href="https://www.w3.org/TR/css-backgrounds-3/#box-shadow">CSS
/// Backgrounds 3 §6</see>.
/// <para>
/// Fail-soft: any malformed layer drops the whole declaration to
/// <see cref="CssBoxShadow.None"/>, matching the spec rule that an invalid
/// value leaves the property unset.
/// </para>
/// </summary>
public static class CssBoxShadowParser
{
    public static CssBoxShadow Parse(CssValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        // Idempotent: the animation interpolator emits typed CssBoxShadow
        // intermediates; pass them straight through.
        if (value is CssBoxShadow typed)
        {
            return typed;
        }

        if (value is CssKeyword { Name: var kw } && kw.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return CssBoxShadow.None;
        }

        var components = value switch
        {
            CssValueList list => list.Values,
            _ => [value],
        };

        var layers = new List<CssShadow>();
        var current = new List<CssValue>();
        foreach (var component in components)
        {
            if (IsCommaSeparator(component))
            {
                if (!TryParseLayer(current, out var parsed))
                {
                    return CssBoxShadow.None;
                }

                layers.Add(parsed);
                current = [];
                continue;
            }

            current.Add(component);
        }

        if (!TryParseLayer(current, out var last))
        {
            return CssBoxShadow.None;
        }

        layers.Add(last);

        return new CssBoxShadow(layers);
    }

    // The value parser turns a top-level comma token into a keyword with an
    // empty (or literal ",") name; real CSS idents are never empty, so this is
    // an unambiguous layer separator.
    private static bool IsCommaSeparator(CssValue value)
        => value is CssKeyword { Name: "" or "," };

    private static bool TryParseLayer(IReadOnlyList<CssValue> parts, out CssShadow shadow)
    {
        shadow = null!;
        var lengths = new List<CssLength>(4);
        CssColor? color = null;
        var inset = false;

        foreach (var part in parts)
        {
            switch (part)
            {
                case CssKeyword { Name: var name } when name.Equals("inset", StringComparison.OrdinalIgnoreCase):
                    if (inset)
                    {
                        return false; // duplicate inset
                    }

                    inset = true;
                    break;
                case CssKeyword { Name: var name } when name.Equals("currentcolor", StringComparison.OrdinalIgnoreCase):
                    if (color is not null)
                    {
                        return false;
                    }

                    color = null; // sentinel: resolved against the element's color at paint
                    break;
                case CssColor c:
                    if (color is not null)
                    {
                        return false;
                    }

                    color = c;
                    break;
                case CssLength len:
                    if (lengths.Count == 4)
                    {
                        return false;
                    }

                    lengths.Add(len);
                    break;
                case CssNumber { Value: 0 }:
                    // Unit-less zero is a valid <length>.
                    if (lengths.Count == 4)
                    {
                        return false;
                    }

                    lengths.Add(CssLength.Zero);
                    break;
                default:
                    return false; // unexpected component → invalid layer
            }
        }

        // Need at least offset-x and offset-y.
        if (lengths.Count < 2)
        {
            return false;
        }

        var offsetX = lengths[0];
        var offsetY = lengths[1];
        var blur = lengths.Count > 2 ? lengths[2] : CssLength.Zero;
        var spread = lengths.Count > 3 ? lengths[3] : CssLength.Zero;

        // Blur radius must be non-negative per spec.
        if (blur.Value < 0)
        {
            return false;
        }

        shadow = new CssShadow(offsetX, offsetY, blur, spread, color, inset);
        return true;
    }
}
