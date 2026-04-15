// SPDX-License-Identifier: MIT
// GpuSplatSorter — primary consumer of the fork's GaussianSplatRenderer extension API
// (ARCHITECTURE.md §8). Subscribes to BeforeSort and, each sort-frame, writes a
// filtered back-to-front splat-index list into renderer.gpuSortKeys via
// SplatSort.compute's CSBuildFilteredIndices kernel. Suppresses Aras's internal sort
// with skipInternalSort = true and drives DrawProcedural's instance count via
// renderer.activeSplatCount (read from a 1-frame-delayed AsyncGPUReadback of the
// per-frame filtered count).
//
// Fallback behavior: if Init() is called with a null renderer or the renderer lacks
// the extension API (gpuSortKeys is null), BeforeSort is NOT subscribed. The legacy
// bitonic Sort() method still works over the pool's SortIndex/SortKey buffers — that
// is what LODBudgetManager uses to order visible clusters back-to-front.

using System;
using GaussianLOD.Runtime.Clustering;
using GaussianLOD.Runtime.LOD;
using GaussianLOD.Runtime.Util;
using GaussianSplatting.Runtime;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianLOD.Runtime.Rendering
{
    public sealed class GpuSplatSorter : IDisposable
    {
        // ---- Legacy bitonic-sort kernel (still used by LODBudgetManager) -------------------
        readonly ComputeShader m_SortShader;
        readonly int m_SortKernel;
        readonly int m_ThreadgroupSize;

        static readonly int s_SortIndices    = Shader.PropertyToID("_SortIndices");
        static readonly int s_SortKeys       = Shader.PropertyToID("_SortKeys");
        static readonly int s_SortRangeStart = Shader.PropertyToID("_SortRangeStart");
        static readonly int s_SortRangeCount = Shader.PropertyToID("_SortRangeCount");
        static readonly int s_SortK          = Shader.PropertyToID("_SortK");
        static readonly int s_SortJ          = Shader.PropertyToID("_SortJ");
        static readonly int s_SortDescending = Shader.PropertyToID("_SortDescending");

        // ---- Filtered-indices kernel (the new primary path) -------------------------------
        readonly int m_FilterKernel;

        static readonly int s_Clusters           = Shader.PropertyToID("_Clusters");
        static readonly int s_VisibleMask        = Shader.PropertyToID("_VisibleMask");
        static readonly int s_LodLevels          = Shader.PropertyToID("_LodLevels");
        static readonly int s_AllSplatIndices    = Shader.PropertyToID("_AllSplatIndices");
        static readonly int s_FilteredIndices    = Shader.PropertyToID("_FilteredIndices");
        static readonly int s_FilteredCount      = Shader.PropertyToID("_FilteredCount");
        static readonly int s_ClusterCount       = Shader.PropertyToID("_ClusterCount");
        static readonly int s_MaxFilteredIndices = Shader.PropertyToID("_MaxFilteredIndices");

        // ---- Subscribed state -------------------------------------------------------------
        GaussianSplatRenderer m_Renderer;
        NativeBufferPool m_Pool;
        LODSelector m_Selector;
        int m_ClusterCount;

        // Owned GPU resources (lifetime = sorter's lifetime, sized from the asset)
        ComputeBuffer m_AllSplatIndicesBuffer;
        ComputeBuffer m_FilteredCountBuffer;

        // CPU scratch for per-frame byte→uint copy of LODSelector.VisibleFlags
        uint[] m_VisibleScratch;
        readonly uint[] m_CountClear = new uint[1] { 0u };

        // Cached filtered count from the previous frame's AsyncGPUReadback. -1 = "draw all"
        // which is the safe default Aras already honors.
        int m_CachedFilteredCount = -1;
        bool m_ReadbackInFlight;
        bool m_Subscribed;
        bool m_Disposed;

        public bool ExtensionPathActive => m_Subscribed;
        public int LastFilteredCount => m_CachedFilteredCount;

        public GpuSplatSorter()
        {
            if (!PlatformCapabilityChecker.IsInitialized)
                throw new InvalidOperationException(
                    "GpuSplatSorter requires PlatformCapabilityChecker.Initialize() first.");

            m_SortShader = ComputeShaderCache.GetShader(ComputeShaderCache.kSplatSort);
            m_SortKernel = ComputeShaderCache.GetKernel(
                ComputeShaderCache.kSplatSort, ComputeShaderCache.kKernelBitonicSort);
            m_FilterKernel = ComputeShaderCache.GetKernel(
                ComputeShaderCache.kSplatSort, ComputeShaderCache.kKernelBuildFilteredIndices);
            m_ThreadgroupSize = PlatformCapabilityChecker.SortThreadgroupSize;
        }

        /// <summary>
        /// Wire the sorter to a specific GaussianSplatRenderer and the cluster/index data
        /// it should filter against. Subscribes to renderer.BeforeSort. Safe to call with
        /// a null renderer — in that case the sorter remains in legacy-only mode and its
        /// BeforeSort event is never subscribed.
        /// </summary>
        public void Init(
            GaussianSplatRenderer renderer,
            SplatClusterIndexAsset indexAsset,
            NativeBufferPool pool,
            LODSelector selector,
            int clusterCount)
        {
            if (m_Subscribed)
                throw new InvalidOperationException("GpuSplatSorter already initialized.");
            if (pool == null) throw new ArgumentNullException(nameof(pool));
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            if (clusterCount <= 0) throw new ArgumentOutOfRangeException(nameof(clusterCount));

            // Extension API not available → fallback mode. Log once and bail.
            if (renderer == null || indexAsset == null || indexAsset.allIndices == null ||
                indexAsset.allIndices.Length == 0)
            {
                Debug.LogWarning(
                    "[GaussianLOD] GpuSplatSorter: extension-API path disabled " +
                    "(missing renderer or SplatClusterIndexAsset). " +
                    "Aras will draw the full asset; LOD culling has no GPU effect this session.");
                return;
            }

            m_Renderer = renderer;
            m_Pool = pool;
            m_Selector = selector;
            m_ClusterCount = clusterCount;
            m_VisibleScratch = new uint[clusterCount];

            // Upload the immutable index array once.
            int[] allIndices = indexAsset.allIndices;
            m_AllSplatIndicesBuffer = new ComputeBuffer(
                allIndices.Length, sizeof(int), ComputeBufferType.Structured)
                { name = "GLOD_AllSplatIndices" };
            m_AllSplatIndicesBuffer.SetData(allIndices);

            // Single-uint counter; raw-typed so we can SetData a 1-element uint[] as clear.
            m_FilteredCountBuffer = new ComputeBuffer(
                1, sizeof(uint), ComputeBufferType.Structured)
                { name = "GLOD_FilteredCount" };

            m_Renderer.BeforeSort += OnBeforeSort;
            m_Subscribed = true;
        }

        void OnBeforeSort(CommandBuffer cmd, Camera cam)
        {
            if (m_Disposed || m_Renderer == null) return;
            if (!m_Renderer.HasValidRenderSetup) return;

            var dstKeys = m_Renderer.gpuSortKeys;
            if (dstKeys == null) return;

            // --- Upload per-frame cluster state (visibility + LOD levels) ------------------
            //
            // LODSelector.VisibleFlags is NativeArray<byte>; the GPU buffer is uint-stride.
            // Convert on CPU (tight loop; ≤16K clusters, < 0.1 ms).
            var visibleFlags = m_Selector.VisibleFlags;
            var lodLevels = m_Selector.LodLevels;
            int n = m_ClusterCount;
            if (visibleFlags.Length < n || lodLevels.Length < n)
                return; // selector hasn't run yet this frame

            for (int i = 0; i < n; ++i)
                m_VisibleScratch[i] = visibleFlags[i];

            cmd.SetBufferData(m_Pool.VisibleMaskBuffer, m_VisibleScratch, 0, 0, n);
            cmd.SetBufferData(m_Pool.LodLevelBuffer, lodLevels, 0, 0, n);

            // Clear the filtered-count counter to 0 before the dispatch.
            cmd.SetBufferData(m_FilteredCountBuffer, m_CountClear, 0, 0, 1);

            // --- Dispatch CSBuildFilteredIndices ------------------------------------------
            cmd.SetComputeBufferParam(m_SortShader, m_FilterKernel, s_Clusters,           m_Pool.ClusterMetadataBuffer);
            cmd.SetComputeBufferParam(m_SortShader, m_FilterKernel, s_VisibleMask,        m_Pool.VisibleMaskBuffer);
            cmd.SetComputeBufferParam(m_SortShader, m_FilterKernel, s_LodLevels,          m_Pool.LodLevelBuffer);
            cmd.SetComputeBufferParam(m_SortShader, m_FilterKernel, s_AllSplatIndices,    m_AllSplatIndicesBuffer);
            cmd.SetComputeBufferParam(m_SortShader, m_FilterKernel, s_FilteredIndices,    dstKeys);
            cmd.SetComputeBufferParam(m_SortShader, m_FilterKernel, s_FilteredCount,      m_FilteredCountBuffer);
            cmd.SetComputeIntParam   (m_SortShader, s_ClusterCount,       n);
            cmd.SetComputeIntParam   (m_SortShader, s_MaxFilteredIndices, dstKeys.count);

            int groups = (n + 63) / 64;
            cmd.DispatchCompute(m_SortShader, m_FilterKernel, groups, 1, 1);

            // --- AsyncGPUReadback of the count (1-frame delay pattern) ---------------------
            if (!m_ReadbackInFlight)
            {
                m_ReadbackInFlight = true;
                cmd.RequestAsyncReadback(m_FilteredCountBuffer, OnCountReadback);
            }

            // --- Drive Aras's draw path ---------------------------------------------------
            // Tell Aras to skip its internal CalcDistances + radix sort — we own gpuSortKeys.
            m_Renderer.skipInternalSort = true;
            // Use the previous frame's count. -1 (first frame only) means "draw all".
            m_Renderer.activeSplatCount = m_CachedFilteredCount;
        }

        void OnCountReadback(AsyncGPUReadbackRequest req)
        {
            m_ReadbackInFlight = false;
            if (m_Disposed) return;
            if (req.hasError) return;

            var data = req.GetData<uint>();
            if (data.Length == 0) return;
            uint count = data[0];
            // Clamp against renderer's splat count and int range. If we ever overflow the
            // destination buffer we already clamped inside the kernel; this is defensive.
            if (count > int.MaxValue) count = (uint)int.MaxValue;
            m_CachedFilteredCount = (int)count;
        }

        // ---- Legacy bitonic API (unchanged; still used by LODBudgetManager) --------------

        /// <summary>
        /// Sort the contiguous subrange [<paramref name="rangeStart"/>, rangeStart+rangeCount)
        /// of <paramref name="indexBuffer"/> and <paramref name="keyBuffer"/> in place,
        /// back-to-front (descending key). Non-power-of-two counts are handled via virtual
        /// padding; indices outside the range are not touched.
        /// </summary>
        public void Sort(CommandBuffer cmd, ComputeBuffer indexBuffer, ComputeBuffer keyBuffer,
            uint rangeStart, uint rangeCount)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            if (indexBuffer == null) throw new ArgumentNullException(nameof(indexBuffer));
            if (keyBuffer == null) throw new ArgumentNullException(nameof(keyBuffer));
            if (rangeCount <= 1) return;

            uint padded = 1;
            while (padded < rangeCount) padded <<= 1;

            cmd.SetComputeBufferParam(m_SortShader, m_SortKernel, s_SortIndices, indexBuffer);
            cmd.SetComputeBufferParam(m_SortShader, m_SortKernel, s_SortKeys, keyBuffer);
            cmd.SetComputeIntParam(m_SortShader, s_SortRangeStart, (int)rangeStart);
            cmd.SetComputeIntParam(m_SortShader, s_SortRangeCount, (int)rangeCount);
            cmd.SetComputeIntParam(m_SortShader, s_SortDescending, 1);

            int groups = (int)((padded + (uint)m_ThreadgroupSize - 1) / (uint)m_ThreadgroupSize);

            for (uint k = 2; k <= padded; k <<= 1)
            {
                for (uint j = k >> 1; j > 0; j >>= 1)
                {
                    cmd.SetComputeIntParam(m_SortShader, s_SortK, (int)k);
                    cmd.SetComputeIntParam(m_SortShader, s_SortJ, (int)j);
                    cmd.DispatchCompute(m_SortShader, m_SortKernel, groups, 1, 1);
                }
            }
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;

            if (m_Renderer != null && m_Subscribed)
            {
                m_Renderer.BeforeSort -= OnBeforeSort;
                // Best-effort revert: allow Aras to own the draw again if this renderer
                // lives past our lifetime (e.g. domain reload without scene unload).
                m_Renderer.skipInternalSort = false;
                m_Renderer.activeSplatCount = -1;
            }
            m_Subscribed = false;

            m_AllSplatIndicesBuffer?.Dispose(); m_AllSplatIndicesBuffer = null;
            m_FilteredCountBuffer?.Dispose();   m_FilteredCountBuffer = null;
            m_VisibleScratch = null;
            m_Renderer = null;
            m_Pool = null;
            m_Selector = null;
        }
    }
}
