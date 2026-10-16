# Emitter Relay Grid and Climate Machines (tier-3 ring-spanning systems)

Status: design spec, read-only investigation, 2026-10-16. Revised the same day after the user answered U1-U4 (Wall Engines; grid coupled only through weather; one-way pulse circuit; 6 of 200 machines offline). No project files were edited apart from this document.
Companions:
- [emitter-site-terrain-spec.md](emitter-site-terrain-spec.md), the "site spec".
- [emitter-poi-streaming-spec.md](emitter-poi-streaming-spec.md), the "POI spec".
- [emitter-milestone1-handoff.md](emitter-milestone1-handoff.md), "M1".
- [weather-system-checklist.md](weather-system-checklist.md).

Scope: two new systems for gen-17 runtime-surface worlds.

- **Part G, the emitter relay grid.** Along-ring light links between the existing 595 emitter sites. They are mostly invisible and are revealed by a one-way pulse that circles the whole ring every 5 minutes.
- **Part C, climate machines.** Wall Engines: a separate, seed-fixed network of 200 weather machines built into both rim walls. Six are offline, and each offline machine parks a permanent storm or drought.

The binding user decisions are at the end of the document. Section §U records how U1-U4 were resolved and lists the lead defaults that still stand.

---

## 0. Findings that shape the design (evidence)

| # | Finding | Evidence |
|---|---|---|
| F1 | **Grid geometry.** The registry has 100 grid rows at pitch C/100 = 314,159.36 m. That is **314 km, not 317 km**; after the ±4 km nudge a link is 306-322 km long. Each row has 6 columns at across 26.5 + 53·j km. One "main row" holds a single main site on the centre line (159 km) instead of six regular sites. Regular indices are compact: `(row < MainRow ? row : row−1)·6 + col`, and the main index is 594. | `EmitterSiteRegistry.cs:55-66, 117-118`; site spec F14 |
| F2 | **The registry already exposes the API a link builder needs.** It has `RegularIndex(row,col)`, `MainRow`, `MainIndex`, `Count`, `Identity`, and signed-wrap helpers. The O(1) influence lookup relies on `R_inf + nudge < half pitch` on both axes, and `AssertInvariants` enforces it. Any new terrain-shaping structure must keep its influence disjoint from every site's influence. | `EmitterSiteRegistry.cs:118, 222-243, 414-443` |
| F3 | **Chord numbers.** Computed with R = C/2π, θ = 2π/100, and both ends at crown height (pad + 1,743 m):<br>- chord length ≈ 313.9-314.0 km;<br>- mid-span sagitta 2,466 m (d²/8R, as the concept note says);<br>- mid-span height above sea level = P̄ + 1,743 + 2,466, which is **4.2 km for pads near sea level** (inside the 2-6 km deck) and 6.2 km for a 2 km pad (at or above the deck top).<br>The chord rises from its end at only s(d−s)/2R: 62 m at 2 km, 304 m at 10 km, 1.3 km at 50 km. Mountains within about 100 km of a low pad will therefore occlude parts of some chords. Golden v4 registry pads span 11-1,728 m with a median of 24 m. | scratch computation; `EmitterSiteFrameMath.cs:166-167`; `tools/fixtures/emitter_site_registry_golden.json` |
| F4 | **Knot links from the main spire.** The main site sits at across 159 km, between columns 2 and 3. A link from the main site to column c of an adjacent row is 315.3 km for c = 2/3, 324.1 km for c = 1/4, and 341.0 km for c = 0/5. | scratch computation; `EmitterSiteRecipe` MainAcrossM |
| F5 | **There are no relay markers on the assets.** The POI scenes have `BeamOrigin`, `ShaftBottom`, `BeamCrownFocus` (1,743 regular / 3,989 main) and `EntryApproach`. The hub beam API (`IEmitterBeamSource`) only aims crowns at the hub. It has no spire-to-spire geometry and no visuals. | `BeamEmitterPOI.tscn:962-971`; `MainSpirePOI.tscn:2300-2309`; `IEmitterBeamSource.cs:13-25`; `EmitterSiteFrameMath.cs:146-159` |
| F6 | **Frame math is double precision and absolute.** `EmitterSiteFrameContext.WorldPosition(around, vertical, across)` produces anchor-relative world positions in double, which are narrowed only at the end. Stand-ins rebuild all transforms synchronously on `CanonicalFloatingOriginAnchorChanged`, never as deltas. | `EmitterSiteFrameMath.cs:67-118`; `EmitterStandInLayer.cs:185-210` |
| F7 | **Day, night and eclipse.**<br>- The global shader uniform `ringworld_solar_visibility` is published by `day_side_atmosphere.gd:683-687`.<br>- `ringworld_effective_solar_day(geometric_day)` multiplies the geometric day by that visibility.<br>- Stand-ins compute the per-point day as `smoothstep(-0.2, 0.35, dot(-radial, sun))`, assuming the ring axis is world Y.<br>- Uniforms `camera_world_position`, `ring_center` and `sun_direction` are set per frame. | `materials/celestial_lighting.gdshaderinc:1-7`; `ring_emitter_standin.gdshader:30-56`; `EmitterStandInLayer.cs:249-269` |
| F8 | **Two saved clocks.**<br>- The weather clock `RingVolumetricCloudLayer.CloudSimulationTimeSeconds` is "authoritative persistent simulation clock for every seeded weather consumer". It advances by the real frame delta and is saved and restored.<br>- The celestial clock is also saved, but it runs at `TimeScale` 1-100×. The day is 4,500 s. | `RingVolumetricCloudLayer.cs:20-21, 582`; `RingWorldSaveManager.cs:119, 122, 285-286, 1023-1030`; `RingDayNightCycle.cs:161-171`; `CelestialConfiguration.cs:8-19` |
| F9 | **Clouds composite after the transparent pass.**<br>- Sunshine is a `POST_TRANSPARENT` compositor effect. It reads resolved depth and blends `mix(scene, cloud, density)` up to scene depth.<br>- An additive beam with `depth_draw_never` is therefore dimmed by every near cloud along its pixel ray, including clouds behind it. A depth-writing transparent (precedent: the ocean) takes part in the depth test.<br>- Near reach is about 72 km; the handoff to far clouds is about 36-48 km. | `addons/SunshineClouds2/SunshineClouds.gd:815-823`; `SunshineCloudsPostCompute.comp:392-431, 476`; `RingSunshineCloudBridge.cs:902-931` |
| F10 | **The main camera draws no sky.** The environment background is switched to `Canvas`, and the sky, far terrain, far clouds and opposite arch are drawn in `RingScaledFarViewport` at 1:1000 as a background layer with no depth interplay. That viewport's camera far plane is 100,000 km real. Its terrain is cut out within 16-625 km. The hooks the POI spec asked for (`AddScaledWorldChild`, public `ScaledPoint`) **do not exist**: `ScaledPoint` is private. | `RingScaledFarViewport.cs:100, 892-938, 2194-2238, 2301-2313, 2614-2673, 3386`; POI spec B.7 |
| F11 | **Main camera reach.** The main camera's far plane is 1,200 km and global LOD fades 500-850 km. **No glow or bloom is enabled in gameplay.** Tonemapping is AgX with auto-exposure, Forward+ HDR. AA defaults to FXAA, with TAA/FSR2 selectable. **No line or ribbon shader with a minimum pixel width exists.** Precedents: `ring_lightning_bolt.gdshader` (unshaded, `blend_add`, `depth_draw_never`), stand-in coverage dithering, and the `far_lightning_glow` minimum footprint. | `world_size_launcher.gd:48-50, 1522, 1619-1621`; `day_side_atmosphere.gd:630-653`; `materials/ring_lightning_bolt.gdshader:2`; `ring_far_cloud_clipmap.gdshader:57-70` |
| F12 | **The weather lifecycle is a pure function of (cell, seed, t).**<br>- 32 km macro cells.<br>- Lifecycle Birth/Develop/Mature/Dissipate/Quiet = 120/300/900/360/420 s.<br>- Spawn chance 0.34.<br>- Uniform field advection by wind (2.2, 0.7) m/s.<br>- Weather seed = terrain seed only: the recipe and terrain hash are **not** mixed in.<br>Consumers treat a rewind, or a jump over 5 s, as a discontinuity and reset. | `RingVolumetricCloudLayer.cs:100-117, 3264-3347, 3688-3906`; `.Lightning.cs:131-158` |
| F13 | **Manual storms only add.** `RegisterManualWeather` is combined with `Math.Max`, is not saved, and its wind history is session-only. Only the Sunshine preview uses it (ID −1003). **No subtractive or drought layer exists.** | `RingVolumetricCloudLayer.Lightning.cs:36-50, 321-345`; `.Wind.cs:12-27, 65-91` |
| F14 | **Lightning is deterministic.** `TryResolveLightningSlot(seed, systemId, now)` gives a 7-24 s cadence and a slot hash. Manual systems get strikes via `TryScheduleManualLightning`. Events are consumed unchanged by local bolts (72 km), far flashes (2 slots) and thunder. A new event source plugs into `ProcessSharedLightning`. | `.Lightning.cs:155-157, 236-254, 284-304`; `RingLightningEvent.cs:7-29` |
| F15 | **Presentation limits for a local storm:**<br>- Sunshine sculpts only the observer's current system.<br>- The far weather atlas samples 128×32 points, about 245 km around.<br>- Rain curtains treat negative SystemIds as manual and use `CloudBaseAltitudeMeters` as-is; only −1003 adds the datum. | `RingSunshineCloudBridge.cs:754-833`; `RingScaledFarViewport.cs:750-770`; `RingDistantRainCurtains.cs:197-206` |
| F16 | **Grass modifier zones** are caller-owned canonical soft ellipses (`SetGrassModifier(id, RingGrassModifierZone)`, at most 256) with tint, tint strength and density multiplier. A change rebuilds the overlapping tiles. They feed both standing grass and card grass. | `RingStandingGrassLayer.Modifiers.cs:28-38`; `RingGrassModifierSnapshot.cs:6-12`; `ring_grass_cards.gdshader:171` |
| F17 | **Structural edge walls rise to 10,000 m** (terrain ceiling 8,000 + 2,000) on both sides. They are drawn by global LOD segments and the far viewport. Sea level is 0, which is also the cloud datum. | `CertifiedRingScale.cs:16-25`; `RingTerrainGlobalLod.cs:1500-1528`; `RingSunshineCloudBridge.cs:108-113` |
| F18 | **Recipe versioning.** `generator_id` v1→v5. v5 enables sites through `EmitterSiteRecipe.Default(seed)` (no JSON fields). The v5 runtime hash appends recipe hash, algorithm version and registry identity. The baseline golden currently has **only gen17_v4 and gen16 cases (no v5)**. | `TerrainGenerationParameters.cs:14-18, 36-39, 66`; `EmitterSiteTerrainShaper.cs:28-41`; `RingTerrainSampler.SurfaceGeneration.cs:226-246`; `tools/fixtures/emitter_site_baseline_golden.json` |
| F19 | **Performance context (A1000, 1600×900).** The CPU floor is 20-23 ms. RenderingServer frame setup is 8.2 ms. The far SubViewport costs 4.8 ms render CPU, and its weather atlas about 2 ms. Sunshine is the largest GPU cost. **Draw calls and main-thread work are scarcer than GPU fill.** | `docs/perf-ranking-2026-10-15.md:23-46` |
| F20 | **Pre-existing, outside this scope:** the fine 3.2 km weather noise (`SampleWeatherNoise`) is not periodic around the ring, so coverage may have a small step at the seam. The anomaly design does not depend on it. | `RingVolumetricCloudLayer.cs:4043-4067` |

---

# Part G: emitter relay grid

## G1. Topology (along-ring only, user decision 1)

Every link is a straight chord between the **relay nodes** of two sites in adjacent rows and the same chain. There are no across-ring links.

