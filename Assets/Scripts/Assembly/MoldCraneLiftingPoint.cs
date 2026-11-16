using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Filtering;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using NOT_Lonely;

namespace Sefpro.Assembly
{
    /// <summary>
    /// Removable two-leg chain sling for the MoldCasting assembly.
    ///
    /// The sling is made of the central lifting Ring plus two U-shaped chains (Chain_1_U / Chain_2_U, 81 links each).
    /// Each chain starts and ends on the Ring; the bottom bend (bight) of each chain is an XR grab handle
    /// (LoopHandle_Chain_1_U / LoopHandle_Chain_2_U).
    ///
    /// - The Ring can be grabbed, carried, dropped, and hung on the crane hook (proximity capture via NL_AttachCargo).
    ///   Grabbing the Ring while it is on the hook takes it off the hook (only while the mould is resting, not while lifted).
    /// - Each chain bight can be grabbed and snapped into its XRSocketInteractor on the mould's pallet end ledge
    ///   (ChainSocket_BottomEdge_Left / ChainSocket_BottomEdge_Right). Pulling the bight out of the socket frees the chain.
    /// - When the Ring is on the hook and BOTH chain ends are socketed, raising the hook tightens the chains and lifts the mould.
    /// - When nothing holds it, the Ring is a real Rigidbody (a hoop of capsule colliders): it falls, and hangs on any collider
    ///   that passes through its hole (a hook, a peg, a bar on a stand). Dropped over the open mould it parks on the mould top.
    /// - Each chain is a rope simulation (one Verlet particle per link) pinned to the Ring and, when socketed or held, at its
    ///   bight. Loose chain swings, drapes and collides with every collider on the chain collision layers.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)]
    public class MoldCraneLiftingPoint : MonoBehaviour
    {
        [Header("Mould & Chain References")]
        [Tooltip("Root transform of the MoldCasting assembly.")]
        public Transform moldRoot;

        [Tooltip("MoldCast transform holding MoldAssemblyManager and the individual MoldPart components.")]
        public Transform moldCastTransform;

        [Tooltip("MoldAssemblyManager managing mould dismantling and reassembly.")]
        public MoldAssemblyManager assemblyManager;

        [Tooltip("Pallet base transform (Cube.004). The chain sockets sit on its end ledges.")]
        public Transform palletBaseTransform;

        [Tooltip("Root transform of the Chains object.")]
        public Transform chainsRoot;

        [Tooltip("Central lifting ring (Chains/Ring).")]
        public Transform centralRing;

        [Tooltip("Chain_1_U (81 links) - goes to the LEFT end of the mould.")]
        public Transform chain1Root;

        [Tooltip("Chain_2_U (81 links) - goes to the RIGHT end of the mould.")]
        public Transform chain2Root;

        [Header("XR Sockets & Grab Handles")]
        [Tooltip("Socket on the LEFT pallet end ledge (accepts LoopHandle_Chain_1_U).")]
        public XRSocketInteractor leftBottomEdgeSocket;

        [Tooltip("Socket on the RIGHT pallet end ledge (accepts LoopHandle_Chain_2_U).")]
        public XRSocketInteractor rightBottomEdgeSocket;

        [Tooltip("Grab handle at the bottom bend of Chain_1_U.")]
        public XRGrabInteractable chain1LoopGrab;

        [Tooltip("Grab handle at the bottom bend of Chain_2_U.")]
        public XRGrabInteractable chain2LoopGrab;

        [Tooltip("Grab interactable on the central lifting Ring.")]
        public XRGrabInteractable centralRingGrab;

        [Tooltip("Not used for positioning (kept switched off so it never fights the hook seating). Optional.")]
        public XRSocketInteractor hookRingSocket;

        [Header("Mould Geometry (MoldCasting local space, metres)")]
        [Tooltip("X distance from the mould centre to the plane the chain hangs in at each end (just outside the pallet ledge and nuts).")]
        public float ledgeHalfSpanX = 0.745f;

        [Tooltip("Height of the chain socket / bight above the mould root (middle of the pallet end ledge).")]
        public float ledgeHeight = 0.11f;

        [Tooltip("Half length of the chain bight along the pallet end ledge when socketed.")]
        public float socketedBightHalfLength = 0.33f;

        [Tooltip("Half length of the chain bight when held or hanging free.")]
        public float looseBightHalfLength = 0.06f;

        [Tooltip("Height of the mould top edge the chain legs pass over (wall top + clearance).")]
        public float topEdgeHeight = 0.90f;

        [Tooltip("Z offset of the points where the legs pass over the top edge of the end walls.")]
        public float topEdgeHalfZ = 0.30f;

        [Tooltip("Half extents (X, Z) of the solid mould body used to keep chain links out of the mould.")]
        public Vector2 bodyHalfExtentsXZ = new Vector2(0.72f, 0.36f);

        [Tooltip("Height of the mould top above the mould root.")]
        public float bodyTopHeight = 0.885f;

        [Header("Chain")]
        [Tooltip("Distance between neighbouring link centres (world metres).")]
        public float linkPitch = 0.0479f;

        [Tooltip("Local attachment offsets on the Ring for the 4 legs (Chain_1 front, Chain_1 back, Chain_2 front, Chain_2 back).")]
        public Vector3[] ringLegLocalOffsets = new Vector3[4]
        {
            new Vector3(-0.088f, -0.025f, -0.095f),
            new Vector3(-0.088f,  0.025f, -0.105f),
            new Vector3( 0.088f, -0.025f, -0.095f),
            new Vector3( 0.088f,  0.025f, -0.105f)
        };

        [Tooltip("Height of the Ring centre above a surface it rests on (upright ring radius).")]
        public float ringRestHeight = 0.095f;

        [Tooltip("How far (m) a hand may pull the Ring or a chain end past the chain's reach before it slips out of the hand.")]
        public float overstretchReleaseDistance = 0.15f;

        [Tooltip("Turn off the 160 per-link BoxColliders at runtime. Ignored while 'Grab Chain Anywhere' is on (the links are the grab colliders then).")]
        public bool disableLinkCollidersAtRuntime = true;

        [Tooltip("Let the player pick a chain up by any link along its sides, not only by the bottom loop. The grabbed link follows the hand; " +
                 "bringing it to a ChainSnapPoint still snaps the chain's bottom loop onto it.")]
        public bool grabChainAnywhere = true;

        [Tooltip("Links next to the Ring at each chain end that stay ungrabbable (so the Ring stays easy to grab on its own).")]
        [Min(0)] public int ungrabbableLinksNearRing = 4;

        [Tooltip("When a chain loop is snapped onto the mould and the Ring is loose (not on the crane hook, not in a hand), set the Ring on the " +
                 "middle of the mould top, so the sling sits on the mould in its natural shape instead of being dragged across the floor.")]
        public bool parkRingOnMouldWhenSnapped = true;

        [Header("Physics (Ring body and rope simulation)")]
        [Tooltip("Mass of the Ring Rigidbody when it is free (falls, hangs on hooks, pegs and bars through its hole).")]
        public float ringMass = 2f;

        [Tooltip("A free Ring dropped over the mould rests on the mould top instead of falling into the open cavity.")]
        public bool ringRestsOnMouldTop = true;

        [Tooltip("Mass of EACH chain (kg). Its hanging part pulls on the free Ring (e.g. one hung on a stand hook), and its links push loose rigidbodies they land on. 8 mm lifting chain is about 1.4 kg/m, so 3.83 m is about 5.5 kg.")]
        public float chainMassKg = 5.5f;

        [Tooltip("Collision radius of each simulated chain link (m).")]
        public float chainParticleRadius = 0.014f;

        [Tooltip("Velocity kept per simulation step (1 = no damping). Lower settles the swinging chain faster.")]
        [Range(0.9f, 1f)]
        public float chainDamping = 0.985f;

        [Tooltip("Simulation sub-steps per frame.")]
        [Range(1, 6)]
        public int chainSubsteps = 2;

        [Tooltip("Distance-constraint passes per sub-step (higher = less stretchy chain).")]
        [Range(4, 40)]
        public int chainIterations = 14;

        [Tooltip("Chain links collide with every non-trigger collider on these layers (floor, stands, mould parts...).")]
        public LayerMask chainCollisionLayers = Physics.DefaultRaycastLayers;

        [Tooltip("Share of a link's sliding speed removed on contact (0 = ice, 1 = sticks).")]
        [Range(0f, 1f)]
        public float chainContactFriction = 0.6f;

        [Header("Crane Hook Seating & Capture")]
        [Tooltip("Offset from WireHookMount origin down to the C-hook saddle centre.")]
        public float hookSaddleOffset = 0.2984f;

        [Tooltip("Offset from the Ring centre to the upper inner rim where the hook saddle seats (ring local).")]
        public Vector3 ringSeatLocalOffset = new Vector3(0f, 0f, 0.115f);

        [Tooltip("Automatically hang the Ring on the hook when the hook saddle and the Ring come close (hook lowered onto it, or Ring carried to it).")]
        public bool autoConnect = true;

        [Tooltip("Distance (m) between hook saddle and Ring seat at which the Ring is hung on the hook.")]
        public float autoConnectRadius = 0.30f;

        [Tooltip("After the Ring is taken off the hook, capture re-arms once the hook is this far (m) from the Ring.")]
        public float autoConnectRearmDistance = 0.58f;

        [Tooltip("Minimum floor Y.")]
        public float minGroundY = 0.0f;

        [Header("Live State (Read Only)")]
        [SerializeField] private bool isAttached = false;
        [SerializeField] private bool isHookInRange = false;
        [SerializeField] private bool isLeftLoopSocketed = false;
        [SerializeField] private bool isRightLoopSocketed = false;
        [SerializeField] private bool isRingHeld = false;
        [SerializeField] private bool isChainTaut = false;
        [SerializeField] private bool isMoldSuspended = false;
        [SerializeField] private float currentTautness = 0f;
        [SerializeField] private float currentClearanceAboveSupport = 0f;
        [SerializeField] private Transform currentHookMount;
        [SerializeField] private Rigidbody currentHookRb;

        private static readonly Quaternion UprightRingRot = Quaternion.Euler(-90f, 0f, 0f);
        private const int PolylineSamplesPerFreeSegment = 20;

        private sealed class ChainEnd
        {
            public string label;
            public float side;                 // -1 left end of the mould, +1 right end
            public int frontOffsetIndex;
            public int backOffsetIndex;
            public Transform root;
            public readonly List<Transform> links = new List<Transform>(81);
            public XRGrabInteractable grab;
            public XRSocketInteractor socket;
            public ChainSnapPoint homeSnap;              // snap point on 'socket' (used for the edit-mode rest pose)
            public XRSocketInteractor cooldownSocket;    // socket switched off after the chain was torn out of it
            public float socketCooldownUntil;
            public int grabLinkIndex = -1;               // link held in the hand (-1 = the bottom loop)
            public bool snapOnReleasePending;            // the hand let go of the chain this frame
            public bool wasSocketed;
            public bool resnapBlocked;                   // just pulled off a snap point: must leave its zone before it can snap again
            public Vector3 lastHeldPos;
            public Vector3 lastHorizontalDir;
            public BoxCollider handleBox;

            // Verlet rope state, one particle per link.
            public Vector3[] pos;
            public Vector3[] prev;
            public bool[] pinned;
            public bool[] contact;       // resting on / pressed against something this frame
            public bool simReady;
            public Vector3 loadFront;    // weight of the hanging front leg, pulling on the Ring (N)
            public Vector3 loadBack;
        }

        private readonly ChainEnd _end1 = new ChainEnd { label = "Chain_1_U (left)", side = -1f, frontOffsetIndex = 0, backOffsetIndex = 1 };
        private readonly ChainEnd _end2 = new ChainEnd { label = "Chain_2_U (right)", side = 1f, frontOffsetIndex = 2, backOffsetIndex = 3 };

        private readonly List<ChainSnapPoint> _snapPoints = new List<ChainSnapPoint>();
        private int _tautSnapKey;
        private XRGrabInteractable _forceAcceptGrab;

        private readonly List<Vector3> _polyline = new List<Vector3>(256);
        private readonly List<float> _cumulative = new List<float>(256);
        private Vector3[] _linkPositions = new Vector3[81];
        private readonly RaycastHit[] _hits = new RaycastHit[32];

        private Collider[] _moldAndSlingColliders;
        private Collider[] _hookColliders;
        private NL_AttachCargo _cachedAttachCargo;
        private Transform _lastHookMount;
        private bool _autoConnectArmed = true;
        private float _lastDetachTime = -10f;
        private Rigidbody _ringRb;
        private bool _ringResting;                 // parked (kinematic) on the mould top
        private float _unparkedAt = -10f;
        private Vector3 _ringRestLocalPos;         // in mould-root space, so it rides along when the mould moves
        private Quaternion _ringRestLocalRot;
        private SphereCollider _probe;             // used only for Physics.ComputePenetration of chain particles
        private readonly Collider[] _overlap = new Collider[16];
        private readonly HashSet<Collider> _ownColliders = new HashSet<Collider>();
        private float _tautRingLocalY = 1.1f;
        private float _simStepDt = 0.01f;
        private bool _prevLeftSocketed, _prevRightSocketed, _prevSuspended, _eventsPrimed;
        private bool _initialised;
        private readonly Dictionary<Rigidbody, RigidbodyInterpolation> _suspendedInterpolation = new Dictionary<Rigidbody, RigidbodyInterpolation>();

