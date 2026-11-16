using TMPro;
using UnityEngine;

[DefaultExecutionOrder(-200)]
public class VibroEffect : MonoBehaviour
{
    public const float FixedPhysicalFrequencyHz = 15.0f;
    public const float FixedSubtleAmplitudeMeters = 0.000585f; // +30% from 0.00045m (0.45mm -> 0.585mm)

    public static readonly float[] DefaultStepFrequencies = { 0f, 35.0f, 70.0f, 105.0f, 140.0f };

    [Header("Fixed Physical Vibration Parameters")]
    [Tooltip("Constant subtle displacement amplitude in local space whenever the platform motor is running. Never altered by the frequency selector.")]
    [SerializeField] private float positionMagnitude = FixedSubtleAmplitudeMeters;

    [Tooltip("Maximum rotational wobble in degrees. Kept at 0 on large 4.14m platform decks to prevent deck-vs-frame twisting.")]
    [SerializeField] private float rotationMagnitude = 0f;

    [Tooltip("Constant physical oscillation frequency (Hz) whenever the platform motor is running. Never altered by the frequency selector.")]
    [SerializeField] private float frequency = FixedPhysicalFrequencyHz;

    [Header("Selector Display Mapping (Audio / Readout Only)")]
    [Tooltip("Nominal frequency labels in Hz mapped to selector positions 0 through 4.")]
    [SerializeField] private float[] stepFrequencies = { 0f, 35.0f, 70.0f, 105.0f, 140.0f };

    [Header("Display")]
    [Tooltip("Optional digital frequency readout (Hz) on the Vibro Controller panel.")]
    [SerializeField] private TMP_Text frequencyDisplayText;

    [Header("Behavior")]
    [SerializeField] private bool vibrateOnStart = false;
    [Tooltip("Use unscaled time so the vibration continues during pause.")]
    [SerializeField] private bool useUnscaledTime = false;

    private Vector3 _originalLocalPos;
    private Quaternion _originalLocalRot;
    private bool _hasCapturedOrigin;
    private bool _isVibrating;
    private float _timeTracker;
    private int _currentStepLevel;
    private float _activePositionMagnitude;

    public float CurrentFrequency => _isVibrating ? FixedPhysicalFrequencyHz : 0f;
    public float CurrentDisplacementMagnitude => _isVibrating ? FixedSubtleAmplitudeMeters : 0f;
    public bool IsVibrating => _isVibrating;
    public bool VibrateOnStart => vibrateOnStart;
    public int CurrentStepLevel => _currentStepLevel;
    public float PhaseAccumulator => _timeTracker;

    private void Awake()
    {
        CaptureOriginIfNeeded();
        SanitizeFixedParameters();
    }

    private void Start()
    {
        CaptureOriginIfNeeded();
        SanitizeFixedParameters();

        if (vibrateOnStart)
        {
            StartVibration();
        }
        else
        {
            StopVibration();
        }
    }

    private void OnDisable()
    {
        StopVibration();
    }

    private void Update()
    {
        if (!_isVibrating)
            return;

        CaptureOriginIfNeeded();

        float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
        _timeTracker += dt * FixedPhysicalFrequencyHz * Mathf.PI * 2f;

        float amp = FixedSubtleAmplitudeMeters;

        // Constant subtle translational micro-oscillation (identical across all selector levels 0..4 while running).
        // Zero rotational tilt preserves 100% rigid alignment between Vibrating_Rubber_Surface and Frame_Left.
        float offsetX = Mathf.Sin(_timeTracker * 1.1f) * (amp * 0.25f);
        float offsetY = Mathf.Sin(_timeTracker * 2.0f + 1.2f) * amp;
        float offsetZ = Mathf.Cos(_timeTracker * 1.5f + 0.5f) * (amp * 0.25f);

        transform.localPosition = _originalLocalPos + new Vector3(offsetX, offsetY, offsetZ);
        transform.localRotation = _originalLocalRot;
    }

