using System.Text.Json;
using AwesomeAssertions;
using Starling.Gui.Mcp;

namespace Starling.Gui.Tests;

/// <summary>
/// Guards the MCP browser tool catalog: the descriptor JSON
/// must stay valid and the browser automation tools must be registered. The
/// server re-parses <c>GetToolDescriptorsJson</c> per request, so a malformed
/// descriptor would break tools/list at runtime.
/// </summary>
[TestClass]
public sealed class BrowserToolsTests
{
    [TestMethod]
    public void Descriptors_parse_and_include_the_browser_automation_tools()
    {
        var tools = new BrowserTools(new ThrowingDispatcher());

        using var doc = JsonDocument.Parse(tools.GetToolDescriptorsJson());
        var names = doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("name").GetString())
            .ToList();

        names.Should().Contain(new[]
        {
            "browser_press_key",
            "browser_wait",
            "browser_click_selector",
            "browser_scroll_to",
            "browser_screenshot_viewport",
            "browser_query",
            "browser_console",
            "browser_network",
            "browser_find",
            "browser_clipboard",
            "browser_bookmarks",
            "browser_scroll",
            "browser_highlight",
            "browser_select",
            "browser_focus",
        });

        // Every declared descriptor must be dispatchable, and vice versa.
        foreach (var name in names)
        {
            tools.HasTool(name!).Should().BeTrue($"'{name}' is declared so it must be dispatchable");
        }

