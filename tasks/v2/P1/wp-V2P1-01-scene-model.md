---
id: "wp:V2P1-01-scene-model"
parent: ""
milestone: "V2-P1"
status: "in_review"
claimed_by: "agent-claude-v2planning"
claimed_at: "2026-06-07T00:00:00Z"
branch: "claude/starling-v2-architecture-xnwsP"
depends_on: []
blocks:
  - "wp:V2P1-03-trivial-scene"
subsystem: "Starling.Scene"
plan_refs:
  - "browser-plan/v2/02_SCENE_MODEL.md"
  - "browser-plan/v2/01_ARCHITECTURE.md"
---

# wp:V2P1-01 — v2 scene model core

## Goal
Stand up `Starling.Scene`, the non-replaceable core of v2. It owns the surface graph,
the render scene, the resource table, hit regions, the text-run handles, and provenance.
It is pure managed and depends on nothing but the base class library. No GPU types, no
native types, no ImageSharp. This replaces v1's `DisplayList`-as-contract center.

## Inputs
- The v2 architecture and scene-model docs in `browser-plan/v2/`.
- v1 inventory: `CompositorLayer`, `CompositedFrameRequest`, `DisplayItem` shaped the
  design but no v1 code is imported.

## Outputs
- `src/v2/Starling.Scene/Starling.Scene.csproj`
- Geometry: `PxRect.cs`, `PxSize.cs`, `RgbaColor.cs`
- Scene tree: `SurfaceGraph.cs`, `SurfaceLayer.cs`, `SurfaceLayerKind.cs`,
  `RenderScene.cs`
- Commands: `RenderCommandBuffer.cs`, `RenderCommand.cs`, `RenderCommandKind.cs`
- Resources: `RenderResourceTable.cs`, `ResourceId.cs`, `ImageResource.cs`,
  `FontResource.cs`, `GlyphRun.cs`, `PositionedGlyph.cs`
- Hit testing: `HitRegion.cs`, `HitRegionSet.cs`
- Provenance: `ProvenanceTag.cs`, `PermissionScope.cs`

## Acceptance
- `Starling.Scene` builds with no package reference beyond the shared analyzers.
- The command set is the small floor in the plan, not a copy of CSS paint primitives.
- Commands are value types stored in one list, with transforms in a side table.
- The resource table dedups images by content hash and fonts by face key.
- `dotnet build Starling.v2.slnx` is green. (Pending: no SDK in the planning session.)

## Notes
- Namespaces are flat: every type is in `Starling.Scene`. Folder layout is by concern.
- `RenderCommand` carries a compact union of fields; variable-size payloads
  (transforms, glyph runs, images) live in side tables and are reached by index.
- `ResourceId` is scoped by kind. The command kind tells the consumer which table to
  read.

## Handoff log
- 2026-06-07T00:00Z — created and landed in the v2 planning pass. All types written.
  Build and test pending: the planning environment had no .NET 10 SDK and no package
  feed (the package host is blocked), so `dotnet build` and `dotnet test` could not run
  here. Verify on a machine with the SDK, then promote to complete.
