# GaussianLOD — Progress

**Status:** ✅ Complete. All 14 phases done.

## Error Triage (post-readonly-fix compile pass)

All 22 compile errors identified during the initial pass have been **fixed and verified**. The four affected files (`LODSelectionTests.cs`, `GaussianLODValidator.cs`, `SplatClusterBaker.cs`, `LODBudgetProfiler.cs`) now match their runtime APIs and compile cleanly.

## Summary
- 4 asmdefs / package.json
- 3 Util classes
- 5 Clustering classes
- 3 Culling classes
- 3 LOD classes
- 2 Stereo classes (StereoCameraRig now with static singleton)
- 3 Rendering classes
- 1 boundary MonoBehaviour (`GaussianLODController`)
- 2 Zone classes (`SpatialZoneManager`, `ZoneBudgetSplitter`)
- 4 compute shaders
- 4 editor tools (Baker, Profiler overlay, Validator, ZoneSetupValidator)
- 4 test files (Clustering, Culling, LODSelection, Zones)
- 1 Python tool (`Tools/split_ply.py` + `requirements.txt`)
- ARCHITECTURE.md, SETUP.md, TASKS.md, PROGRESS.md

## Architectural decisions logged
- **Render integration**: LOD-bucketed sub-asset (B1). B2/B4 unreachable — `GaussianSplatRenderSystem` and `m_GpuSortKeys` are `internal`. ARCHITECTURE.md §2b.
- **Position decode**: Option A — CPU-decode in C# reproducing Aras's HLSL (`SplatOctreeBuilder.DecodePositions`). ARCHITECTURE.md §2c.
- **Hot path**: CPU `LODSelector` in `LateUpdate`. GPU compute kernels remain as alternates because `AsyncGPUReadback` adds 1–3 frames latency, unacceptable at 90 Hz VR.
- **MegaSplat color**: `Color.white` placeholder — Aras uses real per-splat colors at draw time, so megasplat color is informational only.
- **Allocator**: `StereoFrustumMerger.GetMergedPlanes()` returns `Allocator.TempJob` (not `Temp`) so the caller can dispose safely.
- **Future v2**: `InternalsVisibleTo` from Aras's asmdef would unlock per-cluster indirect draws and remove the scene-wide LOD limitation. Out of scope.
- **Zone budget splitting**: `ZoneBudgetSplitter` is pure math — receives total budget + per-zone coverage, outputs per-zone allocations. Min 5,000 splats per active zone.
- **StereoCameraRig singleton**: Ref-counted via `GetOrCreate`/`Release`. Multi-zone scenes share one XR camera query per frame. Single-controller case unchanged.
- **SpatialZoneManager**: Pure orchestrator — no LOD logic. Collects coverage, distributes budget, lets each zone's controller run independently.
- **PLY splitting**: Offline Python tool (`Tools/split_ply.py`). numpy-only. Supports overlap regions, min-splat merging, binary + ASCII PLY.

## Known limitations
- Scene-wide LOD switching per zone, not per-cluster. Documented prominently in SETUP.md §4.
- LOD1..LOD3 buckets initially point at the source asset. Real GPU savings require user-authored decimated assets.
- `SpatialZoneManager.LateUpdate` runs in same phase as each zone's controller — execution order between them is undefined by Unity. Budget override is applied after the initial selector pass. In practice this means the first frame after a budget change may be slightly off.
