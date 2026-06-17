using Silk.NET.Core.Contexts;
using Silk.NET.WebGPU;
using Starling.Common.Diagnostics;
using Starling.Paint.Backend;

namespace Starling.Paint.Compositor;

/// <summary>
/// Owns a WebGPU surface for a native window and presents compositor frames to
/// it. This path keeps layer textures on the GPU and draws the final frame into
/// the surface texture. It does not read pixels back to the CPU.
/// </summary>
/// <remarks>
/// This presenter uses the same <see cref="GpuBlendEngine"/> as the offscreen
/// <see cref="GpuLayerCompositor"/>. Changed layer tiles are drawn into textures
/// on this device and then adopted by the cache. <see cref="PresentOps"/> records
/// the blend pass, draws GPU overlay layers, and presents the current surface
/// texture.
/// </remarks>
public sealed unsafe class GpuSurfacePresenter : IDisposable
{
    private readonly GpuBlendEngine _engine;
    private readonly GpuOverlayRenderer _overlays;
    private readonly Surface* _surface;
    private readonly TextureFormat _format;
    private readonly object _gate = new();
    private int _width, _height;
    private bool _configured;

    // Intermediate whole-frame target used only when the frame carries
    // backdrop-filter ops: a swapchain texture cannot be a copy source for the
    // backdrop snapshot, so those frames render here (RenderAttachment |
    // TextureBinding | CopySrc) and the swapchain gets one full-frame blit.
    private Texture* _sceneTex;
    private TextureView* _sceneView;
    private int _sceneW, _sceneH;

    private GpuSurfacePresenter(GpuBlendEngine engine, Surface* surface, TextureFormat format)
    {
        _engine = engine;
        _overlays = new GpuOverlayRenderer(engine);
        _surface = surface;
        _format = format;
    }

    /// <summary>
    /// Creates a presenter for a Silk.NET native window. Returns <c>null</c> when
    /// WebGPU cannot create a surface or find an adapter. The window must provide
    /// native handles through <see cref="INativeWindowSource"/>.
    /// </summary>
    public static GpuSurfacePresenter? CreateForWindow(INativeWindowSource window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var engine = GpuBlendEngine.CreateForSurface(window, out var surface, out var format);
        if (engine is null || surface == 0)
        {
            return null;
        }

        return new GpuSurfacePresenter(engine, (Surface*)surface, format);
    }

    /// <summary>
    /// Creates a presenter for a host-owned <c>CAMetalLayer</c>. This is used by
    /// the macOS child view that hosts the page surface. Returns <c>null</c> when
    /// the layer is zero or WebGPU cannot create a surface.
    /// </summary>
    public static GpuSurfacePresenter? CreateForMetalLayer(nint caMetalLayer)
    {
        if (caMetalLayer == 0)
        {
            return null;
        }

        var engine = GpuBlendEngine.CreateForMetalLayer(caMetalLayer, out var surface, out var format);
        if (engine is null || surface == 0)
        {
            return null;
        }

        return new GpuSurfacePresenter(engine, (Surface*)surface, format);
    }

    /// <summary>
    /// Creates a presenter for a Windows window handle. Returns <c>null</c> when
    /// <paramref name="hwnd"/> is zero or WebGPU cannot create a surface.
    /// </summary>
    public static GpuSurfacePresenter? CreateForWindowsHwnd(nint hwnd, nint hinstance)
    {
        if (hwnd == 0)
        {
            return null;
        }

        var engine = GpuBlendEngine.CreateForWindowsHwnd(hwnd, hinstance, out var surface, out var format);
        if (engine is null || surface == 0)
        {
            return null;
        }

        return new GpuSurfacePresenter(engine, (Surface*)surface, format);
    }

    /// <summary>
    /// Creates a presenter for an Xlib window. Returns <c>null</c> when the
    /// display or window handle is zero, or when WebGPU cannot create a surface.
    /// </summary>
    public static GpuSurfacePresenter? CreateForXlibWindow(nint display, ulong window)
    {
        if (display == 0 || window == 0)
        {
            return null;
        }

        var engine = GpuBlendEngine.CreateForXlibWindow(display, window, out var surface, out var format);
        if (engine is null || surface == 0)
        {
            return null;
        }

        return new GpuSurfacePresenter(engine, (Surface*)surface, format);
    }

    /// <summary>The color format used by this surface.</summary>
    public TextureFormat Format => _format;

    internal bool HasResidentTexture(long contentHash, int width, int height)
    {
        lock (_gate)
        {
            return _engine.HasResidentTexture(contentHash, width, height);
        }
    }

