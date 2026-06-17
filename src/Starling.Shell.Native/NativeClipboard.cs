using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.GLFW;

namespace Starling.Shell.Native;

/// <summary>
/// Phase-4 native service: the system clipboard, via GLFW
/// (<c>glfwGetClipboardString</c>/<c>glfwSetClipboardString</c>). The first of
/// the native services the Avalonia shell provides for free; IME and
/// accessibility follow (tracked as work packages). Best-effort — returns
/// null / no-ops if the window isn't GLFW-backed.
/// </summary>
internal static unsafe class NativeClipboard
{
    private static readonly Glfw _glfw = Glfw.GetApi();

    /// <summary>Reads the clipboard text, or null when empty/unavailable.</summary>
    public static string? Get(nint glfwWindow)
    {
        if (glfwWindow == 0)
        {
            return null;
        }

        try
        {
            var s = _glfw.GetClipboardString((WindowHandle*)glfwWindow);
            return string.IsNullOrEmpty(s) ? null : s;
        }
        catch (Exception ex)
        {
            NativeClipboardLog.GetFailed(NullLogger.Instance, ex);
            return null;
        }
    }

    /// <summary>Writes text to the clipboard. No-op when unavailable.</summary>
    public static void Set(nint glfwWindow, string text)
    {
        if (glfwWindow == 0)
        {
            return;
        }

        try
        {
            _glfw.SetClipboardString((WindowHandle*)glfwWindow, text);
        }
        catch (Exception ex)
        {
            // Best-effort: clipboard access can fail on a sandboxed host.
            NativeClipboardLog.SetFailed(NullLogger.Instance, ex);
        }
    }
}

internal static partial class NativeClipboardLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Clipboard read failed (best-effort)")]
    public static partial void GetFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Clipboard write failed (best-effort)")]
    public static partial void SetFailed(ILogger logger, Exception ex);
}
