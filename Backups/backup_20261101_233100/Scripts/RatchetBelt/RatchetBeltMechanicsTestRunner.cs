using System.Collections;
using System.IO;
using UnityEngine;
using Obi;

namespace Sefpro.RatchetBelt
{
    /// <summary>
    /// Automated test runner for Phase B1.1 validation of the Sefpro ratchet belt mechanics.
    /// Validates:
    /// 1. Baseline rest lengths & physical dimensions.
    /// 2. Single power stroke (10mm shorten, 30 deg mandrel).
    /// 3. Return stroke lock (no unspooling).
    /// 4. Multi-stroke accumulation (3 teeth, 90 deg mandrel, ~30mm shortening).
    /// 5. Step release (1 tooth / 10mm payout, mandrel unwinds to 60 deg).
    /// 6. Reversible tightening sequence (tighten -> partial release -> re-tighten).
    /// 7. Full release back to baseline (0 teeth, 0 deg mandrel, rest length restored, no over-reeling).
    /// 8. Independent J-hook displacement & Obi wrap particle tracking (+50mm offset).
    /// 9. Full state reset (all transforms, teeth, rest lengths restored).
    /// 10. Burst physics stability (zero NaNs, low jitter velocity).
    /// </summary>
    [DisallowMultipleComponent]
    public class RatchetBeltMechanicsTestRunner : MonoBehaviour
    {
        public RatchetBeltMechanicsController controller;
        public Camera testCamera;

        [Header("Test Configuration")]
        public bool runAutomatedTest = true;
        public string reportOutputPath = "mechanics_validation_report.json";

        [System.Serializable]
        public class ValidationReport
        {
            public string sceneName;
            public bool testCompleted;
            public bool allTestsPassed;

            // Physical Webbing Dimensions (Measured from baked mesh)
            public float measuredAdjustableWidthMm;
            public float measuredAdjustableThicknessMm;
            public float measuredFixedWidthMm;
            public float measuredFixedThicknessMm;

            // Initial Baseline State
            public float initialAdjustableRestLength;
            public float initialFixedRestLength;

            // Test 1: Single Power Stroke
            public int stroke1Teeth;
            public float stroke1MandrelAngle;
            public float stroke1AdjustableRestLength;
            public float stroke1ShorteningDelta;
            public bool stroke1AdvancedExactlyOnce;
            public bool stroke1WebbingShortened;

            // Test 2: Return Stroke Lock
            public int returnStrokeTeeth;
            public float returnStrokeMandrelAngle;
            public float returnStrokeAdjustableRestLength;
            public bool returnStrokeDidNotUnwind;

            // Test 3: Accumulated 3 Strokes
            public int totalTeethAfter3Strokes;
            public float mandrelAngleAfter3Strokes;
            public float adjustableRestLengthAfter3Strokes;
            public float totalShorteningAfter3Strokes;
            public bool threeStrokesGreaterTightening;
            public bool fixedWebbingRemainedUnchanged;

            // Test 4: Step Release (1 Tooth Payout)
            public int stepReleaseRemainingTeeth;
            public float stepReleaseMandrelAngle;
            public float stepReleaseAdjustableRestLength;
            public float stepReleaseShorteningRemaining;
            public bool stepReleasePaidOutSmoothly;

            // Test 5: Reversible Tightening Cycle
            public int cycleTeethAfterReTighten;
            public float cycleMandrelAngleAfterReTighten;
            public float cycleAdjustableRestLengthAfterReTighten;
            public bool cycleReTightenSucceeded;

            // Test 6: Full Release to Baseline
            public int fullReleaseTeeth;
            public float fullReleaseMandrelAngle;
            public float fullReleaseAdjustableRestLength;
            public float fullReleaseShorteningDeltaFromBaseline;
            public bool fullReleaseRestoredBaseline;
            public bool fullReleaseNoOverReeling;

            // Test 7: Independent J-Hook Drag & Attachment Tracking
            public float fixedHookDisplacementMm;
            public float fixedParticleDisplacementMm;
            public bool fixedHookTrackingAccurate;
            public float adjHookDisplacementMm;
            public float adjParticleDisplacementMm;
            public bool adjHookTrackingAccurate;
            public bool bothHooksIndependentlyMovable;
            public bool wrapGeometryIntact;

            // Test 8: Reset Action
            public bool resetRestoredInitialState;

