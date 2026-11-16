using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Sefpro.JawTongs
{
    /// <summary>
    /// Keeps the central lifting ring (Ring_Mesh) seated upright at the apex (midpoint particle)
    /// of the Obi chain rope without non-uniform scale distortion or side-slope tilting.
    /// The bottom inner rim of Ring_Mesh threads through the chain apex particle (p[12]) while
    /// the top inner rim provides the seating saddle for the overhead crane hook.
    /// </summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(500)]
    public class ChainLinkRingFollower : MonoBehaviour
    {
        [Tooltip("Obi rope whose midpoint apex particle the ring is threaded through.")]
        public Obi.ObiRope rope;

        [Tooltip("Index of the chain link (element) the ring passes through.")]
        public int linkIndex = 12;

        [Tooltip("Legacy local offset (kept for serialized compatibility).")]
        public Vector3 localPosition = new Vector3(-0.05495f, 0f, 0f);

        [Tooltip("Vertical offset from the chain apex particle (p[12]) to the ring centre so p[12] rests on the bottom inner rim of the ring (metres).")]
        public float apexVerticalOffset = 0.038f;

        [Tooltip("Uniform local scale applied to Ring_Mesh to preserve circular geometry.")]
        public Vector3 uniformRingScale = new Vector3(0.025f, 0.025f, 0.025f);

        private void LateUpdate()
        {
            if ((transform.localScale - uniformRingScale).sqrMagnitude > 1e-7f)
            {
                transform.localScale = uniformRingScale;
            }

            var grab = GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>();
            if (grab != null && grab.isSelected)
                return;

            if (rope != null && rope.solver != null && rope.isLoaded && rope.activeParticleCount >= 3)
            {
                int count = rope.activeParticleCount;
                int midIdx = count / 2;
                Vector3 p0 = rope.GetParticlePosition(rope.solverIndices[0]);
                Vector3 pMid = rope.GetParticlePosition(rope.solverIndices[midIdx]);
                Vector3 pEnd = rope.GetParticlePosition(rope.solverIndices[count - 1]);

                Vector3 spanDir = pEnd - p0;
                spanDir.y = 0f;
                if (spanDir.sqrMagnitude < 1e-6f)
                {
                    spanDir = transform.root != null ? transform.root.right : Vector3.right;
                }
                else
                {
                    spanDir.Normalize();
                }

                Vector3 planeNormal = Vector3.Cross(spanDir, Vector3.up).normalized;
                if (planeNormal.sqrMagnitude < 1e-6f)
                    planeNormal = Vector3.back;

                // Chain_1 mesh has its hole normal along local Y and its circular ring in local X-Z.
                // LookRotation(-spanDir, planeNormal) places local Y along planeNormal (-Z) and local X along +Y (upright).
                Quaternion ringRot = Quaternion.LookRotation(-spanDir, planeNormal);
                Vector3 ringPos = pMid + Vector3.up * apexVerticalOffset;
                transform.SetPositionAndRotation(ringPos, ringRot);
            }
        }
    }
}
