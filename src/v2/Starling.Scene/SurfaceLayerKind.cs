// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>
/// What kind of producer fed a surface layer. The compositor treats them all as
/// surfaces, but the kind tells it how to source pixels: render a scene, sample a
/// video frame, or blend an external guest texture.
/// </summary>
public enum SurfaceLayerKind
{
    /// <summary>A web document lowered from layout into a render scene.</summary>
    Document,

    /// <summary>A generated UI surface produced from a validated SurfaceSpec.</summary>
    GeneratedUi,

    /// <summary>A video frame fed as an external texture.</summary>
    Video,

    /// <summary>Chrome, devtools, or provenance overlays drawn above the page.</summary>
    Overlay,

    /// <summary>An external guest surface, for example a CEF or WebView texture.</summary>
    ExternalGuest,
}
