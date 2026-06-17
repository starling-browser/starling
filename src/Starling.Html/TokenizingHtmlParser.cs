using Starling.Dom;
using Starling.Html.Tokenizer;

namespace Starling.Html;

internal static class TokenizingHtmlParser
{
    public static Document Parse(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var document = new Document();
        var stack = new Stack<Node>();
        stack.Push(document);

        var tokenizer = new HtmlTokenizer();
        tokenizer.Feed(html);
        tokenizer.EndOfInput();

        while (tokenizer.ReadToken() is { } token)
        {
            switch (token)
            {
                case StartTagToken start:
                    InsertStartTag(document, stack, tokenizer, start);
                    break;
                case EndTagToken end:
                    PopUntil(stack, end.Name);
                    break;
                case CharacterToken character:
                    AppendText(document, stack, char.ConvertFromUtf32(character.CodePoint));
                    break;
                case CommentToken comment:
                    stack.Peek().AppendChild(new Comment(comment.Data));
                    break;
                case DoctypeToken:
                case EndOfFileToken:
                    break;
            }
        }

        return document;
    }

    private static void InsertStartTag(
        Document document,
        Stack<Node> stack,
        HtmlTokenizer tokenizer,
        StartTagToken token)
    {
        var element = document.CreateElement(token.Name);
        foreach (var attribute in token.Attributes)
        {
            element.SetAttribute(attribute.Name, attribute.Value);
        }

        stack.Peek().AppendChild(element);
        if (token.SelfClosing || IsVoidElement(token.Name))
        {
            return;
        }

        stack.Push(element);
        tokenizer.SetState(StateForElement(token.Name));
    }

    private static TokenizerState StateForElement(string tagName) => tagName switch
    {
        "textarea" or "title" => TokenizerState.Rcdata,
        "style" or "xmp" or "iframe" or "noembed" or "noframes" or "noscript" => TokenizerState.Rawtext,
        "script" => TokenizerState.ScriptData,
        "plaintext" => TokenizerState.Plaintext,
        _ => TokenizerState.Data,
    };

    private static void AppendText(Document document, Stack<Node> stack, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (stack.Peek().LastChild is Text existing)
        {
            existing.Data += text;
            return;
        }

        stack.Peek().AppendChild(document.CreateTextNode(text));
    }

    private static void PopUntil(Stack<Node> stack, string tagName)
    {
        foreach (var node in stack.ToArray())
        {
            if (node is Document)
            {
                return;
            }

            stack.Pop();
            if (node is Element element && element.TagName == tagName)
            {
                return;
            }
        }
    }

    private static bool IsVoidElement(string tagName) => tagName switch
    {
        "area" or "base" or "br" or "col" or "embed" or "hr" or "img" or "input"
            or "link" or "meta" or "param" or "source" or "track" or "wbr" => true,
        _ => false,
    };
}