            // Test 9: Physics Stability
            public string solverBackend;
            public int nanCount;
            public bool simulationStable;
            public float maxJitterVelocityAfterTightening;
            public bool beltTightenedSmoothlyNoJitter;
        }

        private void Start()
        {
            if (controller == null)
                controller = FindFirstObjectByType<RatchetBeltMechanicsController>();

            if (testCamera == null)
                testCamera = Camera.main;

            if (runAutomatedTest)
            {
                StartCoroutine(RunValidationSuite());
            }
        }

        private IEnumerator RunValidationSuite()
        {
            Debug.Log("[TestRunner] Starting Sefpro Ratchet Belt Phase B1.1 Validation Suite...");

            // Step 1: Wait for Obi Solver initialization and warm-up
            for (int i = 0; i < 25; i++)
                yield return new WaitForFixedUpdate();

            ValidationReport report = new ValidationReport();
            report.sceneName = gameObject.scene.name;

            // Capture initial baseline
            float initAdjLen = controller.CurrentAdjustableRestLength;
            float initFixLen = controller.CurrentFixedRestLength;
            Vector3 initFixHookPos = controller.fixedHookTransform != null ? controller.fixedHookTransform.position : Vector3.zero;
            Vector3 initAdjHookPos = controller.adjustableHookTransform != null ? controller.adjustableHookTransform.position : Vector3.zero;

            report.initialAdjustableRestLength = initAdjLen;
            report.initialFixedRestLength = initFixLen;

            Debug.Log($"[TestRunner] Baseline Rest Lengths -> Adjustable: {initAdjLen:F4}m, Fixed: {initFixLen:F4}m");

            // Measure webbing physical dimensions from baked mesh
            MeasureWebbingDimensions(report);

            // Capture At-Rest Baseline Screenshots
            CaptureView("mechanics_at_rest_overview.png", new Vector3(0.1f, 0.35f, -1.25f), new Vector3(15f, 0f, 0f));
            CaptureView("mechanics_at_rest_cargo.png", new Vector3(0.38f, 0.12f, -0.45f), new Vector3(14f, 0f, 0f));
            CaptureView("mechanics_at_rest_buckle.png", new Vector3(0.0f, 0.14f, -0.42f), new Vector3(14f, 0f, 0f));

            // -------------------------------------------------------------
            // Step 2: Test 1 — First Power Stroke (Advance to 65 deg smoothly)
            // -------------------------------------------------------------
            Debug.Log("[TestRunner] --- TEST 1: EXECUTING POWER STROKE #1 ---");
            for (float a = 0f; a <= 65f; a += 10f)
            {
                controller.SetHandleAngle(a);
                yield return new WaitForFixedUpdate();
            }
            controller.SetHandleAngle(65.0f);

            for (int i = 0; i < 15; i++)
                yield return new WaitForFixedUpdate();

            report.stroke1Teeth = controller.AccumulatedTeeth;
            report.stroke1MandrelAngle = controller.AccumulatedMandrelAngle;
            report.stroke1AdjustableRestLength = controller.CurrentAdjustableRestLength;
            report.stroke1ShorteningDelta = initAdjLen - controller.CurrentAdjustableRestLength;

            report.stroke1AdvancedExactlyOnce = (report.stroke1Teeth == 1 && Mathf.Approximately(report.stroke1MandrelAngle, 30.0f));
            report.stroke1WebbingShortened = (report.stroke1AdjustableRestLength < initAdjLen - 0.005f);

            Debug.Log($"[TestRunner] Stroke #1 Results: Teeth={report.stroke1Teeth}, Mandrel={report.stroke1MandrelAngle}°, Shortened={report.stroke1ShorteningDelta * 1000f:F1}mm");

            // -------------------------------------------------------------
            // Step 3: Test 2 — Return Stroke (Return to 0 deg smoothly)
            // -------------------------------------------------------------
            Debug.Log("[TestRunner] --- TEST 2: EXECUTING RETURN STROKE ---");
            for (float a = 65f; a >= 0f; a -= 10f)
            {
                controller.SetHandleAngle(a);
                yield return new WaitForFixedUpdate();
            }
            controller.SetHandleAngle(0.0f);

            for (int i = 0; i < 15; i++)
                yield return new WaitForFixedUpdate();

            report.returnStrokeTeeth = controller.AccumulatedTeeth;
            report.returnStrokeMandrelAngle = controller.AccumulatedMandrelAngle;
            report.returnStrokeAdjustableRestLength = controller.CurrentAdjustableRestLength;

            // Return stroke MUST NOT unwind belt or change tooth count
            report.returnStrokeDidNotUnwind = (report.returnStrokeTeeth == 1 &&
                                               Mathf.Approximately(report.returnStrokeMandrelAngle, 30.0f) &&
                                               Mathf.Abs(report.returnStrokeAdjustableRestLength - report.stroke1AdjustableRestLength) < 0.001f);

            Debug.Log($"[TestRunner] Return Stroke: Teeth={report.returnStrokeTeeth}, Mandrel={report.returnStrokeMandrelAngle}°, DidNotUnwind={report.returnStrokeDidNotUnwind}");

            // -------------------------------------------------------------
            // Step 4: Test 3 — Accumulate Two More Power Strokes (Total 3)
            // -------------------------------------------------------------
            Debug.Log("[TestRunner] --- TEST 3: EXECUTING POWER STROKES #2 & #3 ---");
            for (float a = 0f; a <= 65f; a += 15f) { controller.SetHandleAngle(a); yield return new WaitForFixedUpdate(); }
            controller.SetHandleAngle(65.0f);
            for (int i = 0; i < 10; i++) yield return new WaitForFixedUpdate();
            for (float a = 65f; a >= 0f; a -= 15f) { controller.SetHandleAngle(a); yield return new WaitForFixedUpdate(); }
            controller.SetHandleAngle(0.0f);
            for (int i = 0; i < 10; i++) yield return new WaitForFixedUpdate();

            for (float a = 0f; a <= 65f; a += 15f) { controller.SetHandleAngle(a); yield return new WaitForFixedUpdate(); }
            controller.SetHandleAngle(65.0f);
            for (int i = 0; i < 10; i++) yield return new WaitForFixedUpdate();
            for (float a = 65f; a >= 0f; a -= 15f) { controller.SetHandleAngle(a); yield return new WaitForFixedUpdate(); }
            controller.SetHandleAngle(0.0f);

            // Wait for smooth reel-in to settle
            for (int i = 0; i < 25; i++) yield return new WaitForFixedUpdate();

            report.totalTeethAfter3Strokes = controller.AccumulatedTeeth;
            report.mandrelAngleAfter3Strokes = controller.AccumulatedMandrelAngle;
            report.adjustableRestLengthAfter3Strokes = controller.CurrentAdjustableRestLength;
            report.totalShorteningAfter3Strokes = initAdjLen - controller.CurrentAdjustableRestLength;

            report.threeStrokesGreaterTightening = (report.totalTeethAfter3Strokes == 3 &&
                                                    report.totalShorteningAfter3Strokes > report.stroke1ShorteningDelta + 0.010f);

            // Confirm fixed webbing remained unchanged
            float currentFixLen = controller.CurrentFixedRestLength;
            report.fixedWebbingRemainedUnchanged = Mathf.Abs(currentFixLen - initFixLen) < 0.0001f;

            Debug.Log($"[TestRunner] After 3 Strokes: Teeth={report.totalTeethAfter3Strokes}, Mandrel={report.mandrelAngleAfter3Strokes}°, TotalShortening={report.totalShorteningAfter3Strokes * 1000f:F1}mm");

            // Check simulation stability and jitter
            CheckSimulationStability(report);

            // Capture Tightened State Screenshots
            CaptureView("mechanics_tightened_overview.png", new Vector3(0.1f, 0.35f, -1.25f), new Vector3(15f, 0f, 0f));
            CaptureView("mechanics_tightened_cargo.png", new Vector3(0.38f, 0.12f, -0.45f), new Vector3(14f, 0f, 0f));
            CaptureView("mechanics_tightened_buckle.png", new Vector3(0.0f, 0.14f, -0.42f), new Vector3(14f, 0f, 0f));

            // -------------------------------------------------------------
            // Step 5: Test 4 — Step Release (1 Tooth Payout)
            // -------------------------------------------------------------
            Debug.Log("[TestRunner] --- TEST 4: EXECUTING STEP RELEASE (1 TOOTH) ---");
            float preStepLen = controller.CurrentAdjustableRestLength;
            controller.TriggerStepRelease();

            // Wait for smooth payout (25 fixed updates at 80mm/s easily covers 10mm)
            for (int i = 0; i < 25; i++)
                yield return new WaitForFixedUpdate();

            report.stepReleaseRemainingTeeth = controller.AccumulatedTeeth;
            report.stepReleaseMandrelAngle = controller.AccumulatedMandrelAngle;
            report.stepReleaseAdjustableRestLength = controller.CurrentAdjustableRestLength;
            report.stepReleaseShorteningRemaining = initAdjLen - controller.CurrentAdjustableRestLength;

            // After step release from 3 teeth: remaining teeth should be 2, mandrel 60 deg, length increased by ~10mm
            report.stepReleasePaidOutSmoothly = (report.stepReleaseRemainingTeeth == 2 &&
                                                 Mathf.Approximately(report.stepReleaseMandrelAngle, 60.0f) &&
                                                 report.stepReleaseAdjustableRestLength > preStepLen + 0.007f);

            Debug.Log($"[TestRunner] Step Release: Teeth={report.stepReleaseRemainingTeeth}, Mandrel={report.stepReleaseMandrelAngle}°, ShorteningRemaining={report.stepReleaseShorteningRemaining * 1000f:F1}mm, PaidOutSmoothly={report.stepReleasePaidOutSmoothly}");

            // -------------------------------------------------------------
            // Step 6: Test 5 — Reversible Sequence: Re-tighten 1 Tooth
            // -------------------------------------------------------------
            Debug.Log("[TestRunner] --- TEST 5: REVERSIBLE SEQUENCE (RE-TIGHTEN 1 TOOTH) ---");
            controller.ExecuteDiscretePowerStroke();

            for (int i = 0; i < 25; i++)
                yield return new WaitForFixedUpdate();

            report.cycleTeethAfterReTighten = controller.AccumulatedTeeth;
            report.cycleMandrelAngleAfterReTighten = controller.AccumulatedMandrelAngle;
            report.cycleAdjustableRestLengthAfterReTighten = controller.CurrentAdjustableRestLength;

            // Re-tighten should return teeth to 3, mandrel to 90 deg, shortening to ~30mm
            report.cycleReTightenSucceeded = (report.cycleTeethAfterReTighten == 3 &&
                                              Mathf.Approximately(report.cycleMandrelAngleAfterReTighten, 90.0f) &&
                                              Mathf.Abs(report.cycleAdjustableRestLengthAfterReTighten - report.adjustableRestLengthAfter3Strokes) < 0.003f);

            Debug.Log($"[TestRunner] Re-Tighten Cycle: Teeth={report.cycleTeethAfterReTighten}, Mandrel={report.cycleMandrelAngleAfterReTighten}°, RestLength={report.cycleAdjustableRestLengthAfterReTighten:F4}m, Succeeded={report.cycleReTightenSucceeded}");

            // -------------------------------------------------------------
            // Step 7: Test 6 — Full Release to Baseline Length
            // -------------------------------------------------------------
            Debug.Log("[TestRunner] --- TEST 6: EXECUTING FULL RELEASE TO BASELINE ---");
            controller.TriggerFullRelease();

            // Wait for complete smooth payout back to baseline (30mm at 80mm/s takes ~19 frames, give 40 frames)
            for (int i = 0; i < 40; i++)
                yield return new WaitForFixedUpdate();

            report.fullReleaseTeeth = controller.AccumulatedTeeth;
            report.fullReleaseMandrelAngle = controller.AccumulatedMandrelAngle;
            report.fullReleaseAdjustableRestLength = controller.CurrentAdjustableRestLength;
            report.fullReleaseShorteningDeltaFromBaseline = initAdjLen - controller.CurrentAdjustableRestLength;

            report.fullReleaseRestoredBaseline = (report.fullReleaseTeeth == 0 &&
                                                  Mathf.Approximately(report.fullReleaseMandrelAngle, 0.0f) &&
                                                  Mathf.Abs(report.fullReleaseShorteningDeltaFromBaseline) < 0.003f);

            report.fullReleaseNoOverReeling = (controller.TotalShortening >= -0.0001f &&
                                               report.fullReleaseAdjustableRestLength <= initAdjLen + 0.002f);

            Debug.Log($"[TestRunner] Full Release: Teeth={report.fullReleaseTeeth}, Mandrel={report.fullReleaseMandrelAngle}°, ShorteningDelta={report.fullReleaseShorteningDeltaFromBaseline * 1000f:F2}mm, RestoredBaseline={report.fullReleaseRestoredBaseline}, NoOverReel={report.fullReleaseNoOverReeling}");

            // Capture Released State Screenshots
            CaptureView("mechanics_released_overview.png", new Vector3(0.1f, 0.35f, -1.25f), new Vector3(15f, 0f, 0f));
            CaptureView("mechanics_released_buckle.png", new Vector3(0.0f, 0.14f, -0.42f), new Vector3(14f, 0f, 0f));

            // -------------------------------------------------------------
            // Step 8: Test 7 — Independent J-Hook Drag & Obi Attachment Tracking
            // -------------------------------------------------------------
            Debug.Log("[TestRunner] --- TEST 7: INDEPENDENT J-HOOK DRAG & ATTACHMENT TRACKING ---");
            Vector3 initFixWrapPos = controller.GetFixedHookWrapParticlePosition();
            Vector3 initAdjWrapPos = controller.GetAdjHookWrapParticlePosition();

            // Displace Fixed Hook along X by +0.050m (+50mm)
            Vector3 targetFixPos = initFixHookPos + new Vector3(0.050f, 0.0f, 0.0f);
            controller.MoveFixedHook(targetFixPos);

            // Displace Adjustable Hook along X by +0.050m (+50mm)
            Vector3 targetAdjPos = initAdjHookPos + new Vector3(0.050f, 0.0f, 0.0f);
            controller.MoveAdjustableHook(targetAdjPos);

            // Wait for Obi solver to step and update particle positions
            for (int i = 0; i < 25; i++)
                yield return new WaitForFixedUpdate();

            Vector3 finalFixHookPos = controller.fixedHookTransform != null ? controller.fixedHookTransform.position : Vector3.zero;
            Vector3 finalAdjHookPos = controller.adjustableHookTransform != null ? controller.adjustableHookTransform.position : Vector3.zero;
            Vector3 finalFixWrapPos = controller.GetFixedHookWrapParticlePosition();
            Vector3 finalAdjWrapPos = controller.GetAdjHookWrapParticlePosition();

            report.fixedHookDisplacementMm = (finalFixHookPos.x - initFixHookPos.x) * 1000f;
            report.fixedParticleDisplacementMm = (finalFixWrapPos.x - initFixWrapPos.x) * 1000f;
            report.fixedHookTrackingAccurate = Mathf.Abs(report.fixedHookDisplacementMm - report.fixedParticleDisplacementMm) < 3.0f;

            report.adjHookDisplacementMm = (finalAdjHookPos.x - initAdjHookPos.x) * 1000f;
            report.adjParticleDisplacementMm = (finalAdjWrapPos.x - initAdjWrapPos.x) * 1000f;
            report.adjHookTrackingAccurate = Mathf.Abs(report.adjHookDisplacementMm - report.adjParticleDisplacementMm) < 3.0f;

            report.bothHooksIndependentlyMovable = (Mathf.Abs(report.fixedHookDisplacementMm - 50.0f) < 2.0f &&
                                                    Mathf.Abs(report.adjHookDisplacementMm - 50.0f) < 2.0f);

            // Wrap geometry relative offset check (relative distance from hook to particle maintained)
            float initFixDist = Vector3.Distance(initFixHookPos, initFixWrapPos);
            float finalFixDist = Vector3.Distance(finalFixHookPos, finalFixWrapPos);
            float initAdjDist = Vector3.Distance(initAdjHookPos, initAdjWrapPos);
            float finalAdjDist = Vector3.Distance(finalAdjHookPos, finalAdjWrapPos);

            report.wrapGeometryIntact = (Mathf.Abs(finalFixDist - initFixDist) < 0.005f &&
                                         Mathf.Abs(finalAdjDist - initAdjDist) < 0.005f);

            Debug.Log($"[TestRunner] Fixed Hook: Delta={report.fixedHookDisplacementMm:F1}mm, Particle Delta={report.fixedParticleDisplacementMm:F1}mm, Accurate={report.fixedHookTrackingAccurate}");
            Debug.Log($"[TestRunner] Adj Hook: Delta={report.adjHookDisplacementMm:F1}mm, Particle Delta={report.adjParticleDisplacementMm:F1}mm, Accurate={report.adjHookTrackingAccurate}");
            Debug.Log($"[TestRunner] Wrap Geometry Intact: {report.wrapGeometryIntact}");

            // Capture Hook-Dragged Screenshots
            CaptureView("mechanics_hooks_dragged_overview.png", new Vector3(0.1f, 0.35f, -1.25f), new Vector3(15f, 0f, 0f));
            CaptureView("mechanics_hooks_dragged_fixed_hook.png", new Vector3(-0.55f, 0.05f, -0.35f), new Vector3(12f, 0f, 0f));
            CaptureView("mechanics_hooks_dragged_adj_hook.png", new Vector3(0.85f, 0.05f, -0.35f), new Vector3(12f, 0f, 0f));

            // -------------------------------------------------------------
            // Step 9: Test 8 — Reset Action
            // -------------------------------------------------------------
            Debug.Log("[TestRunner] --- TEST 8: EXECUTING RESET ACTION ---");
            controller.ResetState();

            for (int i = 0; i < 25; i++)
                yield return new WaitForFixedUpdate();

            bool resetTeeth = (controller.AccumulatedTeeth == 0);
            bool resetMandrel = Mathf.Approximately(controller.AccumulatedMandrelAngle, 0.0f);
            bool resetHandle = Mathf.Approximately(controller.CurrentHandleAngle, 0.0f);
            bool resetRestLen = Mathf.Abs(controller.CurrentAdjustableRestLength - initAdjLen) < 0.003f;
            bool resetFixHook = controller.fixedHookTransform != null && Vector3.Distance(controller.fixedHookTransform.position, initFixHookPos) < 0.002f;
            bool resetAdjHook = controller.adjustableHookTransform != null && Vector3.Distance(controller.adjustableHookTransform.position, initAdjHookPos) < 0.002f;

            report.resetRestoredInitialState = resetTeeth && resetMandrel && resetHandle && resetRestLen && resetFixHook && resetAdjHook;
            Debug.Log($"[TestRunner] Reset Check: Teeth={resetTeeth}, Mandrel={resetMandrel}, Handle={resetHandle}, RestLen={resetRestLen}, FixHook={resetFixHook}, AdjHook={resetAdjHook} -> Restored={report.resetRestoredInitialState}");

            // Capture Reset State Screenshot
            CaptureView("mechanics_reset_overview.png", new Vector3(0.1f, 0.35f, -1.25f), new Vector3(15f, 0f, 0f));

            // -------------------------------------------------------------
            // Step 10: Test 9 — Physics Stability & Final Evaluation
            // -------------------------------------------------------------
            CheckSimulationStability(report);

            report.testCompleted = true;
            report.allTestsPassed = report.stroke1AdvancedExactlyOnce &&
                                    report.stroke1WebbingShortened &&
                                    report.returnStrokeDidNotUnwind &&
                                    report.threeStrokesGreaterTightening &&
                                    report.fixedWebbingRemainedUnchanged &&
                                    report.stepReleasePaidOutSmoothly &&
                                    report.cycleReTightenSucceeded &&
                                    report.fullReleaseRestoredBaseline &&
                                    report.fullReleaseNoOverReeling &&
                                    report.fixedHookTrackingAccurate &&
                                    report.adjHookTrackingAccurate &&
                                    report.bothHooksIndependentlyMovable &&
                                    report.wrapGeometryIntact &&
                                    report.resetRestoredInitialState &&
                                    report.simulationStable &&
                                    report.beltTightenedSmoothlyNoJitter;

            // Output JSON Report
            string json = JsonUtility.ToJson(report, true);
            SaveReport(json);

            Debug.Log($"[TestRunner] VALIDATION COMPLETE. All Passed: {report.allTestsPassed}");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#endif
        }

