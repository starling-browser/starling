using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using Starling.Engine;
using Starling.Html.Tokenizer;
using Starling.Mcp;
using Starling.Mcp.Telemetry;
using Starling.Telemetry;

namespace Starling.Headless;

/// <summary>
/// Agent-friendly CLI per browser-plan/02_PROJECT_SETUP.md §Headless CLI shape.
///
/// Static rendering and tokenizer inspection are implemented; <c>parse</c>,
/// <c>style</c>, <c>layout</c>, and <c>js</c> still print a "not yet" message
/// and return exit code 2 (per Unix convention: misuse of a builtin / not-yet).
/// </summary>
internal static class Program
{
    private static ILoggerFactory s_loggerFactory = NullLoggerFactory.Instance;

    public static async Task<int> Main(string[] args)
    {
        // Wire OpenTelemetry before we do anything observable. When launched by
        // Aspire, OTEL_EXPORTER_OTLP_ENDPOINT is set and traces/metrics/logs
        // flow to the dashboard. Direct runs keep console diagnostics only
        // unless trace output or MCP telemetry is enabled.
        //
        // When STARLING_HEADLESS_MCP_URL is set we also build the in-memory
        // ring-buffer sinks so the MCP server can hand
        // spans/logs/metrics back to driving agents.
        // If STARLING_TELEMETRY_DAEMON is set, route the OpenTelemetry Protocol
        // exporter to the standalone daemon.
        OtelBootstrap.ConfigureDaemonExportFromEnv();

        var mcpUrl = ResolveMcpUrl();
        var withInMemorySinks = mcpUrl is not null;
        using var telemetry = OtelBootstrap.Initialize("starling-headless", withInMemorySinks);
        // The logger factory from Initialize already writes to stderr (console
        // provider) and, when OTEL_EXPORTER_OTLP_ENDPOINT is set, exports log
        // records over the OpenTelemetry Protocol. STARLING_DIAG_TRACE=1 lowers
        // the console floor to Trace so paint span timings appear on stderr;
        // default stays Info to keep normal CLI runs quiet.
        s_loggerFactory = telemetry.LoggerFactory;

        var mcp = mcpUrl is not null && telemetry.TelemetryStream is not null
            ? StartMcpServer(mcpUrl, telemetry.TelemetryStream)
            : null;
        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 0;
            }

            var sub = args[0];
            var rest = args[1..];

            var exitCode = sub switch
            {
                "render" => await Render(rest),
                "tokenize" => Tokenize(rest),
                "parse" or "style" or "layout" or "js"
                    => StubSubcommand(sub),
                "-h" or "--help" or "help" => UsageOk(),
                _ => UnknownSubcommand(sub),
            };

            // With the MCP enabled, hold the process open
            // after the subcommand finishes so a driving agent can inspect
            // post-mortem telemetry. Exits on Ctrl+C with the subcommand's
            // return code preserved.
            if (mcp is not null)
            {
                WaitForShutdownSignal(mcp.Endpoint);
            }

