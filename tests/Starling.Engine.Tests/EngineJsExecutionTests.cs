using System.Net;
using System.Text;
using AwesomeAssertions;
using SixLabors.ImageSharp;
using Starling.Spec;
namespace Starling.Engine.Tests;

/// <summary>
/// Engine-level coverage for the JS-execution hook landed alongside the
/// google.com B7 smoke test: page scripts now run between HTML parse and
/// layout, so DOM mutations performed by scripts surface in the rendered
/// <see cref="RenderOutcome.DisplayText"/> and in the painted bitmap.
/// </summary>
[TestClass]
public sealed class EngineJsExecutionTests
{
    private static readonly RenderOptions DefaultOptions =
        new(new Size(800, 600), 16f);

    [TestMethod]
    public async Task Inline_script_mutating_body_text_is_visible_in_display_text()
    {
        // The script replaces a placeholder string after parse. If the engine
        // executes the script before extracting display text the assertion
        // passes; if it skips JS we'd see the original placeholder.
        var html = @"<!doctype html><html><body>
            <p id='out'>placeholder</p>
            <script>document.getElementById('out').textContent = 'after-js';</script>
        </body></html>";

        var outcome = await RenderHtmlAsync(html);
        outcome.DisplayText.Should().Contain("after-js");
        outcome.DisplayText.Should().NotContain("placeholder");
    }

    [Spec("html", "https://html.spec.whatwg.org/multipage/parsing.html#parsing-main-inhead",
        "13.2.6.4.4 in head — noscript / scripting flag")]
    [SpecFact]
    public async Task Noscript_contents_are_not_rendered_when_scripting_is_enabled()
    {
        // The engine runs JS (scripting flag enabled). Per WHATWG HTML the
        // <noscript> element must contribute no visible text/boxes: the UA
        // stylesheet rule §15.3.1 `noscript { display: none }` hides it, and
        // the parser turns in-head <noscript> contents into inert raw text.
        // Regression for mcmaster.com showing its "enable JavaScript" fallback.
        var html = @"<!doctype html><html><body>
            <p>VISIBLE</p>
            <noscript>HIDDEN-NOSCRIPT-FALLBACK</noscript>
        </body></html>";

        var outcome = await RenderHtmlAsync(html);
        outcome.DisplayText.Should().Contain("VISIBLE");
        outcome.DisplayText.Should().NotContain("HIDDEN-NOSCRIPT-FALLBACK");
    }

    [TestMethod]
    public async Task Inline_script_appending_a_new_element_is_visible_in_display_text()
    {
        var html = @"<!doctype html><html><body>
            <div id='host'></div>
            <script>
                var d = document.createElement('div');
                d.textContent = 'injected-by-js';
                document.getElementById('host').appendChild(d);
            </script>
        </body></html>";

        var outcome = await RenderHtmlAsync(html);
        outcome.DisplayText.Should().Contain("injected-by-js");
    }

    [TestMethod]
    public async Task DOMContentLoaded_listener_fires_during_engine_render()
    {
        // Listener attached during initial script run mutates the DOM when
        // DOMContentLoaded dispatches — the engine fires DOMContentLoaded
        // before draining microtasks, so the mutation lands ahead of paint.
        var html = @"<!doctype html><html><body>
            <p id='out'>still-loading</p>
            <script>
                document.addEventListener('DOMContentLoaded', function() {
                    document.getElementById('out').textContent = 'dom-ready';
                });
            </script>
        </body></html>";

        var outcome = await RenderHtmlAsync(html);
        outcome.DisplayText.Should().Contain("dom-ready");
        outcome.DisplayText.Should().NotContain("still-loading");
    }

    [TestMethod]
    public async Task Fetch_completion_landing_during_microtask_drain_is_visible_in_display_text()
    {
        // The engine's PumpPendingAsync re-enters the VM after off-thread
        // fetch completions enqueue resolve jobs. This pins that the pump
        // settles before display-text extraction.
        await using var server = await JsonResultsServer.StartAsync(
            "[\"alpha-result\",\"beta-result\",\"gamma-result\"]");

        var html = $@"<!doctype html><html><body>
            <div id='results'></div>
            <script>
                fetch('{server.BaseUrl}/api/results')
                  .then(function(r) {{ return r.json(); }})
                  .then(function(items) {{
                      var host = document.getElementById('results');
                      items.forEach(function(t) {{
                          var d = document.createElement('div');
                          d.textContent = t;
                          host.appendChild(d);
                      }});
                  }});
            </script>
        </body></html>";

        var outcome = await RenderHtmlAsync(html);
        outcome.DisplayText.Should().Contain("alpha-result");
        outcome.DisplayText.Should().Contain("beta-result");
        outcome.DisplayText.Should().Contain("gamma-result");
    }

