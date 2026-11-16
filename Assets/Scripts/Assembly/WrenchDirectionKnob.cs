using TMPro;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Interactive rotary Direction Selector Knob mounted on the Impact Wrench housing.
/// Allows the operator to select TIGHTEN (CW) or LOOSEN (CCW) directly by clicking/selecting/activating
/// the 3D knob on the wrench (or via controller button while holding the wrench), with animated
/// rotary pointer rotation, color-coded status indicator, and crisp mechanical switch click audio.
/// </summary>
[DisallowMultipleComponent]
public class WrenchDirectionKnob : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Target impact wrench motor controller whose ReverseDirection is controlled by this knob.")]
    [SerializeField] private XRElectricScrewdriver screwdriver;

    [Tooltip("Rotary dial / pointer transform that rotates between Tighten (+angle) and Loosen (-angle).")]
    [SerializeField] private Transform knobDialVisual;

    [Tooltip("Color-coded status LED / pointer renderer on the knob.")]
    [SerializeField] private Renderer indicatorRenderer;

    [Tooltip("Readout label on the wrench rear housing showing current rotation mode.")]
    [SerializeField] private TextMeshProUGUI directionLabel;

    [Header("Dial Motion")]
    [Tooltip("Local Euler angle when set to TIGHTEN (Forward / CW).")]
    [SerializeField] private Vector3 tightenLocalEuler = new Vector3(0f, 0f, -38f);

    [Tooltip("Local Euler angle when set to LOOSEN (Reverse / CCW).")]
    [SerializeField] private Vector3 loosenLocalEuler = new Vector3(0f, 0f, 38f);

    private static readonly Color TightenColor = new Color(0.14f, 0.90f, 0.42f, 1f);
    private static readonly Color LoosenColor = new Color(1.00f, 0.36f, 0.18f, 1f);
    private static AudioClip s_switchClickClip;

    private XRSimpleInteractable _simpleInteractable;
    private MaterialPropertyBlock _mpb;
    private AudioSource _clickAudio;
    private Quaternion _targetDialRot;
    private float _lastToggleTime = -10f;

    private void Awake()
    {
        AutoBind();
        EnsureClickAudio();
        SyncVisualsImmediate();
    }

    private void OnEnable()
    {
        AutoBind();
        if (screwdriver != null)
            screwdriver.DirectionChanged += OnDirectionChanged;

        if (_simpleInteractable != null)
        {
            _simpleInteractable.selectEntered.AddListener(OnKnobSelectEntered);
            _simpleInteractable.activated.AddListener(OnKnobActivated);
        }

        SyncVisualsImmediate();
    }

    private void OnDisable()
    {
        if (screwdriver != null)
            screwdriver.DirectionChanged -= OnDirectionChanged;

        if (_simpleInteractable != null)
        {
            _simpleInteractable.selectEntered.RemoveListener(OnKnobSelectEntered);
            _simpleInteractable.activated.RemoveListener(OnKnobActivated);
        }
    }

    private void Update()
    {
        if (knobDialVisual != null)
        {
            knobDialVisual.localRotation = Quaternion.Slerp(
                knobDialVisual.localRotation,
                _targetDialRot,
                Mathf.Clamp01(Time.deltaTime * 18f));
        }
    }

    private void OnMouseDown()
    {
        // Supports quick direct mouse click in Unity Editor / XR Device Simulator view
        ToggleKnobDirection();
    }

    public void ToggleKnobDirection()
    {
        if (screwdriver == null)
            return;

        if (Application.isPlaying && Time.unscaledTime - _lastToggleTime < 0.15f)
            return;

        _lastToggleTime = Application.isPlaying ? Time.unscaledTime : -10f;
        screwdriver.ToggleDirection();
    }

    private void OnKnobSelectEntered(SelectEnterEventArgs args)
    {
        ToggleKnobDirection();
        if (_simpleInteractable != null && _simpleInteractable.interactionManager != null && args.interactorObject != null)
            _simpleInteractable.interactionManager.SelectExit(args.interactorObject, _simpleInteractable);
    }

    private void OnKnobActivated(ActivateEventArgs args)
    {
        ToggleKnobDirection();
    }

    private void OnDirectionChanged(bool isReverseLoosen)
    {
        ApplyVisualTargets(isReverseLoosen);
        if (Application.isPlaying && Time.timeSinceLevelLoad > 0.05f)
            PlaySwitchClick();
    }

    public void SyncVisualsImmediate()
    {
        bool isLoosen = screwdriver != null && screwdriver.ReverseDirection;
        ApplyVisualTargets(isLoosen);
        if (knobDialVisual != null)
            knobDialVisual.localRotation = _targetDialRot;
    }

    private void ApplyVisualTargets(bool isLoosen)
    {
        _targetDialRot = Quaternion.Euler(isLoosen ? loosenLocalEuler : tightenLocalEuler);

        Color activeCol = isLoosen ? LoosenColor : TightenColor;
        if (indicatorRenderer != null)
        {
            if (_mpb == null) _mpb = new MaterialPropertyBlock();
            indicatorRenderer.GetPropertyBlock(_mpb);
            _mpb.SetColor("_BaseColor", activeCol);
            _mpb.SetColor("_Color", activeCol);
            indicatorRenderer.SetPropertyBlock(_mpb);
        }

        if (directionLabel != null)
        {
            directionLabel.enableAutoSizing = true;
            directionLabel.fontSizeMin = 14f;
            directionLabel.fontSizeMax = 24f;
            directionLabel.text = isLoosen ? "<< CCW LOOSEN" : "CW TIGHTEN >>";
            directionLabel.color = activeCol;
        }
    }

    private void AutoBind()
    {
        if (screwdriver == null)
            screwdriver = GetComponentInParent<XRElectricScrewdriver>();

        if (_simpleInteractable == null)
            _simpleInteractable = GetComponent<XRSimpleInteractable>();

        if (knobDialVisual == null)
        {
            Transform dial = transform.Find("Knob_Dial_Pointer");
            knobDialVisual = dial != null ? dial : transform;
        }

        if (indicatorRenderer == null && knobDialVisual != null)
            indicatorRenderer = knobDialVisual.GetComponent<Renderer>();

        if (directionLabel == null)
            directionLabel = GetComponentInChildren<TextMeshProUGUI>(includeInactive: true);
    }

    private void EnsureClickAudio()
    {
        _clickAudio = GetComponent<AudioSource>();
        if (_clickAudio == null)
            _clickAudio = gameObject.AddComponent<AudioSource>();

        _clickAudio.playOnAwake = false;
        _clickAudio.loop = false;
        _clickAudio.spatialBlend = 1f;

        if (s_switchClickClip == null)
            s_switchClickClip = CreateSwitchClickClip();
    }

    private void PlaySwitchClick()
    {
        if (_clickAudio != null && s_switchClickClip != null)
            _clickAudio.PlayOneShot(s_switchClickClip, 0.85f);
    }

    private static AudioClip CreateSwitchClickClip()
    {
        const int sampleRate = 22050;
        int samples = Mathf.RoundToInt(sampleRate * 0.045f);
        float[] data = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / sampleRate;
            float env = Mathf.Exp(-t * 110f);
            data[i] = (Mathf.Sin(2f * Mathf.PI * 1450f * t) * 0.65f + Mathf.Sin(2f * Mathf.PI * 2900f * t) * 0.35f) * env;
        }
        AudioClip clip = AudioClip.Create("KnobDetentClick", samples, 1, sampleRate, false);
        clip.hideFlags = HideFlags.DontSave;
        clip.SetData(data, 0);
        return clip;
    }
}
