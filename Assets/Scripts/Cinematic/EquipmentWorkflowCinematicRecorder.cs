using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using LiquidSolidPrototype.Core;
using LiquidSolidPrototype.Input;
using NOT_Lonely;
using Sefpro.Assembly;
using Sefpro.Crane;
using Sefpro.JawTongs;
using Sefpro.RatchetBelt;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Simulation;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace Sefpro.Cinematic
{
    /// <summary>
    /// On-demand Cinemachine 3.x choreographer and 1080p@30fps frame recorder demonstrating the full
    /// 10-step SEFPRO refractory casting workflow inside MainProject.unity.
    /// Does NOT auto-run on Play Mode startup; starts only when StartRecordingSession() is called explicitly.
    /// </summary>
    [DefaultExecutionOrder(600)]
    public class EquipmentWorkflowCinematicRecorder : MonoBehaviour
    {
        private const int OutputWidth = 1920;
        private const int OutputHeight = 1080;
        private const int TargetFps = 30;
        private const int JpgQuality = 92;

        // VibratingPlatform (1) world XZ center in MainProject.unity (straddled by RatchetBelt at -22.72 and RatchetBelt_2 at -22.36)
        private static readonly Vector3 VibroPlatformCenterXZ = new Vector3(-22.54f, 0f, -5.46f);
        // Clean open factory floor landing spot well clear of VibratingPlatform (1) and Hopper
        private static readonly Vector3 OutsideTileFloorTargetXZ = new Vector3(-19.50f, 0f, -6.20f);

        private string _framesDir;
        private string _statusFilePath;
        private bool _isRecording;
        private int _frameIndex;

        private Camera _mainCam;
        private Camera _outputCam;
        private CinemachineBrain _brain;
        private Transform _shotsRoot;
        private CinemachineCamera _activeShot;
        private Vector3 _activeShotDrift;
        private RenderTexture _captureRT;
        private Texture2D _readTex;

        // Scene Equipment References
        private NL_OverheadCrane _crane;
        private NL_Hook _nlHook;
        private Transform _hookMount;
        private Rigidbody _hookRb;
        private NL_AttachCargo _attachCargo;

        private MoldCraneLiftingPoint _moldLP;
        private Transform _moldCasting;
        private Transform _moldCastTransform;
        private MoldAssemblyManager _assemblyMgr;
        private MoldPourReceiver _moldReceiver;

        private RatchetBeltMechanicsController _belt1;
        private RatchetBeltMechanicsController _belt2;

        private VibroControllerPanel _vibroPanel;
        private VibroPushButton _vibroStartBtn;
        private VibroPushButton _vibroStopBtn;

        private HopperCraneLiftingPoint _hopperLP;
        private HopperPourManager _hopperPourMgr;
        private HopperHandwheel _hopperHandwheel;

        private XRElectricScrewdriver _screwdriver;
        private ScrewTighteningController _screwTightening;

        private JawTongsController _tongsController;
        private JawTongsCraneLiftingPoint _tongsLP;

        private Vector3 _initialChainHangerRingPos;
        private Quaternion _initialChainHangerRingRot;
        private Vector3 _initialScrewdriverPos;
        private Quaternion _initialScrewdriverRot;

        // Tracked smooth camera focus proxy
        private Transform _dynamicFocusProxy;

        // Pass-2 pin visual animation override & floor shadow proxies
        private bool _overridePinPoseActive;
        private Vector3 _overridePinLocalOffset;
        private GameObject _shadowMoldCasting;
        private GameObject _shadowHopperStation;

        public static void StartRecordingSession(string outputFramesDir)
        {
            if (!Application.isPlaying)
            {
                Debug.LogError("[CineRecorder] Must be in Play Mode to start recording.");
                return;
            }

            var existing = FindAnyObjectByType<EquipmentWorkflowCinematicRecorder>();
            if (existing != null)
            {
                Destroy(existing.gameObject);
            }

            var go = new GameObject("EquipmentWorkflowCinematicRecorder");
            DontDestroyOnLoad(go);
            var rec = go.AddComponent<EquipmentWorkflowCinematicRecorder>();
            rec.Begin(outputFramesDir);
        }

        private void Begin(string outputFramesDir)
        {
            _framesDir = outputFramesDir;
            Directory.CreateDirectory(_framesDir);
            string parentDir = Directory.GetParent(_framesDir)?.FullName ?? _framesDir;
            _statusFilePath = Path.Combine(parentDir, "recorder_status.txt");
            File.WriteAllText(_statusFilePath, $"STARTING fps={TargetFps} res={OutputWidth}x{OutputHeight}\n");

            Time.captureFramerate = TargetFps;
            Application.runInBackground = true;

            try
            {
                ResolveEquipment();
                SuppressUnwantedUIAndVR();
                SetupCinemachineRig();
                PrepareSceneObjects();

                _isRecording = true;
                _frameIndex = 0;

                StartCoroutine(CaptureEndOfFrameLoop());
                StartCoroutine(RunChoreographySafe());
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }
        }

        private void OnDestroy()
        {
            _isRecording = false;
            Time.captureFramerate = 0;
            if (_outputCam != null) _outputCam.targetTexture = null;
            if (_captureRT != null) { _captureRT.Release(); Destroy(_captureRT); }
            if (_readTex != null) Destroy(_readTex);
        }

        private void ReportError(Exception ex)
        {
            _isRecording = false;
            Time.captureFramerate = 0;
            string msg = $"ERROR frame={_frameIndex} {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n";
            Debug.LogError("[CineRecorder] " + msg);
            if (!string.IsNullOrEmpty(_statusFilePath))
                File.AppendAllText(_statusFilePath, msg);
        }

        private void AppendStatus(string line)
        {
            Debug.Log("[CineRecorder] " + line);
            if (!string.IsNullOrEmpty(_statusFilePath))
                File.AppendAllText(_statusFilePath, line + "\n");
        }

        private void LateUpdate()
        {
            if (!_isRecording) return;

            if (_overridePinPoseActive && _tongsController != null && _tongsController.pinTransform != null && _tongsController.leftJawArmBody != null)
            {
                Transform armTf = _tongsController.leftJawArmBody.transform;
                _tongsController.pinTransform.SetPositionAndRotation(
                    armTf.TransformPoint(_overridePinLocalOffset),
                    armTf.rotation * _tongsController.pinLocalRotationOffset);
            }

            if (_activeShot != null && _activeShotDrift.sqrMagnitude > 1e-8f)
            {
                _activeShot.transform.position += _activeShotDrift * Time.deltaTime;
            }

            if (_outputCam != null && _mainCam != null)
            {
                _mainCam.transform.SetPositionAndRotation(_outputCam.transform.position, _outputCam.transform.rotation);
                _mainCam.fieldOfView = _outputCam.fieldOfView;
            }
        }

        private IEnumerator CaptureEndOfFrameLoop()
        {
            var waitEof = new WaitForEndOfFrame();
            while (_isRecording)
            {
                yield return waitEof;
                if (!_isRecording || _outputCam == null || _captureRT == null || _readTex == null)
                    yield break;

                RenderTexture prevActive = RenderTexture.active;
                RenderTexture prevTarget = _outputCam.targetTexture;
                try
                {
                    _outputCam.targetTexture = _captureRT;
                    _outputCam.Render();
                    RenderTexture.active = _captureRT;
                    _readTex.ReadPixels(new Rect(0, 0, OutputWidth, OutputHeight), 0, 0, false);
                    _readTex.Apply(false);
                }
                finally
                {
                    _outputCam.targetTexture = prevTarget;
                    RenderTexture.active = prevActive;
                }

                byte[] jpg = ImageConversion.EncodeToJPG(_readTex, JpgQuality);
                string filePath = Path.Combine(_framesDir, $"frame_{_frameIndex:D5}.jpg");
                File.WriteAllBytes(filePath, jpg);
                _frameIndex++;
            }
        }

        private IEnumerator RunChoreographySafe()
        {
            yield return null; // allow frame 0 cleanly

            IEnumerator flow = RunFull10StepWorkflow();
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = flow.MoveNext();
                }
                catch (Exception ex)
                {
                    ReportError(ex);
                    yield break;
                }

                if (!hasNext)
                    break;

                yield return flow.Current;
            }

            // Capture one extra beat before closing out
            yield return WaitSeconds(0.5f);
            _isRecording = false;
            Time.captureFramerate = 0;
            AppendStatus($"COMPLETE totalFrames={_frameIndex}");
        }

        // =========================================================================
        // 1. Setup & Suppression
        // =========================================================================

        private void ResolveEquipment()
        {
            _crane = FindAnyObjectByType<NL_OverheadCrane>();
            _nlHook = FindAnyObjectByType<NL_Hook>();
            if (_nlHook == null)
                throw new InvalidOperationException("NL_Hook not found in scene.");
            _hookMount = _nlHook.wireHookMount;
            _hookRb = _nlHook.rb != null ? _nlHook.rb : _hookMount.GetComponent<Rigidbody>();
            _attachCargo = _hookMount != null ? _hookMount.GetComponent<NL_AttachCargo>() : FindAnyObjectByType<NL_AttachCargo>();

            _moldLP = FindAnyObjectByType<MoldCraneLiftingPoint>();
            if (_moldLP == null)
                throw new InvalidOperationException("MoldCraneLiftingPoint not found in scene.");
            _moldLP.ResolveReferences();
            _moldCasting = _moldLP.moldRoot != null ? _moldLP.moldRoot : GameObject.Find("MoldCasting")?.transform;
            _moldCastTransform = _moldLP.moldCastTransform != null ? _moldLP.moldCastTransform : _moldCasting.Find("MoldCast");
            _assemblyMgr = _moldLP.assemblyManager != null ? _moldLP.assemblyManager : FindAnyObjectByType<MoldAssemblyManager>();
            _moldReceiver = FindAnyObjectByType<MoldPourReceiver>();

            var belts = FindObjectsByType<RatchetBeltMechanicsController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var b in belts)
            {
                if (b.beltIndex == 1) _belt1 = b;
                else if (b.beltIndex == 2) _belt2 = b;
            }
            if (_belt1 == null && belts.Length > 0) _belt1 = belts[0];
            if (_belt2 == null && belts.Length > 1) _belt2 = belts[1];

            _vibroPanel = FindAnyObjectByType<VibroControllerPanel>();
            var pushButtons = FindObjectsByType<VibroPushButton>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var btn in pushButtons)
            {
                if (btn.Role == VibroPushButton.ButtonRole.Start) _vibroStartBtn = btn;
                else if (btn.Role == VibroPushButton.ButtonRole.Stop) _vibroStopBtn = btn;
            }

            _hopperLP = FindAnyObjectByType<HopperCraneLiftingPoint>();
            _hopperPourMgr = FindAnyObjectByType<HopperPourManager>();
            _hopperHandwheel = FindAnyObjectByType<HopperHandwheel>();

            _screwdriver = FindAnyObjectByType<XRElectricScrewdriver>();
            _screwTightening = FindAnyObjectByType<ScrewTighteningController>();

            _tongsController = FindAnyObjectByType<JawTongsController>(FindObjectsInactive.Include);
            _tongsLP = FindAnyObjectByType<JawTongsCraneLiftingPoint>(FindObjectsInactive.Include);

            _mainCam = Camera.main;
        }

        private void SuppressUnwantedUIAndVR()
        {
            // Hide floating XR controllers, simulated hands, line renderers & reticles while keeping Main Camera + XRInteractionManager functional
            var sim = FindAnyObjectByType<XRInteractionSimulator>();
            if (sim != null) sim.enabled = false;

            GameObject xrOrigin = GameObject.Find("XR Origin Hands (XR Rig)");
            if (xrOrigin != null)
            {
                Transform camOffset = xrOrigin.transform.Find("Camera Offset");
                if (camOffset != null)
                {
                    foreach (Renderer r in camOffset.GetComponentsInChildren<Renderer>(true))
                    {
                        if (_mainCam != null && r.transform.IsChildOf(_mainCam.transform)) continue;
                        r.enabled = false;
                    }
                    foreach (LineRenderer lr in camOffset.GetComponentsInChildren<LineRenderer>(true))
                    {
                        lr.enabled = false;
                    }
                }
            }

            // Disable RatchetBelt HUD & legacy mouse drag
            var belts = FindObjectsByType<RatchetBeltMechanicsController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var b in belts)
            {
                b.showHUD = false;
                b.enableLegacyMouseDrag = false;
            }

            // Hide floating HopperBailStatusCanvas & __WallLockBadge_* world-space billboards during cinematic recording
            foreach (Canvas c in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (c.name.IndexOf("HopperBailStatusCanvas", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    c.name.IndexOf("__WallLockBadge_", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    c.gameObject.SetActive(false);
                }
            }

            // Disable WallOrderLockVisualizer so red LOCKED badges never pop up during/after Step 8 dismantling
            foreach (var lockVis in FindObjectsByType<WallOrderLockVisualizer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                lockVis.enabled = false;
            }

            // Prevent NL_AttachCargo & DesktopInteractionManager from auto-triggering/hijacking hook state mid-motion
            if (_attachCargo != null)
            {
                _attachCargo.enabled = false;
                foreach (Collider col in _attachCargo.GetComponents<Collider>())
                {
                    if (col != null && col.isTrigger) col.enabled = false;
                }
            }
            if (_hookMount != null)
            {
                foreach (Collider col in _hookMount.GetComponents<Collider>())
                {
                    if (col != null && col.isTrigger) col.enabled = false;
                }
            }
            foreach (var dm in FindObjectsByType<Sefpro.DesktopTesting.DesktopInteractionManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                dm.enabled = false;
            }

            // Disable crane keyboard/remote manual triggers so nothing fights the choreography
            if (_crane != null) _crane.SwitchKeyboardControls(false);
            var nlRemote = FindAnyObjectByType<NL_Remote>();
            if (nlRemote != null)
            {
                nlRemote.enableKeyboardControls = false;
                nlRemote.StopAllButtonCommandsImmediate();
            }
            var freeRemote = FindAnyObjectByType<FreeFloatingCraneRemote>();
            if (freeRemote != null) freeRemote.StopAllCraneButtonsAndMotion();
        }

        private void SetupCinemachineRig()
        {
            _shotsRoot = new GameObject("CineShots").transform;
            _shotsRoot.SetParent(transform, false);

            var proxyGo = new GameObject("DynamicFocusProxy");
            proxyGo.transform.SetParent(transform, false);
            _dynamicFocusProxy = proxyGo.transform;

            var outCamGo = new GameObject("CineRecordOutputCamera");
            outCamGo.transform.SetParent(transform, false);
            _outputCam = outCamGo.AddComponent<Camera>();

            if (_mainCam != null)
            {
                _outputCam.cullingMask = _mainCam.cullingMask;
                _outputCam.clearFlags = _mainCam.clearFlags;
                _outputCam.backgroundColor = _mainCam.backgroundColor;
                _outputCam.allowHDR = _mainCam.allowHDR;
                _outputCam.fieldOfView = 45f;
                _outputCam.nearClipPlane = 0.08f;
                _outputCam.farClipPlane = 200f;
                _outputCam.transform.SetPositionAndRotation(_mainCam.transform.position, _mainCam.transform.rotation);

                var srcUacd = _mainCam.GetComponent<UniversalAdditionalCameraData>();
                var dstUacd = outCamGo.AddComponent<UniversalAdditionalCameraData>();
                if (srcUacd != null)
                {
                    dstUacd.renderPostProcessing = srcUacd.renderPostProcessing;
                    dstUacd.antialiasing = srcUacd.antialiasing;
                    dstUacd.antialiasingQuality = srcUacd.antialiasingQuality;
                    dstUacd.renderShadows = srcUacd.renderShadows;
                }
            }

            // Keep MainCamera enabled (no AudioListener conflict) at lower depth if needed, outputCam drives captureRT
            _outputCam.enabled = true;

            _brain = outCamGo.AddComponent<CinemachineBrain>();
            _brain.ShowDebugText = false;
            _brain.IgnoreTimeScale = false;
            _brain.UpdateMethod = CinemachineBrain.UpdateMethods.LateUpdate;
            _brain.BlendUpdateMethod = CinemachineBrain.BrainUpdateMethods.LateUpdate;
            _brain.DefaultBlend = new CinemachineBlendDefinition(CinemachineBlendDefinition.Styles.EaseInOut, 0.9f);

            _captureRT = new RenderTexture(OutputWidth, OutputHeight, 24, RenderTextureFormat.ARGB32)
            {
                name = "CineCaptureRT",
                antiAliasing = 1
            };
            _captureRT.Create();
            _readTex = new Texture2D(OutputWidth, OutputHeight, TextureFormat.RGB24, false);
        }

        private void PrepareSceneObjects()
        {
            EnsureMoldChainSocketsWired();

            if (_moldLP.centralRing != null)
            {
                _initialChainHangerRingPos = _moldLP.centralRing.position;
                _initialChainHangerRingRot = _moldLP.centralRing.rotation;
            }

            // Reset Hopper and Mold cavity to empty (0%) at frame 0 so Step 5 pours cleanly
            if (_hopperPourMgr != null)
            {
                _hopperPourMgr.targetMoldReceiver = _moldReceiver;
                _hopperPourMgr.ResetProcess();
            }
            if (_moldReceiver != null)
            {
                _moldReceiver.ResetMoldVolume();
            }

            // Hide pre-existing serialized solidified casting block & its floor shadow so Vibro platform starts clean
            GameObject strayBrick = GameObject.Find("Solidified_Refractory_Casting");
            if (strayBrick != null) strayBrick.SetActive(false);
            GameObject strayBrickShadow = GameObject.Find("Shadow_Solidified_Refractory_Casting");
            if (strayBrickShadow != null) strayBrickShadow.SetActive(false);
            foreach (SolidifiedCastingBlock blk in FindObjectsByType<SolidifiedCastingBlock>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (blk != null && blk.gameObject != null)
                    blk.gameObject.SetActive(false);
            }

            _shadowMoldCasting = GameObject.Find("Shadow_MoldCasting");
            _shadowHopperStation = GameObject.Find("Shadow_SEFPRO_Hopper_Station");

            // Unparent ElectricScrewdriver_V3 from MoldCasting BEFORE MoldCasting is lifted, and rest it flat on the floor
            if (_screwdriver != null)
            {
                if (_moldCasting != null && _screwdriver.transform.IsChildOf(_moldCasting))
                {
                    _screwdriver.transform.SetParent(_moldCasting.parent, true);
                }
                var srRb = _screwdriver.GetComponent<Rigidbody>();
                if (srRb != null)
                {
                    if (!srRb.isKinematic)
                    {
                        srRb.linearVelocity = Vector3.zero;
                        srRb.angularVelocity = Vector3.zero;
                    }
                    srRb.collisionDetectionMode = CollisionDetectionMode.Discrete;
                    srRb.isKinematic = true;
                    srRb.useGravity = false;
                }
                _initialScrewdriverPos = new Vector3(-24.25f, 0.045f, -7.35f);
                _initialScrewdriverRot = Quaternion.Euler(0f, 35f, 90f);
                _screwdriver.transform.SetPositionAndRotation(_initialScrewdriverPos, _initialScrewdriverRot);
                if (srRb != null)
                {
                    srRb.position = _initialScrewdriverPos;
                    srRb.rotation = _initialScrewdriverRot;
                }
            }

            // Hide decorative duplicate Jaw Tongs 5 & shadows so JawTongs_Assembly rests cleanly on the floor
            GameObject jt5 = GameObject.Find("Jaw Tongs 5");
            if (jt5 != null) jt5.SetActive(false);
            GameObject sjt5 = GameObject.Find("Shadow_Jaw Tongs 5");
            if (sjt5 != null) sjt5.SetActive(false);
            GameObject sjta = GameObject.Find("Shadow_JawTongs_Assembly");
            if (sjta != null) sjta.SetActive(false);

            // Activate JawTongs_Assembly cleanly at floor level with upright chain apex & cleared stale isAttached=true flag
            if (_tongsController != null)
            {
                _tongsController.transform.SetPositionAndRotation(new Vector3(-21.75365f, 0.005f, -9.645375f), Quaternion.identity);
                _tongsController.gameObject.SetActive(true);
                if (_tongsLP != null)
                {
                    _tongsLP.ResolveReferences();
                    _tongsLP.DetachFromHook();
                }
                DestroyOrphanTongsJoints();
                _tongsController.EngagePin(true);
                SetTongsKinematicRest(true);
                if (_tongsLP != null) _tongsLP.SyncPoseWithPivot();

                if (_tongsController.chainMidpointAnchor != null && _tongsController.chainRope != null)
                {
                    _tongsController.chainMidpointAnchor.localPosition = new Vector3(0f, 0.085f, 0f);
                    foreach (var att in _tongsController.chainRope.GetComponents<Obi.ObiParticleAttachment>())
                    {
                        if (att != null && att.particleGroup != null && att.particleGroup.name == "ChainMidpoint")
                        {
                            att.enabled = false;
                            att.target = _tongsController.chainMidpointAnchor;
                            att.attachmentType = Obi.ObiParticleAttachment.AttachmentType.Static;
                            att.enabled = true;
                            break;
                        }
                    }
                }
            }

            if (_attachCargo != null &&
                !_moldLP.IsAttached &&
                (_hopperLP == null || !_hopperLP.IsAttached) &&
                (_tongsLP == null || !_tongsLP.IsAttached))
            {
                _attachCargo.isAttached = false;
            }
        }

        private void EnsureMoldChainSocketsWired()
        {
            if (_moldLP == null || _moldCasting == null) return;

            ChainSnapPoint[] snaps = _moldCasting.GetComponentsInChildren<ChainSnapPoint>(true);
            ChainSnapPoint leftSnap = null;
            ChainSnapPoint rightSnap = null;
            float minX = 0f;
            float maxX = 0f;

            foreach (ChainSnapPoint sp in snaps)
            {
                if (sp == null || sp.Socket == null) continue;
                float lx = _moldCasting.InverseTransformPoint(sp.Center).x;
                if (lx < 0f && (leftSnap == null || lx < minX))
                {
                    leftSnap = sp;
                    minX = lx;
                }
                if (lx > 0f && (rightSnap == null || lx > maxX))
                {
                    rightSnap = sp;
                    maxX = lx;
                }
            }

            if (leftSnap != null)
            {
                leftSnap.acceptedChain = ChainSnapPoint.AcceptedChain.Either;
                _moldLP.leftBottomEdgeSocket = leftSnap.Socket;
            }
            if (rightSnap != null)
            {
                rightSnap.acceptedChain = ChainSnapPoint.AcceptedChain.Either;
                _moldLP.rightBottomEdgeSocket = rightSnap.Socket;
            }

            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            Type lpType = _moldLP.GetType();
            object end1 = lpType.GetField("_end1", flags)?.GetValue(_moldLP);
            object end2 = lpType.GetField("_end2", flags)?.GetValue(_moldLP);

            if (end1 != null && leftSnap != null)
            {
                end1.GetType().GetField("socket")?.SetValue(end1, leftSnap.Socket);
                end1.GetType().GetField("homeSnap")?.SetValue(end1, leftSnap);
            }
            if (end2 != null && rightSnap != null)
            {
                end2.GetType().GetField("socket")?.SetValue(end2, rightSnap.Socket);
                end2.GetType().GetField("homeSnap")?.SetValue(end2, rightSnap);
            }

            MethodInfo solveMethod = lpType.GetMethod("SolveTautRingLocalHeight", flags);
            FieldInfo tautField = lpType.GetField("_tautRingLocalY", flags);
            if (solveMethod != null && tautField != null)
            {
                float solved = (float)solveMethod.Invoke(_moldLP, null);
                tautField.SetValue(_moldLP, solved);
            }
        }

        private void DestroyOrphanTongsJoints()
        {
            if (_tongsController == null) return;
            StripNullConnectedJoints(_tongsController.leftJawArmBody);
            StripNullConnectedJoints(_tongsController.rightJawArmBody);
        }

        private static void StripNullConnectedJoints(Rigidbody body)
        {
            if (body == null) return;
            foreach (ConfigurableJoint cj in body.GetComponents<ConfigurableJoint>())
            {
                if (cj != null && cj.connectedBody == null)
                {
                    cj.xMotion = ConfigurableJointMotion.Free;
                    cj.yMotion = ConfigurableJointMotion.Free;
                    cj.zMotion = ConfigurableJointMotion.Free;
                    cj.angularXMotion = ConfigurableJointMotion.Free;
                    cj.angularYMotion = ConfigurableJointMotion.Free;
                    cj.angularZMotion = ConfigurableJointMotion.Free;
                    cj.projectionMode = JointProjectionMode.None;
                    Destroy(cj);
                }
            }
        }

        private void SetTongsKinematicRest(bool rest)
        {
            if (_tongsController == null) return;
            if (_tongsController.leftJawArmBody != null)
            {
                _tongsController.leftJawArmBody.isKinematic = rest;
                _tongsController.leftJawArmBody.useGravity = !rest;
            }
            if (_tongsController.rightJawArmBody != null)
            {
                _tongsController.rightJawArmBody.isKinematic = rest;
                _tongsController.rightJawArmBody.useGravity = !rest;
            }
        }

        // =========================================================================
        // 2. Cinemachine Virtual Camera Helpers
        // =========================================================================

        private CinemachineCamera CreateShotCam(string shotName, Vector3 worldPos, Transform target, Vector3 lookOffset, float fov = 44f)
        {
            var go = new GameObject(shotName);
            go.transform.SetParent(_shotsRoot, false);
            go.transform.position = worldPos;

            var vcam = go.AddComponent<CinemachineCamera>();
            vcam.Priority = 0;
            vcam.Lens.FieldOfView = fov;
            vcam.Lens.NearClipPlane = 0.08f;
            vcam.Lens.FarClipPlane = 200f;
            vcam.Target.TrackingTarget = target;

            var lookAt = go.AddComponent<CinemachineHardLookAt>();
            lookAt.LookAtOffset = lookOffset;

            return vcam;
        }

        private void ActivateShot(CinemachineCamera cam, float blendSeconds = 0.85f, Vector3 driftPerSec = default)
        {
            _brain.DefaultBlend = blendSeconds <= 0.02f
                ? new CinemachineBlendDefinition(CinemachineBlendDefinition.Styles.Cut, 0f)
                : new CinemachineBlendDefinition(CinemachineBlendDefinition.Styles.EaseInOut, blendSeconds);

            if (_activeShot != null && _activeShot != cam)
            {
                _activeShot.Priority = 0;
            }

            _activeShot = cam;
            _activeShotDrift = driftPerSec;
            if (_activeShot != null)
            {
                _activeShot.Priority = 20;
            }
        }

        // =========================================================================
        // 3. Plumb Crane IK Solver & Smooth Motion
        // =========================================================================

        private bool SolveCraneCoordsForHookMount(Vector3 targetMountPos, out float solvedBeam, out float solvedTelpher, out float solvedHookHeight)
        {
            solvedBeam = _crane.initialBeamPos;
            solvedTelpher = _crane.initialTelpherPos;
            solvedHookHeight = _crane.hookHeight;

            if (_crane == null || _nlHook == null || _nlHook.hookJoint == null)
                return false;

            float origBeam = _crane.initialBeamPos;
            float origTp = _crane.initialTelpherPos;
            float origHh = _crane.hookHeight;

            float b = origBeam;
            float tp = origTp;
            float hh = origHh;

            Transform connTf = _nlHook.hookJoint.connectedBody != null
                ? _nlHook.hookJoint.connectedBody.transform
                : _nlHook.wireHookMount.parent;

            float beamSpan = Mathf.Max(0.1f, _crane.craneLength - 2.316f);
            float telpherSpan = Mathf.Max(0.1f, _crane.craneWidth - 1.57f);
            float hoistSpan = Mathf.Max(0.1f, (_crane.craneHeight + _nlHook.hookOffset) - 0.5f);

            for (int iter = 0; iter < 14; iter++)
            {
                _crane.initialBeamPos = b;
                _crane.initialTelpherPos = tp;
                _crane.hookHeight = hh;
                _crane.UpdateMainBeamPos(b);
                _crane.UpdateTelpherPos(tp);
                _crane.UpdateHookHeight();

                Vector3 worldConnAnchor = connTf.TransformPoint(_nlHook.hookJoint.connectedAnchor);
                Vector3 eqMountPos = worldConnAnchor - Vector3.up * _nlHook.hookJoint.anchor.y;

                b = Mathf.Clamp01(b - (targetMountPos.x - eqMountPos.x) / beamSpan);
                tp = Mathf.Clamp01(tp - (targetMountPos.z - eqMountPos.z) / telpherSpan);
                hh = Mathf.Clamp01(hh + (targetMountPos.y - eqMountPos.y) / hoistSpan);
            }

            _crane.initialBeamPos = origBeam;
            _crane.initialTelpherPos = origTp;
            _crane.hookHeight = origHh;
            _crane.UpdateMainBeamPos(origBeam);
            _crane.UpdateTelpherPos(origTp);
            _crane.UpdateHookHeight();

            solvedBeam = b;
            solvedTelpher = tp;
            solvedHookHeight = hh;
            return true;
        }

        private Vector3 EvaluatePlumbHookMountPos()
        {
            if (_nlHook == null || _nlHook.hookJoint == null) return _hookMount.position;
            Transform connTf = _nlHook.hookJoint.connectedBody != null
                ? _nlHook.hookJoint.connectedBody.transform
                : _nlHook.wireHookMount.parent;
            Vector3 worldConnAnchor = connTf.TransformPoint(_nlHook.hookJoint.connectedAnchor);
            return worldConnAnchor - Vector3.up * _nlHook.hookJoint.anchor.y;
        }

        private void SetCraneHookKinematicMode(bool kinematic)
        {
            if (_hookRb != null)
            {
                if (!kinematic)
                {
                    _hookRb.isKinematic = false;
                    _hookRb.useGravity = true;
                }
                else
                {
                    if (!_hookRb.isKinematic)
                    {
                        _hookRb.linearVelocity = Vector3.zero;
                        _hookRb.angularVelocity = Vector3.zero;
                    }
                    _hookRb.isKinematic = true;
                    _hookRb.useGravity = false;
                }
            }

            if (_nlHook != null && _nlHook.inertiaBody != null)
            {
                if (!kinematic)
                {
                    _nlHook.inertiaBody.isKinematic = false;
                    _nlHook.inertiaBody.useGravity = true;
                }
                else
                {
                    if (!_nlHook.inertiaBody.isKinematic)
                    {
                        _nlHook.inertiaBody.linearVelocity = Vector3.zero;
                        _nlHook.inertiaBody.angularVelocity = Vector3.zero;
                    }
                    _nlHook.inertiaBody.isKinematic = true;
                    _nlHook.inertiaBody.useGravity = false;
                }
            }
        }

        private void ApplyHookWorldPoseSafe(Vector3 worldPos, Quaternion worldRot, bool useMovePosition = false)
        {
            if (_hookRb != null && !_hookRb.isKinematic && (_nlHook == null || _nlHook.inertiaBody == null || !_nlHook.inertiaBody.isKinematic))
            {
                _nlHook.SetHookWorldPoseAndZeroVelocity(worldPos, worldRot);
                return;
            }

            if (useMovePosition && _hookRb != null && _hookRb.isKinematic)
            {
                _hookRb.MovePosition(worldPos);
                _hookRb.MoveRotation(worldRot);
                if (_nlHook != null && _nlHook.inertiaBody != null && _nlHook.inertiaBody.isKinematic)
                {
                    _nlHook.inertiaBody.MovePosition(worldPos);
                    _nlHook.inertiaBody.MoveRotation(worldRot);
                }
            }
            else
            {
                if (_hookMount != null)
                    _hookMount.SetPositionAndRotation(worldPos, worldRot);

                if (_hookRb != null)
                {
                    _hookRb.position = worldPos;
                    _hookRb.rotation = worldRot;
                }

                if (_nlHook != null && _nlHook.inertiaBody != null)
                {
                    _nlHook.inertiaBody.transform.SetPositionAndRotation(worldPos, worldRot);
                    _nlHook.inertiaBody.position = worldPos;
                    _nlHook.inertiaBody.rotation = worldRot;
                }
            }

            if (_nlHook != null && _nlHook.wires_mesh != null && _hookMount != null)
            {
                _nlHook.wires_mesh.localBounds = new Bounds(
                    new Vector3(_hookMount.localPosition.x / 2f, _hookMount.localPosition.y / 2f, _hookMount.localPosition.z / 2f + 0.488f),
                    new Vector3(-(Mathf.Abs(_hookMount.localPosition.x) + 0.4f), -_hookMount.localPosition.y, -(Mathf.Abs(_hookMount.localPosition.z) + 0.5f)));
            }
        }

        private IEnumerator MoveCraneToMountWorld(Vector3 targetMountPos, float duration, Quaternion? targetRot = null)
        {
            SolveCraneCoordsForHookMount(targetMountPos, out float endB, out float endTp, out float endHh);

            float startB = _crane.initialBeamPos;
            float startTp = _crane.initialTelpherPos;
            float startHh = _crane.hookHeight;
            Quaternion startRot = _hookMount.rotation;
            Quaternion endRot = targetRot ?? startRot;

            float elapsed = 0f;
            float dur = Mathf.Max(0.05f, duration);
            while (elapsed < dur)
            {
                elapsed += Time.deltaTime;
                float u = Mathf.Clamp01(elapsed / dur);
                float s = Mathf.SmoothStep(0f, 1f, u);

                _crane.initialBeamPos = Mathf.Lerp(startB, endB, s);
                _crane.initialTelpherPos = Mathf.Lerp(startTp, endTp, s);
                _crane.hookHeight = Mathf.Lerp(startHh, endHh, s);
                _crane.UpdateMainBeamPos(_crane.initialBeamPos);
                _crane.UpdateTelpherPos(_crane.initialTelpherPos);
                _crane.UpdateHookHeight();

                Vector3 plumbPos = EvaluatePlumbHookMountPos();
                Quaternion rot = targetRot.HasValue ? Quaternion.Slerp(startRot, endRot, s) : startRot;
                ApplyHookWorldPoseSafe(plumbPos, rot);
                Physics.SyncTransforms();

                yield return null;
            }

            _crane.initialBeamPos = endB;
            _crane.initialTelpherPos = endTp;
            _crane.hookHeight = endHh;
            _crane.UpdateMainBeamPos(endB);
            _crane.UpdateTelpherPos(endTp);
            _crane.UpdateHookHeight();
            ApplyHookWorldPoseSafe(EvaluatePlumbHookMountPos(), endRot);
            Physics.SyncTransforms();
        }

        private IEnumerator MoveCraneToMountWorldFixedUpdate(Vector3 targetMountPos, float duration, Quaternion? targetRot = null)
        {
            SolveCraneCoordsForHookMount(targetMountPos, out float endB, out float endTp, out float endHh);

            float startB = _crane.initialBeamPos;
            float startTp = _crane.initialTelpherPos;
            float startHh = _crane.hookHeight;
            Quaternion startRot = _hookMount.rotation;
            Quaternion endRot = targetRot ?? startRot;

            var waitFixed = new WaitForFixedUpdate();
            float elapsed = 0f;
            float dur = Mathf.Max(0.05f, duration);
            while (elapsed < dur)
            {
                yield return waitFixed;
                elapsed += Time.fixedDeltaTime;
                float u = Mathf.Clamp01(elapsed / dur);
                float s = Mathf.SmoothStep(0f, 1f, u);

                _crane.initialBeamPos = Mathf.Lerp(startB, endB, s);
                _crane.initialTelpherPos = Mathf.Lerp(startTp, endTp, s);
                _crane.hookHeight = Mathf.Lerp(startHh, endHh, s);
                _crane.UpdateMainBeamPos(_crane.initialBeamPos);
                _crane.UpdateTelpherPos(_crane.initialTelpherPos);
                _crane.UpdateHookHeight();

                Vector3 plumbPos = EvaluatePlumbHookMountPos();
                Quaternion rot = targetRot.HasValue ? Quaternion.Slerp(startRot, endRot, s) : startRot;
                ApplyHookWorldPoseSafe(plumbPos, rot, useMovePosition: true);
            }

            yield return waitFixed;
            _crane.initialBeamPos = endB;
            _crane.initialTelpherPos = endTp;
            _crane.hookHeight = endHh;
            _crane.UpdateMainBeamPos(endB);
            _crane.UpdateTelpherPos(endTp);
            _crane.UpdateHookHeight();
            ApplyHookWorldPoseSafe(EvaluatePlumbHookMountPos(), endRot, useMovePosition: true);
            yield return waitFixed;
        }

        private IEnumerator WaitSeconds(float seconds)
        {
            float t = 0f;
            while (t < seconds)
            {
                t += Time.deltaTime;
                yield return null;
            }
        }

        // =========================================================================
        // 4. Master 10-Step Workflow Choreography
        // =========================================================================

        private IEnumerator RunFull10StepWorkflow()
        {
            yield return Step1_MoveCraneToChainAndHook();
            yield return Step2_MoveCraneToMoldAndPlaceOnVibro();
            yield return Step3_TieMoldWithRatchetBelts();
            yield return Step4_StartVibrationViaRemote();
            yield return Step5_HopperPourAndReturnToGround();
            yield return Step6_SetVibrationFrequencyToTopLevel();
            yield return Step7_StopVibrationAndUntieBelts();
            yield return Step8_DismantleMoldWithWrench();
            yield return Step9_PickJawTongsAndPlaceOverMold();
            yield return Step10_RemovePinPickTileAndMoveOutside();
        }

        // -------------------------------------------------------------------------
        // STEP 1: The crane moves to the chain and place the chain to the hook
        // -------------------------------------------------------------------------
        private IEnumerator Step1_MoveCraneToChainAndHook()
        {
            AppendStatus($"[STAGE_START] Step 1 at frame {_frameIndex}");

            var shot1A = CreateShotCam("Shot1A_CraneToChain",
                new Vector3(-21.2f, 2.55f, -10.8f), _hookMount, new Vector3(0f, -0.35f, 0f), 46f);
            ActivateShot(shot1A, 0.0f, new Vector3(-0.18f, -0.04f, -0.25f));

            // OverheadCrane Z max rail travel (tp=1) reaches ~ -14.305f, right in front of the Chain Hanger (-14.848f)
            Vector3 railClosestToChainMount = new Vector3(_initialChainHangerRingPos.x, 2.08f, -14.305f);
            yield return MoveCraneToMountWorld(railClosestToChainMount, 2.4f);

            var shot1B = CreateShotCam("Shot1B_ChainToHookClose",
                new Vector3(-23.75f, 2.02f, -13.10f), _moldLP.centralRing, new Vector3(0f, 0.08f, 0f), 38f);
            ActivateShot(shot1B, 0.75f, new Vector3(-0.04f, 0.02f, -0.05f));
            yield return WaitSeconds(0.45f);

            // Smoothly lift central Ring off the Chain Hanger peg onto the C-hook saddle
            Rigidbody ringRb = _moldLP.centralRing.GetComponent<Rigidbody>();
            if (ringRb != null) ringRb.isKinematic = true;

            Vector3 startRingPos = _moldLP.centralRing.position;
            Quaternion startRingRot = _moldLP.centralRing.rotation;
            Quaternion uprightRingRot = Quaternion.Euler(-90f, 0f, 0f);
            Vector3 ringLocalSeatWorldOffset = uprightRingRot * _moldLP.ringSeatLocalOffset;

            float liftDur = 1.0f;
            float elapsed = 0f;
            while (elapsed < liftDur)
            {
                elapsed += Time.deltaTime;
                float u = Mathf.Clamp01(elapsed / liftDur);
                float s = Mathf.SmoothStep(0f, 1f, u);
                Vector3 saddleWorld = _moldLP.GetHookSaddleWorld(_hookMount);
                Vector3 endRingPos = saddleWorld - ringLocalSeatWorldOffset;

                Vector3 p = Vector3.Lerp(startRingPos, endRingPos, s);
                p.y += Mathf.Sin(u * Mathf.PI) * 0.14f;
                Quaternion r = Quaternion.Slerp(startRingRot, uprightRingRot, s);

                _moldLP.centralRing.SetPositionAndRotation(p, r);
                if (ringRb != null) { ringRb.position = p; ringRb.rotation = r; }
                yield return null;
            }

            _moldLP.AttachToHook(_hookMount, _hookRb, enforceSeatingTolerance: false);
            if (_attachCargo != null) _attachCargo.isAttached = _moldLP.IsAttached;

            // Hoist hook slightly clear of hanger
            yield return MoveCraneToMountWorld(new Vector3(_initialChainHangerRingPos.x, 2.65f, -13.95f), 0.9f);
            AppendStatus($"[STAGE_END] Step 1 at frame {_frameIndex}");
        }

        // -------------------------------------------------------------------------
        // STEP 2: Crane moves to the mold & using the chain places it on vibro platform
        // -------------------------------------------------------------------------
        private IEnumerator Step2_MoveCraneToMoldAndPlaceOnVibro()
        {
            AppendStatus($"[STAGE_START] Step 2 at frame {_frameIndex}");

            var shot2A = CreateShotCam("Shot2A_CraneWithChainToMold",
                new Vector3(-20.4f, 2.40f, -10.5f), _hookMount, new Vector3(0f, -0.55f, 0f), 46f);
            ActivateShot(shot2A, 0.85f, new Vector3(0.10f, -0.05f, 0.15f));

            Vector3 aboveMoldMount = new Vector3(_moldCasting.position.x, _moldCasting.position.y + 1.28f, _moldCasting.position.z);
            yield return MoveCraneToMountWorld(aboveMoldMount, 2.3f);

            var shot2B = CreateShotCam("Shot2B_SnapChainsToMold",
                new Vector3(-21.85f, 1.38f, -9.10f), _moldCasting, new Vector3(0f, 0.42f, 0f), 40f);
            ActivateShot(shot2B, 0.7f, new Vector3(-0.08f, 0.0f, 0.03f));
            yield return WaitSeconds(0.4f);

            // Smoothly bring BOTH Chain_1_U and Chain_2_U loops together and snap them in the same frame
            yield return AnimateAndSnapBothMoldChainEndsSimultaneously(duration: 0.85f);

            var shot2C = CreateShotCam("Shot2C_LiftAndPlaceMold",
                new Vector3(-19.50f, 2.15f, -8.35f), _moldCasting, new Vector3(0f, 0.45f, 0f), 45f);
            ActivateShot(shot2C, 0.8f, new Vector3(0.08f, 0.02f, 0.12f));

            // Hide floor shadow proxy under original MoldCasting spot once lifted
            if (_shadowMoldCasting != null) _shadowMoldCasting.SetActive(false);

            // Hoist mold cleanly into the air
            yield return MoveCraneToMountWorld(new Vector3(_moldCasting.position.x, 2.88f, _moldCasting.position.z), 1.6f);
            AppendStatus($"[MOLD_LIFT] suspended={_moldLP.IsMoldSuspended} moldPos={_moldCasting.position}");

            // Move crane across to center MoldCasting directly over VibratingPlatform (1) & hold briefly so horizontal lag settles
            yield return MoveCraneToMountWorld(new Vector3(VibroPlatformCenterXZ.x, 2.88f, VibroPlatformCenterXZ.z), 2.2f);
            yield return WaitSeconds(0.85f);

            // Lower mold smoothly onto VibratingPlatform (1) deck
            float deckY = _moldLP.SampleSupportSurfaceY(VibroPlatformCenterXZ);
            float settleMountY = deckY + 1.38f;
            yield return MoveCraneToMountWorld(new Vector3(VibroPlatformCenterXZ.x, settleMountY, VibroPlatformCenterXZ.z), 1.65f);
            yield return WaitSeconds(0.35f);

            // Unhook chains cleanly from mold & return chain sling to hanger so mold top is unobstructed for belts & hopper
            ReleaseSocketSelection(_moldLP.chain1LoopGrab);
            ReleaseSocketSelection(_moldLP.chain2LoopGrab);

            yield return MoveCraneToMountWorld(new Vector3(VibroPlatformCenterXZ.x, 2.95f, VibroPlatformCenterXZ.z), 0.9f);
            _moldLP.DetachFromHook(true);
            if (_attachCargo != null) _attachCargo.isAttached = false;

            // Ensure MoldCasting sits squarely centered on VibratingPlatform (1)
            _moldCasting.SetPositionAndRotation(new Vector3(VibroPlatformCenterXZ.x, deckY, VibroPlatformCenterXZ.z), Quaternion.identity);
            Physics.SyncTransforms();

            // Park chain sling back on the Chain Hanger cleanly
            if (_moldLP.centralRing != null)
            {
                var rRb = _moldLP.centralRing.GetComponent<Rigidbody>();
                if (rRb != null) { rRb.isKinematic = true; rRb.useGravity = false; }
                _moldLP.centralRing.SetPositionAndRotation(_initialChainHangerRingPos, _initialChainHangerRingRot);
                if (rRb != null) { rRb.position = _initialChainHangerRingPos; rRb.rotation = _initialChainHangerRingRot; }
            }

            // Refresh MoldVibroPlatformCoupler rest pose at new location on VibratingPlatform (1)
            var coupler = FindAnyObjectByType<MoldVibroPlatformCoupler>(FindObjectsInactive.Include);
            if (coupler != null)
            {
                coupler.SetRestWorldPose(coupler.transform.position, coupler.transform.rotation);
            }

            AppendStatus($"[STAGE_END] Step 2 at frame {_frameIndex}");
        }

        private IEnumerator AnimateAndSnapBothMoldChainEndsSimultaneously(float duration)
        {
            EnsureMoldChainSocketsWired();

            XRGrabInteractable grab1 = _moldLP.chain1LoopGrab;
            XRGrabInteractable grab2 = _moldLP.chain2LoopGrab;
            XRSocketInteractor sock1 = _moldLP.leftBottomEdgeSocket;
            XRSocketInteractor sock2 = _moldLP.rightBottomEdgeSocket;
            if (grab1 == null || grab2 == null || sock1 == null || sock2 == null) yield break;

            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            Type lpType = _moldLP.GetType();
            object end1 = lpType.GetField("_end1", flags)?.GetValue(_moldLP);
            object end2 = lpType.GetField("_end2", flags)?.GetValue(_moldLP);
            var snapPointsObj = lpType.GetField("_snapPoints", flags)?.GetValue(_moldLP) as IList;
            var snapIntoMethod = lpType.GetMethod("SnapInto", flags);

            ChainSnapPoint homeSnap1 = ResolveSnapPointForSocket(end1, sock1, snapPointsObj);
            ChainSnapPoint homeSnap2 = ResolveSnapPointForSocket(end2, sock2, snapPointsObj);

            Vector3 start1 = grab1.transform.position;
            Vector3 start2 = grab2.transform.position;
            Vector3 target1 = homeSnap1 != null ? homeSnap1.Center : sock1.transform.position;
            Vector3 target2 = homeSnap2 != null ? homeSnap2.Center : sock2.transform.position;
            Vector3 axis1 = homeSnap1 != null ? homeSnap1.Axis : sock1.transform.forward;
            Vector3 axis2 = homeSnap2 != null ? homeSnap2.Axis : sock2.transform.forward;

            // Suppress XRI auto-snap during flight so neither side snaps earlier than the other
            sock1.socketActive = false;
            sock2.socketActive = false;

            FieldInfo posField = end1?.GetType().GetField("pos");
            FieldInfo prevField = end1?.GetType().GetField("prev");
            Vector3[] posArr1 = posField?.GetValue(end1) as Vector3[];
            Vector3[] prevArr1 = prevField?.GetValue(end1) as Vector3[];
            Vector3[] posArr2 = posField?.GetValue(end2) as Vector3[];
            Vector3[] prevArr2 = prevField?.GetValue(end2) as Vector3[];

            float t = 0f;
            while (t < duration)
            {
                t += Time.deltaTime;
                float u = Mathf.Clamp01(t / duration);
                float s = Mathf.SmoothStep(0f, 1f, u);
                float arcY = Mathf.Sin(u * Mathf.PI) * 0.14f;

                Vector3 p1 = Vector3.Lerp(start1, target1, s) + Vector3.up * arcY;
                Vector3 p2 = Vector3.Lerp(start2, target2, s) + Vector3.up * arcY;

                grab1.transform.position = p1;
                grab2.transform.position = p2;
                PushVerletBightParticles(posArr1, prevArr1, p1, axis1);
                PushVerletBightParticles(posArr2, prevArr2, p2, axis2);

                yield return null;
            }

            grab1.transform.position = target1;
            grab2.transform.position = target2;
            PushVerletBightParticles(posArr1, prevArr1, target1, axis1);
            PushVerletBightParticles(posArr2, prevArr2, target2, axis2);

            // Clear any socket cooldown & snap BOTH loops on the exact same frame so LateUpdate sees both == true immediately
            ClearChainEndCooldown(end1);
            ClearChainEndCooldown(end2);
            sock1.socketActive = true;
            sock2.socketActive = true;

            if (snapIntoMethod != null && end1 != null && end2 != null && homeSnap1 != null && homeSnap2 != null)
            {
                snapIntoMethod.Invoke(_moldLP, new[] { end1, (object)homeSnap1 });
                snapIntoMethod.Invoke(_moldLP, new[] { end2, (object)homeSnap2 });
            }

            AppendStatus($"[MOLD_SNAP] bothSocketed={_moldLP.AreBothLoopsSocketed} left={_moldLP.IsLeftLoopSocketed} right={_moldLP.IsRightLoopSocketed}");
        }

        private static ChainSnapPoint ResolveSnapPointForSocket(object endObj, XRSocketInteractor socket, IList snapPointsObj)
        {
            if (endObj != null)
            {
                var home = endObj.GetType().GetField("homeSnap")?.GetValue(endObj) as ChainSnapPoint;
                if (home != null && home.Socket == socket) return home;
            }
            if (snapPointsObj != null)
            {
                foreach (var sp in snapPointsObj)
                {
                    if (sp is ChainSnapPoint csp && csp.Socket == socket) return csp;
                }
            }
            return socket != null ? socket.GetComponent<ChainSnapPoint>() : null;
        }

        private static void ClearChainEndCooldown(object endObj)
        {
            if (endObj == null) return;
            Type t = endObj.GetType();
            t.GetField("socketCooldownUntil")?.SetValue(endObj, 0f);
            t.GetField("resnapBlocked")?.SetValue(endObj, false);
            var cdSock = t.GetField("cooldownSocket")?.GetValue(endObj) as XRSocketInteractor;
            if (cdSock != null) cdSock.socketActive = true;
            t.GetField("cooldownSocket")?.SetValue(endObj, null);
        }

        private static void PushVerletBightParticles(Vector3[] pos, Vector3[] prev, Vector3 center, Vector3 axis)
        {
            if (pos == null || pos.Length < 5) return;
            int c = pos.Length / 2;
            Vector3 fwd = axis.sqrMagnitude > 1e-6f ? axis.normalized : Vector3.forward;
            for (int j = -2; j <= 2; j++)
            {
                Vector3 wp = center - fwd * (j * 0.03f);
                pos[c + j] = wp;
                if (prev != null && prev.Length == pos.Length) prev[c + j] = wp;
            }
        }

        private static void ReleaseSocketSelection(XRGrabInteractable grab)
        {
            if (grab != null && grab.isSelected && grab.interactionManager != null)
            {
                grab.interactionManager.CancelInteractableSelection((IXRSelectInteractable)grab);
            }
        }

        // -------------------------------------------------------------------------
        // STEP 3: Tie the mold using the ratchet belt
        // -------------------------------------------------------------------------
        private IEnumerator Step3_TieMoldWithRatchetBelts()
        {
            AppendStatus($"[STAGE_START] Step 3 at frame {_frameIndex}");

            var shot3 = CreateShotCam("Shot3_RatchetBeltsTie",
                new Vector3(-20.85f, 1.25f, -7.15f), _moldCastTransform, new Vector3(0f, 0.35f, 0f), 40f);
            ActivateShot(shot3, 0.85f, new Vector3(-0.18f, -0.02f, 0.04f));
            yield return WaitSeconds(0.45f);

            if (_belt1 != null) yield return WrapAndTightenBelt(_belt1);
            if (_belt2 != null) yield return WrapAndTightenBelt(_belt2);

            yield return WaitSeconds(0.4f);
            AppendStatus($"[STAGE_END] Step 3 at frame {_frameIndex}");
        }

        private IEnumerator WrapAndTightenBelt(RatchetBeltMechanicsController belt)
        {
            var hookAttach = belt.GetComponent<PlatformHookAttachment>();
            if (hookAttach != null && hookAttach.platformHookRight != null)
            {
                hookAttach.activeMoldTransform = _moldCastTransform;
                hookAttach.RecalculateBeltLengthConstraints();

                hookAttach.BeginFreeEndGrab(out _);
                Vector3 p0 = belt.adjustableHookTransform != null
                    ? belt.adjustableHookTransform.position
                    : new Vector3(belt.transform.position.x, 0.1f, -6.7f);
                Vector3 p3 = hookAttach.platformHookRight.AnchorWorldPosition;
                float beltX = p3.x;
                float topY = _moldCasting.position.y + 1.04f;
                Vector3 p1 = new Vector3(beltX, topY, -5.85f);
                Vector3 p2 = new Vector3(beltX, topY, -5.05f);

                float wrapDur = 1.05f;
                float t = 0f;
                while (t < wrapDur)
                {
                    t += Time.deltaTime;
                    float u = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / wrapDur));
                    // Cubic Bezier over top of MoldCast
                    float oma = 1f - u;
                    Vector3 pos = (oma * oma * oma) * p0 +
                                  (3f * oma * oma * u) * p1 +
                                  (3f * oma * u * u) * p2 +
                                  (u * u * u) * p3;
                    hookAttach.UpdateGrabbedFreeEndPose(pos, null);
                    yield return null;
                }

                hookAttach.UpdateGrabbedFreeEndPose(p3, null);
                hookAttach.CompleteFreeEndRelease(out _);
            }

            if (!belt.IsAdjHookAttached)
                belt.ConnectHooks();

            yield return WaitSeconds(0.2f);
            belt.PullSlack();
            yield return WaitSeconds(0.2f);

            // Execute 4 visible ratchet handle strokes to lock tension
            for (int stroke = 0; stroke < 4 && !belt.IsFullyTightened; stroke++)
            {
                yield return AnimateBeltHandleSweep(belt, belt.minHandleAngle, belt.maxHandleAngle, 0.16f);
                belt.ExecuteDiscretePowerStroke();
                yield return AnimateBeltHandleSweep(belt, belt.maxHandleAngle, belt.minHandleAngle, 0.14f);
            }
        }

        private IEnumerator AnimateBeltHandleSweep(RatchetBeltMechanicsController belt, float fromDeg, float toDeg, float dur)
        {
            float t = 0f;
            while (t < dur)
            {
                t += Time.deltaTime;
                float s = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / dur));
                belt.SetHandleAngle(Mathf.Lerp(fromDeg, toDeg, s));
                yield return null;
            }
            belt.SetHandleAngle(toDeg);
        }

        // -------------------------------------------------------------------------
        // STEP 4: Using the remote/controller, turn on the vibration
        // -------------------------------------------------------------------------
        private IEnumerator Step4_StartVibrationViaRemote()
        {
            AppendStatus($"[STAGE_START] Step 4 at frame {_frameIndex}");

            var shot4A = CreateShotCam("Shot4A_VibroControllerStart",
                new Vector3(-24.68f, 1.12f, -7.32f), _vibroPanel.transform, new Vector3(0f, 0.04f, 0f), 34f);
            ActivateShot(shot4A, 0.85f, new Vector3(0.02f, 0f, 0.04f));
            yield return WaitSeconds(0.65f);

            if (_vibroStartBtn != null) _vibroStartBtn.PressButton();
            else if (_vibroPanel != null) _vibroPanel.PressStartButton();

            yield return WaitSeconds(0.55f);

            // Ensure an initial moderate operating step (step 1 or 2) so vibration starts visibly now,
            // reserving maximum frequency ramp-up for Step 6!
            if (_vibroPanel != null && _vibroPanel.RegulatorKnob != null && _vibroPanel.CurrentStep == 0)
            {
                _vibroPanel.RegulatorKnob.StepNext();
                yield return WaitSeconds(0.5f);
            }

            var shot4B = CreateShotCam("Shot4B_MoldVibrating",
                new Vector3(-21.30f, 1.38f, -6.95f), _moldCastTransform, new Vector3(0f, 0.40f, 0f), 40f);
            ActivateShot(shot4B, 0.7f, new Vector3(-0.04f, 0.0f, 0.02f));
            yield return WaitSeconds(1.2f);

            AppendStatus($"[STAGE_END] Step 4 at frame {_frameIndex}");
        }

        // -------------------------------------------------------------------------
        // STEP 5: Crane moves to hopper, brings above mold, pours mixture, places back
        // -------------------------------------------------------------------------
        private IEnumerator Step5_HopperPourAndReturnToGround()
        {
            AppendStatus($"[STAGE_START] Step 5 at frame {_frameIndex}");

            Transform hopperRoot = _hopperLP.transform.root;
            if (_hopperLP.GetComponentInParent<Rigidbody>() != null)
                hopperRoot = _hopperLP.GetComponentInParent<Rigidbody>().transform;

            _hopperLP.ComputeTargetHookMountWorldPose(_hookMount, out Vector3 hopperMountPos, out Quaternion hopperMountRot);

            var shot5A = CreateShotCam("Shot5A_CraneToHopper",
                new Vector3(-16.0f, 2.35f, -9.20f), _hookMount, new Vector3(0f, -0.35f, 0f), 46f);
            ActivateShot(shot5A, 0.85f, new Vector3(0.15f, -0.02f, 0.04f));

            // Approach above hopper bail then lower into C-hook pocket
            yield return MoveCraneToMountWorld(hopperMountPos + Vector3.up * 0.55f, 1.9f, hopperMountRot);
            yield return MoveCraneToMountWorld(hopperMountPos, 0.9f, hopperMountRot);

            _hopperLP.AttachToHook(_hookMount, _hookRb);
            float authWait = 0f;
            while ((!_hopperLP.IsAttached || !_hopperLP.IsLiftAuthorized) && authWait < 1.5f)
            {
                authWait += Time.deltaTime;
                yield return null;
            }

            // Track crane lifting & carrying Hopper to above MoldCast
            var shot5B = CreateShotCam("Shot5B_HopperTransitToMold",
                new Vector3(-17.8f, 2.50f, -9.85f), hopperRoot, new Vector3(0f, 0.75f, 0f), 46f);
            ActivateShot(shot5B, 0.8f, new Vector3(-0.22f, -0.03f, 0.05f));

            if (_shadowHopperStation != null) _shadowHopperStation.SetActive(false);
            yield return MoveCraneToMountWorld(hopperMountPos + Vector3.up * 1.45f, 1.35f, hopperMountRot);

            // Compute exact hook mount target so Hopper Mixture_Outlet lands dead-center over Mold cavity XZ & rim + 0.44m (tight clean vertical stream into MoldCast)
            Vector3 outletOffsetFromMount = (_hopperPourMgr != null && _hopperPourMgr.MixtureOutletTransform != null)
                ? (_hopperPourMgr.MixtureOutletTransform.position - _hookMount.position)
                : (hopperRoot.position - _hookMount.position);
            Vector3 cavityXZ = _moldReceiver != null ? _moldReceiver.CavityWorldCenterXZ : VibroPlatformCenterXZ;
            float rimY = _moldReceiver != null ? _moldReceiver.MoldTopRimWorldY : (_moldCasting.position.y + 0.88f);
            Vector3 targetOutletPos = new Vector3(cavityXZ.x, rimY + 0.44f, cavityXZ.z);
            Vector3 pourMountPos = targetOutletPos - outletOffsetFromMount;

            yield return MoveCraneToMountWorld(new Vector3(pourMountPos.x, Mathf.Max(pourMountPos.y, hopperMountPos.y + 1.45f), pourMountPos.z), 2.4f, hopperMountRot);
            yield return MoveCraneToMountWorld(pourMountPos, 1.0f, hopperMountRot);

            // Shot 5C_Wheel: frame operator handwheel spinning open, then blend to -X/-Z chute side showing unobstructed Mixture_Outlet pour stream
            var shot5CWheel = CreateShotCam("Shot5C_Wheel",
                new Vector3(-20.95f, 2.24f, -7.25f), _moldCastTransform, new Vector3(0f, 0.80f, 0f), 42f);
            var shot5CStream = CreateShotCam("Shot5C_Stream",
                new Vector3(-23.85f, 2.02f, -7.10f), _moldCastTransform, new Vector3(0f, 0.75f, 0f), 40f);
            ActivateShot(shot5CWheel, 0.8f, new Vector3(-0.03f, -0.01f, 0.02f));
            yield return WaitSeconds(0.45f);

            if (_hopperPourMgr != null)
            {
                _hopperPourMgr.targetMoldReceiver = _moldReceiver;
                var rateField = typeof(HopperPourManager).GetField("maxDischargeRate", BindingFlags.NonPublic | BindingFlags.Instance);
                if (rateField != null) rateField.SetValue(_hopperPourMgr, 28f);
            }

            // Smoothly turn handwheel open from 0 -> 1
            float openDur = 0.55f;
            float ot = 0f;
            while (ot < openDur)
            {
                ot += Time.deltaTime;
                if (_hopperHandwheel != null)
                    _hopperHandwheel.SetOpenFraction(Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(ot / openDur)));
                yield return null;
            }
            yield return WaitSeconds(0.25f);

            // Blend across to -X/-Z chute side for clear unobstructed view of granular stream & rising mold bed
            ActivateShot(shot5CStream, 0.65f, new Vector3(0.05f, -0.02f, 0.03f));

            // Pour until mold cavity reaches 100% full
            float pourElapsed = 0f;
            while (_moldReceiver != null && !_moldReceiver.IsFull && pourElapsed < 7.5f)
            {
                pourElapsed += Time.deltaTime;
                if (_hopperHandwheel != null) _hopperHandwheel.SetOpenFraction(1f);
                yield return null;
            }
            AppendStatus($"[HOPPER_POUR] rem={(_hopperPourMgr != null ? _hopperPourMgr.RemainingMaterial : -1f):F1} full={(_moldReceiver != null && _moldReceiver.IsFull)}");
            yield return WaitSeconds(0.35f);

            // Smoothly close handwheel 1 -> 0
            float closeDur = 0.45f;
            float ct = 0f;
            while (ct < closeDur)
            {
                ct += Time.deltaTime;
                if (_hopperHandwheel != null)
                    _hopperHandwheel.SetOpenFraction(1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(ct / closeDur)));
                yield return null;
            }
            if (_hopperHandwheel != null) _hopperHandwheel.CloseMechanismImmediate();

            // Return Hopper back to ground station
            var shot5D = CreateShotCam("Shot5D_HopperReturnToGround",
                new Vector3(-16.8f, 2.25f, -9.20f), hopperRoot, new Vector3(0f, 0.75f, 0f), 46f);
            ActivateShot(shot5D, 0.85f, new Vector3(0.18f, -0.04f, 0f));

            yield return MoveCraneToMountWorld(pourMountPos + Vector3.up * 0.85f, 1.1f, hopperMountRot);
            yield return MoveCraneToMountWorld(hopperMountPos + Vector3.up * 1.15f, 2.2f, hopperMountRot);
            yield return MoveCraneToMountWorld(hopperMountPos, 1.35f, hopperMountRot);

            if (_shadowHopperStation != null) _shadowHopperStation.SetActive(true);
            _hopperLP.DetachFromHook();
            if (_attachCargo != null) _attachCargo.isAttached = false;
            yield return WaitSeconds(0.3f);
            // Lift hook clear while smoothly restoring Quaternion.identity yaw for Step 9 Jaw Tongs pickup
            yield return MoveCraneToMountWorld(hopperMountPos + Vector3.up * 0.75f, 0.95f, Quaternion.identity);

            AppendStatus($"[STAGE_END] Step 5 at frame {_frameIndex}");
        }

        // -------------------------------------------------------------------------
        // STEP 6: Vibration frequency is set to the top level
        // -------------------------------------------------------------------------
        private IEnumerator Step6_SetVibrationFrequencyToTopLevel()
        {
            AppendStatus($"[STAGE_START] Step 6 at frame {_frameIndex}");

            var shot6A = CreateShotCam("Shot6A_FrequencyKnobMax",
                new Vector3(-24.68f, 1.12f, -7.28f), _vibroPanel.transform, new Vector3(0f, 0.04f, 0f), 32f);
            ActivateShot(shot6A, 0.8f);
            yield return WaitSeconds(0.45f);

            if (_vibroPanel != null && _vibroPanel.RegulatorKnob != null)
            {
                XRSteppedKnob knob = _vibroPanel.RegulatorKnob;
                int guard = 0;
                while (knob.StepLevel < knob.MaxStepIndex && guard++ < 12)
                {
                    knob.StepNext();
                    yield return WaitSeconds(0.36f);
                }
            }

            var shot6B = CreateShotCam("Shot6B_TopLevelVibroCompaction",
                new Vector3(-21.25f, 1.80f, -6.70f), _moldCastTransform, new Vector3(0f, 0.55f, 0f), 38f);
            ActivateShot(shot6B, 0.75f, new Vector3(-0.08f, -0.02f, 0.04f));
            yield return WaitSeconds(2.3f);

            if (_moldReceiver != null && !_moldReceiver.IsSolidified)
            {
                _moldReceiver.ForceSolidifyBrickNow();
            }

            AppendStatus($"[STAGE_END] Step 6 at frame {_frameIndex}");
        }

        // -------------------------------------------------------------------------
        // STEP 7: Stop the vibration, and untie the ratchet belt
        // -------------------------------------------------------------------------
        private IEnumerator Step7_StopVibrationAndUntieBelts()
        {
            AppendStatus($"[STAGE_START] Step 7 at frame {_frameIndex}");

            var shot7A = CreateShotCam("Shot7A_VibroStop",
                new Vector3(-24.68f, 1.12f, -7.30f), _vibroPanel.transform, new Vector3(0f, 0.04f, 0f), 34f);
            ActivateShot(shot7A, 0.75f);
            yield return WaitSeconds(0.45f);

            if (_vibroStopBtn != null) _vibroStopBtn.PressButton();
            else if (_vibroPanel != null) _vibroPanel.PressStopButton();
            yield return WaitSeconds(0.75f);

            var shot7B = CreateShotCam("Shot7B_UntieRatchetBelts",
                new Vector3(-20.90f, 1.25f, -7.10f), _moldCastTransform, new Vector3(0f, 0.35f, 0f), 40f);
            ActivateShot(shot7B, 0.8f, new Vector3(-0.15f, 0f, 0.03f));

            if (_belt1 != null) yield return UntieBelt(_belt1);
            if (_belt2 != null) yield return UntieBelt(_belt2);

            yield return WaitSeconds(0.35f);
            AppendStatus($"[STAGE_END] Step 7 at frame {_frameIndex}");
        }

        private IEnumerator UntieBelt(RatchetBeltMechanicsController belt)
        {
            yield return AnimateBeltHandleSweep(belt, belt.minHandleAngle, belt.maxHandleAngle, 0.22f);
            belt.TriggerFullRelease();
            yield return WaitSeconds(0.32f);

            var hookAttach = belt.GetComponent<PlatformHookAttachment>();
            if (hookAttach != null && belt.adjustableHookTransform != null)
            {
                hookAttach.BeginFreeEndGrab(out _);
                Vector3 p0 = belt.adjustableHookTransform.position;
                float beltX = belt.transform.position.x;
                float topY = _moldCasting.position.y + 1.05f;
                Vector3 p1 = new Vector3(beltX, topY, -5.05f);
                Vector3 p2 = new Vector3(beltX, topY, -5.90f);
                Vector3 p3 = new Vector3(beltX, 0.08f, -6.72f);

                float dur = 0.90f;
                float t = 0f;
                while (t < dur)
                {
                    t += Time.deltaTime;
                    float u = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / dur));
                    float oma = 1f - u;
                    Vector3 pos = (oma * oma * oma) * p0 +
                                  (3f * oma * oma * u) * p1 +
                                  (3f * oma * u * u) * p2 +
                                  (u * u * u) * p3;
                    hookAttach.UpdateGrabbedFreeEndPose(pos, null);
                    yield return null;
                }

                hookAttach.CompleteFreeEndRelease(out _);
            }

            if (belt.IsAdjHookAttached)
                belt.DisconnectHooks();

            yield return AnimateBeltHandleSweep(belt, belt.maxHandleAngle, belt.minHandleAngle, 0.16f);
        }

        // -------------------------------------------------------------------------
        // STEP 8: Using the wrench, dismantle the mold
        // -------------------------------------------------------------------------
        private IEnumerator Step8_DismantleMoldWithWrench()
        {
            AppendStatus($"[STAGE_START] Step 8 at frame {_frameIndex}");

            if (_assemblyMgr == null)
                throw new InvalidOperationException("MoldAssemblyManager missing in Step 8.");

            if (_screwTightening != null) _screwTightening.enabled = false;
            if (_screwdriver != null)
            {
                _screwdriver.ReverseDirection = true;
                SetEditorTestMotor(true);
                _screwdriver.StartWorking();
                _screwdriver.StopWorking(false);
            }

            var shot8Front = CreateShotCam("Shot8A_WrenchFrontRight",
                new Vector3(-21.05f, 1.30f, -6.85f), _moldCastTransform, new Vector3(0f, 0.35f, 0f), 40f);
            var shot8Back = CreateShotCam("Shot8B_WrenchBackLeft",
                new Vector3(-23.95f, 1.30f, -4.15f), _moldCastTransform, new Vector3(0f, 0.35f, 0f), 40f);
            bool viewingBack = false;
            ActivateShot(shot8Front, 0.8f);

            int placedCount = 0;

            // 8.1 Unscrew all horizontal tie-rod nuts first (front side then back side to minimize camera cuts)
            var horizSlots = new List<ScrewSlot>();
            foreach (ScrewSlot slot in _assemblyMgr.ScrewSlots)
            {
                if (slot != null && !slot.IsVerticalSlot && slot.IsOccupied && slot.SeatedScrew != null)
                    horizSlots.Add(slot);
            }
            horizSlots.Sort((a, b) => a.transform.position.z.CompareTo(b.transform.position.z));

            foreach (ScrewSlot slot in horizSlots)
            {
                bool isBackSide = slot.transform.position.z > VibroPlatformCenterXZ.z + 0.05f;
                if (isBackSide != viewingBack)
                {
                    viewingBack = isBackSide;
                    ActivateShot(viewingBack ? shot8Back : shot8Front, 0.65f);
                    yield return WaitSeconds(0.35f);
                }
                yield return DriveWrenchOnScrew(slot, isVertical: false, parkIndex: placedCount++);
            }

            // Switch to 3/4 reveal view to slide horizontal rods & unscrew vertical flange nuts + remove walls
            var shot8Reveal = CreateShotCam("Shot8C_DismantleWallsReveal",
                new Vector3(-20.80f, 1.48f, -7.10f), _moldCastTransform, new Vector3(0f, 0.32f, 0f), 42f);
            ActivateShot(shot8Reveal, 0.7f, new Vector3(-0.08f, 0.02f, 0.02f));

            // 8.2 Slide out all horizontal rods
            foreach (MoldSnapPoint sp in _assemblyMgr.RodSnapPoints)
            {
                if (sp == null || !sp.IsOccupied || sp.OccupiedPart == null) continue;
                MoldPart part = sp.OccupiedPart;
                if (part.Category == MoldPartCategory.VerticalBolt) continue;

                var rodComp = part.GetComponent<ConnectingRod>();
                Vector3 axis = rodComp != null ? rodComp.WorldShaftAxis : sp.TargetWorldShaftAxis;
                yield return SlideAndParkPart(part, axis * 0.45f, GetTrayParkPos(placedCount++, isWall: false), 0.20f);
            }

            // 8.3 Unscrew all vertical flange nuts and slide out vertical rods
            foreach (ScrewSlot slot in _assemblyMgr.ScrewSlots)
            {
                if (slot != null && slot.IsVerticalSlot && slot.IsOccupied && slot.SeatedScrew != null)
                {
                    yield return DriveWrenchOnScrew(slot, isVertical: true, parkIndex: placedCount++);
                }
            }
            foreach (MoldSnapPoint sp in _assemblyMgr.RodSnapPoints)
            {
                if (sp == null || !sp.IsOccupied || sp.OccupiedPart == null) continue;
                yield return SlideAndParkPart(sp.OccupiedPart, Vector3.up * 0.30f, GetTrayParkPos(placedCount++, isWall: false), 0.18f);
            }

            // Return wrench cleanly to workbench
            if (_screwdriver != null)
            {
                _screwdriver.StopWorking(false);
                SetEditorTestMotor(false);
                yield return MoveTransformSmooth(_screwdriver.transform, _initialScrewdriverPos, _initialScrewdriverRot, 0.45f);
            }

            // 8.4 Remove the 4 mold walls cleanly onto factory floor tray to expose solidified brick
            int wallIdx = 0;
            foreach (MoldSnapPoint sp in _assemblyMgr.WallSnapPoints)
            {
                if (sp == null || !sp.IsOccupied || sp.OccupiedPart == null) continue;
                MoldPart wallPart = sp.OccupiedPart;
                Vector3 outward = wallPart.transform.position - _moldCastTransform.position;
                outward.y = 0f;
                if (outward.sqrMagnitude < 1e-4f) outward = Vector3.back;
                outward = outward.normalized * 0.35f + Vector3.up * 0.22f;

                Vector3 wallPark = new Vector3(-24.85f, 0.25f, -3.60f - wallIdx * 0.85f);
                wallIdx++;
                yield return SlideAndParkPart(wallPart, outward, wallPark, 0.40f);
            }

            // Ensure solidified brick exists and collides properly after walls are removed
            if (_moldReceiver != null && !_moldReceiver.IsSolidified)
            {
                _moldReceiver.ForceSolidifyBrickNow();
            }

            yield return WaitSeconds(0.6f);
            AppendStatus($"[STAGE_END] Step 8 at frame {_frameIndex}");
        }

        private IEnumerator DriveWrenchOnScrew(ScrewSlot slot, bool isVertical, int parkIndex)
        {
            ScrewInteraction screw = slot.SeatedScrew;
            if (screw == null || screw.Part == null || _screwdriver == null) yield break;

            Vector3 nutCenter = MoldSnapPoint.GetPartWorldGeometricCenter(screw.Part);
            Vector3 inwardDir = slot.WorldTightenDirection.normalized;
            Vector3 handleOpposite;
            if (isVertical)
            {
                inwardDir = Vector3.down;
                float zSign = nutCenter.z >= VibroPlatformCenterXZ.z ? 1f : -1f;
                handleOpposite = new Vector3(0f, 0f, -zSign);
            }
            else
            {
                Vector3 proj = Vector3.ProjectOnPlane(Vector3.up, inwardDir);
                handleOpposite = proj.sqrMagnitude > 1e-4f ? proj.normalized : Vector3.up;
            }

            Quaternion targetRot = Quaternion.LookRotation(handleOpposite, inwardDir);
            float socketLocalY = 0.153f * _screwdriver.transform.lossyScale.y;
            Vector3 targetPos = nutCenter - (targetRot * new Vector3(0f, socketLocalY, 0f));

            yield return MoveTransformSmooth(_screwdriver.transform, targetPos, targetRot, 0.20f);

            _screwdriver.ReverseDirection = true;
            SetEditorTestMotor(true);
            _screwdriver.StartWorking();

            float spinDur = 0.24f;
            float t = 0f;
            while (t < spinDur)
            {
                t += Time.deltaTime;
                float u = Mathf.Clamp01(t / spinDur);
                screw.SetTightenProgress(1f - u);

                Vector3 liveNutCenter = MoldSnapPoint.GetPartWorldGeometricCenter(screw.Part);
                _screwdriver.transform.SetPositionAndRotation(
                    liveNutCenter - (targetRot * new Vector3(0f, socketLocalY, 0f)),
                    targetRot);
                yield return null;
            }

            screw.SetTightenProgress(0f);
            _screwdriver.StopWorking(false);
            SetEditorTestMotor(false);

            screw.Part.ReleaseToFreeState();
            ParkPartKinematic(screw.Part, GetTrayParkPos(parkIndex, isWall: false));
        }

        private IEnumerator SlideAndParkPart(MoldPart part, Vector3 slideDelta, Vector3 parkPos, float duration)
        {
            if (part == null) yield break;
            part.ReleaseToFreeState();
            if (part.Body != null)
            {
                if (!part.Body.isKinematic)
                {
                    part.Body.linearVelocity = Vector3.zero;
                    part.Body.angularVelocity = Vector3.zero;
                }
                part.Body.isKinematic = true;
                part.Body.useGravity = false;
            }

            Vector3 startPos = part.transform.position;
            Vector3 midPos = startPos + slideDelta;

            float t = 0f;
            while (t < duration)
            {
                t += Time.deltaTime;
                float u = Mathf.Clamp01(t / duration);
                Vector3 pos = u < 0.45f
                    ? Vector3.Lerp(startPos, midPos, Mathf.SmoothStep(0f, 1f, u / 0.45f))
                    : Vector3.Lerp(midPos, parkPos, Mathf.SmoothStep(0f, 1f, (u - 0.45f) / 0.55f));
                part.transform.position = pos;
                if (part.Body != null) part.Body.position = pos;
                yield return null;
            }
            ParkPartKinematic(part, parkPos);
        }

        private static void ParkPartKinematic(MoldPart part, Vector3 parkPos)
        {
            if (part == null) return;
            if (part.Body != null)
            {
                if (!part.Body.isKinematic)
                {
                    part.Body.linearVelocity = Vector3.zero;
                    part.Body.angularVelocity = Vector3.zero;
                }
                part.Body.isKinematic = true;
                part.Body.useGravity = false;
                part.Body.position = parkPos;
            }
            part.transform.position = parkPos;
        }

        private static Vector3 GetTrayParkPos(int idx, bool isWall)
        {
            int row = idx / 6;
            int col = idx % 6;
            return new Vector3(-24.40f - row * 0.25f, 0.06f, -6.90f - col * 0.14f);
        }

        private void SetEditorTestMotor(bool active)
        {
            if (_screwdriver == null) return;
            var f = typeof(XRElectricScrewdriver).GetField("editorTestRunMotor", BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) f.SetValue(_screwdriver, active);
        }

        private IEnumerator MoveTransformSmooth(Transform tr, Vector3 targetPos, Quaternion targetRot, float dur)
        {
            Vector3 sPos = tr.position;
            Quaternion sRot = tr.rotation;
            float t = 0f;
            while (t < dur)
            {
                t += Time.deltaTime;
                float u = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / dur));
                tr.SetPositionAndRotation(Vector3.Lerp(sPos, targetPos, u), Quaternion.Slerp(sRot, targetRot, u));
                yield return null;
            }
            tr.SetPositionAndRotation(targetPos, targetRot);
        }

        // -------------------------------------------------------------------------
        // STEP 9: Using the crane, pick the jaw tongs and place above the mold
        // -------------------------------------------------------------------------
        private IEnumerator Step9_PickJawTongsAndPlaceOverMold()
        {
            AppendStatus($"[STAGE_START] Step 9 at frame {_frameIndex}");

            if (_tongsLP == null || _tongsController == null)
                throw new InvalidOperationException("JawTongs components missing in Step 9.");

            DestroyOrphanTongsJoints();

            // Switch crane hook & inertia body to kinematic FixedUpdate drive + identity rotation for clean ConfigurableJoint chain suspension
            SetCraneHookKinematicMode(true);
            ApplyHookWorldPoseSafe(EvaluatePlumbHookMountPos(), Quaternion.identity);

            _tongsLP.SyncPoseWithPivot();
            Vector3 pivotMid0 = 0.5f * (_tongsController.leftJawArmBody.transform.position + _tongsController.rightJawArmBody.transform.position);
            Vector3 ringSeatWorld = (_tongsLP.ringMesh != null && Vector3.Distance(_tongsLP.RingSeatWorldPosition, pivotMid0) < 0.55f)
                ? _tongsLP.RingSeatWorldPosition
                : (pivotMid0 + new Vector3(0f, 0.205f, 0f));
            Vector3 tongsMountPos = ringSeatWorld + Vector3.up * _tongsLP.hookSaddleOffset;

            var shot9A = CreateShotCam("Shot9A_CranePickJawTongs",
                new Vector3(-19.95f, 1.18f, -11.15f), _tongsController.leftJawArmBody.transform, new Vector3(0f, 0.16f, 0f), 40f);
            ActivateShot(shot9A, 0.85f, new Vector3(-0.06f, 0.03f, 0.06f));

            // Move empty crane over JawTongs_Assembly and lower smoothly into ring seat
            yield return MoveCraneToMountWorldFixedUpdate(tongsMountPos + Vector3.up * 0.65f, 1.8f, Quaternion.identity);
            yield return MoveCraneToMountWorldFixedUpdate(tongsMountPos, 0.95f, Quaternion.identity);

            _tongsLP.AttachToHook(_hookMount, _hookRb, enforceSeatingTolerance: false);
            if (_attachCargo != null) _attachCargo.isAttached = _tongsLP.IsAttached;

            // Hoist tongs clear of the floor in FixedUpdate
            yield return MoveCraneToMountWorldFixedUpdate(tongsMountPos + Vector3.up * 1.45f, 1.45f, Quaternion.identity);
            yield return WaitSeconds(0.35f);
            AppendStatus($"[TONGS_PICK] attached={_tongsLP.IsAttached} leftArm={_tongsController.leftJawArmBody.position} hinge={_tongsController.CurrentHingeAngleDegrees:F1}");

            // Track crane carrying Jaw Tongs to position directly above the solidified tile on VibratingPlatform (1)
            SolidifiedCastingBlock tileBlock = FindSolidifiedTile();
            Bounds tileBounds = tileBlock.CastingCollider.bounds;

            var shot9B = CreateShotCam("Shot9B_TongsAboveTile",
                new Vector3(-20.45f, 1.72f, -7.25f), tileBlock.transform, new Vector3(0f, 0.20f, 0f), 42f);
            ActivateShot(shot9B, 0.8f, new Vector3(-0.06f, -0.04f, 0.04f));

            float highMountY = Mathf.Max(tongsMountPos.y + 1.45f, tileBounds.max.y + 1.55f);
            yield return MoveCraneToMountWorldFixedUpdate(new Vector3(tileBounds.center.x, highMountY, tileBounds.center.z), 2.0f, Quaternion.identity);
            yield return WaitSeconds(0.4f);

            // Calculate current vertical distance from hookMount to tongs pivotMid so pivotMid lowers cleanly to ~0.25m above tile center
            Vector3 currentPivotMid = 0.5f * (_tongsController.leftJawArmBody.position + _tongsController.rightJawArmBody.position);
            float mountMinusPivotY = EvaluatePlumbHookMountPos().y - currentPivotMid.y;
            float desiredPivotMidY = tileBounds.center.y + 0.25f;
            float readyMountY = desiredPivotMidY + mountMinusPivotY;

            yield return MoveCraneToMountWorldFixedUpdate(new Vector3(tileBounds.center.x, readyMountY, tileBounds.center.z), 1.35f, Quaternion.identity);
            yield return WaitSeconds(0.45f);

            AppendStatus($"[STAGE_END] Step 9 at frame {_frameIndex}");
        }

        // -------------------------------------------------------------------------
        // STEP 10: Remove pin of tongs, pick the tile, and move it outside vibro platform
        // -------------------------------------------------------------------------
        private IEnumerator Step10_RemovePinPickTileAndMoveOutside()
        {
            AppendStatus($"[STAGE_START] Step 10 at frame {_frameIndex}");

            SolidifiedCastingBlock tileBlock = FindSolidifiedTile();
            Transform pinAnchor = _tongsController.pinTransform != null
                ? _tongsController.pinTransform
                : _tongsController.leftJawArmBody.transform;

            var shot10A = CreateShotCam("Shot10A_PullPinAndGripTile",
                new Vector3(-21.35f, 1.28f, -6.55f), pinAnchor, new Vector3(0f, -0.10f, 0f), 32f);
            ActivateShot(shot10A, 0.7f);
            yield return WaitSeconds(0.45f);

            // Visibly slide locking pin outward from seated position to released offset via LateUpdate order 600 override
            if (_tongsController.pinTransform != null && _tongsController.leftJawArmBody != null)
            {
                Vector3 localStart = _tongsController.pinSeatedLocalOffset;
                Vector3 localEnd = _tongsController.pinReleasedLocalOffset + new Vector3(0f, 0f, 0.08f);
                _overridePinLocalOffset = localStart;
                _overridePinPoseActive = true;

                float pd = 0.60f;
                float t = 0f;
                while (t < pd)
                {
                    t += Time.deltaTime;
                    float s = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / pd));
                    _overridePinLocalOffset = Vector3.Lerp(localStart, localEnd, s);
                    yield return null;
                }
                _overridePinPoseActive = false;
            }

            _tongsController.ReleasePin();

            // Wait for jaws to close and SolidifiedCastingBlock FixedUpdate auto-clamp to engage
            float clampWait = 0f;
            while (!tileBlock.IsGrippedByTongs && clampWait < 1.4f)
            {
                clampWait += Time.deltaTime;
                yield return null;
            }
            if (!tileBlock.IsGrippedByTongs)
            {
                yield return MoveCraneToMountWorldFixedUpdate(EvaluatePlumbHookMountPos() - Vector3.up * 0.05f, 0.45f, Quaternion.identity);
                yield return WaitSeconds(0.35f);
            }
            AppendStatus($"[TONGS_GRIP] gripped={tileBlock.IsGrippedByTongs} tilePos={tileBlock.transform.position}");
            yield return WaitSeconds(0.35f);

            // Hero Finale Shot: lift tile off VibratingPlatform (1), carry outside onto factory floor, and set down
            var shot10B = CreateShotCam("Shot10B_MoveTileOutside",
                new Vector3(-18.30f, 1.85f, -8.55f), tileBlock.transform, Vector3.zero, 44f);
            ActivateShot(shot10B, 0.85f, new Vector3(0.06f, -0.02f, 0.06f));

            Vector3 curMount = EvaluatePlumbHookMountPos();
            // Lift tile cleanly out of mold base
            yield return MoveCraneToMountWorldFixedUpdate(curMount + Vector3.up * 0.95f, 1.55f, Quaternion.identity);

            // Carry tile outside VibratingPlatform (1) to clean open factory floor area
            curMount = EvaluatePlumbHookMountPos();
            yield return MoveCraneToMountWorldFixedUpdate(new Vector3(OutsideTileFloorTargetXZ.x, curMount.y, OutsideTileFloorTargetXZ.z), 2.3f, Quaternion.identity);
            yield return WaitSeconds(0.4f);

            // Lower tile until bottom rests cleanly at floor height (Y = 0)
            float floorY = 0.005f;
            Bounds tb = tileBlock.CastingCollider.bounds;
            float tileMinYMinusMountY = tb.min.y - EvaluatePlumbHookMountPos().y;
            float placeMountY = floorY - tileMinYMinusMountY;

            yield return MoveCraneToMountWorldFixedUpdate(new Vector3(OutsideTileFloorTargetXZ.x, placeMountY, OutsideTileFloorTargetXZ.z), 1.55f, Quaternion.identity);

            // Release brick onto the factory floor & hoist tongs slightly to showcase the placed tile
            if (tileBlock.IsGrippedByTongs)
            {
                tileBlock.ReleasePhysicalTongsClamp();
            }
            if (tileBlock.CastingCollider != null && tileBlock.CastingRigidbody != null)
            {
                float halfH = tileBlock.CastingCollider.bounds.extents.y;
                Vector3 finalTilePos = new Vector3(OutsideTileFloorTargetXZ.x, halfH + 0.002f, OutsideTileFloorTargetXZ.z);
                Quaternion finalTileRot = Quaternion.Euler(0f, tileBlock.transform.eulerAngles.y, 0f);
                if (!tileBlock.CastingRigidbody.isKinematic)
                {
                    tileBlock.CastingRigidbody.linearVelocity = Vector3.zero;
                    tileBlock.CastingRigidbody.angularVelocity = Vector3.zero;
                }
                tileBlock.transform.SetPositionAndRotation(finalTilePos, finalTileRot);
                tileBlock.CastingRigidbody.isKinematic = true;
                tileBlock.CastingRigidbody.useGravity = false;
                tileBlock.CastingRigidbody.position = finalTilePos;
                tileBlock.CastingRigidbody.rotation = finalTileRot;
                Physics.SyncTransforms();
            }

            yield return WaitSeconds(0.25f);
            yield return MoveCraneToMountWorldFixedUpdate(new Vector3(OutsideTileFloorTargetXZ.x, placeMountY + 0.48f, OutsideTileFloorTargetXZ.z), 0.95f, Quaternion.identity);
            yield return WaitSeconds(1.2f);

            AppendStatus($"[STAGE_END] Step 10 at frame {_frameIndex}");
        }

        private SolidifiedCastingBlock FindSolidifiedTile()
        {
            if (_moldReceiver != null && _moldReceiver.SolidifiedCastingObject != null)
            {
                var blk = _moldReceiver.SolidifiedCastingObject.GetComponent<SolidifiedCastingBlock>();
                if (blk != null) return blk;
            }
            var found = FindAnyObjectByType<SolidifiedCastingBlock>();
            if (found != null) return found;
            throw new InvalidOperationException("SolidifiedCastingBlock not found in scene.");
        }
    }
}
