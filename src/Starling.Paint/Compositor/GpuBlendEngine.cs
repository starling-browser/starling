using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using Starling.Common.Image;
using Starling.Css.Values;
using Starling.Paint.Backend;
using Starling.Paint.Interop;
using Rect = Starling.Layout.Rect;
using WgpuBuffer = Silk.NET.WebGPU.Buffer;
using WgpuExt = Silk.NET.WebGPU.Extensions.WGPU.Wgpu;

namespace Starling.Paint.Compositor;

/// <summary>
/// The reusable wgpu blend engine shared by the offscreen
/// <see cref="GpuLayerCompositor"/> (renders to a texture + reads back) and the
/// on-screen <see cref="GpuSurfacePresenter"/> (renders to a swapchain texture +
/// presents, zero readback). It owns the device, queue, sampler, the explicit
/// bind-group layout, a render pipeline per target colour format, and the
/// per-layer texture cache keyed by slice content hash. The blend itself is one
/// textured quad per layer, alpha-over in premultiplied space — see
/// <see cref="GpuLayerCompositor"/> for the colour-math rationale.
/// </summary>
/// <remarks>
/// Two factories: <see cref="CreateOffscreen"/> requests a default device, and
/// <see cref="CreateForSurface"/> requests a surface-compatible device and hands
/// back the configured surface. Either way the blend recording is identical — the
/// caller supplies the render pass and the target format.
/// </remarks>
internal sealed unsafe class GpuBlendEngine : IDisposable
{
    internal const int MaxTextureDimension2D = 8192;

    internal WebGPU Api { get; }
    internal WgpuExt? Poll { get; }
    internal Device* Device { get; }
    internal Queue* Queue { get; }
    internal nint DeviceHandle => (nint)Device;

    private Sampler* _sampler;
    private BindGroupLayout* _bindLayout;
    private PipelineLayout* _pipelineLayout;
    private ShaderModule* _shader;
    private GpuFilterEngine? _filterEngine;
    private readonly Dictionary<(TextureFormat Format, TextureAlphaMode AlphaMode), nint> _pipelines = new();
    private readonly Dictionary<TextureFormat, nint> _blitPipelines = new();

    // Per-layer GPU textures, keyed by slice content hash. Resident across frames
    // so an unchanged layer never re-uploads.
    private readonly Dictionary<long, CachedTexture> _textures = new();
    private ulong _frame;
    private const ulong EvictAfterFrames = 240;

    // Hard cap on resident GPU layer-texture bytes. Age-based eviction alone is
    // unbounded: a dynamic page (load thrash, scrolling a tall page) can touch
    // thousands of distinct tiles inside the EvictAfterFrames window, each a few MB
    // of GPU memory — that is how github.com pinned ~15 GB. The byte budget evicts
    // least-recently-used tiles so the cache can't balloon, mirroring the CPU-side
    // TileGrid budget. Configurable like STARLING_TILE_BUDGET_BYTES.
    private long _textureBytes;
    private readonly long _maxTextureBytes = ReadTextureBudgetEnv();
    private const long DefaultTextureBudgetBytes = 512L * 1024 * 1024; // 512 MB

    private static long ReadTextureBudgetEnv()
    {
        var raw = Environment.GetEnvironmentVariable("STARLING_GPU_TEXTURE_BUDGET_BYTES");
        return long.TryParse(raw, out var v) && v > 0 ? v : DefaultTextureBudgetBytes;
    }

    private static long BytesOf(CachedTexture c) => (long)c.Width * c.Height * 4;

    private WgpuBuffer* _vertexBuffer;
    private nuint _vertexCapacity;
    private bool _disposed;

    // 5 floats per vertex (ndc.x, ndc.y, u, v, opacity), 6 vertices per layer quad.
    private const int FloatsPerVertex = 5;
    private const int VertsPerQuad = 6;
    private const uint VertexStride = FloatsPerVertex * sizeof(float);

    internal int VertsPerQuadCount => VertsPerQuad;

    internal bool HasResidentTexture(long contentHash, int width, int height)
        => _textures.TryGetValue(contentHash, out var cached)
            && cached.Width == width
            && cached.Height == height;

    internal static void ThrowIfTextureOversized(string target, int width, int height)
    {
        if (width > MaxTextureDimension2D || height > MaxTextureDimension2D)
        {
            throw new InvalidOperationException(
                $"{target} {width}x{height} exceeds the supported " +
                $"{MaxTextureDimension2D}x{MaxTextureDimension2D} texture limit.");
        }
    }

    private struct CachedTexture
    {
        public nint Texture;
        public nint View;
        public nint BindGroup;
        public int Width, Height;
        public ulong LastFrame;
        public TextureAlphaMode AlphaMode;
        public GpuPaintTexture? Owner;
    }

    private enum TextureAlphaMode
    {
        Premultiplied,
        Straight,
    }

    private GpuBlendEngine(WebGPU api, WgpuExt? poll, Device* device, Queue* queue)
    {
        Api = api;
        Poll = poll;
        Device = device;
        Queue = queue;
        BuildLayout();
    }

    /// <summary>Default device for offscreen blend + readback. Null on no GPU.</summary>
    internal static GpuBlendEngine? CreateOffscreen()
    {
        try
        {
            var api = WebGPU.GetApi();
            var instance = api.CreateInstance((InstanceDescriptor*)null);
            if (instance == null)
            {
                return null;
            }

            var ok = RequestDevice(api, instance, compatibleSurface: null, out var device, out var queue, out var poll);
            api.InstanceRelease(instance);
            return ok ? new GpuBlendEngine(api, poll, device, queue) : null;
        }
        catch
        {
            _ = WgpuNativeLoader.Diagnose();
            return null;
        }
    }

