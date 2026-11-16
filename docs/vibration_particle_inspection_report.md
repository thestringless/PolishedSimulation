# SEFPRO — Vibrating Platform Particle Effect
## Phase 1: Read-Only Technical Inspection Report

**Project:** `C:\unity3d\FinalRatchetBelt\Project`  
**Active Scene:** `Assets/Exports/RatchetBelt/Demo/MainProject.unity`  
**Unity Version:** `6000.4.6f1` (Universal Render Pipeline - URP)  
**Inspection Date:** November 6, 2026  
**Operating Mode:** STRICT READ-ONLY (No scene, script, or asset modifications)

---

## Executive Summary

A comprehensive read-only technical audit of the vibrating platform assembly in `MainProject.unity` was conducted. The platform employs a procedural noise displacement system (`VibroEffect.cs`) driven by an XR stepped knob (`XRSteppedKnob.cs`) with five discrete operating levels (0 to 4).

Crucially, the project **already possesses a production-quality particle material and texture specifically designed for refractory grains** (`M_RefractoryGrain_Particle.mat` and `T_RefractoryGrain_Particle.png`), making external asset creation unnecessary.

This report establishes the baseline mechanical geometry, frequency characteristics, particle resource availability, mathematical transfer functions, and architecture required for the subsequent Phase 2 implementation.

---

## 1. Platform Hierarchy & Dimensions

### 1.1 Hierarchy Tree

The vibrating platform assembly is structured as follows:

```
VibratingPlatform (1) [World Pos: (-22.5400, 0.0000, -5.4600), Scale: (0.96, 0.96, 0.96)]
├── Anchor_Hook_Front_Left_Loop [XRSocketInteractor, MeshCollider]
├── Frame_Left [MeshFilter, MeshRenderer, MeshCollider]
├── Vibrating_Rubber_Surface [MeshFilter, MeshRenderer, VibroEffect, MeshCollider]
│   └── (Target vibrating surface)
├── LoadDetectionCenter
├── Vibro Controller [AudioSource]
│   ├── Front_Control_Panel_Upper / Lower
│   ├── Main_Vibration_Frequency_Knob [BoxCollider, XRSteppedKnob]
│   ├── Vibration_Level_Selector [Knob & Screws]
│   ├── Digital_Display / Frequency_Display_Digits
│   ├── Vibro_Motor_START_Button / Vibro_Motor_STOP_Button
│   └── Hopper_Vibro_START_Button / Hopper_Vibro_STOP_Button
├── CastingMold [MoldPourReceiver]
│   ├── MoldBottom [BoxCollider, MeshRenderer]
│   ├── MoldWall_Front / Back / Left / Right
│   └── MoldRim / Gussets / Mold_Material_Volume
└── Ratchet Belt Anchors & Hooks:
    ├── Platform_SideHook_Far_1 [PlatformSideHook]
    ├── Platform_SideHook_Near_1 [PlatformSideHook]
    ├── Platform_SideHook_Far_2 [PlatformSideHook]
    └── Platform_SideHook_Near_2 [PlatformSideHook]
```

### 1.2 Exact Geometry & Coordinate Bounds

| Component | Hierarchy Level | World Center $(X, Y, Z)$ | Dimensions $(W \times H \times D)$ | World Bounds Extents $(X_{\min} \dots X_{\max}, Z_{\min} \dots Z_{\max})$ | Surface Elevation ($Y$) |
|---|---|---|---|---|---|
| **VibratingPlatform (1)** | Root | $(-22.5400, 0.0000, -5.4600)$ | $4.42\text{ m} \times 0.96\text{ m} \times 2.80\text{ m}$ | Frame bounds | $0.0000\text{ m}$ |
| **Vibrating_Rubber_Surface** | Primary Surface | $(-22.5400, 0.0783, -5.4600)$ | $4.1400\text{ m} \times 0.0400\text{ m} \times 1.0800\text{ m}$ | $X: [-24.6100, -20.4700]$<br>$Z: [-6.0000, -4.9200]$ | Top surface: $Y = 0.0983\text{ m}$ |
| **CastingMold** | Center Payload | $(-22.5400, 0.2740, -5.4600)$ | $1.0600\text{ m} \times 0.3500\text{ m} \times 0.7200\text{ m}$ | $X: [-23.0700, -22.0100]$<br>$Z: [-5.8200, -5.1000]$ | Bottom: $Y = 0.1000\text{ m}$<br>Top Rim: $Y = 0.4500\text{ m}$ |