        // Hands whose grab on the Ring / a chain end was taken away by the sling (Ring hung on the hook, chain over-stretched).
        // The rig's interactors use the "State" select trigger, so a hand still squeezing grip would grab straight back;
        // it is ignored until it releases grip once.
        private readonly HashSet<IXRSelectInteractor> _handsBlockedUntilRelease = new HashSet<IXRSelectInteractor>();
        private Predicate<IXRSelectInteractor> _handReleasedPredicate;

        // ------------------------------------------------------------------ public API (used by NL_AttachCargo)

        public bool IsAttached => isAttached;
        public bool IsHookInRange => isHookInRange;
        public bool IsLeftLoopSocketed => IsSocketed(_end1);
        public bool IsRightLoopSocketed => IsSocketed(_end2);
        public bool AreBothLoopsSocketed => IsLeftLoopSocketed && IsRightLoopSocketed;
        public bool IsRingHeld => isRingHeld;
        public bool IsChainTaut => isChainTaut;
        public bool IsMoldSuspended => isMoldSuspended;
        public float CurrentTautness => currentTautness;
        public float CurrentClearanceAboveSupport => currentClearanceAboveSupport;
        public Transform CurrentHookMount => currentHookMount;
        public int ActiveLegCount => (IsLeftLoopSocketed ? 2 : 0) + (IsRightLoopSocketed ? 2 : 0);

        // ------------------------------------------------------------------ events (sound, haptics, UI) - see ChainSlingAudio

        /// <summary>A chain link hit a surface: world position, impact speed (m/s).</summary>
        public event Action<Vector3, float> LinkImpact;
        /// <summary>The free Ring hit something: contact point, impact speed (m/s).</summary>
        public event Action<Vector3, float> RingImpact;
        /// <summary>A chain end entered (true) or left (false) its mould socket: chain index (0 = left, 1 = right), socket position.</summary>
        public event Action<int, bool, Vector3> ChainEndSocketChanged;
        /// <summary>The Ring was hung on (true) or taken off (false) the crane hook.</summary>
        public event Action<bool, Vector3> RingHookChanged;
        /// <summary>The mould left the ground (true) or was set down (false).</summary>
        public event Action<bool, Vector3> MouldLiftChanged;
        /// <summary>A hand grabbed the Ring or a chain end: world position.</summary>
        public event Action<Vector3> SlingGrabbed;

        /// <summary>Mean speed (m/s) of the free-moving chain links this frame - drives the rattle loop.</summary>
        public float ChainMotionSpeed { get; private set; }
        /// <summary>Centre of both chains this frame.</summary>
        public Vector3 ChainCentroid { get; private set; }
        /// <summary>Total pull (N) of the hanging chain legs on the Ring this frame.</summary>
        public float ChainLoadOnRing => (_end1.loadFront + _end1.loadBack + _end2.loadFront + _end2.loadBack).magnitude;

        [Header("Events")]
        [Tooltip("Link contacts slower than this (m/s) raise no LinkImpact event.")]
        public float linkImpactMinSpeed = 0.5f;

        /// <summary>World position on the Ring's upper inner rim where the crane hook saddle seats.</summary>
        public Vector3 RingSeatWorldPosition => centralRing != null ? centralRing.TransformPoint(ringSeatLocalOffset) : transform.position;

        /// <summary>World position of the crane hook's load-bearing saddle point.</summary>
        public Vector3 GetHookSaddleWorld(Transform hookMount)
        {
            if (hookMount == null)
                return RingSeatWorldPosition;
            return hookMount.position - hookMount.up * hookSaddleOffset;
        }

        // ------------------------------------------------------------------ lifecycle

        private void Awake()
        {
            Initialise();
        }

        private void OnEnable()
        {
            if (Application.isPlaying)
                Application.onBeforeRender += OnBeforeRenderPoseLinks;
        }

        private void OnDisable()
        {
            Application.onBeforeRender -= OnBeforeRenderPoseLinks;
        }

        private void Start()
        {
            if (!Application.isPlaying)
                return;

            // The chain ends start unsocketed (see ConfigureEnd); the player sockets them on the mould by hand.
            if (hookRingSocket != null)
                hookRingSocket.socketActive = false;

            InitChainSim(_end1);
            InitChainSim(_end2);
        }

        private void FixedUpdate()
        {
            if (!Application.isPlaying || _ringRb == null || moldRoot == null)
                return;
            UpdateFreeRingPhysics();
            if (!isAttached && !_ringResting && !IsHeldByHand(centralRingGrab))
                ApplyChainWeightToRing();
        }

        private void OnDestroy()
        {
            if (isAttached && Application.isPlaying)
                DetachFromHook();
            UnregisterGrabEvents();
            if (_probe != null)
                Destroy(_probe.gameObject);
        }

        private void Initialise()
        {
            if (_initialised)
                return;

            ResolveReferences();
            CacheChainLinks();
            if (Application.isPlaying)
            {
                ConfigureInteractables();
                ConfigureRingBody();
                ConfigureCollisions();
                RegisterGrabEvents();
            }
            _tautRingLocalY = SolveTautRingLocalHeight();
            _initialised = true;
        }

        public void ResolveReferences()
        {
            if (chainsRoot == null)
            {
                if (transform.name == "Ring" && transform.parent != null)
                    chainsRoot = transform.parent;
                else
                {
                    var go = GameObject.Find("Chains");
                    if (go != null) chainsRoot = go.transform;
                }
            }

            if (centralRing == null && chainsRoot != null)
                centralRing = chainsRoot.Find("Ring");
            if (centralRing == null)
                centralRing = transform;

            if (chain1Root == null && chainsRoot != null) chain1Root = chainsRoot.Find("Chain_1_U");
            if (chain2Root == null && chainsRoot != null) chain2Root = chainsRoot.Find("Chain_2_U");

            if (moldRoot == null)
            {
                var mc = GameObject.Find("MoldCasting");
                if (mc != null) moldRoot = mc.transform;
            }
            if (moldCastTransform == null && moldRoot != null)
                moldCastTransform = moldRoot.Find("MoldCast");
            if (assemblyManager == null && moldCastTransform != null)
                assemblyManager = moldCastTransform.GetComponent<MoldAssemblyManager>();
            if (palletBaseTransform == null && moldCastTransform != null)
                palletBaseTransform = moldCastTransform.Find("Cube.004");

            if (moldCastTransform != null)
            {
                if (leftBottomEdgeSocket == null)
                {
                    var ls = moldCastTransform.Find("Cube.004/ChainSocket_BottomEdge_Left") ?? moldCastTransform.Find("ChainSocket_BottomEdge_Left");
                    if (ls != null) leftBottomEdgeSocket = ls.GetComponent<XRSocketInteractor>();
                }
                if (rightBottomEdgeSocket == null)
                {
                    var rs = moldCastTransform.Find("Cube.004/ChainSocket_BottomEdge_Right") ?? moldCastTransform.Find("ChainSocket_BottomEdge_Right");
                    if (rs != null) rightBottomEdgeSocket = rs.GetComponent<XRSocketInteractor>();
                }
            }

            if (chainsRoot != null)
            {
                if (chain1LoopGrab == null)
                {
                    var h1 = chainsRoot.Find("LoopHandle_Chain_1_U");
                    if (h1 != null) chain1LoopGrab = h1.GetComponent<XRGrabInteractable>();
                }
                if (chain2LoopGrab == null)
                {
                    var h2 = chainsRoot.Find("LoopHandle_Chain_2_U");
                    if (h2 != null) chain2LoopGrab = h2.GetComponent<XRGrabInteractable>();
                }
            }

            if (centralRingGrab == null && centralRing != null)
                centralRingGrab = centralRing.GetComponent<XRGrabInteractable>();

            _end1.root = chain1Root; _end1.grab = chain1LoopGrab; _end1.socket = leftBottomEdgeSocket;
            _end2.root = chain2Root; _end2.grab = chain2LoopGrab; _end2.socket = rightBottomEdgeSocket;
            _end1.handleBox = chain1LoopGrab != null ? chain1LoopGrab.GetComponent<BoxCollider>() : null;
            _end2.handleBox = chain2LoopGrab != null ? chain2LoopGrab.GetComponent<BoxCollider>() : null;

            CollectSnapPoints();
        }

        /// <summary>
        /// Finds every <see cref="ChainSnapPoint"/> under the mould. The two original pallet-ledge sockets get one added
        /// (matching the legacy geometry fields) if they do not have it yet, so older scenes keep working.
        /// </summary>
        private void CollectSnapPoints()
        {
            _end1.homeSnap = EnsureSnapPoint(leftBottomEdgeSocket);
            _end2.homeSnap = EnsureSnapPoint(rightBottomEdgeSocket);

            _snapPoints.Clear();
            if (moldRoot != null)
                _snapPoints.AddRange(moldRoot.GetComponentsInChildren<ChainSnapPoint>(true));
            if (_end1.homeSnap != null && !_snapPoints.Contains(_end1.homeSnap)) _snapPoints.Add(_end1.homeSnap);
            if (_end2.homeSnap != null && !_snapPoints.Contains(_end2.homeSnap)) _snapPoints.Add(_end2.homeSnap);
        }

        private ChainSnapPoint EnsureSnapPoint(XRSocketInteractor socket)
        {
            if (socket == null)
                return null;
            var snap = socket.GetComponent<ChainSnapPoint>();
            if (snap != null)
                return snap;

            snap = socket.gameObject.AddComponent<ChainSnapPoint>();
            snap.bightHalfLength = socketedBightHalfLength;
            snap.topEdgeHeight = Mathf.Max(0f, topEdgeHeight - ledgeHeight);
            snap.topEdgeHalfWidth = topEdgeHalfZ;
            var sphere = socket.GetComponent<SphereCollider>();
            if (sphere != null)
                snap.captureRadius = Mathf.Max(snap.captureRadius, sphere.radius * Mathf.Abs(socket.transform.lossyScale.x));
            snap.ApplyCaptureRadius();
            return snap;
        }

        private void CacheChainLinks()
        {
            CollectSortedLinks(_end1.root, _end1.links);
            CollectSortedLinks(_end2.root, _end2.links);
            int max = Mathf.Max(_end1.links.Count, _end2.links.Count);
            if (_linkPositions.Length < max)
                _linkPositions = new Vector3[max];

            if (Application.isPlaying && disableLinkCollidersAtRuntime && !grabChainAnywhere)
            {
                foreach (var l in _end1.links) { var c = l.GetComponent<Collider>(); if (c != null) c.enabled = false; }
                foreach (var l in _end2.links) { var c = l.GetComponent<Collider>(); if (c != null) c.enabled = false; }
            }
        }

        private static void CollectSortedLinks(Transform root, List<Transform> target)
        {
            target.Clear();
            if (root == null) return;
            for (int i = 0; i < root.childCount; i++)
            {
                var c = root.GetChild(i);
                if (c != null && c.name.Contains("_Link_"))
                    target.Add(c);
            }
            target.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        }

        private void ConfigureInteractables()
        {
            if (centralRingGrab != null)
            {
                // Rotation is tracked so the player can turn the Ring to slip its hole over a hook, peg or bar.
                centralRingGrab.trackPosition = true;
                centralRingGrab.trackRotation = true;
                centralRingGrab.throwOnDetach = false;
                centralRingGrab.selectFilters.Add(new XRSelectFilterDelegate((interactor, _) => IsHandAllowed(interactor)));
            }

            ConfigureEnd(_end1);
            ConfigureEnd(_end2);
            ConfigureSnapPoints();
        }

        /// <summary>Each snap point only takes the chain loop(s) it accepts.</summary>
        private void ConfigureSnapPoints()
        {
            for (int i = 0; i < _snapPoints.Count; i++)
            {
                ChainSnapPoint snap = _snapPoints[i];
                XRSocketInteractor socket = snap != null ? snap.Socket : null;
                if (socket == null)
                    continue;

                // Chains start loose: a chain end is only socketed when the player puts it in by hand.
                socket.startingSelectedInteractable = null;
                socket.selectFilters.Clear();
                socket.selectFilters.Add(new XRSelectFilterDelegate((_, interactable) => SnapAccepts(snap, interactable)));
                socket.hoverFilters.Clear();
                socket.hoverFilters.Add(new XRHoverFilterDelegate((_, interactable) => SnapAccepts(snap, interactable)));
            }
        }

