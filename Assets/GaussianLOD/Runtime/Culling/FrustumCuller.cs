// SPDX-License-Identifier: MIT
// FrustumCuller — dispatches SplatCull.compute CSFrustumCull, testing every cluster's
// AABB against six frustum planes. Output is a per-cluster visibility mask in
// CullingResultBuffer. Zero per-frame allocation.

using System;
using GaussianLOD.Runtime.Util;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianLOD.Runtime.Culling
{
    public sealed class FrustumCuller : IDisposable
    {
        // 6 planes × float4 = 24 floats
        const int kPlanesFloatCount = 24;

        readonly ComputeShader m_Shader;
        readonly int m_Kernel;
        readonly NativeBufferPool m_Pool;
        readonly CullingResultBuffer m_Result;

        // Reused per-dispatch scratch — never reallocated.
        readonly float[] m_PlaneScratch = new float[kPlanesFloatCount];

        // Cached property IDs to avoid string lookups per frame.
        static readonly int s_ClustersId = Shader.PropertyToID("_Clusters");
        static readonly int s_FrustumPlanesId = Shader.PropertyToID("_FrustumPlanes");
        static readonly int s_ClusterCountId = Shader.PropertyToID("_ClusterCount");
        static readonly int s_VisibleMaskId = Shader.PropertyToID("_VisibleMask");

        public FrustumCuller(NativeBufferPool pool, CullingResultBuffer result)
        {
            m_Pool = pool ?? throw new ArgumentNullException(nameof(pool));
            m_Result = result ?? throw new ArgumentNullException(nameof(result));
            m_Shader = ComputeShaderCache.GetShader(ComputeShaderCache.kSplatCull);
            m_Kernel = ComputeShaderCache.GetKernel(
                ComputeShaderCache.kSplatCull, ComputeShaderCache.kKernelFrustumCull);
        }

        /// <summary>
        /// Run the cull. <paramref name="planes"/> must contain exactly 6 planes (the order
        /// produced by <c>GeometryUtility.CalculateFrustumPlanes</c> or
        /// <c>StereoFrustumMerger</c>). Caller retains ownership of the NativeArray.
        /// </summary>
        public void Dispatch(CommandBuffer cmd, NativeArray<Plane> planes, int clusterCount)
        {
            if (planes.Length != 6) throw new ArgumentException("Expected 6 frustum planes.", nameof(planes));
            if (clusterCount <= 0) return;
            if (clusterCount > m_Result.ClusterCapacity)
                throw new ArgumentOutOfRangeException(nameof(clusterCount),
                    $"clusterCount {clusterCount} exceeds CullingResultBuffer capacity {m_Result.ClusterCapacity}");

            for (int i = 0; i < 6; ++i)
            {
                Plane p = planes[i];
                m_PlaneScratch[i * 4 + 0] = p.normal.x;
                m_PlaneScratch[i * 4 + 1] = p.normal.y;
                m_PlaneScratch[i * 4 + 2] = p.normal.z;
                m_PlaneScratch[i * 4 + 3] = p.distance;
            }

            cmd.SetComputeBufferParam(m_Shader, m_Kernel, s_ClustersId, m_Pool.ClusterMetadataBuffer);
            cmd.SetComputeBufferParam(m_Shader, m_Kernel, s_VisibleMaskId, m_Result.VisibleMaskBuffer);
            cmd.SetComputeFloatParams(m_Shader, s_FrustumPlanesId, m_PlaneScratch);
            cmd.SetComputeIntParam(m_Shader, s_ClusterCountId, clusterCount);

            int groups = (clusterCount + 63) / 64;
            cmd.DispatchCompute(m_Shader, m_Kernel, groups, 1, 1);
        }

        public void Dispose()
        {
            // No owned GPU resources — pool and result buffer are owned elsewhere.
        }
    }
}
