using AwesomeAssertions;
using Starling.Css.Selectors;
using Starling.Dom;
using Starling.Spec;

namespace Starling.Css.Tests;

[Spec("selectors-4", "https://www.w3.org/TR/selectors-4/")]

[TestClass]
public sealed class NthChildOfTests
{
    [TestMethod]
    public void Parses_nth_child_of_selector_argument()
    {
        var selector = SelectorParser.ParseSelectorList(":nth-child(2n+1 of li.special)")
            .Selectors.Single();
        var pseudo = (PseudoClassSelector)selector.RightmostCompound.SimpleSelectors[0];
        pseudo.Name.Should().Be("nth-child");
        var arg = pseudo.Argument.Should().BeOfType<NthArgument>().Subject;
        arg.Pattern.A.Should().Be(2);
        arg.Pattern.B.Should().Be(1);
        arg.OfSelector.Should().NotBeNull();
        arg.OfSelector!.Selectors.Should().HaveCount(1);
    }

    [TestMethod]
    public void Matches_odd_indexed_li_special_among_siblings()
    {
        var doc = new Document();
        var ul = doc.CreateElement("ul");
        doc.AppendChild(ul);
        var items = new List<Element>();
        for (var i = 0; i < 5; i++)
        {
            var li = doc.CreateElement("li");
            if (i is 0 or 2 or 4)
            {
                li.ClassList.Add("special");
            }

            ul.AppendChild(li);
            items.Add(li);
        }

        // Among "li.special" siblings (indexes 0, 2, 4), the 1st (index 0) and 3rd (index 4)
        // are odd (1-based: 1, 3). Index 2 is the 2nd special.
        var selector = SelectorParser.ParseSelectorList(":nth-child(2n+1 of li.special)");
        SelectorMatcher.Matches(selector, items[0]).Should().BeTrue();
        SelectorMatcher.Matches(selector, items[2]).Should().BeFalse();
        SelectorMatcher.Matches(selector, items[4]).Should().BeTrue();
    }

    [TestMethod]
    public void Plain_nth_child_still_works_without_of_clause()
    {
        var doc = new Document();
        var ul = doc.CreateElement("ul");
        doc.AppendChild(ul);
        var a = doc.CreateElement("li");
        var b = doc.CreateElement("li");
        var c = doc.CreateElement("li");
        ul.AppendChild(a);
        ul.AppendChild(b);
        ul.AppendChild(c);

        SelectorMatcher.Matches(SelectorParser.ParseSelectorList(":nth-child(2)"), b).Should().BeTrue();
        SelectorMatcher.Matches(SelectorParser.ParseSelectorList(":nth-child(2)"), a).Should().BeFalse();
    }

    [TestMethod]
    public void Nth_last_child_with_of_filter()
    {
        var doc = new Document();
        var ul = doc.CreateElement("ul");
        doc.AppendChild(ul);
        var a = doc.CreateElement("li"); a.ClassList.Add("special");
        var b = doc.CreateElement("li");
        var c = doc.CreateElement("li"); c.ClassList.Add("special");
        ul.AppendChild(a);
        ul.AppendChild(b);
        ul.AppendChild(c);

        // From end, c is the 1st special; a is the 2nd special.
        var selector = SelectorParser.ParseSelectorList(":nth-last-child(1 of li.special)");
        SelectorMatcher.Matches(selector, c).Should().BeTrue();
        SelectorMatcher.Matches(selector, a).Should().BeFalse();
    }
}
