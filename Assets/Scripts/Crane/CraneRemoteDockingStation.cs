using UnityEngine;
using TMPro;

namespace Sefpro.Crane
{
    /// <summary>
    /// Physical industrial docking / parking holder for the handheld overhead crane remote.
    /// Evaluates valid docking placement ONLY after the remote has genuinely exited Left-Hand selection,
    /// and updates UI strictly from verified physical state:
    ///   - REMOTE IN USE   : remote is currently selected / held by the hand
    ///   - DOCKING         : remote has detached from the hand and is actively gliding toward DockAnchor
    ///   - REMOTE PARKED   : remote is detached, settled stably, and physically verified at DockAnchor
    ///   - RELEASE TO DOCK : remote is detached outside the dock (not held, not docking, not at DockAnchor)
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class CraneRemoteDockingStation : MonoBehaviour
    {
        public enum DockStatusMode
        {
            Parked,
            InUse,
            ReleasedUndocked,
            Docking
        }

        [Header("References")]
        [Tooltip("The handheld crane remote associated with this docking holder.")]
        public FreeFloatingCraneRemote targetRemote;

        [Tooltip("Exact designated parked pose anchor inside the holder cradle.")]
        public Transform dockAnchor;

        [Tooltip("Optional status label on the holder header.")]
        public TMP_Text dockStatusLabel;

        [Tooltip("Optional status LED bar renderer on the holder header.")]
        public Renderer dockStatusLedRenderer;

        [Header("Dock Anchor Local Pose")]
        public Vector3 dockAnchorLocalPosition = new Vector3(0.000f, 1.150f, 0.045f);
        public Vector3 dockAnchorLocalEuler = new Vector3(-18.0f, 0.0f, 0.0f);

        [Header("Release-to-Dock Region & Pose Verification")]
        [Tooltip("Half-extents (m) in DockAnchor local space defining the valid docking capture region around the cradle.")]
        public Vector3 cradlePocketHalfExtents = new Vector3(0.180f, 0.220f, 0.200f);

        [Tooltip("Maximum 3D Euclidean distance (m) from DockAnchor to Remote center when released to initiate docking.")]
        public float maxDockingDistance = 0.250f;

        [Tooltip("Maximum angle (degrees) between Remote +Y (top) and DockAnchor +Y (allows natural wrist pitch while rejecting upside-down poses).")]
        public float maxUpAxisAngleDegrees = 80f;

        [Tooltip("Maximum angle (degrees) between Remote +Z (front button panel) and DockAnchor +Z (rejects backwards placement).")]
        public float maxForwardAxisAngleDegrees = 85f;

        [Tooltip("Duration (s) of the smooth physical movement into DockAnchor after grip release.")]
        public float gentleSnapDuration = 0.18f;

        [Tooltip("Maximum position tolerance (m) required to verify the remote has physically reached DockAnchor.")]
        public float parkedVerifyPositionTolerance = 0.002f;

        [Tooltip("Maximum rotation tolerance (degrees) required to verify the remote has physically reached DockAnchor.")]
        public float parkedVerifyAngleTolerance = 0.5f;

        [Header("Read-Only Live State")]
        [SerializeField] private DockStatusMode currentStatusMode = DockStatusMode.Parked;
        [SerializeField] private string lastDockingEvaluationMessage = string.Empty;

        private MaterialPropertyBlock _ledPropBlock;

        public DockStatusMode CurrentStatusMode => currentStatusMode;
        public string LastDockingEvaluationMessage => lastDockingEvaluationMessage;

        public Vector3 DockWorldPosition => EnsureDockAnchor().position;
        public Quaternion DockWorldRotation => EnsureDockAnchor().rotation;

        private void Awake()
        {
            EnsureDockAnchor();
            ResolveRemoteReference();
            IgnoreCollisionsWithRemote();
            RefreshActualPhysicalStatus();
        }

        private void OnEnable()
        {
            EnsureDockAnchor();
            ResolveRemoteReference();
            IgnoreCollisionsWithRemote();
            RefreshActualPhysicalStatus();
        }

        public Transform EnsureDockAnchor()
        {
            if (dockAnchor == null)
            {
                dockAnchor = transform.Find("DockAnchor");
                if (dockAnchor == null)
                {
                    var go = new GameObject("DockAnchor");
                    dockAnchor = go.transform;
                    dockAnchor.SetParent(transform, false);
                }
            }

            Quaternion targetLocalRot = Quaternion.Euler(dockAnchorLocalEuler);
            if ((dockAnchor.localPosition - dockAnchorLocalPosition).sqrMagnitude > 1e-10f)
                dockAnchor.localPosition = dockAnchorLocalPosition;
            if (Quaternion.Angle(dockAnchor.localRotation, targetLocalRot) > 1e-4f)
                dockAnchor.localRotation = targetLocalRot;
            if ((dockAnchor.localScale - Vector3.one).sqrMagnitude > 1e-10f)
                dockAnchor.localScale = Vector3.one;

            return dockAnchor;
        }

        public void ResolveRemoteReference()
        {
            if (targetRemote == null)
            {
                targetRemote = FindAnyObjectByType<FreeFloatingCraneRemote>();
            }
            if (targetRemote != null && targetRemote.dockingStation == null)
            {
                targetRemote.dockingStation = this;
            }
        }

        public void IgnoreCollisionsWithRemote()
        {
            if (!Application.isPlaying || targetRemote == null)
                return;

            var holderCols = GetComponentsInChildren<Collider>(true);
            var remoteCols = targetRemote.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < holderCols.Length; i++)
            {
                for (int j = 0; j < remoteCols.Length; j++)
                {
                    if (holderCols[i] != null && remoteCols[j] != null)
                    {
                        Physics.IgnoreCollision(holderCols[i], remoteCols[j], true);
                    }
                }
            }
        }

