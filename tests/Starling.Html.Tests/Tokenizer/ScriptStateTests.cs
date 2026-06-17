using AwesomeAssertions;
using Starling.Html.Tokenizer;
namespace Starling.Html.Tests.Tokenizer;

[TestClass]
public class ScriptStateTests
{
    [TestMethod]
    public void ScriptData_passes_text_through_and_closes_on_matching_end_tag()
    {
        var t = new HtmlTokenizer();
        t.Feed("<script>");
        DrainOne(t).Should().Be(new StartTagToken("script", [], false));

        t.SetState(TokenizerState.ScriptData);
        t.Feed("if (a < b) alert(1);</script>");
        t.EndOfInput();

        var tokens = DrainAll(t);
        CharacterText(tokens[..^2]).Should().Be("if (a < b) alert(1);");
        tokens[^2].Should().Be(new EndTagToken("script", [], false));
        tokens[^1].Should().Be(EndOfFileToken.Instance);
    }

    [TestMethod]
    public void ScriptData_close_tag_only_appropriate_when_name_matches_start()
    {
        var t = new HtmlTokenizer();
        t.Feed("<script>");
        DrainOne(t);
        t.SetState(TokenizerState.ScriptData);
        t.Feed("a</style>b</script>");
        t.EndOfInput();

        var tokens = DrainAll(t);
        CharacterText(tokens[..^2]).Should().Be("a</style>b");
        tokens[^2].Should().Be(new EndTagToken("script", [], false));
        tokens[^1].Should().Be(EndOfFileToken.Instance);
    }

    [TestMethod]
    public void ScriptData_escaped_end_tag_can_close_script()
    {
        var t = new HtmlTokenizer();
        t.Feed("<script>");
        DrainOne(t);
        t.SetState(TokenizerState.ScriptData);
        t.Feed("<!--alert(1)</script>");
        t.EndOfInput();

        var tokens = DrainAll(t);
        CharacterText(tokens[..^2]).Should().Be("<!--alert(1)");
        tokens[^2].Should().Be(new EndTagToken("script", [], false));
        tokens[^1].Should().Be(EndOfFileToken.Instance);
    }

    [TestMethod]
    public void ScriptData_double_escaped_script_end_tag_is_text_until_escape_ends()
    {
        var t = new HtmlTokenizer();
        t.Feed("<script>");
        DrainOne(t);
        t.SetState(TokenizerState.ScriptData);
        t.Feed("<!--<script></script>--></script>");
        t.EndOfInput();

        var tokens = DrainAll(t);
        CharacterText(tokens[..^2]).Should().Be("<!--<script></script>-->");
        tokens[^2].Should().Be(new EndTagToken("script", [], false));
        tokens[^1].Should().Be(EndOfFileToken.Instance);
    }

    [TestMethod]
    public void ScriptData_null_maps_to_replacement_character_with_parse_error()
    {
        var sink = new RecordingSink();
        var t = new HtmlTokenizer(sink);
        t.SetState(TokenizerState.ScriptData);
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
    public void ScriptData_eof_in_escaped_text_reports_script_comment_like_error()
    {
        var sink = new RecordingSink();
        var t = new HtmlTokenizer(sink);
        t.SetState(TokenizerState.ScriptData);
        t.Feed("<!--unterminated");
        t.EndOfInput();

        CharacterText(DrainAll(t)[..^1]).Should().Be("<!--unterminated");
        sink.Errors.Should().ContainSingle()
            .Which.code.Should().Be(HtmlParseError.EofInScriptHtmlCommentLikeText);
    }

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

    private static string CharacterText(IEnumerable<HtmlToken> tokens)
        => new(tokens.Cast<CharacterToken>().Select(t => (char)t.CodePoint).ToArray());

    private sealed class RecordingSink : IParseErrorSink
    {
        public List<(HtmlParseError code, int line, int column)> Errors { get; } = [];

        public void Report(HtmlParseError code, int line, int column)
            => Errors.Add((code, line, column));
    }
}
