// SPDX-License-Identifier: MIT
// GaussianLODController — the ONE and ONLY MonoBehaviour in GaussianLOD.Runtime.
// Constructs every runtime system in Awake() via explicit `new(...)` (no GetComponent),
// runs the per-frame CPU LOD pipeline in LateUpdate(), and disposes all systems on
// OnDestroy(). All other GaussianLOD types are POCOs.

using System;
using System.Collections.Generic;
using GaussianLOD.Runtime.Clustering;
using GaussianLOD.Runtime.Culling;
using GaussianLOD.Runtime.LOD;
using GaussianLOD.Runtime.Rendering;
using GaussianLOD.Runtime.Stereo;
using GaussianLOD.Runtime.Util;
using UnityEngine;

namespace GaussianLOD.Runtime
{
    [DisallowMultipleComponent]
    public sealed class GaussianLODController : MonoBehaviour
    {
        // ---- Serialized references (the spec lists each one explicitly) -----------------------
        [Header("Baked data")]
        [SerializeField] SplatClusterAsset clusterAsset;

        [Header("Compute shaders (Resources fallback if null)")]
        [SerializeField] ComputeShader cullShader;
        [SerializeField] ComputeShader sortShader;
        [SerializeField] ComputeShader lodSelectShader;
        [SerializeField] ComputeShader megaSplatShader;

        [Header("Bucket renderers (LOD0..LOD3)")]
        [Tooltip("LOD0 (full), LOD1 (half), LOD2 (quarter), LOD3 (mega-only). " +
                 "Created by SplatClusterBaker; any may be null and the assembler will fall back.")]
        [SerializeField] GameObject lod0BucketGO;
        [SerializeField] GameObject lod1BucketGO;
        [SerializeField] GameObject lod2BucketGO;
        [SerializeField] GameObject lod3BucketGO;

        [Header("Camera")]
        [Tooltip("Camera that drives culling. If null, uses Camera.main at Awake.")]
        [SerializeField] Camera targetCamera;

        [Header("Budget override")]
        [Tooltip("If > 0, overrides PlatformCapabilityChecker's recommended splats-per-frame.")]
        [SerializeField] int splatsPerFrameOverride = 0;

        // ---- Constructed systems --------------------------------------------------------------
        NativeBufferPool m_Pool;
        StereoCameraRig m_Rig;
        StereoFrustumMerger m_Merger;
        LODSelector m_Selector;
        LODBudgetManager m_Budget;
        LODTransitionController m_Transitions;
        SplatDrawCallAssembler m_Assembler;
        // GPU-path companions, constructed and ready even though the default hot path is CPU
        CullingResultBuffer m_CullResult;
        FrustumCuller m_Culler;
        ScreenCoverageEstimator m_Coverage;
        GpuSplatSorter m_Sorter;

        bool m_Initialized;
        readonly Plane[] m_PlaneScratch = new Plane[6];

        // ---- Public surface for other systems / tests -----------------------------------------
        public SplatClusterAsset ClusterAsset => clusterAsset;
        public NativeBufferPool Pool => m_Pool;
        public LODSelector Selector => m_Selector;
        public LODBudgetManager Budget => m_Budget;
        public SplatDrawCallAssembler Assembler => m_Assembler;
        public bool IsReady => m_Initialized;

