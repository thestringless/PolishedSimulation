using UnityEngine;
using Sefpro.RatchetBelt;

/// <summary>
/// Couples the active MoldCast assembly on the Vibrating Platform to the live
/// Vibrating_Rubber_Surface deck motion without reparenting under non-uniformly scaled
/// or rotated platform meshes. Tracks belt clamping status from both ratchet belts.
/// </summary>
[DefaultExecutionOrder(-100)]
[DisallowMultipleComponent]
public class MoldVibroPlatformCoupler : MonoBehaviour
{
    [Header("Platform & Belt References")]
    [Tooltip("The Vibrating_Rubber_Surface transform that oscillates via VibroEffect.")]
    public Transform vibratingDeckTransform;

    [Tooltip("First ratchet belt securing controller (Belt 1).")]
    public RatchetBeltMechanicsController belt1Controller;

    [Tooltip("Second ratchet belt securing controller (Belt 2).")]
    public RatchetBeltMechanicsController belt2Controller;

    [Header("Resting Pose on Platform (World Space)")]
    [SerializeField] private Vector3 moldRestWorldPosition = new Vector3(-22.5400f, 0.096188f, -5.4600f);
    [SerializeField] private Quaternion moldRestWorldRotation = Quaternion.identity;

    [Header("Runtime Telemetry (Read Only)")]
    [SerializeField] private int securedBeltCount = 0;
    [SerializeField] private bool isFullySecuredByBothBelts = false;
    [SerializeField] private Vector3 currentDeckDisplacement = Vector3.zero;

    private Vector3 _deckRestWorldPosition;
    private Quaternion _deckRestWorldRotation = Quaternion.identity;
    private bool _deckBaselineCaptured;

    public int SecuredBeltCount => securedBeltCount;
    public bool IsFullySecuredByBothBelts => isFullySecuredByBothBelts;
    public Vector3 MoldRestWorldPosition => moldRestWorldPosition;
    public Vector3 CurrentDeckDisplacement => currentDeckDisplacement;

    private void Awake()
    {
        AutoBindReferences();
        CaptureBaselines();
    }

    private void OnEnable()
    {
        AutoBindReferences();
        CaptureBaselines();
    }

    public void AutoBindReferences()
    {
        if (vibratingDeckTransform == null)
        {
            GameObject deckGo = GameObject.Find("VibratingPlatform (1)/Vibrating_Rubber_Surface");
            if (deckGo == null) deckGo = GameObject.Find("Vibrating_Rubber_Surface");
            if (deckGo != null) vibratingDeckTransform = deckGo.transform;
        }

        if (belt1Controller == null || belt2Controller == null)
        {
            var belts = FindObjectsByType<RatchetBeltMechanicsController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var b in belts)
            {
                if (b == null) continue;
                if (b.beltIndex == 1 && belt1Controller == null) belt1Controller = b;
                else if (b.beltIndex == 2 && belt2Controller == null) belt2Controller = b;
            }
        }
    }

    public void CaptureBaselines()
    {
        moldRestWorldPosition = transform.position;
        moldRestWorldRotation = transform.rotation;

        if (vibratingDeckTransform != null)
        {
            _deckRestWorldPosition = vibratingDeckTransform.position;
            _deckRestWorldRotation = vibratingDeckTransform.rotation;
            _deckBaselineCaptured = true;
        }
    }

    public void SetRestWorldPose(Vector3 worldPos, Quaternion worldRot)
    {
        moldRestWorldPosition = worldPos;
        moldRestWorldRotation = worldRot;
        transform.SetPositionAndRotation(worldPos, worldRot);
        if (vibratingDeckTransform != null)
        {
            _deckRestWorldPosition = vibratingDeckTransform.position;
            _deckRestWorldRotation = vibratingDeckTransform.rotation;
            _deckBaselineCaptured = true;
        }
    }

    private void LateUpdate()
    {
        int count = 0;
        if (IsBeltSecuringMold(belt1Controller)) count++;
        if (IsBeltSecuringMold(belt2Controller)) count++;
        securedBeltCount = count;
        isFullySecuredByBothBelts = (count >= 2);

        if (!Application.isPlaying || vibratingDeckTransform == null)
            return;

        if (!_deckBaselineCaptured)
            CaptureBaselines();

        Vector3 deckPosDelta = vibratingDeckTransform.position - _deckRestWorldPosition;
        Quaternion deckRotDelta = vibratingDeckTransform.rotation * Quaternion.Inverse(_deckRestWorldRotation);
        currentDeckDisplacement = deckPosDelta;

        // Couple MoldCast 1:1 with the vibrating rubber deck around the deck's center
        Vector3 pivotToMold = moldRestWorldPosition - _deckRestWorldPosition;
        Vector3 targetPos = _deckRestWorldPosition + deckPosDelta + (deckRotDelta * pivotToMold);
        Quaternion targetRot = deckRotDelta * moldRestWorldRotation;

        transform.SetPositionAndRotation(targetPos, targetRot);
    }

    private static bool IsBeltSecuringMold(RatchetBeltMechanicsController belt)
    {
        return belt != null &&
               belt.IsFixedHookAttached &&
               belt.IsAdjHookAttached &&
               belt.CompletedStrokes > 0;
    }
}