    /// <summary>
    /// Surface-compatible device for on-screen present. Creates the wgpu surface
    /// from <paramref name="window"/>'s native handle, requests an adapter that
    /// supports it, and returns the surface plus its preferred colour format. Null
    /// on no GPU. The engine keeps the instance alive for the surface's lifetime.
    /// </summary>
    internal static GpuBlendEngine? CreateForSurface(INativeWindowSource window, out nint surface, out TextureFormat format)
    {
        surface = 0;
        format = TextureFormat.Bgra8Unorm;
        try
        {
            var api = WebGPU.GetApi();
            var instance = api.CreateInstance((InstanceDescriptor*)null);
            if (instance == null)
            {
                return null;
            }

            var surf = window.CreateWebGPUSurface(api, instance);
            return CreateForCreatedSurface(api, instance, surf, out surface, out format);
        }
        catch
        {
            _ = WgpuNativeLoader.Diagnose();
            return null;
        }
    }

    /// <summary>
    /// Surface-compatible device bound to a host-owned <c>CAMetalLayer</c> (macOS)
    /// — the Avalonia zero-copy page surface. Builds the wgpu surface straight from
    /// the layer via <c>SurfaceDescriptorFromMetalLayer</c> (so it embeds in a child
    /// NSView's layer instead of seizing the NSWindow), requests a surface-compatible
    /// adapter, and returns the surface plus its preferred colour format. Null on no
    /// GPU or a null layer. The engine keeps the instance alive for the surface's
    /// lifetime, the same lease as <see cref="CreateForSurface"/>.
    /// </summary>
    internal static GpuBlendEngine? CreateForMetalLayer(nint caMetalLayer, out nint surface, out TextureFormat format)
    {
        surface = 0;
        format = TextureFormat.Bgra8Unorm;
        if (caMetalLayer == 0)
        {
            return null;
        }

        try
        {
            var api = WebGPU.GetApi();
            var instance = api.CreateInstance((InstanceDescriptor*)null);
            if (instance == null)
            {
                return null;
            }

            var metal = new SurfaceDescriptorFromMetalLayer
            {
                Chain = new ChainedStruct { Next = null, SType = SType.SurfaceDescriptorFromMetalLayer },
                Layer = (void*)caMetalLayer,
            };
            var desc = new SurfaceDescriptor { NextInChain = (ChainedStruct*)&metal };
            var surf = api.InstanceCreateSurface(instance, in desc);
            return CreateForCreatedSurface(api, instance, surf, out surface, out format);
        }
        catch
        {
            _ = WgpuNativeLoader.Diagnose();
            return null;
        }
    }

    internal static GpuBlendEngine? CreateForWindowsHwnd(nint hwnd, nint hinstance, out nint surface, out TextureFormat format)
    {
        surface = 0;
        format = TextureFormat.Bgra8Unorm;
        if (hwnd == 0)
        {
            return null;
        }

        try
        {
            var api = WebGPU.GetApi();
            var instance = api.CreateInstance((InstanceDescriptor*)null);
            if (instance == null)
            {
                return null;
            }

            var windows = new SurfaceDescriptorFromWindowsHWND
            {
                Chain = new ChainedStruct { Next = null, SType = SType.SurfaceDescriptorFromWindowsHwnd },
                Hinstance = (void*)hinstance,
                Hwnd = (void*)hwnd,
            };
            var desc = new SurfaceDescriptor { NextInChain = (ChainedStruct*)&windows };
            var surf = api.InstanceCreateSurface(instance, in desc);
            return CreateForCreatedSurface(api, instance, surf, out surface, out format);
        }
        catch
        {
            _ = WgpuNativeLoader.Diagnose();
            return null;
        }
    }

    internal static GpuBlendEngine? CreateForXlibWindow(nint display, ulong window, out nint surface, out TextureFormat format)
    {
        surface = 0;
        format = TextureFormat.Bgra8Unorm;
        if (display == 0 || window == 0)
        {
            return null;
        }

        try
        {
            var api = WebGPU.GetApi();
            var instance = api.CreateInstance((InstanceDescriptor*)null);
            if (instance == null)
            {
                return null;
            }

            var xlib = new SurfaceDescriptorFromXlibWindow
            {
                Chain = new ChainedStruct { Next = null, SType = SType.SurfaceDescriptorFromXlibWindow },
                Display = (void*)display,
                Window = window,
            };
            var desc = new SurfaceDescriptor { NextInChain = (ChainedStruct*)&xlib };
            var surf = api.InstanceCreateSurface(instance, in desc);
            return CreateForCreatedSurface(api, instance, surf, out surface, out format);
        }
        catch
        {
            _ = WgpuNativeLoader.Diagnose();
            return null;
        }
    }

    private static GpuBlendEngine? CreateForCreatedSurface(
        WebGPU api,
        Instance* instance,
        Surface* surf,
        out nint surface,
        out TextureFormat format)
    {
        surface = 0;
        format = TextureFormat.Bgra8Unorm;
        if (surf == null)
        {
            api.InstanceRelease(instance);
            return null;
        }

        if (!RequestDevice(api, instance, compatibleSurface: surf, out var device, out var queue, out var poll))
        {
            api.SurfaceRelease(surf);
            api.InstanceRelease(instance);
            return null;
        }

        var caps = default(SurfaceCapabilities);
        var capsAdapter = GetAdapterForCaps(api, instance, surf);
        if (capsAdapter != null)
        {
            api.SurfaceGetCapabilities(surf, capsAdapter, ref caps);
            api.AdapterRelease(capsAdapter);
        }
        format = PickFormat(caps);

        surface = (nint)surf;
        return new GpuBlendEngine(api, poll, device, queue);
    }

    // Requesting capabilities needs an adapter; rather than thread it out of
    // RequestDevice we re-request a (cheap, cached by wgpu) compatible adapter.
    private static Adapter* GetAdapterForCaps(WebGPU api, Instance* instance, Surface* surf)
    {
        Adapter* adapter = null;
        var opts = new RequestAdapterOptions { CompatibleSurface = surf, PowerPreference = PowerPreference.HighPerformance };
        var cb = PfnRequestAdapterCallback.From((status, a, _, _) => { if (status == RequestAdapterStatus.Success) { adapter = a; } });
        api.InstanceRequestAdapter(instance, in opts, cb, null);
        return adapter;
    }

