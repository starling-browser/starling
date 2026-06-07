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
/// This uses the C# 15 union type (the project enables the preview language version
/// for exactly this correctness/exhaustiveness modeling, per AGENTS.md). The exact
/// preview syntax is still settling, so this was written without a build against the
/// .NET 11 preview software development kit. If the first build rejects it, revert to
/// the sealed-record closed hierarchy that this replaced:
///
///   public abstract record LayerContent;
///   public sealed record RenderSceneContent(RenderScene Scene) : LayerContent;
///   public sealed record ExternalTextureContent(TextureHandle Texture) : LayerContent;
///   public sealed record VideoContent(VideoHandle Video) : LayerContent;
///   public sealed record NativeGuestContent(NativeSurfaceHandle Surface) : LayerContent;
///
/// Call sites use the case names directly (new RenderSceneContent(...), the "is" and
/// "as" operators, and pattern matching), so the revert is local to this file.
/// </remarks>
public union LayerContent
{
    /// <summary>A layer drawn from a render scene (a document, generated UI, chart, and so on).</summary>
    RenderSceneContent(RenderScene Scene);

    /// <summary>A layer that blends an existing texture (for example, output from another renderer backend).</summary>
    ExternalTextureContent(TextureHandle Texture);

    /// <summary>A layer that samples a video frame source.</summary>
    VideoContent(VideoHandle Video);

    /// <summary>A layer that embeds a native guest surface, for example a CEF or WebView view. CEF is the Chromium Embedded Framework.</summary>
    NativeGuestContent(NativeSurfaceHandle Surface);
}
