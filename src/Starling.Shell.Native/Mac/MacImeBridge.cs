using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Starling.Shell.Native.Mac;

/// <summary>
/// Feeds inline IME preedit (marked text) to the shell on macOS by intercepting
/// the GLFW content view's <c>NSTextInputClient</c> methods. Standard GLFW commits
/// composed characters through its character callback (which the shell already
/// inserts), but it does not surface the in-progress preedit. This bridge swizzles
/// <c>setMarkedText:selectedRange:replacementRange:</c> and <c>unmarkText</c> on
/// <c>GLFWContentView</c> so the composing string reaches the shell, which draws it
/// underlined at the focused field. The original implementations are still called,
/// so GLFW's own bookkeeping and the commit path are unchanged.
/// </summary>
/// <remarks>
/// Experimental and opt-in (the shell installs it only when
/// <c>STARLING_IME_PREEDIT=1</c>). It is native, per-platform, and could not be
/// exercised in a headless harness — there is no display or input method there —
/// so it ships off by default and isolated from the working commit-style path.
/// One window per process (the shell's multi-window model), so a single static
/// handler pair is sufficient.
/// </remarks>
internal static unsafe class MacImeBridge
{
    private static Action<string>? _onPreedit;
    private static Action? _onUnmark;
    private static nint _origSetMarked;
    private static nint _origUnmark;
    private static bool _installed;
    private static ILogger _log = NullLogger.Instance;

    /// <summary>
    /// Swizzles the GLFW content view so preedit changes call
    /// <paramref name="onPreedit"/> and composition end calls
    /// <paramref name="onUnmark"/>. Returns false (a no-op) off macOS, when the
    /// GLFW view class or method is absent, or if already installed.
    /// </summary>
    public static bool Install(Action<string> onPreedit, Action onUnmark, ILoggerFactory? loggerFactory = null)
    {
        if (_installed || !OperatingSystem.IsMacOS())
        {
            return false;
        }

        _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger(typeof(MacImeBridge));
        try
        {
            var cls = ObjC.GetClass("GLFWContentView");
            if (cls == 0)
            {
                return false;
            }

            var setMarked = ObjC.ClassGetInstanceMethod(cls, ObjC.Sel("setMarkedText:selectedRange:replacementRange:"));
            if (setMarked == 0)
            {
                return false;
            }

            _onPreedit = onPreedit;
            _onUnmark = onUnmark;

            _origSetMarked = ObjC.MethodGetImplementation(setMarked);
            ObjC.MethodSetImplementation(setMarked,
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, NSRange, NSRange, void>)&SetMarkedThunk);

            var unmark = ObjC.ClassGetInstanceMethod(cls, ObjC.Sel("unmarkText"));
            if (unmark != 0)
            {
                _origUnmark = ObjC.MethodGetImplementation(unmark);
                ObjC.MethodSetImplementation(unmark,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&UnmarkThunk);
            }

            _installed = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // void setMarkedText:(id)string selectedRange:(NSRange) replacementRange:(NSRange)
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void SetMarkedThunk(nint self, nint cmd, nint str, NSRange selected, NSRange replacement)
    {
        try { _onPreedit?.Invoke(ObjC.StringFromAny(str) ?? ""); }
        catch (Exception ex) { /* never let a managed fault unwind into AppKit */ MacImeBridgeLog.SetMarkedThunkFailed(_log, ex); }

        if (_origSetMarked != 0)
        {
            ((delegate* unmanaged[Cdecl]<nint, nint, nint, NSRange, NSRange, void>)_origSetMarked)(
                self, cmd, str, selected, replacement);
        }
    }

    // void unmarkText
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void UnmarkThunk(nint self, nint cmd)
    {
        try { _onUnmark?.Invoke(); }
        catch (Exception ex) { /* never let a managed fault unwind into AppKit */ MacImeBridgeLog.UnmarkThunkFailed(_log, ex); }

        if (_origUnmark != 0)
        {
            ((delegate* unmanaged[Cdecl]<nint, nint, void>)_origUnmark)(self, cmd);
        }
    }
}

internal static partial class MacImeBridgeLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "IME setMarkedText thunk threw; suppressed to avoid unwinding into AppKit")]
    public static partial void SetMarkedThunkFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "IME unmarkText thunk threw; suppressed to avoid unwinding into AppKit")]
    public static partial void UnmarkThunkFailed(ILogger logger, Exception ex);
}
