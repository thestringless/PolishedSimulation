using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Categories derived from the actual 27-mesh MoldCast (Mold.fbx) industrial casting mold.
/// </summary>
public enum MoldPartCategory
{
    PalletBase,
    FloorPlate,
    LongWall,
    ShortWall,
    ConnectingRod,
    VerticalBolt,
    Fastener
}

/// <summary>
/// Mechanical assembly state of an individual mold component (tracked separately from visuals).
/// </summary>
public enum MoldPartState
{
    Free,
    LooseSeated,
    Installed
}

/// <summary>
/// Represents an independently movable casting mold component, managing its assembled reference pose,
/// mechanical state, dependency lock state, Rigidbody/Collider settings, and XR grabbing lifecycle.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(XRGrabInteractable))]
public class MoldPart : MonoBehaviour
{
    [Header("Part Identity")]
    [Tooltip("Unique identifier for this specific mold component instance (e.g., LongWall_Front, Rod_0, RodNut_0).")]
    [SerializeField] private string partId;

    [Tooltip("Structural/functional group used by snap points and stage validation.")]
    [SerializeField] private MoldPartCategory category = MoldPartCategory.LongWall;

    [Tooltip("Human-readable name shown in assembly UI and logs.")]
    [SerializeField] private string displayName;

    [Tooltip("Index within its category (e.g., 0..5 for rods and rod nuts) when specific slot pairing applies.")]
    [SerializeField] private int slotIndex = 0;

    [Header("Assembled Reference Pose (MoldCast Local Space)")]
    [Tooltip("Parent transform owning the assembled coordinate frame (defaults to MoldCast root).")]
    [SerializeField] private Transform moldContainer;

    [SerializeField] private Vector3 assembledLocalPosition;
    [SerializeField] private Quaternion assembledLocalRotation = Quaternion.identity;
    [SerializeField] private Vector3 assembledLocalScale = Vector3.one;

    [Header("State & Lock Settings")]
    [Tooltip("Initial mechanical state on scene startup.")]
    [SerializeField] private MoldPartState currentState = MoldPartState.Installed;

    [Tooltip("When true, mechanical dependencies currently prevent grabbing/removing this part.")]
    [SerializeField] private bool isLockedByDependency = false;

    [Tooltip("When true (used on the 4 Mold Walls), locks this part to its original assembled rotation while grabbed and when resting free on the floor so walls never tip into a laid-down orientation.")]
    [SerializeField] private bool keepOriginalRotation = false;

    [Header("Dismantle & Reassemble Audio")]
    [Tooltip("Sound effect played when this object is removed/dismantled from the mold (falls back to category-matched procedural clip if unassigned).")]
    [SerializeField] private AudioClip removeSoundClip;

    [Tooltip("Sound effect played when this object is placed/snapped back onto the mold (falls back to category-matched procedural clip if unassigned).")]
    [SerializeField] private AudioClip placeBackSoundClip;

    [Tooltip("Playback volume for object removal and placement sound effects.")]
    [Range(0f, 1f)]
    [SerializeField] private float partSoundVolume = 0.85f;

    /// <summary>When true, suppresses transition sound effects during batch scene reset or initial layout staging.</summary>
    public static bool SuppressStateChangeAudio { get; set; }

    private static AudioClip s_procWallRemove;
    private static AudioClip s_procWallInstall;
    private static AudioClip s_procRodRemove;
    private static AudioClip s_procRodInstall;
    private static AudioClip s_procFastenerRemove;
    private static AudioClip s_procFastenerInstall;

    private Rigidbody _rigidbody;
    private XRGrabInteractable _grabInteractable;
    private Collider _primaryCollider;
    private AudioSource _partAudioSource;
    private readonly List<Collider> _siblingMoldColliders = new List<Collider>();
    private bool _ignoringSiblingCollisions;
    private Bounds _cachedPalletWorldBounds;
    private bool _hasCachedPalletBounds;
    private float _groundRestTimer;
    private float _lastAudioPlayTime = -10f;

