// SPDX-License-Identifier: Apache-2.0
using System.Numerics;
using AwesomeAssertions;

namespace Starling.Gpu.Tests;

[TestClass]
public class GpuAbstractionTests
{
    [TestMethod]
    public void Present_path_records_expected_operations_in_order()
    {
        var log = new List<string>();

        using (var device = new FakeGpuDevice(log))
        using (var surface = new FakeGpuSurface(log))
        {
            using var vertices = device.CreateBuffer(
                new BufferDescriptor(256, GpuBufferUsage.Vertex | GpuBufferUsage.CopyDestination, "verts"));
            device.Queue.WriteBuffer(vertices, new byte[16]);

            using var atlas = device.CreateTexture(
                new TextureDescriptor(64, 48, GpuTextureFormat.Rgba8Unorm, GpuTextureUsage.TextureBinding | GpuTextureUsage.CopyDestination, "atlas"));
            device.Queue.WriteTexture(atlas, new byte[64 * 48 * 4], new TextureWrite(64, 48, 64 * 4));

            surface.Configure(64, 48, GpuTextureFormat.Bgra8Unorm);
            var frame = surface.AcquireNextTexture();
            using var view = frame.CreateView();

            var encoder = device.CreateCommandEncoder();
            var pass = encoder.BeginRenderPass(new RenderPassDescriptor(view, clear: true, new Vector4(0, 0, 0, 1)));
            pass.End();
            var commands = encoder.Finish();
            device.Queue.Submit(commands);
            surface.Present();
        }

        log.Should().ContainInOrder(
            "createBuffer:verts:256",
            "queue.writeBuffer:16",
            "createTexture:64x48",
            "queue.writeTexture:64x48",
            "surface.configure:64x48",
            "surface.acquire",
            "createView",
            "createEncoder",
            "renderPass.begin:clear",
            "renderPass.end",
            "encoder.finish",
            "queue.submit",
            "surface.present");

        log.Should().Contain("disposeBuffer");
        log.Should().Contain("disposeSurface");
        log.Should().Contain("disposeDevice");
    }

    [TestMethod]
    public void Device_reports_limits()
    {
        var log = new List<string>();
        using var device = new FakeGpuDevice(log);

        device.Limits.MaxTextureDimension2D.Should().Be(8192);
    }
}
