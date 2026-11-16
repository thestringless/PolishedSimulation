using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sefpro.JawTongs
{
    /// <summary>
    /// Central crane lifting ring and structural suspension point for the SEFPRO Jaw Tongs assembly.
    /// Allows the OverheadCrane hook to attach/detach via physics joints at the central scissor pivot
    /// while preserving full articulated jaw opening/closing and Obi chain behaviour.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Collider))]
    public class JawTongsCraneLiftingPoint : MonoBehaviour
    {
        [Header("Tongs Assembly References")]
        [Tooltip("Root transform of the JawTongs_Assembly.")]
        public Transform tongsRoot;

        [Tooltip("Controller managing the two articulated scissor arms and visual sync.")]
        public JawTongsController tongsController;

        [Tooltip("Left articulated tong arm Rigidbody.")]
        public Rigidbody leftArmBody;

        [Tooltip("Right articulated tong arm Rigidbody.")]
        public Rigidbody rightArmBody;

        [Header("Ring Geometry & Seating")]
        [Tooltip("Local offset from this transform to the central ring opening where the crane hook seats.")]
        public Vector3 ringSeatLocalOffset = new Vector3(0f, 0.045f, 0f);

        [Tooltip("Offset from the crane WireHookMount origin down to the hook saddle throat.")]
        public float hookSaddleOffset = 0.365f;

        [Tooltip("Maximum horizontal (X/Z) distance between hook saddle and ring center for seating.")]
        public float maxHorizontalSeatingDistance = 0.25f;

        [Tooltip("Allowed vertical (Y) offset window (hookSaddleY - ringSeatY) in metres.")]
        public Vector2 verticalSeatingRange = new Vector2(-0.18f, 0.28f);

        [Tooltip("Maximum 3D Euclidean distance between hook saddle and ring seat in metres.")]
        public float maxTotalSeatingDistance = 0.32f;

        [Header("Floor Clearance")]
        [Tooltip("Minimum warehouse floor Y coordinate.")]
        public float minGroundY = 0.0f;

        [Header("State (Read Only)")]
        [SerializeField] private bool isAttached = false;
        [SerializeField] private bool isHookInRange = false;
        [SerializeField] private Transform currentHookMount;
        [SerializeField] private Rigidbody currentHookRb;

        public bool IsAttached => isAttached;
        public bool IsHookInRange => isHookInRange;
        public Transform CurrentHookMount => currentHookMount;
        public Vector3 RingSeatWorldPosition => transform.TransformPoint(ringSeatLocalOffset);

        private ConfigurableJoint suspensionJointLeft;
        private ConfigurableJoint suspensionJointRight;
        private Collider[] ringAndTongColliders;
        private Collider[] hookColliders;

        private void Awake()
        {
            ResolveReferences();
            ConfigureInternalCollisionIgnores();
        }

        private void OnEnable()
        {
            ResolveReferences();
            ConfigureInternalCollisionIgnores();
        }

        private void OnDestroy()
        {
            if (isAttached)
            {
                DetachFromHook();
            }
        }

        public void ResolveReferences()
        {
            if (tongsController == null)
                tongsController = GetComponentInParent<JawTongsController>();

            if (tongsRoot == null)
                tongsRoot = tongsController != null ? tongsController.transform : transform.root;

            if (tongsController != null)
            {
                if (leftArmBody == null) leftArmBody = tongsController.leftJawArmBody;
                if (rightArmBody == null) rightArmBody = tongsController.rightJawArmBody;
            }

            if (tongsRoot != null)
            {
                ringAndTongColliders = tongsRoot.GetComponentsInChildren<Collider>(true);
            }
            else
            {
                ringAndTongColliders = GetComponentsInChildren<Collider>(true);
            }
        }

        private void ConfigureInternalCollisionIgnores()
        {
            var myCols = GetComponentsInChildren<Collider>(true);
            if (ringAndTongColliders == null || myCols == null)
                return;

            for (int i = 0; i < myCols.Length; i++)
            {
                if (myCols[i] == null) continue;
                for (int j = 0; j < ringAndTongColliders.Length; j++)
                {
                    if (ringAndTongColliders[j] == null || myCols[i] == ringAndTongColliders[j])
                        continue;
                    Physics.IgnoreCollision(myCols[i], ringAndTongColliders[j], true);
                }
            }
        }

        private void LateUpdate()
        {
            SyncPoseWithPivot();
        }

        private void FixedUpdate()
        {
            SyncPoseWithPivot();
        }

        /// <summary>
        /// Keeps the central lifting ring structurally anchored to the central scissor pivot
        /// and bisected between the two tong arms' rotations.
        /// </summary>
        public void SyncPoseWithPivot()
        {
            if (leftArmBody == null || rightArmBody == null)
                return;

            // Central pivot position is the midpoint of the two coaxial arm pivots
            Vector3 pivotWorldPos = (leftArmBody.transform.position + rightArmBody.transform.position) * 0.5f;
            Quaternion bisectorRot = Quaternion.Slerp(leftArmBody.transform.rotation, rightArmBody.transform.rotation, 0.5f);

            // Offset from arm pivot (Z = -11.5030) to Centre_Bolt midpoint (Z = -11.5020) and upward to ring center (+0.165m Y)
            Vector3 ringCenterOffset = new Vector3(0.0002f, 0.1650f, 0.0010f);
            transform.SetPositionAndRotation(
                pivotWorldPos + bisectorRot * ringCenterOffset,
                bisectorRot);
        }

        public void SetHookInRange(bool inRange, Transform hookMount = null, Rigidbody hookRb = null)
        {
            isHookInRange = inRange;
            if (inRange)
            {
                currentHookMount = hookMount;
                currentHookRb = hookRb;
            }
            else if (!isAttached)
            {
                currentHookMount = null;
                currentHookRb = null;
            }
        }

        /// <summary>
        /// Connects the crane hook Rigidbody to the tongs assembly at the central lifting ring
        /// using physics joints that support the full assembly while allowing the scissor arms to open and close.
        /// </summary>
        public bool AttachToHook(Transform hookMount, Rigidbody hookRb, bool enforceSeatingTolerance = true)
        {
            if (isAttached || hookMount == null || hookRb == null)
                return false;

            ResolveReferences();
            if (leftArmBody == null || rightArmBody == null)
                return false;

            SyncPoseWithPivot();

            Vector3 hookSaddleWorld = hookMount.position - hookMount.up * hookSaddleOffset;
            Vector3 ringSeatWorld = RingSeatWorldPosition;

            Vector3 offset = hookSaddleWorld - ringSeatWorld;
            float horizDist = new Vector2(offset.x, offset.z).magnitude;
            float vertOffset = offset.y;
            float totalDist = offset.magnitude;

            if (enforceSeatingTolerance)
            {
                if (horizDist > maxHorizontalSeatingDistance ||
                    vertOffset < verticalSeatingRange.x || vertOffset > verticalSeatingRange.y ||
                    totalDist > maxTotalSeatingDistance)
                {
                    Debug.LogWarning($"[JawTongsCraneLiftingPoint] Attachment rejected - hook not seated in central lifting ring: horiz={horizDist:F3}m (max {maxHorizontalSeatingDistance:F3}m), vert={vertOffset:F3}m, total={totalDist:F3}m (max {maxTotalSeatingDistance:F3}m).");
                    return false;
                }
            }

            currentHookMount = hookMount;
            currentHookRb = hookRb;

            // Ignore collisions between the crane hook and the tongs assembly while connected
            hookColliders = hookMount.GetComponentsInChildren<Collider>(true);
            if (ringAndTongColliders != null && hookColliders != null)
            {
                for (int i = 0; i < ringAndTongColliders.Length; i++)
                {
                    for (int j = 0; j < hookColliders.Length; j++)
                    {
                        if (ringAndTongColliders[i] != null && hookColliders[j] != null)
                        {
                            Physics.IgnoreCollision(ringAndTongColliders[i], hookColliders[j], true);
                        }
                    }
                }
            }

            // Ensure both tong arm Rigidbodies are non-kinematic and awake so they hang naturally from the hook
            leftArmBody.isKinematic = false;
            leftArmBody.useGravity = true;
            leftArmBody.WakeUp();

            rightArmBody.isKinematic = false;
            rightArmBody.useGravity = true;
            rightArmBody.WakeUp();

            // Align the tongs assembly smoothly so the ring seat meets the hook saddle if within tolerance
            Vector3 alignDelta = hookSaddleWorld - ringSeatWorld;
            if (alignDelta.sqrMagnitude > 1e-6f && alignDelta.magnitude <= 0.45f)
            {
                leftArmBody.transform.position += alignDelta;
                rightArmBody.transform.position += alignDelta;
                leftArmBody.position = leftArmBody.transform.position;
                rightArmBody.position = rightArmBody.transform.position;
                Physics.SyncTransforms();
                if (tongsController != null)
                    tongsController.SyncAllVisuals();
                SyncPoseWithPivot();
            }

            // Create balanced suspension ConfigurableJoints from both scissor arms to the crane hook Rigidbody
            // anchored at the central lifting ring seat point. Leaving angularZ Free preserves full scissor jaw opening/closing!
            suspensionJointLeft = CreateArmSuspensionJoint(leftArmBody, hookRb);
            suspensionJointRight = CreateArmSuspensionJoint(rightArmBody, hookRb);

            isAttached = true;
            Debug.Log($"[JawTongsCraneLiftingPoint] Attached JawTongs_Assembly central lifting ring to crane hook '{hookMount.name}' (horiz={horizDist:F3}m, total={totalDist:F3}m).");
            return true;
        }

        private ConfigurableJoint CreateArmSuspensionJoint(Rigidbody armBody, Rigidbody hookRb)
        {
            ConfigurableJoint cj = armBody.gameObject.AddComponent<ConfigurableJoint>();
            cj.autoConfigureConnectedAnchor = false;
            cj.connectedBody = hookRb;

            // Anchor at the coaxial Centre_Bolt scissor pivot (0, 0, 0) on the arm so both arms
            // remain coaxial with their HingeJoint and can open/close without geometric over-constraint.
            cj.anchor = Vector3.zero;

            // Connected anchor on WireHookMount places the Centre_Bolt 0.210m below the hook saddle,
            // which seats the top of Central_Lifting_Ring (+0.210m above Centre_Bolt) directly in the hook throat.
            float pivotToRingSeat = 0.165f + ringSeatLocalOffset.y;
            // The ring seat may be offset horizontally from the scissor pivot (ring interlocked with a chain link),
            // so hang the pivot below the hook by the full pivot->seat offset (X/Z of ringSeatLocalOffset included).
            cj.connectedAnchor = new Vector3(-(0.0002f + ringSeatLocalOffset.x), -(hookSaddleOffset + pivotToRingSeat), -(0.0010f + ringSeatLocalOffset.z));

            cj.xMotion = ConfigurableJointMotion.Locked;
            cj.yMotion = ConfigurableJointMotion.Locked;
            cj.zMotion = ConfigurableJointMotion.Locked;

            // Lock out-of-plane twist/tilt relative to the hook so the tongs hang stably,
            // while keeping angularZ Free so the two scissor arms can open and close freely!
            cj.axis = new Vector3(1f, 0f, 0f);
            cj.secondaryAxis = new Vector3(0f, 1f, 0f);
            cj.angularXMotion = ConfigurableJointMotion.Limited;
            cj.angularYMotion = ConfigurableJointMotion.Limited;
            cj.angularZMotion = ConfigurableJointMotion.Free;

            SoftJointLimit limitX = new SoftJointLimit { limit = 6f };
            cj.lowAngularXLimit = new SoftJointLimit { limit = -6f };
            cj.highAngularXLimit = limitX;
            cj.angularYLimit = new SoftJointLimit { limit = 10f };

            // Balanced angular spring/damping around Z so the suspended tongs remain upright and symmetric
            // under gravity while still opening/closing smoothly when actuated or grabbed.
            JointDrive zDrive = new JointDrive
            {
                positionSpring = 25f,
                positionDamper = 5f,
                maximumForce = 120f
            };
            cj.angularYZDrive = zDrive;

            cj.enableCollision = false;
            cj.projectionMode = JointProjectionMode.PositionAndRotation;
            cj.projectionDistance = 0.005f;
            cj.projectionAngle = 1f;

            return cj;
        }

        /// <summary>
        /// Disconnects the tongs assembly from the crane hook.
        /// </summary>
        public void DetachFromHook()
        {
            if (!isAttached)
                return;

            if (suspensionJointLeft != null)
            {
                Destroy(suspensionJointLeft);
                suspensionJointLeft = null;
            }

            if (suspensionJointRight != null)
            {
                Destroy(suspensionJointRight);
                suspensionJointRight = null;
            }

            if (ringAndTongColliders != null && hookColliders != null)
            {
                for (int i = 0; i < ringAndTongColliders.Length; i++)
                {
                    for (int j = 0; j < hookColliders.Length; j++)
                    {
                        if (ringAndTongColliders[i] != null && hookColliders[j] != null)
                        {
                            Physics.IgnoreCollision(ringAndTongColliders[i], hookColliders[j], false);
                        }
                    }
                }
                hookColliders = null;
            }

            if (leftArmBody != null)
            {
                leftArmBody.linearVelocity = Vector3.zero;
                leftArmBody.angularVelocity = Vector3.zero;
                leftArmBody.WakeUp();
            }

            if (rightArmBody != null)
            {
                rightArmBody.linearVelocity = Vector3.zero;
                rightArmBody.angularVelocity = Vector3.zero;
                rightArmBody.WakeUp();
            }

            isAttached = false;
            currentHookMount = null;
            currentHookRb = null;

            Debug.Log("[JawTongsCraneLiftingPoint] Detached JawTongs_Assembly from crane hook.");
        }

        public void ToggleAttachment(Transform hookMount, Rigidbody hookRb)
        {
            if (isAttached)
                DetachFromHook();
            else
                AttachToHook(hookMount, hookRb);
        }

        /// <summary>
        /// Calculates the minimum allowed normalized hook height based on live ground/stand clearance beneath the tongs jaws.
        /// </summary>
        public bool TryGetMinAllowedHookHeight(float currentHookHeight, float craneHeight, float hookOffset, out float minHookHeight)
        {
            minHookHeight = 0f;
            if (!isAttached || leftArmBody == null || rightArmBody == null)
                return false;

            float lowestJawY = Mathf.Min(leftArmBody.transform.position.y, rightArmBody.transform.position.y) - 0.225f;
            float clearance = lowestJawY - minGroundY;
            if (clearance <= 0.02f)
            {
                minHookHeight = currentHookHeight;
                return true;
            }

            float travelSpan = (craneHeight + hookOffset) - 0.5f;
            if (travelSpan <= 0.01f)
                travelSpan = 4.6f;

            float deltaT = clearance / travelSpan;
            minHookHeight = Mathf.Max(0f, currentHookHeight - deltaT);
            return true;
        }
    }
}