    /// <summary>Optional external rule evaluated before allowing removal from Installed/LooseSeated state.</summary>
    public Func<MoldPart, bool> CanBeRemovedPredicate { get; set; }

    /// <summary>Optional external callback invoked on release to check if a snap point claims this part.</summary>
    public Func<MoldPart, bool> TrySnapOnReleaseCallback { get; set; }

    /// <summary>Raised whenever this part's MoldPartState changes (previousState, newState).</summary>
    public event Action<MoldPart, MoldPartState, MoldPartState> StateChanged;

    public string PartId => string.IsNullOrWhiteSpace(partId) ? name : partId;

    public MoldPartCategory Category => category;

    public string DisplayName => string.IsNullOrWhiteSpace(displayName) ? name : displayName;

    public int SlotIndex => slotIndex;

    public Transform MoldContainer => moldContainer;

    public Vector3 AssembledLocalPosition => assembledLocalPosition;

    public Quaternion AssembledLocalRotation => assembledLocalRotation;

    public Vector3 AssembledWorldPosition =>
        moldContainer != null ? moldContainer.TransformPoint(assembledLocalPosition) : assembledLocalPosition;

    public Quaternion AssembledWorldRotation =>
        moldContainer != null ? moldContainer.rotation * assembledLocalRotation : assembledLocalRotation;

    public MoldPartState CurrentState => currentState;

    public bool IsInstalled => currentState == MoldPartState.Installed;

    public bool IsLooseSeated => currentState == MoldPartState.LooseSeated;

    public bool IsFree => currentState == MoldPartState.Free;

    public bool IsHeld => _grabInteractable != null && _grabInteractable.isSelected;

    public bool IsLockedByDependency => isLockedByDependency;

    public bool KeepOriginalRotation
    {
        get => keepOriginalRotation;
        set
        {
            keepOriginalRotation = value;
            ApplyGrabRotationSettings();
            ApplyPhysicsForCurrentState();
        }
    }

    public Rigidbody Body => _rigidbody;

    public XRGrabInteractable GrabInteractable => _grabInteractable;

    public Collider PrimaryCollider => _primaryCollider;

    private bool _isCaptiveFollower;

    /// <summary>
    /// True when this fastener stays attached to its removed rod (following the rod's tip pose kinematically without floor collision).
    /// </summary>
    public bool IsCaptiveFollower => _isCaptiveFollower;

    public void SetCaptiveFollowerMode(bool captive)
    {
        EnsureComponents();
        _isCaptiveFollower = captive;

        if (_primaryCollider != null)
            _primaryCollider.enabled = !captive;

        if (_rigidbody != null)
            _rigidbody.detectCollisions = !captive;

        ApplyPhysicsForCurrentState();
        RefreshGrabLockState();
    }

    private void Awake()
    {
        EnsureComponents();
        ApplyPhysicsForCurrentState();
        RefreshGrabLockState();
    }

    private void OnEnable()
    {
        EnsureComponents();
        if (_grabInteractable != null)
        {
            _grabInteractable.selectEntered.AddListener(OnSelectEntered);
            _grabInteractable.selectExited.AddListener(OnSelectExited);
        }
    }

    private void OnDisable()
    {
        if (_grabInteractable != null)
        {
            _grabInteractable.selectEntered.RemoveListener(OnSelectEntered);
            _grabInteractable.selectExited.RemoveListener(OnSelectExited);
        }

        SetIgnoreSiblingCollisions(false);
    }

    /// <summary>
    /// Initializes part metadata and caches its current local transform as the assembled reference pose.
    /// </summary>
    public void ConfigureFromAssembledPose(
        string id,
        MoldPartCategory partCategory,
        string label,
        int index,
        Transform container,
        MoldPartState initialState = MoldPartState.Installed)
    {
        EnsureComponents();
        partId = id;
        category = partCategory;
        displayName = label;
        slotIndex = index;
        moldContainer = container != null ? container : transform.parent;
        assembledLocalPosition = transform.localPosition;
        assembledLocalRotation = transform.localRotation;
        assembledLocalScale = transform.localScale;
        currentState = initialState;
        isLockedByDependency = false;
        ApplyPhysicsForCurrentState();
        RefreshGrabLockState();
    }

