using AwesomeAssertions;
using Starling.Js.Ast;
using Starling.Js.Lex;
using Starling.Js.Parse;

namespace Starling.Js.Tests.Parse;

[TestClass]
public class JsParserModuleTests
{
    [TestMethod]
    public void Import_side_effect_only()
    {
        var import = ParseSingle<ImportDeclaration>("import 'polyfill';");
        import.Source.Should().Be("polyfill");
        import.Specifiers.Should().ContainSingle().Which.Should().BeOfType<ImportSideEffectSpecifier>();
    }

    [TestMethod]
    public void Import_default_binding()
    {
        var import = ParseSingle<ImportDeclaration>("import thing from 'mod';");
        var spec = import.Specifiers.Should().ContainSingle().Which.Should().BeOfType<ImportDefaultSpecifier>().Subject;
        spec.Local.Name.Should().Be("thing");
        import.Source.Should().Be("mod");
    }

    [TestMethod]
    public void Import_namespace_binding()
    {
        var spec = ParseSingle<ImportDeclaration>("import * as ns from 'mod';")
            .Specifiers.Should().ContainSingle().Which.Should().BeOfType<ImportNamespaceSpecifier>().Subject;
        spec.Local.Name.Should().Be("ns");
    }

    [TestMethod]
    public void Import_named_single_shorthand()
    {
        var spec = ParseSingle<ImportDeclaration>("import { a } from 'mod';")
            .Specifiers.Should().ContainSingle().Which.Should().BeOfType<ImportNamedSpecifier>().Subject;
        ((Identifier)spec.Imported).Name.Should().Be("a");
        spec.Local.Name.Should().Be("a");
    }

    [TestMethod]
    public void Import_named_aliases()
    {
        var import = ParseSingle<ImportDeclaration>("import { a, b as c, default as d } from 'mod';");
        import.Specifiers.Should().HaveCount(3);
        ((ImportNamedSpecifier)import.Specifiers[1]).Local.Name.Should().Be("c");
        ((Identifier)((ImportNamedSpecifier)import.Specifiers[2]).Imported).Name.Should().Be("default");
    }

    [TestMethod]
    public void Import_named_string_alias()
    {
        var spec = ParseSingle<ImportDeclaration>("import { 'str-name' as local } from 'mod';")
            .Specifiers.Should().ContainSingle().Which.Should().BeOfType<ImportNamedSpecifier>().Subject;
        ((StringLiteral)spec.Imported).Value.Should().Be("str-name");
        spec.Local.Name.Should().Be("local");
    }

    [TestMethod]
    public void Import_default_plus_namespace()
    {
        var import = ParseSingle<ImportDeclaration>("import thing, * as ns from 'mod';");
        import.Specifiers[0].Should().BeOfType<ImportDefaultSpecifier>();
        import.Specifiers[1].Should().BeOfType<ImportNamespaceSpecifier>()
            .Which.Local.Name.Should().Be("ns");
    }

    [TestMethod]
    public void Import_default_plus_named()
    {
        var import = ParseSingle<ImportDeclaration>("import thing, { a as b } from 'mod';");
        import.Specifiers.Should().HaveCount(2);
        import.Specifiers[0].Should().BeOfType<ImportDefaultSpecifier>();
        import.Specifiers[1].Should().BeOfType<ImportNamedSpecifier>()
            .Which.Local.Name.Should().Be("b");
    }

    [TestMethod]
    public void Import_named_empty_list()
    {
        var import = ParseSingle<ImportDeclaration>("import {} from 'empty';");
        import.Specifiers.Should().BeEmpty();
        import.Source.Should().Be("empty");
    }

    [TestMethod]
    public void Import_allows_asi_after_source()
        => ParseProgram("import x from 'm'\nimport y from 'n'").Body.Should().HaveCount(2);

    [TestMethod]
    public void Import_missing_from_throws()
        => Fails("import x 'mod';");

    [TestMethod]
    public void Import_namespace_missing_as_throws()
        => Fails("import * ns from 'mod';");

    [TestMethod]
    public void Import_named_string_without_alias_throws()
        => Fails("import { 'x' } from 'mod';");

    [TestMethod]
    public void Import_requires_string_source()
        => Fails("import x from mod;");

    [TestMethod]
    public void Import_declaration_is_top_level_only()
        => Fails("if (ok) import x from 'mod';");

    [TestMethod]
    public void Export_variable_declaration()
    {
        var export = ParseSingle<ExportLocalDeclaration>("export const answer = 42;");
        export.Declaration.Should().BeOfType<VariableDeclaration>()
            .Which.Kind.Should().Be("const");
    }

