// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Avalonia.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Starling.Gui;

internal static partial class MacDockIconLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "macOS Dock icon setup failed")]
    public static partial void SetupFailed(ILogger logger, Exception ex);
}

/// <summary>
/// Sets the macOS Dock icon for project launches that do not run from a .app
/// bundle, such as <c>aspire run</c> and <c>dotnet run</c>.
/// </summary>
internal static class MacDockIcon
{
    private static readonly Uri IconUri = new("avares://Starling.Gui/Assets/icon_1024.png");

    // Static class — logger sourced from NullLoggerFactory at startup; the host
    // integrator may replace it by calling Apply(ILoggerFactory) before startup.
    private static ILogger _log =
        NullLoggerFactory.Instance.CreateLogger(typeof(MacDockIcon));

    public static void Apply(ILoggerFactory? loggerFactory = null)
    {
        if (loggerFactory is not null)
        {
            _log = loggerFactory.CreateLogger(typeof(MacDockIcon));
        }

        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            var appClass = ObjC.GetClass("NSApplication");
            var imageClass = ObjC.GetClass("NSImage");
            var dataClass = ObjC.GetClass("NSData");
            if (appClass == 0 || imageClass == 0 || dataClass == 0)
            {
                return;
            }

            using var stream = AssetLoader.Open(IconUri);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var bytes = memory.ToArray();
            if (bytes.Length == 0)
            {
                return;
            }

            var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var data = ObjC.SendBytes(
                    dataClass,
                    ObjC.Sel("dataWithBytes:length:"),
                    pinned.AddrOfPinnedObject(),
                    (nuint)bytes.Length);
                if (data == 0)
                {
                    return;
                }

                var image = ObjC.Send(
                    ObjC.Send(imageClass, ObjC.Sel("alloc")),
                    ObjC.Sel("initWithData:"),
                    data);
                if (image == 0)
                {
                    return;
                }

                var app = ObjC.Send(appClass, ObjC.Sel("sharedApplication"));
                if (app != 0)
                {
                    ObjC.SendVoid(app, ObjC.Sel("setApplicationIconImage:"), image);
                }

                ObjC.Release(image);
            }
            finally
            {
                pinned.Free();
            }
        }
        catch (Exception ex)
        {
            // Best effort only. A Dock icon failure must not block startup.
            MacDockIconLog.SetupFailed(_log, ex);
        }
    }

    private static class ObjC
    {
        private const string Lib = "/usr/lib/libobjc.A.dylib";

        [DllImport(Lib, EntryPoint = "objc_getClass")]
        internal static extern nint GetClass(string name);

        [DllImport(Lib, EntryPoint = "sel_registerName")]
        internal static extern nint Sel(string name);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        internal static extern nint Send(nint receiver, nint selector);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        internal static extern nint Send(nint receiver, nint selector, nint arg);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        internal static extern nint SendBytes(nint receiver, nint selector, nint bytes, nuint length);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        internal static extern void SendVoid(nint receiver, nint selector, nint arg);

        internal static void Release(nint obj)
        {
            if (obj != 0)
            {
                Send(obj, Sel("release"));
            }
        }
    }
}
