using System;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace Sefpro.JawTongs
{
    /// <summary>
    /// Coordinates physical two-arm scissor mechanics, VR grab solver stabilization,
    /// and rigid visual synchronization for the SEFPRO Jaw Tongs assembly while preserving
    /// the imported Jaw_Tongs_5 prefab hierarchy intact.
    /// </summary>
    [DisallowMultipleComponent]
    public class JawTongsController : MonoBehaviour
    {
        [Serializable]
        public struct VisualPartBinding
        {
            public Transform visualTransform;
            public Vector3 localPositionOffset;
            public Quaternion localRotationOffset;
        }

        [Header("Articulated Tong Arms")]
        [Tooltip("Rigidbody for Arm 1 (Left lower jaw / Left handle / Right upper chain shackle).")]
        public Rigidbody leftJawArmBody;

        [Tooltip("Rigidbody for Arm 2 (Right lower jaw / Right handle / Left upper chain shackle).")]
        public Rigidbody rightJawArmBody;

        [Tooltip("Mechanical scissor HingeJoint at Centre_Bolt connecting the two arms.")]
        public HingeJoint centrePivotJoint;

        [Header("VR Grab Interactables")]
        public XRGrabInteractable leftArmGrabInteractable;
        public XRGrabInteractable rightArmGrabInteractable;

        [Header("Jaw Measurement Reference Points")]
        public Transform leftJawInnerPadPoint;
        public Transform rightJawInnerPadPoint;

        [Header("Solver Tuning")]
        [SerializeField] private int restingSolverIterations = 16;
        [SerializeField] private int restingVelocityIterations = 8;
        [SerializeField] private int grabbedSolverIterations = 28;
        [SerializeField] private int grabbedVelocityIterations = 12;

        [Header("Visual Synchronization Bindings")]
        public VisualPartBinding[] leftJawArmVisuals = Array.Empty<VisualPartBinding>();
        public VisualPartBinding[] rightJawArmVisuals = Array.Empty<VisualPartBinding>();

        public bool IsLeftArmGrabbed => leftArmGrabInteractable != null && leftArmGrabInteractable.isSelected;
        public bool IsRightArmGrabbed => rightArmGrabInteractable != null && rightArmGrabInteractable.isSelected;
        public bool IsAnyArmGrabbed => IsLeftArmGrabbed || IsRightArmGrabbed;

        public float CurrentHingeAngleDegrees => centrePivotJoint != null ? centrePivotJoint.angle : 0f;

        public float CurrentJawGapMeters
        {
            get
            {
                if (leftJawInnerPadPoint == null || rightJawInnerPadPoint == null)
                    return 0f;
                return Vector3.Distance(leftJawInnerPadPoint.position, rightJawInnerPadPoint.position);
            }
        }

        private void Awake()
        {
            ConfigureInternalCollisionIgnores();
            ApplySolverIterations(false);
            SyncAllVisuals();
        }

        private void OnEnable()
        {
            if (leftArmGrabInteractable != null)
            {
                leftArmGrabInteractable.selectEntered.AddListener(OnArmSelectEntered);
                leftArmGrabInteractable.selectExited.AddListener(OnArmSelectExited);
            }

            if (rightArmGrabInteractable != null)
            {
                rightArmGrabInteractable.selectEntered.AddListener(OnArmSelectEntered);
                rightArmGrabInteractable.selectExited.AddListener(OnArmSelectExited);
            }

            UpdateGrabbedSolverState();
        }

        private void OnDisable()
        {
            if (leftArmGrabInteractable != null)
            {
                leftArmGrabInteractable.selectEntered.RemoveListener(OnArmSelectEntered);
                leftArmGrabInteractable.selectExited.RemoveListener(OnArmSelectExited);
            }

            if (rightArmGrabInteractable != null)
            {
                rightArmGrabInteractable.selectEntered.RemoveListener(OnArmSelectEntered);
                rightArmGrabInteractable.selectExited.RemoveListener(OnArmSelectExited);
            }
        }

        private void FixedUpdate()
        {
            SyncAllVisuals();
        }

        private void LateUpdate()
        {
            SyncAllVisuals();
        }

        public void SyncAllVisuals()
        {
            if (leftJawArmBody != null)
                SyncVisualGroup(leftJawArmBody.transform, leftJawArmVisuals);

            if (rightJawArmBody != null)
                SyncVisualGroup(rightJawArmBody.transform, rightJawArmVisuals);
        }

        private static void SyncVisualGroup(Transform bodyTransform, VisualPartBinding[] bindings)
        {
            if (bindings == null)
                return;

            Vector3 bodyPos = bodyTransform.position;
            Quaternion bodyRot = bodyTransform.rotation;

            for (int i = 0; i < bindings.Length; i++)
            {
                Transform vis = bindings[i].visualTransform;
                if (vis == null)
                    continue;

                vis.SetPositionAndRotation(
                    bodyPos + bodyRot * bindings[i].localPositionOffset,
                    bodyRot * bindings[i].localRotationOffset);
            }
        }

        private void ConfigureInternalCollisionIgnores()
        {
            if (leftJawArmBody == null || rightJawArmBody == null)
                return;

            Collider[] leftCols = leftJawArmBody.GetComponentsInChildren<Collider>(true);
            Collider[] rightCols = rightJawArmBody.GetComponentsInChildren<Collider>(true);

            for (int i = 0; i < leftCols.Length; i++)
            {
                if (leftCols[i] == null) continue;
                for (int j = 0; j < rightCols.Length; j++)
                {
                    if (rightCols[j] == null) continue;
                    Physics.IgnoreCollision(leftCols[i], rightCols[j], true);
                }
            }
        }

        private void OnArmSelectEntered(SelectEnterEventArgs args)
        {
            UpdateGrabbedSolverState();
        }

        private void OnArmSelectExited(SelectExitEventArgs args)
        {
            UpdateGrabbedSolverState();
        }

        private void UpdateGrabbedSolverState()
        {
            ApplySolverIterations(IsAnyArmGrabbed);
        }

        private void ApplySolverIterations(bool grabbed)
        {
            int posIter = grabbed ? grabbedSolverIterations : restingSolverIterations;
            int velIter = grabbed ? grabbedVelocityIterations : restingVelocityIterations;

            if (leftJawArmBody != null)
            {
                leftJawArmBody.solverIterations = posIter;
                leftJawArmBody.solverVelocityIterations = velIter;
            }

            if (rightJawArmBody != null)
            {
                rightJawArmBody.solverIterations = posIter;
                rightJawArmBody.solverVelocityIterations = velIter;
            }
        }
    }
}