- **Effective Rubber Surface Area:** $4.14\text{ m} \times 1.08\text{ m} = 4.471\text{ m}^2$.
- **Casting Mold Footprint:** $1.06\text{ m} \times 0.72\text{ m} = 0.763\text{ m}^2$ resting in the geometric center of the rubber surface.
- **Exposed Rubber Surface (Flanking Mold):**
  - Left flank ($-X$): Length $1.54\text{ m}$, Width $1.08\text{ m}$.
  - Right flank ($+X$): Length $1.54\text{ m}$, Width $1.08\text{ m}$.
  - Front/Back margins ($\pm Z$): Clearance of $0.18\text{ m}$ along each side.

---

## 2. Vibration Controller Script & Component

- **Script Path:** `Assets/Scripts/VibroEffect.cs`
- **Class:** `VibroEffect : MonoBehaviour`
- **Component Host:** Directly attached to `Vibrating_Rubber_Surface`.
- **Operating Mechanism:**
  - Procedural transform displacement executed in `Update()`.
  - Driven by three independent Perlin noise samples for translation and three for rotation:
    $$\Delta \text{pos}_i = \left(\text{Mathf.PerlinNoise}(\text{\_timeTracker}, \text{\_seed}_i) \cdot 2 - 1\right) \cdot \text{positionMagnitude}$$
    $$\Delta \text{rot}_i = \left(\text{Mathf.PerlinNoise}(\text{\_timeTracker}, \text{\_seedRot}_i) \cdot 2 - 1\right) \cdot \text{rotationMagnitude}$$
  - Direct manipulation of `transform.localPosition` and `transform.localRotation`.
  - Displacements are relative to cached `_originalLocalPos` and `_originalLocalRot`.
  - Physics/PhysX rigidbodies are not used (motion is purely kinematic transform displacement).

---

## 3. Exact Frequency Source, Variable & Units

| Property | Inspection Detail |
|---|---|
| **Class** | `VibroEffect` (`Assets/Scripts/VibroEffect.cs`) |
| **Field Name** | `frequency` |
| **Data Type** | `System.Single` (`float`) |
| **Access Level** | `[SerializeField] private float frequency = 24f;` |
| **Physical Units** | **Hertz (Hz) / Cycles per Second**. In code: `_timeTracker += dt * frequency;`. Each $1.0$ unit advancement in `_timeTracker` completes one continuous cycle through the Perlin noise spectrum. |
| **Current Inspector Values** | `frequency = 24.0f`, `minimumStepFrequency = 6.0f`, `maximumStepFrequency = 28.0f`, `positionMagnitude = 0.008f` ($8\text{ mm}$), `rotationMagnitude = 0.35f` ($0.35^\circ$). |

---

## 4. Actual Operating Frequency Range

### 4.1 Stepped vs. Continuous Architecture

Vibration is controlled by `XRSteppedKnob` on `Main_Vibration_Frequency_Knob`, which has `StepCount = 5` (levels $0, 1, 2, 3, 4$). When the knob turns, it calls `VibroEffect.SetVibrationLevel(int level)`.

```csharp
public void SetVibrationLevel(int level)
{
    level = Mathf.Clamp(level, 0, 4);
    if (level == 0)
    {
        StopVibration();
        return;
    }

    frequency = Mathf.Lerp(minimumStepFrequency, maximumStepFrequency, (level - 1) / 3f);
    if (!_isVibrating)
        StartVibration();
}
```

### 4.2 Operating Frequency Matrix