            return exitCode;
        }
        finally
        {
            if (mcp is not null)
            {
                mcp.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }

    private static Uri? ResolveMcpUrl()
    {
        var raw = Environment.GetEnvironmentVariable("STARLING_HEADLESS_MCP_URL");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            Console.Error.WriteLine(
                $"warning: STARLING_HEADLESS_MCP_URL is not a valid absolute URL ('{raw}'); MCP disabled.");
            return null;
        }
        return uri;
    }

    private static StarlingMcpServer? StartMcpServer(Uri endpoint, TelemetryStream telemetry)
    {
        try
        {
            var server = new StarlingMcpServer(
                endpoint: endpoint,
                toolGroups: [new TelemetryTools(telemetry)],
                resourceProviders: [new TelemetryResources(telemetry)],
                serverName: "starling-headless",
                serverTitle: "Starling Headless");
            server.StartAsync().GetAwaiter().GetResult();
            Console.Error.WriteLine($"[mcp] listening on {server.Endpoint}");
            return server;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: MCP server failed to start: {ex.Message}");
            return null;
        }
    }

    private static void WaitForShutdownSignal(Uri endpoint)
    {
        Console.Error.WriteLine(
            $"[mcp] subcommand finished — server still listening on {endpoint}. " +
            "Press Ctrl+C to exit.");
        using var done = new ManualResetEventSlim(false);
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            done.Set();
        };
        Console.CancelKeyPress += handler;
        try
        {
            done.Wait();
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    /// <summary>
    /// Dumps the WHATWG HTML tokenizer's output for the given file. Useful for
    /// debugging tokenizer state output.
    /// </summary>
    private static int Tokenize(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("error: `tokenize` requires a file path.");
            Console.Error.WriteLine("usage: starling tokenize <file>");
            return 1;
        }

        var path = args[0];
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"error: cannot read '{path}': {ex.Message}");
            return 1;
        }

        var sink = new ConsoleParseErrorSink();
        var t = new HtmlTokenizer(sink);
        t.Feed(text);
        t.EndOfInput();

        Console.WriteLine($"tokenizing {path} ({text.Length} bytes)");
        Console.WriteLine(new string('-', 60));

        var n = 0;
        while (true)
        {
            HtmlToken? tok;
            try { tok = t.ReadToken(); }
            catch (NotImplementedException ex)
            {
                Console.WriteLine($"… tokenizer reached an unimplemented state: {ex.Message}");
                Console.WriteLine("   (this file exercises tokenizer states that are not implemented yet)");
                break;
            }
            if (tok is null)
            {
                break;
            }

            Console.WriteLine($"{++n,4}: {FormatToken(tok)}");
            if (tok is EndOfFileToken)
            {
                break;
            }
        }

        if (sink.Count > 0)
        {
            Console.WriteLine(new string('-', 60));
            Console.WriteLine($"{sink.Count} parse error(s) reported.");
        }
        return 0;
    }

    private static string FormatToken(HtmlToken tok) => tok switch
    {
        CharacterToken { CodePoint: var cp } => cp switch
        {
            '\n' => "Character('\\n')",
            '\r' => "Character('\\r')",
            '\t' => "Character('\\t')",
            < 32 or 0x7F => $"Character(U+{cp:X4})",
            _ => $"Character('{(char)cp}')",
        },
        StartTagToken st => $"StartTag <{st.Name}{FormatAttrs(st.Attributes)}{(st.SelfClosing ? "/" : "")}>",
        EndTagToken et => $"EndTag </{et.Name}>",
        CommentToken c => $"Comment(\"{c.Data.Replace("\\", "\\\\").Replace("\"", "\\\"")}\")",
        DoctypeToken d => $"Doctype(name={d.Name ?? "(null)"})",
        EndOfFileToken => "EOF",
        _ => tok.ToString() ?? "(unknown)",
    };

    private static string FormatAttrs(IReadOnlyList<HtmlAttribute> attrs)
    {
        if (attrs.Count == 0)
        {
            return "";
        }

        var parts = attrs.Select(a => $" {a.Name}=\"{a.Value}\"");
        return string.Concat(parts);
    }

    /// <summary>Parse-error sink that prints to stderr. Useful in the CLI.</summary>
    private sealed class ConsoleParseErrorSink : IParseErrorSink
    {
        public int Count { get; private set; }

        public void Report(HtmlParseError code, int line, int column)
        {
            Count++;
            Console.Error.WriteLine($"  parse error at {line}:{column} — {code}");
        }
    }

    private static async Task<int> Render(string[] args, CancellationToken ct = default)
    {
        // starling render <url-or-file> [-o out.png] [--viewport WxH]
        if (args.Length == 0)
        {
            Console.Error.WriteLine("error: `render` requires a URL or file path.");
            Console.Error.WriteLine("usage: starling render <url-or-file> [-o out.png] [--viewport WxH] [--font-size N] [--dump-layout]");
            return 1;
        }

        var input = args[0];
        var output = "out.png";
        var viewport = new Size(800, 600);
        var fontSize = 32f;
        var frames = 1;
        var frameStepMs = 16L;
        var dumpLayout = false;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                case "--dump-layout":
                    dumpLayout = true;
                    break;
                case "--viewport" when i + 1 < args.Length:
                    if (!TryParseViewport(args[++i], out viewport))
                    {
                        Console.Error.WriteLine($"error: invalid --viewport '{args[i]}'. Use WxH, e.g. 1024x768.");
                        return 1;
                    }
                    break;
                case "--font-size" when i + 1 < args.Length:
                    if (!float.TryParse(args[++i], System.Globalization.CultureInfo.InvariantCulture, out fontSize)
                        || fontSize <= 0)
                    {
                        Console.Error.WriteLine($"error: invalid --font-size '{args[i]}'.");
                        return 1;
                    }
                    break;
                case "--frames" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], System.Globalization.CultureInfo.InvariantCulture, out frames)
                        || frames < 1)
                    {
                        Console.Error.WriteLine($"error: invalid --frames '{args[i]}' (must be >= 1).");
                        return 1;
                    }
                    break;
                case "--frame-step" when i + 1 < args.Length:
                    if (!TryParseFrameStep(args[++i], out frameStepMs))
                    {
                        Console.Error.WriteLine($"error: invalid --frame-step '{args[i]}' (expected e.g. '16ms' or '16').");
                        return 1;
                    }
                    break;
                default:
                    Console.Error.WriteLine($"error: unknown render option '{args[i]}'.");
                    return 1;
            }
        }

        // Allow bare paths in addition to file:// URLs — agent ergonomics.
        var url = NormalizeUrlOrPath(input);

        var engine = new StarlingEngine(loggerFactory: s_loggerFactory);

        if (frames > 1)
        {
            return await RenderFrameSequence(engine, url, new RenderOptions(viewport, fontSize), output, frames, frameStepMs, ct);
        }

        var result = await engine.LayoutPageAsync(url, new RenderOptions(viewport, fontSize), ct);
        return result.Match(
            page =>
            {
                using (page)
                {
                    if (dumpLayout)
                    {
                        DumpLayout(page.Root, 0);
                    }

                    try
                    {
                        var ok = engine.CaptureToPngGpu(page, output, nowMs: 0, fullPage: false);
                        Console.WriteLine($"rendered {ok.OutputPath} ({ok.Width}x{ok.Height})");
                        return 0;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"error: GPU render failed: {ex.Message}");
                        return 1;
                    }
                }
            },
            err =>
            {
                Console.Error.WriteLine($"error: {err.Message}");
                return 1;
            });
    }

    private static async Task<int> RenderFrameSequence(
        StarlingEngine engine, string url, RenderOptions options, string outputTemplate,
        int frames, long frameStepMs, CancellationToken ct = default)
    {
        // Lay the page out once, then ask the engine to repaint at evenly
        // spaced frame timestamps. The animation/transition engines on the
        // retained LaidOutPage carry their state forward between calls so
        // CSS animations sample correctly across the sequence.
        var layoutTask = engine.LayoutPageAsync(url, options, ct);
        var layoutResult = await layoutTask;
        return layoutResult.Match(
            page =>
            {
                using (page)
                {
                    var width = 0; var height = 0;
                    var pad = frames.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
                    var dir = Path.GetDirectoryName(outputTemplate);
                    var stem = Path.GetFileNameWithoutExtension(outputTemplate);
                    var ext = Path.GetExtension(outputTemplate);
                    if (string.IsNullOrEmpty(ext))
                    {
                        ext = ".png";
                    }

                    try
                    {
                        using var renderer = engine.CreateCompositedRenderer(page);

                        for (var i = 0; i < frames; i++)
                        {
                            var nowMs = i * frameStepMs;
                            var bitmap = engine.RenderFrameGpu(page, nowMs, renderer);
                            try
                            {
                                var name = $"{stem}{i.ToString("D" + pad, System.Globalization.CultureInfo.InvariantCulture)}{ext}";
                                var path = string.IsNullOrEmpty(dir) ? name : Path.Combine(dir, name);
                                if (!string.IsNullOrEmpty(dir))
                                {
                                    Directory.CreateDirectory(dir);
                                }

                                using var image = Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(
                                    bitmap.Rgba, bitmap.Width, bitmap.Height);
                                image.SaveAsPng(path);
                                width = bitmap.Width; height = bitmap.Height;
                                Console.WriteLine($"rendered {path} ({bitmap.Width}x{bitmap.Height}) @ t={nowMs}ms");
                            }
                            finally
                            {
                                bitmap.Dispose();
                            }
                        }

                        Console.WriteLine($"wrote {frames} frames ({width}x{height}) step={frameStepMs}ms.");
                        return 0;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"error: GPU render failed: {ex.Message}");
                        return 1;
                    }
                }
            },
            err =>
            {
                Console.Error.WriteLine($"error: {err.Message}");
                return 1;
            });
    }

    private static bool TryParseFrameStep(string raw, out long ms)
    {
        ms = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var s = raw.Trim();
        if (s.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^2];
        }
        else if (s.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            if (!double.TryParse(s[..^1], System.Globalization.CultureInfo.InvariantCulture, out var sec) || sec <= 0)
            {
                return false;
            }

            ms = (long)(sec * 1000);
            return ms > 0;
        }
        if (!long.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out ms) || ms <= 0)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Pretty-prints the laid-out box tree to stderr — one indented line per box
    /// with its element tag/classes/id and computed <c>Frame</c> (x/y/w/h in the
    /// page coordinate space). Text boxes also report their line-fragment count
    /// and summed fragment width, which is the actual measured text extent
    /// (a <see cref="Starling.Layout.Box.TextBox"/>'s own <c>Frame</c> stays
    /// zero — its geometry lives on the fragments). Enabled by
    /// <c>render --dump-layout</c>; invaluable for diagnosing layout/centering
    /// bugs without a screenshot.
    /// </summary>
    private static void DumpLayout(Starling.Layout.Box.Box box, int depth)
    {
        var el = box.Element;
        var tag = el?.TagName ?? box.Kind.ToString();
        var cls = el is null ? "" : string.Join(".", el.ClassList);
        var label = string.IsNullOrEmpty(cls) ? tag : $"{tag}.{cls}";
        if (!string.IsNullOrEmpty(el?.Id))
        {
            label += $"#{el!.Id}";
        }

        var txt = "";
        if (box is Starling.Layout.Box.TextBox t)
        {
            var fw = 0d;
            foreach (var fr in t.Fragments)
            {
                fw += fr.Width;
            }

            var preview = t.Text.Length > 30 ? t.Text[..30] : t.Text;
            txt = $" \"{preview}\" frags={t.Fragments.Count} fw={fw:F0}";
        }
        var f = box.Frame;
        Console.Error.WriteLine($"{new string(' ', depth * 2)}{label}{txt}  [x={f.X:F0} y={f.Y:F0} w={f.Width:F0} h={f.Height:F0}]");
        foreach (var c in box.Children)
        {
            DumpLayout(c, depth + 1);
        }
    }

    private static int StubSubcommand(string name)
    {
        Console.Error.WriteLine(
            $"`starling {name}` is not yet implemented. See browser-plan/13_MILESTONES.md.");
        return 2;
    }

    private static int UnknownSubcommand(string sub)
    {
        Console.Error.WriteLine($"error: unknown subcommand '{sub}'.");
        PrintUsage();
        return 1;
    }

    private static int UsageOk()
    {
        PrintUsage();
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            "starling — headless browser CLI\n" +
            "\n" +
            "usage:\n" +
            "  starling render <url-or-file> [-o out.png] [--viewport WxH] [--font-size N] [--dump-layout]\n" +
            "  starling tokenize <file>      (M1; partial — Data/tag/RCDATA/RAWTEXT/PLAINTEXT)\n" +
            "  starling parse    <file>      (M1)\n" +
            "  starling style    <file>      (M1)\n" +
            "  starling layout   <file>      (M1)\n" +
            "  starling js       <file>      (M3)\n");
    }

    private static bool TryParseViewport(string s, out Size size)
    {
        size = default;
        var x = s.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        if (x <= 0 || x == s.Length - 1)
        {
            return false;
        }

        if (!int.TryParse(s[..x], out var w) || !int.TryParse(s[(x + 1)..], out var h))
        {
            return false;
        }

        if (w <= 0 || h <= 0)
        {
            return false;
        }

        size = new Size(w, h);
        return true;
    }

    private static string NormalizeUrlOrPath(string input)
    {
        if (input.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return input;
        }

        if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return input;
        }

        if (input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return input;
        }
        // Bare path → file://
        var full = Path.GetFullPath(input);
        return "file://" + (full.StartsWith('/') ? full : "/" + full.Replace('\\', '/'));
    }
}
