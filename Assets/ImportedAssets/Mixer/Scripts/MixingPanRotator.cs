using UnityEngine;

/// <summary>
/// Continuously rotates an industrial mixer drum (such as SEFPRO_CCHIM250_Mixer's Mixing_Pan_RedDrum)
/// concentrically around its true cylindrical symmetry axis and plays a synchronized 3D looping mixer sound effect.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(AudioSource))]
public class MixingPanRotator : MonoBehaviour
{
    [Header("Drum Rotation")]
    [Tooltip("Continuous drum rotation speed in revolutions per minute (RPM). ~25-32 RPM is standard for a 250L refractory pan mixer.")]
    [Min(0f)]
    [SerializeField] private float rotationSpeedRPM = 28f;

    [Tooltip("When enabled, rotates clockwise when viewed from above; uncheck for counter-clockwise.")]
    [SerializeField] private bool clockwise = true;

    [Tooltip("Automatically begins continuous rotation and audio as soon as Play Mode starts.")]
    [SerializeField] private bool rotateOnStart = true;

    [Tooltip("Local symmetry axis around which the cylindrical drum spins (0,0,1 for Z-up Blender exports; auto-verified from mesh bounds).")]
    [SerializeField] private Vector3 localRotationAxis = Vector3.forward;

    [Tooltip("Automatically detects the cylindrical drum's symmetry axis and pivot offset from its MeshFilter bounds.")]
    [SerializeField] private bool autoDetectCylindricalAxis = true;

    [Header("Sound Effect")]
    [Tooltip("Seamless looping industrial pan mixer sound effect.")]
    [SerializeField] private AudioClip loopingMixerSound;

    [Tooltip("AudioSource playing the continuous drum sound (auto-assigned on this GameObject).")]
    [SerializeField] private AudioSource audioSource;

    [Range(0f, 1f)]
    [SerializeField] private float soundVolume = 0.60f;

    [Range(0f, 1f)]
    [Tooltip("3D spatial blend (0 = 2D, 1 = full 3D positional audio).")]
    [SerializeField] private float spatialBlend = 0.85f;

    [SerializeField] private float minAudioDistance = 1.8f;

    [SerializeField] private float maxAudioDistance = 18f;

    [Tooltip("Reference RPM corresponding to 1.0x audio pitch so live Inspector RPM tweaks subtly shift motor pitch.")]
    [SerializeField] private float nominalPitchRPM = 28f;

    private Vector3 _initialLocalPos;
    private Quaternion _initialLocalRot;
    private Vector3 _resolvedLocalAxis = Vector3.forward;
    private Vector3 _localPerpendicularPivotOffset = Vector3.zero;
    private bool _hasPerpendicularOffset;
    private bool _isRotating;
    private float _accumulatedDegrees;
    private float _spinUpFactor;

    public bool IsRotating => _isRotating;

    public float RotationSpeedRPM
    {
        get => rotationSpeedRPM;
        set => rotationSpeedRPM = Mathf.Max(0f, value);
    }

    private void Reset()
    {
        audioSource = GetComponent<AudioSource>();
        DetectDrumAxisAndPivot();
        ConfigureAudioSourceSettings();
    }

    private void Awake()
    {
        _initialLocalPos = transform.localPosition;
        _initialLocalRot = transform.localRotation;

        if (audioSource == null)
            audioSource = GetComponent<AudioSource>();

        DetectDrumAxisAndPivot();
        ConfigureAudioSourceSettings();
    }

    private void Start()
    {
        if (rotateOnStart)
            StartRotation();
    }

    private void OnDisable()
    {
        if (audioSource != null && audioSource.isPlaying)
            audioSource.Stop();
    }

    private void Update()
    {
        if (!_isRotating)
            return;

        float dt = Time.deltaTime;
        if (dt <= 0f)
            return;

        // Smooth 0.35s motor ramp-up on startup for natural mechanical inertia & pop-free audio
        _spinUpFactor = Mathf.MoveTowards(_spinUpFactor, 1f, dt / 0.35f);

        float degreesPerSecond = rotationSpeedRPM * 6f * _spinUpFactor;
        float directionSign = clockwise ? 1f : -1f;
        _accumulatedDegrees = Mathf.Repeat(_accumulatedDegrees + degreesPerSecond * directionSign * dt, 360f);

        Quaternion spinRot = Quaternion.AngleAxis(_accumulatedDegrees, _resolvedLocalAxis);
        Quaternion targetLocalRot = _initialLocalRot * spinRot;

        if (_hasPerpendicularOffset)
        {
            Vector3 parentPivot = _initialLocalPos + (_initialLocalRot * _localPerpendicularPivotOffset);
            Vector3 newLocalPos = parentPivot - (targetLocalRot * _localPerpendicularPivotOffset);
            transform.SetLocalPositionAndRotation(newLocalPos, targetLocalRot);
        }
        else
        {
            transform.localRotation = targetLocalRot;
        }

        UpdateLoopingAudio();
    }

