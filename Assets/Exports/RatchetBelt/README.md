# Sefpro Industrial Ratchet Belt — Reusable Unity Asset Package (v1.0)

A physically-simulated industrial ratchet tie-down belt asset for Unity, featuring realistic 25 mm polyester webbing with physical hook and crossbar wraps, progressive mechanical ratchet take-up, gradual reversible payout/release, and independent desktop/VR-ready hook manipulation.

---

## 1. Compatibility & System Requirements

| Component | Required Version / Specification |
| :--- | :--- |
| **Unity Editor** | `Unity 6 (6000.4.6f1)` or newer Unity 6 LTS |
| **Render Pipeline** | Universal Render Pipeline (URP) 17.0+ |
| **Physics Backend** | Burst CPU Backend (`com.unity.burst` 1.8.30+) |
| **Mathematics & Collections** | `com.unity.mathematics` (1.3.3+), `com.unity.collections` (2.x / 6.x) |
| **Third-Party Soft-Body Physics** | **Obi Rope 7.2** by Virtual Method *(Must be licensed and installed separately)* |

> [!IMPORTANT]
> **Third-Party Licensing:** In compliance with Virtual Method's license terms, core Obi asset files (assemblies, solvers, scripts, shaders) are **not** redistributed inside this package. Ensure Obi Rope 7.2+ is installed in your target Unity project before importing this package.

---

## 2. Package Contents

```
Assets/
├── Exports/
│   └── RatchetBelt/
│       ├── Prefabs/
│       │   └── RatchetBelt.prefab            # Self-contained, drop-in ratchet belt assembly
│       ├── Blueprints/
│       │   ├── BeltWebbingSection25mm.asset  # 25 mm flat polyester cross-section
│       │   ├── FixedWebbingBlueprint_Wrapped.asset       # Pre-wrapped fixed hook & buckle blueprint
│       │   └── AdjustableWebbingBlueprint_Wrapped.asset  # Pre-wrapped adjustable hook & mandrel blueprint
│       ├── Demo/
│       │   └── MainProject.unity         # Pre-configured demonstration scene with cargo box & lighting
│       └── README.md                         # This documentation file
├── Models/
│   └── RatchetBelt_Industrial.fbx            # High-fidelity industrial ratchet model
├── Materials/
│   └── RatchetBelt/
│       ├── Polyester_Webbing_Blue_25mm.mat   # URP Lit realistic woven webbing material
│       ├── Yellow_Zinc_Steel.mat             # Industrial yellow-zinc plated hardware material
│       ├── Orange_Industrial_Grip.mat        # Ergonomic rubber grip material
│       ├── Stitch_Thread_Gold.mat            # Reinforced safety stitch material
│       ├── Safety_Inspection_Label.mat       # Working load limit compliance label material
│       ├── M_Cargo_Box.mat                   # Demo cargo box material
│       ├── Webbing_Weave_Albedo.png          # High-resolution weave albedo texture
│       └── Webbing_Weave_Normal.png          # High-resolution weave normal map
└── Scripts/
    └── RatchetBelt/
        └── RatchetBeltMechanicsController.cs # Complete runtime mechanics, controls & HUD overlay
```

---

## 3. Quick Setup Instructions

### In an Existing Project:
1. Ensure **Universal Render Pipeline (URP)** and **Obi Rope 7.2** are installed.
2. In Unity Editor, verify that Obi is configured to use the **Burst** backend:
   - Go to `Edit` > `Project Settings` > `Obi` and ensure backend is set to `Burst`.
3. Import `RatchetBelt_Industrial_v1.0.unitypackage` via `Assets` > `Import Package` > `Custom Package...`.
4. Open the demo scene:
   `Assets/Exports/RatchetBelt/Demo/MainProject.unity`
5. Press **Play** to test interactive tightening, release, and hook dragging.

### Adding to a Custom Scene:
1. Drag `Assets/Exports/RatchetBelt/Prefabs/RatchetBelt.prefab` into your scene hierarchy.
2. Position the `RatchetBelt` root GameObject as desired.
3. For any objects the webbing should collide with (e.g. cargo boxes, pallets, truck beds), add an **`ObiCollider`** component alongside standard Unity `BoxCollider` or `MeshCollider` components.
4. Set the `interactionCamera` reference on `RatchetBeltMechanicsController` to your player/scene camera (defaults to `Camera.main` if null).

