using System;
using TMPro;
using UnityEngine;

/// <summary>
/// Central controller for the industrial Vibro Controller panel.
/// Final Required Behaviour:
/// 1. Press START:
///    - Start the platform's fixed, subtle physical vibration.
///    - Start the audio corresponding to the currently selected level (1–4; at level 0 audio stays off).
/// 2. Change levels 1–4:
///    - Physical platform vibration remains identical at every level (same fixed frequency and amplitude).
///    - Only the corresponding audio changes (when running).
/// 3. Select level 0:
///    - Stop audio.
///    - Keep physical platform vibration running (when started via START).
/// 4. Press STOP:
///    - Stop physical platform vibration completely.
///    - Stop all audio immediately.
/// 5. Press START again:
///    - Resume the same fixed physical vibration.
///    - Resume audio according to the selected level.
/// </summary>
public class VibroControllerPanel : MonoBehaviour
{
    public const int StepCount = 5;

    [Header("Selector Display Levels (Hz Readout / Audio Only)")]
    [Tooltip("Readout frequency in Hz at Step 0.")]
    [SerializeField, Min(0f)] private float level0Frequency = 0f;

    [Tooltip("Readout frequency in Hz at Step 1 (Low Audio).")]
    [SerializeField, Min(0f)] private float level1Frequency = 35f;

    [Tooltip("Readout frequency in Hz at Step 2 (Medium-Low Audio).")]
    [SerializeField, Min(0f)] private float level2Frequency = 70f;

    [Tooltip("Readout frequency in Hz at Step 3 (Medium-High Audio).")]
    [SerializeField, Min(0f)] private float level3Frequency = 105f;

    [Tooltip("Readout frequency in Hz at Step 4 (High Audio).")]
    [SerializeField, Min(0f)] private float level4Frequency = 140f;

    [Header("Custom Vibration Audio Per Level")]
    [Tooltip("Optional sound for Step 0 (none — level 0 stops audio).")]
    [SerializeField] private AudioClip level0Sound;

    [Tooltip("Looping vibration sound for Step 1.")]
    [SerializeField] private AudioClip level1Sound;

    [Tooltip("Looping vibration sound for Step 2.")]
    [SerializeField] private AudioClip level2Sound;

    [Tooltip("Looping vibration sound for Step 3.")]
    [SerializeField] private AudioClip level3Sound;

    [Tooltip("Looping vibration sound for Step 4 (falls back to level3Sound if unassigned).")]
    [SerializeField] private AudioClip level4Sound;

    [Tooltip("Optional click sound (unused during continuous industrial vibration to avoid overlapping sounds).")]
    [SerializeField] private AudioClip buttonClickSound;

    [Header("VR Audio Presence & Spatial Settings")]
    [Tooltip("AudioSource volume at Step 1 (Low).")]
    [SerializeField, Range(0f, 1f)] private float level1Volume = 0.72f;

    [Tooltip("AudioSource volume at Step 2 (Medium-Low).")]
    [SerializeField, Range(0f, 1f)] private float level2Volume = 0.82f;

    [Tooltip("AudioSource volume at Step 3 (Medium-High).")]
    [SerializeField, Range(0f, 1f)] private float level3Volume = 0.91f;

    [Tooltip("AudioSource volume at Step 4 (High / Max).")]
    [SerializeField, Range(0f, 1f)] private float level4Volume = 1.00f;

    [Tooltip("Spatial blend (0 = 2D full presence, 1 = 3D). 0.15 delivers 85% direct stereo presence to the VR headset listener while keeping 15% directional cue.")]
    [SerializeField, Range(0f, 1f)] private float audioSpatialBlend = 0.15f;

    [Tooltip("Minimum distance (m) before any 3D attenuation begins (12m covers the entire vibro station and operator spawn).")]
    [SerializeField, Min(1f)] private float audioMinDistance = 12.0f;

    [Tooltip("Maximum distance (m) for linear rolloff across the factory floor.")]
    [SerializeField, Min(5f)] private float audioMaxDistance = 45.0f;

