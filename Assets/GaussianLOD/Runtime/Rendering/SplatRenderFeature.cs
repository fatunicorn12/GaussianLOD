// SPDX-License-Identifier: MIT
// SplatRenderFeature — Unity 6 URP RenderGraph feature that participates in the per-frame
// pipeline alongside Aras's GaussianSplatURPFeature. This feature does NOT draw splats —
// see ARCHITECTURE.md §8. Its responsibilities are:
//
//   1. Log a clear ordering reminder on Create() (the #1 setup mistake — see SETUP.md).
//   2. Insert a tiny named profiling pass at BeforeRenderingTransparents so the
//      frame debugger shows where our LOD decision lives in the timeline.
//
// Under the new per-cluster architecture the real GPU work — dispatching
// CSBuildFilteredIndices and writing activeSplatCount — happens on the
// GaussianSplatRenderer.BeforeSort event inside GpuSplatSorter, NOT in this URP pass.
// Keeping this feature is still worthwhile for the frame-debugger marker and so
// users have a stable place to wire debug overlays via the AfterViewData event.
//
// **CRITICAL SETUP**: This feature MUST be ordered ABOVE Aras's GaussianSplatURPFeature
// in the URP renderer asset's feature list. SETUP.md repeats this in a warning block.

#if GLOD_ENABLE_URP
#if !UNITY_6000_0_OR_NEWER
#error GaussianLOD.SplatRenderFeature requires Unity 6 (RenderGraph URP API)
#endif

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

namespace GaussianLOD.Runtime.Rendering
{
    public sealed class SplatRenderFeature : ScriptableRendererFeature
    {
        const string kProfilerTag = "GaussianLOD.LODDecision";
        static readonly ProfilingSampler s_Sampler = new(kProfilerTag);

        sealed class GLodPass : ScriptableRenderPass
        {
            sealed class PassData { public string tag; }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                using var builder = renderGraph.AddUnsafePass<PassData>(kProfilerTag, out var passData);
                passData.tag = kProfilerTag;

                // We have no GPU resources of our own; we're a marker pass and the optional
                // home for GPU compute dispatches if the user opts into the GPU LOD path.
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext ctx) =>
                {
                    var cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
                    using var _ = new ProfilingScope(cmd, s_Sampler);
                    // Intentionally empty: the CPU LOD pipeline runs in
                    // GaussianLODController.LateUpdate and the GPU filter dispatch runs
                    // on GaussianSplatRenderer.BeforeSort inside GpuSplatSorter. This
                    // pass is a stable marker so users can verify ordering relative to
                    // GaussianSplatURPFeature in the frame debugger.
                });
            }
        }

        GLodPass m_Pass;

        public override void Create()
        {
            m_Pass = new GLodPass
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
            };

            Debug.Log(
                "[GaussianLOD] SplatRenderFeature created. " +
                "REMINDER: This feature MUST be ordered ABOVE 'GaussianSplatURPFeature' " +
                "in your URP renderer asset's feature list. If GaussianSplat draws happen " +
                "before our LOD decision runs, the wrong bucket will render. See SETUP.md.");
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (m_Pass != null)
                renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            m_Pass = null;
        }
    }
}
#endif // GLOD_ENABLE_URP
