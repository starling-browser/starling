using Starling.IdlGen.Model;

namespace Starling.IdlGen.Parsing;

public sealed class IdlParseException : Exception
{
    public IdlParseException() { }
    public IdlParseException(string message) : base(message) { }
    public IdlParseException(string message, Exception innerException) : base(message, innerException) { }
}

// Recursive-descent parser for Web IDL. Consumes the token list from IdlLexer
// and produces an IdlDocument. Covers every construct used by the vendored
// specs: definitions, members, the full type grammar, extended attributes,
// arguments, and default values.
public sealed class IdlParser
{
    private readonly List<IdlToken> _toks;
    private int _i;

    private IdlParser(List<IdlToken> toks) => _toks = toks;

    public static IdlDocument Parse(string source)
    {
        var toks = new IdlLexer(source).Tokenize();
        return new IdlParser(toks).ParseDocument();
    }

    // ---- cursor ------------------------------------------------------------

    private IdlToken Cur => _toks[_i];
    private IdlToken Ahead(int n = 1) => _toks[Math.Min(_i + n, _toks.Count - 1)];
    private bool AtEof => Cur.Kind == IdlTokenKind.Eof;

    private bool IsIdent(string text) => Cur.Kind == IdlTokenKind.Identifier && Cur.Text == text;
    private bool IsPunct(string text) => Cur.Kind == IdlTokenKind.Punct && Cur.Text == text;

    private IdlToken Advance() => _toks[_i++];

    private bool TryIdent(string text)
    {
        if (IsIdent(text)) { _i++; return true; }
        return false;
    }

    private bool TryPunct(string text)
    {
        if (IsPunct(text)) { _i++; return true; }
        return false;
    }

    private void ExpectPunct(string text)
    {
        if (!TryPunct(text))
        {
            throw Error($"expected '{text}'");
        }
    }

    private string ExpectIdentifier()
    {
        if (Cur.Kind != IdlTokenKind.Identifier)
        {
            throw Error("expected identifier");
        }

        return Advance().Text;
    }

    private string ExpectString()
    {
        if (Cur.Kind != IdlTokenKind.String)
        {
            throw Error("expected string");
        }

        return Advance().Text;
    }

    private IdlParseException Error(string what) =>
        new($"Web IDL parse error: {what} but found {Cur.Kind} '{Cur.Text}' at line {Cur.Line}, col {Cur.Col}");

    // ---- document & definitions -------------------------------------------

    private IdlDocument ParseDocument()
    {
        var defs = new List<IdlDefinition>();
        while (!AtEof)
        {
            var ext = ParseExtendedAttributeList();
            defs.Add(ParseDefinition(ext));
        }
        return new IdlDocument { Definitions = defs };
    }

    private IdlDefinition ParseDefinition(IReadOnlyList<IdlExtendedAttribute> ext)
    {
        bool partial = TryIdent("partial");

        if (IsIdent("interface"))
        {
            return ParseInterface(ext, partial);
        }

        if (IsIdent("callback"))
        {
            return ParseCallback(ext);
        }

        if (IsIdent("namespace"))
        {
            return ParseNamespace(ext, partial);
        }

        if (IsIdent("dictionary"))
        {
            return ParseDictionary(ext, partial);
        }

        if (IsIdent("enum"))
        {
            return ParseEnum(ext);
        }

        if (IsIdent("typedef"))
        {
            return ParseTypedef(ext);
        }

        // The only remaining form is "Target includes Mixin;".
        return ParseIncludes(ext);
    }

    private IdlInterface ParseInterface(IReadOnlyList<IdlExtendedAttribute> ext, bool partial)
    {
        Advance(); // "interface"
        bool mixin = TryIdent("mixin");
        string name = ExpectIdentifier();
        string? inherits = null;
        if (!mixin && TryPunct(":"))
        {
            inherits = ExpectIdentifier();
        }

        var members = ParseMemberBlockOrForward();
        return new IdlInterface
        {
            Name = name,
            ExtendedAttributes = ext,
            Partial = partial,
            Mixin = mixin,
            Inherits = inherits,
            Members = members,
        };
    }

    private IdlDefinition ParseCallback(IReadOnlyList<IdlExtendedAttribute> ext)
    {
        Advance(); // "callback"
        if (TryIdent("interface"))
        {
            string iname = ExpectIdentifier();
            var members = ParseMemberBlockOrForward();
            return new IdlInterface { Name = iname, ExtendedAttributes = ext, Callback = true, Members = members };
        }

        string name = ExpectIdentifier();
        ExpectPunct("=");
        var ret = ParseType();
        var args = ParseArgumentList();
        ExpectPunct(";");
        return new IdlCallback { Name = name, ExtendedAttributes = ext, ReturnType = ret, Arguments = args };
    }