    [TestMethod]
    public async Task Script_with_throw_does_not_abort_render()
    {
        // First script throws; second script still runs and its mutation is
        // observed. The engine routes JsThrow through diagnostics rather
        // than failing the render.
        var html = @"<!doctype html><html><body>
            <p id='out'>before</p>
            <script>throw new Error('boom from script #1');</script>
            <script>document.getElementById('out').textContent = 'after-recovery';</script>
        </body></html>";

        var outcome = await RenderHtmlAsync(html);
        outcome.DisplayText.Should().Contain("after-recovery");
    }

    [TestMethod]
    public async Task setTimeout_zero_callback_is_visible_in_display_text()
    {
        // Bundlers commonly defer init via setTimeout(fn, 0). PumpPendingAsync
        // advances the simulated event-loop clock so the chained callback
        // fires before display-text extraction.
        var html = @"<!doctype html><html><body>
            <p id='out'>before-timeout</p>
            <script>
                setTimeout(function() {
                    document.getElementById('out').textContent = 'after-timeout';
                }, 0);
            </script>
        </body></html>";

        var outcome = await RenderHtmlAsync(html);
        outcome.DisplayText.Should().Contain("after-timeout");
        outcome.DisplayText.Should().NotContain("before-timeout");
    }

    [TestMethod]
    public async Task Chained_setTimeouts_with_delays_all_settle_within_budget()
    {
        // Three nested setTimeouts each with a non-zero delay — only land
        // if PumpPendingAsync advances simulated time across multiple steps.
        var html = @"<!doctype html><html><body>
            <p id='out'>0</p>
            <script>
                function tick(remaining) {
                    document.getElementById('out').textContent = String(3 - remaining);
                    if (remaining > 0) setTimeout(function() { tick(remaining - 1); }, 25);
                }
                setTimeout(function() { tick(3); }, 25);
            </script>
        </body></html>";

        var outcome = await RenderHtmlAsync(html);
        outcome.DisplayText.Should().Contain("3");
    }

    [TestMethod]
    public async Task requestAnimationFrame_callback_runs_during_render()
    {
        // Pages that bootstrap via rAF (instead of setTimeout) must settle
        // during PumpPendingAsync — AnimationFrameBinding is installed and
        // AdvanceBy routes through RunFrame so the rAF queue drains.
        var html = @"<!doctype html><html><body>
            <p id='out'>before-raf</p>
            <script>
                requestAnimationFrame(function() {
                    document.getElementById('out').textContent = 'after-raf';
                });
            </script>
        </body></html>";

        var outcome = await RenderHtmlAsync(html);
        outcome.DisplayText.Should().Contain("after-raf");
        outcome.DisplayText.Should().NotContain("before-raf");
    }

    [TestMethod]
    public async Task cancelAnimationFrame_prevents_callback_during_render()
    {
        var html = @"<!doctype html><html><body>
            <p id='out'>start</p>
            <script>
                var id = requestAnimationFrame(function() {
                    document.getElementById('out').textContent = 'should-not-run';
                });
                cancelAnimationFrame(id);
                requestAnimationFrame(function() {
                    document.getElementById('out').textContent = 'sibling-ran';
                });
            </script>
        </body></html>";

        var outcome = await RenderHtmlAsync(html);
        outcome.DisplayText.Should().Contain("sibling-ran");
        outcome.DisplayText.Should().NotContain("should-not-run");
    }

    [TestMethod]
    public async Task Chained_requestAnimationFrame_loop_settles_within_budget()
    {
        // A rAF chain — like a fade-in driven by a counter — must tick
        // multiple frames during PumpPendingAsync. Each nested rAF lands
        // in the next frame's queue.
        var html = @"<!doctype html><html><body>
            <p id='out'>0</p>
            <script>
                function tick(remaining) {
                    document.getElementById('out').textContent = String(3 - remaining);
                    if (remaining > 0) requestAnimationFrame(function() { tick(remaining - 1); });
                }
                requestAnimationFrame(function() { tick(3); });
            </script>
        </body></html>";

        var outcome = await RenderHtmlAsync(html);
        outcome.DisplayText.Should().Contain("3");
    }

    [TestMethod]
    public async Task getBoundingClientRect_returns_nonzero_dimensions_for_styled_box()
    {
        // The engine pre-lays-out the document before running scripts, so
        // getBoundingClientRect should report the styled width/height of
        // the target div (not 0/0 like a never-laid-out doc would).
        var html = @"<!doctype html><html>
          <head><style>#probe { display:block; width:140px; height:60px; background:#abc; }</style></head>
          <body>
            <div id='probe'>probe</div>
            <p id='out'>?</p>
            <script>
                var r = document.getElementById('probe').getBoundingClientRect();
                document.getElementById('out').textContent =
                    'w=' + Math.round(r.width) + ' h=' + Math.round(r.height);
            </script>
          </body></html>";

        var outcome = await RenderHtmlAsync(html);
        outcome.DisplayText.Should().Contain("w=140");
        outcome.DisplayText.Should().Contain("h=60");
    }

