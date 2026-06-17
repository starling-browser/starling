using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Starling.Mcp.Telemetry;
using Starling.Telemetry;

namespace Starling.Mcp.Tests;

[TestClass]
public class TelemetryToolsTests
{
    private const string TestSource = "Starling.Mcp.Tests.Source";
    private const string TestMeter = "Starling.Mcp.Tests.Meter";

    [TestMethod]
    public void Traces_returns_empty_with_no_data()
    {
        using var fixture = new TelemetryFixture();
        var payload = fixture.Tools.BuildTracesPayload(default);

        payload["traces"]!.AsArray().Count.Should().Be(0);
        payload["count"]!.GetValue<int>().Should().Be(0);
    }

    [TestMethod]
    public void Traces_returns_recorded_activities()
    {
        using var fixture = new TelemetryFixture();
        fixture.EmitSpan("engine.render", "phase", "first");
        fixture.EmitSpan("engine.layout", "phase", "second");

        var payload = fixture.Tools.BuildTracesPayload(default);
        var traces = payload["traces"]!.AsArray();

        traces.Count.Should().Be(2);
        traces[0]!["name"]!.GetValue<string>().Should().Be("engine.render");
        traces[0]!["tags"]!["phase"]!.GetValue<string>().Should().Be("first");
        traces[1]!["name"]!.GetValue<string>().Should().Be("engine.layout");
        traces[1]!["status"]!.GetValue<string>().Should().Be("Unset");
        traces[0]!["source"]!.GetValue<string>().Should().Be(TestSource);
    }

    [TestMethod]
    public void Traces_filter_by_source()
    {
        using var fixture = new TelemetryFixture();
        fixture.EmitSpan("render", "k", "v");

        var matching = fixture.InvokeTracesAsync(new { source = TestSource }).AsArray();
        var missing = fixture.InvokeTracesAsync(new { source = "Other.Source" }).AsArray();

        matching.Count.Should().Be(1);
        missing.Count.Should().Be(0);
    }

    [TestMethod]
    public void Traces_filter_by_status_error()
    {
        using var fixture = new TelemetryFixture();
        fixture.EmitSpan("ok-span");
        fixture.EmitSpan("error-span", status: ActivityStatusCode.Error);

        var errors = fixture.InvokeTracesAsync(new { statusError = true }).AsArray();
        errors.Count.Should().Be(1);
        errors[0]!["name"]!.GetValue<string>().Should().Be("error-span");
    }

    [TestMethod]
    public void Traces_filter_by_minDurationMs()
    {
        using var fixture = new TelemetryFixture();
        // Two spans: one too short to clear the bar, one held open longer.
        fixture.EmitSpan("fast-span");
        using (var slow = fixture.Source.StartActivity("slow-span"))
        {
            Thread.Sleep(15);
        }

        var slow_only = fixture.InvokeTracesAsync(new { minDurationMs = 10.0 }).AsArray();
        slow_only.Count.Should().Be(1);
        slow_only[0]!["name"]!.GetValue<string>().Should().Be("slow-span");
    }

    [TestMethod]
    public void Traces_filter_by_since_unix_ms()
    {
        using var fixture = new TelemetryFixture();
        fixture.EmitSpan("first");
        // Capture a wall-clock cutoff that's strictly after the first span's
        // StartUtc. Sleep a bit to keep the comparison robust on fast clocks.
        Thread.Sleep(5);
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Thread.Sleep(5);
        fixture.EmitSpan("second");

        var after = fixture.InvokeTracesAsync(new { sinceUnixMs = cutoff }).AsArray();
        after.Count.Should().Be(1);
        after[0]!["name"]!.GetValue<string>().Should().Be("second");
    }

    [TestMethod]
    public void Traces_respects_limit_and_returns_newest_when_truncated()
    {
        using var fixture = new TelemetryFixture();
        for (var i = 0; i < 5; i++)
        {
            fixture.EmitSpan($"span-{i}");
        }

        var payload = fixture.InvokePayloadAsync(TelemetryTools.TracesToolName, new { limit = 2 });
        var traces = payload["traces"]!.AsArray();

        traces.Count.Should().Be(2);
        payload["truncated"]!.GetValue<bool>().Should().BeTrue();
        // Newest-first selection, then reversed for chronological order in the
        // response: the two most recent spans should be span-3 and span-4.
        traces[0]!["name"]!.GetValue<string>().Should().Be("span-3");
        traces[1]!["name"]!.GetValue<string>().Should().Be("span-4");
    }

    [TestMethod]
    public void Logs_filter_by_category_and_level()
    {
        using var fixture = new TelemetryFixture();
        fixture.EmitLog(LogLevel.Information, "Starling.engine", "info msg");
        fixture.EmitLog(LogLevel.Warning, "Starling.engine.js", "warn msg");
        fixture.EmitLog(LogLevel.Error, "Starling.engine.js", "err msg");

        var jsOnly = fixture.InvokePayloadAsync(TelemetryTools.LogsToolName,
            new { category = "Starling.engine.js" })["logs"]!.AsArray();
        jsOnly.Count.Should().Be(2);

        var warnsAndUp = fixture.InvokePayloadAsync(TelemetryTools.LogsToolName,
            new { minLevel = "Warning" })["logs"]!.AsArray();
        warnsAndUp.Count.Should().Be(2);
        warnsAndUp.Select(n => n!["level"]!.GetValue<string>())
            .Should().BeEquivalentTo(["Warning", "Error"]);
    }

