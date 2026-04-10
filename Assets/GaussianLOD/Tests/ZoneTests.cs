// SPDX-License-Identifier: MIT
// ZoneTests — budget splitting and StereoCameraRig singleton behavior.

#if UNITY_INCLUDE_TESTS
using GaussianLOD.Runtime.Stereo;
using GaussianLOD.Runtime.Util;
using GaussianLOD.Runtime.Zones;
using NUnit.Framework;
using UnityEngine;

namespace GaussianLOD.Tests
{
    public class ZoneTests
    {
        [SetUp]
        public void SetUp()
        {
            PlatformCapabilityChecker.ResetForTests();
            PlatformCapabilityChecker.Initialize();
        }

        // ---- ZoneBudgetSplitter -----------------------------------------------------------

        [Test]
        public void Splitter_ZeroVisibleZones_GetZeroBudget()
        {
            var splitter = new ZoneBudgetSplitter(3, 100_000);
            splitter.Update(new float[] { 0f, 0f, 0f });
            Assert.AreEqual(0, splitter.GetBudgetForZone(0));
            Assert.AreEqual(0, splitter.GetBudgetForZone(1));
            Assert.AreEqual(0, splitter.GetBudgetForZone(2));
            splitter.Dispose();
        }

        [Test]
        public void Splitter_ProportionalMath_SumsToTotalBudget()
        {
            int totalBudget = 200_000;
            var splitter = new ZoneBudgetSplitter(3, totalBudget);
            // Zone 0: high coverage, Zone 1: medium, Zone 2: low
            splitter.Update(new float[] { 0.5f, 0.3f, 0.2f });
            int sum = splitter.GetBudgetForZone(0) + splitter.GetBudgetForZone(1) + splitter.GetBudgetForZone(2);
            Assert.AreEqual(totalBudget, sum, "Budget sum must equal total budget.");
            splitter.Dispose();
        }

        [Test]
        public void Splitter_RespectsMinimumFloor()
        {
            int totalBudget = 100_000;
            var splitter = new ZoneBudgetSplitter(3, totalBudget);
            // Zone 2 has tiny coverage — should still get at least kMinBudgetPerZone.
            splitter.Update(new float[] { 0.9f, 0.09f, 0.01f });
            Assert.GreaterOrEqual(splitter.GetBudgetForZone(2), ZoneBudgetSplitter.kMinBudgetPerZone,
                "Even the smallest active zone must get the minimum budget.");
            splitter.Dispose();
        }

        [Test]
        public void Splitter_InactiveZone_GetsNoBudget_ActiveGetsAll()
        {
            int totalBudget = 100_000;
            var splitter = new ZoneBudgetSplitter(3, totalBudget);
            // Only zone 1 is active.
            splitter.Update(new float[] { 0f, 0.5f, 0f });
            Assert.AreEqual(0, splitter.GetBudgetForZone(0));
            Assert.AreEqual(totalBudget, splitter.GetBudgetForZone(1));
            Assert.AreEqual(0, splitter.GetBudgetForZone(2));
            splitter.Dispose();
        }

        // ---- StereoCameraRig singleton ----------------------------------------------------

        [Test]
        public void Singleton_SecondInstance_DefersToFirst()
        {
            var go = new GameObject("TestCam");
            var cam = go.AddComponent<Camera>();
            try
            {
                var rig1 = StereoCameraRig.GetOrCreate(cam);
                var rig2 = StereoCameraRig.GetOrCreate(cam);
                Assert.AreSame(rig1, rig2, "Second GetOrCreate with same camera must return the same instance.");
                Assert.AreSame(rig1, StereoCameraRig.Instance);

                // Release one — singleton should remain.
                StereoCameraRig.Release(rig2);
                Assert.AreSame(rig1, StereoCameraRig.Instance, "Singleton survives partial release.");

                // Release the last — singleton should clear.
                StereoCameraRig.Release(rig1);
                Assert.IsNull(StereoCameraRig.Instance, "Singleton must clear when ref count hits zero.");
                rig1.Dispose();
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
#endif
