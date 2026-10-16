# Eclipse "False Sun" Event (hub artificial sun)

Status: design spec, read-only investigation, 2026-10-16. No project files were edited apart from this document. The timing numbers below were measured by compiling the real `scripts/CelestialEclipseMath.cs`, `CelestialFrame.cs`, `CelestialConfiguration.cs` and `GasGiantConfiguration.cs` against small Godot type stubs in a scratch console app, using the default configuration (`resources/celestial/*.tres`) and the authored `StarLight` basis from `scenes/VoxelRingIntegrationTest.tscn:122-124`. No windowed or GPU test was run.

Companions:
- [emitter-poi-streaming-spec.md](emitter-poi-streaming-spec.md), the "POI spec" (§B.7 beam hooks, USER and MAIN SPIRE DECISIONS).
- [emitter-grid-climate-machines-spec.md](emitter-grid-climate-machines-spec.md), the "grid spec" (G1-G7, the G4 and T2 notes, USER DECISIONS).
- [emitter-milestone1-handoff.md](emitter-milestone1-handoff.md).
- [cloud-diagnostics-2026-10-16.md](cloud-diagnostics-2026-10-16.md) (night cloud ambient and show-through).
- `prototypes/pda-voice/README.md` (PDA voice prototype).

Scope: during each gas-giant total eclipse, the main spire fires to the ring hub and kindles an artificial sun. A bidirectional ignition pulse then lights every spire's beam, and the hub sun reaches 80 % of the real sun, lighting the whole ring from each point's zenith ("noon everywhere"). The regular spires stand down and the normal one-way grid pulse refuels the sun. The sun collapses dramatically as totality ends. Before ignition and after the collapse the eclipse is near pitch black. Dead emitters are a seam only.

The binding user decisions are at the end. §1 records how this spec interprets them, and §Q lists the open questions.

---

## 0. Findings that shape the design (evidence)

| # | Finding | Evidence |
|---|---|---|
| F1 | **Eclipse cadence and length (verified).** The giant's orbit is T = 28,187.37 s = **7.830 h** of celestial time. Centred-observer totality lasts **4,500.00 s = 75.0 min**, with 44.6 s partial phases on each side. Eclipse k is centred at T·(k + ½) = 14,093.7 s, 42,281.0 s, 70,468.4 s, … (InitialPhase 180°). A fresh world (celestial t = 0) sees its first totality about 3.3 h in. | `GasGiantConfiguration.cs:51-57`; `gas_giant_default.tres`; `CelestialEclipseMath.cs:221-240` (analytic), measured |
| F2 | **The umbra sweeps; it does not cover the ring at once.** Observer parallax (5,000 km ring radius against a 136,610 km giant distance) shifts second contact by **−196 … +195 s** with ring longitude. Measured for eclipse 0: the earliest local totality anywhere starts at 11,647.3 s; **every longitude is in totality during [12,038.7, 16,148.7] s = 4,110 s (68.5 min)**; some longitude is in totality during [11,647.3, 16,540.0] s = 4,893 s. The share of the ring in totality rises 0 → 100 % over about 390 s, nearly linearly. Local durations are 4,437-4,528 s. Altitude barely matters (150 km: −5.7 s). | measured; `RingDayNightCycle.cs:450-465` (observer offset) |
| F3 | **The renderer presents the eclipse ring-wide at the player's local timing.** `day_side_atmosphere.gd:298-301` publishes `1 − coverage` of the *player's* parallax-corrected eclipse as the single global `ringworld_solar_visibility`. Every shader multiplies its per-point day by it (`celestial_lighting.gdshaderinc:1-7`), so the far arch goes dark and light together with the player. | cited |
| F4 | **The eclipse lasts one ring day.** The day is 4,500 s, so the ring turns 329° during the whole-ring window. Every point passes through its would-be noon in darkness: totality is not a night-side event. | `CelestialConfiguration.cs:8-13` |
| F5 | **Celestial clock.** Absolute time, evaluated without accumulation, saved and restored. It advances by `delta × TimeScale`, where TimeScale is 1-100 (`RingDayNightCycle.cs:30-39, 161-171`), and it can be paused. At 100× a totality lasts 45 real seconds. Save fields: `RingWorldSaveManager.cs:119-124, 285-288, 1023-1030`. | cited |
| F6 | **Weather clock.** `CloudSimulationTimeSeconds` advances by the real frame delta unless `CloudSimulationPaused` (`RingVolumetricCloudLayer.cs:21, 582`) and is saved. The grid pulse runs on it (`EmitterGridBeamLayer.cs:409-414`). The two clocks are independent saved values: there is no fixed offset between them. | cited |
| F7 | **Grid pulse speed (verified).** v = C / 300 s = 31,415,936 / 300 = **104,719.8 m/s**. Half the ring takes **150.0 s**. A 314 km row pitch takes 3.0 s. A bidirectional front from the main spire therefore reaches rows m±k at 3k s and ignites **12 beams every 3 s** (6 columns × 2 directions), with the 6 antipode beams (row m+50) at 150 s: 49 × 12 + 6 = 594. | `EmitterGridPulseClock.cs:59-62, 81-84`; grid spec G3 |
| F8 | **Sun pipeline has three writers.** `RingDayNightCycle.ApplySunDirection` writes the `StarLight` basis from `CurrentFrame.ToSun` (`:503-544`). `day_side_atmosphere.gd` writes its colour (`:334`) and energy `day_sun_energy (4.0) × solar visibility` (`:22, 27, 343-350`); the scene's 3.0 is overwritten every frame. Shadow settings belong to the graphics quality controller. Most layers read the sun direction from `StarLight.Basis.Z` (for example `EmitterGridBeamLayer.cs:428`, `RingStandingGrassLayer.Lighting.cs:38`, `RingTerrainGlobalLod.cs:1797`, `RingScaledFarViewport.cs:2318-2328`). | cited |
| F9 | **One function feeds the per-point day factor of 25 shaders.** `ringworld_effective_solar_day(g) = g × ringworld_solar_visibility` is used by terrain (Terrain3D, proxy, global, voxel, hull), trees, grass cards, ocean, sky, atmosphere, every far and near cloud shader, rain curtains, stand-ins and scale anchors (the grep list is in §C.2). Changing that one function changes "day" everywhere. With the new uniforms at 0 it stays bit-identical. | `celestial_lighting.gdshaderinc:5-7` |
| F10 | **Direct light is a single global direction.** 13 shaders have a custom `light()` that uses `LIGHT` from the DirectionalLight. The far viewport lights `ring_terrain_global`, `ring_ocean_global` and `ring_hull_global` with its own `FarRingSun`, which mirrors `StarLight` each frame (`RingScaledFarViewport.cs:971-977, 2318-2328`). A light aimed at the camera's zenith would light the opposite arch from behind (n·l = −1). Per-fragment light is unavoidable there. | `ring_terrain_global.gdshader:148-200`; `ring_terrain_proxy.gdshader:202-239`; `terrain3d_ring_surface.gdshader:327-356` |
| F11 | **The 0.95 cap does not limit totality today.** Direct sun and the published visibility both use raw coverage, so totality is already exactly 0 (`day_side_atmosphere.gd:26-31, 298-301, 346`; sky comment `:757-761`). `GameplayEclipseDarkness` only tints fog and sky by `darkness × day factor` (`:343, 349-358, 707-711`). That product is 0 in totality, so the cap matters only in the partial phases. A test pins it (`tools/CelestialCoreSmokeRunner.cs:277-295`, and the partial scan `:263-270`). **What keeps totality bright is the night baseline:** ambient (0.34, 0.40, 0.52) × 0.30 (`:46-48, 381-387`), cloud night ambient (0.18, 0.23, 0.35) (`RingSunshineCloudBridge.cs:94, 942-950`), atmosphere night colour (0.026, 0.035, 0.055) (`RingAtmosphereModel.cs:74`), and the far viewport's frozen ambient (F12). | cited |
| F12 | **The far viewport's ambient is frozen at creation.** `AmbientLightColor` and `AmbientLightEnergy` are copied once from the main environment (`RingScaledFarViewport.cs:937-941`) and never updated (`_farEnvironment` is only touched for volumetric fog at `:3154, 3367`). The opposite arch keeps the ambient that was current when the world loaded. | cited |
| F13 | **Sunshine is lit by `StarLight`.** The driver reads the light's direction and colour × energy each frame (`SunshineCloudsDriver.gd:230-235`). The bridge passes the solar visibility in the atmosphere profile (`RingSunshineCloudBridge.cs:1008, 1023`). Re-aiming `StarLight` at the camera zenith therefore gives hub-lit cloud tops with no Sunshine change. The night floor `max(ambient, ambientLightColor × 0.35)` (`SunshineCloudsCompute.glsl:1357`) means a black cloud ambient makes clouds translucent. Today's fix restored `NightCloudAmbientColor` to the code default for exactly that reason (cloud diagnostics §3). | cited |
| F14 | **Hub location.** `ScaledPoint(any, R, W/2)` is the scaled-world **origin** (`RingScaledFarViewport.cs:3405-3416`). `AddScaledWorldChild` exists (`:3395-3402`). The far camera's far plane is ≥ 100,000 units, i.e. 100,000 km real (`:907-917, 2310-2313`). The main camera's is 1,200 km. Seen from the floor, the hub lies inside the opposite-arch band (at most 1.52° from it), so the hub must draw *in front of* far terrain. | cited, derivation §E.1 |
| F15 | **Grid front encoding has two spare lanes.** `eg_fronts[24]` is `vec4(headQ, column, 0, 0)` (`EmitterGridBeamLayer.cs:25, 579`; `ring_emitter_grid_pulse.gdshaderinc:14, 41-47`). Direction is one layer-wide uniform. `MaxHeadSprites` is 12. At auto speed only 6 fronts are live. | cited |
| F16 | **Beam geometry hook exists.** `EmitterSiteFrameContext.Beam` computes crown → hub geometry (`EmitterSiteFrameMath.cs:146-159`). Crown heights are 1,743 m (regular) and 3,989 m (main) (`:166-167`). The tilt is at most atan(132.5/4,998) = **1.519°** and the length about 4,998 km. `IEmitterBeamSource` holds boolean flags only (`IEmitterBeamSource.cs:13-25`), implemented by `EmitterStandInLayer` (`:365-394`). | cited |
| F17 | **Sun-direction side effects.** The whole-ring macro shadow mask is rebuilt whenever `StarLight` turns more than 0.51° (`RingTerrainGlobalLod.cs:1794-1805`). In hub mode, where the light follows the camera zenith, that is every 44 km of travel. The local shadow handoff keeps the sun shadow pass on whenever the sun is above the local horizon (`RingGraphicsQualityController.LocalShadows.cs:47-65, 126-155`), **including in totality at zero energy**: a pre-existing wasted shadow pass. The planet light already skips zero-energy shadows (`day_side_atmosphere.gd:886-888`). | cited |
| F18 | **Exposure is constrained.** Auto-exposure sensitivity is limited to 50-160 (1.7 stops) (`day_side_atmosphere.gd:65-69, 640-653`), so it cannot lift a dark eclipse much. Tonemapping is AgX with white 16.29. There is no glow pass (grid spec F11). | cited |
| F19 | **Precedents.** For sound delay, `ThunderPropagation` (343 m/s, 25 km cap, `LateSeconds` for seeking a missed arrival) (`scripts/Weather/ThunderPropagation.cs:10-25`). For the announcer, the PDA prototype has 20 events, an authored fallback, an LLM rewrite with a validator and a ~2 s timeout, critical events that skip the LLM, and Kokoro/Piper TTS (`prototypes/pda-voice/README.md`, `events.json:4-18`). The time-jump UI is `CelestialDebugHud` (F9; `CelestialDebugHud.cs:72-103`). | cited |
| F20 | **Performance context.** On the A1000 at 1600×900 the CPU floor is 20-23 ms, the far SubViewport costs 4.8 ms of render CPU, and draw calls and main-thread work are scarcer than GPU fill (grid spec F19). The eclipse solver evaluates `EvaluateFrame` in about 2 µs, so a 10°-longitude window search (3,672 evaluations) costs about 7 ms: run it off the main thread. | `docs/perf-ranking-2026-10-15.md`; measured |