    [TestMethod]
    public void Metrics_filter_by_instrument()
    {
        using var fixture = new TelemetryFixture();
        fixture.EmitCounter("page_load", 1, ("phase", "render"));
        fixture.EmitCounter("net.requests", 3);

        var pageLoads = fixture.InvokePayloadAsync(TelemetryTools.MetricsToolName,
            new { instrument = "page_load" })["measurements"]!.AsArray();
        pageLoads.Count.Should().Be(1);
        pageLoads[0]!["value"]!.GetValue<double>().Should().Be(1.0);
        pageLoads[0]!["tags"]!["phase"]!.GetValue<string>().Should().Be("render");
    }

    [TestMethod]
    public void Describe_lists_observed_sources_categories_and_instruments()
    {
        using var fixture = new TelemetryFixture();
        fixture.EmitSpan("engine.render", "phase", "raster");
        fixture.EmitLog(LogLevel.Information, "Starling.engine", "hello");
        fixture.EmitCounter("page_load", 1);

        var payload = fixture.Tools.BuildDescribePayload();
        payload["activitySources"]!.AsArray()
            .Select(n => n!.GetValue<string>())
            .Should().Contain(TestSource);
        payload["spanNames"]!.AsArray()
            .Select(n => n!.GetValue<string>())
            .Should().Contain("engine.render");
        payload["logCategories"]!.AsArray()
            .Select(n => n!.GetValue<string>())
            .Should().Contain("Starling.engine");
        payload["meters"]!.AsArray()
            .Select(n => n!.GetValue<string>())
            .Should().Contain(TestMeter);
        payload["instruments"]!.AsArray()
            .Select(n => n!.GetValue<string>())
            .Should().Contain($"{TestMeter}:page_load");
        payload["bufferCapacity"]!.GetValue<int>().Should().Be(2000);
    }

    [TestMethod]
    public void HasTool_recognises_all_four_telemetry_tools()
    {
        using var fixture = new TelemetryFixture();
        fixture.Tools.HasTool(TelemetryTools.TracesToolName).Should().BeTrue();
        fixture.Tools.HasTool(TelemetryTools.LogsToolName).Should().BeTrue();
        fixture.Tools.HasTool(TelemetryTools.MetricsToolName).Should().BeTrue();
        fixture.Tools.HasTool(TelemetryTools.DescribeToolName).Should().BeTrue();
        fixture.Tools.HasTool("browser_navigate").Should().BeFalse();
    }

    [TestMethod]
    public void GetToolDescriptorsJson_returns_well_formed_array()
    {
        using var fixture = new TelemetryFixture();
        var json = fixture.Tools.GetToolDescriptorsJson();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().Be(4);
    }

    private sealed class TelemetryFixture : IDisposable
    {
        public InMemoryActivitySink Activities { get; }
        public InMemoryMeterSink Metrics { get; }
        public InMemoryLogSink Logs { get; }
        public TelemetryStream Stream { get; }
        public TelemetryTools Tools { get; }
        public ActivitySource Source { get; } = new(TestSource);
        public Meter Meter { get; } = new(TestMeter);

        public TelemetryFixture()
        {
            Activities = new InMemoryActivitySink(TestSource);
            Metrics = new InMemoryMeterSink(TestMeter);
            Logs = new InMemoryLogSink();
            Stream = new TelemetryStream(Logs, Activities, Metrics);
            Tools = new TelemetryTools(Stream);
        }

        public void EmitSpan(string name, string? tagKey = null, string? tagValue = null,
            ActivityStatusCode status = ActivityStatusCode.Unset)
        {
            using var activity = Source.StartActivity(name, ActivityKind.Internal);
            if (activity is null)
            {
                return;
            }

            if (tagKey is not null)
            {
                activity.SetTag(tagKey, tagValue);
            }

            activity.SetStatus(status);
        }

        public void EmitLog(LogLevel level, string category, string message)
        {
            var logger = Logs.CreateLogger(category);
            logger.Log(level, message);
        }

        public void EmitCounter(string name, double value, params (string Key, object? Value)[] tags)
        {
            var counter = Meter.CreateCounter<double>(name);
            if (tags.Length == 0)
            {
                counter.Add(value);
                return;
            }
            var pairs = new KeyValuePair<string, object?>[tags.Length];
            for (var i = 0; i < tags.Length; i++)
            {
                pairs[i] = new KeyValuePair<string, object?>(tags[i].Key, tags[i].Value);
            }

            counter.Add(value, pairs.AsSpan());
        }

        public System.Text.Json.Nodes.JsonNode InvokeTracesAsync(object args)
            => InvokePayloadAsync(TelemetryTools.TracesToolName, args)["traces"]!;

        public System.Text.Json.Nodes.JsonObject InvokePayloadAsync(string toolName, object args)
        {
            var json = JsonSerializer.Serialize(args);
            using var doc = JsonDocument.Parse(json);
            var result = Tools.InvokeAsync(toolName, doc.RootElement, CancellationToken.None)
                .GetAwaiter().GetResult();
            return result.StructuredContent.AsObject();
        }

        public void Dispose()
        {
            Stream.Dispose();
            Source.Dispose();
            Meter.Dispose();
        }
    }
}
