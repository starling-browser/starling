using AwesomeAssertions;
using Starling.Html.Tokenizer;
namespace Starling.Html.Tests.Tokenizer;

/// <summary>Character-reference cluster — wp:M1-01g.</summary>
[TestClass]
public class CharRefTests
{
    // ----- Named references -----------------------------------------------

    [TestMethod]
    public void Amp_with_semicolon_decodes()
    {
        Tokenize("&amp;").Should().Equal(
            new CharacterToken('&'),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Lt_gt_quot_decode()
    {
        Tokenize("&lt;a&gt;&quot;").Should().Equal(
            new CharacterToken('<'),
            new CharacterToken('a'),
            new CharacterToken('>'),
            new CharacterToken('"'),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Nbsp_decodes_to_u00a0()
    {
        Tokenize("&nbsp;").Should().Equal(
            new CharacterToken(0x00A0),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Copy_no_semicolon_decodes_with_parse_error()
    {
        // &copy is a legacy "no-semicolon" reference; the spec calls it a
        // missing-semicolon parse error but still decodes.
        var sink = new RecordingSink();
        var t = new HtmlTokenizer(sink);
        t.Feed("&copy hello");
        t.EndOfInput();

        var tokens = DrainAll(t);
        tokens[0].Should().Be(new CharacterToken(0x00A9));
        sink.Errors.Should().ContainSingle()
            .Which.code.Should().Be(HtmlParseError.MissingSemicolonAfterCharacterReference);
    }

    [TestMethod]
    public void Longest_prefix_match_picks_notin_over_not()
    {
        // 'not' and 'notin' both decode; longest must win.
        Tokenize("&notin;").Should().Equal(
            new CharacterToken(0x2209),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Two_codepoint_entity()
    {
        // &nvgt; decodes to U+003E followed by U+20D2.
        Tokenize("&nvgt;").Should().Equal(
            new CharacterToken(0x003E),
            new CharacterToken(0x20D2),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Unknown_named_reference_emits_chars_with_parse_error()
    {
        // &zzz; doesn't exist; tokenizer should emit '&', 'z', 'z', 'z', ';'
        // with an unknown-named-character-reference parse error on the ';'.
        var sink = new RecordingSink();
        var t = new HtmlTokenizer(sink);
        t.Feed("&zzz;");
        t.EndOfInput();

        DrainAll(t).Should().Equal(
            new CharacterToken('&'),
            new CharacterToken('z'),
            new CharacterToken('z'),
            new CharacterToken('z'),
            new CharacterToken(';'),
            EndOfFileToken.Instance);
        sink.Errors.Should().ContainSingle()
            .Which.code.Should().Be(HtmlParseError.UnknownNamedCharacterReference);
    }

    [TestMethod]
    public void Bare_ampersand_passes_through()
    {
        // & not followed by alpha or # is data per §13.2.5.72.
        Tokenize("a&b").Should().Equal(
            new CharacterToken('a'),
            new CharacterToken('&'),
            new CharacterToken('b'),
            EndOfFileToken.Instance);
    }

    // ----- Numeric references ---------------------------------------------

    [TestMethod]
    public void Decimal_numeric_reference()
    {
        // &#65; → 'A'
        Tokenize("&#65;").Should().Equal(
            new CharacterToken('A'),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Hex_numeric_reference()
    {
        // &#x41; → 'A'
        Tokenize("&#x41;").Should().Equal(
            new CharacterToken('A'),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Hex_supports_uppercase_X()
    {
        Tokenize("&#X4F;").Should().Equal(
            new CharacterToken('O'),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Null_reference_becomes_replacement_character()
    {
        var sink = new RecordingSink();
        var t = new HtmlTokenizer(sink);
        t.Feed("&#0;");
        t.EndOfInput();

        DrainAll(t).Should().Equal(
            new CharacterToken(0xFFFD),
            EndOfFileToken.Instance);
        sink.Errors.Should().ContainSingle()
            .Which.code.Should().Be(HtmlParseError.NullCharacterReference);
    }

    [TestMethod]
    public void Out_of_range_reference_becomes_replacement_character()
    {
        var sink = new RecordingSink();
        var t = new HtmlTokenizer(sink);
        t.Feed("&#x110000;");
        t.EndOfInput();

        DrainAll(t).Should().Equal(
            new CharacterToken(0xFFFD),
            EndOfFileToken.Instance);
        sink.Errors.Should().ContainSingle()
            .Which.code.Should().Be(HtmlParseError.CharacterReferenceOutsideUnicodeRange);
    }

    [TestMethod]
    public void Surrogate_reference_becomes_replacement_character()
    {
        var sink = new RecordingSink();
        var t = new HtmlTokenizer(sink);
        t.Feed("&#xD800;");
        t.EndOfInput();

        DrainAll(t).Should().Equal(
            new CharacterToken(0xFFFD),
            EndOfFileToken.Instance);
        sink.Errors.Should().ContainSingle()
            .Which.code.Should().Be(HtmlParseError.SurrogateCharacterReference);
    }

    [TestMethod]
    public void C1_control_replacement_for_0x80_yields_euro_sign()
    {
        // &#128; (0x80) maps to U+20AC EURO SIGN per the spec's compat table.
        var sink = new RecordingSink();
        var t = new HtmlTokenizer(sink);
        t.Feed("&#128;");
        t.EndOfInput();

        DrainAll(t).Should().Equal(
            new CharacterToken(0x20AC),
            EndOfFileToken.Instance);
        sink.Errors.Should().ContainSingle()
            .Which.code.Should().Be(HtmlParseError.ControlCharacterReference);
    }

    [TestMethod]
    public void Hash_alone_emits_literal_chars()
    {
        // &#x without digits = parse error + emit '&', '#', 'x'.
        var sink = new RecordingSink();
        var t = new HtmlTokenizer(sink);
        t.Feed("&#x ");
        t.EndOfInput();

        DrainAll(t).Should().Equal(
            new CharacterToken('&'),
            new CharacterToken('#'),
            new CharacterToken('x'),
            new CharacterToken(' '),
            EndOfFileToken.Instance);
        sink.Errors.Should().ContainSingle()
            .Which.code.Should().Be(HtmlParseError.AbsenceOfDigitsInNumericCharacterReference);
    }

    // ----- References inside attributes ----------------------------------

    [TestMethod]
    public void Entity_inside_double_quoted_attribute_decodes_to_attr_value()
    {
        Tokenize("<a href=\"x?a=1&amp;b=2\">").Should().Equal(
            new StartTagToken("a",
                [new HtmlAttribute("href", "x?a=1&b=2")],
                false),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Entity_inside_single_quoted_attribute_decodes()
    {
        Tokenize("<a title='&lt;hi&gt;'>").Should().Equal(
            new StartTagToken("a",
                [new HtmlAttribute("title", "<hi>")],
                false),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Entity_inside_unquoted_attribute_decodes()
    {
        Tokenize("<a id=&amp;>").Should().Equal(
            new StartTagToken("a",
                [new HtmlAttribute("id", "&")],
                false),
            EndOfFileToken.Instance);
    }

    [TestMethod]
    public void Numeric_reference_inside_attribute_decodes()
    {
        Tokenize("<a t=\"&#65;\">").Should().Equal(
            new StartTagToken("a",
                [new HtmlAttribute("t", "A")],
                false),
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
