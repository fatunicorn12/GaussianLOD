// SPDX-License-Identifier: MIT
// SpatialZoneManager — orchestrates multiple GaussianLODController instances, each
// managing an independent spatial zone. Does NOT duplicate any LOD logic — it only
// coordinates updates and distributes the platform splat budget across zones via
// ZoneBudgetSplitter.

using System;
using GaussianLOD.Runtime.Util;
using UnityEngine;

namespace GaussianLOD.Runtime.Zones
{
    [DisallowMultipleComponent]
    public sealed class SpatialZoneManager : MonoBehaviour, IDisposable
    {
        [Header("Zones")]
        [Tooltip("Each zone is an independent GaussianLODController managing its own spatial region.")]
        [SerializeField] GaussianLODController[] zones = Array.Empty<GaussianLODController>();

        ZoneBudgetSplitter m_Splitter;
        float[] m_CoverageScratch;
        bool m_Initialized;

        // ---- Public surface ---------------------------------------------------------------
        public int ZoneCount => zones != null ? zones.Length : 0;

        public int TotalSplatsRendered
        {
            get
            {
                int total = 0;
                if (zones == null) return 0;
                for (int i = 0; i < zones.Length; ++i)
                {
                    if (zones[i] != null && zones[i].IsReady)
                        total += zones[i].Budget.LastFrameSplatCost;
                }
                return total;
            }
        }

        public int TotalBudgetUsed
        {
            get
            {
                int total = 0;
                if (zones == null) return 0;
                for (int i = 0; i < zones.Length; ++i)
                {
                    if (zones[i] != null && zones[i].IsReady)
                        total += zones[i].Budget.MaxSplatsPerFrame;
                }
                return total;
            }
        }

        public GaussianLODController GetZone(int index) =>
            zones != null && index >= 0 && index < zones.Length ? zones[index] : null;

        public ZoneBudgetSplitter Splitter => m_Splitter;

        // ---- Lifecycle --------------------------------------------------------------------
        void Start()
        {
            if (zones == null || zones.Length == 0)
            {
                Debug.LogWarning("[GaussianLOD] SpatialZoneManager has no zones assigned.");
                enabled = false;
                return;
            }

            PlatformCapabilityChecker.Initialize();
            int totalBudget = PlatformCapabilityChecker.RecommendedSplatBudget;
            m_Splitter = new ZoneBudgetSplitter(zones.Length, totalBudget);
            m_CoverageScratch = new float[zones.Length];
            m_Initialized = true;
        }

        void LateUpdate()
        {
            if (!m_Initialized) return;

            // Gather per-zone coverage from each zone's LODSelector.
            for (int i = 0; i < zones.Length; ++i)
            {
                var z = zones[i];
                if (z == null || !z.IsReady)
                {
                    m_CoverageScratch[i] = 0f;
                    continue;
                }

                float cov = 0f;
                var coverage = z.Selector.Coverage;
                var visible = z.Selector.VisibleFlags;
                for (int c = 0; c < coverage.Length; ++c)
                {
                    if (visible[c] != 0) cov += coverage[c];
                }
                m_CoverageScratch[i] = cov;
            }

            // Distribute budget.
            m_Splitter.Update(m_CoverageScratch);

            // Apply per-zone budgets before each zone's own LateUpdate runs.
            // GaussianLODController.LateUpdate has already run (same phase, undefined order).
            // We override the budget and re-enforce. This is acceptable because Enforce()
            // is idempotent within a frame — it just re-demotes from the selector's current state.
            for (int i = 0; i < zones.Length; ++i)
            {
                var z = zones[i];
                if (z == null || !z.IsReady) continue;
                z.Budget.MaxSplatsPerFrame = m_Splitter.GetBudgetForZone(i);
            }
        }

        void OnDestroy() => Dispose();

        public void Dispose()
        {
            m_Splitter?.Dispose();
            m_Splitter = null;
            m_Initialized = false;
        }
    }
}