        /// <summary>
        /// Checks whether the released Remote lies inside the valid cradle docking region and is oriented upright/forward.
        /// </summary>
        public bool IsWithinValidDockingRegion(Transform remoteTf, out string reason)
        {
            reason = string.Empty;
            if (remoteTf == null)
            {
                reason = "Remote transform is null.";
                return false;
            }

            Transform anchor = EnsureDockAnchor();
            Vector3 localOffset = anchor.InverseTransformPoint(remoteTf.position);
            float dist = Vector3.Distance(remoteTf.position, anchor.position);

            if (Mathf.Abs(localOffset.x) > cradlePocketHalfExtents.x ||
                Mathf.Abs(localOffset.y) > cradlePocketHalfExtents.y ||
                Mathf.Abs(localOffset.z) > cradlePocketHalfExtents.z ||
                dist > maxDockingDistance)
            {
                reason = $"Outside valid docking region: local=({localOffset.x:F3}, {localOffset.y:F3}, {localOffset.z:F3})m, dist={dist:F3}m (max {maxDockingDistance:F3}m).";
                lastDockingEvaluationMessage = reason;
                return false;
            }

            float upAngle = Vector3.Angle(remoteTf.up, anchor.up);
            if (upAngle > maxUpAxisAngleDegrees)
            {
                reason = $"Remote vertical tilt misaligned: upAngle={upAngle:F1}° > {maxUpAxisAngleDegrees:F1}°.";
                lastDockingEvaluationMessage = reason;
                return false;
            }

            float fwdAngle = Vector3.Angle(remoteTf.forward, anchor.forward);
            if (fwdAngle > maxForwardAxisAngleDegrees)
            {
                reason = $"Remote button-face orientation misaligned: fwdAngle={fwdAngle:F1}° > {maxForwardAxisAngleDegrees:F1}°.";
                lastDockingEvaluationMessage = reason;
                return false;
            }

            lastDockingEvaluationMessage = $"Valid docking region verified (dist={dist:F3}m, upAngle={upAngle:F1}°, fwdAngle={fwdAngle:F1}°).";
            return true;
        }

