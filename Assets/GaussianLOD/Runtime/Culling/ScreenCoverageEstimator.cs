// SPDX-License-Identifier: MIT
// ScreenCoverageEstimator — dispatches SplatCull.compute CSCoverageEstimate, computing
// an approximate screen-coverage value per visible cluster (solid-angle approximation:
// coverage ≈ clusterRadius² / distance²). Only writes to clusters whose visible-mask
// bit is set. Zero per-frame allocation.

using System;
using GaussianLOD.Runtime.Util;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianLOD.Runtime.Culling
{
    public sealed class ScreenCoverageEstimator : IDisposable
    {
        readonly ComputeShader m_Shader;
        readonly int m_Kernel;
        readonly NativeBufferPool m_Pool;
        readonly CullingResultBuffer m_VisibleResult;

        static readonly int s_VisibleMaskId = Shader.PropertyToID("_VisibleMask");
        static readonly int s_ClustersId = Shader.PropertyToID("_Clusters");
        static readonly int s_CameraPositionId = Shader.PropertyToID("_CameraPosition");
        static readonly int s_CoverageId = Shader.PropertyToID("_Coverage");
        static readonly int s_ClusterCountId = Shader.PropertyToID("_ClusterCount");

        public ComputeBuffer CoverageBuffer => m_Pool.CoverageBuffer;

        public ScreenCoverageEstimator(NativeBufferPool pool, CullingResultBuffer visibleResult)
        {
            m_Pool = pool ?? throw new ArgumentNullException(nameof(pool));
            m_VisibleResult = visibleResult ?? throw new ArgumentNullException(nameof(visibleResult));
            m_Shader = ComputeShaderCache.GetShader(ComputeShaderCache.kSplatCull);
            m_Kernel = ComputeShaderCache.GetKernel(
                ComputeShaderCache.kSplatCull, ComputeShaderCache.kKernelCoverageEstimate);
        }

        public void Dispatch(CommandBuffer cmd, Vector3 cameraWorldPosition, int clusterCount)
        {
            if (clusterCount <= 0) return;

            cmd.SetComputeBufferParam(m_Shader, m_Kernel, s_VisibleMaskId, m_VisibleResult.VisibleMaskBuffer);
            cmd.SetComputeBufferParam(m_Shader, m_Kernel, s_ClustersId, m_Pool.ClusterMetadataBuffer);
            cmd.SetComputeBufferParam(m_Shader, m_Kernel, s_CoverageId, m_Pool.CoverageBuffer);
            cmd.SetComputeVectorParam(m_Shader, s_CameraPositionId,
                new Vector4(cameraWorldPosition.x, cameraWorldPosition.y, cameraWorldPosition.z, 0f));
            cmd.SetComputeIntParam(m_Shader, s_ClusterCountId, clusterCount);

            int groups = (clusterCount + 63) / 64;
            cmd.DispatchCompute(m_Shader, m_Kernel, groups, 1, 1);
        }

        public void Dispose() { /* no owned resources */ }
    }
}