    /// <summary>
    /// Locks or unlocks XR grabbing based on mechanical assembly dependencies (e.g., rods lock walls; tightened nuts lock rods).
    /// </summary>
    public void SetDependencyLocked(bool locked)
    {
        isLockedByDependency = locked;
        RefreshGrabLockState();
    }

    /// <summary>
    /// Transitions this part to a new mechanical state and applies jitter-free Rigidbody settings.
    /// </summary>
    public void SetState(MoldPartState newState, bool snapToAssembledPose = false)
    {
        EnsureComponents();
        MoldPartState previous = currentState;
        currentState = newState;

        if (snapToAssembledPose)
        {
            if (moldContainer != null && transform.parent != moldContainer)
                transform.SetParent(moldContainer, worldPositionStays: true);

            transform.localPosition = assembledLocalPosition;
            transform.localRotation = assembledLocalRotation;
            transform.localScale = assembledLocalScale;
        }

        ApplyPhysicsForCurrentState();
        RefreshGrabLockState();

        if (previous != currentState)
        {
            if (!_isCaptiveFollower)
                PlayPartTransitionSound(previous, currentState);
            StateChanged?.Invoke(this, previous, currentState);
        }
    }

    /// <summary>
    /// Snaps this part directly to a specified world position and rotation in Installed or LooseSeated state.
    /// </summary>
    public void SnapToWorldPose(Vector3 worldPos, Quaternion worldRot, MoldPartState targetState)
    {
        EnsureComponents();

        // Cleanly release any active hand grab before locking pose.
        if (_grabInteractable != null && _grabInteractable.isSelected && _grabInteractable.interactionManager != null)
        {
            var selecting = new List<UnityEngine.XR.Interaction.Toolkit.Interactors.IXRSelectInteractor>(_grabInteractable.interactorsSelecting);
            for (int i = selecting.Count - 1; i >= 0; i--)
            {
                if (selecting[i] != null)
                    _grabInteractable.interactionManager.SelectExit(selecting[i], _grabInteractable);
            }
        }

        if (moldContainer != null && transform.parent != moldContainer)
            transform.SetParent(moldContainer, worldPositionStays: true);

        transform.SetPositionAndRotation(worldPos, worldRot);
        transform.localScale = assembledLocalScale;
        SetState(targetState, snapToAssembledPose: false);
    }

    [ContextMenu("Test: Reset To Assembled Pose")]
    public void ResetToAssembledPose()
    {
        SetState(MoldPartState.Installed, snapToAssembledPose: true);
    }

    [ContextMenu("Test: Release To Free State")]
    public void ReleaseToFreeState()
    {
        SetDependencyLocked(false);
        SetState(MoldPartState.Free, snapToAssembledPose: false);
    }

    private void OnSelectEntered(SelectEnterEventArgs args)
    {
        if (CanBeRemovedPredicate != null && !IsFree && !CanBeRemovedPredicate(this))
        {
            // Reject out-of-order grab cleanly.
            if (_grabInteractable != null && _grabInteractable.interactionManager != null && args.interactorObject != null)
                _grabInteractable.interactionManager.SelectExit(args.interactorObject, _grabInteractable);
            return;
        }

        _groundRestTimer = 0f;

        // Ignore sibling mold collisions while held so sliding rods/bolts out of walls never snags or wedges.
        SetIgnoreSiblingCollisions(true);

        if (currentState != MoldPartState.Free)
        {
            MoldPartState prev = currentState;
            currentState = MoldPartState.Free;
            PlayPartTransitionSound(prev, currentState);
            StateChanged?.Invoke(this, prev, currentState);
        }

        if (ShouldLockOriginalRotation)
        {
            ApplyGrabRotationSettings();
            if (_rigidbody != null)
                _rigidbody.constraints = RigidbodyConstraints.FreezeRotation;
            LockToOriginalWorldRotationImmediate();
        }
    }

