using Starling.Dom;

namespace Starling.Css.Selectors;

public sealed class SelectorMatchContext
{
    public static SelectorMatchContext Default => new();
    private Dictionary<HasMatchCacheKey, bool>? _hasMatchCache;

    public Element? HoveredElement { get; init; }
    public Element? ActiveElement { get; init; }
    public Element? FocusedElement { get; init; }
    public Element? TargetElement { get; init; }
    /// <summary>The :scope reference element (Selectors 4 §13.4). If null, the document element acts as scope.</summary>
    public Element? ScopeElement { get; init; }
    /// <summary>Target pseudo-element being matched against, or null for the originating element.</summary>
    public PseudoElement? PseudoElement { get; init; }
    /// <summary>Document URL for :local-link resolution.</summary>
    public Uri? DocumentUrl { get; init; }
    /// <summary>Set of href strings considered visited. Default privacy stance: empty/null.</summary>
    public IReadOnlySet<string>? VisitedHrefs { get; init; }

    internal int HasDepth { get; init; }

    internal SelectorMatchContext WithHasDepth(int depth) => new()
    {
        HoveredElement = HoveredElement,
        ActiveElement = ActiveElement,
        FocusedElement = FocusedElement,
        TargetElement = TargetElement,
        ScopeElement = ScopeElement,
        PseudoElement = PseudoElement,
        DocumentUrl = DocumentUrl,
        VisitedHrefs = VisitedHrefs,
        HasDepth = depth,
        _hasMatchCache = _hasMatchCache,
    };

    internal SelectorMatchContext WithScope(Element? scope) => new()
    {
        HoveredElement = HoveredElement,
        ActiveElement = ActiveElement,
        FocusedElement = FocusedElement,
        TargetElement = TargetElement,
        ScopeElement = scope,
        PseudoElement = PseudoElement,
        DocumentUrl = DocumentUrl,
        VisitedHrefs = VisitedHrefs,
        HasDepth = HasDepth,
        _hasMatchCache = _hasMatchCache,
    };

    internal bool TryGetHasMatch(Element scope, SelectorList list, out bool matched)
    {
        if (_hasMatchCache is not null &&
            _hasMatchCache.TryGetValue(new HasMatchCacheKey(scope, list, HasDepth), out matched))
        {
            return true;
        }

        matched = false;
        return false;
    }

    internal void SetHasMatch(Element scope, SelectorList list, bool matched)
    {
        _hasMatchCache ??= new Dictionary<HasMatchCacheKey, bool>();
        _hasMatchCache[new HasMatchCacheKey(scope, list, HasDepth)] = matched;
    }

    private readonly struct HasMatchCacheKey(Element scope, SelectorList list, int hasDepth) : IEquatable<HasMatchCacheKey>
    {
        private readonly Element _scope = scope;
        private readonly SelectorList _list = list;
        private readonly int _hasDepth = hasDepth;

        public bool Equals(HasMatchCacheKey other)
            => ReferenceEquals(_scope, other._scope)
            && ReferenceEquals(_list, other._list)
            && _hasDepth == other._hasDepth;

        public override bool Equals(object? obj) => obj is HasMatchCacheKey other && Equals(other);

        public override int GetHashCode()
        {
            var h = new HashCode();
            h.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_scope));
            h.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_list));
            h.Add(_hasDepth);
            return h.ToHashCode();
        }
    }
}

public readonly record struct SelectorMatchResult(bool Matched, PseudoElement? Pseudo)
{
    public static SelectorMatchResult NoMatch => new(false, null);
    public static SelectorMatchResult Element => new(true, null);
    public static SelectorMatchResult ForPseudo(PseudoElement? pseudo) => new(true, pseudo);
}

public static class SelectorMatcher
{
    private const int MaxHasDepth = 16;