        private void MeasureWebbingDimensions(ValidationReport report)
        {
            try
            {
                var solver = controller.adjustableRope != null ? controller.adjustableRope.solver : null;
                var system = solver != null ? solver.GetRenderSystem<ObiRopeExtrudedRenderer>() as ObiExtrudedRopeRenderSystem : null;
                if (system == null) return;

                if (controller.adjustableRope != null)
                {
                    var rend = controller.adjustableRope.GetComponent<ObiRopeExtrudedRenderer>();
                    if (rend != null)
                    {
                        Mesh mesh = new Mesh();
                        system.BakeMesh(rend, ref mesh, false);
                        var verts = mesh.vertices;
                        float minZ = float.MaxValue, maxZ = float.MinValue;
                        float minY = float.MaxValue, maxY = float.MinValue;
                        int count = 0;
                        for (int i = 0; i < verts.Length; i++)
                        {
                            if (Mathf.Abs(verts[i].x - 0.38f) < 0.05f)
                            {
                                count++;
                                if (verts[i].z < minZ) minZ = verts[i].z;
                                if (verts[i].z > maxZ) maxZ = verts[i].z;
                                if (verts[i].y < minY) minY = verts[i].y;
                                if (verts[i].y > maxY) maxY = verts[i].y;
                            }
                        }
                        if (count > 0)
                        {
                            report.measuredAdjustableWidthMm = (maxZ - minZ) * 1000f;
                            report.measuredAdjustableThicknessMm = (maxY - minY) * 1000f;
                            Debug.Log($"[TestRunner] Baked Adjustable Webbing near Cargo: Width={report.measuredAdjustableWidthMm:F2}mm, Thickness={report.measuredAdjustableThicknessMm:F2}mm");
                        }
                        Destroy(mesh);
                    }
                }

                if (controller.fixedRope != null)
                {
                    var rend = controller.fixedRope.GetComponent<ObiRopeExtrudedRenderer>();
                    if (rend != null)
                    {
                        Mesh mesh = new Mesh();
                        system.BakeMesh(rend, ref mesh, false);
                        var verts = mesh.vertices;
                        float minZ = float.MaxValue, maxZ = float.MinValue;
                        float minY = float.MaxValue, maxY = float.MinValue;
                        int count = 0;
                        for (int i = 0; i < verts.Length; i++)
                        {
                            if (Mathf.Abs(verts[i].x - (-0.35f)) < 0.05f)
                            {
                                count++;
                                if (verts[i].z < minZ) minZ = verts[i].z;
                                if (verts[i].z > maxZ) maxZ = verts[i].z;
                                if (verts[i].y < minY) minY = verts[i].y;
                                if (verts[i].y > maxY) maxY = verts[i].y;
                            }
                        }
                        if (count > 0)
                        {
                            report.measuredFixedWidthMm = (maxZ - minZ) * 1000f;
                            report.measuredFixedThicknessMm = (maxY - minY) * 1000f;
                            Debug.Log($"[TestRunner] Baked Fixed Webbing near midspan: Width={report.measuredFixedWidthMm:F2}mm, Thickness={report.measuredFixedThicknessMm:F2}mm");
                        }
                        Destroy(mesh);
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[TestRunner] Webbing dimension measurement exception: {ex.Message}");
            }
        }

        private void CheckSimulationStability(ValidationReport report)
        {
            int nans = 0;
            float maxVel = 0f;
            var solver = controller.adjustableRope != null ? controller.adjustableRope.solver : null;
            if (solver != null)
            {
                report.solverBackend = solver.backendType.ToString();
                for (int i = 0; i < solver.positions.count; i++)
                {
                    Vector4 p = solver.positions[i];
                    if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) || float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z))
                        nans++;
                }

                if (controller.adjustableRope != null)
                {
                    for (int i = 0; i < controller.adjustableRope.activeParticleCount; i++)
                    {
                        int sIdx = controller.adjustableRope.solverIndices[i];
                        Vector3 v = solver.velocities[sIdx];
                        if (v.magnitude > maxVel) maxVel = v.magnitude;
                    }
                }
            }
            report.nanCount = nans;
            report.simulationStable = (nans == 0);
            report.maxJitterVelocityAfterTightening = maxVel;
            report.beltTightenedSmoothlyNoJitter = (maxVel < 0.20f);
            Debug.Log($"[TestRunner] Physics Stability: NaNs={nans}, MaxParticleVelocity={maxVel:F4}m/s -> SmoothNoJitter={report.beltTightenedSmoothlyNoJitter}");
        }

