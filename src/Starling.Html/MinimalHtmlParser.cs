using Starling.Dom;

namespace Starling.Html;

/// <summary>
/// Legacy minimal HTML parser used by focused parser tests. It is not
/// spec-compliant. It recognizes tag open / close / text content so old
/// fixtures can still exercise the simplest DOM shape.
/// </summary>
/// <remarks>
/// Recognized: start tags, end tags, character data, HTML comments.
/// NOT recognized: entities (returned as literal text), DOCTYPE (skipped),
/// scripts/style raw-text modes, CDATA, foreign content, malformed recovery.
/// </remarks>
public static class MinimalHtmlParser
{
    public static Document Parse(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var document = new Document();
        var stack = new Stack<Node>();
        stack.Push(document);

        var i = 0;
        var n = html.Length;

        while (i < n)
        {
            var c = html[i];
            if (c == '<')
            {
                // Could be: comment, doctype, end tag, start tag
                if (StartsWith(html, i, "<!--"))
                {
                    var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        end = n - 3; // unterminated → swallow to EOF
                    }

                    var data = html[(i + 4)..end];
                    AppendNode(stack, new Comment(data));
                    i = end + 3;
                    continue;
                }
                if (StartsWith(html, i, "<!doctype", ignoreCase: true) ||
                    StartsWith(html, i, "<!DOCTYPE", ignoreCase: true))
                {
                    var end = html.IndexOf('>', i + 1);
                    if (end < 0)
                    {
                        end = n - 1;
                    }

                    i = end + 1;
                    continue;
                }
                if (i + 1 < n && html[i + 1] == '/')
                {
                    // end tag
                    var end = html.IndexOf('>', i + 2);
                    if (end < 0)
                    {
                        end = n - 1;
                    }

                    var name = html[(i + 2)..end].Trim().ToLowerInvariant();
                    PopUntil(stack, name);
                    i = end + 1;
                    continue;
                }

                // start tag
                var tagEnd = html.IndexOf('>', i + 1);
                if (tagEnd < 0)
                {
                    // unterminated; emit the rest as text
                    AppendText(stack, html[i..]);
                    break;
                }
                var raw = html[(i + 1)..tagEnd];
                var selfClosing = raw.EndsWith('/');
                if (selfClosing)
                {
                    raw = raw[..^1];
                }

                var (tag, attrs) = ParseStartTag(raw);
                if (tag.Length == 0)
                {
                    // Looked like a tag but had no name. Treat the '<' as text.
                    AppendText(stack, "<");
                    i++;
                    continue;
                }

                var doc = OwnerDocument(stack);
                var el = doc.CreateElement(tag);
                foreach (var (an, av) in attrs)
                {
                    el.SetAttribute(an, av);
                }

                AppendNode(stack, el);

                if (!IsVoidElement(tag) && !selfClosing)
                {
                    stack.Push(el);
                }

                i = tagEnd + 1;
                continue;
            }

            // Plain character data — read until next '<'.
            var next = html.IndexOf('<', i);
            if (next < 0)
            {
                next = n;
            }

            AppendText(stack, html[i..next]);
            i = next;
        }

        return document;
    }

    private static Document OwnerDocument(Stack<Node> stack)
    {
        foreach (var n in stack)
        {
            if (n is Document d)
            {
                return d;
            }
        }

        throw new InvalidOperationException("No Document at the bottom of the parser stack.");
    }

    private static void AppendNode(Stack<Node> stack, Node node)
        => stack.Peek().AppendChild(node);

    private static void AppendText(Stack<Node> stack, string s)
    {
        if (s.Length == 0)
        {
            return;
        }

        var doc = OwnerDocument(stack);
        stack.Peek().AppendChild(doc.CreateTextNode(s));
    }

    private static void PopUntil(Stack<Node> stack, string tagName)
    {
        // Walk down until we find a matching element. If we don't, ignore the
        // close tag (this is the spec's "parse error -> ignore" fallback).
        var snapshot = stack.ToArray();
        for (var i = 0; i < snapshot.Length; i++)
        {
            if (snapshot[i] is Element e && e.TagName == tagName)
            {
                for (var k = 0; k <= i; k++)
                {
                    stack.Pop();
                }

                return;
            }
            if (snapshot[i] is Document)
            {
                return;
            }
        }
    }

    private static bool StartsWith(string s, int at, string needle, bool ignoreCase = false)
    {
        if (at + needle.Length > s.Length)
        {
            return false;
        }

        return string.Compare(s, at, needle, 0, needle.Length,
            ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) == 0;
    }

    private static bool IsVoidElement(string tag) => tag switch
    {
        "area" or "base" or "br" or "col" or "embed" or "hr" or "img" or "input"
            or "link" or "meta" or "param" or "source" or "track" or "wbr" => true,
        _ => false,
    };

    private static (string tag, List<(string name, string value)> attrs) ParseStartTag(string raw)
    {
        var attrs = new List<(string, string)>();
        var i = 0;
        var n = raw.Length;
        // tag name
        while (i < n && !char.IsWhiteSpace(raw[i]))
        {
            i++;
        }

        var tag = raw[..i].ToLowerInvariant();

        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(raw[i]))
            {
                i++;
            }

            if (i >= n)
            {
                break;
            }

            var nameStart = i;
            while (i < n && raw[i] != '=' && !char.IsWhiteSpace(raw[i]))
            {
                i++;
            }

            var name = raw[nameStart..i].ToLowerInvariant();

            string value = string.Empty;
            if (i < n && raw[i] == '=')
            {
                i++;
                if (i < n && (raw[i] == '"' || raw[i] == '\''))
                {
                    var quote = raw[i++];
                    var valueStart = i;
                    while (i < n && raw[i] != quote)
                    {
                        i++;
                    }

                    value = raw[valueStart..i];
                    if (i < n)
                    {
                        i++; // skip closing quote
                    }
                }
                else
                {
                    var valueStart = i;
                    while (i < n && !char.IsWhiteSpace(raw[i]))
                    {
                        i++;
                    }

                    value = raw[valueStart..i];
                }
            }

            if (name.Length > 0)
            {
                attrs.Add((name, value));
            }
        }

        return (tag, attrs);
    }
}
