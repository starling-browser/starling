using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using Starling.Common.Diagnostics;

namespace Starling.Engine.Tests;

/// <summary>
/// Win B coverage: the engine reuses the box tree it materialized for JS as the
/// final paint when nothing layout-affecting changed after scripts ran, instead
/// of running a second full cascade + layout.
///
/// The probe uses a span-counting <see cref="ActivityListener"/> on
/// <see cref="StarlingTelemetry.Source"/>: every cascade + layout pass emits a
/// <c>paint.layout</c> span, so counting those spans tells us exactly how many
/// layouts ran across the whole render. A render whose script reads geometry but
/// leaves the final DOM unchanged from the layout it last materialized runs layout
/// exactly ONCE (the pre-script layout is reused for paint). It runs twice only
/// when a post-layout mutation or a late resource invalidates the cached tree.
///
/// Note on test construction: writing back a result via <c>textContent</c> is
/// itself a DOM mutation that would bump the mutation version past the last
/// layout and defeat reuse. So the reuse tests read geometry without writing it
/// back, and assert correctness against the static DOM content + bitmap size
/// (and against a forced-relayout twin for pixel-equivalent dimensions/text).
/// </summary>
[TestClass]
public sealed class EngineLayoutReuseTests
{
    private static readonly RenderOptions DefaultOptions = new(new Size(800, 600), 16f);

    [TestMethod]
    public async Task Script_reads_geometry_without_later_mutation_runs_layout_exactly_once()
    {
        // The script reads getBoundingClientRect (materializing a layout) and
        // does NOT mutate the DOM afterwards. The engine must reuse that exact
        // box tree for the final paint, so layout runs ONCE total.
        var html = @"<!doctype html><html>
          <head><style>#probe { display:block; width:140px; height:60px; }</style></head>
          <body>
            <div id='probe'>probe</div>
            <p>static-content</p>
            <script>
                // Read geometry — materializes a layout — but write nothing back.
                var r = document.getElementById('probe').getBoundingClientRect();
                if (r.width !== 140) throw new Error('unexpected width ' + r.width);
            </script>
          </body></html>";

        var (outcome, diag) = await RenderHtmlAsync(html);

        diag.LayoutSpanCount.Should().Be(1,
            "the pre-script layout is reused for the final paint when the DOM is unchanged after scripts");
        diag.CountOf("engine.render.reused_prelayout").Should().Be(1);

        // Output is still correct: the static content renders and the bitmap has
        // real dimensions.
        outcome.DisplayText.Should().Contain("static-content");
        outcome.Width.Should().Be(800);
        outcome.Height.Should().BeGreaterThan(0);
    }

    [TestMethod]
    public async Task Reused_render_matches_a_forced_full_relayout_in_text_and_size()
    {
        // Equivalence: a page that reuses the pre-script layout must produce the
        // same display text and bitmap dimensions as the same page rendered
        // through a full re-layout (forced here by a trailing no-op mutation
        // that bumps the mutation version after the last geometry read).
        const string baseBody = @"
          <head><style>#a { display:block; width:200px; height:40px; }
                       #b { display:block; width:120px; height:80px; }</style></head>
          <body>
            <div id='a'>alpha</div>
            <div id='b'>beta</div>
            <p>gamma</p>";

        // Reads geometry, never mutates -> reuse path.
        var reuseHtml = $@"<!doctype html><html>{baseBody}
            <script>
                var ra = document.getElementById('a').getBoundingClientRect();
                if (ra.width !== 200) throw new Error('w ' + ra.width);
            </script>
          </body></html>";

        // Same content, but the script mutates AFTER reading geometry, forcing a
        // full re-layout for the final paint. The mutation rewrites the `class`
        // attribute (and back) so it lands a Layout-relevant version bump
        // — `data-*` / `aria-*` / framework-private attributes are now
        // explicitly excluded from layout invalidation
        // (Document.IsLayoutRelevantAttribute) so they no longer trip the
        // re-layout path. The net rendered output must still match the reuse
        // path because the toggle is a round-trip to the original class state.
        var relayoutHtml = $@"<!doctype html><html>{baseBody}
            <script>
                var ra = document.getElementById('a').getBoundingClientRect();
                if (ra.width !== 200) throw new Error('w ' + ra.width);
                var el = document.getElementById('a');
                var orig = el.getAttribute('class') || '';
                el.setAttribute('class', orig + ' tmp');
                el.setAttribute('class', orig);
            </script>
          </body></html>";

        var (reuse, reuseDiag) = await RenderHtmlAsync(reuseHtml);
        var (relayout, relayoutDiag) = await RenderHtmlAsync(relayoutHtml);

        reuseDiag.CountOf("engine.render.reused_prelayout").Should().Be(1, "the unchanged page reuses its layout");
        reuseDiag.LayoutSpanCount.Should().Be(1);
        relayoutDiag.CountOf("engine.render.reused_prelayout").Should().Be(0, "the mutated page re-lays-out");
        relayoutDiag.LayoutSpanCount.Should().BeGreaterThan(1);

        reuse.DisplayText.Should().Be(relayout.DisplayText);
        reuse.Width.Should().Be(relayout.Width);
        reuse.Height.Should().Be(relayout.Height);
    }

