---
id: "wp:V2P1-02-gpu-abstraction"
parent: ""
milestone: "V2-P1"
status: "in_review"
claimed_by: "agent-claude-v2planning"
claimed_at: "2026-06-07T00:00:00Z"
branch: "claude/starling-v2-architecture-xnwsP"
depends_on: []
blocks:
  - "wp:V2P1-03-trivial-scene"
subsystem: "Starling.Gpu"
plan_refs:
  - "browser-plan/v2/03_GPU_ABSTRACTION.md"
  - "browser-plan/v2/01_ARCHITECTURE.md"
---

# wp:V2P1-02 — v2 GPU seam

## Goal
Stand up `Starling.Gpu`, the small WebGPU-shaped seam every renderer and compositor in
v2 depends on. Interfaces and value types only, pure managed. This is the rule that
keeps native graphics in one backend project: renderer code calls this seam and never
touches a raw wgpu or Silk.NET pointer.

## Inputs
- The GPU abstraction doc in `browser-plan/v2/`.
- v1 inventory: `GpuBlendEngine`, `GpuSurfacePresenter` show the present and resource
  model the seam must support. No v1 code is imported.

## Outputs
- `src/v2/Starling.Gpu/Starling.Gpu.csproj` (net11.0, C# preview)
- Backend seam: `IGpuBackend.cs` (the one interface a backend implements)
- Facade classes: `GpuInstance.cs`, `GpuAdapter.cs`, `GpuDevice.cs`, `GpuQueue.cs`,
  `GpuBuffer.cs`, `GpuTexture.cs`, `GpuTextureView.cs`, `GpuSurface.cs`,
  `GpuCommandEncoder.cs`, `GpuRenderPass.cs`, `GpuCommandBuffer.cs`
- Descriptors and enums: `GpuAdapterOptions.cs`, `GpuPowerPreference.cs`,
  `GpuDeviceDescriptor.cs`, `NativeSurfaceDescriptor.cs`, `BufferDescriptor.cs`,
  `TextureDescriptor.cs`, `RenderPassDescriptor.cs`, `TextureWrite.cs`, `GpuLimits.cs`,
  `GpuTextureFormat.cs`, `GpuTextureUsage.cs`, `GpuBufferUsage.cs`

## Acceptance
- `Starling.Gpu` builds with no package reference beyond the shared analyzers.
- `Starling.Gpu` does not depend on `Starling.Scene`.
- The public surface is concrete facade classes following the WebGPU init chain
  (instance, adapter, device, queue); the single swap point is `IGpuBackend`.
- The seam covers resource lifetime, buffer and texture upload, and a zero-readback
  surface present path: configure, acquire, render pass with clear, finish, submit,
  present.
- The seam is implementable with a fake backend and no native code (proven in
  wp:V2P1-03).
- `cd src/v2 && dotnet build Starling.v2.slnx` is green. (Pending: no SDK in the planning session.)

## Notes
- `GpuRenderPass` is Phase 1 minimal: begin, clear, end. Pipelines, bind groups,
  shader modules, samplers, vertex buffers, and draw calls land in Phase 2 on
  `GpuDevice` and `GpuRenderPass`, with `Starling.Renderer.WebGpu`.
- `GpuSurface` is created from the instance, not the device, because it needs platform
  window handles.
- The facade classes hold opaque backend tokens and forward to `IGpuBackend`. No raw
  graphics handle ever escapes a backend project.

## Handoff log
- 2026-06-07T00:00Z — created and landed in the v2 planning pass. Build and test pending
  for the same reason as wp:V2P1-01 (no .NET SDK, package host blocked).
- 2026-06-07T01:00Z — design finalization. Reshaped from interface-per-type
  (`IGpuDevice`, `IGpuBuffer`, ...) to concrete facade classes plus a single
  `IGpuBackend`, following the WebGPU instance-to-adapter-to-device chain. Retargeted to
  .NET 11 and C# preview. Pending a build on a .NET 11 preview SDK.
