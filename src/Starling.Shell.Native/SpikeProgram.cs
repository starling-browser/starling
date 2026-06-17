using Silk.NET.Maths;
using Silk.NET.WebGPU;
using Silk.NET.Windowing;

namespace Starling.Shell.Native;

/// <summary>
/// Phase-2 de-risk spike: open a Silk.NET window, create a wgpu surface from its
/// native handle, configure a swapchain, and present a clear color for a few
/// frames. This proves the zero-copy present path (window → wgpu surface →
/// present, no readback) works in this environment before the full compositor is
/// wired in. Run with `--spike`.
/// </summary>
internal static unsafe class SpikeProgram
{
    public static int Run()
    {
        WebGPU wgpu = null!;
        IWindow? window = null;
        Instance* instance = null;
        Adapter* adapter = null;
        Device* device = null;
        Queue* queue = null;
        Surface* surface = null;
        var presented = 0;
        TextureFormat format = TextureFormat.Bgra8Unorm;

        try
        {
            Silk.NET.Windowing.Glfw.GlfwWindowing.Use();

            var opts = WindowOptions.Default with
            {
                Title = "Starling (spike)",
                Size = new Vector2D<int>(640, 480),
                API = GraphicsAPI.None, // wgpu owns the surface; no GL context
                VSync = false,
                ShouldSwapAutomatically = false,
            };

            wgpu = WebGPU.GetApi();
            window = Window.Create(opts);
            window.Initialize();
            Console.WriteLine($"spike: window initialized, fb={window.FramebufferSize}");

            var instDesc = default(InstanceDescriptor);
            instance = wgpu.CreateInstance(in instDesc);
            if (instance == null) { Console.Error.WriteLine("spike: CreateInstance failed"); return 1; }

            surface = window.CreateWebGPUSurface(wgpu, instance);
            if (surface == null) { Console.Error.WriteLine("spike: CreateWebGPUSurface failed"); return 1; }
            Console.WriteLine("spike: surface created from native window");

            var adapterOpts = new RequestAdapterOptions { CompatibleSurface = surface, PowerPreference = PowerPreference.HighPerformance };
            var aCb = PfnRequestAdapterCallback.From((status, a, _, _) => { if (status == RequestAdapterStatus.Success) { adapter = a; } });
            wgpu.InstanceRequestAdapter(instance, in adapterOpts, aCb, null);
            if (adapter == null) { Console.Error.WriteLine("spike: no adapter"); return 1; }

            var devDesc = default(DeviceDescriptor);
            var dCb = PfnRequestDeviceCallback.From((status, d, _, _) => { if (status == RequestDeviceStatus.Success) { device = d; } });
            wgpu.AdapterRequestDevice(adapter, in devDesc, dCb, null);
            if (device == null) { Console.Error.WriteLine("spike: no device"); return 1; }
            queue = wgpu.DeviceGetQueue(device);
            Console.WriteLine("spike: device + queue ready");

            Configure(wgpu, surface, device, window.FramebufferSize, format);

            window.Render += _ =>
            {
                SurfaceTexture st = default;
                wgpu.SurfaceGetCurrentTexture(surface, ref st);
                if (st.Status != SurfaceGetCurrentTextureStatus.Success)
                {
                    return;
                }

                var view = wgpu.TextureCreateView(st.Texture, (TextureViewDescriptor*)null);
                // Cycle the clear color so successive frames are visibly distinct.
                var t = presented / 30f;
                var color = new Silk.NET.WebGPU.Color { R = 0.1, G = 0.4 + 0.2 * t, B = 0.6, A = 1.0 };
                var att = new RenderPassColorAttachment { View = view, LoadOp = LoadOp.Clear, StoreOp = StoreOp.Store, ClearValue = color };
                var passDesc = new RenderPassDescriptor { ColorAttachmentCount = 1, ColorAttachments = &att };

                var enc = wgpu.DeviceCreateCommandEncoder(device, (CommandEncoderDescriptor*)null);
                var pass = wgpu.CommandEncoderBeginRenderPass(enc, in passDesc);
                wgpu.RenderPassEncoderEnd(pass);
                wgpu.RenderPassEncoderRelease(pass);
                var cmd = wgpu.CommandEncoderFinish(enc, (CommandBufferDescriptor*)null);
                wgpu.QueueSubmit(queue, 1, &cmd);
                wgpu.CommandBufferRelease(cmd);
                wgpu.CommandEncoderRelease(enc);
                wgpu.SurfacePresent(surface);
                wgpu.TextureViewRelease(view);
                wgpu.TextureRelease(st.Texture);

                presented++;
                if (presented >= 30)
                {
                    window!.Close();
                }
            };

            window.Run();
            Console.WriteLine($"SPIKE OK: presented {presented} frames via zero-copy wgpu swapchain");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SPIKE FAILED: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 2;
        }
        finally
        {
            if (surface != null) { wgpu.SurfaceRelease(surface); }
            if (queue != null)
            {
                wgpu.QueueRelease(queue);
            }

            if (device != null)
            {
                wgpu.DeviceRelease(device);
            }

            if (adapter != null)
            {
                wgpu.AdapterRelease(adapter);
            }

            if (instance != null)
            {
                wgpu.InstanceRelease(instance);
            }

            window?.Dispose();
        }
    }

    private static void Configure(WebGPU wgpu, Surface* surface, Device* device, Vector2D<int> fb, TextureFormat format)
    {
        var config = new SurfaceConfiguration
        {
            Device = device,
            Format = format,
            Usage = TextureUsage.RenderAttachment,
            Width = (uint)Math.Max(1, fb.X),
            Height = (uint)Math.Max(1, fb.Y),
            PresentMode = PresentMode.Fifo,
            AlphaMode = CompositeAlphaMode.Auto,
        };
        wgpu.SurfaceConfigure(surface, in config);
    }
}
