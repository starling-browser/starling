using Starling.Css.Selectors;
using Starling.Dom;
using Starling.Js.Runtime;

namespace Starling.Bindings;

/// <summary>
/// Selector engine backing <c>querySelector</c>/<c>querySelectorAll</c>/
/// <c>matches</c>/<c>closest</c>. Delegates to the full CSS
/// <see cref="SelectorParser"/> + <see cref="SelectorMatcher"/> from
/// <c>Starling.Css</c>, so the JS layer accepts the same grammar the cascade
/// does — compound selectors, combinators (<c>&gt;</c>, <c>+</c>, <c>~</c>,
/// descendant), attribute selectors, <c>:nth-*</c>, <c>:is/:where/:not</c>, etc.
/// An unparseable selector throws a JS <c>SyntaxError</c> (per the DOM spec).
/// </summary>
internal static class QuerySelectorEngine
{
    /// <summary>First descendant of <paramref name="root"/> matching <paramref name="selector"/>, in tree order.</summary>
    public static Element? First(Node root, string selector, JsRealm realm)
    {
        var list = Parse(selector, realm);
        var ctx = ContextFor(root);
        foreach (var e in root.DescendantElements())
        {
            if (SelectorMatcher.Matches(list, e, ctx))
            {
                return e;
            }
        }

        return null;
    }

    /// <summary>All descendants of <paramref name="root"/> matching <paramref name="selector"/>, in tree order.</summary>
    public static IEnumerable<Element> All(Node root, string selector, JsRealm realm)
    {
        var list = Parse(selector, realm);
        var ctx = ContextFor(root);
        foreach (var e in root.DescendantElements())
        {
            if (SelectorMatcher.Matches(list, e, ctx))
            {
                yield return e;
            }
        }
    }

    /// <summary>Whether <paramref name="element"/> itself matches <paramref name="selector"/> (<c>Element.matches</c>).</summary>
    public static bool Matches(Element element, string selector, JsRealm realm)
        => SelectorMatcher.Matches(Parse(selector, realm), element, ContextFor(element));

    /// <summary>
    /// Nearest inclusive ancestor of <paramref name="element"/> matching
    /// <paramref name="selector"/>, or <c>null</c> (<c>Element.closest</c>).
    /// </summary>
    public static Element? Closest(Element element, string selector, JsRealm realm)
    {
        var list = Parse(selector, realm);
        var ctx = ContextFor(element);
        for (Node? n = element; n is not null; n = n.ParentNode)
        {
            if (n is Element e && SelectorMatcher.Matches(list, e, ctx))
            {
                return e;
            }
        }

        return null;
    }

    private static SelectorMatchContext ContextFor(Node root)
        => root is Element scope
            ? new SelectorMatchContext { ScopeElement = scope }
            : SelectorMatchContext.Default;

    private static SelectorList Parse(string raw, JsRealm realm)
    {
        // DOM §4.2.6 — querySelector*/matches/closest throw a "SyntaxError"
        // DOMException (name SyntaxError, code 12) for an invalid selector, not
        // an ECMAScript SyntaxError, so assert_throws_dom("SyntaxError") matches.
        if (string.IsNullOrEmpty(raw))
        {
            throw DomExceptionBinding.Throw(realm, "SyntaxError", "The selector is empty.");
        }

        SelectorList list;
        try
        {
            list = SelectorParser.ParseSelectorList(raw);
        }
        catch (FormatException ex)
        {
            throw DomExceptionBinding.Throw(realm, "SyntaxError", $"'{raw}' is not a valid selector: {ex.Message}");
        }

        if (list.Selectors.Count == 0)
        {
            throw DomExceptionBinding.Throw(realm, "SyntaxError", $"'{raw}' is not a valid selector.");
        }

        return list;
    }
}
