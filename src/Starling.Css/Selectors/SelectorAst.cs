namespace Starling.Css.Selectors;

public sealed record SelectorList(IReadOnlyList<ComplexSelector> Selectors)
{
    public static SelectorList Empty { get; } = new([]);
}

public sealed record ComplexSelector(IReadOnlyList<ComplexSelectorPart> Parts)
{
    public CompoundSelector RightmostCompound => Parts[^1].Compound;

    public Specificity Specificity => SpecificityCalculator.Calculate(this);

    /// <summary>If the rightmost compound contains a pseudo-element, the kind; otherwise null.
    /// Trailing pseudo-classes are permitted after the pseudo-element (e.g. <c>::before:hover</c>).</summary>
    public PseudoElement? TargetPseudoElement
    {
        get
        {
            if (Parts.Count == 0)
            {
                return null;
            }
            // Indexed loop: this property is read once per selector match, and foreach
            // over the interface-typed list boxes a List enumerator each time.
            var simples = RightmostCompound.SimpleSelectors;
            for (var i = 0; i < simples.Count; i++)
            {
                if (simples[i] is PseudoElementSelector pe)
                {
                    return pe.Kind;
                }
            }
            return null;
        }
    }
}

public sealed record ComplexSelectorPart(
    CompoundSelector Compound,
    SelectorCombinator CombinatorFromPrevious);

public sealed record CompoundSelector(IReadOnlyList<SimpleSelector> SimpleSelectors);

public enum SelectorCombinator
{
    None,
    Descendant,
    Child,
    NextSibling,
    SubsequentSibling,
    Column,
}

public abstract record SimpleSelector;

public sealed record TypeSelector(string LocalName, string? Namespace = null) : SimpleSelector;

public sealed record UniversalSelector(string? Namespace = null) : SimpleSelector;

public sealed record IdSelector(string Id) : SimpleSelector;

public sealed record ClassSelector(string ClassName) : SimpleSelector;

public sealed record AttributeSelector(
    string Name,
    AttributeOperator Operator,
    string? Value,
    bool CaseInsensitive) : SimpleSelector;

public enum AttributeOperator
{
    Exists,
    Equals,
    Includes,
    DashMatch,
    Prefix,
    Suffix,
    Substring,
}

public sealed record PseudoClassSelector(string Name, object? Argument = null) : SimpleSelector;

public sealed record PseudoElementSelector(PseudoElement Kind, string Name) : SimpleSelector
{
    public PseudoElementSelector(string name)
        : this(PseudoElementHelpers.FromName(name), name.ToLowerInvariant()) { }
}

public enum PseudoElement
{
    Before,
    After,
    Marker,
    Placeholder,
    FirstLine,
    FirstLetter,
    Selection,
    Backdrop,
    FileSelectorButton,
    DetailsContent,
    Cue,
    // Functional pseudo-elements (CSS Pseudo 4 / Shadow DOM): ::part(<ident>) and
    // ::slotted(<compound-selector>). Parsed to a dedicated kind; full matching
    // requires shadow-tree semantics.
    Part,
    Slotted,
    Unknown,
}

public static class PseudoElementHelpers
{
    public static PseudoElement FromName(string name) => name.ToLowerInvariant() switch
    {
        "before" => PseudoElement.Before,
        "after" => PseudoElement.After,
        "marker" => PseudoElement.Marker,
        "placeholder" => PseudoElement.Placeholder,
        "first-line" => PseudoElement.FirstLine,
        "first-letter" => PseudoElement.FirstLetter,
        "selection" => PseudoElement.Selection,
        "backdrop" => PseudoElement.Backdrop,
        "file-selector-button" => PseudoElement.FileSelectorButton,
        "details-content" => PseudoElement.DetailsContent,
        "cue" => PseudoElement.Cue,
        "part" => PseudoElement.Part,
        "slotted" => PseudoElement.Slotted,
        _ => PseudoElement.Unknown,
    };

    public static string ToCssName(this PseudoElement element) => element switch
    {
        PseudoElement.Before => "before",
        PseudoElement.After => "after",
        PseudoElement.Marker => "marker",
        PseudoElement.Placeholder => "placeholder",
        PseudoElement.FirstLine => "first-line",
        PseudoElement.FirstLetter => "first-letter",
        PseudoElement.Selection => "selection",
        PseudoElement.Backdrop => "backdrop",
        PseudoElement.FileSelectorButton => "file-selector-button",
        PseudoElement.DetailsContent => "details-content",
        PseudoElement.Cue => "cue",
        PseudoElement.Part => "part",
        PseudoElement.Slotted => "slotted",
        _ => "unknown",
    };
}

/// <summary>Argument for nth-style pseudos with optional "of S" filter (Selectors 4 §15.3).
/// <paramref name="IsValid"/> is false when the An+B microsyntax failed to parse — the
/// pattern then degrades to 0n+0 for matching, but CSSOM selectorText treats the whole
/// selector as a parse error.</summary>
public sealed record NthArgument(NthPattern Pattern, SelectorList? OfSelector = null, bool IsValid = true);

/// <summary>Argument for the functional <c>:heading()</c> pseudo-class (Selectors 5 §heading).
/// The argument is a comma-separated list of plain integers — each names a heading level to
/// match. <paramref name="IsValid"/> is false when the list failed to parse (e.g. An+B forms,
/// non-integers, an empty list); the selector then matches nothing.</summary>
public sealed record HeadingArgument(IReadOnlyList<int> Levels, bool IsValid = true);

public readonly record struct Specificity(int Ids, int Classes, int Types) : IComparable<Specificity>
{
    public static Specificity Zero => new(0, 0, 0);

    public int CompareTo(Specificity other)
    {
        var ids = Ids.CompareTo(other.Ids);
        if (ids != 0)
        {
            return ids;
        }

        var classes = Classes.CompareTo(other.Classes);
        if (classes != 0)
        {
            return classes;
        }

        return Types.CompareTo(other.Types);
    }

    public static Specificity operator +(Specificity left, Specificity right)
        => new(left.Ids + right.Ids, left.Classes + right.Classes, left.Types + right.Types);
}

public sealed record NthPattern(int A, int B)
{
    public bool Matches(int oneBasedIndex)
    {
        if (A == 0)
        {
            return oneBasedIndex == B;
        }

        var difference = oneBasedIndex - B;
        if (difference % A != 0)
        {
            return false;
        }

        return difference / A >= 0;
    }
}