---

## 1. Interpretation of the user decisions

This is how the spec reads the 2026-10-16 decisions. Every point below is implemented as stated unless an open question (§Q) changes it.

1. **"Noon everywhere".** Every surface point is lit from its own local zenith (the inward radial). The 1.52° tilt toward the hub on the centre line is ignored in shading (cos 1.52° = 0.99965) so that shadows, shading and the DirectionalLight agree. Full hub brightness means direct light at 80 % of the real sun's energy (3.2 instead of 4.0) and a day factor of 0.8 (§C.5).
2. **The main spire fires first.** Its beam climbs to the hub in a stylised time. The hub kindles to 10 % when the beam arrives.
3. **The ignition pulse is bidirectional.** After the kindle, 12 fronts leave the main spire: one forward (via `KnotOut`) and one backward (via `KnotIn`) on each of the 6 chains. A site fires its continuous beam at the **first** front to reach it, at `t_ign + min(p, C − p) / v`. The two fronts meet at the antipode at 150 s at the default speed and end there with a meeting flare.
4. **Brightness counts beams at the hub.** Each beam adds its share when its tip *reaches the hub* (fire time + climb), not when it fires. The default is linear in energy: 12 beams per 3 s, so steps of 1.8 % (§B.3).
5. **Hand-off.** A short crescendo with all 595 beams lit, then all 594 regular beams fade together. The main beam stays on for the rest of the event.
6. **Refuel.** During Sustain, each regular site flashes a short beam to the hub when the *normal* grid front passes it. The time comes from `EmitterGridPulseClock.ArrivalTime(q_site, column, n)` on the weather clock; no new clock is added. At the defaults that is one row of 6 flashes every 3 s marching around the ring, and each site flashes once per 5-minute circuit. Each row's arrival at the hub gives the sun a small "heartbeat" kick. The main spire's continuous beam keeps the sun at full brightness; the sun does not fade between pulses.
7. **Collapse.** Instability, then an overload flare, an implosion, a light shockwave and the beams cut. The collapse ends before the first place on the ring leaves totality. The real sun then returns at the player's own third contact (§A.3).
8. **Dark or dead emitters.** A seam only: `IEclipseSiteOutput.Output(site)`, which defaults to 1 everywhere (§B.5).

---

## A. Eclipse geometry and timing

### A.1 Measured numbers (default configuration, eclipse 0)

| Quantity | Value |
|---|---|
| Orbit / eclipse period | 28,187.37 s (7.830 h) |
| Centred totality (zero observer offset) | [11,843.68, 16,343.68] s, **4,500.00 s** |
| Partial phase (ingress / egress) | 44.63 s / 44.63 s |
| Star-giant separation rate at contact | 0.01186 °/s (star radius 0.265°, giant apparent radius 30.82°) |
| Local second contact versus centred | −196 … +195 s by longitude (spread 391 s); latest at longitude ≈ 146°, earliest ≈ 358° (relative to the authored epoch sun) |
| Local totality duration | 4,437-4,528 s |
| **Whole-ring totality window** (every longitude total) | **[12,038.7, 16,148.7] s = 4,110 s** (sampling error ≤ 2 s at 10° steps) |
| Any-longitude totality | [11,647.3, 16,540.0] s = 4,893 s |
| Share of ring in totality, t − centred start = −240 / −120 / 0 / +120 / +240 s | 0 / 28 / 50 / 69 / 100 % |
| Across position (0 / 150 / 300 km) and altitude (150 km) | ±2 s and −5.7 s |
| Totality at TimeScale 10 / 100 | 7.5 min / 45 s real time |

### A.2 Sweep versus presentation

Physically the umbra sweeps the ring in about 6.5 minutes (F2). The renderer, however, shows the player's local eclipse on the whole ring (F3). The spec keeps that model:
- Lighting stays driven by the player's local coverage.
- The **event** is ring-global and deterministic (§A.3), so every spire fires at the same instant wherever the player is.
- A per-point umbra in the shaders (each fragment computes its own parallax-corrected coverage) is listed as optional polish (phase E7). It is not needed for correctness.

### A.3 Anchor: the whole-ring window

**Decision (open question Q1):** the event runs inside the **whole-ring totality window** `[S, E] = [max over longitudes of second contact + margin, min over longitudes of third contact − margin]`. The margin `ParallaxSafetySeconds` defaults to 10 s.

Why:
1. **Deterministic.** The window depends only on the celestial configuration and the eclipse index, never on where the player is. A teleport cannot shift a phase boundary.
2. **The hub sun and the real sun never coexist.** The player's local totality contains `[S, E]` wherever they stand, so local solar visibility is exactly 0 for the whole event. Every switch of the DirectionalLight between sun and hub then happens at zero energy and is invisible (§C.4).
3. **The parallax physics stays unchanged.** `EvaluateFrame` and the `AssertObserverParallax` test are untouched.

Cost: the darkness before ignition is `DarknessBeat + (S − local second contact)`, so 20-411 s with the defaults depending on longitude. After the collapse the player waits `local third contact − E`, 0-401 s, for the real sun. The PDA fills that wait (§F.3). The alternative is a ring-uniform eclipse (drop parallax at runtime, so the sun returns right after the collapse everywhere). It is open question Q1.

**`EclipseWindowSolver`** (pure, new file `scripts/Celestial/Eclipse/EclipseWindowSolver.cs`):
- `EclipseWindow Solve(int k)`. Bisect the centred contacts near T·(k + ½ − InitialPhase/360 + ½), then bisect each of 36 longitudes (10°) at across W/2 and the ground, using `CelestialEclipseMath.EvaluateFrame(..., calculateGiantEvents: false)` and `EclipseType == Total`. Return `(CentredStart, CentredEnd, RingStart S, RingEnd E, AnyStart, AnyEnd)`.
- Run it on a worker (`Task.Run`, about 7 ms). Cache the current and next eclipse. Until the worker returns, the timeline uses the centred window shrunk by 200 s, which is conservative; the event is always more than an hour ahead in normal play.
- `LocalContacts(k, observerOffsetKm)` gives the player's own second and third contacts (2 bisections, about 0.2 ms). Recompute every 10 s for the PDA and the Return phase.
- If the giant is disabled or there is no total eclipse (for example the partial fixture miss of 30.72°), there are no windows and the event never runs.

### A.4 Clocks: which drives what

| Driven by the **celestial clock** (event time τ = t_cel − S) | Driven by the **weather clock** |
|---|---|
| Phase boundaries, darkness, main fire, kindle, the ignition fronts and every site's ignition time, the ramp, crescendo, hand-off, collapse | The normal grid pulse (unchanged), therefore the Sustain refuel flashes (`ArrivalTime`), the heartbeat kicks, the "breathing" |

- **Why the celestial clock owns the event:** totality is a celestial fact, so the collapse must land at E in celestial time whatever the TimeScale.
- **Why refuel stays on the weather clock:** the user asked for the existing grid pulse with no new clock, and the grid already runs there (grid spec G3).
- **Time-scale hold (open question Q2).** With TimeScale > 1 every celestial-timed beat compresses: at 10× the ignition front would travel at 1,047 km/s. Setting `HoldRealTimeDuringEvent` (default **on**): `RingDayNightCycle._Process` advances by `delta × min(TimeScale, 1)` while `t ∈ [S − PreRollSeconds, E + 60 s]`, and caps a step that would cross `S − PreRoll` so that it lands exactly there. `TimeScale` itself (the saved user choice, `RingWorldSaveManager.cs:124`) is not modified. The hold is a pure function of t, so it re-applies after loading. With the hold on, both clocks advance at the real rate during the event, and the celestial ignition front and the weather-clock grid fronts move at the same speed.
- **Save and load, including mid-event.** All event state is a pure function `EclipseEventTimeline.Evaluate(t_cel, t_weather)`. Both clocks are already saved, so no new save field is needed, and loading mid-event reproduces the hub level, which beams are lit, and the collapse stage exactly.
- **Discontinuities.** Following the weather consumers' rule (grid spec F12), a rewind or a jump of more than 5 s on either clock is a discontinuity. The event stream then emits one `Resync(phase)` instead of replaying the one-shot events in between (§F.1). Visual state needs no resync because it is stateless.
- **Pauses.** A paused celestial clock freezes the event, while grid pulses and refuel flashes continue on the weather clock. A paused weather clock freezes refuel only.

---

## B. Timeline with defaults

### B.1 Phases (`EclipseEventPhase`)

`Idle → PreRoll → Darkness → MainFire → Kindle → Ignition → Crescendo → Handoff → Sustain → Instability → Overload → Implosion → Shockwave → Aftermath → Return → Idle`

| Phase | Starts at (event time τ = t_cel − S, defaults) | What happens |
|---|---|---|
| PreRoll | S − 300 s (player-local warnings use the local contacts) | Natural partial eclipse; PDA warnings; time-scale hold engages |
| Darkness | τ = 0 | Near pitch black (§D); the grid is night-bright |
| MainFire | `DarknessBeat` = 20 s | Main beam tip climbs 4,000 km+ in `MainClimb` 4.0 s (ease-in, f = u²) |
| Kindle | 24.0 s | Hub ramps 0 → `KindleLevel` 10 % over 2.5 s; hub body appears small |
| Ignition | 29.5 s (`IgnitionDelay` 3 s after the kindle completes) | 12 fronts leave the main spire; site s fires at 29.5 + min(p_s, C − p_s)/v; beam tips climb `RegularClimb` 1.5 s; each hub arrival adds its share |
| Crescendo | 181.0 s (the last antipode beam reaches the hub at 29.5 + 150 + 1.5) | All 595 beams continuous, hub at 100 %, the fronts' meeting flare at the antipode fades |
| Handoff | 189.0 s (`Crescendo` 8 s) | 594 regular beams fade over 2.5 s; the main beam continues |
| Sustain | 191.5 s | Refuel flashes from the normal grid pulse; heartbeat and breathing (§B.3) |
| Instability | E − S − 75 s (`CollapseLead`) | 45 s of growing flicker |
| Overload | E − S − 30 s | Flare to 2.5× over 4 s |
| Implosion | E − S − 26 s | 2.5 s: hub radius → 0, level → 0; all beams cut (retract top-down in 1 s) |
| Shockwave | E − S − 23.5 s | 8 s expanding light shell; ground "light shock" flash on arrival |
| Aftermath | E − S − 15.5 s | Darkness (§D); the PDA counts down to the player's local third contact |
| Return | player's local third contact (≥ E) | Physical egress, 44.6 s; darkness weight fades with coverage |
| Idle | local coverage reaches 0 | Hold released |

