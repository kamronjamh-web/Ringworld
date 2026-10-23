# Ringworld → Unreal Engine 5 (C++) Rewrite — Master Plan

Status: draft for review · Created 2026-10-23  
Decisions locked by the user (2026-10-23):

- **Rewrite, not port.** Godot code is a behavioural/visual reference only. Use UE-native systems wherever they genuinely fit the ring; build custom where they do not.
- **Engine:** latest UE 5.x stable (pin the exact version at Phase 1 kickoff). C++ first; Blueprints only for tuning/UI glue.
- **Godot project:** frozen as reference after today's final commit. No new features land in Godot.

---

## 1. What we are rewriting (inventory summary)

| Area | Godot today | Size | Rewrite stance |
|---|---|---|---|
| Ring topology, canonical coords | `scripts/Voxel/Core/*`, `RingCanonicalPosition.cs` | small, pure | **Transliterate verbatim** (spec) |
| Terrain generator gen-17 + sites + caves | `TerrainGenerationHeightModel.cs`, `Terrain/Sites/*`, `Voxel/Phase3/Cave*.cs` | medium, mostly pure C# | **Transliterate verbatim**, golden-tested |
| Smooth voxel backend + Transvoxel mesher | `SmoothTerrainGrid.cs`, `native/ring_voxel_mesher` | large | Port mesher C++ directly; rebuild chunk scheduler UE-native |
| Terrain3D surface backends | `Terrain/Surface/*`, `third_party/Terrain3D` + patches | large | **Drop** (single SDF authority — "direction B") |
| Legacy block voxel / hybrid / SDF pilots | `Voxel/Phase1,2,5,7`, pilots | large | **Drop** |
| Proxy clipmap / regional HLOD / analytic far ring | `Voxel/LOD/*` (132 files), `RingScaledFarViewport.cs` | very large | Redesign: GPU clipmap + LWC, no scaled sub-viewport |
| Atmosphere/haze, sky LUT | `materials/ring_haze_shared.gdshaderinc`, `scripts/Atmosphere` | medium | **Custom render pass** (Sky Atmosphere can't do a concave ring) |
| Celestial, eclipse, false hub sun | `RingDayNightCycle.cs`, `Celestial/Eclipse/*` | medium | Logic transliterated; lighting custom |
| Shadows | `scripts/Shadows/*` | medium | VSM + contact (native) + custom heightfield/macro mask |
| Clouds + weather + lightning | `addons/SunshineClouds2` (modified), `Weather/*`, `Lightning/*` | large | Custom cylinder raymarcher; sim in C++; Niagara for VFX |
| Water | `third_party/godot_ocean_waves` (MIT), `RingWater*` | medium | Port FFT to HLSL compute; custom curved ocean mesh |
| Vegetation / grass / wind | `Vegetation/*`, GPU grass, shared gust field | medium | HISM/Nanite foliage, custom cylinder placement, MPC wind |
| Player / gravity / flight / swim | `RingVoxelExplorerController*.cs` (~3.2k lines god class) | medium | CMC subclass with radial gravity + separate double-precision flight pawn |
| Underground mode | `scripts/Underground/*` | small, pure | Transliterate (probe + state machine) |
| POIs (emitters, main spire, Weather Lung, grid beams) | `scripts/poi/*`, `assets/poi/*` | medium | Logic port; Nanite meshes from Blender sources |
| Saves / world slots | `RingWorldSaveManager.cs`, `world_session.gd` | medium | New format; **no Godot save compatibility** |
| UI (start menu, Foundry, pause, HUD, minimap, loading) | `scripts/ui` (98 files, ~14k lines), launcher 2k lines | large | CommonUI/UMG rebuild |
| Grave-key tuning drawer | `RingLiveTuningDrawer*.cs`, 22+ settings categories, ~300–500 tunables | large | Reflected settings framework (see §5) |
| Diagnostics, tests, benchmarks | `scripts/diagnostics`, `tools/` (~620 files), no CI | large | Automation Specs, Gauntlet, CSV/Insights, **add CI** |

---

## 2. Guiding principles

1. **Freeze the Godot code as the reference; it does not become the architecture.** Every subsystem is written fresh against a written spec. Godot output is only used to *verify* the new code, through golden data, screenshots and perf baselines.
2. **Pure core, thin engine shell.** Ring maths, generators, timelines and state machines live in engine-agnostic C++ modules (no `UObject`), unit-tested headless. UE classes are adapters.
3. **One authority per concept.** One terrain density field and one coordinate model. Each tunable has one settings home. No parallel backends.
4. **De-risk before building.** The ring breaks many UE assumptions (§3). Each assumption gets a time-boxed spike with a go/no-go gate before the code that depends on it is written.
5. **Determinism is a feature.** Seeds → identical world, independent of job order, rebase or platform. Goldens enforce this.
6. **Conventions carry over.** The AGENTS.md grave-key rules still apply: only genuine tunables in the menu; POIs automatically inherit haze, shadows and lighting; debug/A-B switches go in debug tooling.

---

## 3. Where UE's assumptions clash with a ring habitat

| UE assumption | Ring reality | Plan |
|---|---|---|
| Fixed gravity / Z-up | Up = inward radial, varies per point | `UCharacterMovementComponent::SetGravityDirection` per tick (5.4+); all "up" queries go through `RingFrame` helpers |
| Sky Atmosphere = convex sphere | Concave floor, air capped at 20 km, sidewalls, far arch overhead, re-entering rays | Custom atmosphere: RDG compute LUTs + `SceneViewExtension` sky/aerial-perspective pass |
| Volumetric Clouds tied to the planet sphere | Cloud deck is a cylindrical shell 2–6 km above the floor | Custom raymarcher (global shader) ported from the modified Sunshine Clouds math |
| Exponential Height Fog = Z-up | Height is radial | Haze function in our own pass; height fog only as a local fallback if it's used at all |
| Landscape / Water / Grass / PCG = flat heightfield | Curved, wrapping, 31,416 × 318 km | Custom terrain meshes; PCG only with custom cylinder samplers; custom ocean |
| World Partition = finite authored grid | Procedural, wrapping cylinder | Custom streaming for terrain; World Partition (or level instances) only for authored POI interiors |
| One directional sun | Far arch is lit from "behind"; hub sun lights every point from its own zenith | Directional light for the near field + custom hub-light / ringshine shading term in materials and far tiers |
| Planet-scale depth via LOD | Far arch 10,000 km away | LWC + reversed-Z; spike before building far tiers (§6 Phase 2) |
| Lumen sky light from "above" | Sky includes a lit, emissive arch | Lumen is an **open question**: spike it against procedural geometry; fall back to SSAO + custom ambient (CPU irradiance port) |

---

## 4. Target architecture

### 4.1 Repository and project

- **New repository** `RingworldUE/` (recommended). The Godot repo gets the `godot-final` tag and becomes read-only. Asset storage: Git LFS (solo dev) or Perforce (team). **Decision needed.**
- UE C++ project with a game module plus plugins under `Plugins/Ring*` so that core modules stay engine-agnostic and testable.

### 4.2 Modules

| Module | Type | Contents | Godot reference |
|---|---|---|---|
| `RingCore` | pure C++ (no UObject) | Topology (`CertifiedRingScale`, Mega Ring indices), `RingCanonicalPosition` (int64/double), wrap/FloorDiv, FNV definition hash, `RingFrame` (local tangent frame), double simulation clock | `Voxel/Core/*`, `RingCoordMath.cs` |
| `RingGen` | pure C++ | Vendored FastNoiseLite (same version Godot 4.8 bundles) + Godot-wrapper shim, gen-17 height model, recipe v1–v8, emitter site registry + shaper, cave pipeline, baseline density composer | `TerrainGenerationHeightModel.cs`, `Terrain/Sites`, `Phase3/Cave*` |
| `RingVoxel` | UE runtime | Density field service, SDF edit journal, Transvoxel mesher (port of `ring_voxel_mesher.cpp`, smooth paths only), chunk scheduler with revision epochs, async Chaos collision cooking | `SmoothTerrainGrid.cs`, native mesher |
| `RingTerrainLOD` | UE runtime + shaders | GPU heightfield clipmap rings (curvature in VS), regional HLOD bands, analytic whole-ring mesh, coverage masking/geomorph | `Voxel/LOD/*`, `RingTerrainLodChainPlanner.cs` |
| `RingRender` | UE runtime + global shaders | `SceneViewExtension`: atmosphere/haze LUTs, sky, heightfield shadow clipmap, macro ring shadow, cloud raymarch + cloud shadows, rain haze composite, hub-light term | `materials/*.gdshaderinc`, `scripts/Shadows`, `scripts/render` |
| `RingEnvironment` | UE subsystems | Celestial clock, eclipse event engine, weather systems sim, wind field (MPC), lightning generator, water FFT (HLSL) | `Celestial/*`, `Weather/*`, `Lightning/*`, ocean waves |
| `RingFoliage` | UE runtime | Grass cards (GPU placement on the cylinder), trees HISM/Nanite + impostors, streaming hysteresis | `Vegetation/*`, grass compute |
| `RingGameplay` | UE gameplay | Radial-gravity CMC, swim, flight/noclip pawn (double integration, cross-ring handoff), underground mode, Enhanced Input | Explorer controller, `Underground/*` |
| `RingPOI` | UE gameplay | Emitter streamer (pure state machine core), stand-in ISM layer, grid beam layer, Weather Lung, main spire, POI shadow extents → VSM | `scripts/poi/*` |
| `RingSettings` | UE runtime | Tuning framework + grave-key drawer (§5) | `RingLiveTuningDrawer*`, `Gameplay*Settings/Defaults` |
| `RingSave` | UE runtime | Slot metadata, versioned world state, binary region edit journals, fail-closed validation | `RingWorldSaveManager.cs` |
| `RingUI` | UE + UMG/CommonUI | Start menu, World Foundry, pause, HUD, minimap, loading, live menu world | `scripts/ui`, `start_menu.gd`, launcher |
| `RingDiagnostics` | UE runtime | Stat groups, CSV profiler categories, GPU markers named after the Godot attribution rows, perf overlay | `scripts/diagnostics` |
| `RingTests` | Automation | Specs, functional tests, golden fixtures, Gauntlet routes | `tools/*` |

### 4.3 Key architecture decisions (each one gets an ADR in `docs/adr/`)

1. **Coordinates.** Authoritative position = canonical (around int64-chunk + local double, vertical, across). UE world = LWC doubles relative to a player-anchored origin, rebased at 32,768 m sectors as today. Decide the ring-axis → UE-axis mapping once and document the handedness flip (Godot frame tangent/inward/+across is left-handed; see `docs/emitter-poi-streaming-spec.md` E3).
2. **Terrain authority.** A single smooth SDF field on gen-17 ("direction B", already the user's decision in `docs/voxel-cave-direction-handoff.md`). Heightfield tiers are *derived* from the same generator, so the surface-to-cave dig handoff disappears as a problem.
3. **Far ring.** No scaled second world. A single scene with LWC + reversed-Z, gated by spike S1. Fallback: a custom far pass rendered into a separate depth range inside the `SceneViewExtension`.
4. **Mesh component.** RealtimeMeshComponent vs `UDynamicMeshComponent` vs raw `FPrimitiveSceneProxy`, decided by spike S2 on throughput and async collision. A custom mesher is preferred over Voxel Plugin (wrap, canonical coords, determinism, licence).
5. **Lighting.** VSM for the sun near field, built-in contact shadows, custom heightfield + macro shadows beyond. Lumen on or off is decided by spike S6.
6. **POI meshes.** Nanite, re-exported from the Blender sources in `prototypes/` (not from the Godot-baked `.res`).
7. **Time.** A double simulation clock in a world subsystem. Never global time dilation. The cloud clock stays separate from the celestial clock, as today.

---

## 5. Live tuning (grave key) framework — built in Phase 1, extended in every phase

Mirrors today's pattern (sparse overrides over a captured baseline, validated limits, atomic save, presets, legacy-key migration):

- One `UDeveloperSettings`-derived class per category (`URingWindSettings`, `URingCloudSettings`, …) with `config=GameUserSettings`. Limits come from `ClampMin/ClampMax` metadata, replacing today's `Limits` tables.
- **Sparse override layer:** only values that differ from the class defaults are persisted; one ini section per category. Saves are debounced and atomic.
- Each property is auto-mirrored to a CVar `ring.<Category>.<Key>`, so values are scriptable from the console, from automation tests and from Gauntlet.
- **Drawer:** the grave key toggles a runtime drawer (CommonUI, or ImGui for the dev build), *generated by reflection*: tabs = categories, sections = `meta=(Category=...)`. Each new feature only declares properties; no hand-built UI.
- Tab order matches today: Lighting, Atmosphere, Shadows, Clouds, Quality, AntiAliasing, Motion, LODs, FarRendering, Loading, Terrain, Water, Wind, Minimap, Grass, Vegetation, Emitters, WeatherLung, Diagnostics.
- Debug/A-B switches use a separate `ring.debug.*` CVar namespace and are **not** reflected into the drawer.

---

## 6. Phased roadmap

Effort is relative (S < M < L < XL). Each phase ends with a **gate**: the criteria must pass before the next phase starts.

### Phase 0 — Freeze and extract the spec (M)
- Land today's final Godot commit and tag it `godot-final`. Archive the toolchain notes (`docs/godot-48-native-texture-streaming.md`) so the reference build stays runnable.
- **Golden data export** (run in Godot, commit to the new repo under `Goldens/`):
  - gen-17 height grids at fixed seeds/regions, including seams, the wrap point and emitter sites
  - cave baseline density bricks
  - emitter site registry and grid topology
  - eclipse window/timeline samples
  - weather fingerprint
  - canonical↔world transform samples
  - existing fixtures in `tools/fixtures/`
- **Reference capture set:** about 40 fixed poses × times of day (noon, dusk, night, eclipse, ignition, storm, underground, coast, far arch), at fixed settings, in PNG.
- **Perf baselines:** the existing `docs/benchmarks` plus a fresh run on the certification hardware.
- **Spec docs:** one concise spec per subsystem, under `RingworldUE/Docs/Spec/`, distilled from the current docs. Formulas and constants only; the agent reports in this session are the starting index.
- **Resolve the known inconsistencies:**
  - terrain floor: -8,192 m in code vs -2 km in `FINAL_RING_SCALE_TARGET.md`
  - gravity: 32 vs 42 m/s²
  - the planned v2 cave placement policy
- **Licence audit:**
  - LOLIPOP trees are CC BY 4.0 and need credits.
  - `timeline_1_outside_ambience.ogg` has no recorded provenance.
  - Verify the SunshineClouds2 and Terrain3D licences before deriving shader maths.
  - The MIT notice for the ocean FFT must be kept.
- Exclude `tools/ninjaone/`, `prototypes/pda-voice` model weights and the legacy scenes.

**Gate:** goldens and captures are reproducible from the tag, spec docs reviewed, and the open decisions in §9 answered.

### Phase 1 — Foundation (M)
- UE project, module skeleton (§4.2), coding standards, `.editorconfig`, clang-format.
- **CI** (none exists today): build plus headless Automation run on every push.
- Update `AGENTS.md` / `.claude/agents` / `.codex/agents` for the UE repo (build commands, test commands, conventions).
- `RingCore` complete, with ported self-tests (`RunSeamWrapSelfTests`, `MegaRingTopology.RunSelfTest`) and transform goldens.
- `RingSettings` framework + grave drawer skeleton; `RingDiagnostics` stat groups.
- **Gate:** CI green; RingCore matches goldens bit-exactly where specified.

### Phase 2 — Risk spikes (L) — time-boxed, throwaway code allowed

| # | Spike | Pass criteria |
|---|---|---|
| S1 | Far arch at 10,000 km in one scene (LWC + reversed-Z), plus an analytic ring mesh | No z-fighting or jitter; ≤ 1.5 ms GPU; beats today's 4.8 ms CPU / 2.6 ms GPU far viewport |
| S2 | Curved chunk meshes + async Chaos trimesh cooking + mesher throughput | No main-thread install > 1 ms; collision matches render; 32 m chunk mesh < target ms |
| S3 | Radial-gravity character walking, jumping, swimming on curved collision, plus rebase | Stable at 0, 1,000 and 15,000 km around the ring; no snapping on rebase |
| S4 | Custom atmosphere pass via `SceneViewExtension` (sky LUT + haze on opaque) | Visual match to reference noon/dusk captures; ≤ 1.5 ms |
| S5 | Cylinder cloud raymarch at reduced quality | Path to ≤ 8 ms GPU (today 21–26 ms) |
| S6 | Lumen vs no-Lumen on procedural terrain + far arch | Decide; document the fallback |
| S7 | gen-17 C++ transliteration on one region | Bit-identical (or within an agreed ULP) to goldens |

**Gate:** each spike passes or gets an accepted fallback written into its ADR. This is the main go/no-go point for the whole rewrite.

### Phase 3 — Walkable ring, vertical slice 1 (XL)
- `RingGen` complete (gen-17, v1–v8 recipes, sites), golden-tested.
- `RingVoxel` near field (1 m smooth chunks, Transvoxel, edits via the SDF journal, async collision).
- `RingTerrainLOD`: clipmap proxies with geomorph and coverage masking → regional bands → analytic ring.
- `RingGameplay`: character, flight presets (24 → 1,000,000 m/s), cross-ring noclip handoff.
- Basic sun/day-night clock, minimal save slot + world state, debug-only start flow.
- **Gate:** fly from spawn to the opposite side of the ring and back with no holes, no hitch > 33 ms, and terrain matching the goldens. Walk and dig at the surface.

### Phase 4 — The look (XL)
- Atmosphere/haze full spec (Physical as default, Cinematic preset, storm Mie, remote shell, sidewall clipping).
- Shadows: VSM near, contact shadows, heightfield clipmap (curvature-correct clearance), whole-ring macro mask, cloud shadows.
- Clouds (near raymarch + far weather bake), weather systems sim, rain curtains (Niagara), lightning visible across the ring.
- Water: FFT cascades (HLSL), curved ocean mesh, analytic lakes/rivers/far water, GPU waterline query for swimming.
- Vegetation: grass cards with terrain-matched colour + hero blades, trees HISM/Nanite + impostors, shared gust field.
- **Gate:** reference captures match by side-by-side review; GPU budget on the A1000 is at or below today's numbers.

### Phase 5 — Underground and digging (L)
- Cave pipeline on gen-17 (v2 placement policy), interval-proof chunk skipping, cave LOD reach of about 4 km.
- Underground mode (17-ray probe, state machine, participants, fog coupling, Surface/Underground profiles).
- Surface-to-cave digging works natively (single authority).
- **Gate:** the cave certification route (port of `tools/run_cave_certification.ps1`) passes; Supermassive cavern loads in ≤ 6 s.

### Phase 6 — Megastructure content and the eclipse (L)
- Emitter sites (595), stand-ins, streamer, grid beams + pulse, main spire, reactors underground, Weather Lung breathing cycle.
- Eclipse false-sun event E0–E7 per `docs/eclipse-false-sun-spec.md`: near pitch-black totality, ignition fronts, hub light from the zenith at 80% of the sun, collapse.
- Audio: weather/water buses → Submixes; underwater low-pass.
- **Gate:** the eclipse timeline matches the goldens; the emitter capture route passes.

### Phase 7 — Product shell (M)
- CommonUI start menu, 3 world slots, World Foundry (presets as `UPrimaryDataAsset`), pause/settings, HUD, minimap, loading screen with time-sliced init (8 ms budget pattern), and the live warm-world menu backdrop with its flight director.
- Suit PDA announcer hook (the LLM-driven lines stay a separate service).
- **Gate:** full user flow from a cold boot works, and the save round-trip is fail-closed.

### Phase 8 — Certification and performance (L)
- Targets from `FINAL_RING_SCALE_TARGET.md`:
  - Arc 140V at 1080p High: 60 fps target, 45 fps floor, ≤ 6 GB working set
  - RTX 3060 at 1440p Very High: 60 fps
- Gauntlet flight routes (ports of the PowerShell harnesses), CSV profiler budgets enforced in CI nightly.
- **Gate:** certification passes on both hardware tiers.

**Explicitly out of scope for v1:** Mega Ring rendering (keep only its topology tests), the Sunblocker demo, the Shorts capture director, the biome workshop (re-evaluate as an Editor Utility Widget later), and multiplayer (the edit journal stays replication-friendly).

---

## 7. Verification strategy

| Layer | Tool | Source of truth |
|---|---|---|
| Ring maths, generator, caves, eclipse timeline, site registry | Automation Spec (headless, CI) | `Goldens/` exported in Phase 0 |
| Settings persistence round-trip per category | Automation Spec | Mirrors today's `*SettingsSmoke` |
| Rendering look | Screenshot comparison + human side-by-side review | Phase 0 capture set |
| Streaming, readiness, hitching | Gauntlet routes + CSV profiler | `docs/benchmarks` + certification baselines |
| GPU/CPU attribution | Insights + named GPU markers matching the Godot rows (`GPU.Clouds.Raymarch`, …) | `docs/performance_diagnostics.md` method (p50/p95/p99, A-B-A captures) |

Test harnesses must run against an isolated save/config directory (same rule as the current PowerShell harnesses).

---

## 8. Risk register

| Risk | Impact | Mitigation |
|---|---|---|
| Far-arch depth or precision fails in one scene | Far rendering redesign | S1 spike first; fallback custom far pass |
| Custom atmosphere/clouds become an engine-within-an-engine | Schedule | Port the maths, not the architecture; keep budgets from §6; reuse UE's RDG/SceneViewExtension plumbing |
| UE version upgrades break `SceneViewExtension`/RDG custom passes | Maintenance | Pin the version; isolate render code in `RingRender`; upgrade deliberately per milestone |
| Generator determinism drifts from Godot (FastNoiseLite wrapper, float order) | Worlds differ from reference | Vendor the identical FNL; port Godot's wrapper semantics; goldens in CI |
| Main-thread hitches (collision/mesh install) carry over | Perf | Async cooking from day one; a per-frame install budget enforced by a stat |
| Lumen incompatible with procedural geometry or ring scale | Lighting quality | S6 decides early; the CPU irradiance port is the fallback |
| Scope creep: rewriting legacy paths | Schedule | §1 drop list is binding; legacy is never ported |
| Licence: CC BY trees, unknown-provenance audio, modified addons | Legal | Phase 0 audit; credits screen; replace unknown assets |

---

## 9. Open decisions for the user

1. **Repository:** new repo (recommended) vs a subfolder of this one; Git LFS vs Perforce.
2. **Exact UE version** to pin (latest 5.x stable at kickoff).
3. **Canonical terrain floor:** -8,192 m (code) vs -2,000 m (scale target doc).
4. **Gravity:** 32 m/s² (current) vs another value. Both current values are high for a 1 g habitat.
5. **Multiplayer:** ever? (Affects whether gameplay is built on replication from Phase 3.)
6. **Lumen:** acceptable to defer to spike S6, or is there a strong preference?
7. **Mega Ring:** keep as a topology-only test (recommended) or as a playable mode.

---

## 10. Agent workflow for the rewrite

This follows the AGENTS.md role table:

- **Primary** owns this plan, the ADRs and the phase gates, and reviews everything.
- **Lead** writes each subsystem spec from the Godot reference and designs the UE module before implementation. Spikes S1–S7 are Lead (or **Deep** for S1/S4/S5) tasks.
- **Implementer** does the transliteration work (`RingCore`, `RingGen`, cave pipeline, timelines, settings categories) against goldens, one module or recipe version at a time.
- The Godot repo remains readable for reference; agents must not modify it after `godot-final`.
