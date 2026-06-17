using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Gui.Core.Rendering;

namespace Starling.Gui.Controls;

internal static partial class PageSurfaceHostLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Win32 child window creation failed")]
    public static partial void Win32CreateFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Win32 child window destroy failed")]
    public static partial void Win32DestroyFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "X11 display open failed")]
    public static partial void X11OpenDisplayFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "X11 child window creation failed")]
    public static partial void X11CreateChildFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "X11 window destroy failed")]
    public static partial void X11DestroyFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "X11 display close failed")]
    public static partial void X11CloseDisplayFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "macOS Metal view creation failed")]
    public static partial void MacMetalCreateFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "macOS Metal contentsScale update failed")]
    public static partial void MacMetalSetScaleFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "macOS ObjC release failed")]
    public static partial void MacMetalReleaseFailed(ILogger logger, Exception ex);
}

/// <summary>
/// Embeds a native GPU surface in the page region while the chrome stays in
/// Avalonia. macOS uses an <c>NSView</c> with a <c>CAMetalLayer</c>. Windows uses
/// a child <c>HWND</c>. Linux uses an X11 child window.
/// </summary>
internal sealed class PageSurfaceHost : NativeControlHost
{
    private nint _view;
    private nint _metalLayer;
    private nint _win32Hwnd;
    private nint _win32Hinstance;
    private nint _x11Display;
    private nint _x11Window;
    private NativePageSurface? _surface;

    /// <summary>The window's device-pixel scale, applied to the metal layer's contentsScale.</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>Raised after the native surface handle becomes available (control attached).</summary>
    public event Action? SurfaceReady;

    /// <summary>True once a presentable native surface handle exists.</summary>
    public bool HasSurface => _surface.HasValue;

    public NativePageSurface? Surface => _surface;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (OperatingSystem.IsMacOS())
        {
            _view = MacMetal.CreatePassthroughMetalView(Scale, out _metalLayer);
            if (_view != 0)
            {
                if (_metalLayer != 0)
                {
                    _surface = NativePageSurface.MetalLayer(_metalLayer);
                    NotifySurfaceReady();
                }
                return new PlatformHandle(_view, "NSView");
            }
        }

        if (OperatingSystem.IsWindows() &&
            parent.Handle != 0 &&
            string.Equals(parent.HandleDescriptor, "HWND", StringComparison.Ordinal))
        {
            _win32Hinstance = Win32PageSurface.GetCurrentModuleHandle();
            _win32Hwnd = Win32PageSurface.CreateChild(parent.Handle, _win32Hinstance);
            if (_win32Hwnd != 0)
            {
                _surface = NativePageSurface.WindowsHwnd(_win32Hwnd, _win32Hinstance);
                NotifySurfaceReady();
                return new PlatformHandle(_win32Hwnd, "HWND");
            }
        }

        if (OperatingSystem.IsLinux() &&
            parent.Handle != 0 &&
            string.Equals(parent.HandleDescriptor, "XID", StringComparison.Ordinal))
        {
            _x11Display = X11PageSurface.OpenDisplay();
            if (_x11Display != 0)
            {
                _x11Window = X11PageSurface.CreateChild(_x11Display, parent.Handle);
                if (_x11Window != 0)
                {
                    _surface = NativePageSurface.XlibWindow(
                        _x11Display,
                        X11PageSurface.ToWindowId(_x11Window));
                    NotifySurfaceReady();
                    return new PlatformHandle(_x11Window, "XID");
                }

                X11PageSurface.CloseDisplay(_x11Display);
                _x11Display = 0;
            }
        }

        return base.CreateNativeControlCore(parent);
    }

    private void NotifySurfaceReady()
        => Dispatcher.UIThread.Post(() => SurfaceReady?.Invoke());

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (OperatingSystem.IsMacOS() && _view != 0)
        {
            MacMetal.Release(_metalLayer);
            MacMetal.Release(_view);
            _surface = null;
            _metalLayer = 0;
            _view = 0;
            return;
        }

        if (OperatingSystem.IsWindows() && _win32Hwnd != 0)
        {
            Win32PageSurface.Destroy(_win32Hwnd);
            _surface = null;
            _win32Hwnd = 0;
            _win32Hinstance = 0;
            return;
        }

        if (OperatingSystem.IsLinux() && _x11Display != 0)
        {
            if (_x11Window != 0)
            {
                X11PageSurface.Destroy(_x11Display, _x11Window);
            }
            X11PageSurface.CloseDisplay(_x11Display);
            _surface = null;
            _x11Display = 0;
            _x11Window = 0;
            return;
        }

        _surface = null;
        base.DestroyNativeControlCore(control);
    }

    /// <summary>Updates the metal layer's contentsScale (call on DPI change).</summary>
    public void UpdateScale(double scale)
    {
        Scale = scale;
        if (_metalLayer != 0)
        {
            MacMetal.SetContentsScale(_metalLayer, scale);
        }
    }
}

