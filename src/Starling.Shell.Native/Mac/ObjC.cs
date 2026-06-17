using System.Runtime.InteropServices;

namespace Starling.Shell.Native.Mac;

/// <summary>
/// Minimal Objective-C runtime interop for the macOS shell — class lookup,
/// selector registration, and the <c>objc_msgSend</c> variants we need. The GUI
/// shell is allowed native interop (it already links GLFW + wgpu). Used by the
/// accessibility bridge and the text-input (IME) hookup.
/// </summary>
/// <remarks>
/// <c>objc_msgSend</c> is untyped in C, so it must be declared once per distinct
/// return type and argument layout — the P/Invoke signature drives the calling
/// convention, and the .NET marshaller applies the platform ABI (including
/// struct-by-value for <see cref="CGRect"/> on arm64/x86_64).
/// </remarks>
internal static partial class ObjC
{
    private const string Lib = "/usr/lib/libobjc.A.dylib";

    [LibraryImport(Lib, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint GetClass(string name);

    [LibraryImport(Lib, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint Sel(string name);

    // Method swizzling (used by the IME preedit bridge to intercept the GLFW
    // content view's NSTextInputClient methods).
    [LibraryImport(Lib, EntryPoint = "class_getInstanceMethod")]
    internal static partial nint ClassGetInstanceMethod(nint cls, nint sel);

    [LibraryImport(Lib, EntryPoint = "method_getImplementation")]
    internal static partial nint MethodGetImplementation(nint method);

    [LibraryImport(Lib, EntryPoint = "method_setImplementation")]
    internal static partial nint MethodSetImplementation(nint method, nint imp);

    // BOOL objc_msgSend(id, SEL, SEL) — e.g. [obj respondsToSelector:sel].
    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool SendBoolSel(nint self, nint sel, nint selArg);

    // id objc_msgSend(id, SEL)
    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    internal static partial nint Send(nint self, nint sel);

    // id objc_msgSend(id, SEL, id)
    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    internal static partial nint Send(nint self, nint sel, nint arg0);

    // id objc_msgSend(id, SEL, const char*)
    [LibraryImport(Lib, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint SendStr(nint self, nint sel, string arg0);

    // void objc_msgSend(id, SEL, id)
    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoid(nint self, nint sel, nint arg0);

    // void objc_msgSend(id, SEL, CGRect)
    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    internal static partial void SendRect(nint self, nint sel, CGRect rect);

    // CGRect objc_msgSend(id, SEL) — struct return. Correct on arm64 (returned
    // via x8). On x86_64 this would need objc_msgSend_stret; we target Apple
    // Silicon, so the plain entry point is right here.
    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    internal static partial CGRect SendRectRet(nint self, nint sel);

    // CGRect objc_msgSend(id, SEL, CGRect) — struct arg + struct return, e.g.
    // [NSWindow convertRectToScreen:]. CGRect is a homogeneous 4-double aggregate,
    // passed and returned in the SIMD registers on arm64.
    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    internal static partial CGRect SendRectRet(nint self, nint sel, CGRect rect);

    /// <summary>Converts a rect in a window's content (bottom-left origin) space to
    /// screen points via AppKit's own conversion — robust against the title bar and
    /// window position, unlike adding the window frame origin by hand.</summary>
    internal static CGRect ConvertRectToScreen(nint window, CGRect windowRect)
        => SendRectRet(window, Sel("convertRectToScreen:"), windowRect);

    /// <summary>Allocates and initializes an instance of <paramref name="className"/>.</summary>
    internal static nint New(string className)
    {
        var cls = GetClass(className);
        if (cls == 0)
        {
            return 0;
        }

        var obj = Send(cls, Sel("alloc"));
        return obj == 0 ? 0 : Send(obj, Sel("init"));
    }

    /// <summary>Wraps a C# string as an autoreleased <c>NSString</c>.</summary>
    internal static nint NSString(string s)
    {
        var cls = GetClass("NSString");
        return cls == 0 ? 0 : SendStr(cls, Sel("stringWithUTF8String:"), s);
    }

    /// <summary>Reads a C# string from an <c>id</c> that is an <c>NSString</c> or
    /// <c>NSAttributedString</c> (IME marked text can be either) — resolving the
    /// attributed string to its plain string first.</summary>
    internal static string? StringFromAny(nint obj)
    {
        if (obj == 0)
        {
            return null;
        }

        var src = obj;
        if (!SendBoolSel(obj, Sel("respondsToSelector:"), Sel("UTF8String"))
            && SendBoolSel(obj, Sel("respondsToSelector:"), Sel("string")))
        {
            src = Send(obj, Sel("string"));
        }

        var cstr = Send(src, Sel("UTF8String"));
        return cstr == 0 ? null : Marshal.PtrToStringUTF8(cstr);
    }
}

/// <summary>An Objective-C <c>NSRange</c> (two unsigned words).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NSRange
{
    public nuint Location, Length;
    public NSRange(nuint location, nuint length) { Location = location; Length = length; }
}

/// <summary>A Core Graphics rect (origin bottom-left on screen, points).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CGRect
{
    public double X, Y, Width, Height;

    public CGRect(double x, double y, double w, double h)
    {
        X = x; Y = y; Width = w; Height = h;
    }
}