    [TestMethod]
    public async Task offsetWidth_and_offsetHeight_track_layout()
    {
        var html = @"<!doctype html><html>
          <head><style>#probe { display:block; width:80px; height:30px; }</style></head>
          <body>
            <div id='probe'></div>
            <p id='out'>?</p>
            <script>
                var probe = document.getElementById('probe');
                document.getElementById('out').textContent =
                    'ow=' + probe.offsetWidth + ' oh=' + probe.offsetHeight;
            </script>
          </body></html>";

        var outcome = await RenderHtmlAsync(html);
        outcome.DisplayText.Should().Contain("ow=80");
        outcome.DisplayText.Should().Contain("oh=30");
    }

    [TestMethod]
    public async Task getComputedStyle_returns_resolved_css_property_value()
    {
        // The cascade snapshot the engine builds before scripts run feeds
        // getComputedStyle. The kebab-case getter and the camelCase
        // accessor should both report the styled value.
        var html = @"<!doctype html><html>
          <head><style>#probe { color:#ff0000; font-size:24px; display:block; }</style></head>
          <body>
            <div id='probe'>probe</div>
            <p id='out'>?</p>
            <script>
                var cs = window.getComputedStyle(document.getElementById('probe'));
                document.getElementById('out').textContent =
                    'd=' + cs.getPropertyValue('display') +
                    ' fs=' + cs.fontSize;
            </script>
          </body></html>";

        var outcome = await RenderHtmlAsync(html);
        outcome.DisplayText.Should().Contain("d=block");
        outcome.DisplayText.Should().Contain("fs=24px");
    }

    [TestMethod]
    public async Task getBoundingClientRect_reflects_element_appended_in_same_script_run()
    {
        // Live re-layout: a script appends a sized element, then immediately
        // reads its geometry. The read must trigger a lazy re-layout and
        // report the new element's box, not the pre-script snapshot (which had
        // no such element and would yield 0/0).
        var html = @"<!doctype html><html>
          <head><style>.sized { display:block; width:120px; height:40px; }</style></head>
          <body>
            <p id='out'>?</p>
            <script>
                var d = document.createElement('div');
                d.id = 'fresh';
                d.className = 'sized';
                document.body.appendChild(d);
                var r = document.getElementById('fresh').getBoundingClientRect();
                document.getElementById('out').textContent =
                    'w=' + Math.round(r.width) + ' h=' + Math.round(r.height);
            </script>
          </body></html>";

        var outcome = await RenderHtmlAsync(html);
        outcome.DisplayText.Should().Contain("w=120");
        outcome.DisplayText.Should().Contain("h=40");
    }

    [TestMethod]
    public async Task offsetTop_of_following_sibling_reflects_content_inserted_before_it()
    {
        // Live re-layout: inserting a sized block before an existing element
        // pushes that element down. Reading its offsetTop after the mutation
        // must reflect the new layout, not the pre-script position.
        var html = @"<!doctype html><html>
          <head><style>.spacer { display:block; height:50px; } #anchor { display:block; }</style></head>
          <body>
            <div id='anchor'>anchor</div>
            <p id='out'>?</p>
            <script>
                var anchor = document.getElementById('anchor');
                var before = anchor.offsetTop;
                var s = document.createElement('div');
                s.className = 'spacer';
                document.body.insertBefore(s, anchor);
                var after = anchor.offsetTop;
                document.getElementById('out').textContent =
                    'before=' + Math.round(before) + ' after=' + Math.round(after);
            </script>
          </body></html>";

        var outcome = await RenderHtmlAsync(html);
        // The spacer is 50px tall, so the anchor moves down by 50px after the
        // insertion. The exact "before" depends on body margins; the load-
        // bearing assertion is that "after" is 50px greater than "before".
        var match = System.Text.RegularExpressions.Regex.Match(
            outcome.DisplayText, @"before=(\d+) after=(\d+)");
        match.Success.Should().BeTrue($"display text was '{outcome.DisplayText}'");
        var before = int.Parse(match.Groups[1].Value);
        var after = int.Parse(match.Groups[2].Value);
        (after - before).Should().Be(50);
    }

    [TestMethod]
    public async Task Module_runs_deferred_after_classic_and_data_block_is_skipped()
    {
        // HTML §4.12.1: classic scripts run during parse; type="module" runs
        // deferred (after parse), so the module's write wins over the classic
        // sibling. A non-JS data block (application/ld+json) never executes.
        var html = @"<!doctype html><html><body>
            <p id='out'>start</p>
            <script type='module'>document.getElementById('out').textContent = 'module-ran';</script>
            <script type='application/ld+json'>{ ""@context"": ""bogus"" }</script>
            <script>document.getElementById('out').textContent = 'classic-ran';</script>
        </body></html>";

        var outcome = await RenderHtmlAsync(html);
        // The deferred module runs last, overwriting the classic result.
        outcome.DisplayText.Should().Contain("module-ran");
        outcome.DisplayText.Should().NotContain("bogus");
    }