        private bool SnapAccepts(ChainSnapPoint snap, IXRInteractable interactable)
        {
            if (snap == null || interactable == null)
                return false;
            if (_end1.grab != null && ReferenceEquals(interactable, _end1.grab))
                return snap.Accepts(0) && IsPlacedByScript(_end1, snap);
            if (_end2.grab != null && ReferenceEquals(interactable, _end2.grab))
                return snap.Accepts(1) && IsPlacedByScript(_end2, snap);
            return false;
        }

        /// <summary>
        /// A socket never picks a chain up by itself: UpdateSnapping decides when a chain goes on (so a loose chain draped
        /// across a socket never snaps in on its own). The socket only keeps what it was given.
        /// </summary>
        private bool IsPlacedByScript(ChainEnd end, ChainSnapPoint snap)
        {
            XRSocketInteractor socket = snap.Socket;
            if (socket != null && socket.hasSelection && ReferenceEquals(socket.firstInteractableSelected, end.grab))
                return true;
            return _forceAcceptGrab != null && ReferenceEquals(_forceAcceptGrab, end.grab);
        }

        /// <summary>
        /// Whether the chain is long enough to lie on this snap point with the Ring where it is. Only a hooked or hand-held
        /// Ring is fixed in place; a free or parked Ring is simply pulled along by the chain, so it never blocks snapping.
        /// </summary>
        private bool CanReachSnap(ChainEnd end, ChainSnapPoint snap)
        {
            if (snap == null || centralRing == null || moldRoot == null)
                return false;

            Vector3 ringPos;
            Quaternion ringRot;
            if (isAttached && currentHookMount != null)
            {
                ringPos = GetHookSaddleWorld(currentHookMount) - Vector3.up * SeatOffsetWorld();
                ringRot = UprightRingRot;
            }
            else if (IsHeldByHand(centralRingGrab))
            {
                ringPos = centralRing.position;
                ringRot = centralRing.rotation;
            }
            else
            {
                return true;
            }

            float legLen = LegLength(snap.bightHalfLength);
            for (int leg = 0; leg < 2; leg++)
            {
                bool front = leg == 0;
                Vector3 attach = ringPos + RingAttachOffsetWorld(front ? end.frontOffsetIndex : end.backOffsetIndex, ringRot);
                Vector3 corner = snap.Corner(front);
                Vector3 edge = snap.Edge(front);
                bool overEdge = snap.routeOverTopEdge &&
                                SegmentHitsBody(moldRoot.InverseTransformPoint(attach), moldRoot.InverseTransformPoint(corner));
                float len = overEdge ? Vector3.Distance(attach, edge) + Vector3.Distance(edge, corner) : Vector3.Distance(attach, corner);
                if (len > legLen * 1.02f)
                    return false;
            }
            return true;
        }

        private void ConfigureEnd(ChainEnd end)
        {
            if (end.grab != null)
            {
                end.grab.trackPosition = true;
                end.grab.trackRotation = false;
                end.grab.throwOnDetach = false;
                // Hands cannot pull a chain end off the mould while the mould is hanging from it.
                end.grab.selectFilters.Add(new XRSelectFilterDelegate((interactor, _) => IsHandAllowed(interactor)));
                var handle = end.grab;
                end.grab.selectEntered.AddListener(args =>
                {
                    if (IsSocketInteractor(args.interactorObject))
                        return;
                    end.grabLinkIndex = FindGrabbedLink(end, args.interactorObject);
                    SlingGrabbed?.Invoke(handle.transform.position);
                });
                end.grab.selectExited.AddListener(args =>
                {
                    if (IsSocketInteractor(args.interactorObject))
                        return;
                    if (!IsHeldByHand(handle))
                    {
                        end.grabLinkIndex = -1;
                        end.snapOnReleasePending = true;
                    }
                });

                if (grabChainAnywhere)
                    AddLinkGrabColliders(end);
            }

            // Chains start loose: a chain end is only socketed when the player puts it in by hand. A Starting Selected
            // Interactable on the socket would pull the chain onto the mould at play start wherever it was placed.
            // Cleared here in Awake, before XRBaseInteractor.Start applies it. Socket filters: ConfigureSnapPoints.
            if (end.socket != null)
                end.socket.startingSelectedInteractable = null;
        }

        /// <summary>Registers the chain's link colliders with its loop handle, so grabbing any link picks the chain up.</summary>
        private void AddLinkGrabColliders(ChainEnd end)
        {
            int n = end.links.Count;
            int skip = Mathf.Clamp(ungrabbableLinksNearRing, 0, n / 2 - 2);
            bool added = false;
            for (int i = skip; i < n - skip; i++)
            {
                var col = end.links[i].GetComponent<Collider>();
                if (col == null || end.grab.colliders.Contains(col))
                    continue;
                col.enabled = true;
                end.grab.colliders.Add(col);
                added = true;
            }
            if (!added)
                return;
            // Colliders are mapped to their interactable when it registers, so register again with the new list.
            if (end.grab.isActiveAndEnabled)
            {
                end.grab.enabled = false;
                end.grab.enabled = true;
            }
        }

