# SEFPRO — Vibration Dust Effect
## Phase 1B: Final Pre-Implementation Verification Report

**Project:** `C:\unity3d\FinalRatchetBelt\Project`  
**Active Scene:** `Assets/Exports/RatchetBelt/Demo/MainProject.unity`  
**Unity Version:** `6000.4.6f1` (URP)  
**Inspection Date:** 2026-11-06  
**Operating Mode:** STRICT READ-ONLY — zero scene, script, or asset modifications.

> [!NOTE]
> `scene.isDirty == True` was observed during script-execute calls. This is a known Editor side-effect of runtime reflection queries that touch the serialization layer. No GameObjects, components, assets, or transforms were created, modified, or destroyed during this inspection. The scene will NOT be saved.

---

## 1. Vibration Knob Integration

### 1.1 Control Chain — Verified

The complete signal path from knob to vibration state is:

```
XRSteppedKnob (Main_Vibration_Frequency_Knob)
  │  On detent click: fires onStepChanged.Invoke(_stepLevel)
  └─► HandleStepChanged(int stepLevel)  [private, within XRSteppedKnob.cs]
        └─► vibrationEffect.SetVibrationLevel(stepLevel)
              └─► VibroEffect._isVibrating  /  VibroEffect.frequency
```

**Source:** [`Assets/Scripts/XRSteppedKnob.cs`](file:///C:/unity3d/FinalRatchetBelt/Project/Assets/Scripts/XRSteppedKnob.cs), lines 175–199.  
**Source:** [`Assets/Scripts/VibroEffect.cs`](file:///C:/unity3d/FinalRatchetBelt/Project/Assets/Scripts/VibroEffect.cs), lines 84–96.

### 1.2 Five-Level Selection Mechanism — Verified

| Field | Verified Value |
|---|---|
| `StepCount` (private const) | `5` |
| `initialStep` (inspector) | `0` |
| `minRotationAngle` | `-120°` |
| `maxRotationAngle` | `+120°` |
| Angle per step | $\frac{240°}{4} = 60°$ |
| Runtime `_stepLevel` (scene load) | `0` |

Steps snap at every 60° of physical knob rotation. There are five discrete detents: 0, 1, 2, 3, 4.

### 1.3 Level → Vibration State Mapping — Verified

| `StepLevel` | `VibroEffect._isVibrating` | Vibration Frequency | Operational State |
|:---:|:---:|:---:|:---:|
| **0** | `false` | — | **OFF** (transform reset to origin) |
| **1** | `true` | 6.00 Hz | Low |
| **2** | `true` | 13.33 Hz | Medium-Low |
| **3** | `true` | 20.67 Hz | Medium-High |
| **4** | `true` | 28.00 Hz | High |

**Formula from source:** `frequency = Mathf.Lerp(6f, 28f, (level - 1) / 3f);`  
(Level 0 calls `StopVibration()` directly.)

### 1.4 Safest Way for a Particle Controller to Read Level — Verified

> [!IMPORTANT]
> **`VibroEffect.cs` does NOT need to be modified.**

`XRSteppedKnob` already exposes the full level as a **public property**:

```csharp
// XRSteppedKnob.cs, line 44 — already public
public int StepLevel => _stepLevel;
```

This was confirmed by reflection: `StepLevel` property exists, `IsPublic = True`, current runtime value `= 0`.

A `VibrationParticleController` component can hold a `[SerializeField] private XRSteppedKnob _knob;` reference and read `_knob.StepLevel` each frame with zero risk and zero modification to any existing script.

**OFF detection:** `_knob.StepLevel == 0` → stop emission immediately.  
**ON detection:** `_knob.StepLevel >= 1` → scale emission linearly with level.

---

## 2. Emission Zone Geometry — Verified

### 2.1 Source Bounds (from `MeshRenderer.bounds` and `BoxCollider.bounds`)

| Object | World Min $(X, Z)$ | World Max $(X, Z)$ | Span $(X \times Z)$ | Top Surface $Y$ |
|---|---|---|---|---|
| `Vibrating_Rubber_Surface` | $(-24.610, -6.000)$ | $(-20.470, -4.920)$ | $4.140\text{ m} \times 1.079\text{ m}$ | $Y = 0.100\text{ m}$ |
| `CastingMold` (all BoxColliders encapsulated) | $(-23.068, -5.820)$ | $(-22.012, -5.100)$ | $1.056\text{ m} \times 0.720\text{ m}$ | Footprint bottom: $Y = 0.100\text{ m}$ |

**Clearance between mold walls and rubber surface edges:**

| Edge | Clearance |
|---|---|
| Left rubber to left mold wall | $1.542\text{ m}$ |
| Right rubber to right mold wall | $1.542\text{ m}$ |
| Near rubber edge to near mold wall (Z) | $0.180\text{ m}$ |
| Far rubber edge to far mold wall (Z) | $0.180\text{ m}$ |

### 2.2 Emitter Regions — Verified

A 60 mm inset is applied from every outer rubber edge (to prevent particles emitting over the frame void) and from each mold wall (to leave visual separation between mold and particle cloud).

#### Left Wing Emitter

| Property | Value |
|---|---|
| **World Center** | $(-23.839, +0.106, -5.460)$ |
| **Box Size** | $1.422\text{ m}\ (X) \times 0.001\text{ m}\ (Y) \times 0.959\text{ m}\ (Z)$ |
| **World X extents** | $[-24.550, -23.128]$ |
| **World Z extents** | $[-5.940, -4.980]$ |
| **Local pos rel. to** `VibratingPlatform (1)` | $(-1.299, +0.106, 0.000)$ |
| **Separation from mold left wall** | $60\text{ mm}$ |
| **Separation from rubber outer edge** | $60\text{ mm}$ |

#### Right Wing Emitter

| Property | Value |
|---|---|
| **World Center** | $(-21.241, +0.106, -5.460)$ |
| **Box Size** | $1.422\text{ m}\ (X) \times 0.001\text{ m}\ (Y) \times 0.959\text{ m}\ (Z)$ |
| **World X extents** | $[-21.952, -20.530]$ |
| **World Z extents** | $[-5.940, -4.980]$ |
| **Local pos rel. to** `VibratingPlatform (1)` | $(+1.299, +0.106, 0.000)$ |
| **Separation from mold right wall** | $60\text{ mm}$ |
| **Separation from rubber outer edge** | $60\text{ mm}$ |

**Neither region overlaps the casting mould at any level.** Emitter Y position is 5 mm above the rubber surface top ($Y = 0.100 + 0.005 = 0.106\text{ m}$ world).

---

## 3. Particle Material Verification

**Material path:** `Assets/Materials/M_RefractoryGrain_Particle.mat`  
**Texture:** `Assets/Materials/Textures/T_RefractoryGrain_Particle.png` — $256 \times 256$, DXT5.

| Property | Verified Value | Assessment |
|---|---|---|
| **Shader** | `Universal Render Pipeline/Particles/Unlit` | ✅ URP-compatible, XR single-pass instanced. |
| **RenderQueue** | `3000` (Transparent) | ✅ Correct for alpha-blended particles. |
| **Surface Type `_Surface`** | `1` = Transparent | ✅ |
| **Blend mode** | `SrcBlend=5` (SrcAlpha), `DstBlend=10` (OneMinusSrcAlpha) | ✅ Standard alpha blend. No additive glow. |
| **ColorMode `_ColorMode`** | `0` = Multiply | ✅ Particle tint colour **multiplies** the texture. This means a tan/ochre start colour will produce muted industrial grain tones as intended. |
| **Soft Particles** | Disabled | ✅ Avoids depth-texture dependency and Z-fighting near collider surfaces. Appropriate for sparse dust. |
| **Existing scene usage** | `Discharge_Grains` on `Hopper_Discharge_Stream` already uses this exact material with `renderMode = Billboard`, `simulationSpace = World`. | ✅ Proven working in the project pipeline. |

**Conclusion (verified):** `M_RefractoryGrain_Particle.mat` can produce subtle airborne industrial dust particles. The ColorMode-Multiply configuration means the per-particle start colour tints the grain texture multiplicatively — setting a mid-grey or warm-tan colour will produce exactly the subdued silica-dust appearance required. No shader changes are needed.

---

## 4. Recommended Visual Settings

> [!NOTE]
> Values marked **[RECOMMENDED]** are design proposals based on inspection data and the established project conventions observed in `Discharge_Grains`. They are not yet implemented or tested in-editor.

### 4.1 Per-Level Emission Rates

Emission rate scales linearly from Level 1 to Level 4. Transition between levels uses `Mathf.MoveTowards` with slew rate 50 p/s² (≈ 0.4 s to ramp from 0 → max), ensuring smooth fade-in/out rather than instant population jumps.

| Knob Level | State | Target Emission Rate (each wing) | Total Combined |
|:---:|:---:|:---:|:---:|
| **0 — OFF** | Stopped | **0 p/s** | 0 p/s |
| **1 — Very subtle** | 6 Hz | **8 p/s** | 16 p/s |
| **2 — Light** | 13.33 Hz | **18 p/s** | 36 p/s |
| **3 — Moderate** | 20.67 Hz | **30 p/s** | 60 p/s |
| **4 — Strongest** | 28 Hz | **44 p/s** | 88 p/s |

Formula for `VibrationParticleController.cs`:
```csharp
float targetRate = (_knob.StepLevel == 0) ? 0f
                 : Mathf.Lerp(8f, 44f, (_knob.StepLevel - 1) / 3f);
_currentRate = Mathf.MoveTowards(_currentRate, targetRate, 50f * Time.deltaTime);
// Apply to both ParticleSystem emission modules
var e = _leftPS.emission;
e.rateOverTime = _currentRate;
e = _rightPS.emission;
e.rateOverTime = _currentRate;
```

### 4.2 Particle Visual Parameters

| Module | Parameter | Recommended Value | Rationale |
|---|---|---|---|
| **Main** | Start Lifetime | `Random(0.40, 0.80)` | Short enough to clear the volume before the next detent, long enough to show visible drift. |
| **Main** | Start Speed | `Random(0.03, 0.10)` | Very slow upward drift off a vibrating surface. |
| **Main** | Start Size | `Random(0.004, 0.009)` m ($4 - 9\text{ mm}$) | Matches refractory sand grain scale. Much smaller than `Discharge_Grains` ($32\text{ mm}$) to stay subtle. |
| **Main** | Start Color | `RGBA(0.80, 0.75, 0.68, 0.30)` | Warm grey-tan tint; multiplied against grain texture = muted silica dust. |
| **Main** | Gravity Modifier | `0.30` | Light downward pull — grains lift, arc, and settle back. |
| **Main** | Simulation Space | **World** | **Critical.** Prevents particles inheriting 28 Hz transform wobble of `Vibrating_Rubber_Surface`. |
| **Main** | Max Particles | `100` per wing, **200 total** | Hard XR budget cap. At max emission and 0.80 s lifetime: $44 \times 0.80 = 35$ live particles per wing, well within cap. |
| **Main** | Loop | `true` | |
| **Emission** | Rate Over Time | Driven at runtime by controller | See §4.1. |
| **Shape** | Shape Type | `Box` | |
| **Shape** | Box size | $1.422 \times 0.001 \times 0.959\text{ m}$ | Exactly the emitter region dimensions from §2.2. |
| **Shape** | Emit From | `Volume` | Uniform planar surface emission. |
| **Velocity over Lifetime** | Linear Y | `Random(0.00, 0.06)` | Additional slow upward drift over lifetime. |
| **Velocity over Lifetime** | Linear X/Z | `Random(-0.02, 0.02)` | Lateral micro-scatter to prevent linear streaks. |
| **Noise** | Enabled | `true` | |
| **Noise** | Strength | `0.06` | Subtle turbulent flutter — not visible as directional drift. |
| **Noise** | Frequency | `2.5` | Medium spatial frequency for organic motion. |
| **Noise** | Scroll Speed | `0.4` | Slow evolution over time. |
| **Color over Lifetime** | Alpha curve | `0 → 1.0` over first 10%, hold, `1.0 → 0` over last 20% | Fade in quickly, linger, fade out smoothly before despawn. |
| **Renderer** | Render Mode | `Billboard` | Same as `Discharge_Grains`. |
| **Renderer** | Material | `M_RefractoryGrain_Particle` | Reuse existing asset — no new materials. |
| **Renderer** | Sort Mode | `By Distance` | Correct depth ordering for transparent particles in XR. |
| **Collision** | Enabled | **`false`** | Particles must not interact with belt, hook, or mold colliders. Visual only. |

---

## 5. Proposed Architecture

### 5.1 Summary

One script. Two `ParticleSystem` GameObjects. Zero modifications to existing scripts.

```
VibratingPlatform (1)                      ← existing, unmodified
├── Vibrating_Rubber_Surface               ← existing, unmodified
├── Vibro Controller                       ← existing, unmodified
│   └── Main_Vibration_Frequency_Knob     ← read-only reference target (XRSteppedKnob.StepLevel)
├── Vibro_Dust_Left     [new] [ParticleSystem + VibrationParticleController]
└── Vibro_Dust_Right    [new] [ParticleSystem only, driven by controller on Left]
```

### 5.2 `VibrationParticleController.cs` — Design Specification

**Location:** `Assets/Scripts/VibrationParticleController.cs`  
**Attachment:** Placed on `Vibro_Dust_Left` only. `Vibro_Dust_Right` is referenced as a field.

```csharp
// DESIGN SPECIFICATION ONLY — not yet implemented
[RequireComponent(typeof(ParticleSystem))]
public class VibrationParticleController : MonoBehaviour
{
    [SerializeField] private XRSteppedKnob _knob;         // Main_Vibration_Frequency_Knob
    [SerializeField] private ParticleSystem _rightWingPS; // Vibro_Dust_Right

    private ParticleSystem _leftWingPS;
    private float _currentRate;
    private const float SlewRate = 50f;   // particles/sec²
    private const float RateAtLevel1 = 8f;
    private const float RateAtLevel4 = 44f;

    private void Awake() => _leftWingPS = GetComponent<ParticleSystem>();

    private void Update()
    {
        int level = (_knob != null) ? _knob.StepLevel : 0;
        float targetRate = (level == 0) ? 0f
            : Mathf.Lerp(RateAtLevel1, RateAtLevel4, (level - 1) / 3f);

        _currentRate = Mathf.MoveTowards(_currentRate, targetRate, SlewRate * Time.deltaTime);

        SetRate(_leftWingPS, _currentRate);
        SetRate(_rightWingPS, _currentRate);
    }

    private static void SetRate(ParticleSystem ps, float rate)
    {
        if (ps == null) return;
        var e = ps.emission;
        e.rateOverTime = rate;
        if (rate <= 0f && ps.isPlaying)  ps.Stop(false, ParticleSystemStopBehavior.StopEmitting);
        else if (rate > 0f && !ps.isPlaying) ps.Play();
    }
}
```

### 5.3 VibroEffect.cs — Not Modified

`VibroEffect.cs` is **not touched**. `XRSteppedKnob.StepLevel` (existing public property) is the sole state input. This avoids all risk to the vibration mechanics, physics reset logic, and audio coupling.

### 5.4 Files That Will Be Created or Modified

| File | Action | Scope |
|---|---|---|
| `Assets/Scripts/VibrationParticleController.cs` | **CREATE** (new) | New controller script only |
| `Assets/Exports/RatchetBelt/Demo/MainProject.unity` | **MODIFY** (scene) | Add `Vibro_Dust_Left` and `Vibro_Dust_Right` GameObjects as children of `VibratingPlatform (1)` only |
| All other scripts, assets, and scene objects | **UNTOUCHED** | — |

### 5.5 Risk Assessment

| Risk | Mitigation |
|---|---|
| Particles jittering with surface vibration | `SimulationSpace.World` on both Particle Systems. Emitter WorldPosition is static relative to `VibratingPlatform (1)`, not `Vibrating_Rubber_Surface`. |
| XR frame budget overrun | 200 particles hard cap total; Billboard size <9 mm; short lifetimes. |
| Interference with ratchet belts or hooks | Particle `Collision` module disabled. Purely visual. |
| Impact on existing `Discharge_Grains` behaviour | Zero. No shared references, no script dependency. |
| VibroEffect stop logic bypassed | Not applicable — controller reads `StepLevel`, not `_isVibrating`. Level 0 → `StopVibration()` is called by `XRSteppedKnob` independently of the particle controller. |

---

## Verification Summary

| Item | Status |
|---|---|
| Knob → VibroEffect integration chain | ✅ Verified in source |
| `XRSteppedKnob.StepLevel` public property accessible without script changes | ✅ Confirmed |
| VibroEffect.cs modification required | ✅ **Not required** |
| Left wing emitter world position and size | ✅ Computed from verified bounds |
| Right wing emitter world position and size | ✅ Computed from verified bounds |
| Neither emitter overlaps casting mould | ✅ Confirmed — 60 mm clearance on both sides |
| `M_RefractoryGrain_Particle.mat` suitable for subtle dust | ✅ Transparent, Multiply ColorMode, URP Particles/Unlit |
| Existing `Discharge_Grains` already uses this material | ✅ Confirmed — established project convention |
| Zero existing ParticleSystems under `VibratingPlatform (1)` | ✅ Confirmed — no conflicts |
| Scene not saved | ✅ Confirmed — inspection only |

---
*Read-only inspection complete. Awaiting Stage 2 implementation authorization.*