    [Header("Scene References")]
    [SerializeField] private XRSteppedKnob regulatorKnob;
    [SerializeField] private VibroEffect targetVibrationEffect;
    [SerializeField] private AudioSource vibrationAudioSource;
    [SerializeField] private TMP_Text frequencyDisplayText;
    [SerializeField] private MeshRenderer outputLedRenderer;

    private bool _isMotorStarted;
    private int _currentStep;
    private bool _hasInitialized;
    private MaterialPropertyBlock _ledPropertyBlock;

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

    public bool IsMotorStarted => _isMotorStarted;
    public int CurrentStep => _currentStep;
    public float CurrentSelectedFrequency => GetFrequencyForStep(_currentStep);
    public float ActiveOutputFrequency => _isMotorStarted ? VibroEffect.FixedPhysicalFrequencyHz : 0f;
    public XRSteppedKnob RegulatorKnob => regulatorKnob;
    public VibroEffect TargetVibrationEffect => targetVibrationEffect;

    public event Action<bool> OnMotorStateChanged;
    public event Action<int> OnFrequencyStageChanged;

    private void Awake()
    {
        _ledPropertyBlock = new MaterialPropertyBlock();
        SanitizeFrequencyLevels();
        AutoResolveReferences();
        ConfigureAudioSource();
        EnsureDefaultAudioClips();
    }

    private void Start()
    {
        SanitizeFrequencyLevels();
        AutoResolveReferences();
        ConfigureAudioSource();
        EnsureDefaultAudioClips();

        _currentStep = regulatorKnob != null ? Mathf.Clamp(regulatorKnob.StepLevel, 0, StepCount - 1) : 0;
        _isMotorStarted = false;

        ApplySystemState();
        _hasInitialized = true;
    }

    private void OnValidate()
    {
        level0Frequency = 0f;
        level1Frequency = Mathf.Max(0f, level1Frequency);
        level2Frequency = Mathf.Max(0f, level2Frequency);
        level3Frequency = Mathf.Max(0f, level3Frequency);
        level4Frequency = Mathf.Max(0f, level4Frequency);

        if (Application.isPlaying && _hasInitialized)
            ApplySystemState();
    }

    private void SanitizeFrequencyLevels()
    {
        level0Frequency = 0f;
        if (level1Frequency <= 0f) level1Frequency = 35f;
        if (level2Frequency <= level1Frequency) level2Frequency = level1Frequency + 35f;
        if (level3Frequency <= level2Frequency) level3Frequency = level2Frequency + 35f;
        if (level4Frequency <= level3Frequency)
        {
            float stepDelta = Mathf.Max(5f, level3Frequency - level2Frequency);
            level4Frequency = level3Frequency + stepDelta;
        }
    }

    /// <summary>
    /// Called by the Regulator Knob (XRSteppedKnob) whenever its step (0..4) changes.
    /// IMPORTANT: The frequency selector never changes the overall running state (_isMotorStarted)
    /// and never changes the physical vibration parameters.
    /// - At levels 1–4: only the corresponding audio changes (if running).
    /// - At level 0: stops audio while keeping physical platform vibration running (if running).
    /// </summary>
    public void OnRegulatorStepChanged(int stepLevel, bool isInitialSetup = false)
    {
        // Regulator cannot change levels before START or after STOP (except during initial setup).
        if (!isInitialSetup && _hasInitialized && !_isMotorStarted)
            return;

        _currentStep = Mathf.Clamp(stepLevel, 0, StepCount - 1);
        ApplySystemState();
        if (!isInitialSetup && _isMotorStarted)
        {
            OnFrequencyStageChanged?.Invoke(_currentStep);
        }
    }

