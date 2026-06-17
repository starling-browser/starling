namespace Starling.Css.Selectors;

public static class SpecificityCalculator
{
    public static Specificity Calculate(ComplexSelector selector)
    {
        var result = Specificity.Zero;
        foreach (var part in selector.Parts)
        {
            result += Calculate(part.Compound);
        }

        return result;
    }

    public static Specificity Calculate(CompoundSelector selector)
    {
        var result = Specificity.Zero;
        foreach (var simple in selector.SimpleSelectors)
        {
            result += simple switch
            {
                IdSelector => new Specificity(1, 0, 0),
                ClassSelector or AttributeSelector => new Specificity(0, 1, 0),
                PseudoClassSelector { Name: "where" } => Specificity.Zero,
                PseudoClassSelector pc when IsSpecificityReplacingPseudo(pc.Name) => MaxArg(pc),
                // nth-child/nth-last-child can carry NthArgument with an "of S" tail; its specificity adds.
                PseudoClassSelector { Name: "nth-child" or "nth-last-child", Argument: NthArgument { OfSelector: { } ofList } } =>
                    new Specificity(0, 1, 0) + MaxList(ofList),
                PseudoClassSelector => new Specificity(0, 1, 0),
                TypeSelector or PseudoElementSelector => new Specificity(0, 0, 1),
                UniversalSelector => Specificity.Zero,
                _ => Specificity.Zero,
            };
        }

        return result;
    }

    private static bool IsSpecificityReplacingPseudo(string name)
        => name is "is" or "not" or "has";

    private static Specificity MaxArg(PseudoClassSelector selector)
        => selector.Argument is SelectorList list ? MaxList(list) : Specificity.Zero;

    private static Specificity MaxList(SelectorList list)
        => list.Selectors.Count == 0
            ? Specificity.Zero
            : list.Selectors.Select(selector => selector.Specificity).Max();
}
