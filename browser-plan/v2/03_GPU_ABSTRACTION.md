# v2 — GPU abstraction

> **Scope (In):** The `Starling.Gpu` seam and the rule that keeps native graphics in one
> project.
> **Scope (Out):** The scene model (see [02_SCENE_MODEL.md](02_SCENE_MODEL.md)) and the
> renderer that records draw calls (Phase 2).
> **Code:** `src/v2/Starling.Gpu/`. Tests: `tests/v2/Starling.Gpu.Tests/`.

---

## A. Why a seam

v1 already drives wgpu-native through Silk.NET, but the device, the queue, the
pipelines, and the texture cache all live inside `Starling.Paint/Compositor`, holding
raw pointers. That works, but it ties the compositor to one binding and to wgpu's
three-month breaking cadence.

v2 puts a small WebGPU-shaped seam in the middle. Renderer and compositor code depends
only on the seam. One backend project holds every raw handle.

```
Starling.Renderer.WebGpu        depends on Starling.Gpu
Starling.Compositor             depends on Starling.Gpu
Starling.Gpu                    interfaces only, pure managed
Starling.Gpu.WgpuNative         the only project with raw wgpu / Silk pointers
Starling.Gpu.Dawn               a later second provider, same seam
```

## B. The seam

The seam is shaped like WebGPU on purpose. wgpu, Dawn, and the web platform all speak
the same model, so the abstraction maps cleanly onto any of them.

```
IGpuDevice          create buffers, textures, encoders; expose the queue and limits
IGpuQueue           write buffers, write textures, submit command buffers
IGpuBuffer          size, usage
IGpuTexture         width, height, format, usage, create a view
IGpuTextureView     a render target or a bind-group entry
IGpuSurface         configure, acquire the next texture, present  (zero readback)
IGpuCommandEncoder  begin a render pass, finish into a command buffer
IGpuRenderPass      Phase 1: begin, clear, end
IGpuCommandBuffer   a finished, submittable list of work
```

Descriptors and enums (`BufferDescriptor`, `TextureDescriptor`, `RenderPassDescriptor`,
`GpuTextureFormat`, `GpuTextureUsage`, `GpuBufferUsage`, `GpuLimits`) are small value
types. Clear color uses a four-component vector in the zero-to-one range.

`IGpuSurface` is the zero-readback present seam carried from v1. The compositor blends
resident textures straight into the acquired frame texture and presents, with no copy
back to the processor. A surface is created by the native backend, not by the device,
because it needs platform window handles.

## C. What Phase 1 covers and what it defers

Phase 1 ships the resource lifetime, the upload paths, and the present path: create a
buffer and a texture, write to them, configure a surface, acquire a frame, run a render
pass that clears, finish, submit, and present. The Phase 1 test proves all of that with
a recording fake device, so the seam is proven implementable with no native code in the
loop.

Phase 2 adds the draw surface: shader modules, render pipelines, bind groups, vertex and
index buffers, and draw calls on `IGpuRenderPass`. That is where `Starling.Renderer.WebGpu`
lives, and where `Starling.Gpu.WgpuNative` first touches a real graphics processor.

## D. The backend rule

The application binary interface is the call contract a native library exposes. The
webgpu.h headers project describes a stable C contract meant for binding into higher
level languages. wgpu-native does not yet implement the stable version of that header,
so v2 takes wgpu-native first for speed and keeps the option to add Dawn later for a
cleaner contract.

The rule that makes this safe:

> Renderer code calls `Starling.Gpu`. Only `Starling.Gpu.WgpuNative` knows about raw
> handles. Adding Dawn means a second backend project, not a change to any renderer or
> compositor code.

---

## Acceptance Tests

1. Which project is allowed to hold a raw wgpu pointer, and what does every other GPU
   consumer depend on instead?
2. Why is the seam shaped like WebGPU?
3. What does the Phase 1 fake device prove?
4. What has to be added to the seam before a real rectangle reaches the screen?
5. How does adding Dawn later avoid touching renderer code?
