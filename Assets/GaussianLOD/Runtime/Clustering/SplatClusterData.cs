// SPDX-License-Identifier: MIT
// SplatClusterData — pure unmanaged, blittable, NativeArray-compatible struct that
// describes one node in the baked octree. The HLSL counterpart in SplatCull.compute
// MUST mirror this layout exactly (field order and sizes).

using System.Runtime.InteropServices;
using UnityEngine;

namespace GaussianLOD.Runtime.Clustering
{
    /// <summary>
    /// One leaf cluster in the baked splat octree. Layout is locked because the GPU
    /// compute shaders consume this as a StructuredBuffer&lt;SplatClusterData&gt;.
    ///
    /// Total size: 68 bytes. The HLSL struct uses identical field order:
    ///   int startIndex, int count, float3 boundsCenter, float3 boundsExtents,
    ///   int lodLevel, float3 megaSplatPosition, float4 megaSplatColor, float megaSplatScale.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SplatClusterData
    {
        /// <summary>Index into <see cref="SplatClusterIndexAsset.allIndices"/>.</summary>
        public int startIndex;
        /// <summary>Number of splat indices in this cluster.</summary>
        public int count;
        /// <summary>Object-space bounds (NOT world-space) of the cluster.</summary>
        public Bounds worldBounds; // 24 bytes: Vector3 center + Vector3 extents
        /// <summary>Initial LOD assignment from the bake; the runtime selector overrides per frame.</summary>
        public int lodLevel;
        /// <summary>Object-space position of the representative splat for this cluster.</summary>
        public Vector3 megaSplatPosition;
        /// <summary>Representative splat color. Informational; the LOD-bucketed render path
        /// does not use this for drawing (Aras's renderer uses its own per-splat colors).</summary>
        public Color megaSplatColor;
        /// <summary>Approximate world radius of the cluster's bounding sphere × 0.6.</summary>
        public float megaSplatScale;

        public const int kStrideBytes = 4 + 4 + 24 + 4 + 12 + 16 + 4; // = 68
    }
}