    public static bool Matches(SelectorList selectorList, Element element, SelectorMatchContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(selectorList);
        ArgumentNullException.ThrowIfNull(element);
        context ??= new SelectorMatchContext();
        var selectors = selectorList.Selectors;
        for (var i = 0; i < selectors.Count; i++)
        {
            if (Matches(selectors[i], element, context))
            {
                return true;
            }
        }
        return false;
    }

    public static bool Matches(ComplexSelector selector, Element element, SelectorMatchContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(element);
        context ??= new SelectorMatchContext();
        return MatchWithResult(selector, element, context).Matched;
    }

    /// <summary>Match and return the targeted pseudo-element (if any) for cascade plumbing.</summary>
    public static SelectorMatchResult MatchWithResult(
        ComplexSelector selector,
        Element element,
        SelectorMatchContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(element);
        context ??= new SelectorMatchContext();
        if (selector.Parts.Count == 0)
        {
            return SelectorMatchResult.NoMatch;
        }

        // Caller filters by pseudo-element kind: if the caller supplied a PseudoElement filter,
        // the selector must target the same one (or null = originating-element-only rules).
        var target = selector.TargetPseudoElement;
        if (context.PseudoElement is { } requested)
        {
            if (target != requested)
            {
                return SelectorMatchResult.NoMatch;
            }
        }
        else
        {
            if (target is not null)
            {
                return SelectorMatchResult.NoMatch;
            }
        }

        return MatchesFrom(selector, selector.Parts.Count - 1, element, context)
            ? SelectorMatchResult.ForPseudo(target)
            : SelectorMatchResult.NoMatch;
    }

    private static bool MatchesFrom(
        ComplexSelector selector,
        int partIndex,
        Element element,
        SelectorMatchContext context)
    {
        var part = selector.Parts[partIndex];
        if (!MatchesCompound(part.Compound, element, context))
        {
            return false;
        }

        if (partIndex == 0)
        {
            return true;
        }

        // Explicit pointer walks (nearest-first, same order as the old Ancestors()/
        // PreviousElementSiblings() iterators): the LINQ Any + iterator pair here was a
        // dominant per-match allocation source in page-load GC profiles.
        switch (part.CombinatorFromPrevious)
        {
            case SelectorCombinator.Child:
                return ParentElement(element) is { } parent &&
                    MatchesFrom(selector, partIndex - 1, parent, context);
            case SelectorCombinator.Descendant:
                for (var ancestor = ParentElement(element); ancestor is not null; ancestor = ParentElement(ancestor))
                {
                    if (MatchesFrom(selector, partIndex - 1, ancestor, context))
                    {
                        return true;
                    }
                }
                return false;
            case SelectorCombinator.NextSibling:
                return PreviousElementSibling(element) is { } sibling &&
                    MatchesFrom(selector, partIndex - 1, sibling, context);
            case SelectorCombinator.SubsequentSibling:
                for (var prior = PreviousElementSibling(element); prior is not null; prior = PreviousElementSibling(prior))
                {
                    if (MatchesFrom(selector, partIndex - 1, prior, context))
                    {
                        return true;
                    }
                }
                return false;
            case SelectorCombinator.Column: // column combinator '||' is not implemented yet
            default:
                return false;
        }
    }

    public static bool Matches(CompoundSelector selector, Element element, SelectorMatchContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(element);
        context ??= new SelectorMatchContext();
        return MatchesCompound(selector, element, context);
    }

    private static bool MatchesCompound(CompoundSelector selector, Element element, SelectorMatchContext context)
    {
        // Indexed loop: SimpleSelectors is interface-typed, so foreach boxes a List enumerator
        // per compound check — measured as a top allocator during page-load selector matching.
        var simples = selector.SimpleSelectors;
        for (var i = 0; i < simples.Count; i++)
        {
            var simple = simples[i];
            // Pseudo-element selectors are filtered at the ComplexSelector level via MatchWithResult.
            // They don't add a per-element constraint, so they pass here as long as they're terminal.
            if (simple is PseudoElementSelector)
            {
                continue;
            }

            if (!MatchesSimple(simple, element, context))
            {
                return false;
            }
        }
        return true;
    }

