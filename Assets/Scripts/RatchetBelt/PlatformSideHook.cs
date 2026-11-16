using UnityEngine;

namespace Sefpro.RatchetBelt
{
    /// <summary>
    /// Represents a designated attachment side hook on the Vibrating Platform.
    /// Provides per-belt hook ownership, spatial anchoring, target detection,
    /// and 3D VR + shader visual alignment feedback for ratchet belt J-hooks.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class PlatformSideHook : MonoBehaviour
    {
        public enum HookSide
        {
            Left,
            Right,
            Far,
            Near
        }

        public enum AlignmentFeedback
        {
            None,
            Hovered,
            ValidAligned,
            WrongHookRejected
        }

        [Header("Hook Identification")]
        [Tooltip("Identifies whether this is the platform's Far (Fixed buckle side) or Near (Adjustable free-end side) anchor hook.")]
        public HookSide side = HookSide.Far;

        [Tooltip("Exclusive belt index (1 for RatchetBelt, 2 for RatchetBelt_2) permitted to attach to this hook.")]
        public int assignedBeltIndex = 1;

        public bool IsFarSide => side == HookSide.Far || side == HookSide.Left;
        public bool IsNearSide => side == HookSide.Near || side == HookSide.Right;

        [Tooltip("Specific transform where the J-hook end fitting seats and aligns.")]
        public Transform attachTransform;

        [Header("State (Read Only)")]
        [SerializeField] private bool isOccupied = false;
        [SerializeField] private Transform attachedHookFitting;
        [SerializeField] private AlignmentFeedback currentFeedback = AlignmentFeedback.None;

        public bool IsOccupied => isOccupied;
        public Transform AttachedFitting => attachedHookFitting;
        public AlignmentFeedback CurrentFeedback => currentFeedback;
        public Vector3 AnchorWorldPosition => attachTransform != null ? attachTransform.position : transform.position;
        public Quaternion AnchorWorldRotation => attachTransform != null ? attachTransform.rotation : transform.rotation;

        private Renderer hookRenderer;
        private MaterialPropertyBlock propBlock;
        private bool isHovered = false;
        private GameObject indicatorRingObj;
        private MeshRenderer indicatorRingRenderer;
        private Material indicatorMatInstance;

        private void Awake()
        {
            AutoInferBeltIndex();

            hookRenderer = GetComponent<Renderer>();
            if (hookRenderer == null && attachTransform != null)
            {
                hookRenderer = attachTransform.GetComponent<Renderer>();
            }
            propBlock = new MaterialPropertyBlock();

            if (attachTransform == null)
            {
                Transform childAttach = transform.Find("Attach_Point");
                attachTransform = childAttach != null ? childAttach : transform;
            }

            EnsureAlignmentIndicator();
            ApplyFeedbackVisuals();
        }

        public void AutoInferBeltIndex()
        {
            if (name.EndsWith("_2") || name.Contains("Hook_2") || name.Contains("Near_2") || name.Contains("Far_2"))
            {
                assignedBeltIndex = 2;
            }
            else if (name.EndsWith("_1") || name.Contains("Hook_1") || name.Contains("Near_1") || name.Contains("Far_1"))
            {
                assignedBeltIndex = 1;
            }
        }

        public void SetOccupied(Transform hookFitting)
        {
            attachedHookFitting = hookFitting;
            isOccupied = (hookFitting != null);
            if (isOccupied && currentFeedback != AlignmentFeedback.None)
            {
                SetAlignmentFeedback(AlignmentFeedback.None);
            }
        }

        public void SetHovered(bool hovered)
        {
            if (isHovered == hovered) return;
            isHovered = hovered;

            if (currentFeedback == AlignmentFeedback.None || currentFeedback == AlignmentFeedback.Hovered)
            {
                currentFeedback = hovered ? AlignmentFeedback.Hovered : AlignmentFeedback.None;
                ApplyFeedbackVisuals();
            }
        }

        public void SetAlignmentFeedback(AlignmentFeedback feedback)
        {
            AlignmentFeedback resolved = feedback;
            if (resolved == AlignmentFeedback.None && isHovered)
                resolved = AlignmentFeedback.Hovered;

            if (currentFeedback == resolved)
                return;

            currentFeedback = resolved;
            ApplyFeedbackVisuals();
        }

        private void EnsureAlignmentIndicator()
        {
            if (!Application.isPlaying || indicatorRingObj != null)
                return;

            Transform existing = transform.Find("VR_Alignment_Beacon");
            if (existing != null)
            {
                indicatorRingObj = existing.gameObject;
                indicatorRingRenderer = indicatorRingObj.GetComponent<MeshRenderer>();
                return;
            }

            indicatorRingObj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            indicatorRingObj.name = "VR_Alignment_Beacon";
            indicatorRingObj.hideFlags = HideFlags.DontSave;
            Collider c = indicatorRingObj.GetComponent<Collider>();
            if (c != null) Destroy(c);

            Transform anchor = attachTransform != null ? attachTransform : transform;
            indicatorRingObj.transform.SetParent(anchor, worldPositionStays: false);
            indicatorRingObj.transform.localPosition = Vector3.zero;
            indicatorRingObj.transform.localRotation = Quaternion.identity;

            // Compensate parent lossyScale so indicator sphere is ~65 mm diameter
            Vector3 ls = anchor.lossyScale;
            float sx = Mathf.Abs(ls.x) > 0.0001f ? 0.065f / Mathf.Abs(ls.x) : 0.065f;
            float sy = Mathf.Abs(ls.y) > 0.0001f ? 0.065f / Mathf.Abs(ls.y) : 0.065f;
            float sz = Mathf.Abs(ls.z) > 0.0001f ? 0.065f / Mathf.Abs(ls.z) : 0.065f;
            indicatorRingObj.transform.localScale = new Vector3(sx, sy, sz);

            indicatorRingRenderer = indicatorRingObj.GetComponent<MeshRenderer>();
            Shader sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            if (sh != null)
            {
                indicatorMatInstance = new Material(sh) { hideFlags = HideFlags.DontSave };
                indicatorRingRenderer.sharedMaterial = indicatorMatInstance;
                indicatorRingRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                indicatorRingRenderer.receiveShadows = false;
            }
            indicatorRingObj.SetActive(false);
        }

        private void ApplyFeedbackVisuals()
        {
            if (propBlock == null) propBlock = new MaterialPropertyBlock();

            if (hookRenderer != null)
            {
                switch (currentFeedback)
                {
                    case AlignmentFeedback.ValidAligned:
                        hookRenderer.GetPropertyBlock(propBlock);
                        propBlock.SetColor("_EmissionColor", new Color(0.05f, 0.85f, 0.35f));
                        propBlock.SetColor("_BaseColor", new Color(0.30f, 1.00f, 0.55f));
                        propBlock.SetColor("_Color", new Color(0.30f, 1.00f, 0.55f));
                        hookRenderer.SetPropertyBlock(propBlock);
                        break;

                    case AlignmentFeedback.WrongHookRejected:
                        hookRenderer.GetPropertyBlock(propBlock);
                        propBlock.SetColor("_EmissionColor", new Color(0.95f, 0.15f, 0.08f));
                        propBlock.SetColor("_BaseColor", new Color(1.00f, 0.30f, 0.22f));
                        propBlock.SetColor("_Color", new Color(1.00f, 0.30f, 0.22f));
                        hookRenderer.SetPropertyBlock(propBlock);
                        break;

                    case AlignmentFeedback.Hovered:
                        hookRenderer.GetPropertyBlock(propBlock);
                        propBlock.SetColor("_EmissionColor", new Color(0.10f, 0.45f, 0.22f));
                        hookRenderer.SetPropertyBlock(propBlock);
                        break;

                    default:
                        hookRenderer.SetPropertyBlock(null);
                        break;
                }
            }

            EnsureAlignmentIndicator();
            if (indicatorRingObj != null)
            {
                bool showBeacon = currentFeedback == AlignmentFeedback.ValidAligned ||
                                  currentFeedback == AlignmentFeedback.WrongHookRejected;
                indicatorRingObj.SetActive(showBeacon);
                if (showBeacon && indicatorMatInstance != null)
                {
                    Color col = currentFeedback == AlignmentFeedback.ValidAligned
                        ? new Color(0.10f, 0.95f, 0.45f, 1f)
                        : new Color(1.00f, 0.22f, 0.15f, 1f);
                    indicatorMatInstance.SetColor("_BaseColor", col);
                    indicatorMatInstance.SetColor("_Color", col);
                }
            }
        }

        private void OnDestroy()
        {
            if (indicatorMatInstance != null)
                Destroy(indicatorMatInstance);
        }
    }
}