    private static TextureFormat PickFormat(SurfaceCapabilities caps)
    {
        // Prefer a plain (non-sRGB) 8-bit format so the blend runs in the same
        // gamma space as the CPU AlphaOver. Fall back to the surface's first
        // advertised format.
        for (var i = 0; i < (int)caps.FormatCount; i++)
        {
            var f = caps.Formats[i];
            if (f == TextureFormat.Bgra8Unorm || f == TextureFormat.Rgba8Unorm)
            {
                return f;
            }
        }
        return caps.FormatCount > 0 ? caps.Formats[0] : TextureFormat.Bgra8Unorm;
    }

    private static bool RequestDevice(WebGPU api, Instance* instance, Surface* compatibleSurface,
        out Device* device, out Queue* queue, out WgpuExt? poll)
    {
        device = null;
        queue = null;
        poll = null;

        Adapter* adapter = null;
        var adapterOpts = new RequestAdapterOptions
        {
            CompatibleSurface = compatibleSurface,
            PowerPreference = PowerPreference.HighPerformance,
        };
        var adapterCb = PfnRequestAdapterCallback.From((status, a, _, _) => { if (status == RequestAdapterStatus.Success) { adapter = a; } });
        api.InstanceRequestAdapter(instance, in adapterOpts, adapterCb, null);
        if (adapter == null)
        {
            return false;
        }

        Device* dev = null;
        var deviceDesc = default(DeviceDescriptor);
        var deviceCb = PfnRequestDeviceCallback.From((status, d, _, _) => { if (status == RequestDeviceStatus.Success) { dev = d; } });
        api.AdapterRequestDevice(adapter, in deviceDesc, deviceCb, null);
        api.AdapterRelease(adapter);
        if (dev == null)
        {
            return false;
        }

        var q = api.DeviceGetQueue(dev);
        if (q == null) { api.DeviceRelease(dev); return false; }

        // Swallow uncaptured device errors instead of letting wgpu's default
        // handler abort() the process.
        var errorCb = PfnErrorCallback.From((_, _, _) => { });
        api.DeviceSetUncapturedErrorCallback(dev, errorCb, null);

        try { poll = new WgpuExt(api.Context); } catch { poll = null; }

        device = dev;
        queue = q;
        return true;
    }

    // Explicit bind-group + pipeline layout shared by every per-format pipeline,
    // so a bind group built once is valid against any of them.
    private void BuildLayout()
    {
        var entries = stackalloc BindGroupLayoutEntry[2];
        entries[0] = new BindGroupLayoutEntry
        {
            Binding = 0,
            Visibility = ShaderStage.Fragment,
            Texture = new TextureBindingLayout
            {
                SampleType = TextureSampleType.Float,
                ViewDimension = TextureViewDimension.Dimension2D,
                Multisampled = false,
            },
        };
        entries[1] = new BindGroupLayoutEntry
        {
            Binding = 1,
            Visibility = ShaderStage.Fragment,
            Sampler = new SamplerBindingLayout { Type = SamplerBindingType.Filtering },
        };
        var bglDesc = new BindGroupLayoutDescriptor { EntryCount = 2, Entries = entries };
        _bindLayout = Api.DeviceCreateBindGroupLayout(Device, in bglDesc);

        var bgl = _bindLayout;
        var plDesc = new PipelineLayoutDescriptor { BindGroupLayoutCount = 1, BindGroupLayouts = &bgl };
        _pipelineLayout = Api.DeviceCreatePipelineLayout(Device, in plDesc);

        var samplerDesc = new SamplerDescriptor
        {
            AddressModeU = AddressMode.ClampToEdge,
            AddressModeV = AddressMode.ClampToEdge,
            AddressModeW = AddressMode.ClampToEdge,
            MagFilter = FilterMode.Linear,
            MinFilter = FilterMode.Linear,
            MipmapFilter = MipmapFilterMode.Nearest,
            LodMinClamp = 0,
            LodMaxClamp = 1,
            MaxAnisotropy = 1,
        };
        _sampler = Api.DeviceCreateSampler(Device, in samplerDesc);

        var codePtr = (byte*)SilkMarshal.StringToPtr(ShaderSource, NativeStringEncoding.UTF8);
        try
        {
            var wgsl = new ShaderModuleWGSLDescriptor
            {
                Chain = new ChainedStruct { SType = SType.ShaderModuleWgslDescriptor },
                Code = codePtr,
            };
            var shaderDesc = new ShaderModuleDescriptor { NextInChain = (ChainedStruct*)&wgsl };
            _shader = Api.DeviceCreateShaderModule(Device, in shaderDesc);
        }
        finally
        {
            SilkMarshal.Free((nint)codePtr);
        }
    }

    internal GpuPaintDevice GpuDevice => new((nint)Device, (nint)Queue);

    /// <summary>The texture-to-texture CSS filter passes (blur/colour matrix)
    /// sharing this engine's device and queue. Built on first use — pages
    /// without filters never pay for the extra pipeline.</summary>
    internal GpuFilterEngine FilterEngine => _filterEngine ??= new GpuFilterEngine(Api, Device, Queue);

    /// <summary>Lazily builds the blend pipeline for a target format and texture alpha mode.</summary>
    private RenderPipeline* PipelineFor(TextureFormat format, TextureAlphaMode alphaMode = TextureAlphaMode.Premultiplied)
    {
        var key = (format, alphaMode);
        if (_pipelines.TryGetValue(key, out var cached))
        {
            return (RenderPipeline*)cached;
        }

        var vsEntry = (byte*)SilkMarshal.StringToPtr("vs_main", NativeStringEncoding.UTF8);
        var fsEntryName = alphaMode == TextureAlphaMode.Straight ? "fs_straight" : "fs_premul";
        var fsEntry = (byte*)SilkMarshal.StringToPtr(fsEntryName, NativeStringEncoding.UTF8);
        try
        {
            var attrs = stackalloc VertexAttribute[3];
            attrs[0] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 };
            attrs[1] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 2 * sizeof(float), ShaderLocation = 1 };
            attrs[2] = new VertexAttribute { Format = VertexFormat.Float32, Offset = 4 * sizeof(float), ShaderLocation = 2 };
            var vbl = new VertexBufferLayout
            {
                ArrayStride = VertexStride,
                StepMode = VertexStepMode.Vertex,
                AttributeCount = 3,
                Attributes = attrs,
            };