    [TestMethod]
    public async Task Deferred_scripts_execute_after_inline_in_document_order()
    {
        // Two external `defer` scripts plus an inline script. Per HTML §4.12.1
        // the inline (non-deferred) script runs first, then the deferred ones
        // in document order. Each appends its tag to #log so the final string
        // pins the relative ordering: inline, then defer-1, then defer-2.
        await using var dir = TempDir.Create();
        dir.WriteFile("defer-a.js", "log('defer-1');");
        dir.WriteFile("defer-b.js", "log('defer-2');");
        var html = @"<!doctype html><html><body>
            <p id='log'></p>
            <script>
                function log(tag) {
                    var p = document.getElementById('log');
                    p.textContent = p.textContent + tag + ' ';
                }
            </script>
            <script defer src='defer-a.js'></script>
            <script defer src='defer-b.js'></script>
            <script>log('inline');</script>
        </body></html>";

        var outcome = await RenderHtmlAsync(html, dir);
        // Inline scripts (the two non-deferred ones) run in document order
        // first; the deferred externals run afterward, still in document order.
        outcome.DisplayText.Should().Contain("inline defer-1 defer-2");
    }

    [TestMethod]
    public async Task Async_script_executes_during_render()
    {
        // An external `async` script runs as soon as it is fetched
        // (order-independent). Pin only that its side effect is observed.
        await using var dir = TempDir.Create();
        dir.WriteFile("async.js",
            "document.getElementById('out').textContent = 'async-ran';");
        var html = @"<!doctype html><html><body>
            <p id='out'>before-async</p>
            <script async src='async.js'></script>
        </body></html>";

        var outcome = await RenderHtmlAsync(html, dir);
        outcome.DisplayText.Should().Contain("async-ran");
        outcome.DisplayText.Should().NotContain("before-async");
    }

    [TestMethod]
    public async Task Inline_script_injecting_a_script_element_runs_the_injected_script()
    {
        // The page's inline script creates a <script> via createElement, sets
        // its inline source, and appends it to the DOM. The engine's
        // runtime-injection hook must fetch/compile/run it through the same
        // path, so the injected script's mutation is visible after render.
        var html = @"<!doctype html><html><body>
            <p id='out'>not-injected</p>
            <script>
                var s = document.createElement('script');
                s.textContent = ""document.getElementById('out').textContent = 'injected-ran';"";
                document.body.appendChild(s);
            </script>
        </body></html>";

        var outcome = await RenderHtmlAsync(html);
        outcome.DisplayText.Should().Contain("injected-ran");
        outcome.DisplayText.Should().NotContain("not-injected");
    }

    // -------------------------------------------------------------------
    // Dynamic <script src=…> — HTML §4.12.1 "prepare a script"
    // -------------------------------------------------------------------

    [Spec("html", "https://html.spec.whatwg.org/multipage/scripting.html#prepare-the-script-element", "4.12.1 prepare a script")]
    [SpecFact]
    public async Task Setting_src_on_deferred_script_fetches_and_runs_it()
    {
        // The seed repro: a loader runs on DOMContentLoaded and copies a custom
        // data-* attribute onto src for each <script>. Setting src on a
        // parser-created empty <script> must run "prepare a script": fetch the
        // (data:) URL and execute it. Without the fix the deferred script never
        // runs and we'd see FALLBACK_NOT_LOADED.
        var html = @"<!doctype html><html><head>
<script>
  window.handlePageReady = function() {
    var scripts = document.getElementsByTagName('script');
    for (var i=0;i<scripts.length;i++){
      var s=scripts[i];
      if (s.getAttribute('data-deferred-src')) s.setAttribute('src', s.getAttribute('data-deferred-src'));
    }
  };
  window.addEventListener('DOMContentLoaded', window.handlePageReady);
</script></head><body>
<p id=""status"">FALLBACK_NOT_LOADED</p>
<script data-deferred-src=""data:text/javascript,document.getElementById('status').textContent='DEFERRED_RAN'""></script>
</body></html>";

        var outcome = await RenderHtmlAsync(html);
        outcome.DisplayText.Should().Contain("DEFERRED_RAN");
        outcome.DisplayText.Should().NotContain("FALLBACK_NOT_LOADED");
    }

    [Spec("html", "https://html.spec.whatwg.org/multipage/scripting.html#prepare-the-script-element", "4.12.1 prepare a script")]
    [SpecFact]
    public async Task Setting_src_via_idl_property_fetches_and_runs_it()
    {
        // The .src IDL setter must behave the same as setAttribute('src', …).
        var html = @"<!doctype html><html><head>
<script>
  window.addEventListener('DOMContentLoaded', function() {
    var s = document.getElementById('boot');
    s.src = ""data:text/javascript,document.getElementById('status').textContent='IDL_SRC_RAN'"";
  });
</script></head><body>
<p id=""status"">IDLE</p>
<script id=""boot""></script>
</body></html>";

        var outcome = await RenderHtmlAsync(html);
        outcome.DisplayText.Should().Contain("IDL_SRC_RAN");
        outcome.DisplayText.Should().NotContain("IDLE");
    }

