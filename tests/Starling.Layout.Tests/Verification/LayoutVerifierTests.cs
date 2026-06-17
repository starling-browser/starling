using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Common.Diagnostics;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout.Box;
using Starling.Layout.Verification;

namespace Starling.Layout.Tests.Verification;

[TestClass]
public sealed class LayoutVerifierTests
{
    private static BlockBox Layout(string html, Size viewport)
        => new LayoutEngine(new StyleEngine()).LayoutDocument(HtmlParser.Parse(html), viewport);

    // --- the identity check that proves the harness itself works --------------

    [TestMethod]
    public void Two_full_rebuilds_of_the_same_document_are_identical()
    {
        // Full-vs-full is what the harness runs until incremental layout lands.
        const string html =
            "<body><h1>Title</h1><p>some words that wrap across lines here</p>" +
            "<div><span>a</span><span>b</span></div></body>";
        var a = Layout(html, new Size(300, 600));
        var b = Layout(html, new Size(300, 600));

        LayoutVerifier.FindFirstDivergence(a, b).Should().BeNull();
    }

    // --- deliberately injected faults must be reported ------------------------

    [TestMethod]
    public void Injected_frame_fault_names_the_element_and_field()
    {
        const string html = "<body><div id=target>x</div></body>";
        var good = Layout(html, new Size(400, 600));
        var faulted = Layout(html, new Size(400, 600));

        // Shove one box 5px to the right — exactly the silent mispositioning the
        // harness exists to catch.
        var target = FindById(faulted, "target")!;
        target.Frame = target.Frame.Translate(5, 0);

        var d = LayoutVerifier.FindFirstDivergence(good, faulted);
        d.Should().NotBeNull();
        d!.Value.Field.Should().Be("Frame.X");
        d.Value.Element.Should().NotBeNull();
        d.Value.Element!.Id.Should().Be("target");
    }

    [TestMethod]
    public void Different_viewport_widths_diverge()
    {
        const string html = "<body><div>x</div></body>";
        var narrow = Layout(html, new Size(400, 600));
        var wide = Layout(html, new Size(800, 600));

        var d = LayoutVerifier.FindFirstDivergence(narrow, wide);
        d.Should().NotBeNull();
        d!.Value.Field.Should().StartWith("Frame.");
    }

    [TestMethod]
    public void Structural_difference_is_reported_as_child_count()
    {
        const string html = "<body><div id=row><span>a</span><span>b</span></div></body>";
        var good = Layout(html, new Size(400, 600));
        var faulted = Layout(html, new Size(400, 600));

        // Drop a child box without touching #row's own frame — a structural
        // divergence with no geometry change to the ancestor, so ChildCount is
        // the first thing that differs rather than a Frame field.
        var row = FindById(faulted, "row")!;
        row.Children.RemoveAt(row.Children.Count - 1);

        var d = LayoutVerifier.FindFirstDivergence(good, faulted);
        d.Should().NotBeNull();
        d!.Value.Field.Should().Be("ChildCount");
        d.Value.Element!.Id.Should().Be("row");
    }

    [TestMethod]
    public void Differing_text_content_is_reported()
    {
        var hello = Layout("<body><p>hello</p></body>", new Size(400, 600));
        var world = Layout("<body><p>world</p></body>", new Size(400, 600));

        var d = LayoutVerifier.FindFirstDivergence(hello, world);
        d.Should().NotBeNull();
        // Either the text node itself or its line fragment carries the mismatch.
        d!.Value.Field.Should().Match(f => f == "Text" || f.StartsWith("Fragments["));
    }

    // --- env-gated dual run inside LayoutEngine -------------------------------

    [TestMethod]
    public void Engine_dual_run_records_ok_when_layout_is_deterministic()
    {
        var rec = new RecordingLoggerProvider();
        using var factory = new RecordingLoggerFactory(rec);
        using var metrics = new MetricRecorder();

        var baselineOk = metrics.CountOf("layout.verify.ok");
        var baselineDivergent = metrics.CountOf("layout.verify.divergent");

        var engine = new LayoutEngine(new StyleEngine(), loggerFactory: factory) { VerifyLayout = true };
        engine.LayoutDocument(
            HtmlParser.Parse("<body><h1>x</h1><p>words words words</p></body>"),
            new Size(320, 600));

        (metrics.CountOf("layout.verify.ok") - baselineOk).Should().Be(1);
        (metrics.CountOf("layout.verify.divergent") - baselineDivergent).Should().Be(0);
        rec.Entries.Where(e => e.Level >= LogLevel.Error).Should().BeEmpty();
    }

