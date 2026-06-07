// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu;

/// <summary>
/// The native window handles a surface binds to. The backend reads the ones its
/// platform needs (an HWND on Windows, an NSView or CAMetalLayer on macOS, an
/// Xlib window and display on Linux). HWND is the Windows window handle.
/// </summary>
public readonly struct NativeSurfaceDescriptor
{
    public NativeSurfaceDescriptor(nint window, nint display, string? label = null)
    {
        Window = window;
        Display = display;
        Label = label;
    }

    public nint Window { get; }
    public nint Display { get; }

    /// <summary>Optional debug label, surfaced to GPU debug tooling.</summary>
    public string? Label { get; }
}
