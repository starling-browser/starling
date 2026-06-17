using Starling.Js.Ast;
using Starling.Js.Lex;

namespace Starling.Js.Parse;

/// <summary>
/// ES2024 §16.2 module item parsing. These productions are only dispatched by
/// <see cref="ParseProgram()"/> so static import/export declarations remain
/// restricted to program scope.
/// </summary>
public ref partial struct JsParser
{
    private Statement ParseProgramStatement()
    {
        // wp:M3-03c — `import(` / `import.` start a dynamic-import call or
        // `import.meta` *expression statement*, not a static ImportDeclaration.
        // Disambiguate on the token after `import` so static `import …` decls
        // stay routed here while the expression forms fall through to
        // ParseStatement → ParseExpression → ParsePrimary.
        if (_current.Kind == JsTokenKind.Import)
        {
            var next = _lex.Peek().Kind;
            if (next is JsTokenKind.LParen or JsTokenKind.Dot)
            {
                return ParseStatement();
            }

            return ParseImportDeclaration();
        }

        return _current.Kind switch
        {
            JsTokenKind.Export => ParseExportDeclaration(),
            _ => ParseStatement(),
        };
    }

    /// <summary>wp:M3-03c — parse the expression-context forms of <c>import</c>:
    /// <c>import(specifier [, options])</c> (ES2024 §13.3.10 ImportCall) and
    /// <c>import.meta</c> (§13.3.12 ImportMeta). The leading <c>import</c> token
    /// is current on entry. Static <c>import …</c> declarations never reach here —
    /// they are dispatched only at program scope by
    /// <see cref="ParseProgramStatement"/>.</summary>
    private Expression ParseImportExpression()
    {
        var start = _current.Start;
        Advance(); // import

        if (Check(JsTokenKind.Dot))
        {
            Advance(); // .
            var meta = ExpectIdentifierName("expected 'meta' after 'import.'");
            if (!meta.TextEquals("meta"))
            {
                throw new JsParseException(
                    $"the only valid meta-property for import is 'import.meta' (got 'import.{meta.Lexeme}')",
                    meta.Start);
            }

            return new ImportMetaExpression(start, meta.End);
        }

        if (Check(JsTokenKind.LParen))
        {
            Advance(); // (
            var specifier = ParseAssignment();
            Expression? options = null;
            if (Match(JsTokenKind.Comma) && !Check(JsTokenKind.RParen))
            {
                options = ParseAssignment(); // import attributes — parsed, ignored
            }
            // tolerate a trailing comma: import(spec,) / import(spec, opts,)
            Match(JsTokenKind.Comma);
            var end = _current.End;
            Expect(JsTokenKind.RParen, "expected ')' to close dynamic import call");
            return new ImportCallExpression(specifier, options, start, end);
        }

        throw new JsParseException(
            "'import' is only valid as 'import(...)' or 'import.meta' in expression position",
            _current.Start);
    }

    /// <summary>Parse ES2024 §16.2.2 ImportDeclaration.</summary>
    private ImportDeclaration ParseImportDeclaration()
    {
        var start = _current.Start;
        Advance(); // import

        if (Check(JsTokenKind.StringLiteral))
        {
            var (source, sourceEnd) = ParseModuleSpecifierString();
            ConsumeSemicolonOrAsi();
            return new ImportDeclaration(
                new ImportSpecifier[] { new ImportSideEffectSpecifier(start, sourceEnd) },
                source, start, sourceEnd);
        }

        var specifiers = new List<ImportSpecifier>();
        if (Check(JsTokenKind.Identifier))
        {
            var local = ParseImportedBinding("expected default import binding name");
            specifiers.Add(new ImportDefaultSpecifier(local, local.Start, local.End));
            if (Match(JsTokenKind.Comma))
            {
                if (Check(JsTokenKind.Star))
                {
                    specifiers.Add(ParseImportNamespaceSpecifier());
                }
                else if (Check(JsTokenKind.LBrace))
                {
                    specifiers.AddRange(ParseNamedImportSpecifiers());
                }
                else
                {
                    throw new JsParseException("expected namespace or named import after ','", _current.Start);
                }
            }
        }
        else if (Check(JsTokenKind.Star))
        {
            specifiers.Add(ParseImportNamespaceSpecifier());
        }
        else if (Check(JsTokenKind.LBrace))
        {
            specifiers.AddRange(ParseNamedImportSpecifiers());
        }
        else
        {
            throw new JsParseException("expected import binding, namespace import, named import, or string literal", _current.Start);
        }

        ExpectContextualIdentifier("from", "expected 'from' in import declaration");
        var (module, end) = ParseModuleSpecifierString();
        ConsumeSemicolonOrAsi();
        return new ImportDeclaration(specifiers, module, start, end);
    }

    private ImportNamespaceSpecifier ParseImportNamespaceSpecifier()
    {
        var start = _current.Start;
        Advance(); // *
        ExpectContextualIdentifier("as", "expected 'as' in namespace import");
        var local = ParseImportedBinding("expected namespace import binding name");
        return new ImportNamespaceSpecifier(local, start, local.End);
    }

    private List<ImportSpecifier> ParseNamedImportSpecifiers()
    {
        Expect(JsTokenKind.LBrace, "expected '{' to open named import list");
        var specifiers = new List<ImportSpecifier>();
        while (!Check(JsTokenKind.RBrace))
        {
            var imported = ParseModuleExportName("expected imported name");
            Identifier local;
            if (MatchContextualIdentifier("as"))
            {
                local = ParseImportedBinding("expected local binding name after 'as'");
            }
            else if (imported is Identifier id)
            {
                // `import { x }` binds local `x` — its BindingIdentifier must
                // satisfy the strict (module) binding-name rules just like the
                // `as`-target above (so `import { eval }` / `import { await }`
                // are early errors).
                CheckBindingIdentifier(id.Name, id.Start);
                local = new Identifier(id.Name, id.Start, id.End);
            }
            else
            {
                throw new JsParseException("string-named imports require an 'as' binding", imported.Start);
            }
            specifiers.Add(new ImportNamedSpecifier(imported, local, imported.Start, local.End));
            if (!Match(JsTokenKind.Comma))
            {
                break;
            }

            if (Check(JsTokenKind.RBrace))
            {
                break;
            }
        }
        Expect(JsTokenKind.RBrace, "expected '}' to close named import list");
        return specifiers;
    }

    /// <summary>Parse ES2024 §16.2.3 ExportDeclaration.</summary>
    private ExportDeclaration ParseExportDeclaration()
    {
        var start = _current.Start;
        Advance(); // export

        // §16.2.3 — the `default` keyword may not contain a UnicodeEscapeSequence
        // (`export default …` is a SyntaxError). The lexer keeps the keyword
        // kind but tags the escape.
        if (Check(JsTokenKind.Default) && _current.ContainsEscape)
        {
            throw new JsParseException(
                "the 'default' keyword may not contain an escape sequence", _current.Start);
        }

        if (Match(JsTokenKind.Default))
        {
            return ParseExportDefaultDeclaration(start);
        }

        if (Check(JsTokenKind.Star))
        {
            return ParseExportAllDeclaration(start);
        }

        if (Check(JsTokenKind.LBrace))
        {
            return ParseExportNamedDeclaration(start);
        }

        Statement declaration = _current.Kind switch
        {
            JsTokenKind.Var => ParseVar("var"),
            JsTokenKind.Const => ParseVar("const"),
            JsTokenKind.Function => ParseFunctionDeclaration(),
            JsTokenKind.Class => ParseClassDeclarationWithExtendsTracking(),
            _ when _current.Kind == JsTokenKind.Identifier && _current.TextEquals("let") && IsLetDeclarationStart()
                => ParseVar("let"),
            _ when _current.Kind == JsTokenKind.Identifier && _current.TextEquals("async") && _lex.Peek().Kind == JsTokenKind.Function
                => ParseAsyncFunctionDeclaration(),
            _ => throw new JsParseException("expected declaration, 'default', '*', or named export list after 'export'", _current.Start),
        };
        return new ExportLocalDeclaration(declaration, start, declaration.End);
    }

    private FunctionDeclaration ParseAsyncFunctionDeclaration()
    {
        var asyncStart = _current.Start;
        Advance(); // async
        return ParseFunctionDeclaration(asyncStart, isAsync: true);
    }

    private ExportDefaultDeclaration ParseExportDefaultDeclaration(JsPosition start)
    {
        AstNode declaration;
        if (Check(JsTokenKind.Function))
        {
            declaration = ParseFunctionExpression();
        }
        else if (Check(JsTokenKind.Class))
        {
            declaration = ParseClassExpression();
        }
        else if (_current.Kind == JsTokenKind.Identifier && _current.TextEquals("async") && _lex.Peek().Kind == JsTokenKind.Function)
        {
            var asyncStart = _current.Start;
            Advance(); // async
            declaration = ParseFunctionExpression(asyncStart, isAsync: true);
        }
        else
        {
            // §16.2.3.1 — `export default AssignmentExpression ;`. It is an
            // AssignmentExpression, NOT an Expression, so a top-level comma
            // sequence (`export default a, b`) is a SyntaxError.
            var expr = ParseAssignment();
            ConsumeSemicolonOrAsi();
            return new ExportDefaultDeclaration(expr, start, expr.End);
        }
        return new ExportDefaultDeclaration(declaration, start, declaration.End);
    }

    private ExportAllDeclaration ParseExportAllDeclaration(JsPosition start)
    {
        Advance(); // *
        Identifier? exportedName = null;
        if (MatchContextualIdentifier("as"))
        {
            var name = ExpectIdentifierName("expected exported namespace name after 'as'");
            exportedName = new Identifier(name.Lexeme, name.Start, name.End);
        }
        ExpectContextualIdentifier("from", "expected 'from' after export *");
        var (source, end) = ParseModuleSpecifierString();
        ConsumeSemicolonOrAsi();
        return new ExportAllDeclaration(source, exportedName, start, end);
    }

    private ExportNamedDeclaration ParseExportNamedDeclaration(JsPosition start)
    {
        Expect(JsTokenKind.LBrace, "expected '{' to open named export list");
        var specifiers = new List<ExportSpecifier>();
        while (!Check(JsTokenKind.RBrace))
        {
            var local = ParseModuleExportName("expected exported local name");
            var exported = local;
            if (MatchContextualIdentifier("as"))
            {
                exported = ParseModuleExportName("expected exported name after 'as'");
            }

            specifiers.Add(new ExportSpecifier(local, exported, local.Start, exported.End));
            if (!Match(JsTokenKind.Comma))
            {
                break;
            }

            if (Check(JsTokenKind.RBrace))
            {
                break;
            }
        }
        Expect(JsTokenKind.RBrace, "expected '}' to close named export list");

        string? source = null;
        JsPosition end = specifiers.Count > 0 ? specifiers[^1].End : _current.End;
        if (MatchContextualIdentifier("from"))
        {
            (source, end) = ParseModuleSpecifierString();
        }
        ConsumeSemicolonOrAsi();
        return new ExportNamedDeclaration(specifiers, source, start, end);
    }

    private Identifier ParseImportedBinding(string message)
    {
        var tok = Expect(JsTokenKind.Identifier, message);
        // §16.2.2 ImportedBinding is a BindingIdentifier in strict (module) code:
        // `eval`/`arguments`/reserved words (incl. `await`) are SyntaxErrors.
        CheckModuleBindingName(tok);
        return new Identifier(tok.Lexeme, tok.Start, tok.End);
    }

    private Expression ParseModuleExportName(string message)
    {
        if (Check(JsTokenKind.StringLiteral))
        {
            var tok = Advance();
            return new StringLiteral((string)tok.Value!, tok.Start, tok.End);
        }
        var name = ExpectIdentifierName(message);
        return new Identifier(name.Lexeme, name.Start, name.End);
    }

    private (string Source, JsPosition End) ParseModuleSpecifierString()
    {
        var tok = Expect(JsTokenKind.StringLiteral, "expected module specifier string literal");
        return ((string)tok.Value!, tok.End);
    }

    private void ExpectContextualIdentifier(string lexeme, string message)
    {
        if (!MatchContextualIdentifier(lexeme))
        {
            throw new JsParseException($"{message} (got {_current.Kind} '{_current.Lexeme}')", _current.Start);
        }
    }

    private bool MatchContextualIdentifier(string lexeme)
    {
        // §16.2.2 / §16.2.3 — the contextual keywords `as` and `from` may not
        // contain a UnicodeEscapeSequence (an escaped `from` / `as` is
        // not the keyword), so reject an escaped spelling here.
        if (_current.Kind != JsTokenKind.Identifier || _current.Lexeme != lexeme
            || _current.ContainsEscape)
        {
            return false;
        }

        Advance();
        return true;
    }
}
