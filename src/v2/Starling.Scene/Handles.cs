// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>
/// An opaque reference to a texture the compositor can blend without a render
/// scene, for example one a renderer backend or guest produced. The id is
/// assigned by the compositor's resource layer.
/// </summary>
public readonly struct TextureHandle
{
    public TextureHandle(ulong id) => Id = id;
    public ulong Id { get; }
}

/// <summary>An opaque reference to a video frame source the compositor samples.</summary>
public readonly struct VideoHandle
{
    public VideoHandle(ulong id) => Id = id;
    public ulong Id { get; }
}

/// <summary>An opaque reference to a native guest surface, for example a CEF or WebView texture. CEF is the Chromium Embedded Framework.</summary>
public readonly struct NativeSurfaceHandle
{
    public NativeSurfaceHandle(nint handle) => Handle = handle;
    public nint Handle { get; }
}
