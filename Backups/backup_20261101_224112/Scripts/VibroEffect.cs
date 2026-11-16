using UnityEngine;

public class VibroEffect : MonoBehaviour
{
    [Header("Vibration Strength")]
    [Tooltip("Maximum displacement distance in local space. Keep low (e.g., 0.005 - 0.02) for subtlety.")]
    [SerializeField] private float positionMagnitude = 0.008f;

    [Tooltip("Maximum rotational wobble in degrees. Set to 0 if rotation vibration is not needed.")]
    [SerializeField] private float rotationMagnitude = 0.35f;

    [Header("Speed")]
    [Tooltip("Frequency of the vibration. Higher values create a faster buzz.")]
    [SerializeField] private float frequency = 24f;

    [Header("Stepped Control")]
    [SerializeField] private float minimumStepFrequency = 6f;
    [SerializeField] private float maximumStepFrequency = 28f;

    [Header("Behavior")]
    [SerializeField] private bool vibrateOnStart = true;
    [Tooltip("Use unscaled time so the vibration continues during pause.")]
    [SerializeField] private bool useUnscaledTime = false;

    private Vector3 _originalLocalPos;
    private Quaternion _originalLocalRot;
    private bool _isVibrating;
    private float _timeTracker;

    // Distinct noise sample offsets to ensure non-identical axis motion
    private float _seedX, _seedY, _seedZ;
    private float _seedRotX, _seedRotY, _seedRotZ;

    private void Awake()
    {
        _originalLocalPos = transform.localPosition;
        _originalLocalRot = transform.localRotation;

        // Assign random offsets so multiple vibrating objects don't sync up
        _seedX = Random.Range(0f, 1000f);
        _seedY = Random.Range(1000f, 2000f);
        _seedZ = Random.Range(2000f, 3000f);
        _seedRotX = Random.Range(3000f, 4000f);
        _seedRotY = Random.Range(4000f, 5000f);
        _seedRotZ = Random.Range(5000f, 6000f);
    }

    private void Start()
    {
        if (vibrateOnStart)
        {
            StartVibration();
        }
    }

    private void Update()
    {
        if (!_isVibrating) return;

        float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
        _timeTracker += dt * frequency;

        // Position noise mapped to range [-1, 1]
        float offsetX = (Mathf.PerlinNoise(_timeTracker, _seedX) * 2f - 1f) * positionMagnitude;
        float offsetY = (Mathf.PerlinNoise(_timeTracker, _seedY) * 2f - 1f) * positionMagnitude;
        float offsetZ = (Mathf.PerlinNoise(_timeTracker, _seedZ) * 2f - 1f) * positionMagnitude;

        transform.localPosition = _originalLocalPos + new Vector3(offsetX, offsetY, offsetZ);

        // Subtle rotational wobble
        if (rotationMagnitude > 0f)
        {
            float rotX = (Mathf.PerlinNoise(_timeTracker, _seedRotX) * 2f - 1f) * rotationMagnitude;
            float rotY = (Mathf.PerlinNoise(_timeTracker, _seedRotY) * 2f - 1f) * rotationMagnitude;
            float rotZ = (Mathf.PerlinNoise(_timeTracker, _seedRotZ) * 2f - 1f) * rotationMagnitude;

            transform.localRotation = _originalLocalRot * Quaternion.Euler(rotX, rotY, rotZ);
        }
    }

    /// <summary>
    /// Begins the subtle vibration.
    /// </summary>
    public void SetVibrationLevel(int level)
    {
        level = Mathf.Clamp(level, 0, 4);
        if (level == 0)
        {
            StopVibration();
            return;
        }

        frequency = Mathf.Lerp(minimumStepFrequency, maximumStepFrequency, (level - 1) / 3f);
        if (!_isVibrating)
            StartVibration();
    }

    public void StartVibration()
    {
        _originalLocalPos = transform.localPosition;
        _originalLocalRot = transform.localRotation;
        _isVibrating = true;
    }

    /// <summary>
    /// Stops vibration and resets back to the original local position and rotation.
    /// </summary>
    public void StopVibration()
    {
        _isVibrating = false;
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
    }
}