            var blend = new BlendState
            {
                Color = new BlendComponent { SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add },
                Alpha = new BlendComponent { SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add },
            };
            var colorTarget = new ColorTargetState { Format = format, Blend = &blend, WriteMask = ColorWriteMask.All };
            var fragment = new FragmentState { Module = _shader, EntryPoint = fsEntry, TargetCount = 1, Targets = &colorTarget };
            var pipelineDesc = new RenderPipelineDescriptor
            {
                Layout = _pipelineLayout,
                Vertex = new VertexState { Module = _shader, EntryPoint = vsEntry, BufferCount = 1, Buffers = &vbl },
                Primitive = new PrimitiveState { Topology = PrimitiveTopology.TriangleList, FrontFace = FrontFace.Ccw, CullMode = CullMode.None },
                Multisample = new MultisampleState { Count = 1, Mask = ~0u, AlphaToCoverageEnabled = false },
                Fragment = &fragment,
            };
            var pipeline = Api.DeviceCreateRenderPipeline(Device, in pipelineDesc);
            if (pipeline == null)
            {
                throw new InvalidOperationException("WebGPU render pipeline creation failed.");
            }

            _pipelines[key] = (nint)pipeline;
            return pipeline;
        }
        finally
        {
            SilkMarshal.Free((nint)vsEntry);
            SilkMarshal.Free((nint)fsEntry);
        }
    }

    internal void BeginFrame() => _frame++;

    internal void AdoptTexture(long contentHash, GpuPaintTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ThrowIfTextureOversized("WebGPU layer texture", texture.Width, texture.Height);

        var textureHandle = texture.TextureHandle;
        var viewHandle = texture.TextureViewHandle;
        if (textureHandle == 0 || viewHandle == 0)
        {
            throw new InvalidOperationException("GPU paint texture did not expose valid WebGPU handles.");
        }

        var bindGroup = CreateBindGroup((TextureView*)viewHandle);
        var cached = new CachedTexture
        {
            Texture = textureHandle,
            View = viewHandle,
            BindGroup = bindGroup,
            Width = texture.Width,
            Height = texture.Height,
            LastFrame = _frame,
            AlphaMode = TextureAlphaMode.Straight,
            Owner = texture,
        };

        if (_textures.TryGetValue(contentHash, out var old))
        {
            _textureBytes -= BytesOf(old);
            ReleaseCached(old);
        }

        _textures[contentHash] = cached;
        _textureBytes += BytesOf(cached);
    }

    /// <summary>Uploads any layer whose content-hash texture isn't already resident.</summary>
    internal void UploadLayerTextures(IReadOnlyList<LayerBlend> ops)
    {
        for (var i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            if (op.IsBackdropFilter)
            {
                // A backdrop op has no pixels to upload — its texture is the
                // filtered snapshot, adopted mid-frame between render passes.
                continue;
            }

            if (_textures.TryGetValue(op.ContentHash, out var cached)
                && cached.Width == op.Width && cached.Height == op.Height)
            {
                cached.LastFrame = _frame;
                _textures[op.ContentHash] = cached;
                continue;
            }

            if (cached.Texture != 0)
            {
                _textureBytes -= BytesOf(cached);
                ReleaseCached(cached);
            }

            var bmp = op.RequireLocalPixels();
            var (texPtr, viewPtr, bgPtr) = CreateAndUpload(bmp);
            var entry = new CachedTexture
            {
                Texture = texPtr,
                View = viewPtr,
                BindGroup = bgPtr,
                Width = op.Width,
                Height = op.Height,
                LastFrame = _frame,
                AlphaMode = TextureAlphaMode.Premultiplied,
            };
            _textures[op.ContentHash] = entry;
            _textureBytes += BytesOf(entry);
        }
    }

    private (nint Texture, nint View, nint BindGroup) CreateAndUpload(RenderedBitmap bmp)
    {
        ThrowIfTextureOversized("WebGPU layer texture", bmp.Width, bmp.Height);
        Texture* tex = null;
        TextureView* view = null;
        BindGroup* bindGroup = null;
        try
        {
            var desc = new TextureDescriptor
            {
                Usage = TextureUsage.TextureBinding | TextureUsage.CopyDst,
                Dimension = TextureDimension.Dimension2D,
                Size = new Extent3D { Width = (uint)bmp.Width, Height = (uint)bmp.Height, DepthOrArrayLayers = 1 },
                Format = TextureFormat.Rgba8Unorm,
                MipLevelCount = 1,
                SampleCount = 1,
            };
            tex = Api.DeviceCreateTexture(Device, in desc);
            if (tex == null)
            {
                throw new InvalidOperationException("WebGPU layer texture creation failed.");
            }

            view = Api.TextureCreateView(tex, (TextureViewDescriptor*)null);
            if (view == null)
            {
                throw new InvalidOperationException("WebGPU layer texture view creation failed.");
            }

            var premul = Premultiply(bmp);
            var bytesPerRow = (uint)(bmp.Width * 4);
            var copyTex = new ImageCopyTexture { Texture = tex, MipLevel = 0, Origin = new Origin3D { X = 0, Y = 0, Z = 0 }, Aspect = TextureAspect.All };
            var layout = new TextureDataLayout { Offset = 0, BytesPerRow = bytesPerRow, RowsPerImage = (uint)bmp.Height };
            var extent = new Extent3D { Width = (uint)bmp.Width, Height = (uint)bmp.Height, DepthOrArrayLayers = 1 };
            fixed (byte* p = premul)
            {
                Api.QueueWriteTexture(Queue, in copyTex, p, (nuint)premul.Length, in layout, in extent);
            }

            bindGroup = (BindGroup*)CreateBindGroup(view);
            return ((nint)tex, (nint)view, (nint)bindGroup);
        }
        catch
        {
            if (bindGroup != null)
            {
                Api.BindGroupRelease(bindGroup);
            }

            if (view != null)
            {
                Api.TextureViewRelease(view);
            }

            if (tex != null)
            {
                Api.TextureRelease(tex);
            }

            throw;
        }
    }

    private nint CreateBindGroup(TextureView* view)
    {
        if (view == null)
        {
            throw new InvalidOperationException("WebGPU layer texture view is null.");
        }

        var entries = stackalloc BindGroupEntry[2];
        entries[0] = new BindGroupEntry { Binding = 0, TextureView = view };
        entries[1] = new BindGroupEntry { Binding = 1, Sampler = _sampler };
        var bgDesc = new BindGroupDescriptor { Layout = _bindLayout, EntryCount = 2, Entries = entries };
        var bindGroup = Api.DeviceCreateBindGroup(Device, in bgDesc);
        if (bindGroup == null)
        {
            throw new InvalidOperationException("WebGPU layer bind group creation failed.");
        }

        return (nint)bindGroup;
    }

    // Upload premultiplied pixels so the linear sampler filters premultiplied
    // colour — matching the CPU Sample(), which premultiplies before interpolating.
    private static byte[] Premultiply(RenderedBitmap bmp)
    {
        var src = bmp.Rgba;
        var dst = new byte[src.Length];
        for (var i = 0; i < src.Length; i += 4)
        {
            var a = src[i + 3];
            if (a == 255)
            {
                dst[i] = src[i];
                dst[i + 1] = src[i + 1];
                dst[i + 2] = src[i + 2];
                dst[i + 3] = 255;
            }
            else if (a == 0)
            {
                // Leave fully transparent texels at zero (premultiplied 0).
            }
            else
            {
                dst[i] = (byte)((src[i] * a + 127) / 255);
                dst[i + 1] = (byte)((src[i + 1] * a + 127) / 255);
                dst[i + 2] = (byte)((src[i + 2] * a + 127) / 255);
                dst[i + 3] = a;
            }
        }
        return dst;
    }

    /// <summary>
    /// Builds the per-op quad geometry and uploads it to the vertex buffer.
    /// Returns the total vertex count. <paramref name="targetWidth"/>/<paramref
    /// name="targetHeight"/> are the device-pixel dimensions of the render target.
    /// </summary>
    internal uint BuildAndUploadVertices(IReadOnlyList<LayerBlend> ops, int targetWidth, int targetHeight)
    {
        var totalVerts = ops.Count * VertsPerQuad;
        var verts = new float[totalVerts * FloatsPerVertex];
        var f = 0;
        for (var i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            var w = op.Width;
            var h = op.Height;
            var m = op.LocalToDevice;

            var c0 = Corner(m, 0, 0, 0, 0, targetWidth, targetHeight);
            var c1 = Corner(m, w, 0, 1, 0, targetWidth, targetHeight);
            var c2 = Corner(m, w, h, 1, 1, targetWidth, targetHeight);
            var c3 = Corner(m, 0, h, 0, 1, targetWidth, targetHeight);

            var op4 = op.Opacity;
            Emit(verts, ref f, c0, op4);
            Emit(verts, ref f, c1, op4);
            Emit(verts, ref f, c2, op4);
            Emit(verts, ref f, c0, op4);
            Emit(verts, ref f, c2, op4);
            Emit(verts, ref f, c3, op4);
        }

        var byteLen = (nuint)(verts.Length * sizeof(float));
        EnsureVertexBuffer(byteLen);
        if (byteLen > 0)
        {
            fixed (float* p = verts)
            {
                Api.QueueWriteBuffer(Queue, _vertexBuffer, 0, p, byteLen);
            }
        }
        return (uint)totalVerts;
    }

    private static (float, float, float, float) Corner(Matrix2D m, double lx, double ly, float u, float v, int width, int height)
    {
        var (dx, dy) = m.Transform(lx, ly);
        var nx = (float)(dx / width * 2.0 - 1.0);
        var ny = (float)(1.0 - dy / height * 2.0);
        return (nx, ny, u, v);
    }

    private static void Emit(float[] verts, ref int f, (float nx, float ny, float u, float v) c, float opacity)
    {
        verts[f++] = c.nx;
        verts[f++] = c.ny;
        verts[f++] = c.u;
        verts[f++] = c.v;
        verts[f++] = opacity;
    }

    private void EnsureVertexBuffer(nuint byteLen)
    {
        if (byteLen == 0)
        {
            return;
        }

        if (_vertexBuffer != null && _vertexCapacity >= byteLen)
        {
            return;
        }

        if (_vertexBuffer != null) { Api.BufferRelease(_vertexBuffer); _vertexBuffer = null; }
        var cap = (nuint)Align256((uint)byteLen);
        var desc = new BufferDescriptor { Usage = BufferUsage.Vertex | BufferUsage.CopyDst, Size = cap, MappedAtCreation = false };
        _vertexBuffer = Api.DeviceCreateBuffer(Device, in desc);
        _vertexCapacity = cap;
    }

    /// <summary>
    /// Records the blend into an already-open render pass: sets the pipeline for
    /// <paramref name="format"/>, binds the vertex buffer, then per layer sets the
    /// scissor + texture bind group and draws its quad. Call
    /// <see cref="BuildAndUploadVertices"/> first.
    /// </summary>
    internal void RecordBlend(RenderPassEncoder* pass, IReadOnlyList<LayerBlend> ops,
        TextureFormat format, uint vertexCount, int targetWidth, int targetHeight)
        => RecordBlendRange(pass, ops, format, 0, ops.Count, targetWidth, targetHeight);

    /// <summary>
    /// Records the blend draws for ops [<paramref name="start"/>,
    /// <paramref name="end"/>). Vertex offsets stay keyed by the GLOBAL op index
    /// (the whole frame's quads are uploaded once by
    /// <see cref="BuildAndUploadVertices"/>), so the backdrop segmentation can
    /// split one frame across several render passes without rebuilding geometry.
    /// </summary>
    internal void RecordBlendRange(RenderPassEncoder* pass, IReadOnlyList<LayerBlend> ops,
        TextureFormat format, int start, int end, int targetWidth, int targetHeight)
    {
        if (start >= end)
        {
            return;
        }

        if (_vertexBuffer != null)
        {
            Api.RenderPassEncoderSetVertexBuffer(pass, 0, _vertexBuffer, 0, _vertexCapacity);
        }

        RenderPipeline* boundPipeline = null;
        for (var i = start; i < end; i++)
        {
            var op = ops[i];
            if (!_textures.TryGetValue(op.ContentHash, out var tex))
            {
                throw new InvalidOperationException("GPU layer texture was not resident after upload.");
            }

            var pipeline = PipelineFor(format, tex.AlphaMode);
            if (pipeline != boundPipeline)
            {
                Api.RenderPassEncoderSetPipeline(pass, pipeline);
                boundPipeline = pipeline;
            }

            if (!TrySetScissor(pass, op.ClipDevice, targetWidth, targetHeight))
            {
                continue;
            }

            Api.RenderPassEncoderSetBindGroup(pass, 0, (BindGroup*)tex.BindGroup, 0, (uint*)null);
            Api.RenderPassEncoderDraw(pass, VertsPerQuad, 1, (uint)(i * VertsPerQuad), 0);
        }
    }

    private bool TrySetScissor(RenderPassEncoder* pass, Rect? clip, int width, int height)
    {
        int x = 0, y = 0, w = width, h = height;
        if (clip is { } cd)
        {
            var minX = Math.Max(0, (int)Math.Floor(cd.X));
            var minY = Math.Max(0, (int)Math.Floor(cd.Y));
            var maxX = Math.Min(width, (int)Math.Ceiling(cd.Right));
            var maxY = Math.Min(height, (int)Math.Ceiling(cd.Bottom));
            if (maxX <= minX || maxY <= minY)
            {
                return false;
            }

            x = minX; y = minY;
            w = maxX - minX;
            h = maxY - minY;
        }
        Api.RenderPassEncoderSetScissorRect(pass, (uint)x, (uint)y, (uint)w, (uint)h);
        return true;
    }

    /// <summary>True when any op in <paramref name="ops"/> is a backdrop-filter op.</summary>
    internal static bool HasBackdropOps(IReadOnlyList<LayerBlend> ops)
    {
        for (var i = 0; i < ops.Count; i++)
        {
            if (ops[i].IsBackdropFilter)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Renders one frame's ops into <paramref name="targetView"/>, splitting the
    /// render pass at every backdrop-filter op: the pass so far is submitted, the
    /// op's region of <paramref name="target"/> is snapshotted and run through
    /// <see cref="FilterEngine"/>, the result is adopted as the op's texture, and
    /// a new pass (LoadOp.Load) draws it back over the region before the
    /// remaining ops continue. A frame without backdrop ops records exactly one
    /// pass, as before. Overlays are recorded in the final pass. The target must
    /// have <see cref="TextureUsage.CopySrc"/> usage when ops carry backdrops.
    /// Call <see cref="UploadLayerTextures"/> and
    /// <see cref="BuildAndUploadVertices"/> first.
    /// </summary>
    internal void RenderSegmentedPasses(
        Texture* target,
        TextureView* targetView,
        TextureFormat format,
        int width,
        int height,
        IReadOnlyList<LayerBlend> ops,
        GpuOverlayRenderer overlays,
        IReadOnlyList<GpuOverlayLayer>? overlayLayers)
    {
        var segStart = 0;
        // Where to scan for the NEXT backdrop op. Distinct from segStart: a
        // successfully filtered backdrop op is drawn as the first op of its
        // segment, so the scan must start past it or the loop never advances.
        var scanFrom = 0;
        var loadOp = LoadOp.Clear;
        while (true)
        {
            var next = -1;
            for (var i = scanFrom; i < ops.Count; i++)
            {
                if (ops[i].IsBackdropFilter)
                {
                    next = i;
                    break;
                }
            }

            var segEnd = next >= 0 ? next : ops.Count;
            var isLast = next < 0;

            var encoder = Api.DeviceCreateCommandEncoder(Device, (CommandEncoderDescriptor*)null);
            if (encoder == null)
            {
                throw new InvalidOperationException("WebGPU blend command encoder creation failed.");
            }

            var colorAttachment = new RenderPassColorAttachment
            {
                View = targetView,
                ResolveTarget = null,
                LoadOp = loadOp,
                StoreOp = StoreOp.Store,
                // Opaque white base — the page background the flat path also clears to.
                ClearValue = new Color { R = 1, G = 1, B = 1, A = 1 },
            };
            var passDesc = new RenderPassDescriptor { ColorAttachmentCount = 1, ColorAttachments = &colorAttachment };
            var pass = Api.CommandEncoderBeginRenderPass(encoder, in passDesc);
            RecordBlendRange(pass, ops, format, segStart, segEnd, width, height);
            if (isLast)
            {
                overlays.Record(pass, overlayLayers, width, height, format);
            }

            Api.RenderPassEncoderEnd(pass);

            // The snapshot copy rides the SAME encoder, after the pass: queue
            // order guarantees it reads everything composited so far.
            GpuPaintTexture? snapshot = null;
            if (!isLast)
            {
                snapshot = EncodeBackdropSnapshot(encoder, target, ops[next], width, height);
            }

            var cmd = Api.CommandEncoderFinish(encoder, (CommandBufferDescriptor*)null);
            Api.QueueSubmit(Queue, 1, &cmd);
            Api.CommandBufferRelease(cmd);
            Api.RenderPassEncoderRelease(pass);
            Api.CommandEncoderRelease(encoder);

            if (isLast)
            {
                return;
            }

            loadOp = LoadOp.Load;
            // On success the backdrop op draws first in the next segment; a
            // chain that can't run keeps today's behavior (no backdrop effect).
            segStart = snapshot is not null && TryApplyBackdrop(snapshot, ops[next]) ? next : next + 1;
            scanFrom = next + 1;
        }
    }

    /// <summary>
    /// Encodes a copy of the backdrop op's region of <paramref name="source"/>
    /// into a fresh sampleable texture. The op's quad was built as an integer
    /// device translation of a region-sized rect, so the texture is op-sized and
    /// the draw's 0..1 UVs map one-to-one; any part clipped by the target edge
    /// stays transparent (wgpu zero-initializes). Returns null when the chain
    /// can't run on the GPU or the region is empty — the caller skips the op.
    /// </summary>
    private GpuPaintTexture? EncodeBackdropSnapshot(
        CommandEncoder* encoder, Texture* source, in LayerBlend op, int targetWidth, int targetHeight)
    {
        if (op.BackdropFilters is not { } filters || !GpuFilterEngine.Supports(filters))
        {
            return null;
        }

        var m = op.LocalToDevice;
        const double eps = 1e-6;
        if (Math.Abs(m.A - 1d) > eps || Math.Abs(m.D - 1d) > eps || Math.Abs(m.B) > eps || Math.Abs(m.C) > eps)
        {
            return null; // built as a pure translation; anything else is unexpected
        }

        var dx = (int)Math.Round(m.E);
        var dy = (int)Math.Round(m.F);
        var x0 = Math.Max(0, dx);
        var y0 = Math.Max(0, dy);
        var x1 = Math.Min(targetWidth, dx + op.Width);
        var y1 = Math.Min(targetHeight, dy + op.Height);
        if (x1 <= x0 || y1 <= y0)
        {
            return null;
        }

        ThrowIfTextureOversized("WebGPU backdrop snapshot", op.Width, op.Height);
        var desc = new TextureDescriptor
        {
            Usage = TextureUsage.TextureBinding | TextureUsage.CopyDst,
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D { Width = (uint)op.Width, Height = (uint)op.Height, DepthOrArrayLayers = 1 },
            Format = TextureFormat.Rgba8Unorm,
            MipLevelCount = 1,
            SampleCount = 1,
        };
        var tex = Api.DeviceCreateTexture(Device, in desc);
        if (tex == null)
        {
            throw new InvalidOperationException("WebGPU backdrop snapshot texture creation failed.");
        }

        var view = Api.TextureCreateView(tex, (TextureViewDescriptor*)null);
        if (view == null)
        {
            Api.TextureRelease(tex);
            throw new InvalidOperationException("WebGPU backdrop snapshot view creation failed.");
        }

        var src = new ImageCopyTexture { Texture = source, MipLevel = 0, Origin = new Origin3D { X = (uint)x0, Y = (uint)y0, Z = 0 }, Aspect = TextureAspect.All };
        var dst = new ImageCopyTexture { Texture = tex, MipLevel = 0, Origin = new Origin3D { X = (uint)(x0 - dx), Y = (uint)(y0 - dy), Z = 0 }, Aspect = TextureAspect.All };
        var extent = new Extent3D { Width = (uint)(x1 - x0), Height = (uint)(y1 - y0), DepthOrArrayLayers = 1 };
        Api.CommandEncoderCopyTextureToTexture(encoder, in src, in dst, in extent);

        return new GpuPaintTexture((nint)tex, (nint)view, op.Width, op.Height,
            PaintTextureFormat.Rgba8Unorm, new OwnedTextureHandles(Api, (nint)tex, (nint)view));
    }

    /// <summary>
    /// Runs the backdrop chain over <paramref name="snapshot"/> (consumed either
    /// way) and adopts the result under the op's content hash so the next
    /// segment's draw finds it resident. The hash is re-adopted every frame —
    /// the backdrop content under an element changes without the op knowing.
    /// </summary>
    private bool TryApplyBackdrop(GpuPaintTexture snapshot, in LayerBlend op)
    {
        GpuPaintTexture filtered;
        try
        {
            filtered = FilterEngine.Apply(snapshot, op.BackdropFilters!, op.FilterScale);
        }
        catch (InvalidOperationException)
        {
            // Apply consumed the snapshot either way; skip the draw.
            return false;
        }

        AdoptTexture(op.ContentHash, filtered);
        return true;
    }

    /// <summary>
    /// Draws <paramref name="source"/> as one full-target triangle into an open
    /// render pass — the final present blit for a frame that had to render into
    /// an intermediate scene texture (the swapchain texture cannot be a copy
    /// source for backdrop snapshots). Returns the bind group handle; the
    /// CALLER must release it AFTER ending the pass — the open pass holds the
    /// recorded id, and releasing early faults wgpu at RenderPassEncoderEnd
    /// (the abort the first live backdrop frames hit).
    /// </summary>
    internal nint RecordFullFrameBlit(RenderPassEncoder* pass, TextureView* source, TextureFormat format)
    {
        var pipeline = BlitPipelineFor(format);
        Api.RenderPassEncoderSetPipeline(pass, pipeline);
        var bindGroup = CreateBindGroup(source);
        Api.RenderPassEncoderSetBindGroup(pass, 0, (BindGroup*)bindGroup, 0, (uint*)null);
        Api.RenderPassEncoderDraw(pass, 3, 1, 0, 0);
        return bindGroup;
    }

    private RenderPipeline* BlitPipelineFor(TextureFormat format)
    {
        if (_blitPipelines.TryGetValue(format, out var cached))
        {
            return (RenderPipeline*)cached;
        }

        var vsEntry = (byte*)SilkMarshal.StringToPtr("vs_blit", NativeStringEncoding.UTF8);
        var fsEntry = (byte*)SilkMarshal.StringToPtr("fs_blit", NativeStringEncoding.UTF8);
        try
        {
            // Replace, not blend: the scene texture is the whole frame.
            var colorTarget = new ColorTargetState { Format = format, Blend = null, WriteMask = ColorWriteMask.All };
            var fragment = new FragmentState { Module = _shader, EntryPoint = fsEntry, TargetCount = 1, Targets = &colorTarget };
            var pipelineDesc = new RenderPipelineDescriptor
            {
                Layout = _pipelineLayout,
                Vertex = new VertexState { Module = _shader, EntryPoint = vsEntry, BufferCount = 0, Buffers = null },
                Primitive = new PrimitiveState { Topology = PrimitiveTopology.TriangleList, FrontFace = FrontFace.Ccw, CullMode = CullMode.None },
                Multisample = new MultisampleState { Count = 1, Mask = ~0u, AlphaToCoverageEnabled = false },
                Fragment = &fragment,
            };
            var pipeline = Api.DeviceCreateRenderPipeline(Device, in pipelineDesc);
            if (pipeline == null)
            {
                throw new InvalidOperationException("WebGPU blit pipeline creation failed.");
            }

            _blitPipelines[format] = (nint)pipeline;
            return pipeline;
        }
        finally
        {
            SilkMarshal.Free((nint)vsEntry);
            SilkMarshal.Free((nint)fsEntry);
        }
    }

    /// <summary>Releases a wgpu texture + view pair owned by a <see cref="GpuPaintTexture"/>.</summary>
    private sealed class OwnedTextureHandles(WebGPU api, nint texture, nint view) : IDisposable
    {
        private nint _texture = texture;
        private nint _view = view;

        public void Dispose()
        {
            if (_view != 0)
            {
                api.TextureViewRelease((TextureView*)_view);
                _view = 0;
            }

            if (_texture != 0)
            {
                api.TextureRelease((Texture*)_texture);
                _texture = 0;
            }
        }
    }

    internal void EvictStale()
    {
        if (_textures.Count == 0)
        {
            return;
        }

        List<long>? drop = null;
        foreach (var kv in _textures)
        {
            if (_frame - kv.Value.LastFrame > EvictAfterFrames)
            {
                (drop ??= new List<long>()).Add(kv.Key);
            }
        }
        if (drop is not null)
        {
            foreach (var key in drop)
            {
                DropEntry(key, _textures[key]);
            }
        }

        EvictToBudget();
    }

    // Evicts least-recently-used tiles (oldest LastFrame first) until resident GPU
    // bytes are under budget. Never evicts this frame's working set (LastFrame ==
    // _frame) — the visible viewport is tens of MB, far under the budget. The sort
    // only runs while over budget, which after the first drain is rare (steady
    // state adds a few tiles per frame).
    private void EvictToBudget()
    {
        if (_textureBytes <= _maxTextureBytes)
        {
            return;
        }

        var candidates = new List<KeyValuePair<long, CachedTexture>>(_textures.Count);
        foreach (var kv in _textures)
        {
            if (kv.Value.LastFrame != _frame)
            {
                candidates.Add(kv);
            }
        }
        candidates.Sort(static (a, b) => a.Value.LastFrame.CompareTo(b.Value.LastFrame));
        foreach (var kv in candidates)
        {
            if (_textureBytes <= _maxTextureBytes)
            {
                break;
            }

            DropEntry(kv.Key, kv.Value);
        }
    }

    private void DropEntry(long key, CachedTexture c)
    {
        _textureBytes -= BytesOf(c);
        ReleaseCached(c);
        _textures.Remove(key);
    }

    private void ReleaseCached(CachedTexture c)
    {
        if (c.BindGroup != 0)
        {
            Api.BindGroupRelease((BindGroup*)c.BindGroup);
        }

        if (c.Owner is not null)
        {
            c.Owner.Dispose();
            return;
        }

        if (c.View != 0)
        {
            Api.TextureViewRelease((TextureView*)c.View);
        }

        if (c.Texture != 0)
        {
            Api.TextureRelease((Texture*)c.Texture);
        }
    }

    internal static uint Align256(uint value) => (value + 255u) & ~255u;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var c in _textures.Values)
        {
            ReleaseCached(c);
        }

        _textures.Clear();
        _textureBytes = 0;
        foreach (var p in _pipelines.Values)
        {
            Api.RenderPipelineRelease((RenderPipeline*)p);
        }

        _pipelines.Clear();
        foreach (var p in _blitPipelines.Values)
        {
            Api.RenderPipelineRelease((RenderPipeline*)p);
        }

        _blitPipelines.Clear();
        _filterEngine?.Dispose();
        _filterEngine = null;
        if (_vertexBuffer != null) { Api.BufferRelease(_vertexBuffer); _vertexBuffer = null; }
        if (_shader != null) { Api.ShaderModuleRelease(_shader); _shader = null; }
        if (_sampler != null) { Api.SamplerRelease(_sampler); _sampler = null; }
        if (_pipelineLayout != null) { Api.PipelineLayoutRelease(_pipelineLayout); _pipelineLayout = null; }
        if (_bindLayout != null) { Api.BindGroupLayoutRelease(_bindLayout); _bindLayout = null; }
        // TryDispose also tears down the adapter's cached WebGPU device context,
        // so the compositor never references the ImageSharp adapter type directly.
        ImageSharpWebGpuDeviceStateCache.TryDispose((nint)Device);
        if (Queue != null)
        {
            Api.QueueRelease(Queue);
        }

        if (Device != null)
        {
            Api.DeviceRelease(Device);
        }
    }

    // CPU uploads are premultiplied before they reach WebGPU. Adopted
    // ImageSharp render targets stay straight RGBA, so they use a fragment entry
    // that premultiplies the sampled color before alpha-over blending.
    private const string ShaderSource = @"