    [Spec("html", "https://html.spec.whatwg.org/multipage/scripting.html#prepare-the-script-element", "4.12.1 prepare a script")]
    [SpecFact]
    public async Task Load_event_chains_a_second_dynamic_script()
    {
        // Sequential loaders set src on script #2 only from script #1's load
        // handler. This pins that (a) the load event fires after the
        // src-triggered fetch+execute settles, and (b) the chained script then
        // runs too. Without the load event only the first bundle would load.
        var html = @"<!doctype html><html><head>
<script>
  window.addEventListener('DOMContentLoaded', function() {
    var a = document.getElementById('a');
    var b = document.getElementById('b');
    a.addEventListener('load', function() {
      b.src = ""data:text/javascript,document.getElementById('status').textContent += '_B'"";
    });
    a.src = ""data:text/javascript,document.getElementById('status').textContent='A'"";
  });
</script></head><body>
<p id=""status"">none</p>
<script id=""a""></script>
<script id=""b""></script>
</body></html>";

        var outcome = await RenderHtmlAsync(html);
        outcome.DisplayText.Should().Contain("A_B");
    }

    [Spec("html", "https://html.spec.whatwg.org/multipage/scripting.html#prepare-the-script-element", "4.12.1 prepare a script")]
    [SpecFact]
    public async Task Error_event_fires_when_dynamic_script_fetch_fails()
    {
        // A src pointing at a missing file fires `error`, not `load`. The error
        // handler's mutation must land in the rendered text.
        var html = @"<!doctype html><html><head>
<script>
  window.addEventListener('DOMContentLoaded', function() {
    var s = document.getElementById('boot');
    s.addEventListener('load', function() {
      document.getElementById('status').textContent = 'LOADED';
    });
    s.addEventListener('error', function() {
      document.getElementById('status').textContent = 'ERRORED';
    });
    s.src = 'file:///nonexistent/path/to/missing-bundle.js';
  });
</script></head><body>
<p id=""status"">PENDING</p>
<script id=""boot""></script>
</body></html>";

        var outcome = await RenderHtmlAsync(html);
        outcome.DisplayText.Should().Contain("ERRORED");
        outcome.DisplayText.Should().NotContain("LOADED");
    }

    [Spec("html", "https://html.spec.whatwg.org/multipage/scripting.html#prepare-the-script-element", "4.12.1 prepare a script")]
    [SpecFact]
    public async Task Sequential_network_bundles_chain_to_quiescence()
    {
        // Real deferred loaders fetch bundles over the network and chain off
        // `load`. Three HTTP bundles, each setting src on the next from its own
        // load handler — only all three settle if the pump waits on in-flight
        // dynamic-script fetches and re-pumps after each completes.
        await using var server = await BundleServer.StartAsync(new()
        {
            ["/b1.js"] = "document.getElementById('log').textContent += '1';",
            ["/b2.js"] = "document.getElementById('log').textContent += '2';",
            ["/b3.js"] = "document.getElementById('log').textContent += '3';",
        });

        var html = $@"<!doctype html><html><head>
<script>
  window.addEventListener('DOMContentLoaded', function() {{
    var s1 = document.getElementById('s1');
    var s2 = document.getElementById('s2');
    var s3 = document.getElementById('s3');
    s1.addEventListener('load', function() {{ s2.src = '{server.BaseUrl}/b2.js'; }});
    s2.addEventListener('load', function() {{ s3.src = '{server.BaseUrl}/b3.js'; }});
    s1.src = '{server.BaseUrl}/b1.js';
  }});
</script></head><body>
<p id=""log"">L:</p>
<script id=""s1""></script>
<script id=""s2""></script>
<script id=""s3""></script>
</body></html>";

        var outcome = await RenderHtmlAsync(html);
        outcome.DisplayText.Should().Contain("L:123");
    }

    [TestMethod]
    public async Task Infinite_raf_loop_does_not_block_settle_on_the_wall_clock_cap()
    {
        // A self-rescheduling requestAnimationFrame loop never reports idle. The
        // load pump must give it a few frames and then settle — handing the
        // ongoing animation to the live phase — instead of spinning until the
        // wall-clock cap (which made every animated page take seconds to "open").
        // Without the rAF frame budget the pump advances one frame per ~50ms of
        // simulated time for the full real cap (twice), running hundreds-plus of
        // frames; the bounded pump runs only a handful, so the frame count is the
        // observable proof that the loop was capped rather than pumped to the cap.
        var html = @"<!doctype html><html><head><script>
  var n = 0;
  function tick() {
    n++;
    document.getElementById('out').textContent = 'frames=' + n;
    requestAnimationFrame(tick);
  }
  requestAnimationFrame(tick);
</script></head><body><p id=""out"">frames=0</p></body></html>";

        var outcome = await RenderHtmlAsync(html);

        // rAF ran during load — bootstrap frames are honored, not skipped.
        var match = System.Text.RegularExpressions.Regex.Match(outcome.DisplayText, @"frames=(\d+)");
        match.Success.Should().BeTrue();
        var frames = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        frames.Should().BeGreaterThan(0);
        // ...but the loop was bounded. An unbounded pump runs far more than this
        // before the 8s cap; the budget keeps it to a small handful per pump pass.
        frames.Should().BeLessThan(100);
    }

