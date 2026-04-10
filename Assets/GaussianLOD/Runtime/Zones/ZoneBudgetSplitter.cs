// SPDX-License-Identifier: MIT
// ZoneBudgetSplitter — splits a total splat budget across active zones proportionally
// by screen coverage. Zones with zero visible clusters get zero budget. Active zones
// receive at least kMinBudgetPerZone splats. Single responsibility: budget math only.

using System;

namespace GaussianLOD.Runtime.Zones
{
    public sealed class ZoneBudgetSplitter : IDisposable
    {
        public const int kMinBudgetPerZone = 5_000;

        readonly int[] m_Budgets;
        readonly float[] m_Coverages;
        int m_TotalBudget;

        public int ZoneCount => m_Budgets.Length;
        public int TotalBudget => m_TotalBudget;

        public ZoneBudgetSplitter(int zoneCount, int totalBudget)
        {
            if (zoneCount <= 0) throw new ArgumentOutOfRangeException(nameof(zoneCount));
            if (totalBudget <= 0) throw new ArgumentOutOfRangeException(nameof(totalBudget));
            m_Budgets = new int[zoneCount];
            m_Coverages = new float[zoneCount];
            m_TotalBudget = totalBudget;
        }

        /// <summary>
        /// Update budget distribution. Call once per frame before enforcing per-zone budgets.
        /// <paramref name="zoneCoverages"/> is the total screen coverage per zone (sum of
        /// per-cluster coverages for visible clusters in that zone). Zones with coverage ≤ 0
        /// receive zero budget.
        /// </summary>
        public void Update(float[] zoneCoverages)
        {
            if (zoneCoverages == null || zoneCoverages.Length != m_Budgets.Length)
                throw new ArgumentException("zoneCoverages length must match zone count.", nameof(zoneCoverages));

            int n = m_Budgets.Length;
            float totalCoverage = 0f;
            int activeCount = 0;

            for (int i = 0; i < n; ++i)
            {
                float c = zoneCoverages[i] > 0f ? zoneCoverages[i] : 0f;
                m_Coverages[i] = c;
                if (c > 0f)
                {
                    totalCoverage += c;
                    activeCount++;
                }
            }

            // No active zones — zero everything.
            if (activeCount == 0 || totalCoverage <= 0f)
            {
                Array.Clear(m_Budgets, 0, n);
                return;
            }

            // Check if the total budget can satisfy all active zones at minimum.
            int minTotal = activeCount * kMinBudgetPerZone;
            int distributable = m_TotalBudget;

            if (distributable < minTotal)
            {
                // Not enough budget for all minimums — give each active zone an equal share.
                int perZone = distributable / activeCount;
                for (int i = 0; i < n; ++i)
                    m_Budgets[i] = m_Coverages[i] > 0f ? perZone : 0;
                return;
            }

            // Reserve minimums, then distribute remainder proportionally.
            int remainder = distributable - minTotal;
            int assigned = 0;

            for (int i = 0; i < n; ++i)
            {
                if (m_Coverages[i] <= 0f)
                {
                    m_Budgets[i] = 0;
                    continue;
                }

                float share = m_Coverages[i] / totalCoverage;
                int extra = (int)(share * remainder);
                m_Budgets[i] = kMinBudgetPerZone + extra;
                assigned += m_Budgets[i];
            }

            // Distribute rounding residual to the highest-coverage active zone.
            int residual = distributable - assigned;
            if (residual != 0)
            {
                int best = -1;
                float bestCov = -1f;
                for (int i = 0; i < n; ++i)
                {
                    if (m_Coverages[i] > bestCov) { bestCov = m_Coverages[i]; best = i; }
                }
                if (best >= 0) m_Budgets[best] += residual;
            }
        }

        public int GetBudgetForZone(int zoneIndex)
        {
            if (zoneIndex < 0 || zoneIndex >= m_Budgets.Length)
                throw new ArgumentOutOfRangeException(nameof(zoneIndex));
            return m_Budgets[zoneIndex];
        }

        public void Dispose() { /* no native resources */ }
    }
}
