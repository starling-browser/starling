using AwesomeAssertions;
using Starling.Html.Tokenizer;
namespace Starling.Html.Tests.Tokenizer;

[TestClass]
public class TagStateTests
{
    [TestMethod]
    public void Bare_start_tag()
    {
        Tokenize("<a>").Should().Equal(
            new StartTagToken("a", [], false),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Bare_end_tag()
    {
        Tokenize("</a>").Should().Equal(
            new EndTagToken("a", [], false),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Self_closing_start_tag()
    {
        Tokenize("<br/>").Should().Equal(
            new StartTagToken("br", [], SelfClosing: true),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Tag_name_is_lowercased()
    {
        Tokenize("<DIV>").Should().Equal(
            new StartTagToken("div", [], false),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Tag_around_text_data()
    {
        Tokenize("<p>hi</p>").Should().Equal(
            new StartTagToken("p", [], false),
            new CharacterToken('h'),
            new CharacterToken('i'),
            new EndTagToken("p", [], false),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Single_attribute_no_value()
    {
        Tokenize("<input checked>").Should().Equal(
            new StartTagToken("input", [new HtmlAttribute("checked", "")], false),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Unquoted_attribute_value()
    {
        Tokenize("<a href=foo>").Should().Equal(
            new StartTagToken("a", [new HtmlAttribute("href", "foo")], false),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Double_quoted_attribute_value()
    {
        Tokenize("<a href=\"/path?x=1\">").Should().Equal(
            new StartTagToken("a", [new HtmlAttribute("href", "/path?x=1")], false),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Single_quoted_attribute_value()
    {
        Tokenize("<a href='/path'>").Should().Equal(
            new StartTagToken("a", [new HtmlAttribute("href", "/path")], false),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Attribute_name_is_lowercased()
    {
        Tokenize("<a HREF=\"x\">").Should().Equal(
            new StartTagToken("a", [new HtmlAttribute("href", "x")], false),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Multiple_attributes()
    {
        Tokenize("<a href=\"x\" id='y' rel=z>").Should().Equal(
            new StartTagToken("a",
                [
                    new HtmlAttribute("href", "x"),
                    new HtmlAttribute("id", "y"),
                    new HtmlAttribute("rel", "z"),
                ],
                false),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Duplicate_attribute_drops_second_and_reports_parse_error()
    {
        var sink = new RecordingSink();
        var t = new HtmlTokenizer(sink);
        t.Feed("<a x=\"1\" x=\"2\">");
        t.EndOfInput();

        Drain(t).Should().Equal(
            new StartTagToken("a", [new HtmlAttribute("x", "1")], false),
            EndOfFileToken.Instance);

        sink.Errors.Should().ContainSingle()
            .Which.code.Should().Be(HtmlParseError.DuplicateAttribute);
    }

    [TestMethod]
    public void Self_closing_with_attributes()
    {
        Tokenize("<img src=\"a.png\" alt=\"hi\"/>").Should().Equal(
            new StartTagToken("img",
                [
                    new HtmlAttribute("src", "a.png"),
                    new HtmlAttribute("alt", "hi"),
                ],
                SelfClosing: true),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Empty_tag_starts_with_invalid_first_char()
    {
        // <> per spec is an invalid-first-character-of-tag-name parse error;
        // we emit '<' as a Char token and reconsume '>' in Data, which emits
        // it as a Char too.
        var sink = new RecordingSink();
        var t = new HtmlTokenizer(sink);
        t.Feed("<>");
        t.EndOfInput();

        Drain(t).Should().Equal(
            new CharacterToken('<'),
            new CharacterToken('>'),
            EndOfFileToken.Instance);

        sink.Errors.Should().ContainSingle()
            .Which.code.Should().Be(HtmlParseError.InvalidFirstCharacterOfTagName);
    }

    [TestMethod]
    public void Missing_end_tag_name_parse_error()
    {
        // </> per §13.2.5.7
        var sink = new RecordingSink();
        var t = new HtmlTokenizer(sink);
        t.Feed("</>");
        t.EndOfInput();

        Drain(t).Should().Equal(EndOfFileToken.Instance);

        sink.Errors.Should().ContainSingle()
            .Which.code.Should().Be(HtmlParseError.MissingEndTagName);
    }

    [TestMethod]
    public void Eof_in_tag_name_drops_tag_and_reports_parse_error()
    {
        var sink = new RecordingSink();
        var t = new HtmlTokenizer(sink);
        t.Feed("<a");
        t.EndOfInput();

        Drain(t).Should().Equal(EndOfFileToken.Instance);

        sink.Errors.Should().ContainSingle()
            .Which.code.Should().Be(HtmlParseError.EofInTag);
    }

    [TestMethod]
    public void Eof_before_tag_name_emits_lt_then_eof()
    {
        var sink = new RecordingSink();
        var t = new HtmlTokenizer(sink);
        t.Feed("<");
        t.EndOfInput();

        Drain(t).Should().Equal(
            new CharacterToken('<'),
            EndOfFileToken.Instance);

        sink.Errors.Should().ContainSingle()
            .Which.code.Should().Be(HtmlParseError.EofBeforeTagName);
    }

    [TestMethod]
    public void Null_in_tag_name_maps_to_replacement_character()
    {
        var sink = new RecordingSink();
        var t = new HtmlTokenizer(sink);
        t.Feed("<a\0b>");
        t.EndOfInput();

        Drain(t).Should().Equal(
            new StartTagToken("a�b", [], false),
            EndOfFileToken.Instance);

        sink.Errors.Should().ContainSingle()
            .Which.code.Should().Be(HtmlParseError.UnexpectedNullCharacter);
    }

    [TestMethod]
    public void Missing_whitespace_between_attributes_reports_and_continues()
    {
        // Spec §13.2.5.39: <a x="1"y="2"> emits both attrs with a parse error.
        var sink = new RecordingSink();
        var t = new HtmlTokenizer(sink);
        t.Feed("<a x=\"1\"y=\"2\">");
        t.EndOfInput();

        Drain(t).Should().Equal(
            new StartTagToken("a",
                [
                    new HtmlAttribute("x", "1"),
                    new HtmlAttribute("y", "2"),
                ],
                false),
            EndOfFileToken.Instance);

        sink.Errors.Should().ContainSingle()
            .Which.code.Should().Be(HtmlParseError.MissingWhitespaceBetweenAttributes);
    }

    [TestMethod]
    public void Missing_attribute_value_after_equals_then_gt()
    {
        // <a foo=> → missing-attribute-value, then emit tag with foo="".
        var sink = new RecordingSink();
        var t = new HtmlTokenizer(sink);
        t.Feed("<a foo=>");
        t.EndOfInput();

        Drain(t).Should().Equal(
            new StartTagToken("a", [new HtmlAttribute("foo", "")], false),
            EndOfFileToken.Instance);

        sink.Errors.Should().ContainSingle()
            .Which.code.Should().Be(HtmlParseError.MissingAttributeValue);
    }

    [TestMethod]
    public void Chunked_input_across_tag_boundaries()
    {
        // Feed one char at a time; tokens must come out identical to a single
        // bulk feed. Demonstrates the push-driven contract: state survives
        // across Feed() calls.
        var whole = Tokenize("<p class=\"hi\">x</p>");
        var t = new HtmlTokenizer();
        foreach (var ch in "<p class=\"hi\">x</p>")
        {
            t.Feed([ch]);
        }

        t.EndOfInput();
        Drain(t).Should().Equal(whole);
    }

    // helpers ---------------------------------------------------------------

    private static List<HtmlToken> Tokenize(string input)
    {
        var t = new HtmlTokenizer();
        t.Feed(input);
        t.EndOfInput();
        return Drain(t);
    }

    private static List<HtmlToken> Drain(HtmlTokenizer t)
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