    private void OnSelectExited(SelectExitEventArgs args)
    {
        if (_grabInteractable != null && _grabInteractable.isSelected)
            return;

        _groundRestTimer = 0f;
        SetIgnoreSiblingCollisions(false);

        if (moldContainer != null && transform.parent != moldContainer)
        {
            transform.SetParent(moldContainer, worldPositionStays: true);
            transform.localScale = assembledLocalScale;
        }

        // Allow snap-point / thread placement handlers (Phases 5-7) to claim this part on release.
        if (TrySnapOnReleaseCallback != null && TrySnapOnReleaseCallback(this))
            return;

        // Remain in Free state with clean non-kinematic physics.
        ApplyPhysicsForCurrentState();
    }

    private bool ShouldLockOriginalRotation =>
        keepOriginalRotation || category == MoldPartCategory.LongWall || category == MoldPartCategory.ShortWall;

    private void LateUpdate()
    {
        if (_isCaptiveFollower)
            return;

        if (ShouldLockOriginalRotation && (IsHeld || currentState == MoldPartState.Free))
            LockToOriginalWorldRotationImmediate();

        if (category == MoldPartCategory.ConnectingRod && IsHeld)
            PreventTieRodPiercingMoldBody();
    }

    private void FixedUpdate()
    {
        if (_isCaptiveFollower)
            return;

        if (ShouldLockOriginalRotation && (IsHeld || currentState == MoldPartState.Free))
            LockToOriginalWorldRotationImmediate();

        if (category == MoldPartCategory.ConnectingRod && (IsHeld || (currentState == MoldPartState.Free && transform.position.y > 0.18f)))
            PreventTieRodPiercingMoldBody();

        if (currentState != MoldPartState.Free || IsHeld || _rigidbody == null)
            return;

        Vector3 pos = transform.position;
        Vector3 vel = _rigidbody.linearVelocity;
        bool modified = false;

        // 1. Below-floor tunnel rescue: only rescue if the collider bottom genuinely tunnels below the y = 0 floor.
        //    (Previously clamping pos.y < 0.025f bounced horizontal 1.35cm-radius rods resting at y = 0.0135m!)
        float bottomY = _primaryCollider != null ? _primaryCollider.bounds.min.y : pos.y;
        if (bottomY < -0.025f)
        {
            pos.y += (-bottomY + 0.002f);
            if (vel.y < 0f)
                vel.y = 0f;
            modified = true;
            bottomY = 0.002f;
        }

        // 2. Keep small fasteners and vertical bolts out from underneath the wooden PalletBase so zero parts hide out of sight.
        if (category == MoldPartCategory.Fastener || category == MoldPartCategory.VerticalBolt)
        {
            if (TryGetPalletWorldBounds(out Bounds palletB) && pos.y < palletB.max.y + 0.03f)
            {
                float pad = 0.05f;
                float minX = palletB.min.x - pad;
                float maxX = palletB.max.x + pad;
                float minZ = palletB.min.z - pad;
                float maxZ = palletB.max.z + pad;

                if (pos.x > minX && pos.x < maxX && pos.z > minZ && pos.z < maxZ)
                {
                    float dxLeft = Mathf.Abs(pos.x - minX);
                    float dxRight = Mathf.Abs(maxX - pos.x);
                    float dzBack = Mathf.Abs(pos.z - minZ);
                    float dzFront = Mathf.Abs(maxZ - pos.z);
                    float minEdge = Mathf.Min(Mathf.Min(dxLeft, dxRight), Mathf.Min(dzBack, dzFront));

                    const float clearMargin = 0.09f;
                    if (minEdge == dxLeft) pos.x = minX - clearMargin;
                    else if (minEdge == dxRight) pos.x = maxX + clearMargin;
                    else if (minEdge == dzBack) pos.z = minZ - clearMargin;
                    else pos.z = maxZ + clearMargin;

                    vel.x = 0f;
                    vel.z = 0f;
                    modified = true;
                }
            }
        }

        // 3. Workstation reach leash: prevent dropped/bumped parts from ever rolling far away from the mold station.
        Vector3 anchor = AssembledWorldPosition;
        Vector2 horizOffset = new Vector2(pos.x - anchor.x, pos.z - anchor.z);
        const float maxHorizRadius = 2.25f;
        if (horizOffset.sqrMagnitude > maxHorizRadius * maxHorizRadius)
        {
            Vector2 clamped = horizOffset.normalized * maxHorizRadius;
            pos.x = anchor.x + clamped.x;
            pos.z = anchor.z + clamped.y;
            vel.x = 0f;
            vel.z = 0f;
            modified = true;
        }

        if (modified)
        {
            _groundRestTimer = 0f;
            transform.position = pos;
            _rigidbody.position = pos;
            _rigidbody.linearVelocity = vel;
            return;
        }

        // 4. Ground rest stabilization: damp & sleep free rods/bolts/nuts once resting on the floor or another part so they never vibrate or micro-roll.
        if (bottomY <= 0.25f && !_rigidbody.IsSleeping())
        {
            Vector3 angVel = _rigidbody.angularVelocity;
            if (vel.sqrMagnitude < 0.045f && angVel.sqrMagnitude < 0.25f)
            {
                _rigidbody.linearVelocity = vel * 0.60f;
                _rigidbody.angularVelocity = angVel * 0.50f;
                _groundRestTimer += Time.fixedDeltaTime;
                if (_groundRestTimer >= 0.16f)
                {
                    _rigidbody.linearVelocity = Vector3.zero;
                    _rigidbody.angularVelocity = Vector3.zero;
                    _rigidbody.Sleep();
                }
            }
            else
            {
                _groundRestTimer = 0f;
            }
        }
        else if (bottomY > 0.25f)
        {
            _groundRestTimer = 0f;
        }
    }

