// SPDX-License-Identifier: MIT
// CullingResultBuffer — owns the GPU mask buffer that records which clusters survived
// frustum culling. The buffer is created once at startup (sized for the worst case)
// and reused every frame; never per-frame allocation.

using System;
using GaussianLOD.Runtime.Util;
using UnityEngine;

namespace GaussianLOD.Runtime.Culling
{
    /// <summary>
    /// Wraps a single ComputeBuffer of cluster-visibility flags (1 = visible, 0 = culled).
    /// Owns the buffer and disposes it on Dispose. Sized at construction; never grows.
    /// </summary>
    public sealed class CullingResultBuffer : IDisposable
    {
        public ComputeBuffer VisibleMaskBuffer { get; }
        public int ClusterCapacity { get; }

        /// <summary>
        /// CPU-mirrored count of visible clusters this frame. Set by the consumer
        /// (LODSelector or SplatDrawCallAssembler) after it processes the GPU mask.
        /// </summary>
        public int VisibleCount { get; set; }

        readonly bool m_OwnsBuffer;

        /// <summary>
        /// Construct from a NativeBufferPool — uses the pool's pre-allocated VisibleMaskBuffer.
        /// Disposing this object does NOT dispose the pool's buffer; the pool owns it.
        /// </summary>
        public CullingResultBuffer(NativeBufferPool pool)
        {
            if (pool == null) throw new ArgumentNullException(nameof(pool));
            VisibleMaskBuffer = pool.VisibleMaskBuffer
                ?? throw new InvalidOperationException("Pool VisibleMaskBuffer is null.");
            ClusterCapacity = pool.ClusterCapacity;
            m_OwnsBuffer = false;
        }

        /// <summary>
        /// Standalone construction (no pool). Allocates its own buffer; will dispose it.
        /// Used by tests.
        /// </summary>
        public CullingResultBuffer(int clusterCapacity)
        {
            if (clusterCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(clusterCapacity));
            VisibleMaskBuffer = new ComputeBuffer(clusterCapacity, sizeof(uint), ComputeBufferType.Structured)
                { name = "GLOD_VisibleMask_Standalone" };
            ClusterCapacity = clusterCapacity;
            m_OwnsBuffer = true;
        }

        public void Dispose()
        {
            if (m_OwnsBuffer)
                VisibleMaskBuffer?.Dispose();
        }
    }
}
