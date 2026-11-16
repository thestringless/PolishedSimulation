using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Manages pickup, directed shaft-axis alignment, mounting-channel installation state,
/// captive nut/washer attachment at the rod tips when removed, and fastener-retention locking
/// for casting mold connecting rods (and vertical flange bolts).
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(MoldPart))]
public class ConnectingRod : MonoBehaviour
{
    private struct CaptiveNutBinding
    {
        public ScrewInteraction Screw;
        public Vector3 LocalPosOnRod;
        public Quaternion LocalRotOnRod;
    }

    [Header("Rod Geometry & Shaft Axis")]
    [Tooltip("Identifier for this connecting rod (e.g., Rod_0 .. Rod_5, Vertical_Bolt).")]
    [SerializeField] private string rodId;

    [Tooltip("Directed local shaft axis pointing from the fixed bolt-head/nut end toward the threaded tip.")]
    [SerializeField] private Vector3 localShaftAxis = new Vector3(0.0068f, 0.1253f, 0.9921f);

    [Tooltip("Mounting channel snap point where this rod starts installed (updated dynamically when moved to any valid channel).")]
    [SerializeField] private MoldSnapPoint currentMountingChannel;

    [Header("Fastener Retention State")]
    [Tooltip("True while a hex nut / fastener is tightened onto this rod's threaded end, preventing rod removal.")]
    [SerializeField] private bool hasFastenerSeated = false;

    private readonly List<CaptiveNutBinding> _captiveNuts = new List<CaptiveNutBinding>(2);
    private MoldPart _moldPart;
    private Func<bool> _retentionBlockerDelegate;

    /// <summary>Raised whenever this rod is installed into (true) or removed from (false) a mounting channel.</summary>
    public event Action<ConnectingRod, bool> InstallationStateChanged;

    public string RodId => string.IsNullOrWhiteSpace(rodId) ? name : rodId;

    public MoldPart Part => _moldPart != null ? _moldPart : (_moldPart = GetComponent<MoldPart>());

    public Vector3 LocalShaftAxis =>
        localShaftAxis.sqrMagnitude > Mathf.Epsilon ? localShaftAxis.normalized : Vector3.forward;

    public Vector3 WorldShaftAxis => transform.TransformDirection(LocalShaftAxis).normalized;

    public MoldSnapPoint CurrentMountingChannel => currentMountingChannel;

    public bool IsInstalled => Part != null && Part.IsInstalled && currentMountingChannel != null && currentMountingChannel.OccupiedPart == Part;

    public bool HasFastenerSeated => hasFastenerSeated;

    private void Awake()
    {
        _moldPart = GetComponent<MoldPart>();
    }

    private void OnEnable()
    {
        if (_moldPart == null)
            _moldPart = GetComponent<MoldPart>();

        if (_moldPart != null)
            _moldPart.StateChanged += OnPartStateChanged;

        SyncCurrentMountingChannel();
        RefreshFastenerRetentionLock();
    }

    private void Start()
    {
        SyncCurrentMountingChannel();
        RefreshFastenerRetentionLock();
    }

    private void OnDisable()
    {
        if (_moldPart != null)
            _moldPart.StateChanged -= OnPartStateChanged;
    }

    /// <summary>
    /// Configures rod identity, calibrated local shaft axis, and starting mounting channel.
    /// </summary>
    public void Configure(string id, Vector3 shaftLocalDirection, MoldSnapPoint initialChannel)
    {
        _moldPart = GetComponent<MoldPart>();
        rodId = id;
        if (shaftLocalDirection.sqrMagnitude > Mathf.Epsilon)
            localShaftAxis = shaftLocalDirection.normalized;
        currentMountingChannel = initialChannel;
    }

    /// <summary>
    /// Updates whether a fastener is seated on this rod's threaded end (locks rod against removal while true).
    /// </summary>
    public void SetFastenerSeated(bool seated)
    {
        hasFastenerSeated = seated;
        RefreshFastenerRetentionLock();
    }

    /// <summary>
    /// Attempts to install this rod into a specific mounting channel snap point.
    /// </summary>
    public bool TryInstallIntoChannel(MoldSnapPoint channel, bool checkPoseTolerance = true)
    {
        if (channel == null || Part == null)
            return false;

        if (!channel.TrySnapPart(Part, checkPoseTolerance))
            return false;

        currentMountingChannel = channel;
        RefreshFastenerRetentionLock();
        InstallationStateChanged?.Invoke(this, true);
        return true;
    }

    private void FixedUpdate()
    {
        if (Part != null && Part.IsFree && _captiveNuts.Count > 0)
            SyncCaptiveNutsToRodPose();
    }

    private void LateUpdate()
    {
        if (Part != null && Part.IsFree && _captiveNuts.Count > 0)
            SyncCaptiveNutsToRodPose();
    }

