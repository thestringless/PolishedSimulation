namespace NOT_Lonely
{
    using UnityEngine;

#if UNITY_EDITOR
    [ExecuteInEditMode]
#endif

    public class NL_Remote : MonoBehaviour
    {
        [HideInInspector] public Transform cable_remoteMount;
        [HideInInspector] public SkinnedMeshRenderer cable_mesh;
        [HideInInspector] public ConfigurableJoint joint;
        [HideInInspector] public MaterialPropertyBlock mtlPropBlock;

        public bool enableKeyboardControls = false;

        public NL_RemoteButton[] buttons = new NL_RemoteButton[6];

        [SerializeField] public NL_OverheadCrane crane;

        private bool _wasKeyboardControlsEnabled = false;
        private NL_RemoteButton _btnFront;
        private NL_RemoteButton _btnBack;
        private NL_RemoteButton _btnLeft;
        private NL_RemoteButton _btnRight;
        private NL_RemoteButton _btnUp;
        private NL_RemoteButton _btnDown;

        void Start()
        {
            if (transform.parent != null && transform.parent.name == "Cable_remoteMount")
            {
                cable_remoteMount = transform.parent;
                cable_mesh = FindNeighbourMesh(cable_remoteMount);
                joint = cable_remoteMount.GetComponent<ConfigurableJoint>();
            }
            else
            {
                cable_remoteMount = null;
                cable_mesh = null;
                joint = null;
            }

            mtlPropBlock = new MaterialPropertyBlock();
            ResolveCraneReference();
            BindButtonsToCoordinator();
        }

        public NL_OverheadCrane ResolveCraneReference()
        {
            if (crane != null)
                return crane;

            crane = GetComponentInParent<NL_OverheadCrane>();
            if (crane == null)
                crane = FindObjectOfType<NL_OverheadCrane>();

            return crane;
        }

        public void BindButtonsToCoordinator()
        {
            if (buttons == null || buttons.Length < 6)
            {
                var found = GetComponentsInChildren<NL_RemoteButton>(true);
                if (found != null && found.Length > 0)
                    buttons = found;
            }

            _btnFront = null;
            _btnBack = null;
            _btnLeft = null;
            _btnRight = null;
            _btnUp = null;
            _btnDown = null;

            var allBtns = GetComponentsInChildren<NL_RemoteButton>(true);
            for (int i = 0; i < allBtns.Length; i++)
            {
                var b = allBtns[i];
                if (b == null) continue;
                b.managedByParentRemote = true;
                switch (b.Direction)
                {
                    case NL_RemoteButton.ButtonDirection.Front: _btnFront = b; break;
                    case NL_RemoteButton.ButtonDirection.Back: _btnBack = b; break;
                    case NL_RemoteButton.ButtonDirection.Left: _btnLeft = b; break;
                    case NL_RemoteButton.ButtonDirection.Right: _btnRight = b; break;
                    case NL_RemoteButton.ButtonDirection.Up: _btnUp = b; break;
                    case NL_RemoteButton.ButtonDirection.Down: _btnDown = b; break;
                }
            }
        }

        public void StopAllButtonCommandsImmediate()
        {
            var allBtns = GetComponentsInChildren<NL_RemoteButton>(true);
            for (int i = 0; i < allBtns.Length; i++)
            {
                if (allBtns[i] != null)
                    allBtns[i].ClearAllHoldSourcesImmediate(invokeReleaseIfWasPressed: true);
            }
        }

        private void OnEnable()
        {
            ResolveCraneReference();
            BindButtonsToCoordinator();

            if (crane != null)
            {
                crane.OnRemoteHeightChanged -= UpdateRemoteHeight;
                crane.OnRemoteHeightChanged += UpdateRemoteHeight;
                crane.OnKeyboardControlsSwitched -= SwitchKeyboardControls;
                crane.OnKeyboardControlsSwitched += SwitchKeyboardControls;
            }
        }

        private void OnDisable()
        {
            if (crane != null)
            {
                crane.OnRemoteHeightChanged -= UpdateRemoteHeight;
                crane.OnKeyboardControlsSwitched -= SwitchKeyboardControls;
            }

            StopAllButtonCommandsImmediate();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus)
            {
                StopAllButtonCommandsImmediate();
            }
        }

        private void SwitchKeyboardControls(bool value)
        {
            if (enableKeyboardControls)
            {
                ReleaseAllKeyboardHolds();
            }
            enableKeyboardControls = false;
        }

        private void ReleaseAllKeyboardHolds()
        {
            if (buttons == null) return;
            for (int i = 0; i < buttons.Length; i++)
            {
                if (buttons[i] != null)
                    buttons[i].OnRelease();
            }
        }

        private void Update()
        {
            UpdateRemoteCableBounds();

            if (!Application.isPlaying)
                return;

            if (_btnFront == null || _btnBack == null || _btnLeft == null || _btnRight == null || _btnUp == null || _btnDown == null)
            {
                BindButtonsToCoordinator();
            }

            // 1. Keyboard controls disabled: crane is operated via remote buttons only
            if (enableKeyboardControls || _wasKeyboardControlsEnabled)
            {
                enableKeyboardControls = false;
                _wasKeyboardControlsEnabled = false;
                ReleaseAllKeyboardHolds();
            }

            // 2. Ensure all buttons prune any stale interactors and reconcile their pressed state first
            PruneAndReconcileButton(_btnFront);
            PruneAndReconcileButton(_btnBack);
            PruneAndReconcileButton(_btnLeft);
            PruneAndReconcileButton(_btnRight);
            PruneAndReconcileButton(_btnUp);
            PruneAndReconcileButton(_btnDown);

            // 3. Execute momentary hold-to-move commands per axis with conflicting-direction safety:
            //    - If both opposing buttons on the same axis are held simultaneously, neither moves (safe stop).
            //    - If one of the opposing buttons is released while the other is still held, the remaining held button resumes immediately.
            //    - Independent axes (e.g. Front + Left + Up) operate simultaneously without interfering with each other.
            DispatchAxisPair(_btnFront, _btnBack);
            DispatchAxisPair(_btnLeft, _btnRight);
            DispatchAxisPair(_btnUp, _btnDown);
        }

        private void UpdateKeyboardButton(KeyCode key, int buttonIndex)
        {
            if (buttons == null || buttonIndex < 0 || buttonIndex >= buttons.Length)
                return;
            NL_RemoteButton btn = buttons[buttonIndex];
            if (btn == null)
                return;

            if (Input.GetKeyDown(key))
            {
                btn.OnPressed();
            }
            else if (Input.GetKey(key))
            {
                btn.OnHold();
            }
            else if (Input.GetKeyUp(key))
            {
                btn.OnRelease();
            }
        }

        private static void PruneAndReconcileButton(NL_RemoteButton btn)
        {
            if (btn == null) return;
            btn.managedByParentRemote = true;
            btn.PruneStaleHoldSources();
        }

        private static void DispatchAxisPair(NL_RemoteButton positiveBtn, NL_RemoteButton negativeBtn)
        {
            bool posHeld = positiveBtn != null && positiveBtn.IsPressed;
            bool negHeld = negativeBtn != null && negativeBtn.IsPressed;

            // Conflicting opposite inputs on the same axis cancel out safely (zero movement)
            if (posHeld && negHeld)
                return;

            if (posHeld)
            {
                positiveBtn.ExecuteHoldStep();
            }
            else if (negHeld)
            {
                negativeBtn.ExecuteHoldStep();
            }
        }

        private SkinnedMeshRenderer FindNeighbourMesh(Transform neighbour)
        {
            if (neighbour == null || neighbour.parent == null)
                return null;

            SkinnedMeshRenderer mesh = null;

            SkinnedMeshRenderer[] allChildMeshes =
                neighbour.parent.GetComponentsInChildren<SkinnedMeshRenderer>();

            for (int i = 0; i < allChildMeshes.Length; i++)
            {
                if (allChildMeshes[i].transform.parent == neighbour.parent)
                {
                    mesh = allChildMeshes[i];
                }
            }

            return mesh;
        }

        public void UpdateRemoteHeight(float craneHeight, float t)
        {
            if (cable_remoteMount == null || cable_mesh == null)
                return;

            float height = craneHeight - 0.5f;

            cable_remoteMount.localPosition =
                new Vector3(
                    0,
                    Mathf.Lerp(-height, -0.3f, t),
                    0
                );

            if (joint != null)
            {
                joint.anchor =
                    new Vector3(
                        0,
                        -cable_remoteMount.localPosition.y,
                        0
                    );
            }

            mtlPropBlock = new MaterialPropertyBlock();

            mtlPropBlock.SetVector(
                "_TilingXY",
                new Vector2(
                    Mathf.Lerp(height, 0.3f, t) * 0.5f,
                    1
                )
            );

            cable_mesh.SetPropertyBlock(mtlPropBlock);
        }

        public void UpdateRemoteCableBounds()
        {
            if (cable_remoteMount == null || cable_mesh == null)
                return;

            cable_mesh.localBounds =
                new Bounds(
                    new Vector3(
                        cable_remoteMount.localPosition.x / 2,
                        cable_remoteMount.localPosition.y / 2,
                        cable_remoteMount.localPosition.z / 2
                    ),
                    new Vector3(
                        -cable_remoteMount.localPosition.x + 0.024f,
                        -cable_remoteMount.localPosition.y,
                        -cable_remoteMount.localPosition.z + 0.024f
                    )
                );
        }
    }
}