Worked example (eclipse 0, defaults, t_cel): S = 12,048.7; main fire 12,068.7; kindled 12,075.2; ignition launch 12,078.2; fronts meet 12,228.2; full 12,229.7; Sustain from 12,240.2; collapse from 16,063.7; Aftermath from 16,123.2; E = 16,138.7. **Sustain lasts 3,823 s (63.7 min).** Full ignition takes 151.5 s from launch ("about 2.5 minutes") and 161 s from main fire.

### B.2 Timeline settings (persisted; §G.1)

| Field | Default | Range | Notes |
|---|---|---|---|
| `EventEnabled` | true | bool | Off = the natural eclipse with the §D darkness only |
| `ParallaxSafetySeconds` | 10 | 0..120 | Shrinks the whole-ring window |
| `PreRollSeconds` | 300 | 0..900 | Hold and warnings |
| `HoldRealTimeDuringEvent` | true | bool | §A.4 |
| `DarknessBeatSeconds` | 20 | 0..600 | Before the main fire |
| `MainClimbSeconds` / `MainClimbExponent` | 4.0 / 2.0 | 0.5..20 / 1..4 | Tip fraction f = u^exp (accelerating climb) |
| `KindleSeconds` / `KindleLevel` | 2.5 / 0.10 | 0.1..10 / 0.02..0.5 | |
| `IgnitionDelaySeconds` | 3 | 0..30 | |
| `IgnitionSpeedMode` / `IgnitionSeconds` | MatchGrid / 150 | MatchGrid, Manual / 30..600 | MatchGrid uses the grid clock's speed (150 s at the defaults) |
| `RegularClimbSeconds` | 1.5 | 0.2..10 | |
| `RampMode` / `RampGamma` / `RampStepSmoothSeconds` | PerBeam / 1.0 / 0.5 | PerBeam, Perceptual / 0.5..3 / 0..3 | §B.3 |
| `IgnitionPlateauLevel` | 0.35 | 0..1 | Chain links stay lit behind an ignition front until Handoff |
| `SuppressGridPulseDuringIgnition` | true | bool | Q3 |
| `CrescendoSeconds` / `HandoffFadeSeconds` | 8 / 2.5 | 0..60 / 0.2..10 | |
| `FlashClimbSeconds` / `FlashHoldSeconds` / `FlashFadeSeconds` | 0.5 / 0.6 / 1.2 | 0.1..3 / 0..3 / 0.1..5 | Refuel flash |
| `HeartbeatKick` / `HeartbeatTauSeconds` / `RelaunchKick` | 0.02 / 0.8 / 0.05 | 0..0.2 / 0.1..5 / 0..0.3 | |
| `BreathAmplitude` / `BreathPeriodSeconds` | 0.015 / 11 | 0..0.1 / 2..60 | |
| `CollapseLeadSeconds` | 75 | 20..600 | Start of Instability before E |
| `InstabilitySeconds` / `FlickerMax` / `FlickerHzStart` / `FlickerHzEnd` | 45 / 0.35 / 0.5 / 6 | 5..300 / 0..0.9 / 0.1..5 / 1..20 | |
| `OverloadSeconds` / `OverloadGain` | 4 / 2.5 | 0.5..20 / 1..5 | |
| `ImplosionSeconds` / `BeamRetractSeconds` | 2.5 / 1.0 | 0.3..10 / 0.1..5 | |
| `ShockwaveSeconds` / `ShockFlashLevel` / `ShockFlashSeconds` | 8 / 0.6 / 0.4 | 1..30 / 0..2 / 0.05..3 | |

The setter clamps the sum so that the collapse ends before E. If the event window is too short for a configuration (for example a shortened totality), the stages compress proportionally and the status line warns.

### B.3 Brightness model (hub level L, pure; CPU only, then published)

- **Kindle:** `L = KindleLevel · smoothstep(0, KindleSeconds, τ − τ_kindle)`.
- **Ignition:** `L = L0 + (1 − L0) · R(N(τ) / N_total)`.
  - `N(τ) = Σ_s Output(s) · smoothstep(a_s, a_s + RampStepSmooth, τ)`, where `a_s` = fire time + climb is the hub arrival time.
  - `N_total = 594`: dead emitters make the sun dimmer, not slower (§B.5).
  - `R(x) = x` (PerBeam) or `x^RampGamma` (Perceptual).
  - The arrival times are sorted once at bind, so each frame needs one binary search.
  - **Choice: PerBeam.** The ignition then reads row by row: every 3 s a visible step of +1.8 % as 12 beams land. The constrained exposure (F18) leaves those steps visible.
- **Crescendo through Sustain:** `L = 1 + Breath + Heartbeat`.
  - `Breath = BreathAmplitude · sin(2π t_w / BreathPeriod)`.
  - `Heartbeat = HeartbeatKick · (1/6) Σ_c exp(−max(0, age_c − FlashClimb) / τ_h)`, where age_c is the time since column c's grid front passed its last site: `(head_c − q_last) / v`, from `GetFronts` plus a binary search on `P0`.
  - Plus `RelaunchKick · FlareEnvelope(t_w)` (the main relaunch flare of the grid clock).
- **Instability:** `L ← L · (1 − FlickerMax · u² · n(t))`, with u the phase fraction and n a deterministic value noise hashed from (seed, eclipse index, t) at a frequency rising from `FlickerHzStart` to `FlickerHzEnd`.
- **Overload:** `L = 1 + (Gain − 1) · sin(π u / 2)`, then held.
- **Implosion:** `L = Gain · (1 − u)³`, body radius `(1 − u²)`.
- **Shockwave:** `L = 0`. The ground flash is a lighting pulse `ShockFlashLevel · bump(t − t_arrive)` (§C.5).
- **Lighting outputs** (§C.5):
  - `HubDirect = 4.0 · HubDirectFraction(0.8) · L`;
  - `HubDay = min(1, HubDayAtFull(0.8) · L)`;
  - `HubWeight` = 1 from the start of Darkness to the end of Aftermath, else 0, smoothed over 1 s for scrubs.

### B.4 Ignition and refuel schedules (pure)

- `p_s = topology[LinkOut(s, col)].P0`, which is the site's loop coordinate (`EmitterGridTopology.cs`).
- `fire_s = τ_ign + min(p_s, C − p_s) / v_ign` and `a_s = fire_s + RegularClimb`.
- The fronts: forward `q_f = v_ign · (τ − τ_ign)` on q ∈ [0, C/2]; backward `p_b = C − v_ign · (τ − τ_ign)` on p ∈ [C/2, C]. Both are clamped at C/2 and absorbed there. The meeting flare lasts 1.5 s at the antipode links.
- Refuel flash of site s for grid launch n: `t_w = gridClock.ArrivalTime(gridClock.ToQ(p_s), col, n)` (`EmitterGridPulseClock.cs:84`). It counts only when it falls inside Sustain, converted with the current offset `t_cel − t_w`, which is constant while the hold is on.

### B.5 Dead-emitter seam (backburner)

`interface IEclipseSiteOutput { float Output(int site); ulong Revision { get; } }`, defaulting to 1 for every site. Output 0 means the site never fires or flashes, its share of N_total is missing (a dimmer sun), and it is skipped in the heartbeat. A dead main spire would cancel the event; that is not implemented. The future reactor state plugs in here.

---

## C. "Noon everywhere" lighting

### C.1 Principle

Two new global shader scalars plus one per-fragment helper. When both are 0 every shader produces exactly today's output.

| Global (`project.godot [shader_globals]`, next to `ringworld_solar_visibility` at `:70-75`) | Meaning |
|---|---|
| `ringworld_hub_day` (float, 0) | Per-point day contributed by the hub; the same value everywhere, because every point sees the hub at its zenith |
| `ringworld_hub_light_weight` (float, 0) | Share of the primary DirectionalLight's *direction* owned by the hub (1 for the whole event) |
| `ringworld_night_light_scale` (float, 1) | §D darkness scale for emission-like night terms |

`celestial_lighting.gdshaderinc` gains:
- `ringworld_effective_solar_day(g) = max(clamp(g) · clamp(vis), clamp(ringworld_hub_day))`.
- `float ringworld_effective_solar_visibility()`, which returns `max(vis, hub_day)`, for the two callers that pass the raw visibility.
- `vec3 ringworld_primary_light(vec3 light_view, vec3 surface_up_world, mat4 view, float primary_weight)`, which returns `normalize(mix(LIGHT, up_view, hub_weight · primary_weight))`.

The CPU side:
- `StarLight` is aimed at the **player's local zenith** during the event: the `surfaceUp` that `RingDayNightCycle.UpdateFrameAndLight` already computes (`:406-413`), applied through a new `IPrimaryLightOverride` that `ApplySunDirection` queries. `ApplySunDirection` stays the single writer of the light basis.
- Its energy is `max(4.0 × vis, HubDirect)` and its colour `lerp(sunColour, HubColour, HubWeight)`, both set in `day_side_atmosphere.gd:334, 343-350`.
- `current_day_factor = max(geo × vis, HubDay)` (`:301`). Do not route the hub through the `blend_speed` lerp at `:293-296`; the timeline is already smooth.

### C.2 Consumer inventory and the hub-mode switch