        void Awake()
        {
            try
            {
                // 1. Platform first — sets sort keyword + budget defaults.
                PlatformCapabilityChecker.Initialize();

                // 2. Compute shader cache — pass serialized refs (Resources fallback if null).
                ComputeShaderCache.Initialize(new (string, ComputeShader)[]
                {
                    (ComputeShaderCache.kSplatCull,      cullShader),
                    (ComputeShaderCache.kSplatSort,      sortShader),
                    (ComputeShaderCache.kSplatLODSelect, lodSelectShader),
                    (ComputeShaderCache.kMegaSplatBlit,  megaSplatShader),
                });

                if (clusterAsset == null)
                    throw new InvalidOperationException("GaussianLODController: clusterAsset is not assigned.");
                if (clusterAsset.clusters == null || clusterAsset.clusters.Length == 0)
                    throw new InvalidOperationException("GaussianLODController: clusterAsset has no clusters.");

                if (targetCamera == null) targetCamera = Camera.main;
                if (targetCamera == null)
                    throw new InvalidOperationException(
                        "GaussianLODController: no camera assigned and Camera.main is null.");

                // 3. Buffer pool sized for worst case.
                int clusterCount = clusterAsset.clusters.Length;
                m_Pool = new NativeBufferPool(
                    clusterCapacity: Mathf.Max(NativeBufferPool.kClusterCapacity, clusterCount),
                    sortIndexCapacity: NativeBufferPool.kSortIndexCapacity,
                    clusterDataStride: SplatClusterData.kStrideBytes);

                // Upload cluster metadata once.
                m_Pool.ClusterMetadataBuffer.SetData(clusterAsset.clusters);

                // 4. Stereo + culling stack (GPU path companions).
                m_Rig = new StereoCameraRig(targetCamera);
                m_Merger = new StereoFrustumMerger(m_Rig);
                m_CullResult = new CullingResultBuffer(m_Pool);
                m_Culler = new FrustumCuller(m_Pool, m_CullResult);
                m_Coverage = new ScreenCoverageEstimator(m_Pool, m_CullResult);

                // 5. CPU LOD stack — the runtime hot path.
                m_Selector = new LODSelector(clusterAsset.clusters);
                m_Budget = new LODBudgetManager(m_Selector, splatsPerFrameOverride);
                m_Transitions = new LODTransitionController(clusterCount);

                // 6. Sorter (GPU bitonic, used over visible-cluster centers when needed).
                m_Sorter = new GpuSplatSorter();

                // 7. Bucket assembler.
                m_Assembler = new SplatDrawCallAssembler(
                    lod0BucketGO, lod1BucketGO, lod2BucketGO, lod3BucketGO);

                m_Initialized = true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[GaussianLOD] Awake failed: {e}");
                DisposeAll();
                enabled = false;
            }
        }

        void LateUpdate()
        {
            if (!m_Initialized || targetCamera == null) return;

            // 1. Refresh stereo eye poses.
            m_Rig.Update();

            // 2. Build merged conservative frustum (caller disposes).
            var planes = m_Merger.GetMergedPlanes();
            try
            {
                for (int i = 0; i < 6; ++i) m_PlaneScratch[i] = planes[i];
            }
            finally
            {
                if (planes.IsCreated) planes.Dispose();
            }

            Vector3 camPos = m_Rig.Center.position;
            Matrix4x4 o2w = transform.localToWorldMatrix;

            // 3. CPU cull + coverage + LOD-select.
            m_Selector.Run(m_PlaneScratch, camPos, o2w);

            // 4. Budget enforcement (furthest-first demotion).
            m_Budget.Enforce(clusterAsset.clusters, camPos, o2w);

            // 5. Hysteresis (3-frame stickiness).
            m_Transitions.Smooth(m_Selector.LodLevels);

            // 6. Toggle the bucket renderer for the worst committed LOD.
            m_Assembler.Apply(m_Selector.LodLevels, m_Selector.VisibleFlags);
        }

        void OnDestroy() => DisposeAll();

        void DisposeAll()
        {
            // Reverse construction order.
            m_Assembler?.Dispose(); m_Assembler = null;
            m_Sorter?.Dispose(); m_Sorter = null;
            m_Transitions?.Dispose(); m_Transitions = null;
            m_Budget?.Dispose(); m_Budget = null;
            m_Selector?.Dispose(); m_Selector = null;
            m_Coverage?.Dispose(); m_Coverage = null;
            m_Culler?.Dispose(); m_Culler = null;
            m_CullResult?.Dispose(); m_CullResult = null;
            m_Merger?.Dispose(); m_Merger = null;
            m_Rig?.Dispose(); m_Rig = null;
            m_Pool?.Dispose(); m_Pool = null;
            ComputeShaderCache.Dispose();
            m_Initialized = false;
        }
    }
}
