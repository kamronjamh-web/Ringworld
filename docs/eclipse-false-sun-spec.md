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
| E3 (landed, see the E3 implementation notes) | Hub lighting: `celestial_lighting.gdshaderinc` functions, the `ringworld_primary_light` helper in the global, proxy and hull terrain and ocean-global `light()` plus macro shadow × (1 − w), macro-rebuild hold, `IPrimaryLightOverride` in `RingDayNightCycle`, `day_side_atmosphere.gd` hub energy, colour and day, sky shader hub uniforms, bridge profile visibility, grid `eg_day_at` hub term plus CPU twin, drawer lighting section, hub level override | E2 | E6b | zero-uniform parity (T1); noon-everywhere captures with the override; grid tests green |
| E4 (landed, see the E4a-E4e implementation notes) | Visuals: `ring_beam_ribbon.gdshaderinc` extraction (grid parity gate), hub beam near and far layers, hub body, grid event fronts (z/w lanes, `SetEventFronts`, `MaxHeadSprites` 24), refuel flashes, in-cloud diffusion, T2 refresh workaround, underground participants, optional stand-in crown glow | E3 | E6b | grid parity; rebase test for beams; capture shots 4-9 |
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

### E1 implementation notes (2026-10-16, landed)

E1 publishes state, events and status only. **No lighting or visual state is read or written.**

Files:
- `scripts/Celestial/Eclipse/EclipseEventEngine.cs`: plain-C# core (`IEclipseClock`, `EclipseObserverPose`, `EclipseEventEngine`). It owns the window cache, the timeline and the stream, and it holds the force/jump/scrub logic and the status text. It is testable with a stub clock.
- `scripts/Celestial/Eclipse/EclipseEventDirector.cs`: scene node "EclipseEvent" (priority -99, after `RingDayNightCycle` at -100) plus `DayNightEclipseClock`, an adapter over `RingDayNightCycle`. It resolves the clock, `EmitterGrid` (topology and pulse clock), the chunk grid, the explorer and the cloud layer; builds the `EclipseWindowSolver` from `RingDayNightCycle.Configuration`; and rebinds when the grid layer rebuilds its pulse clock.
- `scripts/ui/GameplayEclipseEventSettings.cs` and `GameplayEclipseEventDefaults.cs` (`user://gameplay_eclipse_event_defaults.tres`): same pattern as the grid pair. All 41 float fields, 3 enum ints and 2 bools of `EclipseEventSettings` are persisted. `ToCore()` maps them onto the pure record, and the smoke asserts that there is no default drift.
- `scripts/ui/RingLiveTuningDrawer.Eclipse.cs`: Lighting tab sections "Eclipse event: timeline", "sustain and refuel", "collapse", "hub levels" and "debug" (force, jump to phase, scrub, freeze, log events, fired-site strip). The sections are built from a table; the debug controls are disabled on worlds without a bound event.
- Hooks: `RingLiveTuningDrawer.cs` (one line, `BuildEclipseSections(tab)` at the end of `BuildLightingTab`), and `RingLiveTuningDrawer.EmitterSites.cs` (init, process and flush, chained exactly as the grid does).
- `scripts/ui/CelestialDebugHud.cs`: "ECLIPSE EVENT" block with the status, the ring strip and the `< Phase`, `Force eclipse` and `Phase >` buttons.
- `scenes/VoxelRingIntegrationTest.tscn`: `EclipseEvent` node.
- `scripts/RingDayNightCycle.cs`: **one read-only property**, `AuthoredEpochSun` (the captured authored `StarLight` basis Z). The window solver needs it as the epoch sun. Nothing else in the clock changed, and there is no hold and no debug offset.

**Force / jump / scrub mechanism.** These move the *celestial clock itself* via `RingDayNightCycle.SetSimulationSeconds`, the same call as `CelestialDebugHud.Jump`. A debug override offset was rejected: the clock is the saved absolute value, so a seek is just a save/load-equivalent edit. The event is a pure function of it, so everything stays deterministic with no new save field, and every other consumer (sun direction, eclipse darkness) stays consistent with the jump. The cost is that it changes world time, which the tooltips say. Each action also calls `MarkDiscontinuity()`, so the next update emits exactly one `Resync`, even for a scrub below the 5 s threshold. Target selection: the current eclipse until `RingEnd + ReturnTailSeconds` has passed, then the next one. Landing times are nudged up by ulps so that `(S + tau) - S` never rounds below a phase boundary. Force goes to `S - PreRoll`. Scrub is clamped to `[-PreRoll, E - S + 120]` and applies on slider release. Freeze toggles the clock's existing `Paused`.

**Item 6 (TimeScale).** There is no hold. `EclipseEventStream.Advance` receives `max(5, 0.5 x TimeScale)` (`EclipseEventEngine.MaxContinuousStepSeconds`). The director test steps `k/60` s per frame at k = 1, 2, 10, 100: Ignition and Crescendo land at the 1x time divided by k, within one frame, with no Resync. A 6 s step is a Resync at 1x and 10x but not at 20x and 100x.

**Hooks for E2-E4:**
- `EclipseEventDirector.State` (the `EclipseEventState` snapshot, updated every frame at priority -99), `Phase`, `Bound`, `Engine.Timeline` (`Settings`, `Window`, the per-site `SiteFireTau`/`SiteArrivalTau`, `FireOrder`) and `Engine.LocalSecondContact`/`LocalThirdContact` (refreshed every 10 celestial s).
- `EclipseEventDirector.EventRaised` (and `Engine.EventRaised`): the `EclipseEvent` stream. `Engine.EventLog` holds the last 32.
- **Grid-clock hook (E4):** `GridClockOverrideActive` is true from `TauMainFire` until the end of the window (`E`), and only while `RefuelClock == Celestial`. `GridTimeSeconds` is the celestial time while active, and the weather clock otherwise. E4 should have `EmitterGridBeamLayer` use it in place of `CurrentTime` (`CloudSimulationTimeSeconds`) while active. The layer was **not** edited in E1, so the on-screen pulse is unchanged. `State.GridPulseWeight` is the factor for suppressing the normal fronts. The Return phase (after E) is back on the weather clock.
- `Engine.SolverState` (`None`/`Pending`/`Cached`) and `Engine.DescribeStatus()`/`BuildRingStrip()` are the HUD feeds.
- `director.ForceEclipseNow()`/`JumpToPhase()`/`ScrubToTau()`/`SetFrozen()` are the debug entry points. E3's hub-level override and the zenith gizmo are not built.

**Deviations from the spec:**
- No hold and no `HoldRealTimeDuringEvent` (decision 6), as the E0 notes said.
- No centred-window fallback while the solve is pending: the event stays Idle for the 1-3 frames (about 30 ms of work) the worker needs, and the first update after the result is a Resync. Debug actions solve synchronously.
- The window is solved for the nearest eclipse index, and the observer pose uses `RingAcrossBaseOffsetMeters`/`RingVerticalBaseOffsetMeters` exactly as `RingDayNightCycle.ResolveObserverOffsetKm` does. The solver radius comes from the grid circumference; the solver is not created until the grid reports one.
- The Hub-level override, "Show beam ids" and "Show local zenith gizmo" (G.3) belong to E3/E4. The drawer sections for the lighting, beams and audio settings arrive with those phases (E1 persists the timeline and hub-level fields only).
- The save/load mid-event scene test is state-only: the director test shows that restoring a clock value reproduces a bit-identical `EclipseEventState`. A full `RingWorldSaveManager` round trip was not run (it needs a world and a window).

Tests (all headless, all PASS): `tools/EclipseEventSettingsSmoke.cs` (`eclipse_event_settings_smoke.gd`: defaults, round trip, setter and sanitiser clamps, NaN/infinity fallbacks, hostile file, reset, invalid path, ToCore no-drift) and `tools/EclipseEventDirectorTest.cs` (`eclipse_event_director_test.gd`: unbound/disabled, force, jump to every phase, scrub and clamps, Resync on jump/rewind/external seek, TimeScale following, grid hook, local contacts bracketing the window, determinism, pause, async solve equals sync, status text, scene node, director node settings relay). No headless drawer-smoke pattern fits the Lighting tab (the existing smokes need the full scene), so the drawer is covered by the compile and the scene-parse check only.

### E2 implementation notes (2026-10-16, landed)

E2 only darkens. The hub light, `ringworld_hub_day` and `ringworld_hub_light_weight` are not used yet (E3).

Files:
- `scripts/Celestial/Eclipse/EclipseDarknessModel.cs` (new, pure): `EclipseDarknessSettings`, `EclipseDarknessOutput`, `EclipseDarknessModel` (target weight, 0.5 s smoothing, per-term multipliers). `EclipseEventSettings.Darkness` carries it; a darkness-only change does not rebuild the timeline (`EclipseEventEngine.SetSettings`).
- `EclipseEventDirector.UpdateDarkness(coverage, delta)` plus float properties `DarknessWeight`, `DarknessAmbientMultiplier`, `DarknessAtmosphereNightMultiplier`, `DarknessSkyNightMultiplier`, `DarknessCloudAmbientMultiplier`, `DarknessStarFloor`. `day_side_atmosphere.gd` calls it every frame with the same raw coverage it publishes as solar visibility, so the two always agree. Darkness works on every world that has the director node, and with `EventEnabled` off; with no director node everything stays neutral.
- `scripts/day_side_atmosphere.gd`: applies the multipliers (surface ambient energy, fog light energy, night sky top and horizon, star floor), publishes the global `ringworld_night_light_scale`, tells the cloud bridge, and sets the meta `ringworld_eclipse_darkness` on the main WorldEnvironment for the far viewport. Early returns and `_exit_tree` reset the global to 1.
- `project.godot`: `ringworld_hub_day` (0), `ringworld_hub_light_weight` (0), `ringworld_night_light_scale` (1). Only the last is used in E2.
- `materials/ring_atmosphere.gdshaderinc`: declares `global uniform float ringworld_night_light_scale` and multiplies `atmosphere_night_color` by it in `ring_atmosphere_scattering_color` and in the two sky boundary/upper colours. **E3: do not declare this global again in `celestial_lighting.gdshaderinc`** (a duplicate declaration fails to compile in shaders that include both). `ring_atmosphere_shared.gdshaderinc` is untouched: it is also compiled into the Sunshine GLSL compute, which cannot declare a global.
- `RingAtmosphereModel.EclipseNightScale` (static, default 1) scales the night colour in the two CPU-built profiles: Sunshine `ring_atmosphere_profile[6]` (`RingSunshineCloudBridge.UpdateAtmosphereProfile`) and the far cloud mask (`RingScaledFarViewport.CloudMaskAtmosphere.cs`).
- `RingSunshineCloudBridge.SetEclipseDarkness(cloudScale, atmosphereScale)`: runtime multiplier on `NightCloudAmbientColor` (the persisted colour is never rewritten), clamped to a floor of 0.3 (D.2); sets `EclipseNightScale`.
- `RingScaledFarViewport.EclipseAmbient.cs` (new partial) plus fields in `RingScaledFarViewport.cs`: the far and the regional environments keep their creation snapshot; while the meta weight is above 0 they blend toward the live (darkened) main ambient by that weight, and at weight 0 the snapshot is restored exactly. This is narrower than "always follow the live value": following it all the time would change every non-eclipse frame (the snapshot is currently frozen at load time). It matches D.1 ("written only while w_d > 0, and restored afterwards").
- `RingGraphicsQualityController.LocalShadows.cs`: zero-energy shadow gate (finding 4). The sun shadow pass is suspended when `StarLight` energy is below 0.001, with hysteresis (resumes at 0.002) so it cannot flap on a ramp. Reason text "sun energy below 0.001"; `SunShadowZeroEnergySuspended` exposes it. Outside totality the sun energy is 4 x visibility (above 0), so nothing changes.
- Persistence and drawer: `GameplayEclipseEventSettings` and `GameplayEclipseEventDefaults` gain `EclipseDarknessEnabled` (bool) and five floats `EclipseAmbientScale`, `EclipseAtmosphereNightScale`, `EclipseSkyNightScale`, `EclipseCloudAmbientScale` (min 0.3), `EclipseStarVisibility`, mapped in `ToCore`. `RingLiveTuningDrawer.Eclipse.cs` has a new first section "Eclipse darkness" in the Lighting tab (toggle, five sliders, live weight and multiplier readout).
- `CelestialConfiguration.cs`: comment on `GameplayEclipseDarknessCap` (D.3).

**Darkness values (w = 1, defaults):**

| Term | Before | Totality |
|---|---|---|
| Env ambient energy | 0.30 | 0.045 (x 0.15) |
| Fog light energy | 0.38 | 0.057 (x 0.15) |
| Atmosphere night colour | (0.026, 0.035, 0.055) | x 0.35 = (0.0091, 0.0123, 0.0193) |
| Sky night top / horizon | (0.002, 0.004, 0.012) / (0.008, 0.015, 0.035) | x 0.5 |
| Cloud night ambient | (0.18, 0.23, 0.35) | x 0.4 = (0.072, 0.092, 0.14); setting floor 0.3 |
| Stars | 0.62 | 1.0 (a floor: `max(current, w x 1.0)`) |

Not done: the optional `EclipseExposureMaxSensitivity` (T7 decides whether it is needed); the far-cloud shaders' own `night_color` albedo terms (D.1 does not list them; check them in the captures); the shadow gate does not re-enable one frame ahead (it resumes on the first frame the energy reaches 0.002).

**`GameplayEclipseDarknessCap` (D.3):** kept at 0.95, and its test (`CelestialCoreSmokeRunner`: totality = 0.95, plus the partial scan) is unchanged. The new model reads raw coverage only; `EclipseDarknessTest` shows the totality weight is identical for caps 0.95 and 0.5.

**Deliberately changed pin:** `tools/celestial_runtime_integration_smoke_test.gd` asserted that totality uses the plain night star floor (0.62) and night sky palette. E2 changes exactly that, so it now waits for the 0.5 s smoothing and asserts the end-to-end darkened values (weight at least 0.99, star floor 1.0, sky = night x 0.5, atmosphere night scale at most 0.36, ambient at most 0.16 x night ambient, hub globals still 0, darkness exactly neutral again after Restore preview).

**Bit-identical outside an eclipse.** By construction every multiplier is exactly 1 (and the star floor exactly 0) when the weight is 0: the GDScript `_scale_rgb` returns the colour untouched at 1, the shader terms multiply by 1.0, the far-viewport ambient is restored from the snapshot, and the cloud bridge reuses the unscaled `NightCloudAmbientColor`. Headless evidence: `tools/eclipse_darkness_parity_test.gd` runs the real scene at four non-eclipse clock times with this `day_side_atmosphere.gd`, then with the committed HEAD copy swapped in, and compares ambient, fog, sun and planet light and the sky material. Max difference 9.5e-9 (convergence residue of the blend lerps).

Tests: `eclipse_darkness_test` (new: pure model and director relay), `eclipse_darkness_parity_test` (new), `eclipse_event_settings_smoke` (extended), `celestial_runtime_integration_smoke_test` (updated as above). Windowed harness: `tools/eclipse_darkness_capture.gd` (day, night, partial, totality; darkness off and on; three poses; luminance, and an off-vs-on pixel diff of the day and night frames). It was parse-checked only: no windowed run was possible because the user's Godot editor (PID 26264) was open.

**Deviations:** (1) the hub-relief term of D.1 (`w_d` falling at the kindle) exists in the model but is disabled (`HubReliefAvailable = false`) until E3 provides the hub light; otherwise the kindle would brighten the world with no light source. (2) Far-viewport ambient: blend by weight, not always live (above). (3) The darkness settings live on `EclipseEventSettings.Darkness` (a nested record) rather than flat fields, so slider drags do not rebuild the event timeline. (4) The `EclipseDarknessEnabled` master toggle is an addition (it also gives an exact "old lighting" for A/B captures).

### E3 implementation notes (2026-10-16, landed)

E3 lights the world from the hub: every point is lit from its own zenith ("noon everywhere"). With the hub uniforms at 0 every shader and script output is unchanged (T1, below). E4 adds the visuals (hub body, beams, ignition fronts).

