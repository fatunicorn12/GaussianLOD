// SPDX-License-Identifier: MIT
// SplatClusterBaker — EditorWindow that turns a GaussianSplatAsset into a baked
// SplatClusterAsset (+ SplatClusterIndexAsset) and four LOD bucket GameObjects.
//
// Workflow:
//   1. Drag-drop a GaussianSplatAsset.
//   2. Click "Bake". We CPU-decode positions (Aras's HLSL decode reproduced in
//      SplatOctreeBuilder), build an octree, generate mega-splats, and save the
//      SplatClusterAsset/SplatClusterIndexAsset pair next to the source asset.
//   3. We instantiate four GaussianSplatRenderer GameObjects (LOD0..LOD3) under a
//      parent prefab, each pointing initially at the SAME source asset. This is
//      enough to wire the runtime; the user is expected to swap LOD1..LOD3 for
//      decimated assets they author themselves (documented in SETUP.md).
//
// This file is editor-only. Heavy work runs on the main thread inside a
// progress bar — bakes are an offline step, not a hot path.

#if UNITY_EDITOR
using System.IO;
using GaussianLOD.Runtime.Clustering;
using GaussianSplatting.Runtime;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
// NOTE: m_MaxDepth/m_MinSplatsPerLeaf are exposed in the inspector but the current
// SplatOctreeBuilder.Build API uses the kMaxDepth/kMinSplatsPerLeaf constants
// internally. The fields are retained for forward-compat when Build gains parameters.

namespace GaussianLOD.Editor
{
    public sealed class SplatClusterBaker : EditorWindow
    {
        GaussianSplatAsset m_Source;
        int m_MaxDepth = SplatOctreeBuilder.kMaxDepth;
        int m_MinSplatsPerLeaf = SplatOctreeBuilder.kMinSplatsPerLeaf;

        [MenuItem("GaussianLOD/Cluster Baker")]
        public static void Open() => GetWindow<SplatClusterBaker>("GLOD Cluster Baker");

