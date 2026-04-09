// SPDX-License-Identifier: MIT
// SplatClusterIndexAsset — heavyweight ScriptableObject containing only the flat
// per-splat index array. Stored as a separate .asset file from SplatClusterAsset
// so the lightweight metadata can load fast and the index data can stream / unload
// independently.

using UnityEngine;

namespace GaussianLOD.Runtime.Clustering
{
    /// <summary>
    /// Flat array of splat indices into the source <c>GaussianSplatAsset</c>.
    /// Each cluster in <see cref="SplatClusterAsset"/> addresses a contiguous
    /// subspan of this array via <c>startIndex</c> and <c>count</c>.
    /// </summary>
    public sealed class SplatClusterIndexAsset : ScriptableObject
    {
        [SerializeField] int[] m_AllIndices;

        public int[] allIndices
        {
            get => m_AllIndices;
            internal set => m_AllIndices = value;
        }

        public int Length => m_AllIndices?.Length ?? 0;

        /// <summary>Editor-only setter used by the baker.</summary>
        public void SetIndices(int[] indices)
        {
            m_AllIndices = indices ?? throw new System.ArgumentNullException(nameof(indices));
        }
    }
}
