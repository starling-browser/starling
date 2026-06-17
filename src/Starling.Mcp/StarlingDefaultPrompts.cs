using System.Text.Json;

namespace Starling.Mcp;

internal static class StarlingDefaultPrompts
{
    public static IEnumerable<IMcpPromptProvider> ForToolGroups(IReadOnlyList<IMcpToolGroup> groups)
    {
        if (groups.Any(g => g.HasTool("browser_inspect")))
        {
            yield return BrowserPrompts.Instance;
        }

        if (groups.Any(g => g.HasTool("browser_telemetry_describe")))
        {
            yield return TelemetryPrompts.Instance;
        }

        if (groups.Any(g => g.HasTool("lag_overview")))
        {
            yield return LagPrompts.Instance;
        }
    }

    private sealed class BrowserPrompts : IMcpPromptProvider
    {
        public static BrowserPrompts Instance { get; } = new();

        public string GetPromptDescriptorsJson() => """
            [
              {
                "name": "starling_inspect_page",
                "title": "Inspect Page",
                "description": "Inspect the current Starling page and report the visible state."
              },
              {
                "name": "starling_debug_render",
                "title": "Debug Render",
                "description": "Find why the current page renders or behaves incorrectly."
              }
            ]
            """;

        public bool HasPrompt(string name) => name is "starling_inspect_page" or "starling_debug_render";

        public Task<McpPromptResult> GetAsync(string name, JsonElement arguments, CancellationToken ct)
            => Task.FromResult(name switch
            {
                "starling_inspect_page" => new McpPromptResult(
                    "Inspect the current Starling page.",
                    "Use browser_inspect first. If the page has visible issues, use browser_screenshot_viewport and browser_query for the key selectors. Summarize URL, title, load state, visible content, console errors, and likely next action."),
                "starling_debug_render" => new McpPromptResult(
                    "Debug the current Starling render.",
                    "Use browser_screenshot_viewport, browser_inspect, browser_computed_style, browser_console, and browser_network. Compare expected vs actual rendering. Name the smallest likely engine subsystem involved and the next test to add."),
                _ => throw new ArgumentException($"Unknown prompt: {name}", nameof(name)),
            });
    }

    private sealed class TelemetryPrompts : IMcpPromptProvider
    {
        public static TelemetryPrompts Instance { get; } = new();

        public string GetPromptDescriptorsJson() => """
            [
              {
                "name": "starling_summarize_telemetry",
                "title": "Summarize Telemetry",
                "description": "Summarize recent Starling spans, logs, and metrics."
              }
            ]
            """;

        public bool HasPrompt(string name) => name == "starling_summarize_telemetry";

        public Task<McpPromptResult> GetAsync(string name, JsonElement arguments, CancellationToken ct)
            => Task.FromResult(new McpPromptResult(
                "Summarize recent Starling telemetry.",
                "Use browser_telemetry_describe first, then browser_telemetry_traces, browser_telemetry_logs, and browser_telemetry_metrics with focused limits. Report the top errors, slow spans, unusual metrics, and the next code area to inspect."));
    }

    private sealed class LagPrompts : IMcpPromptProvider
    {
        public static LagPrompts Instance { get; } = new();

        public string GetPromptDescriptorsJson() => """
            [
              {
                "name": "starling_analyze_lag",
                "title": "Analyze Lag",
                "description": "Use daemon lag tools to identify frame or span bottlenecks."
              }
            ]
            """;

        public bool HasPrompt(string name) => name == "starling_analyze_lag";

        public Task<McpPromptResult> GetAsync(string name, JsonElement arguments, CancellationToken ct)
            => Task.FromResult(new McpPromptResult(
                "Analyze Starling runtime lag.",
                "Use lag_overview first. Then use lag_frames for frame budget data and lag_top_offenders for hot spans. If one span dominates, call lag_correlate_span. Report whether the issue looks CPU-bound, IO-bound, GPU/readback-bound, or blocked."));
    }
}