    internal GpuPaintDevice GpuDevice => _engine.GpuDevice;

    internal void AdoptTexture(long contentHash, GpuPaintTexture texture)
    {
        lock (_gate)
        {
            _engine.AdoptTexture(contentHash, texture);
        }
    }

    internal static bool SupportsLayerFilters(IReadOnlyList<DisplayList.FilterFunction> filters)
        => GpuFilterEngine.Supports(filters);

    internal GpuPaintTexture? ApplyLayerFilters(GpuPaintTexture source,
        IReadOnlyList<DisplayList.FilterFunction> filters, float scale)
    {
        lock (_gate)
        {
            try
            {
                return _engine.FilterEngine.Apply(source, filters, scale);
            }
            catch (InvalidOperationException)
            {
                // Apply consumed the source either way; null sends the caller
                // down the legacy bracket path.
                return null;
            }
        }
    }

    /// <summary>
    /// Sets the surface size in device pixels. Call after creation and when the
    /// framebuffer size changes. <see cref="PresentOps"/> also reconfigures when
    /// its size does not match the current surface.
    /// </summary>
    public void Configure(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException("Surface dimensions must be positive.");
        }

        GpuBlendEngine.ThrowIfTextureOversized("WebGPU surface target", width, height);
        lock (_gate)
        {
            var config = new SurfaceConfiguration
            {
                Device = _engine.Device,
                Format = _format,
                Usage = TextureUsage.RenderAttachment,
                Width = (uint)width,
                Height = (uint)height,
                PresentMode = PresentMode.Fifo, // vsync; the OS paces us
                AlphaMode = CompositeAlphaMode.Auto,
            };
            _engine.Api.SurfaceConfigure(_surface, in config);
            _width = width;
            _height = height;
            _configured = true;
        }
    }

    /// <summary>
    /// Draws <paramref name="ops"/> into the current surface texture, then
    /// presents it. <paramref name="width"/> and <paramref name="height"/> are
    /// device pixels. Returns <c>true</c> after the frame is submitted and
    /// presented. Surface and GPU errors throw because this path should not return
    /// a CPU fallback frame.
    /// </summary>
    internal bool PresentOps(
        int width,
        int height,
        IReadOnlyList<LayerBlend> ops,
        IReadOnlyList<GpuOverlayLayer>? overlayLayers = null)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException("Surface present target must have positive dimensions.");
        }

        lock (_gate)
        {
            if (!_configured || _width != width || _height != height)
            {
                Configure(width, height);
            }

            var api = _engine.Api;
            // Acquire the swapchain's next drawable. Under PresentMode.Fifo this is
            // the call that blocks when the drawable pool is starved (on a
            // CAMetalLayer it bottoms out in Metal's nextDrawable, which waits up to
            // ~1s). Wrapped in its own span so a present stall is attributed here
            // rather than vanishing into gui.render's self-time.
            SurfaceTexture st = default;
            using (StarlingTelemetry.Span(RenderMetrics.PaintArea, RenderMetrics.PresentAcquireOp))
            {
                api.SurfaceGetCurrentTexture(_surface, ref st);
            }

            if (st.Status != SurfaceGetCurrentTextureStatus.Success)
            {
                if (st.Texture != null)
                {
                    api.TextureRelease(st.Texture);
                }

                throw new InvalidOperationException($"GPU surface acquire failed with status {st.Status}.");
            }
            if (st.Texture == null)
            {
                throw new InvalidOperationException("GPU surface acquire succeeded without a texture.");
            }

            TextureView* view = null;
            CommandEncoder* encoder = null;
            RenderPassEncoder* pass = null;
            CommandBuffer* cmd = null;
            try
            {
                using (StarlingTelemetry.Span(RenderMetrics.PaintArea, RenderMetrics.PresentEncodeOp))
                {
                    _engine.BeginFrame();
                    _engine.UploadLayerTextures(ops);
                    var vertexCount = _engine.BuildAndUploadVertices(ops, width, height);

                    view = api.TextureCreateView(st.Texture, (TextureViewDescriptor*)null);
                    if (view == null)
                    {
                        throw new InvalidOperationException("GPU surface texture view creation failed.");
                    }

                    var hasBackdrops = GpuBlendEngine.HasBackdropOps(ops);
                    if (hasBackdrops)
                    {
                        // Backdrop frame: render every segment into the scene
                        // texture (Rgba8Unorm, snapshot-copyable), then blit it
                        // to the swapchain as the only on-surface draw.
                        EnsureSceneTarget(width, height);
                        _engine.RenderSegmentedPasses(_sceneTex, _sceneView, TextureFormat.Rgba8Unorm,
                            width, height, ops, _overlays, overlayLayers);
                    }

                    encoder = api.DeviceCreateCommandEncoder(_engine.Device, (CommandEncoderDescriptor*)null);
                    if (encoder == null)
                    {
                        throw new InvalidOperationException("GPU surface command encoder creation failed.");
                    }

                    var colorAttachment = new RenderPassColorAttachment
                    {
                        View = view,
                        ResolveTarget = null,
                        LoadOp = LoadOp.Clear,
                        StoreOp = StoreOp.Store,
                        ClearValue = new Color { R = 1, G = 1, B = 1, A = 1 }, // opaque white base
                    };
                    var passDesc = new RenderPassDescriptor { ColorAttachmentCount = 1, ColorAttachments = &colorAttachment };
                    pass = api.CommandEncoderBeginRenderPass(encoder, in passDesc);
                    if (pass == null)
                    {
                        throw new InvalidOperationException("GPU surface render pass creation failed.");
                    }

                    nint blitBindGroup = 0;
                    if (hasBackdrops)
                    {
                        blitBindGroup = _engine.RecordFullFrameBlit(pass, _sceneView, _format);
                    }
                    else
                    {
                        _engine.RecordBlend(pass, ops, _format, vertexCount, width, height);
                        _overlays.Record(pass, overlayLayers, width, height, _format);
                    }

                    api.RenderPassEncoderEnd(pass);
                    // Only after End: the open pass holds the recorded bind-group
                    // id, and an early release faults wgpu inside End.
                    if (blitBindGroup != 0)
                    {
                        api.BindGroupRelease((Silk.NET.WebGPU.BindGroup*)blitBindGroup);
                    }

                    cmd = api.CommandEncoderFinish(encoder, (CommandBufferDescriptor*)null);
                    if (cmd == null)
                    {
                        throw new InvalidOperationException("GPU surface command buffer creation failed.");
                    }

                    api.QueueSubmit(_engine.Queue, 1, &cmd);
                }

                using (StarlingTelemetry.Span(RenderMetrics.PaintArea, RenderMetrics.PresentSwapOp))
                {
                    api.SurfacePresent(_surface);
                }

                _engine.EvictStale();
                return true;
            }
            finally
            {
                if (cmd != null)
                {
                    api.CommandBufferRelease(cmd);
                }

                if (pass != null)
                {
                    api.RenderPassEncoderRelease(pass);
                }

                if (encoder != null)
                {
                    api.CommandEncoderRelease(encoder);
                }

                if (view != null)
                {
                    api.TextureViewRelease(view);
                }

                api.TextureRelease(st.Texture);
            }
        }
    }

    private void EnsureSceneTarget(int width, int height)
    {
        if (_sceneTex != null && _sceneW == width && _sceneH == height)
        {
            return;
        }

        var api = _engine.Api;
        ReleaseSceneTarget(api);

        var desc = new TextureDescriptor
        {
            Usage = TextureUsage.RenderAttachment | TextureUsage.TextureBinding | TextureUsage.CopySrc,
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D { Width = (uint)width, Height = (uint)height, DepthOrArrayLayers = 1 },
            Format = TextureFormat.Rgba8Unorm,
            MipLevelCount = 1,
            SampleCount = 1,
        };
        _sceneTex = api.DeviceCreateTexture(_engine.Device, in desc);
        if (_sceneTex == null)
        {
            throw new InvalidOperationException("GPU scene texture creation failed.");
        }

        _sceneView = api.TextureCreateView(_sceneTex, (TextureViewDescriptor*)null);
        if (_sceneView == null)
        {
            api.TextureRelease(_sceneTex);
            _sceneTex = null;
            throw new InvalidOperationException("GPU scene texture view creation failed.");
        }

        _sceneW = width;
        _sceneH = height;
    }

    private void ReleaseSceneTarget(WebGPU api)
    {
        if (_sceneView != null)
        {
            api.TextureViewRelease(_sceneView);
            _sceneView = null;
        }

        if (_sceneTex != null)
        {
            api.TextureRelease(_sceneTex);
            _sceneTex = null;
        }

        _sceneW = 0;
        _sceneH = 0;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            ReleaseSceneTarget(_engine.Api);
            _engine.Api.SurfaceRelease(_surface);
            _overlays.Dispose();
            _engine.Dispose();
        }
    }
}
