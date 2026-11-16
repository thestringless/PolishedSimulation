using UnityEngine;
using Obi;

namespace Sefpro.RatchetBelt
{
    /// <summary>
    /// Coordinates physical attachment, alignment, and release of the ratchet belt's end fittings
    /// (Fixed J-Hook and Adjustable J-Hook) to either side hook of the Vibrating Platform.
    /// Ensures stability during tightening strokes, prevents drifting or unexpected detachment,
    /// and maintains synchronization with Obi rope webbing.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlatformHookAttachment : MonoBehaviour
    {
        [Header("Controller Reference")]
        public RatchetBeltMechanicsController mechanicsController;

        [Header("Platform Hook Anchors")]
        public PlatformSideHook platformHookLeft;
        public PlatformSideHook platformHookRight;

        [Header("Attachment States (Read Only)")]
        [SerializeField] private bool isFixedHookAttached = false;
        [SerializeField] private PlatformSideHook fixedAttachedTo;
        [SerializeField] private Vector3 fixedHookRestPos;
        [SerializeField] private Quaternion fixedHookRestRot;

        [SerializeField] private bool isAdjHookAttached = false;
        [SerializeField] private PlatformSideHook adjAttachedTo;
        [SerializeField] private Vector3 adjHookRestPos;
        [SerializeField] private Quaternion adjHookRestRot;

        [Header("Drag & Proximity Thresholds")]
        public float attachmentProximityThreshold = 0.40f;

        public bool IsFixedHookAttached => isFixedHookAttached;
        public bool IsAdjHookAttached => isAdjHookAttached;
        public PlatformSideHook FixedAttachedTo => fixedAttachedTo;
        public PlatformSideHook AdjAttachedTo => adjAttachedTo;

        private void Awake()
        {
            if (mechanicsController == null)
            {
                mechanicsController = GetComponent<RatchetBeltMechanicsController>() ?? FindAnyObjectByType<RatchetBeltMechanicsController>();
            }

            FindPlatformHooks();
        }

        private void Start()
        {
            CacheRestTransforms();
        }

        public void FindPlatformHooks()
        {
            if (platformHookLeft != null && platformHookRight != null) return;

            var allHooks = FindObjectsByType<PlatformSideHook>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var h in allHooks)
            {
                if (h.side == PlatformSideHook.HookSide.Left && platformHookLeft == null)
                    platformHookLeft = h;
                else if (h.side == PlatformSideHook.HookSide.Right && platformHookRight == null)
                    platformHookRight = h;
            }
        }

        public void CacheRestTransforms()
        {
            if (mechanicsController != null)
            {
                if (mechanicsController.fixedHookTransform != null && (fixedHookRestPos == Vector3.zero || fixedHookRestPos.z < -5.0f))
                {
                    fixedHookRestPos = new Vector3(-23.75f, 0.14f, -4.24f);
                    fixedHookRestRot = mechanicsController.fixedHookTransform.rotation;
                }

                if (mechanicsController.adjustableHookTransform != null && (adjHookRestPos == Vector3.zero || adjHookRestPos.z < -5.0f))
                {
                    adjHookRestPos = new Vector3(-22.20f, 0.72f, -4.24f);
                    adjHookRestRot = mechanicsController.adjustableHookTransform.rotation;
                }
            }
        }

        private void LateUpdate()
        {
            // Maintain continuous rigid alignment with the vibrating platform hook anchors
            if (isFixedHookAttached && fixedAttachedTo != null && mechanicsController != null && mechanicsController.fixedHookTransform != null)
            {
                Transform targetAnchor = fixedAttachedTo.attachTransform != null ? fixedAttachedTo.attachTransform : fixedAttachedTo.transform;
                mechanicsController.fixedHookTransform.position = targetAnchor.position;
                mechanicsController.fixedHookTransform.rotation = targetAnchor.rotation;

                if (mechanicsController.stitchFixTransform != null)
                {
                    mechanicsController.stitchFixTransform.position = targetAnchor.position + targetAnchor.forward * 0.05f;
                }
            }

            if (isAdjHookAttached && adjAttachedTo != null && mechanicsController != null && mechanicsController.adjustableHookTransform != null)
            {
                Transform targetAnchor = adjAttachedTo.attachTransform != null ? adjAttachedTo.attachTransform : adjAttachedTo.transform;
                mechanicsController.adjustableHookTransform.position = targetAnchor.position;
                mechanicsController.adjustableHookTransform.rotation = targetAnchor.rotation;

                if (mechanicsController.stitchAdjTransform != null)
                {
                    mechanicsController.stitchAdjTransform.position = targetAnchor.position + targetAnchor.forward * 0.05f;
                }
            }
        }

        /// <summary>
        /// Attaches the fixed J-hook fitting to the specified platform side hook.
        /// </summary>
        public bool AttachFixedHook(PlatformSideHook targetHook)
        {
            if (targetHook == null || mechanicsController == null || mechanicsController.fixedHookTransform == null)
                return false;

            if (fixedHookRestPos == Vector3.zero)
                CacheRestTransforms();

            fixedAttachedTo = targetHook;
            targetHook.SetOccupied(mechanicsController.fixedHookTransform);
            isFixedHookAttached = true;

            Transform targetAnchor = targetHook.attachTransform != null ? targetHook.attachTransform : targetHook.transform;
            mechanicsController.fixedHookTransform.position = targetAnchor.position;
            mechanicsController.fixedHookTransform.rotation = targetAnchor.rotation;

            Debug.Log($"[PlatformHookAttachment] Fixed J-Hook attached to Platform {targetHook.side} Hook at {targetAnchor.position:F3}.");
            return true;
        }

