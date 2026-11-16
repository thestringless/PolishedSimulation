using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// Interactive XR push-button for the Vibro Controller Start (Green) and Stop (Red) buttons.
/// Supports XR ray select, hand poke, activate, hovered trigger/button press, and mouse click with visual press animation & haptics.
/// </summary>
[DisallowMultipleComponent]
public class VibroPushButton : MonoBehaviour
{
    public enum ButtonRole
    {
        Start = 0,
        Stop = 1,
        Regulator = 2
    }

    [SerializeField] private ButtonRole role = ButtonRole.Start;
    [SerializeField] private VibroControllerPanel controllerPanel;
    [SerializeField] private Transform movingCap;
    [SerializeField] private XRSimpleInteractable interactable;
    [SerializeField] private Outline hoverOutline;

    [Header("Press Feedback")]
    [SerializeField] private Vector3 localPressOffset = new Vector3(0f, -0.00002f, 0f);
    [SerializeField] private float returnSpeed = 14f;
    [SerializeField, Range(0f, 1f)] private float hapticAmplitude = 0.35f;
    [SerializeField] private float hapticDuration = 0.04f;

    private Vector3 _capRestLocalPos;
    private Vector3 _capTargetLocalPos;
    private bool _capCaptured;
    private float _lastPressTime = -10f;
    private bool _wasHoveredControllerPressed;
    private static readonly List<InputDevice> s_xrDevices = new List<InputDevice>();

    public ButtonRole Role => role;
    public void PressButton() => TriggerPress(null);

    public void Configure(
        ButtonRole buttonRole,
        VibroControllerPanel panel,
        Transform capTransform,
        XRSimpleInteractable xri,
        Outline outline,
        Vector3 pressOffset)
    {
        role = buttonRole;
        controllerPanel = panel;
        movingCap = capTransform;
        interactable = xri;
        hoverOutline = outline;
        localPressOffset = pressOffset;

        if (hoverOutline != null)
        {
            hoverOutline.OutlineMode = Outline.Mode.OutlineAll;
            hoverOutline.OutlineColor = buttonRole == ButtonRole.Start
                ? new Color(0.2f, 1f, 0.45f, 1f)
                : new Color(1f, 0.25f, 0.25f, 1f);
            hoverOutline.OutlineWidth = 5f;
            hoverOutline.enabled = false;
        }
    }

    private void Awake()
    {
        if (controllerPanel == null)
            controllerPanel = GetComponentInParent<VibroControllerPanel>();

        if (interactable == null)
            interactable = GetComponent<XRSimpleInteractable>();

        if (interactable != null)
        {
            var col = GetComponent<Collider>();
            if (col != null && !interactable.colliders.Contains(col))
                interactable.colliders.Add(col);
        }

        if (hoverOutline == null)
            hoverOutline = GetComponent<Outline>();

        CaptureCapOrigin();
    }

    private void CaptureCapOrigin()
    {
        if (movingCap != null && !_capCaptured)
        {
            _capRestLocalPos = movingCap.localPosition;
            _capTargetLocalPos = _capRestLocalPos;
            _capCaptured = true;
        }
    }

    private void OnEnable()
    {
        CaptureCapOrigin();

        if (interactable != null)
        {
            interactable.firstHoverEntered.AddListener(HandleHoverEntered);
            interactable.lastHoverExited.AddListener(HandleHoverExited);
            interactable.selectEntered.AddListener(HandleSelectEntered);
            interactable.activated.AddListener(HandleActivated);
        }

        if (hoverOutline != null)
            hoverOutline.enabled = false;
    }

    private void OnDisable()
    {
        if (interactable != null)
        {
            interactable.firstHoverEntered.RemoveListener(HandleHoverEntered);
            interactable.lastHoverExited.RemoveListener(HandleHoverExited);
            interactable.selectEntered.RemoveListener(HandleSelectEntered);
            interactable.activated.RemoveListener(HandleActivated);
        }

        if (movingCap != null && _capCaptured)
            movingCap.localPosition = _capRestLocalPos;

        if (hoverOutline != null)
            hoverOutline.enabled = false;
    }

    private void Update()
    {
        PollHoveredVRControllerPress();

        if (movingCap != null && _capCaptured)
        {
            movingCap.localPosition = Vector3.Lerp(movingCap.localPosition, _capTargetLocalPos, Time.deltaTime * returnSpeed);
            _capTargetLocalPos = Vector3.Lerp(_capTargetLocalPos, _capRestLocalPos, Time.deltaTime * (returnSpeed * 0.7f));
        }
    }

    private void PollHoveredVRControllerPress()
    {
        if (interactable == null || !interactable.isHovered || interactable.interactorsHovering.Count == 0)
        {
            _wasHoveredControllerPressed = false;
            return;
        }

        IXRInteractor activeInteractor = interactable.interactorsHovering[0];
        var inputInteractor = activeInteractor as XRBaseInputInteractor;
        bool pressedNow = false;

        if (inputInteractor != null)
        {
            if ((inputInteractor.activateInput != null && inputInteractor.activateInput.ReadIsPerformed()) ||
                (inputInteractor.selectInput != null && inputInteractor.selectInput.ReadIsPerformed()))
            {
                pressedNow = true;
            }
        }

        if (!pressedNow)
        {
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
                    pressedNow = true;
                    break;
                }
            }
        }

        if (pressedNow && !_wasHoveredControllerPressed)
        {
            _wasHoveredControllerPressed = true;
            TriggerPress(inputInteractor);
        }
        else if (!pressedNow)
        {
            _wasHoveredControllerPressed = false;
        }
    }

    private void HandleHoverEntered(HoverEnterEventArgs args)
    {
        if (hoverOutline != null)
            hoverOutline.enabled = true;
    }

    private void HandleHoverExited(HoverExitEventArgs args)
    {
        if (hoverOutline != null)
            hoverOutline.enabled = false;
    }

    private void HandleSelectEntered(SelectEnterEventArgs args)
    {
        TriggerPress(args.interactorObject as XRBaseInputInteractor);

        if (interactable != null && interactable.interactionManager != null && args.interactorObject != null)
        {
            interactable.interactionManager.SelectExit(args.interactorObject, interactable);
        }
    }

    private void HandleActivated(ActivateEventArgs args)
    {
        TriggerPress(args.interactorObject as XRBaseInputInteractor);
    }

    private void OnMouseDown()
    {
        TriggerPress(null);
    }

    public void TriggerPress(XRBaseInputInteractor inputInteractor = null)
    {
        if (role == ButtonRole.Regulator)
        {
            if (controllerPanel != null && controllerPanel.RegulatorKnob != null)
                controllerPanel.RegulatorKnob.RegisterVRPress(inputInteractor);
            return;
        }

        if (Time.unscaledTime - _lastPressTime < 0.12f)
            return;

        _lastPressTime = Time.unscaledTime;

        if (movingCap != null && _capCaptured)
        {
            movingCap.localPosition = _capRestLocalPos + localPressOffset;
            _capTargetLocalPos = _capRestLocalPos;
        }

        if (inputInteractor != null)
        {
            inputInteractor.SendHapticImpulse(hapticAmplitude, hapticDuration);
        }

        if (controllerPanel == null)
            return;

        if (role == ButtonRole.Start)
            controllerPanel.PressStartButton();
        else if (role == ButtonRole.Stop)
            controllerPanel.PressStopButton();
    }
}
