using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

[Serializable]
public class XRSteppedKnobEvent : UnityEvent<int>
{
}

/// <summary>
/// VR Regulator with Single-Press (Step Up: 0 -> 1 -> 2 -> 3 -> 4)
/// and Double-Press (Step Down: 4 -> 3 -> 2 -> 1 -> 0) interaction.
/// - Requires START to be active on VibroControllerPanel before allowing level changes.
/// - Single press (no second press within doublePressWindow = 0.35s) increases level by 1 (clamped at 4, no wrap).
/// - Double press (two intentional presses within 0.35s) decreases level by 1 (clamped at 0, no wrap) without incrementing first.
/// - Ignores hover, continuous holding, and continuous collision overlap.
/// </summary>
public class XRSteppedKnob : XRBaseInteractable
{
    public const int DefaultStepCount = 5;
    public const float DefaultDoublePressWindow = 0.35f;

    [Header("Detents (Steps 0 to 4)")]
    [Tooltip("Total number of discrete steps including 0. Default is 5 (Steps 0, 1, 2, 3, 4).")]
    [SerializeField, Range(2, 10)] private int stepCount = DefaultStepCount;
    [SerializeField] private Vector3 rotationAxis = Vector3.forward;
    [SerializeField] private float minRotationAngle = -135f;
    [SerializeField] private float maxRotationAngle = 135f;
    [SerializeField, Min(0)] private int initialStep = 0;

    [Header("Double-Press Timing")]
    [Tooltip("Maximum time window (seconds) between two presses to register a double-press (step down). Default ~350 ms.")]
    [SerializeField, Range(0.15f, 0.60f)] private float doublePressWindow = DefaultDoublePressWindow;
    [Tooltip("Minimum debounce time (seconds) to deduplicate simultaneous XR select/activate/button events from a single physical press.")]
    [SerializeField, Range(0.02f, 0.15f)] private float minPressDebounce = 0.055f;
    [Tooltip("Cooldown window (seconds) after a double-press commits to absorb trailing rapid taps without unintended level changes.")]
    [SerializeField, Range(0.15f, 0.50f)] private float postDoublePressCooldown = 0.35f;

    [Header("Knob Neutral Basis")]
    [Tooltip("When true, uses neutralLocalEulerAngles as the 0-degree center reference before applying minRotationAngle..maxRotationAngle.")]
    [SerializeField] private bool useExplicitNeutralRotation = false;
    [SerializeField] private Vector3 neutralLocalEulerAngles = new Vector3(270f, 0f, 0f);

    [Header("Haptics")]
    [SerializeField, Range(0f, 1f)] private float hapticAmplitude = 0.25f;
    [SerializeField, Min(0f)] private float hapticDuration = 0.035f;

    [Header("Controller Panel Integration")]
    [Tooltip("Primary VibroControllerPanel that manages START/STOP gating and per-level audio.")]
    [SerializeField] private VibroControllerPanel controllerPanel;

    [Header("Legacy Direct References (Fallback if no ControllerPanel)")]
    [SerializeField] private VibroEffect vibrationEffect;
    [SerializeField] private AudioSource vibrationAudioSource;
    [SerializeField] private AudioClip vibrationClip;
    [SerializeField] private float[] stepVolumeMultipliers = { 0.05f, 0.25f, 0.45f, 0.7f, 1f };

    public XRSteppedKnobEvent onStepChanged = new XRSteppedKnobEvent();

    private Quaternion _baseLocalRotation;
    private Vector3 _baseLocalScale = Vector3.one;
    private bool _basisInitialized;
    private Vector3 _rotationAxis;
    private float _currentAngle;
    private int _stepLevel;
    private bool _isInitializing;

    // Press / Double-Press State Machine
    private bool _hasPendingSinglePress;
    private float _firstPressTime = -10f;
    private float _lastRawPressTime = -10f;
    private float _postActionCooldownUntil = -10f;
    private XRBaseInputInteractor _lastInputInteractor;
    private bool _wasHoveredControllerPressed;
    private static readonly List<InputDevice> s_xrDevices = new List<InputDevice>();

