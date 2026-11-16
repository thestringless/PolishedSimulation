using UnityEngine;

namespace LiquidSolidPrototype.Core
{
    /// <summary>
    /// Designated structural lifting point for the SEFPRO Vibrocasting Hopper assembly.
    /// Provides spatial anchoring for the overhead crane hook, enforces whole-assembly movement
    /// without component separation, clamps floor contact ($Y \ge 0$) and warehouse bounds,
    /// and ensures rock-solid stability upon detachment.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class HopperCraneLiftingPoint : MonoBehaviour
    {
        [Header("Hopper Root")]
        [Tooltip("The main structural root transform of the hopper station that moves as a coherent whole.")]
        public Transform hopperRoot;

        [Header("State (Read Only)")]
        [SerializeField] private bool isAttached = false;
        [SerializeField] private bool isHookInRange = false;
        [SerializeField] private Transform currentHookMount;
        [SerializeField] private Rigidbody currentHookRb;

        [Header("Warehouse Physical Limits")]
        public float minGroundY = 0.0f;
        public Vector2 xBounds = new Vector2(-32.0f, -1.5f);
        public Vector2 zBounds = new Vector2(-15.5f, -2.5f);

        [Header("Lifting Attachment Dynamics")]
        [Tooltip("Offset from the hook mount origin down to the hook saddle throat.")]
        public float hookSaddleOffset = 0.365f;

        public bool IsAttached => isAttached;
        public bool IsHookInRange => isHookInRange;
        public Transform CurrentHookMount => currentHookMount;

        private Rigidbody hopperRb;
        private Vector3 localEyeOffset;
        private bool isFallingToGround = false;

        private void Awake()
        {
            if (hopperRoot == null)
            {
                hopperRoot = transform.root;
            }

            localEyeOffset = hopperRoot.InverseTransformPoint(transform.position);

            hopperRb = hopperRoot.GetComponent<Rigidbody>();
            if (hopperRb == null)
            {
                hopperRb = hopperRoot.gameObject.AddComponent<Rigidbody>();
            }

            hopperRb.isKinematic = true;
            hopperRb.useGravity = false;
            hopperRb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
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
        /// Attaches the entire hopper assembly to the crane hook.
        /// </summary>
        public bool AttachToHook(Transform hookMount, Rigidbody hookRb)
        {
            if (isAttached || hookMount == null)
                return false;

            if (hopperRoot == null)
            {
                hopperRoot = transform.root;
            }

            localEyeOffset = hopperRoot.InverseTransformPoint(transform.position);

            currentHookMount = hookMount;
            currentHookRb = hookRb;
            isAttached = true;
            isFallingToGround = false;

            if (hopperRb != null)
            {
                hopperRb.isKinematic = true; // Kinematically locked to crane hook
                hopperRb.useGravity = false;
            }

            Debug.Log($"[HopperCraneLiftingPoint] Attached hopper assembly '{hopperRoot.name}' to crane hook '{hookMount.name}'.");
            return true;
        }

        /// <summary>
        /// Detaches the hopper assembly from the crane hook.
        /// </summary>
        public void DetachFromHook()
        {
            if (!isAttached) return;

            isAttached = false;
            currentHookMount = null;
            currentHookRb = null;

            if (hopperRoot != null)
            {
                if (hopperRoot.position.y > minGroundY + 0.05f)
                {
                    // Fall gently to the floor
                    isFallingToGround = true;
                }
                else
                {
                    // Already at floor level: freeze stably
                    Vector3 pos = hopperRoot.position;
                    pos.y = minGroundY;
                    hopperRoot.position = pos;
                    if (hopperRb != null)
                    {
                        hopperRb.position = pos;
                        hopperRb.isKinematic = true;
                        hopperRb.useGravity = false;
                        hopperRb.linearVelocity = Vector3.zero;
                        hopperRb.angularVelocity = Vector3.zero;
                    }
                }
            }

            Debug.Log($"[HopperCraneLiftingPoint] Detached hopper assembly at {hopperRoot.position:F3}.");
        }

        public void ToggleAttachment(Transform hookMount, Rigidbody hookRb)
        {
            if (isAttached)
            {
                DetachFromHook();
            }
            else
            {
                AttachToHook(hookMount, hookRb);
            }
        }

        private void FixedUpdate()
        {
            if (isAttached && currentHookMount != null && hopperRoot != null)
            {
                if (localEyeOffset == Vector3.zero)
                {
                    localEyeOffset = hopperRoot.InverseTransformPoint(transform.position);
                }

                // Hook saddle point where the lifting eye sits
                Vector3 hookSaddleWorld = currentHookMount.position - currentHookMount.up * hookSaddleOffset;

                // Position the hopper root so that transform.position (the lifting eye) coincides with hook saddle
                Vector3 targetRootPos = hookSaddleWorld - (hopperRoot.rotation * localEyeOffset);

                // Enforce warehouse physical boundaries
                targetRootPos.y = Mathf.Max(minGroundY, targetRootPos.y);
                targetRootPos.x = Mathf.Clamp(targetRootPos.x, xBounds.x, xBounds.y);
                targetRootPos.z = Mathf.Clamp(targetRootPos.z, zBounds.x, zBounds.y);

                hopperRoot.position = targetRootPos;
                if (hopperRb != null)
                {
                    hopperRb.position = targetRootPos;
                }
            }
            else if (isFallingToGround && hopperRoot != null)
            {
                // Smoothly lower to the warehouse floor
                Vector3 pos = hopperRoot.position;
                pos.y -= 1.8f * Time.fixedDeltaTime;
                if (pos.y <= minGroundY)
                {
                    pos.y = minGroundY;
                    isFallingToGround = false;
                    if (hopperRb != null)
                    {
                        hopperRb.isKinematic = true;
                        hopperRb.useGravity = false;
                        hopperRb.linearVelocity = Vector3.zero;
                        hopperRb.angularVelocity = Vector3.zero;
                    }
                    Debug.Log($"[HopperCraneLiftingPoint] Hopper safely grounded at {pos:F3}.");
                }
                hopperRoot.position = pos;
            }
        }
    }
}
