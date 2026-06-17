using System.Text;

namespace Starling.Html.Tests.TreeBuilder;

/// <summary>
/// Parser for the html5lib tree-construction <c>.dat</c> fixture format
/// (see <c>testdata/spec/html5lib-tests/tree-construction/README.md</c>).
///
/// Each file holds N cases separated by a blank line. Each case is a sequence
/// of <c>#</c>-headed sections; only <c>#data</c> and <c>#document</c> are
/// required. We honor <c>#document-fragment</c> (fragment-parse context) and
/// <c>#script-on</c>/<c>#script-off</c> (scripting flag); <c>#errors</c> and
/// <c>#new-errors</c> are recorded for future error-count assertions but the
/// builder doesn't enforce them today.
/// </summary>
internal sealed record Html5LibCase(
    string SourceFile,
    int BlockIndex,
    string Data,
    string Document,
    string? DocumentFragment,
    bool? ScriptingEnabled)
{
    public string Id => $"{Path.GetFileName(SourceFile)}#{BlockIndex}";
}

internal static class Html5LibDatFile
{
    public static IEnumerable<Html5LibCase> Read(string path)
    {
        // Spec: tests are separated by "\n\n" and the file ends with "\n".
        // Read raw bytes so we don't get surprised by BOM or CRLF normalization
        // in any locale.
        var text = File.ReadAllText(path, new UTF8Encoding(false));
        if (text.Length == 0)
        {
            yield break;
        }

        var idx = 0;
        var blockIndex = 0;
        while (idx < text.Length)
        {
            var end = FindBlockEnd(text, idx);
            // Trim the file-terminating newline: the spec says the file ends with
            // "\n", and the final block would otherwise carry that newline as a
            // spurious trailing empty #document line (breaking the last case in
            // every file). Middle blocks end right before "\n\n" and have none.
            var block = text.Substring(idx, end - idx).TrimEnd('\n');
            idx = end;
            // Skip the two-newline separator (or single trailing newline at EOF).
            while (idx < text.Length && text[idx] == '\n')
            {
                idx++;
            }

            if (block.Length == 0)
            {
                continue;
            }

            var parsed = ParseBlock(block);
            if (parsed is null)
            {
                continue;
            }

            yield return parsed with { SourceFile = path, BlockIndex = blockIndex++ };
        }
    }

    private static int FindBlockEnd(string text, int from)
    {
        // The block ends at the first "\n\n" (one separator) or at EOF.
        var i = from;
        while (i < text.Length)
        {
            if (text[i] == '\n' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                return i;
            }

            i++;
        }
        return text.Length;
    }

    private static Html5LibCase? ParseBlock(string block)
    {
        // Split into named sections. Section headers begin with '#' at column 0;
        // a body line that happens to start with '#' inside #data is preserved
        // because the parser only treats known headers as boundaries.
        // The known headers are listed in the README.
        var sections = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        string? current = null;
        foreach (var rawLine in block.Split('\n'))
        {
            if (IsSectionHeader(rawLine, out var name))
            {
                current = name;
                if (!sections.ContainsKey(current))
                {
                    sections[current] = new StringBuilder();
                }
            }
            else if (current is not null)
            {
                // Sections preserve newlines between body lines but not the
                // header's trailing newline or a final blank line.
                if (sections[current].Length > 0)
                {
                    sections[current].Append('\n');
                }

                sections[current].Append(rawLine);
            }
        }
        if (!sections.TryGetValue("data", out var data) || !sections.TryGetValue("document", out var document))
        {
            return null;
        }

        sections.TryGetValue("document-fragment", out var fragment);
        bool? scripting = null;
        if (sections.ContainsKey("script-on"))
        {
            scripting = true;
        }

        if (sections.ContainsKey("script-off"))
        {
            scripting = false;
        }

        return new Html5LibCase(
            SourceFile: "",
            BlockIndex: 0,
            Data: data.ToString(),
            Document: document.ToString(),
            DocumentFragment: fragment?.ToString().Trim('\n', ' '),
            ScriptingEnabled: scripting);
    }

    private static bool IsSectionHeader(string line, out string name)
    {
        name = "";
        if (line.Length < 2 || line[0] != '#')
        {
            return false;
        }

        var rest = line[1..];
        // Known section names per the README — listing them by hand keeps a stray
        // '#' inside #data from being mis-parsed as a section header.
        switch (rest)
        {
            case "data":
            case "errors":
            case "new-errors":
            case "document":
            case "document-fragment":
            case "script-on":
            case "script-off":
                name = rest;
                return true;
            default:
                return false;
        }
    }
}
