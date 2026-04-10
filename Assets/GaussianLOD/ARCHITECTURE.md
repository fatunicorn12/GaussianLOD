# GaussianLOD — Architecture & Ground Truth

This document is the source of truth for the GaussianLOD package. Re-read it before writing any new file. Never call an Aras API not listed here under "Confirmed".

---

## 1. Confirmed Aras Public API (read from source)

**Package**: `org.nesnausk.gaussian-splatting` v1.1.1, embedded at project root and mirrored under `Assets/Runtime/`, `Assets/Editor/`.
**Assembly name**: `GaussianSplatting` (asmdef at `Assets/Runtime/GaussianSplatting.asmdef`)
**Root namespace**: `GaussianSplatting.Runtime`
**autoReferenced**: true (so technically we don't *need* to reference it, but we will explicitly for clarity)

### `GaussianSplatting.Runtime.GaussianSplatAsset : ScriptableObject`
Confirmed public surface (from `Assets/Runtime/GaussianSplatAsset.cs`):

Constants:
- `const int kCurrentVersion = 2023_10_20`
- `const int kChunkSize = 256`
- `const int kTextureWidth = 2048`
- `const int kMaxSplats = 8_600_000`

Properties (all get-only):
- `int formatVersion`
- `int splatCount`
- `Vector3 boundsMin`
- `Vector3 boundsMax`
- `Hash128 dataHash`
- `VectorFormat posFormat`
- `VectorFormat scaleFormat`
- `SHFormat shFormat`
- `ColorFormat colorFormat`
- `TextAsset posData`
- `TextAsset colorData`
- `TextAsset otherData`
- `TextAsset shData`
- `TextAsset chunkData`  *(may be null if data is fully lossless)*
- `CameraInfo[] cameras`

Nested types:
- `enum VectorFormat { Float32, Norm16, Norm11, Norm6 }`
- `enum ColorFormat { Float32x4, Float16x4, Norm8x4, BC7 }`
- `enum SHFormat { Float32, Float16, Norm11, Norm6, Cluster64k, Cluster32k, Cluster16k, Cluster8k, Cluster4k }`
- `struct ChunkInfo { uint colR,colG,colB,colA; float2 posX,posY,posZ; uint sclX,sclY,sclZ; uint shR,shG,shB; }` — 256 splats per chunk, holds min/max for dequant
- `struct CameraInfo { Vector3 pos, axisX, axisY, axisZ; float fov; }`
- SH table item structs (not relevant to LOD)

Static helpers:
- `int GetVectorSize(VectorFormat)`
- `int GetColorSize(ColorFormat)`
- `int GetOtherSizeNoSHIndex(VectorFormat scaleFormat)` → `4 + GetVectorSize(scaleFormat)`
- `int GetSHCount(SHFormat, int splatCount)`
- `(int,int) CalcTextureSize(int splatCount)`
- `GraphicsFormat ColorFormatToGraphics(ColorFormat)`
- `long CalcPosDataSize(int, VectorFormat)`
- `long CalcOtherDataSize(int, VectorFormat)`
- `long CalcColorDataSize(int, ColorFormat)`
- `long CalcSHDataSize(int, SHFormat)`
- `long CalcChunkDataSize(int)`

Mutators (Editor-only use; we will NOT call):
- `Initialize(...)`, `SetDataHash(...)`, `SetAssetFiles(...)`

### `GaussianSplatting.Runtime.GaussianSplatRenderer : MonoBehaviour`
Confirmed public surface (from `Assets/Runtime/GaussianSplatRenderer.cs`):

- `GaussianSplatAsset m_Asset` (public field, serialized)
- `GaussianSplatAsset asset` (get-only property → `m_Asset`)
- `int splatCount` (get-only property; can diverge from `asset.splatCount` after edits)
- `enum RenderMode { ... }` (display modes)
- Tunables (public fields): `m_RenderOrder`, `m_SplatScale`, `m_OpacityScale`, `m_SHOrder`, `m_SHOnly`, `m_SortNthFrame`
- `bool HasValidAsset`
- `bool HasValidRenderSetup`
- `void EnsureMaterials()`
- `void EnsureSorterAndRegister()`
- `void OnEnable() / OnDisable() / Update()`
- `void ActivateCamera(int)`
- A large editing API (`EditStoreSelectionMouseDown`, `EditTranslateSelection`, `EditDeleteSelected`, `EditExportData`, `EditSetSplatCount`, `EditCopySplats*`, etc.) — NOT used by LOD.
- `Props` static class with `Shader.PropertyToID` constants — useful if we ever need to bind to Aras's material setup, but we will not directly.
- `GaussianSplatRenderSystem` (separate class) — global registry singleton; renderers self-register on enable.

GPU buffers (`m_GpuPosData`, `m_GpuOtherData`, `m_GpuSHData`, `m_GpuColorData`, `m_GpuChunks`) are **private** (or `internal`). We cannot read them from outside the assembly.

---

## 2. CRITICAL API DIVERGENCE — needs your decision

The original task brief assumed:
> "Per-splat fields expected: position, rotation, scale, color, opacity"
> "Some form of per-splat data access (NativeArray or GetData method)"

**This does not exist in Aras's public API.** The truth:

- Per-splat data lives in `TextAsset posData` (and `otherData`, `colorData`, `shData`) as compressed bytes.
- Format is determined by `posFormat` (`Float32` = 12B, `Norm16` = 6B, `Norm11` = 4B, `Norm6` = 2B).
- For all formats *except* `Float32`, positions are stored relative to per-chunk min/max bounds in `chunkData` (256 splats per chunk). Decoding requires reading the matching `ChunkInfo` and applying the dequantization that the HLSL shader does.
- If `chunkData == null`, the asset is fully lossless (positions are absolute `Float32`).

### Implications for `SplatOctreeBuilder`
The octree needs per-splat world positions. We have three viable approaches:

**Option A — CPU decode in C# (chosen as default).**
Read `posData.GetData<byte>()` + `chunkData.GetData<ChunkInfo>()` and reproduce Aras's HLSL dequant in C#/Burst. Pros: no GPU readback; deterministic; works headless. Cons: we re-implement format logic — if Aras changes formats we break. Mitigated by reading their HLSL source once and version-pinning against `kCurrentVersion = 2023_10_20`.

**Option B — GPU decode via compute shader + AsyncGPUReadback.**
Stage a compute shader (Editor-only) that decodes positions into a flat `RWStructuredBuffer<float3>` and read back. Pros: reuses Aras's exact decode logic if we copy their HLSL include. Cons: still needs the shader code; readback for 5M splats is fine but slower iteration.

**Option C — Chunk-level octree only.**
Use the 256-splat `ChunkInfo` bounds as the smallest leaf. We get 5M / 256 ≈ 19,500 chunks, sub-divided spatially. Pros: zero decoding, fastest bake. Cons: leaf size is fixed at 256 splats, not 1024, and we lose per-splat granularity for mega-splat centroids (we'd average chunk centers, not real splats).

**Default decision (recorded here, will be revisited):** Option A, with a fallback to Option C if `posFormat == Float32` is not the case AND CPU decode hits accuracy issues. The HLSL decode reference must be copied verbatim into C# from `Assets/Shaders/GaussianSplatting.hlsl` (or wherever Aras puts it) before `SplatOctreeBuilder` is written. **This file has not been read yet — see "Unconfirmed" below.**

### Implications for `MegaSplatGenerator`
Centroid + average color require decoded per-splat data. Same options as above. If we go Option C, mega-splat color comes from chunk `colR/colG/colB/colA` mins/maxes, averaged — acceptable but coarser.

### Implications for the runtime renderer
We do NOT replace Aras's `GaussianSplatRenderer`. The LOD system is a *culling and dispatch director* that:
1. Decides which clusters (= contiguous index ranges into Aras's existing GPU buffers) are visible.
2. ?? — and here is the second open question: **How do we tell Aras's renderer to draw only a subset of its splats?** The public API exposes `m_SortNthFrame`, `m_SplatScale`, `EditSetSplatCount`, but not "draw splats [a..b)". Approaches:
   - **B1.** Call `EditSetSplatCount` per frame to shrink the draw count. This *destructively* edits the renderer's splat count and is wrong.
   - **B2.** Sort the splat order buffer such that visible splats are contiguous at the front, then have Aras draw only the first N. Aras's `m_SortNthFrame` and the existing sort path may already produce a depth-sorted order buffer — we may be able to inject a custom order buffer. Needs source dive into the sort path (`GaussianSplatRenderSystem.GatherSplatsForCamera`).
   - **B3.** Maintain our OWN parallel `GaussianSplatRenderer` instances per LOD bucket — at bake time we generate N copies of the asset, one per LOD level — and toggle them. Heavy on memory.
   - **B4.** Render entirely independently using our own draw path that binds Aras's GPU buffers directly. But those buffers are private fields. We'd need reflection or to fork the package — both forbidden by the spec ("Do not modify Aras's package source").

**No option here is obviously correct without reading more of `GaussianSplatRenderer.cs` and `GaussianSplatRenderSystem`. This is the second blocking question.**

---

## 2b. Render Integration Decision (FINAL)

After fully reading `GaussianSplatRenderer.cs`, `GaussianSplatRenderSystem`, and `GaussianSplatURPFeature.cs`:

- `class GaussianSplatRenderSystem` is **`internal`** (line 17 of `GaussianSplatRenderer.cs`, no `public` modifier).
- `m_GpuSortKeys`, `m_GpuView`, `m_GpuChunks`, `m_MatComposite`, `SortPoints`, `CalcViewData`, `SortAndRenderSplats` are all `internal`.
- The actual draw call (line 165) is hardcoded `instanceCount = gs.splatCount` — Aras always draws every splat from every active+enabled renderer. The order buffer only depth-sorts; it never reduces draw count.
- `GaussianSplatURPFeature` is also `internal`. It is the *only* URP entry point that draws splats. It calls `GaussianSplatRenderSystem.instance.SortAndRenderSplats(...)` directly — an internal cross-call that is impossible from outside the assembly.

**Conclusion: B2 (inject custom order buffer into Aras's draw path) is unreachable from outside the `GaussianSplatting` assembly.** B4 (independent draw path) is also unreachable because the GPU buffers we'd need to bind (`m_GpuPosData`, `m_GpuOtherData`, `m_GpuSHData`, `m_GpuColorData`, `m_GpuChunks`) are private. The only public hooks are:

| Lever | Type | Per-frame safe? |
|---|---|---|
| Enable / disable `GaussianSplatRenderer` GameObject | Toggle | ✅ |
| `m_SortNthFrame` | int field | ✅ |
| `m_SplatScale`, `m_OpacityScale`, `m_RenderOrder`, `m_RenderMode` | field | ✅ |
| `EditSetSplatCount` | method | ❌ (reallocs) |
| `EditCopySplats*` | method | ❌ (Editor) |
| `GaussianSplatAsset.Initialize` + `SetAssetFiles` | constructor pair | ❌ (Editor) |

### Decision: "LOD-bucketed sub-asset" architecture

**Bake time** (`SplatClusterBaker`):
1. `SplatOctreeBuilder` reads the source `GaussianSplatAsset`, CPU-decodes positions (Section 2c below), builds the octree, and writes `SplatClusterAsset` + `SplatClusterIndexAsset`.
2. `MegaSplatGenerator` computes per-cluster representative splats.
3. The baker emits **4 child `GaussianSplatAsset`s** alongside the source asset:
   - `_LOD0` — full asset (just an asset reference, no copy)
   - `_LOD1` — every-other-splat copy (built via `EditCopySplats` into a fresh `GaussianSplatRenderer` and exported)
   - `_LOD2` — every-fourth-splat copy
   - `_LOD3` — mega-splat-only copy (one splat per cluster)
4. Baker also creates a child GameObject hierarchy under the controller, with one `GaussianSplatRenderer` per LOD bucket, all initially disabled except LOD0. (The user can also wire this manually; baker prints instructions.)

**Run time** (`GaussianLODController` + `SplatRenderFeature`):
1. Per-cluster `FrustumCuller` + `ScreenCoverageEstimator` + `LODSelector` + `LODBudgetManager` still execute every frame. They produce a *scene-level decision*: "the active LOD bucket for this frame is N, projected splat cost is M".
2. `SplatDrawCallAssembler` does NOT build indirect args. Its job is reduced to: read the LOD decision, toggle the corresponding child `GaussianSplatRenderer.enabled`, disable the others. One renderer enabled at a time.
3. `LODTransitionController` applies hysteresis to bucket switches (3-frame requirement) to prevent popping.
4. Aras's `GaussianSplatURPFeature` then runs in the SAME frame's `BeforeRenderingTransparents` and draws whichever child renderer we left enabled.
5. `SplatRenderFeature` (ours) is a **culling-and-LOD-decision URP feature**, not a draw feature. It must be ordered BEFORE Aras's `GaussianSplatURPFeature` in the URP renderer asset's feature list. SETUP.md will document this.

**What survives from the original spec, fully functional:**
- All Util/, Clustering/, Culling/, LOD/, Stereo/ classes — same role, same APIs.
- All four compute shaders — same kernels, same role. `MegaSplatBlit` is now an Editor-time helper used by the baker rather than a runtime kernel.
- `GpuSplatSorter` — fully implemented bitonic sorter, kept as a working utility. It is dispatched on the per-cluster *visible-mask* index list each frame to depth-sort the visible-cluster *centers* (not individual splats), used by `LODBudgetManager` to fill nearest-first. This gives it a real, used purpose.

**What changes from the original spec:**
- `SplatDrawCallAssembler` no longer builds indirect args; it selects the active LOD bucket renderer.
- `SplatRenderFeature` no longer draws; it sequences our culling pipeline as a `BeforeRenderingTransparents` URP feature ordered before Aras's feature.
- Per-cluster runtime culling drives **bucket selection and profiling**, not per-cluster draws.
- Memory cost: ~1.94× the source asset for the four LOD child assets (LOD1=½, LOD2=¼, LOD3≈4KB).

The user pre-authorized this scope reduction in their Blocker 2 reply: *"it becomes a smart pre-filter that feeds Aras a reduced, pre-sorted index list rather than controlling his draw path directly. That is still valuable."*

---

## 2c. Aras position decode logic (transcribed from `Assets/Shaders/GaussianSplatting.hlsl`)

Source functions: `LoadSplatPos` (line 409), `LoadSplatPosValue` (394), `LoadAndDecodeVector` (346), `DecodePacked_*` (261–290). Constants: `kChunkSize = 256`, `VECTOR_FMT_32F=0, _16=1, _11=2, _6=3`.

**Per-splat stride** (bytes): `{ 32F: 12, Norm16: 6, Norm11: 4, Norm6: 2 }`.

**Decode of the raw value at byte offset `index * stride`**:

| Format | Read | Decode |
|---|---|---|
| `32F` | 3 × float32 | `float3(x, y, z)` directly |
| `Norm16` | 3 × uint16 | each `/ 65535.0` |
| `Norm11` | 1 × uint32 | `((v) & 2047)/2047, ((v>>11) & 1023)/1023, ((v>>21) & 2047)/2047` |
| `Norm6` | 1 × uint16 | `((v) & 63)/63, ((v>>6) & 31)/31, ((v>>11) & 31)/31` |

**Chunk dequantization** (only for non-`32F` formats; for `32F` positions are already absolute):
```
chunkIdx = splatIndex / 256
if (chunkIdx < chunkCount):
    chunk = chunkData[chunkIdx]
    posMin = float3(chunk.posX.x, chunk.posY.x, chunk.posZ.x)
    posMax = float3(chunk.posX.y, chunk.posY.y, chunk.posZ.y)
    pos = lerp(posMin, posMax, decoded01)
else:
    pos = decoded01   // (degenerate; effectively means "no chunking", treat as absolute)
```

`ChunkInfo.posX/Y/Z` is `Unity.Mathematics.float2` where `.x = min, .y = max`.

The HLSL also has unaligned-uint-load gymnastics (`addrA = addrU & ~3`) because HLSL only supports 4-byte-aligned `Load(addr)` on `ByteAddressBuffer`. **In C# with byte-array access we can read at any offset directly — the unaligned dance is unnecessary.** We will use `BinaryPrimitives.ReadUInt32LittleEndian` / `ReadUInt16LittleEndian` directly at `index * stride`.

Positions output by `LoadSplatPos` are in **the renderer's local object space** (the splat asset is rendered with `GaussianSplatRenderer.transform.localToWorldMatrix` applied). Octree bounds are therefore stored in object space. World-space bounds are computed at runtime by transforming with the active controller's transform.

---

## 3. Unconfirmed — needs verification before code is written

1. **HLSL decode logic for `Norm11` / `Norm16` / `Norm6` positions** — not yet read. Lives in `Assets/Shaders/` (likely `GaussianSplatting.hlsl` or similar). MUST be read and reproduced in C# before `SplatOctreeBuilder` can be implemented.
2. **`GaussianSplatRenderSystem.GatherSplatsForCamera`** — only partially read. Needs full read to determine whether we can inject a custom order buffer / draw range.
3. **Whether `chunkData` is ever null in practice** for the user's target assets. If lossless `Float32`, life is easy.
4. **Unity 6 RenderGraph URP API surface** — Aras already ships `GaussianSplatURPFeature.cs`. We should read it to learn how Aras hooks URP under Unity 6, then mirror the pattern (or coexist with it). Not yet read.
5. **Whether Aras's package's existing `GaussianSplatURPFeature` will conflict with our `SplatRenderFeature`.** If both are present, double-rendering is likely.
6. **Target platform XR plugin presence** — `Unity.XR.Management` is referenced in spec, but we have not confirmed it is in the project's `Packages/manifest.json`. If absent, `StereoCameraRig` cannot compile against it and must use a fallback.
7. **Scale ceiling** — spec says 1M–5M splats. Aras caps at 8.6M. Fine.
8. **`Allocator.Temp` lifetime in `StereoFrustumMerger`** — spec says return `NativeArray<Plane>` with `Allocator.Temp` and let caller dispose. `Temp` allocations are auto-freed at end of frame and *should not* be manually disposed; this is a spec inconsistency. Will use `Allocator.TempJob` instead and document.

---

## 4. System Dependency Graph

```
PlatformCapabilityChecker  (static, no deps)
        │
        ▼
ComputeShaderCache  (static, no deps)
NativeBufferPool    (depends on PlatformCapabilityChecker for sizing)
        │
        ▼
SplatClusterData (struct)  ◄── SplatClusterAsset / SplatClusterIndexAsset (SOs)
        ▲
        │ produced by
        │
SplatOctreeBuilder (Editor) ──► MegaSplatGenerator (Editor)
        │
        ▼  consumed at runtime by
        │
StereoCameraRig  ──►  StereoFrustumMerger
                              │
                              ▼
                       FrustumCuller ──► CullingResultBuffer
                              │
                              ▼
                       ScreenCoverageEstimator
                              │
                              ▼
                          LODSelector
                              │
                              ▼
                       LODBudgetManager
                              │
                              ▼
                    LODTransitionController
                              │
                              ▼
                    SplatDrawCallAssembler ──► GpuSplatSorter
                              │
                              ▼
                       SplatRenderFeature  (URP RenderGraph)
                              ▲
                              │ orchestrated by
                              │
                       GaussianLODController (the only MonoBehaviour)
```

All non-`Util` runtime classes implement `IDisposable`. `GaussianLODController.OnDestroy` walks the dependency graph leaves-first and disposes.

---

## 5. Integration points with Aras

| Integration | Mechanism | Confirmed? |
|---|---|---|
| Read splat count & bounds | `asset.splatCount`, `asset.boundsMin/Max` | ✅ |
| Read raw position bytes | `asset.posData.GetData<byte>()` + `asset.posFormat` + `asset.chunkData.GetData<ChunkInfo>()` | ✅ surface, ❌ decode logic |
| Read raw color bytes | `asset.colorData` + `asset.colorFormat` + `CalcTextureSize` | ✅ surface, ❌ decode logic |
| Find which `GaussianSplatRenderer` to drive | `GaussianLODController` holds an explicit serialized reference (NO `FindObjectOfType`) | ✅ design choice |
| Tell Aras's renderer what to draw | **OPEN — see Section 2** | ❌ |
| Coexist with `GaussianSplatURPFeature` | Probably exclude one or the other; user picks at setup time | ❌ |

---

## 6. Assumptions that could be wrong

- A1. Aras's `GaussianSplatting` assembly is `autoReferenced: true`, so we get it for free, but the spec asks us to reference it explicitly in `GaussianLOD.Runtime.asmdef`. We will reference it by name `GaussianSplatting`.
- A2. The user has Unity 6 LTS. RenderGraph API in URP 17+ is available. If they actually have URP 14 (Unity 2022), `SplatRenderFeature` will not compile.
- A3. The user actually has the URP package installed (the Aras asmdef gates URP code on `GS_ENABLE_URP`).
- A4. We can write to `Assets/GaussianLOD/` freely; no MCP / asset DB conflicts.
- A5. `Resources/` folder loading for compute shaders is acceptable. (We will create `Assets/GaussianLOD/Shaders/Resources/` and load via `Resources.Load<ComputeShader>("GaussianLOD/SplatCull")`, etc., or use a `Resources.Load` path. Alternative: serialize compute shader references on `GaussianLODController`. The spec asks for both — `ComputeShaderCache` loads from Resources AND `GaussianLODController` has serialized refs. We will resolve by having `GaussianLODController` pass its serialized refs *into* `ComputeShaderCache.Initialize(...)` so the cache holds them, and `Resources` is the fallback path. Documented here.)
- A6. The 9000-cluster ceiling assumption: at depth 8 with 1024-splat min leaves, a 5M-splat asset cannot exceed ~5000 leaves; depth-8 max-fanout is 8^8 = 16M but actual leaf count is bounded by `splatCount / minLeafSize ≈ 4883`. We'll size pool buffers at 16384 to be safe.
- A7. Bitonic sort range: we will pad `_SortRangeCount` up to next power of two virtually inside the shader (out-of-range elements compared as +∞ so they sink to the end of the sorted region), but we will NOT touch indices outside `[start, start+count)` in the output buffer. The shader keeps a scratch range and writes back only `count` elements.

---

## 7. Multi-Asset Spatial Zone System (Phase 13)

### Problem
Large PLY scenes (10–50M splats) exceed practical single-asset limits. Scene-wide bucket switching means one distant cluster can force the entire asset to LOD0. Spatial splitting — dividing the scene into independently LOD-managed zones — solves both issues.

### Architecture

```
SpatialZoneManager (MonoBehaviour, orchestrator)
        │ serialized: GaussianLODController[] zones
        │
        ├──► ZoneBudgetSplitter (budget math)
        │       Takes total platform budget → splits proportionally by per-zone screen coverage
        │       Zones with 0 visible clusters → 0 budget
        │       Minimum kMinBudgetPerZone = 5,000 per active zone
        │
        └──► GaussianLODController × N (each zone independent)
                 │ Each owns its own: LODSelector, LODBudgetManager,
                 │ LODTransitionController, SplatDrawCallAssembler
                 │
                 └──► StereoCameraRig.Instance (shared singleton)
                         Ref-counted via GetOrCreate / Release
                         Multiple controllers share one XR camera query per frame
```

### Key design decisions

**SpatialZoneManager** is a pure orchestrator. It does NOT contain any LOD logic — it collects per-zone coverage from each controller's `LODSelector`, feeds them to `ZoneBudgetSplitter`, and writes the resulting per-zone budget into each controller's `LODBudgetManager.MaxSplatsPerFrame` before enforcement.

**ZoneBudgetSplitter** is a pure math class. It receives a total budget integer and an array of per-zone coverages, outputs per-zone budget allocations. No platform awareness, no Unity APIs. Budget distribution: reserve `kMinBudgetPerZone` per active zone, distribute the remainder proportionally by coverage, assign rounding residual to the highest-coverage zone.

**StereoCameraRig** gains a static singleton (`Instance`) with ref-counted lifecycle (`GetOrCreate` / `Release`). Multiple `GaussianLODController` instances in a multi-zone scene share one rig to avoid redundant XR API queries. The single-controller case still works identically — `GetOrCreate` creates the singleton on first call. Constructor remains public for backward compatibility.

**Single-controller backward compat**: If no `SpatialZoneManager` is present, each `GaussianLODController` runs independently using its own `splatsPerFrameOverride` or the platform default. No behavioral change from v1.

### Offline workflow: PLY splitting

`Tools/split_ply.py` is a standalone Python script (numpy-only) that spatially partitions a source PLY into grid cells. Each output PLY preserves all source properties. Overlap regions duplicate splats to prevent seams. Zones below `--min-splats` are merged into their nearest neighbor. This runs before the Unity bake pipeline — the user imports each output PLY into Unity, bakes each via `SplatClusterBaker`, then wires each as a zone in `SpatialZoneManager`.

### Updated dependency graph (multi-zone path)

```
SpatialZoneManager
    ├── ZoneBudgetSplitter
    └── GaussianLODController[] ──► (same per-zone graph as §4)
            └── StereoCameraRig.Instance (shared)
```
