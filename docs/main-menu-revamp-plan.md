# Main menu revamp plan

Builds on the existing flyover menu (`scenes/StartMenu.tscn`,
`scripts/start_menu.gd`, `scripts/ui/RingMenuSpaceBackdrop.cs`; see
`docs/main-menu-space-checklist.md`). Visual direction: evolve the current
left-column layout, not a new style. A Settings screen is out of scope.

## Phase A — quick wins

### A1. Continue
- `WorldSession.get_most_recent_slot()` returns the existing, configured slot
  (non-empty `generation_settings`) with the newest `last_played_unix`, or 0.
- The main panel shows **CONTINUE** above **ENTER RINGWORLD**, with a muted
  subline: world name and "last played 2 days ago". Hidden when there is no slot.
- It loads through the same path as the slot action panel's **Load World**.
- Keyboard focus starts on Continue when it is visible, otherwise on Enter.

### A2. Intro reveal
- `RingMenuSpaceBackdrop.GetBackdropProgress()` returns 0–1 across the
  near-cache rows and far-terrain samples.
- The menu fades in from black, and the title, subtitle and buttons stagger in.
- A muted **SURVEY CACHE 64%** readout is shown until the backdrop is ready, then fades out.
- When the backdrop becomes ready, travel speed eases in over
  `MotionEaseInSeconds` (smoothstep) instead of snapping to full speed.

### A3. Time-of-day drift
- Effective sun elevation = `SunElevationDegrees + SunDriftAmplitudeDegrees *
  sin(2π t / SunDriftPeriodMinutes)`, where t is the flyover animation time.
  An amplitude of 0 means off. Freeze/Paused also stop the drift, so
  deterministic fixtures are unchanged.
- Drift updates the sun at a low rate through a sun-only path that does **not**
  reset cloud temporal history. Drawer edits keep their existing reset behavior.
- Grave-key drawer: the Lighting tab gets amplitude and period, and the Camera
  tab gets `MotionEaseInSeconds`. These settings persist in the menu preset.

### A4. Motion
- A shared helper, `scripts/ui/menu_motion.gd`, provides:
  - an incoming panel fade and 24 px slide (~180 ms, children staggered);
  - a hover underline that grows from the left;
  - a 0.97 press tap.
- Panel visibility switches synchronously; only the visuals animate.

## Phase B — core revamp
- **B1. World cards:** thumbnail (captured on save/exit as
  `slot_N/thumbnail.png`, with a seed-tinted ring fallback), name, last played,
  playtime and size. Seed and cave details are behind a "Details" toggle.
  Selecting a card expands Load/Duplicate/Rename/Delete in place, which
  replaces the separate action panel. Empty slots become a "+ New World" card.
- **B2. New World screen:** name, seed with reroll, and a size preset
  (Compact/Standard/Vast). **Advanced…** opens `WorldSizeLauncher`.
- **B3. Delete confirmation:** a modal (type the name, or hold for 1.5 s).
  Duplicate targets are chosen inside the card.
- **B4. Keyboard/gamepad navigation:** explicit focus neighbors, a focus ring
  that matches hover, and Back mapped to Esc/B.
- **B5. Camera states per screen:** main = flyover, world select = higher with
  more pitch-up to show the ring arc, card focus = slow push-in. Blends use
  critically damped springs. The poses and blend time are drawer tunables.
- **B6. Style pass:** a fixed type scale, uppercase kicker labels, a footer
  with live monospace readouts, and a gradient retuned for card readability.

## Phase C — live world behind the menu (2026-10-21)

The last-played Standard world loads in the background behind the start screen.
Continue flies the camera to the save spot and descends into first person.
Save & Exit climbs back to menu altitude without a scene change.

**Decisions**
- **Cruise altitude:** about 16 km. Five systems switch at exactly 20 km, so stay clear of it.
- **Warm-loading:** at boot. The decorative flyover shows first and crossfades
  to the live world once it is ready.
- **Menu route:** loiter within tens of km of the save spot, so Continue is a
  20–60 s fly-in.
- **Loading screen:** keep it for New World, Advanced and loading a different
  slot. The live flow covers only the last-played world. If there is no saved
  world, or it is a legacy/Compact world, keep the decorative flyover.

**Constraints found**
- About 50 systems stream around `Explorer.CanonicalPosition`. The cinematic
  rig must drive the Explorer (scripted noclip, input held), not a separate camera.
- A world running behind the menu costs about 1.3–1.5 GB VRAM and 3 GB RAM.
  Clouds seen from above are the main GPU cost.
- Saves must stay held (`HoldSavesUntilWorldReady`) during cinematic flight, or
  autosave records the camera pose as the player's position.
- The Terrain3D gameplay window (768 m) cannot follow km/s flight. Request it
  once at the destination before descending.

**Phases**
- **P0, measure:** world cold-start time (`WORLD_LOADING_REPORT`), and GPU/VRAM
  at 16 km near the save spot, at the current quality and with low-cost clouds.
  Measure on both machines.
- **P1, menu-altitude state:** reuse the underground participant switchboard,
  the LOD coordinator's Low preset, Terrain3D workers = 1 and a low-cost cloud
  quality. No new per-system toggles.
- **P2, cinematic Explorer driver:** a scripted noclip path with input and saves
  held, staging of the saved pose, and destination pre-streaming.
- **P3, background loader mode:** no overlay, no `disable_3d`, no SubViewport
  holds. StartMenu stays the current scene. Crossfade from the flyover to the
  live camera.
- **P4, transitions:** Continue fly-in, descent gates (collision and visual
  coverage) and handoff. Save & Exit ascent without a scene change.
- **P5, teardown and swaps:** tear down the warm world before using the loading
  screen for another slot.
- **P6, tests:** update the menu, save-exit and loading lifecycle tests, and add
  a transition test.
- **Grave-key tunables:** cruise altitude, loiter radius, rush speed, descent
  seconds and crossfade seconds, in the menu drawer's Camera tab.

**Whole-ring low-detail bake (estimate)**
- **LOD4 (16 m):** about 15 GB compressed per world, 1–4 h to bake.
- **LOD6/LOD7 (64/128 m):** 0.4–1.4 GB and minutes to bake. This tier matches
  the view from menu altitude.
- Bakes are per world (seed and recipe) and must be redone when the generator
  version changes. The existing sector cache format is unsuitable for this.

## Phase C (original ideas, deferred)
- **C1. Selected-world preview:** re-seed the far mesh with a crossfade when a
  card is focused. Prototype it first, because a full cache rebuild is 25–30 s.
- **C2. POI flybys:** the route bends past the Weather Lung or the beam spire.
  This needs POIs in the menu renderer.
- **C3. Audio:** reuse the `RingWeatherAudio` wind bed, a sparse music layer,
  and UI ticks. Volumes are drawer tunables.

## Status
- [x] Phase A (implemented; awaiting in-game visual check of motion and sun drift)
- [x] Phase B (Standard/Compact presets, hold-to-delete, Save & Exit to Menu added; awaiting in-game check)
- [ ] Phase C