| Knob Step Level | Operational State | Platform Frequency ($f$) | Displacement Amplitude ($\pm$) | Audio Volume Multiplier |
|:---:|:---:|:---:|:---:|:---:|
| **0** | **STOPPED** | $0.00\text{ Hz}$ (`_isVibrating = false`) | $0.0\text{ mm}$ | $0.05$ (Muted / Stopped) |
| **1** | **LOW VIBRATION** | $6.00\text{ Hz}$ ($f_{\min}$) | $8.0\text{ mm}$ peak | $0.25$ |
| **2** | **MEDIUM-LOW** | $13.33\text{ Hz}$ | $8.0\text{ mm}$ peak | $0.45$ |
| **3** | **MEDIUM-HIGH** | $20.67\text{ Hz}$ | $8.0\text{ mm}$ peak | $0.70$ |
| **4** | **MAX VIBRATION** | $28.00\text{ Hz}$ ($f_{\max}$) | $8.0\text{ mm}$ peak | $1.00$ |

---

## 5. Start/Stop State Detection

### 5.1 Internal State Variables
- `private bool _isVibrating;` in `VibroEffect.cs`.
- When `_isVibrating == false`, `Update()` early-exits (`if (!_isVibrating) return;`).
- Calling `StopVibration()` resets `transform.localPosition = _originalLocalPos;` and `transform.localRotation = _originalLocalRot;`.
- Scene start state: `vibrateOnStart = false`, currently `_isVibrating == false`.

### 5.2 Existing Event Hooks vs. Deficiencies
- `XRSteppedKnob.cs` provides `public XRSteppedKnobEvent onStepChanged` which passes `(int stepLevel)`.
- However, `VibroEffect.cs` currently has **NO public getter for `_isVibrating`** and **NO public getter for `frequency`**.
- Any external particle manager in Phase 2 requires clean, read-only public properties on `VibroEffect`:
  ```csharp
  public bool IsVibrating => _isVibrating;
  public float CurrentFrequency => frequency;
  ```

---

## 6. Existing Particle Resources in the Project

The project already contains production-ready assets specifically tailored for industrial refractory casting:

### 6.1 Available Particle Assets

| Asset Type | File Path | Specifications | Suitability |
|---|---|---|---|
| **Material** | `Assets/Materials/M_RefractoryGrain_Particle.mat` | Shader: `Universal Render Pipeline/Particles/Unlit`<br>Surface: Transparent (`_Surface=1`, `_SrcBlend=5`, `_DstBlend=10`)<br>Albedo Tint: `RGBA(1, 1, 1, 1)` | **Perfect match.** Designed specifically for refractory casting dust/grain visualization. |
| **Texture** | `Assets/Materials/Textures/T_RefractoryGrain_Particle.png` | Format: $256 \times 256$, DXT5 Alpha<br>Visual: Irregular micro-crystalline industrial grains | **High.** Crisp, non-blurry particulates. No billowy smoke or cartoon flames. |
| **URP Default Mat** | `Packages/com.unity.render-pipelines.universal/Runtime/Materials/ParticlesUnlit.mat` | Fallback unlit particle material | Usable as neutral fallback if needed. |
| **URP Particle Shaders** | `Universal Render Pipeline/Particles/Unlit`<br>`Universal Render Pipeline/Particles/Simple Lit` | Standard URP GPU-instanced particle shaders | High performance, fully compatible with XR single-pass instanced rendering. |

---

## 7. Recommended Emission Area & Visual Behaviour

### 7.1 Hierarchy Parenting & Simulation Space

> [!IMPORTANT]
> **Parenting Rule:** The Particle System GameObject **MUST be parented to `VibratingPlatform (1)`**, NOT directly to `Vibrating_Rubber_Surface`.
> 
> If parented to `Vibrating_Rubber_Surface` with Local simulation space, every active particle would inherit the high-frequency $28\text{ Hz}$ Perlin wobble of the emitter transform, creating severe visual jitter and flickering. Parent to the static platform frame and set **Simulation Space = World**.

### 7.2 Emitter Placement & Geometry

- **Emitter Shape:** Box
- **Emitter Position (World):** `(-22.5400, 0.1050, -5.4600)` (resting $5\text{ mm}$ above the top rubber surface).
- **Emitter Dimensions:**
  - Length ($X$): $3.90\text{ m}$ (inset $12\text{ cm}$ from rubber outer edges to avoid emitting in mid-air).
  - Width ($Z$): $0.94\text{ m}$ (inset $7\text{ cm}$ from edges).
  - Height ($Y$): $0.01\text{ m}$ (planar emission volume).
