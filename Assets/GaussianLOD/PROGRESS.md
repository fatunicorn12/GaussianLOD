# GaussianLOD — Progress

**Current Step:** Per-cluster rendering rewrite — complete. The LOD-bucket-switching architecture (ARCHITECTURE.md §2b, historical) has been replaced with per-cluster draw-set construction via the fork's new extension API on `GaussianSplatRenderer` (ARCHITECTURE.md §8).

**Status:** ✅ Complete. All 15 phases done.

## Phase 15 — Per-Cluster Rendering (extension-API consumer)

**What changed**
- `SplatSort.compute` gained a second kernel, `CSBuildFilteredIndices`, that per-cluster atomically emits stride-scaled splat indices into the fork's `gpuSortKeys` buffer.
- `GpuSplatSorter` now subscribes to `GaussianSplatRenderer.BeforeSort` on `Init`. Each sort-frame it uploads visibility + LOD levels, clears a single-uint counter, dispatches the filter kernel, issues `cmd.RequestAsyncReadback` on the counter, sets `skipInternalSort = true`, and writes the previous frame's cached readback value to `activeSplatCount`. The legacy `Sort(...)` bitonic API is retained unchanged for `LODBudgetManager`.
- `SplatDrawCallAssembler` no longer toggles GameObjects. It disables the legacy LOD1/LOD2/LOD3 bucket GameObjects once on construction and, each frame, computes the CPU-side stride-scaled visible splat total and writes it to `activeSplatCount` as a fallback. When the sorter is active its BeforeSort writes overwrite this value just before `DrawProcedural`.
- `SplatRenderFeature` is unchanged structurally (still a `BeforeRenderingTransparents` marker pass) but its documentation now points at the event-driven sorter path for the real GPU work.
- `GaussianLODController` resolves the LOD0 `GaussianSplatRenderer` from either `lod0BucketGO` or its own hierarchy and passes it to both the sorter and the assembler alongside the `SplatClusterIndexAsset`.
- `LODBudgetProfiler` replaces its old `CurrentBucket` / `LastFrameSelectedLod` display with `LastFrameVisibleSplatCount` and `LastFrameVisibleClusterCount` — the new meaningful metrics under per-cluster rendering.

**Architectural decisions logged**
- **Kernel allocation**: `InterlockedAdd` on a single-uint counter. Output is cluster-grouped but not strictly back-to-front across clusters. For Gaussian LOD this is acceptable because LODBudgetManager already demotes furthest-first and within-cluster depth variance is small.
- **Readback delay**: `activeSplatCount` uses the previous frame's count; one-frame staleness is invisible at 90 Hz VR and the −1 fallback ("draw all") is safe on frame 0.
- **Fallback**: if the extension API is missing (null renderer or missing `SplatClusterIndexAsset`), the sorter logs a warning and remains unsubscribed; `SplatDrawCallAssembler` still sets `activeSplatCount` from CPU data, producing a nearest-N truncation via Aras's internal depth sort.
- **Legacy bucket GOs**: disabled but not removed. Users can revert to the §2b bucket-switching behavior by re-enabling LOD1/2/3 GOs and disabling the controller.
- **Ownership**: `GpuSplatSorter` owns `_AllSplatIndicesBuffer` and `_FilteredCountBuffer` (sized from the asset / 1 uint), not pool-managed. The pool's pre-existing `VisibleMaskBuffer` and `LodLevelBuffer` are reused for the per-frame uploads.

## Known limitations
- Per-cluster sort is cluster-grouped, not strictly depth-sorted across cluster boundaries. If alpha artifacts emerge at LOD transitions, a second-pass bitonic sort over the filtered range is possible (uses the retained `CSBitonicSort` kernel).
- `activeSplatCount` is one frame behind the actual cluster/LOD distribution. Acceptable at VR frame rates.
- `skipInternalSort` is set each frame by the sorter; if the sorter is ever disabled mid-play, the consumer is responsible for re-clearing it. `GpuSplatSorter.Dispose` does this.

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
