using UnityEngine;

namespace Sefpro.RatchetBelt
{
    /// <summary>
    /// Represents a designated attachment side hook on the Vibrating Platform.
    /// Provides spatial anchoring, target detection, and visual feedback for ratchet belt J-hooks.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class PlatformSideHook : MonoBehaviour
    {
        public enum HookSide
        {
            Left,
            Right
        }

        [Header("Hook Identification")]
        [Tooltip("Identifies whether this is the platform's Left or Right side hook.")]
        public HookSide side = HookSide.Left;

        [Tooltip("Specific transform where the J-hook end fitting seats and aligns.")]
        public Transform attachTransform;

        [Header("State (Read Only)")]
        [SerializeField] private bool isOccupied = false;
        [SerializeField] private Transform attachedHookFitting;

        public bool IsOccupied => isOccupied;
        public Transform AttachedFitting => attachedHookFitting;

        private Renderer hookRenderer;
        private MaterialPropertyBlock propBlock;
        private bool isHovered = false;

        private void Awake()
        {
            hookRenderer = GetComponent<Renderer>();
            propBlock = new MaterialPropertyBlock();

            if (attachTransform == null)
            {
                attachTransform = transform;
            }
        }

        public void SetOccupied(Transform hookFitting)
        {
            attachedHookFitting = hookFitting;
            isOccupied = (hookFitting != null);
        }

        public void SetHovered(bool hovered)
        {
            if (isHovered == hovered) return;
            isHovered = hovered;

            if (hookRenderer != null)
            {
                if (isHovered)
                {
                    propBlock.SetColor("_EmissionColor", new Color(0.1f, 0.4f, 0.2f)); // Soft emerald target feedback
                    hookRenderer.SetPropertyBlock(propBlock);
                }
                else
                {
                    hookRenderer.SetPropertyBlock(null);
                }
            }
        }
    }
}