- **Direction:** Upwards along $+Y$ with slight randomized lateral jitter.

### 7.3 Visual Parameter Specifications

To avoid cartoonish smoke or fantasy sparkle:

| Parameter | Recommended Value | Rationale |
|---|---|---|
| **Particle Size** | $0.003\text{ m} - 0.007\text{ m}$ ($3 - 7\text{ mm}$) | Represents physical refractory sand/grit particles rather than voluminous smoke. |
| **Lifetime** | $0.35\text{ s} - 0.75\text{ s}$ | Short lifespan ensures particles pop, hover, and settle quickly without accumulating into clouds. |
| **Initial Velocity ($Y$)** | $+0.04\text{ m/s}$ to $+0.16\text{ m/s}$ | Simulates mechanical throw/hop from surface acceleration. |
| **Initial Velocity ($X, Z$)** | $\pm 0.02\text{ m/s}$ | Slight micro-scatter. |
| **Gravity Modifier** | $0.25 - 0.40$ | Causes lifted grains to arc back down toward the platform. |
| **Color & Alpha Curve** | `RGBA(0.82, 0.78, 0.72, 0.35)` | Muted industrial silica/alumina grain tone. Alpha fades in rapidly ($0.0 \to 0.35$ over $0.05\text{ s}$) and fades out before despawn. |
| **Noise Module** | Frequency: $2.5$, Strength: $0.08 - 0.18$ | Provides subtle turbulent vibration flutter rather than laminar drift. |
| **Max Particles Cap** | $120$ particles | Strict budget protection for XR/VR frame rate. |

---

## 8. Proposed Frequency-to-Emission Mapping

### 8.1 Mathematical Transfer Function

Let:
- $f$ = current vibration frequency in $\text{Hz}$ ($6.0 \le f \le 28.0$).
- $f_{\min} = 6.0\text{ Hz}$, $f_{\max} = 28.0\text{ Hz}$.
- $\text{isVibrating} \in \{\text{false}, \text{true}\}$.

1. **Normalized Frequency Ratio:**
   $$\phi = \text{Mathf.Clamp01}\left(\frac{f - f_{\min}}{f_{\max} - f_{\min}}\right) = \frac{f - 6.0}{22.0} \in [0.0, 1.0]$$

2. **Target Emission Rate ($R_{\text{target}}$):**
   $$R_{\text{target}} = \begin{cases} 
   0, & \text{if } \neg\text{isVibrating} \lor \text{step} = 0 \\
   R_{\min} + \phi \cdot (R_{\max} - R_{\min}), & \text{if isVibrating}
   \end{cases}$$
   Where:
   - $R_{\min} = 12.0\text{ particles/sec}$ (subtle agitation at Level 1, 6 Hz).
   - $R_{\max} = 75.0\text{ particles/sec}$ (vigorous agitation at Level 4, 28 Hz).

3. **Dynamic Response Per Step:**

| Step Level | Frequency | $\phi$ | Target Emission Rate ($R$) | Upward Velocity ($V_y$) | Noise Turbulence |
|:---:|:---:|:---:|:---:|:---:|:---:|
| **0** | $0\text{ Hz}$ | $0.00$ | **$0\text{ particles/s}$** | $0.00\text{ m/s}$ | $0.00$ |
| **1** | $6.00\text{ Hz}$ | $0.00$ | **$12\text{ particles/s}$** | $0.04\text{ m/s}$ | $0.08$ |
| **2** | $13.33\text{ Hz}$ | $0.33$ | **$33\text{ particles/s}$** | $0.08\text{ m/s}$ | $0.11$ |
| **3** | $20.67\text{ Hz}$ | $0.67$ | **$54\text{ particles/s}$** | $0.12\text{ m/s}$ | $0.15$ |
| **4** | $28.00\text{ Hz}$ | $1.00$ | **$75\text{ particles/s}$** | $0.16\text{ m/s}$ | $0.18$ |

