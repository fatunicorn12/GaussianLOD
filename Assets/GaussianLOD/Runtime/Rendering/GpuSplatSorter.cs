// SPDX-License-Identifier: MIT
// GpuSplatSorter — bitonic GPU sort over a CONTIGUOUS SUBRANGE of an index/key buffer
// pair, descending by key. Backed by Shaders/Resources/GaussianLOD/SplatSort.compute.
// Threadgroup size is selected at compile time via SORT_METAL (32) or SORT_VULKAN (64);
// PlatformCapabilityChecker enables the appropriate global keyword at startup.
//
// In the LOD-bucketed runtime architecture (ARCHITECTURE.md §2b) this sorter is dispatched
// over the visible-cluster *centers* each frame to produce a back-to-front order used by
// LODBudgetManager for nearest-first budget filling. It is NEVER called over the full
// per-splat buffer — only over a small subrange (≤ visible cluster count, typically <2K).

using System;
using GaussianLOD.Runtime.Util;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianLOD.Runtime.Rendering
{
    public sealed class GpuSplatSorter : IDisposable
    {
        readonly ComputeShader m_Shader;
        readonly int m_Kernel;
        readonly int m_ThreadgroupSize;

        static readonly int s_SortIndices    = Shader.PropertyToID("_SortIndices");
        static readonly int s_SortKeys       = Shader.PropertyToID("_SortKeys");
        static readonly int s_SortRangeStart = Shader.PropertyToID("_SortRangeStart");
        static readonly int s_SortRangeCount = Shader.PropertyToID("_SortRangeCount");
        static readonly int s_SortK          = Shader.PropertyToID("_SortK");
        static readonly int s_SortJ          = Shader.PropertyToID("_SortJ");
        static readonly int s_SortDescending = Shader.PropertyToID("_SortDescending");

        public GpuSplatSorter()
        {
            if (!PlatformCapabilityChecker.IsInitialized)
                throw new InvalidOperationException(
                    "GpuSplatSorter requires PlatformCapabilityChecker.Initialize() first.");
            m_Shader = ComputeShaderCache.GetShader(ComputeShaderCache.kSplatSort);
            m_Kernel = ComputeShaderCache.GetKernel(
                ComputeShaderCache.kSplatSort, ComputeShaderCache.kKernelBitonicSort);
            m_ThreadgroupSize = PlatformCapabilityChecker.SortThreadgroupSize;
        }

        /// <summary>
        /// Sort the contiguous subrange [<paramref name="rangeStart"/>, rangeStart+rangeCount)
        /// of <paramref name="indexBuffer"/> and <paramref name="keyBuffer"/> in place,
        /// back-to-front (descending key). Indices outside the range are not touched.
        /// Correct for non-power-of-two <paramref name="rangeCount"/>; the kernel uses
        /// virtual padding so out-of-range slots compare as the worst-case key.
        /// </summary>
        public void Sort(CommandBuffer cmd, ComputeBuffer indexBuffer, ComputeBuffer keyBuffer,
            uint rangeStart, uint rangeCount)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            if (indexBuffer == null) throw new ArgumentNullException(nameof(indexBuffer));
            if (keyBuffer == null) throw new ArgumentNullException(nameof(keyBuffer));
            if (rangeCount <= 1) return;

            // Round up to next power of two for bitonic stages.
            uint padded = 1;
            while (padded < rangeCount) padded <<= 1;

            cmd.SetComputeBufferParam(m_Shader, m_Kernel, s_SortIndices, indexBuffer);
            cmd.SetComputeBufferParam(m_Shader, m_Kernel, s_SortKeys, keyBuffer);
            cmd.SetComputeIntParam(m_Shader, s_SortRangeStart, (int)rangeStart);
            cmd.SetComputeIntParam(m_Shader, s_SortRangeCount, (int)rangeCount);
            cmd.SetComputeIntParam(m_Shader, s_SortDescending, 1);

            int groups = (int)((padded + (uint)m_ThreadgroupSize - 1) / (uint)m_ThreadgroupSize);

            // Standard two-phase bitonic sort: outer k = 2,4,...,padded; inner j = k/2,...,1
            for (uint k = 2; k <= padded; k <<= 1)
            {
                for (uint j = k >> 1; j > 0; j >>= 1)
                {
                    cmd.SetComputeIntParam(m_Shader, s_SortK, (int)k);
                    cmd.SetComputeIntParam(m_Shader, s_SortJ, (int)j);
                    cmd.DispatchCompute(m_Shader, m_Kernel, groups, 1, 1);
                }
            }
        }

        public void Dispose() { /* no owned GPU resources */ }
    }
}
