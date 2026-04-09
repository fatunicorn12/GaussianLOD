// SPDX-License-Identifier: MIT
// MegaSplatGenerator — fills in the per-cluster representative ("mega-splat") fields
// of an already-built SplatClusterData array. Pure CPU, called once during the bake step
// after the octree has produced clusters and a flat splat-index array.
//
// The "representative splat" is the source-asset splat closest to the cluster's centroid.
// Its decoded position is stored as megaSplatPosition. megaSplatScale is the cluster's
// bounding-sphere radius × 0.6. megaSplatColor is set to white because the LOD-bucketed
// render path uses Aras's renderer to draw real splat colors — see ARCHITECTURE.md §2b.

using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianLOD.Runtime.Clustering
{
    public static class MegaSplatGenerator
    {
        /// <summary>Scale factor applied to the cluster's bounding-sphere radius.</summary>
        public const float kBoundingSphereScale = 0.6f;

        /// <summary>Floor for megaSplatScale. Prevents zero-scale mega-splats when a
        /// cluster's AABB collapses to a point (all member splats coincide).</summary>
        public const float kMinMegaSplatScale = 0.01f;

        /// <summary>
        /// Fill in <c>megaSplatPosition</c>, <c>megaSplatColor</c>, and <c>megaSplatScale</c>
        /// for every cluster. <paramref name="clusters"/> is mutated in place. Both inputs
        /// must be non-null and consistent (allIndices.Length == sum of cluster counts).
        /// </summary>
        /// <param name="clusters">Cluster array with bounds + index ranges already populated.</param>
        /// <param name="allIndices">Flat array of source-asset splat indices.</param>
        /// <param name="positions">Decoded object-space splat positions, indexed by source asset index.</param>
        public static void Generate(
            SplatClusterData[] clusters,
            int[] allIndices,
            NativeArray<float3> positions)
        {
            if (clusters == null) throw new System.ArgumentNullException(nameof(clusters));
            if (allIndices == null) throw new System.ArgumentNullException(nameof(allIndices));
            if (!positions.IsCreated) throw new System.ArgumentException("positions not created", nameof(positions));

            for (int c = 0; c < clusters.Length; ++c)
            {
                ref var cl = ref clusters[c];
                int start = cl.startIndex;
                int count = cl.count;
                if (count <= 0)
                {
                    cl.megaSplatPosition = cl.worldBounds.center;
                    cl.megaSplatColor = Color.white;
                    cl.megaSplatScale = 0f;
                    continue;
                }

                float3 centroid = float3.zero;
                for (int i = 0; i < count; ++i)
                    centroid += positions[allIndices[start + i]];
                centroid /= count;

                // find the splat closest to the centroid
                float bestDistSq = float.PositiveInfinity;
                int bestIdx = allIndices[start];
                for (int i = 0; i < count; ++i)
                {
                    int srcIdx = allIndices[start + i];
                    float3 d = positions[srcIdx] - centroid;
                    float ds = math.dot(d, d);
                    if (ds < bestDistSq)
                    {
                        bestDistSq = ds;
                        bestIdx = srcIdx;
                    }
                }

                cl.megaSplatPosition = positions[bestIdx];
                cl.megaSplatColor = Color.white; // see header comment / ARCHITECTURE.md §2b
                // bounding-sphere radius from the AABB extents
                Vector3 ext = cl.worldBounds.extents;
                float sphereRadius = ext.magnitude; // half-diagonal
                cl.megaSplatScale = Mathf.Max(sphereRadius * kBoundingSphereScale, kMinMegaSplatScale);
            }
        }
    }
}