struct VsOut {
    @builtin(position) pos : vec4<f32>,
    @location(0) uv : vec2<f32>,
    @location(1) opacity : f32,
};

@vertex
fn vs_main(@location(0) pos : vec2<f32>, @location(1) uv : vec2<f32>, @location(2) opacity : f32) -> VsOut {
    var o : VsOut;
    o.pos = vec4<f32>(pos, 0.0, 1.0);
    o.uv = uv;
    o.opacity = opacity;
    return o;
}

@group(0) @binding(0) var tex : texture_2d<f32>;
@group(0) @binding(1) var smp : sampler;

@fragment
fn fs_premul(in : VsOut) -> @location(0) vec4<f32> {
    let c = textureSample(tex, smp, in.uv); // premultiplied
    return c * in.opacity;
}

@fragment
fn fs_straight(in : VsOut) -> @location(0) vec4<f32> {
    let c = textureSample(tex, smp, in.uv);
    return vec4<f32>(c.rgb * c.a, c.a) * in.opacity;
}

// Full-target copy used to present an intermediate scene texture (backdrop
// frames render offscreen because a swapchain texture cannot be a copy source).
@vertex
fn vs_blit(@builtin(vertex_index) vi : u32) -> VsOut {
    var corners = array<vec2<f32>, 3>(
        vec2<f32>(-1.0, 3.0),
        vec2<f32>(-1.0, -1.0),
        vec2<f32>(3.0, -1.0));
    let p = corners[vi];
    var o : VsOut;
    o.pos = vec4<f32>(p, 0.0, 1.0);
    o.uv = vec2<f32>((p.x + 1.0) * 0.5, (1.0 - p.y) * 0.5);
    o.opacity = 1.0;
    return o;
}

@fragment
fn fs_blit(in : VsOut) -> @location(0) vec4<f32> {
    return textureSample(tex, smp, in.uv);
}
";
}
