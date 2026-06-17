using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;

namespace Starling.Engine.Tests;

/// <summary>
/// A page's <c>console.*</c> level must survive into the engine's logging so
/// DevTools' ConsolePanel can colour errors and have its level-filter pills
/// match. The engine maps each <c>ConsoleLevel</c> to the matching
/// <see cref="LogLevel"/> (error→Error, warn→Warning, debug/trace→Debug, the rest
/// →Information) and logs under the "Starling.engine.js" category.
/// </summary>
[TestClass]
public sealed class EngineConsoleLevelTests
{
    [TestMethod]
    public async Task Console_levels_map_to_matching_log_levels()
    {
        var html = """
            <!doctype html><html><body><script>
              console.log('a log');
              console.info('an info');
              console.warn('a warn');
              console.error('an error');
              console.debug('a debug');
              console.trace('a trace');
            </script></body></html>
            """;

        var (rec, _) = await RenderAsync(html);

        var jsLogs = rec.Entries
            .Where(e => e.Category == "Starling.engine.js")
            .ToList();

        LogLevel LevelOf(string needle) =>
            jsLogs.Single(l => l.Message.Contains(needle, StringComparison.Ordinal)).Level;

        LevelOf("a log").Should().Be(LogLevel.Information);
        LevelOf("an info").Should().Be(LogLevel.Information);
        LevelOf("a warn").Should().Be(LogLevel.Warning);
        LevelOf("an error").Should().Be(LogLevel.Error);
        LevelOf("a debug").Should().Be(LogLevel.Debug);
        LevelOf("a trace").Should().Be(LogLevel.Debug);
    }

    private static async Task<(RecordingLoggerFactory Rec, RenderOutcome Outcome)> RenderAsync(string html)
    {
        var tempHtml = Path.Combine(Path.GetTempPath(), $"starling-console-{Guid.NewGuid():N}.html");
        var tempPng = Path.Combine(Path.GetTempPath(), $"starling-console-{Guid.NewGuid():N}.png");
        await File.WriteAllTextAsync(tempHtml, html, CancellationToken.None);
        var rec = new RecordingLoggerFactory();
        try
        {
            var engine = new StarlingEngine(loggerFactory: rec);
            var url = new Uri(tempHtml).AbsoluteUri;
            var result = await engine.RenderAsync(
                url, new RenderOptions(new Size(800, 600), 16f), tempPng, CancellationToken.None);
            result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");
            return (rec, result.Value);
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
    /// Minimal <see cref="ILoggerFactory"/> that records every log entry so tests
    /// can assert on category, level, and message. JS console output lands under
    /// category "Starling.engine.js".
    /// </summary>
    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public readonly List<(string Category, LogLevel Level, string Message)> Entries = new();

        public ILogger CreateLogger(string categoryName) => new Rec(this, categoryName);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class Rec(RecordingLoggerFactory o, string cat) : ILogger
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
}