    [TestMethod]
    public void Engine_skips_verification_by_default()
    {
        using var metrics = new MetricRecorder();

        var baselineOk = metrics.CountOf("layout.verify.ok");
        var baselineRuns = metrics.CountOf("layout.runs");

        var engine = new LayoutEngine(new StyleEngine(), loggerFactory: NullLoggerFactory.Instance) { VerifyLayout = false };
        engine.LayoutDocument(HtmlParser.Parse("<body><p>x</p></body>"), new Size(320, 600));

        (metrics.CountOf("layout.verify.ok") - baselineOk).Should().Be(0);
        // Only a single layout run, not the dual run.
        (metrics.CountOf("layout.runs") - baselineRuns).Should().Be(1);
    }

    // --- corpus: every site lays out deterministically ------------------------

    [TestMethod]
    public void Corpus_sites_lay_out_deterministically()
    {
        var sites = LocateSitesDir();
        if (sites is null)
        {
            Assert.Inconclusive("testdata/sites not found from test binary.");
            return;
        }

        var files = Directory.EnumerateFiles(sites, "*.html", SearchOption.AllDirectories).ToList();
        files.Should().NotBeEmpty();

        foreach (var file in files)
        {
            var html = File.ReadAllText(file);
            var doc = HtmlParser.Parse(html);
            var a = new LayoutEngine(new StyleEngine()).LayoutDocument(doc, new Size(1024, 768));
            var b = new LayoutEngine(new StyleEngine()).LayoutDocument(doc, new Size(1024, 768));
            LayoutVerifier.FindFirstDivergence(a, b)
                .Should().BeNull($"layout of {Path.GetFileName(file)} should be deterministic");
        }
    }

    // --- helpers --------------------------------------------------------------

    private static Box.Box? FindById(Box.Box box, string id)
    {
        if (box.Element?.Id == id)
        {
            return box;
        }

        foreach (var child in box.Children)
        {
            if (FindById(child, id) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static string? LocateSitesDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "testdata", "sites");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }

    private sealed class RecordingLoggerFactory(RecordingLoggerProvider provider) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => provider.CreateLogger(categoryName);
        public void AddProvider(ILoggerProvider p) { }
        public void Dispose() { }
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public readonly List<(string Category, LogLevel Level, string Message)> Entries = new();
        public ILogger CreateLogger(string categoryName) => new Rec(this, categoryName);
        public void Dispose() { }
        private sealed class Rec(RecordingLoggerProvider o, string cat) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState s) where TState : notnull => null;
            public bool IsEnabled(LogLevel l) => true;
            public void Log<TState>(LogLevel l, EventId id, TState s, Exception? ex,
                Func<TState, Exception?, string> fmt)
            { lock (o.Entries)
                {
                    o.Entries.Add((cat, l, fmt(s, ex)));
                }
            }
        }
    }

    private sealed class MetricRecorder : IDisposable
    {
        private readonly MeterListener _l = new();
        private readonly ConcurrentDictionary<string, double> _v = new();

        public MetricRecorder()
        {
            _l.InstrumentPublished = (inst, lst) =>
            { if (inst.Meter.Name == StarlingTelemetry.SourceName)
                {
                    lst.EnableMeasurementEvents(inst);
                }
            };
            _l.SetMeasurementEventCallback<double>((inst, m, t, s) => Add(inst.Name, m));
            _l.SetMeasurementEventCallback<long>((inst, m, t, s) => Add(inst.Name, m));
            _l.Start();
        }

        private void Add(string n, double m) => _v.AddOrUpdate(n, m, (_, p) => p + m);
        public double CountOf(string name) => _v.TryGetValue(name, out var x) ? x : 0d;
        public void Dispose() => _l.Dispose();
    }
}
