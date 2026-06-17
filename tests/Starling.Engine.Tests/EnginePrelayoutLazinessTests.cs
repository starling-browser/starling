using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using Starling.Common.Diagnostics;

namespace Starling.Engine.Tests;

/// <summary>
/// The engine defers the pre-script ("prelayout_for_js") layout until a script
/// actually reads geometry / computed style. A page whose scripts never touch
/// layout — the common case for analytics beacons and feature detection — must
/// skip that full layout pass entirely, while pages that do read geometry must
/// still get correct numbers from a lazily-computed layout.
/// </summary>
[TestClass]
public sealed class EnginePrelayoutLazinessTests
{
    [TestMethod]
    public async Task Prelayout_is_skipped_when_no_script_reads_geometry()
    {
        // The script mutates text but never calls getBoundingClientRect /
        // offsetWidth / getComputedStyle, so the pre-script layout should never
        // run. The final render layout still produces the correct output.
        var html = @"<!doctype html><html><body>
            <p id='out'>before</p>
            <script>document.getElementById('out').textContent = 'after';</script>
            </body></html>";

        var (outcome, probe) = await RenderAsync(html);

        probe.PrelayoutCount.Should().Be(0,
            "no script read geometry or computed style, so the pre-script layout must be skipped");
        outcome.DisplayText.Should().Contain("after",
            "the script still ran and its mutation is reflected in the final render");
    }

    [TestMethod]
    public async Task Prelayout_runs_when_a_script_reads_geometry()
    {
        // Reading getBoundingClientRect forces the lazy layout to materialize,
        // and it must report the styled box dimensions.
        var html = @"<!doctype html><html>
            <head><style>#probe{display:block;width:140px;height:60px;}</style></head>
            <body>
            <div id='probe'>probe</div>
            <p id='out'>?</p>
            <script>
                var r = document.getElementById('probe').getBoundingClientRect();
                document.getElementById('out').textContent = 'w=' + Math.round(r.width);
            </script>
            </body></html>";

        var (outcome, probe) = await RenderAsync(html);

        probe.PrelayoutCount.Should().BeGreaterThanOrEqualTo(1,
            "the geometry read must trigger a lazy pre-script layout");
        outcome.DisplayText.Should().Contain("w=140");
    }

    private static async Task<(RenderOutcome Outcome, PrelayoutProbe Probe)> RenderAsync(string html)
    {
        var tempHtml = Path.Combine(Path.GetTempPath(), $"starling-prelayout-{Guid.NewGuid():N}.html");
        var tempPng = Path.Combine(Path.GetTempPath(), $"starling-prelayout-{Guid.NewGuid():N}.png");
        await File.WriteAllTextAsync(tempHtml, html, CancellationToken.None);
        using var probe = new PrelayoutProbe();
        try
        {
            var engine = new StarlingEngine(loggerFactory: NullLoggerFactory.Instance);
            var url = new Uri(tempHtml).AbsoluteUri;
            var result = await engine.RenderAsync(
                url, new RenderOptions(new Size(800, 600), 16f), tempPng, CancellationToken.None);
            result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");
            return (result.Value, probe);
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

    /// <summary>Counts how many times the engine opened a "prelayout_for_js" span.</summary>
    private sealed class PrelayoutProbe : IDisposable
    {
        private int _prelayoutCount;
        private readonly ActivityListener _al;

        public PrelayoutProbe()
        {
            _al = new ActivityListener
            {
                ShouldListenTo = src => src.Name == StarlingTelemetry.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStarted = a =>
                {
                    if (a.OperationName == "engine.prelayout_for_js")
                    {
                        Interlocked.Increment(ref _prelayoutCount);
                    }
                },
            };
            ActivitySource.AddActivityListener(_al);
        }

        public int PrelayoutCount => Volatile.Read(ref _prelayoutCount);

        public void Dispose() => _al.Dispose();
    }
}
