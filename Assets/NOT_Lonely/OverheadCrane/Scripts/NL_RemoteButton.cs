namespace NOT_Lonely
{
    using System.Collections.Generic;
    using UnityEngine;
    using UnityEngine.Events;
    using UnityEngine.XR.Interaction.Toolkit;
    using UnityEngine.XR.Interaction.Toolkit.Interactables;
    using UnityEngine.XR.Interaction.Toolkit.Interactors;
    using Sefpro.Crane;

    public class NL_RemoteButton : MonoBehaviour
    {
        public enum ButtonDirection
        {
            Unknown = 0,
            Front,
            Back,
            Left,
            Right,
            Up,
            Down
        }

        [Header("Trigger Settings")]
        public Collider[] excludeTriggers;

        [HideInInspector]
        public Collider btnCollider;

        [Header("Button Events")]
        public UnityEvent onButtonPressed;
        public UnityEvent onButtonHold;
        public UnityEvent onButtonRelease;

        // Kept for compatibility with the original setup.
        [HideInInspector]
        public bool isInteractable = true;

        // =========================================================
        // EXPLICIT MOMENTARY HOLD-TO-MOVE STATE
        // =========================================================

        private readonly HashSet<IXRInteractor> _selectingInteractors = new HashSet<IXRInteractor>();
        private readonly HashSet<IXRInteractor> _activatingInteractors = new HashSet<IXRInteractor>();
        private readonly HashSet<IXRInteractor> _pokingInteractors = new HashSet<IXRInteractor>();
        private readonly HashSet<Collider> _triggerColliders = new HashSet<Collider>();
        private readonly List<IXRInteractor> _pruneInteractorBuffer = new List<IXRInteractor>();
        private readonly List<Collider> _pruneColliderBuffer = new List<Collider>();

        private bool _manualOrKeyboardHeld = false;
        private bool isPressed = false;
        private bool isHovering = false;
        private IXRInteractor activeOperatingInteractor;

        private XRGrabInteractable parentGrabInteractable;
        private XRSimpleInteractable buttonSimpleInteractable;
        private NL_Remote parentRemote;
        private ButtonDirection buttonDirection = ButtonDirection.Unknown;

        /// <summary>
        /// When true (controlled by NL_Remote), NL_Remote dispatches conflicting-direction-safe movement
        /// so opposite buttons pressed simultaneously cancel cleanly instead of fighting.
        /// </summary>
        [HideInInspector]
        public bool managedByParentRemote = false;

        public bool IsPressed => isPressed;
        public bool IsHovering => isHovering;
        public IXRInteractor ActiveOperatingInteractor => activeOperatingInteractor;
        public ButtonDirection Direction => buttonDirection;

        // =========================================================
        // INITIALIZATION
        // =========================================================

        private void Awake()
        {
            CacheReferences();
            isInteractable = true;
        }

        private void CacheReferences()
        {
            if (btnCollider == null)
                btnCollider = GetComponent<Collider>();
            if (parentGrabInteractable == null)
                parentGrabInteractable = GetComponentInParent<XRGrabInteractable>();
            if (buttonSimpleInteractable == null)
                buttonSimpleInteractable = GetComponent<XRSimpleInteractable>();
            if (parentRemote == null)
                parentRemote = GetComponentInParent<NL_Remote>();

            InferButtonDirection();
        }

        private void InferButtonDirection()
        {
            string n = gameObject.name.ToLowerInvariant();
            if (n.Contains("front") || n.Contains("forward"))
                buttonDirection = ButtonDirection.Front;
            else if (n.Contains("back") || n.Contains("backward"))
                buttonDirection = ButtonDirection.Back;
            else if (n.Contains("left"))
                buttonDirection = ButtonDirection.Left;
            else if (n.Contains("right"))
                buttonDirection = ButtonDirection.Right;
            else if (n.Contains("up"))
                buttonDirection = ButtonDirection.Up;
            else if (n.Contains("down"))
                buttonDirection = ButtonDirection.Down;
        }

        private void OnEnable()
        {
            CacheReferences();
            isInteractable = true;
            ClearAllHoldSourcesImmediate(invokeReleaseIfWasPressed: false);
            ButtonSwitch(false);

            if (buttonSimpleInteractable != null)
            {
                buttonSimpleInteractable.selectEntered.RemoveListener(OnXRSelectEntered);
                buttonSimpleInteractable.selectExited.RemoveListener(OnXRSelectExited);
                buttonSimpleInteractable.activated.RemoveListener(OnXRActivated);
                buttonSimpleInteractable.deactivated.RemoveListener(OnXRDeactivated);
                buttonSimpleInteractable.hoverEntered.RemoveListener(OnXRHoverEntered);
                buttonSimpleInteractable.hoverExited.RemoveListener(OnXRHoverExited);

                buttonSimpleInteractable.selectEntered.AddListener(OnXRSelectEntered);
                buttonSimpleInteractable.selectExited.AddListener(OnXRSelectExited);
                buttonSimpleInteractable.activated.AddListener(OnXRActivated);
                buttonSimpleInteractable.deactivated.AddListener(OnXRDeactivated);
                buttonSimpleInteractable.hoverEntered.AddListener(OnXRHoverEntered);
                buttonSimpleInteractable.hoverExited.AddListener(OnXRHoverExited);
            }
        }

        private void OnDisable()
        {
            if (buttonSimpleInteractable != null)
            {
                buttonSimpleInteractable.selectEntered.RemoveListener(OnXRSelectEntered);
                buttonSimpleInteractable.selectExited.RemoveListener(OnXRSelectExited);
                buttonSimpleInteractable.activated.RemoveListener(OnXRActivated);
                buttonSimpleInteractable.deactivated.RemoveListener(OnXRDeactivated);
                buttonSimpleInteractable.hoverEntered.RemoveListener(OnXRHoverEntered);
                buttonSimpleInteractable.hoverExited.RemoveListener(OnXRHoverExited);
            }

            ClearAllHoldSourcesImmediate(invokeReleaseIfWasPressed: true);
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus)
            {
                ClearAllHoldSourcesImmediate(invokeReleaseIfWasPressed: true);
            }
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            if (pauseStatus)
            {
                ClearAllHoldSourcesImmediate(invokeReleaseIfWasPressed: true);
            }
        }

        // =========================================================
        // FRAME UPDATE & CONTINUOUS VALIDATION (ANTI-STUCK)
        // =========================================================

        private void Update()
        {
            PruneStaleHoldSources();
            ReconcilePressedState();

            if (isPressed && !managedByParentRemote)
            {
                onButtonHold?.Invoke();
            }
        }

        /// <summary>
        /// Removes any interactor or trigger collider that is no longer genuinely selecting, activating,
        /// or physically poking this button. Guarantees zero stuck buttons under any XR transition.
        /// </summary>
        public void PruneStaleHoldSources()
        {
            // 1. Validate selecting interactors
            if (_selectingInteractors.Count > 0)
            {
                _pruneInteractorBuffer.Clear();
                foreach (var inter in _selectingInteractors)
                {
                    if (!IsInteractorStillValidForSelect(inter))
                        _pruneInteractorBuffer.Add(inter);
                }
                for (int i = 0; i < _pruneInteractorBuffer.Count; i++)
                    _selectingInteractors.Remove(_pruneInteractorBuffer[i]);
            }

            // 2. Validate activating interactors
            if (_activatingInteractors.Count > 0)
            {
                _pruneInteractorBuffer.Clear();
                foreach (var inter in _activatingInteractors)
                {
                    if (!IsInteractorStillValidForActivate(inter))
                        _pruneInteractorBuffer.Add(inter);
                }
                for (int i = 0; i < _pruneInteractorBuffer.Count; i++)
                    _activatingInteractors.Remove(_pruneInteractorBuffer[i]);
            }

            // 3. Validate direct poke interactors (must still be hovering and within poke distance)
            if (_pokingInteractors.Count > 0)
            {
                _pruneInteractorBuffer.Clear();
                foreach (var inter in _pokingInteractors)
                {
                    if (!IsInteractorStillValidForPoke(inter))
                        _pruneInteractorBuffer.Add(inter);
                }
                for (int i = 0; i < _pruneInteractorBuffer.Count; i++)
                    _pokingInteractors.Remove(_pruneInteractorBuffer[i]);
            }

            // 4. Validate physical trigger colliders
            if (_triggerColliders.Count > 0)
            {
                _pruneColliderBuffer.Clear();
                foreach (var col in _triggerColliders)
                {
                    if (!IsTriggerColliderStillValid(col))
                        _pruneColliderBuffer.Add(col);
                }
                for (int i = 0; i < _pruneColliderBuffer.Count; i++)
                    _triggerColliders.Remove(_pruneColliderBuffer[i]);
            }
        }

        private bool IsInteractorObjectAlive(IXRInteractor interactor)
        {
            if (interactor == null) return false;
            if (interactor is Object unityObj && unityObj == null) return false;
            if (interactor is Behaviour beh && (!beh.enabled || !beh.gameObject.activeInHierarchy)) return false;
            if (IsInteractorHoldingRemoteBody(interactor)) return false;
            return true;
        }

        private bool IsInteractorStillValidForSelect(IXRInteractor interactor)
        {
            if (!IsInteractorObjectAlive(interactor)) return false;
            if (!(interactor is IXRSelectInteractor selectInteractor)) return false;

            if (buttonSimpleInteractable != null)
            {
                if (!buttonSimpleInteractable.isSelected) return false;
                bool found = false;
                for (int i = 0; i < buttonSimpleInteractable.interactorsSelecting.Count; i++)
                {
                    if (ReferenceEquals(buttonSimpleInteractable.interactorsSelecting[i], selectInteractor))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found) return false;
            }

            return true;
        }

        private bool IsInteractorStillValidForActivate(IXRInteractor interactor)
        {
            if (!IsInteractorObjectAlive(interactor)) return false;

            if (buttonSimpleInteractable != null)
            {
                bool hoveredOrSelected = false;
                if (interactor is IXRHoverInteractor hoverInter)
                {
                    for (int i = 0; i < buttonSimpleInteractable.interactorsHovering.Count; i++)
                    {
                        if (ReferenceEquals(buttonSimpleInteractable.interactorsHovering[i], hoverInter))
                        {
                            hoveredOrSelected = true;
                            break;
                        }
                    }
                }
                if (!hoveredOrSelected && interactor is IXRSelectInteractor selInter)
                {
                    for (int i = 0; i < buttonSimpleInteractable.interactorsSelecting.Count; i++)
                    {
                        if (ReferenceEquals(buttonSimpleInteractable.interactorsSelecting[i], selInter))
                        {
                            hoveredOrSelected = true;
                            break;
                        }
                    }
                }
                if (!hoveredOrSelected) return false;
            }

            return true;
        }

        private bool IsInteractorStillValidForPoke(IXRInteractor interactor)
        {
            if (!IsInteractorObjectAlive(interactor)) return false;
            if (!IsDirectPokeHover(interactor)) return false;

            if (buttonSimpleInteractable != null && interactor is IXRHoverInteractor hoverInter)
            {
                bool isStillHovering = false;
                for (int i = 0; i < buttonSimpleInteractable.interactorsHovering.Count; i++)
                {
                    if (ReferenceEquals(buttonSimpleInteractable.interactorsHovering[i], hoverInter))
                    {
                        isStillHovering = true;
                        break;
                    }
                }
                if (!isStillHovering) return false;
            }

            return true;
        }

        private bool IsTriggerColliderStillValid(Collider col)
        {
            if (col == null || !col.enabled || !col.gameObject.activeInHierarchy)
                return false;
            if (!CanInteract(col))
                return false;
            if (btnCollider != null && btnCollider.enabled)
            {
                Vector3 closest = btnCollider.ClosestPoint(col.bounds.center);
                if (Vector3.Distance(closest, col.bounds.center) > 0.045f)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Evaluates whether at least one valid hold source is currently active, and updates
        /// isPressed, visual button travel, and press/release events accordingly.
        /// </summary>
        private void ReconcilePressedState()
        {
            IXRInteractor primaryInteractor = null;
            foreach (var inter in _selectingInteractors) { primaryInteractor = inter; break; }
            if (primaryInteractor == null)
            {
                foreach (var inter in _activatingInteractors) { primaryInteractor = inter; break; }
            }
            if (primaryInteractor == null)
            {
                foreach (var inter in _pokingInteractors) { primaryInteractor = inter; break; }
            }

            activeOperatingInteractor = primaryInteractor;

            bool shouldBePressed = _manualOrKeyboardHeld ||
                                   _selectingInteractors.Count > 0 ||
                                   _activatingInteractors.Count > 0 ||
                                   _pokingInteractors.Count > 0 ||
                                   _triggerColliders.Count > 0;

            isHovering = shouldBePressed;

            if (shouldBePressed && !isPressed)
            {
                isPressed = true;
                ButtonSwitch(true);
                if (!managedByParentRemote)
                {
                    onButtonPressed?.Invoke();
                }
            }
            else if (!shouldBePressed && isPressed)
            {
                isPressed = false;
                activeOperatingInteractor = null;
                ButtonSwitch(false);
                onButtonRelease?.Invoke();
            }
        }

        /// <summary>
        /// Invoked by NL_Remote each frame while this button is the active non-conflicting direction.
        /// </summary>
        public void ExecuteHoldStep()
        {
            if (!isPressed) return;
            onButtonHold?.Invoke();
        }

        public bool IsInteractorHoldingButton(IXRInteractor interactor)
        {
            if (interactor == null) return false;
            return _selectingInteractors.Contains(interactor) ||
                   _activatingInteractors.Contains(interactor) ||
                   _pokingInteractors.Contains(interactor);
        }

        public void ClearInteractorHold(IXRInteractor interactor)
        {
            if (interactor == null) return;
            bool removed = false;
            removed |= _selectingInteractors.Remove(interactor);
            removed |= _activatingInteractors.Remove(interactor);
            removed |= _pokingInteractors.Remove(interactor);
            if (removed)
            {
                ReconcilePressedState();
            }
        }

        public void ClearAllHoldSourcesImmediate(bool invokeReleaseIfWasPressed = true)
        {
            bool wasPressed = isPressed;
            _selectingInteractors.Clear();
            _activatingInteractors.Clear();
            _pokingInteractors.Clear();
            _triggerColliders.Clear();
            _manualOrKeyboardHeld = false;
            isPressed = false;
            isHovering = false;
            activeOperatingInteractor = null;
            ButtonSwitch(false);

            if (wasPressed && invokeReleaseIfWasPressed)
            {
                onButtonRelease?.Invoke();
            }
        }

        // =========================================================
        // XR SIMPLE INTERACTABLE CALLBACKS
        // =========================================================

        private void OnXRSelectEntered(SelectEnterEventArgs args)
        {
            if (args.interactorObject == null || IsInteractorHoldingRemoteBody(args.interactorObject))
                return;

            ReleaseOtherButtonsForInteractor(args.interactorObject);
            _selectingInteractors.Add(args.interactorObject);
            ReconcilePressedState();
        }

        private void OnXRSelectExited(SelectExitEventArgs args)
        {
            if (args.interactorObject != null)
            {
                _selectingInteractors.Remove(args.interactorObject);
                _activatingInteractors.Remove(args.interactorObject);
                _pokingInteractors.Remove(args.interactorObject);
            }
            ReconcilePressedState();
        }

        private void OnXRActivated(ActivateEventArgs args)
        {
            if (args.interactorObject == null || IsInteractorHoldingRemoteBody(args.interactorObject))
                return;

            ReleaseOtherButtonsForInteractor(args.interactorObject);
            _activatingInteractors.Add(args.interactorObject);
            ReconcilePressedState();
        }

        private void OnXRDeactivated(DeactivateEventArgs args)
        {
            if (args.interactorObject != null)
            {
                _activatingInteractors.Remove(args.interactorObject);
            }
            ReconcilePressedState();
        }

        private void OnXRHoverEntered(HoverEnterEventArgs args)
        {
            if (args.interactorObject == null || IsInteractorHoldingRemoteBody(args.interactorObject))
                return;

            if (IsDirectPokeHover(args.interactorObject))
            {
                ReleaseOtherButtonsForInteractor(args.interactorObject);
                _pokingInteractors.Add(args.interactorObject);
                ReconcilePressedState();
            }
        }

        private void OnXRHoverExited(HoverExitEventArgs args)
        {
            if (args.interactorObject != null)
            {
                _pokingInteractors.Remove(args.interactorObject);
                // If an interactor stops hovering while only activating (not selecting), clear its activation
                if (!_selectingInteractors.Contains(args.interactorObject))
                {
                    _activatingInteractors.Remove(args.interactorObject);
                }
            }
            ReconcilePressedState();
        }

        private bool IsDirectPokeHover(IXRInteractor interactor)
        {
            if (interactor == null) return false;

            // Only XRPokeInteractor or direct sphere interactors within tight poke range count as direct poke hover;
            // NearFarInteractor must press Select/Activate to actuate buttons so hovering alone never sticks a button!
            bool isPokeInteractor = interactor is XRPokeInteractor;
            if (!isPokeInteractor)
                return false;

            Transform attach = FreeFloatingCraneRemote.ResolveHandReferenceTransform(interactor, buttonSimpleInteractable);
            if (attach == null) attach = interactor.transform;

            float dist = Vector3.Distance(attach.position, transform.position);
            if (dist > 0.022f)
                return false;

            if (transform.parent != null)
            {
                var siblings = transform.parent.GetComponentsInChildren<NL_RemoteButton>();
                for (int i = 0; i < siblings.Length; i++)
                {
                    if (siblings[i] == null || siblings[i] == this) continue;
                    if (Vector3.Distance(attach.position, siblings[i].transform.position) < dist)
                        return false;
                }
            }
            return true;
        }

        private void ReleaseOtherButtonsForInteractor(IXRInteractor interactor)
        {
            if (interactor == null || transform.parent == null) return;
            var siblings = transform.parent.GetComponentsInChildren<NL_RemoteButton>();
            for (int i = 0; i < siblings.Length; i++)
            {
                var s = siblings[i];
                if (s == null || s == this) continue;
                s.ClearInteractorHold(interactor);
            }
        }

        public bool IsInteractorHoldingRemoteBody(IXRInteractor interactor)
        {
            if (interactor == null)
                return false;

            if (parentGrabInteractable == null)
                parentGrabInteractable = GetComponentInParent<XRGrabInteractable>();

            if (parentGrabInteractable != null && parentGrabInteractable.isSelected)
            {
                for (int i = 0; i < parentGrabInteractable.interactorsSelecting.Count; i++)
                {
                    if (ReferenceEquals(parentGrabInteractable.interactorsSelecting[i], interactor))
                        return true;
                }
            }

            return false;
        }

        // =========================================================
        // DESKTOP / KEYBOARD / DIRECT API
        // =========================================================

        public void StartHover()
        {
            isInteractable = true;
            _manualOrKeyboardHeld = true;
            ReconcilePressedState();
        }

        public void HoldHover()
        {
            if (!isPressed)
                return;

            if (!managedByParentRemote)
            {
                onButtonHold?.Invoke();
            }
        }

        public void StopHover()
        {
            _manualOrKeyboardHeld = false;
            // Also clear any stale non-selected sources when StopHover is explicitly requested
            _pokingInteractors.Clear();
            _activatingInteractors.Clear();
            _triggerColliders.Clear();
            ReconcilePressedState();
        }

        // =========================================================
        // PHYSICAL TRIGGER COMPATIBILITY
        // =========================================================

        private void OnTriggerEnter(Collider other)
        {
            if (!CanInteract(other))
                return;

            _triggerColliders.Add(other);
            ReconcilePressedState();
        }

        private void OnTriggerStay(Collider other)
        {
            if (!CanInteract(other))
            {
                if (_triggerColliders.Remove(other))
                    ReconcilePressedState();
                return;
            }

            if (_triggerColliders.Add(other))
                ReconcilePressedState();
        }

        private void OnTriggerExit(Collider other)
        {
            if (_triggerColliders.Remove(other))
            {
                ReconcilePressedState();
            }
        }

        // =========================================================
        // ORIGINAL KEYBOARD COMPATIBILITY
        // =========================================================

        public void OnPressed()
        {
            StartHover();
        }

        public void OnHold()
        {
            if (!_manualOrKeyboardHeld)
            {
                _manualOrKeyboardHeld = true;
                ReconcilePressedState();
            }
        }

        public void OnRelease()
        {
            _manualOrKeyboardHeld = false;
            ReconcilePressedState();
        }

        // =========================================================
        // BUTTON VISUAL MOVEMENT
        // =========================================================

        public void ButtonSwitch(bool value)
        {
            float pos = value ? -0.005f : 0f;

            transform.localPosition = new Vector3(
                transform.localPosition.x,
                transform.localPosition.y,
                pos
            );
        }

        // =========================================================
        // COLLIDER FILTER
        // =========================================================

        public bool CanInteract(Collider other)
        {
            if (other == null)
                return false;

            // Ignore any collider on the remote hierarchy itself.
            if (transform.parent != null &&
                transform.parent.parent != null &&
                other.transform.IsChildOf(transform.parent.parent))
            {
                return false;
            }

            // Ignore any collider belonging to an XR Interactor.
            // XR Interactors must use XRSimpleInteractable Select/Activate/Poke events rather than raw trigger overlap,
            // preventing a hand's 3.5cm trigger sphere from latching buttons on mere proximity!
            var interactor = other.GetComponentInParent<IXRInteractor>();
            if (interactor != null)
            {
                return false;
            }

            // Ignore explicitly excluded colliders.
            if (excludeTriggers != null)
            {
                for (int i = 0; i < excludeTriggers.Length; i++)
                {
                    if (other == excludeTriggers[i])
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