| Link class | Rule | Count |
|---|---|---|
| Chain | regular(r, c) → regular(r+1 mod 100, c), for every r where neither r nor r+1 is `MainRow` | 98 × 6 = **588** |
| Knot out | main → regular(MainRow+1, c), c = 0..5 | 6 |
| Knot in | regular(MainRow−1, c) → main, c = 0..5 | 6 |
| **Total** | | **600** |

**Main row as the "knot".**
- Each of the six chains is a **closed loop of 100 links through the main spire**: main → row m+1 → … → row m−1 → main.
- The main spire is a degree-12 junction where all six chains fan in. That matches its lore role as the network's heart and the origin and terminus of the one-way pulse (§G3).
- **Per chain, in the pulse direction, there is exactly one exit link and one return link at the main spire.**
  - Prograde (the default): the exit link is KnotOut (main → row m+1) and the return link is KnotIn (row m−1 → main).
  - Retrograde: the same two links, traversed the other way. The return link is KnotOut and the exit link is KnotIn.
  - The link records keep their geometric (+around) orientation. Direction is applied only in the pulse coordinate (§G3).
- From the ground near the main spire, the 12 knot links splay toward the outer columns at up to 22.9° off the along direction. This gives the main spire a distinctive fan. Every period, the fan lights on the return side and the exit side at the same moment (§G3 relaunch flare).

**Rejected alternatives:**
- Chains skip the main row with 628 km chords. Those have a mid-span sagitta of 9.9 km, which lifts them above the wall tops, and the main spire would be disconnected.
- Chains terminate at the main row, which breaks the closed loop the pulse needs.

**Seam / wrap link.** The ring seam (around = 0 ≡ C) is not special for links:
- An endpoint is a 3D point from `EmitterSiteFrameContext.WorldPosition(A, P + clearance + relayHeight, X)`, and the chord is the straight segment between two such points. Nothing interpolates in canonical space, so there is no wrap artefact.
- The row successor is `(r+1) mod 100`, and the link from row 99 to row 0 is an ordinary chain link (unless one of them is the main row).
- The only parameter that must be wrap-aware is the pulse coordinate p (§G3), which is built with `signedWrap`.

**Relay node height.** The default is `RegularRelayHeightM = 1743` (the `BeamCrownFocus` marker), so relay beams leave the crown. There is technical verification T1 for the main spire:
- `MainRelayHeightM` is a constant, default **1,743 m**. This keeps the six chains level through the knot; 3,989 would tilt the knot links by 0.4°, which is visually indistinguishable but would put them through the main crown structure.
- Confirm against the main spire's connector rings, which were added to its far proxy in M1.
- Later assets add `RelayNode` (regular) and `RelayNode_0..5` (main) Marker3Ds. `EmitterPoiMarkers.TryParse` gains optional relay markers and falls back to the constants when they are absent.

**Data (pure, new file `scripts/Terrain/Sites/EmitterGridTopology.cs`, namespace `RingworldTechDemo.Terrain.Sites`):**

```
readonly record struct EmitterLink(int Index, int FromSite, int ToSite, int Column, EmitterLinkKind Kind,
                                   double P0, double P1, double FromRelayHeightM, double ToRelayHeightM);
enum EmitterLinkKind : byte { Chain, KnotOut, KnotIn }
sealed class EmitterGridTopology {
  static EmitterGridTopology Build(EmitterSiteRegistry r, double regularRelayM = 1743, double mainRelayM = 1743);
  int LinkCount; ulong Identity;            // FNV(registry.Identity, TopologyVersion=1, relay heights)
  ReadOnlySpan<EmitterLink> Links;
  int LinkOut(int site, int column);       // O(1) arrays; -1 when absent
  int LinkIn(int site, int column);
  void FillWorldEndpoints(in RingLodGeometrySnapshot g, Span<Vector3D> a, Span<Vector3D> b); // no allocation
}
```

- Link index order: column-major, then chain order starting at the main spire. Links 0..99 are chain 0 from KnotOut to KnotIn. This makes "the k-th link after the main spire" `c·100 + k`.
- **Pulse coordinate p** (metres along the loop, measured from the main spire):
  - `P0 = wrap(A_from − A_main, C)`, where KnotOut gives exactly 0.
  - `P1 = P0 + signedWrap(A_to − A_from, C)`, where KnotIn gives ≈ C.
  - p is continuous along each loop, including across the ring seam (row 99 → 0). Its only discontinuity, p = C ≡ 0, is at the main spire, which is exactly where the one-way front ends one circuit and the next front launches.
  - Direction-adjusted coordinate used by the pulse: `q = Direction > 0 ? p : C − p`. A front always runs from q = 0 to q = C.

**This applies to v5 and v6 worlds.** The grid is a pure function of the existing emitter registry and touches no terrain. Existing v5 worlds gain the visuals without any terrain or hash change.

## G2. Visibility model (user decisions 4 and 5)

Links are **near-invisible at rest**. They are seen through the pulse, through lightning crawl (§G6), and optionally through a very faint residual.

**Brightness of a fragment:**

```
I = Peak(B) · Pulse(p, t) + Residual(B) + Crawl(link, p, t)
```

- `Peak(B) = mix(NightPeak, DayPeak, B)`. Defaults: NightPeak 1.0, **DayPeak 0.2**.
- `Residual(B) = mix(NightResidual, DayResidual, B)`. Defaults: **NightResidual 0.03**, **DayResidual 0.0**. At 0.03 the residual reads as a hair-thin thread within about 30 km at night and vanishes beyond, which is effectively invisible.
- **Background brightness B (0 = night sky, 1 = full day):**
  - `B = max(dayAt(camera), dayAt(fragment) · smoothstep(50 km, 500 km, viewDistance))`.
  - `dayAt(x) = ringworld_effective_solar_day(smoothstep(-0.2, 0.35, dot(surfaceUp(x), sun)))`, exactly the stand-in formula (F7).
  - So a beam is faint whenever either the observer's sky is bright, or a distant beam is backed by sunlit far-arch terrain.
  - An eclipse drives `ringworld_solar_visibility` to 0, making B = 0 and the beams night-bright.
- **Atmosphere:** multiply by `(1 − sample_ring_atmosphere(camera, fragment, ring_center, B).a)`, so that haze and aerial perspective extinguish distant beams. This uses the same include and `RingAtmosphereModel.ConfigureMaterial` as the stand-ins.
- Colour: emissive pale teal-white `(0.70, 0.92, 1.0)`, the hue of the existing bore/plug teal accents. Emission scale is `BaseEmission` = 6.0 (HDR; AgX handles roll-off).

## G3. The pulse (user decisions 5 and U3: one-way circuit)

**Shape of the pulse (U3, binding).**
- Every `PulsePeriod` (default **300 s**), the main spire launches **one front on every chain**.
- The front leaves through the chain's exit link, travels the whole closed loop (100 links, about 31,416 km), and returns through the return link to the main spire.
- With **Speed = Auto**, `v = C / Period` = **104.72 km/s**, which is **3.0 s per 314 km link**. Each front returns at the instant the next one launches, so exactly one front is in flight per chain and every point on the ring is passed once every 5 minutes.
- With **Speed = Manual** (5-500 km/s), slower speeds put several fronts in flight on a chain and faster ones leave dark gaps between circuits. The math handles any count.
- Period and speed are separate grave-key settings (§C8). Auto speed recomputes from the period.

**Direction: prograde (+around) by default, exposed as the setting `PulseDirection` (Prograde / Retrograde).**
- Prograde matches the sense of the canonical around coordinate, the link records (KnotOut = exit) and the natural weather drift (`WeatherWindMetersPerSecond.X = +2.2`, F12). Pulses then run "downstream" with the weather field, and every pulse-derived ID and arrival time is computed in the coordinate the registry already uses.
- Nothing else depends on the choice. Retrograde is the mirror `q = C − p`. It is kept as a setting in case the user prefers the pulse to cross the sky the other way relative to the sun's motion.

**Chain sync: all six chains stay in sync** (column stagger default 0 s).
- The pulse coordinate advances with the along-ring component only, so the six heads form one straight band across the full 318 km width. That reads as a single ring-wide event and is one dot at far range.
- `ColumnStaggerSeconds` (0..5 s) delays column c by `ColumnStagger · |c − 2.5| / 2.5`, giving a V-shaped chevron if wanted later.

**Clock: the weather clock `CloudSimulationTimeSeconds`** (F8). Reasons:
1. It is saved and restored, so pulses are consistent across saves.
2. It runs at real-time rate. The celestial clock runs 1-100× (the day can be fast-forwarded), which would strobe the 5-minute cadence.
3. The lightning that crawls or triggers on beams (§G6) runs on the same clock, so beam events and weather events never drift apart.

If no weather source exists (non-Sunshine presets), the layer falls back to a session clock and its status reads "unsynchronised".

**Launch times and fronts** (pure `EmitterGridPulseClock`, double precision, file `scripts/Terrain/Sites/EmitterGridPulseClock.cs`):

```
phase  = Unit(Derive(seed, "emitter-grid-pulse")) · Period          // seed-fixed epoch
n      = floor((t − phase) / Period)                                  // latest launch
J      = ceil((C + trailReach) / (v · Period)) + 1                    // launches that can still light something
for j in 0..J−1:
  τ   = t − (phase + (n−j)·Period + stagger(c));  if τ < 0: skip
  q_f = v·τ                                       // head position in the direction-adjusted coordinate
  if q_f − C > trailReach: skip                   // fully absorbed into the main spire
  head drawn only while 0 ≤ q_f ≤ C; trail lit on q ∈ [0, min(q_f, C)]
```

- **Continuity.** Intensity is continuous in q everywhere on the loop, including the ring-seam link and every chain-to-chain junction. The only discontinuity is at q = C ≡ 0, the main spire:
  - the arriving front's trail ends there; its head is "absorbed" while its trail keeps fading in place;
  - the new front's head starts there with no trail behind it.
  **Trails never wrap through the main spire**, so the two never double-count.
- **Relaunch flare.** The head reaches q = C at the same instant (at auto speed) that the next head launches from q = 0. At that moment the main crown flares for 1.5 s at 3× head brightness, and the return-side and exit-side knot links of all six chains light together. This is the network's visible heartbeat every 5 minutes.

**Pulse profile along q** (b = q_f − q is the distance behind the head in metres; b < 0 means ahead):

| Part | Formula | Default and reasoning |
|---|---|---|
| Leading edge | `smoothstep(−E, 0, b)` for b ≤ 0 | E = **3 km** (0.03 s at auto speed: a sharp head) |
| Head streak | full peak over b ∈ [0, v·Δt_frame] | Analytic motion blur. At 105 km/s the head moves 1.75 km per 60 fps frame; lighting the whole swept segment stops the head from stepping |
| Trail | `exp(−max(0, b − v·Δt_frame) / (v·τ_t))` | τ_t = **2.5 s** (262 km, under one link). Reasoning below |
| Wake | `+ W·exp(−b / (v·τ_w))` | W = **0.05**, τ_w = **10 s** (1,047 km): the chain behind the head stays faintly lit for about half a minute |
| Cut-off | 0 beyond b > 4·v·τ_w | about 4,190 km, about 13 links behind each head |
| Relaunch flare | 1.5 s, 3× at the main crown and the 12 knot links | see above |

**Overhead re-check at 104.7 km/s (why the defaults changed from the two-front draft).**
- **Pass duration.** The nearest chain is at most 26.5 km to the side, at 1.7-4 km altitude. In that worst case the head crosses about 150° of sky (±100 km of travel) in about **2 s**, and the whole ±300 km nearby passage lasts about **5.7 s**. An observer within a few km of a chain sees a sub-second flash instead.
- **Screen speed.** At 26.5 km the head sweeps about 4 rad/s, about 80 px per frame at 1600 px and 75° FOV. A point sprite would visibly strobe there. Two consequences:
  - near the camera the head is drawn only by the ribbon, using the head-streak window above, which is continuous;
  - the screen-space **head sprite fades in from 300 to 600 km** view distance (`HeadSpriteNearKm` / `HeadSpriteFullKm`), where angular speed is a few px per frame or less.
