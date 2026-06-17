using AwesomeAssertions;
using Starling.Html.Tokenizer;
namespace Starling.Html.Tests.Tokenizer;

[TestClass]
public class DataStateTests
{
    [TestMethod]
    public void Empty_input_emits_only_eof()
    {
        var t = new HtmlTokenizer();
        t.EndOfInput();

        Drain(t).Should().ContainSingle().Which.Should().Be(EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Plain_ascii_emits_character_tokens_then_eof()
    {
        var t = new HtmlTokenizer();
        t.Feed("abc");
        t.EndOfInput();

        Drain(t).Should().Equal(
            new CharacterToken('a'),
            new CharacterToken('b'),
            new CharacterToken('c'),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Crlf_collapses_to_lf_character()
    {
        var t = new HtmlTokenizer();
        t.Feed("a\r\nb");
        t.EndOfInput();

        Drain(t).Should().Equal(
            new CharacterToken('a'),
            new CharacterToken('\n'),
            new CharacterToken('b'),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Null_in_data_state_emits_literal_zero_and_reports_parse_error()
    {
        // §13.2.5.1: Data state emits NULL verbatim as a character token.
        // The replacement-character mapping is per name-buffer state, not Data.
        var sink = new RecordingSink();
        var t = new HtmlTokenizer(sink);
        t.Feed("a\0b");
        t.EndOfInput();

        Drain(t).Should().Equal(
            new CharacterToken('a'),
            new CharacterToken(0),
            new CharacterToken('b'),
            EndOfFileToken.Instance);

        sink.Errors.Should().ContainSingle()
            .Which.code.Should().Be(HtmlParseError.UnexpectedNullCharacter);
    }

    [TestMethod]
    public void Chunked_feed_produces_same_tokens_as_whole_input()
    {
        var whole = TokenizeWhole("hello, world.");
        var chunked = TokenizeChunked("hello, world.", chunkSize: 1);

        chunked.Should().Equal(whole);
    }

    [TestMethod]
    public void Eof_token_is_sticky()
    {
        // Once EOF is emitted, further ReadToken calls keep returning null
        // (no further tokens — EOF is terminal). We assert null rather than
        // re-emitting because the parser drives one EOF and stops.
        var t = new HtmlTokenizer();
        t.EndOfInput();

        var first = t.ReadToken();
        first.Should().Be(EndOfFileToken.Instance);

        t.ReadToken().Should().BeNull();
        t.ReadToken().Should().BeNull();
    }

    [TestMethod]
    public void Read_token_returns_null_when_blocked_on_more_input()
    {
        var t = new HtmlTokenizer();
        // No Feed, no EndOfInput → no data, no decision possible.
        t.ReadToken().Should().BeNull();

        // After feeding one char we should get exactly one token, then null
        // again (blocked: more chars may yet arrive).
        t.Feed("x");
        t.ReadToken().Should().Be(new CharacterToken('x'));
        t.ReadToken().Should().BeNull();
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

    private static List<HtmlToken> TokenizeWhole(string input)
    {
        var t = new HtmlTokenizer();
        t.Feed(input);
        t.EndOfInput();
        return Drain(t);
    }

    private static List<HtmlToken> TokenizeChunked(string input, int chunkSize)
    {
        var t = new HtmlTokenizer();
        for (var i = 0; i < input.Length; i += chunkSize)
        {
            var len = Math.Min(chunkSize, input.Length - i);
            t.Feed(input.AsSpan(i, len));
        }
        t.EndOfInput();
        return Drain(t);
    }

    private sealed class RecordingSink : IParseErrorSink
    {
        public List<(HtmlParseError code, int line, int column)> Errors { get; } = [];

        public void Report(HtmlParseError code, int line, int column)
            => Errors.Add((code, line, column));
    }
}
