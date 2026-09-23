# Ringworld Shorts Capture Plan

Format target: **9:16 vertical, 1080 × 1920, 30 FPS, 12 seconds** on the certified **5,000 km radius / 318 km wide** ring. Every route loads at the Very High terrain preset and Ultra cloud quality, holds still for an 8-second pre-roll, and trims that loading period from the delivery file. Each final clip opens with a Halo-player-focused field-note card, then clears every HUD element for the gameplay shot.

## Recording set 01

- [x] Build the clean start-menu, world-foundry, gameplay HUD, and pause-menu visual system.
- [x] Add `F8` cinematic mode, `F6` opt-in engineering diagnostics, and `F1` control hints.
- [x] Add repeatable engine-level camera choreography for the first three topics.
- [ ] Record **01 — What Standing on a Halo Ring Feels Like** (`scale`).
  - Hook: “Halo players—this is what a full-scale ring feels like from the ground.”
  - Shot: rise from low flight toward the 850 km scaled-space handoff while the inner surface climbs overhead.
  - On-screen copy: “5,000 KM RADIUS • 318 KM WIDE • FULL-SCALE HABITAT.”
  - Caption idea: “A first-person walkable ring at the certified Halo-scale target—not a skybox.”
- [ ] Record **02 — The Sky Is Part of the Ring** (`clouds`).
  - Hook: “A Halo ring would need real weather, not painted clouds.”
  - Shot: lock onto a generated seamless-union cluster and drift toward the merged cloud mass.
  - On-screen copy: “ULTRA CLOUDS • SEAMLESS STORMS • INNER-SURFACE WEATHER.”
  - Caption idea: “The clouds live inside the same 5,000 km radius habitat and merge without overlap seams.”
- [ ] Record **03 — This Halo-Scale Ring Is Editable** (`voxels`).
  - Hook: “The ring isn’t scenery—you can build on it.”
  - Shot: hold on a terrain-level composition while a concrete gateway is built block by block.
  - On-screen copy: “1 METER VOXELS • 5,000 KM RADIUS.”
  - Caption idea: “One-meter voxel edits rebuild locally while the Halo-scale terrain LOD stays intact.”

## Follow-up topics

- [ ] **From One-Meter Voxels to Planet Scale** — demonstrate detailed chunks handing off through the terrain LOD stack.
- [ ] **Fast Travel Without Moving the World** — explain canonical coordinates, floating origin, and cross-ring travel.
- [ ] **Cloud Shadows Across a Storm System** — compare local conventional casters with projected long-range shadows.
- [ ] **A Halo-Scale Habitat** — reveal the 5,000 km radius / 318 km width preset once the larger-world presentation route is ready.
- [ ] **Digging Into the Ring** — excavate below the terrain surface and expose structural depth.
- [ ] **Daylight on the Inside** — show atmosphere and lighting changes against the rising far side of the ring.

## Capture controls

- `F8`: toggle the clean HUD for manual cinematic shots.
- `F6`: toggle engineering/LOD diagnostics.
- `F1`: reopen the compact control hint.
- Automated topics: `scale`, `clouds`, and `voxels`.

Run all three with:

```powershell
./tools/record_shorts.ps1 -Topic all
```

Godot writes a lossless-timing 16:9 engine master containing a centered 540 × 960 vertical render. The recording script then crops and upscales that exact 9:16 render to a delivery-ready 1080 × 1920 H.264 MP4. Both files remain in `%APPDATA%\Godot\app_userdata\RingworldTechDemo\generated\captures\shorts\`, outside the OneDrive checkout.