    [TestMethod]
    public async Task Script_reads_geometry_then_mutates_re_lays_out_and_reflects_mutation()
    {
        // Regression: a script reads geometry, then mutates the DOM. The cached
        // pre-mutation tree is stale, so the engine must re-layout for the final
        // paint and the output must reflect the post-mutation content.
        var html = @"<!doctype html><html>
          <head><style>#probe { display:block; width:90px; height:30px; }</style></head>
          <body>
            <div id='probe'>probe</div>
            <p id='out'>placeholder</p>
            <script>
                // Read geometry first (materializes a layout)...
                var r = document.getElementById('probe').getBoundingClientRect();
                // ...then mutate the DOM after the read.
                document.getElementById('out').textContent = 'mutated-after-read ' + Math.round(r.width);
            </script>
          </body></html>";

        var (outcome, diag) = await RenderHtmlAsync(html);

        diag.CountOf("engine.render.reused_prelayout").Should().Be(0,
            "a post-read mutation invalidates the cached layout");
        diag.LayoutSpanCount.Should().BeGreaterThan(1,
            "the stale cached tree forces a full re-layout for the final paint");

        outcome.DisplayText.Should().Contain("mutated-after-read");
        outcome.DisplayText.Should().Contain("90");
        outcome.DisplayText.Should().NotContain("placeholder");
    }

    [TestMethod]
    public async Task Script_mutates_then_reads_geometry_last_reuses_post_mutation_layout()
    {
        // Realistic reuse: the script mutates first, then reads geometry as its
        // final action. The geometry read lazily re-lays-out at the post-
        // mutation version, leaving the host's layout current with the final
        // DOM — so the engine reuses it for the paint (one layout for paint,
        // the read having forced a second; reuse keeps render_document from
        // adding a third). The output reflects the mutation.
        var html = @"<!doctype html><html>
          <head><style>.sized { display:block; width:120px; height:40px; }</style></head>
          <body>
            <div id='host'></div>
            <p>tail-content</p>
            <script>
                var d = document.createElement('div');
                d.id = 'fresh';
                d.className = 'sized';
                d.textContent = 'appended-by-js';
                document.getElementById('host').appendChild(d);
                // Final action: read geometry of the appended node. This brings
                // the JS layout host current with the post-mutation DOM.
                var r = document.getElementById('fresh').getBoundingClientRect();
                if (r.width !== 120) throw new Error('w ' + r.width);
            </script>
          </body></html>";

        var (outcome, diag) = await RenderHtmlAsync(html);

        diag.CountOf("engine.render.reused_prelayout").Should().Be(1,
            "the geometry read brought the cached layout current with the mutated DOM");
        outcome.DisplayText.Should().Contain("appended-by-js");
        outcome.DisplayText.Should().Contain("tail-content");
    }

