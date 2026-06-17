using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starling.SpecGen;

/// <summary>
/// Subset of the webref <c>ed/css/*.json</c> schema we read.
/// Full schema: https://github.com/w3c/webref/tree/main/ed/css
/// </summary>
public sealed record WebrefCss(
    [property: JsonPropertyName("spec")] WebrefSpec Spec,
    [property: JsonPropertyName("properties")] IReadOnlyList<WebrefProperty>? Properties,
    [property: JsonPropertyName("atrules")] IReadOnlyList<WebrefAtRule>? AtRules,
    [property: JsonPropertyName("selectors")] IReadOnlyList<WebrefSelector>? Selectors,
    [property: JsonPropertyName("values")] IReadOnlyList<WebrefValueType>? Values);

public sealed record WebrefSpec(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("url")] string Url);

public sealed record WebrefProperty(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("href")] string? Href,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("initial")] string? Initial,
    [property: JsonPropertyName("inherited")] string? Inherited,
    [property: JsonPropertyName("newValues")] string? NewValues);

public sealed record WebrefAtRule(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("href")] string? Href,
    [property: JsonPropertyName("prose")] string? Prose);

public sealed record WebrefSelector(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("href")] string? Href,
    [property: JsonPropertyName("prose")] string? Prose);

public sealed record WebrefValueType(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("href")] string? Href);

public static class WebrefLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static IReadOnlyList<(string SpecId, WebrefCss Doc)> LoadAll(string cssDir)
    {
        var results = new List<(string, WebrefCss)>();
        foreach (var path in Directory.EnumerateFiles(cssDir, "*.json").OrderBy(p => p))
        {
            using var stream = File.OpenRead(path);
            var doc = JsonSerializer.Deserialize<WebrefCss>(stream, Options);
            if (doc is null)
            {
                continue;
            }

            var specId = Path.GetFileNameWithoutExtension(path);
            results.Add((specId, doc));
        }
        return results;
    }
}