        /// <summary>Link nearest to where the hand grabbed (-1 = the bottom loop handle itself).</summary>
        private int FindGrabbedLink(ChainEnd end, IXRSelectInteractor interactor)
        {
            if (!grabChainAnywhere || end.links.Count < 4 || interactor == null)
                return -1;
            Transform attach = interactor.GetAttachTransform(end.grab);
            Vector3 p = attach != null ? attach.position : interactor.transform.position;

            // Grabbing the bottom loop itself keeps the original behaviour.
            if (end.handleBox != null && end.handleBox.bounds.Contains(p))
                return -1;

            int n = end.links.Count;
            int skip = Mathf.Clamp(ungrabbableLinksNearRing, 1, n / 2 - 2);
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = skip; i < n - skip; i++)
            {
                float d = (end.links[i].position - p).sqrMagnitude;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>
        /// The Ring is a real Rigidbody when free: it falls, rolls and hangs on anything with a collider that passes
        /// through its hole (a hook, a peg, a bar). Its shape must be a ring of colliders (see BuildRingColliders), not a solid box.
        /// While held, XRI makes it kinematic; while on the crane hook or parked on the mould top, this script does.
        /// </summary>
        private void ConfigureRingBody()
        {
            if (centralRing == null)
                return;
            _ringRb = centralRing.GetComponent<Rigidbody>();
            if (_ringRb == null)
                return;
            _ringRb.mass = Mathf.Max(0.05f, ringMass);
            _ringRb.useGravity = true;
            _ringRb.interpolation = RigidbodyInterpolation.Interpolate;
            _ringRb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            _ringRb.isKinematic = false;

            var probeGo = new GameObject("ChainCollisionProbe") { hideFlags = HideFlags.HideAndDontSave };
            probeGo.transform.position = new Vector3(0f, -10000f, 0f); // far away: it never touches anything itself
            _probe = probeGo.AddComponent<SphereCollider>();
            _probe.isTrigger = true;
            _probe.radius = chainParticleRadius;
        }

        private void ConfigureCollisions()
        {
            var moldCols = moldRoot != null ? moldRoot.GetComponentsInChildren<Collider>(true) : Array.Empty<Collider>();
            var slingCols = chainsRoot != null ? chainsRoot.GetComponentsInChildren<Collider>(true) : Array.Empty<Collider>();
            var ringCols = centralRing != null ? centralRing.GetComponentsInChildren<Collider>(true) : Array.Empty<Collider>();

            var all = new List<Collider>(moldCols.Length + slingCols.Length);
            all.AddRange(moldCols);
            all.AddRange(slingCols);
            _moldAndSlingColliders = all.ToArray();

            _ownColliders.Clear();
            foreach (var c in slingCols) if (c != null) _ownColliders.Add(c);
            foreach (var c in ringCols) if (c != null) _ownColliders.Add(c);
            if (_probe != null) _ownColliders.Add(_probe);

            // The chain handles must never push mould parts around (both are moved kinematically), and the Ring never
            // collides with its own chain handles. The Ring DOES collide with the mould, the floor and everything else.
            // Trigger colliders are left alone: the chain-end sockets on the pallet detect the chain handles through them.
            for (int i = 0; i < slingCols.Length; i++)
            {
                Collider a = slingCols[i];
                if (a == null || a.isTrigger) continue;
                bool aIsRing = Array.IndexOf(ringCols, a) >= 0;
                for (int j = 0; j < all.Count; j++)
                {
                    Collider b = all[j];
                    if (b == null || b == a || b.isTrigger) continue;
                    bool bIsRing = Array.IndexOf(ringCols, b) >= 0;
                    bool bIsSling = Array.IndexOf(slingCols, b) >= 0;
                    if (aIsRing && !bIsSling) continue;   // Ring vs mould: real collisions
                    if (aIsRing && bIsRing) continue;
                    Physics.IgnoreCollision(a, b, true);
                }
            }
        }

        private void RegisterGrabEvents()
        {
            if (centralRingGrab != null)
            {
                centralRingGrab.selectEntered.AddListener(OnRingSelectEntered);
                centralRingGrab.selectExited.AddListener(OnRingSelectExited);
            }
        }

        private void UnregisterGrabEvents()
        {
            if (centralRingGrab != null)
            {
                centralRingGrab.selectEntered.RemoveListener(OnRingSelectEntered);
                centralRingGrab.selectExited.RemoveListener(OnRingSelectExited);
            }
        }

        private void OnRingSelectEntered(SelectEnterEventArgs args)
        {
            if (IsSocketInteractor(args.interactorObject))
                return;

            _ringResting = false;
            SlingGrabbed?.Invoke(centralRing.position);

            // Hand took the Ring: lift it off the hook.
            if (isAttached)
                DetachFromHook();
        }

        private void OnRingSelectExited(SelectExitEventArgs args)
        {
            // XRI restores the kinematic flag the Ring had when it was grabbed (it may have been parked or hooked then).
            // Once no hand holds it and it is not on the crane hook, it is a free physical body again.
            if (_ringRb == null || isAttached || centralRingGrab.isSelected)
                return;
            _ringResting = false;
            SetRingDrivenByScript(false);
            _ringRb.linearVelocity = Vector3.zero;
            _ringRb.angularVelocity = Vector3.zero;
        }

        // ------------------------------------------------------------------ hook connection

        public void SetHookInRange(bool inRange, Transform hookMount = null, Rigidbody hookRb = null)
        {
            isHookInRange = inRange;
            if (inRange)
            {
                currentHookMount = hookMount;
                currentHookRb = hookRb;
                if (hookMount != null) _lastHookMount = hookMount;
            }
            else if (!isAttached)
            {
                currentHookMount = null;
                currentHookRb = null;
            }
        }

        /// <summary>Hangs the Ring on the hook when the hook saddle is close enough to the Ring seat.</summary>
        public bool TryAutoConnect(Transform hookMount, Rigidbody hookRb)
        {
            if (isAttached || !autoConnect || hookMount == null)
                return false;

            float dist = Vector3.Distance(GetHookSaddleWorld(hookMount), RingSeatWorldPosition);
            if (!_autoConnectArmed)
            {
                if (dist >= autoConnectRearmDistance)
                    _autoConnectArmed = true;
                else
                    return false;
            }

            return dist <= autoConnectRadius && AttachToHook(hookMount, hookRb, enforceSeatingTolerance: false);
        }

        /// <summary>Hangs the central Ring on the crane hook.</summary>
        public bool AttachToHook(Transform hookMount, Rigidbody hookRb, bool enforceSeatingTolerance = false)
        {
            if (isAttached || hookMount == null)
                return false;

            Initialise();

            // Just taken off the hook by hand: ignore the trigger re-entering straight away.
            if (!_autoConnectArmed && Time.time - _lastDetachTime < 1.0f)
                return false;

            float dist = Vector3.Distance(GetHookSaddleWorld(hookMount), RingSeatWorldPosition);
            if (enforceSeatingTolerance && dist > autoConnectRadius * 1.25f)
                return false;

            currentHookMount = hookMount;
            _lastHookMount = hookMount;
            currentHookRb = hookRb != null ? hookRb : hookMount.GetComponent<Rigidbody>();
            _cachedAttachCargo = hookMount.GetComponent<NL_AttachCargo>();

            _hookColliders = hookMount.GetComponentsInChildren<Collider>(true);
            SetHookCollisionIgnored(true);

            // Take the Ring out of the hand that carried it to the hook.
            if (Application.isPlaying)
                ReleaseFromHand(centralRingGrab, "Ring released from the hand onto the crane hook.");

            isAttached = true;
            _autoConnectArmed = false;
            _ringResting = false;
            SetRingDrivenByScript(true); // the hook carries it now
            SuspendMouldInterpolation();

            if (_cachedAttachCargo != null)
                _cachedAttachCargo.isAttached = true;

            RingHookChanged?.Invoke(true, centralRing.position);

            Debug.Log($"[MoldCraneLiftingPoint] Ring hung on crane hook '{hookMount.name}' (dist={dist:F3}m, left socketed={IsLeftLoopSocketed}, right socketed={IsRightLoopSocketed}).");
            return true;
        }

        /// <summary>Takes the Ring off the crane hook. The mould is set down on its support first if it was lifted.</summary>
        public void DetachFromHook(bool force = false)
        {
            if (!isAttached)
                return;

            if (moldRoot != null && isMoldSuspended)
            {
                float supportY = SampleSupportSurfaceY(moldRoot.position);
                Vector3 pos = moldRoot.position;
                pos.y = supportY;
                moldRoot.position = pos;
                SyncInstalledMoldPartsPhysics();
            }

            SetHookCollisionIgnored(false);
            _hookColliders = null;

            isAttached = false;
            isChainTaut = false;
            isMoldSuspended = false;
            currentTautness = 0f;
            currentClearanceAboveSupport = 0f;
            _autoConnectArmed = false;
            _lastDetachTime = Time.time;
            RestoreMouldInterpolation();

            // Not taken by a hand (manual release / crane button): it drops off the hook as a free body.
            if (!IsHeldByHand(centralRingGrab))
                SetRingDrivenByScript(false);

            if (_cachedAttachCargo != null)
                _cachedAttachCargo.isAttached = false;

            RingHookChanged?.Invoke(false, centralRing.position);

            Debug.Log("[MoldCraneLiftingPoint] Ring taken off the crane hook.");
        }

        /// <summary>
        /// Installed mould parts are kinematic Rigidbodies with interpolation. When the sling moves the mould root by transform,
        /// interpolation writes stale poses back onto the parts every frame and they drift out of the assembly (they sink while lifted).
        /// Interpolation is switched off on those parts while the Ring is on the hook and restored afterwards.
        /// </summary>
        private void SuspendMouldInterpolation()
        {
            if (moldRoot == null)
                return;
            var bodies = moldRoot.GetComponentsInChildren<Rigidbody>(true);
            for (int i = 0; i < bodies.Length; i++)
            {
                Rigidbody rb = bodies[i];
                if (rb == null || !rb.isKinematic || rb.interpolation == RigidbodyInterpolation.None || _suspendedInterpolation.ContainsKey(rb))
                    continue;
                _suspendedInterpolation[rb] = rb.interpolation;
                rb.interpolation = RigidbodyInterpolation.None;
            }
        }

        private void RestoreMouldInterpolation()
        {
            Physics.SyncTransforms();
            foreach (var kv in _suspendedInterpolation)
            {
                if (kv.Key != null)
                    kv.Key.interpolation = kv.Value;
            }
            _suspendedInterpolation.Clear();
        }

        private void ReleaseFromHand(XRGrabInteractable grab, string reason)
        {
            if (grab == null || !grab.isSelected || grab.interactionManager == null)
                return;
            var selecting = grab.interactorsSelecting;
            bool anyHand = false;
            for (int i = 0; i < selecting.Count; i++)
            {
                if (selecting[i] is XRSocketInteractor) continue;
                _handsBlockedUntilRelease.Add(selecting[i]);
                anyHand = true;
            }
            if (!anyHand)
                return;
            grab.interactionManager.CancelInteractableSelection((IXRSelectInteractable)grab);
            Debug.Log($"[MoldCraneLiftingPoint] {reason}");
        }

        private bool IsHandAllowed(IXRSelectInteractor interactor)
        {
            if (IsSocketInteractor(interactor))
                return true;
            return !isMoldSuspended && !_handsBlockedUntilRelease.Contains(interactor);
        }

        private void ForgetReleasedHands()
        {
            if (_handsBlockedUntilRelease.Count == 0)
                return;
            if (_handReleasedPredicate == null)
                _handReleasedPredicate = i => i == null || (i as UnityEngine.Object) == null || !i.isSelectActive;
            _handsBlockedUntilRelease.RemoveWhere(_handReleasedPredicate);
        }

        private void SetHookCollisionIgnored(bool ignored)
        {
            if (_moldAndSlingColliders == null || _hookColliders == null)
                return;
            for (int i = 0; i < _moldAndSlingColliders.Length; i++)
            {
                if (_moldAndSlingColliders[i] == null) continue;
                for (int j = 0; j < _hookColliders.Length; j++)
                {
                    if (_hookColliders[j] != null)
                        Physics.IgnoreCollision(_moldAndSlingColliders[i], _hookColliders[j], ignored);
                }
            }
        }

        /// <summary>
        /// Lowest normalized hook height allowed while the Ring is on the hook: the Ring cannot be pushed into the mould top or the floor.
        /// </summary>
        public bool TryGetMinAllowedHookHeight(float currentHookHeight, float craneHeight, float hookOffset, out float minHookHeight)
        {
            minHookHeight = 0f;
            if (!isAttached || currentHookMount == null || centralRing == null)
                return false;

            float travelSpan = (craneHeight + hookOffset) - 0.5f;
            if (travelSpan <= 0.01f)
                travelSpan = 4.3f;

            float minRingCenterY = RingFloorY(centralRing.position);
            float minSaddleY = minRingCenterY + SeatOffsetWorld();
            float remainingDrop = GetHookSaddleWorld(currentHookMount).y - minSaddleY;

            if (remainingDrop <= 0.002f)
            {
                minHookHeight = currentHookHeight;
                return true;
            }

            minHookHeight = Mathf.Max(0f, currentHookHeight - remainingDrop / travelSpan);
            return true;
        }

        // ------------------------------------------------------------------ per-frame simulation

        private void LateUpdate()
        {
            if (!Application.isPlaying || centralRing == null || moldRoot == null)
                return;

            float dt = Mathf.Min(Time.deltaTime, 0.05f);

            UpdateRearm();
            ForgetReleasedHands();

            isRingHeld = IsHeldByHand(centralRingGrab);
            UpdateSnapping(_end1);
            UpdateSnapping(_end2);
            isLeftLoopSocketed = IsSocketed(_end1);
            isRightLoopSocketed = IsSocketed(_end2);
            RefreshTautHeightIfSnapsChanged();
            UpdateSnapGuides();

            UpdateSocketCooldown(_end1);
            UpdateSocketCooldown(_end2);

            if (isAttached && currentHookMount != null)
                UpdateHookedRingAndMould(dt);
            else
                UpdateUnhookedRing();

            ReleaseOverstretchedEnd(_end1);
            ReleaseOverstretchedEnd(_end2);

            UpdateHandleColliders();
            float s1 = SimulateChain(_end1, dt, out Vector3 c1);
            float s2 = SimulateChain(_end2, dt, out Vector3 c2);
            ChainMotionSpeed = 0.5f * (s1 + s2);
            ChainCentroid = 0.5f * (c1 + c2);

            RaiseStateEvents();
        }

        /// <summary>Raises socket / lift change events (the sockets select their starting chain ends silently on the first frames).</summary>
        private void RaiseStateEvents()
        {
            if (!_eventsPrimed)
            {
                _prevLeftSocketed = isLeftLoopSocketed;
                _prevRightSocketed = isRightLoopSocketed;
                _prevSuspended = isMoldSuspended;
                _eventsPrimed = Time.timeSinceLevelLoad > 0.5f;
                return;
            }
            if (isLeftLoopSocketed != _prevLeftSocketed && _end1.grab != null)
                ChainEndSocketChanged?.Invoke(0, isLeftLoopSocketed, _end1.grab.transform.position);
            if (isRightLoopSocketed != _prevRightSocketed && _end2.grab != null)
                ChainEndSocketChanged?.Invoke(1, isRightLoopSocketed, _end2.grab.transform.position);
            if (isMoldSuspended != _prevSuspended)
                MouldLiftChanged?.Invoke(isMoldSuspended, centralRing.position);
            _prevLeftSocketed = isLeftLoopSocketed;
            _prevRightSocketed = isRightLoopSocketed;
            _prevSuspended = isMoldSuspended;
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (!Application.isPlaying || isAttached || collision.contactCount == 0)
                return;
            // Normal component only: a Ring being dragged along the floor by its chain is not an impact.
            ContactPoint cp = collision.GetContact(0);
            float speed = Mathf.Abs(Vector3.Dot(collision.relativeVelocity, cp.normal));
            if (speed > 0.6f)
                RingImpact?.Invoke(cp.point, speed);
        }

        private void OnBeforeRenderPoseLinks()
        {
            // XRI moves held objects again just before rendering; move the pinned links with them so the chain stays glued to the hands.
            if (!isActiveAndEnabled || centralRing == null || moldRoot == null)
                return;
            RefreshPins(_end1);
            RefreshPins(_end2);
            ApplyLinkTransforms(_end1);
            ApplyLinkTransforms(_end2);
        }

        private void UpdateRearm()
        {
            if (isAttached || _autoConnectArmed)
                return;
            Transform hook = currentHookMount != null ? currentHookMount : _lastHookMount;
            if (hook == null || Vector3.Distance(GetHookSaddleWorld(hook), RingSeatWorldPosition) >= autoConnectRearmDistance)
                _autoConnectArmed = true;
        }

        private void UpdateHookedRingAndMould(float dt)
        {
            if (currentHookRb != null && !currentHookRb.isKinematic)
            {
                currentHookRb.linearVelocity *= 0.2f;
                currentHookRb.angularVelocity *= 0.2f;
            }

            Vector3 ringPos = GetHookSaddleWorld(currentHookMount) - Vector3.up * SeatOffsetWorld();
            bool both = isLeftLoopSocketed && isRightLoopSocketed;

            float supportY = SampleSupportSurfaceY(moldRoot.position);
            float liftedMoldY = ringPos.y - _tautRingLocalY;

            if (both && liftedMoldY > supportY + 0.002f)
            {
                // Taut sling: the mould hangs under the Ring.
                if (!isMoldSuspended)
                    SuspendMouldInterpolation(); // catch parts installed since the Ring was hooked
                Vector3 m = moldRoot.position;
                float k = 1f - Mathf.Exp(-4f * dt);
                Vector3 target = new Vector3(Mathf.Lerp(m.x, ringPos.x, k), liftedMoldY, Mathf.Lerp(m.z, ringPos.z, k));
                moldRoot.SetPositionAndRotation(target, Quaternion.identity);
                SyncInstalledMoldPartsPhysics();

                isChainTaut = true;
                currentTautness = 1f;
                currentClearanceAboveSupport = liftedMoldY - supportY;
                isMoldSuspended = currentClearanceAboveSupport > 0.005f;
            }
            else
            {
                if (isMoldSuspended || moldRoot.position.y > supportY + 0.0005f)
                {
                    Vector3 pos = moldRoot.position;
                    pos.y = supportY;
                    moldRoot.position = pos;
                    SyncInstalledMoldPartsPhysics();
                }

                isMoldSuspended = false;
                currentClearanceAboveSupport = 0f;
                float restRingLocalY = ringPos.y - moldRoot.position.y;
                float slackRange = Mathf.Max(0.05f, _tautRingLocalY - (bodyTopHeight + ringRestHeight));
                currentTautness = both ? Mathf.Clamp01(1f - (_tautRingLocalY - restRingLocalY) / slackRange) : 0f;
                isChainTaut = both && currentTautness > 0.98f;

                // Only one end socketed: the Ring going up would tear that end out of its socket rather than tilt the mould.
                if (!both)
                {
                    PullOutIfOverstretched(_end1, ringPos);
                    PullOutIfOverstretched(_end2, ringPos);
                }
            }

            // Never push the Ring into the mould top / floor.
            ringPos.y = Mathf.Max(ringPos.y, RingFloorY(ringPos));
            centralRing.SetPositionAndRotation(ringPos, UprightRingRot);
            if (_ringRb != null && _ringRb.isKinematic)
            {
                _ringRb.position = ringPos;
                _ringRb.rotation = UprightRingRot;
            }
        }

        private void PullOutIfOverstretched(ChainEnd end, Vector3 ringPos)
        {
            ChainSnapPoint snap = ActiveSnap(end);
            if (snap == null)
                return;
            float legLen = LegLength(snap.bightHalfLength);
            if (SocketedLegPathLength(end, ringPos) <= legLen * 1.04f)
                return;

            var manager = end.grab.interactionManager;
            if (manager != null)
                manager.CancelInteractableSelection((IXRSelectInteractable)end.grab);
            end.socketCooldownUntil = Time.time + 1.5f;
            end.cooldownSocket = snap.Socket;
            end.cooldownSocket.socketActive = false;
            Debug.LogWarning($"[MoldCraneLiftingPoint] {end.label} was pulled out of its socket - socket BOTH chain ends on the mould before lifting.");
        }

        private void UpdateSocketCooldown(ChainEnd end)
        {
            if (end.cooldownSocket == null || end.cooldownSocket.socketActive || Time.time < end.socketCooldownUntil)
                return;
            end.cooldownSocket.socketActive = true;
            end.cooldownSocket = null;
        }

        private void UpdateUnhookedRing()
        {
            isChainTaut = false;
            currentTautness = 0f;
            if (isMoldSuspended)
            {
                isMoldSuspended = false;
                currentClearanceAboveSupport = 0f;
            }

            if (isRingHeld)
            {
                // The hand cannot pull the Ring further than the chain reaches: it slips out of the hand instead.
                Vector3 reachable = centralRing.position;
                Quaternion rot = centralRing.rotation;
                for (int iter = 0; iter < 4; iter++)
                {
                    ConstrainRingToEnd(_end1, ref reachable, rot);
                    ConstrainRingToEnd(_end2, ref reachable, rot);
                }
                if ((reachable - centralRing.position).sqrMagnitude > overstretchReleaseDistance * overstretchReleaseDistance)
                    ReleaseFromHand(centralRingGrab, "Ring slipped out of the hand - the chain is fully stretched.");
                return;
            }

            if (_ringResting)
            {
                // Parked on the mould top: ride along if the mould is moved.
                Vector3 p = moldRoot.TransformPoint(_ringRestLocalPos);
                Quaternion r = moldRoot.rotation * _ringRestLocalRot;
                centralRing.SetPositionAndRotation(p, r);
                if (_ringRb != null) { _ringRb.position = p; _ringRb.rotation = r; }
            }
            // Otherwise the Ring is a free Rigidbody: gravity and collisions move it (FixedUpdate adds the chain's reach).
        }

        /// <summary>
        /// Free Ring (not held, not hooked, not parked): it is a normal Rigidbody. This adds what physics does not know about:
        /// a chain end that is socketed or held is a rope tether, and the open mould top is a resting shelf.
        /// </summary>
        private void UpdateFreeRingPhysics()
        {
            if (isAttached || IsHeldByHand(centralRingGrab))
                return;

            if (_ringResting)
            {
                // A chain end carried away (or a raised socket) tugs the parked Ring off the mould top.
                Vector3 probe = _ringRb.position;
                for (int iter = 0; iter < 4; iter++)
                {
                    ConstrainRingToEnd(_end1, ref probe, _ringRb.rotation);
                    ConstrainRingToEnd(_end2, ref probe, _ringRb.rotation);
                }
                if ((probe - _ringRb.position).sqrMagnitude < 0.02f * 0.02f)
                    return;
                _ringResting = false;
                _unparkedAt = Time.time;
                SetRingDrivenByScript(false);
            }

            if (_ringRb.isKinematic)
                return;

            Vector3 p = _ringRb.position;
            Quaternion rot = _ringRb.rotation;
            Vector3 q = p;
            bool tethered = false;
            for (int iter = 0; iter < 4; iter++)
            {
                tethered |= ConstrainRingToEnd(_end1, ref q, rot);
                tethered |= ConstrainRingToEnd(_end2, ref q, rot);
            }
            if (tethered)
            {
                Vector3 corr = q - p;
                _ringRb.position = q;
                if (corr.sqrMagnitude > 1e-10f)
                {
                    Vector3 n = corr.normalized;
                    Vector3 v = _ringRb.linearVelocity;
                    float away = Vector3.Dot(v, n);
                    if (away < 0f) _ringRb.linearVelocity = v - away * n; // chain is taut: no more moving away from the anchor
                }
            }

            if (!ringRestsOnMouldTop)
                return;

            Vector3 local = moldRoot.InverseTransformPoint(q);
            bool overMould = Mathf.Abs(local.x) < bodyHalfExtentsXZ.x - 0.02f && Mathf.Abs(local.z) < bodyHalfExtentsXZ.y - 0.02f;
            float shelf = bodyTopHeight + ringRestHeight;
            if (overMould && Time.time - _unparkedAt > 0.75f && local.y <= shelf && local.y > shelf - 0.35f && _ringRb.linearVelocity.y <= 0.05f)
            {
                // Park it on the top edge of the open mould (it would otherwise drop into the cavity).
                local.y = shelf;
                float yaw = _ringRb.rotation.eulerAngles.y;
                _ringRestLocalPos = local;
                _ringRestLocalRot = Quaternion.Inverse(moldRoot.rotation) * Quaternion.Euler(-90f, yaw, 0f);
                _ringResting = true;
                SetRingDrivenByScript(true);
                Vector3 wp = moldRoot.TransformPoint(local);
                Quaternion wr = moldRoot.rotation * _ringRestLocalRot;
                _ringRb.position = wp;
                _ringRb.rotation = wr;
                centralRing.SetPositionAndRotation(wp, wr);
            }
        }

        /// <summary>True: kinematic, no interpolation (this script places it every frame). False: a free dynamic body.</summary>
        private void SetRingDrivenByScript(bool driven)
        {
            if (_ringRb == null)
                return;
            if (driven)
            {
                if (!_ringRb.isKinematic)
                {
                    _ringRb.linearVelocity = Vector3.zero;
                    _ringRb.angularVelocity = Vector3.zero;
                }
                _ringRb.isKinematic = true;
                _ringRb.interpolation = RigidbodyInterpolation.None;
            }
            else
            {
                _ringRb.interpolation = RigidbodyInterpolation.Interpolate;
                _ringRb.isKinematic = false;
                _ringRb.WakeUp();
            }
        }

        /// <summary>Pulls the Ring back so neither leg of this chain is longer than the chain allows.</summary>
        private bool ConstrainRingToEnd(ChainEnd end, ref Vector3 ringPos, Quaternion ringRot)
        {
            bool socketed = IsSocketed(end);
            bool held = !socketed && IsHeldByHand(end.grab);
            if (!socketed && !held)
                return false;

            bool changed = false;
            float bight = socketed ? SocketedBight(end) : looseBightHalfLength;
            float legLen = LegLength(bight);

            for (int leg = 0; leg < 2; leg++)
            {
                int offsetIndex = leg == 0 ? end.frontOffsetIndex : end.backOffsetIndex;
                Vector3 attachOffset = RingAttachOffsetWorld(offsetIndex, ringRot);
                Vector3 attach = ringPos + attachOffset;
                GetLegLowerPoints(end, leg == 0, socketed, attach, out Vector3 corner, out bool useEdge, out Vector3 edge);

                Vector3 anchor = useEdge ? edge : corner;
                float maxLen = useEdge ? legLen - Vector3.Distance(edge, corner) : legLen;
                if (held && end.grabLinkIndex >= 0)
                {
                    // Held by a side link: each leg is the chain between the Ring and that link.
                    int g = HeldLinkIndex(end);
                    anchor = HeldPoint(end);
                    maxLen = linkPitch * (leg == 0 ? g : end.links.Count - 1 - g);
                }
                if (maxLen < 0.02f) maxLen = 0.02f;

                Vector3 d = attach - anchor;
                float len = d.magnitude;
                if (len > maxLen && len > 1e-5f)
                {
                    attach = anchor + d / len * maxLen;
                    ringPos = attach - attachOffset;
                    changed = true;
                }
            }
            return changed;
        }

        private void ReleaseOverstretchedEnd(ChainEnd end)
        {
            if (end.grab == null || !IsHeldByHand(end.grab))
                return;
            // A free Ring is dragged along by the chain (FixedUpdate); a hooked or held Ring is not, so the chain end slips out of the hand.
            Vector3 off = end.grab.transform.position - RingAttachMid(end);
            float reach = LegLength(looseBightHalfLength);
            if (end.grabLinkIndex >= 0)
            {
                int g = HeldLinkIndex(end);
                reach = linkPitch * Mathf.Min(g, end.links.Count - 1 - g);
            }
            if ((isAttached || isRingHeld) && off.magnitude > reach + overstretchReleaseDistance)
                ReleaseFromHand(end.grab, $"{end.label} slipped out of the hand - the chain is fully stretched.");
        }

        private void UpdateHandleColliders()
        {
            UpdateHandleCollider(_end1);
            UpdateHandleCollider(_end2);
        }

        private void UpdateHandleCollider(ChainEnd end)
        {
            if (end.handleBox == null)
                return;
            float scale = Mathf.Max(1e-4f, Mathf.Abs(end.handleBox.transform.lossyScale.z));
            float bight = IsSocketed(end) ? SocketedBight(end) : looseBightHalfLength;
            Vector3 size = end.handleBox.size;
            float wantZ = (2f * bight + 0.12f) / scale;
            if (Mathf.Abs(size.z - wantZ) > 1e-3f)
            {
                size.z = wantZ;
                end.handleBox.size = size;
            }
        }

        // ------------------------------------------------------------------ chain geometry

        private float TotalChainLength(ChainEnd end) => linkPitch * Mathf.Max(1, end.links.Count - 1);

        private float LegLength(float bightHalf)
        {
            int n = Mathf.Max(_end1.links.Count, 2);
            return (linkPitch * (n - 1) - 2f * bightHalf) * 0.5f;
        }

        private float SeatOffsetWorld() => ringSeatLocalOffset.z * GetRingWorldScale();

        private float GetRingWorldScale() => centralRing == null ? 0.64f : Mathf.Abs(centralRing.lossyScale.z);

        /// <summary>Leg attach offset with the Ring upright (as it hangs on the crane hook) - used by the lift maths.</summary>
        private Vector3 RingAttachOffsetWorld(int index) => RingAttachOffsetWorld(index, UprightRingRot);

        private Vector3 RingAttachOffsetWorld(int index, Quaternion ringRot)
        {
            Vector3 local = ringLegLocalOffsets != null && index < ringLegLocalOffsets.Length ? ringLegLocalOffsets[index] : Vector3.zero;
            Vector3 s = centralRing != null ? centralRing.lossyScale : Vector3.one * 0.64f;
            return ringRot * Vector3.Scale(local, s);
        }

        private Vector3 RingAttachMid(ChainEnd end)
        {
            Vector3 p = centralRing.position;
            Quaternion r = centralRing.rotation;
            return p + 0.5f * (RingAttachOffsetWorld(end.frontOffsetIndex, r) + RingAttachOffsetWorld(end.backOffsetIndex, r));
        }

        /// <summary>
        /// Bottom corner of one leg (where it meets the bight) and, when needed, the point where that leg passes over the mould's top edge.
        /// </summary>
        private void GetLegLowerPoints(ChainEnd end, bool front, bool socketed, Vector3 attachWorld, out Vector3 corner, out bool useEdge, out Vector3 edge)
        {
            float sz = front ? 1f : -1f;
            if (socketed)
            {
                ChainSnapPoint snap = RouteSnap(end);
                if (snap != null)
                {
                    corner = snap.Corner(front);
                    edge = snap.Edge(front);
                    useEdge = snap.routeOverTopEdge &&
                              SegmentHitsBody(moldRoot.InverseTransformPoint(attachWorld), moldRoot.InverseTransformPoint(corner));
                    return;
                }
                corner = end.grab.transform.position + moldRoot.forward * (sz * socketedBightHalfLength);
                edge = moldRoot.TransformPoint(new Vector3(end.side * ledgeHalfSpanX, topEdgeHeight, sz * topEdgeHalfZ));
                Vector3 aLocal = moldRoot.InverseTransformPoint(attachWorld);
                Vector3 cLocal = moldRoot.InverseTransformPoint(corner);
                useEdge = SegmentHitsBody(aLocal, cLocal);
            }
            else
            {
                Vector3 dir = end.grab.transform.forward;
                dir.y = 0f;
                if (dir.sqrMagnitude < 1e-4f) dir = Vector3.forward;
                dir.Normalize();
                corner = end.grab.transform.position + dir * (sz * looseBightHalfLength);
                edge = corner;
                useEdge = false;
            }
        }

        private bool SegmentHitsBody(Vector3 a, Vector3 b)
        {
            Vector3 min = new Vector3(-bodyHalfExtentsXZ.x, 0.02f, -bodyHalfExtentsXZ.y);
            Vector3 max = new Vector3(bodyHalfExtentsXZ.x, bodyTopHeight, bodyHalfExtentsXZ.y);
            Vector3 d = b - a;
            float tMin = 0f, tMax = 1f;
            for (int i = 0; i < 3; i++)
            {
                if (Mathf.Abs(d[i]) < 1e-6f)
                {
                    if (a[i] < min[i] || a[i] > max[i]) return false;
                    continue;
                }
                float inv = 1f / d[i];
                float t1 = (min[i] - a[i]) * inv;
                float t2 = (max[i] - a[i]) * inv;
                if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                tMin = Mathf.Max(tMin, t1);
                tMax = Mathf.Min(tMax, t2);
                if (tMin > tMax) return false;
            }
            return true;
        }

        private float SocketedLegPathLength(ChainEnd end, Vector3 ringPos)
        {
            float worst = 0f;
            for (int leg = 0; leg < 2; leg++)
            {
                Vector3 attach = ringPos + RingAttachOffsetWorld(leg == 0 ? end.frontOffsetIndex : end.backOffsetIndex);
                GetLegLowerPoints(end, leg == 0, true, attach, out Vector3 corner, out bool useEdge, out Vector3 edge);
                float len = useEdge ? Vector3.Distance(attach, edge) + Vector3.Distance(edge, corner) : Vector3.Distance(attach, corner);
                worst = Mathf.Max(worst, len);
            }
            return worst;
        }

        /// <summary>Ring centre height above the mould root (Ring centred over the mould) at which both socketed chains are taut.</summary>
        private float SolveTautRingLocalHeight()
        {
            if (centralRing == null || moldRoot == null || _end1.links.Count < 2)
                return 1.1f;

            float maxLeg = Mathf.Max(LegLength(SocketedBight(_end1)), LegLength(SocketedBight(_end2)));
            float lo = bodyTopHeight + ringRestHeight;
            float hi = lo + maxLeg * 2f;
            for (int i = 0; i < 40; i++)
            {
                float mid = 0.5f * (lo + hi);
                if (TautExcessAtLocalHeight(mid) >= 0f) hi = mid; else lo = mid;
            }
            return hi;
        }

        /// <summary>Largest (path length - available leg length) over all four legs with the Ring at this height.</summary>
        private float TautExcessAtLocalHeight(float ringLocalY)
        {
            float worst = float.NegativeInfinity;
            for (int e = 0; e < 2; e++)
            {
                ChainEnd end = e == 0 ? _end1 : _end2;
                ChainSnapPoint snap = RouteSnap(end);
                float side = e == 0 ? -1f : 1f;
                int fi = e == 0 ? 0 : 2;
                float bight = snap != null ? snap.bightHalfLength : socketedBightHalfLength;
                float legLen = LegLength(bight);
                for (int leg = 0; leg < 2; leg++)
                {
                    bool front = leg == 0;
                    float sz = front ? 1f : -1f;
                    Vector3 attach = new Vector3(0f, ringLocalY, 0f) + RingAttachOffsetWorld(fi + leg);
                    Vector3 corner, edge;
                    bool overEdge;
                    if (snap != null)
                    {
                        corner = moldRoot.InverseTransformPoint(snap.Corner(front));
                        edge = moldRoot.InverseTransformPoint(snap.Edge(front));
                        overEdge = snap.routeOverTopEdge && SegmentHitsBody(attach, corner);
                    }
                    else
                    {
                        corner = new Vector3(side * ledgeHalfSpanX, ledgeHeight, sz * socketedBightHalfLength);
                        edge = new Vector3(side * ledgeHalfSpanX, topEdgeHeight, sz * topEdgeHalfZ);
                        overEdge = SegmentHitsBody(attach, corner);
                    }
                    float len = overEdge
                        ? Vector3.Distance(attach, edge) + Vector3.Distance(edge, corner)
                        : Vector3.Distance(attach, corner);
                    worst = Mathf.Max(worst, len - legLen);
                }
            }
            return worst;
        }

        /// <summary>Re-solves the taut lift height whenever the chains move to different snap points.</summary>
        private void RefreshTautHeightIfSnapsChanged()
        {
            ChainSnapPoint s1 = RouteSnap(_end1), s2 = RouteSnap(_end2);
            int key = ((s1 != null ? s1.GetHashCode() : 0) * 397) ^ (s2 != null ? s2.GetHashCode() : 0);
            if (key == _tautSnapKey)
                return;
            _tautSnapKey = key;
            _tautRingLocalY = SolveTautRingLocalHeight();
        }

        // ------------------------------------------------------------------ rope simulation (one Verlet particle per link)

        private void InitChainSim(ChainEnd end)
        {
            int n = end.links.Count;
            end.simReady = n >= 4;
            if (!end.simReady)
                return;
            end.pos = new Vector3[n];
            end.prev = new Vector3[n];
            end.pinned = new bool[n];
            end.contact = new bool[n];
            for (int i = 0; i < n; i++)
            {
                end.pos[i] = end.links[i] != null ? end.links[i].position : Vector3.zero;
                end.prev[i] = end.pos[i];
            }
        }

        /// <summary>
        /// Marks the pinned particles and moves them to where they are held:
        /// both chain ends hang on the Ring; a socketed bight lies along the pallet ledge; a held bight follows the hand.
        /// A loose bight is not pinned - it swings and drapes, and its grab handle follows it.
        /// </summary>
        private void RefreshPins(ChainEnd end)
        {
            if (!end.simReady || end.grab == null)
                return;
            int n = end.pos.Length;
            int c = n / 2;
            Array.Clear(end.pinned, 0, n);

            Quaternion rr = centralRing.rotation;
            Pin(end, 0, centralRing.position + RingAttachOffsetWorld(end.frontOffsetIndex, rr));
            Pin(end, n - 1, centralRing.position + RingAttachOffsetWorld(end.backOffsetIndex, rr));

            Transform h = end.grab.transform;
            ChainSnapPoint snap = ActiveSnap(end);
            if (snap != null)
            {
                float bight = snap.bightHalfLength;
                int k = Mathf.Clamp(Mathf.RoundToInt(bight / linkPitch), 1, c - 2);
                Vector3 fwd = snap.Axis;
                Vector3 centre = snap.Center;
                for (int j = -k; j <= k; j++)
                    Pin(end, c + j, centre - fwd * (bight * j / k));
            }
            else if (IsHeldByHand(end.grab))
            {
                // Held by a link on the chain's side: that link stays in the hand and the rest hangs from it.
                int g = HeldLinkIndex(end);
                Vector3 fwd;
                if (g != c && g > 0 && g < n - 1)
                {
                    fwd = end.pos[g - 1] - end.pos[g + 1];
                    fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Vector3.up;
                }
                else
                {
                    fwd = h.forward;
                    fwd.y = 0f;
                    fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
                }
                Vector3 heldAt = g != c ? HeldPoint(end) : h.position;
                for (int j = -1; j <= 1; j++)
                    Pin(end, g + j, heldAt - fwd * (linkPitch * j));
            }
        }

        /// <summary>
        /// Where the hand holding this chain is. The grab handle only catches up with the hand a frame after the grab, so
        /// the hand itself is used wherever the chain is measured from the held point.
        /// </summary>
        private static Vector3 HeldPoint(ChainEnd end)
        {
            var selecting = end.grab.interactorsSelecting;
            for (int i = 0; i < selecting.Count; i++)
            {
                if (selecting[i] is XRSocketInteractor)
                    continue;
                Transform attach = selecting[i].GetAttachTransform(end.grab);
                if (attach != null)
                    return attach.position;
            }
            return end.grab.transform.position;
        }

        /// <summary>Link the hand is holding: the grabbed side link, else the middle of the bottom loop.</summary>
        private static int HeldLinkIndex(ChainEnd end)
        {
            int n = end.links.Count;
            int g = end.grabLinkIndex;
            return g >= 1 && g <= n - 2 ? g : n / 2;
        }

        private static void Pin(ChainEnd end, int i, Vector3 p)
        {
            end.pinned[i] = true;
            end.pos[i] = p;
        }

        /// <summary>Steps one chain; returns the mean speed (m/s) of its free links and outputs their centre.</summary>
        private float SimulateChain(ChainEnd end, float dt, out Vector3 centroid)
        {
            centroid = centralRing != null ? centralRing.position : transform.position;
            if (!end.simReady || end.grab == null || dt <= 0f)
                return 0f;

            int n = end.pos.Length;
            int steps = Mathf.Max(1, chainSubsteps);
            float h = dt / steps;
            _simStepDt = h;
            Vector3 gStep = Physics.gravity * (h * h);
            float rest = linkPitch;

            for (int s = 0; s < steps; s++)
            {
                RefreshPins(end);
                for (int i = 0; i < n; i++)
                {
                    if (end.pinned[i])
                    {
                        end.prev[i] = end.pos[i];
                        continue;
                    }
                    Vector3 v = (end.pos[i] - end.prev[i]) * chainDamping;
                    end.prev[i] = end.pos[i];
                    end.pos[i] += v + gStep;
                }

                for (int it = 0; it < chainIterations; it++)
                {
                    for (int i = 0; i < n - 1; i++)
                        SatisfyLink(end, i, i + 1, rest);
                }

                CollideChain(end);
            }

            // A loose bight carries its grab handle with it, so the player can pick the chain up where it lies.
            if (!IsSocketed(end) && !IsHeldByHand(end.grab))
            {
                int c = n / 2;
                Vector3 along = end.pos[c - 1] - end.pos[c + 1];
                along.y = 0f;
                Quaternion hr = along.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(along.normalized, Vector3.up) : end.grab.transform.rotation;
                end.grab.transform.SetPositionAndRotation(end.pos[c], hr);
            }

            ApplyLinkTransforms(end);
            end.loadFront = LegLoad(end, 0, 1);
            end.loadBack = LegLoad(end, n - 1, -1);

            float speedSum = 0f;
            int free = 0;
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                sum += end.pos[i];
                if (end.pinned[i]) continue;
                speedSum += (end.pos[i] - end.prev[i]).magnitude / h;
                free++;
            }
            centroid = sum / n;
            return free > 0 ? speedSum / free : 0f;
        }

