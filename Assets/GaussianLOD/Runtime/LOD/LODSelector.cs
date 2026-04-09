// SPDX-License-Identifier: MIT
// LODSelector — CPU per-frame pass that performs frustum culling, screen-coverage
// estimation, and threshold-based LOD assignment over the cluster array. Output is
// a writable NativeArray<int> of per-cluster LOD levels (0..3) plus per-frame stats.
//
// Why CPU and not the GPU SplatLODSelect kernel? At 5K–16K clusters this is well under
// 1ms on a single thread; running it CPU-side eliminates the 1–3 frame AsyncGPUReadback
// latency that would otherwise lag budget/bucket decisions in 90Hz VR. The HLSL kernel
// in SplatLODSelect.compute remains the canonical authoring reference and is dispatched
// by SplatRenderFeature for the optional "GPU mode" path used when the cluster count
// scales past 100K.

using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using GaussianLOD.Runtime.Clustering;

namespace GaussianLOD.Runtime.LOD
{
    public sealed class LODSelector : IDisposable
    {
        public const int kLODFull    = 0;
        public const int kLODHalf    = 1;
        public const int kLODQuarter = 2;
        public const int kLODMega    = 3;

        // Default coverage thresholds (per spec). Public so LODBudgetManager / EditorTools
        // can read them for profiling display.
        public const float kLod0Threshold = 0.02f;
        public const float kLod1Threshold = 0.005f;
        public const float kLod2Threshold = 0.001f;

        public float lod0Threshold = kLod0Threshold;
        public float lod1Threshold = kLod1Threshold;
        public float lod2Threshold = kLod2Threshold;

        readonly NativeArray<SplatClusterData> m_Clusters;
        NativeArray<int> m_LodLevels;
        NativeArray<byte> m_Visible; // 1 = visible, 0 = culled
        NativeArray<float> m_Coverage;

        public NativeArray<int> LodLevels => m_LodLevels;
        public NativeArray<byte> VisibleFlags => m_Visible;
        public NativeArray<float> Coverage => m_Coverage;
        public int ClusterCount => m_Clusters.Length;

        public int VisibleCount { get; private set; }
        public int LastLod0Count { get; private set; }
        public int LastLod1Count { get; private set; }
        public int LastLod2Count { get; private set; }
        public int LastLod3Count { get; private set; }

        bool m_Disposed;

        /// <summary>
        /// Construct from a baked cluster array. Internally clones the data into
        /// Persistent native memory for lock-free CPU access. The source array
        /// can be released after construction.
        /// </summary>
        public LODSelector(SplatClusterData[] clusters)
        {
            if (clusters == null) throw new ArgumentNullException(nameof(clusters));
            m_Clusters = new NativeArray<SplatClusterData>(clusters, Allocator.Persistent);
            m_LodLevels = new NativeArray<int>(clusters.Length, Allocator.Persistent);
            m_Visible = new NativeArray<byte>(clusters.Length, Allocator.Persistent);
            m_Coverage = new NativeArray<float>(clusters.Length, Allocator.Persistent);
        }

        /// <summary>
        /// Cull, estimate coverage, and assign per-cluster LOD level. Object-to-world
        /// matrix transforms cluster bounds (which are stored in object space) into
        /// the camera's world frame.
        /// </summary>
        public void Run(Plane[] frustumPlanes, Vector3 cameraWorldPos, Matrix4x4 objectToWorld)
        {
            if (frustumPlanes == null || frustumPlanes.Length != 6)
                throw new ArgumentException("Expected 6 frustum planes.", nameof(frustumPlanes));

            int n = m_Clusters.Length;
            int visible = 0;
            int c0 = 0, c1 = 0, c2 = 0, c3 = 0;

            for (int i = 0; i < n; ++i)
            {
                SplatClusterData cl = m_Clusters[i];

                // Transform object-space bounds to world space.
                Bounds wb = TransformBounds(cl.worldBounds, objectToWorld);

                bool inside = TestAABB(wb, frustumPlanes);
                if (!inside)
                {
                    m_Visible[i] = 0;
                    m_Coverage[i] = 0f;
                    m_LodLevels[i] = kLODMega;
                    continue;
                }

                m_Visible[i] = 1;
                visible++;

                Vector3 toCam = wb.center - cameraWorldPos;
                float distSq = math.max(0.0001f, toCam.sqrMagnitude);
                float radius = wb.extents.magnitude;
                // Solid-angle approximation: clusterRadius^2 / distance^2.
                float coverage = (radius * radius) / distSq;
                m_Coverage[i] = coverage;

                int lod;
                if (coverage > lod0Threshold) { lod = kLODFull;    c0++; }
                else if (coverage > lod1Threshold) { lod = kLODHalf;    c1++; }
                else if (coverage > lod2Threshold) { lod = kLODQuarter; c2++; }
                else                                { lod = kLODMega;    c3++; }
                m_LodLevels[i] = lod;
            }

            VisibleCount = visible;
            LastLod0Count = c0;
            LastLod1Count = c1;
            LastLod2Count = c2;
            LastLod3Count = c3;
        }

        static Bounds TransformBounds(Bounds local, Matrix4x4 m)
        {
            // 8-corner transform; conservative.
            Vector3 c = local.center;
            Vector3 e = local.extents;
            Vector3 p0 = m.MultiplyPoint3x4(new Vector3(c.x - e.x, c.y - e.y, c.z - e.z));
            Bounds wb = new Bounds(p0, Vector3.zero);
            wb.Encapsulate(m.MultiplyPoint3x4(new Vector3(c.x + e.x, c.y - e.y, c.z - e.z)));
            wb.Encapsulate(m.MultiplyPoint3x4(new Vector3(c.x - e.x, c.y + e.y, c.z - e.z)));
            wb.Encapsulate(m.MultiplyPoint3x4(new Vector3(c.x + e.x, c.y + e.y, c.z - e.z)));
            wb.Encapsulate(m.MultiplyPoint3x4(new Vector3(c.x - e.x, c.y - e.y, c.z + e.z)));
            wb.Encapsulate(m.MultiplyPoint3x4(new Vector3(c.x + e.x, c.y - e.y, c.z + e.z)));
            wb.Encapsulate(m.MultiplyPoint3x4(new Vector3(c.x - e.x, c.y + e.y, c.z + e.z)));
            wb.Encapsulate(m.MultiplyPoint3x4(new Vector3(c.x + e.x, c.y + e.y, c.z + e.z)));
            return wb;
        }

        static bool TestAABB(Bounds b, Plane[] planes)
        {
            // Conservative AABB / 6-plane test.
            Vector3 c = b.center;
            Vector3 e = b.extents;
            for (int i = 0; i < 6; ++i)
            {
                Plane p = planes[i];
                Vector3 n = p.normal;
                float r = e.x * math.abs(n.x) + e.y * math.abs(n.y) + e.z * math.abs(n.z);
                float s = Vector3.Dot(n, c) + p.distance;
                if (s + r < 0f) return false; // fully outside this plane
            }
            return true;
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            if (m_Clusters.IsCreated) m_Clusters.Dispose();
            if (m_LodLevels.IsCreated) m_LodLevels.Dispose();
            if (m_Visible.IsCreated) m_Visible.Dispose();
            if (m_Coverage.IsCreated) m_Coverage.Dispose();
        }
    }
}
