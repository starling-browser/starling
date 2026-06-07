// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>A layer drawn from a render scene (a document, generated UI, chart, and so on).</summary>
public sealed record RenderSceneContent(RenderScene Scene);

/// <summary>A layer that blends an existing texture (for example, output from another renderer backend).</summary>
public sealed record ExternalTextureContent(TextureHandle Texture);

/// <summary>A layer that samples a video frame source.</summary>
public sealed record VideoContent(VideoHandle Video);

/// <summary>A layer that embeds a native guest surface, for example a CEF or WebView view. CEF is the Chromium Embedded Framework.</summary>
public sealed record NativeGuestContent(NativeSurfaceHandle Surface);

/// <summary>
/// What a surface layer is made of: exactly one of a render scene, an external
/// texture, a video, or a native guest. A C# 15 union type, so the compiler checks
/// that code switching on the content kind is exhaustive. A video or guest layer
/// has no draw commands, so the union models content honestly instead of forcing a
/// render scene onto every layer.
/// </summary>
public union LayerContent(RenderSceneContent, ExternalTextureContent, VideoContent, NativeGuestContent);
