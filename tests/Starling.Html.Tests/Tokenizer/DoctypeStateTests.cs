using AwesomeAssertions;
using Starling.Html.Tokenizer;
namespace Starling.Html.Tests.Tokenizer;

/// <summary>Doctype cluster — wp:M1-01f.</summary>
[TestClass]
public class DoctypeStateTests
{
    [TestMethod]
    public void Bare_html_doctype()
    {
        Tokenize("<!DOCTYPE html>").Should().Equal(
            new DoctypeToken("html", null, null, ForceQuirks: false),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Doctype_name_is_lowercased()
    {
        Tokenize("<!DOCTYPE HTML>").Should().Equal(
            new DoctypeToken("html", null, null, false),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Lowercase_doctype_keyword_also_works()
    {
        Tokenize("<!doctype html>").Should().Equal(
            new DoctypeToken("html", null, null, false),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Public_identifier_double_quoted()
    {
        Tokenize("<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Transitional//EN\">").Should().Equal(
            new DoctypeToken("html", "-//W3C//DTD XHTML 1.0 Transitional//EN", null, false),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Public_identifier_single_quoted()
    {
        Tokenize("<!DOCTYPE html PUBLIC '-//foo//'>").Should().Equal(
            new DoctypeToken("html", "-//foo//", null, false),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Public_and_system_identifiers()
    {
        Tokenize(
                "<!DOCTYPE html PUBLIC \"-//W3C//DTD HTML 4.01//EN\" \"http://www.w3.org/TR/html4/strict.dtd\">")
            .Should().Equal(
                new DoctypeToken(
                    "html",
                    "-//W3C//DTD HTML 4.01//EN",
                    "http://www.w3.org/TR/html4/strict.dtd",
                    false),
                EndOfFileToken.Instance);
    }

    [TestMethod]
    public void System_only_identifier()
    {
        Tokenize("<!DOCTYPE html SYSTEM \"about:legacy-compat\">").Should().Equal(
            new DoctypeToken("html", null, "about:legacy-compat", false),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Missing_name_forces_quirks()
    {
        var sink = new RecordingSink();
        var t = new HtmlTokenizer(sink);
        t.Feed("<!DOCTYPE>");
        t.EndOfInput();

        DrainAll(t).Should().Equal(
            new DoctypeToken(null, null, null, ForceQuirks: true),
            EndOfFileToken.Instance);
        sink.Errors.Should().ContainSingle()
            .Which.code.Should().Be(HtmlParseError.MissingDoctypeName);
    }

    [TestMethod]
    public void Missing_whitespace_before_name_is_a_parse_error_but_continues()
    {
        // <!DOCTYPEhtml> — no space between DOCTYPE and html.
        var sink = new RecordingSink();
        var t = new HtmlTokenizer(sink);
        t.Feed("<!DOCTYPEhtml>");
        t.EndOfInput();

        DrainAll(t).Should().Equal(
            new DoctypeToken("html", null, null, false),
            EndOfFileToken.Instance);
        sink.Errors.Should().ContainSingle()
            .Which.code.Should().Be(HtmlParseError.MissingWhitespaceBeforeDoctypeName);
    }

    [TestMethod]
    public void Eof_in_doctype_forces_quirks()
    {
        var sink = new RecordingSink();
        var t = new HtmlTokenizer(sink);
        t.Feed("<!DOCTYPE htm");
        t.EndOfInput();

        DrainAll(t).Should().Equal(
            new DoctypeToken("htm", null, null, ForceQuirks: true),
            EndOfFileToken.Instance);
        sink.Errors.Should().ContainSingle()
            .Which.code.Should().Be(HtmlParseError.EofInDoctype);
    }

    [TestMethod]
    public void Garbage_after_name_drops_into_bogus_doctype()
    {
        // <!DOCTYPE html FOO> — anything after the name that isn't PUBLIC/
        // SYSTEM/whitespace/'>' fires the parse error and absorbs to '>'.
        var sink = new RecordingSink();
        var t = new HtmlTokenizer(sink);
        t.Feed("<!DOCTYPE html FOO>");
        t.EndOfInput();

        DrainAll(t).Should().Equal(
            new DoctypeToken("html", null, null, ForceQuirks: true),
            EndOfFileToken.Instance);
        sink.Errors.Should().ContainSingle()
            .Which.code.Should().Be(HtmlParseError.InvalidCharacterSequenceAfterDoctypeName);
    }

    [TestMethod]
    public void Doctype_followed_by_more_html()
    {
        Tokenize("<!DOCTYPE html><html>").Should().Equal(
            new DoctypeToken("html", null, null, false),
            new StartTagToken("html", [], false),
            EndOfFileToken.Instance);
    }

    // Helpers ---------------------------------------------------------------

    private static List<HtmlToken> Tokenize(string s)
    {
        var t = new HtmlTokenizer();
        t.Feed(s);
        t.EndOfInput();
        return DrainAll(t);
    }

    private static List<HtmlToken> DrainAll(HtmlTokenizer t)
    {
        var tokens = new List<HtmlToken>();
        while (true)
        {
            var tok = t.ReadToken();
            if (tok is null)
            {
                return tokens;
            }

            tokens.Add(tok);
            if (tok is EndOfFileToken)
            {
                return tokens;
            }
        }
    }

    private sealed class RecordingSink : IParseErrorSink
    {
        public List<(HtmlParseError code, int line, int column)> Errors { get; } = [];

        public void Report(HtmlParseError code, int line, int column)
            => Errors.Add((code, line, column));
    }
}
