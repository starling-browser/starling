using System.Text;
using Starling.Dom;

namespace Starling.Html;

/// <summary>
/// Minimal, spec-aligned HTML fragment serializer
/// ([HTML §13.3](https://html.spec.whatwg.org/multipage/parsing.html#serialising-html-fragments)).
/// Walks a DOM subtree and produces HTML markup.
/// </summary>
/// <remarks>
/// <b>Covered:</b> void elements emit no closing tag and no children; text and
/// attribute values are escaped per the spec; raw-text elements
/// (<c>&lt;script&gt;</c>, <c>&lt;style&gt;</c>, …) emit their text verbatim;
/// comments, doctypes, and CDATA are serialized.
/// <para><b>Deferred:</b> the spec's <c>&lt;pre&gt;</c>/<c>&lt;textarea&gt;</c>
/// leading-newline rule and the <c>&lt;template&gt;</c> content document.</para>
/// </remarks>
public static class HtmlSerializer
{
    private const char Nbsp = '\u00A0'; // U+00A0 NO-BREAK SPACE

    /// <summary>Serialize the children of <paramref name="node"/> (the
    /// <c>innerHTML</c> shape — the node's own tag is not emitted).</summary>
    public static string SerializeChildren(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var sb = new StringBuilder();
        for (var child = node.FirstChild; child is not null; child = child.NextSibling)
        {
            SerializeNode(child, node, sb);
        }

        return sb.ToString();
    }

    /// <summary>Serialize <paramref name="node"/> itself and its subtree (the
    /// <c>outerHTML</c> shape).</summary>
    public static string SerializeNode(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var sb = new StringBuilder();
        SerializeNode(node, node.ParentNode, sb);
        return sb.ToString();
    }

    private static void SerializeNode(Node node, Node? parent, StringBuilder sb)
    {
        switch (node)
        {
            case Element element:
                SerializeElement(element, sb);
                break;
            case Text text:
                AppendText(text.Data, parent, sb);
                break;
            case Comment comment:
                sb.Append("<!--").Append(comment.Data).Append("-->");
                break;
            case CData cdata:
                sb.Append("<![CDATA[").Append(cdata.Data).Append("]]>");
                break;
            case ProcessingInstruction pi:
                sb.Append("<?").Append(pi.Target).Append(' ').Append(pi.Data).Append('>');
                break;
            case DocumentType doctype:
                sb.Append("<!DOCTYPE ").Append(doctype.Name).Append('>');
                break;
            default:
                // Document / DocumentFragment: serialize the children only.
                for (var child = node.FirstChild; child is not null; child = child.NextSibling)
                {
                    SerializeNode(child, node, sb);
                }

                break;
        }
    }

    private static void SerializeElement(Element element, StringBuilder sb)
    {
        var tag = element.TagName;
        sb.Append('<').Append(tag);
        foreach (var attr in element.Attributes)
        {
            sb.Append(' ').Append(attr.Name).Append("=\"");
            AppendAttributeValue(attr.Value, sb);
            sb.Append('"');
        }
        sb.Append('>');

        if (IsVoidElement(tag))
        {
            return; // §13.3: void elements have no end tag and no children.
        }

        if (IsRawTextElement(tag))
        {
            // §13.3: raw-text / RCDATA element contents are emitted literally.
            for (var child = element.FirstChild; child is not null; child = child.NextSibling)
            {
                if (child is Text t)
                {
                    sb.Append(t.Data);
                }
            }
        }
        else
        {
            for (var child = element.FirstChild; child is not null; child = child.NextSibling)
            {
                SerializeNode(child, element, sb);
            }
        }

        sb.Append("</").Append(tag).Append('>');
    }

    private static void AppendText(string data, Node? parent, StringBuilder sb)
    {
        // §13.3: inside raw-text / RCDATA parents, text is not escaped.
        if (parent is Element pe && IsRawTextElement(pe.TagName))
        {
            sb.Append(data);
            return;
        }

        foreach (var ch in data)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case Nbsp: sb.Append("&nbsp;"); break;
                default: sb.Append(ch); break;
            }
        }
    }

    private static void AppendAttributeValue(string value, StringBuilder sb)
    {
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '"': sb.Append("&quot;"); break;
                case Nbsp: sb.Append("&nbsp;"); break;
                default: sb.Append(ch); break;
            }
        }
    }

    private static bool IsVoidElement(string tagName) => tagName.ToLowerInvariant() switch
    {
        "area" or "base" or "br" or "col" or "embed" or "hr" or "img" or "input"
            or "link" or "meta" or "param" or "source" or "track" or "wbr" => true,
        _ => false,
    };

    private static bool IsRawTextElement(string tagName) => tagName.ToLowerInvariant() switch
    {
        "style" or "script" or "xmp" or "iframe" or "noembed" or "noframes"
            or "noscript" or "plaintext" => true,
        _ => false,
    };
}
