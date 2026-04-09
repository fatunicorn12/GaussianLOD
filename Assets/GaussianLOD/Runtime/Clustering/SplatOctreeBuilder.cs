// SPDX-License-Identifier: MIT
// SplatOctreeBuilder — Editor-time octree construction over a GaussianSplatAsset's
// decoded splat positions. Produces a flat SplatClusterData[] and a paired flat
// int[] of source-splat indices, suitable for serialization into a SplatClusterAsset
// + SplatClusterIndexAsset pair.
//
// Algorithm:
//   1. CPU-decode every splat position (object-space) into a NativeArray<float3>,
//      reproducing Aras's HLSL `LoadSplatPos` faithfully (see ARCHITECTURE.md §2c).
//   2. Build an octree by in-place index partitioning. Each leaf becomes a contiguous
//      [start, start+count) span of the final index array, which is exactly what
//      SplatClusterData stores.
//   3. Cancellable via EditorUtility.DisplayCancelableProgressBar; all native memory
//      is released in finally blocks.
//
// Editor-only. Lives in Runtime/ because the bake step is invoked by SplatClusterBaker
// (Editor) but the data structures it produces are runtime-visible. Wrapping the entire
// type in #if UNITY_EDITOR keeps it stripped from builds.

#if UNITY_EDITOR
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using GaussianSplatting.Runtime;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace GaussianLOD.Runtime.Clustering
{
    public static class SplatOctreeBuilder
    {
        public const int kMaxDepth = 8;
        public const int kMinSplatsPerLeaf = 1024;
        public const int kDecodeChunkSize = 500_000;

        public struct BuildResult
        {
            public SplatClusterData[] clusters;
            public int[] allIndices;
            public Bounds sceneBounds;
            public int maxDepth;
            public bool cancelled;
        }

        /// <summary>
        /// Build the octree from a source <c>GaussianSplatAsset</c>. Pure function
        /// (other than progress reporting). Throws on invalid input. Returns
        /// <c>cancelled = true</c> if the user cancelled the progress dialog;
        /// in that case all native memory is released and <c>clusters</c> is null.
        /// </summary>
        public static BuildResult Build(GaussianSplatAsset src, bool showProgressBar)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            int n = src.splatCount;
            if (n <= 0) throw new ArgumentException("Source asset has no splats.", nameof(src));

            var positions = new NativeArray<float3>(n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            try
            {
                if (!DecodePositions(src, positions, showProgressBar))
                {
                    return new BuildResult { cancelled = true };
                }

                // Compute root bounds (asset-declared bounds may be tighter or looser; recompute to be safe).
                Bounds root = new Bounds(positions[0], Vector3.zero);
                for (int i = 1; i < n; ++i)
                    root.Encapsulate(positions[i]);
                // pad slightly to avoid points exactly on a splitting plane
                root.Expand(0.0001f);

                int[] indices = new int[n];
                for (int i = 0; i < n; ++i) indices[i] = i;

                var clustersOut = new List<SplatClusterData>(Mathf.Max(64, n / kMinSplatsPerLeaf));
                int observedMaxDepth = 0;

                if (showProgressBar)
                    EditorUtility.DisplayProgressBar("GaussianLOD", "Building octree...", 0.5f);

                BuildRecursive(indices, 0, n, root, 0, positions, clustersOut, ref observedMaxDepth);

                return new BuildResult
                {
                    clusters = clustersOut.ToArray(),
                    allIndices = indices,
                    sceneBounds = root,
                    maxDepth = observedMaxDepth,
                    cancelled = false
                };
            }
            finally
            {
                if (positions.IsCreated) positions.Dispose();
                if (showProgressBar) EditorUtility.ClearProgressBar();
            }
        }

        // ---- Recursive in-place octree partition --------------------------------------------------

        static void BuildRecursive(
            int[] indices, int start, int count,
            Bounds bounds, int depth,
            NativeArray<float3> positions,
            List<SplatClusterData> clustersOut,
            ref int observedMaxDepth)
        {
            if (depth > observedMaxDepth) observedMaxDepth = depth;

            bool isLeaf = (count <= kMinSplatsPerLeaf) || (depth >= kMaxDepth);
            if (isLeaf)
            {
                // tighten bounds to the actual point set in this leaf
                Bounds tight = new Bounds(positions[indices[start]], Vector3.zero);
                for (int i = 1; i < count; ++i)
                    tight.Encapsulate(positions[indices[start + i]]);

                // If every member splat shares the same position the AABB collapses to a
                // point; expand by a small epsilon so downstream code (validator,
                // mega-splat scale, frustum tests) sees a finite volume.
                if (tight.size == Vector3.zero)
                    tight.Expand(0.01f);

                clustersOut.Add(new SplatClusterData
                {
                    startIndex = start,
                    count = count,
                    worldBounds = tight,
                    lodLevel = 0,
                });
                return;
            }

            Vector3 c = bounds.center;

            // Pass 1: count splats per octant.
            Span<int> octCounts = stackalloc int[8];
            for (int i = 0; i < count; ++i)
            {
                int srcIdx = indices[start + i];
                float3 p = positions[srcIdx];
                int o = (p.x >= c.x ? 1 : 0) | (p.y >= c.y ? 2 : 0) | (p.z >= c.z ? 4 : 0);
                octCounts[o]++;
            }

            // Pass 2: scatter into a per-call temp.
            Span<int> octStarts = stackalloc int[8];
            int acc = 0;
            for (int o = 0; o < 8; ++o)
            {
                octStarts[o] = acc;
                acc += octCounts[o];
            }
            int[] temp = new int[count];
            Span<int> octCursors = stackalloc int[8];
            for (int o = 0; o < 8; ++o) octCursors[o] = octStarts[o];

            for (int i = 0; i < count; ++i)
            {
                int srcIdx = indices[start + i];
                float3 p = positions[srcIdx];
                int o = (p.x >= c.x ? 1 : 0) | (p.y >= c.y ? 2 : 0) | (p.z >= c.z ? 4 : 0);
                temp[octCursors[o]++] = srcIdx;
            }
            Array.Copy(temp, 0, indices, start, count);

            // Recurse.
            for (int o = 0; o < 8; ++o)
            {
                int childCount = octCounts[o];
                if (childCount == 0) continue;
                Bounds childBounds = OctantBounds(bounds, o);
                BuildRecursive(indices, start + octStarts[o], childCount,
                    childBounds, depth + 1, positions, clustersOut, ref observedMaxDepth);
            }
        }

        static Bounds OctantBounds(Bounds parent, int octant)
        {
            Vector3 ext = parent.extents * 0.5f;
            Vector3 c = parent.center;
            Vector3 childCenter = new Vector3(
                c.x + ((octant & 1) != 0 ? ext.x : -ext.x),
                c.y + ((octant & 2) != 0 ? ext.y : -ext.y),
                c.z + ((octant & 4) != 0 ? ext.z : -ext.z));
            return new Bounds(childCenter, ext * 2f);
        }

        // ---- Position decode (reproduces Aras HLSL LoadSplatPos) ----------------------------------

        /// <summary>
        /// Decode every splat position from the source asset's compressed posData / chunkData
        /// into the supplied NativeArray. Returns false if the user cancelled.
        /// Object space — same coordinate frame Aras uses internally.
        /// </summary>
        public static bool DecodePositions(GaussianSplatAsset src, NativeArray<float3> outPositions, bool showProgressBar)
        {
            int n = src.splatCount;
            if (outPositions.Length < n)
                throw new ArgumentException("outPositions too small.", nameof(outPositions));

            int stride = GaussianSplatAsset.GetVectorSize(src.posFormat);

            // posData.GetData<byte>() returns a NativeArray<byte> view of the underlying TextAsset bytes.
            NativeArray<byte> posBytes = src.posData.GetData<byte>();
            try
            {
                NativeArray<GaussianSplatAsset.ChunkInfo> chunks = default;
                bool hasChunks = src.chunkData != null && src.chunkData.dataSize != 0;
                try
                {
                    if (hasChunks)
                        chunks = src.chunkData.GetData<GaussianSplatAsset.ChunkInfo>();

                    int chunkCount = hasChunks ? chunks.Length : 0;
                    int processed = 0;
                    int reportEvery = Math.Max(1, n / 50);

                    for (int i = 0; i < n; ++i)
                    {
                        float3 decoded = DecodeOne(posBytes, i * stride, src.posFormat);

                        if (hasChunks)
                        {
                            int chunkIdx = i / GaussianSplatAsset.kChunkSize;
                            if (chunkIdx < chunkCount)
                            {
                                var ch = chunks[chunkIdx];
                                float3 mn = new float3(ch.posX.x, ch.posY.x, ch.posZ.x);
                                float3 mx = new float3(ch.posX.y, ch.posY.y, ch.posZ.y);
                                decoded = math.lerp(mn, mx, decoded);
                            }
                        }
                        outPositions[i] = decoded;

                        processed++;
                        if (showProgressBar && (processed % reportEvery == 0))
                        {
                            float p = processed / (float)n;
                            if (EditorUtility.DisplayCancelableProgressBar(
                                "GaussianLOD", $"Decoding splat positions ({processed:N0} / {n:N0})", p * 0.5f))
                            {
                                return false;
                            }
                        }
                    }
                }
                finally
                {
                    // chunks is a view into the TextAsset and does not need disposing,
                    // but we null-guard regardless.
                }
            }
            finally
            {
                // posBytes is also a view; no Dispose needed.
            }
            return true;
        }

        static float3 DecodeOne(NativeArray<byte> data, int byteOffset, GaussianSplatAsset.VectorFormat fmt)
        {
            // We have byte-level access in C#, so we can read at arbitrary offsets directly,
            // unlike the HLSL path which has to do unaligned-uint gymnastics on ByteAddressBuffer.
            switch (fmt)
            {
                case GaussianSplatAsset.VectorFormat.Float32:
                {
                    float x = ReadF32(data, byteOffset + 0);
                    float y = ReadF32(data, byteOffset + 4);
                    float z = ReadF32(data, byteOffset + 8);
                    return new float3(x, y, z);
                }
                case GaussianSplatAsset.VectorFormat.Norm16:
                {
                    ushort x = ReadU16(data, byteOffset + 0);
                    ushort y = ReadU16(data, byteOffset + 2);
                    ushort z = ReadU16(data, byteOffset + 4);
                    const float k = 1f / 65535f;
                    return new float3(x * k, y * k, z * k);
                }
                case GaussianSplatAsset.VectorFormat.Norm11:
                {
                    uint v = ReadU32(data, byteOffset);
                    float x = (v & 2047u) / 2047f;
                    float y = ((v >> 11) & 1023u) / 1023f;
                    float z = ((v >> 21) & 2047u) / 2047f;
                    return new float3(x, y, z);
                }
                case GaussianSplatAsset.VectorFormat.Norm6:
                {
                    ushort v = ReadU16(data, byteOffset);
                    float x = (v & 63u) / 63f;
                    float y = ((v >> 6) & 31u) / 31f;
                    float z = ((v >> 11) & 31u) / 31f;
                    return new float3(x, y, z);
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(fmt));
            }
        }

        static unsafe float ReadF32(NativeArray<byte> data, int offset)
        {
            // Slow-path safe read using BinaryPrimitives over a span. Native pointer access
            // would be faster, but Editor-time bake speed is fine here.
            ReadOnlySpan<byte> span = stackalloc byte[4];
            fixed (byte* ptr = &span.GetPinnableReference()) { }
            // Cleaner: copy 4 bytes manually
            byte b0 = data[offset + 0];
            byte b1 = data[offset + 1];
            byte b2 = data[offset + 2];
            byte b3 = data[offset + 3];
            uint u = (uint)b0 | ((uint)b1 << 8) | ((uint)b2 << 16) | ((uint)b3 << 24);
            return BitConverter.Int32BitsToSingle((int)u);
        }

        static uint ReadU32(NativeArray<byte> data, int offset)
        {
            byte b0 = data[offset + 0];
            byte b1 = data[offset + 1];
            byte b2 = data[offset + 2];
            byte b3 = data[offset + 3];
            return (uint)b0 | ((uint)b1 << 8) | ((uint)b2 << 16) | ((uint)b3 << 24);
        }

        static ushort ReadU16(NativeArray<byte> data, int offset)
        {
            byte b0 = data[offset + 0];
            byte b1 = data[offset + 1];
            return (ushort)((uint)b0 | ((uint)b1 << 8));
        }
    }
}
#endif