        private float ParticleMass(ChainEnd end) => Mathf.Max(0f, chainMassKg) / Mathf.Max(1, end.pos.Length);

        /// <summary>
        /// Pull of one leg on the Ring: the weight of the links hanging from the attach point, down to the first link that
        /// is supported (lying on something, or pinned at the socket / hand), along the first link's direction.
        /// A leg running sideways or upward from the Ring hangs nothing from it.
        /// </summary>
        private Vector3 LegLoad(ChainEnd end, int start, int step)
        {
            int n = end.pos.Length;
            Vector3 dir = end.pos[start + step] - end.pos[start];
            if (dir.sqrMagnitude < 1e-8f)
                return Vector3.zero;
            dir.Normalize();
            float down = -dir.y;
            if (down <= 0f)
                return Vector3.zero;

            int hanging = 0;
            for (int i = start + step; i >= 0 && i < n; i += step)
            {
                if (end.pinned[i] || end.contact[i])
                    break;
                hanging++;
            }
            return dir * (hanging * ParticleMass(end) * Physics.gravity.magnitude * down);
        }

        /// <summary>Adds the hanging chain weight to a free Ring (FixedUpdate).</summary>
        private void ApplyChainWeightToRing()
        {
            if (_ringRb == null || _ringRb.isKinematic || chainMassKg <= 0f)
                return;
            Quaternion r = _ringRb.rotation;
            Vector3 c = _ringRb.position;
            AddLegLoad(_end1.loadFront, c + RingAttachOffsetWorld(_end1.frontOffsetIndex, r));
            AddLegLoad(_end1.loadBack, c + RingAttachOffsetWorld(_end1.backOffsetIndex, r));
            AddLegLoad(_end2.loadFront, c + RingAttachOffsetWorld(_end2.frontOffsetIndex, r));
            AddLegLoad(_end2.loadBack, c + RingAttachOffsetWorld(_end2.backOffsetIndex, r));
        }

