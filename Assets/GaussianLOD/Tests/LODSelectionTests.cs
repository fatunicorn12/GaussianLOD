// SPDX-License-Identifier: MIT
// LODSelectionTests — coverage→LOD threshold mapping, budget demotion order, and
// hysteresis behaviour of LODTransitionController.

#if UNITY_INCLUDE_TESTS
using GaussianLOD.Runtime.Clustering;
using GaussianLOD.Runtime.LOD;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace GaussianLOD.Tests
{
    public class LODSelectionTests
    {
        static SplatClusterData Cl(Vector3 center, float radius, int splatCount = 1000) =>
            new SplatClusterData
            {
                startIndex = 0,
                count = splatCount,
                worldBounds = new Bounds(center, Vector3.one * (radius * 2f / Mathf.Sqrt(3f))),
                lodLevel = 0,
            };

        static Plane[] WideFrustum()
        {
            return new Plane[]
            {
                new Plane(Vector3.right,    1000f),
                new Plane(-Vector3.right,   1000f),
                new Plane(Vector3.up,       1000f),
                new Plane(-Vector3.up,      1000f),
                new Plane(Vector3.forward,  1000f),
                new Plane(-Vector3.forward, 1000f),
            };
        }

        [Test]
        public void Coverage_AboveLod0Threshold_AssignsLod0()
        {
            // r=1, d=5 → coverage = 1/25 = 0.04 > 0.02
            var sel = new LODSelector(new[] { Cl(new Vector3(0, 0, 5), 1f) });
            try
            {
                sel.Run(WideFrustum(), Vector3.zero, Matrix4x4.identity);
                Assert.AreEqual(LODSelector.kLODFull, sel.LodLevels[0]);
            }
            finally { sel.Dispose(); }
        }

        [Test]
        public void Coverage_BetweenLod1AndLod2_AssignsLod2()
        {
            // r=1, d=20 → coverage = 1/400 = 0.0025 (between 0.001 and 0.005) → LOD2
            var sel = new LODSelector(new[] { Cl(new Vector3(0, 0, 20), 1f) });
            try
            {
                sel.Run(WideFrustum(), Vector3.zero, Matrix4x4.identity);
                Assert.AreEqual(LODSelector.kLODQuarter, sel.LodLevels[0]);
            }
            finally { sel.Dispose(); }
        }

        [Test]
        public void BudgetEnforcement_DemotesFurthestFirst()
        {
            // Two clusters: near (z=5, big) and far (z=15, big). Budget too small for both at LOD0.
            var clusters = new[]
            {
                Cl(new Vector3(0, 0, 5),  1f, 10_000), // near
                Cl(new Vector3(0, 0, 15), 1f, 10_000), // far
            };
            var sel = new LODSelector(clusters);
            try
            {
                sel.Run(WideFrustum(), Vector3.zero, Matrix4x4.identity);
                // Both started at LOD0 (coverage 0.04 and ~0.0044).
                // Actually far cluster lands at LOD2 already (0.0044 < 0.005). Force both to LOD0:
                var lods = sel.LodLevels;
                lods[0] = LODSelector.kLODFull;
                lods[1] = LODSelector.kLODFull;

                var budget = new LODBudgetManager(sel, budget: 12_000);
                try
                {
                    budget.Enforce(clusters, Vector3.zero, Matrix4x4.identity);
                    Assert.IsTrue(budget.LastFrameOverBudget);
                    // Furthest (index 1) should be demoted before the nearest (index 0).
                    Assert.GreaterOrEqual(sel.LodLevels[1], sel.LodLevels[0],
                        "Furthest cluster must be demoted at least as far as the nearest.");
                }
                finally { budget.Dispose(); }
            }
            finally { sel.Dispose(); }
        }

        [Test]
        public void Hysteresis_BlocksLodSwitch_BeforeRequiredFrames()
        {
            var ctrl = new LODTransitionController(1);
            var levels = new NativeArray<int>(1, Allocator.TempJob);
            try
            {
                // Frame 1: seed
                levels[0] = 0; ctrl.Smooth(levels);
                Assert.AreEqual(0, levels[0]);
                // Frame 2: request switch
                levels[0] = 1; ctrl.Smooth(levels);
                Assert.AreEqual(0, levels[0], "Single-frame request must not commit");
                // Frame 3: still requesting
                levels[0] = 1; ctrl.Smooth(levels);
                Assert.AreEqual(0, levels[0], "Two-frame request must not commit (need 3)");
            }
            finally { levels.Dispose(); ctrl.Dispose(); }
        }

        [Test]
        public void Hysteresis_AllowsLodSwitch_AfterExactlyThreeFrames()
        {
            var ctrl = new LODTransitionController(1);
            var levels = new NativeArray<int>(1, Allocator.TempJob);
            try
            {
                levels[0] = 0; ctrl.Smooth(levels);                  // frame 1 seed
                levels[0] = 1; ctrl.Smooth(levels); // pending=1
                levels[0] = 1; ctrl.Smooth(levels); // pending=2
                levels[0] = 1; ctrl.Smooth(levels); // pending=3 → commit
                Assert.AreEqual(1, levels[0], "Three consecutive identical requests must commit");
            }
            finally { levels.Dispose(); ctrl.Dispose(); }
        }
    }
}
#endif
