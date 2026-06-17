using Starling.Css.Parser;
using Starling.Css.Tokenizer;

namespace Starling.Css.ViewTransitions;

/// <summary>
/// Extracts <see cref="ViewTransitionRule"/> values from parsed stylesheets. The
/// CSS parser recognises <c>@view-transition</c> as an at-rule with a declaration
/// list (its descriptors); this turns the <c>navigation</c> and <c>types</c>
/// descriptors into a strongly-typed model (CSS View Transitions 1 §2.1).
/// </summary>
public static class ViewTransitionParser
{
    public static IEnumerable<ViewTransitionRule> ParseAll(StyleSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        foreach (var rule in sheet.Rules)
        {
            if (rule is AtRule atRule
                && string.Equals(atRule.Name, "view-transition", StringComparison.OrdinalIgnoreCase))
            {
                yield return Parse(atRule);
            }
        }
    }

    public static ViewTransitionRule Parse(AtRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var navigation = "none";
        var types = new List<string>();

        foreach (var decl in rule.Declarations)
        {
            switch (decl.Name.ToLowerInvariant())
            {
                case "navigation":
                    var nav = FirstIdent(decl.Value);
                    if (nav is "auto" or "none")
                    {
                        navigation = nav;
                    }

                    break;
                case "types":
                    foreach (var v in decl.Value)
                    {
                        if (v is CssTokenValue { Token: { Type: CssTokenType.Ident, Value: var t } }
                            && !t.Equals("none", StringComparison.OrdinalIgnoreCase))
                        {
                            types.Add(t);
                        }
                    }

                    break;
            }
        }

        return new ViewTransitionRule(navigation, types);
    }

    private static string? FirstIdent(IReadOnlyList<CssComponentValue> value)
    {
        foreach (var v in value)
        {
            if (v is CssTokenValue { Token: { Type: CssTokenType.Ident, Value: var ident } })
            {
                return ident.ToLowerInvariant();
            }
        }

        return null;
    }
}