    [TestMethod]
    public async Task Raf_loop_with_intersection_observer_still_settles_on_the_frame_budget()
    {
        // Regression: an animated page (self-rescheduling rAF) that ALSO uses an
        // IntersectionObserver must still hit the rAF-frame-budget early-exit. The
        // IO "pump front" reports pending until its first delivery; if it is drained
        // only BELOW the rAF front, the rAF loop (which never reports idle) starves
        // it forever, so HasPending stays true, OnlyAnimationFramePending stays
        // false, the budget exit never fires, and the load burns the full 8s cap at
        // full CPU. The IO front runs above the rAF front so it converges, then the
        // bounded rAF budget settles the page (frames stays small, not ~160 = cap).
        var html = @"<!doctype html><html><head><script>
  var n = 0;
  function tick() {
    n++;
    document.getElementById('out').textContent = 'frames=' + n;
    requestAnimationFrame(tick);
  }
  requestAnimationFrame(tick);
  var io = new IntersectionObserver(function () {});
  io.observe(document.getElementById('out'));
</script></head><body><p id=""out"">frames=0</p></body></html>";

        var outcome = await RenderHtmlAsync(html);

        var match = System.Text.RegularExpressions.Regex.Match(outcome.DisplayText, @"frames=(\d+)");
        match.Success.Should().BeTrue();
        var frames = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        frames.Should().BeGreaterThan(0);
        // Bounded by the frame budget, not run to the wall-clock cap.
        frames.Should().BeLessThan(100);
    }

    [Spec("html", "https://html.spec.whatwg.org/multipage/scripting.html#prepare-the-script-element", "4.12.1 prepare a script")]
    [SpecFact]
    public async Task Already_run_external_script_does_not_rerun_when_src_reassigned()
    {
        // A parser-discovered external script that already ran must not re-run
        // when its src is reassigned to the same kind of resource. We count
        // executions via a window counter; reassigning src should not increment
        // it again (the "already started" flag).
        await using var server = await BundleServer.StartAsync(new()
        {
            ["/once.js"] = "window.__runs = (window.__runs||0)+1;",
        });

        var html = $@"<!doctype html><html><head>
<script src=""{server.BaseUrl}/once.js""></script>
<script>
  window.addEventListener('DOMContentLoaded', function() {{
    var scripts = document.getElementsByTagName('script');
    // Reassign src on the already-run external script.
    scripts[0].src = '{server.BaseUrl}/once.js';
    document.getElementById('out').textContent = 'runs=' + (window.__runs||0);
  }});
</script></head><body>
<p id=""out"">?</p>
</body></html>";

        var outcome = await RenderHtmlAsync(html);
        outcome.DisplayText.Should().Contain("runs=1");
    }

    [TestMethod]
    public async Task Injected_async_external_script_runs_via_dynamic_runner()
    {
        // GA/gtag pattern: an inline script createElements a <script>, sets
        // async + src, and appends it. Per HTML §4.12.1 a script-inserted
        // external script defaults to async, so the engine routes it to the
        // dynamic-script pump (deferred phase) instead of running it inline on
        // insertion. On the headless path the pump still drains before paint, so
        // its DOM mutation is visible in the final render.
        await using var server = await BundleServer.StartAsync(new()
        {
            ["/ga.js"] = "document.getElementById('status').textContent='GA_RAN';",
        });
        var html = $@"<!doctype html><html><head>
<script>
  (function(){{
    var el = document.createElement('script');
    el.async = true;
    el.src = '{server.BaseUrl}/ga.js';
    document.head.appendChild(el);
  }})();
</script></head><body>
<p id=""status"">GA_PENDING</p>
</body></html>";
        var outcome = await RenderHtmlAsync(html);
        outcome.DisplayText.Should().Contain("GA_RAN");
        outcome.DisplayText.Should().NotContain("GA_PENDING");
    }