internal static class Win32PageSurface
{
    // Static helper class — logger sourced from NullLoggerFactory until the host
    // integrator wires a real factory via PageSurfaceHost.LoggerFactory.
    private static readonly ILogger Log =
        NullLoggerFactory.Instance.CreateLogger(typeof(Win32PageSurface));

    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsClipChildren = 0x02000000;
    private const uint WsClipSiblings = 0x04000000;

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint exStyle,
        string className,
        string? windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    public static nint GetCurrentModuleHandle() => GetModuleHandle(null);

    public static nint CreateChild(nint parent, nint hinstance)
    {
        try
        {
            return CreateWindowEx(
                0,
                "STATIC",
                null,
                WsChild | WsVisible | WsClipChildren | WsClipSiblings,
                0,
                0,
                1,
                1,
                parent,
                0,
                hinstance,
                0);
        }
        catch (Exception ex)
        {
            PageSurfaceHostLog.Win32CreateFailed(Log, ex);
            return 0;
        }
    }

    public static void Destroy(nint hwnd)
    {
        try
        {
            DestroyWindow(hwnd);
        }
        catch (Exception ex)
        {
            /* best effort */
            PageSurfaceHostLog.Win32DestroyFailed(Log, ex);
        }
    }
}

internal static class X11PageSurface
{
    // Static helper class — logger sourced from NullLoggerFactory until the host
    // integrator wires a real factory via PageSurfaceHost.LoggerFactory.
    private static readonly ILogger Log =
        NullLoggerFactory.Instance.CreateLogger(typeof(X11PageSurface));

    private const string X11 = "libX11.so.6";

    [DllImport(X11)]
    private static extern nint XOpenDisplay(string? displayName);

    [DllImport(X11)]
    private static extern int XCloseDisplay(nint display);

    [DllImport(X11)]
    private static extern nint XCreateSimpleWindow(
        nint display,
        nint parent,
        int x,
        int y,
        uint width,
        uint height,
        uint borderWidth,
        ulong border,
        ulong background);

    [DllImport(X11)]
    private static extern int XDestroyWindow(nint display, nint window);

    [DllImport(X11)]
    private static extern int XFlush(nint display);

    public static nint OpenDisplay()
    {
        try
        {
            return XOpenDisplay(null);
        }
        catch (Exception ex)
        {
            PageSurfaceHostLog.X11OpenDisplayFailed(Log, ex);
            return 0;
        }
    }

    public static nint CreateChild(nint display, nint parent)
    {
        try
        {
            var window = XCreateSimpleWindow(display, parent, 0, 0, 1, 1, 0, 0, 0);
            if (window != 0)
            {
                XFlush(display);
            }
            return window;
        }
        catch (Exception ex)
        {
            PageSurfaceHostLog.X11CreateChildFailed(Log, ex);
            return 0;
        }
    }

    public static ulong ToWindowId(nint window) => (ulong)(nuint)window;

    public static void Destroy(nint display, nint window)
    {
        try
        {
            XDestroyWindow(display, window);
            XFlush(display);
        }
        catch (Exception ex)
        {
            /* best effort */
            PageSurfaceHostLog.X11DestroyFailed(Log, ex);
        }
    }

    public static void CloseDisplay(nint display)
    {
        try
        {
            XCloseDisplay(display);
        }
        catch (Exception ex)
        {
            /* best effort */
            PageSurfaceHostLog.X11CloseDisplayFailed(Log, ex);
        }
    }
}

/// <summary>
/// Minimal Objective-C runtime interop for a click-through, metal-layer-backed
/// <c>NSView</c>. Confined to macOS.
/// </summary>
internal static unsafe class MacMetal
{
    // Static helper class — logger sourced from NullLoggerFactory until the host
    // integrator wires a real factory via PageSurfaceHost.LoggerFactory.
    private static readonly ILogger Log =
        NullLoggerFactory.Instance.CreateLogger(typeof(MacMetal));

    private const string Objc = "/usr/lib/libobjc.A.dylib";

    [DllImport(Objc)]
    private static extern nint objc_getClass(string name);

    [DllImport(Objc)]
    private static extern nint sel_registerName(string name);

    [DllImport(Objc)]
    private static extern nint objc_allocateClassPair(nint superclass, string name, nint extraBytes);

    [DllImport(Objc)]
    private static extern void objc_registerClassPair(nint cls);

    [DllImport(Objc)]
    private static extern nint objc_lookUpClass(string name);