- **Trail length.** The trail is specified in **seconds, not km**, so the overhead impression is the same at any speed setting.
  - At the old 3 s it would be 314 km: a whole link lit at once, which loses the "travelling segment" from far away.
  - At 1.5 s (the old 157 km) the overhead afterglow is too brief to register after a 2 s streak.
  - **2.5 s** gives a meteor-like streak plus a 2-3 s afterglow overhead, and still reads as a segment (262 km at 1/e) from far away.
- **Wake.** Shortened from 25 s to 10 s. At 105 km/s a 25 s wake would leave about 40 links faintly lit per chain, raising the cost and blurring the segment into a line.
- **Leading edge.** Kept at 3 km. That is wider than one frame's travel (1.75 km), so the edge never aliases, and it still looks like a hard front.

**Head sprite.** Each live head draws a camera-facing glow sprite (only beyond 300 km, see above) with a minimum of `FrontSpriteMinPixels` (default 3 px). Its brightness follows the same B, atmosphere and cloud factors. The sprite is what stays legible at thousands of km, where the ribbon is sub-pixel.

**What a player sees (auto speed, night).** Elevation on the arch of a point s along the ring from the observer is ≈ s/2R. A front therefore climbs or descends the arch at a constant **0.60°/s** everywhere except near the observer, and makes one full sweep of the sky per period.
1. **Approach from behind.** The head descends the rear arch toward the rear horizon as a small bright head with a dash of trail on a faint thread, brightening and sharpening as it nears. Example: an observer 5,000 km downstream of the main spire sees the relaunch flare at about 29° on the rear arch, then watches the new head descend and arrive about 48 s later.
2. **Overhead pass.** A streak across the sky in about 2 s (the 26.5 km worst case), then a 2-3 s afterglow on the nearby chain, then the faint wake for about half a minute.
3. **Climb up the forward side.** The head climbs the forward arch at 0.6°/s: a slowly rising star with a short tail.
4. **Far arch.** About 150 s after the pass it is at the zenith, which is the far side of the ring, 10,000 km away across the hub. It crosses the far arch as a 3 px head with a trail about 1.5° long (§G4.3).
5. **Return.** It descends the rear arch over the next 150 s. Somewhere on that path it reaches the main spire, where it is absorbed and the next head launches at the same instant, so the moving dot continues without a gap.

In practice, at night a pulse is always somewhere in view and makes one full circuit of the sky every 5 minutes. The dramatic close pass lasts about 2-6 s.

**Hooks for later work (not implemented here):**
- `EmitterGridPulseClock.ArrivalTime(siteIndex, column, n)` gives the time front n reaches a site.
- The eclipse event (the regular spires "periodically sending pulses to the false sun") can fire each spire's hub beam via `IEmitterBeamSource.SetBeamActive` when the relay pulse arrives.

## G4. Rendering

### G4.1 Layers

| Layer | Owner | Draws | Range |
|---|---|---|---|
| Near/regional | `EmitterGridBeamLayer` (Node3D "EmitterGrid", scene root next to `EmitterStandIns`, main camera) | 1 MultiMesh: 600 link ribbons. 1 MultiMesh: ≤ 12 head sprites (6 at auto speed, one per chain, plus slack for manual speeds) | fragments with view distance < `NearLayerMaxKm` (default 700, clamped to the effective global-LOD fade and 0.98 × camera far), with a 100 km crossfade |
| Far | `EmitterGridFarLayer`, a child of the far viewport World3D via a **new public** `RingScaledFarViewport.AddScaledWorldChild(Node3D)` and public `ScaledPoint` (the POI spec B.7 hooks; F10) | Same meshes in scaled space, built once (absolute, static, no rebase) | fragments with view distance > `NearLayerMaxKm − 100 km`, out to the opposite arch |

**Why two layers:**
- The main camera stops at 1,200 km, and global LOD terrain fades by 850 km.
- The far viewport holds the opposite-arch terrain and far clouds, so far beams get correct occlusion by the far arch and correct blending under far clouds.
- Beams in the far viewport sit behind every main-camera pixel. That is correct: anything the main camera draws within 850 km is nearer.
- As built, the handoff is per pixel (main opaque depth) and follows the cloud handoff band on backdrop pixels. See the G4 and T2 implementation notes under G4.4.

### G4.2 Geometry, precision, rebase

**Link mesh.** A ribbon strip of 16 sub-segments (34 vertices).
- Vertex attributes: `UV.x = f` (0..1 along the link), `UV.y = side` (−1/+1), and `UV2 = (f_a, f_b)`, the enclosing sub-segment.
- Instance transform: origin = endpoint A (layer-local), basis X = B − A. Basis Y and Z are unit placeholders and unused.
- `INSTANCE_CUSTOM = (P0, P1, column, kindFlags)` as float. p is at most 3.2e7, so the float ULP is 2 m, far below the 3 km edge. The shader derives `q = Direction > 0 ? p : C − p` from a uniform.

**Vertex shader:**
- Position = A + f·(B − A).
- Expand the ribbon sideways in view space, perpendicular to the projected link direction.
- Width = `max(projected(CoreWidth + 2·HaloWidth), MinPixelWidth)`.
- Energy conservation: `coverage = projectedTrueWidth / drawnWidth`, then intensity × `coverage^FarCompensation`. `FarCompensation` defaults to 0.6: 1 is physically exact (lines vanish), 0 keeps far lines bright.
- Use `abs(PROJECTION_MATRIX[1][1])`, because the RD projection is Y-flipped (M1 note).
- Sub-segments behind the near plane are clipped against z = −near using the neighbouring `f`.
- **Collapse rule:** if the sub-segment's q interval `[q(f_a), q(f_b)]` lies entirely outside every lit window `[max(0, q_f − cutoff), min(C, q_f + E)]`, there is no crawl on the link, the link is not lit by the relaunch flare, and `Residual(B) < 1e-3`, then set `VERTEX = vec3(0)`. Unlit links then rasterise nothing. Lit windows never wrap through q = 0 (§G3).

**Fragment shader:** `blend_add, unshaded, cull_disabled, shadows_disabled, fog_disabled`.
- The cross profile is a Gaussian core plus an exponential halo. This is an in-shader "bloom", because gameplay has no glow pass (F11).
- Then apply the pulse profile at the interpolated q (including the head-streak window, which uses a per-frame `frame_dt` uniform), B, atmosphere, and the cloud factor (§G5).

**Head sprites.** Quads with a minimum pixel size, faded in between 300 and 600 km view distance (§G3: nearer than that a sprite would step about 80 px per frame). CPU writes their transforms each frame (≤ 12).

**Precision:**
- World positions are anchor-relative, at most about 1.2 Mm inside the near layer, so the float ULP is ≤ 0.125 m. That is sub-pixel at any distance where a 40 m core is resolved.
- Far layer: 1:1000 absolute, ULP about 0.5 m real (POI spec B.7).
- Interpolating p in float along a 314 km link has an error of ≤ 2 m.

**Rebase.**
- Subscribe to `CanonicalFloatingOriginAnchorChanged`, like `EmitterStandInLayer.HandleAnchorChanged`.
- Recompute all 600 endpoint pairs via `EmitterGridTopology.FillWorldEndpoints` from the **current** snapshot (absolute, never a delta).
- Rewrite the instance transforms and bump `GeometryRevision`.
- Also rebuild when the grid or layer global transform changes (the root-shift path; `EmitterStandInLayer.cs:108-109`).
- Budget: ≤ 0.5 ms for 1,200 `WorldPosition` evaluations.

**Culling.** `IgnoreOcclusionCulling`, `RenderingServer.InstanceSetIgnoreCulling(true)`, `CastShadow=Off`, `GIMode=Disabled`, matching `EmitterStandInLayer.CreateMultiMeshInstance`.

### G4.3 Opposite-ring visibility

- The far layer's camera is the far viewport camera, which follows the eye (`RingScaledFarViewport.cs:2249-2285`).
- Its materials use the far conventions: ring_center = 0, canonical sun direction (`:2488-2517`), and the same B rule.
- Far-arch beams are about 0.002 px wide, so only the residual (≈ 0) and the energy-compensated pulse trail plus head sprites survive.
- **Example.** On the far arch across the hub (10,000 km), the 262 km trail (2.5 s at auto speed) spans about 26 mrad, roughly 30 px. With FarCompensation 0.6 it reads as a soft dash, and the head as a 3 px star. The head moves about 0.6°/s there, under 0.3 px per frame, so the far sprite never strobes.
- **Draw order:** RenderPriority below the far cloud layers (`12+layer`, `:1833-1879`). Far clouds then blend over the beams and dim them.

### G4.4 Interaction with near clouds (F9) and depth

Because Sunshine composites after the transparent pass:
- A `depth_draw_never` beam is dimmed by clouds behind it as well as clouds in front of it.
- Looking up at a beam at 1.7-2 km with the deck behind it, the beam would wrongly vanish.

**Decision:** the **lit core** of the near layer (pixels where `coverage·I > CoreDepthThreshold`, default 0.35) writes depth (`depth_draw_always`). The halo pass does not.
- A beam behind a cloud is then dimmed correctly.
- A beam in front of a cloud cuts a line-wide hole in that cloud. This is visually equivalent to "beam in front", because the line is bright and thin.
- Only the travelling lit segment writes depth, so the hole moves with the pulse and never persists.
- This needs two passes (`next_pass` halo material) and one extra draw call.
- It is verified as T2. The fallback is `depth_draw_never` everywhere, accepting dimming behind clouds. Setting: `DepthWriteCore`.

**G4 and T2 implementation notes (2026-10-16; headless done, windowed checks pending).** The trigger was the cloud diagnosis (`docs/cloud-diagnostics-2026-10-16.md` §1): beams kept only 0.28 (ground) / 0.51 (9 km) of their luminance under clouds, and a 1-2 px core depth write recovered only 0.35 / 0.60.
- **Shaders.** The ribbon code moved to `ring_emitter_grid_beam_common.gdshaderinc` and the head sprite code to `ring_emitter_grid_head_common.gdshaderinc`. Both include `ring_emitter_grid_layers.gdshaderinc` (per-layer uniforms and weights) and are compiled three ways from thin entry shaders:
  - `ring_emitter_grid_beam.gdshader`: the near additive halo;
  - `ring_emitter_grid_beam_core.gdshader` (`EG_CORE_PASS`): the near depth-only core;
  - `ring_emitter_grid_beam_far.gdshader` / `ring_emitter_grid_head_far.gdshader` (`EG_FAR_LAYER`): the far layer.
  
  A `world_scale` uniform (world units per metre: 1 near, `CoordinateScale` far) keeps every width, distance and atmosphere term in real metres, so both layers compute the same intensity at the same point. That is what makes the crossfade seamless. The far shaders take the eye from `CAMERA_POSITION_WORLD`.
- **Core pass (T2).** `next_pass` of the halo material, `blend_add` with zero colour and `depth_draw_always`. Render priorities: halo 0, heads 0, core 1. The core must draw after the halo and the head sprites; otherwise they depth-fight the depth it writes.
  - It writes depth only where `level · core_gain > CoreDepthThreshold` (0.35). `level` already includes the atmosphere, the layer weight and day/night. By day the peak is 0.2, so nothing is written, and the 0.03 night residual never writes either. Only the travelling pulse cuts a hole, and the hole moves with it.
  - **Footprint.** Sunshine's prepass keeps the furthest depth of each 2×2 block (`PreCompute.glsl:36-47`). The written half-width is therefore `max(natural, 0.5 · CoreDepthMinPixels · px)`, where the natural half-width is `σ_eff·sqrt(2·ln(peak/threshold))`. `CoreDepthMinPixels` defaults to 4. A full block lies inside the strip for every offset at ≥ 3 px axis-aligned, but a 45° line needs ≥ 2√2 + 2 ≈ 4.9 px for every offset. At 4 px some diagonal offsets still miss. Measured: 5 px gains only about 12-14% more light than 4 px but cuts a 25-30% deeper hole, so 4 px stays the default (see the windowed results below).
  - The core strip is rasterised 1 px wider than the footprint, then clipped per fragment. Vertex collapse in the core pass also drops sub-segments lit only by a residual below the threshold.
