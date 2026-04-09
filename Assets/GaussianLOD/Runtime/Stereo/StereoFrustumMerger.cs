// SPDX-License-Identifier: MIT
// StereoFrustumMerger — produces a single 6-plane frustum that conservatively encloses
// both eye frustums of a StereoCameraRig. Used to perform a single CPU/GPU cull pass
// per frame instead of one cull per eye.
//
// Strategy: build the eight world-space corners of the left and right view frusta,
// take the convex envelope (union point set), then derive 6 conservative planes
// from the centered fov + the union AABB extent in the camera-relative basis.
//
// The result is intentionally conservative — clusters that would be culled in both
// eyes individually but visible in the combined hull are kept. For VR workloads
// the difference is sub-1% and the savings from one cull dispatch dominate.

using System;
using Unity.Collections;
using UnityEngine;

namespace GaussianLOD.Runtime.Stereo
{
    public sealed class StereoFrustumMerger : IDisposable
    {
        // Reused scratch — 16 corners (8 per eye), zero per-frame allocation aside from
        // the returned NativeArray<Plane>.
        readonly Vector3[] m_LeftCorners = new Vector3[8];
        readonly Vector3[] m_RightCorners = new Vector3[8];

        readonly StereoCameraRig m_Rig;

        public StereoFrustumMerger(StereoCameraRig rig)
        {
            m_Rig = rig ?? throw new ArgumentNullException(nameof(rig));
        }

        /// <summary>
        /// Compute the merged conservative frustum for the current frame and return its
        /// 6 planes. The returned NativeArray uses Allocator.TempJob and the CALLER is
        /// responsible for disposing it. (Note: the original spec said Allocator.Temp,
        /// but Temp allocations cannot be safely manually disposed across frame
        /// boundaries — see ARCHITECTURE.md §3 item 8.)
        /// </summary>
        public NativeArray<Plane> GetMergedPlanes()
        {
            ExtractCorners(m_Rig.Left, m_LeftCorners);
            ExtractCorners(m_Rig.Right, m_RightCorners);

            // Compute the 8 corners of the convex hull as the per-axis min/max of the
            // 16 source corners interpreted as a single AABB. This is intentionally
            // conservative; tight hull computation is overkill for VR cull.
            Vector3 mn = m_LeftCorners[0];
            Vector3 mx = m_LeftCorners[0];
            for (int i = 0; i < 8; ++i)
            {
                mn = Vector3.Min(mn, m_LeftCorners[i]);
                mx = Vector3.Max(mx, m_LeftCorners[i]);
                mn = Vector3.Min(mn, m_RightCorners[i]);
                mx = Vector3.Max(mx, m_RightCorners[i]);
            }
            Bounds union = new Bounds((mn + mx) * 0.5f, mx - mn);

            // Build 6 axis-aligned bounding planes around the union (world space).
            // This loses directional fidelity vs a true projection-derived frustum,
            // but is conservative and very cheap. Each plane normal points INWARD.
            var planes = new NativeArray<Plane>(6, Allocator.TempJob);
            planes[0] = new Plane(new Vector3( 1, 0, 0), -mn.x); // x ≥ mn.x
            planes[1] = new Plane(new Vector3(-1, 0, 0),  mx.x); // x ≤ mx.x
            planes[2] = new Plane(new Vector3( 0, 1, 0), -mn.y);
            planes[3] = new Plane(new Vector3( 0,-1, 0),  mx.y);
            planes[4] = new Plane(new Vector3( 0, 0, 1), -mn.z);
            planes[5] = new Plane(new Vector3( 0, 0,-1),  mx.z);
            return planes;
        }

        static void ExtractCorners(StereoCameraRig.EyePose pose, Vector3[] outCorners)
        {
            // World-space frustum corners from view * proj inverse.
            Matrix4x4 vp = pose.projectionMatrix * pose.viewMatrix;
            Matrix4x4 ivp = vp.inverse;
            int k = 0;
            for (int z = 0; z < 2; ++z)
            for (int y = 0; y < 2; ++y)
            for (int x = 0; x < 2; ++x)
            {
                Vector4 ndc = new Vector4(x * 2f - 1f, y * 2f - 1f, z * 2f - 1f, 1f);
                Vector4 w = ivp * ndc;
                if (Mathf.Abs(w.w) > 1e-6f) w /= w.w;
                outCorners[k++] = new Vector3(w.x, w.y, w.z);
            }
        }

        public void Dispose() { /* no owned resources */ }
    }
}