    /// <summary>
    /// Called by the Green START push-buttons:
    /// - Starts/resumes the platform's fixed, subtle physical vibration.
    /// - Starts/resumes the audio corresponding to the currently selected level (auto-advancing from level 0 to level 1 so pressing START immediately plays vibration sound).
    /// - Enables regulator interaction.
    /// </summary>
    public void PressStartButton()
    {
        _isMotorStarted = true;
        if (regulatorKnob != null)
            _currentStep = Mathf.Clamp(regulatorKnob.StepLevel, 0, StepCount - 1);

        // If the regulator is currently at Step 0 when START is pressed, advance to Step 1
        // so pressing START immediately responds with Level 1 vibration sound and 35.0 Hz readout.
        if (_currentStep <= 0)
        {
            _currentStep = 1;
            if (regulatorKnob != null)
                regulatorKnob.SetStep(1, invokeEvents: false);
        }

        PlayButtonClick();
        ApplySystemState();
        OnMotorStateChanged?.Invoke(true);
        if (_currentStep > 0)
        {
            OnFrequencyStageChanged?.Invoke(_currentStep);
        }
    }

    /// <summary>
    /// Called by the Red STOP or Emergency Stop push-buttons:
    /// - Immediately stops physical platform vibration and all audio.
    /// - Disables regulator level changes until START is pressed again.
    /// - Preserves the selected level when stopped.
    /// </summary>
    public void PressStopButton()
    {
        _isMotorStarted = false;
        if (regulatorKnob != null)
            regulatorKnob.CancelPendingPress();

        if (vibrationAudioSource != null && vibrationAudioSource.isPlaying)
        {
            vibrationAudioSource.Stop();
        }
        PlayButtonClick();
        ApplySystemState();
        OnMotorStateChanged?.Invoke(false);
    }

    public float GetFrequencyForStep(int step)
    {
        SanitizeFrequencyLevels();
        switch (Mathf.Clamp(step, 0, StepCount - 1))
        {
            case 0: return level0Frequency;
            case 1: return level1Frequency;
            case 2: return level2Frequency;
            case 3: return level3Frequency;
            case 4: return level4Frequency;
            default: return 0f;
        }
    }

    public AudioClip GetSoundForStep(int step)
    {
        switch (Mathf.Clamp(step, 0, StepCount - 1))
        {
            case 0: return null; // Level 0 always stops audio
            case 1: return level1Sound;
            case 2: return level2Sound;
            case 3: return level3Sound;
            case 4: return level4Sound != null ? level4Sound : level3Sound;
            default: return null;
        }
    }

    public float GetMaxConfiguredFrequency()
    {
        return Mathf.Max(1f, level1Frequency, level2Frequency, level3Frequency, level4Frequency);
    }

    private void ApplySystemState()
    {
        float selectedHz = GetFrequencyForStep(_currentStep);
        float maxConfiguredHz = GetMaxConfiguredFrequency();

        // 1. Update digital display (shows selected level Hz when running, or 0.0 when stopped)
        UpdateFrequencyDisplay(_isMotorStarted ? selectedHz : 0f, _isMotorStarted);

        // 2. Update OUT status LED (lit whenever motor is started)
        UpdateOutputLed(_isMotorStarted);

        // 3. Drive physical platform vibration:
        //    Controlled strictly by _isMotorStarted (START/STOP).
        //    Physical frequency and amplitude remain fixed at all levels 0..4 while _isMotorStarted is true.
        if (targetVibrationEffect != null)
        {
            targetVibrationEffect.ApplyRegulatorFrequency(
                VibroEffect.FixedPhysicalFrequencyHz,
                maxConfiguredHz,
                _isMotorStarted,
                _currentStep);
        }

        // 4. Drive vibration audio:
        //    Plays only when _isMotorStarted is true AND _currentStep > 0.
        //    Selecting level 0 stops audio while keeping physical vibration running.
        //    Pressing STOP stops all audio immediately.
        bool shouldPlayAudio = _isMotorStarted && _currentStep > 0;
        UpdateVibrationAudio(shouldPlayAudio, selectedHz, maxConfiguredHz);
    }