    [TestMethod]
    public async Task Progressive_layout_first_paints_then_reflows_after_deferred_dom_change()
    {
        await using var server = await BundleServer.StartAsync(new()
        {
            // Deferred (injected async external) script mutates the DOM.
            ["/defer.js"] = "document.getElementById('out').textContent='DEFERRED';",
        });
        var html = $@"<!doctype html><html><head>
<script>
  document.getElementById; // critical phase runs synchronously
</script></head><body>
<p id=""out"">CRITICAL</p>
<script>
  var el = document.createElement('script');
  el.async = true;
  el.src = '{server.BaseUrl}/defer.js';
  document.head.appendChild(el);
</script>
</body></html>";
        var fixture = Path.Combine(Path.GetTempPath(), $"starling-prog-{Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(fixture, html);
        try
        {
            var engine = new StarlingEngine();
            LaidOutPage? firstPaintPage = null;
            var result = await engine.LayoutPageAsync(
                "file://" + fixture.Replace('\\', '/'),
                DefaultOptions,
                CancellationToken.None,
                onFirstPaint: p => firstPaintPage = p);

            result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");
            firstPaintPage.Should().NotBeNull("onFirstPaint must fire for a scripted page");
            // The deferred script mutated the DOM, so the returned page is a
            // successor distinct from the first-paint page.
            ReferenceEquals(result.Value, firstPaintPage).Should().BeFalse();
            using var page = result.Value;
            var outEl = page.Document.GetElementById("out");
            outEl!.TextContent.Should().Be("DEFERRED");
            // Clean up the inert first-paint page.
            firstPaintPage!.Dispose();
        }
        finally
        {
            if (File.Exists(fixture))
            {
                File.Delete(fixture);
            }
        }
    }

    [TestMethod]
    public async Task Progressive_layout_first_paint_does_not_wait_for_defer_script_fetch()
    {
        using var server = new ConcurrencyTrackingServer(new()
        {
            ["/defer.js"] = new Route(
                "application/javascript",
                "document.getElementById('out').textContent='DEFERRED';",
                DelayMs: 1500),
        });
        var html = $@"<!doctype html><html><head></head><body>
<p id=""out"">START</p>
<script>document.getElementById('out').textContent='CRITICAL';</script>
<script defer src=""http://127.0.0.1:{server.Port}/defer.js""></script>
</body></html>";
        var fixture = Path.Combine(Path.GetTempPath(), $"starling-prog-{Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(fixture, html);
        try
        {
            var engine = new StarlingEngine();
            LaidOutPage? firstPaintPage = null;
            string? firstPaintText = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long firstPaintMs = -1;

            var result = await engine.LayoutPageAsync(
                "file://" + fixture.Replace('\\', '/'),
                DefaultOptions,
                CancellationToken.None,
                onFirstPaint: p =>
                {
                    firstPaintMs = sw.ElapsedMilliseconds;
                    firstPaintPage = p;
                    firstPaintText = p.Document.GetElementById("out")?.TextContent;
                });

            result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");
            firstPaintPage.Should().NotBeNull("onFirstPaint must fire before deferred scripts settle");
            firstPaintText.Should().Be("CRITICAL");
            firstPaintMs.Should().BeLessThan(1000);
            ReferenceEquals(result.Value, firstPaintPage).Should().BeFalse();
            result.Value.Document.GetElementById("out")!.TextContent.Should().Be("DEFERRED");
            using var page = result.Value;
            firstPaintPage!.Dispose();
        }
        finally
        {
            if (File.Exists(fixture))
            {
                File.Delete(fixture);
            }
        }
    }

    [TestMethod]
    public async Task Progressive_layout_returns_same_page_when_deferred_changes_nothing()
    {
        // The analytics-only common case: deferred scripts fire beacons but do
        // not mutate the DOM, so the engine returns the very page handed to
        // onFirstPaint (no successor reflow). The GUI relies on this reference
        // identity to skip a redundant re-show.
        var html = @"<!doctype html><html><head>
<script>window.__x = 1; // critical, no DOM change</script>
</head><body>
<p id=""out"">STABLE</p>
<script>
  // Injected async script that does NOT touch the DOM.
  var el = document.createElement('script');
  el.async = true;
  el.src = ""data:text/javascript,window.__beacon = true;"";
  document.head.appendChild(el);
</script>
</body></html>";
        var fixture = Path.Combine(Path.GetTempPath(), $"starling-prog-{Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(fixture, html);
        try
        {
            var engine = new StarlingEngine();
            LaidOutPage? firstPaintPage = null;
            var result = await engine.LayoutPageAsync(
                "file://" + fixture.Replace('\\', '/'),
                DefaultOptions,
                CancellationToken.None,
                onFirstPaint: p => firstPaintPage = p);

            result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");
            firstPaintPage.Should().NotBeNull();
            // No DOM mutation in the deferred phase → same page instance back.
            ReferenceEquals(result.Value, firstPaintPage).Should().BeTrue();
            result.Value.Dispose();
        }
        finally
        {
            if (File.Exists(fixture))
            {
                File.Delete(fixture);
            }
        }
    }

    [TestMethod]
    public async Task Disposing_live_page_fires_beforeunload_then_unload()
    {
        var html = @"<!doctype html><html><body>
<p id=""out""></p>
<script>
  window.addEventListener('beforeunload', function () {
    document.getElementById('out').textContent += 'before';
  });
  window.addEventListener('unload', function () {
    document.getElementById('out').textContent += '-unload';
  });
</script>
</body></html>";
        var fixture = Path.Combine(Path.GetTempPath(), $"starling-life-{Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(fixture, html);
        try
        {
            var engine = new StarlingEngine();
            var result = await engine.LayoutPageAsync(
                "file://" + fixture.Replace('\\', '/'),
                DefaultOptions,
                CancellationToken.None,
                onFirstPaint: _ => { });

            result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");
            var page = result.Value;
            var outEl = page.Document.GetElementById("out");
            page.Dispose();
            outEl!.TextContent.Should().Be("before-unload");
        }
        finally
        {
            if (File.Exists(fixture))
            {
                File.Delete(fixture);
            }
        }
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private static Task<RenderOutcome> RenderHtmlAsync(string html) => RenderHtmlAsync(html, dir: null);

    private static async Task<RenderOutcome> RenderHtmlAsync(string html, TempDir? dir)
    {
        // Place the HTML inside the companion dir (when supplied) so relative
        // <script src> attributes resolve against the sibling .js files.
        var baseDir = dir?.Path ?? Path.GetTempPath();
        var tempHtml = Path.Combine(baseDir, $"starling-js-{Guid.NewGuid():N}.html");
        var tempPng = Path.Combine(Path.GetTempPath(), $"starling-js-{Guid.NewGuid():N}.png");
        await File.WriteAllTextAsync(tempHtml, html, CancellationToken.None);
        try
        {
            var engine = new StarlingEngine();
            var url = new Uri(tempHtml).AbsoluteUri;
            var result = await engine.RenderAsync(url, DefaultOptions, tempPng,
                CancellationToken.None);
            result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");
            return result.Value;
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

    /// <summary>A throwaway directory for tests that need companion files
    /// (e.g. external <c>&lt;script src&gt;</c> resources) resolvable via
    /// <c>file://</c> relative URLs. Deleted on dispose.</summary>
    private sealed class TempDir : IAsyncDisposable
    {
        public string Path { get; }

        private TempDir(string path) => Path = path;

        public static TempDir Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"starling-js-dir-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDir(path);
        }

        public void WriteFile(string name, string contents)
            => File.WriteAllText(System.IO.Path.Combine(Path, name), contents);

        public ValueTask DisposeAsync()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best-effort */ }
            return ValueTask.CompletedTask;
        }
    }

    private static int FindFreePort()
    {
        var sock = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        sock.Start();
        var port = ((IPEndPoint)sock.LocalEndpoint).Port;
        sock.Stop();
        return port;
    }

    /// <summary>HttpListener-backed JS file server: maps absolute paths to
    /// JavaScript source, returned as <c>text/javascript</c>. Used by the
    /// sequential-bundle quiescence tests.</summary>
    private sealed class BundleServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly IReadOnlyDictionary<string, string> _routes;
        public string BaseUrl { get; }

        private BundleServer(HttpListener listener, string baseUrl, IReadOnlyDictionary<string, string> routes)
        {
            _listener = listener;
            BaseUrl = baseUrl;
            _routes = routes;
            _loop = Task.Run(LoopAsync);
        }

        public static Task<BundleServer> StartAsync(Dictionary<string, string> routes)
        {
            var prefix = $"http://127.0.0.1:{FindFreePort()}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();
            return Task.FromResult(new BundleServer(listener, prefix.TrimEnd('/'), routes));
        }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch { return; }
                _ = Task.Run(() =>
                {
                    try
                    {
                        var path = ctx.Request.Url?.AbsolutePath ?? "";
                        if (_routes.TryGetValue(path, out var js))
                        {
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "text/javascript";
                            var bytes = Encoding.UTF8.GetBytes(js);
                            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                        }
                        else
                        {
                            ctx.Response.StatusCode = 404;
                        }
                    }
                    catch { /* swallow */ }
                    finally
                    {
                        try { ctx.Response.OutputStream.Close(); } catch { }
                        try { ctx.Response.Close(); } catch { }
                    }
                });
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            try { await _loop.ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>HttpListener-backed JSON endpoint shared by the fetch test.</summary>
    private sealed class JsonResultsServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        public string BaseUrl { get; }

        private JsonResultsServer(HttpListener listener, string baseUrl, string json)
        {
            _listener = listener;
            BaseUrl = baseUrl;
            _loop = Task.Run(() => LoopAsync(json));
        }

        public static Task<JsonResultsServer> StartAsync(string json)
        {
            var prefix = $"http://127.0.0.1:{FindFreePort()}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();
            return Task.FromResult(new JsonResultsServer(listener, prefix.TrimEnd('/'), json));
        }

        private async Task LoopAsync(string json)
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch { return; }
                _ = Task.Run(() =>
                {
                    try
                    {
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "application/json";
                        var bytes = Encoding.UTF8.GetBytes(json);
                        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    }
                    catch { /* swallow */ }
                    finally
                    {
                        try { ctx.Response.OutputStream.Close(); } catch { }
                        try { ctx.Response.Close(); } catch { }
                    }
                });
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            try { await _loop.ConfigureAwait(false); } catch { }
        }
    }
}
