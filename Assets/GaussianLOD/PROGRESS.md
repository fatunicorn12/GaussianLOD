# GaussianLOD — Progress

**Status:** ✅ Complete. All 12 phases done.

## Error Triage (post-readonly-fix compile pass)

All 22 errors are caller-side; runtime definitions are correct.

| Error | File (wrong side) | Fix |
|---|---|---|
| CS1612 ×2: modify return value of `LodLevels` | LODSelectionTests.cs | Copy `sel.LodLevels` into a local `var` first; NativeArray indexer mutates underlying memory through the copy |
| CS1654 ×7: modify `using` variable | LODSelectionTests.cs | Drop `using var levels = …`; use try/finally with manual `Dispose()` |
| CS1061 ×4: `AllIndices` missing | GaussianLODValidator.cs | Real property name is `allIndices` (lowercase) |
| CS7036/CS1501/CS1503/CS1061 ×5: wrong DecodePositions/Build signatures | SplatClusterBaker.cs | Rewrite Bake() to match real API: `Build(GaussianSplatAsset, bool)` decodes internally; re-decode positions separately for `MegaSplatGenerator` |
| CS1503 ×2: SetBakedData arg order | SplatClusterBaker.cs | Real order is `(clusters, indexAsset, int totalSplatCount, int maxDepth, Bounds sceneBounds)` |
| CS1061 ×2: `LastFrameRequestedSplats` / `SplatsPerFrameBudget` missing | LODBudgetProfiler.cs | Real names: `LastFrameSplatCost` / `MaxSplatsPerFrame` |


## Summary
- 4 asmdefs / package.json
- 3 Util classes
- 5 Clustering classes
- 3 Culling classes
- 3 LOD classes
- 2 Stereo classes
- 3 Rendering classes
- 1 boundary MonoBehaviour (`GaussianLODController`)
- 4 compute shaders
- 3 editor tools (Baker, Profiler overlay, Validator)
- 3 test files
- ARCHITECTURE.md, SETUP.md, TASKS.md, PROGRESS.md

## Architectural decisions logged
- **Render integration**: LOD-bucketed sub-asset (B1). B2/B4 unreachable — `GaussianSplatRenderSystem` and `m_GpuSortKeys` are `internal`. ARCHITECTURE.md §2b.
- **Position decode**: Option A — CPU-decode in C# reproducing Aras's HLSL (`SplatOctreeBuilder.DecodePositions`). ARCHITECTURE.md §2c.
- **Hot path**: CPU `LODSelector` in `LateUpdate`. GPU compute kernels remain as alternates because `AsyncGPUReadback` adds 1–3 frames latency, unacceptable at 90 Hz VR.
- **MegaSplat color**: `Color.white` placeholder — Aras uses real per-splat colors at draw time, so megasplat color is informational only.
- **Allocator**: `StereoFrustumMerger.GetMergedPlanes()` returns `Allocator.TempJob` (not `Temp`) so the caller can dispose safely.
- **Future v2**: `InternalsVisibleTo` from Aras's asmdef would unlock per-cluster indirect draws and remove the scene-wide LOD limitation. Out of scope.

## Known limitations
- Scene-wide LOD switching, not per-cluster. Documented prominently in SETUP.md §4.
- LOD1..LOD3 buckets initially point at the source asset. Real GPU savings require user-authored decimated assets.