        tools.HasTool("browser_highlight").Should().BeTrue();
        tools.HasTool("browser_select").Should().BeTrue();
        tools.HasTool("browser_focus").Should().BeTrue();
        tools.HasTool("browser_scroll").Should().BeTrue();
        tools.HasTool("browser_press_key").Should().BeTrue();
        tools.HasTool("browser_wait").Should().BeTrue();
    }

    [TestMethod]
    public void Highlight_descriptor_requires_a_selector()
    {
        var tools = new BrowserTools(new ThrowingDispatcher());
        using var doc = JsonDocument.Parse(tools.GetToolDescriptorsJson());
        var highlight = doc.RootElement.EnumerateArray()
            .First(e => e.GetProperty("name").GetString() == "browser_highlight");

        var required = highlight.GetProperty("inputSchema").GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        required.Should().Contain("selector");
    }

    [TestMethod]
    public async Task Scroll_invocation_dispatches_delta_args()
    {
        var dispatcher = new CapturingDispatcher();
        var tools = new BrowserTools(dispatcher);
        using var doc = JsonDocument.Parse("""{"deltaX":12.5,"deltaY":300}""");

        var result = await tools.InvokeAsync("browser_scroll", doc.RootElement, CancellationToken.None);

        result.IsError.Should().BeFalse();
        dispatcher.ScrollDelta.Should().Be((12.5, 300));
    }

    [TestMethod]
    public async Task Required_number_arguments_are_strict()
    {
        var tools = new BrowserTools(new ThrowingDispatcher());
        using var doc = JsonDocument.Parse("""{"deltaX":12.5}""");

        var act = () => tools.InvokeAsync("browser_scroll", doc.RootElement, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*deltaY*");
    }

    [TestMethod]
    public async Task Required_string_arguments_are_strict()
    {
        var tools = new BrowserTools(new ThrowingDispatcher());
        using var doc = JsonDocument.Parse("""{}""");

        var act = () => tools.InvokeAsync("browser_navigate", doc.RootElement, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*url*");
    }

    [TestMethod]
    public async Task Press_key_invocation_dispatches_modifier_args()
    {
        var dispatcher = new CapturingDispatcher();
        var tools = new BrowserTools(dispatcher);
        using var doc = JsonDocument.Parse("""{"key":"Tab","shift":true,"ctrl":true,"alt":false,"meta":true}""");

        var result = await tools.InvokeAsync("browser_press_key", doc.RootElement, CancellationToken.None);

        result.IsError.Should().BeFalse();
        dispatcher.PressedKey.Should().Be(("Tab", true, true, false, true));
    }

    [TestMethod]
    public async Task Work_queue_dispatches_tool_calls_when_drained()
    {
        var queue = new BrowserControlWorkQueue();
        var controller = new CapturingController();
        queue.Attach(controller);

        var move = queue.MoveMouseAsync(42, 75, CancellationToken.None);
        move.IsCompleted.Should().BeFalse("the native host owns when queued work runs");

        queue.Drain();
        var result = await move;

        result.Ok.Should().BeTrue();
        result.Detail.Should().Be("moved");
        controller.MousePosition.Should().Be((42, 75));
    }

    // The catalog only needs a dispatcher to construct; these tests never invoke it.
    private class ThrowingDispatcher : IBrowserControlDispatcher
    {
        public virtual Task<BrowserControlResult> NavigateAsync(string url, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<BrowserControlResult> BackAsync(CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<BrowserControlResult> ForwardAsync(CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<BrowserControlResult> ReloadAsync(CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<BrowserControlResult> ScreenshotAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<BrowserControlResult> ScreenshotViewportAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<BrowserControlResult> InspectAsync(bool includeHtml, string? logPath, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<BrowserControlResult> ConsoleAsync(string? minLevel, int limit, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<BrowserControlResult> NetworkAsync(int limit, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<BrowserControlResult> ClickAsync(double x, double y, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<BrowserControlResult> ClickSelectorAsync(string selector, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<BrowserControlResult> MoveMouseAsync(double x, double y, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<BrowserControlResult> ScrollAsync(double deltaX, double deltaY, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<BrowserControlResult> ScrollToAsync(double? x, double? y, string? selector, string? position, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<BrowserControlResult> PressKeyAsync(string key, bool shift, bool ctrl, bool alt, bool meta, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<BrowserControlResult> TypeTextAsync(string text, bool submit, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<BrowserControlResult> ResizeAsync(double width, double height, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<BrowserControlResult> WaitAsync(string state, string? value, int timeoutMs, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<BrowserControlResult> QueryAsync(string selector, bool includeText, bool includeHtml, int limit, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<BrowserControlResult> HighlightAsync(string selector, string? color, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<BrowserControlResult> SelectElementAsync(string selector, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<BrowserControlResult> FocusElementAsync(string selector, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<BrowserControlResult> FindAsync(string query, string direction, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<BrowserControlResult> ClipboardAsync(string action, string? text, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<BrowserControlResult> BookmarksAsync(string? id, CancellationToken ct) => throw new NotSupportedException();
        public virtual Task<BrowserControlResult> ComputedStyleAsync(string selector, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class CapturingDispatcher : ThrowingDispatcher
    {
        public (double X, double Y) ScrollDelta { get; private set; }
        public (string Key, bool Shift, bool Ctrl, bool Alt, bool Meta) PressedKey { get; private set; }

        public override Task<BrowserControlResult> ScrollAsync(double deltaX, double deltaY, CancellationToken ct)
        {
            ScrollDelta = (deltaX, deltaY);
            return Task.FromResult(BrowserControlResult.Success(
                url: "about:blank",
                title: "Test",
                canGoBack: false,
                canGoForward: false,
                isBusy: false,
                detail: "ok"));
        }

        public override Task<BrowserControlResult> PressKeyAsync(string key, bool shift, bool ctrl, bool alt, bool meta, CancellationToken ct)
        {
            PressedKey = (key, shift, ctrl, alt, meta);
            return Task.FromResult(BrowserControlResult.Success(
                url: "about:blank",
                title: "Test",
                canGoBack: false,
                canGoForward: false,
                isBusy: false,
                detail: "ok"));
        }
    }

    private sealed class CapturingController : IBrowserController
    {
        public (double X, double Y) MousePosition { get; private set; }

        public Task<BrowserControlResult> MoveMouseFromToolAsync(double x, double y, CancellationToken ct)
        {
            MousePosition = (x, y);
            return Task.FromResult(BrowserControlResult.Success(
                url: "about:blank",
                title: "Test",
                canGoBack: false,
                canGoForward: false,
                detail: "moved"));
        }

        public Task<BrowserControlResult> NavigateFromToolAsync(string url, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowserControlResult> BackFromToolAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowserControlResult> ForwardFromToolAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowserControlResult> ReloadFromToolAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowserControlResult> ScreenshotFromToolAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowserControlResult> ScreenshotViewportFromToolAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowserControlResult> InspectFromToolAsync(bool includeHtml, string? logPath, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowserControlResult> ConsoleFromToolAsync(string? minLevel, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowserControlResult> NetworkFromToolAsync(int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowserControlResult> ClickFromToolAsync(double x, double y, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowserControlResult> ClickSelectorFromToolAsync(string selector, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowserControlResult> ScrollFromToolAsync(double deltaX, double deltaY, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowserControlResult> ScrollToFromToolAsync(double? x, double? y, string? selector, string? position, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowserControlResult> PressKeyFromToolAsync(string key, bool shift, bool ctrl, bool alt, bool meta, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowserControlResult> TypeTextFromToolAsync(string text, bool submit, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowserControlResult> ResizeFromToolAsync(double width, double height, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowserControlResult> WaitFromToolAsync(string state, string? value, int timeoutMs, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowserControlResult> QueryFromToolAsync(string selector, bool includeText, bool includeHtml, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowserControlResult> HighlightFromToolAsync(string selector, string? color, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowserControlResult> SelectElementFromToolAsync(string selector, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowserControlResult> FocusElementFromToolAsync(string selector, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowserControlResult> FindFromToolAsync(string query, string direction, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowserControlResult> ClipboardFromToolAsync(string action, string? text, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowserControlResult> BookmarksFromToolAsync(string? id, CancellationToken ct) => throw new NotSupportedException();
        public Task<BrowserControlResult> ComputedStyleFromToolAsync(string selector, CancellationToken ct) => throw new NotSupportedException();
    }
}