    private bool TryGetPalletWorldBounds(out Bounds bounds)
    {
        if (_hasCachedPalletBounds)
        {
            bounds = _cachedPalletWorldBounds;
            return true;
        }

        Transform root = moldContainer != null ? moldContainer : transform.parent;
        if (root != null)
        {
            Transform pallet = root.Find("Cube.004");
            if (pallet != null)
            {
                Renderer r = pallet.GetComponent<Renderer>();
                if (r != null)
                {
                    _cachedPalletWorldBounds = r.bounds;
                    _hasCachedPalletBounds = true;
                    bounds = _cachedPalletWorldBounds;
                    return true;
                }
            }
        }

        bounds = default;
        return false;
    }

    private void ApplyPhysicsForCurrentState()
    {
        if (_rigidbody == null)
            return;

        if (_isCaptiveFollower || currentState == MoldPartState.Installed || currentState == MoldPartState.LooseSeated)
        {
            if (!_rigidbody.isKinematic)
            {
                _rigidbody.linearVelocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            _rigidbody.constraints = RigidbodyConstraints.None;
            _rigidbody.useGravity = false;
            _rigidbody.isKinematic = true;
        }
        else // Free
        {
            _groundRestTimer = 0f;

            if (category == MoldPartCategory.ConnectingRod)
            {
                _rigidbody.linearDamping = Mathf.Max(_rigidbody.linearDamping, 3.0f);
                _rigidbody.angularDamping = Mathf.Max(_rigidbody.angularDamping, 8.0f);
                _rigidbody.sleepThreshold = 0.02f;
                _rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            }
            else if (category == MoldPartCategory.Fastener || category == MoldPartCategory.VerticalBolt)
            {
                _rigidbody.linearDamping = Mathf.Max(_rigidbody.linearDamping, 2.0f);
                _rigidbody.angularDamping = Mathf.Max(_rigidbody.angularDamping, 4.5f);
                _rigidbody.sleepThreshold = 0.015f;
                _rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            }
            else
            {
                if (_rigidbody.linearDamping < 1.2f)
                    _rigidbody.linearDamping = 1.4f;
                if (_rigidbody.angularDamping < 1.8f)
                    _rigidbody.angularDamping = 2.0f;
            }

            if (ShouldLockOriginalRotation)
            {
                _rigidbody.constraints = RigidbodyConstraints.FreezeRotation;
                LockToOriginalWorldRotationImmediate();
            }
            else
            {
                _rigidbody.constraints = RigidbodyConstraints.None;
            }

            if (!IsHeld)
            {
                _rigidbody.isKinematic = false;
                _rigidbody.useGravity = true;
                _rigidbody.linearVelocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
        }
    }

    /// <summary>
    /// Plays a spatialized sound effect whenever this part is removed from the mold or placed back into position.
    /// </summary>
    private void PlayPartTransitionSound(MoldPartState previous, MoldPartState current)
    {
        if (!Application.isPlaying || SuppressStateChangeAudio)
            return;

        bool isRemoving = (previous == MoldPartState.Installed || previous == MoldPartState.LooseSeated)
                          && current == MoldPartState.Free;
        bool isPuttingBack = previous == MoldPartState.Free
                             && (current == MoldPartState.Installed || current == MoldPartState.LooseSeated);

        if (!isRemoving && !isPuttingBack)
            return;

        if (Time.unscaledTime - _lastAudioPlayTime < 0.05f)
            return;
        _lastAudioPlayTime = Time.unscaledTime;

        AudioClip clip = ResolveTransitionClip(isRemoving);
        if (clip == null)
            return;

        EnsurePartAudioSource();
        if (_partAudioSource != null)
        {
            _partAudioSource.pitch = UnityEngine.Random.Range(0.97f, 1.03f);
            _partAudioSource.PlayOneShot(clip, partSoundVolume);
        }
    }

    public AudioClip ResolveTransitionClip(bool isRemoving)
    {
        AudioClip assigned = isRemoving ? removeSoundClip : placeBackSoundClip;
        if (assigned != null)
            return assigned;

        EnsureProceduralClips();
        switch (category)
        {
            case MoldPartCategory.LongWall:
            case MoldPartCategory.ShortWall:
            case MoldPartCategory.FloorPlate:
            case MoldPartCategory.PalletBase:
                return isRemoving ? s_procWallRemove : s_procWallInstall;

            case MoldPartCategory.ConnectingRod:
            case MoldPartCategory.VerticalBolt:
                return isRemoving ? s_procRodRemove : s_procRodInstall;

            case MoldPartCategory.Fastener:
            default:
                return isRemoving ? s_procFastenerRemove : s_procFastenerInstall;
        }
    }

    private void EnsurePartAudioSource()
    {
        if (_partAudioSource != null)
            return;

        _partAudioSource = GetComponent<AudioSource>();
        if (_partAudioSource == null)
            _partAudioSource = gameObject.AddComponent<AudioSource>();

        _partAudioSource.playOnAwake = false;
        _partAudioSource.loop = false;
        _partAudioSource.spatialBlend = 0.65f;
        _partAudioSource.minDistance = 1.0f;
        _partAudioSource.maxDistance = 15.0f;
        _partAudioSource.rolloffMode = AudioRolloffMode.Logarithmic;
    }

    private static void EnsureProceduralClips()
    {
        if (s_procWallRemove != null)
            return;

        const int sr = 22050;

        // Wall Remove: warm low mold-panel unlatch + wooden/steel lift slide
        s_procWallRemove = BuildClip("Proc_Wall_Remove", sr, 0.20f, t =>
        {
            float body = Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(145f, 235f, t / 0.20f) * t) * Mathf.Exp(-t * 22f) * 0.65f;
            float click = Mathf.Sin(2f * Mathf.PI * 520f * t) * Mathf.Exp(-t * 65f) * 0.40f;
            return body + click;
        });

        // Wall Place Back: heavy mold wall thud + positive mechanical lock click
        s_procWallInstall = BuildClip("Proc_Wall_Install", sr, 0.22f, t =>
        {
            float thud = Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(175f, 88f, t / 0.22f) * t) * Mathf.Exp(-t * 20f) * 0.75f;
            float latch = t >= 0.045f
                ? Mathf.Sin(2f * Mathf.PI * 780f * (t - 0.045f)) * Mathf.Exp(-(t - 0.045f) * 75f) * 0.48f
                : 0f;
            return thud + latch;
        });

        // Rod / Bolt Remove: steel shaft slide-out + metallic release ring
        s_procRodRemove = BuildClip("Proc_Rod_Remove", sr, 0.16f, t =>
        {
            float slide = Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(420f, 640f, t / 0.16f) * t) * Mathf.Exp(-t * 32f) * 0.50f;
            float ring = Mathf.Sin(2f * Mathf.PI * 1380f * t) * Mathf.Exp(-t * 48f) * 0.38f;
            return slide + ring;
        });

