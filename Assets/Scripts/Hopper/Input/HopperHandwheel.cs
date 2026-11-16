using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace LiquidSolidPrototype.Input
{
    /// <summary>
    /// Interactive handwheel controller for the SEFPRO Vibrocasting Hopper.
    /// Supports both left and right linked handwheel rims in VR (via XRSimpleInteractable on Handwheel_L_Rim
    /// and Handwheel_R_Rim, spoke orbit, wrist roll, and active turn-hold) and desktop mouse-drag / scroll-wheel rotation.
    /// Grabbing and turning the handwheel rim activates pouring; releasing or closing the mechanism stops pouring.
    /// </summary>
    public class HopperHandwheel : MonoBehaviour
    {
        public const float RimCenterRadiusLocal = 0.00205f;
        public const float RimSegBoxXYLocal = 0.00044f;
        public const float RimSegBoxZLocal = 0.00032f;
        public const int RimSegmentCount = 16;
        public const float MinRimGrabRadialMeters = 0.155f;
        public const float MaxRimGrabRadialMeters = 0.285f;
        public const float MinRimRayHitRadialMeters = 0.170f;
        public const float MaxRimRayHitRadialMeters = 0.240f;
        public const float MaxRimGrabDistanceMeters = 1.5f;

        [Header("Handwheel Rotation Limits")]
        [Tooltip("Maximum distance (in meters) from which the Right Arm grab ray interactor can grab the outer rim.")]
        [SerializeField] private float maxGrabRayDistance = MaxRimGrabDistanceMeters;
        [Tooltip("Number of full 360-degree rotations required to fully open the gate.")]
        [SerializeField] private float turnsToFullyOpen = 1.0f;
        [Tooltip("Max rotation angle in degrees around local X when fully open (turnsToFullyOpen * 360).")]
        [SerializeField] private float maxWheelAngle = 360f;
        [Tooltip("Drag sensitivity (degrees of wheel rotation per pixel of mouse drag).")]
        [SerializeField] private float dragSensitivity = 0.65f;

        [Header("Linked Hierarchy Nodes")]
        [SerializeField] private Transform gatePivot;
        [SerializeField] private Transform linkedHandwheelRight;
        [SerializeField] private Transform leftWheelRim;
        [SerializeField] private Transform rightWheelRim;
        [SerializeField] private Transform leftRimAttachPoint;
        [SerializeField] private Transform rightRimAttachPoint;

        [Header("Gate Settings")]
        [Tooltip("Max angle of Gate_Pivot around local X when fully open.")]
        [SerializeField] private float maxGateAngle = 28f;

        [Header("Auto-Close on Release")]
        [Tooltip("When true, releasing the handwheel automatically closes the gate and stops pouring.")]
        [SerializeField] private bool closeOnRelease = true;

        [Header("Interaction State (Read Only)")]
        [SerializeField] private float currentAngle = 0f;
        [SerializeField] private bool isDragging = false;
        [SerializeField] private bool isHovered = false;
        [SerializeField] private float visualWheelSpinAngle = 0f;

        private Quaternion gateClosedRot = Quaternion.identity;
        private Quaternion gateOpenRot;
        private Quaternion wheelLeftClosedRot = Quaternion.identity;
        private Quaternion wheelRightClosedRot = Quaternion.identity;

        private Vector2 lastMousePos;

        // XR Grab Interaction State on Outer Rim (Right Arm Only, Manual Rim Rotation Only)
        private XRGrabInteractable xrGrabInteractableLeftRim;
        private XRGrabInteractable xrGrabInteractableRightRim;
        private readonly List<Collider> leftRimColliders = new List<Collider>();
        private readonly List<Collider> rightRimColliders = new List<Collider>();
        private IXRSelectInteractor activeXrInteractor;
        private Transform activeWheelPivot;
        private Transform activeWheelRim;
        private Transform activeRimAttachPoint;
        private Transform cachedXrOriginTransform;
        private Vector3 lastXrSpokeDir;
        private Quaternion lastXrInteractorRot = Quaternion.identity;
        private Vector3 lastXrOriginPos;
        private Quaternion lastXrOriginRot = Quaternion.identity;
        private bool explicitOpenHold = false;

        private static readonly List<UnityEngine.XR.InputDevice> s_xrDevices = new List<UnityEngine.XR.InputDevice>();

        public float CurrentAngle => currentAngle;
        public float MaxWheelAngle => maxWheelAngle;
        public float TurnsToFullyOpen => turnsToFullyOpen;
        public float OpenFraction => maxWheelAngle > 0.001f ? Mathf.Clamp01(currentAngle / maxWheelAngle) : 0f;
        public bool IsOpen => OpenFraction > 0.015f;
        public bool IsDragging => isDragging || activeXrInteractor != null;
        public float MaxGrabRayDistance => maxGrabRayDistance;
        public Transform LeftWheelRim => leftWheelRim;
        public Transform RightWheelRim => rightWheelRim;
        public Transform LeftRimAttachPoint => leftRimAttachPoint;
        public Transform RightRimAttachPoint => rightRimAttachPoint;
        public XRGrabInteractable XrInteractableLeftRim => xrGrabInteractableLeftRim;
        public XRGrabInteractable XrInteractableRightRim => xrGrabInteractableRightRim;

        public event Action<float> OnOpenFractionChanged;

        private void Awake()
        {
            turnsToFullyOpen = 1.0f;
            maxWheelAngle = 360f;

            wheelLeftClosedRot = transform.localRotation;

            if (gatePivot == null)
            {
                var hopper = transform.parent;
                if (hopper != null) gatePivot = hopper.Find("Gate_Pivot");
            }

            if (linkedHandwheelRight == null)
            {
                var hopper = transform.parent;
                if (hopper != null) linkedHandwheelRight = hopper.Find("Handwheel_R_Pivot");
            }

            if (leftWheelRim == null)
            {
                leftWheelRim = transform.Find("Handwheel_L_Rim");
            }

            if (rightWheelRim == null && linkedHandwheelRight != null)
            {
                rightWheelRim = linkedHandwheelRight.Find("Handwheel_R_Rim");
            }

            if (gatePivot != null)
            {
                gateClosedRot = gatePivot.localRotation;
                gateOpenRot = Quaternion.Euler(maxGateAngle, 0f, 0f) * gateClosedRot;
            }

            if (linkedHandwheelRight != null)
            {
                wheelRightClosedRot = linkedHandwheelRight.localRotation;
            }

            EnsureXRInteractables();
            UpdateMechanism();
        }

        private void OnEnable()
        {
            EnsureXRInteractables();
            BindInteractableEvents(xrGrabInteractableLeftRim, true);
            BindInteractableEvents(xrGrabInteractableRightRim, true);
        }

        private void OnDisable()
        {
            BindInteractableEvents(xrGrabInteractableLeftRim, false);
            BindInteractableEvents(xrGrabInteractableRightRim, false);
            activeXrInteractor = null;
        }

        private void BindInteractableEvents(XRGrabInteractable interactable, bool subscribe)
        {
            if (interactable == null) return;
            interactable.selectEntered.RemoveListener(OnXRSelectEntered);
            interactable.selectExited.RemoveListener(OnXRSelectExited);

            if (subscribe)
            {
                interactable.selectEntered.AddListener(OnXRSelectEntered);
                interactable.selectExited.AddListener(OnXRSelectExited);
            }
        }

        public void EnsureXRInteractables()
        {
            if (leftWheelRim == null)
                leftWheelRim = transform.Find("Handwheel_L_Rim");
            if (linkedHandwheelRight == null && transform.parent != null)
                linkedHandwheelRight = transform.parent.Find("Handwheel_R_Pivot");
            if (rightWheelRim == null && linkedHandwheelRight != null)
                rightWheelRim = linkedHandwheelRight.Find("Handwheel_R_Rim");

            // Strip any non-rim interactables/colliders from the pivot roots and non-rim children
            // so the grab interactable works strictly on Handwheel_L_Rim and Handwheel_R_Rim.
            StripNonRimInteractablesAndColliders(transform, leftWheelRim);
            if (linkedHandwheelRight != null)
            {
                StripNonRimInteractablesAndColliders(linkedHandwheelRight, rightWheelRim);
            }

            // 1. Primary (Left/+X) handwheel outer rim (Handwheel_L_Rim)
            if (leftWheelRim != null)
            {
                ConfigureRimGrabInteractableAndAttachPoint(
                    leftWheelRim,
                    leftRimColliders,
                    out leftRimAttachPoint,
                    out xrGrabInteractableLeftRim);
            }

            // 2. Linked (Right/-X) handwheel outer rim (Handwheel_R_Rim)
            if (rightWheelRim != null)
            {
                ConfigureRimGrabInteractableAndAttachPoint(
                    rightWheelRim,
                    rightRimColliders,
                    out rightRimAttachPoint,
                    out xrGrabInteractableRightRim);
            }
        }

        private static void StripNonRimInteractablesAndColliders(Transform wheelPivot, Transform allowedRim)
        {
            if (wheelPivot == null) return;

            foreach (var xri in wheelPivot.GetComponentsInChildren<XRBaseInteractable>(true))
            {
                if (xri != null && (xri.transform != allowedRim || !(xri is XRGrabInteractable)))
                {
                    if (Application.isPlaying) Destroy(xri);
                    else DestroyImmediate(xri);
                }
            }

            foreach (var col in wheelPivot.GetComponentsInChildren<Collider>(true))
            {
                if (col != null && col.transform != allowedRim)
                {
                    if (Application.isPlaying) Destroy(col);
                    else DestroyImmediate(col);
                }
            }

            foreach (var rb in wheelPivot.GetComponentsInChildren<Rigidbody>(true))
            {
                if (rb != null && rb.transform != allowedRim)
                {
                    if (Application.isPlaying) Destroy(rb);
                    else DestroyImmediate(rb);
                }
            }
        }

        private static void ConfigureRimGrabInteractableAndAttachPoint(
            Transform rimTransform,
            List<Collider> rimColliderList,
            out Transform rimAttachPoint,
            out XRGrabInteractable rimGrabInteractable)
        {
            rimColliderList.Clear();

            // Remove any legacy XRSimpleInteractable or full-disk SphereCollider
            foreach (var simple in rimTransform.GetComponents<XRSimpleInteractable>())
            {
                if (Application.isPlaying) Destroy(simple);
                else DestroyImmediate(simple);
            }
            foreach (var sc in rimTransform.GetComponents<SphereCollider>())
            {
                if (Application.isPlaying) Destroy(sc);
                else DestroyImmediate(sc);
            }

            // Create or locate the explicit attach point on the outer rim torus (r = 0.205m)
            rimAttachPoint = rimTransform.Find("Rim_AttachPoint");
            if (rimAttachPoint == null)
            {
                var attachGo = new GameObject("Rim_AttachPoint");
                rimAttachPoint = attachGo.transform;
                rimAttachPoint.SetParent(rimTransform, false);
                rimAttachPoint.localPosition = new Vector3(0f, RimCenterRadiusLocal, 0f);
                rimAttachPoint.localRotation = Quaternion.identity;
                rimAttachPoint.localScale = Vector3.one;
            }
            else if (rimAttachPoint.localPosition.sqrMagnitude < 1e-8f)
            {
                rimAttachPoint.localPosition = new Vector3(0f, RimCenterRadiusLocal, 0f);
            }

            // Isolate rim colliders on a kinematic Rigidbody required by XRGrabInteractable
            var rb = rimTransform.GetComponent<Rigidbody>();
            if (rb == null)
                rb = rimTransform.gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;

            // Exact non-convex MeshCollider for precise outer rim surface
            var mf = rimTransform.GetComponent<MeshFilter>();
            var mc = rimTransform.GetComponent<MeshCollider>();
            if (mf != null && mf.sharedMesh != null)
            {
                if (mc == null)
                    mc = rimTransform.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
                mc.convex = false;
                mc.isTrigger = false;
                rimColliderList.Add(mc);
            }

            // 16-segment ring of BoxColliders along the outer rim torus (r = 0.205m, leaving r < 0.174m completely empty)
            var boxes = rimTransform.GetComponents<BoxCollider>();
            while (boxes.Length < RimSegmentCount)
            {
                rimTransform.gameObject.AddComponent<BoxCollider>();
                boxes = rimTransform.GetComponents<BoxCollider>();
            }
            for (int i = 0; i < boxes.Length; i++)
            {
                if (i < RimSegmentCount)
                {
                    float ang = i * Mathf.PI * 2f / RimSegmentCount;
                    boxes[i].center = new Vector3(
                        Mathf.Cos(ang) * RimCenterRadiusLocal,
                        Mathf.Sin(ang) * RimCenterRadiusLocal,
                        0f);
                    boxes[i].size = new Vector3(RimSegBoxXYLocal, RimSegBoxXYLocal, RimSegBoxZLocal);
                    boxes[i].isTrigger = false;
                    boxes[i].enabled = true;
                    rimColliderList.Add(boxes[i]);
                }
                else
                {
                    if (Application.isPlaying) Destroy(boxes[i]);
                    else DestroyImmediate(boxes[i]);
                }
            }

            // Configure XRGrabInteractable on the outer rim with Rim_AttachPoint wired
            // and position/rotation tracking disabled so grabbing holds the rim without detaching the mesh from the wheel pivot.
            rimGrabInteractable = rimTransform.GetComponent<XRGrabInteractable>();
            if (rimGrabInteractable == null)
                rimGrabInteractable = rimTransform.gameObject.AddComponent<XRGrabInteractable>();

            rimGrabInteractable.movementType = XRBaseInteractable.MovementType.Kinematic;
            rimGrabInteractable.trackPosition = false;
            rimGrabInteractable.trackRotation = false;
            rimGrabInteractable.trackScale = false;
            rimGrabInteractable.throwOnDetach = false;
            rimGrabInteractable.useDynamicAttach = false;
            rimGrabInteractable.selectMode = InteractableSelectMode.Single;
            rimGrabInteractable.attachTransform = rimAttachPoint;
            rimGrabInteractable.secondaryAttachTransform = null;

            rimGrabInteractable.colliders.Clear();
            for (int i = 0; i < rimColliderList.Count; i++)
            {
                if (rimColliderList[i] != null)
                    rimGrabInteractable.colliders.Add(rimColliderList[i]);
            }
        }

        /// <summary>
        /// Evaluates whether a world-space point lies on the circular handwheel outer rim band
        /// (rejecting the central hub, nut, shaft, and inner spokes).
        /// </summary>
        public bool IsWorldPointOnRim(Vector3 worldPoint, Transform wheelPivot, float minRadialMeters = MinRimGrabRadialMeters, float maxRadialMeters = MaxRimGrabRadialMeters)
        {
            Transform pivot = wheelPivot != null ? wheelPivot : transform;
            Vector3 axis = transform.parent != null ? transform.parent.right : Vector3.right;
            Vector3 radialVec = Vector3.ProjectOnPlane(worldPoint - pivot.position, axis);
            float radialDist = radialVec.magnitude;
            return radialDist >= minRadialMeters && radialDist <= maxRadialMeters;
        }

        /// <summary>
        /// Strictly checks that the interactor belongs to the player's RIGHT arm/hand
        /// (rejecting Left Controller, Left Hand, LeftHandXRDirectInteractor, gaze, or non-right interactors).
        /// </summary>
        public static bool IsRightArmInteractor(IXRInteractor interactor)
        {
            if (interactor == null || interactor.transform == null)
                return false;

            if (interactor.handedness == InteractorHandedness.Left)
                return false;

            Transform t = interactor.transform;
            bool hasRightInHierarchy = (interactor.handedness == InteractorHandedness.Right);
            while (t != null)
            {
                string n = t.name;
                if (n.IndexOf("Left", StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
                if (n.IndexOf("Gaze", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Teleport", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Poke", StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
                if (n.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0)
                    hasRightInHierarchy = true;
                t = t.parent;
            }

            return hasRightInHierarchy;
        }

        /// <summary>
        /// Returns true if any VR thumbstick (2D joystick axis) is currently deflected,
        /// so VR sticks can never rotate the handwheel or open the gate.
        /// </summary>
        private static bool IsAnyVRThumbstickActive()
        {
            s_xrDevices.Clear();
            InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.HeldInHand, s_xrDevices);
            for (int i = 0; i < s_xrDevices.Count; i++)
            {
                var dev = s_xrDevices[i];
                if (!dev.isValid) continue;
                if (dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out Vector2 pri) && pri.sqrMagnitude > 0.02f)
                    return true;
                if (dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.secondary2DAxis, out Vector2 sec) && sec.sqrMagnitude > 0.02f)
                    return true;
            }
            return false;
        }

        private Transform ResolveWheelPivotFromRim(Transform candidateTransform)
        {
            if (candidateTransform == null) return transform;
            if (rightWheelRim != null && (candidateTransform == rightWheelRim || candidateTransform.IsChildOf(rightWheelRim)))
                return linkedHandwheelRight != null ? linkedHandwheelRight : transform;
            if (linkedHandwheelRight != null && candidateTransform == linkedHandwheelRight)
                return linkedHandwheelRight;
            return transform;
        }

        private bool IsInteractorValidForRim(
            IXRInteractor interactor,
            Transform interactableTransform,
            out Transform resolvedPivot,
            out Transform resolvedRim,
            out Transform resolvedAttachPoint)
        {
            resolvedPivot = ResolveWheelPivotFromRim(interactableTransform);
            bool isRightWheel = (resolvedPivot == linkedHandwheelRight && rightWheelRim != null);
            resolvedRim = isRightWheel ? rightWheelRim : leftWheelRim;
            resolvedAttachPoint = isRightWheel ? rightRimAttachPoint : leftRimAttachPoint;

            if (interactor == null || resolvedRim == null)
                return false;

            // 1. Only the RIGHT arm/hand can grab and open the handwheel rim (never left arm or sticks)
            if (!IsRightArmInteractor(interactor))
                return false;

            // 2. Interactable must strictly be one of the handwheel outer rims
            if (interactableTransform != leftWheelRim && interactableTransform != rightWheelRim)
                return false;

            // 3. Allow grabbing the outer rim with the Right Arm up to maxGrabRayDistance (1.5 m).
            //    When grabbing directly up close (within the wheel plane), still reject grabs inside the hub/inner spokes.
            Vector3 handPos = interactor.transform.position;
            Vector3 axis = transform.parent != null ? transform.parent.right : Vector3.right;
            float axialDist = Mathf.Abs(Vector3.Dot(handPos - resolvedPivot.position, axis));
            Vector3 radialVec = Vector3.ProjectOnPlane(handPos - resolvedPivot.position, axis);
            float radialDist = radialVec.magnitude;

            float allowedRayDist = maxGrabRayDistance > 0.01f ? maxGrabRayDistance : MaxRimGrabDistanceMeters;
            float distToPivot = Vector3.Distance(handPos, resolvedPivot.position);

            // Closest distance from handPos to the circular outer rim ring (radius = 0.205 m)
            float rimRadiusWorld = 0.205f;
            float radialDeltaToRim = Mathf.Abs(radialDist - rimRadiusWorld);
            float distToRim = Mathf.Sqrt(axialDist * axialDist + radialDeltaToRim * radialDeltaToRim);

            if (distToRim > allowedRayDist && distToPivot > allowedRayDist)
                return false;

            // If the hand is directly inside the wheel plane (close direct reach), reject the central hub / inner spokes
            if (axialDist <= 0.14f && radialDist < MinRimGrabRadialMeters)
                return false;

            return true;
        }

        private Vector3 GetInteractorWheelPlanePoint(IXRInteractor interactor, Transform pivotTf)
        {
            if (interactor == null || interactor.transform == null)
                return pivotTf != null ? pivotTf.position : transform.position;

            Vector3 handPos = interactor.transform.position;
            Vector3 pivotPos = pivotTf != null ? pivotTf.position : transform.position;
            Vector3 axis = transform.parent != null ? transform.parent.right : Vector3.right;
            float axialDist = Mathf.Abs(Vector3.Dot(handPos - pivotPos, axis));

            // When grabbing via the grab ray from a distance (> 0.25 m), project the controller's forward ray
            // onto the wheel plane if it points toward the wheel; otherwise fall back to the hand's planar position.
            if (axialDist > 0.25f || Vector3.Distance(handPos, pivotPos) > 0.32f)
            {
                Plane wheelPlane = new Plane(axis, pivotPos);
                Ray aimRay = new Ray(handPos, interactor.transform.forward);
                if (wheelPlane.Raycast(aimRay, out float enter) && enter > 0f && enter <= (maxGrabRayDistance + 0.5f))
                {
                    Vector3 hitPt = aimRay.GetPoint(enter);
                    Vector3 hitRadial = Vector3.ProjectOnPlane(hitPt - pivotPos, axis);
                    if (hitRadial.sqrMagnitude > 0.0004f)
                        return hitPt;
                }
            }

            return handPos;
        }

        private static void SnapRimAttachPointToHand(Transform rimTransform, Transform rimAttachPoint, Vector3 targetWorldPos)
        {
            if (rimTransform == null || rimAttachPoint == null) return;
            Vector3 localHand = rimTransform.InverseTransformPoint(targetWorldPos);
            Vector2 dir2D = new Vector2(localHand.x, localHand.y);
            if (dir2D.sqrMagnitude > 1e-8f)
            {
                dir2D.Normalize();
                rimAttachPoint.localPosition = new Vector3(
                    dir2D.x * RimCenterRadiusLocal,
                    dir2D.y * RimCenterRadiusLocal,
                    0f);
            }
        }

        private void OnXRSelectEntered(SelectEnterEventArgs args)
        {
            Transform targetTf = args.interactableObject?.transform;
            if (!IsInteractorValidForRim(args.interactorObject, targetTf, out Transform pivot, out Transform rim, out Transform attachPt))
            {
                // Immediately cancel selection if Left Arm, beyond 1.5 m, or non-rim grab attempted
                if (args.manager != null && args.interactorObject != null && args.interactableObject != null)
                {
                    args.manager.SelectExit(args.interactorObject, args.interactableObject);
                }
                return;
            }

            // Snap outer rim attach point to the grabbed rim position and start tracking right-arm manual rotation
            Vector3 grabPlanePt = GetInteractorWheelPlanePoint(args.interactorObject, pivot);
            SnapRimAttachPointToHand(rim, attachPt, grabPlanePt);
            BeginXRTurn(args.interactorObject, pivot, rim, attachPt);
        }

        private void OnXRSelectExited(SelectExitEventArgs args)
        {
            if (args.interactorObject == activeXrInteractor)
            {
                EndXRTurn();
            }
        }

        private void BeginXRTurn(IXRSelectInteractor interactor, Transform wheelPivot, Transform wheelRim, Transform rimAttachPt)
        {
            activeXrInteractor = interactor;
            activeWheelPivot = wheelPivot != null ? wheelPivot : transform;
            activeWheelRim = wheelRim != null ? wheelRim : leftWheelRim;
            activeRimAttachPoint = rimAttachPt != null ? rimAttachPt : leftRimAttachPoint;
            explicitOpenHold = false;

            if (cachedXrOriginTransform == null)
            {
                var originGo = GameObject.Find("XR Origin Hands (XR Rig)");
                if (originGo != null) cachedXrOriginTransform = originGo.transform;
            }
            if (cachedXrOriginTransform != null)
            {
                lastXrOriginPos = cachedXrOriginTransform.position;
                lastXrOriginRot = cachedXrOriginTransform.rotation;
            }

            lastXrSpokeDir = GetXRSpokeDirection(activeXrInteractor, activeWheelPivot);
            lastXrInteractorRot = activeXrInteractor != null ? activeXrInteractor.transform.rotation : Quaternion.identity;
        }

        private void EndXRTurn()
        {
            activeXrInteractor = null;
            activeWheelPivot = null;
            activeWheelRim = null;
            activeRimAttachPoint = null;
            if (closeOnRelease && !explicitOpenHold)
            {
                CloseMechanismImmediate();
            }
        }

        private Vector3 GetXRSpokeDirection(IXRInteractor interactor, Transform pivotTf)
        {
            if (interactor == null) return Vector3.up;
            Vector3 pivotPos = pivotTf != null ? pivotTf.position : transform.position;
            Vector3 axis = transform.parent != null ? transform.parent.right : Vector3.right;
            Vector3 planePt = GetInteractorWheelPlanePoint(interactor, pivotTf);
            Vector3 radial = Vector3.ProjectOnPlane(planePt - pivotPos, axis);
            if (radial.sqrMagnitude <= 0.0001f)
            {
                radial = Vector3.ProjectOnPlane(interactor.transform.position - pivotPos, axis);
            }
            return radial.sqrMagnitude > 0.0001f ? radial.normalized : Vector3.up;
        }

        private void Update()
        {
            HandleXRWheelRotation();
            HandleMouseInteraction();
            UpdateMechanism();
        }

        private void HandleXRWheelRotation()
        {
            if (activeXrInteractor == null) return;

            bool isStillSelected = (xrGrabInteractableLeftRim != null && xrGrabInteractableLeftRim.isSelected && xrGrabInteractableLeftRim.interactorsSelecting.Contains(activeXrInteractor)) ||
                                   (xrGrabInteractableRightRim != null && xrGrabInteractableRightRim.isSelected && xrGrabInteractableRightRim.interactorsSelecting.Contains(activeXrInteractor));
            if (!isStillSelected)
            {
                EndXRTurn();
                return;
            }

            if (!IsInteractorValidForRim(activeXrInteractor, activeWheelRim != null ? activeWheelRim : leftWheelRim, out _, out _, out _))
            {
                EndXRTurn();
                return;
            }

            Vector3 curSpoke = GetXRSpokeDirection(activeXrInteractor, activeWheelPivot);
            Quaternion curRot = activeXrInteractor.transform.rotation;

            // Check if XR Origin moved/rotated via VR thumbsticks this frame or if any VR stick is pushed
            bool rigMovedViaStick = false;
            if (cachedXrOriginTransform != null)
            {
                float rigMove = Vector3.Distance(cachedXrOriginTransform.position, lastXrOriginPos);
                float rigTurn = Quaternion.Angle(cachedXrOriginTransform.rotation, lastXrOriginRot);
                lastXrOriginPos = cachedXrOriginTransform.position;
                lastXrOriginRot = cachedXrOriginTransform.rotation;
                if (rigMove > 0.0005f || rigTurn > 0.05f)
                    rigMovedViaStick = true;
            }

            if (rigMovedViaStick || IsAnyVRThumbstickActive())
            {
                // Ignore any frame motion caused by VR thumbsticks; re-anchor baseline to current hand pose
                lastXrSpokeDir = curSpoke;
                lastXrInteractorRot = curRot;
                return;
            }

            Vector3 axis = transform.parent != null ? transform.parent.right : Vector3.right;
            float spokeDelta = Mathf.Abs(Vector3.SignedAngle(lastXrSpokeDir, curSpoke, axis));
            lastXrSpokeDir = curSpoke;
            lastXrInteractorRot = curRot;

            // Strictly require manual orbital rotation of the Right Arm along the outer rim (no auto-spin, no stick spin)
            if (spokeDelta > 0.08f)
            {
                float manualDeltaAngle = spokeDelta * 2.2f;
                Vector3 grabPlanePt = GetInteractorWheelPlanePoint(activeXrInteractor, activeWheelPivot);
                SnapRimAttachPointToHand(activeWheelRim, activeRimAttachPoint, grabPlanePt);
                ApplyAngleDelta(manualDeltaAngle);
            }
        }

        private void HandleMouseInteraction()
        {
            Vector2 mousePos = GetMousePosition();
            bool isMouseDown = GetMouseButtonDown(0);
            bool isMouseHeld = GetMouseButtonHeld(0);
            bool isMouseUp = GetMouseButtonUp(0);

            var cam = Camera.main;
            if (cam == null) return;

            Ray ray = cam.ScreenPointToRay(mousePos);
            bool rayHit = false;
            if (Physics.Raycast(ray, out RaycastHit hit, 50f))
            {
                bool hitLeftRim = leftWheelRim != null && hit.transform == leftWheelRim &&
                                  IsWorldPointOnRim(hit.point, transform, MinRimRayHitRadialMeters, MaxRimRayHitRadialMeters);
                bool hitRightRim = rightWheelRim != null && hit.transform == rightWheelRim &&
                                   IsWorldPointOnRim(hit.point, linkedHandwheelRight != null ? linkedHandwheelRight : transform, MinRimRayHitRadialMeters, MaxRimRayHitRadialMeters);
                rayHit = hitLeftRim || hitRightRim;
            }
            isHovered = rayHit;

            if (isMouseDown && rayHit)
            {
                isDragging = true;
                explicitOpenHold = false;
                lastMousePos = mousePos;
            }

            if (isDragging)
            {
                if (isMouseHeld)
                {
                    Vector2 delta = mousePos - lastMousePos;
                    lastMousePos = mousePos;

                    float deltaAngle = (-delta.y + delta.x * 0.5f) * dragSensitivity;
                    if (Mathf.Abs(deltaAngle) > 0.01f)
                    {
                        ApplyAngleDelta(deltaAngle);
                    }
                }
                else if (isMouseUp)
                {
                    isDragging = false;
                    if (closeOnRelease && !explicitOpenHold)
                    {
                        CloseMechanismImmediate();
                    }
                }
            }
        }

        /// <summary>
        /// Rotates the handwheel by deltaAngle degrees. Positive opens and activates pouring;
        /// zero or negative to 0 closes and stops pouring.
        /// </summary>
        public void ApplyAngleDelta(float deltaAngle)
        {
            visualWheelSpinAngle = Mathf.Clamp(visualWheelSpinAngle + deltaAngle, 0f, maxWheelAngle * 4f);
            SetAngle(currentAngle + deltaAngle);
        }

        /// <summary>
        /// Sets normalized valve open fraction [0..1]. Passing 0 immediately closes and stops pouring.
        /// </summary>
        public void SetOpenFraction(float fraction)
        {
            float clamped = Mathf.Clamp01(fraction);
            explicitOpenHold = clamped > 0.015f;
            visualWheelSpinAngle = clamped * maxWheelAngle;
            SetAngle(clamped * maxWheelAngle);
        }

        /// <summary>
        /// Called when desktop drag or external turning stops/releases the wheel.
        /// Immediately closes the gate and stops pouring.
        /// </summary>
        public void NotifyDesktopDragEnded()
        {
            isDragging = false;
            if (closeOnRelease && !explicitOpenHold)
            {
                CloseMechanismImmediate();
            }
        }

        /// <summary>
        /// Stops turning and closes the pouring gate mechanism immediately.
        /// </summary>
        public void CloseMechanismImmediate()
        {
            isDragging = false;
            activeXrInteractor = null;
            explicitOpenHold = false;
            SetAngle(0f);
        }

        private void SetAngle(float angle)
        {
            float prevAngle = currentAngle;
            currentAngle = Mathf.Clamp(angle, 0f, maxWheelAngle);
            if (currentAngle <= 0.001f)
                visualWheelSpinAngle = 0f;

            if (Mathf.Abs(currentAngle - prevAngle) > 0.0005f)
            {
                UpdateMechanism();
                OnOpenFractionChanged?.Invoke(OpenFraction);
            }
            else
            {
                UpdateMechanism();
            }
        }

        private void UpdateMechanism()
        {
            float displayAngle = currentAngle > 0.001f ? Mathf.Max(currentAngle, visualWheelSpinAngle) : 0f;
            transform.localRotation = wheelLeftClosedRot * Quaternion.Euler(displayAngle, 0f, 0f);

            if (linkedHandwheelRight != null)
            {
                linkedHandwheelRight.localRotation = wheelRightClosedRot * Quaternion.Euler(-displayAngle, 0f, 0f);
            }

            if (gatePivot != null)
            {
                gatePivot.localRotation = Quaternion.Slerp(gateClosedRot, gateOpenRot, OpenFraction);
            }
        }

        public void ResetWheel()
        {
            isDragging = false;
            activeXrInteractor = null;
            explicitOpenHold = false;
            currentAngle = 0f;
            visualWheelSpinAngle = 0f;
            UpdateMechanism();
            OnOpenFractionChanged?.Invoke(0f);
        }

        private Vector2 GetMousePosition()
        {
            try
            {
#if ENABLE_INPUT_SYSTEM
                if (Mouse.current != null) return Mouse.current.position.ReadValue();
#endif
            }
            catch { }
            return Vector2.zero;
        }

        private bool GetMouseButtonDown(int button)
        {
            try
            {
#if ENABLE_INPUT_SYSTEM
                if (Mouse.current != null && button == 0) return Mouse.current.leftButton.wasPressedThisFrame;
#endif
            }
            catch { }
            return false;
        }

        private bool GetMouseButtonHeld(int button)
        {
            try
            {
#if ENABLE_INPUT_SYSTEM
                if (Mouse.current != null && button == 0) return Mouse.current.leftButton.isPressed;
#endif
            }
            catch { }
            return false;
        }

        private bool GetMouseButtonUp(int button)
        {
            try
            {
#if ENABLE_INPUT_SYSTEM
                if (Mouse.current != null && button == 0) return Mouse.current.leftButton.wasReleasedThisFrame;
#endif
            }
            catch { }
            return false;
        }
    }
}