        void OnGUI()
        {
            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
            m_Source = (GaussianSplatAsset)EditorGUILayout.ObjectField(
                "GaussianSplatAsset", m_Source, typeof(GaussianSplatAsset), false);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Octree", EditorStyles.boldLabel);
            m_MaxDepth = EditorGUILayout.IntSlider("Max depth", m_MaxDepth, 1, 8);
            m_MinSplatsPerLeaf = EditorGUILayout.IntField("Min splats / leaf", m_MinSplatsPerLeaf);
            if (m_MinSplatsPerLeaf < 64) m_MinSplatsPerLeaf = 64;

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(m_Source == null))
            {
                if (GUILayout.Button("Bake", GUILayout.Height(28)))
                    Bake();
            }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "After baking, the LOD1..LOD3 child renderers point at the same source " +
                "asset as LOD0. For real GPU savings, replace them with decimated " +
                "GaussianSplatAssets you author yourself (see SETUP.md).",
                MessageType.Info);
        }

        void Bake()
        {
            // Build() handles its own progress bar + decoding internally.
            var result = SplatOctreeBuilder.Build(m_Source, showProgressBar: true);
            if (result.cancelled)
            {
                Debug.LogWarning("[GaussianLOD] Bake cancelled.");
                return;
            }

            // Re-decode positions (Build consumed and released its own copy) so the
            // mega-splat generator can pick a representative splat per cluster.
            var positions = new NativeArray<float3>(
                m_Source.splatCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            try
            {
                EditorUtility.DisplayProgressBar("GaussianLOD", "Generating mega-splats...", 0.7f);
                if (!SplatOctreeBuilder.DecodePositions(m_Source, positions, showProgressBar: false))
                {
                    Debug.LogWarning("[GaussianLOD] Mega-splat decode cancelled.");
                    return;
                }
                MegaSplatGenerator.Generate(result.clusters, result.allIndices, positions);

                EditorUtility.DisplayProgressBar("GaussianLOD", "Saving assets...", 0.9f);
                SaveAssets(result);
            }
            finally
            {
                if (positions.IsCreated) positions.Dispose();
                EditorUtility.ClearProgressBar();
            }
        }

        void SaveAssets(SplatOctreeBuilder.BuildResult r)
        {
            string srcPath = AssetDatabase.GetAssetPath(m_Source);
            string dir = Path.GetDirectoryName(srcPath);
            string baseName = Path.GetFileNameWithoutExtension(srcPath);
            string clusterPath = Path.Combine(dir, baseName + "_Clusters.asset").Replace('\\', '/');
            string indexPath = Path.Combine(dir, baseName + "_ClusterIndices.asset").Replace('\\', '/');
            string prefabPath = Path.Combine(dir, baseName + "_LODBuckets.prefab").Replace('\\', '/');

            var indexAsset = ScriptableObject.CreateInstance<SplatClusterIndexAsset>();
            indexAsset.SetIndices(r.allIndices);
            AssetDatabase.CreateAsset(indexAsset, indexPath);

            var clusterAsset = ScriptableObject.CreateInstance<SplatClusterAsset>();
            clusterAsset.SetBakedData(r.clusters, indexAsset, m_Source.splatCount, r.maxDepth, r.sceneBounds);
            AssetDatabase.CreateAsset(clusterAsset, clusterPath);

            // Build a parent prefab with four child GaussianSplatRenderer GameObjects.
            var root = new GameObject(baseName + "_LODBuckets");
            try
            {
                for (int lod = 0; lod < 4; ++lod)
                {
                    var child = new GameObject($"LOD{lod}");
                    child.transform.SetParent(root.transform, false);
                    var renderer = child.AddComponent<GaussianSplatRenderer>();
                    renderer.m_Asset = m_Source;
                    AssignShaderResources(renderer);
                    child.SetActive(lod == 0);
                }

                var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                clusterAsset.SetLodPrefabs(
                    prefab.transform.Find("LOD0")?.gameObject,
                    prefab.transform.Find("LOD1")?.gameObject,
                    prefab.transform.Find("LOD2")?.gameObject,
                    prefab.transform.Find("LOD3")?.gameObject);
                EditorUtility.SetDirty(clusterAsset);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorGUIUtility.PingObject(clusterAsset);
            Debug.Log($"[GaussianLOD] Baked {r.clusters.Length} clusters → {clusterPath}");
        }

        static void AssignShaderResources(GaussianSplatRenderer renderer)
        {
            const string kShaders = "Packages/org.nesnausk.gaussian-splatting/Shaders/";

            renderer.m_ShaderSplats      = AssetDatabase.LoadAssetAtPath<Shader>(kShaders + "RenderGaussianSplats.shader");
            renderer.m_ShaderComposite   = AssetDatabase.LoadAssetAtPath<Shader>(kShaders + "GaussianComposite.shader");
            renderer.m_ShaderDebugPoints = AssetDatabase.LoadAssetAtPath<Shader>(kShaders + "GaussianDebugRenderPoints.shader");
            renderer.m_ShaderDebugBoxes  = AssetDatabase.LoadAssetAtPath<Shader>(kShaders + "GaussianDebugRenderBoxes.shader");
            renderer.m_CSSplatUtilities  = AssetDatabase.LoadAssetAtPath<ComputeShader>(kShaders + "SplatUtilities.compute");

            if (renderer.m_ShaderSplats == null || renderer.m_ShaderComposite == null ||
                renderer.m_ShaderDebugPoints == null || renderer.m_ShaderDebugBoxes == null ||
                renderer.m_CSSplatUtilities == null)
                Debug.LogWarning("[GaussianLOD] One or more GaussianSplatRenderer shader resources " +
                                 "failed to load from Packages/org.nesnausk.gaussian-splatting/Shaders/. " +
                                 "Check that the package is installed correctly.");
        }
    }
}
#endif
