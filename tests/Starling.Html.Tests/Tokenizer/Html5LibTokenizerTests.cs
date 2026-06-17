using System.Text.Json;
using AwesomeAssertions;
using Starling.Html.Tokenizer;
namespace Starling.Html.Tests.Tokenizer;

[TestClass]
public class Html5LibTokenizerTests
{
    [TestMethod]
    [DynamicData(nameof(Cases))]
    public void Tokenizer_matches_html5lib_expected_tokens(Html5LibCase testCase)
    {
        var tokenizer = new HtmlTokenizer();
        if (testCase.LastStartTag is { Length: > 0 } lastStartTag)
        {
            tokenizer.Feed($"<{lastStartTag}>");
            DrainAllAvailable(tokenizer).Should().ContainSingle()
                .Which.Should().Be(new StartTagToken(lastStartTag, [], false));
        }

        tokenizer.SetState(MapState(testCase.InitialState, testCase.DoubleEscaped));
        tokenizer.Feed(testCase.Input);
        tokenizer.EndOfInput();

        var actual = CollapseCharacters(DrainAllAvailable(tokenizer)
            .Where(t => t is not EndOfFileToken));

        actual.Should().Equal(testCase.ExpectedTokens);
    }

    public static IEnumerable<object[]> Cases()
    {
        var tokenizerDir = Path.Combine(
            AppContext.BaseDirectory,
            "testdata",
            "spec",
            "html5lib-tests",
            "tokenizer");

        foreach (var path in Directory.EnumerateFiles(tokenizerDir, "*.test").OrderBy(p => p))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("tests", out var tests))
            {
                continue;
            }

