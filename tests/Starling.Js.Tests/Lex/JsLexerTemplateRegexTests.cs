using AwesomeAssertions;
using Starling.Js.Lex;
namespace Starling.Js.Tests.Lex;

/// <summary>
/// Lexer hardening (B1a): template literals, regex literals, private
/// identifiers, and hashbang comments. These are parser-driven for the
/// ambiguous cases (template body / regex vs. division), so the tests drive
/// the lexer directly via its <c>Scan*</c> entry points where needed.
/// </summary>
[TestClass]
public class JsLexerTemplateRegexTests
{
    [TestMethod]
    public void Template_with_no_substitution_yields_single_token()
    {
        var lex = new JsLexer("`hello`");
        var t = lex.Next();
        t.Kind.Should().Be(JsTokenKind.TemplateNoSubstitution);
        t.Value.Should().Be("hello");
        lex.Next().Kind.Should().Be(JsTokenKind.EndOfFile);
    }

    [TestMethod]
    public void Template_with_substitution_yields_head_expr_tail()
    {
        var lex = new JsLexer("`hi ${ name }!`");
        var head = lex.Next();
        head.Kind.Should().Be(JsTokenKind.TemplateHead);
        head.Value.Should().Be("hi ");

        var ident = lex.Next();
        ident.Kind.Should().Be(JsTokenKind.Identifier);
        ident.Lexeme.ToString().Should().Be("name");

        // The parser would close the substitution by seeing `}` as RBrace,
        // then call ScanTemplateContinuation. The lexer first emits `}` as
        // RBrace via the normal Scan path; we exercise both modes here.
        var closing = lex.Next();
        closing.Kind.Should().Be(JsTokenKind.RBrace);

        var tail = lex.ScanTemplateContinuation();
        tail.Kind.Should().Be(JsTokenKind.TemplateTail);
        tail.Value.Should().Be("!");
    }

    [TestMethod]
    public void Template_with_two_substitutions_emits_middle()
    {
        var lex = new JsLexer("`${a}-${b}`");
        lex.Next().Kind.Should().Be(JsTokenKind.TemplateHead);
        lex.Next().Lexeme.ToString().Should().Be("a");
        lex.Next().Kind.Should().Be(JsTokenKind.RBrace);

        var mid = lex.ScanTemplateContinuation();
        mid.Kind.Should().Be(JsTokenKind.TemplateMiddle);
        mid.Value.Should().Be("-");

        lex.Next().Lexeme.ToString().Should().Be("b");
        lex.Next().Kind.Should().Be(JsTokenKind.RBrace);

        var tail = lex.ScanTemplateContinuation();
        tail.Kind.Should().Be(JsTokenKind.TemplateTail);
        tail.Value.Should().Be("");
    }

    [TestMethod]
    public void Template_handles_escapes_and_newlines()
    {
        var lex = new JsLexer("`line1\nline2\\nline3`");
        var t = lex.Next();
        t.Kind.Should().Be(JsTokenKind.TemplateNoSubstitution);
        t.Value.Should().Be("line1\nline2\nline3");
    }

    [TestMethod]
    public void Regex_literal_lexed_via_parser_entry_point()
    {
        var lex = new JsLexer("/ab\\/c/gi");
        var rx = lex.ScanRegExp();
        rx.Kind.Should().Be(JsTokenKind.RegExpLiteral);
        var (pattern, flags) = ((string, string))rx.Value!;
        pattern.Should().Be("ab\\/c");
        flags.Should().Be("gi");
    }

    [TestMethod]
    public void Regex_character_class_swallows_slashes()
    {
        var lex = new JsLexer("/[/]/");
        var rx = lex.ScanRegExp();
        rx.Kind.Should().Be(JsTokenKind.RegExpLiteral);
        var (pattern, flags) = ((string, string))rx.Value!;
        pattern.Should().Be("[/]");
        flags.Should().BeEmpty();
    }

    [TestMethod]
    public void Private_identifier_emitted_with_hash_prefix()
    {
        var lex = new JsLexer("#field");
        var t = lex.Next();
        t.Kind.Should().Be(JsTokenKind.PrivateIdentifier);
        t.Value.Should().Be("#field");
    }

    [TestMethod]
    public void Hashbang_skipped_at_start_of_file()
    {
        var lex = new JsLexer("#!/usr/bin/env node\nlet x;");
        var letTok = lex.Next();
        letTok.Lexeme.ToString().Should().Be("let");
        letTok.Kind.Should().Be(JsTokenKind.Identifier);
    }
}
