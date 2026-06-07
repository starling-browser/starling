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
- `src/v2/Starling.Gpu/Starling.Gpu.csproj`
- Interfaces: `IGpuDevice.cs`, `IGpuQueue.cs`, `IGpuBuffer.cs`, `IGpuTexture.cs`,
  `IGpuTextureView.cs`, `IGpuSurface.cs`, `IGpuCommandEncoder.cs`, `IGpuRenderPass.cs`,
  `IGpuCommandBuffer.cs`
- Descriptors and enums: `BufferDescriptor.cs`, `TextureDescriptor.cs`,
  `RenderPassDescriptor.cs`, `TextureWrite.cs`, `GpuLimits.cs`, `GpuTextureFormat.cs`,
  `GpuTextureUsage.cs`, `GpuBufferUsage.cs`

## Acceptance
- `Starling.Gpu` builds with no package reference beyond the shared analyzers.
- `Starling.Gpu` does not depend on `Starling.Scene`.
- The seam covers resource lifetime, buffer and texture upload, and a zero-readback
  surface present path: configure, acquire, render pass with clear, finish, submit,
  present.
- The seam is implementable with a fake backend and no native code (proven in
  wp:V2P1-03).
- `dotnet build Starling.v2.slnx` is green. (Pending: no SDK in the planning session.)

## Notes
- `IGpuRenderPass` is Phase 1 minimal: begin, clear, end. Pipelines, bind groups,
  vertex buffers, and draw calls land in Phase 2 with `Starling.Renderer.WebGpu`.
- `IGpuSurface` is created by the native backend, not the device, because it needs
  platform window handles.

## Handoff log
- 2026-06-07T00:00Z — created and landed in the v2 planning pass. Build and test pending
  for the same reason as wp:V2P1-01 (no .NET 10 SDK, package host blocked). Verify on a
  machine with the SDK, then promote to complete.