    [DllImport(Objc)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool class_addMethod(nint cls, nint sel, nint imp, string types);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern nint MsgSend(nint receiver, nint sel);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSendVoidPtr(nint receiver, nint sel, nint arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSendVoidBool(nint receiver, nint sel, [MarshalAs(UnmanagedType.I1)] bool arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSendVoidDouble(nint receiver, nint sel, double arg);

    private static nint _passthroughClass;

    // Fields are never read in managed code — they exist only to give objc_msgSend
    // the correct by-value calling convention for hitTest:'s NSPoint argument.
#pragma warning disable CS0649
    private struct CGPoint
    {
        public double X;
        public double Y;
    }
#pragma warning restore CS0649

    // hitTest: implementation that always returns nil, so AppKit continues the hit
    // search to the views behind this overlay (the Avalonia content view), letting
    // pointer/scroll events reach the Avalonia page canvas.
    [UnmanagedCallersOnly]
    private static nint HitTestReturnNil(nint self, nint sel, CGPoint point) => 0;

    private static nint PassthroughViewClass()
    {
        if (_passthroughClass != 0)
        {
            return _passthroughClass;
        }

        // A second run in the same process (rare) would find the class already
        // registered; reuse it rather than re-allocating (which returns nil).
        var existing = objc_lookUpClass("StarlingPassthroughMetalView");
        if (existing != 0)
        {
            _passthroughClass = existing;
            return _passthroughClass;
        }

        var cls = objc_allocateClassPair(objc_getClass("NSView"), "StarlingPassthroughMetalView", 0);
        if (cls == 0)
        {
            return 0;
        }

        delegate* unmanaged<nint, nint, CGPoint, nint> imp = &HitTestReturnNil;
        // "@@:{CGPoint=dd}" — returns id, args: self (id), _cmd (SEL), point (CGPoint of two doubles).
        class_addMethod(cls, sel_registerName("hitTest:"), (nint)imp, "@@:{CGPoint=dd}");
        objc_registerClassPair(cls);
        _passthroughClass = cls;
        return cls;
    }

    /// <summary>
    /// Creates a click-through NSView backed by a fresh CAMetalLayer, returning the
    /// view pointer (NSView*) and, via <paramref name="metalLayer"/>, the layer
    /// pointer (CAMetalLayer*). Returns 0 on failure. wgpu sets the layer's device and
    /// pixel format when it creates the surface; we set contentsScale for crisp Retina.
    /// </summary>
    public static nint CreatePassthroughMetalView(double scale, out nint metalLayer)
    {
        metalLayer = 0;
        try
        {
            var viewClass = PassthroughViewClass();
            if (viewClass == 0)
            {
                return 0;
            }

            var view = MsgSend(MsgSend(viewClass, sel_registerName("alloc")), sel_registerName("init"));
            if (view == 0)
            {
                return 0;
            }

            var metalClass = objc_getClass("CAMetalLayer");
            if (metalClass == 0)
            {
                return view;
            }

            var layer = MsgSend(MsgSend(metalClass, sel_registerName("alloc")), sel_registerName("init"));
            if (layer == 0)
            {
                return view;
            }

            // The render session configures the swapchain
            // with RenderAttachment usage only, so the default framebufferOnly = YES
            // would actually suffice. We clear it anyway so the drawable also allows
            // copy/sample usages — leaving room for a future surface-readback path
            // without a reconfigure (an unsupported usage makes wgpuSurfaceConfigure
            // abort the process with a non-unwinding panic, so it must be set up front).
            MsgSendVoidBool(layer, sel_registerName("setFramebufferOnly:"), false);

            // Layer-hosting view: assign the custom layer, then set wantsLayer.
            MsgSendVoidPtr(view, sel_registerName("setLayer:"), layer);
            MsgSendVoidBool(view, sel_registerName("setWantsLayer:"), true);
            if (scale > 0)
            {
                MsgSendVoidDouble(layer, sel_registerName("setContentsScale:"), scale);
            }

            metalLayer = layer;
            return view;
        }
        catch (Exception ex)
        {
            PageSurfaceHostLog.MacMetalCreateFailed(Log, ex);
            metalLayer = 0;
            return 0;
        }
    }

    public static void SetContentsScale(nint metalLayer, double scale)
    {
        if (metalLayer == 0 || scale <= 0)
        {
            return;
        }

        try
        {
            MsgSendVoidDouble(metalLayer, sel_registerName("setContentsScale:"), scale);
        }
        catch (Exception ex)
        {
            /* best effort */
            PageSurfaceHostLog.MacMetalSetScaleFailed(Log, ex);
        }
    }

    public static void Release(nint obj)
    {
        if (obj == 0)
        {
            return;
        }

        try
        {
            MsgSend(obj, sel_registerName("release"));
        }
        catch (Exception ex)
        {
            /* best effort */
            PageSurfaceHostLog.MacMetalReleaseFailed(Log, ex);
        }
    }
}