    private IdlNamespace ParseNamespace(IReadOnlyList<IdlExtendedAttribute> ext, bool partial)
    {
        Advance(); // "namespace"
        string name = ExpectIdentifier();
        var members = ParseMemberBlockOrForward();
        return new IdlNamespace { Name = name, ExtendedAttributes = ext, Partial = partial, Members = members };
    }

    private IdlDictionary ParseDictionary(IReadOnlyList<IdlExtendedAttribute> ext, bool partial)
    {
        Advance(); // "dictionary"
        string name = ExpectIdentifier();
        string? inherits = TryPunct(":") ? ExpectIdentifier() : null;
        ExpectPunct("{");
        var members = new List<IdlDictionaryMember>();
        while (!IsPunct("}") && !AtEof)
        {
            var memExt = ParseExtendedAttributeList();
            bool required = TryIdent("required");
            var type = ParseType();
            string mname = ExpectIdentifier();
            IdlDefaultValue? def = TryPunct("=") ? ParseDefaultValue() : null;
            ExpectPunct(";");
            members.Add(new IdlDictionaryMember
            {
                Name = mname,
                Type = type,
                Required = required,
                Default = def,
                ExtendedAttributes = memExt,
            });
        }
        ExpectPunct("}");
        ExpectPunct(";");
        return new IdlDictionary { Name = name, ExtendedAttributes = ext, Partial = partial, Inherits = inherits, Members = members };
    }

    private IdlEnum ParseEnum(IReadOnlyList<IdlExtendedAttribute> ext)
    {
        Advance(); // "enum"
        string name = ExpectIdentifier();
        ExpectPunct("{");
        var values = new List<string>();
        if (!IsPunct("}"))
        {
            values.Add(ExpectString());
            while (TryPunct(","))
            {
                if (IsPunct("}"))
                {
                    break;   // trailing comma
                }

                values.Add(ExpectString());
            }
        }
        ExpectPunct("}");
        ExpectPunct(";");
        return new IdlEnum { Name = name, ExtendedAttributes = ext, Values = values };
    }

    private IdlTypedef ParseTypedef(IReadOnlyList<IdlExtendedAttribute> ext)
    {
        Advance(); // "typedef"
        var type = ParseType();
        string name = ExpectIdentifier();
        ExpectPunct(";");
        return new IdlTypedef { Name = name, ExtendedAttributes = ext, Type = type };
    }

    private IdlIncludes ParseIncludes(IReadOnlyList<IdlExtendedAttribute> ext)
    {
        string target = ExpectIdentifier();
        if (!TryIdent("includes"))
        {
            throw Error("expected 'includes'");
        }

        string mixin = ExpectIdentifier();
        ExpectPunct(";");
        return new IdlIncludes { Name = target, Mixin = mixin, ExtendedAttributes = ext };
    }

    // Parses "{ members };" or a bare ";" forward declaration.
    private List<IdlMember> ParseMemberBlockOrForward()
    {
        if (TryPunct(";"))
        {
            return [];   // forward declaration
        }

        ExpectPunct("{");
        var members = new List<IdlMember>();
        while (!IsPunct("}") && !AtEof)
        {
            members.Add(ParseMember());
        }

        ExpectPunct("}");
        ExpectPunct(";");
        return members;
    }

    // ---- members -----------------------------------------------------------

