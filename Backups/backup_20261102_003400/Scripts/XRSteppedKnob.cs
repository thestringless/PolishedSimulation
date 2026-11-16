using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

[Serializable]
public class XRSteppedKnobEvent : UnityEvent<int>
{
}

public class XRSteppedKnob : XRBaseInteractable
{
    private const int StepCount = 5;

    [Header("Detents")]
    [SerializeField] private Vector3 rotationAxis = Vector3.forward;
    [SerializeField] private float minRotationAngle = -120f;
    [SerializeField] private float maxRotationAngle = 120f;
    [SerializeField, Range(0, StepCount - 1)] private int initialStep;

    [Header("Haptics")]
    [SerializeField, Range(0f, 1f)] private float hapticAmplitude = 0.25f;
    [SerializeField, Min(0f)] private float hapticDuration = 0.035f;

    [Header("Platform and Audio")]
    [SerializeField] private VibroEffect vibrationEffect;
    [SerializeField] private AudioSource vibrationAudioSource;
    [SerializeField] private AudioClip vibrationClip;
    [SerializeField] private float[] stepVolumeMultipliers = { 0.05f, 0.25f, 0.45f, 0.7f, 1f };

    public XRSteppedKnobEvent onStepChanged = new XRSteppedKnobEvent();

    private Quaternion _baseLocalRotation;
    private IXRSelectInteractor _selectInteractor;
    private XRBaseInputInteractor _inputInteractor;
    private Vector3 _previousInteractorDirection;
    private Vector3 _rotationAxis;
    private float _unclampedAngle;
    private float _currentAngle;
    private int _stepLevel;

    public int StepLevel => _stepLevel;

    public void SetStepLevel(int nextStep)
    {
        nextStep = Mathf.Clamp(nextStep, 0, StepCount - 1);
        if (nextStep == _stepLevel)
            return;

        _stepLevel = nextStep;
        _currentAngle = AngleForStep(_stepLevel);
        _unclampedAngle = _currentAngle;
        if (_baseLocalRotation == default)
            _baseLocalRotation = transform.localRotation;
        transform.localRotation = _baseLocalRotation * Quaternion.AngleAxis(_currentAngle, _rotationAxis);

        onStepChanged.Invoke(_stepLevel);
    }

    public void StepNext()
    {
        SetStepLevel((_stepLevel + 1) % StepCount);
    }

    public void StepPrevious()
    {
        SetStepLevel((_stepLevel - 1 + StepCount) % StepCount);
    }

    private void Start()
    {
        if (maxRotationAngle <= minRotationAngle)
            maxRotationAngle = minRotationAngle + 240f;

        _rotationAxis = rotationAxis.sqrMagnitude > Mathf.Epsilon
            ? rotationAxis.normalized
            : Vector3.forward;
        _baseLocalRotation = transform.localRotation;

        selectEntered.AddListener(HandleSelectEntered);
        selectExited.AddListener(HandleSelectExited);
        onStepChanged.AddListener(HandleStepChanged);

        _stepLevel = Mathf.Clamp(initialStep, 0, StepCount - 1);
        _currentAngle = AngleForStep(_stepLevel);
        _unclampedAngle = _currentAngle;
        transform.localRotation = _baseLocalRotation * Quaternion.AngleAxis(_currentAngle, _rotationAxis);
        onStepChanged.Invoke(_stepLevel);
    }

    private void OnDestroy()
    {
        selectEntered.RemoveListener(HandleSelectEntered);
        selectExited.RemoveListener(HandleSelectExited);
        onStepChanged.RemoveListener(HandleStepChanged);
    }

    private void Update()
    {
        if (_selectInteractor == null || !TryGetInteractorDirection(out var direction))
            return;

        var worldAxis = transform.TransformDirection(_rotationAxis);
        var delta = Vector3.SignedAngle(_previousInteractorDirection, direction, worldAxis);
        _previousInteractorDirection = direction;
        _unclampedAngle = Mathf.Clamp(_unclampedAngle + delta, minRotationAngle, maxRotationAngle);

        var anglePerStep = (maxRotationAngle - minRotationAngle) / (StepCount - 1);
        var nextStep = Mathf.Clamp(
            Mathf.RoundToInt((_unclampedAngle - minRotationAngle) / anglePerStep),
            0,
            StepCount - 1);
        if (nextStep == _stepLevel)
            return;

        _stepLevel = nextStep;
        _currentAngle = AngleForStep(_stepLevel);
        transform.localRotation = _baseLocalRotation * Quaternion.AngleAxis(_currentAngle, _rotationAxis);

        if (_inputInteractor != null)
            _inputInteractor.SendHapticImpulse(hapticAmplitude, hapticDuration);

        onStepChanged.Invoke(_stepLevel);
    }

    private void HandleSelectEntered(SelectEnterEventArgs args)
    {
        _selectInteractor = args.interactorObject as IXRSelectInteractor;
        _inputInteractor = args.interactorObject as XRBaseInputInteractor;
        _unclampedAngle = _currentAngle;

        if (_selectInteractor == null || !TryGetInteractorDirection(out _previousInteractorDirection))
        {
            _selectInteractor = null;
            _inputInteractor = null;
        }
    }

    private void HandleSelectExited(SelectExitEventArgs args)
    {
        if (args.interactorObject != _selectInteractor)
            return;

        _selectInteractor = null;
        _inputInteractor = null;
    }

    private bool TryGetInteractorDirection(out Vector3 direction)
    {
        direction = Vector3.zero;
        if (_selectInteractor == null)
            return false;

        var attachTransform = _selectInteractor.GetAttachTransform(this);
        if (attachTransform == null)
            return false;

        var worldAxis = transform.TransformDirection(_rotationAxis);
        direction = Vector3.ProjectOnPlane(attachTransform.position - transform.position, worldAxis);
        if (direction.sqrMagnitude < 0.000001f)
            return false;

        direction.Normalize();
        return true;
    }

    private float AngleForStep(int step)
    {
        var anglePerStep = (maxRotationAngle - minRotationAngle) / (StepCount - 1);
        return minRotationAngle + anglePerStep * step;
    }

    private void HandleStepChanged(int stepLevel)
    {
        if (vibrationEffect != null)
            vibrationEffect.SetVibrationLevel(stepLevel);

        if (vibrationAudioSource == null)
            return;

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
            : Mathf.Lerp(0.05f, 1f, stepLevel / (float)(StepCount - 1));
        vibrationAudioSource.volume = Mathf.Clamp01(volume);
        if (!vibrationAudioSource.isPlaying)
            vibrationAudioSource.Play();
    }
}
