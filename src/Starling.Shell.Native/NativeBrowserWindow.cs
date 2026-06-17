using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SixLabors.ImageSharp;
using Starling.Common;
using Starling.Css.Cascade;
using Starling.Css.Properties;
using Starling.Css.Selectors;
using Starling.Dom;
using Starling.Dom.Events;
using Starling.Engine;
using Starling.Gui;
using Starling.Html;
using Starling.Layout.Box;
using Starling.Layout.Text;
using LayoutSize = Starling.Layout.Size;
using Starling.Gui.Core.Accessibility;
using Starling.Gui.Core.Rendering;
using Starling.Shell.Native.Mac;

namespace Starling.Shell.Native;

/// <summary>
/// Phase-3 native browser window: real navigation, scroll, hover-cursor,
/// click, and keyboard-text wired to the Starling engine via
/// <see cref="BrowserSession"/>, presented through a render session with
/// engine-rendered chrome above
/// the page in a single composited frame.
/// </summary>
internal sealed class NativeBrowserWindow : IDisposable
{
    // ── Chrome ───────────────────────────────────────────────────────────────

    // The native shell mirrors the Avalonia layout: a 232px sidebar at the left
    // and a toolbar above the page on the right.
    private const double SidebarWidthCss = 232;
    private const double ToolbarHeightCss = 58;
    private const double ChromeHeightCss = ToolbarHeightCss;
    private const double StatusBarHeightCss = 32;
    private const double ToolbarPadX = 16;
    private const double ToolbarButtonW = 34;
    private const double ToolbarButtonGap = 2;
    private const double UrlBarHeightCss = 38;
    private const double UrlFindChipW = 112;
    private const double SidebarWordmarkHeightCss = 54;
    private const double SidebarSectionHeightCss = 25;
    private const double SidebarRowHeightCss = 34;
    private const double SidebarRowGapCss = 1;
    private const double SidebarRowXCss = 10;
    private const double SidebarRowWCss = SidebarWidthCss - SidebarRowXCss * 2;
    private const string SansFont = "\"Geist\", -apple-system, BlinkMacSystemFont, \"Segoe UI\", sans-serif";
    private const string MonoFont = "\"Geist Mono\", \"SFMono-Regular\", Menlo, Consolas, monospace";

    // Chrome icons — the same 16×16 stroked SVG paths the Avalonia shell uses
    // (Starling.Gui/Chrome/Icons.cs). Rendered as inline <svg> through the
    // managed SVG rasterizer (see ChromeImageResolver) instead of Unicode glyphs.
    private const string IconBack = "M9.5 3.5 5 8l4.5 4.5";
    private const string IconFwd = "M6.5 3.5 11 8l-4.5 4.5";
    private const string IconReload = "M13 8a5 5 0 1 1-1.5-3.5M13 3v2h-2";
    private const string IconStop = "M4.5 4.5h7v7h-7z";
    private const string IconFind = "M7 12a5 5 0 1 0 0-10 5 5 0 0 0 0 10ZM14 14l-3.2-3.2";
    private const string IconBug = "M5 5.5V4a3 3 0 0 1 6 0v1.5M5 5.5h6v4a3 3 0 0 1-6 0v-4ZM3 7h2M11 7h2M3 11h2M11 11h2M8 5.5v8";
    private const string IconLock = "M4.5 7V5.5a3.5 3.5 0 0 1 7 0V7M3.5 7h9v6h-9z";
    private const string IconSun = "M8 5.25a2.75 2.75 0 1 0 0 5.5 2.75 2.75 0 0 0 0-5.5ZM8 1.5v1.5M8 13v1.5M14.5 8H13M3 8H1.5M12.6 3.4l-1.1 1.1M4.5 11.5l-1.1 1.1M12.6 12.6l-1.1-1.1M4.5 4.5l-1.1-1.1";
    private const string IconMoon = "M13.25 9.5A5 5 0 1 1 6.5 2.75a4 4 0 0 0 6.75 6.75Z";

    // An inline <svg> icon that inherits the surrounding `color` via
    // stroke="currentColor", stroked at 1.5px on a 16×16 grid like the Avalonia set.
    private static string Icon(string d, double size = 17) =>
        $"<svg width=\"{size}\" height=\"{size}\" viewBox=\"0 0 16 16\" fill=\"none\" " +
        "stroke=\"currentColor\" stroke-width=\"1.5\" stroke-linecap=\"round\" " +
        $"stroke-linejoin=\"round\"><path d=\"{d}\"/></svg>";

    // Context menu geometry (one shared definition for render + click hit-test).
    private const double MenuItemH = 26;
    private const double MenuW = 200;

    private static readonly NativeBookmark[] Bookmarks =
    [
        new("b0s", "starlingbrowser.com", "Starling Browser", "https://starlingbrowser.com/"),
        new("b0g", "localhost:8088", "SVG Logo", "http://localhost:8088/svg-logo/"),
        new("b0t", "localhost:8088", "Todo", "http://localhost:8088/todo/"),
        new("b0n", "localhost:8088", "Animations", "http://localhost:8088/animations/"),
        new("b0a", "example.com", "Example", "https://example.com"),
        new("b0b", "jsonplaceholder.typicode.com", "Todos", "https://jsonplaceholder.typicode.com/todos"),
        new("b0c", "netclaw.dev", "netclaw.dev", "https://netclaw.dev/"),
        new("b1", "google.com", "Google", "https://google.com"),
        new("b2", "localhost:8088", "Words", "http://localhost:8088/words/"),
        new("b3", "ladybird.org", "Ladybird", "https://ladybird.org/"),
        new("b4", "www.mcmaster.com", "McMaster-Carr", "https://www.mcmaster.com/"),
        new("b5", "github.com", "GitHub", "https://github.com/"),
    ];

    // ── Demo HTML written to temp files ─────────────────────────────────────

    private const string Page1Html = """
        <!DOCTYPE html>
        <html>
        <head><title>Starling Browser – Page 1</title></head>
        <body style="margin:0;font-family:sans-serif;background:#f0f4ff;padding:24px">
          <h1 style="color:#1a1a6e;font-size:36px">Starling Native Browser</h1>
          <p style="font-size:18px">Phase 3: real interaction wired to the Starling engine.</p>
          <p style="font-size:16px"><a href="PAGE2URL" style="color:#2244cc">Go to page 2 &rarr;</a></p>
          <label for="q" style="font-size:16px">Search: </label>
          <input id="q" type="text" placeholder="type here"
                 style="font-size:16px;padding:4px 8px;border:1px solid #aac;border-radius:4px;width:220px"/>
          <div style="height:8px"></div>
          <p style="font-size:14px;color:#555">Scroll down to see more content.</p>
          <div style="height:1200px;background:linear-gradient(to bottom,#e8efff,#c0cfee);
               padding:16px;font-size:14px;color:#333">
            <p>Tall scrollable section. Scroll down with the mouse wheel.</p>
            <p style="margin-top:600px">Bottom of the tall section.</p>
          </div>
        </body>
        </html>
        """;

    private const string Page2Html = """
        <!DOCTYPE html>
        <html>
        <head><title>Starling – Page 2</title></head>
        <body style="margin:0;font-family:sans-serif;background:#fff0e8;padding:24px">
          <h1 style="color:#6e1a00;font-size:36px">Page 2</h1>
          <p style="font-size:18px">You navigated here by clicking the link.</p>
          <p style="font-size:16px"><a href="PAGE1URL" style="color:#cc2200">&larr; Back to page 1</a></p>
          <p style="font-size:14px;color:#555">Page 2 has no tall content.</p>
        </body>
        </html>
        """;

    private const string StatusIslandHtml = """
        <!DOCTYPE html>
        <html>
        <head><title>Starling status island</title></head>
        <body style="margin:0;background:#18171a;color:#b8b6b3;font-family:sans-serif">
          <div id="wasm-island" style="height:32px;display:flex;align-items:center;border-top:1px solid #343238;padding:0 14px;cursor:pointer">
            <span style="width:6px;height:6px;border-radius:3px;background:#7ec59e"></span>
            <span style="margin-left:8px;font-size:11.5px;font-weight:600;color:#ececec">WASM island</span>
            <span id="wasm-state" style="margin-left:12px;flex:1;font-family:monospace;font-size:11px;color:#82807c;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">booting</span>
            <button id="wasm-clicks" style="height:22px;min-width:40px;border:1px solid #343238;border-radius:6px;background:#232225;color:#b8b6b3;font-family:monospace;font-size:11px">0</button>
          </div>
          <script>
          (function () {
            var state = document.getElementById('wasm-state');
            var island = document.getElementById('wasm-island');
            var clicks = document.getElementById('wasm-clicks');
            var count = 0;
            island.addEventListener('click', function () {
              count = count + 1;
              clicks.textContent = String(count);
            });
            var bytes = new Uint8Array([
              0x00,0x61,0x73,0x6d,0x01,0x00,0x00,0x00,
              0x01,0x07,0x01,0x60,0x02,0x7f,0x7f,0x01,0x7f,
              0x03,0x02,0x01,0x00,
              0x07,0x07,0x01,0x03,0x61,0x64,0x64,0x00,0x00,
              0x0a,0x09,0x01,0x07,0x00,0x20,0x00,0x20,0x01,0x6a,0x0b
            ]);
            WebAssembly.instantiate(bytes).then(function (result) {
              state.textContent = 'Wasmtime add(19, 23) = ' + result.instance.exports.add(19, 23);
            }, function (err) {
              state.textContent = 'WASM failed: ' + (err && err.message ? err.message : String(err));
            });
          })();
          </script>
        </body>
        </html>
        """;

    // ── Fields ───────────────────────────────────────────────────────────────

    private readonly int _maxFrames;
    private readonly string? _startUrl;
    private readonly string? _wasmIslandUrl;
    private readonly ILogger _log;

    public NativeBrowserWindow(
        int maxFrames = 0,
        string? startUrl = null,
        string? wasmIslandUrl = null,
        ILogger<NativeBrowserWindow>? log = null)
    {
        _maxFrames = maxFrames;
        _startUrl = startUrl;
        _wasmIslandUrl = wasmIslandUrl;
        _log = log ?? NullLogger<NativeBrowserWindow>.Instance;
    }

    public void Dispose() { }

    // ── Entry point ──────────────────────────────────────────────────────────

    public int Run()
    {
        // Write the two demo pages to temp files so the engine can load them
        // as file:// URLs.
        var page1Path = Path.Combine(Path.GetTempPath(), $"starling_p1_{Environment.ProcessId}.html");
        var page2Path = Path.Combine(Path.GetTempPath(), $"starling_p2_{Environment.ProcessId}.html");
        var statusPath = Path.Combine(Path.GetTempPath(), $"starling_status_{Environment.ProcessId}.html");
        var page1Url = "file://" + page1Path.Replace('\\', '/');
        var page2Url = "file://" + page2Path.Replace('\\', '/');
        var statusUrl = "file://" + statusPath.Replace('\\', '/');

        File.WriteAllText(page1Path, Page1Html.Replace("PAGE2URL", page2Url));
        File.WriteAllText(page2Path, Page2Html.Replace("PAGE1URL", page1Url));
        File.WriteAllText(statusPath, StatusIslandHtml);

        // A --url argument opens that address at launch (normalized like the URL
        // bar, so `--url example.com` becomes https://example.com). Otherwise the
        // built-in demo page loads.
        var startUrl = _startUrl is { Length: > 0 }
            ? UrlBarInputNormalizer.Normalize(_startUrl) ?? _startUrl
            : page1Url;
        var wasmIslandUrl = _wasmIslandUrl is { Length: > 0 }
            ? UrlBarInputNormalizer.Normalize(_wasmIslandUrl) ?? _wasmIslandUrl
            : statusUrl;

        try
        {
            return RunWindow(startUrl, wasmIslandUrl);
        }
        finally
        {
            try { File.Delete(page1Path); } catch (Exception ex) { /* best-effort */ NativeBrowserWindowLog.TempFileDeleteFailed(_log, ex, page1Path); }
            try { File.Delete(page2Path); } catch (Exception ex) { /* best-effort */ NativeBrowserWindowLog.TempFileDeleteFailed(_log, ex, page2Path); }
            try { File.Delete(statusPath); } catch (Exception ex) { /* best-effort */ NativeBrowserWindowLog.TempFileDeleteFailed(_log, ex, statusPath); }
        }
    }

    // ── Chrome layout ────────────────────────────────────────────────────────

