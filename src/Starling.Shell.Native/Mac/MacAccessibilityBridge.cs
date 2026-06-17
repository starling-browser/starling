using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Gui.Core.Accessibility;

namespace Starling.Shell.Native.Mac;

/// <summary>
/// Exposes the managed <see cref="AccessibilityNode"/> tree to macOS
/// <c>NSAccessibility</c>, so VoiceOver can read the page in the native shell.
/// Builds an <c>NSAccessibilityElement</c> per semantic node (role + label +
/// on-screen frame) and sets them as the content view's accessibility children.
/// </summary>
/// <remarks>
/// Best-effort and macOS-only. The role and label path uses only id/NSString
/// arguments, whose ABI is exact. The on-screen frame conversion (document
/// top-left to AppKit bottom-left screen points, minus chrome and scroll) is
/// approximate and wants tuning against VoiceOver on a real Mac. This harness
/// has no way to drive the accessibility client.
/// </remarks>
internal sealed class MacAccessibilityBridge
{
    private readonly nint _nsWindow;
    private readonly nint _contentView;
    private readonly ILogger _log;

    private MacAccessibilityBridge(nint nsWindow, nint contentView, ILogger log)
    {
        _nsWindow = nsWindow;
        _contentView = contentView;
        _log = log;
    }

    /// <summary>Builds a bridge for the window, or null if not macOS / no content view.</summary>
    public static MacAccessibilityBridge? TryCreate(nint nsWindow, ILogger<MacAccessibilityBridge>? log = null)
    {
        if (!OperatingSystem.IsMacOS() || nsWindow == 0)
        {
            return null;
        }

        try
        {
            var view = ObjC.Send(nsWindow, ObjC.Sel("contentView"));
            return view == 0 ? null : new MacAccessibilityBridge(nsWindow, view, log ?? NullLogger<MacAccessibilityBridge>.Instance);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Pushes the current accessibility tree to the OS. Call after each layout or
    /// navigation. <paramref name="chromeHeightCss"/> and <paramref name="scrollY"/>
    /// place the page region; <paramref name="windowHeightCss"/> is the window's
    /// logical height for the top/bottom-origin flip.
    /// </summary>
    public void Update(AccessibilityNode tree, double chromeHeightCss, double scrollY, double windowHeightCss)
    {
        try
        {
            var array = ObjC.Send(ObjC.GetClass("NSMutableArray"), ObjC.Sel("array"));
            if (array == 0)
            {
                return;
            }

            foreach (var node in Flatten(tree))
            {
                AddElement(array, node, chromeHeightCss, scrollY, windowHeightCss);
            }

            ObjC.SendVoid(_contentView, ObjC.Sel("setAccessibilityChildren:"), array);
        }
        catch (Exception ex)
        {
            // Best-effort: a runtime/ABI mismatch must never crash the shell.
            MacAccessibilityBridgeLog.UpdateFailed(_log, ex);
        }
    }

    private void AddElement(nint array, AccessibilityNode node,
        double chromeHeightCss, double scrollY, double windowHeightCss)
    {
        var element = ObjC.New("NSAccessibilityElement");
        if (element == 0)
        {
            return;
        }

        ObjC.SendVoid(element, ObjC.Sel("setAccessibilityRole:"), ObjC.NSString(AxRole(node.Role)));

        var label = node.Name;
        if ((node.Role is AccessibilityRole.TextField or AccessibilityRole.ComboBox)
            && !string.IsNullOrEmpty(node.Value))
        {
            label = string.IsNullOrEmpty(label) ? node.Value! : $"{label}: {node.Value}";
        }

        if (!string.IsNullOrEmpty(label))
        {
            ObjC.SendVoid(element, ObjC.Sel("setAccessibilityLabel:"), ObjC.NSString(label));
        }

        // Document top-left CSS px -> the window's content space (AppKit bottom-left
        // origin, points; CSS px == points at the logical level), then to screen via
        // AppKit so the title bar and window position are handled for us. topInWindow
        // is the element's distance below the window-content top.
        var topInWindow = chromeHeightCss + (node.Bounds.Y - scrollY);
        var winRect = new CGRect(
            node.Bounds.X,
            windowHeightCss - topInWindow - node.Bounds.Height,
            node.Bounds.Width,
            node.Bounds.Height);
        var screenRect = ObjC.ConvertRectToScreen(_nsWindow, winRect);
        ObjC.SendRect(element, ObjC.Sel("setAccessibilityFrame:"), screenRect);

        ObjC.SendVoid(element, ObjC.Sel("setAccessibilityParent:"), _contentView);
        ObjC.SendVoid(array, ObjC.Sel("addObject:"), element);
    }

    private static IEnumerable<AccessibilityNode> Flatten(AccessibilityNode node)
    {
        // Skip the synthetic Document root; expose its semantic descendants flat.
        foreach (var child in node.Children)
        {
            yield return child;
            foreach (var d in Flatten(child))
            {
                yield return d;
            }
        }
    }

    // AppKit accessibility role strings (the underlying values of the
    // NSAccessibility*Role constants).
    private static string AxRole(AccessibilityRole role) => role switch
    {
        AccessibilityRole.Heading => "AXHeading",
        AccessibilityRole.Link => "AXLink",
        AccessibilityRole.Button => "AXButton",
        AccessibilityRole.TextField => "AXTextField",
        AccessibilityRole.CheckBox => "AXCheckBox",
        AccessibilityRole.RadioButton => "AXRadioButton",
        AccessibilityRole.Image => "AXImage",
        AccessibilityRole.List => "AXList",
        AccessibilityRole.ListItem => "AXStaticText",
        AccessibilityRole.Paragraph => "AXStaticText",
        AccessibilityRole.ComboBox => "AXComboBox",
        AccessibilityRole.Navigation => "AXGroup",
        AccessibilityRole.Banner => "AXGroup",
        AccessibilityRole.ContentInfo => "AXGroup",
        AccessibilityRole.Main => "AXGroup",
        AccessibilityRole.Article => "AXGroup",
        AccessibilityRole.Region => "AXGroup",
        AccessibilityRole.Complementary => "AXGroup",
        AccessibilityRole.Form => "AXGroup",
        AccessibilityRole.Search => "AXGroup",
        AccessibilityRole.Document => "AXGroup",
        _ => "AXGroup",
    };
}

internal static partial class MacAccessibilityBridgeLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Accessibility tree update failed (best-effort; ABI/runtime mismatch)")]
    public static partial void UpdateFailed(ILogger logger, Exception ex);
}