        // Rod / Bolt Place Back: solid steel channel clack + seating ping
        s_procRodInstall = BuildClip("Proc_Rod_Install", sr, 0.18f, t =>
        {
            float clack = Mathf.Sin(2f * Mathf.PI * 340f * t) * Mathf.Exp(-t * 42f) * 0.62f;
            float ping = t >= 0.025f
                ? (Mathf.Sin(2f * Mathf.PI * 1160f * (t - 0.025f)) * 0.45f + Mathf.Sin(2f * Mathf.PI * 1740f * (t - 0.025f)) * 0.25f) * Mathf.Exp(-(t - 0.025f) * 40f)
                : 0f;
            return clack + ping;
        });

        // Fastener Remove: crisp metallic nut unthread pop + clink
        s_procFastenerRemove = BuildClip("Proc_Fastener_Remove", sr, 0.13f, t =>
        {
            float pop = Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(680f, 480f, t / 0.13f) * t) * Mathf.Exp(-t * 45f) * 0.52f;
            float clink = Mathf.Sin(2f * Mathf.PI * 2150f * t) * Mathf.Exp(-t * 62f) * 0.38f;
            return pop + clink;
        });

        // Fastener Place Back: crisp metallic thread-catch click + confirmation tone
        s_procFastenerInstall = BuildClip("Proc_Fastener_Install", sr, 0.14f, t =>
        {
            float click1 = Mathf.Sin(2f * Mathf.PI * 1420f * t) * Mathf.Exp(-t * 70f) * 0.55f;
            float click2 = t >= 0.03f
                ? Mathf.Sin(2f * Mathf.PI * 1960f * (t - 0.03f)) * Mathf.Exp(-(t - 0.03f) * 58f) * 0.48f
                : 0f;
            return click1 + click2;
        });
    }

    private static AudioClip BuildClip(string clipName, int sampleRate, float durationSec, Func<float, float> synth)
    {
        int samples = Mathf.Max(64, Mathf.RoundToInt(sampleRate * durationSec));
        float[] data = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / sampleRate;
            data[i] = Mathf.Clamp(synth(t), -1f, 1f);
        }
        AudioClip clip = AudioClip.Create(clipName, samples, 1, sampleRate, false);
        clip.hideFlags = HideFlags.DontSave;
        clip.SetData(data, 0);
        return clip;
    }

    private void RefreshGrabLockState()
    {
        if (_grabInteractable == null)
            return;

        bool allowGrab = !_isCaptiveFollower && (!isLockedByDependency || currentState == MoldPartState.Free);
        if (_grabInteractable.enabled != allowGrab)
            _grabInteractable.enabled = allowGrab;
    }

    private void EnsureComponents()
    {
        if (_rigidbody == null)
            _rigidbody = GetComponent<Rigidbody>();

        if (_grabInteractable == null)
            _grabInteractable = GetComponent<XRGrabInteractable>();

        if (_grabInteractable != null)
        {
            // Never allow XRI 3.5 predictedVisualsTransform to hijack a detached nut or child washer transform!
            if (_grabInteractable.predictedVisualsTransform != null)
                _grabInteractable.predictedVisualsTransform = null;

            ApplyGrabRotationSettings();
        }

        if (_primaryCollider == null)
            _primaryCollider = GetComponent<Collider>();

        if (moldContainer == null && transform.parent != null)
            moldContainer = transform.parent;
    }

    private void ApplyGrabRotationSettings()
    {
        if (_grabInteractable == null)
            return;

        if (ShouldLockOriginalRotation)
            _grabInteractable.trackRotation = false;
    }

    private void LockToOriginalWorldRotationImmediate()
    {
        Quaternion origRot = AssembledWorldRotation;
        if (Quaternion.Angle(transform.rotation, origRot) > 0.02f)
            transform.rotation = origRot;

        if (_rigidbody != null && !_rigidbody.isKinematic)
        {
            _rigidbody.rotation = origRot;
            _rigidbody.angularVelocity = Vector3.zero;
        }
    }

    private void PreventTieRodPiercingMoldBody()
    {
        Transform root = moldContainer != null ? moldContainer : transform.parent;
        if (root == null)
            return;

        // Only guard against piercing when at least one mold wall is installed on the base
        Transform frontWall = root.Find("Cube.002");
        Transform backWall = root.Find("Cube.008");
        MoldPart fwPart = frontWall != null ? frontWall.GetComponent<MoldPart>() : null;
        MoldPart bwPart = backWall != null ? backWall.GetComponent<MoldPart>() : null;
        bool wallsPresent = (fwPart != null && fwPart.IsInstalled) || (bwPart != null && bwPart.IsInstalled);
        if (!wallsPresent)
            return;

        Vector3 center = MoldSnapPoint.GetPartWorldGeometricCenter(this);

        // Mold solid body interior bounds (keeping external Front z=-6.798 and Back z=-7.4325 channels 100% open)
        const float minX = -0.445f;
        const float maxX = 0.855f;
        const float minY = 0.205f;
        const float maxY = 0.878f;
        const float minZ = -7.416f;
        const float maxZ = -6.817f;

        if (center.x > minX && center.x < maxX &&
            center.y > minY && center.y < maxY &&
            center.z > minZ && center.z < maxZ)
        {
            float dBack = Mathf.Abs(center.z - minZ);
            float dFront = Mathf.Abs(maxZ - center.z);
            float dTop = Mathf.Abs(maxY - center.y);

            Vector3 shift = Vector3.zero;
            if (dTop <= dBack && dTop <= dFront)
                shift.y = (maxY + 0.008f) - center.y;
            else if (dBack <= dFront)
                shift.z = (minZ - 0.006f) - center.z;
            else
                shift.z = (maxZ + 0.006f) - center.z;

            transform.position += shift;
            if (_rigidbody != null && !_rigidbody.isKinematic)
                _rigidbody.position = transform.position;
        }
    }

    private void SetIgnoreSiblingCollisions(bool ignore)
    {
        if (_primaryCollider == null)
            return;

        if (ignore)
        {
            _siblingMoldColliders.Clear();
            Transform root = moldContainer != null ? moldContainer : transform.parent;
            if (root != null)
            {
                Collider[] cols = root.GetComponentsInChildren<Collider>(includeInactive: true);
                for (int i = 0; i < cols.Length; i++)
                {
                    Collider c = cols[i];
                    if (c == null || c == _primaryCollider || c.isTrigger)
                        continue;

                    _siblingMoldColliders.Add(c);
                    if (CanAdjustIgnoreCollision(_primaryCollider, c))
                        Physics.IgnoreCollision(_primaryCollider, c, true);
                }
            }
            _ignoringSiblingCollisions = true;
        }
        else if (_ignoringSiblingCollisions)
        {
            for (int i = 0; i < _siblingMoldColliders.Count; i++)
            {
                if (CanAdjustIgnoreCollision(_primaryCollider, _siblingMoldColliders[i]))
                    Physics.IgnoreCollision(_primaryCollider, _siblingMoldColliders[i], false);
            }
            _siblingMoldColliders.Clear();
            _ignoringSiblingCollisions = false;
        }
    }

    private static bool CanAdjustIgnoreCollision(Collider a, Collider b)
    {
        return Application.isPlaying &&
               a != null && b != null && a != b &&
               a.enabled && b.enabled &&
               a.gameObject.activeInHierarchy && b.gameObject.activeInHierarchy &&
               a.gameObject.scene.IsValid() && b.gameObject.scene.IsValid();
    }
}
