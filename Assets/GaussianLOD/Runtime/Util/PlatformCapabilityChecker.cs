// SPDX-License-Identifier: MIT
// PlatformCapabilityChecker — the ONLY class allowed to branch on platform / GPU backend.
// Detects target platform, picks sort threadgroup keyword, derives default LOD splat budget.
// Static. No MonoBehaviour. Initialize() is called explicitly by GaussianLODController.Awake().

using UnityEngine;
using UnityEngine.Rendering;
#if GLOD_ENABLE_XR
using UnityEngine.XR;
#endif

namespace GaussianLOD.Runtime.Util
{
    /// <summary>
    /// Single source of truth for platform-conditional behavior across the package.
    /// Sets the global SORT_METAL / SORT_VULKAN shader keyword and exposes the
    /// recommended per-platform splat budget.
    /// </summary>
    public static class PlatformCapabilityChecker
    {
        public enum SplatPlatform
        {
            Unknown,
            AppleVisionProStreamed,
            QuestAndroidVulkan,
            PCDeveloperALVR,
            PCStandalone,
            EditorDesktop
        }

        public enum GpuBackend
        {
            Unknown,
            Metal,
            Vulkan,
            D3D12,
            D3D11
        }

        // Platform splat budgets (defaults; user may override on LODBudgetManager).
        public const int kBudgetAVP = 120_000;
        public const int kBudgetQuest3S = 80_000;
        public const int kBudgetPCALVR = 200_000;
        public const int kBudgetEditor = 250_000;

        // Shader keywords (global). Mutually exclusive — one is enabled, the other disabled.
        public const string kKeywordSortMetal = "SORT_METAL";
        public const string kKeywordSortVulkan = "SORT_VULKAN";

        public static SplatPlatform Platform { get; private set; } = SplatPlatform.Unknown;
        public static GpuBackend Backend { get; private set; } = GpuBackend.Unknown;
        public static int RecommendedSplatBudget { get; private set; } = kBudgetEditor;
        public static int SortThreadgroupSize { get; private set; } = 64;
        public static bool IsInitialized { get; private set; }

        /// <summary>
        /// Detect the runtime platform and GPU backend, set the global sort keyword,
        /// and derive the recommended splat budget. Idempotent.
        /// </summary>
        public static void Initialize()
        {
            if (IsInitialized) return;

            Backend = DetectBackend();
            Platform = DetectPlatform();
            RecommendedSplatBudget = Platform switch
            {
                SplatPlatform.AppleVisionProStreamed => kBudgetAVP,
                SplatPlatform.QuestAndroidVulkan => kBudgetQuest3S,
                SplatPlatform.PCDeveloperALVR => kBudgetPCALVR,
                SplatPlatform.PCStandalone => kBudgetPCALVR,
                SplatPlatform.EditorDesktop => kBudgetEditor,
                _ => kBudgetEditor
            };

            // Metal needs smaller threadgroups for Apple silicon SIMD efficiency
            // (Apple GPUs have 32-thread SIMD groups). Vulkan/D3D prefer 64.
            if (Backend == GpuBackend.Metal)
            {
                SortThreadgroupSize = 32;
                Shader.EnableKeyword(kKeywordSortMetal);
                Shader.DisableKeyword(kKeywordSortVulkan);
            }
            else
            {
                SortThreadgroupSize = 64;
                Shader.EnableKeyword(kKeywordSortVulkan);
                Shader.DisableKeyword(kKeywordSortMetal);
            }

            IsInitialized = true;

            Debug.Log($"[GaussianLOD] Platform={Platform} Backend={Backend} " +
                      $"Budget={RecommendedSplatBudget} SortTG={SortThreadgroupSize}");
        }

        /// <summary>Force a re-detection. Test-only.</summary>
        public static void ResetForTests()
        {
            IsInitialized = false;
            Platform = SplatPlatform.Unknown;
            Backend = GpuBackend.Unknown;
            RecommendedSplatBudget = kBudgetEditor;
            SortThreadgroupSize = 64;
        }

        static GpuBackend DetectBackend()
        {
            return SystemInfo.graphicsDeviceType switch
            {
                GraphicsDeviceType.Metal => GpuBackend.Metal,
                GraphicsDeviceType.Vulkan => GpuBackend.Vulkan,
                GraphicsDeviceType.Direct3D12 => GpuBackend.D3D12,
                GraphicsDeviceType.Direct3D11 => GpuBackend.D3D11,
                _ => GpuBackend.Unknown
            };
        }

        static SplatPlatform DetectPlatform()
        {
            // Apple Vision Pro reports VisionOS at runtime; in editor we land on EditorDesktop.
#if UNITY_VISIONOS && !UNITY_EDITOR
            return SplatPlatform.AppleVisionProStreamed;
#elif UNITY_ANDROID && !UNITY_EDITOR
            // Quest 3S is Android+Vulkan; if we're on Android with Vulkan we assume a Meta HMD.
            return SplatPlatform.QuestAndroidVulkan;
#elif UNITY_EDITOR
            return SplatPlatform.EditorDesktop;
#else
            // Standalone PC. Detect ALVR/SteamVR via the active OpenXR runtime name when present.
            if (IsALVRorSteamVRActive())
                return SplatPlatform.PCDeveloperALVR;
            return SplatPlatform.PCStandalone;
#endif
        }

        static bool IsALVRorSteamVRActive()
        {
#if GLOD_ENABLE_XR
            // XRSettings.loadedDeviceName is legacy but still works in Unity 6 for OpenXR.
            // Cheap, allocation-free, sufficient to distinguish ALVR/SteamVR from null.
            string n = XRSettings.loadedDeviceName;
            if (string.IsNullOrEmpty(n)) return false;
            n = n.ToLowerInvariant();
            return n.Contains("openxr") || n.Contains("steamvr") || n.Contains("alvr");
#else
            return false;
#endif
        }
    }
}