        private void AddLegLoad(Vector3 force, Vector3 at)
        {
            if (force.sqrMagnitude > 1e-6f)
                _ringRb.AddForceAtPosition(force, at, ForceMode.Force);
        }

        private static void SatisfyLink(ChainEnd end, int a, int b, float rest)
        {
            Vector3 delta = end.pos[b] - end.pos[a];
            float d = delta.magnitude;
            if (d < 1e-7f)
                return;
            float wa = end.pinned[a] ? 0f : 1f;
            float wb = end.pinned[b] ? 0f : 1f;
            float w = wa + wb;
            if (w <= 0f)
                return;
            Vector3 corr = delta * ((d - rest) / (d * w));
            end.pos[a] += corr * wa;
            end.pos[b] -= corr * wb;
        }

        /// <summary>Pushes every free link out of any collider it overlaps (floor, mould parts, stands, hooks) and applies contact friction.</summary>
        private void CollideChain(ChainEnd end)
        {
            if (_probe == null)
                return;
            float r = chainParticleRadius;
            _probe.radius = r;
            float floorY = minGroundY + r;
            int n = end.pos.Length;
            float pm = ParticleMass(end);

            for (int i = 0; i < n; i++)
            {
                if (end.pinned[i])
                    continue;
                Vector3 p = end.pos[i];
                Vector3 vIn = p - end.prev[i]; // this step's motion before any contact
                int hits = Physics.OverlapSphereNonAlloc(p, r, _overlap, chainCollisionLayers, QueryTriggerInteraction.Ignore);
                bool touched = false;
                Vector3 normalSum = Vector3.zero;
                for (int k = 0; k < hits; k++)
                {
                    Collider col = _overlap[k];
                    if (col == null || col.isTrigger || _ownColliders.Contains(col))
                        continue;
                    if (col.transform.IsChildOf(moldRoot))
                        continue; // the mould is handled as one solid block below (same shape the lift maths uses)
                    if (Physics.ComputePenetration(_probe, p, Quaternion.identity, col, col.transform.position, col.transform.rotation, out Vector3 dir, out float dist))
                    {
                        p += dir * dist;
                        normalSum += dir;
                        touched = true;

                        // A heavy chain shoves loose bodies it lands on or sweeps through (equal and opposite push).
                        Rigidbody other = col.attachedRigidbody;
                        if (other != null && !other.isKinematic && other != _ringRb && pm > 0f)
                        {
                            Vector3 impulse = -dir * Mathf.Min(pm * dist / Mathf.Max(1e-4f, _simStepDt), 0.5f);
                            other.AddForceAtPosition(impulse, p, ForceMode.Impulse);
                        }
                    }
                }
                if (PushOutOfMouldBlock(ref p, r, out Vector3 blockNormal))
                {
                    normalSum += blockNormal;
                    touched = true;
                }
                if (p.y < floorY)
                {
                    p.y = floorY;
                    normalSum += Vector3.up;
                    touched = true;
                }

                if (touched)
                {
                    Vector3 nrm = normalSum.sqrMagnitude > 1e-8f ? normalSum.normalized : Vector3.up;
                    float impact = -Vector3.Dot(vIn, nrm) / Mathf.Max(1e-4f, _simStepDt);
                    if (impact > linkImpactMinSpeed)
                        LinkImpact?.Invoke(p, impact);
                    Vector3 v = p - end.prev[i];
                    float vn = Vector3.Dot(v, nrm);
                    if (vn < 0f) v -= vn * nrm;                 // no bouncing into the surface
                    Vector3 vt = v - Vector3.Dot(v, nrm) * nrm;
                    v -= vt * chainContactFriction;              // links grip what they lie on
                    end.prev[i] = p - v;
                }
                end.contact[i] = touched;
                end.pos[i] = p;
            }
        }

