# GaussianLOD — Setup

## Prerequisites
- Unity 6 LTS (6000.0+) — required for the URP RenderGraph API used by `SplatRenderFeature`.
- Aras Pranckevicius's Gaussian Splatting package (`org.nesnausk.gaussian-splatting` 1.1.x) installed and working.
- URP renderer asset configured for your project.

## 1. Bake a cluster asset
1. `GaussianLOD ▸ Cluster Baker`
2. Drop a `GaussianSplatAsset` into the **Source** field.
3. Click **Bake**. You will get three new files next to the source:
   - `*_Clusters.asset` — the `SplatClusterAsset` (cluster metadata + LOD bucket refs)
   - `*_ClusterIndices.asset` — the per-splat index list
   - `*_LODBuckets.prefab` — parent with four child `GaussianSplatRenderer` GameObjects (LOD0..LOD3)

## 2. ⚠️ CRITICAL — Renderer feature ordering

> **🛑 STOP. READ THIS. 🛑**
>
> In your URP renderer asset's **Renderer Features** list:
>
> > **`SplatRenderFeature` MUST appear ABOVE `GaussianSplatURPFeature`.**
>
> If you do not do this, the wrong LOD bucket will render every frame and the
> entire LOD pipeline silently produces garbage. **This is the #1 setup mistake.**
> The order of items in the URP feature list controls execution order. Drag
> `SplatRenderFeature` so it sits *above* `GaussianSplatURPFeature` in that list.

After adding the feature you should see this on first play:

> `[GaussianLOD] SplatRenderFeature created. REMINDER: This feature MUST be ordered ABOVE 'GaussianSplatURPFeature'...`

That log line is your reminder — do not ignore it.

## 3. Drop the controller into your scene
1. Add an empty GameObject and attach `GaussianLODController`.
2. Assign:
   - **clusterAsset** — the `*_Clusters.asset` you baked.
   - **lod0..lod3 BucketGO** — drag the four `LOD0`..`LOD3` children of the baked prefab. Any of LOD1..LOD3 may be left null; the assembler will fall back to a coarser-then-finer available bucket.
   - **targetCamera** — your XR rig's main camera (leave empty to use `Camera.main`).
   - Compute shader fields can be left null; they fall back to `Resources/GaussianLOD/*`.
3. Press play. The `GaussianLOD Budget` overlay (Scene view ▸ overlay menu) shows live stats.

## 4. ⚠️ Known limitation — scene-wide LOD

Aras's `GaussianSplatRenderer` draws an entire asset as one indirect draw call.
We cannot inject a custom per-cluster index list into his renderer (the relevant
fields are `internal`). As a result **the LOD bucket selection is scene-wide,
not per-cluster**: each frame we pick the worst (most pessimistic) LOD across
visible clusters and switch the matching child renderer on.

For real GPU savings you must **author decimated `GaussianSplatAsset`s yourself**
and assign them to the LOD1/LOD2/LOD3 child renderers in the baked prefab. Out of
the box the baker wires all four buckets to the source asset, which is enough to
exercise the runtime end-to-end but does not save any GPU work.

A future v2 path that lifts this limitation is documented in
`ARCHITECTURE.md` ("Future v2 — InternalsVisibleTo").

## 5. Validating a bake
`GaussianLOD ▸ Validate Selected Cluster Asset` runs index integrity, bounds,
and mega-splat sanity checks and reports a summary dialog.

## 6. Budget tuning
`GaussianLODController.splatsPerFrameOverride` overrides the platform default
(AVP 120k, Quest 3S 80k, PC/ALVR 200k, Editor 250k). The overlay's bar turns
yellow above 75% and red at 100%.