    /// <summary>
    /// Returns the nominal selector frequency label for position 0..4 (for UI/audio reference only).
    /// </summary>
    public float GetFrequencyForStep(int level)
    {
        if (level <= 0)
            return 0f;

        int clamped = Mathf.Clamp(level, 0, DefaultStepFrequencies.Length - 1);
        if (stepFrequencies != null && stepFrequencies.Length > clamped && stepFrequencies[clamped] > 0f)
            return stepFrequencies[clamped];

        return DefaultStepFrequencies[clamped];
    }

    /// <summary>
    /// Controls the platform's running state from the controller panel.
    /// Physical vibration parameters (frequency and amplitude) remain strictly fixed whenever motorRunning is true,
    /// regardless of the selector step (0..4) or targetHz.
    /// </summary>
    public void ApplyRegulatorFrequency(float targetHz, float maxReferenceHz, bool motorRunning, int explicitStepLevel = -1)
    {
        CaptureOriginIfNeeded();
        SanitizeFixedParameters();

        if (explicitStepLevel >= 0)
            _currentStepLevel = Mathf.Clamp(explicitStepLevel, 0, DefaultStepFrequencies.Length - 1);

        if (!motorRunning)
        {
            StopVibration();
            return;
        }

        // Physical vibration stays identical at every selector level (0..4) while motorRunning is true.
        frequency = FixedPhysicalFrequencyHz;
        positionMagnitude = FixedSubtleAmplitudeMeters;
        _activePositionMagnitude = FixedSubtleAmplitudeMeters;

        if (!_isVibrating)
            StartVibration();
    }

    /// <summary>
    /// Updates the selector step index and applies motorRunning state without ever altering physical vibration parameters.
    /// </summary>
    public void ApplyStepFrequency(int level, bool startMotor)
    {
        _currentStepLevel = Mathf.Clamp(level, 0, DefaultStepFrequencies.Length - 1);
        ApplyRegulatorFrequency(GetFrequencyForStep(_currentStepLevel), 140f, startMotor, _currentStepLevel);
    }

    public void SetVibrationLevel(int level)
    {
        _currentStepLevel = Mathf.Clamp(level, 0, DefaultStepFrequencies.Length - 1);
        // Never alter physical vibration parameters when changing selector level; preserve current _isVibrating state.
        ApplyRegulatorFrequency(GetFrequencyForStep(_currentStepLevel), 140f, _isVibrating, _currentStepLevel);
    }

    public void UpdateFrequencyDisplay()
    {
        // Managed by VibroControllerPanel.
    }

    public void StartVibration()
    {
        CaptureOriginIfNeeded();
        frequency = FixedPhysicalFrequencyHz;
        positionMagnitude = FixedSubtleAmplitudeMeters;
        _activePositionMagnitude = FixedSubtleAmplitudeMeters;
        _isVibrating = true;
    }

    /// <summary>
    /// Stops vibration completely and resets back to the exact original local position and rotation.
    /// </summary>
    public void StopVibration()
    {
        CaptureOriginIfNeeded();
        _isVibrating = false;
        _activePositionMagnitude = 0f;
        transform.localPosition = _originalLocalPos;
        transform.localRotation = _originalLocalRot;
    }

    /// <summary>
    /// Re-anchors the base position if the object moves via parent or pathing.
    /// </summary>
    public void SetNewOrigin(Vector3 newLocalPos, Quaternion newLocalRot)
    {
        _originalLocalPos = newLocalPos;
        _originalLocalRot = newLocalRot;
        _hasCapturedOrigin = true;
    }

    private void CaptureOriginIfNeeded()
    {
        if (_hasCapturedOrigin)
            return;

        _originalLocalPos = transform.localPosition;
        _originalLocalRot = transform.localRotation;
        _hasCapturedOrigin = true;
    }

    private void SanitizeFixedParameters()
    {
        frequency = FixedPhysicalFrequencyHz;
        positionMagnitude = FixedSubtleAmplitudeMeters;
        rotationMagnitude = 0f;

        if (stepFrequencies == null || stepFrequencies.Length < DefaultStepFrequencies.Length)
        {
            stepFrequencies = (float[])DefaultStepFrequencies.Clone();
        }
    }
}
