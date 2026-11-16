using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sefpro.JawTongs
{
    /// <summary>
    /// Central crane lifting ring and structural suspension point for the SEFPRO Jaw Tongs assembly.
    /// Allows the OverheadCrane hook to attach/detach via physics joints at the central lifting ring
    /// while preserving full articulated jaw opening/closing, locking-pin restraint, and Obi chain behaviour.
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
        public float maxHorizontalSeatingDistance = 0.28f;

        [Tooltip("Allowed vertical (Y) offset window (hookSaddleY - ringSeatY) in metres.")]
        public Vector2 verticalSeatingRange = new Vector2(-0.22f, 0.30f);

        [Tooltip("Maximum 3D Euclidean distance between hook saddle and ring seat in metres.")]
        public float maxTotalSeatingDistance = 0.35f;

        [Header("Floor Clearance")]
        [Tooltip("Minimum warehouse floor Y coordinate.")]
        public float minGroundY = 0.0f;

        [Tooltip("Optional currently gripped casting block (used for accurate floor/pallet clearance while lowering).")]
        public BoxCollider grippedPayloadCollider;

        [Header("State (Read Only)")]
        [SerializeField] private bool isAttached = false;
        [SerializeField] private bool isHookInRange = false;
        [SerializeField] private Transform currentHookMount;
        [SerializeField] private Rigidbody currentHookRb;

        public bool IsAttached => isAttached;
        public bool IsHookInRange => isHookInRange;
        public Transform CurrentHookMount => currentHookMount;

        [Header("Chain Load Path (Ring -> Chain -> Shackles)")]
        [Tooltip("Interlocked ring mesh that rides on the middle chain link. When set, the hook seat tracks the live ring.")]
        public Transform ringMesh;

        [Tooltip("Obi chain rope whose middle particle is carried by the crane hook while attached.")]
        public Obi.ObiRope chainRope;

        [Tooltip("Hook seat height above the ring centre (metres).")]
        public float ringSeatUpOffset = 0.038f;

        [Tooltip("Safety margin removed from each chain leg length so the legs go taut just before the Obi chain stretches (metres).")]
        public float chainLegSlack = 0.014f;

        [Tooltip("While attached, the tongs' angular damping is set to this natural value so jaws fall smoothly under gravity when unlocked.")]
        public float attachedArmAngularDamping = 0.8f;

        public Vector3 RingSeatWorldPosition =>
            ringMesh != null ? ringMesh.position + Vector3.up * ringSeatUpOffset : transform.TransformPoint(ringSeatLocalOffset);

        private ConfigurableJoint suspensionJointLeft;
        private ConfigurableJoint suspensionJointRight;
        private Transform apexCarrier;
        private Obi.ObiParticleAttachment midAttachment;
        private Transform midAttachmentOriginalTarget;
        private Obi.ObiParticleAttachment.AttachmentType midAttachmentOriginalType;
        private float savedLeftLinDamp, savedRightLinDamp;
        private float savedLeftAngDamp, savedRightAngDamp;
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

            // Legacy path (no chain assigned): snap the tongs so the ring seat meets the hook saddle.
            Vector3 alignDelta = hookSaddleWorld - ringSeatWorld;
            if (!UseChainLoadPath && alignDelta.sqrMagnitude > 1e-6f && alignDelta.magnitude <= 0.45f)
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

            if (UseChainLoadPath)
            {
                AttachChainLoadPath(hookMount, hookRb, hookSaddleWorld);
            }
            else
            {
                // Legacy: rigid pivot suspension joints.
                suspensionJointLeft = CreateArmSuspensionJoint(leftArmBody, hookRb);
                suspensionJointRight = CreateArmSuspensionJoint(rightArmBody, hookRb);
            }

            isAttached = true;
            Debug.Log($"[JawTongsCraneLiftingPoint] Attached JawTongs_Assembly central lifting ring to crane hook '{hookMount.name}' (horiz={horizDist:F3}m, total={totalDist:F3}m, chainPath={UseChainLoadPath}).");
            return true;
        }

        private bool UseChainLoadPath => ringMesh != null && chainRope != null;

        /// <summary>
        /// Load path: Hook -> ring/apex -> two chain legs -> arm eyelets -> arms -> jaw pads.
        /// While the locking pin is engaged, JawTongsController locks the two arms at 0 deg so both jaw ends
        /// remain horizontally secured. When the locking pin is released, the scissor arms unlock and both
        /// jaw ends fall naturally under gravity.
        /// </summary>
        private void AttachChainLoadPath(Transform hookMount, Rigidbody hookRb, Vector3 hookSaddleWorld)
        {
            // Ring_Mesh centre sits 0.038m below top inner rim (hookSaddleWorld), and p[12] sits 0.038m below Ring_Mesh centre (bottom inner rim).
            Vector3 pinWorld = hookSaddleWorld - Vector3.up * 0.076f;

            // Carrier transform parented to the hook: the chain's middle particle is pinned here.
            apexCarrier = new GameObject("TongsChainApex_Carrier").transform;
            apexCarrier.SetParent(hookMount, false);
            apexCarrier.position = pinWorld;

            midAttachment = null;
            foreach (var a in chainRope.GetComponents<Obi.ObiParticleAttachment>())
                if (a.particleGroup != null && a.particleGroup.name == "ChainMidpoint") { midAttachment = a; break; }
            if (midAttachment != null)
            {
                midAttachmentOriginalTarget = midAttachment.target;
                midAttachmentOriginalType = midAttachment.attachmentType;
                midAttachment.enabled = false;
                midAttachment.target = apexCarrier;
                midAttachment.attachmentType = Obi.ObiParticleAttachment.AttachmentType.Static;
                midAttachment.enabled = true;
            }

            float effectiveSlack = Mathf.Max(0.014f, chainLegSlack);
            float legLength = (chainRope.restLength * 0.5f + JawTongsController.EndShackleLengthMeters) - effectiveSlack;
            Vector3 pinLocal = hookRb.transform.InverseTransformPoint(pinWorld);
            suspensionJointLeft = CreateChainLeg(leftArmBody, hookRb, JawTongsController.RightArmEyeletLocal, pinLocal, legLength);
            suspensionJointRight = CreateChainLeg(rightArmBody, hookRb, JawTongsController.LeftArmEyeletLocal, pinLocal, legLength);

            savedLeftLinDamp = leftArmBody.linearDamping;
            savedRightLinDamp = rightArmBody.linearDamping;
            savedLeftAngDamp = leftArmBody.angularDamping;
            savedRightAngDamp = rightArmBody.angularDamping;
            float targetAngDamp = Mathf.Clamp(attachedArmAngularDamping, 0.4f, 1.0f);
            leftArmBody.linearDamping = Mathf.Max(savedLeftLinDamp, 1.6f);
            rightArmBody.linearDamping = Mathf.Max(savedRightLinDamp, 1.6f);
            leftArmBody.angularDamping = targetAngDamp;
            rightArmBody.angularDamping = targetAngDamp;
        }

        private ConfigurableJoint CreateChainLeg(Rigidbody armBody, Rigidbody hookRb, Vector3 eyeletLocal, Vector3 pinLocal, float legLength)
        {
            ConfigurableJoint cj = armBody.gameObject.AddComponent<ConfigurableJoint>();
            cj.autoConfigureConnectedAnchor = false;
            cj.connectedBody = hookRb;
            cj.anchor = eyeletLocal;
            cj.connectedAnchor = pinLocal;

            // Rope behaviour: free inside the leg length, hard stop at full length with projection so the Obi chain never stretches.
            cj.xMotion = ConfigurableJointMotion.Limited;
            cj.yMotion = ConfigurableJointMotion.Limited;
            cj.zMotion = ConfigurableJointMotion.Limited;
            cj.linearLimit = new SoftJointLimit { limit = legLength, bounciness = 0f, contactDistance = 0.005f };

            // Keep the tongs aligned in the X-Y plane relative to the hook while leaving Z scissor rotation 100% free.
            cj.axis = new Vector3(1f, 0f, 0f);
            cj.secondaryAxis = new Vector3(0f, 1f, 0f);
            cj.angularXMotion = ConfigurableJointMotion.Limited;
            cj.angularYMotion = ConfigurableJointMotion.Limited;
            cj.angularZMotion = ConfigurableJointMotion.Free;
            cj.lowAngularXLimit = new SoftJointLimit { limit = -5f };
            cj.highAngularXLimit = new SoftJointLimit { limit = 5f };
            cj.angularYLimit = new SoftJointLimit { limit = 6f };

            // Zero angular spring on Z so neither jaw end is ever artificially held up when the pin is released.
            cj.angularYZDrive = new JointDrive
            {
                positionSpring = 0f,
                positionDamper = 0.35f,
                maximumForce = 40f
            };

            cj.projectionMode = JointProjectionMode.PositionAndRotation;
            cj.projectionDistance = 0.001f;
            cj.projectionAngle = 0.5f;
            cj.enableCollision = false;
            cj.enablePreprocessing = false;
            return cj;
        }

        private ConfigurableJoint CreateArmSuspensionJoint(Rigidbody armBody, Rigidbody hookRb)
        {
            ConfigurableJoint cj = armBody.gameObject.AddComponent<ConfigurableJoint>();
            cj.autoConfigureConnectedAnchor = false;
            cj.connectedBody = hookRb;
            cj.anchor = Vector3.zero;

            float pivotToRingSeat = 0.165f + ringSeatLocalOffset.y;
            cj.connectedAnchor = new Vector3(-(0.0002f + ringSeatLocalOffset.x), -(hookSaddleOffset + pivotToRingSeat), -(0.0010f + ringSeatLocalOffset.z));

            cj.xMotion = ConfigurableJointMotion.Locked;
            cj.yMotion = ConfigurableJointMotion.Locked;
            cj.zMotion = ConfigurableJointMotion.Locked;

            cj.axis = new Vector3(1f, 0f, 0f);
            cj.secondaryAxis = new Vector3(0f, 1f, 0f);
            cj.angularXMotion = ConfigurableJointMotion.Limited;
            cj.angularYMotion = ConfigurableJointMotion.Limited;
            cj.angularZMotion = ConfigurableJointMotion.Free;

            SoftJointLimit limitX = new SoftJointLimit { limit = 6f };
            cj.lowAngularXLimit = new SoftJointLimit { limit = -6f };
            cj.highAngularXLimit = limitX;
            cj.angularYLimit = new SoftJointLimit { limit = 10f };

            // Zero spring so legacy suspension never artificially locks or holds the jaw ends up.
            JointDrive zDrive = new JointDrive
            {
                positionSpring = 0f,
                positionDamper = 0.5f,
                maximumForce = 40f
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

            if (midAttachment != null)
            {
                midAttachment.enabled = false;
                midAttachment.target = midAttachmentOriginalTarget;
                midAttachment.attachmentType = midAttachmentOriginalType;
                midAttachment.enabled = true;
                midAttachment = null;
            }

            if (apexCarrier != null)
            {
                Destroy(apexCarrier.gameObject);
                apexCarrier = null;
            }

            if (leftArmBody != null && savedLeftLinDamp > 0f) leftArmBody.linearDamping = savedLeftLinDamp;
            if (rightArmBody != null && savedRightLinDamp > 0f) rightArmBody.linearDamping = savedRightLinDamp;
            if (leftArmBody != null && savedLeftAngDamp > 0f) leftArmBody.angularDamping = savedLeftAngDamp;
            if (rightArmBody != null && savedRightAngDamp > 0f) rightArmBody.angularDamping = savedRightAngDamp;

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

            float lowestY = Mathf.Min(leftArmBody.transform.position.y, rightArmBody.transform.position.y) - 0.225f;
            if (grippedPayloadCollider != null)
            {
                lowestY = Mathf.Min(lowestY, grippedPayloadCollider.bounds.min.y - 0.02f);
            }

            float clearance = lowestY - minGroundY;
            if (clearance <= 0.01f)
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
