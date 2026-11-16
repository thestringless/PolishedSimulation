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
    /// Standalone coordinator for the SEFPRO Vibrocasting Hopper discharge operation.
    /// Completely self-contained: manages handwheel drive input, discharge gate state,
    /// dynamic internal refractory volume lowering, cohesive extruded stream visual,
    /// chassis vibrocasting micro-vibration, and automatic empty cutoff.
    /// Free of any external scene or mold dependencies for direct portability into VR projects.
    /// </summary>
    public class HopperPourManager : MonoBehaviour
    {
        [Header("Linked Hopper Components")]
        [SerializeField] private HopperHandwheel handwheel;
        [SerializeField] private HopperMaterialVolume hopperVolume;
        [SerializeField] private HopperStreamVisual streamVisual;
        [SerializeField] private Transform vibratorHousing;

        [Header("Material Capacity & Rates")]
        [Tooltip("Total volume capacity of semi-solid refractory castable in the hopper.")]
        [SerializeField] private float hopperCapacity = 100f;
        [Tooltip("Current volume of material remaining in the hopper.")]
        [SerializeField] private float currentHopperMaterial = 100f;
        [Tooltip("Max discharge rate in volume units/second at full gate opening.")]
        [SerializeField] private float maxDischargeRate = 15f;

        [Header("Vibrocasting Simulation")]
        [SerializeField] private bool enableVibrationShake = true;
        [SerializeField] private float vibrationFrequency = 60f;
        [SerializeField] private float vibrationAmplitude = 0.0015f;

        [Header("Runtime State (Read Only)")]
        [SerializeField] private bool isCurrentlyDischarging = false;
        [SerializeField] private bool isHopperEmpty = false;

        private Vector3 vibratorOriginalPos;

        public float HopperCapacity => hopperCapacity;
        public float RemainingMaterial => currentHopperMaterial;
        public float HopperFillFraction => hopperCapacity > 0.001f ? Mathf.Clamp01(currentHopperMaterial / hopperCapacity) : 0f;
        public bool IsDischarging => isCurrentlyDischarging;
        public bool IsPouring => isCurrentlyDischarging;
        public bool IsHopperEmpty => isHopperEmpty;
        public float OpenFraction => handwheel != null ? handwheel.OpenFraction : 0f;

        public event Action<bool> OnDischargeStateChanged;
        public event Action<float> OnRemainingMaterialChanged;
        public event Action OnHopperEmptied;

        private void Awake()
        {
            AutoWireComponents();

            if (vibratorHousing != null)
            {
                vibratorOriginalPos = vibratorHousing.localPosition;
            }

            currentHopperMaterial = hopperCapacity;
        }

        private void Start()
        {
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
                ResetProcess();
            }
        }

        private void UpdateDischargeProcess()
        {
            float openFraction = handwheel != null ? handwheel.OpenFraction : 0f;
            bool gateIsOpen = openFraction > 0.015f;
            isHopperEmpty = currentHopperMaterial <= 0.001f;

            bool canDischarge = gateIsOpen && !isHopperEmpty;

            if (canDischarge)
            {
                if (!isCurrentlyDischarging)
                {
                    isCurrentlyDischarging = true;
                    OnDischargeStateChanged?.Invoke(true);
                }

                // Progressive discharge: flow amount scales directly with gate open fraction
                float delta = maxDischargeRate * openFraction * Time.deltaTime;
                if (currentHopperMaterial - delta < 0f)
                {
                    delta = currentHopperMaterial;
                }

                currentHopperMaterial = Mathf.Max(0f, currentHopperMaterial - delta);

                // Lower internal refractory surface level
                if (hopperVolume != null)
                {
                    hopperVolume.SetFillFraction(HopperFillFraction);
                }

                // Update viscous discharge stream
                if (streamVisual != null)
                {
                    streamVisual.SetStreamActive(true, openFraction);
                }

                // Industrial vibrocasting micro-shake on vibrator housing
                if (enableVibrationShake && vibratorHousing != null)
                {
                    float shake = Mathf.Sin(Time.time * vibrationFrequency) * vibrationAmplitude;
                    vibratorHousing.localPosition = vibratorOriginalPos + new Vector3(shake, 0f, shake * 0.5f);
                }

                OnRemainingMaterialChanged?.Invoke(HopperFillFraction);

                // Check for empty hopper cutoff
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
            }
        }

        private void StopDischarge()
        {
            isCurrentlyDischarging = false;

            if (streamVisual != null)
            {
                streamVisual.SetStreamActive(false, 0f);
            }

            if (vibratorHousing != null)
            {
                vibratorHousing.localPosition = vibratorOriginalPos;
            }

            OnDischargeStateChanged?.Invoke(false);
        }

        /// <summary>
        /// Resets the hopper material volume, handwheel, and gate back to initial 100% full state.
        /// </summary>
        public void ResetProcess()
        {
            StopDischarge();

            currentHopperMaterial = hopperCapacity;
            isHopperEmpty = false;

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

            OnRemainingMaterialChanged?.Invoke(1.0f);
            Debug.Log("[HopperPourManager] Hopper reset to 100% full capacity. Handwheel and gate closed.");
        }

        /// <summary>
        /// Automatically wires references to children in the hierarchy if not assigned in Inspector.
        /// Makes the prefab self-contained and robust.
        /// </summary>
        public void AutoWireComponents()
        {
            if (handwheel == null) handwheel = GetComponentInChildren<HopperHandwheel>(true);
            if (hopperVolume == null) hopperVolume = GetComponentInChildren<HopperMaterialVolume>(true);
            if (streamVisual == null) streamVisual = GetComponentInChildren<HopperStreamVisual>(true);

            if (vibratorHousing == null)
            {
                var v = transform.Find("SEFPRO_Vibrocasting_Hopper/Vibrator_Housing");
                if (v == null) v = transform.Find("Vibrator_Housing");
                if (v != null) vibratorHousing = v;
            }
        }

        private void OnValidate()
        {
            AutoWireComponents();
        }
    }
}
