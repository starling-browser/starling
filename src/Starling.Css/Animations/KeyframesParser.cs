using Starling.Css.Parser;
using Starling.Css.Tokenizer;
using Starling.Css.Values;

namespace Starling.Css.Animations;

/// <summary>
/// Extracts <see cref="KeyframesRule"/> values from parsed stylesheets. The
/// CSS parser already recognises <c>@keyframes</c> as a nested-rule at-rule
/// (see <see cref="Parser.CssParser"/>); this turns those nested rules into
/// a strongly-typed keyframes description the animation engine can sample.
/// </summary>
/// <remarks>
/// Fail-soft (CSS Animations 1 §4): a rule with no name is skipped; nested
/// rules that aren't valid keyframe selectors are dropped; declarations
/// inside a keyframe that can't be parsed as <see cref="CssValue"/> are
/// dropped (the rest of the rule still applies). The vendor-prefixed
/// <c>@-webkit-keyframes</c> / <c>@-moz-keyframes</c> aliases are recognised —
/// many in-the-wild stylesheets still ship them.
/// </remarks>
public static class KeyframesParser
{
    public static IEnumerable<KeyframesRule> ParseAll(StyleSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        foreach (var rule in sheet.Rules)
        {
            if (rule is AtRule atRule && IsKeyframes(atRule.Name) && TryParse(atRule, out var keyframes))
            {
                yield return keyframes!;
            }
        }
    }

    public static bool TryParse(AtRule rule, out KeyframesRule? keyframes)
    {
        ArgumentNullException.ThrowIfNull(rule);
        keyframes = null;
        if (!IsKeyframes(rule.Name))
        {
            return false;
        }

        var name = ExtractName(rule.Prelude);
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var frames = new List<Keyframe>();
        foreach (var nested in rule.Rules)
        {
            if (nested is not StyleRule style)
            {
                continue;
            }

            var offsets = ParseKeyframeSelectors(style.Prelude);
            if (offsets.Count == 0)
            {
                continue;
            }

            var declarations = new List<KeyframeDeclaration>(style.Declarations.Count);
            TimingFunction? segmentTiming = null;
            foreach (var decl in style.Declarations)
            {
                // CSS Animations 1 §4.1: !important is ignored inside keyframes.
                CssValue value;
                try { value = CssValueParser.Parse(decl.Value); }
                catch { continue; }

                // §7.1: animation-timing-function declared inside a keyframe
                // overrides the animation-level timing function for the
                // segment *starting at this keyframe*. Strip it from the
                // declaration list (it's not a regular animatable property)
                // and parse it into a TimingFunction.
                if (string.Equals(decl.Name, "animation-timing-function", StringComparison.OrdinalIgnoreCase))
                {
                    segmentTiming = TimingFunction.FromCss(value);
                    continue;
                }

                declarations.Add(new KeyframeDeclaration(decl.Name.ToLowerInvariant(), value));
            }

            foreach (var offset in offsets)
            {
                frames.Add(new Keyframe(offset, declarations, segmentTiming));
            }
        }

        // Stable sort by offset so consumers can binary-search or step linearly.
        frames.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        keyframes = new KeyframesRule(name, frames);
        return true;
    }

    private static bool IsKeyframes(string name)
        => name.Equals("keyframes", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("-webkit-keyframes", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("-moz-keyframes", StringComparison.OrdinalIgnoreCase);

    private static string ExtractName(IReadOnlyList<CssComponentValue> prelude)
    {
        foreach (var component in prelude)
        {
            if (component is CssTokenValue { Token: { Type: CssTokenType.Ident, Value: var ident } })
            {
                return ident;
            }

            if (component is CssTokenValue { Token: { Type: CssTokenType.String, Value: var quoted } })
            {
                return quoted;
            }
        }
        return string.Empty;
    }

    private static List<double> ParseKeyframeSelectors(IReadOnlyList<CssComponentValue> prelude)
    {
        var offsets = new List<double>();
        // Selectors are a comma-separated list of `from` | `to` | <percentage>.
        var current = new List<CssToken>();
        foreach (var component in prelude)
        {
            if (component is not CssTokenValue tv)
            {
                continue;
            }

            var token = tv.Token;
            if (token.Type == CssTokenType.Whitespace)
            {
                continue;
            }

            if (token.Type == CssTokenType.Comma)
            {
                AddIfValid(current, offsets);
                current.Clear();
                continue;
            }
            current.Add(token);
        }
        AddIfValid(current, offsets);
        return offsets;
    }

    private static void AddIfValid(List<CssToken> tokens, List<double> offsets)
    {
        if (tokens.Count != 1)
        {
            return;
        }

        var t = tokens[0];
        switch (t.Type)
        {
            case CssTokenType.Ident when t.Value.Equals("from", StringComparison.OrdinalIgnoreCase):
                offsets.Add(0.0);
                break;
            case CssTokenType.Ident when t.Value.Equals("to", StringComparison.OrdinalIgnoreCase):
                offsets.Add(1.0);
                break;
            case CssTokenType.Percentage:
                var pct = t.Number / 100.0;
                // §4: keyframe selectors outside [0,1] are invalid — drop them.
                if (pct is >= 0 and <= 1)
                {
                    offsets.Add(pct);
                }

                break;
        }
    }
}
