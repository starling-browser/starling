// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>
/// What a surface layer is made of. A layer is not always a render scene: a video
/// or a guest surface carries a handle, not draw commands. Modeling content as a
/// closed union lets the compositor source pixels correctly per layer and treat
/// them all as surfaces.
/// </summary>
/// <remarks>
/// This is a closed union expressed as a sealed-record hierarchy. It is the
/// intended adoption site for a C# 15 union type (the project enables the preview
/// language version for exactly this kind of correctness/exhaustiveness modeling,
/// per AGENTS.md). The switch waits on a green build against the .NET 11 preview
/// software development kit, since the union syntax is still settling.
/// </remarks>
public abstract record LayerContent;

/// <summary>A layer drawn from a render scene (a document, generated UI, chart, and so on).</summary>
public sealed record RenderSceneContent(RenderScene Scene) : LayerContent;

/// <summary>A layer that blends an existing texture (for example, output from another renderer backend).</summary>
public sealed record ExternalTextureContent(TextureHandle Texture) : LayerContent;

/// <summary>A layer that samples a video frame source.</summary>
public sealed record VideoContent(VideoHandle Video) : LayerContent;

/// <summary>A layer that embeds a native guest surface, for example a CEF or WebView view.</summary>
public sealed record NativeGuestContent(NativeSurfaceHandle Surface) : LayerContent;
