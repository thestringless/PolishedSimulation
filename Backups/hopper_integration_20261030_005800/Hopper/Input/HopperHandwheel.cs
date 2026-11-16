using System;
using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace LiquidSolidPrototype.Input
{
    /// <summary>
    /// Interactive handwheel controller for the SEFPRO Vibrocasting Hopper.
    /// Provides physical multi-turn mouse-drag rotation for desktop testing
    /// and a clean decoupled programmatic API for future XR grab/lever interaction.
    /// Mechanically drives the hopper's discharge gate mechanism and material pour state.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class HopperHandwheel : MonoBehaviour
    {
        [Header("Handwheel Rotation Limits")]
        [Tooltip("Number of full 360-degree rotations required to fully open the gate.")]
        [SerializeField] private float turnsToFullyOpen = 2.0f;
        [Tooltip("Max rotation angle in degrees around local X when fully open (turnsToFullyOpen * 360).")]
        [SerializeField] private float maxWheelAngle = 720f;
        [Tooltip("Drag sensitivity (degrees of wheel rotation per pixel of mouse drag).")]
        [SerializeField] private float dragSensitivity = 0.35f;

        [Header("Linked Hierarchy Nodes")]
        [SerializeField] private Transform gatePivot;
        [SerializeField] private Transform linkedHandwheelRight;

        [Header("Gate Settings")]
        [Tooltip("Max angle of Gate_Pivot around local X when fully open.")]
        [SerializeField] private float maxGateAngle = 28f;

        [Header("Interaction State (Read Only)")]
        [SerializeField] private float currentAngle = 0f;
        [SerializeField] private bool isDragging = false;
        [SerializeField] private bool isHovered = false;

        private Quaternion gateClosedRot = Quaternion.identity;
        private Quaternion gateOpenRot;
        private Quaternion wheelLeftClosedRot = Quaternion.identity;
        private Quaternion wheelRightClosedRot = Quaternion.identity;

        private Vector2 lastMousePos;

        public float CurrentAngle => currentAngle;
        public float MaxWheelAngle => maxWheelAngle;
        public float TurnsToFullyOpen => turnsToFullyOpen;
        public float OpenFraction => maxWheelAngle > 0.001f ? Mathf.Clamp01(currentAngle / maxWheelAngle) : 0f;
        public bool IsOpen => OpenFraction > 0.015f;
        public bool IsDragging => isDragging;

        /// <summary>
        /// Event invoked when handwheel rotation changes: (float openFraction).
        /// </summary>
        public event Action<float> OnOpenFractionChanged;

        private void Awake()
        {
            if (turnsToFullyOpen > 0f)
            {
                maxWheelAngle = turnsToFullyOpen * 360f;
            }

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

            if (gatePivot != null)
            {
                gateClosedRot = gatePivot.localRotation;
                gateOpenRot = Quaternion.Euler(maxGateAngle, 0f, 0f) * gateClosedRot;
            }

            if (linkedHandwheelRight != null)
            {
                wheelRightClosedRot = linkedHandwheelRight.localRotation;
            }

            UpdateMechanism();
        }

        private void Update()
        {
            HandleMouseInteraction();
            UpdateMechanism();
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
            bool rayHit = Physics.Raycast(ray, out RaycastHit hit, 50f) && (hit.collider == GetComponent<Collider>() || hit.transform == transform);
            isHovered = rayHit;

            if (isMouseDown && rayHit)
            {
                isDragging = true;
                lastMousePos = mousePos;
            }

            if (isDragging)
            {
                if (isMouseHeld)
                {
                    Vector2 delta = mousePos - lastMousePos;
                    lastMousePos = mousePos;

                    // Dragging down or right turns clockwise (opens gate)
                    // Dragging up or left turns counter-clockwise (closes gate)
                    float deltaAngle = (-delta.y + delta.x * 0.5f) * dragSensitivity;

                    ApplyAngleDelta(deltaAngle);
                }
                else if (isMouseUp)
                {
                    isDragging = false;
                }
            }
        }

        /// <summary>
        /// Programmatic API to adjust rotation by delta degrees.
        /// Usable by future XR grab/lever interactables.
        /// </summary>
        public void ApplyAngleDelta(float deltaAngle)
        {
            SetAngle(currentAngle + deltaAngle);
        }

        /// <summary>
        /// Programmatic API to directly set the open fraction (0.0 to 1.0).
        /// Ideal for XR slider/dial interop.
        /// </summary>
        public void SetOpenFraction(float fraction)
        {
            SetAngle(Mathf.Clamp01(fraction) * maxWheelAngle);
        }

        private void SetAngle(float angle)
        {
            float prevAngle = currentAngle;
            currentAngle = Mathf.Clamp(angle, 0f, maxWheelAngle);

            if (Mathf.Abs(currentAngle - prevAngle) > 0.001f)
            {
                UpdateMechanism();
                OnOpenFractionChanged?.Invoke(OpenFraction);
            }
        }

        private void UpdateMechanism()
        {
            // Spin Left Handwheel around local X
            transform.localRotation = wheelLeftClosedRot * Quaternion.Euler(currentAngle, 0f, 0f);

            // Mirror on Right Handwheel (connected via drive chain)
            if (linkedHandwheelRight != null)
            {
                linkedHandwheelRight.localRotation = wheelRightClosedRot * Quaternion.Euler(-currentAngle, 0f, 0f);
            }

            // Drive discharge gate shutter
            if (gatePivot != null)
            {
                gatePivot.localRotation = Quaternion.Slerp(gateClosedRot, gateOpenRot, OpenFraction);
            }
        }

        /// <summary>
        /// Smoothly closes the handwheel and gate back to 0.
        /// </summary>
        public void ResetWheel()
        {
            isDragging = false;
            currentAngle = 0f;
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
