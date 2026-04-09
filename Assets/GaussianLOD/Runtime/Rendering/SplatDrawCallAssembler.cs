// SPDX-License-Identifier: MIT
// SplatDrawCallAssembler — under the LOD-bucketed sub-asset architecture
// (ARCHITECTURE.md §2b), this class does NOT build indirect draw arguments. Aras's
// renderer owns the actual draw calls. Our job is to choose which of the four child
// GaussianSplatRenderer GameObjects (LOD0–LOD3) is enabled this frame, based on the
// scene-wide LOD decision arriving from LODBudgetManager / LODTransitionController.
//
// "Scene-wide" means: we look at the most pessimistic per-cluster LOD currently
// committed across all visible clusters, and pick the bucket renderer that matches.
// Concretely: the chosen bucket is max(committedLod[i]) over visible i. This guarantees
// the active bucket can represent every visible cluster at least as coarsely as
// requested. If a higher-detail bucket is missing, we fall back to the next coarser
// available bucket.

using System;
using GaussianLOD.Runtime.Clustering;
using GaussianLOD.Runtime.LOD;
using Unity.Collections;
using UnityEngine;

namespace GaussianLOD.Runtime.Rendering
{
    public sealed class SplatDrawCallAssembler : IDisposable
    {
        readonly GameObject[] m_BucketGOs = new GameObject[4]; // index = LOD level

        public int CurrentBucket { get; private set; } = -1;
        public int LastFrameSelectedLod { get; private set; } = -1;

        /// <summary>
        /// Construct with the four bucket GameObjects (LOD0..LOD3). Any of them MAY be null;
        /// in that case the assembler falls back to the next coarser bucket that exists
        /// when the LOD selector requests an unavailable level.
        /// </summary>
        public SplatDrawCallAssembler(GameObject lod0, GameObject lod1, GameObject lod2, GameObject lod3)
        {
            m_BucketGOs[0] = lod0;
            m_BucketGOs[1] = lod1;
            m_BucketGOs[2] = lod2;
            m_BucketGOs[3] = lod3;

            if (lod0 == null && lod1 == null && lod2 == null && lod3 == null)
                throw new ArgumentException("All four LOD bucket GameObjects are null.");

            // Disable everything to start; the first Apply() will pick the active bucket.
            for (int i = 0; i < 4; ++i)
                if (m_BucketGOs[i] != null)
                    m_BucketGOs[i].SetActive(false);
        }

        /// <summary>
        /// Compute the scene-wide LOD level (the max of committed per-cluster LODs across
        /// visible clusters) and toggle the matching bucket GameObject on, others off.
        /// </summary>
        public void Apply(NativeArray<int> committedLodLevels, NativeArray<byte> visibleFlags)
        {
            int worst = -1;
            for (int i = 0; i < committedLodLevels.Length; ++i)
            {
                if (visibleFlags[i] == 0) continue;
                int l = committedLodLevels[i];
                if (l > worst) worst = l;
            }

            if (worst < 0)
            {
                // Nothing visible — disable all buckets to save GPU work.
                LastFrameSelectedLod = -1;
                SetActiveBucket(-1);
                return;
            }

            LastFrameSelectedLod = worst;

            // Find the closest-available bucket at this level or coarser, walking down to LOD0
            // if no coarser exists. (Coarser buckets are cheaper but may not exist; finer buckets
            // can always represent a coarser request, just at higher cost.)
            int chosen = -1;
            for (int l = worst; l < 4; ++l)
            {
                if (m_BucketGOs[l] != null) { chosen = l; break; }
            }
            if (chosen < 0)
            {
                for (int l = worst - 1; l >= 0; --l)
                {
                    if (m_BucketGOs[l] != null) { chosen = l; break; }
                }
            }

            SetActiveBucket(chosen);
        }

        void SetActiveBucket(int bucket)
        {
            if (CurrentBucket == bucket) return;
            for (int i = 0; i < 4; ++i)
            {
                if (m_BucketGOs[i] == null) continue;
                bool shouldBe = (i == bucket);
                if (m_BucketGOs[i].activeSelf != shouldBe)
                    m_BucketGOs[i].SetActive(shouldBe);
            }
            CurrentBucket = bucket;
        }

        public void Dispose()
        {
            // Don't destroy the bucket GOs — they're owned by the controller / scene.
            CurrentBucket = -1;
        }
    }
}
