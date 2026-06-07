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
Starling.Gpu                    facade classes + one backend interface, pure managed
Starling.Gpu.WgpuNative         the only project with raw wgpu / Silk pointers
Starling.Gpu.Dawn               a later second provider, same seam
```

## B. The shape: facade classes plus one backend

The public surface is concrete classes shaped like WebGPU, following the init chain
instance to adapter to device to queue. They are a thin facade: each holds an opaque
backend token and forwards calls. The single swap point is `IGpuBackend`.

```
GpuInstance         RequestAdapter, CreateSurface          (GpuInstance.Create over a backend)
GpuAdapter          RequestDevice, Limits
GpuDevice           Queue, CreateBuffer, CreateTexture, CreateCommandEncoder
GpuQueue            WriteBuffer, WriteTexture, Submit
GpuBuffer           size, usage
GpuTexture          width, height, format, usage, CreateView
GpuTextureView      a render target or a bind-group entry
GpuSurface          Configure, AcquireNextTexture, Present  (zero readback)
GpuCommandEncoder   BeginRenderPass, Finish
GpuRenderPass       Phase 1: End
GpuCommandBuffer    a finished, submittable list of work

IGpuBackend         the one interface a backend implements
```

Only `Starling.Gpu.WgpuNative` implements `IGpuBackend` and maps the opaque tokens to
real wgpu handles. Renderer code uses `GpuDevice` and friends and never sees a handle.
Swapping in Dawn later means a second `IGpuBackend`, not a change to any caller. The
abstraction stays close to WebGPU on purpose, not a giant engine abstraction, so it
maps onto wgpu, Dawn, and the web platform cleanly.

`IGpuSurface` is the zero-readback present seam carried from v1. The compositor blends
resident textures straight into the acquired frame texture and presents, with no copy
back to the processor. A surface is created from an instance, since it needs platform
window handles, not from the device.

## C. What Phase 1 covers and what it defers

Phase 1 ships the init chain, the resource lifetime, the upload paths, and the present
path: create an instance, pick an adapter, request a device, create a buffer and a
texture, write to them, configure a surface, acquire a frame, run a render pass that
clears, finish, submit, and present. The Phase 1 test proves all of that with a
recording fake backend, so the seam is proven implementable with no native code in the
loop.

Phase 2 adds the draw surface to `GpuDevice` and `GpuRenderPass`: shader modules, render
pipelines, bind groups, samplers, vertex and index buffers, and draw calls. That is
where `Starling.Renderer.WebGpu` lives, and where `Starling.Gpu.WgpuNative` first
touches a real graphics processor.

## D. The backend rule

The application binary interface is the call contract a native library exposes. The
webgpu.h headers project describes a stable C contract meant for binding into higher
level languages. wgpu-native does not yet implement the stable version of that header,
so v2 takes wgpu-native first for speed and keeps the option to add Dawn later for a
cleaner contract.

The rule that makes this safe:

> Renderer code uses the `Starling.Gpu` facade. Only `Starling.Gpu.WgpuNative`
> implements `IGpuBackend` and knows about raw handles. Adding Dawn means a second
> backend project, not a change to any renderer or compositor code.

---

## Acceptance Tests

1. Which project is allowed to hold a raw wgpu pointer, and what does every other GPU
   consumer use instead?
2. What is the one interface a backend implements, and how do the public classes reach
   it?
3. What does the Phase 1 fake backend prove?
4. What has to be added to the seam before a real rectangle reaches the screen?
5. How does adding Dawn later avoid touching renderer code?
