using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Gui.Controls;
using Starling.Gui.Theme;

namespace Starling.Gui.Headless.Tests;

/// <summary>
/// Regression coverage for the Retina "everything 2× too big" bug (M12-01).
///
/// The page bitmap WebviewPanel hands to its image control is in DEVICE pixels
/// (viewport DIP × RenderScaling), tagged at 96 DPI by BitmapBridge — so its
/// logical (DIP) size equals its device-pixel size. <c>_pageImage</c> is given
/// the DIP viewport Width/Height, and <see cref="Stretch.Uniform"/> downscales
/// the device bitmap to fit, yielding a crisp 1:1 device mapping at any
/// RenderScaling. A regression to <see cref="Stretch.None"/> draws the device
/// bitmap at its full DIP size — scale× too big and clipped — which is exactly
/// what shipped briefly and made every page render 2× too large on Retina.
/// </summary>
public class WebviewPanelScaleTests
{
    [AvaloniaFact]
    public void Page_image_maps_device_pixels_to_dip_at_2x_scale()
    {
        const double scale = 2.0;
        const int dip = 100;
        const int dev = (int)(dip * scale); // 200 device px

        using var panel = NewPanel();
        var image = GetPageImage(panel);
        // Host the real _pageImage standalone so we don't need a laid-out page.
        if (image.Parent is Panel parent)
        {
            parent.Children.Remove(image);
        }

        // Device-pixel source as BitmapBridge would produce: a red field with a
        // GREEN marker in the far bottom-right corner. The marker is visible in
        // the frame ONLY when the entire bitmap is drawn (Stretch.Uniform
        // downscales the whole device bitmap into the DIP-sized control). Under
        // Stretch.None the oversized bitmap is drawn 1:1 with DIP — 2× too big —
        // and centered+clipped, so the far corner is cropped away and never
        // reaches the frame's corner. (Green, not blue, so the assertion is
        // robust to any R/B channel ordering — green is invariant under R/B swap.)
        const int markerFrom = dev * 4 / 5; // last 20% is the green marker
        image.Source = MakeCornerMarkerBitmap(dev, dev, markerFrom);
        image.Width = dip;
        image.Height = dip;
        image.IsVisible = true;

        var window = new Window { Content = image, Width = dip, Height = dip };
        window.Show();
        window.SetRenderScaling(scale);

        var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull("the headless window should render a frame");

        // Sanity: RenderScaling took effect (frame is device-sized, not
        // DIP-sized). Without this a scale-agnostic bug could pass vacuously.
        frame!.PixelSize.Width.Should().BeGreaterThan(dip,
            "the framebuffer must be the DIP client size × RenderScaling");

        // The frame's bottom-right corner. Under Stretch.Uniform the full bitmap
        // maps across the frame, so the corner shows the bitmap's GREEN marker.
        // Under the Stretch.None bug the bitmap is drawn 2× too big and clipped,
        // so the marker is off-screen and this pixel is the RED field.
        var px = frame.PixelSize.Width - 8;
        var py = frame.PixelSize.Height - 8;
        var (r, g, b, _) = ReadPixel(frame, px, py);

        g.Should().BeGreaterThan(200,
            $"frame corner ({px},{py}) must show the bitmap's far-corner green marker; a non-green " +
            "pixel means the device bitmap was drawn scale× too big and clipped (Stretch.None regression)");
        r.Should().BeLessThan(64, "the green marker has no red component");
        b.Should().BeLessThan(64, "the green marker has no blue component");
    }

    [AvaloniaFact]
    public void Page_image_uses_a_downscaling_stretch()
    {
        using var panel = NewPanel();
        GetPageImage(panel).Stretch.Should().Be(Stretch.Uniform,
            "the device-pixel page bitmap must be downscaled to its DIP bounds; Stretch.None " +
            "renders it scale× too big on any non-1.0 RenderScaling display (e.g. Retina)");
    }

    private static WebviewPanel NewPanel()
        => new(new ThemeManager(), NullLoggerFactory.Instance, _ => { }, (_, _) => { });

    private static Image GetPageImage(WebviewPanel panel)
    {
        var field = typeof(WebviewPanel).GetField("_pageImage", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Image)field.GetValue(panel)!;
    }

    private static WriteableBitmap MakeCornerMarkerBitmap(int w, int h, int markerFrom)
    {
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var o = (y * w + x) * 4;
                var green = x >= markerFrom && y >= markerFrom;
                pixels[o] = (byte)(green ? 0 : 255);     // R
                pixels[o + 1] = (byte)(green ? 255 : 0);  // G
                pixels[o + 2] = 0;                        // B
                pixels[o + 3] = 255;                      // A
            }
        }

        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using var fb = bmp.Lock();
        var srcStride = w * 4;
        if (fb.RowBytes == srcStride)
        {
            Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
        }
        else
        {
            for (var y = 0; y < h; y++)
            {
                Marshal.Copy(pixels, y * srcStride, fb.Address + (y * fb.RowBytes), srcStride);
            }
        }

        return bmp;
    }

    private static (byte R, byte G, byte B, byte A) ReadPixel(WriteableBitmap bmp, int x, int y)
    {
        using var fb = bmp.Lock();
        var buf = new byte[4];
        Marshal.Copy(fb.Address + (y * fb.RowBytes) + (x * 4), buf, 0, 4);
        return (buf[0], buf[1], buf[2], buf[3]);
    }
}