4. **Temporal Smoothing (Anti-Popping Filter):**
   To prevent abrupt bursts when the operator clicks between knob detents, the active emission rate $R(t)$ should smoothly approach $R_{\text{target}}$ via exponential damping:
   $$R(t) = \text{Mathf.MoveTowards}\left(R(t), R_{\text{target}}, \text{slewRate} \cdot \Delta t\right)$$
   With $\text{slewRate} = 60.0\text{ particles/s}^2$ (transitions complete within $\approx 0.3 - 0.5\text{ s}$).

---

## 9. Safest Integration Architecture

### 9.1 Architecture Options Evaluated

| Architecture | Description | Pros | Cons | Recommendation |
|---|---|---|---|---|
| **Option A: Monolithic Injection** | Add `ParticleSystem` fields directly into `VibroEffect.cs`. | No new components required; single update loop. | Violates Single Responsibility Principle; tightly couples audio/motion mechanics with rendering VFX. | **Not recommended** |
| **Option B: Dedicated Observer Component** | Add public read-only properties to `VibroEffect.cs`; create `VibrationParticleController.cs` on the particle GameObject. | Clean separation of concerns; safe independent testing; easily enabled/disabled; zero risk to ratchet belt or XR systems. | Requires adding one public property getter to `VibroEffect.cs`. | **RECOMMENDED** |
| **Option C: UI Knob Listener Only** | Hook purely into `XRSteppedKnob.onStepChanged`. | Does not touch `VibroEffect.cs`. | Fragile: ignores direct calls to `VibroEffect.StartVibration()`, `StopVibration()`, or emergency stops. | **Not recommended** |

### 9.2 Recommended Architecture: Option B (Observer Pattern)
1. Add lightweight public getters to `Assets/Scripts/VibroEffect.cs`:
   ```csharp
   public bool IsVibrating => _isVibrating;
   public float CurrentFrequency => frequency;
   ```
2. Create a dedicated component `Assets/Scripts/VibrationParticleController.cs` that references `VibroEffect` and its own `ParticleSystem`.
3. In `Update()`, `VibrationParticleController` queries `VibroEffect.IsVibrating` and `VibroEffect.CurrentFrequency`, computes the smoothed emission rate, and adjusts the `ParticleSystem.emission.rateOverTime`.

---

## 10. Potential Risks & Dependencies

1. **XR/VR Render Budget & Overdraw:**
   - Overlapping transparent particle quads can saturate mobile/XR GPU fill rate.
   - *Mitigation:* Capped emission rate ($\le 75\text{ p/s}$), small particle sizes ($<7\text{ mm}$), low max particle limit ($120$), and short lifetime ($<0.75\text{ s}$).
2. **Transform Shaking Artifacts:**
   - Emitter must not be parented to the vibrating child transform with local simulation.
   - *Mitigation:* Parent to `VibratingPlatform (1)` with `SimulationSpace.World`.
3. **Physics / Interactor Collisions:**
   - Ratchet belts and casting mold contain colliders and XR socket interactors.
   - *Mitigation:* Particle System `Collision` module must remain **disabled** (`collision.enabled = false`). Particles are purely visual cues.
4. **Scene Independence:**
   - Ratchet belts 1 and 2 operate independently on the platform side hooks. The particle system will occupy the platform deck without interfering with hook attach points or belt meshes.

---

## 11. Implementation Action Plan & Target Files

When Stage 2 authorization is granted, modifications will be strictly confined to:

1. **`Assets/Scripts/VibroEffect.cs`**
   - Add `public bool IsVibrating => _isVibrating;`
   - Add `public float CurrentFrequency => frequency;`
2. **`Assets/Scripts/VibrationParticleController.cs`** (New File)
   - Frequency-responsive controller script driving `ParticleSystem`.
3. **`Assets/Exports/RatchetBelt/Demo/MainProject.unity`**
   - Instantiate `Vibrating_Dust_Particles` GameObject under `VibratingPlatform (1)`.
   - Assign `M_RefractoryGrain_Particle.mat` to its `ParticleSystemRenderer`.
   - Wire `VibroEffect` reference on `VibrationParticleController`.

---
*Report prepared in Strict Read-Only Mode. MainProject.unity scene state: `isDirty == False`.*
