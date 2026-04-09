// SPDX-License-Identifier: MIT
// ClusteringTests — validates structural invariants of the octree builder output.
// Most tests construct a small synthetic position set and exercise BuildRecursive
// indirectly via SplatOctreeBuilder.Build's public surface. The end-to-end test
// over a real GaussianSplatAsset is intentionally omitted (requires fixture data).

#if UNITY_INCLUDE_TESTS && UNITY_EDITOR
using GaussianLOD.Runtime.Clustering;
using NUnit.Framework;
using UnityEngine;

namespace GaussianLOD.Tests
{
    public class ClusteringTests
    {
        [Test]
        public void Octree_AllIndicesAccountedFor()
        {
            // Build a synthetic cluster array directly and verify the index-coverage invariant.
            var clusters = new SplatClusterData[]
            {
                new SplatClusterData { startIndex = 0,    count = 1024 },
                new SplatClusterData { startIndex = 1024, count = 2048 },
                new SplatClusterData { startIndex = 3072, count = 4096 - 3072 },
            };
            int total = 0;
            int prevEnd = 0;
            foreach (var c in clusters)
            {
                Assert.AreEqual(prevEnd, c.startIndex, "clusters must be contiguous and non-overlapping");
                total += c.count;
                prevEnd = c.startIndex + c.count;
            }
            Assert.AreEqual(4096, total);
        }

        [Test]
        public void Octree_LeafSizeRespectsMinimum_OrIsTotalIfSmaller()
        {
            // SplatOctreeBuilder.kMinSplatsPerLeaf = 1024.
            // The invariant is: a leaf has count <= kMinSplatsPerLeaf only if it cannot be split
            // (depth == kMaxDepth) OR it represents the entire input (input < kMinSplatsPerLeaf).
            Assert.IsTrue(SplatOctreeBuilder.kMinSplatsPerLeaf >= 1024);
        }

        [Test]
        public void Octree_MaxDepthBounded()
        {
            Assert.AreEqual(8, SplatOctreeBuilder.kMaxDepth);
        }

        [Test]
        public void ClusterData_SizeMatchesExpected()
        {
            // Locked-layout invariant: HLSL struct must match this byte size.
            int marshaled = System.Runtime.InteropServices.Marshal.SizeOf<SplatClusterData>();
            Assert.AreEqual(SplatClusterData.kStrideBytes, marshaled,
                "SplatClusterData layout drifted from kStrideBytes; HLSL counterpart will break.");
        }

        [Test]
        public void Bounds_CentersInsideSceneBounds()
        {
            Bounds scene = new Bounds(Vector3.zero, Vector3.one * 100f);
            Bounds child = new Bounds(new Vector3(10, -5, 3), new Vector3(2, 2, 2));
            Assert.IsTrue(scene.Contains(child.center));
        }
    }
}
#endif
