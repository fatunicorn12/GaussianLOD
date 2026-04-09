// SPDX-License-Identifier: MIT
// LODTransitionController — applies hysteresis to per-cluster LOD switches. A cluster
// must request the same new LOD for kHysteresisFrames consecutive frames before the
// switch is committed. Prevents popping caused by single-frame coverage spikes.

using System;
using Unity.Collections;

namespace GaussianLOD.Runtime.LOD
{
    public sealed class LODTransitionController : IDisposable
    {
        public const int kHysteresisFrames = 3;

        // Committed LOD per cluster, plus pending request and consecutive-frame counter.
        NativeArray<int> m_Committed;
        NativeArray<int> m_PendingRequest;
        NativeArray<int> m_PendingCount;
        bool m_Disposed;
        bool m_FirstFrame = true;

        public int ClusterCount => m_Committed.Length;
        public int RequiredFrames => kHysteresisFrames;

        public LODTransitionController(int clusterCount)
        {
            if (clusterCount <= 0) throw new ArgumentOutOfRangeException(nameof(clusterCount));
            m_Committed = new NativeArray<int>(clusterCount, Allocator.Persistent);
            m_PendingRequest = new NativeArray<int>(clusterCount, Allocator.Persistent);
            m_PendingCount = new NativeArray<int>(clusterCount, Allocator.Persistent);
        }

        /// <summary>
        /// Smooth a per-cluster LOD-level array. The first call seeds the committed
        /// state directly (no hysteresis on frame 1). Subsequent calls only commit a
        /// new LOD when the same value has been requested for <see cref="kHysteresisFrames"/>
        /// consecutive frames; otherwise the previously committed value is written back.
        /// Operates in place on <paramref name="lodLevels"/>.
        /// </summary>
        public void Smooth(NativeArray<int> lodLevels)
        {
            if (!lodLevels.IsCreated) throw new ArgumentException("lodLevels not created.", nameof(lodLevels));
            if (lodLevels.Length != m_Committed.Length)
                throw new ArgumentException("lodLevels length mismatch.", nameof(lodLevels));

            int n = lodLevels.Length;

            if (m_FirstFrame)
            {
                for (int i = 0; i < n; ++i)
                {
                    m_Committed[i] = lodLevels[i];
                    m_PendingRequest[i] = lodLevels[i];
                    m_PendingCount[i] = 0;
                }
                m_FirstFrame = false;
                return;
            }

            for (int i = 0; i < n; ++i)
            {
                int requested = lodLevels[i];
                int committed = m_Committed[i];

                if (requested == committed)
                {
                    // No change requested — clear any pending state.
                    m_PendingRequest[i] = committed;
                    m_PendingCount[i] = 0;
                    // committed value is already what we want
                    continue;
                }

                if (requested == m_PendingRequest[i])
                {
                    int c = m_PendingCount[i] + 1;
                    if (c >= kHysteresisFrames)
                    {
                        // Commit the switch.
                        m_Committed[i] = requested;
                        m_PendingRequest[i] = requested;
                        m_PendingCount[i] = 0;
                    }
                    else
                    {
                        m_PendingCount[i] = c;
                        lodLevels[i] = committed; // hold previous committed value
                    }
                }
                else
                {
                    // New target — restart the counter and hold the previous committed value.
                    m_PendingRequest[i] = requested;
                    m_PendingCount[i] = 1;
                    lodLevels[i] = committed;
                }
            }
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            if (m_Committed.IsCreated) m_Committed.Dispose();
            if (m_PendingRequest.IsCreated) m_PendingRequest.Dispose();
            if (m_PendingCount.IsCreated) m_PendingCount.Dispose();
        }
    }
}
