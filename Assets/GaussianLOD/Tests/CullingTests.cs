// SPDX-License-Identifier: MIT
// CullingTests — exercises the CPU LODSelector cull/coverage path. The GPU compute
// path is tested via runtime smoke only (no GPU test rig in CI by default).

#if UNITY_INCLUDE_TESTS
using GaussianLOD.Runtime.Clustering;
using GaussianLOD.Runtime.LOD;
using NUnit.Framework;
using UnityEngine;

namespace GaussianLOD.Tests
{
    public class CullingTests
    {
        static SplatClusterData MakeCluster(Vector3 center, Vector3 ext, int splatCount = 1024)
        {
            return new SplatClusterData
            {
                startIndex = 0,
                count = splatCount,
                worldBounds = new Bounds(center, ext * 2f),
                lodLevel = 0,
            };
        }

        static Plane[] BuildSimpleFrustum()
        {
            // Camera at origin looking +Z, near=1, far=100, ±10 in X/Y.
            return new Plane[]
            {
                new Plane(Vector3.right,    -(-10f)), // x ≥ -10  →  +x normal, d = 10
                new Plane(-Vector3.right,    10f),    // x ≤  10
                new Plane(Vector3.up,        10f),
                new Plane(-Vector3.up,       10f),
                new Plane(Vector3.forward,  -1f),     // z ≥ 1
                new Plane(-Vector3.forward,  100f),   // z ≤ 100
            };
        }

        [Test]
        public void ClusterFullyInside_IsVisible()
        {
            var clusters = new[] { MakeCluster(new Vector3(0, 0, 50f), Vector3.one) };
            var sel = new LODSelector(clusters);
            try
            {
                sel.Run(BuildSimpleFrustum(), Vector3.zero, Matrix4x4.identity);
                Assert.AreEqual(1, sel.VisibleCount);
                Assert.AreEqual(1, sel.VisibleFlags[0]);
            }
            finally { sel.Dispose(); }
        }

        [Test]
        public void ClusterFullyOutside_IsCulled()
        {
            // Way behind the camera
            var clusters = new[] { MakeCluster(new Vector3(0, 0, -500f), Vector3.one) };
            var sel = new LODSelector(clusters);
            try
            {
                sel.Run(BuildSimpleFrustum(), Vector3.zero, Matrix4x4.identity);
                Assert.AreEqual(0, sel.VisibleCount);
                Assert.AreEqual(0, sel.VisibleFlags[0]);
            }
            finally { sel.Dispose(); }
        }

        [Test]
        public void ClusterStraddlingPlane_IsConservativelyVisible()
        {
            // Bounds extend across the near plane (z=1)
            var clusters = new[] { MakeCluster(new Vector3(0, 0, 1f), new Vector3(1, 1, 5f)) };
            var sel = new LODSelector(clusters);
            try
            {
                sel.Run(BuildSimpleFrustum(), Vector3.zero, Matrix4x4.identity);
                Assert.AreEqual(1, sel.VisibleCount, "AABB straddling a plane must be conservatively visible");
            }
            finally { sel.Dispose(); }
        }

        [Test]
        public void Coverage_MatchesFormulaWithinTolerance()
        {
            // Cluster radius 1 (extent 1,1,1 → magnitude sqrt3) at distance 10 from camera.
            // Expected coverage = (sqrt3)^2 / 10^2 = 3/100 = 0.03
            var clusters = new[] { MakeCluster(new Vector3(0, 0, 10f), Vector3.one) };
            var sel = new LODSelector(clusters);
            try
            {
                sel.Run(BuildSimpleFrustum(), Vector3.zero, Matrix4x4.identity);
                float expected = 3f / 100f;
                Assert.That(sel.Coverage[0], Is.EqualTo(expected).Within(expected * 0.01f));
            }
            finally { sel.Dispose(); }
        }
    }
}
#endif