---

## 4. Controls & Interaction Guide

### Desktop Controls (Mouse & Keyboard):
- **Ratchet Tightening (Handle Drag):**
  - Click and drag the **Ratchet Handle** forward (clockwise) past the trip angle ($55^\circ$).
  - Each completed forward stroke advances the ratchet by **1 tooth**, rotates the mandrel by **$30^\circ$**, and takes up **$10\,\text{mm}$** ($0.010\,\text{m}$) of webbing rest length.
  - Return the handle back upright ($< 5^\circ$) to reset the pawl mechanism for the next stroke.
- **Power Stroke Hotkey:**
  - Press **`[P]`** or **`[Space]`** to execute a discrete single-tooth power stroke without mouse dragging.
- **Loosening & Step Release:**
  - Press **`[L]`** or click the **`Release (1 Tooth)`** HUD button.
  - Pays out **$10\,\text{mm}$** of webbing slack smoothly and unwinds the mandrel drum by **$30^\circ$**.
- **Continuous Loosening:**
  - Hold **`[L]`** or toggle the **`Loosen / Release Belt`** HUD button.
  - Webbing rest length smoothly pays out at $80\,\text{mm/s}$ back toward the baseline rest length.
- **Full Release:**
  - Click the **`Full Release`** HUD button.
  - Webbing rest length smoothly unwinds all accumulated tension back to baseline ($0.0\,\text{mm}$ shortening, mandrel at $0^\circ$).
  - Webbing drapes naturally under gravity without snapping or jitter.
- **Independent J-Hook Dragging:**
  - Click and drag **`J_Hook_Fixed`** (left hook) to position the fixed anchor.
  - Click and drag **`J_Hook_Adjustable`** (right hook) to position the adjustable anchor.
  - Webbing wraps, stitches, and simulated rope particles track the hook translations with sub-millimeter precision ($\Delta_{\text{Wrap}} = \Delta_{\text{Hook}}$).
- **Reset All:**
  - Press **`[R]`** or click **`Reset All`** to restore the handle, mandrel, teeth count, both hooks, and webbing rest lengths to the pristine initial baseline.
- **Toggle HUD:**
  - Uncheck `Show HUD` on the `RatchetBeltMechanicsController` component in the Inspector to hide the on-screen UI overlay.

---

## 5. Technical Specifications

- **Nominal Webbing Width:** $25.0\,\text{mm}$ ($0.025\,\text{m}$).
- **Webbing Thickness:** $\approx 2.4\,\text{mm}$ ($0.0024\,\text{m}$).
- **Take-up Rate:** $10.0\,\text{mm}$ per tooth ($0.010\,\text{m}$).
- **Mandrel Angular Advance:** $30.0^\circ$ per tooth ($12\text{-tooth}$ industrial ratchet gear).
- **Reel & Payout Velocity:** $0.08\,\text{m/s}$ ($80\,\text{mm/s}$) rate-limited physics interpolation.
- **Over-Reel Protection:** Hard clamp at baseline rest length ($0.0\,\text{mm}$ shortening); cannot over-extend.
- **Physics Stability:** 0 NaNs, Burst CPU multi-threaded solver execution, maximum particle jitter velocity $< 0.15\,\text{m/s}$.

---

## 6. Known Considerations & Best Practices

1. **Obi Burst Backend:** For optimal performance and zero solver jitter, ensure `Burst` backend is selected in Obi project settings.
2. **Fixed Webbing Isolation:** As on a real ratchet belt, the short fixed webbing span between the fixed hook and the buckle chassis remains invariant in rest length during ratcheting; all spooling and take-up occurs on the adjustable span.
3. **VR Controller Support:** The `RatchetBeltMechanicsController` exposes public methods (`SetHandleAngle()`, `TriggerToothClick()`, `TriggerStepRelease()`, `TriggerFullRelease()`, `MoveFixedHook()`, `MoveAdjustableHook()`, and `ResetState()`) designed for direct binding to XR Interaction Toolkit, Oculus Integration, or SteamVR grab interactables.

---
*Created for Sefpro by Advanced Agentic Coding.*