- **Far layer (G4.1).** `EmitterGridFarLayer` is built once from `RingScaledFarViewport.ScaledPoint(RelayNode)`: absolute, never rebased. It is parented with the new public `RingScaledFarViewport.AddScaledWorldChild(Node3D)`. Its `RenderPriority` is 5, below the far clouds (shell 10, canopy 11, clipmap 12-14), so far clouds blend over far beams. It never writes depth, because the far depth capture feeds Sunshine. Its head sprites follow the near layer's head evaluation (same link and fraction) in scaled space.
- **Near/far handoff: per pixel, not a plain distance cap.** The far viewport is a canvas backdrop behind every main-camera pixel. Main global terrain is drawn out to its discard edge at (fade start + fade end)/2 = 675 km, and the far terrain cutout is 625 km.
  - From the ground, a beam at distance d and height h is seen against the ring floor about `d + 2Rh/d` away (R ≈ 5,000 km). That is 346-675 km for d = 171-620 km, so nearly every beam between Sunshine's reach and about 620 km has main-camera terrain **behind** it.
  - Capping the near layer at Sunshine's reach would therefore hide those beams behind that terrain. A far beam can never draw over a main-camera pixel.
  - Clouds beyond Sunshine's reach are not drawn over main-camera terrain either; only the terrain's analytic cloud shadows are. A near beam there is therefore consistent with the rest of the image.
  - The only place a near beam can wrongly draw over clouds is a backdrop pixel, where the main camera drew nothing and far-viewport clouds show.
  - The near shaders therefore read the main opaque depth (`hint_depth_texture`, reverse Z, 0 = backdrop):

  | Pixel | Near weight | Far weight |
  |---|---|---|
  | Opaque behind the beam | `near_fade(d)` | far layer hidden |
  | Backdrop | `min(near_fade, band)` | `1 − min(near_fade, band)` |

  On backdrop pixels the two weights sum to exactly 1. With the default `NearLayerMaxKm` of 700, `near_fade` is the old range fade.
- **Band choice.** `band(d) = 1 − smoothstep(start, end, d)`. By default (`FarHandoffFollowsClouds`) the band follows the live cloud ownership the far viewport receives: `FarCloudClipmapHandoffStartMeters` → `LocalCloudRadiusMeters`. With the Sunshine defaults that is the effective handoff, about 120 → 171.5 km.
  - The near share of a backdrop-pixel beam then equals Sunshine's density share at that distance, and the far share equals the far-cloud share. Each part of the beam is covered by the cloud representation that owns that distance.
  - Ending the near fade at the handoff start instead would lose the depth core across the band.
  - Ending it later would leave near beams over far clouds, which is the reported reverse case.
  - The residual error is bounded by `w(1−w)` of the band's partial cloud, worst at mid-band.
  - When near cloud ownership is off (the far clouds own the whole disk), the band is `(0, 1 m)`: every backdrop-pixel beam is drawn by the far layer, under the far clouds.
  - Manual override: `FarHandoffStartKm` / `FarHandoffEndKm` (120 / 172 km). When the far layer is off or unavailable, the band is out of reach, and the behaviour is the G2 near-only layer.
- **Settings** (persisted in `GameplayEmitterGridSettings`/`Defaults`; drawer section "Emitter relay grid: clouds and far layer"): `DepthWriteCore`, `CoreDepthThreshold`, `CoreDepthMinPixels`, `FarLayerEnabled`, `NearLayerMaxKm`, `FarHandoffFollowsClouds`, `FarHandoffStartKm`, `FarHandoffEndKm`.
- **Known limits:**
  - Far beams on backdrop pixels are still dimmed by the fading Sunshine clouds *behind* them inside the band, because they write no depth.
  - Inside the far viewport, far clouds behind a far beam also blend over it. Both effects are minor at those distances.
  - Near beams over main-camera terrain between 600 and 675 km sit in the existing `NearLayerMaxKm` fade (pre-existing from G2).
  - The opt-in regional far viewport (`RegionalFarLodsEnabled`, off by default) is layered over the far texture without depth, so it would cover far beams.
- **Proposed Sunshine change (not made; Sunshine files are owned elsewhere).** The beam-side widening is a workaround. The general fix is **two-depth compositing**:
  - The prepass keeps the nearest as well as the furthest depth per 2×2 block.
  - The march snapshots the accumulated colour and α when it passes the nearest depth.
  - The full-res composite picks the snapshot for pixels whose own depth matches the block's nearest depth, and the full integration otherwise.
  
  This costs one extra RGBA sample per low-res pixel. It fixes every thin foreground occluder (beams, poles, branches), and then `CoreDepthMinPixels` could return to 1-2 px.