    public int StepLevel => _stepLevel;
    public int MaxStepIndex => Mathf.Max(1, stepCount - 1);
    public float DoublePressWindow => doublePressWindow;
    public bool HasPendingSinglePress => _hasPendingSinglePress;
    public VibroEffect VibrationEffect => controllerPanel != null && controllerPanel.TargetVibrationEffect != null
        ? controllerPanel.TargetVibrationEffect
        : vibrationEffect;
    public VibroControllerPanel ControllerPanel => controllerPanel;

    /// <summary>
    /// Returns true only when START has been pressed on the controller panel (and STOP has not been pressed).
    /// </summary>
    public bool IsRegulatorInteractionAllowed()
    {
        if (controllerPanel == null)
            controllerPanel = GetComponentInParent<VibroControllerPanel>();

        return controllerPanel == null || controllerPanel.IsMotorStarted;
    }

    /// <summary>
    /// Cancels any pending single-press timer (called when STOP is pressed).
    /// </summary>
    public void CancelPendingPress()
    {
        _hasPendingSinglePress = false;
    }

    private void EnsureUniformRotationBasis()
    {
        if (_basisInitialized)
            return;

        if (stepCount < DefaultStepCount)
            stepCount = DefaultStepCount;

        if (doublePressWindow <= 0.05f)
            doublePressWindow = DefaultDoublePressWindow;

        _rotationAxis = rotationAxis.sqrMagnitude > Mathf.Epsilon
            ? rotationAxis.normalized
            : Vector3.forward;

        _baseLocalRotation = useExplicitNeutralRotation
            ? Quaternion.Euler(neutralLocalEulerAngles)
            : transform.localRotation;
        _baseLocalScale = transform.localScale;
        _basisInitialized = true;
    }

    private void Start()
    {
        if (stepCount < DefaultStepCount)
            stepCount = DefaultStepCount;

        if (maxRotationAngle <= minRotationAngle)
            maxRotationAngle = minRotationAngle + 270f;

        if (doublePressWindow <= 0.05f)
            doublePressWindow = DefaultDoublePressWindow;

        EnsureUniformRotationBasis();

        if (controllerPanel == null)
            controllerPanel = GetComponentInParent<VibroControllerPanel>();

        selectEntered.RemoveListener(HandleSelectEntered);
        selectEntered.AddListener(HandleSelectEntered);
        activated.RemoveListener(HandleActivated);
        activated.AddListener(HandleActivated);
        onStepChanged.RemoveListener(HandleStepChanged);
        onStepChanged.AddListener(HandleStepChanged);

        _isInitializing = true;
        ApplyStepInternal(Mathf.Clamp(initialStep, 0, MaxStepIndex), invokeEvents: true);
        _isInitializing = false;
    }

    private void OnDestroy()
    {
        selectEntered.RemoveListener(HandleSelectEntered);
        activated.RemoveListener(HandleActivated);
        onStepChanged.RemoveListener(HandleStepChanged);
    }

    private void Update()
    {
        // 1. Poll rising-edge VR controller press while hovered by an XRDirectInteractor
        PollHoveredVRControllerPress();

        // 2. Resolve pending single press once the 350 ms double-press window elapses without a second press
        if (!_hasPendingSinglePress)
            return;

        if (!IsRegulatorInteractionAllowed())
        {
            _hasPendingSinglePress = false;
            return;
        }

        if (Time.unscaledTime - _firstPressTime >= doublePressWindow)
        {
            _hasPendingSinglePress = false;
            ExecuteSinglePressIncrement();
        }
    }

    private void PollHoveredVRControllerPress()
    {
        if (!isHovered || interactorsHovering.Count == 0)
        {
            _wasHoveredControllerPressed = false;
            return;
        }

        IXRInteractor activeHoverInteractor = null;
        for (int i = 0; i < interactorsHovering.Count; i++)
        {
            if (interactorsHovering[i] != null)
            {
                activeHoverInteractor = interactorsHovering[i];
                break;
            }
        }

        if (activeHoverInteractor == null)
        {
            _wasHoveredControllerPressed = false;
            return;
        }

        bool pressedNow = IsAnyXRControllerButtonPressed(activeHoverInteractor as XRBaseInputInteractor);
        if (pressedNow && !_wasHoveredControllerPressed)
        {
            _wasHoveredControllerPressed = true;
            RegisterVRPress(activeHoverInteractor);
        }
        else if (!pressedNow)
        {
            _wasHoveredControllerPressed = false;
        }
    }

