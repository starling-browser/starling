using System.Text;
using Starling.Dom;

namespace Starling.Engine;

/// <summary>
/// Serializes a parsed inline <c>&lt;svg&gt;</c> DOM subtree back into an SVG
/// document string the managed <see cref="Starling.Paint.Svg.SvgImageDecoder"/>
/// can rasterize. The decoder parses with <c>System.Xml.Linq</c> and looks up
/// element/attribute names case-insensitively by local name, so we emit plain
/// (namespace-free) tags and rely on XML escaping for attribute/text values.
/// </summary>
internal static class InlineSvgSerializer
{
    public static string Serialize(Element svg)
    {
        var sb = new StringBuilder();
        WriteElement(sb, svg);
        return sb.ToString();
    }

    private static void WriteElement(StringBuilder sb, Element el)
    {
        var tag = el.LocalName;
        sb.Append('<').Append(tag);

        foreach (var attr in el.Attributes)
        {
            // Skip namespace declarations: the decoder ignores namespaces and a
            // bare `xmlns` round-trips fine, but `xmlns:foo` prefixed attrs add
            // nothing the local-name lookup uses.
            if (attr.Name.StartsWith("xmlns:", StringComparison.Ordinal))
            {
                continue;
            }

            sb.Append(' ').Append(attr.Name).Append("=\"");
            AppendEscaped(sb, attr.Value, attribute: true);
            sb.Append('"');
        }

        if (el.FirstChild is null)
        {
            sb.Append("/>");
            return;
        }

        sb.Append('>');
        for (var child = el.FirstChild; child is not null; child = child.NextSibling)
        {
            switch (child)
            {
                case Element childEl:
                    WriteElement(sb, childEl);
                    break;
                case Text text:
                    AppendEscaped(sb, text.Data, attribute: false);
                    break;
            }
        }
        sb.Append("</").Append(tag).Append('>');
    }

    private static void AppendEscaped(StringBuilder sb, string? value, bool attribute)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        foreach (var c in value)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"' when attribute: sb.Append("&quot;"); break;
                default: sb.Append(c); break;
            }
        }
    }
}