    [TestMethod]
    public void Export_function_declaration()
    {
        var export = ParseSingle<ExportLocalDeclaration>("export function f() { return 1; }");
        export.Declaration.Should().BeOfType<FunctionDeclaration>()
            .Which.Name.Name.Should().Be("f");
    }

    [TestMethod]
    public void Export_async_function_declaration()
    {
        var export = ParseSingle<ExportLocalDeclaration>("export async function f() { return await g(); }");
        export.Declaration.Should().BeOfType<FunctionDeclaration>()
            .Which.Async.Should().BeTrue();
    }

    [TestMethod]
    public void Export_class_declaration()
    {
        var export = ParseSingle<ExportLocalDeclaration>("export class C extends B {}");
        export.Declaration.Should().BeOfType<ClassDeclaration>()
            .Which.Name.Name.Should().Be("C");
    }

    [TestMethod]
    public void Export_named_list()
    {
        var export = ParseSingle<ExportNamedDeclaration>("export { a, b as c, default as d };");
        export.Source.Should().BeNull();
        export.Specifiers.Should().HaveCount(3);
        ((Identifier)export.Specifiers[1].Exported).Name.Should().Be("c");
    }

    [TestMethod]
    public void Export_named_list_with_string_export_name()
    {
        var spec = ParseSingle<ExportNamedDeclaration>("export { internal as 'public-name' };")
            .Specifiers.Should().ContainSingle().Subject;
        ((Identifier)spec.Local).Name.Should().Be("internal");
        ((StringLiteral)spec.Exported).Value.Should().Be("public-name");
    }

    [TestMethod]
    public void Export_named_reexport_from_source()
    {
        var export = ParseSingle<ExportNamedDeclaration>("export { a as b } from 'mod';");
        export.Source.Should().Be("mod");
        export.Specifiers.Should().ContainSingle();
    }

    [TestMethod]
    public void Export_empty_named_list()
    {
        var export = ParseSingle<ExportNamedDeclaration>("export {};");
        export.Specifiers.Should().BeEmpty();
        export.Source.Should().BeNull();
    }

    [TestMethod]
    public void Export_default_expression()
    {
        var export = ParseSingle<ExportDefaultDeclaration>("export default a + b;");
        export.Declaration.Should().BeOfType<BinaryExpression>()
            .Which.Op.Should().Be(JsTokenKind.Plus);
    }

    [TestMethod]
    public void Export_default_anonymous_function()
    {
        var export = ParseSingle<ExportDefaultDeclaration>("export default function () { return 1; }");
        export.Declaration.Should().BeOfType<FunctionExpression>()
            .Which.Name.Should().BeNull();
    }

    [TestMethod]
    public void Export_default_named_class()
    {
        var export = ParseSingle<ExportDefaultDeclaration>("export default class C {}");
        export.Declaration.Should().BeOfType<ClassExpression>()
            .Which.Name!.Name.Should().Be("C");
    }

    [TestMethod]
    public void Export_star_from_source()
    {
        var export = ParseSingle<ExportAllDeclaration>("export * from 'mod';");
        export.Source.Should().Be("mod");
        export.ExportedName.Should().BeNull();
    }

    [TestMethod]
    public void Export_star_as_namespace_from_source()
    {
        var export = ParseSingle<ExportAllDeclaration>("export * as ns from 'mod';");
        export.ExportedName!.Name.Should().Be("ns");
        export.Source.Should().Be("mod");
    }

    [TestMethod]
    public void Export_named_missing_closing_brace_throws()
        => Fails("export { a from 'mod';");

    [TestMethod]
    public void Export_star_requires_from_throws()
        => Fails("export * 'mod';");

    [TestMethod]
    public void Export_default_requires_declaration_or_expression_throws()
        => Fails("export default ;");

    [TestMethod]
    public void Export_declaration_is_top_level_only()
        => Fails("while (x) export { x };");

    [TestMethod]
    public void Program_can_mix_imports_exports_and_statements()
    {
        var program = ParseProgram("import x from 'm'; export { x }; x();");
        program.Body[0].Should().BeOfType<ImportDeclaration>();
        program.Body[1].Should().BeOfType<ExportNamedDeclaration>();
        program.Body[2].Should().BeOfType<ExpressionStatement>();
    }

    private static T ParseSingle<T>(string source) where T : Statement
    {
        var statement = ParseProgram(source).Body.Should().ContainSingle().Subject;
        return statement.Should().BeOfType<T>().Subject;
    }

    private static Program ParseProgram(string source) => new JsParser(source).ParseProgram();

    private static void Fails(string source)
    {
        var act = () => ParseProgram(source);
        act.Should().Throw<JsParseException>();
    }
}