    private void UpdateFrequencyDisplay(float hzValue, bool motorActive)
    {
        if (frequencyDisplayText == null)
            return;

        float displayedHz = motorActive ? hzValue : 0f;
        frequencyDisplayText.text = $"{displayedHz:F1}";
        frequencyDisplayText.color = motorActive
            ? new Color(0.25f, 1f, 0.45f, 1f)   // Bright active green when motor running
            : new Color(0.38f, 1f, 0.52f, 0.85f); // Standby green when stopped
    }

    private void UpdateOutputLed(bool active)
    {
        if (outputLedRenderer == null)
            return;

        if (_ledPropertyBlock == null)
            _ledPropertyBlock = new MaterialPropertyBlock();

        outputLedRenderer.GetPropertyBlock(_ledPropertyBlock);
        Color baseCol = active ? new Color(1f, 0.58f, 0.08f, 1f) : new Color(0.18f, 0.14f, 0.08f, 1f);
        Color emissionCol = active ? new Color(1f, 0.5f, 0.05f, 1f) * 2.5f : Color.black;

        _ledPropertyBlock.SetColor(BaseColorId, baseCol);
        _ledPropertyBlock.SetColor(ColorId, baseCol);
        _ledPropertyBlock.SetColor(EmissionColorId, emissionCol);
        outputLedRenderer.SetPropertyBlock(_ledPropertyBlock);
    }

    public float GetVolumeForStep(int step)
    {
        switch (Mathf.Clamp(step, 0, StepCount - 1))
        {
            case 0: return 0f;
            case 1: return Mathf.Clamp01(level1Volume);
            case 2: return Mathf.Clamp01(level2Volume);
            case 3: return Mathf.Clamp01(level3Volume);
            case 4: return Mathf.Clamp01(level4Volume);
            default: return 0f;
        }
    }

    public void ConfigureAudioSource()
    {
        if (AudioListener.pause)
            AudioListener.pause = false;
        if (AudioListener.volume < 0.99f)
            AudioListener.volume = 1f;

        if (vibrationAudioSource == null)
            return;

        vibrationAudioSource.enabled = true;
        vibrationAudioSource.mute = false;
        vibrationAudioSource.playOnAwake = false;
        vibrationAudioSource.loop = true;
        vibrationAudioSource.bypassEffects = true;
        vibrationAudioSource.bypassListenerEffects = true;
        vibrationAudioSource.bypassReverbZones = true;
        vibrationAudioSource.spatialize = false;
        vibrationAudioSource.spatialBlend = Mathf.Clamp01(audioSpatialBlend);
        vibrationAudioSource.rolloffMode = AudioRolloffMode.Linear;
        vibrationAudioSource.minDistance = Mathf.Max(8f, audioMinDistance);
        vibrationAudioSource.maxDistance = Mathf.Max(vibrationAudioSource.minDistance + 10f, audioMaxDistance);
        vibrationAudioSource.dopplerLevel = 0f;
        vibrationAudioSource.spread = 90f;
        vibrationAudioSource.priority = 0;
    }

    private void UpdateVibrationAudio(bool shouldPlayAudio, float selectedHz, float maxConfiguredHz)
    {
        if (vibrationAudioSource == null)
            return;

        ConfigureAudioSource();

        if (!shouldPlayAudio)
        {
            if (vibrationAudioSource.isPlaying)
                vibrationAudioSource.Stop();
            return;
        }

        AudioClip stepClip = GetSoundForStep(_currentStep);
        if (stepClip == null)
        {
            if (vibrationAudioSource.isPlaying)
                vibrationAudioSource.Stop();
            return;
        }

        vibrationAudioSource.volume = GetVolumeForStep(_currentStep);
        vibrationAudioSource.pitch = (_currentStep == 4 && level4Sound == null) ? 1.12f : 1.0f;

        if (vibrationAudioSource.clip != stepClip)
        {
            vibrationAudioSource.clip = stepClip;
            vibrationAudioSource.Play();
        }
        else if (!vibrationAudioSource.isPlaying)
        {
            vibrationAudioSource.Play();
        }
    }

