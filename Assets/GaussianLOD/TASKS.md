# GaussianLOD — Task List

All phases complete.

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
