using UnityEngine;

namespace Sefpro.DesktopTesting
{
    /// <summary>
    /// First-person desktop controller designed for VR training warehouse testing.
    /// Supports WASD navigation, mouse look, sprint, jump, cursor lock/unlock,
    /// floor grounding, warehouse wall bounds clamping, and station control pausing.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class DesktopPlayerController : MonoBehaviour
    {
        [Header("Movement Settings")]
        [Tooltip("Standard walking speed in m/s.")]
        public float walkSpeed = 3.8f;

        [Tooltip("Running speed in m/s when Left Shift is held.")]
        public float runSpeed = 6.8f;

        [Tooltip("Jump velocity / height in meters.")]
        public float jumpHeight = 0.8f;

        [Tooltip("Gravity acceleration in m/s^2.")]
        public float gravity = -18.0f;

        [Header("Look Settings")]
        [Tooltip("Mouse look sensitivity.")]
        public float mouseSensitivity = 2.0f;

        [Tooltip("Minimum vertical camera angle in degrees.")]
        public float minPitch = -85.0f;

        [Tooltip("Maximum vertical camera angle in degrees.")]
        public float maxPitch = 85.0f;

        [Tooltip("Realistic first-person eye height above ground in meters.")]
        public float eyeHeight = 1.70f;

        [Header("Warehouse Boundary Clamping")]
        [Tooltip("Enforces safety bounds inside the warehouse to prevent walking through exterior walls.")]
        public bool enforceWarehouseBounds = true;
        public Vector3 minWarehouseBounds = new Vector3(-40.5f, 0.0f, -17.5f);
        public Vector3 maxWarehouseBounds = new Vector3(-1.0f, 6.0f, -2.8f);

        [Header("State")]
        public bool isMovementEnabled = true;
        public bool isLookEnabled = true;

        private CharacterController characterController;
        private Transform cameraTransform;
        private float verticalVelocity = 0.0f;
        private float pitch = 0.0f;

        public Transform CameraTransform => cameraTransform;
        public bool IsCursorLocked => Cursor.lockState == CursorLockMode.Locked;

        private void Awake()
        {
            characterController = GetComponent<CharacterController>();
            ConfigureCharacterController();
            SetupCamera();
        }

        private void Start()
        {
            LockCursor();
        }

        private void ConfigureCharacterController()
        {
            if (characterController != null)
            {
                characterController.height = eyeHeight + 0.1f;
                characterController.radius = 0.35f;
                characterController.center = new Vector3(0.0f, (eyeHeight + 0.1f) * 0.5f, 0.0f);
                characterController.stepOffset = 0.3f;
                characterController.slopeLimit = 45.0f;
            }
        }

        private void SetupCamera()
        {
            Camera mainCam = Camera.main;
            if (mainCam == null)
            {
                mainCam = FindAnyObjectByType<Camera>();
            }

            if (mainCam != null)
            {
                // Create or find CameraHolder
                Transform holder = transform.Find("CameraHolder");
                if (holder == null)
                {
                    GameObject holderGo = new GameObject("CameraHolder");
                    holder = holderGo.transform;
                    holder.SetParent(transform, false);
                }
                holder.localPosition = new Vector3(0.0f, eyeHeight, 0.0f);
                holder.localRotation = Quaternion.identity;

                mainCam.transform.SetParent(holder, false);
                mainCam.transform.localPosition = Vector3.zero;
                mainCam.transform.localRotation = Quaternion.identity;
                cameraTransform = mainCam.transform;
            }
        }

        private void Update()
        {
            HandleCursorLockInput();

            if (IsCursorLocked)
            {
                HandleMouseLook();
            }

            if (isMovementEnabled)
            {
                HandleMovement();
            }

            EnforceBoundsAndFloor();
        }

        private void HandleCursorLockInput()
        {
            // Escape releases cursor
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                UnlockCursor();
            }
            // Left click recaptures cursor when unlocked
            else if (!IsCursorLocked && Input.GetMouseButtonDown(0))
            {
                // Only lock if not clicking on GUI elements (handled by InteractionManager)
                LockCursor();
            }
        }

        public void LockCursor()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        public void UnlockCursor()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void HandleMouseLook()
        {
            if (!isLookEnabled || cameraTransform == null) return;

            float mouseX = Input.GetAxis("Mouse X") * mouseSensitivity;
            float mouseY = Input.GetAxis("Mouse Y") * mouseSensitivity;

            // Yaw (horizontal) rotates the character body
            transform.Rotate(Vector3.up * mouseX);

            // Pitch (vertical) rotates the camera holder
            pitch -= mouseY;
            pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
            cameraTransform.parent.localRotation = Quaternion.Euler(pitch, 0.0f, 0.0f);
        }

        private void HandleMovement()
        {
            float horizontal = Input.GetAxisRaw("Horizontal");
            float vertical = Input.GetAxisRaw("Vertical");

            Vector3 moveDirection = (transform.forward * vertical + transform.right * horizontal).normalized;

            bool isRunning = Input.GetKey(KeyCode.LeftShift);
            float currentSpeed = isRunning ? runSpeed : walkSpeed;

            // Gravity & Jump
            bool isGrounded = characterController.isGrounded || transform.position.y <= 0.02f;
            if (isGrounded)
            {
                if (verticalVelocity < 0.0f)
                {
                    verticalVelocity = -2.0f; // Small grounding force
                }

                if (Input.GetKeyDown(KeyCode.Space))
                {
                    verticalVelocity = Mathf.Sqrt(jumpHeight * -2.0f * gravity);
                }
            }
            else
            {
                verticalVelocity += gravity * Time.deltaTime;
            }

            Vector3 velocity = moveDirection * currentSpeed + Vector3.up * verticalVelocity;
            characterController.Move(velocity * Time.deltaTime);
        }

        private void EnforceBoundsAndFloor()
        {
            Vector3 pos = transform.position;

            // Absolute floor safeguard so player never falls into the void
            if (pos.y < 0.0f)
            {
                pos.y = 0.0f;
                verticalVelocity = 0.0f;
            }

            if (enforceWarehouseBounds)
            {
                pos.x = Mathf.Clamp(pos.x, minWarehouseBounds.x, maxWarehouseBounds.x);
                pos.z = Mathf.Clamp(pos.z, minWarehouseBounds.z, maxWarehouseBounds.z);
            }

            transform.position = pos;
        }
    }
}