    private void PlayButtonClick()
    {
        if (buttonClickSound == null)
            return;

        if (vibrationAudioSource != null)
            vibrationAudioSource.PlayOneShot(buttonClickSound, 0.85f);
        else
            AudioSource.PlayClipAtPoint(buttonClickSound, transform.position, 0.85f);
    }

    public void AutoResolveReferences()
    {
        if (regulatorKnob == null)
            regulatorKnob = GetComponentInChildren<XRSteppedKnob>(true);

        if (vibrationAudioSource == null)
            vibrationAudioSource = GetComponent<AudioSource>();

        if (targetVibrationEffect == null && transform.parent != null)
            targetVibrationEffect = transform.parent.GetComponentInChildren<VibroEffect>(true);

        if (outputLedRenderer == null)
        {
            Transform outLed = transform.Find("OUT_LED");
            if (outLed != null)
                outputLedRenderer = outLed.GetComponent<MeshRenderer>();
        }

        if (frequencyDisplayText == null)
        {
            Transform digitalDisplay = transform.Find("Digital_Display");
            if (digitalDisplay != null)
                frequencyDisplayText = digitalDisplay.GetComponentInChildren<TMP_Text>(true);
        }
    }

    private void EnsureDefaultAudioClips()
    {
        if (level1Sound != null && level2Sound != null && level3Sound != null && level4Sound != null && buttonClickSound != null)
            return;

        const int sampleRate = 44100;

        if (level1Sound == null)
            level1Sound = SynthesizeVibrationLoop("Vibro_Level1_Low", sampleRate, 55f, 0.35f);

        if (level2Sound == null)
            level2Sound = SynthesizeVibrationLoop("Vibro_Level2_Med", sampleRate, 95f, 0.55f);

        if (level3Sound == null)
            level3Sound = SynthesizeVibrationLoop("Vibro_Level3_High", sampleRate, 135f, 0.80f);

        if (level4Sound == null)
            level4Sound = level3Sound;

        if (buttonClickSound == null)
            buttonClickSound = SynthesizeButtonClick("Vibro_ButtonClick", sampleRate);
    }

    private static AudioClip SynthesizeVibrationLoop(string clipName, int sampleRate, float baseFreq, float intensity)
    {
        int sampleCount = sampleRate;
        float[] samples = new float[sampleCount];
        float twoPi = Mathf.PI * 2f;

        for (int i = 0; i < sampleCount; i++)
        {
            float t = i / (float)sampleRate;
            float fundamental = Mathf.Sin(twoPi * baseFreq * t) * 0.55f;
            float secondHarmonic = Mathf.Sin(twoPi * (baseFreq * 2f) * t + 0.4f) * 0.25f;
            float thirdHarmonic = Mathf.Sin(twoPi * (baseFreq * 3f) * t + 1.1f) * 0.12f;
            float rumbleMod = 0.88f + 0.12f * Mathf.Sin(twoPi * 6f * t);
            samples[i] = Mathf.Clamp((fundamental + secondHarmonic + thirdHarmonic) * rumbleMod * intensity, -0.95f, 0.95f);
        }

        AudioClip clip = AudioClip.Create(clipName, sampleCount, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    private static AudioClip SynthesizeButtonClick(string clipName, int sampleRate)
    {
        int sampleCount = Mathf.RoundToInt(sampleRate * 0.045f);
        float[] samples = new float[sampleCount];
        float twoPi = Mathf.PI * 2f;

        for (int i = 0; i < sampleCount; i++)
        {
            float t = i / (float)sampleRate;
            float env = Mathf.Exp(-t * 95f);
            float click = (Mathf.Sin(twoPi * 920f * t) * 0.6f + Mathf.Sin(twoPi * 1840f * t) * 0.4f) * env;
            samples[i] = Mathf.Clamp(click, -0.95f, 0.95f);
        }

        AudioClip clip = AudioClip.Create(clipName, sampleCount, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }
}
