// SPDX-License-Identifier: MIT
// SplatClusterAsset — lightweight ScriptableObject holding cluster metadata only.
// Pairs with a SplatClusterIndexAsset that holds the flat splat-index array.

using UnityEngine;

namespace GaussianLOD.Runtime.Clustering
{
    /// <summary>
    /// Baked clustered LOD metadata for one source <c>GaussianSplatAsset</c>.
    /// Contains the cluster array (positions, bounds, mega-splat representative),
    /// scene-wide totals, and a pointer to the paired index asset.
    /// </summary>
    public sealed class SplatClusterAsset : ScriptableObject
    {
        [SerializeField] SplatClusterData[] m_Clusters;
        [SerializeField] SplatClusterIndexAsset m_IndexAsset;
        [SerializeField] int m_TotalSplatCount;
        [SerializeField] int m_MaxDepth;
        [SerializeField] Bounds m_SceneBounds;

        // The four LOD-bucket child assets produced alongside the cluster data.
        // LOD0 is the source asset itself; LOD1..3 are progressively decimated copies.
        // Any of these may be null if the user opts out of generating them at bake time.
        [SerializeField] GameObject m_Lod0RendererPrefab;
        [SerializeField] GameObject m_Lod1RendererPrefab;
        [SerializeField] GameObject m_Lod2RendererPrefab;
        [SerializeField] GameObject m_Lod3RendererPrefab;

        public SplatClusterData[] clusters => m_Clusters;
        public SplatClusterIndexAsset indexAsset => m_IndexAsset;
        public int totalSplatCount => m_TotalSplatCount;
        public int maxDepth => m_MaxDepth;
        public Bounds sceneBounds => m_SceneBounds;
        public int clusterCount => m_Clusters?.Length ?? 0;

        public GameObject lod0RendererPrefab => m_Lod0RendererPrefab;
        public GameObject lod1RendererPrefab => m_Lod1RendererPrefab;
        public GameObject lod2RendererPrefab => m_Lod2RendererPrefab;
        public GameObject lod3RendererPrefab => m_Lod3RendererPrefab;

        /// <summary>Editor-only initializer used by the baker. Not for runtime use.</summary>
        public void SetBakedData(
            SplatClusterData[] clusters,
            SplatClusterIndexAsset indexAsset,
            int totalSplatCount,
            int maxDepth,
            Bounds sceneBounds)
        {
            m_Clusters = clusters ?? throw new System.ArgumentNullException(nameof(clusters));
            m_IndexAsset = indexAsset ?? throw new System.ArgumentNullException(nameof(indexAsset));
            m_TotalSplatCount = totalSplatCount;
            m_MaxDepth = maxDepth;
            m_SceneBounds = sceneBounds;
        }

        public void SetLodPrefabs(GameObject lod0, GameObject lod1, GameObject lod2, GameObject lod3)
        {
            m_Lod0RendererPrefab = lod0;
            m_Lod1RendererPrefab = lod1;
            m_Lod2RendererPrefab = lod2;
            m_Lod3RendererPrefab = lod3;
        }
    }
}
