// SPDX-License-Identifier: MIT
// LODBudgetProfiler — Unity 6 SceneView Overlay showing per-frame GaussianLOD stats:
// total splats requested vs budget, per-LOD distribution, visible cluster count, and
// the currently active bucket. When a SpatialZoneManager is present, shows collapsible
// per-zone rows. Read-only — pulls state from live controllers each repaint.

#if UNITY_EDITOR
using GaussianLOD.Runtime;
using GaussianLOD.Runtime.LOD;
using GaussianLOD.Runtime.Zones;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;
using UnityEngine.UIElements;

namespace GaussianLOD.Editor
{
    [Overlay(typeof(SceneView), "GaussianLOD Budget", true)]
    public sealed class LODBudgetProfiler : Overlay
    {
        Label m_Header;
        Label m_Budget;
        Label m_PerLod;
        Label m_Bucket;
        IMGUIContainer m_Bar;

        // Multi-zone elements
        VisualElement m_ZoneContainer;
        Foldout m_ZoneFoldout;
        Label[] m_ZoneLabels;
        IMGUIContainer[] m_ZoneBars;
        float[] m_ZoneFills;

        public override VisualElement CreatePanelContent()
        {
            var root = new VisualElement { style = { minWidth = 220, paddingLeft = 6, paddingRight = 6, paddingTop = 4, paddingBottom = 4 } };
            m_Header = new Label("GaussianLOD") { style = { unityFontStyleAndWeight = FontStyle.Bold } };
            m_Budget = new Label("\u2014");
            m_PerLod = new Label("\u2014");
            m_Bucket = new Label("\u2014");
            m_Bar = new IMGUIContainer(DrawBar) { style = { height = 8, marginTop = 2, marginBottom = 2 } };
            root.Add(m_Header);
            root.Add(m_Budget);
            root.Add(m_Bar);
            root.Add(m_PerLod);
            root.Add(m_Bucket);

            // Multi-zone section (hidden until a SpatialZoneManager is detected).
            m_ZoneFoldout = new Foldout { text = "Per-Zone", value = false };
            m_ZoneContainer = new VisualElement();
            m_ZoneFoldout.Add(m_ZoneContainer);
            m_ZoneFoldout.style.display = DisplayStyle.None;
            root.Add(m_ZoneFoldout);

            EditorApplication.update += root.MarkDirtyRepaint;
            root.RegisterCallback<DetachFromPanelEvent>(_ => EditorApplication.update -= root.MarkDirtyRepaint);
            root.schedule.Execute(Refresh).Every(100);
            return root;
        }

        float m_Fill;

        void Refresh()
        {
            var manager = Object.FindFirstObjectByType<SpatialZoneManager>();
            if (manager != null && manager.ZoneCount > 0)
            {
                RefreshMultiZone(manager);
                return;
            }

            // Single-zone mode.
            m_ZoneFoldout.style.display = DisplayStyle.None;
            RefreshSingleController();
        }

        void RefreshSingleController()
        {
            var ctrl = Object.FindFirstObjectByType<GaussianLODController>();
            if (ctrl == null || !ctrl.IsReady)
            {
                m_Budget.text = "(no controller in scene)";
                m_PerLod.text = "";
                m_Bucket.text = "";
                m_Fill = 0;
                return;
            }

            var sel = ctrl.Selector;
            var bud = ctrl.Budget;
            PopulateSingleView(sel, bud, ctrl.Assembler);
        }

        void PopulateSingleView(LODSelector sel, LODBudgetManager bud, Runtime.Rendering.SplatDrawCallAssembler asm)
        {
            var lods = sel.LodLevels;
            var vis = sel.VisibleFlags;

            int visibleClusters = 0;
            int n0 = 0, n1 = 0, n2 = 0, n3 = 0;
            for (int i = 0; i < lods.Length; ++i)
            {
                if (vis[i] == 0) continue;
                visibleClusters++;
                switch (lods[i]) { case 0: n0++; break; case 1: n1++; break; case 2: n2++; break; default: n3++; break; }
            }

            int requested = bud.LastFrameSplatCost;
            int budget = bud.MaxSplatsPerFrame;
            float pct = budget <= 0 ? 0f : (float)requested / budget;
            m_Fill = Mathf.Clamp01(pct);

            m_Budget.text = $"Splats: {requested:N0} / {budget:N0}  ({pct * 100f:F0}%)";
            m_PerLod.text = $"LOD0:{n0}  LOD1:{n1}  LOD2:{n2}  LOD3:{n3}   visible:{visibleClusters}";
            m_Bucket.text = $"Active bucket: LOD{asm.CurrentBucket} (req LOD{asm.LastFrameSelectedLod})";
        }

