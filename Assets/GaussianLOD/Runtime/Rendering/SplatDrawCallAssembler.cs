// SPDX-License-Identifier: MIT
// SplatDrawCallAssembler — under the per-cluster architecture (ARCHITECTURE.md §8)
// this class no longer toggles LOD-bucket GameObjects on/off (that was the §2b design,
// kept as historical record). Its new role:
//
//   1. On construction, disable the legacy LOD1/LOD2/LOD3 bucket GameObjects so the
//      scene does not double-render. They remain in the scene as a rollback aid.
//   2. Each frame, compute the CPU-side total of "visible splats at their assigned LOD"
//      and write it into renderer.activeSplatCount.
//
// The CPU-side write is a fallback: if GpuSplatSorter's BeforeSort handler fires the
// same frame it will overwrite activeSplatCount with the GPU-readback value just before
// DrawProcedural. When the extension API is unavailable (renderer null or legacy
// Aras package), the CPU write here is the ONLY thing limiting the draw — which
// produces "nearest-N" truncation because Aras will still depth-sort the full asset.

using System;
using GaussianLOD.Runtime.Clustering;
using GaussianSplatting.Runtime;
using Unity.Collections;
using UnityEngine;

namespace GaussianLOD.Runtime.Rendering
{
    public sealed class SplatDrawCallAssembler : IDisposable
    {
        readonly GaussianSplatRenderer m_Renderer;
        readonly SplatClusterData[] m_Clusters;

        public int LastFrameVisibleSplatCount { get; private set; }
        public int LastFrameVisibleClusterCount { get; private set; }

        /// <summary>
        /// Construct the assembler. <paramref name="lod0Renderer"/> is the single
        /// GaussianSplatRenderer that the runtime now drives; <paramref name="unusedBucketGOs"/>
        /// is the list of legacy LOD1/LOD2/LOD3 GameObjects produced by the old baker —
        /// they are disabled once here and never touched again.
        /// </summary>
        public SplatDrawCallAssembler(
            GaussianSplatRenderer lod0Renderer,
            SplatClusterData[] clusters,
            GameObject[] unusedBucketGOs)
        {
            m_Renderer = lod0Renderer; // null allowed — assembler is then a no-op.
            m_Clusters = clusters ?? throw new ArgumentNullException(nameof(clusters));

            if (unusedBucketGOs != null)
            {
                for (int i = 0; i < unusedBucketGOs.Length; ++i)
                {
                    var go = unusedBucketGOs[i];
                    if (go != null && go.activeSelf)
                        go.SetActive(false);
                }
            }
        }

        /// <summary>
        /// Compute the CPU-side visible splat total using LODSelector output and write it
        /// to renderer.activeSplatCount. Mirrors the per-cluster stride math in
        /// CSBuildFilteredIndices exactly so the CPU value matches the GPU counter.
        /// </summary>
        public void Apply(NativeArray<int> committedLodLevels, NativeArray<byte> visibleFlags)
        {
            int visibleSplats = 0;
            int visibleClusters = 0;
            int n = m_Clusters.Length;

            for (int i = 0; i < n; ++i)
            {
                if (visibleFlags[i] == 0) continue;
                visibleClusters++;

                int c = m_Clusters[i].count;
                int lod = committedLodLevels[i];
                int emit;
                if (lod <= 0)      emit = c;
                else if (lod == 1) emit = (c + 1) >> 1;
                else if (lod == 2) emit = (c + 3) >> 2;
                else               emit = 1;
                visibleSplats += emit;
            }

            LastFrameVisibleClusterCount = visibleClusters;
            LastFrameVisibleSplatCount = visibleSplats;

            if (m_Renderer == null) return;
            // A value of 0 means "skip the draw". We pass the computed count directly —
            // the field auto-clamps to [0, splatCount] inside GaussianSplatRenderer.
            m_Renderer.activeSplatCount = visibleSplats;
        }

        public void Dispose()
        {
            if (m_Renderer != null)
            {
                m_Renderer.activeSplatCount = -1; // revert to "draw all"
            }
            LastFrameVisibleClusterCount = 0;
            LastFrameVisibleSplatCount = 0;
        }
    }
}
