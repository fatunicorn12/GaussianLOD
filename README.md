# GaussianLOD

A modular Unity package that adds clustered LOD (Level of Detail) to 
Gaussian Splat scenes in VR. Built on top of 
[Aras Pranckevičius's gaussian-splatting package](https://github.com/aras-p/UnityGaussianSplatting).

## What It Does

Gaussian splat scenes are expensive because every frame Unity must sort 
every splat by distance before drawing. A 5 million splat scene means 
sorting 5 million things at 90fps — which destroys VR performance.

GaussianLOD divides your splat cloud into spatial clusters. Clusters 
close to the camera get full detail. Clusters far away get fewer splats. 
Clusters barely visible get replaced by a single representative splat. 
Clusters outside your field of view are skipped entirely.

The result: instead of sorting millions of splats every frame, you sort 
only the ones that matter.

## Status

**Alpha — bake pipeline and runtime verified, VR headset testing in progress.**

| System | Status |
|---|---|
| Octree bake pipeline | ✅ Verified |
| Cluster validation | ✅ Verified |
| Runtime LOD selection | ✅ Verified |
| Budget overlay | ✅ Verified |
| PC testing (Editor) | ✅ Verified |
| ALVR / SteamVR | 🔄 In progress |
| Meta Quest 3S | 🔄 Planned |
| Apple Vision Pro (streamed) | 🔄 Planned |

## Known Limitations

- **LOD switching is scene-wide per asset, not per cluster.** Aras's 
  renderer draws an entire asset as one draw call and does not expose 
  a per-cluster index injection API. The LOD system picks the most 
  conservative LOD required across all visible clusters and switches 
  the whole asset. Per-cluster granularity is planned for v2 via 
  `InternalsVisibleTo` — see `ARCHITECTURE.md`.

- **Real GPU savings require decimated assets.** The bake step creates 
  four LOD bucket renderers but wires them all to the same source asset 
  by default. To actually reduce GPU work, replace LOD1/LOD2/LOD3 
  buckets with decimated `GaussianSplatAsset`s you author yourself. 
  See SETUP.md §4.

## Requirements

- Unity 6 LTS (6000.0+)
- Universal Render Pipeline (URP)
- [Aras's gaussian-splatting package](https://github.com/aras-p/UnityGaussianSplatting) 
  installed and working in your project

## Target Platforms

- Apple Vision Pro (streamed via Mac)
- Meta Quest 3S
- PC via ALVR + SteamVR (primary development path)

## Installation

1. Install Aras's package first via Package Manager → 
   Add from git URL:
https://github.com/aras-p/UnityGaussianSplatting.git

2. Clone or download this repo and copy the `Assets/GaussianLOD` 
   folder into your Unity project's `Assets/` folder.

3. Unity will import and compile the package automatically.

## Quick Start

Full instructions are in [SETUP.md](Assets/GaussianLOD/SETUP.md). 
The short version:

1. **Bake** — `GaussianLOD → Cluster Baker`, drop in your 
   `GaussianSplatAsset`, click Bake
2. **Validate** — `GaussianLOD → Validate Selected Cluster Asset`
3. **Add renderer feature** — add `SplatRenderFeature` to your URP 
   renderer asset, ordered ABOVE `GaussianSplatURPFeature`
4. **Wire the scene** — add `GaussianLODController` to a GameObject, 
   assign your cluster asset and LOD bucket GameObjects

## Architecture
GaussianLOD/
├── Runtime/
│   ├── GaussianLODController.cs    ← Only MonoBehaviour. Wires all systems.
│   ├── Clustering/                 ← Octree data structures
│   ├── Culling/                    ← Frustum cull + coverage estimate
│   ├── LOD/                        ← Budget enforcement + LOD selection
│   ├── Rendering/                  ← URP feature + sort + draw assembly
│   ├── Stereo/                     ← Stereo camera rig + frustum merge
│   └── Util/                       ← Platform detection, buffer pool, cache
├── Shaders/                        ← Compute shaders (cull, sort, LOD select)
├── Editor/                         ← Baker, validator, budget profiler overlay
└── Tests/                          ← EditMode unit tests

Each component has a single responsibility. No god classes. 
Platform branching lives exclusively in `PlatformCapabilityChecker.cs`.

## Per-Platform Splat Budgets

| Platform | Default Budget |
|---|---|
| PC / Editor | 250,000 splats |
| PC via ALVR | 200,000 splats |
| Apple Vision Pro (streamed) | 120,000 splats |
| Meta Quest 3S | 80,000 splats |

Override via `GaussianLODController.splatsPerFrameOverride`.

## Contributing

Issues and PRs welcome. See `ARCHITECTURE.md` for the full system 
design, API surface, and documented future v2 paths.

## License

MIT
