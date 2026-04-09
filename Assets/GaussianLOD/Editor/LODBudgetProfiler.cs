// SPDX-License-Identifier: MIT
// LODBudgetProfiler — Unity 6 SceneView Overlay showing per-frame GaussianLOD stats:
// total splats requested vs budget, per-LOD distribution, visible cluster count, and
// the currently active bucket. Read-only — pulls state from the live
// GaussianLODController in the active scene each repaint.

#if UNITY_EDITOR
using GaussianLOD.Runtime;
using GaussianLOD.Runtime.LOD;
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

        public override VisualElement CreatePanelContent()
        {
            var root = new VisualElement { style = { minWidth = 220, paddingLeft = 6, paddingRight = 6, paddingTop = 4, paddingBottom = 4 } };
            m_Header = new Label("GaussianLOD") { style = { unityFontStyleAndWeight = FontStyle.Bold } };
            m_Budget = new Label("—");
            m_PerLod = new Label("—");
            m_Bucket = new Label("—");
            m_Bar = new IMGUIContainer(DrawBar) { style = { height = 8, marginTop = 2, marginBottom = 2 } };
            root.Add(m_Header);
            root.Add(m_Budget);
            root.Add(m_Bar);
            root.Add(m_PerLod);
            root.Add(m_Bucket);
            EditorApplication.update += root.MarkDirtyRepaint;
            root.RegisterCallback<DetachFromPanelEvent>(_ => EditorApplication.update -= root.MarkDirtyRepaint);
            root.schedule.Execute(Refresh).Every(100);
            return root;
        }

        float m_Fill;

        void Refresh()
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
            m_Bucket.text = $"Active bucket: LOD{ctrl.Assembler.CurrentBucket} (req LOD{ctrl.Assembler.LastFrameSelectedLod})";
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
    }
}
#endif