**G4 and T2 windowed results (2026-10-16).** Setup: RTX A1000, 1600×900, `--cloudy`, frozen weather and sun, night, ShowAllLinks. The explorer is frozen after each move, because the flying camera drifted tens of metres between captures in the first run. Harness: `eg_t2_capture.gd` in the session scratchpad (`scratchpad/eg_t2/`), a copy of the cloud-diagnosis harness.
- **Shader parity** (`tools/emitter_grid_shader_parity_test.gd`, first run): PASS. Worst absolute error 1.1e-4 against a tolerance of 4e-3. The per-case print used `%e`, which GDScript does not support; it now uses `%.7f`.
- **Blocking finding: Sunshine ignores the core until its compute is rebuilt.**
  - With the factory flow, the core changes nothing in Sunshine's output, at any time after it is enabled (probes 4 and 5: ±0.03 %).
  - `request_history_reset` shows the hole for about 2 frames only.
  - After `RefreshCloudCompute` (any path that rebuilds Sunshine's uniform sets; here the diagnostic matte toggle), the hole appears within 0.5 s, persists, and follows the core live: off clears it in 0.5 s, on restores it in 0.5 s.
  - So Sunshine's startup uniform set samples a depth that carries the opaque depth but not later transparent depth writes. Its rebuild check compares colour attachment, size, view count and MSAA, but not `buffers.get_depth_layer(view, false)`.
  - Rain haze and water already read `hint_depth_texture`, so the grid shaders do not introduce the depth copy.
  - **Follow-up (verified windowed):** the core only takes effect after a `refresh_compute` that happens **while it is lit**. Refreshes earlier in the session, with the core attached but unlit, do not help. After such a refresh it works live: core off clears the hole within 0.5 s, and core on restores it within 0.5 s.
  - The bound depth RID is stable: one RID per session, unchanged across every rebuild. A stale depth binding is therefore ruled out, and a depth-RID rebuild trigger did not help (tried, then reverted).
  - Turning the history compatibility gate off at runtime changes nothing.
  - The cause is under investigation in Sunshine by its owner.
  - Until it is fixed, the T2 core costs a draw call and gains nothing in a fresh session. The results below emulate a working core with one `RefreshCloudCompute` after each move, made while the core is lit (ShowAllLinks).
- **Beam light kept under clouds** (Σ(beam lit − beam dark) / Σ(beam without cloud density); the dark capture keeps the core's depth):

  | View | base | core4 | core5 | full (core4 + far) |
  |---|---|---|---|---|
  | ground, pitch 25° | 0.264 | 0.351 | 0.361 | 0.366 |
  | ground, 12° | 0.711 | 0.776 | 0.784 | 0.773 |
  | ground, 4° | 0.704 | 0.779 | 0.789 | 0.778 |
  | 9 km looking back | 0.487 | 0.574 | 0.586 | 0.589 |
  | 9 km level | 0.861 | 0.861 | 0.861 | 0.859 |

  - With the night ambient now (0.18, 0.23, 0.35), the clouds removed behind a lit core darken those beam pixels by 2.8-4.8 % at 4 px and 3.4-6.0 % at 5 px.
  - Where the dimming clouds are in front of the beam (9 km level, most of ground 25°), the core correctly changes nothing.
  - **Decision: 4 px.** 5 px adds about 12-14 % more recovered light for about 25-30 % more hole.
- **History and the moving core** (`hist3`): readback of Sunshine's history-filtered colour, frame-to-frame relative change, on the pulse path vs 6-16 px beside it, outgoing pulse.
  - While the head crosses the view, the path changes 2.3-2.5× as much as beside it. That is the intended hole moving with the head.
  - From the next second on, path/beside is back to base: 0.91 vs 0.89 at ground 12°, 1.03 vs 1.05 at ground 25°.
  - There is no grain or smear trail behind the head. No Sunshine change is needed for history.
- **Reverse case.** Beam light with far clouds on vs off: base 1.000 (far clouds never touch the beams); full 0.977-0.995. The far layer's beams sit under the far clouds. The views tested have few backdrop beam pixels.
- **Item 6, clouds over main terrain beyond Sunshine's reach.** Far clouds on vs off changes only the arch backdrop (beyond the main terrain edge). The main-terrain band between Sunshine's reach and the terrain edge does not change. This confirms the per-pixel handoff rationale above.
- **T7 far capture diff** (far layer hidden vs removed): mean |Δ| 1.9e-6 and 1.4e-5, with 32 / 211 isolated single-pixel speckles on the far terrain. That is temporal noise, not beam-shaped. PASS.
- **Day/night look.** In live-pulse sequences by night, the knot links stay bright in front of the cloud bank with the core (base dims them into it). By day the beams are faint. The far layer draws the six chains up the arch beyond 700 km.
- **Perf: not measured.** The GPU was throttled (P5, 19.9 W, 810 MHz memory) during the run. An A-B-A sequence with all links lit and the pulse ran in one run: main GPU mean 22.7 / 24.0 / 23.7 / 24.0 / 21.6 ms (off / base / core4 / full / off). The two "off" samples differ by 1.1 ms, so the grid's cost is below the noise; far viewport 3.1-3.35 ms. Repeat unthrottled before checking the G7 budgets.

## G5. Cloud interaction (start cheap; only the pulse touches clouds)

Only pulse heads and trails ever interact with clouds. The residual does not.

**Tier 1 (default): CPU weather sample at the fronts.**
- At `CloudSampleHz` (default 10 Hz), for each live head: the head itself, one prefetch point at `q_f + v/CloudSampleHz` (the head travels 10.5 km between samples at auto speed, so the next value is fetched ahead and interpolated along q), and 2 trail points. That is 6 heads × 4 = **24 points**, or ≤ 48 with a slow manual speed.
- Map p → link → canonical (around, across) and the chord height h.
- Call `RingVolumetricCloudLayer.QueryLocalWeatherAtTime(position, clock)`. This is pure C#, cached and macro-scale; the Sunshine `add_sample` GPU path is async, one frame late and GDScript-queued (F9 report), so it is not used.
- In-cloud factor: `k = CloudCoverage · smoothstep(base−200, base+200, h) · (1 − smoothstep(top−200, top+200, h))`, using the returned base and top.
- Pass `k` per front as uniforms:
  - inside cloud the core dims by `(1 − 0.7k)`;
  - the halo widens by `(1 + 3k)` and brightens by `(1 + 1.5k)`, a "diffused in cloud" look.
- Cost: ≤ 24 queries every 0.1 s, so ≤ 0.03 ms per frame amortised (weather queries are microseconds; 128-entry cache). The weather macro cells are 32 km, so 10.5 km steps with interpolation are adequate.

**Tier 2 (default on): far-cloud glow.**
- Add a `far_pulse_glow[4]` uniform array to `ring_far_cloud_clipmap.gdshader` and `ring_cloud_layer.gdshader`, reusing `far_lightning_glow()` (`:57-70`) with its minimum-footprint logic.
- The far viewport packs the 4 most visible in-cloud heads (of the 6), as `vec4(aroundU, acrossV, radius/C, k·I)`, mirroring `ConfigureFarStormLightningMaterial` (`RingScaledFarViewport.cs:570-603`).
- Far cloud decks then light up softly where a pulse runs through them.
- Cost: a few ALU per far-cloud fragment, and no new draw calls.

**Tier 3 (opt-in, measurement-gated): near Sunshine point light.**
- One `OmniLight3D` "GridPulseCloudLight" placed at the nearest in-cloud head within 60 km.
- Registered in the Sunshine driver's `tracked_point_lights` (exported, currently unused in production; `SunshineCloudsDriver.gd:42-46`).
- Apply history suppression as for lightning (`RingSunshineCloudBridge.SetLightningFlashIntensity`, `:683-690`), because the light moves 105 km/s. At that speed the light crosses the 60 km window in about 1 s per pass, which is a further reason it stays opt-in.
- Ships only if T3 measures ≤ 0.5 ms GPU on A1000. Default **off**.

## G6. Lightning on beams

**G6a, crawl (consumer only; deterministic because it derives from shared events).**
- For each new `RingLightningEvent` (natural, parked-storm or manual), the CPU finds links whose segment passes within `CrawlRadiusKm` (default 8 km) of the event's `CloudPosition`. That is a point-to-segment test in world double space: 600 tests per new event, about 10 µs.
- The closest link gets a crawl: a lit span centred on the closest point.
  - Half-length = `CrawlLengthKm·(0.5 + 0.5·Unit(ShapeSeed))`, default 40 km.
  - The span grows outward at 150 km/s.
  - Intensity follows `RingLightningEvent.PulseAtAge` (flash plus two restrikes, 0.72 s).
- Up to 4 crawls are packed as `vec4(link, pCentre, halfLength, intensity)` uniforms.
- Rewind or reset clears crawls, just like the existing event list. Crawls reveal otherwise-invisible beams for under 1 s. This is the main daytime "beam sighting".

**G6b, pulse-triggered strikes (optional phase, default off; a new deterministic lightning source).**
- When a head is inside a storm (`k > 0.6` and the query's `PrecipitationPotential ≥ 0.62` on a Major/Exceptional system), emit at most one strike per (launch, link). At auto speed a head spends 3 s on a link, so this is at most one strike per 3 s per chain.
- Identity is `hash(seed, linkIndex, launch n, column)`, with start time = the exact head crossing time from `EmitterGridPulseClock`. It is therefore reproducible from the clock alone, as `TryResolveLightningSlot` is.
- It is added as a fourth loop in `ProcessSharedLightning` (`.Lightning.cs:155-157`), respects the 128-event cap and dedupes by ID.
- Local bolts, far flashes and thunder pick these strikes up unchanged.
- It pairs with parked storms: each pulse passing over an offline storm machine triggers a strike burst every 5 minutes.

## G7. Budgets (RTX A1000, 1600×900, same style as M2)

| Item | Budget | Notes |
|---|---|---|
| Near layer GPU, typical (pulse only) | ≤ 0.12 ms | at most about 90 of 600 links lit: 6 chains × (1 head link + about 13 trail and wake links + the previous circuit's fading wake near the main spire, which shares that budget). Lit links are 1-6 px wide; the rest collapse in the vertex stage. Only the few near links are wider than a couple of pixels |
| Near layer GPU, worst (night residual on, all links, depth-core pass) | ≤ 0.30 ms | 600 links, 20k vertices |
| Far layer GPU | ≤ 0.05 ms | |
| Draw calls | main +3 (ribbons, halo pass, heads); far viewport +2 | F19: draw calls are the scarce resource |
| Main-thread CPU per frame | ≤ 0.06 ms | head evaluation in double, ≤ 12 head transforms, ≤ 16 uniform sets; no per-link CPU work per frame |
| Cloud sampling (Tier 1) | ≤ 0.03 ms per frame amortised | ≤ 24 queries at 10 Hz |
| Far-cloud glow (Tier 2) | ≤ 0.05 ms GPU | |
| Rebase rebuild | ≤ 0.5 ms | 600 links (plus 200 machine stand-ins, §C5) |
| Crawl search | ≤ 0.02 ms per new strike | |
| M1 emitter capture shots | within ±0.2 ms of current | regression gate |
| Frame spikes | none over 8 ms attributable to the grid (pulse passes, rebases) | |

---

# Part C: climate machines

## C1. Form, scale, spacing, placement: Wall Engines (user decision U1)

**Station cadence.** There is one station per emitter-row gap, at around `A_g = RowPhase + (g + ½)·pitch`: 100 stations, 314 km apart. That is **mid-way between emitter rows**, for three reasons:
- It is as far as possible from every emitter site's terrain influence: at least 120 km of clearance along the ring (157 km half-pitch, minus at most 25 km of emitter reach, minus at most 9 km of machine reach). The two O(1) lookups therefore stay disjoint (F2).
- It places each machine under the highest point of the relay chords (mid-span), a deliberate composition: the light lines arc over the machines.
- Each station "owns" one 314 km climate cell between two emitter rows, which reads naturally from the ground and on the map.

**Form**
- Paired machines are built into **both structural rim walls** (across 0 and across W) at every station: 200 machines.
- Each is a recessed bay in the 10 km wall (F17), **24 km wide along the ring**.
- Each bay contains 8 intake columns (1.2 km wide, 9 km tall) and three horizontal vent galleries at **2, 4 and 6 km**, exactly spanning the cloud deck the machine "breathes".
- Condenser fins crown each bay at the 10 km wall top.
- A forecourt plinth extends 3 km onto the floor: a flat quay, 20 km along, at a seed-chosen pad height.

**Surface view**
- From the forecourt, a 10 km cliff of machinery filling the sky.
- From the centre line (159 km), each engine subtends about 3.6° tall and 8.6° wide.
- With pairs on both walls, an observer anywhere sees machines on both horizons.
- Online engines show slow-breathing vent glow (amber by night). Clouds streaming into or out of the galleries is a later art pass.

**Far view**
- Two strings of lit vent bands run along both rim walls around the whole arch, one bay every 314 km.
- **A dead engine is a dark gap in the string**, visible from across the ring, so the lore is readable from orbit.
- The relay grid (columns 26.5 km in from each wall) arcs over the forecourts.

**Terrain.** Only a forecourt (a half-stadium) at the wall foot. There is no bore, aperture or underground.

**Known costs and risks**
- A machine affects weather out to about 100-150 km from its wall, which is plausible but asymmetric.
- Near-field wall presentation is unverified (T4).

**Rejected alternatives (user decision U1, 2026-10-16).**
- **B. Centre-line Weather Spires** (100 needles, 14 km tall). They would dwarf and compete with the 4 km main spire. They would also need long-shadow handling and the heaviest new terrain shaping.
- **C. Sky Weirs** (about 50 arches spanning the ring wall to wall). They would radically change the sky's identity and add sweeping 318 km shadow bands. There would be nothing to visit, and the anomalies would become linear bands.

## C2. Registry (pure, mirrors `EmitterSiteRegistry`)

New files `scripts/Terrain/Sites/ClimateMachineRecipe.cs` and `ClimateMachineRegistry.cs`.

**`ClimateMachineRecipe`** is immutable. Every field is hashed and the algorithm version is 1. It is derived from the seed (`Default(seed)`), as v5 derives the site recipe (`EmitterSiteTerrainShaper.RecipeFor`).

| Field | Default | Notes |
|---|---|---|
| `Stations` | 100 | Equals emitter `Rows`; the station pitch and phase are taken from the emitter recipe (`RowPitchM`, `RowPhaseM`) so the two grids are locked together |
| `Sides` | 2 | Machine i = g·2 + side, where side 0 is across 0 (west) and side 1 is across W (east) |
| `FaceWidthM` / `ForecourtDepthM` / `ForecourtLengthM` | 24,000 / 3,000 / 20,000 | |
| `SkirtM` / `ApronM` | 5,000 / 400 | Influence radius from the footprint segment ≤ 9 km |
| `OfflineFraction` | **0.03 of machines (= 6 of 200; U4)** | At most one offline machine per station |
| `MinOfflineSpacingStations` | 10 | ≥ 3,140 km apart |
| `MainExclusionStations` | 2 | No offline station within 2 gaps of `MainRow`. This keeps fresh-world spawns, which land at the main spire's Far pose, out of permanent weather |
| `StormRadiiM` / `DroughtRadiiM` | (110 km along, 70 km across) / (160 km, 100 km) | Anomaly ellipse half-axes |
| `AnomalyInsetM` | 70,000 (storm) / 90,000 (drought) | Ellipse centre measured across from the dead machine's wall |
| `AnomalyFeather` | 0.35 of radii | Smooth falloff |

**Algorithm (deterministic; output independent of thread count):**
1. **Placement.** For each machine, `Around = snap2(wrap(RowPhase + (g+½)·pitch))`, `Across = side == 0 ? 0 : W`.
2. **Forecourt.** Pad height P is the integer median of the natural height over a 9-point forecourt stencil, clamped to ≥ Sea + 8.
   - `needsQuay` is set when wet; the forecourt is then raised max-only with a 250 m beach, reusing `EmitterSiteSurfaceOperator`'s PCHIP raise.
   - Cut and fill are capped at 150 m.
   - The stencil uses the unshaped natural source (`Gen17NaturalHeightSource`), exactly as the emitter registry does.
3. **Offline selection.**
   - Rank stations by `Unit(Derive(seed, "climate-offline", g))`.
   - Walk the ranking, accepting a station when it is clear of the main exclusion and at least `MinOfflineSpacing` gaps (circularly) from every accepted station, until K = round(fraction × 200) are accepted.
   - The dead side is `Derive(seed, "climate-side", g) & 1`.
4. **Anomaly kind.**
   - Compute the land fraction of each anomaly ellipse from 64 natural samples.
   - **Balanced split (U4: about 3 storms and 3 droughts).** The ⌈K/2⌉ accepted stations with the highest land fraction become droughts, and the rest become storms. Ties go to the lower station index. With K = 6 that is exactly 3 and 3.
   - Land fraction picks which station gets which kind because a drought over open sea would be invisible apart from clear sky, and a storm reads well over land or sea.
5. **Invariants (asserted).**
   - Every machine's influence is disjoint from every emitter site's influence, including the main site: an O(200×595) pair check.
   - No influence reaches the far wall.
   - Offline spacing holds.
   - The record count is 200; exactly 6 are offline, 3 Storm and 3 Drought.

**Records:**

```
readonly struct ClimateMachine { int Index; int Station; byte Side; long Around; int Across; int PadHeight;
  ClimateMachineFlags Flags /* Offline, Quay, CutFillConflict */; ClimateAnomalyKind Anomaly /* None, Storm, Drought */;
  float InfluenceRadius; ulong MachineHash; }
readonly record struct ClimateAnomaly(int MachineIndex, ClimateAnomalyKind Kind, double CentreAround, double CentreAcross,
  double RadiusAround, double RadiusAcross, int SystemId /* −20000 − machineIndex */, ulong Hash);
```

**Lookups (O(1), allocation-free, any thread):**
- `TryGetInfluencingMachine(around, across, out idx, out du, out dw)`:
  - g = the station whose gap contains the point, `floor((wrap(a) − phase)/pitch) mod 100`;
  - side from across < W/2;
  - one distance-to-segment test.
- `TryGetNearestMachine(around, across, filter)` scans the 3 nearest stations, plus a linear scan over the ≤ 12 anomalies for the Offline, Storm and Drought filters.
- `EvaluateAnomalies(around, across, out storm, out drought)` is O(K ≤ 12) with signed-wrap distances.
- Identity is FNV over the records plus the algorithm version plus the emitter registry identity.

The registry is cached like `EmitterSiteRegistry.GetOrBuild`: a static `ConcurrentDictionary<(naturalIdentity, recipeHash)>`. The build costs about 200 × 80 natural samples, about 50 ms single-threaded, so `Parallel.For` is optional.

## C3. Terrain integration and versioning

**Rule: existing worlds never change.** The v4 terrain stays frozen, and so do the v5 terrain, hash and weather.

- **New generator id `ringworld_surface_climate_v6`** in `TerrainGenerationParameters`.
  - Fields = `SitesFields`; there are no new JSON fields, since the climate recipe is seed-derived like the site recipe.
  - `HasEmitterSites` and `HasMountainDetail`/`HasEnhancedErosion` are true for v6, and there is a new `HasClimateMachines`.
  - `SitesDefault()` gains a sibling, `ClimateDefault()`.
  - The `generator_id` checks are at `:48, 76`. `RuntimeSurfaceGeneratorVersion` stays 17 (F5/F18 precedent).
- **Shaper.** `EmitterSiteTerrainShaper` gains an optional `ClimateMachineRegistry` plus a `ClimateForecourtOperator`, non-null only for v6.
  - `ShapeHeight` tries the emitter lookup first, then the climate lookup. They cannot both hit, by the C2 invariant.
  - The forecourt operator reuses `EmitterSiteSurfaceOperator`'s stages (max-only raise, terraced cone clamp, skirt fade, exact pad), with r replaced by **distance to the footprint segment** (a stadium) and clipped to the floor (across ∈ [0, W]).
- **Diagnostics.** Add `ClimateMachineIndex` (default −1) to `TerrainGenerationHeightModel.Diagnostics`. `SiteMask` is reused (Hardscape on the quay, Rock on cuts, Sand on the beach). Vegetation gating is therefore automatic through `SiteMask.BlocksVegetation` at every existing gate (`RingGrassTileBuilder.cs:237`, `RingBiomeDecorationLayer.cs:848`, `TreeStreaming.cs:1137, 1220`, `RingScaleAnchorLayer.cs:284`). `SiteIndex` keeps meaning "emitter site" only.
- **Hash.** `CalculateRuntimeTerrainDefinitionHash` (`RingTerrainSampler.SurfaceGeneration.cs:226-246`) appends the climate recipe hash, algorithm version and registry identity **after** the emitter terms, and only when present. The v5 hash is byte-identical.
- **Sampler.** Add `RingTerrainSampler.ClimateMachines` (null unless v6), next to `EmitterSites` (`RingTerrainSampler.EmitterSites.cs:20`).
- **Caches.** The tree disk-cache key gains `|cm1` on v6, next to `|es1`.
- **No apertures, holes or underground.**
- **Launcher and preview (U4).** New runtime-surface worlds can be created as v6 only behind a launcher toggle until phase C3 ships. From then on, **v6 is the default for new worlds**. The switch is a separate one-line change, gated on C3's tests. v5 slots keep v5, untouched. `world_size_launcher.gd` and `terrain_generation_recipe.gd` follow the v5 precedent (M1-2).

**C2 implementation notes (done 2026-10-16).**
- `ClimateForecourtOperator` (`scripts/Terrain/Sites/`, `OperatorVersion` 1, mixed into the v6 hash) is the forecourt operator. The distance s is measured to the 20 km footprint segment (a half-stadium on the floor). Exactly P inside s <= 3,400 m (pad 3,000 + apron 400); then the emitter bowl stages with the emitter recipe's constants: (1) quay raise, only for `Quay` machines (flat deck, 600 m gentle apron, 250 m beach, 300 m shelf, 30 degree flank), max-only, with its weight faded out between the shelf and the 8.4 km influence radius so it stays C0 even in deep ocean; (2) 60 degree terrace staircase cut / 33 degree embankment fill cone; (3) a 4 km skirt fade back to the (raised) natural height; (4) the exact pad. No recipe fields were added (C1 goldens unchanged).
- Seed 1337 consequences (110 quay, 170 conflict): quay machines sit in 2-4.8 km deep ocean, so their plinth is a seamount (underwater flank about 50 degrees, invisible above the sea); about half of all pad area is raised out of water. 43 of 200 machines have a cut > 1 km and 14 > 2 km somewhere in their region (a mountain beside the 20 km forecourt). Those are listed by `climate_terrain_v6_test` (`CLIMATE_V6_WORST_CUT`) for a user decision on whether to keep them as dramatic pits.
- Composition: `EmitterSiteTerrainShaper` tries the emitter lookup first, then the climate lookup; the C1 invariant makes them disjoint (min clearance 122 km). `Diagnostics.ClimateMachineIndex` is new; `SiteIndex` still means "emitter site" only.
- Launcher: climate toggle (default OFF) for new runtime-generation worlds; the preview drawer has the same experimental toggle. `world_size_launcher.gd` treats the launcher toggle as authoritative.

## C4. Weather coupling

**Principle:** `weather(t, x) = Anomaly(x, t) ∘ Natural(t, x)`, with **no feedback into natural births, lifecycles or advection**.
- Both terms are pure functions of (seed, registry, x, t).
- Repeatability, rewinds, forward jumps and save/load hold by construction. There is no state to save, and the manual path (unsaved, session wind history; F13) is **not** used.
- With no climate registry (v1-v5), the anomaly term is null and **v5 weather is bit-identical**. This is a test gate.

**Source.** `RingVolumetricCloudLayer` builds a `ClimateAnomalyField` from `_grid.TerrainSampler?.ClimateMachines` once per registry identity, mirroring how layers bind `EmitterSites` (`EmitterStandInLayer.cs:101-105`).

### Parked storm (offline Storm machine)

**Integration.** Inside `QueryRawWeatherAtTime`, immediately after `EvaluateMacroWeather` and **before** `ResolveLocalCloudCoverage` (`RingVolumetricCloudLayer.cs:~3296-3308`), merge one synthetic `ExceptionalFront` system per storm anomaly:
- Its centre is **fixed in canonical space**: it is not advected, so it is evaluated at the true position, not the source-space position.
- Radii come from the recipe; orientation is along the ring.
- Lifecycle is pinned at Mature with deterministic breathing: `envelope = 0.85 + 0.15·sin(2π·t/1800 + 2π·Unit(hash))`.
- Influence is combined with the existing `1 − (1−a)(1−b)` rule and strongest-system selection.

Because of where it is merged, cloud coverage, precipitation, vertical profile, `SystemId` (−20000 − machineIndex), wind `StormFactor` and the 8-sample wind look-back (`Wind.cs:44-60`) all follow automatically and stay pure.

**Lightning.**
- Add a loop next to the manual one (`.Lightning.cs:157`) that calls the same path as `TryScheduleManualLightning` for each storm anomaly's `LocalWeatherState`.
- Strikes are then deterministic from (seed, SystemId, clock) with a 7-24 s cadence.

**Rain curtains.**
- `RingDistantRainCurtains.cs:203-206` treats negative IDs as absolute cloud bases. Add the climate ID range (−20000..−20199) to the **natural-front** branch, so it uses datum + `StormFrontCloudBaseAltitudeMeters` (500 m).
- Also audit `TryGetRandomActiveStormCell` (`RingVolumetricCloudLayer.cs:3398-3442`), which uses the negative-ID convention.

**Presentation limits (F15) and fixes:**
- Sunshine sculpts the observer's dominant system. Inside or near the parked storm that system *is* the parked storm, which is accepted. Approaching from outside, the near deck shows natural coverage until handoff, the same limitation all storms have today.
- **Far atlas:** the 128-column atlas (245 km) can alias a 110 km storm. Add an analytic post-pass in `RingScaledFarViewport`'s atlas build:
  - for each anomaly, splat its ellipse weight into overlapping texels;
  - storm: max of coverage and storm channels; drought: multiply down.
  - Cost: O(K × few texels).

**Cache and revision.** The `QueryLocalWeatherAtTime` cache signature (`Wind.cs:33`) mixes in the anomaly registry identity, `AnomaliesEnabled` and `AnomalyStrength`. Changing those settings bumps a new `ClimateAnomalyRevision`, which the far atlas, ambience probes and wind cache already treat like `ManualWeatherRevision` (`RingScaledFarViewport.cs:734-749`, `RingWeatherAmbience.cs:614`).

### Drought (offline Drought machine)

**Weather.** In the same function, after coverage and precipitation are computed (`:3302-3308`), with `w = droughtWeight · AnomalyStrength`:

```
coverage *= 1 − 0.85·w;  precipitation *= 1 − w;  SystemInfluence *= 1 − w
```

Natural storms drifting through dissolve in the drought core and re-form outside it. Their identity and lifecycle are untouched.

**Lightning.** In `TryCreateNaturalEvent`, skip natural strikes whose sampled target has drought weight > 0.5. That path calls `TryBuildWeatherSystem` directly and would otherwise bypass the query.

**Dust haze.**
- Reuse the rain-haze compositor path (`RingLocalWeatherEffects.RainFog.cs:76-131`).
- Add a `DustHazeOpacity = w_cam · DustHazeStrength · (0.5 + 0.5·windFactor)` input with a sand tint `(0.78, 0.66, 0.48)`.
- It is off while raining or snowing, underwater and in shelter, using the existing gates.
- Far skies are clear automatically, via the atlas.

**Dry grass.**
- At bind, call `SetGrassModifier("climate:drought:<i>", zone)` per drought anomaly, with:
  - half-size = drought radii;
  - feather 30 km;
  - density 0.55;
  - tint `(0.62, 0.55, 0.33)`;
  - tint strength 0.7.
- Set once, never animated (F16: changes rebuild tiles).
- It is removed on world unload or when the setting is disabled.
- Card grass and standing grass both honour it.
- Trees and terrain colour are unchanged in this scope.

### How the other systems see it

| System | Storm machine | Drought machine |
|---|---|---|
| Wind | Automatic: `StormFactor` from the merged system, with pure look-back | Natural wind unchanged; dust haze scales with it |
| Lightning | Deterministic strikes at the parked storm; local bolt, far flash and thunder unchanged | No natural strikes in the core |
| Ambience | Rain layers, distant-storm bed and thunder via the existing queries and events | Rain silent; wind bed unchanged |
| Rain curtains | Yes, with the ID-branch fix | None |
| Far clouds | Via atlas, plus splat | Clear patch, via atlas plus splat |
| Emitter grid | Pulses through the storm dim and diffuse (§G5); crawls from its strikes; optional pulse-triggered strikes (§G6b) | Pulses read clearer and brighter (no cloud) |

**Offline machine → emitter grid (user decision U2: only through the weather).** There is **no direct coupling**: no link dims, flickers or reroutes because a machine is dead. The two structures are separate, and the grid reacts only physically, through the weather. With Wall Engines, the parked storm (centred 70 km in from the wall, 70 km across radius) covers chains 0-1 (or 4-5). Every pulse through it (one every 5 minutes, crossing the storm in about 2 s) shows dimming, diffusion and crawls, and with G6b on, triggered strikes. This makes offline storms the best beam spectacle on the ring without inventing lore.

## C5. Machine visuals, far stand-ins and asset path

There are no real assets yet, so everything is **procedural placeholder** geometry built in code, as the bore liner is (`EmitterBoreGeometry.cs`).

**`ClimateMachineMeshBuilder` (pure):**
- **Near mesh** (≤ 20k tris): bay recess, 8 intake columns, 3 vent galleries, crown fins, forecourt kerb. Vent strips are a separate surface with an emissive material.
- **Far proxy** (≤ 400 tris): a 24 × 10 km slab with column grooves, plus vent strips as a separate thin surface.

**`ClimateMachineStandInLayer`** (scene root, "ClimateMachineStandIns", main camera) mirrors `EmitterStandInLayer`:
- One MultiMesh of 200 instances.
- Custom data `.r` = presented, `.g` = hidden, `.b` = offline.
- Shader `ring_climate_machine_standin.gdshader` is derived from `ring_emitter_standin.gdshader`. Vent emission is `mix(NightVent, DayVent, dayAt)` × (offline ? 0 : 1).
- It uses atmosphere and coverage dither, absolute rebuild on rebase, and range out to min(850 km, global fade).
- Offline machines are flagged dark. Their slab still draws, so the **dark gap** is visible.

**Far viewport strips.** A static scaled-space MultiMesh of 200 emissive vent strips on the far-viewport walls, through the same `AddScaledWorldChild` hook as G4. The wall lights then string the opposite arch, with gaps at dead machines.

**`ClimateMachineStreamer`** (near detail plus collision).
- Reuses `EmitterPoiStreamerCore`, which is placement-agnostic: it uses observations, slot kinds and `SiteKind` (`EmitterPoiStreamerCore.cs:8-16, 55, 119`). Add `SiteKind.ClimateMachine = 2` to the core's kind tables only, not to the emitter registry.
- Pool of 2. Load 40 km, unload 45 km, collision 3 km.
- The slot holds the procedural near mesh plus a `ConcavePolygonShape3D` from its walkable kerb and forecourt faces.
- Presented-flag handoff to the stand-in, as for emitters.

**Asset import path for later.**
- `assets/poi/climate_machine/ClimateMachinePOI.tscn`, following `assets/poi/README.md` conventions: merged meshes, a `collision/` folder, `FarProxy` with a visibility range.
- Markers: `ForecourtCentre`, `EntryApproach`, `VentGallery_2k/4k/6k`, `CrownFins`.
- Local frame: +X along the ring, +Y inward up, −Z into the wall. The wall face is at Z = 0; the forecourt is +Z.
- If the scene exists, the streamer threaded-loads it (`RingThreadedResourceClaims`) and the stand-in uses its `FarProxy`. Otherwise it falls back to procedural geometry.
- A failed load degrades to procedural geometry and never blocks loading (the M1-5 rule).

## C6. Navigation

- **`ClimateMachineNavigator`** (pure) mirrors `EmitterSiteNavigator` (`EmitterSiteNavigator.cs:83-104, 202`).
  - `FindNearest(around, across, ClimateMachineFilter { Any, Offline, Storm, Drought }, out distance)`.
  - Viewpoints:
    - Forecourt: ground, 2.5 km out, facing the wall.
    - AnomalyCentre: ground at the anomaly centre, using the dry-flat landing check with 200 m hover fallback.
    - Overlook: 1.5 km altitude, 30 km out.
    - High: 150 km.
- Teleport uses the existing `EmitterSiteTeleport` sequencer and `TryTeleportToCanonical`.
- **Grave-key actions** (not saved): "Nearest offline machine", "Nearest storm machine", "Nearest drought machine", "Machine N" spinner with Prev/Next, and a viewpoint selector.

## C7. Underground mode

- **`EmitterGridParticipant`** (FarTerrain group), added to `UndergroundParticipantFactory.CreateDefaults` (`UndergroundParticipants.cs:12-20`).
  - Suspend: hold uniform and cloud-sample updates. The pulse stays clock-driven, so resume is exact.
  - Hide: hide the near layer, head sprites and crawls.
  - `RetainedInstanceIds` returns both MultiMeshes.
  - The far layer is hidden by the far viewport's own `SetRenderSuspended` (`FarViewportParticipant`).
- **`ClimateMachineStandInParticipant`** (FarTerrain) behaves as `EmitterStandInParticipant` (`:209-224`) does. Machine near slots are not participants, the same rule as emitter POIs.
- **Weather.** The anomaly field is data and keeps evaluating. Dust haze follows the rain-haze gating under `WeatherEffectsParticipant`, which is verified in the underground round-trip smoke. Grass zones persist; grass is hidden by `GrassParticipant`.
- **G6b strikes** keep scheduling, since they are weather events. Thunder is already suppressed underground by `WeatherAudioParticipant`.

## C8. Grave-key settings and persistence

Two resource and defaults pairs follow the `GameplayEmitterSiteSettings`/`Defaults` pattern (POI spec E12/C.1): `Initialize`, `Set*` returning changed, atomic `Save`, `Reset`, `Clamp*`, path override, and a `Sanitize`. Drawer sections go in new partials, built in the Surface terrain tab after the emitter sections (`RingLiveTuningDrawer.SurfaceTerrain.cs:133-136`), with debounced save and retry and a 0.25 s live status label.

**`GameplayEmitterGridSettings`** (`user://gameplay_emitter_grid_defaults.tres`), section "Emitter relay grid":

| Field | Default | Range |
|---|---|---|
| `GridEnabled` | true | bool |
| `PulsePeriodSeconds` | 300 | 30..1800 |
| `PulseSpeedMode` | Auto (C / period: 104.7 km/s at 300 s) | Auto / Manual |
| `PulseSpeedKmPerSecond` (Manual) | 104.7 | 5..500 |
| `PulseDirection` | Prograde | Prograde / Retrograde |
| `PulseLeadingEdgeKm` | 3 | 0.5..20 |
| `PulseTrailSeconds` / `PulseWakeSeconds` / `PulseWakeLevel` | 2.5 / 10 / 0.05 | 0.5..20 / 0..60 / 0..0.3 |
| `RelaunchFlareStrength` | 3.0 | 0..6 |
| `ColumnStaggerSeconds` | 0 | 0..5 |
| `NightPeakIntensity` / `DayPeakIntensity` | 1.0 / 0.2 | 0..4 / 0..1 |
| `NightResidual` / `DayResidual` | 0.03 / 0.0 | 0..0.2 / 0..0.1 |
| `CoreWidthMeters` / `HaloWidthMeters` | 40 / 600 | 5..200 / 0..3000 |
| `MinPixelWidth` / `FarCompensation` / `FrontSpriteMinPixels` | 1.5 / 0.6 / 3 | 0.5..4 / 0..1 / 0..8 |
| `HeadSpriteNearKm` / `HeadSpriteFullKm` | 300 / 600 | 50..2000 / 100..3000 |
| `NearLayerMaxKm` / `FarLayerEnabled` | 700 / true | 100..850 / bool |
| `CloudInteraction` | DimAndFarGlow | Off / Dim / DimAndFarGlow / Full (adds Tier 3 point light) |
| `CloudSampleHz` | 10 | 2..30 |
| `DepthWriteCore` | true | bool |
| `LightningCrawl` / `CrawlRadiusKm` / `CrawlLengthKm` | true / 8 / 40 | bool / 1..30 / 5..150 |
| `PulseTriggeredStrikes` (G6b) | false | bool |

**Debug** (not saved):
- "Show all links": residual 1, coloured by column, KnotIn/Out highlighted.
- "Freeze pulse".
- "Pulse scrub", a time-offset slider applied to the visual only and never written to the clock.
- "Show cloud samples", markers at the sampled head points.
- "Fire pulse now", a visual-only offset so the next launch is now.

**Status line:** link count, identity, clock source, speed and direction, current launch n, head q per chain (with the link it is on), the next pass ETA at the player's nearest chain, lit links, and last rebuild ms.

**`GameplayClimateMachineSettings`** (`user://gameplay_climate_machine_defaults.tres`), section "Climate machines":

| Field | Default | Range |
|---|---|---|
| `MachinesVisible` | true | bool |
| `StandInMaxDistanceKm` | 850 | 50..850 |
| `NightVentEmission` / `DayVentEmission` | 1.0 / 0.15 | 0..4 / 0..1 |
| `FarWallStrips` | true | bool |
| `PoiLoadRadiusMeters` / `PoiCollisionRadiusMeters` | 40,000 / 3,000 | 20k..80k / 1k..8k |
| `AnomaliesEnabled` | true | bool (debug; off = natural weather only) |
| `AnomalyStrength` | 1.0 | 0..1.5 (part of the weather cache signature; not saved in the world) |
| `DustHazeStrength` | 1.0 | 0..2 |
| `DroughtGrassTint` / `DroughtTintStrength` / `DroughtGrassDensity` | true / 0.7 / 0.55 | bool / 0..1 / 0.1..1 |

**Debug** (not saved):
- "Show anomaly footprints": ellipses and forecourt rings, draped like `EmitterSiteDebugRings`.
- "Label machines".
- Navigation actions (§C6).

**Status line:** machine count, offline list (index, kind, station), nearest machine with distance, the anomaly weights at the camera, and registry identity.

---

## T. Technical verifications (not user decisions)

1. **T1.** Main spire relay height and knot attachment against the main spire asset's connector rings. Default 1,743 m.
2. **T2.** Depth-writing beam core in the transparent pass. Confirm that Sunshine's resolved depth includes it (precedent: ocean), and that a 1-3 px moving hole leaves no temporal-history trails. If it fails, set `DepthWriteCore` off.
3. **T3.** Sunshine `tracked_point_lights` cost and moving-light history artefacts. Gate: ≤ 0.5 ms on A1000.
4. **T4.** Near-field rim-wall presentation within 0-5 km of the wall (global LOD edge walls near the player, Terrain3D at across ≈ 0). The machine near mesh must hide any gap.
5. **T5.** Report the chord terrain occlusion as a diagnostic: the fraction of each chord below terrain, sampled every 1 km. Occluded spans are accepted, since the depth test simply hides them.
6. **T6.** `EmitterPoiStreamerCore` accepts a third kind without behaviour change for emitter kinds (`emitter_poi_streamer_test` stays green).
7. **T7.** The far-viewport child hook changes no existing far-viewport output when no child is added (far capture diff).

---

## V. Tests and gates

### V.1 Headless (pattern: `tools/X.cs` plus a `tools/x.gd` runner)

| Test | Asserts |
|---|---|
| `emitter_grid_topology_test` | 600 links. Every regular site has in-degree and out-degree 1 on its column; the main site has 6 + 6. Each column is one cycle of 100 links through main. The seam link (row 99 → 0) exists when neither row is main, and its endpoints equal the two sites' relay points in world space (< 1 cm). Along each chain, `P1(k) == P0(k+1)` (< 1 mm) and Σ Δp = C ± 1 m. Chord length is in [300, 345] km. The mid-span height matches the analytic `R − ρ·cos(θ/2)` within 1 m. Identity is stable across 1, 3 and 8 registry build threads. A golden JSON holds the counts and the first and last link per column for seed 1337. |
| Rebase invariance | Anchor sequences including a seam wrap and random 32 km recentres: canonical endpoints round-trip < 1 cm. `FillWorldEndpoints` is identical to per-link `WorldPosition`. Rebuild < 0.5 ms (Release). |
| `emitter_grid_pulse_clock_test` (pure, CPU reference `EvaluateIntensity(link, f, t, frameDt)`) | **Timing determinism:**<br>- Heads are identical at t and t + k·Period.<br>- A direct evaluation equals 72,000 steps of 1/60 s (pure; no drift).<br>- Restoring a saved clock value reproduces the head list and every link intensity exactly.<br>- A rewind or a 1 h forward jump gives the same result as direct evaluation.<br>**One-way circuit:**<br>- At auto speed, exactly one head per chain is in flight at every sampled t.<br>- A head reaches q = C at τ = Period ± 1 ms, the same instant the next head launches at q = 0.<br>- Per-link transit is 3.0 s ± 1 ms for a 314 km link.<br>- The in-flight count matches J for manual speeds 10, 104.7 and 300 km/s.<br>- The relaunch flare fires once per period.<br>**Direction:** retrograde `intensity(p)` equals prograde `intensity(C − p)`.<br>**Sync:** with stagger 0, all six chains have equal q at all times; stagger offsets match.<br>**Continuity:**<br>- Intensity is continuous (finite-difference bound) along each whole loop for q ∈ (0, C): across every chain-to-chain junction and across the ring-seam link (row 99 → 0), including a test registry that puts a row at around ≈ 0.<br>- The only allowed discontinuity is at q = 0 ≡ C (the main spire).<br>- No trail is lit at q < 0, so nothing wraps through the main spire.<br>**Lit links:** at auto speed the lit-link count stays ≤ 15 per chain at all sampled t (cost gate for §G7). |
| `emitter_grid_settings_smoke` / `climate_machine_settings_smoke` | Defaults, round-trip, clamps, reset, invalid path. |
| `climate_machine_registry_test` | Determinism across 1, 3 and 8 threads, plus a byte image golden (seed 1337 and a seed > 2³²). 200 machines; exactly 6 offline (3 Storm, 3 Drought, assigned by land fraction); at most one per station; spacing ≥ 10 stations; none within 2 gaps of `MainRow`. Influence is disjoint from all 595 emitter sites. O(1) lookup equals brute force on 2M random points plus seam, wall and corner points. |
| `emitter_site_baseline_test` (extended) | **v4 and gen-16 goldens unchanged.** A **new gen17_v5 case** (recorded in phase G0, before any change) is unchanged. A v6 case is added after C3. |
| `climate_terrain_v6_test` | The forecourt is exactly flat at P inside the pad. Vegetation is blocked (grass and tree tiles over the quay have 0 instances). The v6 hash differs from v5. A v5 world loads with identical hash and terrain. |
| `climate_weather_test` | (1) **The v5 weather fingerprint** (raw query at 256 points × 6 times, plus a lightning event list over 10 min) equals the G0 golden. (2) v6 repeatability: two runs, and a rewind or forward jump, give identical queries and identical lightning event IDs. (3) At the storm centre, precipitation ≥ 0.8 at every 37 s sample over 3 h. (4) In the drought core, precipitation is 0, coverage ≤ 0.15 of natural, and no natural strike targets fall inside the core. (5) Anomaly weight is continuous (finite differences), including an anomaly forced across the seam by a test recipe override. (6) The cache signature changes with `AnomalyStrength`. (7) U2: no grid code reads the climate registry. A source-level check confirms that `EmitterGrid*` files have no reference to `ClimateMachine*`. |
| `climate_machine_navigator_test` | Nearest machine and its filters agree with brute force; pose landing checks pass. |
| Underground round-trip | Existing smoke plus the new participants: hidden at deep hide, retained, restored. |
| Regressions | `emitter_far_standin_test`, `emitter_poi_streamer_test`, `emitter_site_registry_test`, `natural_weather_lifecycle_smoke_test`, `thunder_behavior_smoke_test`, `weather_ambience_runtime_smoke_test`, `world_loading_startup_contract`. |

### V.2 Windowed capture route

`tools/emitter_grid_capture_route.gd` plus `run_emitter_grid_capture_route.ps1`, modelled on `tools/emitter_capture_route.gd`. It runs on a v6 world, with isolated preferences, scheduled by the pulse clock. Run it only when no other Godot process is running.

**Shots:**
1. Night ground pose: a time series of −20 s..+20 s around the computed pass of the nearest chain, at 30 fps capture for ±3 s. Shows the approach, the about 2 s overhead streak (check the head streak does not step), the afterglow and the recession.
2. The far arch at night, aimed at the opposite ring, about 150 s after a local pass: the head is near the zenith and sprite-visible.
2b. The main spire at relaunch: the flare, and the return-side and exit-side knot fan.
3. The same pose by day (faint) and at total eclipse (forced; bright).
4. A pulse through the deck at 4 km mid-span, from below and from above the clouds (T2 check).
5. A lightning crawl next to the parked storm.
6. Wall Engine forecourt, day and night.
7. An offline storm machine and an offline drought machine (dust, dry grass).
8. A 150 km high view of the wall strings, with a dark gap.
9. M1 emitter shots for the ±0.2 ms regression.

**Report:** frame-time p95 per shot, lit link count, GPU markers, and the human checklist (shimmer under FXAA/TAA, halo look, hole artefacts, cloud glow).

**Gates:**
- Every V.1 test passes.
- The §G7 budgets are met.
- No shader errors.
- v4 and v5 goldens unchanged.
- Primary-agent visual review of the route before reporting completion.

---

## P. Phases (implementer-sized: Sonnet, medium)

| Step | Deliverable | Depends on | Parallel with | Tests |
|---|---|---|---|---|
| **G0** | Goldens only, no behaviour change: the gen17_v5 terrain case in `emitter_site_baseline_golden.json`, and a v5 weather fingerprint fixture (extend `natural_weather_lifecycle_smoke_test`) | none | none | baseline and weather goldens |
| G1 | `EmitterGridTopology` plus `EmitterGridPulseClock` (pure, one-way circuit, direction, stagger, relaunch) plus CPU reference intensity | G0 | C1 | topology, pulse clock, rebase |
| G2 | `EmitterGridBeamLayer` (main camera): ribbon mesh, MultiMesh, shader (energy-conserving width, day/night/eclipse B, atmosphere, pulse, residual), head sprites, rebase rebuild, scene node, settings resource/defaults plus smoke | G1 | C2, C5 | settings smoke; manual windowed check |
| G3 | Drawer section "Emitter relay grid", debug views, status; `EmitterGridParticipant` | G2 | G4, C3 | underground round-trip; drawer smoke |
| G4 | Far-viewport hooks (`AddScaledWorldChild`, public `ScaledPoint`), `EmitterGridFarLayer`, near/far crossfade | G2 | G3, G5 | T7 far capture diff |
| G5 | Cloud interaction: Tier 1 CPU sampling, Tier 2 far-cloud glow uniforms, depth-write core (T2); Tier 3 behind a measurement gate (T3) | G2 (Tier 2 needs G4) | G6a | capture shot 4 |
| G6a | Lightning crawl (event consumer) | G2 | G5 | crawl unit test with synthetic events; capture shot 5 |
| C1 | `ClimateMachineRecipe` plus `ClimateMachineRegistry` (pure, Wall Engines, 6 offline with a 3/3 split) plus goldens | G0 | G1, G2 | registry test |
| C2 | v6 generator id, forecourt operator, shaper composition, diagnostics, hash, `RingTerrainSampler.ClimateMachines`, tree cache key, launcher/preview toggle (off by default) | C1 | G2-G6 | v6 terrain test; baseline goldens |
| C3 | Weather anomaly field: parked-storm merge, drought suppression, lightning loop and natural-strike suppression, rain-curtain ID branch, cache signature and revision, far-atlas splat | C2 | G3-G6a (touches different files; **not** with G6b) | climate weather test; weather regressions |
| C3b | Make v6 the default for new runtime-surface worlds (U4). A one-line launcher change, after C3's tests pass. v5 slots untouched | C3 | C4 | `world_loading_startup_contract`; fresh-world spawn test |
| C4 | Drought presentation: dust haze input, grass modifier zones | C3 | C5 | capture shot 7 |
| C5 | `ClimateMachineMeshBuilder`, `ClimateMachineStandInLayer` plus shader, far wall strips (needs G4 hook), `ClimateMachineStandInParticipant`, asset-import fallback | C1 (far strips: G4) | C2, C3 | stand-in test (as `emitter_far_standin_test`) |
| C6 | `ClimateMachineStreamer` (core reuse, T6) plus collision | C5, C2 | C4, C7 | streamer core test; T6 |
| C7 | Navigator, drawer section "Climate machines", settings, teleport actions | C1 (filters need C3) | C6 | navigator test; settings smoke |
| G6b | (Optional) pulse-triggered strikes, a new deterministic lightning source | G5, C3 | none | deterministic event IDs; rewind |
| V1 | Capture route plus perf report for both tracks | all | none | §V.2 gates |

**Parallelism:**
- After G0, the G track (beams) and the C track (machines) are independent.
- Shared files are serialised as follows:
  - `RingScaledFarViewport.cs`: G4 → G5 Tier 2 → C3 atlas splat → C5 strips.
  - `RingVolumetricCloudLayer*.cs`: C3 → G6b.
  - `UndergroundParticipants.cs`: G3 ↔ C5, a one-line merge.
- **Critical path:** G0 → C1 → C2 → C3 → C4 → V1.

---

## U. User decisions: resolved

All four questions were answered by the user on 2026-10-16. They are recorded as binding decisions 6-9 below, and the spec has been revised to match.

- **U1** Machine form: Wall Engines. Spires and Sky Weirs are rejected (§C1).
- **U2** Grid coupling: only through the weather. There is no direct effect (§C4).
- **U3** Pulse: a one-way circuit, one front per chain per period (§G3).
- **U4** 6 of 200 offline, 3 storms and 3 droughts, spread out, none near the main spire. v6 becomes the default after C3; v5 is untouched (§C2, §C3, phase C3b).

No user questions are open. The lead defaults below stand unless the user overrides them:
- NightResidual 0.03 and DayResidual 0, with DayPeak 0.2 of night.
- Pulse direction prograde (a setting); the six chains in sync (stagger 0).
- Trail 2.5 s, wake 10 s at 0.05, leading edge 3 km.
- Head sprite only beyond 300-600 km.
- Relay height 1,743 m on both kinds.
- Weather clock for pulses.
- Lightning crawl on; pulse-triggered strikes off.
- Cloud interaction tiers 1+2 on, tier 3 off.

---

## Appendix: key numbers

- **Grid:**
  - Row pitch 314.159 km; links 588 chain + 12 knot = 600.
  - Chord ≈ 314.0 km; sagitta 2,466 m; mid-span ≈ P̄ + 4.21 km.
  - Knot links 315 / 324 / 341 km. Per chain, one exit link and one return link at the main spire.
  - The chord rises 62 m / 304 m / 1.32 km at 2 / 10 / 50 km from an end.
- **Pulse (auto, one-way):**
  - v = C/period = 104.72 km/s; 3.0 s per link; one head per chain in flight.
  - Arch sweep 0.60°/s; zenith about 150 s after a local pass.
  - Overhead streak about 2 s at 26.5 km lateral; the ±300 km close passage lasts 5.7 s.
  - Trail 2.5 s (262 km); wake 10 s (1,047 km); lit links ≤ 15 per chain, ≤ 90 in total.
  - Head moves 1.75 km per 60 fps frame.
- **Clocks:** weather clock (real time, saved); day 4,500 s; celestial TimeScale 1-100.
- **Wall Engines:**
  - 100 stations × 2 walls; walls 10 km.
  - Face 24 km × 10 km; vents at 2/4/6 km; forecourt 20 × 3 km; influence ≤ 9 km.
  - At least 120 km clear of emitter influence.
  - 6 offline (3 Storm, 3 Drought), ≥ 10 stations apart, none within 2 gaps of the main row.
  - Anomaly radii: storm 110 × 70 km, drought 160 × 100 km.
  - Parked storm IDs −20000 − machineIndex.

---

## USER DECISIONS (2026-10-16): binding

1. **Emitter grid.** The existing beam emitters form one network.
   - Relay beams run **along the ring only**. Each site links to its counterpart in the adjacent row, giving parallel chains around the whole ring. There are **no across-ring links**.
   - Concept, verified above (F3): straight chords with about a 2.5 km sagitta that cross the cloud deck mid-span and read as light lines arcing up the sky. Travelling light pulses; lightning may crawl along beams.
   - The main-row fit (the knot) and the seam link are specified in §G1.
2. **Climate machines are a SEPARATE structure** from the emitters: ring-spanning tier-3 constructs that run the weather. Some are offline, which explains a local permanent storm or drought.
3. **Failures are fixed by the world seed.**
   - Deterministic lore, with no dynamic failure or repair.
   - Offline machines produce a local anomaly through the weather system: a parked mature storm, or a drought (haze/dust, clear skies, dry grass tint).
4. **Beams are faint by day and more visible by night**, driven by the existing sun, day and eclipse lighting state.
5. **Refinement of decision 4: links are NOT always visible.**
   - They are mostly seen through a pulse that sweeps the length of the ring about every 5 minutes.
   - The day/night rule applies to the pulse and to any faint baseline: faint by day, more visible at night.
   - The steady-state baseline is near-invisible.
   - The pulse runs on a deterministic, saved clock.
   - Pulse period and pulse speed are separate grave-key settings, with the 5-minute cadence as the default.
6. **U1, machine form: Wall Engines.** Paired machine bays are built into both rim walls at every emitter-row gap (§C1). Centre-line Weather Spires and Sky Weirs are rejected.
7. **U2, grid coupling: only through the weather.** An offline machine has no direct effect on the emitter grid. The grid reacts only physically: dimming and diffusion in a parked storm, lightning crawl, and the optional pulse-triggered strikes (§C4, §G5, §G6).
8. **U3, pulse: a one-way circuit.**
   - A single front per chain leaves the main spire and travels all the way around the closed loop back to it, in one period.
   - Defaults: period 5 minutes; auto speed C/period ≈ 105 km/s, about 3 s per 314 km link, so about 2 s overhead.
   - Period and speed stay separate grave-key settings (§G3, §C8).
9. **U4, offline machines and rollout.**
   - 6 of the 200 machines are offline, about 3 storms and 3 droughts, spread out, none near the main spire (§C2).
   - v6 becomes the default for new worlds once C3 ships; v5 worlds are untouched (§C3, phase C3b).