    [ContextMenu("Start Drum Rotation")]
    public void StartRotation()
    {
        _isRotating = true;
        if (audioSource != null && loopingMixerSound != null)
        {
            if (audioSource.clip != loopingMixerSound)
                audioSource.clip = loopingMixerSound;

            ConfigureAudioSourceSettings();
            audioSource.volume = 0f;
            if (!audioSource.isPlaying)
                audioSource.Play();
        }
    }

    [ContextMenu("Stop Drum Rotation")]
    public void StopRotation()
    {
        _isRotating = false;
        _spinUpFactor = 0f;
        if (audioSource != null && audioSource.isPlaying)
            audioSource.Stop();
    }

    private void UpdateLoopingAudio()
    {
        if (audioSource == null || loopingMixerSound == null)
            return;

        if (audioSource.clip != loopingMixerSound)
            audioSource.clip = loopingMixerSound;

        if (rotationSpeedRPM <= 0.01f)
        {
            if (audioSource.isPlaying)
                audioSource.Stop();
            return;
        }

        if (!audioSource.isPlaying)
        {
            audioSource.loop = true;
            audioSource.Play();
        }

        audioSource.volume = soundVolume * _spinUpFactor;
        float refRpm = Mathf.Max(1f, nominalPitchRPM);
        float targetPitch = Mathf.Clamp(Mathf.Lerp(0.85f, 1.18f, (rotationSpeedRPM * _spinUpFactor) / (refRpm * 1.35f)), 0.78f, 1.25f);
        audioSource.pitch = targetPitch;
    }

    private void DetectDrumAxisAndPivot()
    {
        _resolvedLocalAxis = localRotationAxis.sqrMagnitude > 1e-5f
            ? localRotationAxis.normalized
            : Vector3.forward;

        MeshFilter mf = GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null)
        {
            _localPerpendicularOffsetClear();
            return;
        }

        Bounds b = mf.sharedMesh.bounds;
        if (autoDetectCylindricalAxis)
        {
            // Pick the local cardinal axis most aligned with World Up (+Y), which matches both
            // Z-up Blender exports (0,0,1) and native Y-up Unity meshes (0,1,0).
            Vector3 localWorldUp = transform.InverseTransformDirection(Vector3.up);
            float ax = Mathf.Abs(localWorldUp.x);
            float ay = Mathf.Abs(localWorldUp.y);
            float az = Mathf.Abs(localWorldUp.z);

            if (az >= ax && az >= ay)
                _resolvedLocalAxis = new Vector3(0f, 0f, Mathf.Sign(localWorldUp.z));
            else if (ay >= ax && ay >= az)
                _resolvedLocalAxis = new Vector3(0f, Mathf.Sign(localWorldUp.y), 0f);
            else
                _resolvedLocalAxis = new Vector3(Mathf.Sign(localWorldUp.x), 0f, 0f);

            localRotationAxis = _resolvedLocalAxis;
        }

        // Compute any mesh-center offset perpendicular to the spin axis so off-origin meshes never wobble/orbit
        Vector3 axialComponent = Vector3.Project(b.center, _resolvedLocalAxis);
        _localPerpendicularPivotOffset = b.center - axialComponent;
        _hasPerpendicularOffset = _localPerpendicularPivotOffset.sqrMagnitude > 1e-6f;
    }

    private void _localPerpendicularOffsetClear()
    {
        _localPerpendicularPivotOffset = Vector3.zero;
        _hasPerpendicularOffset = false;
    }

    private void ConfigureAudioSourceSettings()
    {
        if (audioSource == null)
            return;

        if (loopingMixerSound != null)
            audioSource.clip = loopingMixerSound;

        audioSource.playOnAwake = false;
        audioSource.loop = true;
        audioSource.spatialBlend = spatialBlend;
        audioSource.rolloffMode = AudioRolloffMode.Logarithmic;
        audioSource.minDistance = Mathf.Max(0.2f, minAudioDistance);
        audioSource.maxDistance = Mathf.Max(audioSource.minDistance + 1f, maxAudioDistance);
        audioSource.dopplerLevel = 0f;
    }
}
