using Starling.Js.Ast;
using Starling.Js.Lex;

namespace Starling.Js.Parse;

/// <summary>
/// ES2024 §16.2.1.6.2 Module: Static Semantics: Early Errors. After a Module
/// goal is parsed (<see cref="ParseModule"/>) these whole-module checks run over
/// the ModuleItemList:
/// <list type="bullet">
///   <item>It is a Syntax Error if the ExportedNames of ModuleItemList contains
///   any duplicate entries.</item>
///   <item>It is a Syntax Error if any element of the ExportedBindings of
///   ModuleItemList does not also occur in either the VarDeclaredNames or the
///   LexicallyDeclaredNames of ModuleItemList (i.e. <c>export { x }</c> with no
///   declared <c>x</c>).</item>
///   <item>It is a Syntax Error if the LexicallyDeclaredNames of ModuleItemList
///   contains any duplicate entries — where, for a Module, an import binding is
///   a lexical name, and so are <c>export default</c>'s synthetic binding and the
///   bound names of an exported declaration. Also a lexical name may not collide
///   with a VarDeclaredName.</item>
/// </list>
/// The per-statement strict-mode binding checks (a strict module forbids
/// <c>eval</c>/<c>arguments</c>/reserved words as binding names, and an escaped
/// reserved word is never a keyword) run while parsing each
/// import/export specifier via <see cref="CheckModuleBindingName"/>.
/// </summary>
public ref partial struct JsParser
{
    /// <summary>Run the whole-module §16.2.1.6.2 early-error checks over the
    /// already-parsed ModuleItemList.</summary>
    private void CheckModuleEarlyErrors(IReadOnlyList<Statement> body)
    {
        CheckModuleLexicalNames(body);
        CheckModuleExportedNames(body);
        CheckModuleExportedBindings(body);
    }

    // -----------------------------------------------------------------------
    // LexicallyDeclaredNames: duplicates + collision with VarDeclaredNames.
    // For a Module the lexical names include import bindings, let/const/class
    // (bare and exported), and the `export default` binding (*default*).
    // -----------------------------------------------------------------------

    private void CheckModuleLexicalNames(IReadOnlyList<Statement> body)
    {
        var lexical = new Dictionary<string, JsPosition>(StringComparer.Ordinal);
        var varNames = new HashSet<string>(StringComparer.Ordinal);
        var sawDefault = false;

        foreach (var stmt in body)
        {
            switch (stmt)
            {
                case ImportDeclaration imp:
                    foreach (var spec in imp.Specifiers)
                    {
                        var local = spec switch
                        {
                            ImportDefaultSpecifier d => d.Local,
                            ImportNamespaceSpecifier ns => ns.Local,
                            ImportNamedSpecifier named => named.Local,
                            _ => null,
                        };
                        if (local is not null)
                        {
                            AddLexical(lexical, local.Name, local.Start);
                        }
                    }
                    break;

                case VariableDeclaration vd when vd.Kind is "let" or "const":
                    foreach (var n in BoundNamesOf(vd))
                    {
                        AddLexical(lexical, n.Name, n.Pos);
                    }

                    break;
                case ClassDeclaration cd:
                    AddLexical(lexical, cd.Name.Name, cd.Name.Start);
                    break;
                case FunctionDeclaration fd:
                    // §16.2.1.6 — at the Module top level a FunctionDeclaration is
                    // a LexicallyDeclaredName (unlike a Script body, where it is
                    // var-scoped), so two same-named top-level functions — or one
                    // colliding with a let/const/class or a var — is an early
                    // error.
                    AddLexical(lexical, fd.Name.Name, fd.Name.Start);
                    break;

                case ExportLocalDeclaration { Declaration: var decl }:
                    switch (decl)
                    {
                        case VariableDeclaration evd when evd.Kind is "let" or "const":
                            foreach (var n in BoundNamesOf(evd))
                            {
                                AddLexical(lexical, n.Name, n.Pos);
                            }

                            break;
                        case ClassDeclaration ecd:
                            AddLexical(lexical, ecd.Name.Name, ecd.Name.Start);
                            break;
                        case FunctionDeclaration efd:
                            AddLexical(lexical, efd.Name.Name, efd.Name.Start);
                            break;
                            // exported `var` falls into VarDeclaredNames below.
                    }
                    break;

                case ExportDefaultDeclaration edd:
                    // §16.2.3.7 — every `export default` introduces a *default*
                    // binding; two of them is a duplicate lexical name.
                    if (sawDefault)
                    {
                        throw new JsParseException(
                            "a module may only have one default export", edd.Start);
                    }

                    sawDefault = true;
                    // A *named* default declaration (`export default function F` /
                    // `export default class F`) ALSO declares the binding `F` in
                    // the module's lexical scope, so it collides with another
                    // top-level declaration of `F`.
                    var defaultName = edd.Declaration switch
                    {
                        FunctionExpression { Name: { } fn } => fn,
                        ClassExpression { Name: { } cn } => cn,
                        _ => null,
                    };
                    if (defaultName is not null)
                    {
                        AddLexical(lexical, defaultName.Name, defaultName.Start);
                    }

                    break;
            }
        }

        // VarDeclaredNames over the whole module (exported `var` included).
        foreach (var stmt in body)
        {
            switch (stmt)
            {
                case ExportLocalDeclaration { Declaration: Statement decl }:
                    CollectVarNames(decl, varNames);
                    break;
                default:
                    CollectVarNames(stmt, varNames);
                    break;
            }
        }

        foreach (var kv in lexical)
        {
            if (varNames.Contains(kv.Key))
            {
                throw new JsParseException(
                    $"'{kv.Key}' is already declared as a var binding", kv.Value);
            }
        }
    }

    // -----------------------------------------------------------------------
    // ExportedNames: no duplicates. ExportedNames are the *exported* names
    // (the `as` target), expanding `export *` (anonymous star contributes no
    // name; `export * as ns` contributes `ns`).
    // -----------------------------------------------------------------------

    private static void CheckModuleExportedNames(IReadOnlyList<Statement> body)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string name, JsPosition pos)
        {
            if (!seen.Add(name))
            {
                throw new JsParseException(
                    $"duplicate export name '{name}'", pos);
            }
        }

        foreach (var stmt in body)
        {
            switch (stmt)
            {
                case ExportDefaultDeclaration edd:
                    Add("default", edd.Start);
                    break;

                case ExportLocalDeclaration { Declaration: var decl } eld:
                    foreach (var n in ExportedDeclarationNames(decl))
                    {
                        Add(n, eld.Start);
                    }

                    break;

                case ExportNamedDeclaration named:
                    foreach (var spec in named.Specifiers)
                    {
                        Add(ModuleNameOf(spec.Exported), spec.Exported.Start);
                    }

                    break;

                case ExportAllDeclaration { ExportedName: { } ns } all:
                    Add(ns.Name, ns.Start); // export * as ns from "..."
                    break;
            }
        }
    }

    /// <summary>The exported names contributed by an <c>export</c> on a
    /// declaration (<c>export const a, b</c> / <c>export function f</c> /
    /// <c>export class C</c>).</summary>
    private static List<string> ExportedDeclarationNames(Statement decl)
    {
        var names = new List<string>();
        switch (decl)
        {
            case VariableDeclaration vd:
                foreach (var n in BoundNamesOf(vd))
                {
                    names.Add(n.Name);
                }

                break;
            case FunctionDeclaration fd: names.Add(fd.Name.Name); break;
            case ClassDeclaration cd: names.Add(cd.Name.Name); break;
        }
        return names;
    }

    // -----------------------------------------------------------------------
    // ExportedBindings: a `export { x }` / `export { x as y }` whose `from`
    // clause is absent binds a *local* name x that must be declared somewhere
    // in the module (var/lex/import). `export { x } from "..."` re-exports and
    // does NOT require a local binding.
    // -----------------------------------------------------------------------

    private static void CheckModuleExportedBindings(IReadOnlyList<Statement> body)
    {
        // Collect every name a module declares at top level: var, let/const,
        // function, class (bare + exported), and import bindings.
        var declared = new HashSet<string>(StringComparer.Ordinal);

        void AddVarLex(Statement decl)
        {
            switch (decl)
            {
                case VariableDeclaration vd:
                    foreach (var n in BoundNamesOf(vd))
                    {
                        declared.Add(n.Name);
                    }

                    break;
                case FunctionDeclaration fd: declared.Add(fd.Name.Name); break;
                case ClassDeclaration cd: declared.Add(cd.Name.Name); break;
            }
        }

        foreach (var stmt in body)
        {
            switch (stmt)
            {
                case ImportDeclaration imp:
                    foreach (var spec in imp.Specifiers)
                    {
                        var local = spec switch
                        {
                            ImportDefaultSpecifier d => d.Local.Name,
                            ImportNamespaceSpecifier ns => ns.Local.Name,
                            ImportNamedSpecifier named => named.Local.Name,
                            _ => null,
                        };
                        if (local is not null)
                        {
                            declared.Add(local);
                        }
                    }
                    break;
                case VariableDeclaration:
                case FunctionDeclaration:
                case ClassDeclaration:
                    AddVarLex(stmt);
                    break;
                case ExportLocalDeclaration { Declaration: Statement decl }:
                    AddVarLex(decl);
                    break;
                    // nested `var` (inside blocks/loops) also contributes — pick those
                    // up via CollectVarNames below.
            }
        }
        foreach (var stmt in body)
        {
            if (stmt is ExportLocalDeclaration { Declaration: Statement d })
            {
                CollectVarNames(d, declared);
            }
            else
            {
                CollectVarNames(stmt, declared);
            }
        }

        // Now validate each local (non-`from`) export specifier.
        foreach (var stmt in body)
        {
            if (stmt is not ExportNamedDeclaration { Source: null } named)
            {
                continue;
            }

            foreach (var spec in named.Specifiers)
            {
                // §16.2.3.1 — without a `from` clause the local is an
                // IdentifierReference: a string-literal local (`export { "foo" }`)
                // is only valid when re-exporting via `from`, so here it is an
                // early SyntaxError.
                if (spec.Local is StringLiteral strLocal)
                {
                    throw new JsParseException(
                        $"a string module-export name '{strLocal.Value}' requires a 'from' clause",
                        strLocal.Start);
                }
                // An identifier local must resolve to a declared top-level binding.
                if (spec.Local is Identifier id && !declared.Contains(id.Name))
                {
                    throw new JsParseException(
                        $"export '{id.Name}' is not defined in module", id.Start);
                }
            }
        }
    }

    private static string ModuleNameOf(Expression e) => e switch
    {
        Identifier id => id.Name,
        StringLiteral s => s.Value,
        _ => throw new InvalidOperationException("unsupported module name node"),
    };

    /// <summary>§12.7.1 / §16.2.2 — validate a binding name introduced by an
    /// import specifier (a default/namespace/named import's local name). Module
    /// code is strict, so <c>eval</c>/<c>arguments</c> and reserved words are
    /// forbidden as binding names, and an escaped reserved word is never a
    /// keyword. Delegates to the shared strict binding-identifier check.</summary>
    private void CheckModuleBindingName(JsToken token)
    {
        // §16.2.1.6.2 — `await` is reserved in module code, so it is never a
        // valid imported-binding name (the general strict check below does not
        // cover it because `await` is not a strict FutureReservedWord).
        if (token.TextEquals("await"))
        {
            throw new JsParseException(
                "'await' may not be used as a binding identifier in a module", token.Start);
        }

        CheckBindingIdentifier(token.Lexeme, token.Start);
    }
}
