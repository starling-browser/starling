using System.Globalization;
using System.Text;

namespace Starling.Css.Selectors;

/// <summary>
/// Serializes a parsed selector back to its canonical CSS text form
/// (CSSOM §6.7.2 "serialize a group of selectors" + CSS Syntax §9.2 "serialize
/// an &lt;an+b&gt; value"). Used by <c>CSSStyleRule.selectorText</c>.
/// </summary>
public static class SelectorSerializer
{
    private const char Replacement = '�';

    public static string Serialize(SelectorList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        return string.Join(", ", list.Selectors.Select(Serialize));
    }

    public static string Serialize(ComplexSelector complex)
    {
        ArgumentNullException.ThrowIfNull(complex);
        var sb = new StringBuilder();
        for (var i = 0; i < complex.Parts.Count; i++)
        {
            var part = complex.Parts[i];
            if (i > 0)
            {
                sb.Append(Combinator(part.CombinatorFromPrevious));
            }
            else if (part.CombinatorFromPrevious is not (SelectorCombinator.None or SelectorCombinator.Descendant))
            {
                // A leading explicit combinator (e.g. inside :has(> a)).
                sb.Append(Combinator(part.CombinatorFromPrevious).TrimStart());
            }

            sb.Append(Serialize(part.Compound));
        }
        return sb.ToString();
    }

    public static string Serialize(CompoundSelector compound)
    {
        ArgumentNullException.ThrowIfNull(compound);
        var sb = new StringBuilder();
        // CSSOM §6.7.2 "serialize a compound selector": a universal selector with
        // no namespace prefix is omitted when the compound has other simple
        // selectors (e.g. `*.c` serializes as `.c`, `*:hover` as `:hover`). A
        // namespaced universal (`ns|*`) is always kept, and a lone `*` stays `*`.
        var omitUniversal = compound.SimpleSelectors.Count > 1
            && compound.SimpleSelectors.Any(s => s is UniversalSelector { Namespace: null });
        foreach (var simple in compound.SimpleSelectors)
        {
            if (omitUniversal && simple is UniversalSelector { Namespace: null })
            {
                continue;
            }

            sb.Append(Serialize(simple));
        }
        return sb.ToString();
    }

    public static string Serialize(SimpleSelector simple)
    {
        ArgumentNullException.ThrowIfNull(simple);
        return simple switch
        {
            TypeSelector t => SerializeNamespace(t.Namespace) + SerializeIdent(t.LocalName),
            UniversalSelector u => SerializeNamespace(u.Namespace) + "*",
            IdSelector id => "#" + SerializeIdent(id.Id),
            ClassSelector c => "." + SerializeIdent(c.ClassName),
            AttributeSelector a => SerializeAttribute(a),
            PseudoClassSelector pc => SerializePseudoClass(pc),
            PseudoElementSelector pe => "::" + SerializeIdent(pe.Name),
            _ => string.Empty,
        };
    }

    private static string SerializeNamespace(string? ns) => ns switch
    {
        null => string.Empty,
        "*" => "*|",
        "" => "|",
        _ => SerializeIdent(ns) + "|",
    };

    private static string SerializeAttribute(AttributeSelector a)
    {
        var op = a.Operator switch
        {
            AttributeOperator.Exists => null,
            AttributeOperator.Equals => "=",
            AttributeOperator.Includes => "~=",
            AttributeOperator.DashMatch => "|=",
            AttributeOperator.Prefix => "^=",
            AttributeOperator.Suffix => "$=",
            AttributeOperator.Substring => "*=",
            _ => "=",
        };
        var sb = new StringBuilder();
        sb.Append('[').Append(SerializeIdent(a.Name));
        if (op is not null)
        {
            sb.Append(op).Append(SerializeString(a.Value ?? string.Empty));
            if (a.CaseInsensitive)
            {
                sb.Append(" i");
            }
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static string SerializePseudoClass(PseudoClassSelector pc)
    {
        var name = SerializeIdent(pc.Name);
        return pc.Argument switch
        {
            null => ":" + name,
            NthArgument nth => ":" + name + "(" + SerializeAnb(nth.Pattern) +
                (nth.OfSelector is { Selectors.Count: > 0 } of ? " of " + Serialize(of) : "") + ")",
            NthPattern pat => ":" + name + "(" + SerializeAnb(pat) + ")",
            SelectorList sl => ":" + name + "(" + Serialize(sl) + ")",
            HeadingArgument h => ":" + name + "(" +
                string.Join(", ", h.Levels.Select(l => l.ToString(CultureInfo.InvariantCulture))) + ")",
            string s => ":" + name + "(" + s + ")",
            _ => ":" + name,
        };
    }

    /// <summary>Serialize an &lt;an+b&gt; value (CSS Syntax 3 §9.2).</summary>
    public static string SerializeAnb(NthPattern p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var a = p.A;
        var b = p.B;

        if (a == 0)
        {
            return b.ToString(CultureInfo.InvariantCulture);
        }

        var sb = new StringBuilder();
        if (a == 1)
        {
            sb.Append('n');
        }
        else if (a == -1)
        {
            sb.Append("-n");
        }
        else
        {
            sb.Append(a.ToString(CultureInfo.InvariantCulture)).Append('n');
        }

        if (b > 0)
        {
            sb.Append('+').Append(b.ToString(CultureInfo.InvariantCulture));
        }
        else if (b < 0)
        {
            sb.Append('-').Append((-b).ToString(CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    private static string Combinator(SelectorCombinator c) => c switch
    {
        SelectorCombinator.Descendant => " ",
        SelectorCombinator.Child => " > ",
        SelectorCombinator.NextSibling => " + ",
        SelectorCombinator.SubsequentSibling => " ~ ",
        SelectorCombinator.Column => " || ",
        _ => " ",
    };

    /// <summary>Serialize an identifier (CSSOM §6.7.1 "serialize an identifier").</summary>
    public static string SerializeIdent(string ident)
    {
        if (string.IsNullOrEmpty(ident))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(ident.Length);
        for (var i = 0; i < ident.Length; i++)
        {
            var c = ident[i];
            if (c == '\0')
            {
                sb.Append(Replacement);
            }
            else if (IsControlOrDelete(c))
            {
                AppendCodePointEscape(sb, c);
            }
            else if (i == 0 && char.IsAsciiDigit(c))
            {
                AppendCodePointEscape(sb, c);
            }
            else if (i == 1 && char.IsAsciiDigit(c) && ident[0] == '-')
            {
                AppendCodePointEscape(sb, c);
            }
            else if (i == 0 && c == '-' && ident.Length == 1)
            {
                sb.Append('\\').Append(c);
            }
            else if (c >= 0x80 || c == '-' || c == '_' || char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('\\').Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>Serialize a string (CSSOM §6.7.1 "serialize a string").</summary>
    public static string SerializeString(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            if (c == '\0')
            {
                sb.Append(Replacement);
            }
            else if (IsControlOrDelete(c))
            {
                AppendCodePointEscape(sb, c);
            }
            else if (c == '"' || c == '\\')
            {
                sb.Append('\\').Append(c);
            }
            else
            {
                sb.Append(c);
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    // U+0001..U+001F or U+007F.
    private static bool IsControlOrDelete(char c) => (c >= 0x01 && c <= 0x1F) || c == 0x7F;

    private static void AppendCodePointEscape(StringBuilder sb, char c)
    {
        sb.Append('\\')
          .Append(((int)c).ToString("x", CultureInfo.InvariantCulture))
          .Append(' ');
    }
}
