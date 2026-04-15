# GaussianLOD — Task List

All 15 phases complete. Phase 15 consumes the fork's new `GaussianSplatRenderer` extension API for per-cluster draw-set construction (ARCHITECTURE.md §8).

## Phase 0 — Discovery
- [x] Explore Aras package
- [x] Write `ARCHITECTURE.md`
- [x] Write `TASKS.md`
- [x] Write `PROGRESS.md`
- [x] Read Aras HLSL position decode logic
- [x] Render Integration Decision (LOD-bucketed sub-assets)
- [x] Read `GaussianSplatURPFeature.cs` — confirmed internal

## Phase 1 — Project skeleton
- [x] `GaussianLOD.Runtime.asmdef`
- [x] `GaussianLOD.Editor.asmdef`
- [x] `GaussianLOD.Tests.asmdef`
- [x] `package.json`

## Phase 2 — Util
- [x] `Util/PlatformCapabilityChecker.cs`
- [x] `Util/ComputeShaderCache.cs`
- [x] `Util/NativeBufferPool.cs`

## Phase 3 — Clustering
- [x] `Clustering/SplatClusterData.cs`
- [x] `Clustering/SplatClusterIndexAsset.cs`
- [x] `Clustering/SplatClusterAsset.cs`
- [x] `Clustering/MegaSplatGenerator.cs`
- [x] `Clustering/SplatOctreeBuilder.cs`

## Phase 4 — Culling
- [x] `Culling/CullingResultBuffer.cs`
- [x] `Culling/FrustumCuller.cs`
- [x] `Culling/ScreenCoverageEstimator.cs`

## Phase 5 — LOD
- [x] `LOD/LODSelector.cs`
- [x] `LOD/LODBudgetManager.cs`
- [x] `LOD/LODTransitionController.cs`

## Phase 6 — Stereo
- [x] `Stereo/StereoCameraRig.cs`
- [x] `Stereo/StereoFrustumMerger.cs`

## Phase 7 — Rendering
- [x] `Rendering/GpuSplatSorter.cs`
- [x] `Rendering/SplatDrawCallAssembler.cs`
- [x] `Rendering/SplatRenderFeature.cs`

## Phase 8 — Boundary
- [x] `Runtime/GaussianLODController.cs`

## Phase 9 — Compute shaders
- [x] `Shaders/Resources/GaussianLOD/SplatCull.compute`
- [x] `Shaders/Resources/GaussianLOD/SplatSort.compute`
- [x] `Shaders/Resources/GaussianLOD/SplatLODSelect.compute`
- [x] `Shaders/Resources/GaussianLOD/MegaSplatBlit.compute`

## Phase 10 — Editor tools
- [x] `Editor/SplatClusterBaker.cs`
- [x] `Editor/LODBudgetProfiler.cs`
- [x] `Editor/GaussianLODValidator.cs`

## Phase 11 — Tests
- [x] `Tests/ClusteringTests.cs`
- [x] `Tests/CullingTests.cs`
- [x] `Tests/LODSelectionTests.cs`

## Phase 12 — Docs
- [x] `SETUP.md`
- [x] Final TASKS.md / PROGRESS.md sweep
- [x] Final folder tree output

## Phase 13 — Multi-Asset Spatial Zone Support
- [x] `Runtime/Zones/ZoneBudgetSplitter.cs`
- [x] `Runtime/Zones/SpatialZoneManager.cs`
- [x] Modify `Runtime/Stereo/StereoCameraRig.cs` — add static singleton
- [x] Modify `Runtime/GaussianLODController.cs` — use shared StereoCameraRig.Instance
- [x] Modify `Editor/LODBudgetProfiler.cs` — multi-zone overlay view
- [x] `Editor/ZoneSetupValidator.cs`
- [x] `Tests/ZoneTests.cs`
- [x] Update ARCHITECTURE.md with zone system design

## Phase 14 — Python PLY Spatial Splitter
- [x] `Tools/split_ply.py`
- [x] `Tools/requirements.txt`

## Phase 15 — Per-Cluster Rendering (fork extension API)
Drive Aras's draw path directly instead of toggling LOD-bucket GameObjects. See ARCHITECTURE.md §8.
- [x] Update ARCHITECTURE.md §8 (Per-Cluster Rendering)
- [x] Update TASKS.md (this list) with phase 15 work
- [x] Update PROGRESS.md — Current Step = Per-cluster rendering rewrite
- [x] Add `CSBuildFilteredIndices` kernel to `Shaders/Resources/GaussianLOD/SplatSort.compute`
- [x] Rewrite `Runtime/Rendering/GpuSplatSorter.cs` (BeforeSort subscription, filter dispatch, readback, skipInternalSort, activeSplatCount)
- [x] Rewrite `Runtime/Rendering/SplatDrawCallAssembler.cs` (drop bucket toggles; thin activeSplatCount wrapper; disable LOD1/2/3 GOs on init)
- [x] Update `Runtime/Rendering/SplatRenderFeature.cs` (marker-only; event-driven work now lives on sorter)
- [x] Update `Runtime/GaussianLODController.cs` (find LOD0 renderer, pass to sorter+assembler, disable unused bucket GOs)
- [x] Update `Editor/LODBudgetProfiler.cs` — swap removed `CurrentBucket`/`LastFrameSelectedLod` for `LastFrameVisibleSplatCount`
- [x] Add `kKernelBuildFilteredIndices` constant to `Runtime/Util/ComputeShaderCache.cs`
- [x] Final TASKS.md / PROGRESS.md sweep