    private static string EscapeHtml(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static float PageViewportWidth(float logicalW) =>
        Math.Max(1, logicalW - (float)SidebarWidthCss);

    private static float PageViewportHeight(float logicalH) =>
        Math.Max(1, logicalH - (float)ChromeHeightCss - (float)StatusBarHeightCss);

    private static double ToolbarButtonX(int index) =>
        ToolbarPadX + index * (ToolbarButtonW + ToolbarButtonGap);

    private static double UrlBarX() =>
        ToolbarPadX + (ToolbarButtonW + ToolbarButtonGap) * 3 + 12;

    // The right cluster has two buttons: DevTools (inner) then the theme toggle
    // (rightmost). The URL bar fills the gap between the nav buttons and the
    // cluster. These mirror the flex layout in BuildChrome for click hit-testing.
    private static double ThemeToggleBtnX(float contentW) =>
        Math.Max(UrlBarX() + 188, contentW - ToolbarPadX - ToolbarButtonW - ToolbarButtonGap);

    private static double DevToolsBtnX(float contentW) =>
        ThemeToggleBtnX(contentW) - ToolbarButtonW - ToolbarButtonGap;

    private static double UrlBarW(float contentW) =>
        Math.Max(120, DevToolsBtnX(contentW) - UrlBarX() - 12);

    private static int SidebarBookmarkIndexAt(double y)
    {
        var localY = y - SidebarWordmarkHeightCss - SidebarSectionHeightCss;
        if (localY < 0)
        {
            return -1;
        }

        var stride = SidebarRowHeightCss + SidebarRowGapCss;
        var idx = (int)(localY / stride);
        if (idx < 0 || idx >= Bookmarks.Length)
        {
            return -1;
        }

        return localY - idx * stride < SidebarRowHeightCss ? idx : -1;
    }

    private static string HostKey(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "";
        }

        return uri.IsDefaultPort ? uri.Host : uri.Authority;
    }

    private static string BookmarkColor(string host)
    {
        string[] colors = ["#e07a55", "#4a8a78", "#6e7fc6", "#d18a3d", "#9d6fb5", "#c25e7a", "#5a8a5a"];
        var hash = 0;
        foreach (var ch in host)
        {
            hash = unchecked(hash * 31 + ch);
        }

        return colors[(int)((uint)hash % colors.Length)];
    }

    private static char BookmarkInitial(NativeBookmark bookmark)
    {
        var source = bookmark.Title.Length > 0 ? bookmark.Title : bookmark.Host;
        return source.Length == 0 ? '?' : char.ToUpperInvariant(source[0]);
    }

    private static string JsEngineLabel() =>
        Environment.GetEnvironmentVariable("STARLING_JS_ENGINE") is { Length: > 0 } engine
            ? engine
            : "starling";

    private static string RenderBackendLabel() =>
        Environment.GetEnvironmentVariable("STARLING_PAINT_BACKEND") is { Length: > 0 } backend
            ? backend
            : "imagesharp-gpu";

    // The sidebar footer only changes with the theme. Cache the HTML per theme so the
    // sidebar can rebuild on every hover without re-running the renderer.
    private static readonly Dictionary<NativeThemeMode, string> FooterHtmlCache = new();

    private static string SidebarFooterHtml(NativeThemeMode mode, NativeTheme t)
    {
        if (!FooterHtmlCache.TryGetValue(mode, out var html))
        {
            html = BuildFactsRenderer.Render(JsEngineLabel(), RenderBackendLabel(), t.Faint, t.Muted);
            FooterHtmlCache[mode] = html;
        }
        return html;
    }

    private static (string Scheme, string Sep, string Host, string Path) UrlSegments(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return ("", "", "", "");
        }