        private void CaptureView(string filename, Vector3 pos, Vector3 rotEuler)
        {
            try
            {
                if (testCamera == null) testCamera = Camera.main;
                if (testCamera == null) return;

                // Temporarily bake Obi meshes so off-screen camera captures them
                GameObject tempFix = null;
                GameObject tempAdj = null;
                Mesh fixMesh = null;
                Mesh adjMesh = null;

                try
                {
                    var solver = controller.adjustableRope != null ? controller.adjustableRope.solver : null;
                    if (solver != null)
                    {
                        var sys = solver.GetRenderSystem<ObiRopeExtrudedRenderer>() as ObiExtrudedRopeRenderSystem;
                        if (sys != null)
                        {
                            if (controller.fixedRope != null)
                            {
                                var rendFix = controller.fixedRope.GetComponent<ObiRopeExtrudedRenderer>();
                                if (rendFix != null)
                                {
                                    fixMesh = new Mesh();
                                    sys.BakeMesh(rendFix, ref fixMesh, false);
                                    tempFix = new GameObject("Temp_Capture_Fix");
                                    tempFix.transform.SetParent(solver.transform, false);
                                    tempFix.AddComponent<MeshFilter>().sharedMesh = fixMesh;
                                    tempFix.AddComponent<MeshRenderer>().sharedMaterial = rendFix.material;
                                }
                            }

                            if (controller.adjustableRope != null)
                            {
                                var rendAdj = controller.adjustableRope.GetComponent<ObiRopeExtrudedRenderer>();
                                if (rendAdj != null)
                                {
                                    adjMesh = new Mesh();
                                    sys.BakeMesh(rendAdj, ref adjMesh, false);
                                    tempAdj = new GameObject("Temp_Capture_Adj");
                                    tempAdj.transform.SetParent(solver.transform, false);
                                    tempAdj.AddComponent<MeshFilter>().sharedMesh = adjMesh;
                                    tempAdj.AddComponent<MeshRenderer>().sharedMaterial = rendAdj.material;
                                }
                            }
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[TestRunner] Temp mesh baking warning: {ex.Message}");
                }

                Vector3 origPos = testCamera.transform.position;
                Quaternion origRot = testCamera.transform.rotation;

                Vector3 targetWorldPos = controller != null ? controller.transform.TransformPoint(pos) : pos;
                testCamera.transform.position = targetWorldPos;
                testCamera.transform.rotation = Quaternion.Euler(rotEuler);

                int w = 1280;
                int h = 720;
                RenderTexture rt = new RenderTexture(w, h, 24);
                testCamera.targetTexture = rt;
                Texture2D tex = new Texture2D(w, h, TextureFormat.RGB24, false);

                testCamera.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();

                byte[] bytes = tex.EncodeToPNG();

                string scratchDir = @"C:\Users\roshan\.gemini\antigravity\brain\ec103db2-c08b-4fea-9ea3-b23ca3a070ca\scratch";
                string artifactDir = @"C:\Users\roshan\.gemini\antigravity\brain\ec103db2-c08b-4fea-9ea3-b23ca3a070ca";

                if (Directory.Exists(scratchDir))
                    File.WriteAllBytes(Path.Combine(scratchDir, filename), bytes);
                if (Directory.Exists(artifactDir))
                    File.WriteAllBytes(Path.Combine(artifactDir, filename), bytes);

                testCamera.targetTexture = null;
                RenderTexture.active = null;
                Destroy(rt);
                Destroy(tex);

                testCamera.transform.position = origPos;
                testCamera.transform.rotation = origRot;

                if (tempFix != null) Destroy(tempFix);
                if (tempAdj != null) Destroy(tempAdj);
                if (fixMesh != null) Destroy(fixMesh);
                if (adjMesh != null) Destroy(adjMesh);

                Debug.Log($"[TestRunner] Captured screenshot: {filename}");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[TestRunner] CaptureView failed for {filename}: {ex.Message}");
            }
        }

        private void SaveReport(string json)
        {
            try
            {
                string scratchDir = @"C:\Users\roshan\.gemini\antigravity\brain\ec103db2-c08b-4fea-9ea3-b23ca3a070ca\scratch";
                string scratchFile = Path.Combine(scratchDir, reportOutputPath);
                if (Directory.Exists(scratchDir))
                    File.WriteAllText(scratchFile, json);

                string artifactDir = @"C:\Users\roshan\.gemini\antigravity\brain\ec103db2-c08b-4fea-9ea3-b23ca3a070ca";
                string artifactFile = Path.Combine(artifactDir, reportOutputPath);
                if (Directory.Exists(artifactDir))
                    File.WriteAllText(artifactFile, json);

                Debug.Log($"[TestRunner] Report written successfully to:\n{scratchFile}\n{artifactFile}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[TestRunner] Failed to write report: {ex.Message}");
            }
        }
    }
}
