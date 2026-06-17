using AwesomeAssertions;
using SixLabors.ImageSharp;

namespace Starling.Engine.Tests;

/// <summary>
/// WP4 of browser-plan/scroll-model.md at the frame pump: offset writes only
/// flag the page's <see cref="LaidOutPage.ScrollState"/>, and
/// <see cref="PageScripting.PumpFrame"/> drains the flagged set once per frame
/// — before that frame's <c>requestAnimationFrame</c> callbacks — and
/// dispatches the coalesced <c>scroll</c> events into the live realm.
/// </summary>
[TestClass]
public sealed class ScrollEventPumpTests
{
    private static readonly RenderOptions Options = new(new Size(800, 600), 16f);

    private const string Html = """
        <!doctype html><html><body>
          <div id='s' style='overflow:auto;width:200px;height:100px'>
            <div style='width:600px;height:500px'></div>
          </div>
          <div id='out'></div>
          <script>
            var s = document.getElementById('s');
            var out = document.getElementById('out');
            s.addEventListener('scroll', function (e) {
              out.textContent += 'scroll:' + e.bubbles + ':' + e.cancelable
                + ':' + e.isTrusted + ';';
              requestAnimationFrame(function () { out.textContent += 'raf;'; });
            });
          </script>
        </body></html>
        """;

    [TestMethod]
    public async Task Pump_coalesces_writes_and_fires_scroll_before_raf()
    {
        using var page = await LoadInteractiveAsync(Html);
        var scripting = page.Scripting!;
        var el = page.Document.GetElementById("s")!;
        var output = page.Document.GetElementById("out")!;

        page.ScrollState.TryGet(el, out _).Should().BeTrue(
            "the interactive layout pass measures the scroller into the store");

        // Arm the live clock baseline so the next pump advances the loop.
        scripting.PumpFrame(1);
        output.TextContent.Should().Be("", "no scroll event before any write");

        // Three writes in one frame...
        page.ScrollState.Write(el, 0, 10);
        page.ScrollState.Write(el, 0, 20);
        page.ScrollState.Write(el, 0, 30);

        // ...one pump: exactly one scroll event, and the rAF the listener
        // scheduled fires in the SAME frame's rAF phase — proof the scroll
        // steps ran before the frame's animation callbacks.
        var mutated = scripting.PumpFrame(100);
        mutated.Should().BeTrue("the scroll listener mutated the DOM");
        output.TextContent.Should().Be("scroll:false:false:true;raf;");

        // Nothing pending: the next pump dispatches no further event.
        scripting.PumpFrame(200);
        output.TextContent.Should().Be("scroll:false:false:true;raf;");
    }

    [TestMethod]
    public async Task Write_from_a_scroll_listener_fires_on_the_next_pump_bounded()
    {
        using var page = await LoadInteractiveAsync("""
            <!doctype html><html><body>
              <div id='s' style='overflow:auto;width:200px;height:100px'>
                <div style='width:600px;height:500px'></div>
              </div>
              <div id='out'>0</div>
              <script>
                var n = 0;
                document.getElementById('s').addEventListener('scroll', function () {
                  n = n + 1;
                  document.getElementById('out').textContent = String(n);
                });
              </script>
            </body></html>
            """);
        var scripting = page.Scripting!;
        var el = page.Document.GetElementById("s")!;
        var output = page.Document.GetElementById("out")!;

        // Host-side stand-in for a scrollTop write from inside the listener
        // (the WP3 setter routes to the same store Write): the first event
        // writes the offset again during its own dispatch.
        var rewrites = 0;
        el.AddEventListener("scroll", _ =>
        {
            if (rewrites++ == 0)
            {
                page.ScrollState.Write(el, 0, 77);
            }
        });

        scripting.PumpFrame(1);
        page.ScrollState.Write(el, 0, 30);

        // Frame 1: one event; the in-dispatch write lands in the NEXT drain.
        scripting.PumpFrame(50);
        output.TextContent.Should().Be("1", "the re-flag must not recurse the same frame's drain");
        page.ScrollState.HasPendingEvents.Should().BeTrue();

        // Frame 2: the re-flag fires once more; frame 3: bounded, silent.
        scripting.PumpFrame(100);
        output.TextContent.Should().Be("2");
        scripting.PumpFrame(150);
        output.TextContent.Should().Be("2");
        page.ScrollState.HasPendingEvents.Should().BeFalse();
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<LaidOutPage> LoadInteractiveAsync(string html)
    {
        var fixture = Path.Combine(Path.GetTempPath(), $"starling-scrollev-{Guid.NewGuid():N}.html");
        File.WriteAllText(fixture, html);
        var engine = new StarlingEngine();
        // onFirstPaint non-null selects the progressive/interactive path that
        // retains the live realm (and the scroll store) on the returned page.
        var result = await engine.LayoutPageAsync(
            "file://" + fixture.Replace('\\', '/'), Options, CancellationToken.None, onFirstPaint: _ => { });
        result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");
        return result.Value;
    }
}