    [TestMethod]
    public async Task Page_with_no_geometry_reading_script_renders_correctly()
    {
        // Regression: a page whose script never reads geometry but DOES mutate
        // (the common case) re-lays-out for the final paint and reflects the
        // mutation. The reuse machinery must not corrupt this path.
        var html = @"<!doctype html><html><body>
            <p id='out'>start</p>
            <script>document.getElementById('out').textContent = 'no-geometry-read';</script>
          </body></html>";

        var (outcome, diag) = await RenderHtmlAsync(html);

        diag.CountOf("engine.render.reused_prelayout").Should().Be(0,
            "the textContent write mutated the DOM after the pre-script layout");
        outcome.DisplayText.Should().Contain("no-geometry-read");
        outcome.DisplayText.Should().NotContain("start");
        outcome.Width.Should().Be(800);
    }

    [TestMethod]
    public async Task Page_with_no_script_at_all_runs_exactly_one_layout()
    {
        // Regression: the no-JS path is unchanged — a script-free page runs
        // exactly the single render_document layout and never reuses anything.
        var html = @"<!doctype html><html><body>
            <p>just static content</p>
            <div style='width:100px;height:50px;'>box</div>
          </body></html>";

        var (outcome, diag) = await RenderHtmlAsync(html);

        diag.LayoutSpanCount.Should().Be(1);
        diag.CountOf("engine.render.reused_prelayout").Should().Be(0,
            "no scripts ran, so there is no pre-script layout to reuse");
        outcome.DisplayText.Should().Contain("just static content");
    }

    // -------------------------------------------------------------------

    private static async Task<(RenderOutcome Outcome, RenderProbe Diag)> RenderHtmlAsync(string html)
    {
        var tempHtml = Path.Combine(Path.GetTempPath(), $"starling-reuse-{Guid.NewGuid():N}.html");
        var tempPng = Path.Combine(Path.GetTempPath(), $"starling-reuse-{Guid.NewGuid():N}.png");
        await File.WriteAllTextAsync(tempHtml, html, CancellationToken.None);
        try
        {
            using var probe = new RenderProbe();
            var engine = new StarlingEngine(loggerFactory: NullLoggerFactory.Instance);
            var url = new Uri(tempHtml).AbsoluteUri;
            var result = await engine.RenderAsync(url, DefaultOptions, tempPng, CancellationToken.None);
            result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");
            return (result.Value, probe.Snapshot());
        }
        finally
        {
            TryDelete(tempHtml);
            TryDelete(tempPng);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Captures <c>paint.layout</c> spans via <see cref="ActivityListener"/> and
    /// named counters via <see cref="MeterListener"/> for the duration of one render.
    /// Concurrent because the paint backend may bump counters from canvas worker threads.
    /// </summary>
    private sealed class RenderProbe : IDisposable
    {
        private int _layoutSpanCount;
        private readonly ConcurrentDictionary<string, double> _counters = new();
        private readonly ActivityListener _al;
        private readonly MeterListener _ml;

        public RenderProbe()
        {
            _al = new ActivityListener
            {
                ShouldListenTo = src => src.Name == StarlingTelemetry.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStarted = a =>
                {
                    if (a.OperationName == "paint.layout")
                    {
                        Interlocked.Increment(ref _layoutSpanCount);
                    }
                },
            };
            ActivitySource.AddActivityListener(_al);

            _ml = new MeterListener();
            _ml.InstrumentPublished = (inst, lst) =>
            {
                if (inst.Meter.Name == StarlingTelemetry.SourceName)
                {
                    lst.EnableMeasurementEvents(inst);
                }
            };
            _ml.SetMeasurementEventCallback<double>((inst, m, _, _) =>
                _counters.AddOrUpdate(inst.Name, m, (_, prev) => prev + m));
            _ml.SetMeasurementEventCallback<long>((inst, m, _, _) =>
                _counters.AddOrUpdate(inst.Name, m, (_, prev) => prev + m));
            _ml.Start();
        }

        /// <summary>Number of <c>paint.layout</c> spans seen — i.e. how many
        /// full cascade + layout passes ran across the render.</summary>
        public int LayoutSpanCount => Volatile.Read(ref _layoutSpanCount);

        public double CountOf(string name) => _counters.TryGetValue(name, out var v) ? v : 0d;

        /// <summary>Returns a detached snapshot so callers can query after dispose.</summary>
        public RenderProbe Snapshot() => this;

        public void Dispose()
        {
            _al.Dispose();
            _ml.Dispose();
        }
    }
}
