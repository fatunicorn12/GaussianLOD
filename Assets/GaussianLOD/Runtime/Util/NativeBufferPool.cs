// SPDX-License-Identifier: MIT
// NativeBufferPool — preallocates the ComputeBuffers needed by the LOD pipeline at
// startup, sized for a 5M-splat worst-case scene. After Initialize, Rent() returns
// an existing buffer that is large enough for the request; it never allocates a new
// one at runtime. Return() makes a buffer available for reuse.
//
// This is intentionally simple: a small list of (stride -> buffer-list) buckets.
// We are not aiming for a general-purpose pool; we're aiming for "the LOD pipeline
// never allocates a ComputeBuffer after Awake".

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianLOD.Runtime.Util
{
    public sealed class NativeBufferPool : IDisposable
    {
        // Worst-case sizing for a 5M splat scene with depth-8 octree, min-leaf 1024.
        // Cluster ceiling = 5_000_000 / 1024 ≈ 4883; we round up to 16_384 for headroom.
        public const int kClusterCapacity = 16_384;
        public const int kSortIndexCapacity = 5_000_000;

        public int ClusterCapacity { get; }
        public int SortIndexCapacity { get; }

        // Pre-allocated, named "core" buffers exposed for direct binding.
        public ComputeBuffer ClusterMetadataBuffer { get; private set; } // stride = sizeof(SplatClusterData)
        public ComputeBuffer VisibleMaskBuffer { get; private set; }      // stride = 4 (uint per cluster)
        public ComputeBuffer CoverageBuffer { get; private set; }         // stride = 4 (float per cluster)
        public ComputeBuffer LodLevelBuffer { get; private set; }         // stride = 4 (int per cluster)
        public ComputeBuffer SortIndexBuffer { get; private set; }        // stride = 4 (uint per splat)
        public ComputeBuffer SortKeyBuffer { get; private set; }          // stride = 4 (uint distance key)

        // Spare bucket pool for transient/secondary buffers, keyed by (stride, minCapacity).
        // Stays small in practice; the LOD pipeline only needs a handful.
        readonly Dictionary<int, List<ComputeBuffer>> m_FreeByStride = new();
        readonly HashSet<ComputeBuffer> m_Owned = new();

        bool m_Disposed;

        public NativeBufferPool(int clusterCapacity, int sortIndexCapacity, int clusterDataStride)
        {
            if (clusterCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(clusterCapacity));
            if (sortIndexCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(sortIndexCapacity));
            if (clusterDataStride <= 0) throw new ArgumentOutOfRangeException(nameof(clusterDataStride));

            ClusterCapacity = clusterCapacity;
            SortIndexCapacity = sortIndexCapacity;

            ClusterMetadataBuffer = Track(new ComputeBuffer(
                clusterCapacity, clusterDataStride, ComputeBufferType.Structured)
                { name = "GLOD_ClusterMetadata" });

            VisibleMaskBuffer = Track(new ComputeBuffer(
                clusterCapacity, sizeof(uint), ComputeBufferType.Structured)
                { name = "GLOD_VisibleMask" });

            CoverageBuffer = Track(new ComputeBuffer(
                clusterCapacity, sizeof(float), ComputeBufferType.Structured)
                { name = "GLOD_Coverage" });

            LodLevelBuffer = Track(new ComputeBuffer(
                clusterCapacity, sizeof(int), ComputeBufferType.Structured)
                { name = "GLOD_LodLevels" });

            SortIndexBuffer = Track(new ComputeBuffer(
                sortIndexCapacity, sizeof(uint), ComputeBufferType.Structured)
                { name = "GLOD_SortIndices" });

            SortKeyBuffer = Track(new ComputeBuffer(
                sortIndexCapacity, sizeof(uint), ComputeBufferType.Structured)
                { name = "GLOD_SortKeys" });
        }

        ComputeBuffer Track(ComputeBuffer b)
        {
            m_Owned.Add(b);
            return b;
        }

        /// <summary>
        /// Returns a pooled buffer with at least <paramref name="minimumSize"/> elements
        /// and exact <paramref name="stride"/>. Reuses an existing one if possible;
        /// otherwise allocates ONCE and tracks it for later disposal. Throws after the
        /// pool has been disposed.
        /// </summary>
        public ComputeBuffer Rent(int minimumSize, int stride)
        {
            if (m_Disposed) throw new ObjectDisposedException(nameof(NativeBufferPool));
            if (minimumSize <= 0) throw new ArgumentOutOfRangeException(nameof(minimumSize));
            if (stride <= 0) throw new ArgumentOutOfRangeException(nameof(stride));

            if (m_FreeByStride.TryGetValue(stride, out var list))
            {
                for (int i = list.Count - 1; i >= 0; --i)
                {
                    var b = list[i];
                    if (b.count >= minimumSize)
                    {
                        list.RemoveAt(i);
                        return b;
                    }
                }
            }

            var fresh = new ComputeBuffer(minimumSize, stride, ComputeBufferType.Structured)
                { name = $"GLOD_Pool_{stride}_{minimumSize}" };
            m_Owned.Add(fresh);
            return fresh;
        }

        /// <summary>
        /// Returns a buffer to the pool. The buffer must have been obtained from Rent
        /// (or be one of the named core buffers, in which case Return is a no-op).
        /// </summary>
        public void Return(ComputeBuffer buffer)
        {
            if (buffer == null) return;
            if (m_Disposed) return;

            // Don't pool the named core buffers; they're permanent.
            if (buffer == ClusterMetadataBuffer || buffer == VisibleMaskBuffer ||
                buffer == CoverageBuffer || buffer == LodLevelBuffer ||
                buffer == SortIndexBuffer || buffer == SortKeyBuffer)
                return;

            int stride = buffer.stride;
            if (!m_FreeByStride.TryGetValue(stride, out var list))
            {
                list = new List<ComputeBuffer>(4);
                m_FreeByStride[stride] = list;
            }
            list.Add(buffer);
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;

            foreach (var b in m_Owned)
                b?.Dispose();
            m_Owned.Clear();
            m_FreeByStride.Clear();

            ClusterMetadataBuffer = null;
            VisibleMaskBuffer = null;
            CoverageBuffer = null;
            LodLevelBuffer = null;
            SortIndexBuffer = null;
            SortKeyBuffer = null;
        }
    }
}