        /// <summary>
        /// The mould as one solid block (Body Half Extents XZ, 0 .. Body Top Height, mould-root space): a link inside is pushed
        /// out through the nearest side or the top. Treating the open cavity, nuts and rods as one block keeps the chain from
        /// snagging on part edges and matches the geometry the lift uses, so socketed legs settle instead of jittering.
        /// </summary>
        private bool PushOutOfMouldBlock(ref Vector3 p, float r, out Vector3 worldNormal)
        {
            worldNormal = Vector3.zero;
            Vector3 l = moldRoot.InverseTransformPoint(p);
            float hx = bodyHalfExtentsXZ.x + r, hz = bodyHalfExtentsXZ.y + r, top = bodyTopHeight + r;
            if (l.y > top || l.y < -0.02f || Mathf.Abs(l.x) > hx || Mathf.Abs(l.z) > hz)
                return false;

            float dTop = top - l.y;
            float dX = hx - Mathf.Abs(l.x);
            float dZ = hz - Mathf.Abs(l.z);
            Vector3 n;
            if (dTop <= dX && dTop <= dZ) { l.y = top; n = Vector3.up; }
            else if (dX <= dZ) { l.x = Mathf.Sign(l.x) * hx; n = new Vector3(Mathf.Sign(l.x), 0f, 0f); }
            else { l.z = Mathf.Sign(l.z) * hz; n = new Vector3(0f, 0f, Mathf.Sign(l.z)); }

            p = moldRoot.TransformPoint(l);
            worldNormal = moldRoot.TransformDirection(n);
            return true;
        }

        private void ApplyLinkTransforms(ChainEnd end)
        {
            if (!end.simReady)
                return;
            int n = end.pos.Length;
            for (int i = 0; i < n; i++)
            {
                Transform link = end.links[i];
                if (link == null) continue;

                Vector3 prev = i > 0 ? end.pos[i - 1] : end.pos[i];
                Vector3 next = i < n - 1 ? end.pos[i + 1] : end.pos[i];
                Vector3 tangent = prev - next;
                tangent = tangent.sqrMagnitude > 1e-8f ? tangent.normalized : Vector3.up;

                Vector3 sideRef = Vector3.Cross(tangent, Vector3.right);
                if (sideRef.sqrMagnitude < 1e-4f)
                    sideRef = Vector3.Cross(tangent, Vector3.forward);
                sideRef.Normalize();

                Quaternion rot = Quaternion.LookRotation(tangent, sideRef) * Quaternion.Euler(0f, 0f, (i & 1) == 0 ? 0f : 90f);
                link.SetPositionAndRotation(end.pos[i], rot);
            }
        }

        /// <summary>Adds points from p to q (q included, p only if the polyline is empty), bulging so the arc length is about desiredLength.</summary>
        private void AppendSaggedSegment(Vector3 p, Vector3 q, float desiredLength, Vector3 moldCenter, bool includeStart)
        {
            Vector3 d = q - p;
            float len = d.magnitude;
            Vector3 t = len > 1e-5f ? d / len : Vector3.down;

            float sag = 0f;
            if (desiredLength > len + 1e-4f && len > 1e-4f)
                sag = Mathf.Min(Mathf.Sqrt(3f * len * (desiredLength - len) / 8f), desiredLength * 0.5f);
            else if (len <= 1e-4f)
                sag = desiredLength * 0.5f;

            Vector3 sagDir = Vector3.down - Vector3.Dot(Vector3.down, t) * t;
            if (sagDir.sqrMagnitude < 0.09f)
            {
                Vector3 outward = 0.5f * (p + q) - moldCenter;
                outward.y = 0f;
                if (outward.sqrMagnitude < 1e-4f) outward = Vector3.right;
                sagDir = outward - Vector3.Dot(outward, t) * t;
                if (sagDir.sqrMagnitude < 1e-6f) sagDir = Vector3.Cross(t, Vector3.forward);
            }
            sagDir.Normalize();

            int start = includeStart && _polyline.Count == 0 ? 0 : 1;
            for (int i = start; i <= PolylineSamplesPerFreeSegment; i++)
            {
                float u = (float)i / PolylineSamplesPerFreeSegment;
                _polyline.Add(Vector3.Lerp(p, q, u) + sagDir * (4f * u * (1f - u) * sag));
            }
        }

        private void ResamplePolyline(int count)
        {
            _cumulative.Clear();
            float acc = 0f;
            _cumulative.Add(0f);
            for (int i = 1; i < _polyline.Count; i++)
            {
                acc += Vector3.Distance(_polyline[i - 1], _polyline[i]);
                _cumulative.Add(acc);
            }

            int seg = 0;
            for (int i = 0; i < count; i++)
            {
                float target = acc * i / (count - 1);
                while (seg < _polyline.Count - 2 && _cumulative[seg + 1] < target)
                    seg++;
                float segLen = _cumulative[seg + 1] - _cumulative[seg];
                float u = segLen > 1e-6f ? (target - _cumulative[seg]) / segLen : 0f;
                _linkPositions[i] = Vector3.Lerp(_polyline[seg], _polyline[seg + 1], Mathf.Clamp01(u));
            }
        }

        private Vector3 KeepOutOfMouldAndFloor(Vector3 p, float floorY)
        {
            Vector3 local = moldRoot.InverseTransformPoint(p);
            if (Mathf.Abs(local.x) < bodyHalfExtentsXZ.x && Mathf.Abs(local.z) < bodyHalfExtentsXZ.y &&
                local.y < bodyTopHeight + 0.012f && local.y > -0.05f)
            {
                local.y = bodyTopHeight + 0.012f;
                p = moldRoot.TransformPoint(local);
            }
            if (p.y < floorY)
                p.y = floorY;
            return p;
        }

        // ------------------------------------------------------------------ surfaces

        /// <summary>Lowest the Ring centre may go at this XZ: resting on the mould top or on the floor below it.</summary>
        private float RingFloorY(Vector3 ringPos)
        {
            Vector3 local = moldRoot.InverseTransformPoint(ringPos);
            if (Mathf.Abs(local.x) < bodyHalfExtentsXZ.x + 0.04f && Mathf.Abs(local.z) < bodyHalfExtentsXZ.y + 0.04f)
                return moldRoot.TransformPoint(new Vector3(0f, bodyTopHeight, 0f)).y + ringRestHeight;
            return SampleFloorY(ringPos, true) + ringRestHeight;
        }

        /// <summary>Support surface under the mould (ignores the mould, the sling, the hook and ratchet belts).</summary>
        public float SampleSupportSurfaceY(Vector3 worldXZ) => SampleFloorY(worldXZ, false);

        private float SampleFloorY(Vector3 world, bool includeMould)
        {
            float startY = Mathf.Max(world.y, moldRoot != null ? moldRoot.position.y : 0f) + 0.45f;
            Vector3 origin = new Vector3(world.x, startY, world.z);
            int n = Physics.RaycastNonAlloc(origin, Vector3.down, _hits, 20f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float best = float.NegativeInfinity;
            float bestDist = float.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                Collider col = _hits[i].collider;
                if (col == null) continue;
                Transform ct = col.transform;
                if (chainsRoot != null && ct.IsChildOf(chainsRoot)) continue;
                if (!includeMould && moldRoot != null && ct.IsChildOf(moldRoot)) continue;
                if (currentHookMount != null && ct.IsChildOf(currentHookMount.root)) continue;
                if (col.name.Contains("RatchetBelt") || (ct.root != null && ct.root.name.Contains("RatchetBelt"))) continue;
                if (col.attachedRigidbody != null && !col.attachedRigidbody.isKinematic) continue;
                if (_hits[i].point.y > world.y + 0.05f && includeMould) continue;
                if (_hits[i].distance < bestDist)
                {
                    bestDist = _hits[i].distance;
                    best = _hits[i].point.y;
                }
            }
            return float.IsNegativeInfinity(best) ? minGroundY : Mathf.Max(minGroundY, best);
        }

        private void SyncInstalledMoldPartsPhysics()
        {
            if (!Application.isPlaying)
                return;

            Physics.SyncTransforms();
            var couplers = moldRoot.GetComponentsInChildren<MoldVibroPlatformCoupler>(true);
            for (int i = 0; i < couplers.Length; i++)
            {
                if (couplers[i] != null)
                    couplers[i].SetRestWorldPose(couplers[i].transform.position, couplers[i].transform.rotation);
            }
        }

        // ------------------------------------------------------------------ helpers

        private static bool IsSocketInteractor(IXRInteractor interactor) => interactor is XRSocketInteractor;

        private static bool IsHeldByHand(XRGrabInteractable grab)
        {
            if (grab == null || !grab.isSelected)
                return false;
            var list = grab.interactorsSelecting;
            for (int i = 0; i < list.Count; i++)
            {
                if (!(list[i] is XRSocketInteractor))
                    return true;
            }
            return false;
        }

        private bool IsSocketed(ChainEnd end) => ActiveSnap(end) != null;

        /// <summary>The snap point currently holding this chain end, or null.</summary>
        private ChainSnapPoint ActiveSnap(ChainEnd end)
        {
            if (end.grab == null)
                return null;
            for (int i = 0; i < _snapPoints.Count; i++)
            {
                ChainSnapPoint snap = _snapPoints[i];
                XRSocketInteractor socket = snap != null ? snap.Socket : null;
                if (socket != null && socket.hasSelection && ReferenceEquals(socket.firstInteractableSelected, end.grab))
                    return snap;
            }
            return null;
        }

        /// <summary>Snap point whose geometry a socketed chain end follows (the active one, else its home one).</summary>
        private ChainSnapPoint RouteSnap(ChainEnd end) => ActiveSnap(end) ?? end.homeSnap;

        private float SocketedBight(ChainEnd end)
        {
            ChainSnapPoint snap = RouteSnap(end);
            return snap != null ? snap.bightHalfLength : socketedBightHalfLength;
        }

        /// <summary>
        /// Decides when a chain goes onto a snap point. The capture zone is the snap point's whole chain path (down the end
        /// of the mould and along its bottom ledge), so the player only has to bring the chain to the end of the mould, at
        /// any height, holding it by any link. With 'Snap While Held' the chain goes on as soon as it gets there; otherwise
        /// when the hand lets go there. A loose chain never snaps on by itself, and a chain a hand has just pulled off must
        /// leave the zone before it can go back on.
        /// </summary>
        private void UpdateSnapping(ChainEnd end)
        {
            if (end.grab == null)
                return;

            bool released = end.snapOnReleasePending;
            end.snapOnReleasePending = false;

            if (IsSocketed(end))
            {
                end.wasSocketed = true;
                end.resnapBlocked = false;
                return;
            }

            bool held = IsHeldByHand(end.grab);
            if (end.wasSocketed)
            {
                end.wasSocketed = false;
                end.resnapBlocked = held;
            }

            if (held)
            {
                end.lastHeldPos = HeldPoint(end);
            }
            else if (!released)
            {
                end.resnapBlocked = false;
                return;
            }

            ChainSnapPoint snap = FindSnapFor(end, end.lastHeldPos, out float nearness);
            if (end.resnapBlocked)
            {
                if (!held || nearness > 1.3f)
                    end.resnapBlocked = false;
                return;
            }
            if (snap == null || nearness > 1f || isMoldSuspended)
                return;
            if (held && !snap.snapWhileHeld)
                return;
            if (!CanReachSnap(end, snap))
            {
                if (released)
                    Debug.Log($"[MoldCraneLiftingPoint] {end.label} cannot reach '{snap.name}' from where the Ring is - the chain is too short. " +
                              "Bring the crane hook down over the middle of the mould, or take the Ring off the hook first.");
                return;
            }
            SnapInto(end, snap);
        }

