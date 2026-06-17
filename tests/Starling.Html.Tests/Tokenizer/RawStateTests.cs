using AwesomeAssertions;
using Starling.Html.Tokenizer;
namespace Starling.Html.Tests.Tokenizer;

/// <summary>
/// RCDATA / RAWTEXT / PLAINTEXT cluster — wp:M1-01c. The tokenizer enters
/// these states explicitly via <see cref="HtmlTokenizer.SetState"/>; in a
/// real run, the tree builder calls it after parsing a host element like
/// &lt;textarea&gt; or &lt;style&gt;.
/// </summary>
[TestClass]
public class RawStateTests
{
    // ----- RCDATA ----------------------------------------------------------

    [TestMethod]
    public void Rcdata_passes_text_through()
    {
        var t = new HtmlTokenizer();
        t.Feed("<textarea>");
        DrainOne(t).Should().Be(new StartTagToken("textarea", [], false));

        t.SetState(TokenizerState.Rcdata);
        t.Feed("hello</textarea>");
        t.EndOfInput();

        DrainAll(t).Should().Equal(
            new CharacterToken('h'), new CharacterToken('e'),
            new CharacterToken('l'), new CharacterToken('l'),
            new CharacterToken('o'),
            new EndTagToken("textarea", [], false),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Rcdata_keeps_lt_chars_when_not_followed_by_slash()
    {
        // "<3" inside <textarea> should pass through as '<', '3'.
        var t = new HtmlTokenizer();
        t.Feed("<textarea>");
        DrainOne(t);
        t.SetState(TokenizerState.Rcdata);
        t.Feed("a<3");
        t.EndOfInput();

        DrainAll(t).Should().Equal(
            new CharacterToken('a'),
            new CharacterToken('<'),
            new CharacterToken('3'),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Rcdata_close_tag_only_appropriate_when_name_matches_start()
    {
        // </p> inside <textarea> is NOT appropriate — emit as chars and stay
        // in RCDATA.
        var t = new HtmlTokenizer();
        t.Feed("<textarea>");
        DrainOne(t);
        t.SetState(TokenizerState.Rcdata);
        t.Feed("a</p>b</textarea>");
        t.EndOfInput();

        DrainAll(t).Should().Equal(
            new CharacterToken('a'),
            new CharacterToken('<'),
            new CharacterToken('/'),
            new CharacterToken('p'),
            // The '>' after the inappropriate end tag attempt is reconsumed
            // in Rcdata (the host state); '>' isn't special in Rcdata so it
            // emits as a character.
            new CharacterToken('>'),
            new CharacterToken('b'),
            new EndTagToken("textarea", [], false),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Rcdata_null_maps_to_replacement_character_with_parse_error()
    {
        var sink = new RecordingSink();
        var t = new HtmlTokenizer(sink);
        t.Feed("<textarea>");
        DrainOne(t);
        t.SetState(TokenizerState.Rcdata);
        t.Feed("a\0b");
        t.EndOfInput();

        DrainAll(t).Should().Equal(
            new CharacterToken('a'),
            new CharacterToken(0xFFFD),
            new CharacterToken('b'),
            EndOfFileToken.Instance);

        sink.Errors.Should().ContainSingle()
            .Which.code.Should().Be(HtmlParseError.UnexpectedNullCharacter);
    }

    [TestMethod]
    public void Rcdata_uppercase_end_tag_name_normalizes_and_closes()
    {
        var t = new HtmlTokenizer();
        t.Feed("<textarea>");
        DrainOne(t);
        t.SetState(TokenizerState.Rcdata);
        t.Feed("x</TEXTAREA>");
        t.EndOfInput();

        DrainAll(t).Should().Equal(
            new CharacterToken('x'),
            new EndTagToken("textarea", [], false),
            EndOfFileToken.Instance);
    }

    // ----- RAWTEXT ---------------------------------------------------------

    [TestMethod]
    public void Rawtext_does_not_decode_entities()
    {
        var t = new HtmlTokenizer();
        t.Feed("<style>");
        DrainOne(t);
        t.SetState(TokenizerState.Rawtext);
        t.Feed("a&amp;b</style>");
        t.EndOfInput();

        DrainAll(t).Should().Equal(
            new CharacterToken('a'),
            new CharacterToken('&'),
            new CharacterToken('a'),
            new CharacterToken('m'),
            new CharacterToken('p'),
            new CharacterToken(';'),
            new CharacterToken('b'),
            new EndTagToken("style", [], false),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Rawtext_closes_only_on_matching_end_tag_name()
    {
        var t = new HtmlTokenizer();
        t.Feed("<style>");
        DrainOne(t);
        t.SetState(TokenizerState.Rawtext);
        t.Feed("</foo></style>");
        t.EndOfInput();

        DrainAll(t).Should().Equal(
            new CharacterToken('<'),
            new CharacterToken('/'),
            new CharacterToken('f'),
            new CharacterToken('o'),
            new CharacterToken('o'),
            new CharacterToken('>'),
            new EndTagToken("style", [], false),
            EndOfFileToken.Instance);
    }

    // ----- PLAINTEXT -------------------------------------------------------

    [TestMethod]
    public void Plaintext_consumes_everything_to_eof()
    {
        var t = new HtmlTokenizer();
        t.SetState(TokenizerState.Plaintext);
        t.Feed("<a>not a tag</a>");
        t.EndOfInput();

        // Plaintext has no escape — every character (including '<', '>') is
        // emitted as a character token.
        var tokens = DrainAll(t);
        tokens.Last().Should().Be(EndOfFileToken.Instance);
        var chars = tokens.Take(tokens.Count - 1)
            .Cast<CharacterToken>().Select(t => (char)t.CodePoint);
        new string(chars.ToArray()).Should().Be("<a>not a tag</a>");
    }

    [TestMethod]
    public void Plaintext_null_maps_to_replacement_character()
    {
        var sink = new RecordingSink();
        var t = new HtmlTokenizer(sink);
        t.SetState(TokenizerState.Plaintext);
        t.Feed("a\0b");
        t.EndOfInput();

        DrainAll(t).Should().Equal(
            new CharacterToken('a'),
            new CharacterToken(0xFFFD),
            new CharacterToken('b'),
            EndOfFileToken.Instance);

        sink.Errors.Should().ContainSingle()
            .Which.code.Should().Be(HtmlParseError.UnexpectedNullCharacter);
    }

    // ----- EOF in mid-attempt ---------------------------------------------

    [TestMethod]
    public void Rcdata_eof_mid_end_tag_attempt_emits_buffered_chars()
    {
        // Feed: <textarea>x</textare<EOF> — the close attempt is partial; the
        // tokenizer must flush '<', '/', 't', 'e', 'x', 't', 'a', 'r', 'e'
        // as character tokens before EOF.
        var t = new HtmlTokenizer();
        t.Feed("<textarea>");
        DrainOne(t);
        t.SetState(TokenizerState.Rcdata);
        t.Feed("x</textare");
        t.EndOfInput();

        DrainAll(t).Should().Equal(
            new CharacterToken('x'),
            new CharacterToken('<'),
            new CharacterToken('/'),
            new CharacterToken('t'),
            new CharacterToken('e'),
            new CharacterToken('x'),
            new CharacterToken('t'),
            new CharacterToken('a'),
            new CharacterToken('r'),
            new CharacterToken('e'),
            EndOfFileToken.Instance);
    }

    // helpers ---------------------------------------------------------------

    private static HtmlToken DrainOne(HtmlTokenizer t)
    {
        var tok = t.ReadToken();
        tok.Should().NotBeNull();
        return tok!;
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
