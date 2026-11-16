using System;
using UnityEngine;
using LiquidSolidPrototype.Input;
using LiquidSolidPrototype.Visuals;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace LiquidSolidPrototype.Core
{
    /// <summary>
    /// Coordinates the SEFPRO Vibrocasting Hopper discharge into the active MoldCast cavity on the Vibrating Platform.
    /// - Activates granular refractory pouring whenever the operator turns the handwheel (and stops immediately
    ///   when the handwheel is released, stopped, or closed, or when the mould reaches 100% capacity).
    /// - Streams granular refractory material from Mixture_Outlet into MoldPourReceiver and progressively fills
    ///   the mould without wall bleed or floor overflow.
    /// </summary>
    public class HopperPourManager : MonoBehaviour
    {
        [Header("Linked Hopper Components")]
        [SerializeField] private HopperHandwheel handwheel;
        [SerializeField] private HopperMaterialVolume hopperVolume;
        [SerializeField] private HopperStreamVisual streamVisual;
        [SerializeField] private Transform vibratorHousing;
        [SerializeField] private Transform mixtureOutletTransform;

        [Header("Target Mold Cavity Receiver")]
        public MoldPourReceiver targetMoldReceiver;

        [Tooltip("When true, validates that the target mould is assembled and not yet full before discharging.")]
        public bool requireMoldAlignmentToDischarge = true;

        [Header("Material Capacity & Rates")]
        [Tooltip("Total volume capacity of granular refractory castable in the hopper.")]
        [SerializeField] private float hopperCapacity = 100f;
        [Tooltip("Current volume of material remaining in the hopper.")]
        [SerializeField] private float currentHopperMaterial = 100f;
        [Tooltip("Max discharge rate in volume units/second at full gate opening.")]
        [SerializeField] private float maxDischargeRate = 22f;

        [Header("Vibrocasting Simulation")]
        [SerializeField] private bool enableVibrationShake = true;
        [SerializeField] private float vibrationFrequency = 60f;
        [SerializeField] private float vibrationAmplitude = 0.0015f;

        [Header("Runtime State (Read Only)")]
        [SerializeField] private bool isCurrentlyDischarging = false;
        [SerializeField] private bool isHopperEmpty = false;
        [SerializeField] private bool isBlockedByMoldAlignment = false;
        [SerializeField] private string alignmentBlockReason = "";

        private Vector3 vibratorOriginalPos;

        public float HopperCapacity => hopperCapacity;
        public float RemainingMaterial => currentHopperMaterial;
        public float HopperFillFraction => hopperCapacity > 0.001f ? Mathf.Clamp01(currentHopperMaterial / hopperCapacity) : 0f;
        public bool IsDischarging => isCurrentlyDischarging;
        public bool IsPouring => isCurrentlyDischarging;
        public bool IsHopperEmpty => isHopperEmpty;
        public bool IsBlockedByMoldAlignment => isBlockedByMoldAlignment;
        public string AlignmentBlockReason => alignmentBlockReason;
        public float OpenFraction => handwheel != null ? handwheel.OpenFraction : 0f;
        public HopperHandwheel Handwheel => handwheel;
        public HopperStreamVisual StreamVisual => streamVisual;
        public Transform MixtureOutletTransform => mixtureOutletTransform;

        public event Action<bool> OnDischargeStateChanged;
        public event Action<float> OnRemainingMaterialChanged;
        public event Action OnHopperEmptied;

        private void Awake()
        {
            AutoWireComponents();

            if (maxDischargeRate < 32f)
            {
                maxDischargeRate = 32f;
            }

            if (vibratorHousing != null)
            {
                vibratorOriginalPos = vibratorHousing.localPosition;
            }

            currentHopperMaterial = hopperCapacity;
        }

        private void Start()
        {
            AutoWireComponents();

            if (hopperVolume != null)
            {
                hopperVolume.SetFillFraction(HopperFillFraction);
            }

            if (streamVisual != null)
            {
                streamVisual.SetStreamActive(false, 0f);
            }
        }

        private void Update()
        {
            try
            {
                HandleKeyboardReset();
                UpdateDischargeProcess();
            }
            catch (Exception ex)
            {
                Debug.LogError("[HopperPourManager Exception in Update] " + ex);
            }
        }

        private void HandleKeyboardReset()
        {
            bool resetPressed = false;
            try
            {
#if ENABLE_INPUT_SYSTEM
                if (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
                {
                    resetPressed = true;
                }
#endif
            }
            catch { }

            if (resetPressed)
            {
                Camera cam = Camera.main;
                if (cam != null && Vector3.Distance(cam.transform.position, transform.position) <= 4.5f)
                {
                    ResetProcess();
                }
            }
        }

        private void UpdateDischargeProcess()
        {
            if (targetMoldReceiver == null)
            {
                targetMoldReceiver = FindAnyObjectByType<MoldPourReceiver>();
            }

            float openFraction = handwheel != null ? handwheel.OpenFraction : 0f;
            bool gateIsOpen = openFraction > 0.015f;
            isHopperEmpty = currentHopperMaterial <= 0.001f;

            Vector3 outletPos = mixtureOutletTransform != null
                ? mixtureOutletTransform.position
                : (streamVisual != null ? streamVisual.transform.position : transform.position);

            Vector3 cavityTargetXZ = outletPos;
            float granularSurfaceY = outletPos.y - 0.36f;
            bool isHopperDirectlyOverMold = false;

            isBlockedByMoldAlignment = false;
            alignmentBlockReason = string.Empty;

            if (gateIsOpen && !isHopperEmpty && targetMoldReceiver != null)
            {
                if (targetMoldReceiver.IsFull || targetMoldReceiver.IsSolidified)
                {
                    isBlockedByMoldAlignment = true;
                    alignmentBlockReason = "Mold cavity is 100% FULL — close discharge gate to prevent overflow!";
                }
                else if (requireMoldAlignmentToDischarge && !targetMoldReceiver.IsMoldAssembledForPouring(out string wallReason))
                {
                    isBlockedByMoldAlignment = true;
                    alignmentBlockReason = wallReason;
                }
                else
                {
                    isHopperDirectlyOverMold = targetMoldReceiver.CanReceiveFromOutlet(
                        outletPos,
                        out cavityTargetXZ,
                        out granularSurfaceY,
                        out _);
                }
            }

            bool canDischarge = gateIsOpen && !isHopperEmpty && !isBlockedByMoldAlignment;

            if (canDischarge)
            {
                if (!isCurrentlyDischarging)
                {
                    isCurrentlyDischarging = true;
                    OnDischargeStateChanged?.Invoke(true);
                }

                float requestedDelta = maxDischargeRate * openFraction * Time.deltaTime;
                if (currentHopperMaterial - requestedDelta < 0f)
                {
                    requestedDelta = currentHopperMaterial;
                }

                float actualDelta = requestedDelta;
                if (targetMoldReceiver != null)
                {
                    actualDelta = targetMoldReceiver.AddPouredMaterial(requestedDelta, hopperCapacity);
                    granularSurfaceY = targetMoldReceiver.CurrentSurfaceWorldY;
                }

                if (actualDelta <= 0.00001f)
                {
                    // Mold cavity just reached 100% full — cut off flow cleanly with zero overflow!
                    isBlockedByMoldAlignment = true;
                    alignmentBlockReason = "Mold cavity is 100% FULL — automatic overflow cutoff engaged.";
                    StopDischarge();
                    return;
                }

                currentHopperMaterial = Mathf.Max(0f, currentHopperMaterial - actualDelta);

                if (hopperVolume != null)
                {
                    hopperVolume.SetFillFraction(HopperFillFraction);
                }

                if (streamVisual != null)
                {
                    if (isHopperDirectlyOverMold && targetMoldReceiver != null)
                    {
                        streamVisual.SetTargetImpactSurface(granularSurfaceY, cavityTargetXZ);
                        streamVisual.SetStreamActive(true, openFraction);
                        targetMoldReceiver.SetCavityPourStreamActive(false, 0f);
                    }
                    else
                    {
                        // Hopper is being operated away from the mold: show granular outlet discharge at the chute
                        // and simultaneously run the strictly-contained cavity stream inside MoldCast.
                        streamVisual.ClearImpactTarget();
                        streamVisual.SetStreamActive(true, openFraction);
                        if (targetMoldReceiver != null)
                        {
                            targetMoldReceiver.SetCavityPourStreamActive(true, openFraction);
                        }
                    }
                }
                else if (targetMoldReceiver != null)
                {
                    targetMoldReceiver.SetCavityPourStreamActive(true, openFraction);
                }

                if (enableVibrationShake && vibratorHousing != null)
                {
                    float shake = Mathf.Sin(Time.time * vibrationFrequency) * vibrationAmplitude;
                    vibratorHousing.localPosition = vibratorOriginalPos + new Vector3(shake, 0f, shake * 0.5f);
                }

                OnRemainingMaterialChanged?.Invoke(currentHopperMaterial);

                if (currentHopperMaterial <= 0.001f)
                {
                    isHopperEmpty = true;
                    StopDischarge();
                    OnHopperEmptied?.Invoke();
                    Debug.Log("[HopperPourManager] Hopper is now completely EMPTY. Discharge stopped.");
                }
            }
            else
            {
                if (isCurrentlyDischarging)
                {
                    StopDischarge();
                }
                else if (targetMoldReceiver != null)
                {
                    targetMoldReceiver.SetCavityPourStreamActive(false, 0f);
                }
            }
        }

        private void StopDischarge()
        {
            isCurrentlyDischarging = false;

            if (streamVisual != null)
            {
                streamVisual.SetStreamActive(false, 0f);
            }

            if (targetMoldReceiver != null)
            {
                targetMoldReceiver.SetCavityPourStreamActive(false, 0f);
            }

            if (vibratorHousing != null)
            {
                vibratorHousing.localPosition = vibratorOriginalPos;
            }

            OnDischargeStateChanged?.Invoke(false);
        }

        /// <summary>
        /// Resets the hopper material volume, handwheel, gate, and target mold receiver back to initial state
        /// for a fresh casting cycle.
        /// </summary>
        public void ResetProcess()
        {
            StopDischarge();

            currentHopperMaterial = hopperCapacity;
            isHopperEmpty = false;
            isBlockedByMoldAlignment = false;
            alignmentBlockReason = string.Empty;

            if (handwheel != null)
            {
                handwheel.ResetWheel();
            }

            if (hopperVolume != null)
            {
                hopperVolume.ResetFill();
            }

            if (streamVisual != null)
            {
                streamVisual.SetStreamActive(false, 0f);
            }

            if (targetMoldReceiver != null)
            {
                targetMoldReceiver.ResetMoldVolume();
            }

            OnRemainingMaterialChanged?.Invoke(currentHopperMaterial);
            Debug.Log("[HopperPourManager] Hopper reset to 100% full capacity and mold cavity cleared for new cycle.");
        }

        public void AutoWireComponents()
        {
            if (handwheel == null) handwheel = GetComponentInChildren<HopperHandwheel>(true);
            if (hopperVolume == null) hopperVolume = GetComponentInChildren<HopperMaterialVolume>(true);
            if (streamVisual == null) streamVisual = GetComponentInChildren<HopperStreamVisual>(true);

            if (mixtureOutletTransform == null)
            {
                Transform[] all = GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].name == "Mixture_Outlet")
                    {
                        mixtureOutletTransform = all[i];
                        break;
                    }
                }
                if (mixtureOutletTransform == null && streamVisual != null)
                    mixtureOutletTransform = streamVisual.transform;
            }

            if (vibratorHousing == null)
            {
                var v = transform.Find("SEFPRO_Vibrocasting_Hopper/Vibrator_Housing");
                if (v == null) v = transform.Find("Vibrator_Housing");
                if (v != null) vibratorHousing = v;
            }

            if (targetMoldReceiver == null)
            {
                targetMoldReceiver = FindAnyObjectByType<MoldPourReceiver>();
            }
        }

        private void OnValidate()
        {
            AutoWireComponents();
        }

        private void OnGUI()
        {
            if (!isBlockedByMoldAlignment || string.IsNullOrEmpty(alignmentBlockReason))
                return;

            float w = 580f;
            float h = 38f;
            float x = (Screen.width - w) * 0.5f;
            float y = 112f;

            GUIStyle style = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 12,
                richText = true
            };

            GUI.Box(new Rect(x, y, w, h), $"<color=#FFCC00><b>[HOPPER INTERLOCK] {alignmentBlockReason}</b></color>", style);
        }
    }
}