        /// <summary>
        /// Free snap point this chain is closest to. <paramref name="nearness"/> is the distance from the snap point's chain
        /// path divided by its capture radius (1 or less = inside its capture zone).
        /// </summary>
        private ChainSnapPoint FindSnapFor(ChainEnd end, Vector3 heldPoint, out float nearness)
        {
            int chainIndex = end == _end1 ? 0 : 1;
            ChainSnapPoint best = null;
            nearness = float.MaxValue;
            for (int i = 0; i < _snapPoints.Count; i++)
            {
                ChainSnapPoint snap = _snapPoints[i];
                XRSocketInteractor socket = snap != null ? snap.Socket : null;
                if (socket == null || !snap.isActiveAndEnabled || !socket.socketActive || socket.hasSelection || !snap.Accepts(chainIndex))
                    continue;
                float n = snap.DistanceToPath(heldPoint) / Mathf.Max(0.01f, snap.captureRadius);
                if (n < nearness)
                {
                    nearness = n;
                    best = snap;
                }
            }
            return best;
        }

        /// <summary>Takes the chain out of the hand (if any) and puts its bottom loop on the snap point.</summary>
        private bool SnapInto(ChainEnd end, ChainSnapPoint snap)
        {
            var manager = end.grab.interactionManager;
            if (manager == null)
                return false;

            ReleaseFromHand(end.grab, $"{end.label} snapped onto '{snap.name}'.");

            _forceAcceptGrab = end.grab;
            try
            {
                manager.SelectEnter((IXRSelectInteractor)snap.Socket, (IXRSelectInteractable)end.grab);
            }
            finally
            {
                _forceAcceptGrab = null;
            }

            end.grabLinkIndex = -1;
            end.snapOnReleasePending = false;
            if (!IsSocketed(end))
            {
                Debug.LogWarning($"[MoldCraneLiftingPoint] {end.label} could not snap onto '{snap.name}': socketActive={snap.Socket.socketActive}, " +
                                 $"socketHasSelection={snap.Socket.hasSelection}, chainStillSelected={end.grab.isSelected}, " +
                                 $"layers socket={snap.Socket.interactionLayers.value}/chain={end.grab.interactionLayers.value}.");
                return false;
            }

            end.wasSocketed = true;
            ParkRingOnMouldTop();
            return true;
        }

        /// <summary>
        /// Sets a loose Ring (not on the crane hook, not in a hand) on the middle of the mould top, turned so each chain leaves
        /// it towards the end of the mould it is snapped on. See <see cref="parkRingOnMouldWhenSnapped"/>.
        /// </summary>
        private void ParkRingOnMouldTop()
        {
            if (!parkRingOnMouldWhenSnapped || _ringRb == null || moldRoot == null || isAttached || IsHeldByHand(centralRingGrab))
                return;

            bool flip = false;
            ChainSnapPoint s1 = ActiveSnap(_end1), s2 = ActiveSnap(_end2);
            if (s1 != null) flip = moldRoot.InverseTransformPoint(s1.Center).x > 0f;
            else if (s2 != null) flip = moldRoot.InverseTransformPoint(s2.Center).x < 0f;

            _ringRestLocalPos = new Vector3(0f, bodyTopHeight + ringRestHeight, 0f);
            _ringRestLocalRot = Quaternion.Euler(-90f, flip ? 180f : 0f, 0f);
            _ringResting = true;
            SetRingDrivenByScript(true);
            Vector3 wp = moldRoot.TransformPoint(_ringRestLocalPos);
            Quaternion wr = moldRoot.rotation * _ringRestLocalRot;
            _ringRb.position = wp;
            _ringRb.rotation = wr;
            centralRing.SetPositionAndRotation(wp, wr);
        }

        /// <summary>Shows each free snap point's guide while a chain it accepts is in a hand.</summary>
        private void UpdateSnapGuides()
        {
            bool held1 = IsHeldByHand(_end1.grab);
            bool held2 = IsHeldByHand(_end2.grab);
            for (int i = 0; i < _snapPoints.Count; i++)
            {
                ChainSnapPoint snap = _snapPoints[i];
                if (snap == null)
                    continue;
                XRSocketInteractor socket = snap.Socket;
                bool free = socket != null && socket.socketActive && !socket.hasSelection && snap.isActiveAndEnabled;
                bool show1 = free && held1 && snap.Accepts(0);
                bool show2 = free && held2 && snap.Accepts(1);
                bool inRange = (show1 && snap.DistanceToPath(HeldPoint(_end1)) <= snap.captureRadius)
                            || (show2 && snap.DistanceToPath(HeldPoint(_end2)) <= snap.captureRadius);
                bool reachable = (show1 && CanReachSnap(_end1, snap)) || (show2 && CanReachSnap(_end2, snap));
                snap.UpdateGuide(show1 || show2, inRange, reachable);
            }
        }

        // ------------------------------------------------------------------ editor setup

        /// <summary>
        /// Edit-mode helper: moves the chain sockets onto the pallet end ledges, places the chain ends in them,
        /// rests the Ring on the mould top and poses all links. Also used to preview the sling in the Scene view.
        /// </summary>
        [ContextMenu("Snap Sling To Rest Pose (sockets on pallet end ledges)")]
        public void SnapToRestPose()
        {
            _initialised = false;
            Initialise();
            if (moldRoot == null || centralRing == null)
                return;

            PlaceSocket(_end1);
            PlaceSocket(_end2);

            Vector3 ringPos = moldRoot.TransformPoint(new Vector3(0f, bodyTopHeight + ringRestHeight, 0f));
            centralRing.SetPositionAndRotation(ringPos, UprightRingRot);

            PoseRestChain(_end1);
            PoseRestChain(_end2);
        }

        /// <summary>
        /// Edit-mode helper: replaces the Ring's solid collider with a hoop of capsules that follows the ring mesh,
        /// so a hook, peg or bar can pass through the hole and the Ring can hang on it. Also points the Ring's
        /// XRGrabInteractable at the new colliders.
        /// </summary>
        [ContextMenu("Build Ring Hoop Colliders (lets the Ring hang on hooks)")]
        public void BuildRingColliders()
        {
            ResolveReferences();
            if (centralRing == null)
                return;
            var mf = centralRing.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null)
                return;

            // Ring plane = local XZ, thickness along local Y (the mesh is a torus around local Y).
            var verts = mf.sharedMesh.vertices;
            Vector3 centre = mf.sharedMesh.bounds.center;
            float rMin = float.MaxValue, rMax = 0f;
            foreach (var v in verts)
            {
                float r = new Vector2(v.x - centre.x, v.z - centre.z).magnitude;
                rMin = Mathf.Min(rMin, r);
                rMax = Mathf.Max(rMax, r);
            }
            float tube = Mathf.Max(0.001f, 0.5f * (rMax - rMin));
            float mid = 0.5f * (rMax + rMin);
            const int segments = 12;
            float segLen = 2f * Mathf.PI * mid / segments;

            for (int i = centralRing.childCount - 1; i >= 0; i--)
            {
                var c = centralRing.GetChild(i);
                if (c.name.StartsWith("RingCollider_"))
                    DestroyRingChild(c.gameObject);
            }

            var cols = new List<Collider>(segments);
            for (int i = 0; i < segments; i++)
            {
                float a = (i + 0.5f) * Mathf.PI * 2f / segments;
                var go = new GameObject($"RingCollider_{i:00}");
#if UNITY_EDITOR
                UnityEditor.Undo.RegisterCreatedObjectUndo(go, "Build Ring Hoop Colliders");
#endif
                go.layer = centralRing.gameObject.layer;
                go.transform.SetParent(centralRing, false);
                go.transform.localPosition = centre + new Vector3(Mathf.Cos(a) * mid, 0f, Mathf.Sin(a) * mid);
                Vector3 tangent = new Vector3(-Mathf.Sin(a), 0f, Mathf.Cos(a));
                go.transform.localRotation = Quaternion.LookRotation(tangent, Vector3.up);
                var cap = go.AddComponent<CapsuleCollider>();
                cap.direction = 2;
                cap.radius = tube;
                cap.height = segLen + 2f * tube;
                cols.Add(cap);
            }

            foreach (var box in centralRing.GetComponents<BoxCollider>())
                box.enabled = false;

            if (centralRingGrab != null)
            {
                centralRingGrab.colliders.Clear();
                centralRingGrab.colliders.AddRange(cols);
#if UNITY_EDITOR
                UnityEditor.EditorUtility.SetDirty(centralRingGrab);
#endif
            }
            Debug.Log($"[MoldCraneLiftingPoint] Ring hoop: {segments} capsules, centre radius {mid:F3}, tube radius {tube:F3} (ring-local units).");
        }

        private static void DestroyRingChild(GameObject go)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) { UnityEditor.Undo.DestroyObjectImmediate(go); return; }
#endif
            Destroy(go);
        }

        private void PlaceSocket(ChainEnd end)
        {
            // A ChainSnapPoint is placed by hand: leave it where it is and only put the chain end on it.
            if (end.homeSnap != null)
            {
                if (end.grab != null)
                    end.grab.transform.SetPositionAndRotation(end.homeSnap.Center, Quaternion.LookRotation(end.homeSnap.Axis, Vector3.up));
                return;
            }

            Vector3 p = moldRoot.TransformPoint(new Vector3(end.side * ledgeHalfSpanX, ledgeHeight, 0f));
            if (end.socket != null)
                end.socket.transform.SetPositionAndRotation(p, moldRoot.rotation);
            if (end.grab != null)
                end.grab.transform.SetPositionAndRotation(p, moldRoot.rotation);
        }

        private void PoseRestChain(ChainEnd end)
        {
            // In edit mode the socket has no live selection yet, so pose as socketed by temporarily treating it as such.
            if (end.links.Count < 4 || end.grab == null)
                return;

            float total = TotalChainLength(end);
            Vector3 aFront = centralRing.position + RingAttachOffsetWorld(end.frontOffsetIndex);
            Vector3 aBack = centralRing.position + RingAttachOffsetWorld(end.backOffsetIndex);
            GetLegLowerPoints(end, true, true, aFront, out Vector3 cFront, out bool edgeFront, out Vector3 eFront);
            GetLegLowerPoints(end, false, true, aBack, out Vector3 cBack, out bool edgeBack, out Vector3 eBack);

            Vector3 fFront = edgeFront ? eFront : cFront;
            Vector3 fBack = edgeBack ? eBack : cBack;
            float fixedLen = Vector3.Distance(cFront, cBack) + (edgeFront ? Vector3.Distance(eFront, cFront) : 0f) + (edgeBack ? Vector3.Distance(eBack, cBack) : 0f);
            float dF = Vector3.Distance(aFront, fFront), dB = Vector3.Distance(aBack, fBack);
            float slack = Mathf.Max(0f, total - fixedLen - dF - dB);
            Vector3 moldCenter = moldRoot.TransformPoint(new Vector3(0f, bodyTopHeight * 0.5f, 0f));

            _polyline.Clear();
            AppendSaggedSegment(aFront, fFront, dF + slack * 0.5f, moldCenter, true);
            if (edgeFront) _polyline.Add(cFront);
            _polyline.Add(cBack);
            if (edgeBack) _polyline.Add(eBack);
            AppendSaggedSegment(fBack, aBack, dB + slack * 0.5f, moldCenter, false);
            ResamplePolyline(end.links.Count);

            float floor = SampleFloorY(end.grab.transform.position, true) + 0.012f;
            for (int i = 0; i < end.links.Count; i++)
            {
                Vector3 p = KeepOutOfMouldAndFloor(_linkPositions[i], floor);
                Vector3 prev = i > 0 ? _linkPositions[i - 1] : _linkPositions[i];
                Vector3 next = i < end.links.Count - 1 ? _linkPositions[i + 1] : _linkPositions[i];
                Vector3 tangent = (prev - next).sqrMagnitude > 1e-8f ? (prev - next).normalized : Vector3.up;
                Vector3 sideRef = Vector3.Cross(tangent, Vector3.right);
                if (sideRef.sqrMagnitude < 1e-4f) sideRef = Vector3.Cross(tangent, Vector3.forward);
                Quaternion rot = Quaternion.LookRotation(tangent, sideRef.normalized) * Quaternion.Euler(0f, 0f, (i & 1) == 0 ? 0f : 90f);
                end.links[i].SetPositionAndRotation(p, rot);
            }
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (moldRoot == null)
                return;
            Gizmos.color = new Color(1f, 0.3f, 0.2f, 0.8f);
            for (int s = -1; s <= 1; s += 2)
            {
                Vector3 a = moldRoot.TransformPoint(new Vector3(s * ledgeHalfSpanX, ledgeHeight, -socketedBightHalfLength));
                Vector3 b = moldRoot.TransformPoint(new Vector3(s * ledgeHalfSpanX, ledgeHeight, socketedBightHalfLength));
                Gizmos.DrawLine(a, b);
                Gizmos.DrawWireSphere(0.5f * (a + b), 0.05f);
            }
        }
#endif
    }
}
