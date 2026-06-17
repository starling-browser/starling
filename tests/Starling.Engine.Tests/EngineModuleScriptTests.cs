using AwesomeAssertions;
using SixLabors.ImageSharp;

namespace Starling.Engine.Tests;

/// <summary>
/// Engine-level coverage for <c>&lt;script type="module"&gt;</c>: inline modules,
/// external module entry points, and a multi-file file:// import graph including
/// a cyclic-import case. Modules run deferred (after parse) and may mutate the
/// DOM, so results surface in <see cref="RenderOutcome.DisplayText"/>.
/// </summary>
[TestClass]
public sealed class EngineModuleScriptTests
{
    private static readonly RenderOptions DefaultOptions = new(new Size(800, 600), 16f);

    [TestMethod]
    public async Task Inline_module_with_relative_import_mutates_dom()
    {
        await using var fixture = new ModuleFixture();
        fixture.WriteFile("lib.js", "export const message = 'from-module';");
        var html = @"<!doctype html><html><body>
            <p id='out'>placeholder</p>
            <script type='module'>
                import { message } from './lib.js';
                document.getElementById('out').textContent = message;
            </script>
        </body></html>";

        var outcome = await fixture.RenderAsync(html);
        outcome.DisplayText.Should().Contain("from-module");
        outcome.DisplayText.Should().NotContain("placeholder");
    }

    [TestMethod]
    public async Task External_module_entry_resolves_named_imports()
    {
        await using var fixture = new ModuleFixture();
        fixture.WriteFile("math.js", "export function add(a, b){ return a + b; } export const base = 10;");
        fixture.WriteFile("entry.js", @"
            import { add, base } from './math.js';
            document.getElementById('out').textContent = 'sum=' + add(base, 5);
        ");
        var html = @"<!doctype html><html><body>
            <p id='out'>placeholder</p>
            <script type='module' src='./entry.js'></script>
        </body></html>";

        var outcome = await fixture.RenderAsync(html);
        outcome.DisplayText.Should().Contain("sum=15");
    }

    [TestMethod]
    public async Task Module_graph_with_cycle_resolves_via_file_urls()
    {
        await using var fixture = new ModuleFixture();
        // a <-> b cyclic import; function declarations cross the cycle.
        fixture.WriteFile("a.js", @"
            import { labelB } from './b.js';
            export function labelA(){ return 'A'; }
            globalThis.__cycleResult = labelB();
        ");
        fixture.WriteFile("b.js", @"
            import { labelA } from './a.js';
            export function labelB(){ return 'B+' + labelA(); }
        ");
        var html = @"<!doctype html><html><body>
            <p id='out'>placeholder</p>
            <script type='module'>
                import './a.js';
                document.getElementById('out').textContent = globalThis.__cycleResult;
            </script>
        </body></html>";

        var outcome = await fixture.RenderAsync(html);
        outcome.DisplayText.Should().Contain("B+A");
    }

    [TestMethod]
    public async Task Live_binding_across_module_boundary_is_observed()
    {
        await using var fixture = new ModuleFixture();
        fixture.WriteFile("counter.js",
            "export let count = 0; export function inc(){ count = count + 1; }");
        var html = @"<!doctype html><html><body>
            <p id='out'>placeholder</p>
            <script type='module'>
                import { count, inc } from './counter.js';
                inc(); inc(); inc();
                document.getElementById('out').textContent = 'count=' + count;
            </script>
        </body></html>";

        var outcome = await fixture.RenderAsync(html);
        outcome.DisplayText.Should().Contain("count=3");
    }

    /// <summary>Temp directory holding the HTML entry plus sibling .js modules so
    /// relative file:// imports resolve.</summary>
    private sealed class ModuleFixture : IAsyncDisposable
    {
        private readonly string _dir =
            Path.Combine(Path.GetTempPath(), $"starling-mod-{Guid.NewGuid():N}");

        public ModuleFixture() => Directory.CreateDirectory(_dir);

        public void WriteFile(string name, string content) =>
            File.WriteAllText(Path.Combine(_dir, name), content);

        public async Task<RenderOutcome> RenderAsync(string html)
        {
            var htmlPath = Path.Combine(_dir, "index.html");
            var pngPath = Path.Combine(_dir, "out.png");
            await File.WriteAllTextAsync(htmlPath, html, CancellationToken.None);

            var engine = new StarlingEngine();
            var url = new Uri(htmlPath).AbsoluteUri;
            var result = await engine.RenderAsync(url, DefaultOptions, pngPath, CancellationToken.None);
            result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");
            return result.Value;
        }

        public ValueTask DisposeAsync()
        {
            try { if (Directory.Exists(_dir))
                {
                    Directory.Delete(_dir, recursive: true);
                }
            }
            catch { /* best-effort */ }
            return ValueTask.CompletedTask;
        }
    }
}
