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
- `src/v2/Starling.Scene/Starling.Scene.csproj` (net11.0, C# preview)
- Geometry: `PxRect.cs`, `PxSize.cs`, `RgbaColor.cs`
- Scene tree: `SurfaceGraph.cs`, `SurfaceLayer.cs`, `LayerId.cs`, `LayerContent.cs`,
  `Handles.cs`, `RenderScene.cs`
- Path-first commands: `RenderCommandBuffer.cs`, `RenderCommand.cs`,
  `RenderCommandKind.cs`, `BlendMode.cs`
- Geometry and paint: `Path.cs`, `PathBuilder.cs`, `Brush.cs`, `StrokeStyle.cs`
- Resources: `RenderResourceTable.cs`, `ResourceIds.cs` (PathId, BrushId, ImageId,
  FontId, GlyphRunId), `ImageResource.cs`, `FontResource.cs`, `GlyphRun.cs`,
  `PositionedGlyph.cs`
- Hit testing: `HitRegion.cs`, `HitRegionSet.cs`
- Accessibility: `AccessibilityRole.cs`, `AccessibilityNode.cs`
- Provenance and actions: `ProvenanceTag.cs`, `ActionRef.cs`, `PermissionScope.cs`

## Acceptance
- `Starling.Scene` builds with no package reference beyond the shared analyzers.
- The command set is path-first (FillPath/StrokePath with brush handles), not a
  FillRect menu and not a copy of CSS paint primitives.
- A layer's content is a closed union (render scene, external texture, video, native
  guest); a layer carries a LayerId and a content hash.
- Commands are value types stored in one list, with transforms in a side table.
- The resource table dedups images by content hash and fonts by face key.
- A layer carries an accessibility tree and an optional typed action.
- `cd src/v2 && dotnet build Starling.v2.slnx` is green. (Pending: no SDK in the planning session.)

## Notes
- Namespaces are flat: every type is in `Starling.Scene`. Folder layout is by concern.
- `RenderCommand` carries a compact union of fields; variable-size payloads
  (transforms, glyph runs, images) live in side tables and are reached by index.
- Resource ids are typed by kind (`PathId`, `BrushId`, `ImageId`, `FontId`,
  `GlyphRunId`), so a command can never reference the wrong table.

## Handoff log
- 2026-06-07T00:00Z — created and landed in the v2 planning pass. All types written.
  Build and test pending: the planning environment had no .NET SDK and no package
  feed (the package host is blocked), so `dotnet build` and `dotnet test` could not run
  here.
- 2026-06-07T01:00Z — design finalization. Reconciled against the source design chat.
  Reworked to the path-first scene IR with brush handles (was a FillRect/FillRoundedRect
  menu), made layer content a closed union with LayerId and content hash (was a single
  RenderScene plus a kind enum), added the accessibility tree and a typed `ActionRef`
  (was a string action id). Retargeted to .NET 11 and C# preview. Still pending a build
  on a .NET 11 preview SDK.
- 2026-06-07T02:00Z — converted `LayerContent` to a C# 15 union type (unverified, by
  request). The syntax is preview and was not built here, so `LayerContent.cs` keeps the
  sealed-record fallback in a comment for a one-step revert.
- 2026-06-07T03:00Z — the net11 CI job built v2 for the first time. `Starling.Gpu`
  compiled clean. `Starling.Scene` had two real errors: a `Brush.Image` property vs
  factory name clash (CS0102), and the union syntax — preview.4 rejected the record-style
  cases (CS9370 "union must specify at least one case type", CS9374 no single-parameter
  constructors). Renamed the brush property to `ImageHandle` and reverted `LayerContent`
  to the sealed-record closed union. Union keyword adoption waits on the confirmed spec.
