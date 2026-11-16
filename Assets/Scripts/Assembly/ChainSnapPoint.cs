using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace Sefpro.Assembly
{
    /// <summary>
    /// A place on the mould where the bottom loop (bight) of a sling chain can be snapped in.
    ///
    /// Put this on a GameObject that has an <see cref="XRSocketInteractor"/> and a trigger collider, anywhere under the
    /// MoldCasting root. <see cref="MoldCraneLiftingPoint"/> picks up every snap point under the mould automatically, so
    /// snap points can be moved, added or removed in the editor without code changes.
    ///
    /// Placement:
    ///   - Position = where the middle of the chain's bottom loop rests (e.g. on the pallet end ledge).
    ///   - Blue arrow (local Z) = the direction the bottom loop lies along.
    ///   - The two chain legs rise from the loop ends and, if <see cref="routeOverTopEdge"/> is on, pass over the
    ///     mould's top edge at <see cref="topEdgeHeight"/> above this point before going up to the Ring.
    ///   - The capture zone is the whole panel framed by that chain path (the end face of the mould), widened by
    ///     <see cref="captureRadius"/>: the player brings the chain anywhere against the end of the mould and it snaps on.
    /// Select the GameObject to see the chain path and the capture zone in the Scene view.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(XRSocketInteractor))]
    public class ChainSnapPoint : MonoBehaviour
    {
        public enum AcceptedChain
        {
            /// <summary>Either chain loop can be snapped here (easiest for the player).</summary>
            Either,
            Chain1,
            Chain2
        }

        [Header("Which chain")]
        [Tooltip("Which chain loop this point accepts. 'Either' lets the player put any loop on any side.")]
        public AcceptedChain acceptedChain = AcceptedChain.Either;

        [Header("Snapping")]
        [Tooltip("How close (m) the hand holding the chain has to come to the area framed by the guide line (the end face of the mould, " +
                 "between the two chain legs and the bottom ledge) for the chain to snap on. Larger = easier to attach.")]
        [Min(0.05f)] public float captureRadius = 0.35f;

        [Tooltip("On: the chain snaps on as soon as the hand brings it into the capture zone. Off: it snaps on when the hand lets go there.")]
        public bool snapWhileHeld = true;

        [Tooltip("Show a guide of where the chain will sit while the player is holding a chain loop.")]
        public bool showGuide = true;

        [Tooltip("Optional material for the guide line. Leave empty to use a built-in unlit material.")]
        public Material guideMaterial;

        public Color guideColor = new Color(1f, 0.78f, 0.1f, 0.55f);
        public Color guideInRangeColor = new Color(0.25f, 1f, 0.35f, 0.9f);

        [Tooltip("Guide colour when the chain cannot reach this point from where the Ring is (e.g. the crane hook is too high or too far away).")]
        public Color guideOutOfReachColor = new Color(1f, 0.25f, 0.2f, 0.85f);

        [Header("Chain path")]
        [Tooltip("Half the length of the chain's bottom loop, measured along the blue arrow (m).")]
        [Min(0.02f)] public float bightHalfLength = 0.33f;

        [Tooltip("Route the chain legs over the mould's top edge (needed when the snap point is low on the side of the mould).")]
        public bool routeOverTopEdge = true;

        [Tooltip("Height (m) above this point where the chain legs pass over the mould's top edge.")]
        [Min(0f)] public float topEdgeHeight = 0.79f;

        [Tooltip("Half the distance (m) between the two chain legs where they pass over the top edge, along the blue arrow.")]
        [Min(0f)] public float topEdgeHalfWidth = 0.30f;

        private XRSocketInteractor _socket;
        private LineRenderer _guide;
        private static Material s_defaultGuideMaterial;
        private static readonly Vector3[] s_guidePoints = new Vector3[4];

        public XRSocketInteractor Socket => _socket != null ? _socket : (_socket = GetComponent<XRSocketInteractor>());

        /// <summary>Snap position: the middle of the chain's bottom loop.</summary>
        public Vector3 Center => Socket != null && Socket.attachTransform != null ? Socket.attachTransform.position : transform.position;

        /// <summary>Direction the bottom loop lies along (horizontal).</summary>
        public Vector3 Axis
        {
            get
            {
                Vector3 f = (Socket != null && Socket.attachTransform != null ? Socket.attachTransform : transform).forward;
                f.y = 0f;
                return f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
            }
        }

        public bool Accepts(int chainIndex) =>
            acceptedChain == AcceptedChain.Either ||
            (acceptedChain == AcceptedChain.Chain1 && chainIndex == 0) ||
            (acceptedChain == AcceptedChain.Chain2 && chainIndex == 1);

        /// <summary>Bottom corner of one chain leg (front = +blue arrow side).</summary>
        public Vector3 Corner(bool front) => Center + Axis * ((front ? 1f : -1f) * bightHalfLength);

        /// <summary>Point where one chain leg passes over the mould's top edge.</summary>
        public Vector3 Edge(bool front) => Center + Vector3.up * topEdgeHeight + Axis * ((front ? 1f : -1f) * topEdgeHalfWidth);

        /// <summary>Top of one chain leg as drawn by the guide: the mould's top edge, or a short stub when not routed over it.</summary>
        public Vector3 LegTop(bool front) => routeOverTopEdge ? Edge(front) : Corner(front) + Vector3.up * 0.25f;

        /// <summary>
        /// Distance from a point to the panel framed by the chain path of this snap point (the bottom loop, both legs and
        /// everything between them - the end face of the mould). Zero anywhere on that panel.
        /// </summary>
        public float DistanceToPath(Vector3 p)
        {
            Vector3 cf = Corner(true), cb = Corner(false);
            Vector3 tf = LegTop(true), tb = LegTop(false);
            return Mathf.Min(DistanceToTriangle(p, cf, cb, tf), DistanceToTriangle(p, cb, tb, tf));
        }

        // Closest point on a triangle (Ericson, Real-Time Collision Detection).
        private static float DistanceToTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return Vector3.Distance(p, a);

            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return Vector3.Distance(p, b);

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
                return Vector3.Distance(p, a + ab * (d1 / (d1 - d3)));

            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return Vector3.Distance(p, c);

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
                return Vector3.Distance(p, a + ac * (d2 / (d2 - d6)));

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
                return Vector3.Distance(p, b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))));

            float denom = 1f / (va + vb + vc);
            return Vector3.Distance(p, a + ab * (vb * denom) + ac * (vc * denom));
        }

        /// <summary>Applies the capture radius to the socket's trigger collider.</summary>
        public void ApplyCaptureRadius()
        {
            var sphere = GetComponent<SphereCollider>();
            if (sphere == null && GetComponent<Collider>() == null)
                sphere = gameObject.AddComponent<SphereCollider>();
            if (sphere == null)
                return;
            sphere.isTrigger = true;
            float scale = Mathf.Max(1e-4f, Mathf.Max(Mathf.Abs(transform.lossyScale.x), Mathf.Abs(transform.lossyScale.y), Mathf.Abs(transform.lossyScale.z)));
            sphere.radius = captureRadius / scale;
        }

        private void Awake()
        {
            ApplyCaptureRadius();
            // Chains start loose; they are only snapped in by the player.
            if (Socket != null)
                Socket.startingSelectedInteractable = null;
        }

        private void OnValidate()
        {
            ApplyCaptureRadius();
        }

        /// <summary>Shows or hides the guide. Called by <see cref="MoldCraneLiftingPoint"/> every frame.</summary>
        public void UpdateGuide(bool visible, bool inRange, bool reachable = true)
        {
            if (!showGuide || !visible)
            {
                if (_guide != null && _guide.enabled)
                    _guide.enabled = false;
                return;
            }

            if (_guide == null)
                CreateGuide();

            s_guidePoints[0] = LegTop(true);
            s_guidePoints[1] = Corner(true);
            s_guidePoints[2] = Corner(false);
            s_guidePoints[3] = LegTop(false);
            _guide.SetPositions(s_guidePoints);

            Color c = !reachable ? guideOutOfReachColor : inRange ? guideInRangeColor : guideColor;
            _guide.startColor = c;
            _guide.endColor = c;
            float w = inRange ? 0.035f : 0.022f;
            _guide.startWidth = w;
            _guide.endWidth = w;
            _guide.enabled = true;
        }

        private void CreateGuide()
        {
            var go = new GameObject("ChainSnapGuide") { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(transform, false);
            _guide = go.AddComponent<LineRenderer>();
            _guide.useWorldSpace = true;
            _guide.positionCount = 4;
            _guide.numCornerVertices = 3;
            _guide.numCapVertices = 3;
            _guide.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _guide.receiveShadows = false;
            _guide.sharedMaterial = guideMaterial != null ? guideMaterial : DefaultGuideMaterial();
            _guide.enabled = false;
        }

        private static Material DefaultGuideMaterial()
        {
            if (s_defaultGuideMaterial != null)
                return s_defaultGuideMaterial;
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            s_defaultGuideMaterial = new Material(shader) { name = "ChainSnapGuide (runtime)", hideFlags = HideFlags.DontSave };
            return s_defaultGuideMaterial;
        }

        private void OnDisable()
        {
            if (_guide != null)
                _guide.enabled = false;
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(1f, 0.78f, 0.1f, 0.9f);
            Gizmos.DrawLine(Corner(true), Corner(false));
            Gizmos.DrawSphere(Center, 0.025f);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.78f, 0.1f, 1f);
            Vector3 cf = Corner(true), cb = Corner(false);
            Vector3 tf = LegTop(true), tb = LegTop(false);
            Gizmos.DrawLine(cf, cb);
            Gizmos.DrawLine(cf, tf);
            Gizmos.DrawLine(cb, tb);
            // Capture zone: everything within captureRadius of the chain path.
            Gizmos.color = new Color(0.25f, 1f, 0.35f, 0.35f);
            Gizmos.DrawWireSphere(cf, captureRadius);
            Gizmos.DrawWireSphere(cb, captureRadius);
            Gizmos.DrawWireSphere(tf, captureRadius);
            Gizmos.DrawWireSphere(tb, captureRadius);
            Gizmos.color = Color.blue;
            Gizmos.DrawRay(Center, Axis * (bightHalfLength + 0.1f));
        }
#endif
    }
}
