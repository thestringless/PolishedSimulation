using System;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace Sefpro.JawTongs
{
    /// <summary>
    /// Coordinates physical two-arm scissor mechanics, locking-pin restraint and release,
    /// pivoting end-shackle alignment, VR grab solver stabilization, and rigid visual synchronization
    /// for the SEFPRO Jaw Tongs assembly while preserving the imported Jaw_Tongs_5 hierarchy intact.
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

        [Header("Locking Pin (Jaw Restraint)")]
        [Tooltip("When true, the locking pin holds both scissor arms in their secured horizontal configuration.")]
        [SerializeField] private bool isPinEngaged = true;

        [Tooltip("Transform of the locking Pin mesh (Jaw_Tongs_5/Pin).")]
        public Transform pinTransform;

        [Tooltip("Kinematic Rigidbody on the locking Pin.")]
        public Rigidbody pinRigidbody;

        [Tooltip("XRGrabInteractable on the locking Pin for VR hand removal/insertion.")]
        public XRGrabInteractable pinGrabInteractable;

        [Tooltip("Pull distance in metres required to release the locking pin when grabbed in VR.")]
        public float pinReleaseDistance = 0.020f;

        [Tooltip("Local position offset of the Pin on leftJawArmBody when engaged.")]
        public Vector3 pinSeatedLocalOffset = new Vector3(0.3919f, -0.0063f, 0.0292f);

        [Tooltip("Local position offset of the Pin on leftJawArmBody when withdrawn/released.")]
        public Vector3 pinReleasedLocalOffset = new Vector3(0.3919f, -0.0063f, 0.0950f);

        [Tooltip("Local rotation offset of the Pin on leftJawArmBody.")]
        public Quaternion pinLocalRotationOffset = Quaternion.Euler(0f, 270f, 0f);

        [Header("VR Grab Interactables")]
        public XRGrabInteractable leftArmGrabInteractable;
        public XRGrabInteractable rightArmGrabInteractable;
        [Tooltip("Primary VR grab interactable on Ring_Mesh so grabbing the ring mesh holds and moves the tongs assembly.")]
        public XRGrabInteractable ringGrabInteractable;

        [Header("Ring Grab Suspension")]
        [Tooltip("Chain rope whose midpoint is suspended by the ring grab interactable while held.")]
        public Obi.ObiRope chainRope;
        [Tooltip("Target transform for the chain's middle particle attachment.")]
        public Transform chainMidpointAnchor;
        [Tooltip("Safety margin removed from each chain leg length while held by the ring (metres).")]
        public float ringGrabChainLegSlack = 0.014f;
        [Tooltip("Angular damping applied to the tong arms while held by the ring.")]
        public float ringGrabbedArmAngularDamping = 0.8f;

        [Header("Jaw Measurement Reference Points")]
        public Transform leftJawInnerPadPoint;
        public Transform rightJawInnerPadPoint;

        [Header("Solver Tuning")]
        [SerializeField] private int restingSolverIterations = 20;
        [SerializeField] private int restingVelocityIterations = 10;
        [SerializeField] private int grabbedSolverIterations = 28;
        [SerializeField] private int grabbedVelocityIterations = 12;

        [Header("Visual Synchronization Bindings")]
        public VisualPartBinding[] leftJawArmVisuals = Array.Empty<VisualPartBinding>();
        public VisualPartBinding[] rightJawArmVisuals = Array.Empty<VisualPartBinding>();

        public bool IsLeftArmGrabbed => leftArmGrabInteractable != null && leftArmGrabInteractable.isSelected;
        public bool IsRightArmGrabbed => rightArmGrabInteractable != null && rightArmGrabInteractable.isSelected;
        public bool IsRingGrabbed => ringGrabInteractable != null && ringGrabInteractable.isSelected;
        public bool IsPinGrabbed => pinGrabInteractable != null && pinGrabInteractable.isSelected;
        public bool IsAnyArmGrabbed => IsLeftArmGrabbed || IsRightArmGrabbed || IsRingGrabbed;
        public bool IsPinEngaged => isPinEngaged;
        public bool IsClampedOnBlock => isClampedOnBlock;

        public event Action<bool> OnPinStateChanged;

        private ConfigurableJoint coaxialPivotJoint;
        private ConfigurableJoint ringGrabJointLeft;
        private ConfigurableJoint ringGrabJointRight;
        private float savedLeftAngDamp;
        private float savedRightAngDamp;
        private bool isClampedOnBlock = false;
        private float clampedBlockAngleDegrees = 10f;

        private Transform leftShackleObiAnchor;
        private Transform rightShackleObiAnchor;

        // Arm eyelet pivot offsets and rest shackle geometry
        public static readonly Vector3 LeftArmEyeletLocal = new Vector3(-0.5473f, 0.0407f, 0.0180f);   // on rightJawArmBody (Arm_2)
        public static readonly Vector3 RightArmEyeletLocal = new Vector3(0.5387f, 0.0408f, -0.0087f);  // on leftJawArmBody (Arm_1)
        private static readonly Vector3 LeftShackleRestLocal = new Vector3(-0.4548f, 0.1032f, 0.0190f);
        private static readonly Vector3 RightShackleRestLocal = new Vector3(0.4502f, 0.1062f, 0.0020f);
        private static readonly Vector3 LeftShackleRestDir2D = new Vector3(0.0925f, 0.0625f, 0f).normalized;
        private static readonly Vector3 RightShackleRestDir2D = new Vector3(-0.0885f, 0.0654f, 0f).normalized;
        public const float EndShackleLengthMeters = 0.111f;

        public float CurrentHingeAngleDegrees
        {
            get
            {
                float angle = centrePivotJoint != null ? centrePivotJoint.angle : float.NaN;
                if (float.IsNaN(angle) || float.IsInfinity(angle))
                {
                    if (leftJawArmBody != null && rightJawArmBody != null)
                        angle = Mathf.DeltaAngle(leftJawArmBody.transform.eulerAngles.z, rightJawArmBody.transform.eulerAngles.z);
                    else
                        angle = 0f;
                }
                return angle;
            }
        }

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
            ResolveComponentReferences();
            ConfigureNaturalArmDamping();
            ConfigureInternalCollisionIgnores();
            EnsureCoaxialPivotAndPinState();
            ApplySolverIterations(false);
            SyncAllVisuals();
        }

        private void OnEnable()
        {
            ResolveComponentReferences();
            ConfigureInternalCollisionIgnores();
            EnsureCoaxialPivotAndPinState();

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

            if (ringGrabInteractable != null)
            {
                ringGrabInteractable.selectEntered.AddListener(OnRingSelectEntered);
                ringGrabInteractable.selectExited.AddListener(OnRingSelectExited);
            }

            if (pinGrabInteractable != null)
            {
                pinGrabInteractable.selectEntered.AddListener(OnPinSelectEntered);
                pinGrabInteractable.selectExited.AddListener(OnPinSelectExited);
                pinGrabInteractable.activated.AddListener(OnPinActivated);
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

            if (ringGrabInteractable != null)
            {
                ringGrabInteractable.selectEntered.RemoveListener(OnRingSelectEntered);
                ringGrabInteractable.selectExited.RemoveListener(OnRingSelectExited);
            }

            if (pinGrabInteractable != null)
            {
                pinGrabInteractable.selectEntered.RemoveListener(OnPinSelectEntered);
                pinGrabInteractable.selectExited.RemoveListener(OnPinSelectExited);
                pinGrabInteractable.activated.RemoveListener(OnPinActivated);
            }

            ReleaseRingSuspensionJoints();
        }

        public void ResolveComponentReferences()
        {
            if (pinTransform == null)
            {
                var t = transform.Find("Jaw_Tongs_5/TongSuspensionAnchor/Pin") ?? transform.Find("Jaw_Tongs_5/Pin");
                if (t == null)
                {
                    foreach (var child in GetComponentsInChildren<Transform>(true))
                    {
                        if (child.name == "Pin") { t = child; break; }
                    }
                }
                if (t != null) pinTransform = t;
            }

            if (pinTransform != null)
            {
                if (pinRigidbody == null) pinRigidbody = pinTransform.GetComponent<Rigidbody>();
                if (pinGrabInteractable == null) pinGrabInteractable = pinTransform.GetComponent<XRGrabInteractable>();
            }

            if (rightJawArmBody != null && leftShackleObiAnchor == null)
            {
                var t = rightJawArmBody.transform.Find("LeftShackle_ObiAnchor");
                if (t != null) leftShackleObiAnchor = t;
            }

            if (leftJawArmBody != null && rightShackleObiAnchor == null)
            {
                var t = leftJawArmBody.transform.Find("RightShackle_ObiAnchor");
                if (t != null) rightShackleObiAnchor = t;
            }
        }

        private void ConfigureNaturalArmDamping()
        {
            // Ensure natural gravity fall speed when pin is released (avoid excessive 4.5-6.0 angular drag)
            if (leftJawArmBody != null)
            {
                if (leftJawArmBody.linearDamping > 0.8f) leftJawArmBody.linearDamping = 0.5f;
                if (leftJawArmBody.angularDamping > 1.2f) leftJawArmBody.angularDamping = 0.6f;
            }

            if (rightJawArmBody != null)
            {
                if (rightJawArmBody.linearDamping > 0.8f) rightJawArmBody.linearDamping = 0.5f;
                if (rightJawArmBody.angularDamping > 1.2f) rightJawArmBody.angularDamping = 0.6f;
            }
        }

        private void EnsureCoaxialPivotAndPinState()
        {
            if (!Application.isPlaying || leftJawArmBody == null || rightJawArmBody == null)
                return;

            if (centrePivotJoint != null)
            {
                centrePivotJoint.useSpring = false;
            }

            if (coaxialPivotJoint == null)
            {
                foreach (var cj in rightJawArmBody.GetComponents<ConfigurableJoint>())
                {
                    if (cj.connectedBody == leftJawArmBody)
                    {
                        coaxialPivotJoint = cj;
                        break;
                    }
                }
            }

            if (coaxialPivotJoint == null)
            {
                coaxialPivotJoint = rightJawArmBody.gameObject.AddComponent<ConfigurableJoint>();
                coaxialPivotJoint.autoConfigureConnectedAnchor = false;
                coaxialPivotJoint.connectedBody = leftJawArmBody;
                coaxialPivotJoint.anchor = Vector3.zero;
                coaxialPivotJoint.connectedAnchor = Vector3.zero;
                coaxialPivotJoint.axis = new Vector3(0f, 0f, 1f);
                coaxialPivotJoint.secondaryAxis = new Vector3(1f, 0f, 0f);
                coaxialPivotJoint.xMotion = ConfigurableJointMotion.Locked;
                coaxialPivotJoint.yMotion = ConfigurableJointMotion.Locked;
                coaxialPivotJoint.zMotion = ConfigurableJointMotion.Locked;
                coaxialPivotJoint.angularYMotion = ConfigurableJointMotion.Locked;
                coaxialPivotJoint.angularZMotion = ConfigurableJointMotion.Locked;
                coaxialPivotJoint.projectionMode = JointProjectionMode.PositionAndRotation;
                coaxialPivotJoint.projectionDistance = 0.0005f;
                coaxialPivotJoint.projectionAngle = 0.2f;
                coaxialPivotJoint.enableCollision = false;
            }

            coaxialPivotJoint.angularXMotion = (isPinEngaged || isClampedOnBlock)
                ? ConfigurableJointMotion.Locked
                : ConfigurableJointMotion.Free;
        }

        /// <summary>
        /// Releases the lifting/locking pin so both jaw ends are unlocked and fall naturally under gravity.
        /// </summary>
        public void ReleasePin()
        {
            if (!isPinEngaged && !isClampedOnBlock && coaxialPivotJoint != null && coaxialPivotJoint.angularXMotion == ConfigurableJointMotion.Free)
                return;

            isPinEngaged = false;
            isClampedOnBlock = false;

            if (coaxialPivotJoint != null)
            {
                coaxialPivotJoint.targetRotation = Quaternion.identity;
                coaxialPivotJoint.angularXMotion = ConfigurableJointMotion.Free;
            }

            if (centrePivotJoint != null)
            {
                centrePivotJoint.useSpring = false;
            }

            if (leftJawArmBody != null)
            {
                leftJawArmBody.isKinematic = false;
                leftJawArmBody.useGravity = true;
                leftJawArmBody.WakeUp();
            }

            if (rightJawArmBody != null)
            {
                rightJawArmBody.isKinematic = false;
                rightJawArmBody.useGravity = true;
                rightJawArmBody.WakeUp();
            }

            SyncAllVisuals();
            OnPinStateChanged?.Invoke(false);
            Debug.Log("[JawTongsController] Locking pin RELEASED — both jaw ends unlocked to fall under gravity.");
        }

        /// <summary>
        /// Locks or unlocks the scissor arms at a specific clamping angle around the solidified refractory brick
        /// without applying continuous opposing torques that would fight PhysX or deform the Obi chain.
        /// </summary>
        public void SetBlockClampedPose(bool clamped, float armAngleDegrees = 10f)
        {
            EnsureCoaxialPivotAndPinState();
            if (leftJawArmBody == null || rightJawArmBody == null || coaxialPivotJoint == null)
                return;

            if (clamped)
            {
                isClampedOnBlock = true;
                clampedBlockAngleDegrees = armAngleDegrees;

                Vector3 pivotMid = (leftJawArmBody.transform.position + rightJawArmBody.transform.position) * 0.5f;
                float yaw = leftJawArmBody.transform.eulerAngles.y;

                // If suspended from chain apex, place pivotMid at the geometric equilibrium height for this arm angle
                if (Application.isPlaying && chainRope != null && chainRope.isLoaded && chainRope.activeParticleCount >= 3)
                {
                    Vector3 pMid = chainRope.GetParticlePosition(chainRope.solverIndices[chainRope.activeParticleCount / 2]);
                    if (pMid.y > pivotMid.y + 0.15f)
                    {
                        Vector3 lEye = Quaternion.Euler(0f, 0f, -armAngleDegrees) * LeftArmEyeletLocal;
                        float legLen = (chainRope.restLength * 0.5f + EndShackleLengthMeters) - ringGrabChainLegSlack;
                        float eyeHoriz = Mathf.Abs(lEye.x);
                        float vertFromEyeletToPin = Mathf.Sqrt(Mathf.Max(0f, legLen * legLen - eyeHoriz * eyeHoriz));
                        float pivotBelowPin = vertFromEyeletToPin + lEye.y;
                        pivotMid = new Vector3(pMid.x, pMid.y - pivotBelowPin, pivotMid.z);
                    }
                }

                Quaternion leftRot = Quaternion.Euler(0f, yaw, armAngleDegrees);
                Quaternion rightRot = Quaternion.Euler(0f, yaw, -armAngleDegrees);

                // Temporarily unlock angularXMotion, snap both arms to the exact clamping angle around the brick, and re-lock
                coaxialPivotJoint.angularXMotion = ConfigurableJointMotion.Free;
                DestroyImmediate(coaxialPivotJoint);
                coaxialPivotJoint = null;

                leftJawArmBody.transform.SetPositionAndRotation(pivotMid, leftRot);
                rightJawArmBody.transform.SetPositionAndRotation(pivotMid, rightRot);
                leftJawArmBody.position = pivotMid;
                leftJawArmBody.rotation = leftRot;
                rightJawArmBody.position = pivotMid;
                rightJawArmBody.rotation = rightRot;

                if (!leftJawArmBody.isKinematic)
                {
                    leftJawArmBody.linearVelocity = Vector3.zero;
                    leftJawArmBody.angularVelocity = Vector3.zero;
                }
                if (!rightJawArmBody.isKinematic)
                {
                    rightJawArmBody.linearVelocity = Vector3.zero;
                    rightJawArmBody.angularVelocity = Vector3.zero;
                }
                Physics.SyncTransforms();

                // Recreate coaxialPivotJoint in this exact clamped relative pose so Locked holds the jaws at armAngleDegrees
                EnsureCoaxialPivotAndPinState();
                if (coaxialPivotJoint != null)
                {
                    coaxialPivotJoint.angularXMotion = ConfigurableJointMotion.Locked;
                }

                SyncAllVisuals();
            }
            else if (isClampedOnBlock)
            {
                isClampedOnBlock = false;
                if (coaxialPivotJoint != null)
                {
                    coaxialPivotJoint.angularXMotion = isPinEngaged
                        ? ConfigurableJointMotion.Locked
                        : ConfigurableJointMotion.Free;
                }
                if (leftJawArmBody != null) leftJawArmBody.WakeUp();
                if (rightJawArmBody != null) rightJawArmBody.WakeUp();
                SyncAllVisuals();
            }
        }

        /// <summary>
        /// Engages the lifting/locking pin, securing both jaw ends in their closed/latched horizontal configuration.
        /// </summary>
        public void EngagePin(bool alignArmsToSecuredPose = true)
        {
            isClampedOnBlock = false;
            if (leftJawArmBody != null && rightJawArmBody != null && alignArmsToSecuredPose)
            {
                if (coaxialPivotJoint != null)
                {
                    DestroyImmediate(coaxialPivotJoint);
                    coaxialPivotJoint = null;
                }
                Vector3 pivotMid = (leftJawArmBody.transform.position + rightJawArmBody.transform.position) * 0.5f;
                float yaw = leftJawArmBody.transform.eulerAngles.y;
                Quaternion securedRot = Quaternion.Euler(0f, yaw, 0f);

                // If suspended from chain apex, place pivotMid at the geometric horizontal triangle height below p[12]
                if (Application.isPlaying && chainRope != null && chainRope.isLoaded && chainRope.activeParticleCount >= 3)
                {
                    Vector3 pMid = chainRope.GetParticlePosition(chainRope.solverIndices[chainRope.activeParticleCount / 2]);
                    if (pMid.y > pivotMid.y + 0.15f)
                    {
                        // Horizontal eyelet offset is ~0.543m, leg length is ~0.6035m -> vertical drop from p[12] to eyelet is ~0.263m, plus 0.0408m to Centre_Bolt = 0.304m
                        pivotMid = new Vector3(pMid.x, pMid.y - 0.304f, pivotMid.z);
                    }
                }

                leftJawArmBody.transform.SetPositionAndRotation(pivotMid, securedRot);
                rightJawArmBody.transform.SetPositionAndRotation(pivotMid, securedRot);
                leftJawArmBody.position = pivotMid;
                leftJawArmBody.rotation = securedRot;
                rightJawArmBody.position = pivotMid;
                rightJawArmBody.rotation = securedRot;

                if (!leftJawArmBody.isKinematic)
                {
                    leftJawArmBody.linearVelocity = Vector3.zero;
                    leftJawArmBody.angularVelocity = Vector3.zero;
                }
                if (!rightJawArmBody.isKinematic)
                {
                    rightJawArmBody.linearVelocity = Vector3.zero;
                    rightJawArmBody.angularVelocity = Vector3.zero;
                }
                Physics.SyncTransforms();
            }

            isPinEngaged = true;
            EnsureCoaxialPivotAndPinState();

            if (leftJawArmBody != null) leftJawArmBody.WakeUp();
            if (rightJawArmBody != null) rightJawArmBody.WakeUp();

            SyncAllVisuals();
            OnPinStateChanged?.Invoke(true);
            Debug.Log("[JawTongsController] Locking pin ENGAGED — both jaw ends secured.");
        }

        public void TogglePin()
        {
            if (isPinEngaged)
                ReleasePin();
            else
                EngagePin(true);
        }

        private void FixedUpdate()
        {
            if (IsRingGrabbed && ringGrabInteractable != null && chainMidpointAnchor != null)
            {
                chainMidpointAnchor.position = ringGrabInteractable.transform.position - Vector3.up * 0.038f;
            }

            CheckPinGrabDistance();
            SyncAllVisuals();
        }

        private void LateUpdate()
        {
            if (IsRingGrabbed && ringGrabInteractable != null && chainMidpointAnchor != null)
            {
                chainMidpointAnchor.position = ringGrabInteractable.transform.position - Vector3.up * 0.038f;
            }

            CheckPinGrabDistance();
            SyncAllVisuals();
        }

        private void CheckPinGrabDistance()
        {
            if (!IsPinGrabbed || pinTransform == null || leftJawArmBody == null)
                return;

            Vector3 seatedWorld = leftJawArmBody.transform.TransformPoint(pinSeatedLocalOffset);
            float dist = Vector3.Distance(pinTransform.position, seatedWorld);
            if (isPinEngaged && dist >= pinReleaseDistance)
            {
                ReleasePin();
            }
        }

        public void SyncAllVisuals()
        {
            if (leftJawArmBody != null)
                SyncVisualGroup(leftJawArmBody.transform, leftJawArmVisuals, pinTransform, IsPinGrabbed, isPinEngaged, pinSeatedLocalOffset, pinReleasedLocalOffset, pinLocalRotationOffset);

            if (rightJawArmBody != null)
                SyncVisualGroup(rightJawArmBody.transform, rightJawArmVisuals, pinTransform, IsPinGrabbed, isPinEngaged, pinSeatedLocalOffset, pinReleasedLocalOffset, pinLocalRotationOffset);

            SyncEndShacklesToChain();
        }

        private static void SyncVisualGroup(
            Transform bodyTransform,
            VisualPartBinding[] bindings,
            Transform pinTrans,
            bool pinGrabbed,
            bool pinEngaged,
            Vector3 pinSeatedLocal,
            Vector3 pinReleasedLocal,
            Quaternion pinRotLocal)
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

                if (vis == pinTrans || vis.name == "Pin")
                {
                    if (pinGrabbed)
                        continue;

                    Vector3 localOff = pinEngaged ? pinSeatedLocal : pinReleasedLocal;
                    vis.SetPositionAndRotation(
                        bodyPos + bodyRot * localOff,
                        bodyRot * pinRotLocal);
                    continue;
                }

                vis.SetPositionAndRotation(
                    bodyPos + bodyRot * bindings[i].localPositionOffset,
                    bodyRot * bindings[i].localRotationOffset);
            }
        }

        /// <summary>
        /// Pivots the end clevis shackles (Chain_4/5 on rightJawArmBody and Chain_6/7 on leftJawArmBody)
        /// and their Obi particle anchors around the steel arm eyelets toward the chain apex when lifted,
        /// eliminating the 90-degree kink between the rigid arm shackles and the Obi chain legs.
        /// </summary>
        private void SyncEndShacklesToChain()
        {
            if (leftJawArmBody == null || rightJawArmBody == null)
                return;

            ResolveComponentReferences();

            bool hasLoadedRope = Application.isPlaying && chainRope != null && chainRope.isLoaded && chainRope.activeParticleCount >= 3;
            if (!hasLoadedRope)
            {
                if (leftShackleObiAnchor != null) leftShackleObiAnchor.localPosition = LeftShackleRestLocal;
                if (rightShackleObiAnchor != null) rightShackleObiAnchor.localPosition = RightShackleRestLocal;
                return;
            }

            Vector3 pMid = chainRope.GetParticlePosition(chainRope.solverIndices[chainRope.activeParticleCount / 2]);

            // 1. Left shackle group (Chain_5, Chain_4, LeftShackle_ObiAnchor) on rightJawArmBody (Arm_2)
            Vector3 leftEyeletWorld = rightJawArmBody.transform.TransformPoint(LeftArmEyeletLocal);
            float leftLiftBlend = Mathf.Clamp01((pMid.y - leftEyeletWorld.y + 0.01f) / 0.08f);
            if (leftLiftBlend > 0.001f)
            {
                Vector3 toApexLocal = rightJawArmBody.transform.InverseTransformPoint(pMid) - LeftArmEyeletLocal;
                toApexLocal.z = 0f;
                if (toApexLocal.sqrMagnitude > 1e-5f)
                {
                    float angleDeg = Vector3.SignedAngle(LeftShackleRestDir2D, toApexLocal.normalized, Vector3.forward);
                    Quaternion localDeltaRot = Quaternion.Euler(0f, 0f, angleDeg * leftLiftBlend);
                    PivotShackleBindings(rightJawArmBody.transform, rightJawArmVisuals, LeftArmEyeletLocal, localDeltaRot, "Chain_4", "Chain_5");
                    if (leftShackleObiAnchor != null)
                    {
                        leftShackleObiAnchor.localPosition = LeftArmEyeletLocal + localDeltaRot * (LeftShackleRestLocal - LeftArmEyeletLocal);
                    }
                }
            }
            else if (leftShackleObiAnchor != null)
            {
                leftShackleObiAnchor.localPosition = LeftShackleRestLocal;
            }

            // 2. Right shackle group (Chain_7, Chain_6, RightShackle_ObiAnchor) on leftJawArmBody (Arm_1)
            Vector3 rightEyeletWorld = leftJawArmBody.transform.TransformPoint(RightArmEyeletLocal);
            float rightLiftBlend = Mathf.Clamp01((pMid.y - rightEyeletWorld.y + 0.01f) / 0.08f);
            if (rightLiftBlend > 0.001f)
            {
                Vector3 toApexLocal = leftJawArmBody.transform.InverseTransformPoint(pMid) - RightArmEyeletLocal;
                toApexLocal.z = 0f;
                if (toApexLocal.sqrMagnitude > 1e-5f)
                {
                    float angleDeg = Vector3.SignedAngle(RightShackleRestDir2D, toApexLocal.normalized, Vector3.forward);
                    Quaternion localDeltaRot = Quaternion.Euler(0f, 0f, angleDeg * rightLiftBlend);
                    PivotShackleBindings(leftJawArmBody.transform, leftJawArmVisuals, RightArmEyeletLocal, localDeltaRot, "Chain_6", "Chain_7");
                    if (rightShackleObiAnchor != null)
                    {
                        rightShackleObiAnchor.localPosition = RightArmEyeletLocal + localDeltaRot * (RightShackleRestLocal - RightArmEyeletLocal);
                    }
                }
            }
            else if (rightShackleObiAnchor != null)
            {
                rightShackleObiAnchor.localPosition = RightShackleRestLocal;
            }
        }

        private static void PivotShackleBindings(
            Transform armTransform,
            VisualPartBinding[] bindings,
            Vector3 eyeletLocal,
            Quaternion localDeltaRot,
            string nameA,
            string nameB)
        {
            if (bindings == null)
                return;

            Vector3 bodyPos = armTransform.position;
            Quaternion bodyRot = armTransform.rotation;

            for (int i = 0; i < bindings.Length; i++)
            {
                Transform vis = bindings[i].visualTransform;
                if (vis == null)
                    continue;

                if (vis.name == nameA || vis.name == nameB)
                {
                    Vector3 pivotedLocalPos = eyeletLocal + localDeltaRot * (bindings[i].localPositionOffset - eyeletLocal);
                    Quaternion pivotedLocalRot = localDeltaRot * bindings[i].localRotationOffset;
                    vis.SetPositionAndRotation(
                        bodyPos + bodyRot * pivotedLocalPos,
                        bodyRot * pivotedLocalRot);
                }
            }
        }

        private void ConfigureInternalCollisionIgnores()
        {
            if (leftJawArmBody == null || rightJawArmBody == null)
                return;

            Collider[] allCols = GetComponentsInChildren<Collider>(true);
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

            if (ringGrabInteractable != null)
            {
                Collider[] ringCols = ringGrabInteractable.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < ringCols.Length; i++)
                {
                    if (ringCols[i] == null) continue;
                    for (int j = 0; j < allCols.Length; j++)
                    {
                        if (allCols[j] == null || ringCols[i] == allCols[j]) continue;
                        Physics.IgnoreCollision(ringCols[i], allCols[j], true);
                    }
                }
            }

            if (pinTransform != null)
            {
                Collider[] pinCols = pinTransform.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < pinCols.Length; i++)
                {
                    if (pinCols[i] == null) continue;
                    for (int j = 0; j < allCols.Length; j++)
                    {
                        if (allCols[j] == null || pinCols[i] == allCols[j]) continue;
                        Physics.IgnoreCollision(pinCols[i], allCols[j], true);
                    }
                }
            }
        }

        private void OnPinSelectEntered(SelectEnterEventArgs args)
        {
            // Pulling the pin by >= pinReleaseDistance or activating releases it;
            // if grabbed while already engaged, check distance continuously in FixedUpdate/LateUpdate.
        }

        private void OnPinSelectExited(SelectExitEventArgs args)
        {
            if (pinTransform != null && leftJawArmBody != null)
            {
                Vector3 seatedWorld = leftJawArmBody.transform.TransformPoint(pinSeatedLocalOffset);
                float dist = Vector3.Distance(pinTransform.position, seatedWorld);
                if (isPinEngaged && dist >= pinReleaseDistance * 0.5f)
                {
                    ReleasePin();
                }
                else if (!isPinEngaged && dist < pinReleaseDistance * 1.5f && Mathf.Abs(CurrentHingeAngleDegrees) < 12f)
                {
                    EngagePin(true);
                }
            }
            SyncAllVisuals();
        }

        private void OnPinActivated(ActivateEventArgs args)
        {
            TogglePin();
        }

        private void OnRingSelectEntered(SelectEnterEventArgs args)
        {
            CreateRingSuspensionJoints();
            UpdateGrabbedSolverState();
        }

        private void OnRingSelectExited(SelectExitEventArgs args)
        {
            ReleaseRingSuspensionJoints();
            UpdateGrabbedSolverState();
        }

        private void CreateRingSuspensionJoints()
        {
            ReleaseRingSuspensionJoints();
            if (ringGrabInteractable == null || leftJawArmBody == null || rightJawArmBody == null)
                return;

            Rigidbody ringRb = ringGrabInteractable.GetComponent<Rigidbody>();
            if (ringRb == null)
                return;

            float halfRope = chainRope != null ? chainRope.restLength * 0.5f : 0.5065f;
            float legLength = (halfRope + EndShackleLengthMeters) - ringGrabChainLegSlack;
            ringGrabJointLeft = CreateArmChainJoint(leftJawArmBody, ringRb, RightArmEyeletLocal, legLength);
            ringGrabJointRight = CreateArmChainJoint(rightJawArmBody, ringRb, LeftArmEyeletLocal, legLength);

            savedLeftAngDamp = leftJawArmBody.angularDamping;
            savedRightAngDamp = rightJawArmBody.angularDamping;
            leftJawArmBody.angularDamping = Mathf.Max(savedLeftAngDamp, ringGrabbedArmAngularDamping);
            rightJawArmBody.angularDamping = Mathf.Max(savedRightAngDamp, ringGrabbedArmAngularDamping);
            leftJawArmBody.WakeUp();
            rightJawArmBody.WakeUp();
        }

        private void ReleaseRingSuspensionJoints()
        {
            if (ringGrabJointLeft != null)
            {
                Destroy(ringGrabJointLeft);
                ringGrabJointLeft = null;
            }
            if (ringGrabJointRight != null)
            {
                Destroy(ringGrabJointRight);
                ringGrabJointRight = null;
            }
            if (leftJawArmBody != null && savedLeftAngDamp > 0f)
                leftJawArmBody.angularDamping = savedLeftAngDamp;
            if (rightJawArmBody != null && savedRightAngDamp > 0f)
                rightJawArmBody.angularDamping = savedRightAngDamp;
        }

        private static ConfigurableJoint CreateArmChainJoint(Rigidbody armBody, Rigidbody ringRb, Vector3 eyeletLocal, float legLength)
        {
            ConfigurableJoint cj = armBody.gameObject.AddComponent<ConfigurableJoint>();
            cj.autoConfigureConnectedAnchor = false;
            cj.connectedBody = ringRb;
            cj.anchor = eyeletLocal;
            cj.connectedAnchor = Vector3.zero;
            cj.xMotion = ConfigurableJointMotion.Limited;
            cj.yMotion = ConfigurableJointMotion.Limited;
            cj.zMotion = ConfigurableJointMotion.Limited;
            cj.linearLimit = new SoftJointLimit { limit = legLength, bounciness = 0f, contactDistance = 0.005f };
            cj.angularXMotion = ConfigurableJointMotion.Free;
            cj.angularYMotion = ConfigurableJointMotion.Free;
            cj.angularZMotion = ConfigurableJointMotion.Free;
            cj.projectionMode = JointProjectionMode.PositionAndRotation;
            cj.projectionDistance = 0.001f;
            cj.projectionAngle = 0.5f;
            cj.enableCollision = false;
            cj.enablePreprocessing = false;
            return cj;
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