        void RefreshMultiZone(SpatialZoneManager manager)
        {
            // Aggregate header.
            int totalRendered = manager.TotalSplatsRendered;
            int totalBudget = manager.Splitter != null ? manager.Splitter.TotalBudget : 0;
            float pct = totalBudget <= 0 ? 0f : (float)totalRendered / totalBudget;
            m_Fill = Mathf.Clamp01(pct);

            m_Budget.text = $"Total: {totalRendered:N0} / {totalBudget:N0}  ({pct * 100f:F0}%)";
            m_PerLod.text = $"Zones: {manager.ZoneCount}";
            m_Bucket.text = "";

            // Per-zone rows.
            m_ZoneFoldout.style.display = DisplayStyle.Flex;
            EnsureZoneElements(manager.ZoneCount);

            for (int i = 0; i < manager.ZoneCount; ++i)
            {
                var z = manager.GetZone(i);
                if (z == null || !z.IsReady)
                {
                    m_ZoneLabels[i].text = $"Zone {i}: (not ready)";
                    m_ZoneFills[i] = 0f;
                    continue;
                }

                int zBudget = z.Budget.MaxSplatsPerFrame;
                int zCost = z.Budget.LastFrameSplatCost;
                float zPct = zBudget <= 0 ? 0f : (float)zCost / zBudget;
                m_ZoneFills[i] = Mathf.Clamp01(zPct);
                m_ZoneLabels[i].text = $"Zone {i}: {zCost:N0}/{zBudget:N0} ({zPct * 100f:F0}%)  LOD{z.Assembler.CurrentBucket}";
            }
        }

        void EnsureZoneElements(int count)
        {
            if (m_ZoneLabels != null && m_ZoneLabels.Length == count) return;

            m_ZoneContainer.Clear();
            m_ZoneLabels = new Label[count];
            m_ZoneBars = new IMGUIContainer[count];
            m_ZoneFills = new float[count];

            for (int i = 0; i < count; ++i)
            {
                int idx = i; // capture for closure
                m_ZoneLabels[i] = new Label($"Zone {i}");
                m_ZoneBars[i] = new IMGUIContainer(() => DrawZoneBar(idx))
                    { style = { height = 6, marginTop = 1, marginBottom = 2 } };
                m_ZoneContainer.Add(m_ZoneLabels[i]);
                m_ZoneContainer.Add(m_ZoneBars[i]);
            }
        }

        void DrawBar()
        {
            var r = GUILayoutUtility.GetRect(1, 8, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(r, new Color(0.15f, 0.15f, 0.15f));
            var fill = new Rect(r.x, r.y, r.width * m_Fill, r.height);
            var col = m_Fill < 0.75f ? new Color(0.3f, 0.8f, 0.3f)
                    : m_Fill < 1.0f  ? new Color(0.9f, 0.8f, 0.2f)
                                     : new Color(0.9f, 0.3f, 0.3f);
            EditorGUI.DrawRect(fill, col);
        }

        void DrawZoneBar(int index)
        {
            if (m_ZoneFills == null || index >= m_ZoneFills.Length) return;
            float f = m_ZoneFills[index];
            var r = GUILayoutUtility.GetRect(1, 6, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(r, new Color(0.15f, 0.15f, 0.15f));
            var fill = new Rect(r.x, r.y, r.width * f, r.height);
            var col = f < 0.75f ? new Color(0.3f, 0.8f, 0.3f)
                    : f < 1.0f  ? new Color(0.9f, 0.8f, 0.2f)
                                : new Color(0.9f, 0.3f, 0.3f);
            EditorGUI.DrawRect(fill, col);
        }
    }
}
#endif
