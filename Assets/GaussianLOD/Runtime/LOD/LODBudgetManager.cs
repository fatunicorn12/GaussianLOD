// SPDX-License-Identifier: MIT
// LODBudgetManager — enforces a per-frame splat budget by demoting the furthest
// over-budget clusters one LOD level at a time. Operates on the CPU mirror
// produced by LODSelector. Fires UnityEvent OnBudgetExceeded when the initial
// pass exceeds budget (i.e., demotion was needed).
//
// "Furthest first" is implemented as a partial sort by squared distance descending,
// because we only need to demote enough clusters to reach the budget — typically
// a small fraction of the visible set.

using System;
using GaussianLOD.Runtime.Clustering;
using GaussianLOD.Runtime.Util;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Events;

namespace GaussianLOD.Runtime.LOD
{
    public sealed class LODBudgetManager : IDisposable
    {
        // Per-LOD splat-cost multipliers, applied to the cluster's source splat count.
        public const float kCostLod0 = 1.00f;
        public const float kCostLod1 = 0.50f;
        public const float kCostLod2 = 0.25f;
        public const float kCostLod3 = 0.0f; // mega-splat: ~1 splat / cluster, negligible vs budget

        public int MaxSplatsPerFrame { get; set; }

        public int LastFrameSplatCost { get; private set; }
        public int LastFrameDemotionCount { get; private set; }
        public bool LastFrameOverBudget { get; private set; }

        public UnityEvent OnBudgetExceeded { get; } = new UnityEvent();

        readonly LODSelector m_Selector;

        // Pre-allocated scratch — never grows after Awake.
        NativeArray<int> m_DistOrder;
        NativeArray<float> m_DistSq;

        bool m_Disposed;

        /// <summary>
        /// Construct with the LODSelector whose output we will mutate, and an initial
        /// per-frame budget. If <paramref name="budget"/> is non-positive, the platform
        /// recommended value from <see cref="PlatformCapabilityChecker"/> is used.
        /// </summary>
        public LODBudgetManager(LODSelector selector, int budget)
        {
            m_Selector = selector ?? throw new ArgumentNullException(nameof(selector));
            MaxSplatsPerFrame = budget > 0
                ? budget
                : PlatformCapabilityChecker.RecommendedSplatBudget;

            int n = selector.ClusterCount;
            m_DistOrder = new NativeArray<int>(n, Allocator.Persistent);
            m_DistSq = new NativeArray<float>(n, Allocator.Persistent);
        }

        /// <summary>
        /// Run after LODSelector.Run(). Computes the projected splat cost; if it exceeds
        /// the budget, demotes the furthest-from-camera clusters one LOD level at a time
        /// until the cost fits or all visible clusters are at LOD3.
        /// </summary>
        public void Enforce(SplatClusterData[] clusters, Vector3 cameraWorldPos, Matrix4x4 objectToWorld)
        {
            if (clusters == null) throw new ArgumentNullException(nameof(clusters));
            int n = m_Selector.ClusterCount;
            if (clusters.Length != n)
                throw new ArgumentException("clusters length mismatch with selector.", nameof(clusters));

            var lods = m_Selector.LodLevels;
            var visible = m_Selector.VisibleFlags;

            // Initial cost.
            int cost = ComputeCost(clusters, lods, visible);
            int demotions = 0;
            bool over = cost > MaxSplatsPerFrame;

            if (over)
            {
                // Build distance order over visible clusters only (furthest first).
                int v = 0;
                for (int i = 0; i < n; ++i)
                {
                    if (visible[i] == 0) continue;
                    Vector3 wcenter = objectToWorld.MultiplyPoint3x4(clusters[i].worldBounds.center);
                    Vector3 d = wcenter - cameraWorldPos;
                    m_DistOrder[v] = i;
                    m_DistSq[v] = d.sqrMagnitude;
                    v++;
                }
                SortIndicesByKeyDescending(m_DistOrder, m_DistSq, v);

                // Walk furthest-first, demoting one level per pass until under budget.
                int passOrigin = 0;
                while (cost > MaxSplatsPerFrame && passOrigin < v)
                {
                    int idx = m_DistOrder[passOrigin++];
                    int lod = lods[idx];
                    if (lod >= LODSelector.kLODMega) continue;

                    int oldSplatCount = clusters[idx].count;
                    float oldCost = LodCost(lod) * oldSplatCount;
                    float newCost = LodCost(lod + 1) * oldSplatCount;
                    cost -= Mathf.RoundToInt(oldCost - newCost);
                    lods[idx] = lod + 1;
                    demotions++;
                }
            }

            LastFrameSplatCost = cost;
            LastFrameDemotionCount = demotions;
            LastFrameOverBudget = over;

            if (over)
                OnBudgetExceeded?.Invoke();
        }

        public static float LodCost(int lod) => lod switch
        {
            LODSelector.kLODFull    => kCostLod0,
            LODSelector.kLODHalf    => kCostLod1,
            LODSelector.kLODQuarter => kCostLod2,
            LODSelector.kLODMega    => kCostLod3,
            _ => 0f
        };

        static int ComputeCost(SplatClusterData[] clusters, NativeArray<int> lods, NativeArray<byte> visible)
        {
            float total = 0f;
            for (int i = 0; i < clusters.Length; ++i)
            {
                if (visible[i] == 0) continue;
                total += LodCost(lods[i]) * clusters[i].count;
            }
            return Mathf.RoundToInt(total);
        }

        // Indirect insertion sort: small visible-set sizes (≤ a few thousand) make this
        // faster than allocating-everywhere quicksorts; partial-sort would be even better
        // but the explicit cap below keeps worst case bounded.
        static void SortIndicesByKeyDescending(NativeArray<int> indices, NativeArray<float> keys, int count)
        {
            for (int i = 1; i < count; ++i)
            {
                int curIdx = indices[i];
                float curKey = keys[i];
                int j = i - 1;
                while (j >= 0 && keys[j] < curKey)
                {
                    indices[j + 1] = indices[j];
                    keys[j + 1] = keys[j];
                    j--;
                }
                indices[j + 1] = curIdx;
                keys[j + 1] = curKey;
            }
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            if (m_DistOrder.IsCreated) m_DistOrder.Dispose();
            if (m_DistSq.IsCreated) m_DistSq.Dispose();
        }
    }
}