    private IdlMember ParseMember()
    {
        var ext = ParseExtendedAttributeList();

        bool isStatic = false, isReadonly = false, isStringifier = false, isInherit = false;
        var special = IdlSpecialKind.None;
        while (true)
        {
            if (TryIdent("static")) { isStatic = true; continue; }
            if (TryIdent("stringifier")) { isStringifier = true; continue; }
            if (TryIdent("readonly")) { isReadonly = true; continue; }
            if (TryIdent("inherit")) { isInherit = true; continue; }
            if (TryIdent("getter")) { special = IdlSpecialKind.Getter; continue; }
            if (TryIdent("setter")) { special = IdlSpecialKind.Setter; continue; }
            if (TryIdent("deleter")) { special = IdlSpecialKind.Deleter; continue; }
            break;
        }

        IdlMember member;

        if (!isStringifier && special == IdlSpecialKind.None && IsIdent("const"))
        {
            member = ParseConstant();
        }
        else if (IsIdent("attribute"))
        {
            Advance();
            var type = ParseType();
            string name = ExpectIdentifier();
            member = new IdlAttribute
            {
                Name = name,
                Type = type,
                Readonly = isReadonly,
                Static = isStatic,
                Stringifier = isStringifier,
                Inherit = isInherit,
            };
            ExpectPunct(";");
        }
        else if (IsIdent("constructor"))
        {
            Advance();
            var args = ParseArgumentList();
            ExpectPunct(";");
            member = new IdlConstructor { Arguments = args };
        }
        else if (IsIdent("iterable"))
        {
            member = ParseIterable(async: false);
        }
        else if (IsIdent("async") && Ahead().Kind == IdlTokenKind.Identifier && Ahead().Text == "iterable")
        {
            Advance(); // "async"
            member = ParseIterable(async: true);
        }
        else if (IsIdent("maplike"))
        {
            Advance();
            ExpectPunct("<");
            var k = ParseType();
            ExpectPunct(",");
            var v = ParseType();
            ExpectPunct(">");
            ExpectPunct(";");
            member = new IdlMaplike { KeyType = k, ValueType = v, Readonly = isReadonly };
        }
        else if (IsIdent("setlike"))
        {
            Advance();
            ExpectPunct("<");
            var v = ParseType();
            ExpectPunct(">");
            ExpectPunct(";");
            member = new IdlSetlike { ValueType = v, Readonly = isReadonly };
        }
        else if (isStringifier && IsPunct(";"))
        {
            Advance();
            member = new IdlStringifier();
        }
        else
        {
            // Operation. Return type, optional identifier (anonymous for special ops), args.
            var ret = ParseType();
            string? name = Cur.Kind == IdlTokenKind.Identifier ? Advance().Text : null;
            var args = ParseArgumentList();
            ExpectPunct(";");
            member = new IdlOperation
            {
                Name = name,
                ReturnType = ret,
                Arguments = args,
                Static = isStatic,
                Special = special,
                Stringifier = isStringifier,
            };
        }

        return ext.Count > 0 ? member with { ExtendedAttributes = ext } : member;
    }

    private IdlConstant ParseConstant()
    {
        Advance(); // "const"
        var type = ParseType();
        string name = ExpectIdentifier();
        ExpectPunct("=");
        string value = ParseConstValue();
        ExpectPunct(";");
        return new IdlConstant { Name = name, Type = type, Value = value };
    }

    private string ParseConstValue()
    {
        if (TryIdent("true"))
        {
            return "true";
        }

        if (TryIdent("false"))
        {
            return "false";
        }

        if (TryIdent("Infinity"))
        {
            return "Infinity";
        }

        if (TryIdent("NaN"))
        {
            return "NaN";
        }

        if (IsPunct("-") && Ahead().Text == "Infinity") { Advance(); Advance(); return "-Infinity"; }
        if (Cur.Kind is IdlTokenKind.Integer or IdlTokenKind.Decimal)
        {
            return Advance().Text;
        }

        throw Error("expected constant value");
    }

    private IdlIterable ParseIterable(bool async)
    {
        Advance(); // "iterable"
        ExpectPunct("<");
        var t1 = ParseType();
        IdlType? key = null;
        IdlType value;
        if (TryPunct(","))
        {
            key = t1;
            value = ParseType();
        }
        else
        {
            value = t1;
        }
        ExpectPunct(">");
        var args = new List<IdlArgument>();
        if (async && IsPunct("("))
        {
            args = ParseArgumentList();
        }

        ExpectPunct(";");
        return new IdlIterable { KeyType = key, ValueType = value, Async = async, Arguments = args };
    }

    // ---- types -------------------------------------------------------------

    private IdlType ParseType()
    {
        var ext = ParseExtendedAttributeList();
        var t = ParseTypeInner();
        return ext.Count > 0 ? t with { ExtendedAttributes = ext } : t;
    }

    private IdlType ParseTypeInner()
    {
        if (IsPunct("("))
        {
            Advance();
            var members = new List<IdlType> { ParseTypeInner() };
            while (TryIdent("or"))
            {
                members.Add(ParseTypeInner());
            }

            ExpectPunct(")");
            bool n = TryPunct("?");
            return new IdlType { Union = members, Nullable = n };
        }
        return ParseNonUnionType();
    }

    private IdlType ParseNonUnionType()
    {
        string name;
        if (TryIdent("unsigned"))
        {
            if (TryIdent("long"))
            {
                name = TryIdent("long") ? "unsigned long long" : "unsigned long";
            }
            else { if (!TryIdent("short")) { throw Error("expected 'short' or 'long'"); } name = "unsigned short"; }
        }
        else if (TryIdent("long"))
        {
            name = TryIdent("long") ? "long long" : "long";
        }
        else if (TryIdent("unrestricted"))
        {
            name = "unrestricted " + ExpectIdentifier();
        }
        else
        {
            name = ExpectIdentifier();
        }

        var args = new List<IdlType>();
        if (name is "sequence" or "Promise" or "FrozenArray" or "ObservableArray")
        {
            ExpectPunct("<");
            args.Add(ParseType());
            ExpectPunct(">");
        }
        else if (name == "record")
        {
            ExpectPunct("<");
            args.Add(ParseType());
            ExpectPunct(",");
            args.Add(ParseType());
            ExpectPunct(">");
        }

        bool nullable = TryPunct("?");
        return new IdlType { Name = name, TypeArgs = args, Nullable = nullable };
    }

