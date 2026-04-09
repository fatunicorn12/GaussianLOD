// SPDX-License-Identifier: MIT
// GaussianLODValidator — menu-driven sanity check for SplatClusterAsset bakes.
// Verifies index range integrity, no degenerate clusters, index asset is wired,
// and mega-splat fields are populated. Reports a single summary dialog plus
// per-issue console errors.

#if UNITY_EDITOR
using System.Text;
using GaussianLOD.Runtime.Clustering;
using UnityEditor;
using UnityEngine;

namespace GaussianLOD.Editor
{
    public static class GaussianLODValidator
    {
        [MenuItem("GaussianLOD/Validate Selected Cluster Asset")]
        public static void ValidateSelected()
        {
            var asset = Selection.activeObject as SplatClusterAsset;
            if (asset == null)
            {
                EditorUtility.DisplayDialog("GaussianLOD", "Select a SplatClusterAsset first.", "OK");
                return;
            }
            Validate(asset);
        }

        public static bool Validate(SplatClusterAsset asset)
        {
            var sb = new StringBuilder();
            int errors = 0;

            if (asset.clusters == null || asset.clusters.Length == 0)
            { sb.AppendLine("- No clusters in asset."); errors++; }
            if (asset.indexAsset == null || asset.indexAsset.allIndices == null || asset.indexAsset.allIndices.Length == 0)
            { sb.AppendLine("- Missing or empty SplatClusterIndexAsset."); errors++; }
            if (asset.totalSplatCount <= 0)
            { sb.AppendLine("- totalSplatCount is zero."); errors++; }

            if (errors == 0)
            {
                int total = asset.totalSplatCount;
                var seen = new System.Collections.Generic.HashSet<int>();
                for (int c = 0; c < asset.clusters.Length; ++c)
                {
                    var cl = asset.clusters[c];
                    if (cl.count <= 0) { sb.AppendLine($"- Cluster {c}: degenerate (count={cl.count})."); errors++; continue; }
                    if (cl.startIndex < 0 || cl.startIndex + cl.count > asset.indexAsset.allIndices.Length)
                    { sb.AppendLine($"- Cluster {c}: index range out of bounds."); errors++; continue; }
                    if (cl.worldBounds.size.sqrMagnitude <= 0f)
                    { sb.AppendLine($"- Cluster {c}: zero-sized bounds."); errors++; }
                    if (cl.megaSplatScale <= 0f)
                    { sb.AppendLine($"- Cluster {c}: mega-splat scale not generated."); errors++; }

                    for (int k = 0; k < cl.count; ++k)
                    {
                        int idx = asset.indexAsset.allIndices[cl.startIndex + k];
                        if (idx < 0 || idx >= total)
                        { sb.AppendLine($"- Cluster {c}: index {idx} out of [0,{total})."); errors++; goto nextCluster; }
                        if (!seen.Add(idx))
                        { sb.AppendLine($"- Cluster {c}: duplicated splat index {idx}."); errors++; goto nextCluster; }
                    }
                    nextCluster: ;
                }
                if (seen.Count != total)
                    sb.AppendLine($"- Index coverage incomplete: {seen.Count}/{total} splats referenced.");
            }

            if (errors == 0)
            {
                Debug.Log($"[GaussianLOD] {asset.name}: OK ({asset.clusters.Length} clusters, {asset.totalSplatCount} splats)");
                EditorUtility.DisplayDialog("GaussianLOD", "Cluster asset is valid.", "OK");
                return true;
            }

            Debug.LogError($"[GaussianLOD] {asset.name} failed validation:\n{sb}");
            EditorUtility.DisplayDialog("GaussianLOD", $"{errors} issue(s). See Console.", "OK");
            return false;
        }
    }
}
#endif
