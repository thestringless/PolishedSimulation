using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace Sefpro.RatchetBelt
{
    /// <summary>
    /// Configures and drives XR Interaction Toolkit (XRI 3.x) interactables on a RatchetBelt assembly:
    /// 1. Free-end Adjustable J-Hook (`XRGrabInteractable`): kinematic, constraint-authoritative 3D grabbing
    ///    with zero XR-vs-Rigidbody fighting, zero snap-jump, strict belt-length enforcement, and collision isolation.
    /// 2. Ratchet Lever (`XRSimpleInteractable`): physical controller lever pumping in the true mandrel Y-Z plane
    ///    around the coaxial horizontal +X axle across 0°..65° to advance teeth.
    /// 3. Release Catch & Ratchet Pawl (`XRSimpleInteractable`): full tension release and single-tooth step release.
    /// Works seamlessly alongside DesktopInteractionManager without conflict.
    /// </summary>
    [DisallowMultipleComponent]
    public class RatchetBeltXRInteractables : MonoBehaviour
    {
        [Header("Core References")]
        public RatchetBeltMechanicsController mechanicsController;
        public PlatformHookAttachment hookAttachment;

        [Header("Configured XR Interactables (Auto-Wired)")]
        public XRGrabInteractable freeHookGrabInteractable;
        public XRSimpleInteractable handleLeverInteractable;
        public XRSimpleInteractable releaseCatchInteractable;
        public XRSimpleInteractable pawlStepReleaseInteractable;

        private IXRSelectInteractor _activeFreeHookInteractor;
        private Vector3 _freeHookGrabOffset;

        private IXRSelectInteractor _activeHandleInteractor;
        private Vector3 _handleGrabStartPos;
        private float _handleGrabStartAngle;
        private bool _pawlHeld;
        private float _pawlHoldStartTime;

        private void Awake()
        {
            EnsureSetup();
        }

        private void Start()
        {
            ConfigureCollisionIsolation();
        }

        private void OnEnable()
        {
            EnsureSetup();
            BindEvents(true);
            Application.onBeforeRender += OnBeforeRenderSync;
        }

        private void OnDisable()
        {
            BindEvents(false);
            Application.onBeforeRender -= OnBeforeRenderSync;
            _activeFreeHookInteractor = null;
            _activeHandleInteractor = null;
            _pawlHeld = false;
        }

        public void EnsureSetup()
        {
            if (mechanicsController == null)
                mechanicsController = GetComponent<RatchetBeltMechanicsController>();
            if (hookAttachment == null)
                hookAttachment = GetComponent<PlatformHookAttachment>();

            if (mechanicsController == null)
                return;

            mechanicsController.AutoBindReferences();

            SetupAdjustableHookGrabInteractable();
            SetupHandleLeverInteractable();
            SetupReleaseInteractables();
            ConfigureCollisionIsolation();
        }

        private void SetupAdjustableHookGrabInteractable()
        {
            Transform adjHook = mechanicsController.adjustableHookTransform;
            if (adjHook == null) return;

            Rigidbody rb = adjHook.GetComponent<Rigidbody>();
            if (rb == null) rb = adjHook.gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.None;
            rb.collisionDetectionMode = CollisionDetectionMode.Discrete;

            BoxCollider col = adjHook.GetComponent<BoxCollider>();
            if (col == null) col = adjHook.gameObject.AddComponent<BoxCollider>();

            MeshFilter mf = adjHook.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                Bounds mb = mf.sharedMesh.bounds;
                col.center = mb.center;
                col.size = new Vector3(
                    Mathf.Max(mb.size.x, 0.095f),
                    Mathf.Max(mb.size.y, 0.075f),
                    Mathf.Max(mb.size.z, 0.065f));
            }
            else
            {
                col.size = new Vector3(0.098f, 0.075f, 0.065f);
            }

            freeHookGrabInteractable = adjHook.GetComponent<XRGrabInteractable>();
            if (freeHookGrabInteractable == null)
                freeHookGrabInteractable = adjHook.gameObject.AddComponent<XRGrabInteractable>();

            // Prevent XRGrabInteractable from overwriting constrained belt length/orientation directly:
            // PlatformHookAttachment.UpdateGrabbedFreeEndPose drives the constrained pose synchronously.
            freeHookGrabInteractable.movementType = XRBaseInteractable.MovementType.Kinematic;
            freeHookGrabInteractable.useDynamicAttach = true;
            freeHookGrabInteractable.matchAttachPosition = true;
            freeHookGrabInteractable.matchAttachRotation = false;
            freeHookGrabInteractable.trackPosition = false;
            freeHookGrabInteractable.trackRotation = false;
            freeHookGrabInteractable.throwOnDetach = false;
            freeHookGrabInteractable.retainTransformParent = true;

            if (!freeHookGrabInteractable.colliders.Contains(col))
            {
                freeHookGrabInteractable.colliders.Clear();
                freeHookGrabInteractable.colliders.Add(col);
            }
        }

        private void SetupHandleLeverInteractable()
        {
            Transform handle = mechanicsController.handleTransform;
            if (handle == null) return;

            // Disable the lower arm collider and any old XRSimpleInteractable on Ratchet_Handle
            // so the grab point is exclusively Handle_Grip at the top of the lever!
            Collider handleCol = handle.GetComponent<Collider>();
            if (handleCol != null)
                handleCol.enabled = false;

            XRSimpleInteractable oldHandleInteractable = handle.GetComponent<XRSimpleInteractable>();
            if (oldHandleInteractable != null)
                oldHandleInteractable.enabled = false;

            Transform grip = handle.Find("Handle_Grip");
            Transform targetTr = grip != null ? grip : handle;

            Rigidbody gripRb = targetTr.GetComponent<Rigidbody>();
            if (gripRb == null) gripRb = targetTr.gameObject.AddComponent<Rigidbody>();
            gripRb.isKinematic = true;
            gripRb.useGravity = false;

            BoxCollider bc = targetTr.GetComponent<BoxCollider>();
            if (bc == null)
                bc = targetTr.gameObject.AddComponent<BoxCollider>();

            MeshFilter mf = targetTr.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                bc.center = mf.sharedMesh.bounds.center;
                bc.size = Vector3.Max(mf.sharedMesh.bounds.size, new Vector3(0.045f, 0.085f, 0.045f));
            }
            else
            {
                bc.center = Vector3.zero;
                bc.size = new Vector3(0.045f, 0.085f, 0.045f);
            }
            bc.enabled = true;

            handleLeverInteractable = targetTr.GetComponent<XRSimpleInteractable>();
            if (handleLeverInteractable == null)
                handleLeverInteractable = targetTr.gameObject.AddComponent<XRSimpleInteractable>();

            handleLeverInteractable.enabled = true;
            handleLeverInteractable.selectMode = InteractableSelectMode.Single;
            handleLeverInteractable.colliders.Clear();
            handleLeverInteractable.colliders.Add(bc);
        }

        private void SetupReleaseInteractables()
        {
            Transform catchTr = mechanicsController.releaseCatchTransform;
            if (catchTr != null)
            {
                BoxCollider catchCol = catchTr.GetComponent<BoxCollider>();
                if (catchCol == null)
                {
                    catchCol = catchTr.gameObject.AddComponent<BoxCollider>();
                    MeshFilter mf = catchTr.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null)
                    {
                        catchCol.center = mf.sharedMesh.bounds.center;
                        catchCol.size = Vector3.Max(mf.sharedMesh.bounds.size * 1.35f, new Vector3(0.055f, 0.045f, 0.045f));
                    }
                    else
                    {
                        catchCol.size = new Vector3(0.06f, 0.045f, 0.045f);
                    }
                }

                releaseCatchInteractable = catchTr.GetComponent<XRSimpleInteractable>();
                if (releaseCatchInteractable == null)
                    releaseCatchInteractable = catchTr.gameObject.AddComponent<XRSimpleInteractable>();

                if (!releaseCatchInteractable.colliders.Contains(catchCol))
                {
                    releaseCatchInteractable.colliders.Clear();
                    releaseCatchInteractable.colliders.Add(catchCol);
                }
            }

            Transform pawlTr = mechanicsController.pawlTransform;
            if (pawlTr != null)
            {
                BoxCollider pawlCol = pawlTr.GetComponent<BoxCollider>();
                if (pawlCol == null)
                {
                    pawlCol = pawlTr.gameObject.AddComponent<BoxCollider>();
                    MeshFilter mf = pawlTr.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null)
                    {
                        pawlCol.center = mf.sharedMesh.bounds.center;
                        pawlCol.size = Vector3.Max(mf.sharedMesh.bounds.size * 1.4f, new Vector3(0.055f, 0.035f, 0.045f));
                    }
                    else
                    {
                        pawlCol.size = new Vector3(0.06f, 0.035f, 0.045f);
                    }
                }

                pawlStepReleaseInteractable = pawlTr.GetComponent<XRSimpleInteractable>();
                if (pawlStepReleaseInteractable == null)
                    pawlStepReleaseInteractable = pawlTr.gameObject.AddComponent<XRSimpleInteractable>();

                if (!pawlStepReleaseInteractable.colliders.Contains(pawlCol))
                {
                    pawlStepReleaseInteractable.colliders.Clear();
                    pawlStepReleaseInteractable.colliders.Add(pawlCol);
                }
            }
        }

        private void ConfigureCollisionIsolation()
        {
            if (!Application.isPlaying) return;

            Collider[] beltCols = GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < beltCols.Length; i++)
            {
                for (int j = i + 1; j < beltCols.Length; j++)
                {
                    if (beltCols[i] != null && beltCols[j] != null)
                        Physics.IgnoreCollision(beltCols[i], beltCols[j], true);
                }
            }

            // Prevent kinematic hook collider from generating depenetration spikes against MoldCast or VibratingPlatform
            if (hookAttachment != null)
            {
                if (hookAttachment.activeMoldTransform != null)
                {
                    Collider[] moldCols = hookAttachment.activeMoldTransform.GetComponentsInChildren<Collider>(true);
                    IgnoreColliderSets(beltCols, moldCols);
                }
                if (hookAttachment.platformDeckTransform != null && hookAttachment.platformDeckTransform.parent != null)
                {
                    Collider[] platCols = hookAttachment.platformDeckTransform.parent.GetComponentsInChildren<Collider>(true);
                    IgnoreColliderSets(beltCols, platCols);
                }
            }
        }

        private static void IgnoreColliderSets(Collider[] setA, Collider[] setB)
        {
            for (int i = 0; i < setA.Length; i++)
            {
                if (setA[i] == null) continue;
                for (int j = 0; j < setB.Length; j++)
                {
                    if (setB[j] == null || setA[i] == setB[j]) continue;
                    Physics.IgnoreCollision(setA[i], setB[j], true);
                }
            }
        }

        private void BindEvents(bool subscribe)
        {
            if (freeHookGrabInteractable != null)
            {
                freeHookGrabInteractable.selectEntered.RemoveListener(OnFreeHookSelectEntered);
                freeHookGrabInteractable.selectExited.RemoveListener(OnFreeHookSelectExited);
                if (subscribe)
                {
                    freeHookGrabInteractable.selectEntered.AddListener(OnFreeHookSelectEntered);
                    freeHookGrabInteractable.selectExited.AddListener(OnFreeHookSelectExited);
                }
            }

            if (handleLeverInteractable != null)
            {
                handleLeverInteractable.selectEntered.RemoveListener(OnHandleSelectEntered);
                handleLeverInteractable.selectExited.RemoveListener(OnHandleSelectExited);
                if (subscribe)
                {
                    handleLeverInteractable.selectEntered.AddListener(OnHandleSelectEntered);
                    handleLeverInteractable.selectExited.AddListener(OnHandleSelectExited);
                }
            }

            if (releaseCatchInteractable != null)
            {
                releaseCatchInteractable.selectEntered.RemoveListener(OnReleaseCatchSelected);
                releaseCatchInteractable.activated.RemoveListener(OnReleaseCatchActivated);
                if (subscribe)
                {
                    releaseCatchInteractable.selectEntered.AddListener(OnReleaseCatchSelected);
                    releaseCatchInteractable.activated.AddListener(OnReleaseCatchActivated);
                }
            }

            if (pawlStepReleaseInteractable != null)
            {
                pawlStepReleaseInteractable.selectEntered.RemoveListener(OnPawlSelectEntered);
                pawlStepReleaseInteractable.selectExited.RemoveListener(OnPawlSelectExited);
                if (subscribe)
                {
                    pawlStepReleaseInteractable.selectEntered.AddListener(OnPawlSelectEntered);
                    pawlStepReleaseInteractable.selectExited.AddListener(OnPawlSelectExited);
                }
            }
        }

        // ------------------------------------------------------------------
        // 1. Free-End Adjustable J-Hook XR Handlers
        // ------------------------------------------------------------------

        private void OnFreeHookSelectEntered(SelectEnterEventArgs args)
        {
            if (hookAttachment == null || mechanicsController == null || args.interactorObject == null)
                return;

            RatchetBeltMechanicsController.ActiveBeltIndex = mechanicsController.beltIndex;

            if (!hookAttachment.BeginFreeEndGrab(out _))
            {
                _activeFreeHookInteractor = null;
                if (freeHookGrabInteractable != null && freeHookGrabInteractable.interactionManager != null)
                {
                    freeHookGrabInteractable.interactionManager.SelectExit(args.interactorObject, freeHookGrabInteractable);
                }
                return;
            }

            _activeFreeHookInteractor = args.interactorObject;
            Transform attach = _activeFreeHookInteractor.GetAttachTransform(freeHookGrabInteractable);
            Vector3 handPos = attach != null ? attach.position : _activeFreeHookInteractor.transform.position;
            Vector3 hookPos = mechanicsController.adjustableHookTransform != null
                ? mechanicsController.adjustableHookTransform.position
                : handPos;

            // Preserve initial contact offset (clamped for ray interactors) so grabbing NEVER snaps or jumps!
            _freeHookGrabOffset = Vector3.ClampMagnitude(hookPos - handPos, 0.12f);
        }

        private void OnFreeHookSelectExited(SelectExitEventArgs args)
        {
            if (args.interactorObject == _activeFreeHookInteractor)
            {
                _activeFreeHookInteractor = null;
            }

            if (hookAttachment == null || !hookAttachment.IsFreeEndGrabbed)
                return;

            hookAttachment.CompleteFreeEndRelease(out _);
        }

        // ------------------------------------------------------------------
        // 2. Ratchet Handle Lever XR Handlers
        // ------------------------------------------------------------------

        private void OnHandleSelectEntered(SelectEnterEventArgs args)
        {
            if (mechanicsController == null || args.interactorObject == null)
                return;

            RatchetBeltMechanicsController.ActiveBeltIndex = mechanicsController.beltIndex;
            _activeHandleInteractor = args.interactorObject;
            Transform attach = _activeHandleInteractor.GetAttachTransform(handleLeverInteractable);
            _handleGrabStartPos = attach != null ? attach.position : _activeHandleInteractor.transform.position;
            _handleGrabStartAngle = mechanicsController.CurrentHandleAngle;
        }

        private void OnHandleSelectExited(SelectExitEventArgs args)
        {
            if (args.interactorObject == _activeHandleInteractor)
            {
                _activeHandleInteractor = null;
            }
        }

        private void UpdateHandleFromXRInteractor()
        {
            if (_activeHandleInteractor == null || mechanicsController == null || mechanicsController.handleTransform == null)
                return;

            Transform handleTr = mechanicsController.handleTransform;
            Transform attach = _activeHandleInteractor.GetAttachTransform(handleLeverInteractable);
            Vector3 handPos = attach != null ? attach.position : _activeHandleInteractor.transform.position;
            Vector3 pivot = handleTr.position;

            // Ratchet_Handle hinges coaxially with the Mandrel around handleTr.up (World +X):
            // Measure signed angle in the orthogonal Y-Z plane from the 0° rest lever direction.
            Vector3 hingeAxis = handleTr.up.normalized;
            Quaternion restWorldRot = (handleTr.parent != null ? handleTr.parent.rotation : Quaternion.identity) *
                                      Quaternion.Euler(285f, 270f, 90f);
            Vector3 restLeverDir = (restWorldRot * Vector3.forward);
            restLeverDir = (restLeverDir - hingeAxis * Vector3.Dot(restLeverDir, hingeAxis)).normalized;

            Vector3 radial = handPos - pivot;
            Vector3 radialProj = radial - hingeAxis * Vector3.Dot(radial, hingeAxis);

            float targetAngle;
            if (radialProj.sqrMagnitude > 0.0006f && restLeverDir.sqrMagnitude > 0.1f)
            {
                float signedDeg = Vector3.SignedAngle(restLeverDir, radialProj.normalized, hingeAxis);
                targetAngle = Mathf.Clamp(signedDeg, mechanicsController.minHandleAngle, mechanicsController.maxHandleAngle);
            }
            else
            {
                Vector3 delta = handPos - _handleGrabStartPos;
                targetAngle = Mathf.Clamp(_handleGrabStartAngle + (delta.y + delta.z * 0.6f) * 320.0f,
                    mechanicsController.minHandleAngle, mechanicsController.maxHandleAngle);
            }

            mechanicsController.SetHandleAngle(targetAngle);
        }

        // ------------------------------------------------------------------
        // 3. Release Catch & Pawl XR Handlers
        // ------------------------------------------------------------------

        private void OnReleaseCatchSelected(SelectEnterEventArgs args)
        {
            if (mechanicsController == null) return;
            RatchetBeltMechanicsController.ActiveBeltIndex = mechanicsController.beltIndex;
            mechanicsController.TriggerFullRelease();
        }

        private void OnReleaseCatchActivated(ActivateEventArgs args)
        {
            if (mechanicsController == null) return;
            RatchetBeltMechanicsController.ActiveBeltIndex = mechanicsController.beltIndex;
            mechanicsController.TriggerFullRelease();
        }

        private void OnPawlSelectEntered(SelectEnterEventArgs args)
        {
            if (mechanicsController == null) return;
            RatchetBeltMechanicsController.ActiveBeltIndex = mechanicsController.beltIndex;
            mechanicsController.TriggerStepRelease();
            _pawlHeld = true;
            _pawlHoldStartTime = Time.time;
        }

        private void OnPawlSelectExited(SelectExitEventArgs args)
        {
            _pawlHeld = false;
        }

        // ------------------------------------------------------------------
        // Frame Sync
        // ------------------------------------------------------------------

        private void Update()
        {
            SyncGrabbedFreeHook();
            UpdateHandleFromXRInteractor();

            if (_pawlHeld && mechanicsController != null && Time.time - _pawlHoldStartTime > 0.55f)
            {
                mechanicsController.TriggerFullRelease();
                _pawlHeld = false;
            }
        }

        private void LateUpdate()
        {
            SyncGrabbedFreeHook();
        }

        private void OnBeforeRenderSync()
        {
            SyncGrabbedFreeHook();
        }

        private void SyncGrabbedFreeHook()
        {
            if (_activeFreeHookInteractor == null || freeHookGrabInteractable == null || !freeHookGrabInteractable.isSelected)
                return;
            if (hookAttachment == null || !hookAttachment.IsFreeEndGrabbed || mechanicsController == null || mechanicsController.adjustableHookTransform == null)
                return;

            Transform attach = _activeFreeHookInteractor.GetAttachTransform(freeHookGrabInteractable);
            Vector3 handPos = attach != null ? attach.position : _activeFreeHookInteractor.transform.position;
            Quaternion handRot = attach != null ? attach.rotation : _activeFreeHookInteractor.transform.rotation;

            hookAttachment.UpdateGrabbedFreeEndPose(handPos + _freeHookGrabOffset, handRot);
        }
    }
}