    // ---- arguments & defaults ---------------------------------------------

    private List<IdlArgument> ParseArgumentList()
    {
        ExpectPunct("(");
        var args = new List<IdlArgument>();
        if (!IsPunct(")"))
        {
            args.Add(ParseArgument());
            while (TryPunct(","))
            {
                args.Add(ParseArgument());
            }
        }
        ExpectPunct(")");
        return args;
    }

    private IdlArgument ParseArgument()
    {
        var ext = ParseExtendedAttributeList();
        bool optional = TryIdent("optional");
        var type = ParseType();
        bool variadic = TryPunct("...");
        string name = ExpectIdentifier();
        IdlDefaultValue? def = TryPunct("=") ? ParseDefaultValue() : null;
        return new IdlArgument
        {
            Name = name,
            Type = type,
            Optional = optional,
            Variadic = variadic,
            Default = def,
            ExtendedAttributes = ext,
        };
    }

    private IdlDefaultValue ParseDefaultValue()
    {
        if (TryIdent("null"))
        {
            return new IdlDefaultValue { Kind = IdlDefaultKind.Null };
        }

        if (TryIdent("true"))
        {
            return new IdlDefaultValue { Kind = IdlDefaultKind.Boolean, Value = "true" };
        }

        if (TryIdent("false"))
        {
            return new IdlDefaultValue { Kind = IdlDefaultKind.Boolean, Value = "false" };
        }

        if (TryIdent("Infinity"))
        {
            return new IdlDefaultValue { Kind = IdlDefaultKind.Infinity };
        }

        if (TryIdent("NaN"))
        {
            return new IdlDefaultValue { Kind = IdlDefaultKind.NaN };
        }

        if (IsPunct("-") && Ahead().Text == "Infinity") { Advance(); Advance(); return new IdlDefaultValue { Kind = IdlDefaultKind.NegInfinity }; }
        if (TryPunct("[")) { ExpectPunct("]"); return new IdlDefaultValue { Kind = IdlDefaultKind.EmptySequence }; }
        if (TryPunct("{")) { ExpectPunct("}"); return new IdlDefaultValue { Kind = IdlDefaultKind.EmptyDictionary }; }
        if (Cur.Kind == IdlTokenKind.String)
        {
            return new IdlDefaultValue { Kind = IdlDefaultKind.String, Value = Advance().Text };
        }

        if (Cur.Kind is IdlTokenKind.Integer or IdlTokenKind.Decimal)
        {
            return new IdlDefaultValue { Kind = IdlDefaultKind.Number, Value = Advance().Text };
        }

        throw Error("expected default value");
    }

    // ---- extended attributes ----------------------------------------------

    private List<IdlExtendedAttribute> ParseExtendedAttributeList()
    {
        if (!TryPunct("["))
        {
            return [];
        }

        var list = new List<IdlExtendedAttribute> { ParseExtendedAttribute() };
        while (TryPunct(","))
        {
            list.Add(ParseExtendedAttribute());
        }

        ExpectPunct("]");
        return list;
    }

    private IdlExtendedAttribute ParseExtendedAttribute()
    {
        string name = ExpectIdentifier();

        if (TryPunct("="))
        {
            if (IsPunct("("))
            {
                Advance();
                var ids = new List<string> { ExpectIdentifier() };
                while (TryPunct(","))
                {
                    ids.Add(ExpectIdentifier());
                }

                ExpectPunct(")");
                return new IdlExtendedAttribute { Name = name, Kind = IdlExtAttrKind.IdentList, Identifiers = ids };
            }
            if (TryPunct("*"))
            {
                return new IdlExtendedAttribute { Name = name, Kind = IdlExtAttrKind.Wildcard };
            }
            string id = ExpectIdentifier();
            if (IsPunct("("))
            {
                var args = ParseArgumentList();
                return new IdlExtendedAttribute { Name = name, Kind = IdlExtAttrKind.NamedArgList, Identifier = id, Arguments = args };
            }
            return new IdlExtendedAttribute { Name = name, Kind = IdlExtAttrKind.Ident, Identifier = id };
        }

        if (IsPunct("("))
        {
            var args = ParseArgumentList();
            return new IdlExtendedAttribute { Name = name, Kind = IdlExtAttrKind.ArgList, Arguments = args };
        }

        return new IdlExtendedAttribute { Name = name, Kind = IdlExtAttrKind.NoArgs };
    }
}
