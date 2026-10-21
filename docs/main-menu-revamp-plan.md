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

## Phase C — ambitious
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