| Consumer | Today | Hub mode | Evidence |
|---|---|---|---|
| `StarLight` direction and shadows | `ToSun` basis, stabilised in 0.01° steps | Override = player zenith. Shadows stay on (local elevation 90°). Exact at the camera, ≤ 0.05° error across the 4 km shadow range | `RingDayNightCycle.cs:397-448, 503-544`; `LocalShadows.cs:146-153` |
| `StarLight` energy and colour | 4.0 × vis, white | 3.2·L, `HubLightColor` | `day_side_atmosphere.gd:22, 334, 346` |
| PlanetLight | Night × phase (backlit, about 0.9 % in totality) | Unchanged | `day_side_atmosphere.gd:856-888` |
| Terrain3D near disk | Include day, `LIGHT` | Include day; helper optional (≤ 128 m) | `terrain3d_ring_surface.gdshader:269, 327-356` |
| Proxy terrain | Include day, `LIGHT`, macro shadow | Include day; **helper**; macro shadow × (1 − w) | `ring_terrain_proxy.gdshader:105, 202-239` |
| Global terrain (main ≤ 850 km **and** far viewport) | Include day, `LIGHT`, macro and planet shadow | Include day; **helper (required: the far arch is lit from behind otherwise)**; macro shadow × (1 − w) | `ring_terrain_global.gdshader:105, 148-200` |
| Macro-shadow producer | Rebuilds on a 0.51° sun change | **Hold rebuilds while w > 0**, keep the last mask (it is zeroed in the shader) | `RingTerrainGlobalLod.cs:1794-1805` |
| Hull, voxel terrain, legacy far surface | Include day | Include day; helper in the hull `light()` | `ring_hull_global.gdshader:99`; `voxel_ring_terrain.gdshader:41`; `ring_surface_far.gdshader:88`; `ring_surface_daynight.gdshader:75` |
| Trees (procedural, imported, impostor) | Include day, `LIGHT` | Include day; `LIGHT` (near) | `ring_tree.gdshader:48`; `imported_tree.gdshader:75`; `imported_tree_impostor.gdshader:79` |
| Grass (standing and cards) | CPU geometric day from `StarLight`; include day | Automatic (`StarLight` at zenith gives day 1); include day | `RingStandingGrassLayer.Lighting.cs:36-41`; `ring_grass_card_common.gdshaderinc:65` |
| Ocean proxy / global | Include day, `LIGHT` glint | Include day; **helper in global** (far glint) | `ring_ocean_proxy.gdshader:328`; `ring_ocean_global.gdshader:66` |
| Sky shader | Real sun disk and halo, scattering toward `to_sun` | The real disk stays (eclipsed). New `hub_direction`, `hub_level`, `hub_color` uniforms: a zenith halo and the in-scatter phase toward the hub; per-sample day via the include | `ring_local_sky.gdshader:227-247, 268-290`; `day_side_atmosphere.gd:696-760` |
| Ring atmosphere (object haze) | Local air day from `to_sun` × vis | Include gives hub day; `to_sun` is `StarLight` (zenith), consistent | `ring_atmosphere.gdshaderinc:196-206, 383-386` |
| Sunshine clouds | `StarLight` direction and energy; profile vis | Lit from the zenith (hub-lit tops) automatically; profile[6].w = `max(vis, HubDay)`. Error ≤ 0.83° at the 72 km reach | `SunshineCloudsDriver.gd:230-235`; `RingSunshineCloudBridge.cs:1008, 1023` |
| Cloud ambient | `SetLocalDayFactor(current_day_factor)` | Follows the new day factor | `day_side_atmosphere.gd:307`; `RingSunshineCloudBridge.cs:654-662, 942-950` |
| Cloud shadow map and receivers | `StarLight` direction | Straight down (zenith); one sliced refresh during Darkness (T5) | `RingCloudShadowProjector.cs:322` |
| Far clouds (layer, clipmap, canopy, volumetric, mesh) | Include day | Include day; directional terms keep `sun_direction` (minor) | `ring_cloud_layer.gdshader:85`; `ring_far_cloud_clipmap.gdshader:125`; `ring_cloud_canopy.gdshader:56`; `ring_volumetric_cloud.gdshader:64`; `ring_local_volumetric_cloud.gdshader:128`; `ring_mesh_cloud.gdshader:111` |
| Far cloud mask and haze | Include day; raw vis in haze | Include; haze uses `ringworld_effective_solar_visibility()` | `ring_cloud_transmittance_mask.gdshader:160, 214` |
| Rain curtains | Include × sun energy | Unchanged (energy follows the hub) | `ring_distant_rain_curtain.gdshader:46` |
| Emitter stand-ins, scale anchors | Include day, Godot lighting from `StarLight` | Include day; ≤ 8° error at 700 km on 1-2 px objects, accepted | `ring_emitter_standin.gdshader:52`; `ring_scale_anchor.gdshader:29` |
| Grid beams and heads (B) | `eg_day_at(…, vis)` | `max(eg_day_at, ringworld_hub_day)` plus the CPU twin `EmitterGridIntensity.DayAt`; event peak override §E.3 | `ring_emitter_grid_beam_common.gdshaderinc:53-54, 132`; `…head_common.gdshaderinc:50-51`; `EmitterGridPulseClock.cs` |
| Environment ambient, fog, sky palette, stars | Day factor | Day factor = max(…, HubDay); stars fade as the hub rises | `day_side_atmosphere.gd:311, 360-387, 707-711, 842-853` |
| Far viewport environment ambient | Frozen at creation (F12) | Scaled by darkness (§D); the hub day reaches far shaders through the include | `RingScaledFarViewport.cs:937-941` |
| POI detail meshes (`StandardMaterial3D`) | `StarLight` | Zenith light, exact within 16 km (≤ 0.18°) | POI spec B.6 |
| Weather ambience | Geometric daylight | Unchanged (it should stay "night-ish" for audio; minor) | `RingLocalWeatherEffects.RainFog.cs:101` |

**Floating origin and rebase.** Every shader already derives the local up from `world_position − ring_center`, with the ring axis as world Y. `ring_center` comes from `GetCanonicalRingCenterWorld` (`VoxelChunkGridManager.cs:1576-1588`) and is rewritten after every anchor change. The helper uses that same up, so it adds no world-space state and is rebase-safe by construction. The far viewport is absolute scaled space and never rebases. The `StarLight` override uses the explorer's canonical surface up, which is independent of the anchor.

### C.3 Error where a global direction remains

The angle between the camera zenith and a point's zenith is θ = d / R.

| Distance d | θ | Flat n·l | 45° slope n·l (correct 0.707) | Who is affected |
|---|---|---|---|---|
| 4 km | 0.05° | 1.0000 | ±0.0006 | DirectionalLight shadow range |
| 16 km | 0.18° | 1.0000 | ±0.002 | POI detail |
| 72 km | 0.83° | 0.9999 | ±0.010 | Sunshine reach |
| 100 km | 1.15° | 0.9998 | ±0.014 | Trees, proxy terrain |
| 700 km | 8.0° | 0.990 | 0.60 / 0.80 | Stand-ins (accepted) |
| 850 km | 9.7° | 0.986 | 0.58 / 0.82 | **Global LOD: fixed by the helper** |
| 10,000 km | 180° | −1 | | **Far arch: fixed by the helper** |

Ignoring the 1.52° hub tilt costs at most 0.035 % of n·l and a ≤ 1.52° shadow lean toward the centre line, which is invisible.

### C.4 Transition: real sun → hub sun → real sun

Because of the anchor (§A.3), the player's local solar visibility is exactly 0 from S to E:
- **At S** `HubWeight` steps to 1 while every source is at zero energy. `StarLight` swings to the zenith in darkness, and the 1 s smoothing only matters for scrubs. The Sunshine shadow map and the near cloud shadow refresh (sliced over frames) during Darkness (T5).
- **Kindle and Ignition** raise `HubDirect` and `HubDay` (§B.3).
- **Collapse:** the level returns to 0 at the end of Implosion.
- **The end of Aftermath** returns the weight to 0, again at zero energy.
- **Return** is the unmodified physical egress.

The only lit transition is the ground flash on shockwave arrival, which is an energy pulse along the zenith direction.

**The local-shadow handoff needs a zero-energy gate.** Today the sun shadow pass runs through totality at zero energy (F17). Add the reason "sun energy below 0.001": no shadow pass, re-enabled one frame before energy rises. The event knows that moment in advance, so the shadow map is warm when needed. T4 measures the saving.

### C.5 The 80 % mapping and colour defaults

| Quantity | Real noon | Hub full (L = 1) | Setting |
|---|---|---|---|
| `StarLight` energy | 4.0 | **3.2** | `HubDirectFraction` 0.80 (0..1.5) |
| Day factor (ambient, sky, haze, cloud ambient, night tint) | 1.0 | **0.8** | `HubDayAtFull` 0.80 (0..1) |
| Env ambient energy (0.30 → 0.35 by day) | 0.35 | 0.34 | follows the day factor |
| Sunshine light power (colour × energy × multiplier) | 100 % | 80 % | follows energy |
| Sky palette | day | 80 % day + `HubSkyTint` | `HubSkyTint` (0.85, 0.95, 1.0), strength 0.25 |
| Light colour | white (scene default) | **(0.92, 0.97, 1.00)** cool white | `HubLightColor` |
| Hub body core / halo colour | — | white / beam teal (0.70, 0.92, 1.00) | `HubBodyColor`, `HubHaloColor` |
| Ground flash at shockwave | — | +0.6 direct, +0.3 day for 0.4 s | `ShockFlashLevel` |

During the kindle (L = 0.1) the energy is 0.32 and the day factor 0.08: a bright point at the zenith over a night landscape. During the ramp the sky turns blue from the zenith down as the day factor rises.

---

## D. Pitch-black eclipse

### D.1 Model

- Darkness weight `w_d = localCoverage × (1 − min(1, L / KindleLevel × 0.5))`, smoothed over 0.5 s. It is 1 in totality before the kindle, falls to 0.5 at the kindle, and the hub day takes over from there. It returns to 1 in Aftermath and fades with coverage during Return.
- `day_side_atmosphere.gd` applies the scales below. `ringworld_night_light_scale = 1 − w_d · (1 − EclipseAtmosphereNightScale)` reaches every shader that uses the atmosphere night colour (`ring_atmosphere.gdshaderinc`). Normal nights keep w_d = 0, so they are unchanged.

| Term | Night / totality today | Totality, proposed (w_d = 1) | Setting (default, range) |
|---|---|---|---|
| Env ambient energy | 0.30 | **0.045** | `EclipseAmbientScale` 0.15 (0..1) |
| Atmosphere night colour (haze emission, far arch glow) | (0.026, 0.035, 0.055) | × 0.35 | `EclipseAtmosphereNightScale` 0.35 |
| Sky night palette | 0.002-0.035 | × 0.5 | `EclipseSkyNightScale` 0.5 |
| Cloud night ambient (bridge, runtime multiplier only; the persisted colour is never rewritten) | (0.18, 0.23, 0.35) | × **0.4** = (0.072, 0.092, 0.14) | `EclipseCloudAmbientScale` 0.4 (**floor 0.3**, see D.2) |
| Far viewport env ambient | Frozen snapshot | snapshot × `EclipseAmbientScale` (written only while w_d > 0, and restored afterwards) | — |
| Stars | 0.62 surface visibility | 1.0 | `EclipseStarVisibility` 1.0 (0..1) |
| Auto-exposure | 50-160 | unchanged (already constrained); optional `EclipseExposureMaxSensitivity` 120 | (50..160) |
| Fog light energy | night | × `EclipseAmbientScale` | — |
| Grid beams | B = 0 → night peak | unchanged: pulses, flare and 0.03 residual read clearly | grid settings |
| Direct sun | 0 | 0 | — |

The result: a black landscape lit only by starlight. The giant shows as a black disc 61.6° across that hides the stars. The far arch is a faint band, and the grid fronts and the main relaunch flare are the brightest things in the sky.

### D.2 Constraint from today's cloud fix

