// SPDX-License-Identifier: MIT
// StereoCameraRig — exposes per-frame Left/Right/Center eye poses for stereo rendering.
// Uses Unity's XR display API when available; otherwise falls back to a single-eye
// "mono" camera (the supplied camera). All platform branching is delegated to
// PlatformCapabilityChecker — this class only checks IsInitialized and reads.

using System;
using GaussianLOD.Runtime.Util;
using UnityEngine;
#if GLOD_ENABLE_XR
using UnityEngine.XR;
#endif

namespace GaussianLOD.Runtime.Stereo
{
    public sealed class StereoCameraRig : IDisposable
    {
        public struct EyePose
        {
            public Vector3 position;
            public Quaternion rotation;
            public Matrix4x4 viewMatrix;
            public Matrix4x4 projectionMatrix;
        }

        // ---- Singleton for multi-zone sharing ------------------------------------------------
        static StereoCameraRig s_Instance;
        static int s_RefCount;

        /// <summary>
        /// Shared singleton instance. Null until the first controller creates one via
        /// <see cref="GetOrCreate"/>. Multiple GaussianLODControllers in a multi-zone
        /// scene share this single instance to avoid redundant XR API queries.
        /// </summary>
        public static StereoCameraRig Instance => s_Instance;

        /// <summary>
        /// Return the existing singleton if it targets the same camera, otherwise create a
        /// new one. Each caller must pair this with <see cref="Release"/> on dispose.
        /// </summary>
        public static StereoCameraRig GetOrCreate(Camera cam)
        {
            if (s_Instance != null && s_Instance.m_Camera == cam)
            {
                s_RefCount++;
                return s_Instance;
            }

            // First caller or different camera — create fresh.
            var rig = new StereoCameraRig(cam);
            s_Instance = rig;
            s_RefCount = 1;
            return rig;
        }

        /// <summary>
        /// Decrement the ref count. When it reaches zero the singleton is cleared.
        /// </summary>
        public static void Release(StereoCameraRig rig)
        {
            if (rig == null || rig != s_Instance) return;
            s_RefCount--;
            if (s_RefCount <= 0)
            {
                s_Instance = null;
                s_RefCount = 0;
            }
        }

        // ---- Instance members ----------------------------------------------------------------
        public EyePose Left { get; private set; }
        public EyePose Right { get; private set; }
        public EyePose Center { get; private set; }
        public bool IsStereoActive { get; private set; }

        readonly Camera m_Camera;

        public StereoCameraRig(Camera cam)
        {
            m_Camera = cam ?? throw new ArgumentNullException(nameof(cam));
            if (!PlatformCapabilityChecker.IsInitialized)
                throw new InvalidOperationException(
                    "StereoCameraRig requires PlatformCapabilityChecker.Initialize() to have run first.");
        }

        /// <summary>
        /// Refresh the eye poses from the current frame's XR or mono state.
        /// Call once per frame, before any system that needs frustum information.
        /// </summary>
        public void Update()
        {
            if (m_Camera == null) return;

#if GLOD_ENABLE_XR
            // Detect stereo via the camera's stereoEnabled flag (works for both XR Plugin
            // Management and OpenXR; covers AVP, Quest, and PC ALVR/SteamVR uniformly).
            if (m_Camera.stereoEnabled)
            {
                IsStereoActive = true;
                Matrix4x4 lProj = m_Camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left);
                Matrix4x4 rProj = m_Camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right);
                Matrix4x4 lView = m_Camera.GetStereoViewMatrix(Camera.StereoscopicEye.Left);
                Matrix4x4 rView = m_Camera.GetStereoViewMatrix(Camera.StereoscopicEye.Right);

                // World-space eye positions = inverse view * origin
                Vector3 lPos = lView.inverse.MultiplyPoint3x4(Vector3.zero);
                Vector3 rPos = rView.inverse.MultiplyPoint3x4(Vector3.zero);
                Vector3 cPos = (lPos + rPos) * 0.5f;
                Quaternion rot = m_Camera.transform.rotation;

                Left = new EyePose { position = lPos, rotation = rot, viewMatrix = lView, projectionMatrix = lProj };
                Right = new EyePose { position = rPos, rotation = rot, viewMatrix = rView, projectionMatrix = rProj };
                Center = new EyePose
                {
                    position = cPos,
                    rotation = rot,
                    viewMatrix = m_Camera.worldToCameraMatrix,
                    projectionMatrix = m_Camera.projectionMatrix
                };
                return;
            }
#endif

            // Mono fallback: single eye, single matrix set.
            IsStereoActive = false;
            EyePose mono = new EyePose
            {
                position = m_Camera.transform.position,
                rotation = m_Camera.transform.rotation,
                viewMatrix = m_Camera.worldToCameraMatrix,
                projectionMatrix = m_Camera.projectionMatrix
            };
            Left = mono;
            Right = mono;
            Center = mono;
        }

        public void Dispose() { /* no owned resources */ }
    }
}
