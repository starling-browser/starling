using System.Text;
using Starling.Dom;

namespace Starling.Html.Tests.TreeBuilder;

/// <summary>
/// Renders a parsed DOM into the indented-tree dump format that
/// <c>html5lib-tests/tree-construction</c> uses for <c>#document</c> sections.
///
/// Format per the corpus README (also linked from
/// <c>testdata/spec/html5lib-tests/tree-construction/README.md</c>):
///
///   | &lt;html&gt;
///   |   &lt;head&gt;
///   |   &lt;body&gt;
///   |     "Hello"
///   |     &lt;img&gt;
///   |       src="x"
///
/// - Each line is prefixed with <c>"| "</c> + two spaces per ancestor.
/// - Element lines are <c>&lt;tag&gt;</c>. SVG/MathML use <c>svg </c>/<c>math </c>
///   namespace prefixes.
/// - Attributes appear as <c>name="value"</c> on the lines immediately
///   following their element, sorted lexicographically by UTF-16.
/// - Text nodes are <c>"…"</c> (newlines NOT escaped).
/// - Comments are <c>&lt;!-- … --&gt;</c>.
/// - Doctypes are <c>&lt;!DOCTYPE name&gt;</c> (and " public-id" / " system-id"
///   when present).
/// - Template content uses a synthetic <c>content</c> child line.
/// </summary>
internal static class Html5LibTreeSerializer
{
    private const string Indent = "  ";
    private const string LinePrefix = "| ";
    private const string SvgNs = "http://www.w3.org/2000/svg";
    private const string MathMlNs = "http://www.w3.org/1998/Math/MathML";
    private const string XmlNs = "http://www.w3.org/XML/1998/namespace";
    private const string XmlNsNs = "http://www.w3.org/2000/xmlns/";
    private const string XLinkNs = "http://www.w3.org/1999/xlink";

    /// <summary>Serialize a whole document. Children of the Document are walked
    /// in order, doctype + comments + root element.</summary>
    public static string Serialize(Document document)
    {
        var sb = new StringBuilder();
        WriteChildren(document, depth: 0, sb);
        // Trim the trailing newline so the result lines up with the corpus's
        // captured #document body (which never has a trailing blank line).
        if (sb.Length > 0 && sb[^1] == '\n')
        {
            sb.Length--;
        }

        return sb.ToString();
    }

    /// <summary>Serialize a fragment's children (used for #document-fragment
    /// cases — the spec dump omits the synthetic root element).</summary>
    public static string SerializeFragment(DocumentFragment fragment)
    {
        var sb = new StringBuilder();
        WriteChildren(fragment, depth: 0, sb);
        if (sb.Length > 0 && sb[^1] == '\n')
        {
            sb.Length--;
        }

        return sb.ToString();
    }

    private static void WriteChildren(Node parent, int depth, StringBuilder sb)
    {
        for (var child = parent.FirstChild; child is not null; child = child.NextSibling)
        {
            WriteNode(child, depth, sb);
        }
    }

    private static void WriteNode(Node node, int depth, StringBuilder sb)
    {
        switch (node)
        {
            case DocumentType dt:
                sb.Append(LinePrefix);
                AppendIndent(sb, depth);
                sb.Append("<!DOCTYPE ");
                sb.Append(dt.Name);
                if (!string.IsNullOrEmpty(dt.PublicId) || !string.IsNullOrEmpty(dt.SystemId))
                {
                    sb.Append(' ');
                    sb.Append('"').Append(dt.PublicId ?? string.Empty).Append('"');
                    sb.Append(' ');
                    sb.Append('"').Append(dt.SystemId ?? string.Empty).Append('"');
                }
                sb.Append('>').Append('\n');
                break;

            case Comment c:
                sb.Append(LinePrefix);
                AppendIndent(sb, depth);
                sb.Append("<!-- ").Append(c.Data).Append(" -->").Append('\n');
                break;

            case Text t:
                sb.Append(LinePrefix);
                AppendIndent(sb, depth);
                sb.Append('"').Append(t.Data).Append('"').Append('\n');
                break;

            case Element el:
                WriteElement(el, depth, sb);
                break;

            default:
                // Document fragments, processing instructions and other node
                // kinds aren't emitted by the HTML parser; fall through silent.
                break;
        }
    }

    private static void WriteElement(Element el, int depth, StringBuilder sb)
    {
        sb.Append(LinePrefix);
        AppendIndent(sb, depth);
        sb.Append('<').Append(ElementDesignator(el)).Append(el.LocalName).Append('>').Append('\n');

        // Attributes — one per line at depth+1, sorted lexicographically by
        // the *attribute name string* (namespace designator + local name).
        if (el.Attributes.Count > 0)
        {
            var attrs = new List<(string key, string value)>(el.Attributes.Count);
            foreach (var a in el.Attributes)
            {
                attrs.Add((AttributeDesignator(a) + a.LocalName, a.Value));
            }

            attrs.Sort(static (a, b) => string.CompareOrdinal(a.key, b.key));
            foreach (var (key, value) in attrs)
            {
                sb.Append(LinePrefix);
                AppendIndent(sb, depth + 1);
                sb.Append(key).Append('=').Append('"').Append(value).Append('"').Append('\n');
            }
        }

        // A <template>'s contents live in a separate fragment. The corpus dumps
        // them under a synthetic "content" line.
        if (el is HtmlTemplateElement template)
        {
            sb.Append(LinePrefix);
            AppendIndent(sb, depth + 1);
            sb.Append("content").Append('\n');
            WriteChildren(template.Content, depth + 2, sb);
            return;
        }

        WriteChildren(el, depth + 1, sb);
    }

    private static string ElementDesignator(Element el) => el.Namespace switch
    {
        SvgNs => "svg ",
        MathMlNs => "math ",
        _ => string.Empty,
    };

    private static string AttributeDesignator(AttrNode attr) => attr.Namespace switch
    {
        XLinkNs => "xlink ",
        XmlNs => "xml ",
        XmlNsNs => "xmlns ",
        _ => string.Empty,
    };

    private static void AppendIndent(StringBuilder sb, int depth)
    {
        for (var i = 0; i < depth; i++)
        {
            sb.Append(Indent);
        }
    }
}
