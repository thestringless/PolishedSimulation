namespace NOT_Lonely
{
    using UnityEngine;
    using System.Collections;

#if UNITY_EDITOR
    [ExecuteInEditMode]
#endif
    public class NL_Hook : MonoBehaviour
    {
        [SerializeField] public Transform wireDrumMount;
        [SerializeField] public Transform wireHookMount;
        [SerializeField] public SkinnedMeshRenderer wires_mesh;
        [SerializeField] public MaterialPropertyBlock mtlPropBlock;
        [SerializeField] public ConfigurableJoint hookJoint;
        [SerializeField] public Rigidbody rb;
        [HideInInspector] public Rigidbody inertiaBody;

        public LayerMask layerMask = -1;
        public float hookOffset = 0.3f;
        private NL_OverheadCrane crane;
        public NL_RemoteButton buttonDown;
        public NL_AttachCargo attachCargo;

        public NL_OverheadCrane Crane => crane;

        public void Init()
        {
            wireDrumMount = GetChildByName(transform, "WireDrumMount");
            wireHookMount = GetChildByName(transform, "WireHookMount");
            wires_mesh = GetChildByName(transform, "Wires_mesh").GetComponent<SkinnedMeshRenderer>();

            hookJoint = wireHookMount.GetComponent<ConfigurableJoint>();
            rb = hookJoint.GetComponent<Rigidbody>();

            mtlPropBlock = new MaterialPropertyBlock();
        }

        private void OnEnable()
        {
            crane = transform.parent.parent.parent.parent.parent.parent.parent.GetComponent<NL_OverheadCrane>();
            crane.OnHookHeightChanged += UpdateHookHeight;
        }

        private void OnDisable()
        {
            if (crane != null)
                crane.OnHookHeightChanged -= UpdateHookHeight;
        }

        private void Start()
        {
            if (Application.isPlaying)
            {
                var inertiaGo = new GameObject("Hook Inertial Body");
                if (wireHookMount != null)
                {
                    inertiaGo.transform.SetPositionAndRotation(wireHookMount.position, wireHookMount.rotation);
                }
                inertiaBody = inertiaGo.AddComponent<Rigidbody>();
                FixedJoint inertiaBodyJoint = inertiaGo.AddComponent<FixedJoint>();
                inertiaBodyJoint.connectedBody = rb;
            }
        }

        /// <summary>
        /// Synchronizes WireHookMount, its Rigidbody, and Hook Inertial Body to an exact world pose
        /// with zero linear/angular velocity and updated cable bounds, preventing joint recoil or oscillation.
        /// </summary>
        public void SetHookWorldPoseAndZeroVelocity(Vector3 worldPos, Quaternion worldRot)
        {
            if (wireHookMount != null)
            {
                wireHookMount.SetPositionAndRotation(worldPos, worldRot);
            }
            if (rb != null)
            {
                if (Application.isPlaying)
                {
                    rb.position = worldPos;
                    rb.rotation = worldRot;
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
            }
            if (inertiaBody != null)
            {
                inertiaBody.transform.SetPositionAndRotation(worldPos, worldRot);
                if (Application.isPlaying)
                {
                    inertiaBody.position = worldPos;
                    inertiaBody.rotation = worldRot;
                    inertiaBody.linearVelocity = Vector3.zero;
                    inertiaBody.angularVelocity = Vector3.zero;
                }
            }
            if (wires_mesh != null && wireHookMount != null)
            {
                wires_mesh.localBounds = new Bounds(
                    new Vector3(wireHookMount.localPosition.x / 2, wireHookMount.localPosition.y / 2, wireHookMount.localPosition.z / 2 + 0.488f),
                    new Vector3(-(Mathf.Abs(wireHookMount.localPosition.x) + 0.4f), -wireHookMount.localPosition.y, -(Mathf.Abs(wireHookMount.localPosition.z) + 0.5f)));
            }
        }

        private Transform GetChildByName(Transform parent, string childName)
        {
            Transform[] allChildren = parent.GetComponentsInChildren<Transform>(true);
            Transform child = null;

            for (int i = 0; i < allChildren.Length; i++)
            {
                if (allChildren[i].name == childName)
                {
                    child = allChildren[i];
                }
            }

            if (child != null)
            {
                return child;
            }
            else
            {
                Debug.Log("Object with name " + childName + " NOT found");
                return null;
            }
        }

        private void Update()
        {
            if (rb != null && !rb.IsSleeping() && wires_mesh != null && wireHookMount != null)
            {
                wires_mesh.localBounds = new Bounds(new Vector3(wireHookMount.localPosition.x / 2, wireHookMount.localPosition.y / 2, wireHookMount.localPosition.z / 2 + 0.488f), new Vector3(-(Mathf.Abs(wireHookMount.localPosition.x) + 0.4f), -wireHookMount.localPosition.y, -(Mathf.Abs(wireHookMount.localPosition.z) + 0.5f)));
            }
        }

        public void UpdateHookHeight(float speed, float t, float craneHeight)
        {
            bool blockedDown = false;
            RaycastHit[] hits = Physics.RaycastAll(hookJoint.transform.position, Vector3.down, 0.35f, layerMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider c = hits[i].collider;
                if (c == null) continue;
                if (c.transform.IsChildOf(transform)) continue;
                if (c.GetComponentInParent<LiquidSolidPrototype.Core.HopperCraneLiftingPoint>() != null) continue;
                if (c.GetComponentInParent<Sefpro.JawTongs.JawTongsCraneLiftingPoint>() != null) continue;
                if (c.GetComponentInParent<Sefpro.Assembly.MoldCraneLiftingPoint>() != null) continue;
                blockedDown = true;
                break;
            }

            if (blockedDown && (attachCargo == null || !attachCargo.isAttached))
            {
                if (buttonDown != null) buttonDown.isInteractable = false;
            }
            else
            {
                if (buttonDown != null) buttonDown.isInteractable = true;
            }
            if (attachCargo != null && attachCargo.isAttached && buttonDown != null)
            {
                buttonDown.isInteractable = true;
            }
            wireDrumMount.localPosition = new Vector3(0.024f, 0, Mathf.Lerp(-0.798f, -0.488f, t));

            hookJoint.anchor = new Vector3(0, Mathf.Lerp(craneHeight + hookOffset, 0.5f, t), 0);
            hookJoint.connectedAnchor = new Vector3(hookJoint.connectedAnchor.x, 0, Mathf.Lerp(0.64f, 0.488f, t));
            
            // Maintain consistent hook yaw orientation so the C-hook throat stays aligned with the hopper lifting bail
            hookJoint.targetRotation = Quaternion.identity;

            mtlPropBlock = new MaterialPropertyBlock();

            mtlPropBlock.SetVector("_TilingXY", new Vector2(Mathf.Lerp(craneHeight + 0.3f, 0.5f, t) * 2, 1));
            wires_mesh.SetPropertyBlock(mtlPropBlock);
        }
    }
}