    private static bool IsAnyXRControllerButtonPressed(XRBaseInputInteractor inputInteractor = null)
    {
        if (inputInteractor != null)
        {
            if (inputInteractor.activateInput != null && inputInteractor.activateInput.ReadIsPerformed())
                return true;
            if (inputInteractor.selectInput != null && inputInteractor.selectInput.ReadIsPerformed())
                return true;
        }

        s_xrDevices.Clear();
        InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.HeldInHand, s_xrDevices);
        for (int i = 0; i < s_xrDevices.Count; i++)
        {
            var dev = s_xrDevices[i];
            if (!dev.isValid) continue;
            if ((dev.TryGetFeatureValue(CommonUsages.triggerButton, out bool trig) && trig) ||
                (dev.TryGetFeatureValue(CommonUsages.gripButton, out bool grip) && grip) ||
                (dev.TryGetFeatureValue(CommonUsages.primaryButton, out bool prim) && prim))
            {
                return true;
            }
        }
        return false;
    }

    private void OnMouseDown()
    {
        RegisterVRPress(null);
    }

    private void HandleSelectEntered(SelectEnterEventArgs args)
    {
        RegisterVRPress(args.interactorObject);

        // Immediately release selection so holding the button does not latch the knob
        // and a second press within 350 ms can cleanly trigger selectEntered again.
        if (interactionManager != null && args.interactorObject != null)
        {
            interactionManager.SelectExit(args.interactorObject, this);
        }
    }

    private void HandleActivated(ActivateEventArgs args)
    {
        RegisterVRPress(args.interactorObject);
    }

    /// <summary>
    /// Unified entry point for an intentional discrete VR press on the regulator.
    /// - Single press (no 2nd press within 350 ms): increases level by 1 (0 -> 1 -> 2 -> 3 -> 4, clamped at 4).
    /// - Double press (2nd press within 350 ms): decreases level by 1 (4 -> 3 -> 2 -> 1 -> 0, clamped at 0) without incrementing first.
    /// </summary>
    public void RegisterVRPress(IXRInteractor interactor = null)
    {
        // Gate: Regulator cannot be operated before START or after STOP.
        if (!IsRegulatorInteractionAllowed())
        {
            _hasPendingSinglePress = false;
            return;
        }

        float now = Time.unscaledTime;

        // Deduplicate simultaneous events from a single physical button press (e.g., Select + Activate on the same frame).
        if (now - _lastRawPressTime < minPressDebounce)
            return;

        _lastRawPressTime = now;

        // Absorb trailing rapid presses during post-double-press cooldown so rapid mashing never causes unintended level jumps.
        if (now < _postActionCooldownUntil)
        {
            _hasPendingSinglePress = false;
            return;
        }

        if (interactor is XRBaseInputInteractor inputInteractor)
            _lastInputInteractor = inputInteractor;

        if (_hasPendingSinglePress && (now - _firstPressTime) <= doublePressWindow)
        {
            // Second intentional press within 350 ms -> Commit DOUBLE PRESS (decrease by 1) immediately!
            _hasPendingSinglePress = false;
            _postActionCooldownUntil = now + postDoublePressCooldown;
            ExecuteDoublePressDecrement();
        }
        else
        {
            // First intentional press -> Start 350 ms window to distinguish single press vs double press.
            _hasPendingSinglePress = true;
            _firstPressTime = now;
        }
    }

    private void ExecuteSinglePressIncrement()
    {
        if (!IsRegulatorInteractionAllowed())
            return;

        // Boundary check: Maximum level is 4 (no wrap-around). At level 4, another single press does nothing.
        if (_stepLevel >= MaxStepIndex)
            return;

        ApplyStepInternal(_stepLevel + 1, invokeEvents: true);
        if (_lastInputInteractor != null)
            _lastInputInteractor.SendHapticImpulse(hapticAmplitude, hapticDuration);
    }

    private void ExecuteDoublePressDecrement()
    {
        if (!IsRegulatorInteractionAllowed())
            return;

        // Boundary check: Minimum level is 0 (no wrap-around). At level 0, another double press does nothing.
        if (_stepLevel <= 0)
            return;

        ApplyStepInternal(_stepLevel - 1, invokeEvents: true);
        if (_lastInputInteractor != null)
            _lastInputInteractor.SendHapticImpulse(hapticAmplitude, hapticDuration);
    }

    /// <summary>
    /// Increases level by 1 (0 -> 1 -> 2 -> 3 -> 4) with no wrap-around, only when regulator is enabled (START active).
    /// </summary>
    public void StepNext()
    {
        if (!IsRegulatorInteractionAllowed())
            return;

        CancelPendingPress();
        ExecuteSinglePressIncrement();
    }

    /// <summary>
    /// Decreases level by 1 (4 -> 3 -> 2 -> 1 -> 0) with no wrap-around, only when regulator is enabled (START active).
    /// </summary>
    public void StepPrevious()
    {
        if (!IsRegulatorInteractionAllowed())
            return;

        CancelPendingPress();
        ExecuteDoublePressDecrement();
    }

    /// <summary>
    /// Sets the regulator step (0..MaxStepIndex) only when START is active (unless called during initialization).
    /// </summary>
    public void SetStep(int step, bool invokeEvents = true)
    {
        if (!_isInitializing && !IsRegulatorInteractionAllowed())
            return;

        CancelPendingPress();
        ApplyStepInternal(step, invokeEvents);
    }

    public void SetStepLevel(int nextStep)
    {
        SetStep(nextStep, invokeEvents: true);
    }

    private void ApplyStepInternal(int step, bool invokeEvents)
    {
        EnsureUniformRotationBasis();

        _stepLevel = Mathf.Clamp(step, 0, MaxStepIndex);
        _currentAngle = AngleForStep(_stepLevel);

        transform.localRotation = _baseLocalRotation * Quaternion.AngleAxis(_currentAngle, _rotationAxis);

        if (invokeEvents)
            onStepChanged.Invoke(_stepLevel);
    }

    private float AngleForStep(int step)
    {
        var anglePerStep = (maxRotationAngle - minRotationAngle) / MaxStepIndex;
        return minRotationAngle + anglePerStep * step;
    }

    private void HandleStepChanged(int stepLevel)
    {
        if (controllerPanel == null)
            controllerPanel = GetComponentInParent<VibroControllerPanel>();

        if (controllerPanel != null)
        {
            controllerPanel.OnRegulatorStepChanged(stepLevel, _isInitializing);
            return;
        }

        // Legacy fallback when no VibroControllerPanel is attached
        bool shouldStart = stepLevel > 0 && (!_isInitializing || (vibrationEffect != null && vibrationEffect.VibrateOnStart));
        if (vibrationEffect != null)
        {
            if (shouldStart)
                vibrationEffect.SetVibrationLevel(stepLevel);
            else
                vibrationEffect.ApplyStepFrequency(stepLevel, startMotor: false);
        }

        if (vibrationAudioSource == null)
            return;

        if (!shouldStart)
        {
            if (vibrationAudioSource.isPlaying)
                vibrationAudioSource.Stop();
            return;
        }

        vibrationAudioSource.playOnAwake = false;
        vibrationAudioSource.loop = true;
        if (vibrationClip == null)
        {
            if (vibrationAudioSource.isPlaying)
                vibrationAudioSource.Stop();
            vibrationAudioSource.clip = null;
            return;
        }

        vibrationAudioSource.clip = vibrationClip;
        var volume = stepVolumeMultipliers != null && stepVolumeMultipliers.Length > stepLevel
            ? stepVolumeMultipliers[stepLevel]
            : Mathf.Lerp(0.05f, 1f, stepLevel / (float)MaxStepIndex);
        vibrationAudioSource.volume = Mathf.Clamp01(volume);
        if (!vibrationAudioSource.isPlaying)
            vibrationAudioSource.Play();
    }
}
