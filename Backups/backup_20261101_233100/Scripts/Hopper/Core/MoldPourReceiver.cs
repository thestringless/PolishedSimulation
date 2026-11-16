using UnityEngine;

namespace LiquidSolidPrototype.Core
{
    /// <summary>
    /// Synchronizes the refractory castable mixture poured from the SEFPRO Hopper
    /// into the hollow CastingMold cavity on the Vibrocasting Platform.
    /// Dynamically scales and positions the mold's internal material volume as discharge occurs.
    /// </summary>
    public class MoldPourReceiver : MonoBehaviour
    {
        [Header("References")]
        public HopperPourManager hopperPourManager;
        public Transform moldMaterialVolume;

        [Header("Mold Cavity Physical Bounds (Local to CastingMold)")]
        public float bottomY = 0.04f;
        public float maxFilledHeight = 0.48f;
        public float lengthX = 1.68f;
        public float widthZ = 1.08f;

        [Header("Runtime State")]
        [SerializeField] private float currentFillFraction = 0f;

        public float CurrentFillFraction => currentFillFraction;

        private void Start()
        {
            if (hopperPourManager == null)
            {
                hopperPourManager = FindAnyObjectByType<HopperPourManager>();
            }

            if (moldMaterialVolume == null)
            {
                var t = transform.Find("Mold_Material_Volume");
                if (t != null) moldMaterialVolume = t;
            }

            if (moldMaterialVolume != null)
            {
                moldMaterialVolume.gameObject.SetActive(false);
            }

            if (hopperPourManager != null)
            {
                hopperPourManager.OnRemainingMaterialChanged -= HandleRemainingMaterialChanged;
                hopperPourManager.OnRemainingMaterialChanged += HandleRemainingMaterialChanged;
            }
        }

        private void OnDestroy()
        {
            if (hopperPourManager != null)
            {
                hopperPourManager.OnRemainingMaterialChanged -= HandleRemainingMaterialChanged;
            }
        }

        private void Update()
        {
            if (hopperPourManager != null)
            {
                // Synchronize fill fraction from hopper discharge
                float poured = Mathf.Max(0f, hopperPourManager.HopperCapacity - hopperPourManager.RemainingMaterial);
                float fraction = hopperPourManager.HopperCapacity > 0.001f ? Mathf.Clamp01(poured / hopperPourManager.HopperCapacity) : 0f;
                SetMoldFillFraction(fraction);
            }
        }

        private void HandleRemainingMaterialChanged(float remaining)
        {
            if (hopperPourManager == null) return;
            float poured = Mathf.Max(0f, hopperPourManager.HopperCapacity - remaining);
            float fraction = hopperPourManager.HopperCapacity > 0.001f ? Mathf.Clamp01(poured / hopperPourManager.HopperCapacity) : 0f;
            SetMoldFillFraction(fraction);
        }

        public void SetMoldFillFraction(float fraction)
        {
            currentFillFraction = Mathf.Clamp01(fraction);

            if (moldMaterialVolume == null)
            {
                var t = transform.Find("Mold_Material_Volume");
                if (t != null) moldMaterialVolume = t;
                if (moldMaterialVolume == null) return;
            }

            if (currentFillFraction <= 0.005f)
            {
                moldMaterialVolume.gameObject.SetActive(false);
            }
            else
            {
                if (!moldMaterialVolume.gameObject.activeSelf)
                {
                    moldMaterialVolume.gameObject.SetActive(true);
                }

                float currentHeight = Mathf.Max(0.015f, currentFillFraction * maxFilledHeight);
                float centerY = bottomY + (currentHeight * 0.5f);

                moldMaterialVolume.localPosition = new Vector3(0f, centerY, 0f);
                moldMaterialVolume.localScale = new Vector3(lengthX, currentHeight, widthZ);
            }
        }

        public void ResetMoldVolume()
        {
            SetMoldFillFraction(0f);
        }
    }
}