        var schemeIdx = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx < 0)
        {
            return ("", "", url, "");
        }

        var scheme = url[..schemeIdx];
        var rest = url[(schemeIdx + 3)..];
        var slashIdx = rest.IndexOfAny(['/', '?', '#']);
        if (slashIdx < 0)
        {
            return (scheme, "://", rest, "");
        }

        return (scheme, "://", rest[..slashIdx], rest[slashIdx..]);
    }

    private static BlockBox BuildChrome(
        float contentW, NativeTheme t, ChromeImageResolver icons, string url, bool urlFocused,
        bool canGoBack, bool canGoForward, bool loading, bool devtoolsActive, NativeThemeMode themeMode)
    {
        string ToolbarButton(string label, string iconSvg, bool enabled, bool on = false)
        {
            var fg = enabled ? (on ? t.Accent : t.Text2) : t.Faint;
            var bg = on ? t.AccentBg : "transparent";
            return $"<div title=\"{EscapeHtml(label)}\" style=\"width:{ToolbarButtonW}px;height:{ToolbarButtonW}px;" +
                   $"margin-right:{ToolbarButtonGap}px;border-radius:9px;background:{bg};color:{fg};" +
                   $"display:flex;align-items:center;justify-content:center\">{iconSvg}</div>";
        }

        var sb = new StringBuilder();
        sb.Append($"<body style=\"margin:0;padding:0;background:{t.Bg};width:{contentW}px;" +
                  $"height:{ChromeHeightCss}px;font-family:{SansFont}\">");

        var shown = urlFocused ? url + "│" : url;
        var field = urlFocused
            ? $"background:{t.Surface};border:1px solid {t.Accent};box-shadow:0 0 0 3px {t.AccentBg}"
            : $"background:{t.Surface};border:1px solid {t.Border}";
        var secure = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var showWhole = urlFocused || shown.StartsWith("Find:", StringComparison.Ordinal);
        var segments = UrlSegments(shown);

        sb.Append($"<div style=\"height:{ToolbarHeightCss}px;background:{t.Bg};display:flex;align-items:center;" +
                  $"padding:0 {ToolbarPadX}px\">");
        sb.Append(ToolbarButton("Back", Icon(IconBack), canGoBack));
        sb.Append(ToolbarButton("Forward", Icon(IconFwd), canGoForward));
        sb.Append(ToolbarButton("Reload", Icon(loading ? IconStop : IconReload), true, loading));

        // URL bar.
        sb.Append($"<div style=\"height:{UrlBarHeightCss}px;flex:1;margin:0 12px;{field};border-radius:10px;" +
                  $"display:flex;align-items:center;overflow:hidden;color:{t.Text}\">");
        sb.Append($"<div style=\"width:38px;color:{(secure ? t.Accent : t.Muted)};" +
                  $"display:flex;align-items:center;justify-content:center\">{Icon(IconLock, 13)}</div>");
        sb.Append($"<div style=\"flex:1;font-family:{MonoFont};font-size:13px;white-space:nowrap;" +
                  $"overflow:hidden;text-overflow:ellipsis;color:{t.Text};display:flex;align-items:center\">");
        if (showWhole)
        {
            sb.Append($"<span style=\"color:{t.Text};overflow:hidden;text-overflow:ellipsis\">" +
                      $"{EscapeHtml(shown)}</span>");
        }
        else
        {
            sb.Append($"<span style=\"color:{t.Faint}\">{EscapeHtml(segments.Scheme)}</span>");
            sb.Append($"<span style=\"color:{t.Faint}\">{EscapeHtml(segments.Sep)}</span>");
            sb.Append($"<span style=\"color:{t.Text}\">{EscapeHtml(segments.Host)}</span>");
            sb.Append($"<span style=\"color:{t.Muted};overflow:hidden;text-overflow:ellipsis\">{EscapeHtml(segments.Path)}</span>");
        }
        sb.Append("</div>");

        // Loading progress pill (mirrors the Avalonia URL-bar pill).
        if (loading)
        {
            sb.Append($"<div style=\"display:flex;align-items:center;border-radius:999px;background:{t.AccentBg};" +
                      $"color:{t.Accent};font-family:{MonoFont};font-size:10.5px;padding:3px 10px;margin-right:6px;" +
                      "white-space:nowrap\">loading</div>");
        }

        // Divider + find chip.
        sb.Append($"<div style=\"width:1px;height:22px;background:{t.Border};margin:0 12px\"></div>");
        sb.Append($"<div style=\"display:flex;align-items:center;color:{t.Muted};font-family:{SansFont};" +
                  "font-size:12px;padding-right:14px;white-space:nowrap\">" +
                  $"<span style=\"display:flex;align-items:center;margin-right:7px\">{Icon(IconFind, 13)}</span>" +
                  $"<span>Find</span><span style=\"font-family:{MonoFont};font-size:10px;" +
                  $"color:{t.Faint};border:1px solid {t.Border};border-radius:4px;padding:0 5px;margin-left:7px\">&#8984;F</span></div>");
        sb.Append("</div>"); // end URL bar

        // Right cluster: DevTools (inner) then the theme toggle (rightmost).
        sb.Append(ToolbarButton("DevTools", Icon(IconBug), true, devtoolsActive));
        var toggleIcon = themeMode == NativeThemeMode.Light ? IconMoon : IconSun;
        sb.Append(ToolbarButton("Toggle theme", Icon(toggleIcon), true));
        sb.Append("</div></body>");

        return new Starling.Layout.LayoutEngine(new StyleEngine(), DefaultTextMeasurer.Instance, icons)
            .LayoutDocument(HtmlParser.Parse(sb.ToString()),
                new LayoutSize(contentW, ChromeHeightCss));
    }

    private static BlockBox BuildSidebar(
        float logicalH, NativeTheme t, ChromeImageResolver icons,
        string activeBookmarkHost, int hoveredIndex, string footerHtml)
    {
        var sb = new StringBuilder();
        sb.Append($"<body style=\"margin:0;padding:0;background:{t.Panel};width:{SidebarWidthCss}px;" +
                  $"height:{logicalH}px;font-family:{SansFont};position:relative;color:{t.Text};" +
                  $"border-right:1px solid {t.Border}\">");

        // Wordmark: gradient app-mark (the chrome's one spot of brand color) +
        // "Starling". The mark carries the same stroked spark glyph as the
        // Avalonia shell.
        sb.Append("<div style=\"position:absolute;left:18px;top:16px;height:22px;" +
                  "display:flex;align-items:center\">");
        sb.Append($"<div style=\"width:22px;height:22px;border-radius:7px;" +
                  $"background:linear-gradient(135deg,{t.Accent},{t.Accent2});box-shadow:0 1px 2px {t.AccentLine};" +
                  "color:#ffffff;display:flex;align-items:center;justify-content:center;margin-right:9px\">" +
                  $"{Icon("M3 11 L7 5 L10 9 L13 4", 13)}</div>");
        sb.Append($"<div style=\"font-size:15.5px;font-weight:600;color:{t.Text}\">Starling</div>");
        sb.Append("</div>");

        // Section label + count.
        sb.Append($"<div style=\"position:absolute;left:22px;top:{SidebarWordmarkHeightCss + 8}px;" +
                  $"width:{SidebarWidthCss - 44}px;height:11px;display:flex;align-items:center;" +
                  $"color:{t.Muted};font-size:11px;font-weight:500\">");
        sb.Append("<div style=\"flex:1\">Bookmarks</div>");
        sb.Append($"<div style=\"font-family:monospace;color:{t.Faint};font-size:10px\">{Bookmarks.Length}</div>");
        sb.Append("</div>");

        // Bookmark rows. Each row is a flex line (favicon + title vertically
        // centered) so the list reads as a tight, evenly-spaced column — active
        // and hovered rows get a filled background like the Avalonia tiles.
        for (var i = 0; i < Bookmarks.Length; i++)
        {
            var b = Bookmarks[i];
            var top = SidebarWordmarkHeightCss + SidebarSectionHeightCss + i * (SidebarRowHeightCss + SidebarRowGapCss);
            var active = string.Equals(activeBookmarkHost, b.Host, StringComparison.OrdinalIgnoreCase);
            var hovered = !active && i == hoveredIndex;
            var bg = active ? t.Surface : hovered ? t.Hover : "transparent";
            var border = active ? t.Hair : "transparent";
            var shadow = active ? ";box-shadow:0 1px 2px rgba(0,0,0,0.20)" : "";
            var fg = active ? t.Text : t.Text2;
            var weight = active ? "500" : "400";
            sb.Append($"<div style=\"position:absolute;left:{SidebarRowXCss}px;top:{top}px;" +
                      $"width:{SidebarRowWCss}px;height:{SidebarRowHeightCss}px;border-radius:7px;" +
                      $"border:1px solid {border};background:{bg}{shadow};" +
                      "display:flex;align-items:center;padding:0 10px;overflow:hidden\">");
            sb.Append($"<div style=\"width:16px;height:16px;flex:none;margin-right:11px;" +
                      $"border-radius:4px;background:{BookmarkColor(b.Host)};color:#ffffff;" +
                      "font-size:9px;font-weight:600;display:flex;align-items:center;justify-content:center\">" +
                      $"{BookmarkInitial(b)}</div>");
            sb.Append($"<div style=\"flex:1;font-size:13px;font-weight:{weight};color:{fg};" +
                      "white-space:nowrap;overflow:hidden;text-overflow:ellipsis\">" +
                      $"{EscapeHtml(b.Title)}</div>");
            sb.Append("</div>");
        }

        const double footerHeight = 76;
        var footerTop = Math.Max(SidebarWordmarkHeightCss + SidebarSectionHeightCss + Bookmarks.Length * (SidebarRowHeightCss + SidebarRowGapCss) + 16,
            logicalH - footerHeight);
        sb.Append($"<div style=\"position:absolute;left:18px;top:{footerTop}px;width:{SidebarWidthCss - 36}px;height:{footerHeight}px\">");
        sb.Append(footerHtml);
        sb.Append("</div>");
        sb.Append("</div></body>");

        return new Starling.Layout.LayoutEngine(new StyleEngine(), DefaultTextMeasurer.Instance, icons)
            .LayoutDocument(HtmlParser.Parse(sb.ToString()),
                new LayoutSize(SidebarWidthCss, logicalH));
    }

    // The bottom status bar — mirrors Starling.Gui/Chrome/StatusBar.cs: a state
    // dot + label, a hint, then View / Doc / Hist cells with hairline separators.
    private static BlockBox BuildStatusBar(
        float contentW, NativeTheme t, NativeStatusState state, string hint,
        string view, string doc, string hist)
    {
        var (dot, label) = state switch
        {
            NativeStatusState.Loading => (t.Warn, "Loading"),
            NativeStatusState.Error => (t.Err, "Error"),
            _ => (t.Accent, "Ready"),
        };
        var labelColor = state == NativeStatusState.Error ? t.Err : t.Text2;
        var hintColor = state == NativeStatusState.Error ? t.Err : t.Text2;

        string Kv(string key, string value) =>
            $"<div style=\"border-left:1px solid {t.Border};padding:0 13px;height:{StatusBarHeightCss}px;" +
            "display:flex;align-items:center\">" +
            $"<span style=\"font-size:10.5px;color:{t.Faint}\">{EscapeHtml(key)}</span>" +
            $"<span style=\"margin-left:7px;font-family:{MonoFont};font-size:11px;color:{t.Text2}\">{EscapeHtml(value)}</span></div>";

        var sb = new StringBuilder();
        sb.Append($"<body style=\"margin:0;padding:0;background:{t.Panel};width:{contentW}px;" +
                  $"height:{StatusBarHeightCss}px;font-family:{SansFont};border-top:1px solid {t.Border}\">");
        sb.Append($"<div style=\"height:{StatusBarHeightCss}px;display:flex;align-items:center\">");
        sb.Append("<div style=\"padding:0 14px 0 16px;display:flex;align-items:center\">");
        sb.Append($"<div style=\"width:6px;height:6px;border-radius:3px;background:{dot}\"></div>");
        sb.Append($"<span style=\"margin-left:8px;font-size:11.5px;font-weight:500;color:{labelColor}\">{label}</span></div>");
        sb.Append($"<div style=\"flex:1;padding:0 14px;font-family:{MonoFont};font-size:11px;color:{hintColor};" +
                  "white-space:nowrap;overflow:hidden;text-overflow:ellipsis\">" +
                  $"{EscapeHtml(hint)}</div>");
        sb.Append(Kv("View", view));
        sb.Append(Kv("Doc", doc));
        sb.Append(Kv("Hist", hist));
        sb.Append("</div></body>");

        return new Starling.Layout.LayoutEngine(new StyleEngine(), DefaultTextMeasurer.Instance)
            .LayoutDocument(HtmlParser.Parse(sb.ToString()),
                new LayoutSize(contentW, StatusBarHeightCss));
    }

    private static BlockBox BuildIslandFallbackBar(
        float contentW,
        NativeTheme t,
        NativeStatusState state,
        string label,
        string hint)
    {
        var dot = state == NativeStatusState.Error ? t.Err : t.Warn;
        var text = state == NativeStatusState.Error ? t.Err : t.Text2;
        var sb = new StringBuilder();
        sb.Append($"<body style=\"margin:0;padding:0;background:{t.Panel};width:{contentW}px;" +
                  $"height:{StatusBarHeightCss}px;font-family:{SansFont};border-top:1px solid {t.Border}\">");
        sb.Append($"<div style=\"height:{StatusBarHeightCss}px;display:flex;align-items:center;padding:0 14px;overflow:hidden\">");
        sb.Append($"<div style=\"width:6px;height:6px;border-radius:3px;background:{dot};flex:none\"></div>");
        sb.Append($"<span style=\"margin-left:8px;font-size:11.5px;font-weight:600;color:{text};white-space:nowrap\">" +
                  $"{EscapeHtml(label)}</span>");
        sb.Append($"<span style=\"margin-left:12px;flex:1;font-family:{MonoFont};font-size:11px;color:{text};" +
                  "white-space:nowrap;overflow:hidden;text-overflow:ellipsis\">" +
                  $"{EscapeHtml(hint)}</span>");
        sb.Append("</div></body>");

        return new Starling.Layout.LayoutEngine(new StyleEngine(), DefaultTextMeasurer.Instance)
            .LayoutDocument(HtmlParser.Parse(sb.ToString()),
                new LayoutSize(contentW, StatusBarHeightCss));
    }

    // ── Window loop ──────────────────────────────────────────────────────────

    private int RunWindow(string startUrl, string wasmIslandUrl)
    {
        Silk.NET.Windowing.Glfw.GlfwWindowing.Use();
        Silk.NET.Input.Glfw.GlfwInput.RegisterPlatform();

        var opts = WindowOptions.Default with
        {
            Title = "Starling (browser)",
            Size = new Vector2D<int>(900, 700),
            API = GraphicsAPI.None,
            VSync = false,
            ShouldSwapAutomatically = false,
        };

        using var window = Window.Create(opts);
        window.Initialize();

        var dpr = window.Size.X > 0 ? (float)window.FramebufferSize.X / window.Size.X : 1f;
        Console.WriteLine($"browser: fb={window.FramebufferSize} logical={window.Size} dpr={dpr}");

        using var renderer = RenderSessionFactory.Create();
        if (!renderer.SupportsSurfaceTargets)
        {
            Console.Error.WriteLine("browser: selected render backend does not support surface targets.");
            return 1;
        }
        var target = WindowSurfaceFrameTarget.TryCreate(window);
        if (target is null)
        {
            Console.Error.WriteLine("browser: no GPU adapter / surface; cannot create surface target.");
            return 1;
        }
        using var _target = target;

        // macOS accessibility bridge (phase 4) — null off macOS / no content view.
        var a11y = MacAccessibilityBridge.TryCreate(window.Native?.Cocoa ?? 0);

        // GLFW window handle for the native clipboard (phase 4). Declared up here
        // so the menu/clipboard local functions can capture it definitely-assigned.
        var glfwHandle = window.Native?.Glfw ?? 0;

        // ── State ────────────────────────────────────────────────────────────
        float logicalW = window.FramebufferSize.X / dpr;
        float logicalH = window.FramebufferSize.Y / dpr;
        var clock = Stopwatch.StartNew();
        var lastFb = window.FramebufferSize;
        int presented = 0;
        int failures = 0;

        // Tabs. Each Tab owns its session + view state; the live locals below
        // mirror the active tab and are swapped in/out by SwitchTab so every input
        // handler can keep using `page` / `session` / `scrollY` unchanged.
        var tabs = new List<Tab>();
        int activeIndex = 0;
        BrowserSession session = null!;   // assigned from the first tab below
        double scrollY = 0;
        // The store-root offset scrollY was last synced against, either way.
        // The shell owns root scroll in v1 (scroll-model.md, Decision 1); this
        // snapshot lets the Update loop tell a store-driven root write (the JS
        // bindings, WP3) from the value the shell itself last wrote.
        double storeRootSyncedY = 0;
        LaidOutPage? page = null;
        int lastLayoutVersion = -1;

        // In-flight navigation. Loads run off the main thread; the Update loop polls
        // the task and applies the result on the main thread when it completes. The
        // window stays live and shows a loading state instead of blocking — a slow
        // or hung fetch no longer freezes the window gray on launch. pendingNavTab
        // is the tab the load belongs to (it may not be the active one by the time
        // it finishes); loadingUrl drives the URL bar while the first page loads.
        Task<Result<LaidOutPage, RenderError>>? pendingNav = null;
        int pendingNavTab = -1;
        string loadingUrl = startUrl;
        BlockBox? loadingBox = null;

        // Text-input state
        Element? focusedInput = null;

        // URL-bar edit state. When focused, keystrokes edit urlBarText instead of
        // the page and Enter navigates. Click the chrome strip or press Cmd/Ctrl+L
        // to focus; Esc or navigating clears it.
        bool urlBarFocused = false;
        string urlBarText = "";

        // Chrome state — the toolbar, sidebar, and status bar are cached
        // independently, each rebuilt only when its own signature changes (so a
        // sidebar hover doesn't re-lay-out the toolbar, a nav doesn't rebuild the
        // sidebar, etc.).
        BlockBox? chromeBox = null;
        BlockBox? sidebarBox = null;
        BlockBox? statusBox = null;
        string chromeSig = "";
        string sidebarSig = "";
        string statusSig = "";
        BrowserSession? wasmIslandSession = null;
        Task<Result<LaidOutPage, RenderError>>? pendingWasmIslandNav = null;
        LaidOutPage? wasmIslandPage = null;
        int wasmIslandLastLayoutVersion = -1;
        string wasmIslandStatusHint = "Loading WASM island";
        bool wasmIslandLoadError = false;
        BlockBox? wasmIslandFallbackBox = null;
        string wasmIslandFallbackSig = "";

        // Which sidebar bookmark row the pointer is over (-1 = none), for hover.
        int sidebarHover = -1;

        // Theme. Like the Avalonia shell, the selection lives in memory and starts
        // on Dark; the toolbar sun/moon button cycles Dark → Light → Contrast.
        var themeMode = NativeThemeMode.Dark;

        // The icon resolver rasterizes the chrome's inline <svg> glyphs; one
        // instance for the window's lifetime so decoded icons stay cached.
        using var icons = new ChromeImageResolver();

        // Whether the most recent navigation failed — drives the status-bar state.
        bool lastNavError = false;

        // Hover + animation styling. hoverElement is the innermost element
        // under the pointer; hoverOverrides maps each affected element to its
        // :hover computed style; hoverScope tracks which elements the current hover
        // touches so the next change can register reverse transitions. animClockMs
        // is the shared animation/transition clock, read by the styleOverride at
        // paint time. Mirrors WebviewPanel.
        Element? hoverElement = null;
        Dictionary<Element, ComputedStyle>? hoverOverrides = null;
        HashSet<Element> hoverScope = new();
        long animClockMs = 0;

        // Find-in-page. Cmd/Ctrl+F opens; keystrokes edit findQuery; Enter
        // and Shift+Enter step matches; Esc closes. The current match is drawn by
        // an overlay document composited over the page in document space.
        bool findActive = false;
        string findQuery = "";
        List<BoxHitTester.PlacedFragment>? findFragments = null;
        int findCursor = -1;  // index of the current match fragment, -1 none
        int findMatchTotal = 0;
        BlockBox? findOverlay = null;

        // Context menu. Right-click opens a menu of actions whose items
        // depend on what is under the pointer (a link adds Open/Copy Link). Drawn
        // as a screen-fixed overlay; the next click runs an item or dismisses.
        bool menuActive = false;
        double menuX = 0, menuY = 0;
        var menuItems = new List<(string Label, Action Run)>();
        BlockBox? menuOverlay = null;

        // IME preedit. On macOS with STARLING_IME_PREEDIT=1 the MacImeBridge
        // feeds the active composition string here; it is drawn underlined at the
        // focused field. Committed text still arrives via the GLFW char callback.
        string preedit = "";

        // Devtools: F12 toggles a read-only DOM-tree inspector panel on the
        // right, an engine-rendered screen-fixed overlay rebuilt when the DOM or
        // window size changes.
        bool devtoolsActive = false;
        BlockBox? devtoolsOverlay = null;

        // "Something visible changed this frame" signal, set wherever state that
        // affects the picture changes. It does NOT gate the present today — the
        // loop presents every frame so the macOS surface stays flushed (gating it
        // left the window gray). It is kept up to date for a future damage-based
        // present that skips the re-raster while still flushing the surface.

        // ── Start the initial load (non-blocking) ──────────────────────────────
        // The first tab opens with no page yet; the window loop starts immediately
        // and shows a loading state while the fetch runs. The Update loop applies
        // the page when the task completes. Page viewport is the window minus the
        // chrome strip at the top.
        var firstSession = new BrowserSession();
        var options = NavOpts();
        tabs.Add(new Tab(firstSession));
        activeIndex = 0;
        session = firstSession;
        pendingNav = firstSession.NavigateInteractiveAsync(startUrl, options);
        pendingNavTab = 0;
        Console.WriteLine($"browser: loading {startUrl} …");

        wasmIslandSession = new BrowserSession();
        pendingWasmIslandNav = wasmIslandSession.NavigateInteractiveAsync(wasmIslandUrl, IslandOpts());
        Console.WriteLine($"browser: loading WASM island {wasmIslandUrl} …");

        // Push the accessibility tree to the OS after a (re)layout or navigation.
        void PushA11y()
        {
            if (a11y is null || page is null)
            {
                return;
            }

            var tree = AccessibilityTreeBuilder.Build(page.Root, page.Document);
            a11y.Update(tree, ChromeHeightCss, scrollY, logicalH);
        }

        PushA11y();

        // Mirror the shell's root scroll position into the page store's root
        // entry (write-only: the store clamps and flags, dispatch waits for the
        // frame pump). Reading the clamped value back keeps the snapshot stable
        // against geometry rounding.
        void SyncRootScrollToStore()
        {
            if (page is null) { storeRootSyncedY = 0; return; }
            page.ScrollState.WriteRoot(0, scrollY);
            storeRootSyncedY = page.ScrollState.Root.OffsetY;
        }

        // ── Input ─────────────────────────────────────────────────────────────
        var input = window.CreateInput();

        if (input.Mice.Count > 0)
        {
            var mouse = input.Mice[0];

            mouse.Scroll += (m, wheel) =>
            {
                if (page is null)
                {
                    return;
                }

                // Inner scroll first: when the pointer is over the page region,
                // route the wheel through the shared ScrollController so the
                // deepest `overflow: scroll | auto` container with room takes
                // the delta (latched per axis) into the page's scroll store.
                // The loop presents every frame, so a consumed tick repaints on
                // its own — no relayout, no synchronous event dispatch (the
                // store's pending flags wait for the frame pump, WP4).
                var pos = m.Position;
                if (pos.X >= SidebarWidthCss && pos.Y >= ChromeHeightCss && !IsStatusBarPoint(pos.X, pos.Y))
                {
                    var hit = BoxHitTester.HitTest(
                        page.Root,
                        pos.X - SidebarWidthCss,
                        (pos.Y - ChromeHeightCss) + scrollY,
                        viewportX: 0,
                        viewportY: scrollY,
                        page.ScrollOffsetLookup,
                        page.StickyShiftLookup);
                    // Silk wheel deltas are lines, positive = up; negate into
                    // content-space. No precision flag on this input path, so
                    // every tick takes the lines x40 conversion.
                    if (hit.Box is not null && ScrollController.TryScroll(
                            page.ScrollState, hit.Box, -wheel.X, -wheel.Y, precise: false))
                    {
                        return;
                    }
                }

                // Fall through to the shell-owned root scroller, mirrored into
                // the store's root entry for the JS surface (WP3/WP4).
                var maxScroll = Math.Max(0, page.DocumentHeight - PageViewportHeight(logicalH));
                var newScroll = Math.Clamp(scrollY - wheel.Y * 40, 0, maxScroll);
                if (newScroll != scrollY)
                {
                    scrollY = newScroll;
                    SyncRootScrollToStore();
                }
            };

            mouse.MouseMove += (m, pos) =>
            {
                // Sidebar bookmark hover — tracked even before the first page loads.
                var newSidebarHover = pos.X >= SidebarRowXCss && pos.X <= SidebarRowXCss + SidebarRowWCss && pos.Y < logicalH
                    ? SidebarBookmarkIndexAt(pos.Y)
                    : -1;
                if (newSidebarHover != sidebarHover)
                {
                    sidebarHover = newSidebarHover;
                }

                if (page is null)
                {
                    return;
                }

                // Pointer is over chrome. Use default cursor and skip page hover.
                if (pos.X < SidebarWidthCss || pos.Y < ChromeHeightCss || IsStatusBarPoint(pos.X, pos.Y))
                {
                    SetCursor(m.Cursor, "default", _log);
                    UpdateHover(null);
                    return;
                }

                var pageX = pos.X - SidebarWidthCss;
                var pageY = (pos.Y - ChromeHeightCss) + scrollY;

                var hit = BoxHitTester.HitTest(
                    page.Root,
                    pageX,
                    pageY,
                    viewportX: 0,
                    viewportY: scrollY,
                    page.ScrollOffsetLookup,
                    page.StickyShiftLookup);
                var cursor = BoxHitTester.ResolveCursor(hit);
                SetCursor(m.Cursor, cursor, _log);

                // Drive CSS :hover from the innermost element under the pointer.
                Element? hoverEl = null;
                for (var b = hit.Box; b is not null; b = b.Parent)
                {
                    if (b.Element is { } e) { hoverEl = e; break; }
                }

                UpdateHover(hoverEl);
            };

            mouse.MouseDown += (m, button) =>
            {
                var pos = m.Position;

                // An open context menu eats the next click: run the item under it,
                // else dismiss.
                if (menuActive)
                {
                    var n = menuItems.Count;
                    if (button == MouseButton.Left
                        && pos.X >= menuX && pos.X <= menuX + MenuW
                        && pos.Y >= menuY && pos.Y < menuY + n * MenuItemH)
                    {
                        var idx = (int)((pos.Y - menuY) / MenuItemH);
                        CloseMenu();
                        if (idx >= 0 && idx < n)
                        {
                            menuItems[idx].Run();
                        }
                    }
                    else
                    {
                        CloseMenu();
                    }
                    return;
                }

                // Right-click opens the context menu.
                if (button == MouseButton.Right)
                {
                    if (page is null)
                    {
                        return;
                    }

                    OpenMenuAt(pos.X, pos.Y);
                    return;
                }

                if (button != MouseButton.Left)
                {
                    return;
                }

                // Sidebar bookmarks.
                if (pos.X < SidebarWidthCss)
                {
                    if (pos.X >= SidebarRowXCss && pos.X <= SidebarRowXCss + SidebarRowWCss)
                    {
                        var idx = SidebarBookmarkIndexAt(pos.Y);
                        if (idx >= 0)
                        {
                            Navigate(Bookmarks[idx].Url);
                        }
                    }
                    return;
                }

                if (IsStatusBarPoint(pos.X, pos.Y))
                {
                    DispatchIslandClick(pos.X - SidebarWidthCss, pos.Y - (logicalH - StatusBarHeightCss));
                    return;
                }

                // Toolbar buttons and the URL bar.
                if (pos.Y < ChromeHeightCss)
                {
                    var x = pos.X - SidebarWidthCss;
                    if (x >= ToolbarButtonX(0) && x < ToolbarButtonX(0) + ToolbarButtonW) { GoBack(); return; }
                    if (x >= ToolbarButtonX(1) && x < ToolbarButtonX(1) + ToolbarButtonW) { GoForward(); return; }
                    if (x >= ToolbarButtonX(2) && x < ToolbarButtonX(2) + ToolbarButtonW) { Reload(); return; }

                    var contentW = PageViewportWidth(logicalW);
                    var toggle = ThemeToggleBtnX(contentW);
                    if (x >= toggle && x < toggle + ToolbarButtonW)
                    {
                        themeMode = NativeTheme.Next(themeMode);
                        chromeSig = "";   // force a chrome rebuild under the new theme
                        return;
                    }

                    var devtools = DevToolsBtnX(contentW);
                    if (x >= devtools && x < devtools + ToolbarButtonW)
                    {
                        devtoolsActive = !devtoolsActive;
                        devtoolsOverlay = null;
                        return;
                    }

                    var ux = UrlBarX();
                    var uw = UrlBarW(PageViewportWidth(logicalW));
                    if (x >= ux && x < ux + uw)
                    {
                        if (x >= ux + Math.Max(0, uw - UrlFindChipW))
                        {
                            OpenFind();
                            return;
                        }

                        if (focusedInput is not null && page is not null)
                        {
                            page.Scripting?.DispatchEvent(focusedInput,
                                new FocusEvent("blur", new EventInit(Bubbles: false)));
                            focusedInput = null;
                            page.Document.FocusedElement = null;
                        }
                        urlBarFocused = true;
                        urlBarText = page?.Url ?? loadingUrl;
                    }
                    return;
                }

                if (page is null)
                {
                    return;
                }

                // Click landed in the page — drop URL-bar focus.
                if (urlBarFocused) { urlBarFocused = false; }

                var pageX = pos.X - SidebarWidthCss;
                var pageY = (pos.Y - ChromeHeightCss) + scrollY;

                var hit = BoxHitTester.HitTest(
                    page.Root,
                    pageX, pageY,
                    viewportX: 0,
                    viewportY: scrollY,
                    page.ScrollOffsetLookup,
                    page.StickyShiftLookup);

                // Focus / text input
                if (hit.Box?.Element is { } hitEl
                    && HtmlFormControls.IsTextControl(hitEl))
                {
                    focusedInput = hitEl;
                    page.Document.FocusedElement = hitEl;
                    page.Scripting?.DispatchEvent(hitEl,
                        new FocusEvent("focus", new EventInit(Bubbles: false)));
                    return;
                }

                // Blur any previously focused input
                if (focusedInput is not null)
                {
                    page.Scripting?.DispatchEvent(focusedInput,
                        new FocusEvent("blur", new EventInit(Bubbles: false)));
                    focusedInput = null;
                    page.Document.FocusedElement = null;
                }

                // Link navigation
                if (hit.LinkAnchor is { } anchor)
                {
                    var href = anchor.GetAttribute("href");
                    if (!string.IsNullOrEmpty(href))
                    {
                        var resolved = LinkResolver.Resolve(href, page.Url);
                        if (resolved is not null)
                        {
                            Navigate(resolved);
                            return;
                        }
                    }
                }

                // Generic click event
                if (hit.Box?.Element is { } clickTarget && page.Scripting is { } scripting)
                {
                    scripting.DispatchEvent(clickTarget, new MouseEvent("click",
                        new EventInit(Bubbles: true, Cancelable: true))
                    {
                        ClientX = pageX,
                        ClientY = pageY,
                        Button = 0,
                    });
                }
            };
        }

        if (input.Keyboards.Count > 0)
        {
            var keyboard = input.Keyboards[0];

            // Cmd (macOS) or Ctrl (elsewhere) held — the clipboard chord modifier.
            bool CmdOrCtrl() =>
                keyboard.IsKeyPressed(Key.SuperLeft) || keyboard.IsKeyPressed(Key.SuperRight)
                || keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight);

            keyboard.KeyChar += (_, c) =>
            {
                if (page is null)
                {
                    return;
                }

                if (CmdOrCtrl())
                {
                    return; // a clipboard/shortcut chord, not text
                }

                // Find-in-page takes text first when open.
                if (findActive)
                {
                    findQuery += c;
                    findCursor = -1;
                    RunFind(1);
                    return;
                }

                // URL bar takes text next when it owns focus.
                if (urlBarFocused)
                {
                    urlBarText += c;
                    return;
                }

                if (focusedInput is null)
                {
                    return;
                }

                var current = HtmlFormControls.Value(focusedInput);
                HtmlFormControls.SetValue(focusedInput, current + c);
                page.Scripting?.DispatchEvent(focusedInput,
                    new InputEvent("input", new EventInit(Bubbles: true))
                    {
                        Data = c.ToString(),
                        InputType = "insertText",
                    });
                RefreshLayout();
            };

            bool Alt() =>
                keyboard.IsKeyPressed(Key.AltLeft) || keyboard.IsKeyPressed(Key.AltRight);

            keyboard.KeyDown += (_, key, _) =>
            {
                if (page is null)
                {
                    return;
                }

                // An open context menu swallows Escape (dismiss).
                if (menuActive)
                {
                    if (key == Key.Escape)
                    {
                        CloseMenu();
                    }

                    return;
                }

                // F12 toggles the devtools DOM inspector.
                if (key == Key.F12)
                {
                    devtoolsActive = !devtoolsActive;
                    devtoolsOverlay = null;
                    return;
                }

                // Global history chords: Cmd/Ctrl+[ back, +] forward, +R reload,
                // and Alt+Left / Alt+Right back / forward. Handled before URL-bar
                // editing so they work no matter what has focus.
                if (CmdOrCtrl() && key == Key.LeftBracket) { GoBack(); return; }
                if (CmdOrCtrl() && key == Key.RightBracket) { GoForward(); return; }
                if (CmdOrCtrl() && key == Key.R) { Reload(); return; }
                if (Alt() && key == Key.Left) { GoBack(); return; }
                if (Alt() && key == Key.Right) { GoForward(); return; }

                // Cmd/Ctrl+N opens a new browser window (a fresh process).
                if (CmdOrCtrl() && key == Key.N) { LaunchNewWindow(); return; }

                // Tab chords: Cmd/Ctrl+T new, +W close, +1..9 select, +Tab next.
                if (CmdOrCtrl() && key == Key.T) { NewTab(startUrl); return; }
                if (CmdOrCtrl() && key == Key.W) { CloseTab(activeIndex); return; }
                if (CmdOrCtrl() && key == Key.Tab) { SwitchTab((activeIndex + 1) % tabs.Count); return; }
                if (CmdOrCtrl() && key >= Key.Number1 && key <= Key.Number9)
                {
                    SwitchTab((int)(key - Key.Number1));
                    return;
                }

                // Cmd/Ctrl+F — open find-in-page.
                if (CmdOrCtrl() && key == Key.F) { OpenFind(); return; }

                // Find-in-page editing owns the keyboard while it is open.
                if (findActive)
                {
                    if (CmdOrCtrl() && key == Key.V)
                    {
                        var clip = NativeClipboard.Get(glfwHandle);
                        if (!string.IsNullOrEmpty(clip)) { findQuery += clip; findCursor = -1; RunFind(1); }
                        return;
                    }
                    if (CmdOrCtrl())
                    {
                        return;
                    }

                    switch (key)
                    {
                        case Key.Backspace:
                            if (findQuery.Length > 0) { findQuery = findQuery[..^1]; findCursor = -1; RunFind(1); }
                            break;
                        case Key.Escape:
                            CloseFind();
                            break;
                        case Key.Enter:
                        case Key.KeypadEnter:
                            var back = keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight);
                            RunFind(back ? -1 : 1);
                            break;
                    }
                    return;
                }

                // Cmd/Ctrl+L — focus + select the URL bar (standard browser chord).
                if (CmdOrCtrl() && key == Key.L)
                {
                    if (focusedInput is not null)
                    {
                        page.Scripting?.DispatchEvent(focusedInput,
                            new FocusEvent("blur", new EventInit(Bubbles: false)));
                        focusedInput = null;
                        page.Document.FocusedElement = null;
                    }
                    urlBarFocused = true;
                    urlBarText = page.Url ?? "";
                    return;
                }

                // URL-bar editing owns the keyboard while it has focus.
                if (urlBarFocused)
                {
                    if (CmdOrCtrl())
                    {
                        if (key == Key.V)
                        {
                            var clip = NativeClipboard.Get(glfwHandle);
                            if (!string.IsNullOrEmpty(clip))
                            {
                                urlBarText += clip;
                            }
                        }
                        else if (key is Key.C or Key.X)
                        {
                            NativeClipboard.Set(glfwHandle, urlBarText);
                            if (key == Key.X)
                            {
                                urlBarText = "";
                            }
                        }
                        else if (key == Key.A)
                        {
                            // Select-all is implicit (we have no caret model yet);
                            // the next typed char replaces. Treat as a no-op clear.
                        }
                        return;
                    }

                    switch (key)
                    {
                        case Key.Backspace:
                            if (urlBarText.Length > 0)
                            {
                                urlBarText = urlBarText[..^1];
                            }

                            break;
                        case Key.Escape:
                            urlBarFocused = false;
                            break;
                        case Key.Enter:
                        case Key.KeypadEnter:
                            var target = UrlBarInputNormalizer.Normalize(urlBarText);
                            urlBarFocused = false;
                            if (target is not null)
                            {
                                Navigate(target);
                            }
                            else
                            {
                                Console.Error.WriteLine($"browser: '{urlBarText}' is not a URL");
                            }

                            break;
                    }
                    return;
                }

                if (focusedInput is null)
                {
                    return;
                }

                // Clipboard chords (phase 4): copy / cut / paste on the focused input.
                if (CmdOrCtrl() && key is Key.C or Key.X or Key.V)
                {
                    var val = HtmlFormControls.Value(focusedInput);
                    if (key == Key.C)
                    {
                        NativeClipboard.Set(glfwHandle, val);
                    }
                    else if (key == Key.X)
                    {
                        NativeClipboard.Set(glfwHandle, val);
                        HtmlFormControls.SetValue(focusedInput, "");
                        page.Scripting?.DispatchEvent(focusedInput,
                            new InputEvent("input", new EventInit(Bubbles: true)) { InputType = "deleteByCut" });
                        RefreshLayout();
                    }
                    else // V
                    {
                        var clip = NativeClipboard.Get(glfwHandle);
                        if (!string.IsNullOrEmpty(clip))
                        {
                            HtmlFormControls.SetValue(focusedInput, val + clip);
                            page.Scripting?.DispatchEvent(focusedInput,
                                new InputEvent("input", new EventInit(Bubbles: true)) { Data = clip, InputType = "insertFromPaste" });
                            RefreshLayout();
                        }
                    }
                    return;
                }

                if (key == Key.Backspace)
                {
                    var v = HtmlFormControls.Value(focusedInput);
                    if (v.Length > 0)
                    {
                        HtmlFormControls.SetValue(focusedInput, v[..^1]);
                        page.Scripting?.DispatchEvent(focusedInput,
                            new InputEvent("input", new EventInit(Bubbles: true))
                            {
                                InputType = "deleteContentBackward",
                            });
                        RefreshLayout();
                    }
                }
                else if (key is Key.Enter or Key.KeypadEnter)
                {
                    // Best-effort blur + change
                    page.Scripting?.DispatchEvent(focusedInput,
                        new Event("change", new EventInit(Bubbles: true)));
                    page.Scripting?.DispatchEvent(focusedInput,
                        new FocusEvent("blur", new EventInit(Bubbles: false)));
                    focusedInput = null;
                    page.Document.FocusedElement = null;
                }
            };
        }

        // IME preedit driver. Off by default, since the
        // commit-style path already works; this adds the inline composing display.
        if (Environment.GetEnvironmentVariable("STARLING_IME_PREEDIT") == "1")
        {
            MacImeBridge.Install(
                s => { preedit = s; },
                () => { preedit = ""; });
        }

        // ── Window events ─────────────────────────────────────────────────────
        window.FramebufferResize += sz =>
        {
            target.Configure(Math.Max(1, sz.X), Math.Max(1, sz.Y));
        };

        // Animation pacing. Cap continuous animation/transition frames to a target
        // rate so a fast page does not spin the present at hundreds of fps.
        // STARLING_REDUCE_MOTION freezes animation entirely — heavy pages (a tall
        // page with a page-wide animation) then render once and stay responsive
        // instead of re-rasterizing every frame.
        var reduceMotion = Environment.GetEnvironmentVariable("STARLING_REDUCE_MOTION") == "1";
        long lastAnimMs = long.MinValue;
        const double animFrameBudgetMs = 1000.0 / 60; // 60 fps

        window.Update += _ =>
        {
            // Apply a finished navigation (incl. the initial load) on the main thread.
            PollNav();
            PollIslandNav(wasmIslandSession, ref pendingWasmIslandNav, ref wasmIslandPage, ref wasmIslandLastLayoutVersion,
                ref wasmIslandStatusHint, ref wasmIslandLoadError, "WASM island");
            PumpIsland(wasmIslandSession, ref wasmIslandPage, ref wasmIslandLastLayoutVersion);

            if (page is null)
            {
                return;
            }

            page.Document.DecayRecentMutations();

            var clockMs = clock.ElapsedMilliseconds;
            page.Scripting?.PumpFrame(clockMs);

            // Relayout if DOM changed in a layout-relevant way
            var lv = page.Document.LayoutInvalidationVersion;
            if (lv != lastLayoutVersion)
            {
                lastLayoutVersion = lv;
                RefreshLayout();
            }

            // Apply a store-driven root write (window.scrollTo, WP3 — script
            // ran in PumpFrame above) back to the shell's root scroller. The
            // storeRootSyncedY snapshot keeps the two-way sync from
            // ping-ponging with the shell's own writes.
            var rootState = page.ScrollState.Root;
            if (rootState.OffsetY != storeRootSyncedY)
            {
                scrollY = rootState.OffsetY;
                storeRootSyncedY = rootState.OffsetY;
            }

            // Animation frame: advance the clock, re-sample the hovered scope at
            // the new time so a hover transition progresses, and present. Paced to
            // the frame budget; reduced-motion skips it entirely.
            if (!reduceMotion
                && clockMs - lastAnimMs >= animFrameBudgetMs
                && session.HasActiveAnimations(page))
            {
                lastAnimMs = clockMs;
                session.PrepareAnimationFrame(page, clockMs);
                animClockMs = clockMs;
                if (hoverElement is not null)
                {
                    hoverOverrides = BuildHoverOverrides(hoverElement, clockMs);
                }
            }
        };

        window.Render += _ =>
        {
            var frameStart = clock.ElapsedMilliseconds;

            var fb = window.FramebufferSize;
            if (fb.X <= 0 || fb.Y <= 0)
            {
                return;
            }

            if (fb != lastFb)
            {
                dpr = window.Size.X > 0 ? (float)fb.X / window.Size.X : 1f;
                logicalW = fb.X / dpr;
                logicalH = fb.Y / dpr;
                lastFb = fb;
                RefreshLayout();
                RefreshIslandLayout(wasmIslandSession, ref wasmIslandPage, ref wasmIslandLastLayoutVersion);
            }

            // Present every frame. The present is what keeps the on-screen surface
            // live — skipping it (an earlier "only present when changed"
            // optimization) left the macOS swapchain unflushed and the whole window
            // gray. The loop is instead paced by a short sleep AFTER the present
            // (below), once the frame is already on screen, so the CPU stays bounded
            // without ever starving the display.

            var loading = page is null; // the initial page hasn't arrived yet

            // Build (or reuse) the chrome BlockBox. The URL-bar row shows the find
            // query while find is open, else the edit buffer when focused, else the
            // active page URL (or the loading URL before the first page arrives).
            string shownUrl;
            bool urlFocusVisual;
            if (findActive)
            {
                var status = findQuery.Length == 0
                    ? ""
                    : findMatchTotal == 0
                        ? "  (no matches)"
                        : $"  ({findMatchTotal} match{(findMatchTotal == 1 ? "" : "es")})";
                shownUrl = $"Find: {findQuery}{status}";
                urlFocusVisual = true;
            }
            else
            {
                shownUrl = urlBarFocused ? urlBarText : (page?.Url ?? loadingUrl);
                urlFocusVisual = urlBarFocused;
            }

            var contentW = PageViewportWidth(logicalW);
            var activeBookmarkHost = HostKey(page?.Url ?? loadingUrl);
            var loadingNav = pendingNav is not null;
            var theme = NativeTheme.For(themeMode);

            // Status-bar fields — real engine state, mirroring the Avalonia bar.
            var statusState = loadingNav ? NativeStatusState.Loading
                            : lastNavError ? NativeStatusState.Error
                            : NativeStatusState.Ready;
            var statusHint = statusState switch
            {
                NativeStatusState.Loading => "Loading…",
                NativeStatusState.Error => "Load failed",
                _ => page?.Url ?? "",
            };
            var statusView = page is not null ? $"{page.Viewport.Width}×{page.Viewport.Height}" : "—";
            var statusDoc = page is not null ? $"{(int)page.DocumentHeight} px" : "—";
            var statusHist = $"{session.History.Index + 1}/{Math.Max(1, session.History.Count)}";

            // Toolbar: depends on URL text/focus, nav state, width, theme.
            var chromeSigNew = $"{shownUrl}|{urlFocusVisual}|{contentW}|" +
                               $"{session.History.CanGoBack}|{session.History.CanGoForward}|{loadingNav}|" +
                               $"{devtoolsActive}|{themeMode}";
            if (chromeBox is null || chromeSigNew != chromeSig)
            {
                chromeBox = BuildChrome(
                    contentW, theme, icons, shownUrl, urlFocusVisual,
                    session.History.CanGoBack,
                    session.History.CanGoForward, loadingNav, devtoolsActive, themeMode);
                chromeSig = chromeSigNew;
            }

            // Sidebar: depends on height, active bookmark, hovered row, theme.
            var sidebarSigNew = $"{logicalH}|{activeBookmarkHost}|{sidebarHover}|{themeMode}";
            if (sidebarBox is null || sidebarSigNew != sidebarSig)
            {
                sidebarBox = BuildSidebar(
                    logicalH, theme, icons, activeBookmarkHost, sidebarHover,
                    SidebarFooterHtml(themeMode, theme));
                sidebarSig = sidebarSigNew;
            }

            // Status bar: depends on width, state, hint, info cells, theme.
            var statusSigNew = $"{contentW}|{statusState}|{statusHint}|{statusView}|{statusDoc}|{statusHist}|{themeMode}";
            if (statusBox is null || statusSigNew != statusSig)
            {
                statusBox = BuildStatusBar(contentW, theme, statusState, statusHint, statusView, statusDoc, statusHist);
                statusSig = statusSigNew;
            }
            BlockBox? bottomChrome = statusBox;
            BlockBox? bottomChromeRight = null;
            double bottomChromeLeftW = 0;
            if (wasmIslandPage is not null || pendingWasmIslandNav is not null || wasmIslandLoadError)
            {
                bottomChrome = wasmIslandPage?.Root ?? IslandFallback(
                    ref wasmIslandFallbackBox,
                    ref wasmIslandFallbackSig,
                    contentW,
                    theme,
                    "WASM island",
                    wasmIslandStatusHint,
                    wasmIslandLoadError);
            }

            // While the first page loads, present the chrome over a "Loading…" page
            // so the window is live instead of gray.
            if (loading)
            {
                loadingBox = BuildLoadingPage();
                using var loadFrame = renderer.RenderComposited(new CompositedFrameRequest
                {
                    SurfaceWidth = fb.X,
                    SurfaceHeight = fb.Y,
                    Scale = dpr,
                    ChromeRoot = chromeBox,
                    ChromeHeightCss = ChromeHeightCss,
                    LeftChromeRoot = sidebarBox,
                    LeftChromeWidthCss = SidebarWidthCss,
                    PageRoot = loadingBox,
                    BottomChromeRoot = bottomChrome,
                    BottomChromeRightRoot = bottomChromeRight,
                    BottomChromeLeftWidthCss = bottomChromeLeftW,
                    BottomChromeHeightCss = StatusBarHeightCss,
                }, target);
                var okLoad = loadFrame.Presented;
                if (okLoad)
                {
                    presented++;
                }
                else
                {
                    failures++;
                }
            }
            else
            {
                // Screen-fixed overlay: a context menu wins over the devtools panel.
                if (devtoolsActive && devtoolsOverlay is null)
                {
                    devtoolsOverlay = BuildDevtoolsOverlay();
                }

                var screenOverlay = menuActive ? menuOverlay
                                  : devtoolsActive ? devtoolsOverlay
                                  : null;

                using var frame = renderer.RenderComposited(new CompositedFrameRequest
                {
                    SurfaceWidth = fb.X,
                    SurfaceHeight = fb.Y,
                    Scale = dpr,
                    ChromeRoot = chromeBox,
                    ChromeHeightCss = ChromeHeightCss,
                    LeftChromeRoot = sidebarBox,
                    LeftChromeWidthCss = SidebarWidthCss,
                    PageRoot = page!.Root,
                    ScrollX = 0,
                    ScrollY = scrollY,
                    PageScrollOffsets = page.ScrollOffsetLookup,
                    PageStickyShifts = page.StickyShiftLookup,
                    PageAnimating = box => IsAnimatingLayerRoot(page, box),
                    StyleOverride = StyleOverride,
                    Images = page.ImageResolver,
                    OverlayRoot = findActive ? findOverlay : (preedit.Length > 0 ? BuildPreeditOverlay() : null),
                    ScreenOverlayRoot = screenOverlay,
                    BottomChromeRoot = bottomChrome,
                    BottomChromeRightRoot = bottomChromeRight,
                    BottomChromeLeftWidthCss = bottomChromeLeftW,
                    BottomChromeHeightCss = StatusBarHeightCss,
                }, target);
                var ok = frame.Presented;

                if (ok) { presented++; } else
                {
                    failures++;
                }
            }

            if (_maxFrames > 0 && presented >= _maxFrames)
            {
                window.Close();
            }

            // Pace to ~60 Hz AFTER presenting, so a cheap frame doesn't spin a CPU
            // core. The frame is already on screen, so this never starves the
            // display. A heavy frame that already took longer than the budget just
            // doesn't sleep. Skipped in --frames smoke mode.
            if (_maxFrames == 0)
            {
                const long frameBudgetMs = 16;
                var spent = clock.ElapsedMilliseconds - frameStart;
                if (spent < frameBudgetMs)
                {
                    System.Threading.Thread.Sleep((int)(frameBudgetMs - spent));
                }
            }
        };

        window.Run();

        SaveActive();
        wasmIslandPage?.Dispose();
        wasmIslandSession?.Dispose();
        foreach (var t in tabs)
        {
            t.Dispose();
        }

        Console.WriteLine(
            $"BROWSER OK: {presented} frames presented zero-copy ({failures} surface-reconfig frames)");
        return presented > 0 ? 0 : 1;

        // ── Local helpers ────────────────────────────────────────────────────
        void RefreshLayout()
        {
            if (page is null)
            {
                return;
            }

            var reOpts = NavOpts();
            var successor = session.RelayoutCurrent(page, reOpts);
            page.Dispose();
            page = successor;
            lastLayoutVersion = page.Document.LayoutInvalidationVersion;
            // Fragment geometry changed — drop the stale find index and highlight,
            // and the devtools panel (size / DOM may have changed).
            findFragments = null;
            findOverlay = null;
            devtoolsOverlay = null;
            PushA11y();
        }

        // The page viewport is the window minus the sidebar and toolbar.
        // FontSize is the document's root font size (1rem / the initial value for
        // "medium"). The Avalonia host passes 16f — the standard browser default —
        // so the page must too. Leaving it at the RenderOptions default (32f)
        // rendered every page at 2× its real size ("viewport contents too big").
        RenderOptions NavOpts() =>
            new(new Size((int)PageViewportWidth(logicalW), (int)PageViewportHeight(logicalH)), FontSize: 16f);

        double BottomIslandWidth(double currentLogicalW) =>
            Math.Max(1, PageViewportWidth((float)currentLogicalW));

        RenderOptions IslandOpts() =>
            new(new Size((int)BottomIslandWidth(logicalW), (int)StatusBarHeightCss));

        bool IsStatusBarPoint(double x, double y) =>
            x >= SidebarWidthCss && y >= logicalH - StatusBarHeightCss && y < logicalH;

        BlockBox IslandFallback(
            ref BlockBox? fallbackBox,
            ref string fallbackSig,
            double width,
            NativeTheme fallbackTheme,
            string label,
            string hint,
            bool error)
        {
            var sig = $"{width}|{themeMode}|{label}|{hint}|{error}";
            if (fallbackBox is null || sig != fallbackSig)
            {
                fallbackBox = BuildIslandFallbackBar(
                    (float)width,
                    fallbackTheme,
                    error ? NativeStatusState.Error : NativeStatusState.Loading,
                    label,
                    hint);
                fallbackSig = sig;
            }

            return fallbackBox;
        }

        void RefreshIslandLayout(
            BrowserSession? islandSession,
            ref LaidOutPage? islandPage,
            ref int islandLastLayoutVersion)
        {
            if (islandPage is null || islandSession is null)
            {
                return;
            }

            var successor = islandSession.RelayoutCurrent(islandPage, IslandOpts());
            islandPage.Dispose();
            islandPage = successor;
            islandLastLayoutVersion = islandPage.Document.LayoutInvalidationVersion;
        }

        void ApplyIslandNav(
            BrowserSession? islandSession,
            Result<LaidOutPage, RenderError> result,
            ref LaidOutPage? islandPage,
            ref int islandLastLayoutVersion,
            ref string islandStatusHint,
            ref bool islandLoadError,
            string label)
        {
            if (result.IsErr)
            {
                islandStatusHint = result.Error.Message;
                islandLoadError = true;
                Console.Error.WriteLine($"browser: {label} failed: {result.Error.Message}");
                return;
            }

            islandPage?.Dispose();
            islandPage = result.Value;
            islandLastLayoutVersion = islandPage.Document.LayoutInvalidationVersion;
            islandStatusHint = islandPage.Url ?? label;
            islandLoadError = false;
            if (islandPage.Viewport.Width != IslandOpts().Viewport.Width)
            {
                RefreshIslandLayout(islandSession, ref islandPage, ref islandLastLayoutVersion);
            }

            Console.WriteLine($"browser: loaded {label}");
        }

        void PollIslandNav(
            BrowserSession? islandSession,
            ref Task<Result<LaidOutPage, RenderError>>? pendingIslandNav,
            ref LaidOutPage? islandPage,
            ref int islandLastLayoutVersion,
            ref string islandStatusHint,
            ref bool islandLoadError,
            string label)
        {
            if (pendingIslandNav is not { IsCompleted: true })
            {
                return;
            }

            var task = pendingIslandNav;
            pendingIslandNav = null;
            try
            {
                ApplyIslandNav(islandSession, task.GetAwaiter().GetResult(), ref islandPage, ref islandLastLayoutVersion,
                    ref islandStatusHint, ref islandLoadError, label);
            }
            catch (Exception ex)
            {
                islandStatusHint = ex.Message;
                islandLoadError = true;
                Console.Error.WriteLine($"browser: {label} failed: {ex.Message}");
            }
        }

        void PumpIsland(
            BrowserSession? islandSession,
            ref LaidOutPage? islandPage,
            ref int islandLastLayoutVersion)
        {
            if (islandPage is null)
            {
                return;
            }

            islandPage.Document.DecayRecentMutations();
            islandPage.Scripting?.PumpFrame(clock.ElapsedMilliseconds);
            if (islandPage.Document.LayoutInvalidationVersion != islandLastLayoutVersion)
            {
                RefreshIslandLayout(islandSession, ref islandPage, ref islandLastLayoutVersion);
            }
        }

        void DispatchIslandClick(double x, double y)
        {
            DispatchIslandPageClick(wasmIslandSession, ref wasmIslandPage, ref wasmIslandLastLayoutVersion, x, y);
        }

        void DispatchIslandPageClick(
            BrowserSession? islandSession,
            ref LaidOutPage? islandPage,
            ref int islandLastLayoutVersion,
            double x,
            double y)
        {
            if (islandPage?.Scripting is null)
            {
                return;
            }

            var hit = BoxHitTester.HitTest(islandPage.Root, x, y, 0, 0, scrollOffsets: null);
            if (FindClickTarget(hit.Box) is not { } targetElement)
            {
                return;
            }

            if (islandPage.Scripting.DispatchEvent(targetElement,
                new MouseEvent("click", new EventInit(Bubbles: true, Cancelable: true))
                {
                    ClientX = x,
                    ClientY = y,
                    Button = 0,
                }))
            {
                RefreshIslandLayout(islandSession, ref islandPage, ref islandLastLayoutVersion);
            }
        }

        static Element? FindClickTarget(Starling.Layout.Box.Box? box)
        {
            for (var b = box; b is not null; b = b.Parent)
            {
                if (b.Element is Element el)
                {
                    return el;
                }
            }

            return null;
        }

        // Swap a freshly-laid-out page into the ACTIVE tab. On the initial load the
        // old page is null (the tab opened on a loading state). On a failed load the
        // old page stays visible (a blank window on a typo or dead link is worse).
        void ApplyNav(Result<LaidOutPage, RenderError> navResult)
        {
            if (navResult.IsErr)
            {
                lastNavError = true;
                Console.Error.WriteLine($"browser: nav failed: {navResult.Error.Message}");
                return;
            }

            lastNavError = false;
            var oldPage = page;
            renderer.ResetForNavigation();
            scrollY = 0;
            storeRootSyncedY = 0; // fresh page load = fresh store, root at 0
            focusedInput = null;
            preedit = "";
            urlBarFocused = false;
            findActive = false;
            findOverlay = null;
            findFragments = null;
            devtoolsOverlay = null;
            menuActive = false;
            menuOverlay = null;
            hoverElement = null;
            hoverOverrides = null;
            hoverScope.Clear();
            page = navResult.Value;
            oldPage?.Dispose();
            lastLayoutVersion = page.Document.LayoutInvalidationVersion;
            Console.WriteLine($"browser: loaded {page.Url}  height={page.DocumentHeight:F0}px");
            PushA11y();
        }

        // Apply a completed navigation on the main thread. The old page stays
        // visible until the new one is ready, so a navigation never flashes blank.
        void PollNav()
        {
            if (pendingNav is not { IsCompleted: true })
            {
                return;
            }

            var task = pendingNav;
            var tabIdx = pendingNavTab;
            pendingNav = null;
            pendingNavTab = -1;

            Result<LaidOutPage, RenderError> r;
            try { r = task.GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"browser: load failed: {ex.Message}");
                return; // keep the loading state / old page
            }

            if (tabIdx == activeIndex)
            {
                ApplyNav(r);
            }
            else if (tabIdx >= 0 && tabIdx < tabs.Count && r.IsOk)
            {
                // Finished for a background tab: stash it without disturbing the view.
                var t = tabs[tabIdx];
                t.Page?.Dispose();
                t.Page = r.Value;
                t.LastLayoutVersion = r.Value.Document.LayoutInvalidationVersion;
            }
        }

        void Navigate(string url)
        {
            loadingUrl = url;
            pendingNav = session.NavigateInteractiveAsync(url, NavOpts());
            pendingNavTab = activeIndex;
        }

        void GoBack()
        {
            if (!session.History.CanGoBack)
            {
                return;
            }

            pendingNav = session.BackInteractiveAsync(NavOpts());
            pendingNavTab = activeIndex;
        }

        void GoForward()
        {
            if (!session.History.CanGoForward)
            {
                return;
            }

            pendingNav = session.ForwardInteractiveAsync(NavOpts());
            pendingNavTab = activeIndex;
        }

        void Reload()
        {
            if (session.History.Current is null)
            {
                return;
            }

            pendingNav = session.ReloadInteractiveAsync(NavOpts());
            pendingNavTab = activeIndex;
        }

        // ── Tabs ───────────────────────────────────────────────────────────────

        // Copy the live view state back into the active Tab record.
        void SaveActive()
        {
            if (tabs.Count == 0)
            {
                return;
            }

            var t = tabs[activeIndex];
            t.Page = page;
            t.ScrollY = scrollY;
            t.FocusedInput = focusedInput;
            t.HoverElement = hoverElement;
            t.HoverOverrides = hoverOverrides;
            t.HoverScope = hoverScope;
            t.LastLayoutVersion = lastLayoutVersion;
        }

        // Load the active Tab record into the live locals. Layer caches key on the
        // document, so a tab switch resets them. URL-bar focus does not survive a
        // switch.
        void LoadActive()
        {
            var t = tabs[activeIndex];
            session = t.Session;
            page = t.Page;
            scrollY = t.ScrollY;
            focusedInput = t.FocusedInput;
            hoverElement = t.HoverElement;
            hoverOverrides = t.HoverOverrides;
            hoverScope = t.HoverScope;
            lastLayoutVersion = t.LastLayoutVersion;
            urlBarFocused = false;
            preedit = "";
            findActive = false;
            findOverlay = null;
            findFragments = null;
            devtoolsOverlay = null;
            menuActive = false;
            menuOverlay = null;
            SyncRootScrollToStore(); // this tab's page store may hold another tab's era
            renderer.ResetForNavigation();
            PushA11y();
        }

        void SwitchTab(int i)
        {
            if (i < 0 || i >= tabs.Count || i == activeIndex)
            {
                return;
            }

            SaveActive();
            activeIndex = i;
            LoadActive();
        }

        void NewTab(string url)
        {
            SaveActive();
            var s = new BrowserSession();
            var r = s.NavigateInteractiveAsync(url, NavOpts()).GetAwaiter().GetResult();
            if (r.IsErr)
            {
                Console.Error.WriteLine($"browser: new-tab nav failed: {r.Error.Message}");
                s.Dispose();
                return;
            }
            tabs.Add(new Tab(s)
            {
                Page = r.Value,
                LastLayoutVersion = r.Value.Document.LayoutInvalidationVersion,
            });
            activeIndex = tabs.Count - 1;
            LoadActive();
        }

        void CloseTab(int i)
        {
            if (i < 0 || i >= tabs.Count)
            {
                return;
            }

            if (tabs.Count == 1) { window.Close(); return; } // closing the last tab closes the window

            var closingActive = i == activeIndex;
            if (!closingActive)
            {
                SaveActive(); // keep the still-active tab's live state
            }

            var t = tabs[i];
            tabs.RemoveAt(i);
            t.Dispose();

            if (i < activeIndex)
            {
                activeIndex--;
            }
            else if (activeIndex >= tabs.Count)
            {
                activeIndex = tabs.Count - 1;
            }

            LoadActive();
        }

        // ── Find-in-page ─────────────────────────────────────────────────────

        void OpenFind()
        {
            if (page is null)
            {
                return;
            }

            findActive = true;
            findQuery = "";
            findCursor = -1;
            findMatchTotal = 0;
            findOverlay = null;
            findFragments = BoxHitTester.CollectFragments(page.Root);
            urlBarFocused = false;
        }

        void CloseFind()
        {
            findActive = false;
            findOverlay = null;
            findFragments = null;
        }

        // A semi-transparent highlight box at the matched fragment, in document
        // space, composited over the page (so it scrolls with the page).
        BlockBox BuildFindOverlay(BoxHitTester.PlacedFragment f, float contentW, double docH)
        {
            var html =
                $"<body style=\"margin:0;padding:0;position:relative;width:{contentW}px;height:{docH}px\">" +
                $"<div style=\"position:absolute;left:{f.X}px;top:{f.Y}px;" +
                $"width:{f.Width}px;height:{f.Height}px;background:rgba(255,213,0,0.45)\"></div></body>";
            return new Starling.Layout.LayoutEngine(new StyleEngine(), DefaultTextMeasurer.Instance)
                .LayoutDocument(HtmlParser.Parse(html), new LayoutSize(contentW, (float)Math.Max(1, docH)));
        }

        // Step to the next (dir +1) or previous (dir -1) fragment whose text
        // contains the query, scroll it into view, and build its highlight.
        void RunFind(int dir)
        {
            findOverlay = null;
            if (page is null || findQuery.Length == 0) { findMatchTotal = 0; return; }
            findFragments ??= BoxHitTester.CollectFragments(page.Root);
            var frags = findFragments;
            var n = frags.Count;

            findMatchTotal = 0;
            foreach (var fr in frags)
            {
                if (fr.Text.Contains(findQuery, StringComparison.OrdinalIgnoreCase))
                {
                    findMatchTotal++;
                }
            }

            if (n == 0 || findMatchTotal == 0) { findCursor = -1; return; }

            var start = findCursor + dir;
            for (var step = 0; step < n; step++)
            {
                var idx = ((start + dir * step) % n + n) % n;
                if (!frags[idx].Text.Contains(findQuery, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                findCursor = idx;
                var f = frags[idx];
                var viewportH = PageViewportHeight(logicalH);
                var maxScroll = Math.Max(0, page.DocumentHeight - viewportH);
                scrollY = Math.Clamp(f.Y - viewportH / 3, 0, maxScroll);
                SyncRootScrollToStore();
                findOverlay = BuildFindOverlay(f, PageViewportWidth(logicalW), page.DocumentHeight);
                break;
            }
        }

        // Underlined preedit drawn at the focused field's position, in document
        // space so it scrolls with the page. Approximate placement (field start);
        // glyph-accurate caret tracking is a refinement.
        BlockBox? BuildPreeditOverlay()
        {
            if (page is null || focusedInput is null || preedit.Length == 0)
            {
                return null;
            }

            if (FindAbs(page.Root, focusedInput, 0, 0) is not { } pos)
            {
                return null;
            }

            var html =
                $"<body style=\"margin:0;padding:0;position:relative;" +
                $"width:{PageViewportWidth(logicalW)}px;height:{page.DocumentHeight}px\">" +
                $"<div style=\"position:absolute;left:{pos.X + 4}px;top:{pos.Y + 2}px;" +
                "background:#fff3c4;color:#000;font-size:13px;text-decoration:underline;" +
                $"padding:0 2px;white-space:nowrap\">{EscapeHtml(preedit)}</div></body>";
            return new Starling.Layout.LayoutEngine(new StyleEngine(), DefaultTextMeasurer.Instance)
                .LayoutDocument(HtmlParser.Parse(html),
                    new LayoutSize(PageViewportWidth(logicalW), (float)Math.Max(1, page.DocumentHeight)));
        }

        // ── Context menu ─────────────────────────────────────────────────────

        void CloseMenu()
        {
            menuActive = false;
            menuOverlay = null;
        }

        BlockBox BuildMenuOverlay()
        {
            var sb = new StringBuilder();
            sb.Append($"<body style=\"margin:0;padding:0;position:relative;" +
                      $"width:{logicalW}px;height:{logicalH}px\">");
            sb.Append($"<div style=\"position:absolute;left:{menuX}px;top:{menuY}px;width:{MenuW}px;" +
                      "background:#2b2b2b;border:1px solid #555;border-radius:4px;" +
                      "font-family:sans-serif;font-size:13px;color:#e0e0e0;overflow:hidden\">");
            foreach (var it in menuItems)
            {
                sb.Append($"<div style=\"height:{MenuItemH}px;padding:0 12px;" +
                          $"display:flex;align-items:center\">{EscapeHtml(it.Label)}</div>");
            }

            sb.Append("</div></body>");
            return new Starling.Layout.LayoutEngine(new StyleEngine(), DefaultTextMeasurer.Instance)
                .LayoutDocument(HtmlParser.Parse(sb.ToString()), new LayoutSize(logicalW, logicalH));
        }

        void OpenMenuAt(double x, double y)
        {
            if (page is null)
            {
                return;
            }

            menuItems.Clear();

            // Link under the pointer (page area only) adds link actions.
            if (x >= SidebarWidthCss && y >= ChromeHeightCss)
            {
                var hit = BoxHitTester.HitTest(
                    page.Root, x - SidebarWidthCss, (y - ChromeHeightCss) + scrollY,
                    viewportX: 0, viewportY: scrollY, page.ScrollOffsetLookup, page.StickyShiftLookup);
                if (hit.LinkAnchor is { } a)
                {
                    var href = a.GetAttribute("href");
                    var target = string.IsNullOrEmpty(href) ? null : LinkResolver.Resolve(href, page.Url);
                    if (target is not null)
                    {
                        menuItems.Add(("Open Link", () => Navigate(target)));
                        menuItems.Add(("Copy Link Address", () => NativeClipboard.Set(glfwHandle, target)));
                    }
                }
            }

            if (session.History.CanGoBack)
            {
                menuItems.Add(("Back", GoBack));
            }

            if (session.History.CanGoForward)
            {
                menuItems.Add(("Forward", GoForward));
            }

            menuItems.Add(("Reload", Reload));

            var n = menuItems.Count;
            menuX = Math.Clamp(x, 0, Math.Max(0, logicalW - MenuW));
            menuY = Math.Clamp(y, 0, Math.Max(0, logicalH - n * MenuItemH));
            menuOverlay = BuildMenuOverlay();
            menuActive = true;
        }

        // The page-area placeholder shown while the first page loads.
        BlockBox BuildLoadingPage()
        {
            var w = PageViewportWidth(logicalW);
            var h = PageViewportHeight(logicalH);
            var html =
                $"<body style=\"margin:0;padding:0;background:#ffffff;font-family:sans-serif;" +
                $"width:{w}px;height:{h}px;display:flex;align-items:center;justify-content:center\">" +
                "<div style=\"font-size:15px;color:#888\">Loading…</div></body>";
            return new Starling.Layout.LayoutEngine(new StyleEngine(), DefaultTextMeasurer.Instance)
                .LayoutDocument(HtmlParser.Parse(html), new LayoutSize(w, (float)h));
        }

        // ── Devtools (read-only DOM inspector) ───────────────────────────────

        BlockBox BuildDevtoolsOverlay()
        {
            const double panelW = 380;
            var panelX = Math.Max(0, logicalW - panelW);
            panelX = Math.Max(SidebarWidthCss, panelX);
            var panelH = PageViewportHeight(logicalH);
            var maxRows = Math.Max(0, (int)((panelH - 30) / 16));

            var sb = new StringBuilder();
            sb.Append($"<body style=\"margin:0;padding:0;position:relative;" +
                      $"width:{logicalW}px;height:{logicalH}px\">");
            sb.Append($"<div style=\"position:absolute;left:{panelX}px;top:{ChromeHeightCss}px;" +
                      $"width:{panelW}px;height:{panelH}px;background:#1b1b1b;border-left:1px solid #444;" +
                      "font-family:monospace;font-size:12px;color:#cfcfcf;overflow:hidden\">");
            sb.Append("<div style=\"padding:4px 10px;color:#888;border-bottom:1px solid #333\">DOM</div>");

            var rows = 0;
            void Walk(Element el, int depth)
            {
                if (rows >= maxRows)
                {
                    return;
                }

                var tag = el.TagName.ToLowerInvariant();
                var label = "<" + tag;
                if (!string.IsNullOrEmpty(el.Id))
                {
                    label += "#" + el.Id;
                }

                var cls = el.GetAttribute("class");
                if (!string.IsNullOrWhiteSpace(cls))
                {
                    label += "." + string.Join('.', cls.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                }

                label += ">";
                sb.Append($"<div style=\"padding:1px 10px 1px {10 + depth * 14}px;" +
                          $"white-space:nowrap;overflow:hidden\">{EscapeHtml(label)}</div>");
                rows++;
                for (var c = el.FirstChild; c is not null; c = c.NextSibling)
                {
                    if (c is Element ce)
                    {
                        Walk(ce, depth + 1);
                    }
                }
            }
            if (page?.Document.DocumentElement is { } root)
            {
                Walk(root, 0);
            }

            sb.Append("</div></body>");
            return new Starling.Layout.LayoutEngine(new StyleEngine(), DefaultTextMeasurer.Instance)
                .LayoutDocument(HtmlParser.Parse(sb.ToString()), new LayoutSize(logicalW, logicalH));
        }

        // ── Hover + animation styling (mirrors WebviewPanel) ──────────────────

        // Re-cascade the hovered element's subtree plus its ancestor chain under a
        // :hover context, sampling any triggered transition at nowMs. Returns the
        // per-element override styles the painter overlays, or null when nothing
        // is hovered.
        Dictionary<Element, ComputedStyle>? BuildHoverOverrides(Element? hovered, long nowMs)
        {
            if (hovered is null || page is null)
            {
                return null;
            }

            var style = page.Style;
            var ctx = new SelectorMatchContext { HoveredElement = hovered };
            var result = new Dictionary<Element, ComputedStyle>();

            Recurse(hovered);
            // Hovering an element also hovers its ancestors (`.ancestor:hover`).
            for (var n = hovered.ParentNode; n is not null; n = n.ParentNode)
            {
                if (n is Element p && Compose(p) is { } ps)
                {
                    result[p] = ps;
                }
            }

            // Only elements whose :hover cascade actually changes a paint-relevant
            // property are overridden. Without this, hovering a big container (or the
            // body while the pointer sweeps) re-cascaded and overrode its WHOLE
            // subtree on every move — shadowing each element's animated style with the
            // hover sample, so animated/just-styled content flashed to its base state
            // (invisible) until the pointer stopped. Pruning to the genuinely
            // :hover-affected elements lets everyone else keep their animated style.
            return result.Count == 0 ? null : result;

            // The override style for `el` when :hover changes its paint, else null.
            // Relevance is judged from the STATIC cascade (with vs. without :hover) so a
            // running animation's per-frame sample doesn't make every animating element
            // look "changed"; the stored style still carries the animation sample.
            ComputedStyle? Compose(Element el)
            {
                var hoverStatic = style.Compute(el, ctx);
                // Prune the element when nothing the painter emits changes under
                // :hover (SamePaint value-compares, so identical cascades match).
                if (SamePaint(style.Compute(el), hoverStatic))
                {
                    return null;
                }

                return style.ComputeWithAnimations(el, nowMs, ctx);
            }

            void Recurse(Element el)
            {
                if (Compose(el) is { } s)
                {
                    result[el] = s;
                }

                for (var child = el.FirstChild; child is not null; child = child.NextSibling)
                {
                    if (child is Element c)
                    {
                        Recurse(c);
                    }
                }
            }

            // True when a and b paint identically for the properties the painter
            // emits (mirrors WebviewPanel.SamePaintProperties). Lets :hover relevance
            // be judged without re-rendering on CssValue identity churn.
            static bool SamePaint(ComputedStyle a, ComputedStyle b)
            {
                if (a.GetColor(PropertyId.Color) != b.GetColor(PropertyId.Color))
                {
                    return false;
                }

                if (a.GetColor(PropertyId.BackgroundColor) != b.GetColor(PropertyId.BackgroundColor))
                {
                    return false;
                }

                if (a.GetColor(PropertyId.BorderTopColor) != b.GetColor(PropertyId.BorderTopColor))
                {
                    return false;
                }

                if (a.GetColor(PropertyId.BorderRightColor) != b.GetColor(PropertyId.BorderRightColor))
                {
                    return false;
                }

                if (a.GetColor(PropertyId.BorderBottomColor) != b.GetColor(PropertyId.BorderBottomColor))
                {
                    return false;
                }

                if (a.GetColor(PropertyId.BorderLeftColor) != b.GetColor(PropertyId.BorderLeftColor))
                {
                    return false;
                }
                // Value-compare: two independent cascade runs (with vs. without
                // :hover) produce non-identical CssKeyword instances for the same
                // keyword, so ReferenceEquals would report a spurious change and
                // pull every element into the override set (the invisibility bug).
                if (!Equals(a.Get(PropertyId.TextDecoration), b.Get(PropertyId.TextDecoration)))
                {
                    return false;
                }

                if (!Equals(a.Get(PropertyId.Transform), b.Get(PropertyId.Transform)))
                {
                    return false;
                }

                if (!Equals(a.Get(PropertyId.Opacity), b.Get(PropertyId.Opacity)))
                {
                    return false;
                }

                return true;
            }
        }

        // Pointer moved onto a new element (or off the page). Advance the clock,
        // rebuild the overrides, and re-cascade elements leaving the hover scope so
        // their reverse transition registers.
        void UpdateHover(Element? newHover)
        {
            if (page is null || ReferenceEquals(newHover, hoverElement))
            {
                return;
            }

            hoverElement = newHover;

            var nowMs = clock.ElapsedMilliseconds;
            session.PrepareAnimationFrame(page, nowMs); // tick before re-cascade
            animClockMs = nowMs;

            var newOverrides = BuildHoverOverrides(hoverElement, nowMs);
            var newScope = newOverrides is null ? null : new HashSet<Element>(newOverrides.Keys);

            if (hoverScope.Count > 0)
            {
                var newCtx = hoverElement is null
                    ? null
                    : new SelectorMatchContext { HoveredElement = hoverElement };
                foreach (var el in hoverScope)
                {
                    if (newScope is not null && newScope.Contains(el))
                    {
                        continue;
                    }

                    page.Style.ComputeWithAnimations(el, nowMs, newCtx);
                }
            }

            hoverScope = newScope ?? new HashSet<Element>();
            hoverOverrides = newOverrides;
        }

        // Per-box style the painter overlays: the :hover override if any, else the
        // sampled animation/transition style, else null (use the box's own style).
        ComputedStyle? StyleOverride(Box box)
        {
            if (hoverOverrides is not null && ResolveOverride(box, hoverOverrides) is { } ov)
            {
                return ov;
            }

            if (page is null || box.Element is not { } el)
            {
                return null;
            }

            foreach (var _ in page.Style.AnimationEngine.ActiveProperties(el))
            {
                return page.Style.ComputeWithAnimations(el, animClockMs);
            }

            foreach (var _ in page.Style.TransitionEngine.ActiveProperties(el))
            {
                return page.Style.ComputeWithAnimations(el, animClockMs);
            }

            return null;
        }

        // Look up a box's hover override; text/anonymous boxes inherit from the
        // nearest ancestor box whose element is in the map.
        static ComputedStyle? ResolveOverride(Box box, Dictionary<Element, ComputedStyle> overrides)
        {
            if (box.Element is { } el && overrides.TryGetValue(el, out var direct))
            {
                return direct;
            }

            for (var p = box.Parent; p is not null; p = p.Parent)
            {
                if (p.Element is { } pel && overrides.TryGetValue(pel, out var inherited))
                {
                    return inherited;
                }
            }

            return null;
        }
    }

    // ── Chrome models ─────────────────────────────────────────────────────────

    private sealed record NativeBookmark(string Id, string Host, string Title, string Url);

    // ── Tab ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// One browser tab: its own <see cref="BrowserSession"/> (independent history
    /// + connection pool) plus the per-tab view state the window mirrors into its
    /// live locals while the tab is active.
    /// </summary>
    private sealed class Tab : IDisposable
    {
        public Tab(BrowserSession session) => Session = session;

        public BrowserSession Session { get; }
        public LaidOutPage? Page;
        public double ScrollY;
        public Element? FocusedInput;
        public Element? HoverElement;
        public Dictionary<Element, ComputedStyle>? HoverOverrides;
        public HashSet<Element> HoverScope = new();
        public int LastLayoutVersion = -1;

        public void Dispose()
        {
            Page?.Dispose();
            Session.Dispose();
        }
    }

    // ── Per-frame layer-promotion predicate (mirrors WebviewPanel) ────────────

    /// <summary>Finds an element's box and its absolute document position by
    /// accumulating parent-relative frames. Used to place the IME preedit at the
    /// focused field.</summary>
    private static (double X, double Y)? FindAbs(Starling.Layout.Box.Box box, Element el, double ox, double oy)
    {
        var x = ox + box.Frame.X;
        var y = oy + box.Frame.Y;
        if (ReferenceEquals(box.Element, el))
        {
            return (x, y);
        }

        foreach (var child in box.Children)
        {
            if (FindAbs(child, el, x, y) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static bool IsAnimatingLayerRoot(LaidOutPage page, Starling.Layout.Box.Box box)
    {
        if (box.Element is not { } el)
        {
            return false;
        }

        if (page.Document.WasRecentlyMutated(el))
        {
            return true;
        }

        foreach (var _ in page.Style.AnimationEngine.ActiveProperties(el))
        {
            return true;
        }

        foreach (var _ in page.Style.TransitionEngine.ActiveProperties(el))
        {
            return true;
        }

        return false;
    }

    // ── Multi-window (process per window) ─────────────────────────────────────

    /// <summary>
    /// Opens another browser window by relaunching this shell as a fresh process.
    /// A new OS process owns its own GLFW window and main thread, which sidesteps
    /// macOS's requirement that windowing stay on the main thread (per-thread
    /// windows are not an option there). Each window is independent — its own tabs,
    /// history, and session.
    /// </summary>
    private static void LaunchNewWindow()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                return;
            }

            var psi = new System.Diagnostics.ProcessStartInfo { FileName = exe, UseShellExecute = false };

            // Under `dotnet run`/`dotnet App.dll`, ProcessPath is the dotnet host —
            // re-pass the managed entry dll so the child runs this app, not the host.
            var argv0 = Environment.GetCommandLineArgs() is { Length: > 0 } a ? a[0] : "";
            if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                && argv0.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                psi.ArgumentList.Add(argv0);
            }

            psi.ArgumentList.Add("--browser");
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"browser: new window failed: {ex.Message}");
        }
    }

    // ── Cursor mapping ────────────────────────────────────────────────────────

    private static void SetCursor(ICursor cursor, string cssKeyword, ILogger log)
    {
        var sc = cssKeyword switch
        {
            "pointer" => StandardCursor.Hand,
            "text" => StandardCursor.IBeam,
            "crosshair" => StandardCursor.Crosshair,
            "wait" => StandardCursor.Wait,
            "not-allowed" => StandardCursor.NotAllowed,
            "ew-resize" => StandardCursor.HResize,
            "ns-resize" => StandardCursor.VResize,
            "nwse-resize" => StandardCursor.NwseResize,
            "nesw-resize" => StandardCursor.NeswResize,
            "move" => StandardCursor.ResizeAll,
            _ => StandardCursor.Default,
        };
        try
        {
            cursor.StandardCursor = sc;
        }
        catch (Exception ex)
        {
            // Cursor API is best-effort; suppress if the backend doesn't support it.
            NativeBrowserWindowLog.SetCursorFailed(log, ex, cssKeyword);
        }
    }
}

internal static partial class NativeBrowserWindowLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to delete temp file '{Path}' (best-effort cleanup)")]
    public static partial void TempFileDeleteFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cursor API rejected CSS keyword '{CssKeyword}' (best-effort; backend may not support it)")]
    public static partial void SetCursorFailed(ILogger logger, Exception ex, string cssKeyword);
}