Clouds must not go below about 30 % of the night ambient. Sunshine's night floor `max(ambient, ambientLightColor × 0.35)` (`SunshineCloudsCompute.glsl:1357`) scales a black ambient to black clouds, and partial α then lets the arch, terrain and beams show through (cloud diagnostics §3). The default 0.4 keeps clouds as dark-grey silhouettes against a starfield. Check it in capture shot 3 (§G.6). `NightCloudAmbientColor` stays the code default and is persisted unchanged.

### D.3 `GameplayEclipseDarknessCap`

Keep the field and its value 0.95. `CelestialCoreSmokeRunner.cs:293-294` and the partial scan pin it, and it only tints the partial phases (F11). The "pitch black" behaviour comes from §D.1, not from the cap. Document it in `CelestialConfiguration.cs:17` as a partial-phase tint control.

---

## E. Visuals

### E.1 Hub sun body (far viewport)

- **Where:** the far viewport, through `AddScaledWorldChild`, at the scaled origin (F14). The main camera cannot reach 5,000 km (far plane 1,200 km).
- **Why it must be geometry, not the sky:** from the floor the hub sits in front of the opposite-arch band. From across x it is `(W/2 − x)/R` off the zenith, ≤ 1.82° at the walls; the arch at the zenith spans −0.15° … 1.66° at column 0. The sky shader draws behind far terrain, so it cannot draw the hub. Near Sunshine clouds composite over the far texture, so they correctly hide the hub.
- **Mesh:** one camera-facing quad. Shader `ring_eclipse_hub_body.gdshader`: unshaded, `blend_add`, depth test on, `depth_draw_never`, `RenderPriority` 20 (above far clouds 10-14 and grid far 5). Size comes from the angular radius, with a minimum of 4 px.
- **Profile (in-shader glow; there is no bloom):** an HDR core disk (`HubRadiusKm` 30 → 0.34°, slightly larger than the 0.265° star) with mild limb brightening, plus an exponential halo `HaloRadiusKm` 400 and amplitude 0.15. Core emission is `HubCoreEmission` 40 × L, saturating to white under AgX.
- **During the event:** the radius scales with `0.5 + 0.5·sqrt(min(L, 1))` (kindle → full). The flicker modulates the halo. The implosion shrinks the radius to 0.
- **Sky shader halo** (`hub_direction`, `hub_level`): a wide atmospheric glow around the zenith (forward scatter, `pow(dot(view, hub), 24)`). It is in front of the vacuum but behind the far arch. The hub body's own halo covers the arch band.
- **T3:** the far viewport's output curve. The far environment copies AgX (`RingScaledFarViewport.cs:947-956`), so tune the core emission in that output space.

### E.2 Spire → hub beams

- **Layers** (mirroring the grid spec G4.1):
  - `EclipseHubBeamLayer` (Node3D "EclipseBeams", scene root next to `EmitterGrid`, main camera): one MultiMesh of 595 ribbons plus the depth-core `next_pass`;
  - `EclipseHubFarLayer` (far viewport): the same 595 ribbons in scaled space from `ScaledPoint(crown)` to the origin, built once, plus the hub body and the shockwave.
- **Handoff:** per pixel, through `ring_emitter_grid_layers.gdshaderinc` (near ≤ `NearLayerMaxKm` 700, band following the cloud handoff of about 120-172 km).
- **Geometry:** instance origin = crown (A, P + clearance + 1,743 or 3,989, X) from `EmitterSiteFrameContext.WorldPosition`; basis X = hub − crown. The hub point comes from `RelativeLocal` with radial 0 and across W/2 (as in `Beam`, `EmitterSiteFrameMath.cs:147-159`). Add `EmitterGridTopology`-style `FillHubBeamEndpoints(snapshot, a, b)`. Rebuild absolutely on `CanonicalFloatingOriginAnchorChanged`, exactly like `EmitterGridBeamLayer.Rebuild` (`:274-293`); the budget is ≤ 0.3 ms for 1,190 evaluations. Tilt ≤ 1.519°; length about 4,998 km.
- **Shader reuse:** extract the generic ribbon helpers from `ring_emitter_grid_beam_common.gdshaderinc` into `ring_beam_ribbon.gdshaderinc`: view-space expansion, minimum pixel width, energy-conserving coverage with `abs(PROJECTION_MATRIX[1][1])`, sub-segment near clip, collapse, the Gaussian core plus exponential halo, and the core depth footprint. Then build three thin entries: `ring_eclipse_hub_beam.gdshader` (halo), `…_core.gdshader` (`EB_CORE_PASS`), `…_far.gdshader` (`EB_FAR_LAYER`). **Gate:** the grid shader parity test (`tools/emitter_grid_shader_parity_test.gd`) must still pass after the extraction.
- **State is fully GPU-side; there are no per-frame CPU writes per beam:**
  - `INSTANCE_CUSTOM = (p_site, column, fire_τ, kind/flags)`, written once per eclipse at bind (fire times are known in advance).
  - Uniforms: `ev_time` (τ), `ev_marks` (`vec4`s: handoff start, handoff fade, cut time, retract), `ev_level`, and the grid fronts `eg_fronts` (for refuel).
  - The tip fraction is `clamp((τ − fire_τ)/climb)^exp`. Fragments above the tip are discarded with a 0.5 % soft tip.
  - A refuel flash comes from the latest grid front on the column: `age = (head − q_site)/v`.
  - The collapse retracts top-down: `f_top = 1 − (τ − cut)/retract`.
- **Widths:** core = bore diameter (150 m regular, 300 m main), halo 1,200 m (2,400 m main); min pixel width 1.5. `FarCompensation` 0.45 is lower than the grid's 0.6 so the far-arch starburst reads: a beam at 10,000 km is about 0.01 px true width, giving 6 % intensity at 0.45. `BaseEmission` 12 (main ×1.5). The colour is beam teal (0.70, 0.92, 1.0) with a white core.
- **What it looks like.** From anywhere you see the near spires' pillars (main camera), plus a starburst of 594 hair-lines converging on the zenith from the whole arch (far layer). The main spire's pillar is the thick one. During Sustain only the main pillar remains, with rows of six bolts flashing up every 3 s.
- **Visibility against the hub noon:** these beams ignore B. The day factor does not dim them, because they are the event. A `DayVisibility` 0.6 factor applies only while HubDay > 0.5 so they do not flatten the noon sky.

### E.3 Ignition pulse visuals (grid shader, bidirectional)

The two spare front lanes (F15) are used, with no new layer:
- `eg_fronts[i] = vec4(headQ, column, signedTravelled, plateau)`. When z = 0 the legacy path is exact (normal fronts).
- When z ≠ 0: `b = sign(z) · (head − q)`, and the profile is `max(eg_profile(b), plateau · step(0, b) · step(b, |z|))`. The plateau keeps the travelled span lit (the "energised" chain).
- The collapse rule `eg_interval_lit` adds the window `[head, head ± |z|]`.
- The CPU twin `EmitterGridPulseClock.PulseAtQ` gets the same branch, with a parity case.

`EmitterGridBeamLayer` gains `SetEventFronts(ReadOnlySpan<Vector4>)`, appended after the normal fronts (6 + 12 = 18 ≤ 24), and an `EventPeak` (default 1.5 × peak, B forced to night while fronts are live). `MaxHeadSprites` 12 → 24 (the far layer shares the constant) so the 12 heads climb the arch both ways. `SuppressGridPulseDuringIgnition` sets the normal fronts' weight to 0 from MainFire to the end of Handoff. Meeting flare at the antipode links: plateau → 1.0 for 1.5 s on the 12 links around p = C/2. At Handoff the plateau fades to 0 over `HandoffFadeSeconds`.

### E.4 Refuel flashes and the hub heartbeat

These are pure GPU (§E.2) plus the CPU heartbeat (§B.3). With six chains in sync (stagger 0), the six sites of a row flash together. The flashes visibly march around the arch at 0.6°/s (grid spec G3), and the hub pulses each time a row lands. The main crown flare at relaunch (every 5 min) adds `RelaunchKick`.

### E.5 Collapse VFX

| Stage | Hub body | Beams | Lighting | Clouds and history |
|---|---|---|---|---|
| Instability (45 s) | Halo shimmer and limb wobble; level flicker (§B.3) | Main beam width jitter ±20 %, deterministic | Flicker on energy and day | `SetLightningFlashIntensity(flickerDelta)` history suppression while \|dL/dt\| is large (T6) |
| Overload (4 s) | Swell to 1.6× radius, white-out core | All beam halos ×2 | Energy to 2.5 × 3.2 = 8.0 (AgX handles it) | Suppression on |
| Implosion (2.5 s) | Radius → 0 (ease-in), then a point flash | Cut at start: retract top-down 1 s; crowns flash (stand-in `.b` channel) | Level → 0 | |
| Shockwave (8 s) | — | — | Ground flash on arrival (the shell reaches the floor centre line at about 75 % of the duration): +0.6 direct, +0.3 day, 0.4 s | Suppression for 0.5 s |
| Aftermath | — | — | Darkness weight returns | |

**Shockwave shell:** an icosphere (about 2k triangles) in the far layer centred at the origin. Radius `r = R · smoothstep_out(u)`. Shader: fresnel rim, `blend_add`, `cull_disabled`, priority 21. Its intensity fades as r approaches the camera distance; the camera is inside after arrival. From the ground this reads as a bright circle growing outward from the zenith until it fills the sky, then a flash. Because the hub is equidistant from every floor point, it arrives everywhere at once (the walls 0.05 % later). A camera shake of 0.15 amplitude is a setting, default on (Q5).

### E.6 Clouds