            foreach (var test in tests.EnumerateArray())
            {
                var states = test.TryGetProperty("initialStates", out var initialStates)
                    ? initialStates.EnumerateArray().Select(e => e.GetString()!).ToArray()
                    : ["Data state"];
                var description = test.GetProperty("description").GetString()!;
                var input = DecodeEscapes(test.GetProperty("input").GetString()!);
                var lastStartTag = test.TryGetProperty("lastStartTag", out var lastStart)
                    ? lastStart.GetString()
                    : null;
                var doubleEscaped = test.TryGetProperty("doubleEscaped", out var escaped) &&
                    escaped.ValueKind == JsonValueKind.True;

                foreach (var state in states)
                {
                    var expected = ParseExpectedTokens(test.GetProperty("output"));
                    yield return
                    [
                        new Html5LibCase(
                            $"{Path.GetFileName(path)}: {description} [{state}]",
                            input,
                            state,
                            lastStartTag,
                            doubleEscaped,
                            expected),
                    ];
                }
            }
        }
    }

    private static TokenizerState MapState(string state, bool doubleEscaped) => state switch
    {
        "Data state" => TokenizerState.Data,
        "RCDATA state" => TokenizerState.Rcdata,
        "RAWTEXT state" => TokenizerState.Rawtext,
        "PLAINTEXT state" => TokenizerState.Plaintext,
        "Script data state" when doubleEscaped => TokenizerState.ScriptDataDoubleEscaped,
        "Script data state" => TokenizerState.ScriptData,
        "CDATA section state" => TokenizerState.CdataSection,
        _ => throw new NotSupportedException($"Unsupported html5lib tokenizer state '{state}'."),
    };

    private static List<string> ParseExpectedTokens(JsonElement output)
    {
        var tokens = new List<string>();
        foreach (var token in output.EnumerateArray())
        {
            var type = token[0].GetString();
            tokens.Add(type switch
            {
                "Character" => $"Character:{DecodeEscapes(token[1].GetString()!)}",
                "Comment" => $"Comment:{DecodeEscapes(token[1].GetString()!)}",
                "DOCTYPE" => FormatDoctype(token),
                "StartTag" => FormatStartTag(token),
                "EndTag" => $"EndTag:{token[1].GetString()!}",
                _ => throw new NotSupportedException($"Unsupported html5lib token type '{type}'."),
            });
        }

        return tokens;
    }

    private static string FormatDoctype(JsonElement token)
    {
        var name = token[1].ValueKind == JsonValueKind.Null ? "" : token[1].GetString();
        var publicId = token[2].ValueKind == JsonValueKind.Null ? "" : token[2].GetString();
        var systemId = token[3].ValueKind == JsonValueKind.Null ? "" : token[3].GetString();
        var correctness = token[4].GetBoolean();
        return $"DOCTYPE:{name}:{publicId}:{systemId}:{correctness}";
    }

    private static string FormatStartTag(JsonElement token)
    {
        var attributes = token[2].EnumerateObject()
            .Select(p => $"{p.Name}={DecodeEscapes(p.Value.GetString()!)}");
        var selfClosing = token.GetArrayLength() > 3 && token[3].GetBoolean();
        return $"StartTag:{token[1].GetString()!}:{string.Join(",", attributes)}:{selfClosing}";
    }

    private static List<string> CollapseCharacters(IEnumerable<HtmlToken> tokens)
    {
        var collapsed = new List<string>();
        var characterBuffer = new List<char>();

        foreach (var token in tokens)
        {
            if (token is CharacterToken character)
            {
                if (character.CodePoint <= 0xFFFF)
                {
                    characterBuffer.Add((char)character.CodePoint);
                }
                else
                {
                    characterBuffer.AddRange(char.ConvertFromUtf32(character.CodePoint));
                }

                continue;
            }

            FlushCharacters();
            collapsed.Add(token switch
            {
                CommentToken comment => $"Comment:{comment.Data}",
                DoctypeToken doctype => FormatActualDoctype(doctype),
                StartTagToken start => FormatActualStartTag(start),
                EndTagToken end => $"EndTag:{end.Name}",
                _ => throw new NotSupportedException($"Unsupported tokenizer token '{token}'."),
            });
        }

        FlushCharacters();
        return collapsed;

        void FlushCharacters()
        {
            if (characterBuffer.Count == 0)
            {
                return;
            }

            collapsed.Add($"Character:{new string(characterBuffer.ToArray())}");
            characterBuffer.Clear();
        }
    }

    private static string FormatActualDoctype(DoctypeToken token)
        => $"DOCTYPE:{token.Name}:{token.PublicId}:{token.SystemId}:{!token.ForceQuirks}";

    private static string FormatActualStartTag(StartTagToken token)
    {
        var attributes = token.Attributes.Select(a => $"{a.Name}={a.Value}");
        return $"StartTag:{token.Name}:{string.Join(",", attributes)}:{token.SelfClosing}";
    }

    private static List<HtmlToken> DrainAllAvailable(HtmlTokenizer tokenizer)
    {
        var tokens = new List<HtmlToken>();
        while (tokenizer.ReadToken() is { } token)
        {
            tokens.Add(token);
        }

        return tokens;
    }

    private static string DecodeEscapes(string value)
        => value
            .Replace("\\u0000", "\0", StringComparison.Ordinal)
            .Replace("\\u000D", "\r", StringComparison.Ordinal)
            .Replace("\\u000A", "\n", StringComparison.Ordinal)
            .Replace("\\u0009", "\t", StringComparison.Ordinal)
            .Replace("\\u000C", "\f", StringComparison.Ordinal)
            .Replace("\\u001B", "\u001b", StringComparison.Ordinal)
            .Replace("\\uD800", "\ud800", StringComparison.Ordinal)
            .Replace("\\uDFFF", "\udfff", StringComparison.Ordinal)
            .Replace("\\uDBFF", "\udbff", StringComparison.Ordinal)
            .Replace("\\uDC00", "\udc00", StringComparison.Ordinal)
            .Replace("\\uFFFF", "\uffff", StringComparison.Ordinal)
            .Replace("\\uFFFE", "\ufffe", StringComparison.Ordinal)
            .Replace("\\uFFFD", "\ufffd", StringComparison.Ordinal);
}

public sealed record Html5LibCase(
    string Name,
    string Input,
    string InitialState,
    string? LastStartTag,
    bool DoubleEscaped,
    IReadOnlyList<string> ExpectedTokens)
{
    public override string ToString() => Name;
}
