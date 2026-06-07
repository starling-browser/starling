// SPDX-License-Identifier: Apache-2.0
using System.Numerics;

namespace Starling.Gpu;

/// <summary>
/// The one swap point for the GPU. The concrete Gpu* classes are a thin, public,
/// WebGPU-shaped facade; they hold opaque backend tokens and forward every call
/// here. Only a backend (Starling.Gpu.WgpuNative now, Starling.Gpu.Dawn later)
/// implements this and maps the tokens to real handles. No renderer or compositor
/// code ever sees a raw graphics handle, and adding Dawn is a second backend, not
/// a change to any caller.
/// </summary>
public interface IGpuBackend : IDisposable
{
    /// <summary>Creates the root instance and returns its opaque token.</summary>
    object CreateInstance();

    object RequestAdapter(object instance, in GpuAdapterOptions options);
    object CreateSurface(object instance, in NativeSurfaceDescriptor descriptor);

    object RequestDevice(object adapter, in GpuDeviceDescriptor descriptor);
    GpuLimits GetLimits(object adapter);

    object GetQueue(object device);
    object CreateBuffer(object device, in BufferDescriptor descriptor);
    object CreateTexture(object device, in TextureDescriptor descriptor);
    object CreateTextureView(object texture);
    object CreateCommandEncoder(object device);

    void ConfigureSurface(object surface, int width, int height, GpuTextureFormat format);
    object AcquireSurfaceTexture(object surface);
    void Present(object surface);

    void WriteBuffer(object queue, object buffer, ReadOnlySpan<byte> data, int offsetBytes);
    void WriteTexture(object queue, object texture, ReadOnlySpan<byte> data, in TextureWrite layout);
    void Submit(object queue, object commandBuffer);

    object BeginRenderPass(object encoder, object colorTargetView, bool clear, Vector4 clearColor);
    void EndRenderPass(object renderPass);
    object FinishEncoder(object encoder);

    /// <summary>Releases a resource named by its token.</summary>
    void Release(object resource);
}
