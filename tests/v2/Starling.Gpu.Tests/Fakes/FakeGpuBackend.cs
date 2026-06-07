// SPDX-License-Identifier: Apache-2.0
using System.Numerics;

namespace Starling.Gpu.Tests;

/// <summary>
/// A recording implementation of <see cref="IGpuBackend"/>. It proves the GPU
/// seam is implementable with no native code and lets tests assert the exact
/// sequence of GPU operations a present takes. Tokens are small records so the
/// log can name which resource each call touched.
/// </summary>
internal sealed class FakeGpuBackend : IGpuBackend
{
    private readonly List<string> _log;

    public FakeGpuBackend(List<string> log) => _log = log;

    public object CreateInstance()
    {
        _log.Add("createInstance");
        return new Token("instance");
    }

    public object RequestAdapter(object instance, in GpuAdapterOptions options)
    {
        _log.Add($"requestAdapter:{options.Power}");
        return new Token("adapter");
    }

    public object CreateSurface(object instance, in NativeSurfaceDescriptor descriptor)
    {
        _log.Add($"createSurface:{descriptor.Label}");
        return new Token("surface");
    }

    public object RequestDevice(object adapter, in GpuDeviceDescriptor descriptor)
    {
        _log.Add("requestDevice");
        return new Token("device");
    }

    public GpuLimits GetLimits(object adapter) => new(maxTextureDimension2D: 8192);

    public object GetQueue(object device)
    {
        _log.Add("getQueue");
        return new Token("queue");
    }

    public object CreateBuffer(object device, in BufferDescriptor descriptor)
    {
        _log.Add($"createBuffer:{descriptor.Label}:{descriptor.SizeBytes}");
        return new Token("buffer");
    }

    public object CreateTexture(object device, in TextureDescriptor descriptor)
    {
        _log.Add($"createTexture:{descriptor.Width}x{descriptor.Height}");
        return new Token("texture");
    }

    public object CreateTextureView(object texture)
    {
        _log.Add("createView");
        return new Token("view");
    }

    public object CreateCommandEncoder(object device)
    {
        _log.Add("createEncoder");
        return new Token("encoder");
    }

    public void ConfigureSurface(object surface, int width, int height, GpuTextureFormat format)
        => _log.Add($"surface.configure:{width}x{height}");

    public object AcquireSurfaceTexture(object surface)
    {
        _log.Add("surface.acquire");
        return new Token("frame");
    }

    public void Present(object surface) => _log.Add("surface.present");

    public void WriteBuffer(object queue, object buffer, ReadOnlySpan<byte> data, int offsetBytes)
        => _log.Add($"queue.writeBuffer:{data.Length}");

    public void WriteTexture(object queue, object texture, ReadOnlySpan<byte> data, in TextureWrite layout)
        => _log.Add($"queue.writeTexture:{layout.Width}x{layout.Height}");

    public void Submit(object queue, object commandBuffer) => _log.Add("queue.submit");

    public object BeginRenderPass(object encoder, object colorTargetView, bool clear, Vector4 clearColor)
    {
        _log.Add(clear ? "renderPass.begin:clear" : "renderPass.begin");
        return new Token("pass");
    }

    public void EndRenderPass(object renderPass) => _log.Add("renderPass.end");

    public object FinishEncoder(object encoder)
    {
        _log.Add("encoder.finish");
        return new Token("commandBuffer");
    }

    public void Release(object resource) => _log.Add($"release:{((Token)resource).Name}");

    public void Dispose()
    {
        _log.Add("disposeBackend");
        GC.SuppressFinalize(this);
    }

    private sealed record Token(string Name);
}