Files:
- `materials/celestial_lighting.gdshaderinc`: declares `ringworld_hub_day` and `ringworld_hub_light_weight` (project.godot already had them from E2; `ringworld_night_light_scale` stays only in `ring_atmosphere.gdshaderinc`). `ringworld_effective_solar_day(g) = max(clamp(g) x clamp(vis), clamp(hub_day))`, `ringworld_effective_solar_visibility()`, and `ringworld_primary_light(light_view, surface_up_world, view, primary_weight)`. The helper returns its input unchanged (early return, so the value is bit-exact) when `hub_light_weight x primary_weight` is 0, and never produces a NaN when the two directions cancel.
- `ring_terrain_global`, `ring_terrain_proxy`, `ring_hull_global`, `ring_ocean_global` (`light()`): `n_dot_l` uses `ringworld_primary_light(LIGHT, surface_up_world, VIEW_MATRIX, primary_sun_weight)`; the macro shadow of the global, proxy and ocean-global shaders is multiplied by `(1 - hub_light_weight)`. A `surface_up_world` varying was added to global, hull and ocean (the proxy had one). The hull builds on the 1803339 structural-cue shader (the cue code is untouched). Terrain3D near disk, trees and grass keep `LIGHT` (the camera zenith error is at most 0.05 degrees at 4 km).
- `ring_cloud_transmittance_mask.gdshader`: the haze gets `ringworld_effective_solar_visibility()`.
- `ring_local_sky.gdshader`: uniforms `hub_direction`, `hub_level`, `hub_color`, `hub_halo_intensity`, and a zenith halo (`pow(dot, 24)` plus a wide skirt) that draws nothing at `hub_level` 0.
- `ring_emitter_grid_pulse.gdshaderinc` (`eg_day_at_hub(dot, vis, hub_day) = max(eg_day_at, hub_day)`), `ring_emitter_grid_beam_common` and `..._head_common` (the three beam and head call sites). CPU twin: `EmitterGridIntensity.DayAt(dot, vis, hubDay = 0)`. `tools/shaders/emitter_grid_intensity_parity.gdshader`, `EmitterGridShaderFixture` and `emitter_grid_shader_parity_test.gd` got a `hub_day` case (`prograde_hub_day`, hub day 0.55).
- `scripts/Celestial/IPrimaryLightOverride.cs` (new) and `RingDayNightCycle.PrimaryLightOverride`: `ApplySunDirection` stays the single writer of the StarLight basis. It blends the real sun direction toward `surfaceUp` (the explorer's canonical up, already computed in `UpdateFrameAndLight`) by `PrimaryLightWeight`. Weight 0 takes no new code path.
- `scripts/Celestial/Eclipse/EclipseHubLightModel.cs` (new, pure): `EclipseHubLightModel.Evaluate(settings, state, levelOverride)` gives `EclipseHubLightOutput(Weight, Level, DirectEnergy, Day, colour, SkyTintStrength, ZenithHaloIntensity, Overridden)`: from the timeline state (`HubWeight`, `Level`, `HubDirect`, `HubDay`) or from the debug override (weight 1, `RealSunEnergy x HubDirectFraction x L`, `min(1, HubDayAtFull x L)`). `EclipseHubLighting` is a static (weight, `Tick`, `HoldMacroShadows`) for non-render consumers.
- `EclipseEventDirector` implements `IPrimaryLightOverride`: `HubLight`, `HubLightWeight` (eased over about 1 s, snapped on the first frame and for the override), `HubLightDay/Direct/Level/Color`, `SetHubLevelOverride(level)` (negative clears; 0..2.5; visual only, not saved), `ShowZenithGizmo` (debug lines: local zenith green, StarLight yellow). `UpdateDarkness` now feeds the hub level (including the override) to the darkness model, and `EclipseDarknessModel.HubReliefAvailable` is `true`: the darkness weight falls to half at the kindle level and to 0 at twice that.
- `scripts/day_side_atmosphere.gd`: `current_day_factor = max(geo x vis, hub_day)` (hub day does not go through the blend lerp), `StarLight` energy `max(4 x vis, HubDirect)`, colour `lerp(sun colour, HubLightColor, hub weight)`, the two shader globals (published only on change, reset to 0 on every early return and in `_exit_tree`), the sky hub uniforms, and the sky palette lean `HubSkyTintStrength x hub_day x (1 - vis)` toward (0.85, 0.95, 1.0). The partial-eclipse sky and fog tints (`eclipse_sky_factor`, `eclipse_day_coverage`) now use `current_real_sun_day_factor` (the real sun's share), otherwise the hub-lit totality would be tinted as a dimmed real sun. Equal to the old value while the hub is off.
- `RingSunshineCloudBridge.UpdateAtmosphereProfile`: profile[6].w = `max(vis, current_hub_day)`.
- `RingTerrainGlobalLod.RequestMacroShadowRebuildForSunChange`: no rebuild while `EclipseHubLighting.HoldMacroShadows` (weight above 0, or less than 3 frames since it returned to exactly 0).
- Persistence and drawer: `GameplayEclipseEventSettings`/`Defaults` gain `HubLightRed/Green/Blue` (0.92, 0.97, 1.0), `HubSkyTintStrength` (0.25), `HubZenithHaloIntensity` (0.35), mapped in `ToCore`. `HubDirectFraction`, `HubDayAtFull` and `RealSunEnergy` already existed. `RingLiveTuningDrawer.Eclipse.cs`: the "hub levels" section is now "Eclipse event: lighting" (live hub readout, the five new sliders and the three old ones). The debug section gains "Hub level override" (toggle plus 0..2.5 slider) and "Show local zenith gizmo"; both work with any director, not only a bound event.

Not built (E3 scope notes): Terrain3D near-disk helper (optional, at most 128 m); the "in-scatter phase toward the hub" in the sky (the atmosphere scattering keeps the real sun direction; the halo and the day factor carry the hub); far viewport sun colour (`FarRingSun` mirrors only the direction and energy, so the far arch is lit white where the near ground is cool white; a colour mirror was tried and dropped while chasing the parity failure described below, which turned out to have another cause, so it can be re-added); far viewport ambient (it is restored to its creation snapshot once the darkness weight reaches 0).

**Macro-shadow hold, and a race the T1 gate found.** The mask is built from the light direction at the moment a rebuild starts. A first version released the hold as soon as the published weight fell below 0.001, while the eased weight was still a few 1e-4 and the light basis (written by `RingDayNightCycle` one frame before the director) still a hundredth of a degree off the sun. The rebuild then used a direction that depended on timing, which flipped macro-shadow texels by one level in a different pattern every process and showed as 60-130k scattered one-level pixel differences on the ground in the zero-uniform parity capture (the old code: 1.4-1.7k, the noise floor). `HoldMacroShadows` now releases only after the weight has been exactly 0 for 3 frames. Keep this rule for any other consumer that builds something from the StarLight direction at a single moment.

**T1 (hub uniforms 0, bit parity).** `tools/eclipse_hub_capture.gd --mode=parity` (new): day, night and totality with the darkness ON, at five poses (ground horizon, ground up, 9 km looking down, and the main spire's Far and High emitter viewpoints, the M1 poses), clocks frozen, auto-exposure settled for 8 s, every state shot twice (a, b). Pre-E3 build (HEAD 1803339) = reference, shot twice in two separate processes (base1, base2). Exact equality is not reachable: the Sunshine cloud compute and the cloud-layer dither differ by a few thousand single pixels between any two shots, even of the same build (base1 against base2: up to 40k of 1.44 M pixels in the high-altitude day frame). So the gate is the noise-baseline method: a frame passes when the post-E3 against reference difference (changed-pixel fraction above 2.5/255, mean signed and mean absolute luma, taken over a, b and cross pairs) stays within 2x the largest noise measure: the a-b pair of each run and base2 against base1, with floors of 0.1 % pixels, 0.0002 and 0.0003 luma that only matter where the noise is exactly 0. Result with the final code, all 15 states pass: 13 within 0.89-1.45x of the noise, and 2 (the main spire High pose at night and in totality) **bit-identical** (all four frames byte-equal to the reference, 0 of 1.44 M pixels differ). Per-state numbers are in the run's `results.json` (`compare`); the script also takes `--ref2` for the cross-process noise and `--only` for single states (only the first state, ground horizon day, is comparable with a full-run reference).

**"Noon everywhere" captures** (`--mode=noon`: totality with the hub override at 0, 0.1, 0.5, 1.0, plus the real sun at its highest elevation, at ground horizon, ground up, ground looking at the far arch, 9 km looking down, and 150 km looking up at the arch). Findings (the evenness metric is the mean luma of the arch strip in 10 bands, ground looking straight up, fixed exposure): real noon 0.094-0.470 (the far arch is mostly night side); hub 1.0 0.468-0.509, max over min 1.09. Without the per-fragment helper (hub day on, light weight forced to 0) the same strip is 0.20-0.33: the far arch is lit from behind, which is why the helper is required (F10). Mean luma with fixed exposure, hub 1.0 over real noon: ground horizon 0.92, ground up 1.01, ground arch 1.11, 9 km down 0.84. That is above the 70-90 % target at the ground: the terrain shaders gate the direct light by the day factor (3.2 x 0.8, so 64 % of the real noon direct term) but the sky, atmosphere and ambient at day 0.8 add up to about the real noon value. If the user wants 80 % of noon exactly, lower `HubDayAtFull` or `HubDirectFraction`.

**Budget (G.7, RTX 4070, 1600x900, GPU render time averaged over 240 frames; the budgets are written for the A1000).** Hub mode against the same frame with the hub off (real sun noon, override on and off, same sun energy and shadow passes): ground horizon +0.10 ms (9.90 to 10.00), ground up -0.06, ground arch -0.08, 9 km down -0.12, 150 km arch +0.13, with a repeat of the hub-off frame differing by 0.00-0.08 ms. So the hub light() path and the sky halo cannot be separated from noise on the 4070 (about 0.1 ms); there are no extra passes, draw calls or lights. Switching only the per-fragment helper off (light stays at the zenith) changes it by -0.16 to +0.02 ms, also inside the noise. The 0.05 ms budget is not demonstrated either way and needs the A1000.

**Deviations:** (1) macro-shadow hold rule (above); (2) sky and fog eclipse tints use the real sun's day (above); (3) the 1 s easing of the hub weight lives in the director (the shaders and the light read the eased value), as the spec's "smoothed over 1 s for scrubs" implies; (4) `HubReliefAvailable` is a constant, now `true`, and the E2 test that pinned it `false` was rewritten; (5) three light-colour channel sliders instead of a colour picker (the drawer has no colour widget); (6) no far-sun colour and no in-scatter phase (above).

**Open items for E4:** (a) hub body and beams must ignore B and the day factor as in E.2; (b) the hub body will sit where the zenith halo is: tune the halo (`HubZenithHaloIntensity`) once the body exists; (c) the far viewport still lights the far arch with a white sun; a colour mirror onto `FarRingSun` (and the regional far sun) is a few lines in `RingScaledFarViewport` but was dropped here to keep the parity gate clean, add it with its own parity run; (d) the sky reads grey-lavender at hub 1.0 (the sky shader's in-scatter keeps the real sun direction); a hub phase in `sample_ring_sky_atmosphere` would restore the blue; (e) shadow gate re-enable one frame ahead (T4) is still not done; (f) the shock flash is in `HubDirect` and `HubDay` already, so E5 only needs the shell.

Tests (all PASS, headless unless noted): `eclipse_hub_light_test` (new: pure model, override, CPU twins bit-exact at hub 0, director easing and snap, darkness relief by level, macro hold, and a scene check that StarLight follows the zenith at weight 1 and returns exactly at 0), `eclipse_event_settings_smoke` (extended: the five new fields round-trip, clamp and map to the core record), `eclipse_darkness_test` (relief now on), `eclipse_event_director_test`, `eclipse_window_solver_test`, `eclipse_event_timeline_test`, `eclipse_event_stream_test`, `eclipse_darkness_parity_test`, `celestial_runtime_integration_smoke_test`, `celestial_eclipse_smoke_test`, `sun_shadow_ownership_smoke_test`, `sun_shadow_stability_smoke_test`, `emitter_grid_g1_test`, `emitter_grid_layer_test`, `emitter_grid_settings_smoke`, `emitter_far_standin_test`, `world_loading_startup_contract`; windowed: `emitter_grid_shader_parity_test` (includes the hub day case), `eclipse_darkness_capture` (9 gates), `eclipse_hub_capture` parity and noon.

### E4 plan (Lead, 2026-10-16)

Base: 17b02dc. T2 and T3 were settled by experiment before planning. The harness was a temporary scratchpad script (`e4_t2t3_capture.gd`, two runs, RTX 4070, 1600×900, windowed, alongside the idle editor; image tests only, no GPU timing). It is not in the repo. Its patterns are described below so E4b/E4c can rebuild them in `tools/`.

**T2 result: the refresh workaround is not needed; drop it from E4.**
- **Setup.** A vertical test pillar 3 km ahead of a ground camera (pitch 4°), under a spawned storm front (`SpawnStormFrontAhead`), at dark totality (hub 0) and at hub noon (override 1.0). The halo pass is `blend_add`/`depth_draw_never`. Its `next_pass` is a depth-only core: `blend_add`, `depth_draw_always`, zero colour, 80 m wide (14 px), drawn one priority later. These are the grid core's render modes.
- **No refresh while lit.** The pillar was created after the session's last `refresh_compute` (Sunshine's `temporal_history_explicit_refresh_request_count` stayed at 8 from pillar creation to the end of the no-refresh shots). Mean luma of the core strip:
  - pillar hidden: 0.0799;
  - halo only: 0.1181;
  - halo + core: 0.7418 / 0.7432 (2 s and 6 s after the core was attached).
  - So without the core, clouds behind the beam kept only 6 % of its light. The core restores it, and it follows live: core off gives 0.1180 within 2 s, back on gives 0.7443. At hub noon: 0.1380 without the core, 0.7527 with it.
  - A band 6-20 px beside the strip is unchanged by the core (0.0836 / 0.0835): no hole spill.
- **A refresh while lit changes nothing.** After one refresh while lit (`SetDiagnosticTemporalHistoryDisabled(false)`, count 8 → 9): 0.7504 against 0.7511 before it. The core still toggles live.
- **The real grid core, same session pattern** (storm spawned, then `ShowAllLinks` with `FreezePulse`; refresh count 6 before and after). Mean luma over the 49,633 link pixels:
  - base 0.077;
  - links without the core 0.143;
  - with the core 0.293, repeated 0.293.
- **Conclusion.** Before the fix, a core attached but unlit at the last refresh did not help (grid spec T2 follow-up). Both tests reproduce exactly that condition, and the core now works. The per-callback `z_far` fix resolved the blocker; Sunshine reported `callback_z_far` = 838,861 throughout. **E4 therefore makes no `RefreshCloudCompute` call and no `RingSunshineCloudBridge` change**, so the bridge chain becomes E2 → E3 → E5. Keep `DepthWriteCore` as a setting.
- **Hitch.** The refresh hitch was not measured; it is not needed now. The CPU side of the call was 2.3 ms; the rebuild itself runs in the render callback.

**T3 result: the far viewport is tonemapped twice; draw the hub body in the main viewport.**
- **Measurement.** A screen-locked emission wedge (0.05 … 100) was drawn in the far viewport and in the main viewport. Each was compared in the far texture and in the final root image, at fixed exposure and at hub noon.
- **The far texture is LDR AgX.** The far `SubViewport` is RGBA8 (`use_hdr_2d` false on both viewports). Its values (0.235, 0.461, 0.671, 0.790, 0.878, 0.937, 0.973, 0.999, 1, 1) equal the main viewport's own single-AgX row (0.235, 0.463, 0.671, 0.792, 0.878, 0.937, 0.973, 1, 1, 1), so the two curves are the same.
- **The canvas background passes through the main post chain.** The far wedge in the final image reads (0.219, 0.461, 0.631, 0.700, 0.745, 0.768, 0.780, 0.791, 0.792, 0.792): that is main AgX applied to the linearised far texture.
  - With `tonemap_exposure` 2 on the main environment (set through `day_side_atmosphere.tonemap_exposure`; the script rewrites the environment every frame), the ceiling rises to 0.878.
  - With the main tonemap set to linear, the final far row equals the far texture exactly.
  - Main auto-exposure applies to it too: at hub noon the ceiling moves 0.792 → 0.847.
- **What that means.**
  - Any far-viewport pixel peaks at AgX(1 × main exposure), about 0.79-0.85: light grey, never white.
  - HDR above about 16 is lost in the far 8-bit stage.
  - Double AgX is close to identity at mid grey (0.18 linear → 0.461 → 0.461) and compresses only highlights, so dim far beams hand off to the near layer without a visible seam.
  - The sky (the far viewport's background) is also tonemapped twice. Tune the hub in-scatter in final-output space.
- **Hub body prototype** (ground, looking up, auto-exposure on; core `40 × L` white, radius `30 km × (0.5 + 0.5√min(L, 1))`, teal halo with a 400 km e-fold and peak `0.015 × core × L`):
  - **In the far viewport:** the disc centre is 0.847 at L = 1 and 2.5, against a sky of 0.70-0.75 around it. That is a faint grey dot, not a bright disc.
  - **In the main viewport** (proxy billboard at 0.5 × camera far = 600 km along the hub direction, scaled to the same angular size; same shader): the centre is 1.000 at L = 1 and 2.5, and 0.984 at the kindle (L = 0.1). The halo lifts the 24 px ring to 0.79 (L = 1) and 0.87 (L = 2.5), so the overload swell reads.
  - Core emission 40 is white at every L ≥ 0.1. Growth is carried by the radius and the halo, as E.1 intends.
- **Why a main-viewport proxy is safe.**
  - It is drawn with depth test on and `depth_draw_never`, so Sunshine still sees backdrop depth and composites near clouds over it (E.1's occlusion rule holds).
  - Main geometry overhead (spire interiors, overhangs) occludes it.
  - The hub is always within 1.82° of the zenith from the floor, so far clouds and terrain are never in front of it.
  - Crops: `hubmain_far_L100.png` against `hubmain_main_L100.png` in the run folder.
  - **This changes E.1** ("far viewport"); see user question 1.

**Sub-steps** (implementer-sized, Sonnet medium). Every step keeps hub-off frames unchanged: hub level 0 and no event gives bit or noise parity, using the E3 T1 method.

| Step | Deliverable | Files | Gates and tests | Capture shots | Serialisation |
|---|---|---|---|---|---|
| **E4a** Ribbon extraction | Move the generic ribbon helpers into `ring_beam_ribbon.gdshaderinc` as functions taking arguments, with no uniforms: view-space side vector and near-plane sub-segment clip; metres per pixel with `abs(PROJECTION_MATRIX[1][1])`; footprint widening and the energy-conserving gains; the core depth footprint (natural half-width, minimum pixels); the cross profile (Gaussian core plus exponential halo plus side edge). The grid keeps its uniform names and its behaviour. | new `materials/ring_beam_ribbon.gdshaderinc`; `materials/ring_emitter_grid_beam_common.gdshaderinc` | `emitter_grid_shader_parity_test` (windowed) PASS with an unchanged worst error; `emitter_grid_layer_test`, `emitter_grid_g1_test`. **Extraction parity:** a windowed A/B of the grid (`ShowAllLinks`, `FreezePulse`, night, 2 poses including the T2 storm pose) against frames from HEAD, by the E3 noise-baseline method. Expect bit identity. | none (prerequisite) | Touches `ring_emitter_grid_*`: not in parallel with E4d or any grid G5/G6 work |
| **E4b** Hub beams, near and far | `EclipseHubBeamLayer` (scene node "EclipseBeams", main camera): MultiMesh of 595 ribbons plus the depth-core `next_pass` (priority +1, the T2 pattern). `EclipseHubFarLayer` in the far viewport (built once from `ScaledPoint(crown)` to the origin, priority 5, no depth). Pure `FillHubBeamEndpoints` and the rebase rebuild (E.2). Fully GPU state: `INSTANCE_CUSTOM = (p_site, column, fire_τ, kind)` written at bind (eclipse change or timeline-settings change only); uniforms `ev_time` (τ as a float, not absolute seconds), `ev_marks`, `ev_level`, `ev_main_tip`. Tip climb, handoff fade, cut and retract in the shader, with a pure CPU twin `EclipseHubBeamMath`. Beams ignore B and day, with `DayVisibility` while HubDay > 0.5. Near/far handoff through `ring_emitter_grid_layers.gdshaderinc`. Settings and drawer section "beams and hub". Underground participant (one line). | new `scripts/Celestial/Eclipse/EclipseHubBeamLayer.cs`, `EclipseHubFarLayer.cs`, `EclipseHubBeamMath.cs`; new `materials/ring_eclipse_hub_beam_common.gdshaderinc`, `ring_eclipse_hub_beam.gdshader`, `…_core.gdshader` (`EB_CORE_PASS`), `…_far.gdshader` (`EB_FAR_LAYER`); `EmitterSiteFrameMath.cs` or `EmitterGridTopology.cs` (endpoints only); `GameplayEclipseEventSettings`/`Defaults`; `RingLiveTuningDrawer.Eclipse.cs`; `scenes/VoxelRingIntegrationTest.tscn`; `UndergroundParticipants.cs` | New headless `eclipse_hub_beam_layer_test`: 595 instances; every tip within 1 m of the hub point; tilt ≤ 1.519°; length 4,998 ± 2 km; custom data equal to `SiteFireTau`; world endpoints invariant across a floating-origin rebase; no per-frame per-instance writes (counted); rebuild time reported (≤ 0.3 ms target). CPU-shader parity for the tip/handoff/cut maths (new `tools/shaders/eclipse_hub_beam_parity.gdshader`, windowed, same harness as the grid parity). Settings smoke extended. Underground round trip shows the new participant. Hub-off parity. **T2 regression capture:** under the storm, hub beam core on against off without a refresh (the pillar pattern above); the core must recover at least 5× the halo-only strip light. | 4 (main fire, Far pose), 6 (nearest spire from 5 km), 7 (arch starburst at full) | After E4a. Settings, drawer and capture files are shared by every E4 step: E4b → E4c → E4d → E4e |
| **E4c** Hub body, sky in-scatter, halo, far-sun colour | **Hub body in the main viewport** (T3; user question 1): a camera-facing proxy at 0.5 × camera far along the hub direction (computed each frame from the canonical hub point in double precision), angular size preserved, `blend_add`, unshaded, depth test on, `depth_draw_never`, `fog_disabled`. Profile: `HubCoreEmission` 40 × L, `HubRadiusKm` 30 × (0.5 + 0.5√min(L, 1)) with a 2 px minimum radius, `HubHaloAmplitude` 0.6 at L = 1, `HubHaloRadiusKm` 400; E5 drives the radius and flicker. **Sky:** hub in-scatter phase in `sample_ring_sky_atmosphere` toward `hub_direction`, weighted by `hub_level × hub_day`; early return at `hub_level` 0. Tune it together with the body and `HubZenithHaloIntensity` (user decision after E3). **Far sun colour:** mirror `StarLight.LightColor` onto `FarRingSun` and the regional far sun (E3 open item c). | new `materials/ring_eclipse_hub_body.gdshader` and `scripts/Celestial/Eclipse/EclipseHubBody.cs` (or a child of EclipseBeams); `materials/ring_local_sky.gdshader`; `scripts/day_side_atmosphere.gd` (sky uniforms only); `RingScaledFarViewport.cs` (colour mirror; supersedes "E2 only" for this file); settings and drawer | `eclipse_hub_capture --mode=parity`, reference = the pre-E4c build, all 15 states within the noise baseline. The far-sun colour must equal the old white exactly at hub weight 0 (headless check of `light_color`). New noon gates in final-output space: at L = 1 the disc centre ≥ 0.98 and centre − sky at 24 px ≥ 0.15; at L = 0.1 the centre ≥ 0.9. Zenith sky blue/red ratio at L = 1 ≥ 0.9 × the real-noon ratio at the same pose. Hub hidden under a full Sunshine deck. `eclipse_hub_light_test` green. | 5 (kindle overhead), 7 (full noon, ground and 150 km), 9 (cloudy noon; disc hidden, hub-lit tops) | `day_side_atmosphere.gd` and `ring_local_sky.gdshader`: E3 → **E4c** → E5. `RingScaledFarViewport.cs`: E4c only. Not in parallel with E5 |
| **E4d** Event fronts, refuel flashes, grid clock | Grid z/w lanes: `eg_fronts[i] = (headQ, column, signedTravelled, plateau)`, with the legacy path exact at z = 0; `eg_interval_lit` window; CPU twin `PulseAtQ` and `CountLitLinks` branch. `EmitterGridBeamLayer.SetEventFronts(ReadOnlySpan<Vector4>)` appended after the normal fronts (≤ 24); `EventPeak`; `MaxHeadSprites` 12 → 24 (the far layer shares it); normal-front weight from `State.GridPulseWeight`; **grid time override:** the layer uses the director's `GridTimeSeconds` instead of `CloudSimulationTimeSeconds` while `GridClockOverrideActive`, then returns to the weather clock (E0/E1 notes). The director pushes the 12 ignition fronts (`ForwardHeadMeters`, `BackwardHeadMeters`, `PlateauLevel`, `MeetingFlare`). Refuel flashes in the hub beam shader from `eg_fronts`: age = (head − q_site)/v, using the `Flash*` settings. | `materials/ring_emitter_grid_pulse.gdshaderinc`, the grid head shaders; `scripts/Terrain/Sites/EmitterGridPulseClock.cs`; `scripts/poi/Emitters/EmitterGridBeamLayer.cs`, `EmitterGridFarLayer.cs`; `EclipseEventDirector.cs`; `ring_eclipse_hub_beam_common.gdshaderinc`; `tools/EmitterGridShaderFixture.cs`, `tools/shaders/emitter_grid_intensity_parity.gdshader` | Parity: z = 0 bit-identical to today (`emitter_grid_shader_parity_test` plus a CPU golden of `PulseAtQ`/`CountLitLinks`); new bidirectional and plateau cases. `emitter_grid_layer_test`: 24 heads, appended fronts, legacy count unchanged without events. Director test: the override feeds the layer only inside [MainFire, E) with `RefuelClock = Celestial`, and the normal pulse is suppressed (weight 0) from MainFire to the end of Handoff. A refuel flash's CPU twin time equals `Timeline.RefuelFlash` within one frame at TimeScale 1 and 10. Hub-off grid parity. | 6 (fronts climbing both ways), 8 (sustain row flash) | Grid files: after E4a, not in parallel with grid G5/G6. Hub beam include: after E4b |
| **E4e** In-cloud diffusion, underground, crown glow | In-cloud factor k per near site (≤ 700 km) from one `QueryLocalWeatherAtTime` at fire, at most 30 per eclipse. Carry it in MultiMesh `COLOR` (k, deck base, deck top, flags; `INSTANCE_CUSTOM` is full). The halo widens by × (1 + 3k) over the deck span. **Tuning point:** consider suppressing the depth core over the deck span when k > 0.5, so Sunshine blends the in-cloud segment and the widened halo stands in for scattering. Optional stand-in crown glow on `INSTANCE_CUSTOM.b` (written on fire, handoff and cut only). Near POI bore light boost. Far-cloud glow stays off (G5). | `EclipseHubBeamLayer.cs`, the hub beam include; `scripts/poi/Emitters/EmitterStandInLayer.cs`, `materials/ring_emitter_standin.gdshader`; settings and drawer | k query count ≤ 30 per eclipse (counted). Crown-glow writes = 595 × 3 per eclipse, and 0 per frame. `emitter_far_standin_test` green. Stand-in output unchanged at `.b` = 0 (hub-off parity). Underground round trip. | 9 (cloudy, diffusion near a spire), 6 | Stand-in files: not in parallel with POI work; after E4b |

**Notes for all steps.**
- **Budgets (G.7).** Measure only with no other Godot process running, and on the A1000 for the written budgets (4070 numbers are reference only).
- **Capture route.** Grow `tools/eclipse_hub_capture.gd` with a `--mode=visuals --shots=…` route. Each step adds its shots, serialised in step order; the full G.6 route remains E7.
- **Far beams in final-output space.** Far-layer beams are tonemapped twice (T3). Judge `FarCompensation` 0.45 and the far emission in final-output space.
- **Pre-existing gap.** `EmitterGridBeamLayer` has `UndergroundHold`/`SetUndergroundHidden` but is **not** registered in `UndergroundParticipants` (only the stand-ins and the far viewport are), so the grid draws underground today. E4b can register the grid next to the hub beams (user question 3).

**User questions (E4).**
1. **Hub body in the main viewport (changes E.1).** The recommendation is a main-viewport proxy billboard: a white disc that clouds and overhead geometry still hide. The alternative is to keep it in the far viewport as written and accept a grey dot that caps at about 0.85 against a 0.74 sky.
2. **Far-viewport double tonemap.** Recommended: leave it as it is in E4, and tune the far beams and the sky in-scatter in final-output space. Fixing it means making the far texture linear HDR with a linear far tonemap, plus HDR 2D on the root. That changes every far pixel (the arch, the sky and the far clouds) and the goldens, so it would be its own task.
3. **Register the grid as an underground participant** in E4b (a one-line fix of a pre-existing gap; it changes the grid's behaviour underground). Recommended: yes.

**E4a implementation notes (2026-10-17).**
- **Moved into `materials/ring_beam_ribbon.gdshaderinc`** (pure `ribbon_*` functions, no uniforms or varyings; it includes `ring_atmosphere.gdshaderinc`):
  - `ribbon_clip_near` (near-plane sub-segment clip; takes the near distance as an argument, because the grid's `near_clip` uniform lives in the layers include);
  - `ribbon_side_vector`, `ribbon_meters_per_pixel` (`abs(PROJECTION_MATRIX[1][1])` passed in);
  - `ribbon_profile` (footprint widening to `core_eff`/`halo_eff` and the energy-conserving gains, packed as `vec4`);
  - `ribbon_depth_half`, `ribbon_half_width_core`, `ribbon_half_width_halo`, `ribbon_depth_core_covers` (the depth-core pattern);
  - `ribbon_cross_profile`, `ribbon_edge`, `ribbon_atmosphere_visibility` (fog attenuation, `1 - atmosphere.a`).
- **Stays in the grid include:** every uniform and varying, day/background/layer-weight/pulse logic and the lit test, so the near, core and far shaders keep their uniform names and behaviour. Include parameters must not reuse global uniform names (a parameter called `near_clip` was a redefinition error).
- **Parity (4070, 1600x900, windowed, `ShowAllLinks` + `FreezePulse`):** 10 states (night, day, hub noon x ground / 9 km down / 150 km arch with the far layer, plus the T2 storm pose), each shot twice, HEAD against the extraction. All pass the 2x noise gate; frames are not bit-identical (cloud temporal noise: the HEAD a/b pair alone differs by 500-156,000 pixels), and the HEAD-vs-edit differing-pixel counts match the HEAD a/b counts.


**E4b implementation notes (2026-10-17).** Spire to hub beams, near and far. Base: 17b02dc plus the uncommitted E4a. Hub-off frames are unchanged (parity below).

Files:
- `scripts/Celestial/Eclipse/EclipseHubBeamLayer.cs` (scene node "EclipseBeams", main camera), `EclipseHubFarLayer.cs` (far viewport), `EclipseHubBeamMath.cs` (pure CPU twin), `EclipseBeamSettings.cs` (look settings record, nested in `EclipseEventSettings.Beams`).
- Shaders: `materials/ring_eclipse_hub_beam.gdshader` (halo), `..._core.gdshader` (`EB_CORE_PASS`), `..._far.gdshader` (`EB_FAR_LAYER`), `ring_eclipse_hub_beam_common.gdshaderinc` (vertex and fragment, built on the E4a `ribbon_*` API and `ring_emitter_grid_layers.gdshaderinc`) and `ring_eclipse_hub_beam_math.gdshaderinc` (pure state math: tip, handoff, cut, soft tip, day visibility, level).
- `EmitterGridTopology`: `HubBeamCount`, `HubBeamSite(b)`, `HubBeamCrown(b)`, `HubAcrossMeters`, `FillHubBeamEndpoints(snapshot, crown, out hub)` (beam 0 = the main spire, then the regular spires in chain-link order, which is the `EclipseSiteTable` order; one absolute `WorldPosition` per crown plus one hub point, so 596 evaluations, not 1,190). `EmitterSiteFrameContext.HubWorldPosition`.
- Persistence and drawer: 17 fields on `GameplayEclipseEventSettings`/`Defaults` (`BeamsEnabled`, `BeamFarLayerEnabled`, `BeamDepthWriteCore`, `BeamCoreWidthMeters` 150, `BeamHaloWidthMeters` 1,200, `BeamMainCoreWidthMeters` 300, `BeamMainHaloWidthMeters` 2,400, `BeamMinPixelWidth` 1.5, `BeamFarCompensation` 0.45, `BeamBaseEmission` 12, `BeamMainEmissionScale` 1.5, `BeamHaloAmplitude` 0.12, `BeamBaseFadeMeters` 400, `BeamDayVisibility` 0.6, `BeamCoreDepthThreshold` 0.35, `BeamCoreDepthMinPixels` 4, `BeamSoftTipFraction` 0.005), mapped in `ToCore`; drawer section "Eclipse event: beams and hub" with a live status line. A beam-only change does not rebuild the timeline (`EclipseEventEngine.SetSettings`, like darkness).
- `UndergroundParticipants.cs`: `EmitterGridParticipant` and `EclipseHubBeamsParticipant` (group FarTerrain, hold on Suspend, hide on Hide, like the stand-ins). User decision 3: the grid had hide hooks but was never registered.
- `EclipseEventDirector`: capture hooks `TimelineMarkTau(name)` and `SiteFireTauSeconds(site)`. Scene: `EclipseBeams` node next to `EmitterGrid`.

**Design.**
- **State on the GPU.** `INSTANCE_CUSTOM = (p_site, column, fire_tau, kind)` is written once per timeline object (a new eclipse or a timeline-setting change) by `BindTimeline`; kind 1 is the main spire; a dead site (`SiteAlive` false) gets fire tau 1e9 (never). Per frame the layer writes only uniforms: `ev_time` (tau as a float32; ulp 0.25 ms near the collapse), the camera, and values that changed. `ev_marks` = (handoff start, handoff fade, cut tau, retract), `ev_climb` = (main climb, main exponent, regular climb, regular exponent), `ev_level` (global gain, 1 until the collapse stage), `ev_shape` = (soft tip, day visibility).
- **Beam model** (`ehb_*`, twin `EclipseHubBeamMath`): `tip = min(climb, cut_top)`, `climb = clamp((tau - fire)/climb_seconds)^exponent` (main: `MainClimbExponent`; regular: 1), `cut_top = 1 - clamp((tau - cut)/retract)` (top-down), regular beams fade by `1 - smooth01((tau - handoff)/fade)`, soft tip 0.5 % (a complete beam is never cut), and sub-segments above the tip collapse in the vertex stage. `level = handoff x tip mask x day visibility x ev_level x atmosphere x layer weight`. The beams ignore B and the day factor: the atmosphere is sampled with background 1 (only its opacity attenuates), and the only day term is `DayVisibility` 0.6 while `ringworld_hub_day` > 0.5. Colour is beam teal mixed to white by the core share.
- **Ribbon mesh.** A hub beam is 5,000 km long, so uniform sub-segments of 312 km would be useless inside the 700 km near range. The 16 sub-segments are spaced `(k/16)^3`: the first is 1.2 km, the 8th ends at 625 km. The same mesh feeds the far layer (about 38k vertices).
- **Layers.** Near: halo material priority 0 with the depth core as `next_pass` (priority 1), +2 draw calls. Far: one MultiMesh in the scaled space from `ScaledPoint(crown)` to the origin (the hub), priority 5, never writes depth, +1 draw call. The per-pixel handoff uses `ring_emitter_grid_layers.gdshaderinc` with the near range and cloud band resolved exactly as the grid does (`EmitterGridBeamLayer.ResolveHandoffBand`, grid settings for `NearLayerMaxKm` and the manual band).
- **Existence.** The layer builds `PrebuildSeconds` (10 s) before the main fire, is visible while `State.MainBeamLit`, and is freed after the retract (tau > implosion + retract + 1 s). Outside that window (every non-eclipse frame, the PreRoll, the Darkness beat) it is not built, not drawn and costs one comparison. The hub-level debug override does not show beams.
- **Rebase.** `HandleAnchorChanged` rebuilds the 595 transforms absolutely (0.11-0.14 ms steady, zero managed allocation, headless); custom data and materials are untouched.

**Deviations from the plan.**
1. **Tilt and length.** The work order's tilt <= 1.519 deg and length 4,998 +- 2 km do not hold for the seed-1337 registry: the widest regular site is 136.4 km off the centreline, so the maximum tilt is **1.5634 deg** (analytic bound for that offset 1.5643 deg) and beam lengths are 4,994.9-5,000.1 km (pad heights up to about 3 km). The test derives the bound from the registry (`atan(widest offset / radial distance)`), cross-checks every tilt against the independent `EmitterSiteFrameContext.Beam`, and asserts the main spire's tilt is < 0.01 deg and every tip is within 1 m of the hub (0.000 m measured).
2. Two includes instead of one: the pure math (`ring_eclipse_hub_beam_math.gdshaderinc`) is separate so the canvas_item parity shader can include it without the spatial built-ins.
3. No `ev_main_tip` uniform: the shader computes the main tip from the main beam's fire tau like every other beam. `ev_level` is a plain global gain for now.
4. `DayVisibility` is smoothed over hub day 0.45-0.55 (a hard step at 0.5 would pop during the ramp). The regular climb is linear (exponent constant `EclipseHubBeamMath.RegularClimbExponent` = 1).
5. Additions after the first captures: the halo fades in over `BeamBaseFadeMeters` (400 m) above the crown (without it the halo starts with a hard horizontal edge, see `s6_complete`), and the near/far halo strip is 6 e-folds wide instead of the grid's 4 (the beams are far brighter than the grid links, so a 4 e-fold edge showed as a vertical step). Both are in the drawer.
6. Far layer hub = the scaled origin literally (spec E.1), not `ScaledPoint(radius, W/2)`.

**Tests (all PASS).**
- `tools/eclipse_hub_beam_layer_test` (headless; `EclipseHubBeamLayerTest.cs`): count 595, crown/hub endpoints, tilt, custom data against `SiteFireTau` and `P`/column/kind (and a dead site), build window (`ShouldBuild`), CPU twin against `Evaluate` (main tip, handoff, lit regular count at 16 times), rebase (transforms +595, custom data +0, same instance and multimesh ids, beam vector invariant, absolute round trip, 0 bytes allocated), steady state (600 frames of `ApplyEventTime`: 0 transform, 0 custom-data and 0 timeline writes, near and far), settings reaching both materials, far layer (595 beams, tips at the scaled origin, writes counted), underground round trip with the factory participants (hold, hide, restore, `Describe` and retained ids equal), inert without an event.
- `tools/eclipse_hub_beam_shader_parity_test` (windowed GPU; `EclipseHubBeamShaderFixture.cs`, `tools/shaders/eclipse_hub_beam_parity.gdshader`): 8 cases (pre-fire, main climb, ignition, regular climb, handoff, sustain at gain 0.7, retract, after cut), 2,048 samples each concentrated around the tips and the 0.45-0.55 day band: worst absolute error tip 2e-7, handoff 1e-7, tip mask 3.7e-5, level 2.5e-5 (tolerance 4e-3).
- `eclipse_event_settings_smoke` extended: the 17 beam fields round-trip, clamp, fall back on NaN, map to the core record without drift, and an older save with no beam fields loads the defaults.
- Regression: `emitter_grid_shader_parity_test` (all 5 cases, unchanged worst error), `emitter_grid_g1_test`, `emitter_grid_layer_test`, `emitter_grid_settings_smoke`, `emitter_far_standin_test`, `eclipse_event_director_test`, `eclipse_event_timeline_test`, `eclipse_event_stream_test`, `eclipse_window_solver_test`, `eclipse_darkness_test`, `eclipse_hub_light_test`, `eclipse_darkness_parity_test`, `celestial_eclipse_smoke_test`, `underground_mode_smoke_test`, `underground_mode_settings_smoke_test`, `world_loading_startup_contract`; windowed `eclipse_darkness_capture` passes all 9 gates.
- **Hub-off parity (`eclipse_hub_capture --mode=parity`, reference `base1`/`base2`, RTX 4070):** all 15 states PASS (13 inside the 2x noise-baseline gate, `main_high_night` and `main_high_totality` bit-identical). The Darkness-phase states sit before the prebuild window, so no beam object exists in them.
- **Storm check (`--mode=visuals --shots=storm`, T2 method on the real beams, no cloud refresh while lit: 9 to 9):** a regular spire 3 km ahead (Far viewpoint set to 3 km), storm front spawned, beams off / halo only / halo + depth core, gain measured over the pixels the beam changes. With the clouds covering the beam the halo-only light is about 0 (it is even 0.01 below the base from cloud noise), so the recovery is taken against `max(halo gain, 0.005)`: **11.8x at full noon** (all 595 lit, 15,111 px; core gain +0.059) and **51.7x with the hub dark** (22 lit; 6,566 px; core gain +0.259). A first run in which the camera was 20-40 km from the beam showed nothing at all: the storm's cloud is then in front of the beam, which the depth core cannot fix by design.

**Captures** (`scratchpad\e4b\captures`, 1600x900, clock paused, director scrub; also `results.json` and the log). Reads:
- Shot 4, main spire Far pose (40 km): `s4_main_climb_mid.png` (tau 22.4, the pillar leaves the spire and runs off the top of the frame, grid links fan out below it), `s4_main_full_dark.png` (kindle, hub 0.1), `s4_main_sustain.png` (hub noon, a white pillar through the sky). The main pillar reads as a thick white core with a teal halo.
- Shot 6, nearest regular spire from 5 km: `s6_before.png` (tau 30.5, dark spire), `s6_climb_mid.png` (tau 33.3), `s6_complete.png` (tau 35.5), `s6_complete_late.png` (tau 44.5, 60 lit). The beam leaves the crown with a soft base, the halo is about 150 px wide, and neighbouring beams show as thin hair-lines rising from the horizon. The fronts climbing the arch both ways are E4d.
- Shot 7, full noon with all 595 lit (`s7_ground_up`, `s7_ground_arch`, `s7_ground_zenith`, `s7_alt_arch`, `s7_alt_zenith`, each with a `_nobeams` reference): from the ground the beams are an hourglass of thin white lines converging on the zenith point (the arch is a narrow stripe, so the bundle is narrow: all spires lie within 1.6 deg); from 150 km the same bundle reads as a white spike on black. Mean luma with beams against without: ground 0.642 against 0.627 (+2 %), 150 km 0.089 against 0.031. The beams are not too bright against the noon sky (they are cool white on pale blue-grey), but they are 2 px wide everywhere (minimum pixel width); the hub body (E4c) will sit where they meet.
- Storm: `storm_noon_{base,halo_only,core,core2}.png`, `storm_dark_...`: the halo-only frame shows no beam, the core frame shows the pillar through the storm above the spire.

Not measured (budgets are for later): GPU time. CPU side: rebuild 0.11-0.14 ms headless, 0.15-0.43 ms in the windowed game; bind = 595 custom-data writes.

**Open items for E4c/E4d.**
- E4c: the beams meet at the hub, so the hub body proxy should be tuned with them (the converging point shows the far arch through it). Beam colour is not yet tied to hub level. The far starburst is tonemapped twice (T3): the 2 px lines hold up, but `BeamFarCompensation`/emission were judged only in the captures above, not against a measured final-output target.
- E4d: refuel flashes must light the regular beams during Sustain (today `ehb_handoff` is 0 for them after the handoff and the vertex collapse removes them); the custom data already carries `p_site` and `column` for the `eg_fronts` lookup. `ev_level` is the hook for the instability/overload gain (E5).

**E4c implementation notes (2026-10-17).** Hub sun body (main-viewport proxy), sky in-scatter, zenith halo tuning and the far-sun colour mirror. Base: 17b02dc plus the uncommitted E4a and E4b. Hub-off frames are unchanged (parity below).

Files:
- `scripts/Celestial/Eclipse/EclipseHubBody.cs` (scene node "EclipseHubBody", next to "EclipseBeams"), `EclipseHubBodySettings.cs` (record nested in `EclipseEventSettings.HubBody`, plus the pure `EclipseHubBodyMath`), `materials/ring_eclipse_hub_body.gdshader`.
- `materials/ring_local_sky.gdshader` (hub in-scatter, uniforms `hub_in_scatter`, `hub_scatter_color`), `scripts/day_side_atmosphere.gd` (publishes `hub_in_scatter` with the other hub sky uniforms, reset with them), `scripts/Voxel/LOD/RingScaledFarViewport.EclipseAmbient.cs` plus one call in `RingScaledFarViewport.cs` (far sun colour), `EclipseHubLighting` (static hub colour next to the static weight, published by the director), `EclipseEventDirector.HubSkyInScatter`.
- Persistence and drawer: `GameplayEclipseEventSettings`/`Defaults` gain `HubBodyEnabled`, `HubBodyCoreEmission` 40, `HubBodyRadiusKm` 30, `HubBodyMinRadiusPixels` 2, `HubBodyHaloAmplitude` 0.6, `HubBodyHaloRadiusKm` 400, `HubSkyInScatter` 1, mapped in `ToCore` to `HubBody`. A hub body change does not rebuild the timeline (`EclipseEventEngine.SetSettings`, like darkness and beams). Drawer: the "beams and hub" section gets a live body status line, a "Hub sun body" toggle and six sliders; the "Zenith halo" slider already existed.
- `UndergroundParticipants.cs`: `EclipseHubBodyParticipant` (group FarTerrain, hold on Suspend, hide on Hide). Scene: `EclipseHubBody` node.

**Design.**
- **Body.** One camera-facing QuadMesh placed each frame at 0.5 x camera far along (hub point - camera), the hub point being `EmitterSiteFrameContext.HubWorldPosition(W/2)` evaluated in double and rebuilt on `CanonicalFloatingOriginAnchorChanged` or a grid transform change (zero evaluations in steady frames). Settings are real km at the hub and are multiplied by proxy distance / hub distance, so the angular size is exact (30 km = 0.34 degrees at 5,000 km). Shader: `blend_add`, unshaded, `depth_draw_never`, depth test on, `fog_disabled`, render priority 20, `cull_disabled`. The billboard faces the camera ray (not the view axis), so the disc keeps its angular shape off the screen centre. Profile: white core `CoreEmission` 40 x L with 15 % limb brightening and a 2 px minimum radius, plus an exponential teal halo (amplitude 0.6 x L, e-fold 400 km) with a smooth window to 0 at the quad edge (the quad is 5 e-folds half-width). Core radius is `30 km x (0.5 + 0.5 sqrt(min(L, 1)))`; at L > 1 only the emission and the halo grow (the overload swell, E5 drives radius and flicker). Visible while the director's hub level is above 0 and hub weight above 0 (including the debug override); hidden when disabled, underground, or with no emitter topology to take the ring width from. It ignores B and the day factor like the beams.
- **Occlusion.** The quad never writes depth, so Sunshine near clouds composite over it and any main-view geometry in front fails the depth test (gates below).
- **Sky in-scatter.** `ring_local_sky.gdshader` adds `hub_scatter_color` (saturated blue, 0.04 0.20 0.55) x Rayleigh-like phase `0.35 + 0.65 cos^2` toward `hub_direction` x `hub_in_scatter` x min(level, 1.5) x hub day to the palette sky, inside the `hub_level > 0` branch (nothing runs at level 0).
- **Far sun colour.** `RingScaledFarViewport.ResolveFarSunColor(weight, hubColour)`: exactly `Colors.White` for weight <= 0 or non-finite, else white lerped to the hub colour by the weight; applied to `FarRingSun` and, when it exists, `FarRingRegionalSun` (only on change). The regional sun is not created in the capture world.

**Deviations from the plan.**
1. **The in-scatter is not inside `sample_ring_sky_atmosphere`.** A first version put the hub phase in that function (a `sky_hub_scatter` weight added to the shell colours). It changed the sky by less than a thousandth: the air shell's opacity (a few percent at the zenith) times the 0.26 scattering energy leaves almost nothing, and the shared include is untouched now. The term lives in the sky shader as a palette addition.
2. **Two defaults changed to meet the sky gate:** `HubSkyTintStrength` 0.25 to 0.10 and `HubZenithHaloIntensity` 0.35 to 0.15. The sweep (ground zenith pose, blue/red of two side patches against real noon, 93.9 % at the chosen values): tint 0.25 gave 82 to 83 % whatever the in-scatter or halo (the near-white lean desaturates the sky), tint 0.10 with in-scatter 0 gave 91.6 %, in-scatter 1 adds a further 2 points; the wide white halo skirt cost about 1 point. The in-scatter colour had to be a saturated blue: the same term in the atmosphere day colour (0.34 0.46 0.60) lowered the ratio.
3. **No `HubSkyInScatter` or body toggles for the far viewport** (the body is not drawn there); the far-sun mirror has no switch (the hub colour is already tunable).
4. Min radius uses pixels at the proxy plane (`PROJECTION_MATRIX[1][1]` and `VIEWPORT_SIZE.y`), not the far viewport.

**Gates (RTX 4070, 1600x900, windowed, final image; `--mode=visuals --shots=gates`).** Pose ground zenith (pitch 89), override hub level at totality, auto exposure settled 8 s.
- Disc at L = 1: centre luma 1.0000 (rgb 1.000 1.000 1.000), need 0.98. Centre minus the 24 px ring 1.0000 - 0.7888 = 0.2112, need 0.15 (the sky without the body at the ring is 0.6765, so the halo adds 0.11). White (above 0.9) radius 4 px.
- Kindle (L = 0.1): centre 0.968 (0.967 0.968 0.968), need 0.90; white radius 2 px, ring at 24 px 0.5695: a small white point.
- Sky blue/red, hub L = 1 over real noon (same poses, two patches each): ground zenith 1.290 against 1.374 (93.9 %), ground looking up 1.315 against 1.391 (94.5 %); need 90 %.
- Far suns: (1, 1, 1, 1) exactly at hub weight 0; (0.92, 0.97, 1.0, 1.0) at weight 1 (override).
- Occlusion (body on minus body off at the disc centre): clear sky +0.3931 (visible), behind a 600 m slab 150 m overhead +0.0000, under a storm front centred on the camera +0.0000.
- Hub-off parity (`--mode=parity --ref=base1 --ref2=base2`): all 15 states PASS (13 inside the 2x noise gate, `main_high_night` and `main_high_totality` bit-identical).
- Tests (all PASS): `eclipse_hub_body_test` (new: far sun colour white at weights 0, -0, -1, NaN, -inf, hub colour at 1, director publishing; core radius 15 / 30 km, kindle 19.5 km, emission 40 x L; node inert without a hub light; placement at 0.5 x far along the direction to the hub, cross-checked against the beam layer's hub point; uniforms in proxy metres; overload; camera far change; rebase (one evaluation per anchor change, none in 120 steady frames); disable switch; underground participant round trip), `eclipse_event_settings_smoke` (extended: the seven fields round-trip, clamp, NaN fallback, core mapping, an older save loads the defaults, default drift), `eclipse_hub_light_test`, `eclipse_hub_beam_layer_test`, `eclipse_event_director_test`, `eclipse_event_timeline_test`, `eclipse_event_stream_test`, `eclipse_window_solver_test`, `eclipse_darkness_test`, `eclipse_darkness_parity_test`, `celestial_runtime_integration_smoke_test`, `emitter_grid_g1_test`, `emitter_grid_layer_test`, `emitter_grid_settings_smoke`, `emitter_far_standin_test`, `underground_mode_smoke_test`; windowed `eclipse_hub_beam_shader_parity_test` (8 cases, worst tip error 2e-7), `emitter_grid_shader_parity_test` (5 cases, unchanged worst error), `eclipse_darkness_capture` (all gates PASS).

**Captures** (`scratchpad\e4c\g2`, 1600x900, clock paused, hub weight 1; `--shots=s5,s7,s9,gates`). Reads:
- Shot 5, `s5_kindle_overhead.png` (tau 26.8, looking at the zenith from the ground): the main pillar rises from below and ends in the white point at the hub, in a soft halo; stars around, the arch strip above. `s5_before_kindle.png` (tau 21): dark, only the pillar tip climbing.
- Shot 7, `s7_ground_zenith_beams.png` (and `_up`, `_arch`, `_alt_arch`, `_alt_zenith`, each with a `_nobeams` reference): from the ground the beams are an hourglass converging on the hub disc, which sits in a broad cool halo on a blue-grey noon sky; from 150 km (`s7_alt_zenith_beams.png`) a bright white disc and halo on black with the bundle. The disc is no longer a hole in the beams: the converging point reads as one source.
- Shot 9, `s9_ground_under_deck.png` (storm centred on the camera): the deck is dark and hides the disc and the beams (the same result as the occlusion gate). `s9_alt9km_tops.png` (9 km, looking down at a storm): the cloud tops read dim blue-grey with the beam bundle and the hub pillar through the arch; the tops are not bright at the fixed exposure used here (a cloud-lighting matter of the E3 hub light, not of the body).
- Gate frames: `gate_ground_zenith_realnoon.png` (blue sky with the sun and the giant), `gate_ground_zenith_hub100.png` (hub-lit sky, disc at the arch), `..._hub100_nobody.png`, `..._hub010.png`, `gate_occlusion_{slab,storm}_{on,off}.png`.

Not measured: GPU time (no timings were taken); the body is one additive quad (about 5 e-folds across, so up to a large part of the screen near the hub) plus the in-scatter branch in the sky shader.

**Open items for E4d/E4e.**
- E5 drives the body: radius (`1 - u^2` at the implosion), the flicker on the halo amplitude and the overload swell; `EclipseHubBodyMath` and the uniforms are the hooks (level only today).
- The hub-lit cloud tops from altitude read dim (shot 9); if wanted, that is the E3 lighting (`HubDayAtFull`, cloud ambient), not E4c.
- The body does not depend on the beam layer; the beams still show the main pillar through the disc at the kindle (`s5_kindle_overhead`): the pillar tip and the disc coincide by design.
- Sky in-scatter colour is a shader uniform default (`hub_scatter_color`), not yet in the drawer.

**E4d implementation notes (2026-10-17).** Grid ignition fronts (z/w lanes), the normal-pulse suppression, the grid-time override and the Sustain refuel flashes. Base: 17b02dc plus the uncommitted E4a, E4b and E4c. The legacy grid is unchanged outside the event (parity below).

Files:
- `materials/ring_emitter_grid_pulse.gdshaderinc`: the `eg_fronts` z/w lanes, the new `eg_force_night` uniform, `eg_event_front_at_q` and the event branches of `eg_pulse_at_q` and `eg_interval_lit`.
- `scripts/Terrain/Sites/EmitterGridPulseClock.cs`: the CPU twin (`EmitterEventFront`, `MakeEventFront`, `EventFrontAtQ`, `PulseAtQ`/`PulseAtLink`/`CountLitLinks` overloads taking the event fronts, `IsLinkLit`, `EventFrontWindow`). The legacy methods are untouched.
- `scripts/poi/Emitters/EmitterGridBeamLayer.cs`: `MaxHeadSprites` 24 (the far layer shares it), `SetEventFronts`, `EventPeak`, `NormalPulseWeight`, `SetGridClockOverride`, plus read-only `EventFrontCount`, `EventWeight`, `ShaderFronts`, `GridClockOverridden`, `GridTimeOverrideSeconds`, `LookWriteCount`.
- `scripts/Celestial/Eclipse/EclipseGridEventFronts.cs` (new, pure): the lanes from the timeline and the published state. `EclipseEventDirector.ApplyGridState(engine, layer)` pushes them every frame (and `_ExitTree` resets the layer). Capture hook `SiteRefuelTauSeconds(site, notBeforeTau)`.
- Hub beams: `materials/ring_eclipse_hub_beam_math.gdshaderinc` (now includes the grid pulse include: `ev_flash`, `ev_refuel`, `ehb_refuel`, `ehb_lit_refuel`, `ehb_level_refuel`), `ring_eclipse_hub_beam_common.gdshaderinc` (the vertex stage lets a flash through the collapse and passes `v_flash`), `EclipseHubBeamMath` (twin: `RefuelFlash`, `IsLitRefuel`, `LevelRefuel`; the uniform record gains `Flash` and `Refuel`), `EclipseHubBeamLayer` (publishes the normal fronts while a flash can be visible).
- Setting `BeamGridEventPeak` (core `Beams.GridEventPeak`, default 1.5, 0.25..4), persisted and in the grave-key drawer section "Eclipse event: beams and hub" as "Ignition front peak". No other new setting: the plateau level, the meeting flare, the Q3 suppression switch and the `Flash*` times already existed.

**Design.**
- **Lane layout.** `eg_fronts[i] = (headQ, column, signedTravelled, plateau)` in the direction-adjusted q. A front with z = 0 takes the legacy path exactly. Otherwise `b = sign(z) (head - q)` and the contribution is `max(profile(b), |w| step(0, b) step(b, |z|))`. `MakeEventFront` converts a p-space front (forward = toward +p, backward = toward -p) for either grid direction; the retrograde grid mirrors both the head and the travel sign. A travelled distance is never 0 (minimum 1 m), because z = 0 would be a normal front.
- **Fronts written by the director.** Per chain a forward and a backward front (12) from Ignition while the heads move. When a head reaches C/2 it is absorbed and becomes headless (see deviation 1) at the plateau level, and one meeting-flare span per chain (headless, plateau = `State.MeetingFlare`, one link either side of C/2, i.e. 12 links) is added for `MeetingFlareSeconds`: 12 + 6 = 18 lanes at most. Nothing is written outside [Ignition, Sustain). The plateau fades with the hand-off (`State.PlateauLevel`).
- **Layer.** The event fronts are appended after the normal ones and keep their slots (24 in all; the normal fronts give way); only fronts that still have a head get a head sprite (so 12 sprites climb the arch both ways). `NormalPulseWeight` 0 (`State.GridPulseWeight`, MainFire to the end of Hand-off) removes the normal fronts and their sprites. The weight is binary in the timeline, so it is not blended.
- **Event look.** `EventWeight` (the hand-off fade of `State.RegularBeamIntensity`, 1 through the ignition) multiplies the night peak by `EventPeak` and sets `eg_force_night`, which scales the background B to 0 in `eg_background` (`b * (1 - eg_force_night)`, exact at 0). Both are written only when they change (`LookWriteCount`), so a normal frame writes nothing extra. The head sprites use the same `eg_background`.
- **Grid-time override.** `ApplyGridState` calls `SetGridClockOverride(GridClockOverrideActive ? GridTimeSeconds : null)`; `CurrentTime` returns it first (the debug scrub offset is still added). It is on from MainFire to E with the Celestial refuel clock and off in Return and Idle, which are back on the weather clock.
- **Refuel flash (GPU).** `ehb_refuel(fire_tau, kind, p_site, column)`: the latest normal front on the column that has passed the spire's q gives `age = (head - q) / v`; `pass_tau = ev_time - age` must lie in [Sustain, Instability); the envelope is the `Timeline.RefuelFlash` envelope (smooth rise over `FlashClimb`, hold, fade) and the bolt's tip fraction is `age / FlashClimb`. The beam level is `max(own, envelope x tip mask)`, so a regular beam is relit after the hand-off, and the vertex stage keeps the sub-segments below the bolt's tip. The main spire and dead sites (fire tau never) never flash. With no flash the level is bit-identical to the E4b level (tested over 4,000 random inputs).
- **Fronts for the flash.** The hub layer computes the normal fronts itself from the pulse clock at `Engine.GridTimeSeconds` and writes `eg_fronts`/`eg_front_count` to its own materials (near, core, far) only from Sustain to the end of the last flash before Instability (plus the flash length); outside that window it writes the count 0 once. They therefore do not depend on the grid layer's update order or on its suppression. `eg_direction`, `eg_circumference` and `ev_flash`/`ev_refuel` are written once per clock or timeline.

**Deviations from the plan.**
1. **Headless fronts (negative `w`).** The plan has w = plateau. A front whose head is absorbed at the antipode must stop drawing the head profile (otherwise a 261 km bright blob stays at p = C/2 until the hand-off), and the meeting flare needs a plateau span with no head. A negative w means "headless": only the plateau span |w| is drawn and the collapse window has no head part. z = 0 and the legacy path are not affected.
2. **The meeting flare is six extra headless spans** (one per chain, plateau = `MeetingFlare`, smooth 1 to 0 in 1.5 s), not a change of the plateau of the existing fronts. It sums with the 0.35 plateau (clamped to 1).
3. **EventPeak is a multiplier of the night peak** (applied to `eg_visibility.x` while the event look is on), not a separate lane or per-front gain. If `SuppressGridPulseDuringIgnition` is off the normal pulse is brightened too.
4. **The grid layer does not feed the flashes**; the hub layer computes its own normal fronts (see Design). The hub math include now includes the grid pulse include and reuses the `eg_*` uniform names.
5. **Flash climb.** The flash is a bolt climbing the beam over `FlashClimbSeconds` and then a full beam (the plan only had the envelope); the envelope itself is exactly `Timeline.RefuelFlash`.
6. **`State.GridTime` is always the celestial clock** with the Celestial refuel clock, even outside the override window (the timeline does not know the window). The layers and the hub flash use `Engine.GridTimeSeconds`, which is correct in every phase.
7. **Parity extensions beyond the plan.** The grid parity shader has a fifth output field (the vertex collapse rule `eg_interval_lit` on the link interval, exact) and the hub parity shader six (adds the flash envelope and tip); both runners use three sample rows.
8. **CPU golden of the legacy maths.** The bit patterns and lit-link counts in `EmitterGridLayerTest` (`legacy_golden`) were printed after the change. The legacy methods `PulseAtQ`, `PulseAtLink`, `CountLitLinks` and `GetFronts` are textually unchanged (the change only adds code), and the empty-event overloads equal them bit for bit over 4,000 random points; so the golden pins the maths from now on.

**Tests (all PASS; RTX 4070).**
- `emitter_grid_shader_parity_test` (windowed): the five existing cases have exactly the same errors as with the pre-change include (checked by running the same runner against `git show HEAD:` of `ring_emitter_grid_pulse.gdshaderinc`: pulse 1.07e-4, intensity 3.8e-5 at the worst, and the old include fails the first event case, as it must), and four new ones pass: `ignition_bidirectional` (12 moving fronts with heads and plateau, 18 lanes), `ignition_bidirectional_retro`, `ignition_plateau_absorbed` (headless spans plus the meeting-flare spans, 24 lanes) and `ignition_plateau_absorbed_retro` (worst pulse 1.05e-4, intensity 1.4e-4, the collapse rule exact in every case).
- `emitter_grid_layer_test` (extended in `EmitterGridLayerEventTest.cs`): legacy golden, empty-event overloads, CPU event maths (bidirectional, plateau, headless, retrograde mirror, collapse rule), 24 head sprites and appended fronts, suppression, overflow, no extra uniform writes in normal frames (`LookWriteCount`), the override time on a real layer update with a camera, and the director's push over a real engine and layer at TimeScale 1 and 10.
- `eclipse_event_director_test` (extended in `EclipseEventDirectorGridTest.cs`): the override window over the whole event at 0.1 s real frames at TimeScale 1 and 10 (flag true exactly from MainFire to E and changing twice, `GridTimeSeconds` celestial inside and weather outside, Return on the weather clock, pulse weight 0 only from MainFire to Sustain), exact edges, the weather refuel clock never overriding, and suppression off.
- `eclipse_hub_beam_layer_test` (extended in `EclipseHubBeamRefuelTest.cs`): the refuel twin against `Timeline.RefuelFlash` (400 samples, 303 flashing, worst difference 2.5e-5); flash timing at TimeScale 1 and 10 (eight sites each, frame stepping with the grid on the celestial clock): the twin and the timeline start on the same frame, within one frame (16.7 ms and 166.7 ms) of `ArrivalTime`; the no-flash level bit-identical; the Instability gate; fronts published only in the flash window, no per-beam writes; the three hub shaders compile with the new uniforms.
- `eclipse_hub_beam_shader_parity_test` (windowed): ten cases including `sustain_flash` (319 flashing samples) and `instability_edge`; worst flash error 2e-7.
- `eclipse_event_settings_smoke` (the new field is in the float and beam lists), `eclipse_hub_body_test`, `eclipse_hub_light_test`, `eclipse_event_timeline_test`, `eclipse_event_stream_test`, `eclipse_window_solver_test`, `eclipse_darkness_test`, `eclipse_darkness_parity_test`, `celestial_runtime_integration_smoke_test`, `emitter_grid_g1_test`, `emitter_grid_settings_smoke`, `emitter_far_standin_test`, `underground_mode_smoke_test`, `world_loading_startup_contract`: PASS. Windowed `eclipse_darkness_capture`: all 9 gates PASS.
- **Hub-off parity (`--mode=parity --ref=base1 --ref2=base2`):** all 15 states PASS (13 inside the 2x noise gate, `main_high_night` and `main_high_totality` bit-identical).

**Captures** (`scratchpad\e4d`, 1600x900, clock paused, director scrub; `--mode=visuals --shots=s6,s8`). Reads:
- Shot 6, fronts climbing both ways: `s6_fronts_alt_wide_t25_nobeams.png` (4,500 km above the main spire, looking straight down, tau 54.5 s) is the clearest: the ring band with the six chains lit along their whole length, and two bright caps of head sprites at 2,618 km either side of the spire (top and bottom of the frame), symmetric about the spire. `t12` and `t35` are the same view earlier and later (the heads are inside the frame at t12 and just outside at t35); the same frames with the hub beams (`s6_fronts_alt_wide_t12/t25/t35.png`) are dominated by the beams and the hub body. From the ground (`s6_fronts_ground_up_t25/t60/t110.png`) the energised chains read as a fan of bright rails from the spire's base running up both sides of the arch while the beams stand over them; the heads themselves are beyond the frame. `s6_fronts_ground_up_met.png` is the meeting (18 lanes, no sprites); `s6_fronts_alt_arch_t60.png` is the 150 km starburst. The nearest-spire shots (`s6_before`, `s6_climb_mid`, `s6_complete`, `s6_complete_late`) are as in E4b.
- Shot 8, Sustain row flash: `s8_before.png` (1 s before the arrival, a dark spire at 5 km with only the thin main pillar), `s8_climb.png`, `s8_hold.png` (a bright pillar rising from the spire through the top of the frame; the grid's normal pulse is back as a bright line on the left horizon), `s8_fade.png`, `s8_gone.png` (dark again). `s8_ground_up_hold/gone.png` and `s8_alt_arch_hold/gone.png` show the row: from 150 km a handful of beams flash together and converge on the hub. The climb frame is already indistinguishable from the hold frame because the bolt climbs 5,000 km in 0.5 s and the tip leaves the frame at once.

**Open items for E4e and E5.**
- E4e: the CPU `LitBeamCount()` (status line) does not count flash-relit beams. The flash widening in cloud (the k factor) can reuse `v_flash` as its per-beam trigger.
- E5: `ev_level` still carries only 1; the hub heartbeat is unchanged (it already uses the timeline's age maths). The flash bolt does not yet modulate the hub body (the heartbeat kick in `State.Level` does).
- At E the grid returns to the weather clock, so the normal pulse's phase jumps once (the fronts reappear at their weather-clock positions) while the sky is in Aftermath/Return darkness. If that is visible, either keep the override until the Return starts fading or hold the grid time through it.
- The grid layer's own `DescribeStatus` still counts lit links without the event fronts.
- One capture run aborted at start-up with global-class parse errors (`TerrainGenerationRecipe`) right after a `dotnet build`; an immediate rerun was clean. Another failed only because the C# assembly was stale.

### E4e implementation notes (2026-10-17, landed; E4 complete)

In-cloud diffusion of the near hub beams, the stand-in crown glow and the near POI bore light boost. Base: 17b02dc plus the uncommitted E4a-E4d. Hub-off frames are unchanged (parity below).

Files:
- `EclipseHubBeamLayer.cs` (cloud queries, COLOR data, crown glow driver, bore boost driver, `LitBeamCount` fix), `EclipseHubBeamMath.cs` (twin: `CloudMask`, `CloudFactor`, `CloudCoreKeep`, `EncodeCloud`), `EclipseBeamSettings.cs`.
- Shaders: `ring_eclipse_hub_beam_math.gdshaderinc` (`cloud_params`, `ehb_cloud_mask`, `ehb_cloud_factor`, `ehb_cloud_core_keep`), `ring_eclipse_hub_beam_common.gdshaderinc` (vertex strip widening, fragment halo widening and boost, core pass cut), `ring_emitter_standin.gdshader` (crown glow).
- `EmitterStandInLayer.cs` (`SetSiteCrownGlow`, `SetCrownGlowEmission`, `CrownGlowWriteCount`), `EmitterBoreLayer.cs` (`SetSlotEventScale`).
- Settings (persisted, grave-key drawer section "Eclipse event: beams and hub"): `BeamCloudDiffusionEnabled`, `BeamCloudWidening` 3, `BeamCloudHaloBoost` 1, `BeamCloudCoreCut` 1.5 (never cuts), `BeamCrownGlowEnabled`, `BeamCrownGlowEmission` 3, `BeamBoreLightBoost` 4 (core: `Beams.CloudDiffusionEnabled`, `CloudWidening`, `CloudHaloBoost`, `CloudCoreCut`, `CrownGlowEnabled`, `CrownGlowEmission`, `BoreLightBoost`).
- Tools: `tools/EclipseHubBeamCloudTest.cs` (new, part of `eclipse_hub_beam_layer_test`), the parity shader and fixture extended, `eclipse_hub_capture.gd` (the shots below, `--storm-distance`, `--keep-grid`).

**Design.**
- **Queries.** A scan every 0.25 s picks the nearest fired spires (`tau > fire tau`) within the near range (`NearLayerMaxKm`, 700 km) that have not been queried, and calls `QueryLocalWeatherAtTime(RingCanonicalPosition(around, 0, across), CloudSimulationTimeSeconds)` once each, nearest first, **at most 30 per eclipse** (`EclipseHubBeamLayer.MaxWeatherQueries`). The counter and the answers are kept per `Window.Index`, so a rebuild or a timeline-setting change inside one eclipse neither repeats nor resets them. About 30 or more spires lie within 700 km, so the cap, not the range, usually decides which spires are served. No query is made per frame.
- **Data.** The answer goes into the MultiMesh `COLOR` of that beam (`UseColors`, half-float storage): `(k, deck base above the crown, deck top above the crown, 1 = queried)`. `k` is `CloudCoverage` (0 under 2 % or when the deck is missing); the altitudes are relative to the crown (`HubBeamCrown.Vertical`) so half floats stay exact. 595 zero writes at the bind, then one write per query, none in steady frames. The far layer has no colours and ignores the deck (it starts beyond 120 km).
- **Widening (E.6).** Inside the deck span `h = f x length` the halo e-fold is multiplied by `1 + CloudWidening x k x mask` and its amplitude by `1 + CloudHaloBoost x k x mask`. `mask` is a smoothstep deck window with 400 m half-width edges (a cloud base is not a plane; 200 m showed a visible shelf). The vertex stage widens the strip of the sub-segments that overlap the deck so it is wide enough. With the feature off, or k = 0, every term is exactly 1 (the diffusion-off shader path equals the E4d one).
- **Decision: the depth core stays on in cloud (`CloudCoreCut` 1.5 never cuts).** The plan tuning point (switch the core off where `k x mask > 0.5`, so Sunshine blends the segment and the widened halo stands in for scattering) was implemented and measured, and rejected as the default. The halo is a pre-cloud additive pass, so Sunshine dims the widened halo like any other halo; it cannot stand in for scattering. Under the storm the cut leaves only the halo-only light (noon 0.0124 against 0.0472 with the core, dark 0.0607 against 0.1475), and in the s9 frames the beam vanishes into the deck (`s9_beam_cloud_d12_cut.png` against `..._diffusion.png`). The cut stays a tuning setting (`CloudCoreCut` 0.05 to 1.5: the core is cut where `k x mask` passes it, smooth over +-0.1).
- **Crown glow (E.7).** `INSTANCE_CUSTOM.b` of the far stand-ins is written by the beam layer from a sorted event list built in `BindTimeline`: 1.0 at the spire fire tau, an ember at the end of the hand-off (`TauHandoff + HandoffFade`: 0.3 regular, 0.7 main, which keeps its beam), 0 at the cut (`TauImplosion`). Dead sites get none. **595 x 3 = 1,785 writes per eclipse, 0 per frame.** Events of one beam in the same frame collapse into one write, a backward jump (scrub) resyncs the whole state, and disabling the glow, unbinding or the end of the beams clears it. The shader adds `teal x glow x CrownGlowEmission x (1 - atmosphere.a)` to `EMISSION` on the top 12 % of the mesh height, only where the lane is above 0, so the stand-in is bit-identical at 0. The lane is only visible while a stand-in draws (beyond the POI handoff, 11.5 / 26 km).
- **Bore light (E.7).** Each frame the layer sets `EmitterBoreLayer.SetSlotEventScale(slot, 1 + (BoreLightBoost - 1) x crown level)` for the (at most two) active bore slots. The crown level is the CPU twin of the beam at its crown (handoff x lit, or the refuel flash envelope), so the plug light is 4 x at the fire, falls with the hand-off, kicks again with each Sustain flash and is back to 1 at the end. The value is written only when it changes, a slot assignment or release resets it to 1, and `Configure` keeps it.
- **Status.** `LitBeamCount` now counts flash-relit beams (an E4d open item); the status line shows `cloud queries n/30` and the crown glow write count.

**Deviations from the plan.**
1. The depth core is **not** cut in the deck by default (measured above). It is a setting.
2. `v_flash` is not used as the per-beam trigger: the k data is static per beam and applies whenever the beam is lit, so a Sustain flash widens in cloud too (any near fired spire is queried during Sustain as well).
3. The query budget is kept per window index, not per timeline object, so timeline edits cannot multiply the queries.
4. The glow ember at the hand-off has two levels (0.3 / 0.7) so the main spire third write is not a no-op and the count is exactly 595 x 3.
5. Debug hooks (not used by the game): `ResetCloudQueries`, `SetForcedDeck`, `CloudNearRangeOverrideMeters`, `WeatherQueryOverride`, `ResolveCollaborators`.

**Tests (all PASS; RTX 4070 for the windowed ones).**
- `eclipse_hub_beam_layer_test` (new `CheckClouds`, `CheckCrownGlowAndBore`):
  - the pure twin (mask edges, flags, core keep, encoding);
  - 30 queries at the near range (the 30 nearest fired spires, equal to an independent CPU selection), 30 colour writes, and 0 queries and 0 writes over 240 further frames;
  - the cap with a huge range (30 queries, 30 writes, the 30 nearest); a new eclipse resets, a rebind inside one eclipse does not repeat; nothing is queried before the first fire or with the feature off; the `cloud_params` uniform; the colour flags (near on, far off);
  - crown glow: 1,785 writes in the stand-in and in the layer over a 16,201-frame (0.25 s) walk through the event, the lane values at fire, ember (0.3, main 0.7) and cut, 0 writes over 600 steady frames, the flags untouched, a backward jump resyncs, disabling clears, the emission uniform;
  - the flash-aware `LitBeamCount` (1 in Sustain, more during a flash);
  - the bore light: 2.00 before the fire, 8.00 (scale 4.00) while firing, `Configure` keeps the boost, a boost of 1 changes nothing, 2.00 again after the hand-off, a released slot and `Unbind` reset it. The underground round trip (existing check) passes.
- `eclipse_hub_beam_shader_parity_test` (windowed): ten cases, now nine fields per sample; worst error of the new fields: deck mask 5e-7, in-cloud factor 5e-7, core keep 3.7e-6 (tolerance 4e-3); the old fields are unchanged.
- PASS: `eclipse_event_settings_smoke` (the seven fields: round trip, clamps, NaN fallback, core mapping), `emitter_far_standin_test`, `eclipse_hub_body_test`, `eclipse_hub_light_test`, `eclipse_event_director_test`, `eclipse_event_timeline_test`, `eclipse_event_stream_test`, `eclipse_window_solver_test`, `eclipse_darkness_test`, `eclipse_darkness_parity_test`, `celestial_eclipse_smoke_test`, `emitter_grid_layer_test`, `emitter_grid_g1_test`, `emitter_grid_settings_smoke`, `emitter_bore_aperture_test`, `underground_mode_smoke_test`, `underground_mode_settings_smoke_test`, `world_loading_startup_contract`. Windowed: `emitter_grid_shader_parity_test` (all 9 cases) and `eclipse_darkness_capture` (all 9 gates).
- **Hub-off parity (`--mode=parity --ref=base1 --ref2=base2`):** all 15 states PASS (13 inside the 2x noise gate, `main_high_night` and `main_high_totality` bit-identical).
- **Storm check (`--mode=visuals --shots=storm`, T2 method, no cloud refresh while lit: 10 to 10).**
  - In the E4b geometry (a regular spire 3 km ahead, the storm 12 km ahead), with the diffusion off, the core against halo-only recovers **9.4x at full noon** (mask 23,403 px) and **29.5x with the hub dark** (10,093 px): both above 5x.
  - The default configuration (diffusion on, core on) keeps **1.21 and 1.37 of that core light**: the widened halo adds light next to the core, so the default is at least as bright as the old behaviour. The cut at 0.5 keeps 0.22 and 0.30 of the light of the default.
  - **Harness change (superseded by the E4 review fixes).** The E4e run hid the near relay grid layer (`--keep-grid` restored it), because with the grid drawn the same run gave 2.0x and 2.2x. The cause was not a grid bug: the camera stood on the old on-axis storm pose, and the "pillar" was a lit relay link seen end-on, which opens the cloud along the beam and so lifts the halo-only gain. The check now steps 1.5 km across and passes with the grid drawn (see "E4 review fixes").
  - A new gate: the default must keep at least 0.8 of the core light (`STORM_DIFFUSION_KEEP_MIN`).

**Captures** (`scratchpad\e4e`, 1600x900, clock paused, director scrub; `--mode=visuals --shots=s6,s7,storm,s9`; `results.json`, `run_final_visuals.log`). Reads:
- Shot 6 (`s6_before`, `s6_climb_mid`, `s6_complete`, `s6_complete_late`, `s6_fronts_*`): the nearest spire from 5 km. `s6_complete.png` now shows the diffusion on the natural weather: the beam halo widens into a glow bank over the deck span and falls back above its top, with the neighbours hair-lines in front. The fronts shots are as in E4d.
- Shot 9, beams through cloud (`s9_beam_cloud_{d12,d6,alt}_{diffusion,off,cut,halo_only,beams_off}`): a storm 12 km (`d12`) and 6.5 km (`d6`) ahead, and from 10.5 km up (`alt`).
  - `d12_diffusion`: the depth core punches a bright white column of about 12 px through the deck, ending where the cloud in front becomes dense. `d12_halo_only`: the beam is invisible (the clouds hide it). `d12_cut` (core cut at 0.5): invisible as well. The core, not the halo, carries the beam through a dense deck, and the diffusion changes the dense-deck frame very little.
  - `d6_*` (the storm around the spire): the beam is hidden by the clouds in front in every variant. `alt_*`: the beam rises out of the cloud tops as a white column with the neighbours hair-lines.
  - `s9_deck_{diffusion,off,wide,beams_off}` (clear sky, a forced deck 2.5-6 km at the spire, `SetForcedDeck`): the visible proof of the widening without Sunshine clouds. On shows the glow bank over the deck, off the plain halo, `wide` (widening 6, boost 2) a much larger glow.
  - `s9_ground_under_deck` and `s9_alt9km_tops` are the E4c frames.
- Storm frames `storm_{noon,dark}_{base,halo_only,core,core2,diffusion_default,diffusion_halo_only,diffusion_cut05}`. The "grid pillar" in the `*_beams_off` frames of the s9 set and in the first runs of the storm set (grid drawn) is a lit relay link seen end-on (see "E4 review fixes").

Not measured: GPU time (no timings were taken). CPU: 595 zero COLOR writes at the bind; per frame one 0.25 s scan of 595 distances (microseconds); the weather queries are cached macro-scale lookups.

**Open items for E5.**
- ~~The near relay grid draws a bright vertical pillar at the crown of a fired spire.~~ Resolved in the E4 review fixes: it is a lit relay link seen end-on from the old on-axis storm pose, not a bug.
- The widened halo is pre-cloud, so dense Sunshine cloud dims it like every additive halo: under a dense deck only the depth core shows. A real in-cloud glow would need the light fed into the cloud pass (the lightning glow slot or the G5 far-cloud glow) or a wider depth footprint in cloud (a bright cavity). Not done.
- The crown glow goes off at the cut. E5 collapse table asks for a crown flash there: a fourth lane value written at `TauImplosion` and a short fade would need one more write per site or a time uniform.
- `ev_level` is still 1: the instability and overload gains (E5) will scale the diffused halo with the rest.
- The A1000 budgets (G.7) are still unmeasured for the E4 layers.


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
2. **T2:** cost of `RefreshCloudCompute` once the main beam lights (hitch ≤ 8 ms?), and whether the core then works for the hub beams. **Resolved (E4 plan):** the depth core works without any refresh after the `z_far` fix. No workaround is needed.
3. **T3:** the far viewport's HDR output space for the hub body (does the canvas background pass through the main tonemap?). **Resolved (E4 plan):** yes. Far content is tonemapped twice and peaks at about 0.79-0.85, so the hub body goes in the main viewport (user question 1).
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

### USER DECISIONS (2026-10-16, after E3) — binding
- **Hub brightness stays as implemented.** At hub level 1.0 it measures 0.92 / 1.01 / 1.11 / 0.84 of real noon (ground horizon / looking up / far arch / 9 km). It is not tuned down to 80%. `HubDayAtFull` remains available in the grave-key menu.
- **Hub in-scatter in the sky shader is deferred to E4**, so it is tuned together with the hub body, beams and zenith halo. Until then the hub-lit sky reads grey.

### E3 review fixes (2026-10-16)
- **Macro-shadow hold:**
  - A change in the hub override weight now bypasses the StarLight stabilisation step (`RingDayNightCycle._appliedPrimaryLightWeight`), so the light lands exactly on the real sun at weight 0.
  - While the hold is on, the last requested sun direction is forgotten (`RingTerrainGlobalLod`), so the first frame after the release always rebuilds from the real sun.
- **Planet light:** global and proxy terrain scale `primary_planet_weight` by `1 − hub_weight × primary_sun_weight`. The zenith-aimed hub light is never classed as planet light near real noon. The factor is exactly 1 at hub weight 0.
- **Director:**
  - The hub weight eases in celestial time (delta × max(1, TimeScale)).
  - `ResolveNodes` re-registers the primary-light override whenever it is not this director.
  - `_ExitTree` resets the eased weight and the primed flag.

### USER DECISIONS (2026-10-17, E4 plan) — binding
1. **The hub body is a main-viewport proxy**, not the far viewport of §E.1. It is a camera-facing white disc that clouds and overhead geometry still hide. Reason: T3 found the far viewport is tonemapped twice and tops out at about 0.85.
2. **The far-viewport double tonemap is out of scope for E4.** It is a separate later task. E4 tunes the far beams and the sky on the final image.
3. **The emitter grid is registered as an underground participant in E4b.**

### E4 review fixes (2026-10-17)
- **`halo_amp` rename.** In `materials/ring_beam_ribbon.gdshaderinc` the halo amplitude parameter of `ribbon_cross_profile` is now `halo_amp` (it was `halo_amplitude`, which is also the name of the uniform in the including shaders). No behaviour change.
- **Storm pose.** `tools/eclipse_hub_capture.gd` (`--shots=storm`) now steps 1.5 km sideways (across the ring) from the old on-axis pose. The "grid pillar" of the E4e notes was a lit relay link of the grid seen end-on from the old pose, not a bug. With the grid drawn (no `--keep-grid` needed) the storm check passes: **noon 10.27x, dark 51.77x** (re-run after fixes 1 to 3: 10.28x and 51.79x, no cloud refresh while lit: 5 to 5).
- **Fix 1, grid clock jump at E.** `EmitterGridBeamLayer.CurrentTime` used to snap from the celestial grid clock back to the weather clock at E, teleporting the normal fronts by `v x ((celestial - weather) mod 300 s)` at night peak. The hand-over is now slewed by the new `EmitterGridClockSlew` (same file):
  - On override to none it latches `d = wrap(lastResult - weather, P)` into (-P/2, P/2] (P = `PeriodSeconds`) and returns `weather + d (1 - u)`, `u = clamp((weather - w0) / P, 0, 1)`. None to override is the mirror: `override - d (1 - u)` over P of override time.
  - The pulse speed is temporarily 0.5 to 1.5x, never reverses, and stays phase-continuous mod P.
  - A step of either clock larger than 5 s between two evaluations (scrub, load, time jump) skips or cancels the slew and snaps. Consequence: a celestial time scale above about 300x at 60 fps also counts as a jump and keeps the old snap.
  - `GridTimeOverrideSeconds` still reports the raw director time. Hub flash fronts use the director time directly and are unaffected.
- **Fix 2, knot relaunch flare during the Q3 window.** With `NormalPulseWeight` 0 (MainFire to the end of Handoff) the 12 knot links of the main spire still flashed once per period with no front. `EmitterGridBeamLayer.Update` now sets `flare = NormalPulseWeight > 0 ? FlareEnvelope : 0`, which the near and the far head flare share. The CPU twin (`IsLinkLit`, the event `CountLitLinks` and `PulseAtLink`) takes `bool knotFlare = true`; at weight 1 and with the default the results are bit-identical (legacy golden and the shader parity cases unchanged).
- **Fix 3, stand-in crown glow mirror after a rebind.** `EmitterStandInLayer` reallocates `_crownGlow` on `Bind`. It now exposes `BindRevision`. `EclipseHubBeamLayer` resyncs (`_glowLastTau = NaN`, `_glowWritten` cleared, emission re-applied) when the revision changes or when it resolves a different stand-in layer node. Steady frames still write nothing.
- **Design note.** During ignition the plateau keeps the grid depth core on along the lit chains within 700 km. That is intended: the chains stay energised and drawn with their core until the hand-off fade.
- **Tests.** `EmitterGridLayerEventTest.cs`: slew continuity at both edges, speed bounds, snap on a jump, the knot flare CPU twin and the layer's `eg_flare` uniform at weight 0 and 1 (the existing override check now expects the override time after one period instead of an instant switch). `EclipseHubBeamCloudTest.cs`: a stand-in rebind mid-event restores the glow on the next update and steady frames write 0.

### USER DECISIONS (2026-10-17, tweaks) — binding
1. **Sustain refuel pulses are orbs only.** Each refuel pulse is an orb travelling from the spire base to the hub in about 30 s. No lit beam follows it or stays behind it.

### Tweaks plan (Lead, 2026-10-17)

Base: 17e50ea (main). Five user requests: the main spire relay height, Sustain orbs, water drawn over the beams, beams from the spire base, and beams lighting clouds. Request 3 is fixed in the working tree (T0 below). The rest is planned here. Evidence frames are in the lead scratchpad under `tweaks\` (`water1`, `water2`, `cloud2`), shot at 1600x900 on the RTX 4070 alongside the idle editor (image tests only, no GPU timing). The scripts are `tweaks\water_diag.gd` and `tweaks\cloud_proto.gd`; both extend `tools/eclipse_hub_capture.gd`.

#### R1. Main spire relay height (root cause)

**What happens.** `EmitterGridTopology.DefaultMainRelayHeightM = 1743` (`scripts/Terrain/Sites/EmitterGridTopology.cs:28`) is the regular crown height, and it is also used for the main site (`:64`). It is a placeholder that grid spec §G1 T1 left open ("confirm against the main connector rings"), and it was never revisited.

**Main spire geometry.**
- Tips at 4,000 m.
- Inner tower crown at 3,600 m (`prototypes/Ringworld_Main_Spire_4km_Outer_Ring/README.md:8`).
- Six outer connector rings at 550, 1,050, 1,550, 2,050, 2,550 and 3,050 m (`README.md:10`).
- `BeamCrownFocus` at 3,989 m, the centre of the open outer crown ring (`assets/poi/main_spire/MainSpirePOI.tscn:2305-2306`, `EmitterSiteFrameMath.DefaultMainCrownHeightMeters`, `EmitterSiteRecipe.MainCrownHeightM`).

So the 12 knot links leave the main spire at 44 % of its height, between the 1,550 and 2,050 m rings. On a regular spire the same constant sits at 97 % of the height (its crown focus, 1,743 of 1,800 m).

**Design.** Apply the same rule as the regular spires: the relay node is the crown focus, so `DefaultMainRelayHeightM = EmitterSiteFrameMath.DefaultMainCrownHeightMeters` (3,989). The alternative anchor is the top connector ring at 3,050 m (user question 1).

**Consequences** (Δh = 2,246 m against the regular relay):
- **Knot link slope:** 0.41° / 0.40° / 0.38° for the 315.3 / 324.1 / 341.0 km links. This is invisible at the knot. Only the 12 knot links change; the 588 chain links are untouched.
- **Mid-span height:** rises by Δh/2 = 1,123 m. The sagitta itself stays at d²/8R (2,484 / 2,625 / 2,905 m). For sea-level pads the knot mid-span altitude goes from 4.2-4.7 km to 5.3-5.8 km: still inside the 2-6 km deck, near its top.
- **Unchanged:** the pulse coordinate p, the link order and the CPU pulse golden (`legacy_golden` uses only p).
- **Changed:** `Identity` changes, because the hash mixes the relay heights (`:113-114`).
- **Far layer and head sprites:** they take the relay node from the topology, so they follow automatically.
- **Visual:** the links leave the bore axis at the crown ring and cross the ring's annulus. The depth test hides the few metres inside solid geometry, as at the regular crowns.

#### R2. Sustain refuel orbs (design)

**Today.**
- `ehb_refuel` (`materials/ring_eclipse_hub_beam_math.gdshaderinc:90-116`) takes the latest normal grid front on the column that has passed the spire's q: `age = (head - q)/v`.
- It returns the `Timeline.RefuelFlash` envelope and a bolt tip `age / FlashClimb` (0.5 s for 5,000 km).
- `ehb_level_refuel` relights the regular beam below the tip, and the vertex stage keeps those sub-segments (`ring_eclipse_hub_beam_common.gdshaderinc:67-69`).
- The CPU twins are `EclipseHubBeamMath.RefuelFlash` and `EclipseEventTimeline.RefuelFlash` (`EclipseEventTimeline.cs:276-290`).
- The hub heartbeat kicks when a row's front **passes** the spire: `exp(-max(0, age - FlashClimb)/HeartbeatTau)` (`EclipseEventTimeline.cs:418-443`).

**Between orbs (coordinator question).** The regular beam has no post-handoff residual today. `ehb_handoff` falls to exactly 0 after `TauHandoff + HandoffFade` (`ring_eclipse_hub_beam_math.gdshaderinc:45-50`), and `ehb_lit` then collapses the whole ribbon (`:67-70`). The beam is therefore already fully dark between flashes. The only residual glow is off the beam:
- the stand-in crown ember 0.3 (`EclipseHubBeamLayer.CrownGlowEmberRegular`, drawn only beyond the POI handoff);
- the plug light, which returns to 1×.

**Recommendation:**
- Keep the beam fully dark between orbs, as the binding decision requires.
- Keep the stand-in ember.
- Turn the bore light boost into a short **launch kick**: the existing age envelope (rise `FlashClimb`, hold, fade) measured from the pass, no longer tied to a bolt.

**Orb design** (GPU only; no per-frame per-beam CPU writes, as in E4b):
- **Geometry.** Append one camera-facing quad (4 vertices) to the ribbon mesh in `EclipseHubBeamLayer.BuildRibbonMesh`, marked by `UV2 = (-1, -1)`. The same MultiMesh therefore carries it, with the same instance data and no extra draw call, in the near layer, the depth core and the far layer. The far layer shares the mesh, so the 594 orbs also climb the arch starburst.
- **Vertex stage.**
  - Orb vertices call `ehb_orb(fire_tau, kind, p_site, column) -> vec2(envelope, f_orb)` and place the quad at `origin + span * f_orb`.
  - The radius is `max(OrbRadiusMeters, OrbMinPixelRadius * meters_per_pixel)`; the intensity is energy-conserving when the pixel minimum clamps, as in `ribbon_profile`.
  - Layer weights (`eg_near_fade`, `eg_band_weight`) and the atmosphere are taken at the orb centre. The quad collapses when the envelope is 0, out of range, or behind the near plane.
  - Ribbon vertices drop the bolt relight: `ehb_level_refuel` and `ehb_lit_refuel` revert to `ehb_level` and `ehb_lit` for the beam body. This is the binding decision: no lit beam.
- **Fragment.** A radial profile: a white Gaussian core plus a teal exponential halo, the same colour rule as the beam. The core pass writes the orb's depth footprint (`ribbon_depth_core_covers` with the radial distance), so clouds behind an orb do not dim it (the T2 lesson).
- **Motion.** `f_orb = clamp(age_launch / OrbTravelSeconds, 0, 1)^OrbEaseExponent`.
  - With exponent 1 the orb moves at a constant 167 km/s, crosses a 6 km deck in 0.04 s, and is a streak near the spire.
  - With the recommended exponent 3 (user question 2), it lifts off over about 3 s (6 km at t = 3.2 s), then rushes to the hub. At t = 10 / 20 / 30 s it is at 185 / 1,480 / 5,000 km.
- **Envelope.** It rises over `FlashClimbSeconds` (0.5 s) at the launch, holds while travelling, holds `FlashHoldSeconds` at the hub, then fades over `FlashFadeSeconds`. About 64 orbs are in flight at once: six per 3 s row, about 32 s each.
- **Fronts.**
  - With T = 30 s an orb lives up to 31.8 s after its front passes. `GetFronts` drops a front once `head - C > CutoffMeters` (4,189 km at the defaults; `EmitterGridPulseClock.cs:77,128`), so orbs of the last rows would vanish once T exceeds about 38 s.
  - `UpdateRefuelFronts` (`EclipseHubBeamLayer.cs:427-456`) must therefore enumerate the two latest launches per column without the cutoff (a new `EmitterGridPulseClock.GetFrontsWithin(t, column, maxBehindMeters, span)`). That is at most 12 lanes, within the 24.
  - The publish window becomes `[TauSustain, TauInstability + T + hold + fade + 1)`; `FlashTailSeconds` gains T.
- **Arrival and the heartbeat.**
  - `HeartbeatAt` shifts to arrival: per column, take the latest site with `age >= T_arr`, where `T_arr = T` is the travel time to f = 1 (the ease does not change the arrival time), and kick `exp(-(age - T_arr)/HeartbeatTau)`. There is no kick before arrival.
  - When `head - v T_arr < qs[0]`, the previous launch's front supplies the site.
  - The relaunch kick and `EclipseEventStream.RefuelFlash` stay at the pass, so the spire launch stays the audio and PDA cue.
  - The hub therefore pulses about 30 s after a row launches, still every 3 s, and still six in sync.
- **Timeline and parity.**
  - New pure functions `EclipseEventTimeline.RefuelOrb(entry, tau, grid) -> (envelope, f)` and the twin `EclipseHubBeamMath.Orb`. They replace `RefuelFlash` and its `Tip`. `RefuelFlash` stays as the launch-kick envelope for the bore light and `CrownLevel`.
  - The uniforms grow by `ev_orb = (travel s, ease exponent, radius m, min px)` and an orb emission factor.
  - `LitBeamCount` stops counting flash-relit beams; the status line gains "orbs in flight n".
- **Settings** (persisted; grave-key section "Eclipse event: beams and hub"):
  - Timeline group: `OrbTravelSeconds` 30 (5-60). A change rebuilds the timeline because it moves the heartbeat.
  - Look group: `OrbEaseExponent` 3 (1-4), `OrbRadiusMeters` 350, `OrbHaloMeters` 1,500, `OrbMinPixelRadius` 2.5, `OrbEmission` 16.
  - The labels of `FlashClimbSeconds`, `FlashHoldSeconds` and `FlashFadeSeconds` become "Orb launch fade-in", "Orb hold at hub" and "Orb fade at hub".
  - An older save keeps its 0.5 / 0.6 / 1.2 s and gains T = 30.

#### R3. Water drawn over the beams (root cause, fixed)

**Root cause: transparent sorting.**
- The near ocean (`materials/ring_ocean_proxy.gdshader:2`) is `blend_mix`, `depth_draw_always`, and `ALPHA = 0.995` (`:583`). It refracts the opaque screen copy.
- Its material keeps render priority 0 (`RingTerrainProxySector.cs:5553-5590`).
- The near hub beam halo and the grid halo and head sprites were also at priority 0, with the cores at 1 (`EclipseHubBeamLayer.cs:34`, `EmitterGridBeamLayer.cs:34`).
- Godot sorts the transparent list by priority, then by instance depth. A 595-beam MultiMesh with a ±10,000 km custom AABB therefore lands before or after a water sector depending on the camera.
- When the beam is drawn first, the 99.5 %-opaque water paints over the additive halo wherever water lies behind the beam, and over the core everywhere except the depth-written core strip.
- The global ocean is opaque (`ring_ocean_global.gdshader:5`) and the far viewport is a separate backdrop, so neither is involved.

**Reproduction** (`water1`, island site 488, Far viewpoint 5 km, beam complete at fire + 12 s, grid `ShowAllLinks`):
- From 4.3 km up (`w_up4000_A_shipped.png`), the beam halo stops at every shoreline and shows only over land.
- With the beams raised above the water (`w_up4000_B_raised.png`), the halo runs continuously over the sea.
- Mean luma: 0.2652 shipped against 0.2736 raised. The other three poses had no water behind the beam and are unchanged.

**Fix (T0, applied).**
- `EmitterGridBeamLayer.NearWaterRenderPriority = 0` documents the water's priority.
- In `EmitterGridBeamLayer` and `EclipseHubBeamLayer`: halo and heads = `NearWaterRenderPriority + 1` (1), core = halo + 1 (2). The hub layer now references the grid constants.
- `EmitterGridLayerTest` and `EclipseHubBeamLayerTest` assert halo > water and core > halo.
- The water writes depth, so a beam below the surface stays hidden. Blending order against other additive passes does not matter. Far layers (5) and the hub body (20) are unchanged.

**After** (`water2`, same poses): shipped equals raised in every pose (`w_up4000`: 0.27359 / 0.27359). `emitter_grid_layer_test` and `eclipse_hub_beam_layer_test` PASS.

#### R4. Hub beams from the spire base (design)

**Today.** The origin is the crown: `_hubBeamVertical = pad + clearance + 1,743 / 3,989` (`EmitterGridTopology.cs:106-107`). It is read by:
- `FillHubBeamEndpoints` (`:192-202`);
- `EclipseHubFarLayer` (`:61`);
- the cloud encoding (`EclipseHubBeamLayer.cs:541`, deck altitudes relative to the crown);
- the tests.

**Single swappable anchor.**
- Add `enum EmitterBeamOrigin : byte { Crown = 0, Base = 1, Reactor = 2 }`.
- Add one pure function, `EmitterSiteFrameMath.BeamOriginHeightMeters(SiteKind, EmitterBeamOrigin)`, measured from the pad:
  - Crown = 1,743 / 3,989;
  - **Base = -200 m**, the bore iris (`EmitterSiteApertures.PlugDepthM`; user question 3: the alternative is the pad surface, +0.05 m);
  - Reactor = `EmitterSiteUnderground.Shape(kind).ExhaustTopYM` (-2,961 / -1,922, the reactor exhaust the shaft rises from).
- The topology takes the origin as an argument: `HubBeamOrigin(beam, origin)` and `FillHubBeamEndpoints(snapshot, origin, Span<Vector3D>, out hub)`. The topology stays immutable and shared with the grid, and `HubBeamCrown` remains for the crown glow.
- Setting: `BeamOrigin` (persisted int, default Base) in `EclipseBeamSettings`, with a drawer option. Reactor is listed as a preview until M2-4 places the reactors.
- A change is a beam-only change. It calls `Rebuild` (595 transforms) and re-encodes the queried cloud data relative to the new origin, with no timeline rebuild. The future switch to the reactor is therefore a default change, not code.

**Visual consequences:**
- **Bore and spire.**
  - The 150 / 300 m core (Gaussian sigma 75 / 150 m) runs up the clear R 75 / R 150 bore.
  - The camera-facing ribbon is depth-tested, so the arms in front of the axis occlude it, and the halo glows through the lattice gaps (the spire reads energised).
  - Below the ground the liner and the terrain hide the first 200 m from the side; from above, the beam rises out of the teal iris.
- **Base fade.** `BeamBaseFadeMeters` (400 m) now starts 200 m under the pad, so the halo is about 0 at the rim and full about 200 m up. Retune it if the rim looks hard.
- **Depth core.** It now writes depth from ground level up. That is harmless: it is zero colour and only where the core is bright.
- **Near plane and close range.** A player at the bore rim is 75 m from a 12× emission core. The existing near clip handles the geometry, but expect a white-out. A close-camera softening (core gain fades in over the first ~300 m of view distance) is a tuning item in T2, judged at the spire's entry viewpoint.
- **Length and tilt.** Length grows by 1.94 km (regular) and 4.19 km (main), to about 4,997-5,002 km. Tilt changes by under 0.001°.
- **Hub-off parity.** Unaffected: no beam object exists outside the prebuild window.
- **Underground.** Reactor mode puts the beam in the cavern, but `EclipseHubBeamsParticipant` hides the beams underground. M2-6 must revisit that before Reactor becomes the default.

#### R5. Beams lighting clouds (options, prototype, recommendation)

**How Sunshine takes light today.**
- Directional lights: `StarLight`, with an 8-step shadow march per in-cloud step.
- Up to 128 point lights (`PointLight {position.xyz, radius; color.rgb, intensity}`, `CloudsInc.comp:369`), uploaded through a 6 KB uniform buffer only when a tracked `OmniLight3D` changes (`SunshineCloudsDriver.gd:134-139, 237-242`; `SunshineClouds.gd:1782, 2474`). The scene tracks exactly one: `LocalWeatherEffects/LocalStormFlash` (the lightning glow slot, `VoxelRingIntegrationTest.tscn:613`).
- Per in-cloud step and per light within its radius, the raymarch runs `sampleLighting(3, …)` (3 density evaluations), Henyey-Greenstein and Beer (`SunshineCloudsCompute.glsl:1232-1251`).
- With `lightning_history_isolation_enabled` the light goes into `transientLightColor`, which bypasses the temporal history.

**Prototype** (`cloud2`; nearest regular spire, 5 km Far viewpoint, spawned storm front, dark hub, fixed exposure). OmniLights with `light_cull_mask = 0` (Sunshine-only) were placed along the beam's in-deck span (-1.3 … +7.2 km above the crown, k = 0.94) and added to `tracked_point_lights`.
- **Storm 12 km ahead (behind the spire)**, mean luma (top half in brackets):
  - beams off: 0.0948 (0.0950);
  - shipped beam: 0.0971 (0.0996);
  - + 3 lights, r 3.5 km, energy 18: 0.1390 (0.1833);
  - + 12 lights, r 2 km, energy 10: 0.1097 (0.1247);
  - + 12 lights, r 3 km, energy 24: 0.1486 (0.2026).
  - The cloud bank around the beam lights up from inside (`c_d12_beam_pt3.png`, `c_d12_beam_pt12_strong.png`).
  - On the transient path it is grainy. With the history path (isolation off, `c_d12_beam_pt12_strong_history.png`) it is smooth: the strongest look of the set.
- **Storm 6.5 km ahead (around the spire).** The shipped beam is hidden (0.0944 against 0.0938 off, `c_d6_beam.png`). The point lights light the fog around it (0.1037 / 0.1075, `c_d6_beam_pt12_strong.png`), so the beam reads as a glow through the storm.
- **Shape.** The radius-bounded lights give an elliptical blob with a soft but visible outer bound; they do not give a line.

| Option | Look | GPU cost (RTX A1000, estimate; measure in T5) | CPU | Work | Verdict |
|---|---|---|---|---|---|
| **A. Stock Sunshine point lights**, a chain of 3-12 per near beam | Proven above. Volumetric and occluded correctly by cloud in front. Lumpy blob, not a line. Grainy unless on the history path | Each light in range adds 3 density evaluations per in-cloud step. The step already pays about 10 (1 density + 8 sun shadow + ambient), so about +30 % per overlapping light inside its radius. Raymarch about 10.6 ms (`perf-ranking-2026-10-15.md:38`): 3 lights about +0.5-1.5 ms, 12 overlapping about +3-5 ms in a view filled by the glow | OmniLight nodes, 6 KB upload on change | Small | Too expensive for a line, and the wrong shape |
| **B. Segment line lights in the Sunshine raymarch** (recommended) | The look of A-dense on the history path, but continuous along the beam with no radius blob. Analytic line irradiance `(atan((L - t)/d) + atan(t/d))/d` × HG × Beer on the local density. Occluded by cloud in front through Sunshine's own accumulation. Steady lights go to the history path; moving ones (grid heads, orbs in the deck) to the transient path | Per in-cloud step: up to 8 segments × about 10 ALU for the closest-point reject (about +1 % of the raymarch, about 0.1 ms), plus about 40 ALU per in-range segment (about 1/5 of one density evaluation), plus an optional 1-tap shadow. Beams are 50+ km apart, so 1-2 segments are in range at a sample. **About +0.1-0.3 ms** | ≤ 8 segments per frame, written only on change | Medium: addon shader, encoder and a feeder | **Recommended** |
| **C. Post-cloud screen-space glow** (a compositor pass after Sunshine, masked by Sunshine's alpha and density distance) | Glow painted onto the cloud image near the projected beam. Clouds in front are over-lit (no in-cloud transmittance). Sharp beam-to-cloud edges at silhouettes | About 0.1-0.2 ms at full resolution (8 segments analytic per pixel) | Same feeder | High: a new RD raster/compute pass, and access to SunshineClouds' internal RIDs | Fallback only |
| **D. G5 far-cloud glow slot** (`far_pulse_glow`, spec E.6) | Far clouds (beyond about 72 km) only | About 0 | Trivial | Small, but G5 has not landed | Complement to B, later |
| **E. Wider depth core in cloud** (a bright cavity) | Reveals the halo through clouds behind; does not light the cloud | 0 | 0 | Small | Does not answer the request |

**Recommendation: B, then D for far clouds.** Prototype A shows the target look and that Sunshine's integration (occlusion, phase, Beer) is right. B gets that look along a line at a fraction of A's cost.
- **Encoding.** A segment takes two `PointLight` slots: `(A.xyz, -radius)(color, intensity)` then `(B.xyz, flags)(0)`. The negative radius is the marker; flags bit 0 is steady (history path). The buffer layout and the existing point-light path stay unchanged.
- **Zero-segment parity.** With zero segments the loop is identical, so the parity gate is bit or noise parity against HEAD.
- **Feeder.** Each frame picks ≤ 8 segments, nearest first, within the Sunshine near reach (about 72-80 km):
  - lit hub beams: the CPU twin level at the origin, clipped to Sunshine's cloud floor and ceiling;
  - orbs inside the deck (transient; with ease 3 an orb is about 3 s in the deck);
  - lit grid spans near the camera (`PulseAtLink` windows, transient, clipped to the deck band).
- **Upload.** Values are quantised and uploaded only on change.
- **Not measured.** GPU time. Measure on the A1000 with no other Godot running (budget ≤ 0.3 ms at the storm pose; user question 4).

#### Steps (implementer-sized, Sonnet medium)

Every step keeps hub-off frames unchanged (`eclipse_hub_capture --mode=parity`, reference `base1`/`base2`, all 15 states).

| Step | Deliverable | Files | Gates and tests | Serialisation |
|---|---|---|---|---|
| **T0** Water sorting (**applied, not committed**) | Beams draw after the near water (priorities 1/2) | `EmitterGridBeamLayer.cs:33-43`, `EclipseHubBeamLayer.cs:33-37`, `tools/EmitterGridLayerTest.cs`, `tools/EclipseHubBeamLayerTest.cs` | `emitter_grid_layer_test`, `eclipse_hub_beam_layer_test`, `emitter_grid_shader_parity_test` PASS. Before/after `water1` / `water2`. Still to run before commit: the hub-off parity (the grid now draws after the water in those poses too, so expect a small change where a lit link lies over the near sea) | None |
| **T1** Main relay at the crown | `DefaultMainRelayHeightM = EmitterSiteFrameMath.DefaultMainCrownHeightMeters` (or 3,050 per question 1). Grid spec §G1 / U line 841 / appendix updated. G1 test identity alternative changed (it builds `(1743, 3989)` as the "different" topology, `EmitterGridG1Test.cs:119`) | `EmitterGridTopology.cs`, `tools/EmitterGridG1Test.cs`, `docs/emitter-grid-climate-machines-spec.md` | `emitter_grid_g1_test` (knot slope ≤ 0.45°, mid-span range ≤ 7 km still holds), `emitter_grid_layer_test` (legacy golden unchanged), `emitter_grid_shader_parity_test` (windowed), `eclipse_hub_beam_layer_test`, `eclipse_event_timeline_test`. Capture: `main_far` and `main_high` with `ShowAllLinks`: the 12-link fan leaves the crown ring | Before T2 (same file) |
| **T2** Beam origin anchor, default Base | `EmitterBeamOrigin`, `BeamOriginHeightMeters`, topology origin-aware hub API; layer, far layer and cloud encoding from the origin; setting `BeamOrigin` plus drawer option; close-camera core softening if the entry-viewpoint capture whites out; hub beam test bounds re-derived (length, tilt, origin heights for all three modes) | `EmitterSiteFrameMath.cs`, `EmitterGridTopology.cs`, `EclipseHubBeamLayer.cs`, `EclipseHubFarLayer.cs`, `EclipseBeamSettings.cs`, `GameplayEclipseEventSettings`/`Defaults`, `RingLiveTuningDrawer.Eclipse.cs`, `tools/EclipseHubBeamLayerTest.cs`, `tools/EclipseHubBeamCloudTest.cs` | `eclipse_hub_beam_layer_test` (endpoints within 1 m of the hub, origins at pad − 200 / crown / exhaust, rebase invariance, origin switch = one rebuild and no custom-data writes), `eclipse_event_settings_smoke` (new field round trip and old save), `eclipse_hub_beam_shader_parity_test`, storm check (`--shots=storm` ≥ 5×), hub-off parity. Captures: s6 (5 km), regular entry viewpoint (vp 0) and `main_far`, beam rising out of the bore through the lattice | After T1. Before T3 (layer, include, settings and drawer) |
| **T3** Sustain orbs | Orb quad in the ribbon mesh; `ehb_orb` plus the twin `EclipseHubBeamMath.Orb`; the beam body no longer relit; `RefuelOrb` and the arrival heartbeat in the timeline; `GetFrontsWithin`; publish window; bore launch kick; settings (`OrbTravelSeconds` timeline, look fields) plus drawer and relabels; status "orbs in flight" | `ring_eclipse_hub_beam_math.gdshaderinc`, `ring_eclipse_hub_beam_common.gdshaderinc`, `EclipseHubBeamMath.cs`, `EclipseHubBeamLayer.cs`, `EclipseEventTimeline.cs`, `EclipseEventSettings.cs`, `EmitterGridPulseClock.cs` (one new method, legacy untouched), settings/defaults/drawer, `tools/EclipseHubBeamRefuelTest.cs`, `tools/EclipseHubBeamShaderFixture.cs`, `tools/shaders/eclipse_hub_beam_parity.gdshader`, `tools/eclipse_hub_capture.gd` (s8) | Twin against `RefuelOrb` (envelope and f, 400 samples, ≤ 1e-4). **Arrival:** f reaches 1 exactly at `ArrivalTime + T`, and the heartbeat kick starts on the same frame (within one frame at TimeScale 1 and 10). No kick before arrival. GPU parity: orb envelope, f and the collapse rule (tolerance 4e-3). No ribbon sub-segment of a regular beam lit after the hand-off (the binding decision). T = 55 s keeps the last-row orbs (no cutoff loss). 0 per-beam writes in steady frames. `emitter_grid_layer_test` legacy golden unchanged. `eclipse_event_director_test`, `eclipse_event_timeline_test`, `eclipse_event_stream_test` (refuel event still at the pass), settings smoke, hub-off parity. Captures: s8 reworked (orb leaving the 5 km spire at +0.5 / 2 / 4 / 10 s, ground-up and 150 km arch views of a row converging on the hub, hub kick about 30 s later) | After T2 |
| **T4** Sunshine segment lights | Segment encoding in the point-light array (negative-radius marker, steady flag), analytic line irradiance, history or transient routing; `segment_lights_data` export and encoder | `addons/SunshineClouds2/SunshineCloudsCompute.glsl` (point-light loop), `SunshineClouds.gd` (encode), `CloudsInc.comp` (comment only) | Zero-segment cloud frames bit or noise identical to HEAD (Sunshine parity at the M1 poses). A synthetic segment at the storm pose lights the cloud. Lightning flash unchanged (transient). Mind the user's uncommitted `SunshineClouds.gd` edit: merge, never overwrite | Independent of T1-T3. Not in parallel with any Sunshine work |
| **T5** Cloud-light feeder and settings | ≤ 8 segments per frame (hub beams, orbs in the deck, lit grid spans) within the near reach, clipped to the cloud band, written on change; settings `BeamCloudLightEnabled`, `BeamCloudLightIntensity`, `BeamCloudLightRadiusMeters`, `GridCloudLightEnabled`, `MaxCloudLightSegments` plus drawer | new `scripts/Celestial/Eclipse/EclipseCloudLightFeeder.cs` (or in `EclipseHubBeamLayer`), `RingSunshineCloudBridge.cs` (setter), grid layer read-only access, settings/drawer | Headless: segment selection and order, clipping, ≤ 8, 0 uploads in steady frames, nothing outside an event and with the grid dark. Windowed: storm d12 / d6 frames against `cloud2` (look). **A1000 GPU time with no other Godot running: ≤ 0.3 ms** at the storm pose (on against off), hub-off parity | After T3 and T4 |
| **T6** Far-cloud glow (optional) | The G5 `far_pulse_glow` slot fed by the main pillar and lit beams beyond the near reach | far cloud clipmap shader, `RingScaledFarViewport` | Far parity at zero | With G5 |

#### User questions (tweaks), each with a recommended default
1. **Main relay height.** Recommended: **3,989 m**, the crown focus, the same rule as the regular spires (the knot fan leaves the crown ring). Alternative: 3,050 m, the top connector ring.
2. **Orb motion.** Recommended: **ease-in, exponent 3** (setting): the orb lifts off visibly over about 3 s, clears the clouds, then rushes to the hub, arriving at 30 s. Alternative: constant 167 km/s, a near-instant streak at the spire.
3. **Where "base" is.** Recommended: **the bore iris, 200 m below the pad**: the beam rises out of the bore with no cut at the rim. Alternative: the pad surface.
4. **Cloud lighting.** Recommended: **option B** (segment lights inside Sunshine, budget ≤ 0.3 ms on the A1000). It edits the vendored Sunshine addon, and lands hub beams and orbs first, then the grid links. Alternatives: A (no addon edit, about 1-5 ms) or C (post pass, cheaper, less convincing).

### USER DECISIONS (2026-10-17, tweaks plan answers) — binding
1. **Main relay height:** the main spire's grid links attach at its crown focus, 3,989 m.
2. **Orb motion:** ease-in (exponent 3). The orb visibly lifts off over about 3 s, then rushes to the hub, for about 30 s total. Between orbs the beam is fully dark.
3. **Beam origin "Base":** the bore iris, 200 m below the pad. The `EmitterBeamOrigin` anchor (Crown / Base / Reactor) stays swappable for the later reactor origin.
4. **Cloud lighting:** option B is approved. Segment line lights go inside the Sunshine raymarch, which edits the vendored addon, under a ≤ 0.3 ms A1000 budget. Hub beams and orbs come first, then the grid links. With zero segments the clouds must stay unchanged.

### Tweaks T0-T2 implementation notes (2026-10-17)

**T0 verify (hub-off parity, water fix in).** `eclipse_hub_capture --mode=parity` against base1/base2: **13 of 15 states pass, `alt_down_night` and `alt_down_totality` fail** (diff_changed 0.0441 and 0.0459 against limits 0.00114 and 0.00100, dluma 0.0052 and 0.0063, about 19,700 differing pixels; frames `tweaks/t0/alt_down_night_a.png` against `base1/alt_down_night_a.png`). Cause, read from the frames: the lit relay links over the sea to the left of the coast, which the old transparent order hid behind the near water, are now drawn. That is the plan's warned effect, and no gate was loosened. All five day frames and the other night, totality frames pass (`main_high_night` and `main_high_totality` bit-identical). `tweaks/t0` is the post-T0 baseline for later steps.

**T1, main relay at the crown.**
- `EmitterGridTopology.DefaultMainRelayHeightM = EmitterSiteFrameMath.DefaultMainCrownHeightMeters` (3,989 m). `Identity` changes. Grid spec §G1, T1 (resolved) and the defaults appendix updated.
- `EmitterGridG1Test`: the main relay assertion now expects 3,989 m on the first and last knot link, and the "different topology" for the identity check is `Build(registry, 1743, 1743)`. G1 result: mid-span altitude 4,187-6,602 m (seed 1337), sagitta 2,375-2,576 m, knot chords 312-344 km; the knot slope from the relay heights alone is atan(2,246 / 312,420) = 0.41 degrees at worst.
- Hub-off parity after T1 + T2 against `tweaks/t0`: **day frames 5/5 pass, all 10 night and totality frames fail** (diff_changed 0.2-0.3, for example `ground_horizon_night`): the 12-link knot fan now meets at the crown focus instead of the 1,743 m node, and every pose in the parity set has the main spire's fan in view. The same build with only T1 reverted (`DefaultMainRelayHeightM` back to 1,743, temporary, restored): **15/15 pass against `tweaks/t0`** (`tweaks/par12_t1off`), so T2 is hub-off neutral and T1 is the only cause. Post-T1 frames: `tweaks/par12`.
- Visual check: `tweaks/t1/t1_main_far.png`, `t1_main_far_up8.png`, `t1_main_high.png`, `t1_main_high_up8.png` (`ShowAllLinks`, beams off). The 12 links leave the spire at its crown (the top of the lattice), and from 150 km they meet in one point.

**T2, beam origin anchor.**
- `enum EmitterBeamOrigin { Crown, Base, Reactor }` and `EmitterSiteFrameMath.BeamOriginHeightMeters(kind, origin)`, metres above the pad datum: Crown = pad clearance + 1,743 / 3,989 (exactly the old origin, so Crown reproduces the E4 beams bit for bit), Base = -200 (`EmitterSiteApertures.PlugDepthM`, the bore iris, no clearance), Reactor = `EmitterSiteUnderground.Shape(kind).ExhaustTopYM` (-2,961 regular, -1,922 main).
- `EmitterGridTopology.HubBeamOrigin(beam, origin)` and `FillHubBeamEndpoints(snapshot, origin, points, out hub)`; the old crown overload and `HubBeamCrown` stay (the crown glow and the tests use them). The topology stays immutable.
- `EclipseBeamSettings.BeamOrigin` (default **Base**) and `CloseSoftenMeters` (default 1,000 m, 0 to 5,000, 0 = off). Persisted as `BeamOrigin` (int 0-2, default 1) and `BeamCloseSoftenMeters`; grave-key section "Eclipse event: beams and hub" gets a "Beam origin" option (Reactor labelled "preview") and a "Close-range softening (m)" slider. An older save loads Base and 1,000.
- `EclipseHubBeamLayer`: the instance origin is now the beam origin (`_origins`, `GetWorldOrigin`, `AppliedBeamOrigin`; `GetWorldCrown` removed). A changed `BeamOrigin` rebuilds only the 595 near transforms, writes the 595 far-layer transforms (`EclipseHubFarLayer.SetOrigin`) and re-encodes the queried decks relative to the new origin from the stored absolute deck altitudes (one COLOR write per beam that has a deck, no weather query). No custom data write, no timeline bind, same MultiMesh. `_Process` applies the settings before the first bind, so the first build already uses the right origin. The in-cloud data is now "metres above the beam origin".
- Shader (`ring_eclipse_hub_beam_common.gdshaderinc`): uniform `close_soften_m`; the light is multiplied by `smoothstep(0, 1, view_distance / close_soften_m)`, where the view distance is to the beam-axis point of the fragment (two new flat varyings). It leaves the depth core untouched. The base fade (400 m) now starts at the iris. Beam lengths are 4,996.8-5,002.0 km (1.94 km longer regular, 4.19 km main); tilt unchanged (max 1.563 degrees).
- Tests: `EclipseHubBeamLayerTest` (anchor heights for all three modes, origin verticals, a switch through Crown, Reactor and Base: exactly one rebuild and 595 near plus 595 far transform writes, 0 custom data writes, 0 binds, same MultiMesh, endpoints within 1 mm of the independent topology evaluation, tips at the hub, crown-to-origin distances 1,943.05 / 4,189.05 m (Base) and 4,704.05 / 5,911.05 m (Reactor), no rebuild for an unchanged origin or a look change, `close_soften_m` on the near and core material, clamps), `EclipseHubBeamCloudTest` (decks encoded relative to the origin, 30 re-encoded beams after a switch with 30 colour writes and 0 queries, shift equals the origin height difference, round trip drift under 0.5 m), `EclipseEventSettingsSmoke` (round trip, setter and sanitiser bounds, ToCore mapping, older save), G1 (above).
- Captures (`tweaks/t2`; poses by `tweaks/t2_cap.gd`): regular spire (site 488, island) and the main spire, each from the entry viewpoint (inside the hall), looking up 60 degrees, 120 m above the iris looking down, and from 5 km. The beam now rises out of the bore through the lattice from the ground up; at 5 km a flat glow sits at pad level where the base fade and the terrain cut it. From inside the hall the 12x core fills the bore opening. Softening: at 0 and 300 m the above-iris frame is a white-out; at 1,000 m the lattice and the iris read through a grey-white column (`tweaks/soften0`, `soften1500`, `soften1000`); the 5 km frames are unchanged at every value, so 1,000 m is the default. Looking straight up the bore from inside stays bright by design.
- **Storm check with the new default fails its 5x gate and needs a decision.** With Base: noon 2.18x, dark 2.25x (`tweaks/storm12`). With `--set=BeamOrigin:0` (Crown) the same build gives **10.02x and 51.30x** (`tweaks/storm12c`), so the depth core itself is unchanged. The ratio is diluted: the beam now also shows in clear air below the cloud deck (the lit lattice interior), which both the halo-only and the core frames show equally, and the mask is every pixel the beam changes. Re-measured on the saved frames over the rows above the spire crown only (the in-deck column): **111x noon, 116x dark**; below the crown 1.3x. Not changed here (it is a gate decision); the capture mask should be restricted to the in-deck rows.
- Hub-off and neutral runs: `eclipse_darkness_capture` 9/9 on its second run (the first failed `alt_down_totality_darkens` at 0.904: that frame had the live-tuning drawer open with a spawned storm in it, an outside interaction with the capture window, not a render change; the rerun gives 0.644).

### Hub-off reference and storm gate (2026-10-17, after T0-T2 accepted)
- **New hub-off reference: `scratchpad/base3` and `scratchpad/base4`** (current tree, T0 + T1 + T2). The old `base1`/`base2` are retired: they had the water draw-order bug (links over the sea cut at the shoreline) and the old 1,743 m main relay (the knot fan).
- **Protocol.** `base3` is a plain run (no `--ref`). `base4` must be a run with `--ref=base3`, not a second plain run: a run that takes its clock times from a reference differs systematically from a plain run in the cloud-driven `main_high_day` frame (changed fraction about 0.0015, the plain run searches the clock times and leaves the cloud state different). Two plain runs agree to 0.0001 and then fail a ref-mode check (0.0019 against a limit of 0.0011). With `base4` made in ref mode the cross-process noise includes that offset and the gate passes. The check is `--mode=parity --ref=base3 --ref2=base4`.
- **Result:** base5 and base6 (ref-mode runs while the protocol was being found) and the first base7 attempt are not valid evidence: base5/base6 failed `main_high_day` for the reason above, the first base7 started from a different base yaw (every ground and alt_down state 30-70 % changed; a startup variation, not a render change). The accepted check, `scratchpad/base7` against `--ref=base3 --ref2=base4`: **15/15 PASS** (worst: `alt_down_day` 0.00167 against 0.00209, `main_high_day` 0.00221 against 0.00297).
- **Storm check fix (`tools/eclipse_hub_capture.gd`, `--shots=storm`).** With `BeamOrigin` Base the beam also shows below the crown (the lit lattice interior) and in the clear air under the deck, equally in the halo-only and core frames, which diluted the core-recovery ratio to 2.2x. The mask now keeps only the pixels in the rows above the higher of the spire crown and the cloud deck base on the beam (`_deck_base_row`, using the new `EclipseHubBeamLayer.CrownHeightAboveOrigin`; header documented). The 5x gate is unchanged. Result with the grid drawn: **Base 20.02x noon, 54.52x dark; Crown (`--set=BeamOrigin:0`) 19.58x and 53.74x**; diffusion keep 1.09 / 1.07, no cloud refresh (5 to 5). (Measured with the deck-base-only mask first: 3.33x and 4.05x, because the rows between the crown and the deck base still contain the lattice interior.)

### Tweaks T3 implementation notes (2026-10-17)
**Design as built** (the plan R2, orbs only, binding decisions 1 and 2):
- **Orb quad.** `EclipseHubBeamLayer.BuildRibbonMesh` appends one 4-vertex billboard quad (`UV2 = (-1, -1)`, `UV` = the corners; 68 vertices, 102 indices). The same MultiMesh draws it in the near layer, the depth core pass and the far layer (the 594 orbs also climb the starburst); no extra draw call and no per-frame per-beam write.
- **Shader** (`ring_eclipse_hub_beam_math.gdshaderinc`, `..._common.gdshaderinc`): `ehb_orb(fire_tau, kind, p_site, column) -> (envelope, fraction)` and `ehb_orb_level`. The vertex stage places the quad at `origin + span * fraction`; the radius is the ribbon rule (`ribbon_profile`, core sigma `OrbRadiusMeters`, minimum `OrbMinPixelRadius`, energy-conserving with the beam's far compensation); layer weights and atmosphere are taken at the orb centre; the quad collapses when the envelope is 0, out of range or behind the near plane. The fragment is a radial white Gaussian core plus a teal exponential halo (amplitude 0.25), and the core pass writes the orb's depth footprint (`ribbon_depth_core_covers` on the radial distance), so clouds behind an orb do not dim it. The beam body no longer uses `ehb_level_refuel` / `ehb_lit_refuel` (they stay in the include and the twin as the launch-kick envelope and for their parity cases): a regular beam is fully dark between orbs, and nothing relights it after the hand-off. Orbs vanish at the cut.
- **Motion and envelope.** Launch (age 0) is the grid normal front passing the spire; `fraction = (age / OrbTravelSeconds)^OrbEaseExponent` (exponent 3: 6.07 km up at 3.2 s, exactly 1 at 30 s); envelope = smooth rise over `FlashClimbSeconds`, 1 while travelling and for `FlashHoldSeconds` at the hub, then fade over `FlashFadeSeconds`. Only passes inside [Sustain, Instability) launch an orb; the main spire and dead sites have none.
- **Fronts.** `EmitterGridPulseClock.GetFrontsWithin(t, column, maxBehindMeters, span)` is `GetFronts` with the age cutoff replaced by the argument (`GetFronts` unchanged). The layer publishes fronts up to travel + hold + fade + 2 s behind the loop end during `[TauSustain, TauInstability + travel + hold + fade + 1)`, so a travel time above about 38 s keeps the last rows' orbs (T = 55 test: the last row of column 0 still has envelope 1.000 at age 50 s, while the legacy lookup drops that front and gives 0).
- **Hub heartbeat on arrival.** `EclipseEventTimeline.HeartbeatAt` now takes, per column, the newest front that has a spire passed at least `OrbTravelSeconds` ago (the previous front supplies it just after a launch) and kicks `exp(-(age - travel) / HeartbeatTau)`: nothing before the first arrival (first Sustain arrival at tau 222.83 = Sustain 191.50 + 30 + the first pass offset; zero before, 0.0189 after), and a kick within one frame of the arrival at TimeScale 1 (16.7 ms) and 10 (166.7 ms). The relaunch kick and the stream's refuel event stay at the launch (audio / PDA cue). `EclipseEventTimeline.RefuelOrb(entry, tau, gridTime, ease)` is the pure timeline function; `EclipseHubBeamMath.Orb` / `OrbLevel` are the shader twins (`EclipseHubBeamUniforms.Orb` = ev_orb).
- **Bore light.** Unchanged code path: `CrownLevel` already measures the launch envelope (`RefuelFlash`, `FlashClimb`/hold/fade from the pass), so the bore plug light boost is the short kick at launch and is no longer tied to a bolt. The crown ember (`CrownGlowEmber*`) is kept.
- **Status line** counts "orbs in flight n"; `LitBeamCount` no longer counts flash-relit beams (lit stays 1 in Sustain, about 60 orbs in flight).
- **Settings** (persisted, grave-key): timeline `OrbTravelSeconds` 30 (5-60, a change rebuilds the timeline); beams `OrbEaseExponent` 3 (1-4), `OrbRadiusMeters` 350, `OrbHaloMeters` 1,500, `OrbMinPixelRadius` 2.5, `OrbEmission` 16. The flash sliders are relabelled "Orb launch fade-in", "Orb hold at hub", "Orb fade at hub"; heartbeat kick tooltip says arrival. An older save loads 30 s and the new defaults.
- **Golden.** `tools/fixtures/eclipse_event_timeline_golden.json` was regenerated: only the Sustain-time `Level`, `HubDirect`, `HubDay` samples moved (by 0.001-0.004, the heartbeat term landing on arrival instead of the pass).
- **Tests.** `EclipseHubBeamRefuelTest.CheckOrbs` (in `eclipse_hub_beam_layer_test`): twin against `RefuelOrb` over 500 (site, time) pairs (470 alive, worst envelope 1.5e-5, fraction within 2e-4), lift-off 6.07 km at 3.2 s, arrival fraction 1 at the travel time, no orb before launch, in Ignition, for the main spire, dead sites, without fronts or after the cut, T = 55 last row, heartbeat on arrival at TimeScale 1 and 10, 0 per-beam writes in steady frames, mesh shape (68 / 102, orb quad), uniform plumbing. `EclipseHubBeamShaderFixture` / parity shader: 12 fields per sample (new: orb envelope, orb fraction, orb level), tolerance 4e-3, worst GPU error 2.7e-6 / 4e-7 / 1.1e-6 over 605 orb samples in `sustain_flash`. Settings smoke: round trip, bounds, `ToCore`, older save. `EclipseHubBeamCloudTest`: lit count stays 1 in Sustain, orbs in flight counted.
- **Captures** (`scratchpad/tweaks/t3`, from `tools/eclipse_hub_capture.gd --shots=s8`, reworked for orbs): `s8_before`, `s8_launch` (+0.4 s), `s8_lift1s`, `s8_lift3s`, `s8_lift6s`, `s8_gone`, and the ground-up and 150 km poses at orb ages 12 s, 22 s, arrival and hold. Read: at launch a bright halo sits on the spire base while the beam is dark; at 3.2 s the orb is a bright glow at the top of the 5 km frame (about 6 km up) and about 60 other orbs show as small dots around the other spires; at 22 s the six orbs of a row show as a line of dots climbing the main pillar and at arrival they have reached the top edge. The 150 km pose looks along the main pillar, so the hub itself is not framed.
