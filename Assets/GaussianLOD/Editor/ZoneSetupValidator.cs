// SPDX-License-Identifier: MIT
// ZoneSetupValidator — menu-driven sanity check for multi-zone setups. Verifies
// cluster assets are assigned and unique, StereoCameraRig singleton won't conflict,
// and SpatialZoneManager presence when multiple controllers exist.

#if UNITY_EDITOR
using System.Collections.Generic;
using GaussianLOD.Runtime;
using GaussianLOD.Runtime.Clustering;
using GaussianLOD.Runtime.Zones;
using UnityEditor;
using UnityEngine;

namespace GaussianLOD.Editor
{
    public static class ZoneSetupValidator
    {
        [MenuItem("GaussianLOD/Validate Zone Setup")]
        public static void Validate()
        {
            var controllers = Object.FindObjectsByType<GaussianLODController>(FindObjectsSortMode.None);
            int errors = 0;
            int warnings = 0;

            // 1. Check SpatialZoneManager presence when multiple controllers exist.
            if (controllers.Length > 1)
            {
                var manager = Object.FindFirstObjectByType<SpatialZoneManager>();
                if (manager == null)
                {
                    Debug.LogWarning("[GaussianLOD] Multiple GaussianLODControllers found but no SpatialZoneManager. " +
                                     "Add a SpatialZoneManager to coordinate budget splitting.");
                    warnings++;
                }
            }

            if (controllers.Length == 0)
            {
                Debug.Log("[GaussianLOD] No GaussianLODControllers in scene. Nothing to validate.");
                EditorUtility.DisplayDialog("GaussianLOD", "No controllers found in scene.", "OK");
                return;
            }

            // 2. Check each controller has a valid SplatClusterAsset.
            var seenAssets = new HashSet<SplatClusterAsset>();
            for (int i = 0; i < controllers.Length; ++i)
            {
                var ctrl = controllers[i];
                var asset = ctrl.ClusterAsset;
                if (asset == null)
                {
                    Debug.LogError($"[GaussianLOD] Controller '{ctrl.name}' has no SplatClusterAsset assigned.");
                    errors++;
                    continue;
                }

                // 3. Check no two controllers share the same asset.
                if (!seenAssets.Add(asset))
                {
                    Debug.LogError($"[GaussianLOD] Controller '{ctrl.name}' shares SplatClusterAsset '{asset.name}' " +
                                   "with another controller. Each zone must use a unique cluster asset.");
                    errors++;
                }
            }

            // 4. Warn if multiple XR rig references found (potential StereoCameraRig conflict).
#if GLOD_ENABLE_XR
            var xrOrigins = Object.FindObjectsByType<Unity.XR.CoreUtils.XROrigin>(FindObjectsSortMode.None);
            if (xrOrigins != null && xrOrigins.Length > 1)
            {
                Debug.LogWarning($"[GaussianLOD] {xrOrigins.Length} XROrigin components found. " +
                                 "StereoCameraRig singleton shares one camera — multiple XR rigs may cause issues.");
                warnings++;
            }
#endif

            // Summary.
            string summary = errors == 0 && warnings == 0
                ? $"Zone setup valid. {controllers.Length} controller(s) checked."
                : $"{errors} error(s), {warnings} warning(s). See Console.";
            Debug.Log($"[GaussianLOD] Zone validation: {summary}");
            EditorUtility.DisplayDialog("GaussianLOD", summary, "OK");
        }
    }
}
#endif
