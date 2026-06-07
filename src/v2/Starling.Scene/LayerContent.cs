// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>
/// What a surface layer is made of. A layer is not always a render scene: a video
/// or a guest surface carries a handle, not draw commands. Modeling content as a
/// closed union lets the compositor source pixels correctly per layer and treat
/// them all as surfaces, and gives the renderer exhaustiveness when it switches on
/// the content kind.
/// </summary>
/// <remarks>
/// This is a closed union expressed as a sealed-record hierarchy. A C# 15 union
/// type was attempted, but the .NET 11 preview.4 compiler rejected the record-style
/// positional cases (it wants a "union must specify at least one case type" form and
/// forbids single-parameter constructors in a union). Until the union syntax settles
/// and is confirmed against the spec, the records below are the closed union. They
/// compile, and call sites use the case names directly plus `is` pattern matching.
/// </remarks>
public abstract record LayerContent;

/// <summary>A layer drawn from a render scene (a document, generated UI, chart, and so on).</summary>
public sealed record RenderSceneContent(RenderScene Scene) : LayerContent;

/// <summary>A layer that blends an existing texture (for example, output from another renderer backend).</summary>
public sealed record ExternalTextureContent(TextureHandle Texture) : LayerContent;

/// <summary>A layer that samples a video frame source.</summary>
public sealed record VideoContent(VideoHandle Video) : LayerContent;

/// <summary>A layer that embeds a native guest surface, for example a CEF or WebView view. CEF is the Chromium Embedded Framework.</summary>
public sealed record NativeGuestContent(NativeSurfaceHandle Surface) : LayerContent;