- **Hub-lit cloud tops** come for free through `StarLight` (F13). Undersides are dark from the ground, as at real noon. Far clouds are evenly lit by the include day.
- **Beam and cloud occlusion (today's T2 lessons):** the near hub beams use the depth core (4 px footprint) so clouds behind a beam do not dim it. **Known blocker:** Sunshine ignores a depth core until `refresh_compute` runs *while it is lit* (grid spec G4 and T2 notes). Workaround: one `RingSunshineCloudBridge.RefreshCloudCompute()` 0.5 s after the main beam lights. Measure the hitch (T2); if it exceeds 8 ms, skip the refresh and accept the dimming until Sunshine is fixed. The proposed two-depth compositing would remove the workaround.
- **In-cloud diffusion:** a beam crosses the deck only near its spire (vertical). When a near-layer site (≤ 700 km) fires, query `QueryLocalWeatherAtTime` once at the site and pass `k` (in-cloud factor) per instance: the halo widens by ×(1 + 3k) over the deck span. That is ≤ 30 queries per eclipse.
- **Optional far-cloud glow** around the main pillar base: reuse the G5 Tier 2 `far_pulse_glow` slot; it stays off until G5 lands.

### E.7 Stand-ins and POI

- **Stand-in crown glow:** `INSTANCE_CUSTOM.b` = beam activity (free lane; `EmitterStandInLayer.cs:286`, `ring_emitter_standin.gdshader:33-34`). It is written on fire, hand-off and cut: 595 writes per eclipse, not per frame. Optional, phase E4.
- **Near POI** (active slot): boost the existing bore plug `OmniLight3D` while its site fires. This is one light, so it is cheap.

---

## F. Audio and announcer hooks

### F.1 Event stream (`EclipseEventStream`, pure)

Each frame: `Advance(t_cel_prev, t_cel, t_w_prev, t_w, observer)` returns events whose time lies in (prev, now]. A jump of more than 5 s or a rewind on either clock emits a single `Resync(phase, facts)` and nothing in between. Each event is a `readonly record struct EclipseEvent(EclipseEventKind Kind, double CelestialTime, int Site, float DistanceKm, float BearingDegrees, EclipseEventPhase Phase, ...)`.

| Kind | When | Facts |
|---|---|---|
| `PhaseChanged` | Every phase boundary | from, to, seconds to the next phase |
| `TotalityWarning` | Local second contact − 300 / − 60 s | minutes |
| `LocalTotality` / `LocalThirdContact` | The player's own contacts | |
| `MainBeamFired` / `HubKindled` / `IgnitionLaunched` | §B.1 | distance and bearing to main |
| `SiteFired` | Site fire, only for the 6 nearest sites and those ≤ `AudibleRadiusKm` | site, distance, bearing, column |
| `NearestSpireFired` | Once per eclipse, the nearest regular site | as above |
| `FrontsMet` / `FullIgnition` / `HandoffComplete` | | level % |
| `RefuelFlash` | Nearest site only, rate-limited to 1 per 60 s | |
| `InstabilityBegan` / `Overload` / `Implosion` / `ShockwaveArrival` / `BeamsCut` | | seconds to local third contact |
| `Resync` | Load, scrub or jump | phase, level |

Consumers subscribe to `EclipseEventDirector.EventRaised`. `IEmitterBeamSource.SetBeamActive` is mirrored on SiteFired, Handoff and Cut, so other systems see the booleans. Flashes are not mirrored.

### F.2 Sound

- **Spire booms:** for `SiteFired` and `BeamsCut` within `AudibleRadiusKm` (default 40), schedule an acoustic arrival at 343 m/s with the `ThunderPropagation` pattern (F19). At 26.5 km (the nearest column) the boom arrives 77 s after the visible fire, which is a strong "distance" cue. `LateSeconds` lets a resumed or loaded session seek instead of replaying.
- **Structure-borne rumble** (stylised, immediate-ish): sites within 400 km at `GroundSpeedKmPerSecond` 4.0 (≤ 100 s), volume 1/d.
- **Hub:** no acoustic path through the vacuum. The suit plays a sensor tone that follows L (pitch ∝ L) during the ramp, and a sub-bass swell on Overload. The shockwave arrival is a low "thump" plus the camera shake.
- New `scripts/Audio/EclipseEventAudio.cs`, on its own bus. Settings: `EventAudioEnabled`, `EventAudioVolumeDb`, `AudibleRadiusKm`, `GroundSpeedKmPerSecond`, `HubToneEnabled`.

### F.3 PDA line table (authored, `resources/pda/eclipse_lines.json`, same schema as `prototypes/pda-voice/events.json`)

| id | Trigger | Priority | Critical (skip LLM) | Authored fallback | Facts |
|---|---|---|---|---|---|
| `eclipse_warning_5min` | TotalityWarning 300 | 1 | no | "Eclipse in 5 minutes. External light will fall to near zero." | minutes |
| `eclipse_warning_1min` | TotalityWarning 60 | 0 | yes | "Eclipse in 1 minute. Ambient light is about to fail." | minutes |
| `eclipse_totality` | LocalTotality | 0 | yes | "Totality. External light at 0 percent." | pct |
| `main_spire_fired` | MainBeamFired | 1 | no | "Energy discharge at the primary spire, {distance_km} kilometres, bearing {bearing}." | distance_km, bearing |
| `hub_kindled` | HubKindled | 1 | no | "Light source forming at the ring axis. Output {pct} percent of stellar." | pct |
| `relay_ignition` | IgnitionLaunched | 2 | no | "Relay network activating in both directions." | |
| `nearest_spire_fired` | NearestSpireFired | 1 | no | "Emitter {site} has fired, {distance_km} kilometres away." | site, distance_km |
| `fronts_met` | FrontsMet | 3 | no | "Relay fronts have converged at the antipode." | |
| `full_ignition` | FullIgnition | 1 | no | "Axial light source stable at {pct} percent of stellar output." | pct |
| `handoff` | HandoffComplete | 3 | no | "Regular emitters standing down. Primary spire is sustaining the source." | |
| `refuel_note` | First RefuelFlash | 4 | no | "Relay pulses are maintaining the axial source." | |
| `instability` | InstabilityBegan | 1 | no | "Axial source output fluctuating." | |
| `overload` | Overload | 0 | yes | "Warning: axial source overload." | |
| `collapse` | Implosion | 0 | yes | "Axial source collapsing. Brace for a light surge." | |
| `aftermath_countdown` | Aftermath start, and every 60 s while > 60 s remain | 2 | no | "Stellar light returns in {seconds} seconds." | seconds |
| `sun_return` | LocalThirdContact | 1 | no | "Stellar light restored." | |
| `event_resync` | Resync | 3 | no | "Eclipse event in progress: {phase}." | phase |

Queue rules (prototype README pipeline):
- Order by priority; drop events older than `MaxStaleSeconds` (8).
- Cooldown 4 s between lines, 60 s per id (except countdowns).
- Critical lines pre-empt the queue.
- Subtitles always show.
- On Resync, only `event_resync` is spoken, and no backlog is replayed.

### F.4 LLM-ready seam

```
interface IPdaLineProvider { bool TryCompose(in PdaRequest request, CancellationToken token, out string line); }
readonly record struct PdaRequest(string Id, int Priority, bool Critical, string AuthoredLine,
                                  IReadOnlyDictionary<string, string> Facts, IReadOnlyList<string> RequiredTerms);
interface IPdaVoiceOutput { void Speak(string line, int priority); void Subtitle(string line); }
```

- Implementations:
  - `AuthoredPdaLineProvider`, the default: fills the fallback template;
  - later an `LlmPdaLineProvider` with the prototype's validator (numbers and terms kept, single line, ≤ 2 s timeout, falling back to authored);
  - `SubtitleOnlyVoiceOutput` now, Kokoro or Piper later (licence notes in the prototype README).
- `PdaAnnouncer` (new `scripts/Pda/`) owns the queue and is not eclipse-specific.
- Settings: `PdaEnabled`, `PdaVolumeDb`, `PdaLlmRewrite` (false), `PdaSubtitles` (true), `PdaRadioFilter` (0.7).

---

## G. Tooling, tests and budgets

### G.1 Persistence

- `GameplayEclipseEventSettings` plus `GameplayEclipseEventDefaults` (`user://gameplay_eclipse_event_defaults.tres`), following `GameplayEmitterGridSettings` and `Defaults` (`scripts/ui/GameplayEmitterGridSettings.cs`, `GameplayEmitterGridDefaults.cs:11`): `Initialize`, `Set*` returning changed, atomic `Save`, `Reset`, `Clamp*`, `Sanitize`, a path override, and enums stored as ints.
- Fields: §B.2, §C.5, §D.1, §E (the `Hub*` and `Beam*` widths, emissions, `FarCompensation`, `DepthWriteCore`, `ShakeEnabled`), §F.2, §F.4.
- World-shape values (eclipse geometry) are not here; they belong to the celestial configuration.

### G.2 Grave-key drawer

New partial `scripts/ui/RingLiveTuningDrawer.Eclipse.cs`, built in the **Lighting** tab after the existing sections (`RingLiveTuningDrawer.cs:345, 377-465`). Debounced save and retry, and a 0.25 s live status label, as in `RingLiveTuningDrawer.Underground.cs:24-58`. Sections:
1. "Eclipse darkness" (all worlds).
2. "Eclipse false sun: timeline".
3. "…: lighting".
4. "…: beams and hub".
5. "…: audio and PDA".
6. "…: debug".

The false-sun sections are disabled on worlds without emitter sites (v1-v4, gen-16), where the event is darkness only.

**Status line:** eclipse index k; S / E and the local contacts as countdowns; phase and seconds in phase; hub level L, energy and day; beams lit; fronts; next refuel ETA at the nearest site; both clock values; effective TimeScale (hold on or off); solver state (cached / pending / none).

### G.3 Debug actions (not saved)

- **Force eclipse now:** a real clock jump to `S − PreRoll`, through `SetSimulationSeconds`, the same mechanism as `CelestialDebugHud.Jump` (`:103`). The tooltip warns that it changes world time.
- **Jump to phase:** a dropdown that jumps to the phase start (a real jump; emits Resync).
- **Timeline scrub:** a slider over [S − PreRoll, E + 120] (a real jump).
- **Freeze event:** pauses the celestial clock (existing `Paused`).
- **Hub level override:** visual only, 0..2.5. It forces `HubWeight` 1 and the level, and is meant for lighting captures outside an eclipse (it will show the hub alongside the real sun, and the status warns about it).
- Also: "Show beam ids", "Log events", "Show local zenith gizmo".

### G.4 Debug HUD

Extend `CelestialDebugHud` (F9) with an "Eclipse event" block: the status line, a ring map strip showing which sites have fired, and buttons for force, next and previous phase.

### G.5 Headless tests (`tools/X.cs` plus a `tools/x.gd` runner)

| Test | Asserts |
|---|---|
| `eclipse_window_solver_test` (pure) | Centred totality 4,500 ± 0.01 s at T(k+½), for k = 0..3. The whole-ring window lies inside the local window of every one of 360 longitudes × 3 across × 2 altitudes. Length 4,110 ± 5 s; spread 391 ± 5 s. Giant disabled or the partial fixture (30.72°) gives no window. The result is the same on a worker and on the main thread. Golden JSON for (S, E) at k = 0..2. |
| `eclipse_event_timeline_test` (pure, seed-1337 v5 registry fixture as in `emitter_grid_topology_test`) | Phase-boundary goldens. Site fire times equal `τ_ign + min(p, C − p)/v` (< 1 ms). The fired count is monotone: 12 per 3 s, 594 at 150 s ± 0.05. L = 0.10 after the kindle, monotone through Ignition, 1.0 at Crescendo. Refuel times equal `ArrivalTime` exactly. The collapse ends before E. A dead-site seam (Output 0 on 10 sites) gives L_full = 1 − 0.9·10/594. |
| Determinism | Direct evaluation equals 1/60 s stepping over 1.5 h (pure, no drift). Restoring any (t_cel, t_w) reproduces every output bit-exactly. The hold clamp lands exactly on S − PreRoll from TimeScale 100. With the hold off at TimeScale 10, every beat compresses by 10. |
| `eclipse_event_stream_test` | Each one-shot fires exactly once per eclipse at 30, 60 and 144 fps. Loading mid-Ignition gives exactly one Resync and no SiteFired backlog. Scrubbing backwards gives one Resync. Rate limits are respected. |
| Grid fronts extension | z = 0 is bit-identical to the current `PulseAtQ` and `CountLitLinks`. Bidirectional and plateau CPU and shader parity cases (extend `emitter_grid_shader_parity_test`). |
| `eclipse_event_settings_smoke` | Defaults, round-trip, clamps, reset, invalid path. |
| Save and load mid-event (scene-level, `RingWorldSaveManager`) | Save at Ignition + 60 s, reload: phase, L, fired count and grid fronts are equal; no duplicate PDA lines. |
| Regressions | `CelestialCoreSmokeRunner` (all), `celestial_runtime_integration_smoke_test`, `sun_shadow_ownership_smoke_test`, `emitter_grid_*`, `emitter_far_standin_test`, `world_loading_startup_contract`, underground round-trip (new participants). |

### G.6 Windowed capture route (`tools/eclipse_capture_route.gd` plus `run_eclipse_capture_route.ps1`, modelled on `tools/emitter_capture_route.gd`; v5 world, isolated preferences, scheduled by the timeline; run only when no other Godot process is running)

Shots:
1. Pre-eclipse day (reference luminance).
2. Totality at ground and at 9 km.
3. Totality under clouds (the D.2 show-through check).
4. Main fire from the main spire's Far pose.
5. Kindle overhead.
6. Ignition: fronts climbing the arch both ways; the nearest spire firing from 5 km.
7. Full noon at ground, looking at the far arch, and from 150 km altitude (evenness).
8. Sustain row flash.
9. Cloudy noon from 9 km (hub-lit tops).
10. Collapse sequence at 30 fps (flicker, flare, implosion, shockwave).
11. Aftermath.
12. Sun return.
13. Zero-uniform parity: hub uniforms 0 against the pre-change build at the M1 poses (T1).

**Report metrics:**
- Mean scene luminance per shot. Targets: totality ≤ 3 % of day; hub full 70-90 % of real noon; far-arch to near-ground luminance ratio within 0.6-1.4 at full.
- Frame-time p95 per phase, lit-beam count, shader errors 0.

**Human checklist:** beam starburst legibility, hub halo, flicker comfort, cloud show-through, history smear.

### G.7 Budgets (RTX A1000, 1600×900)

| Item | Budget | Notes |
|---|---|---|
| Hub-mode lighting | ≤ 0.05 ms GPU, 0 extra passes | Reuses `StarLight` and its shadows; a few ALU in `light()` |
| Near hub beams (halo + core) | ≤ 0.15 ms GPU, main +2 draw calls | ≤ 30 lit near beams; unlit sub-segments collapse |
| Far layer (595 beams + hub body + shockwave) | ≤ 0.10 ms GPU, far +3 draw calls (shockwave only during collapse) | About 38k vertices |
| Sky halo | ≤ 0.02 ms | |
| Event CPU per frame | ≤ 0.08 ms | Timeline eval, heartbeat (6 binary searches), ≤ 12 uniform sets; no per-beam writes |
| Bind per eclipse | ≤ 1 ms main thread | Custom data for 595 instances; the solver runs on a worker |
| Rebase rebuild | ≤ 0.3 ms | 1,190 `WorldPosition` evaluations |
| Shadow gate saving in Darkness and Aftermath | report (T4) | Expected positive |
| Frame spikes | none over 8 ms attributable to the event | The T2 refresh is measured separately |

---

## H. Phases (implementer-sized: Sonnet, medium)

| Step | Deliverable | Depends on | Parallel with | Tests |
|---|---|---|---|---|
| **E0** | Pure core: `EclipseWindowSolver`, `EclipseEventTimeline` (+ settings record, brightness, ignition and refuel schedules), `EclipseEventStream`, `IEclipseSiteOutput`, goldens | none | E6a | §G.5 solver, timeline, determinism, stream |
| E1 | `EclipseEventDirector` node (scene root "EclipseEvent", priority −99), settings resource and defaults plus smoke, drawer partial skeleton (timeline and debug sections), HUD block, time-scale hold in `RingDayNightCycle`, force, jump and scrub | E0 | E6a | settings smoke; hold test; save/load mid-event (state only) |
| E2 | Darkness: the three shader globals in `project.godot` (all added here), `ringworld_night_light_scale` in `ring_atmosphere.gdshaderinc`, `day_side_atmosphere.gd` darkness scales, bridge cloud ambient multiplier, far-environment ambient scale, stars, zero-energy shadow gate | E1 | E6a | headless property checks at totality; capture shots 2-3 |
| E3 | Hub lighting: `celestial_lighting.gdshaderinc` functions, the `ringworld_primary_light` helper in the global, proxy and hull terrain and ocean-global `light()` plus macro shadow × (1 − w), macro-rebuild hold, `IPrimaryLightOverride` in `RingDayNightCycle`, `day_side_atmosphere.gd` hub energy, colour and day, sky shader hub uniforms, bridge profile visibility, grid `eg_day_at` hub term plus CPU twin, drawer lighting section, hub level override | E2 | E6b | zero-uniform parity (T1); noon-everywhere captures with the override; grid tests green |
| E4 | Visuals: `ring_beam_ribbon.gdshaderinc` extraction (grid parity gate), hub beam near and far layers, hub body, grid event fronts (z/w lanes, `SetEventFronts`, `MaxHeadSprites` 24), refuel flashes, in-cloud diffusion, T2 refresh workaround, underground participants, optional stand-in crown glow | E3 | E6b | grid parity; rebase test for beams; capture shots 4-9 |
| E5 | Collapse: flicker, overload, implosion, shockwave shell, beam retract, ground flash, camera shake, history suppression | E4 | E6b | capture shot 10; T6 |
| E6a | `PdaAnnouncer`, `IPdaLineProvider` (authored), `IPdaVoiceOutput` (subtitles), line table JSON | E0 (event kinds) | E1-E3 | queue unit test (priority, cooldown, stale, resync) |
| E6b | `EclipseEventAudio`: boom scheduler (ThunderPropagation pattern), rumble, hub tone; drawer audio section | E1 | E3-E5 | arrival-time unit test; LateSeconds seek |
| E7 | Capture route plus perf report; optional per-point umbra sweep | all | none | §G.6 gates, §G.7 budgets |

**Shared files, serialised:**
- `day_side_atmosphere.gd`: E2 → E3 → E5.
- `RingSunshineCloudBridge.cs`: E2 (ambient multiplier) → E3 (profile visibility) → E4 (refresh workaround) → E5 (history suppression).
- `RingDayNightCycle.cs`: E1 (hold) → E3 (override).
- `project.godot`: E2 only (all three globals). `celestial_lighting.gdshaderinc`: E3 only. `ring_atmosphere.gdshaderinc`: E2 only.
- The 4-5 terrain and ocean shaders with `light()`: E3 only.
- `ring_emitter_grid_*` and `EmitterGridBeamLayer.cs`: E3 (`eg_day_at`) → E4 (fronts, extraction). Do not run in parallel with any open grid G5/G6 work.
- `RingScaledFarViewport.cs`: E2 only (ambient). E4 uses the existing `AddScaledWorldChild`.
- `RingLiveTuningDrawer.cs`: one hook line in E1. Later phases edit only `RingLiveTuningDrawer.Eclipse.cs`, in order.
- `scenes/VoxelRingIntegrationTest.tscn`: E1 (EclipseEvent) → E4 (EclipseBeams). `UndergroundParticipants.cs`: E4 (one line).
- Several of these files have uncommitted changes in the working tree today (`project.godot`, `RingScaledFarViewport.cs`, `RingSunshineCloudBridge*.cs`, `RingLiveTuningDrawer.cs`, the scene). Land or coordinate that work before E2.

**Critical path:** E0 → E1 → E2 → E3 → E4 → E5 → E7. E6a and E6b run alongside.

### E0 implementation notes (2026-10-16, landed)

Files: `scripts/Celestial/Eclipse/` (`EclipseWindowSolver`, `EclipseEventSettings`, `EclipseSiteTable` + `IEclipseSiteOutput`, `EclipseEventTimeline`, `EclipseEventStream`); tests `tools/EclipseWindowSolverTest.cs`, `EclipseEventTimelineTest.cs`, `EclipseEventStreamTest.cs` with `eclipse_*_test.gd` runners; goldens `tools/fixtures/eclipse_window_golden.json` and `eclipse_event_timeline_golden.json` (`-- --capture` writes a missing golden).

**Clock finding (item 6).** The two clocks do not share a rate:
- Celestial: `RingDayNightCycle._Process` does `_simulationSeconds += delta × TimeScale` (`RingDayNightCycle.cs:161-168`), TimeScale 1-100, pausable via `Paused`.
- Weather: `RingVolumetricCloudLayer._Process` does `CloudSimulationTimeSeconds += delta` (`RingVolumetricCloudLayer.cs:582`), real rate, gated only by `CloudSimulationPaused`. It has no reference to TimeScale.
- So at TimeScale k the grid pulse (weather clock) is k times slower than the celestial event. With the hold removed (decision 6), a weather-clock refuel would run its 3 s row cadence in real time while the show is compressed.
- **Decision implemented (the item 6 default):** `EclipseEventSettings.RefuelClock = Celestial`. The grid maths (`EmitterGridPulseClock.ArrivalTime`, `GetFronts`, `FlareEnvelope`) are fed the *celestial* time as the grid time, so the Sustain flashes, heartbeat and relaunch kick compress with TimeScale (at 10x: a row every 0.3 s real). `Evaluate(tCel, tWeather)` then ignores `tWeather` (tested bit-exactly). `RefuelClock = Weather` keeps the spec A.4 behaviour (grid time = weather clock, flash times converted with the offset `tCel - tWeather`; that offset is not constant when TimeScale ≠ 1).
- **Consequence for E1/E4:** the visible normal grid pulse is still on the weather clock. While the event is active (at least from MainFire to the end of Aftermath) the director must drive the grid layer's clock from the celestial time (same formula as the timeline's `GridTime`) or the on-screen flashes will not coincide with the timeline's flashes at TimeScale ≠ 1. The phase relationship between the weather and celestial grid positions is arbitrary, so switch the layer's clock at MainFire while the normal pulse is suppressed (`GridPulseWeight = 0`), and switch back after Aftermath.
- **No hold:** `HoldRealTimeDuringEvent` is not in the settings model, and E1 must not add the `RingDayNightCycle` hold. All event timing is a pure function of celestial time, so it compresses with TimeScale. Test: stepping the clock by `k/60` s per frame, every milestone (phase starts, fired counts, level 0.5, fronts met) lands at the 1x real time divided by k (within one frame), at k = 2, 10, 100. Ignition (launch to full) takes 151.5 s real at 1x, 15.15 s at 10x, 1.52 s at 100x.
- **Stream threshold:** `EclipseEventStream.Advance(..., maxContinuousStepSeconds)` treats a larger celestial step as a discontinuity. The director must pass `max(5, 0.5 × TimeScale)`; a fixed 5 s would produce spurious Resyncs on slow frames at 100x.