    /// <summary>
    /// Registers or refreshes a captive nut's exact rod-tip pose relative to this rod's installed coordinate frame.
    /// </summary>
    public void RegisterOrUpdateCaptiveNut(
        ScrewInteraction screw,
        Vector3 looseWorldPos,
        Quaternion looseWorldRot,
        Vector3 rodInstalledWorldPos,
        Quaternion rodInstalledWorldRot)
    {
        if (screw == null)
            return;

        Vector3 rodScale = transform.lossyScale;
        Vector3 invRel = Quaternion.Inverse(rodInstalledWorldRot) * (looseWorldPos - rodInstalledWorldPos);
        Vector3 localPos = new Vector3(
            Mathf.Abs(rodScale.x) > 1e-5f ? invRel.x / rodScale.x : invRel.x,
            Mathf.Abs(rodScale.y) > 1e-5f ? invRel.y / rodScale.y : invRel.y,
            Mathf.Abs(rodScale.z) > 1e-5f ? invRel.z / rodScale.z : invRel.z);
        Quaternion localRot = Quaternion.Inverse(rodInstalledWorldRot) * looseWorldRot;

        for (int i = 0; i < _captiveNuts.Count; i++)
        {
            if (_captiveNuts[i].Screw == screw)
            {
                _captiveNuts[i] = new CaptiveNutBinding
                {
                    Screw = screw,
                    LocalPosOnRod = localPos,
                    LocalRotOnRod = localRot
                };
                return;
            }
        }

        _captiveNuts.Add(new CaptiveNutBinding
        {
            Screw = screw,
            LocalPosOnRod = localPos,
            LocalRotOnRod = localRot
        });
    }

    /// <summary>
    /// Updates all captive nuts (and their washers) to stay locked onto this rod's tips while the rod is removed/free.
    /// </summary>
    public void SyncCaptiveNutsToRodPose()
    {
        for (int i = 0; i < _captiveNuts.Count; i++)
        {
            CaptiveNutBinding b = _captiveNuts[i];
            if (b.Screw == null || !b.Screw.IsAttachedToRemovedRod)
                continue;

            Vector3 targetPos = transform.TransformPoint(b.LocalPosOnRod);
            Quaternion targetRot = transform.rotation * b.LocalRotOnRod;
            b.Screw.transform.SetPositionAndRotation(targetPos, targetRot);
            b.Screw.SyncWasherImmediate();
        }
    }

    internal void AttachAllSeatedCaptiveNutsForRemoval()
    {
        for (int i = 0; i < _captiveNuts.Count; i++)
        {
            ScrewInteraction nut = _captiveNuts[i].Screw;
            if (nut != null)
                nut.AttachCaptiveToRemovedRod(this);
        }
        SyncCaptiveNutsToRodPose();
    }

    private void SeatCaptiveNutsIntoMountedChannel()
    {
        if (currentMountingChannel == null || _captiveNuts.Count == 0)
            return;

        SyncCaptiveNutsToRodPose();

        var allSlots = ScrewSlot.AllActive;
        for (int i = 0; i < _captiveNuts.Count; i++)
        {
            ScrewInteraction nut = _captiveNuts[i].Screw;
            if (nut == null || nut.IsSeated)
                continue;

            Vector3 nutCenter = MoldSnapPoint.GetPartWorldGeometricCenter(nut.Part);
            ScrewSlot bestSlot = null;
            float bestDist = float.MaxValue;

            for (int s = 0; s < allSlots.Count; s++)
            {
                ScrewSlot slot = allSlots[s];
                if (slot == null || !slot.isActiveAndEnabled || slot.IsOccupied || slot.HostRodSnapPoint != currentMountingChannel)
                    continue;

                float d = Vector3.Distance(nutCenter, slot.LooseWorldGeometricCenter);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestSlot = slot;
                }
            }

            if (bestSlot != null)
                nut.DetachCaptiveAndSeatIntoSlot(bestSlot);
        }
    }

    private void OnPartStateChanged(MoldPart part, MoldPartState previousState, MoldPartState newState)
    {
        if (newState == MoldPartState.Installed)
        {
            SyncCurrentMountingChannel();
            SeatCaptiveNutsIntoMountedChannel();
            RefreshFastenerRetentionLock();
            InstallationStateChanged?.Invoke(this, true);
        }
        else if (previousState == MoldPartState.Installed)
        {
            currentMountingChannel = null;
            AttachAllSeatedCaptiveNutsForRemoval();
            InstallationStateChanged?.Invoke(this, false);
        }
    }

    private void SyncCurrentMountingChannel()
    {
        if (Part == null || !Part.IsInstalled)
        {
            currentMountingChannel = null;
            return;
        }

        if (currentMountingChannel != null && currentMountingChannel.OccupiedPart == Part)
            return;

        var activePoints = MoldSnapPoint.AllActive;
        for (int i = 0; i < activePoints.Count; i++)
        {
            MoldSnapPoint sp = activePoints[i];
            if (sp != null && sp.OccupiedPart == Part)
            {
                currentMountingChannel = sp;
                return;
            }
        }
    }

    private void RefreshFastenerRetentionLock()
    {
        if (Part == null)
            return;

        if (currentMountingChannel != null)
        {
            if (_retentionBlockerDelegate == null)
                _retentionBlockerDelegate = () => hasFastenerSeated && currentMountingChannel != null && currentMountingChannel.OccupiedPart == Part;

            currentMountingChannel.AddExternalRemovalBlocker(_retentionBlockerDelegate);
            currentMountingChannel.RefreshLockState();
        }
        else if (Part.IsInstalled)
        {
            Part.SetDependencyLocked(hasFastenerSeated);
        }
    }
}