    private static bool MatchesSimple(SimpleSelector selector, Element element, SelectorMatchContext context)
        => selector switch
        {
            TypeSelector type => MatchesType(type, element),
            UniversalSelector universal => MatchesUniversal(universal, element),
            IdSelector id => element.Id.Equals(id.Id, StringComparison.Ordinal),
            ClassSelector @class => element.ClassList.Contains(@class.ClassName),
            AttributeSelector attribute => MatchesAttribute(attribute, element),
            PseudoClassSelector pseudo => MatchesPseudoClass(pseudo, element, context),
            PseudoElementSelector => true, // handled at complex-selector level
            _ => false,
        };

    private static bool MatchesType(TypeSelector selector, Element element)
    {
        if (!element.LocalName.Equals(selector.LocalName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return MatchesNamespace(selector.Namespace, element);
    }

    private static bool MatchesUniversal(UniversalSelector selector, Element element)
        => MatchesNamespace(selector.Namespace, element);

    private static bool MatchesNamespace(string? ns, Element element)
    {
        // v1: treat namespace as opaque; only enforce when an explicit prefix is supplied.
        // TODO: integrate @namespace at-rule + default namespace once the rule pipeline exposes it.
        if (ns is null)
        {
            return true;
        }

        if (ns == "*")
        {
            return true;
        }

        if (ns.Length == 0)
        {
            return string.IsNullOrEmpty(element.Namespace);
        }

        return element.Prefix?.Equals(ns, StringComparison.OrdinalIgnoreCase) ?? true;
    }

    private static bool MatchesAttribute(AttributeSelector selector, Element element)
    {
        var actual = element.GetAttribute(selector.Name);
        if (actual is null)
        {
            return false;
        }

        if (selector.Operator == AttributeOperator.Exists)
        {
            return true;
        }

        var expected = selector.Value ?? string.Empty;
        var comparison = selector.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return selector.Operator switch
        {
            AttributeOperator.Equals => actual.Equals(expected, comparison),
            AttributeOperator.Includes => actual
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(expected, selector.CaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal),
            AttributeOperator.DashMatch => actual.Equals(expected, comparison) ||
                actual.StartsWith(expected + "-", comparison),
            AttributeOperator.Prefix => actual.StartsWith(expected, comparison),
            AttributeOperator.Suffix => actual.EndsWith(expected, comparison),
            AttributeOperator.Substring => actual.Contains(expected, comparison),
            _ => false,
        };
    }

    private static bool MatchesPseudoClass(
        PseudoClassSelector selector,
        Element element,
        SelectorMatchContext context)
        => selector.Name switch
        {
            "root" => element.OwnerDocument?.DocumentElement == element,
            "scope" => ScopeElement(context) == element,
            "empty" => IsEmpty(element),
            "first-child" => PreviousElementSibling(element) is null,
            "last-child" => NextElementSibling(element) is null,
            "only-child" => PreviousElementSibling(element) is null && NextElementSibling(element) is null,
            "first-of-type" => !HasPrecedingSiblingOfSameType(element),
            "last-of-type" => !HasFollowingSiblingOfSameType(element),
            "only-of-type" => !HasPrecedingSiblingOfSameType(element) &&
                !HasFollowingSiblingOfSameType(element),
            "nth-child" => MatchesNth(selector.Argument, element, context, ofType: false, fromEnd: false),
            "nth-last-child" => MatchesNth(selector.Argument, element, context, ofType: false, fromEnd: true),
            "nth-of-type" => selector.Argument is NthPattern nth1 && nth1.Matches(ElementIndex(element, ofType: true, fromEnd: false)),
            "nth-last-of-type" => selector.Argument is NthPattern nth2 && nth2.Matches(ElementIndex(element, ofType: true, fromEnd: true)),
            "is" or "where" or "matches" => selector.Argument is SelectorList list && Matches(list, element, context),
            "not" => selector.Argument is SelectorList listN && !Matches(listN, element, context),
            "has" => MatchesHas(selector.Argument, element, context),
            "lang" => selector.Argument is string lang && MatchesLanguage(element, lang),
            "dir" => selector.Argument is string dir && MatchesDirection(element, dir),
            "heading" => MatchesHeading(selector.Argument, element),
            // An element is :hover when the pointer is over it OR any of its
            // descendants, so the hovered element and every ancestor match
            // (mirrors :focus-within). The host passes the innermost hovered
            // element as HoveredElement.
            "hover" => context.HoveredElement is not null &&
                (context.HoveredElement == element || HasAncestor(context.HoveredElement, element)),
            "active" => context.ActiveElement == element,
            "focus" => context.FocusedElement == element,
            "focus-visible" => context.FocusedElement == element,
            "focus-within" => context.FocusedElement is not null &&
                (context.FocusedElement == element || HasAncestor(context.FocusedElement, element)),
            "target" => context.TargetElement == element,
            "target-within" => context.TargetElement is not null &&
                (context.TargetElement == element || HasAncestor(context.TargetElement, element)),
            "checked" => element.HasAttribute("checked") || element.HasAttribute("selected"),
            "disabled" => element.HasAttribute("disabled"),
            "enabled" => !element.HasAttribute("disabled") && IsFormElement(element),
            "required" => element.HasAttribute("required"),
            "optional" => !element.HasAttribute("required") && IsFormElement(element),
            "placeholder-shown" => element.HasAttribute("placeholder") &&
                string.IsNullOrEmpty(element.GetAttribute("value")),
            "read-only" => IsReadOnly(element),
            "read-write" => !IsReadOnly(element) && IsEditable(element),
            "indeterminate" => element.HasAttribute("indeterminate"),
            "default" => element.HasAttribute("selected") || element.HasAttribute("checked") ||
                (IsFormElement(element) && string.Equals(element.GetAttribute("type"), "submit", StringComparison.OrdinalIgnoreCase)),
            "defined" => true, // built-in elements are always defined; custom-element state is downstream
            "any-link" => IsAnyLink(element),
            "link" => IsAnyLink(element) && !IsVisited(element, context),
            "visited" => false, // privacy default: never match
            "local-link" => IsLocalLink(element, context),
            // Stubs that parse but never match until state model lands:
            "valid" or "invalid" or "user-valid" or "user-invalid" or "in-range" or "out-of-range" or
            "blank" or "autofill" or "fullscreen" or "modal" or "picture-in-picture" or
            "playing" or "paused" or "muted" or "volume-locked" or "current" or "past" or "future" => false,
            _ => false,
        };

    private static Element? ScopeElement(SelectorMatchContext context)
        => context.ScopeElement;

    private static bool MatchesNth(
        object? argument,
        Element element,
        SelectorMatchContext context,
        bool ofType,
        bool fromEnd)
    {
        var (pattern, ofSelector) = argument switch
        {
            NthArgument na => (na.Pattern, na.OfSelector),
            NthPattern np => (np, (SelectorList?)null),
            _ => (null!, (SelectorList?)null),
        };
        if (pattern is null)
        {
            return false;
        }

        if (ofSelector is null)
        {
            return pattern.Matches(ElementIndex(element, ofType, fromEnd));
        }

        // "of S" form: index counts only siblings matching S.
        var siblings = fromEnd
            ? ElementSiblingsFromEnd(element)
            : ElementSiblingsFromStart(element);
        var index = 0;
        foreach (var sibling in siblings)
        {
            if (Matches(ofSelector, sibling, context))
            {
                index++;
            }

            if (sibling == element)
            {
                return Matches(ofSelector, element, context) && pattern.Matches(index);
            }
        }
        return false;
    }

    private static bool MatchesHas(object? argument, Element element, SelectorMatchContext context)
    {
        if (argument is not SelectorList list)
        {
            return false;
        }

        if (context.HasDepth >= MaxHasDepth)
        {
            return false; // depth bound — Selectors 4 §16.8
        }

        if (context.TryGetHasMatch(element, list, out var cached))
        {
            return cached;
        }

        var childContext = context.WithHasDepth(context.HasDepth + 1).WithScope(element);

        foreach (var selector in list.Selectors)
        {
            if (selector.Parts.Count == 0)
            {
                continue;
            }

            var search = ElementsForHas(element, selector);
            foreach (var candidate in search)
            {
                if (MatchesScoped(selector, candidate, element, childContext))
                {
                    context.SetHasMatch(element, list, true);
                    return true;
                }
            }
        }
        context.SetHasMatch(element, list, false);
        return false;
    }

    private static IEnumerable<Element> ElementsForHas(Element scope, ComplexSelector selector)
    {
        var leading = selector.Parts[0].CombinatorFromPrevious;
        var onePart = selector.Parts.Count == 1;
        return leading switch
        {
            SelectorCombinator.Child when onePart => ChildElements(scope),
            SelectorCombinator.NextSibling when onePart => SingleElement(NextElementSibling(scope)),
            SelectorCombinator.SubsequentSibling when onePart => NextElementSiblings(scope),
            SelectorCombinator.NextSibling or SelectorCombinator.SubsequentSibling =>
                NextElementSiblingsAndDescendants(scope),
            _ => scope.Descendants().OfType<Element>(),
        };
    }

    private static IEnumerable<Element> ChildElements(Element scope)
    {
        for (var child = scope.FirstChild; child is not null; child = child.NextSibling)
        {
            if (child is Element childElement)
            {
                yield return childElement;
            }
        }
    }

    private static IEnumerable<Element> SingleElement(Element? element)
    {
        if (element is not null)
        {
            yield return element;
        }
    }

    private static IEnumerable<Element> NextElementSiblingsAndDescendants(Element scope)
    {
        for (var sibling = NextElementSibling(scope); sibling is not null; sibling = NextElementSibling(sibling))
        {
            yield return sibling;
            foreach (var d in sibling.Descendants().OfType<Element>())
            {
                yield return d;
            }
        }
    }

    /// <summary>Match a selector whose leftmost combinator is anchored to <paramref name="scope"/>.</summary>
    private static bool MatchesScoped(
        ComplexSelector selector,
        Element element,
        Element scope,
        SelectorMatchContext context)
    {
        // Walk right-to-left like MatchesFrom but stop when the leftmost part anchors against scope.
        return MatchScopedFrom(selector, selector.Parts.Count - 1, element, scope, context);
    }

    private static bool MatchScopedFrom(
        ComplexSelector selector,
        int partIndex,
        Element element,
        Element scope,
        SelectorMatchContext context)
    {
        var part = selector.Parts[partIndex];
        if (!MatchesCompound(part.Compound, element, context))
        {
            return false;
        }

        if (partIndex == 0)
        {
            // For :has(F), the leftmost part is anchored relative to scope according to its leading combinator.
            return part.CombinatorFromPrevious switch
            {
                SelectorCombinator.None => true, // descendant of scope is implicit
                SelectorCombinator.Child => ParentElement(element) == scope,
                SelectorCombinator.Descendant => element == scope || HasAncestor(element, scope),
                SelectorCombinator.NextSibling => PreviousElementSibling(element) == scope,
                SelectorCombinator.SubsequentSibling => HasPrecedingSibling(element, scope),
                _ => false,
            };
        }

        // Same allocation-free walks as MatchesFrom; order matches the old iterators.
        switch (part.CombinatorFromPrevious)
        {
            case SelectorCombinator.Child:
                return ParentElement(element) is { } parent &&
                    MatchScopedFrom(selector, partIndex - 1, parent, scope, context);
            case SelectorCombinator.Descendant:
                for (var ancestor = ParentElement(element); ancestor is not null; ancestor = ParentElement(ancestor))
                {
                    if (MatchScopedFrom(selector, partIndex - 1, ancestor, scope, context))
                    {
                        return true;
                    }
                }
                return false;
            case SelectorCombinator.NextSibling:
                return PreviousElementSibling(element) is { } sibling &&
                    MatchScopedFrom(selector, partIndex - 1, sibling, scope, context);
            case SelectorCombinator.SubsequentSibling:
                for (var prior = PreviousElementSibling(element); prior is not null; prior = PreviousElementSibling(prior))
                {
                    if (MatchScopedFrom(selector, partIndex - 1, prior, scope, context))
                    {
                        return true;
                    }
                }
                return false;
            default:
                return false;
        }
    }

    private static bool IsEmpty(Element element)
    {
        for (var child = element.FirstChild; child is not null; child = child.NextSibling)
        {
            var nonEmpty = child switch
            {
                Element => true,
                Text text => text.Data.Length != 0,
                CData cdata => cdata.Data.Length != 0,
                _ => false,
            };
            if (nonEmpty)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>:heading and :heading(&lt;list&gt;) (Selectors 5 §heading). Bare :heading matches any
    /// h1–h6. The functional form matches only when the element's heading level appears in the list.</summary>
    private static bool MatchesHeading(object? argument, Element element)
    {
        var level = HeadingLevel(element);
        if (level == 0)
        {
            return false;
        }

        // Bare :heading (no argument) matches any heading element.
        if (argument is null)
        {
            return true;
        }

        if (argument is HeadingArgument heading)
        {
            if (!heading.IsValid)
            {
                return false;
            }

            return heading.Levels.Contains(level);
        }

        return false;
    }

    /// <summary>The heading level (1–6) for an h1–h6 element, or 0 for non-headings.</summary>
    private static int HeadingLevel(Element element)
    {
        var name = element.LocalName;
        if (name.Length != 2 || (name[0] != 'h' && name[0] != 'H'))
        {
            return 0;
        }

        var digit = name[1];
        if (digit is >= '1' and <= '6')
        {
            return digit - '0';
        }

        return 0;
    }

    private static bool MatchesLanguage(Element element, string language)
    {
        for (Element? current = element; current is not null; current = ParentElement(current))
        {
            var actual = current.GetAttribute("lang");
            if (actual is null)
            {
                continue;
            }

            return actual.Equals(language, StringComparison.OrdinalIgnoreCase) ||
                actual.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool MatchesDirection(Element element, string direction)
        => ResolveDirectionality(element).Equals(direction, StringComparison.OrdinalIgnoreCase);

    /// <summary>The directionality of an element ("ltr" or "rtl") per the HTML standard
    /// (https://html.spec.whatwg.org/multipage/dom.html#the-directionality). Drives <c>:dir()</c>.
    /// An invalid <c>dir</c> value is ignored (inherit); <c>dir=auto</c> (and a bare <c>bdi</c>)
    /// uses the first strong bidi character of the relevant text, defaulting to ltr.</summary>
    private static string ResolveDirectionality(Element element)
    {
        for (Element? current = element; current is not null; current = ParentElement(current))
        {
            var attr = current.GetAttribute("dir");

            if (attr is not null)
            {
                if (attr.Equals("ltr", StringComparison.OrdinalIgnoreCase))
                {
                    return "ltr";
                }

                if (attr.Equals("rtl", StringComparison.OrdinalIgnoreCase))
                {
                    return "rtl";
                }

                if (attr.Equals("auto", StringComparison.OrdinalIgnoreCase))
                {
                    return AutoDirectionality(current);
                }
            }

            // No dir attribute, or an invalid value (e.g. "foo"): a <bdi> with no valid dir
            // defaults to auto (HTML directionality); any other element inherits — keep walking up.
            if (current.LocalName.Equals("bdi", StringComparison.OrdinalIgnoreCase))
            {
                return AutoDirectionality(current);
            }
        }

        // The default directionality of the document is ltr.
        return "ltr";
    }

    /// <summary>Directionality for <c>dir=auto</c>: the first strong directional character of the
    /// element's contributing text, defaulting to ltr when there is none.</summary>
    private static string AutoDirectionality(Element element)
    {
        var text = AutoDirectionalityText(element);
        foreach (var ch in text)
        {
            var strong = StrongDirection(ch);
            if (strong == 'L')
            {
                return "ltr";
            }

            if (strong == 'R')
            {
                return "rtl";
            }
        }
        return "ltr";
    }

    /// <summary>The text that feeds <c>dir=auto</c>. For auto-directionality form-associated inputs
    /// and textarea this is the control's value; for any other element it is its text content.</summary>
    private static string AutoDirectionalityText(Element element)
    {
        if (element.LocalName.Equals("input", StringComparison.OrdinalIgnoreCase))
        {
            // Only "auto-directionality form-associated" input types use the value for dir=auto.
            // Other types (date, number, checkbox, radio, …) do not, so they contribute no text.
            return UsesValueForAutoDir(element)
                ? element.InputValue ?? element.GetAttribute("value") ?? string.Empty
                : string.Empty;
        }
        if (element.LocalName.Equals("textarea", StringComparison.OrdinalIgnoreCase))
        {
            return element.InputValue ?? element.TextContent;
        }

        return element.TextContent;
    }

    private static bool UsesValueForAutoDir(Element input)
    {
        var type = (input.GetAttribute("type") ?? "text").Trim();
        // HTML "auto-directionality form-associated" input states (the default/missing state is Text).
        return type.Length == 0 || type.ToLowerInvariant() switch
        {
            "hidden" or "text" or "search" or "tel" or "url" or "email" or "password" or
            "submit" or "reset" or "button" => true,
            _ => false,
        };
    }

    /// <summary>The strong bidi class of a character: 'L' (left-to-right), 'R' (right-to-left
    /// or arabic), or '\0' for weak/neutral. Covers the ranges needed for first-strong detection.</summary>
    private static char StrongDirection(char c)
    {
        // Basic Latin and common LTR letters.
        if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
        {
            return 'L';
        }
        // Hebrew + Hebrew presentation forms (U+0590-U+05FF, U+FB1D-U+FB4F).
        if (c is >= '֐' and <= '׿' or >= 'יִ' and <= 'ﭏ')
        {
            return 'R';
        }
        // Arabic / Syriac / Thaana / NKo / Samaritan / Mandaic (U+0600-U+08FF) and
        // Arabic presentation forms A & B (U+FB50-U+FDFF, U+FE70-U+FEFF).
        if (c is >= '؀' and <= 'ࣿ' or >= 'ﭐ' and <= '﷿'
            or >= 'ﹰ' and <= '﻿')
        {
            return 'R';
        }

        return '\0';
    }

    private static bool IsFormElement(Element element)
        => element.LocalName is "input" or "select" or "textarea" or "button" or "fieldset" or "option";

    private static bool IsReadOnly(Element element)
    {
        if (element.LocalName is "input" or "textarea")
        {
            return element.HasAttribute("readonly") || element.HasAttribute("disabled");
        }
        // Non-form, non-contenteditable elements are :read-only by default.
        return !IsEditable(element);
    }

    private static bool IsEditable(Element element)
    {
        if (element.LocalName is "input" or "textarea")
        {
            return !element.HasAttribute("readonly") && !element.HasAttribute("disabled");
        }

        for (Element? cur = element; cur is not null; cur = ParentElement(cur))
        {
            var ce = cur.GetAttribute("contenteditable");
            if (ce is null)
            {
                continue;
            }

            if (ce.Length == 0 || ce.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (ce.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return false;
    }

    private static bool IsAnyLink(Element element)
        => element.LocalName is "a" or "area" && element.HasAttribute("href");

    private static bool IsVisited(Element element, SelectorMatchContext context)
    {
        if (context.VisitedHrefs is null)
        {
            return false;
        }

        var href = element.GetAttribute("href");
        return href is not null && context.VisitedHrefs.Contains(href);
    }

    private static bool IsLocalLink(Element element, SelectorMatchContext context)
    {
        if (!IsAnyLink(element))
        {
            return false;
        }

        if (context.DocumentUrl is null)
        {
            return false;
        }

        var href = element.GetAttribute("href");
        if (string.IsNullOrEmpty(href))
        {
            return false;
        }

        if (!Uri.TryCreate(context.DocumentUrl, href, out var resolved))
        {
            return false;
        }

        return resolved.Scheme == context.DocumentUrl.Scheme &&
            resolved.Host.Equals(context.DocumentUrl.Host, StringComparison.OrdinalIgnoreCase) &&
            resolved.AbsolutePath.Equals(context.DocumentUrl.AbsolutePath, StringComparison.Ordinal);
    }

    private static int ElementIndex(Element element, bool ofType, bool fromEnd)
    {
        // A parent-less element is its own only child (Selectors 4 §child-index): it is the
        // first/last/only child, so child-indexed pseudos count it at index 1. WPT
        // child-indexed-pseudo-class.html asserts a detached div matches :nth-child(1)/:nth-child(n).
        if (element.ParentNode is null)
        {
            return 1;
        }

        // Walk sibling pointers from the element itself instead of enumerating the parent's
        // child list (avoids the ChildNodes iterator + OfType/Reverse wrappers per nth check).
        var index = 1; // the element always counts itself
        if (fromEnd)
        {
            for (var sibling = NextElementSibling(element); sibling is not null; sibling = NextElementSibling(sibling))
            {
                if (!ofType || SameType(sibling, element))
                {
                    index++;
                }
            }
        }
        else
        {
            for (var sibling = PreviousElementSibling(element); sibling is not null; sibling = PreviousElementSibling(sibling))
            {
                if (!ofType || SameType(sibling, element))
                {
                    index++;
                }
            }
        }

        return index;
    }

    private static IEnumerable<Element> ElementSiblingsFromStart(Element element)
        => element.ParentNode?.ChildNodes.OfType<Element>() ?? [];

    private static IEnumerable<Element> ElementSiblingsFromEnd(Element element)
        => ElementSiblingsFromStart(element).Reverse();

    private static bool SameType(Element left, Element right)
        => left.Namespace == right.Namespace &&
           left.LocalName.Equals(right.LocalName, StringComparison.OrdinalIgnoreCase);

    private static Element? ParentElement(Element element) => element.ParentNode as Element;

    private static bool HasAncestor(Element element, Element candidate)
    {
        for (var current = ParentElement(element); current is not null; current = ParentElement(current))
        {
            if (current == candidate)
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasPrecedingSibling(Element element, Element candidate)
    {
        for (var sibling = PreviousElementSibling(element); sibling is not null; sibling = PreviousElementSibling(sibling))
        {
            if (sibling == candidate)
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasPrecedingSiblingOfSameType(Element element)
    {
        for (var sibling = PreviousElementSibling(element); sibling is not null; sibling = PreviousElementSibling(sibling))
        {
            if (SameType(sibling, element))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasFollowingSiblingOfSameType(Element element)
    {
        for (var sibling = NextElementSibling(element); sibling is not null; sibling = NextElementSibling(sibling))
        {
            if (SameType(sibling, element))
            {
                return true;
            }
        }
        return false;
    }

    private static Element? PreviousElementSibling(Element element)
    {
        for (var sibling = element.PreviousSibling; sibling is not null; sibling = sibling.PreviousSibling)
        {
            if (sibling is Element siblingElement)
            {
                return siblingElement;
            }
        }

        return null;
    }

    private static Element? NextElementSibling(Element element)
    {
        for (var sibling = element.NextSibling; sibling is not null; sibling = sibling.NextSibling)
        {
            if (sibling is Element siblingElement)
            {
                return siblingElement;
            }
        }

        return null;
    }

    private static IEnumerable<Element> NextElementSiblings(Element element)
    {
        for (var sibling = NextElementSibling(element); sibling is not null; sibling = NextElementSibling(sibling))
        {
            yield return sibling;
        }
    }
}