        /// <summary>
        /// Verifies that the Remote transform has physically reached DockAnchor within strict position and angle tolerances.
        /// </summary>
        public bool IsRemotePhysicallyAtAnchor(Transform remoteTf)
        {
            if (remoteTf == null)
                return false;

            Transform anchor = EnsureDockAnchor();
            float posErr = Vector3.Distance(remoteTf.position, anchor.position);
            float angErr = Quaternion.Angle(remoteTf.rotation, anchor.rotation);
            return posErr <= parkedVerifyPositionTolerance && angErr <= parkedVerifyAngleTolerance;
        }

        private void Update()
        {
            EnsureDockAnchor();
            if (targetRemote == null)
                ResolveRemoteReference();

            RefreshActualPhysicalStatus();
        }

        /// <summary>
        /// Computes and displays UI state strictly from actual physical state:
        /// - REMOTE IN USE   : remote is currently held/selected.
        /// - DOCKING         : remote has detached from hand and is physically moving toward DockAnchor.
        /// - REMOTE PARKED   : remote is not held, not moving, IsDocked == true, AND its physical pose at DockAnchor is verified.
        /// - RELEASE TO DOCK : remote is released outside the dock and not yet docked.
        /// </summary>
        public void RefreshActualPhysicalStatus()
        {
            if (targetRemote == null)
            {
                UpdateVisualFeedback();
                return;
            }

            if (!Application.isPlaying)
            {
                currentStatusMode = (targetRemote.IsDocked && IsRemotePhysicallyAtAnchor(targetRemote.transform))
                    ? DockStatusMode.Parked
                    : DockStatusMode.ReleasedUndocked;
                UpdateVisualFeedback();
                return;
            }

            if (targetRemote.IsHeld)
            {
                currentStatusMode = DockStatusMode.InUse;
            }
            else if (targetRemote.IsDocking)
            {
                currentStatusMode = DockStatusMode.Docking;
            }
            else if (targetRemote.IsDocked && IsRemotePhysicallyAtAnchor(targetRemote.transform))
            {
                currentStatusMode = DockStatusMode.Parked;
            }
            else
            {
                currentStatusMode = DockStatusMode.ReleasedUndocked;
            }

            UpdateVisualFeedback();
        }

        public void UpdateVisualFeedback()
        {
            string text;
            Color color;

            switch (currentStatusMode)
            {
                case DockStatusMode.Parked:
                    text = "REMOTE PARKED";
                    color = new Color(0.15f, 0.95f, 0.45f, 1.0f);
                    break;
                case DockStatusMode.InUse:
                    text = "REMOTE IN USE";
                    color = new Color(0.35f, 0.78f, 1.00f, 1.0f);
                    break;
                case DockStatusMode.Docking:
                    text = "DOCKING";
                    color = new Color(1.00f, 0.85f, 0.20f, 1.0f);
                    break;
                case DockStatusMode.ReleasedUndocked:
                default:
                    text = "RELEASE TO DOCK";
                    color = new Color(1.00f, 0.65f, 0.20f, 1.0f);
                    break;
            }

            if (dockStatusLabel != null)
            {
                if (dockStatusLabel.text != text)
                    dockStatusLabel.text = text;
                dockStatusLabel.color = color;
            }

            if (dockStatusLedRenderer != null)
            {
                if (_ledPropBlock == null)
                    _ledPropBlock = new MaterialPropertyBlock();
                dockStatusLedRenderer.GetPropertyBlock(_ledPropBlock);
                _ledPropBlock.SetColor("_BaseColor", color);
                _ledPropBlock.SetColor("_Color", color);
                _ledPropBlock.SetColor("_EmissionColor", color * 1.4f);
                dockStatusLedRenderer.SetPropertyBlock(_ledPropBlock);
            }
        }
    }
}
