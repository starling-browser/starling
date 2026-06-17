using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Starling.Mcp;

public static class StarlingMcpSdkBridge
{
    private const string BrowserResultSchemaJson = """
        {
          "type": "object",
          "properties": {
            "ok": { "type": "boolean" },
            "url": { "type": ["string", "null"] },
            "title": { "type": ["string", "null"] },
            "canGoBack": { "type": "boolean" },
            "canGoForward": { "type": "boolean" },
            "isBusy": { "type": "boolean" },
            "error": { "type": ["string", "null"] },
            "detail": true
          },
          "required": ["ok", "canGoBack", "canGoForward", "isBusy"]
        }
        """;

    private const string ObjectSchemaJson = """
        {
          "type": "object",
          "additionalProperties": true
        }
        """;

    private static readonly JsonElement EmptyObjectInputSchema = ParseSchema(
        """{"type":"object","additionalProperties":false}""");

    public static IMcpServerBuilder WithStarlingToolGroups(
        this IMcpServerBuilder builder,
        IReadOnlyList<IMcpToolGroup> toolGroups)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(toolGroups);

        return builder
            .WithListToolsHandler((_, _) =>
                new ValueTask<ListToolsResult>(new ListToolsResult
                {
                    Tools = BuildTools(toolGroups),
                }))
            .WithCallToolHandler(async (context, ct) =>
                await CallToolAsync(toolGroups, context.Params, ct).ConfigureAwait(false));
    }

    public static IMcpServerBuilder WithStarlingResourceProviders(
        this IMcpServerBuilder builder,
        IReadOnlyList<IMcpResourceProvider> resourceProviders)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(resourceProviders);

        if (resourceProviders.Count == 0)
        {
            return builder;
        }

        return builder
            .WithListResourcesHandler((_, _) =>
                new ValueTask<ListResourcesResult>(new ListResourcesResult
                {
                    Resources = BuildResources(resourceProviders),
                }))
            .WithReadResourceHandler(async (context, ct) =>
                await ReadResourceAsync(resourceProviders, context.Params, ct).ConfigureAwait(false));
    }

    public static IMcpServerBuilder WithStarlingPromptProviders(
        this IMcpServerBuilder builder,
        IReadOnlyList<IMcpPromptProvider> promptProviders)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(promptProviders);

        if (promptProviders.Count == 0)
        {
            return builder;
        }

        return builder
            .WithListPromptsHandler((_, _) =>
                new ValueTask<ListPromptsResult>(new ListPromptsResult
                {
                    Prompts = BuildPrompts(promptProviders),
                }))
            .WithGetPromptHandler(async (context, ct) =>
                await GetPromptAsync(promptProviders, context.Params, ct).ConfigureAwait(false));
    }

    private static IList<Tool> BuildTools(IEnumerable<IMcpToolGroup> groups)
    {
        var tools = new List<Tool>();
        foreach (var group in groups)
        {
            using var doc = JsonDocument.Parse(group.GetToolDescriptorsJson());
            foreach (var descriptor in doc.RootElement.EnumerateArray())
            {
                tools.Add(ToTool(descriptor));
            }
        }

        return tools;
    }

    private static Tool ToTool(JsonElement descriptor)
    {
        var name = RequiredString(descriptor, "name");
        var title = ReadString(descriptor, "title") ?? HumanizeToolName(name);
        return new Tool
        {
            Name = name,
            Title = title,
            Description = ReadString(descriptor, "description"),
            InputSchema = descriptor.TryGetProperty("inputSchema", out var input)
                ? input.Clone()
                : EmptyObjectInputSchema.Clone(),
            OutputSchema = descriptor.TryGetProperty("outputSchema", out var output)
                ? output.Clone()
                : DefaultOutputSchema(name),
            Annotations = descriptor.TryGetProperty("annotations", out var annotations)
                ? ParseAnnotations(annotations, title)
                : DefaultToolAnnotations(name, title),
        };
    }

    private static async ValueTask<CallToolResult> CallToolAsync(
        IReadOnlyList<IMcpToolGroup> groups,
        CallToolRequestParams @params,
        CancellationToken ct)
    {
        var arguments = ToArgumentsElement(@params.Arguments);
        foreach (var group in groups)
        {
            if (!group.HasTool(@params.Name))
            {
                continue;
            }

            try
            {
                var result = await group.InvokeAsync(@params.Name, arguments, ct).ConfigureAwait(false);
                return ToCallToolResult(result);
            }
            catch (ArgumentException ex)
            {
                return ToolErrorResult(ex.Message);
            }
            catch (Exception ex)
            {
                return ToolErrorResult($"Tool '{@params.Name}' failed: {ex.Message}");
            }
        }

        return ToolErrorResult($"Unknown tool: {@params.Name}");
    }

    private static CallToolResult ToCallToolResult(McpToolResult result)
    {
        var text = result.StructuredContent.ToJsonString();
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = text },
            ],
            StructuredContent = ParseSchema(text),
            IsError = result.IsError,
        };
    }

    private static CallToolResult ToolErrorResult(string message) => new()
    {
        Content =
        [
            new TextContentBlock { Text = message },
        ],
        IsError = true,
    };

    private static IList<Resource> BuildResources(IEnumerable<IMcpResourceProvider> providers)
    {
        var resources = new List<Resource>();
        foreach (var provider in providers)
        {
            using var doc = JsonDocument.Parse(provider.GetResourceDescriptorsJson());
            foreach (var descriptor in doc.RootElement.EnumerateArray())
            {
                var name = RequiredString(descriptor, "name");
                resources.Add(new Resource
                {
                    Uri = RequiredString(descriptor, "uri"),
                    Name = name,
                    Title = ReadString(descriptor, "title") ?? name,
                    Description = ReadString(descriptor, "description"),
                    MimeType = ReadString(descriptor, "mimeType"),
                    Annotations = new Annotations
                    {
                        Audience = [Role.Assistant],
                        Priority = 0.7f,
                    },
                });
            }
        }

        return resources;
    }

    private static async ValueTask<ReadResourceResult> ReadResourceAsync(
        IReadOnlyList<IMcpResourceProvider> providers,
        ReadResourceRequestParams @params,
        CancellationToken ct)
    {
        foreach (var provider in providers)
        {
            if (!provider.HasResource(@params.Uri))
            {
                continue;
            }

            var content = await provider.ReadAsync(@params.Uri, ct).ConfigureAwait(false);
            return new ReadResourceResult
            {
                Contents =
                [
                    new TextResourceContents
                    {
                        Uri = @params.Uri,
                        MimeType = content.MimeType,
                        Text = content.Text,
                    },
                ],
            };
        }

        throw new McpProtocolException(
            $"Unknown resource: {@params.Uri}",
            McpErrorCode.ResourceNotFound);
    }

    private static IList<Prompt> BuildPrompts(IEnumerable<IMcpPromptProvider> providers)
    {
        var prompts = new List<Prompt>();
        foreach (var provider in providers)
        {
            using var doc = JsonDocument.Parse(provider.GetPromptDescriptorsJson());
            foreach (var descriptor in doc.RootElement.EnumerateArray())
            {
                var name = RequiredString(descriptor, "name");
                prompts.Add(new Prompt
                {
                    Name = name,
                    Title = ReadString(descriptor, "title") ?? HumanizeToolName(name),
                    Description = ReadString(descriptor, "description"),
                });
            }
        }

        return prompts;
    }

    private static async ValueTask<GetPromptResult> GetPromptAsync(
        IReadOnlyList<IMcpPromptProvider> providers,
        GetPromptRequestParams @params,
        CancellationToken ct)
    {
        var arguments = ToPromptArgumentsElement(@params.Arguments);
        foreach (var provider in providers)
        {
            if (!provider.HasPrompt(@params.Name))
            {
                continue;
            }

            var result = await provider.GetAsync(@params.Name, arguments, ct).ConfigureAwait(false);
            return new GetPromptResult
            {
                Description = result.Description,
                Messages =
                [
                    new PromptMessage
                    {
                        Role = Role.User,
                        Content = new TextContentBlock { Text = result.Text },
                    },
                ],
            };
        }

        throw new McpProtocolException(
            $"Unknown prompt: {@params.Name}",
            McpErrorCode.InvalidParams);
    }

    private static JsonElement ToArgumentsElement(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return default;
        }

        var obj = new JsonObject();
        foreach (var (key, value) in arguments)
        {
            obj[key] = JsonNode.Parse(value.GetRawText());
        }

        using var doc = JsonDocument.Parse(obj.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static JsonElement ToPromptArgumentsElement(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return default;
        }

        var obj = new JsonObject();
        foreach (var (key, value) in arguments)
        {
            obj[key] = JsonNode.Parse(value.GetRawText());
        }

        using var doc = JsonDocument.Parse(obj.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static ToolAnnotations DefaultToolAnnotations(string name, string title)
    {
        var readOnly = IsReadOnlyTool(name);
        return new ToolAnnotations
        {
            Title = title,
            ReadOnlyHint = readOnly,
            DestructiveHint = false,
            IdempotentHint = readOnly,
            OpenWorldHint = name is "browser_navigate" or "browser_refresh" ||
                            name.StartsWith("browser_telemetry_", StringComparison.Ordinal) ||
                            name.StartsWith("lag_", StringComparison.Ordinal),
        };
    }

    private static ToolAnnotations ParseAnnotations(JsonElement annotations, string title)
    {
        var parsed = DefaultToolAnnotations(string.Empty, title);
        if (annotations.TryGetProperty("readOnlyHint", out var readOnly) &&
            readOnly.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            parsed.ReadOnlyHint = readOnly.GetBoolean();
        }

        if (annotations.TryGetProperty("destructiveHint", out var destructive) &&
            destructive.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            parsed.DestructiveHint = destructive.GetBoolean();
        }

        if (annotations.TryGetProperty("idempotentHint", out var idempotent) &&
            idempotent.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            parsed.IdempotentHint = idempotent.GetBoolean();
        }

        if (annotations.TryGetProperty("openWorldHint", out var openWorld) &&
            openWorld.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            parsed.OpenWorldHint = openWorld.GetBoolean();
        }

        return parsed;
    }

    private static bool IsReadOnlyTool(string name)
        => name is "browser_inspect"
            or "browser_console"
            or "browser_network"
            or "browser_query"
            or "browser_computed_style"
            or "browser_telemetry_traces"
            or "browser_telemetry_logs"
            or "browser_telemetry_metrics"
            or "browser_telemetry_describe"
           || name.StartsWith("lag_", StringComparison.Ordinal);

    private static JsonElement? DefaultOutputSchema(string name)
    {
        if (name.StartsWith("browser_", StringComparison.Ordinal) &&
            !name.StartsWith("browser_telemetry_", StringComparison.Ordinal))
        {
            return ParseSchema(BrowserResultSchemaJson);
        }

        if (name.StartsWith("browser_telemetry_", StringComparison.Ordinal) ||
            name.StartsWith("lag_", StringComparison.Ordinal))
        {
            return ParseSchema(ObjectSchemaJson);
        }

        return null;
    }

    private static JsonElement ParseSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string RequiredString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : throw new InvalidOperationException($"MCP descriptor is missing string property '{property}'.");

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string HumanizeToolName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Starling Tool";
        }

        var text = name.Replace('_', ' ').Replace('-', ' ').Replace('.', ' ');
        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}