        /// <summary>
        /// Attaches the adjustable J-hook fitting to the specified platform side hook.
        /// </summary>
        public bool AttachAdjustableHook(PlatformSideHook targetHook)
        {
            if (targetHook == null || mechanicsController == null || mechanicsController.adjustableHookTransform == null)
                return false;

            if (adjHookRestPos == Vector3.zero)
                CacheRestTransforms();

            adjAttachedTo = targetHook;
            targetHook.SetOccupied(mechanicsController.adjustableHookTransform);
            isAdjHookAttached = true;

            Transform targetAnchor = targetHook.attachTransform != null ? targetHook.attachTransform : targetHook.transform;
            mechanicsController.adjustableHookTransform.position = targetAnchor.position;
            mechanicsController.adjustableHookTransform.rotation = targetAnchor.rotation;

            Debug.Log($"[PlatformHookAttachment] Adjustable J-Hook attached to Platform {targetHook.side} Hook at {targetAnchor.position:F3}.");
            return true;
        }

        public bool IsWithinAttachmentRange(out float currentDistance)
        {
            currentDistance = float.MaxValue;
            if (platformHookRight == null || mechanicsController == null || mechanicsController.adjustableHookTransform == null)
                return false;

            Transform targetAnchor = platformHookRight.attachTransform != null ? platformHookRight.attachTransform : platformHookRight.transform;
            currentDistance = Vector3.Distance(mechanicsController.adjustableHookTransform.position, targetAnchor.position);
            return currentDistance <= attachmentProximityThreshold;
        }

        public void SetAdjustableHookDragPosition(Vector3 worldPos)
        {
            if (mechanicsController == null || mechanicsController.adjustableHookTransform == null) return;
            worldPos.z = -4.24f; // Stay aligned across center of mold
            worldPos.y = Mathf.Max(0.14f, worldPos.y); // Don't clip below platform surface
            mechanicsController.adjustableHookTransform.position = worldPos;
        }

        public bool TryAttachAdjustableHookFromDrag(out string resultMessage)
        {
            if (IsWithinAttachmentRange(out float dist))
            {
                AttachAdjustableHook(platformHookRight);
                resultMessage = "<color=#00FFAA><b>★ Belt Secured: Adjustable Hook Attached to Platform Right Anchor! ★</b></color>";
                return true;
            }
            else
            {
                if (adjHookRestPos != Vector3.zero && mechanicsController != null && mechanicsController.adjustableHookTransform != null)
                {
                    mechanicsController.adjustableHookTransform.position = adjHookRestPos;
                    mechanicsController.adjustableHookTransform.rotation = adjHookRestRot;
                }
                resultMessage = $"<color=#FFCC00><b>⚠ Attachment Incomplete: Must release within {attachmentProximityThreshold:F2}m of anchor hook (Current: {dist:F2}m)!</b></color>";
                return false;
            }
        }

        /// <summary>
        /// Detaches the fixed J-hook fitting and restores it to its baseline unhooked position.
        /// </summary>
        public void DetachFixedHook()
        {
            if (!isFixedHookAttached) return;

            if (fixedAttachedTo != null)
            {
                fixedAttachedTo.SetOccupied(null);
                fixedAttachedTo = null;
            }

            isFixedHookAttached = false;

            if (mechanicsController != null && mechanicsController.fixedHookTransform != null && fixedHookRestPos != Vector3.zero)
            {
                mechanicsController.fixedHookTransform.position = fixedHookRestPos;
                mechanicsController.fixedHookTransform.rotation = fixedHookRestRot;

                if (mechanicsController.stitchFixTransform != null)
                {
                    mechanicsController.stitchFixTransform.position = fixedHookRestPos + Vector3.right * 0.05f;
                }
            }

            Debug.Log("[PlatformHookAttachment] Fixed J-Hook detached from vibrating platform.");
        }

        /// <summary>
        /// Detaches the adjustable J-hook fitting and restores it to its baseline unhooked position.
        /// </summary>
        public void DetachAdjustableHook()
        {
            if (!isAdjHookAttached) return;

            if (adjAttachedTo != null)
            {
                adjAttachedTo.SetOccupied(null);
                adjAttachedTo = null;
            }

            isAdjHookAttached = false;

            if (mechanicsController != null && mechanicsController.adjustableHookTransform != null && adjHookRestPos != Vector3.zero)
            {
                mechanicsController.adjustableHookTransform.position = adjHookRestPos;
                mechanicsController.adjustableHookTransform.rotation = adjHookRestRot;

                if (mechanicsController.stitchAdjTransform != null)
                {
                    mechanicsController.stitchAdjTransform.position = adjHookRestPos - Vector3.right * 0.05f;
                }
            }

            Debug.Log("[PlatformHookAttachment] Adjustable J-Hook detached from vibrating platform.");
        }

        /// <summary>
        /// Toggles attachment of the fixed hook to the nearest available platform side hook.
        /// </summary>
        public void ToggleFixedHook()
        {
            if (isFixedHookAttached)
            {
                DetachFixedHook();
            }
            else
            {
                PlatformSideHook target = (platformHookLeft != null && !platformHookLeft.IsOccupied) ? platformHookLeft : platformHookRight;
                if (target != null) AttachFixedHook(target);
            }
        }

        /// <summary>
        /// Toggles attachment of the adjustable hook to the nearest available platform side hook.
        /// </summary>
        public void ToggleAdjustableHook()
        {
            if (isAdjHookAttached)
            {
                DetachAdjustableHook();
            }
            else
            {
                PlatformSideHook target = (platformHookRight != null && !platformHookRight.IsOccupied) ? platformHookRight : platformHookLeft;
                if (target != null) AttachAdjustableHook(target);
            }
        }
    }
}
