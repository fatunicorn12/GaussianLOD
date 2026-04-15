// SPDX-License-Identifier: MIT
// ComputeShaderCache — central registry for the four GaussianLOD compute shaders and
// their kernel handles. Caches kernel indices by name to avoid per-frame string lookups.
// Loaded once at startup by GaussianLODController, which may pass serialized references
// (preferred) or fall back to Resources.Load.

using System.Collections.Generic;
using UnityEngine;

namespace GaussianLOD.Runtime.Util
{
    public static class ComputeShaderCache
    {
        // Canonical shader names — must match file names AND the keys returned by GetShader.
        public const string kSplatCull = "SplatCull";
        public const string kSplatSort = "SplatSort";
        public const string kSplatLODSelect = "SplatLODSelect";
        public const string kMegaSplatBlit = "MegaSplatBlit";

        // Canonical kernel names — must match HLSL [numthreads]/kernel declarations.
        public const string kKernelFrustumCull = "CSFrustumCull";
        public const string kKernelCoverageEstimate = "CSCoverageEstimate";
        public const string kKernelBitonicSort = "CSBitonicSort";
        public const string kKernelBuildFilteredIndices = "CSBuildFilteredIndices";
        public const string kKernelLODSelect = "CSLODSelect";
        public const string kKernelMegaSplatBlit = "CSMegaSplatBlit";

        static readonly Dictionary<string, ComputeShader> s_Shaders = new();
        static readonly Dictionary<(string, string), int> s_Kernels = new();
        static bool s_Initialized;

        public static bool IsInitialized => s_Initialized;

        /// <summary>
        /// Initialize the cache from a small set of (name, ComputeShader) pairs.
        /// Pass serialized references from GaussianLODController. If a pair has a null
        /// shader, it falls back to Resources.Load("GaussianLOD/&lt;name&gt;").
        /// Calling Initialize a second time clears and rebuilds the cache.
        /// </summary>
        public static void Initialize(IEnumerable<(string name, ComputeShader shader)> shaders)
        {
            s_Shaders.Clear();
            s_Kernels.Clear();
            s_Initialized = false;

            foreach (var (name, shader) in shaders)
            {
                if (string.IsNullOrEmpty(name))
                    throw new System.ArgumentException("ComputeShaderCache: empty shader name.");

                ComputeShader cs = shader;
                if (cs == null)
                    cs = Resources.Load<ComputeShader>($"GaussianLOD/{name}");
                if (cs == null)
                    throw new System.IO.FileNotFoundException(
                        $"ComputeShaderCache: compute shader '{name}' was null and " +
                        $"Resources/GaussianLOD/{name}.compute could not be loaded.");

                s_Shaders[name] = cs;
            }

            s_Initialized = true;
        }

        public static ComputeShader GetShader(string shaderName)
        {
            if (!s_Initialized)
                throw new System.InvalidOperationException(
                    "ComputeShaderCache.GetShader called before Initialize.");
            if (!s_Shaders.TryGetValue(shaderName, out var cs))
                throw new System.Collections.Generic.KeyNotFoundException(
                    $"ComputeShaderCache: shader '{shaderName}' not registered.");
            return cs;
        }

        /// <summary>
        /// Resolve and cache a kernel handle. Throws with descriptive context on miss.
        /// </summary>
        public static int GetKernel(string shaderName, string kernelName)
        {
            var key = (shaderName, kernelName);
            if (s_Kernels.TryGetValue(key, out int idx))
                return idx;

            var cs = GetShader(shaderName);
            if (!cs.HasKernel(kernelName))
                throw new System.Collections.Generic.KeyNotFoundException(
                    $"ComputeShaderCache: shader '{shaderName}' has no kernel '{kernelName}'.");

            idx = cs.FindKernel(kernelName);
            s_Kernels[key] = idx;
            return idx;
        }

        public static void Dispose()
        {
            s_Shaders.Clear();
            s_Kernels.Clear();
            s_Initialized = false;
        }
    }
}