**Measured solver numbers (default configuration, 36 longitudes × across {-159, 0, +159} km, ground):** centred totality 4,500.00 s at T(k+½); ring window 4,110.14 s raw, 4,090.14 s with the 10 s margins (S = 12,048.45, E = 16,138.59 for k = 0); umbra sweep 395.0 s (the spec's 391 s was with one across sample; the extra ±159 km adds 4 s); any-longitude totality 4,900.2 s (spec 4,893 s, same cause). Solve time is about 30 ms per eclipse (3 across samples), so keep it off the main thread (`SolveAsync`).

**Timeline numbers (seed-1337 registry/topology, default settings):** MainFire 20.0, Kindle 24.0, kindled 26.5, Ignition 29.5, FrontsMet 179.5, Crescendo 181.0 (last arrival), Handoff 189.0, Sustain 191.5 (length 3,823.6 s), Instability E−S−75, Aftermath start E−S−15.5 (all relative to S). Fire times equal `τ_ign + min(p, C−p)/v` exactly; 12.0-12.5 beams fire per 3 s; the ramp step is +1.82 % per row; a dead-site seam of 10 sites gives `FullLevel = 1 − 0.9·10/594`. Evaluate costs about 2.3 µs.

**Deviations and additions (E0):**
- Namespace `RingworldTechDemo.Celestial.Eclipse`. `HoldRealTimeDuringEvent` omitted (decision 6). Added settings: `ShockwaveArrivalFraction` (0.75), `ReturnTailSeconds` (450), `RefuelClock`, `MeetingFlareSeconds`, `RealSunEnergy`, `HubDirectFraction`, `HubDayAtFull`, `MainClimbExponent`.
- Ring window uses three across offsets (-W/2, 0, +W/2) instead of only the W/2 edge, and the observer frame is parameterised by ring axis and epoch sun (`DefaultAuthoredEpochSun` is the scene's `StarLight` basis Z, axis Up). The runtime may pass the real axis.
- The Return phase has no pure end: `Evaluate`/`PhaseAt` take an optional local third contact (Return ends 45 s after it, never before E + 45 s); otherwise `ReturnTailSeconds`.
- A window too short for the stages scales every authored duration by `TimeCompression` (including the ignition speed); at the default window it is exactly 1.
- Breath is computed relative to the start of Crescendo (continuous) and in celestial time. The heartbeat counts only grid landings that happen during Sustain.
- `HubWeight` is a hard 1 on [S, E) (the 1 s smoothing for scrubs belongs to the director).
- The timeline test uses the gen-17 v4 registry (`EmitterSiteTestWorlds.CreateGen17(1337)`, 595 sites), like `emitter_grid_g1_test`; there is no separate `emitter_grid_topology_test`. The stream test uses `EclipseSiteTable.Uniform` (synthetic evenly spaced chains).
- Nearest-site and refuel logic in the stream keep a little state (one-shot flag, last refuel time); after a Resync the flags are restored to what has already happened at the new time.

---

## R. Risks

1. **Per-fragment light in shared terrain shaders** (main global LOD, the far viewport and proxies): a regression risk for every view. Mitigation: a single include helper, the zero-uniform bit-parity gate (T1), and E3 limited to four shaders.
2. **Sunshine depth-core bug (T2):** hub beams are dimmed by clouds behind them until the refresh workaround runs or Sunshine is fixed.
3. **The time-scale hold changes how the clock advances.** Mitigation: a pure function of t; the user's TimeScale is never written. CelestialDebugHud speed buttons appear to "not work" inside the window, so the status line explains why.
4. **Exposure and tonemap:** AgX and the far viewport's separate curve may flatten the ramp or the overload (T3, T7).
5. **The darkness scales could re-trigger today's cloud show-through.** Mitigation: the 0.3 cloud floor and capture shot 3.
6. **Draw calls (F20):** +2 main and +3 far, against a scarce budget.
7. **Temporal history smear** during the flicker and flare (T6).
8. **Macro-shadow churn** if the hold is missed (44 km of travel triggers a whole-ring rebuild).
9. **Long variable darkness** (up to about 7 min at the worst longitude) may feel like a bug without the PDA countdown (Q1).
10. **Load ordering:** the director must evaluate after `RingDayNightCycle` (priority −100) and before `day_side_atmosphere` and the layers. `ApplySunDirection` pulls the override synchronously, so `StarLight` keeps a single writer.

## T. Technical verifications (not user decisions)

1. **T1:** hub uniforms at 0 give bit-identical captures at the M1 poses, before and after E3.
2. **T2:** cost of `RefreshCloudCompute` once the main beam lights (hitch ≤ 8 ms?), and whether the core then works for the hub beams.
3. **T3:** the far viewport's HDR output space for the hub body (does the canvas background pass through the main tonemap?).
4. **T4:** the zero-energy shadow gate's saving, and no pop when energy rises (re-enable one frame ahead).
5. **T5:** the Sunshine shadow-map and near cloud-shadow refresh after the `StarLight` swing completes inside the 20 s darkness beat.
6. **T6:** Sunshine history during the flicker, with `SetLightningFlashIntensity` suppression.
7. **T7:** auto-exposure behaviour across darkness → kindle → full; is `EclipseExposureMaxSensitivity` needed?
8. **T8:** with the macro mask held and zeroed by w, no shadow pops at the start and end of the event.

---

## Q. Open questions for the user (each with a recommended default)

1. **Umbra and anchoring.** The umbra really sweeps the ring in about 6.5 min (F2). With the event anchored to the whole-ring window, the darkness before ignition is 20 s-7 min and the wait for the real sun after the collapse is 0-6.7 min, depending on longitude. The alternative is a ring-uniform eclipse: drop parallax at runtime so the sun returns right after the collapse everywhere, at the cost of the existing parallax realism (the giant shifts ≤ 2.1° on the sky). **Recommended: keep parallax and the whole-ring window, with a PDA countdown.**
2. **Celestial TimeScale during the event.** Hold at 1× from the 5-minute pre-roll until the sun returns, or let the 1-100× speed compress the show (at 10× the ignition takes 15 s)? **Recommended: hold at 1× (setting, on).**
3. **The grid pulse during ignition, and the refuel cadence.** Suppress the normal one-way pulse from the main fire until the hand-off so the bidirectional ignition reads cleanly? Sustain refuel then uses the existing circuit: each spire flashes once per 5 minutes, a row of six every 3 s marching round the ring, and the hub "heartbeats" every 3 s. **Recommended: suppress during ignition, and use the existing cadence.**
4. **Brightness accounting.** Linear energy per beam at the hub (+1.8 % steps every 3 s) instead of a perceptual curve, and future dead emitters making the sun proportionally **dimmer** rather than slower to ignite? **Recommended: yes to both.**
5. **Collapse consequences.** Keep the collapse cosmetic (visuals, a light flash, audio and a small camera shake; no wind, damage or gameplay effect)? **Recommended: cosmetic only, shake on (setting).**

---

## Appendix: key numbers

- **Eclipse:** period 28,187.37 s (7.83 h); centred totality 4,500 s; partial phases 44.6 s; first totality about 3.3 h into a fresh world; whole-ring totality 4,110 s (the event window is 4,090 s with the 10 s margins); any-longitude totality 4,893 s; umbra sweep about 390 s; the ring turns 329° during the window.
- **Pulse and ignition:** v = 104.72 km/s; 3.0 s per row; half the ring in 150.0 s; 12 beams per 3 s, 594 regular + 1 main; full ignition 151.5 s after launch.
- **Geometry:** hub about 4,998 km above the crowns; beam tilt ≤ 1.519°; the hub is the scaled-world origin; hub body 30 km radius (0.34°).
- **Lighting:** hub full = 3.2 direct energy (80 % of 4.0), day factor 0.8, colour (0.92, 0.97, 1.0); zenith error 0.05° at 4 km, 0.83° at 72 km, 9.7° at 850 km.
- **Darkness:** ambient 0.30 → 0.045; cloud ambient × 0.4 (floor 0.3); stars 1.0.
- **Default timeline (τ from S):** dark 0-20 s; main fire 20 s; kindled 26.5 s; ignition 29.5-181 s; hand-off 189-191.5 s; sustain about 63.7 min; collapse E − 75 s → E − 15.5 s.

---

## USER DECISIONS — binding

2026-10-14:
- The event happens during the gas-giant total eclipse (verified: 75.0 min centred, every 7.83 h of celestial time; whole-ring 68.5 min).
- Spires fire beams that converge exactly on the hub, which lights an artificial sun.
- The main spire sustains it, and the regular spires refuel it with pulses.
- The eclipse is close to pitch black (replacing the 0.95 `GameplayEclipseDarknessCap` behaviour) until the hub sun lights the ring.
- Announcer: a Subnautica-style suit PDA voice (not a ring AI). An LLM may drive the lines later. For now: a scripted event stream, a line table and an LLM-ready seam.

2026-10-16:
1. **Lighting: "noon everywhere".** Light comes from each point's zenith; the whole arch, including the far side, is lit evenly with no night side. Full brightness = 80 % of the real sun.
2. **Ignition.**
   - The main emitter fires first and establishes the hub sun at about 10 %.
   - A bidirectional ignition pulse leaves the main spire along the 6 chains.
   - Each emitter it reaches fires a continuous laser to the hub, and each beam reaching the hub raises the brightness toward 100 %.
   - Full ignition takes about 2.5 min: half the ring at about 104.72 km/s (verified, 150.0 s), so both fronts meet at the antipode.
   - Once the sun is fully lit, the non-main emitters stop; the normal one-way network pulse then refuels the sun, each spire flashing to the hub as the pulse passes.
   - Reuse `EmitterGridPulseClock.ArrivalTime`; no new clock.
3. **End: a dramatic collapse** as totality ends: instability, flicker, an overload flare, implosion, a light shockwave, beams cut. Then the real sun returns.
4. **Dark or dead emitters: backburner.** A seam only.

2026-10-16 (answers to §Q):
5. **Q1 timing: keep the sweep.** Keep parallax and anchor the event to the whole-ring window, with a PDA countdown filling the variable dark wait (20 s to 7 min before ignition, 0 to 6.7 min after the collapse).
6. **Q2 time scale: FOLLOW the celestial TimeScale (overrides the recommendation).** No 1× hold, so the whole show compresses with the 1-100× speed (at 10× the ignition takes 15 s).
   - E0/E1 must time the ignition fronts, hub beams, collapse and audio cues in celestial time, so they stay in sync with the compressed timeline.
   - Check whether the weather clock (refuel flashes via `ArrivalTime`) scales with TimeScale. If it does not, the sustain heartbeat runs at real-time cadence while the timeline is compressed; make that explicit and decide during E0 (default: refuel in celestial time during the event).
   - The PDA must drop or merge lines that can't fit at high speed (E6a).
7. **Q3 grid pulse: suppress it during ignition.** Refuel uses the existing cadence after the hand-off.
8. **Q4 brightness: linear per beam.** Steps are +1.8 % per 3 s; future dead emitters make the sun dimmer, not slower to light.
9. **Q5 collapse: default accepted.** Cosmetic only, camera shake on (setting).
