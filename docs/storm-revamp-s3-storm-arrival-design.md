# Storm revamp S3 design: storm arrival sequence (lead, 2026-10-25, RTX 4070)

Work orders for `docs/storm-revamp-plan.md` step S3, plus the cross-cutting flight-ready wind/turbulence query. Design only; no GPU runs were made. Line refs are the working tree of 2026-10-25 (main 3110b7e plus the uncommitted S0/S1/S4/S5 work).

User direction: immersion and spectacle only (no gameplay effects), exaggerated storms, walking now, flying vehicles later.

## 1. Findings that shape the design

1. **Storms barely translate; they grow and shrink in place.**
   - Every natural system is an ellipse advected by one uniform vector, `WeatherWindMetersPerSecond` = (2.2, 0.7) m/s (`RingVolumetricCloudLayer.cs:107`). A 50 km MajorFront takes about 6 hours to drift past a point.
   - The lifecycle envelope scales the radii (`TryBuildWeatherSystem`, radii x `lifecycle.Envelope`): birth 120 s (0 to 0.3), develop 300 s (0.3 to 1), mature 900 s, dissipate 360 s (1 to 0), quiet 420 s (`:113-117`, `EvaluateWeatherLifecycle`), with per-generation variation.
   - So the rim of a 50 km storm sweeps outward over an observer at about 100-200 m/s during develop and retreats at a similar speed in dissipate. During mature it moves only at the 2.3 m/s drift.
   - Consequence: the sequence must be defined relative to the **rim motion** (drift + growth), not the drift alone. A standing observer then gets the full sequence once per ~35 min cycle: rim sweep (approach, gust front, fat drops) during develop, core or trailing during mature, trailing then clearing in dissipate.
2. **The rain rim is not the cloud rim.**
   - Influence is `1 - SmoothUnit((n - 0.32) / 0.68)` in normalised ellipse distance n (`EvaluateMacroWeather`).
   - Precipitation is `P x influence x SmoothUnit((coverage - 0.35) / 0.55)` (`QueryRawWeatherAtTime`:3320). Local effects then gate at `PrecipitationStart` 0.18.
   - For a MajorFront, rain therefore starts near n ~ 0.7-0.8: there is a cloud skirt without rain between the rain rim and n = 1. That skirt is where the shelf cloud, gust front and fat drops belong.
3. **The whole field is a pure function of (position, time)**, including lifecycle and rewinds. Wind already exploits this (`QueryLocalWeatherAtTime` uses an 8-sample history quadrature, `RingVolumetricCloudLayer.Wind.cs`). The arrival signal can be pure as well, so captures and rewinds stay reproducible.
4. **Wind today** (`ResolveSurfaceWind`):
   - Speed is `lerp(base, StormWindSpeed 28, StormFactor)` and direction is the base direction rotated by 18 deg x factor. StormFactor = influence x precip x kind weight.
   - The history quadrature gives a 5 s rise and an 18 s decay. The travelling gust texture multiplies speed (`RingWindField`, base 0.55 + 0.9 x noise x 1.6).
   - There is no outflow direction, no lull and no gust-front peak.
5. **Lightning**:
   - Strikes are world events, deterministic per system slot. They fall uniformly within normalised radius 0.6 (`TrySampleStormPosition`), so they never come near the rim.
   - `TryCreateNaturalEvent` feeds `natural_weather_v5_fingerprint_golden.json` through reflection (`tools/NaturalWeatherFingerprintTest.cs`). Any change to it breaks the golden. An additional stream does not.
6. **Temperature exists** as a normalised terrain climate value (`TerrainSample.Temperature`). It is used only for snow (`SnowTemperatureThreshold` -0.08, `RingLocalWeatherEffects.cs:829-835`).
7. **Haze mist**:
   - Physical haze has a mist layer `sigma_M0 x exp(-h / H_mist)` (400 m) above the nominal floor (`ring_haze_shared.gdshaderinc`). It is driven from `RingAtmosphereModel` (`HazeMistDensityScale`, `HazeMistBaseHeightMeters`), and Sunshine reads it through the CPU profile.
   - A local FogVolume was measured at about +0.14 ms by day (`docs/night-vegetation-atmosphere-diagnostics.md`). It was left diagnostic-only because Sunshine composites after transparents.
8. **Compositor order** is enforced by `RingLocalWeatherEffects.EnsureRainHazeCompositorLast` (RainFog.cs): Sunshine and anything else first, then the streak raster pass, then the rain haze composite. It only moves those two to the end, so an effect inserted after Sunshine keeps its place.
9. **Ring physics note.** The spin axis is the across axis, so Coriolis has no horizontal component for horizontal wind. There is no Ekman veer; wind direction changes with height only because of the storm (surface outflow) versus the upper-level drift direction that also shears the anvils (`header1.w`).

## 2. The arrival signal

### 2.1 Geometry (pure, deterministic)
For each candidate system S near the observer x at time t (natural systems, plus manual ones treated as static):
- `n(x,t)`: normalised elliptical distance, using `EvaluateMacroWeather`'s source-frame maths (wrapped around, enveloped radii).
- **Rim speed** `v_r` (m/s, > 0 = rain advancing over the observer):
  - Analytic: `dn/dt = -(M^T M (x - c) . w) / (E^2 n) - n * rho`.
    - `M` is the unscaled ellipse normaliser; `w` is `WeatherWindMetersPerSecond` (0 for manual systems).
    - `rho = E'/E` is the relative envelope rate. `E'` comes from a central difference of `EvaluateWeatherLifecycle` at t +/- 1 s; it is continuous across stages because SmoothUnit has zero end slopes.
  - Then `v_r = -dn/dt * |x - c| / n`.
- **Rain rim** `n_rain`: solves `P x I(n) x C(n) = PrecipitationStart` by 16-step bisection.
  - `C(n)` mirrors `ResolveLocalCloudCoverage` with weather noise fixed at its mean 0.5 and a synthetic `MacroWeatherSample`.
  - This makes the rim a property of the system, not of local noise; local noise still modulates the actual rain amount.
  - If no solution exists (a dry system), `n_rain` = 0.85 for the gust and shelf logic, and the rain weights stay 0.
- **Signed rim distance** `d_r = |x - c| * (1 - n_rain / n)`, measured along the radial ray, > 0 outside the rain.
- **Bearings**: `StormBearing` points toward the rain, `OutflowDirection` = -StormBearing. Both are in the ring frame (around, across).
- **Severity**: `P x density x kind weight`, with kind weight Cumulus 0.15, CloudBank 0.45, MajorFront 1.0, Exceptional 1.25, and Superstorm 1.5 once S4 WO-B lands. Clamped to 0..1.5.

### 2.2 Bands (time-scaled with a metre floor)
- Band width `W = clamp(|v_r| x Lead, 400 m, 6 km)`, where Lead is "Gust front lead" (default 60 s); `u = d_r / W`.
- Fast rim sweeps therefore last about Lead seconds. Drift-only rims give a 400 m band, which takes about 3 min to cross while standing or 1-5 min while walking.
- Leading factor `lead = smoothstep(0, 3 m/s, v_r)`; trailing factor `trail = smoothstep(0.5, 7 m/s, -v_r)`.

| Weight | Definition (all 0..1, independent) |
|---|---|
| Approach | `outside x smoothstep(W + 18 km, W, d_r) x lead` |
| GustFront | `smoothstep(1.0, 0.88, u) x smoothstep(-0.35, 0, u) x mix(0.35, 1, lead)`. The sharp front edge (12% of the band) gives the speed jump. Minimum 0.35 on flanks and trailing sides for Major+ ("outflow skirt", so walking into any strong storm gives a gust front). Scaled by kind: CloudBank 0.4, Cumulus 0.15 |
| FatDrop | `smoothstep(0.35, 0.1, u) x smoothstep(-0.4, -0.1, u)`: big sparse drops from the skirt cloud just before and at the rain rim (only where n < 1, so there is cloud overhead) |
| Core | `smoothstep(n_rain - 0.08, n_rain - 0.35, n) x (1 - trail)` |
| Trailing | `inside(n < n_rain) x trail` |
| Clearing | `smoothstep(0.05, 0.3, RecentRain) x (1 - inside) x exp(-TimeSinceRain / 1200 s)` |

- `Phase` is the argmax of the weights, with ties ordered Core > GustFront > FatDrop > Trailing > Approach > Clearing. Clear when all weights are below 0.05.
- With two overlapping systems, each weight is the max over the top-2 systems ranked by severity x proximity. Bearing, kind and id come from the dominant system.

**Resulting sequences:**
- Growth: Approach, GustFront, FatDrop, Core (front half of the storm) or Trailing (back half once mature drift sets the sign), then Trailing through dissipate, then Clearing.
- Walking into a mature storm: Approach (leading side only), then GustFront (the skirt at 0.35 on other sides), then FatDrop and Core.

### 2.3 Rain history (deterministic aftermath)
- `TimeSinceRainSeconds` and `RecentRainAmount` at x: the system-geometry rain proxy `P x I(n)`, evaluated at lags {15, 30, 60, 120, 240, 480, 960, 1500} s with exponential weights (tau 600 s), using `BuildNearbyWeatherSystems` at each lag.
- Refresh: round-robin one lag per frame, a full refresh every 1 s.
- This signal is S3's shared aftermath clock for mist, rainbow, the S2 drips and the S5 drip one-shots (see section 9).

### 2.4 Temperature
- `CoolingOffset` (normalised terrain-temperature units): GustFront -0.10, Core -0.12, recovering with `exp(-TimeSinceRain / 600 s)`. `EffectiveTemperature = Temperature + CoolingOffset` appears in diagnostics.
- Consumers: mist formation, and hail size (colder means larger stones).
- **Not wired into snow in v1**: rain/snow flapping at the gust front would read as a bug. It is a one-line option later.

### 2.5 API
- `scripts/Weather/StormArrival.cs` (pure): `StormArrivalPhase` enum, `StormArrivalState` record, `StormArrivalMath.Evaluate(in StormSystemGeometry, in StormArrivalTuning)`, `HailAt`, and the shared leading formula `RimLeadingWeight(...)`. It also hosts the static `Provider` (pattern of `PrecipitationAltitude.ColumnProvider`).
- `StormArrivalState` fields:
  - identity: `SystemId, Kind, Severity, Phase`;
  - weights: `Approach/GustFront/FatDrop/Core/Trailing/Clearing`;
  - rim: `RimDistanceMeters, RimSpeedMetersPerSecond, TimeToRainSeconds` (+inf unless approaching);
  - directions: `StormBearing, OutflowDirection`;
  - hail: `HailIntensity, HailSize`;
  - aftermath: `CoolingOffset, TimeSinceRainSeconds, RecentRainAmount`;
  - `Valid`.
- `scripts/RingVolumetricCloudLayer.StormArrival.cs`, a **new partial**. It reads private weather helpers but makes no edits to the sculpt-owned main file. It provides:
  - `QueryStormArrivalAtTime(pos, t)` (pure, uncached; for tests and flight);
  - `ObserverStormArrival`: lazily computed once per process frame at the explorer, with no node and no `_Process` hook;
  - `GetStormArrivalDiagnostic()`;
  - debug override env `RING_STORM_ARRIVAL=<phase>:<weight>` for captures.
- Search reach: `BuildNearbyWeatherSystems(x, t, 20 km)` (about 169 cell builds). Cached per 0.25 s lattice and refreshed when the observer moves more than 16 m.
- Altitude: above the cloud top (`PrecipitationAltitude` Above) all precipitation weights are 0; debris needs an AGL under 30 m. This keeps flight sane.

## 3. Consumers

| Consumer | Reads | Behaviour |
|---|---|---|
| Wind (WO-B1) | all weights, Outflow, Bearing | See section 3.1 |
| Rain tiers (WO-C1, streak owner) | FatDrop, Core x Severity, Trailing | FatDrop: density floor 0.04-0.12 even below `PrecipitationStart`, drop length x1.6 and width x1.8, big splashes. Core: intensity bias +0.15 x Severity. Trailing: tier capped at Steady, length x0.85. **S1 haze-veil gate:** heavy core = `Core x Severity >= 0.7` (the formal definition of S1's "severe system / heavy core") |
| Hail (WO-C2, C3) | HailIntensity, HailSize | See section 4 |
| Debris (WO-D1) | GustFront x "Gust front strength", RecentRain | Leaves and grass bits (Forest/Grassland), dust (arid), spindrift (snow); dust is suppressed once RecentRain > 0.2 |
| Mist and low fog (WO-E1/E2) | Clearing, CoolingOffset, calm wind | See section 5 |
| Rainbow (WO-F1) | Clearing/Trailing geometry, rain proxy along azimuths | See section 6 |
| Audio (WO-C3, S5 owner) | GustFront edge, FatDrop, Hail, Approach, Bearing, TimeSinceRain | Gust-front roar swell one-shot on GustFront rising past 0.3, from the Bearing sector; leaf-skitter/dust-hiss loops x GustFront x biome; fat-drop plips at a rate from FatDrop; hail bed per sector material plus one-shots; distant-storm bed +3 dB x Approach; drips keyed to TimeSinceRain |
| Lightning (WO-G1) | none (world-intrinsic) | Leading-edge strike stream, see section 3.2 |
| Temperature | CoolingOffset | Diagnostics, mist, hail size |
| Light/darkening | none | "Darkening sky ahead" is already delivered by the S0 path darkening (4 taps over 40 km) plus the V3 deck. S3 adds no light term, to avoid double darkening |

### 3.1 Wind modifier (applied once, after the history quadrature, in `QueryLocalWeatherAtTime`)
Applied to the quadrature mean before caching, so vegetation, water, streak slant, audio and the minimap all agree. "Gust front strength" = 0 makes it the identity (bit-identical to today).

| Weight | Speed | Direction | Gust strength / base |
|---|---|---|---|
| Approach | x(1 - 0.45a) ("calm before the storm"), plus 2-4 m/s inflow toward the storm | blend 0.4a toward Bearing | x0.7 |
| GustFront | `max(current, StormWindSpeed x (1 + 0.3 S))`, blended by g | blend 0.85g toward Outflow | x(1 + 0.8g) / +0.15g |
| Core | existing storm wind | existing (+18 deg) | x1.3 |
| Trailing | x(1 - 0.4t) | relax to base | x1 |
| Clearing | x(1 - 0.5c) (calm air lets mist form) | base | x0.6 |

- The gust multipliers go into `CaptureWindSnapshot` / `SampleAtTime` as a per-call factor (the global `ring_wind_gust_strength` is camera-local, which is fine at km scales).
- Add the arrival settings to the `_windQuerySignature`. **Conflict:** S4 WO-C also edits that line, so B1 and WO-C run serially.

### 3.2 Lightning distance bias (world-intrinsic, fingerprint-safe)
- Add `TryCreateLeadingEdgeEvent`, a second stream per Major+ system with new salts (>= 140):
  - cadence `(10 + 20 x hash) / LightningVisualPolicy.FrequencyScale`, so it obeys WO-E's frequency setting and reduced flashes;
  - position: n in [0.55, 1.0] on the leading arc, by rejection sampling with `RimLeadingWeight` (the same formula as the shelf cloud);
  - "bolt from the blue": 10% of strikes land at n 1.0-1.08, ahead of the rain.
- Strikes therefore close in with the rim, and the observer needs no special case. `TryCreateNaturalEvent` is unchanged, so the fingerprint stays bit-identical.
- Acceptance: in a production growth timeline, the median strike distance during GustFront is at most 0.5x the median during Approach.

## 4. Hail
- **Field `HailAt(x,t)`**, deterministic per system:
  - Kind >= MajorFront and lifecycle code L in [0.8, 2.3].
  - Swath count `floor(Hail x (Major 0-2, Exceptional 1-4, Superstorm 2-5))` from the hash.
  - Each swath is an ellipse 1.5-4 km x 6-15 km, elongated along the rim motion (radial for growth, drift for mature), centred at n 0.15-0.55 on the leading half.
  - Each swath is live for 6-12 min with a hashed start; the profile has a smooth edge; the result is multiplied by Severity and gated by the local rain proxy >= 0.6.
  - `HailSize` 0..1 (pea to golf ball) comes from the hash plus cooling.
  - Exaggerated: hail occurs in most mature fronts.
- **Visuals (WO-C2, streak owner).** Hail is a drop class in the existing stateless lattice (`ring_rain_streaks.glsl`), not a new system:
  - per-drop hash < hailFraction (= 0.6 x HailIntensity) renders a lit round ice pellet: 6-25 mm, min 1.5 px, fall 25-35 m/s, little slant;
  - analytic bounce: 1-2 hops of 0.2-0.6 m over 0.3-0.5 s at the drop's map landing height;
  - white impact puff on hard ground;
  - this needs one new `hail` vec4 in the Frame block.
  - Ground whitening under sustained hail is an optional S2 wetness-include term (coordination item).
- **Audio assets** (none downloaded; Freesound/OpenGameArt CC0, recorded with SHA-256 and processing per `resources/audio/weather/LICENSE.md`; mono 44.1 kHz, loops >= 20 s and seamless):
  - loops: `hail_ground_loop` (soil/grass), `hail_hard_loop` (rock/concrete), `hail_metal_loop` (POI roofs; also the Roof/Indoors drum), `hail_water_loop`, `hail_foliage_loop`;
  - one-shots: `hail_hit_hard` x10, `hail_hit_metal` x8, `hail_hit_ground` x8, `hail_large_thud` x6, `hail_bounce_tick` x6.
  - This supersedes S5's "hail loop + 8 one-shots" line.
- **Other S3 audio assets:** `gust_front_roar` x3 (8-15 s swells), `dust_hiss_loop`, `leaf_skitter_loop`, `fat_drop_plip` x10 (hard and leaf). S5 already lists the foliage wind-rustle loop and drips.
- Optional: `birds_after_rain_loop`.

## 5. Mist and low fog after rain
- **WO-E1, analytic post-rain mist (no shader change, no reimport).**
  - `RingAtmosphereModel` gets a weather mist factor pair (density x, height x) that multiplies the user's Mist density/height in the same place `SetPhysicalControls` applies them.
  - Target: `M = PostRainMist x Clearing x calm x cool`, with calm = `1 - smoothstep(3, 8 m/s, wind)` and cool = `smoothstep(0, -0.1, CoolingOffset)`.
  - Mapping: density x(1 + 300M) (ground visibility about 3-6 km at M = 1), height x(1 - 0.7M) (400 to 120 m), so the mist pools in lowlands and the observer on a ridge sees it below.
  - The factor is quantised to 1/32 steps, and a change is published at most every 2 s through the existing Revision path. That bounds ambient/LUT recomputes to a few per minute and keeps Sunshine, sky, objects and the CPU irradiance consistent automatically.
  - Fades with the Clearing weight (tau 20 min).
- **WO-E2, low fog patches: spike, go/no-go.**
  - 4-8 FogVolumes, 60-200 m, at terrain height in hollows within 300 m (heights from the S1 occlusion map), density 0.008-0.02, only while M > 0.3.
  - **No-go if** Sunshine paints cloud over patch tops against the sky at the horizon, or the cost exceeds 0.2 ms. Godot fog also needs one owner for the Environment volumetric fog settings, which is `day_side_atmosphere.gd` (S0 balance owner), so its enable flag is a request to that owner.
- **S2 boundary:** S3 owns the atmospheric mist and patches; S2 owns surface effects (wet sheen, puddles, drips, any surface steam wisps). Both read the S3 rain history.

## 6. Rainbow (physically placed)
- **Geometry.**
  - Antisolar direction `a = -toSun` (the direction the DirectionalLight travels), from the light's direction in world space and the local ring frame.
  - Primary bow at 40.6-42.4 deg from a (red outside); secondary at 50.3-53.5 deg (colours reversed, 0.43x). Smear by the 0.5 deg sun disc.
  - Inside-primary glow 0.15x fading to 0 at 25 deg, so Alexander's dark band reads without darkening anything.
  - Visible only when sun elevation < 42.4 deg (part of the circle above the horizon); the below-horizon part shows only against rain in front of terrain.
- **Illumination and rain (CPU, `scripts/Weather/RingRainbow.cs`, 2 Hz).**
  - For 5 azimuths across the visible arc and distances {0.5, 1.5, 3, 6, 10} km: rain proxy `P x I(n)` from the arrival evaluator's cached systems, times drop lighting `sunVisCam x exp(-depthIntoRain / 3 km)`.
  - The sun must reach the observer: camera direct-sun visibility x (1 - F(D at camera)) from the S0 terms. Light enters from the clear side, so only rain near the rim is lit.
  - Haze: multiply by the haze transmittance to the weighted mean distance (use the CPU mirror of the physical haze, or `exp(-sigma_ext(h_cam) x s)` from RingAtmosphereModel's published sigmas plus the storm add).
  - Per-system drop-size hash x0.4-1.2: some storms give vivid bows, most faint ones ("occasionally" comes from geometry plus this hash).
  - Output: a 5-entry azimuth strength array (so partial bows happen), x "Rainbows".
- **Render (WO-F1).**
  - `scripts/render/RingRainbowComposite.gd` + `ring_rainbow.glsl`, a full-screen additive raster CompositorEffect.
  - Inserted after Sunshine's effects and before the streak compositor, re-validated each frame; `EnsureRainHazeCompositorLast` keeps the streaks and haze after it, so near rain and the heavy-rain veil draw in front of the bow and Sunshine's dark storm sits behind it.
  - Depth fade `saturate((sceneDepth - 30 m) / 200 m)`, so nothing is painted on nearby walls.
  - No dispatch when strength is 0.

## 7. Shelf cloud (later work order for the sculpt v2 owner, after V3; serial with WO-B)
**WO-H1.**
- **Bake.** `RingStormProfileBake.glsl` adds a shelf/arcus to the **body** interval along the leading rim:
  - Leading weight `RimLeadingWeight = smoothstep(0, 3 m/s, dot(w, n_hat) + rho * dot(p_b, n_hat))`: the identical formula to the CPU, in the `StormArrival.cs` reference with a matching test.
  - Body base lowered to band ~0.03 (300-500 m AGL) in an annulus n 0.82-1.02.
  - Top rising inward from the leading lip to the deck top over 3-6 km; a sloped leading face with 2-3 noise-defined tiers (no analytic terraces, per V2 rule 4).
  - Present at L 0.3-2.0 and gone in dissipate.
- **Data.** One float per system: `rho` (1/s, signed), computed on the CPU in `BuildNaturalRenderSystem`. The upload slot is the owner's choice (the record layout is theirs). Also write the leading weight to the free aux `.w` for readback.
- **Cost.** Bake only: the march still fetches one texel, so the march cost does not change. The darkness comes from V3's light shortcut.
- **Accept:**
  - from 15-25 km ahead on the leading side at 2 m eye height, sun 35 deg, rain off: a low dark horizontal band with a lighter lip under the deck;
  - plan view: the shelf only on the leading arc (all around during develop);
  - aux readback vs CPU `RimLeadingWeight` within 0.05 at 32 rim points;
  - `GPU.CloudStormMap` +0.05 ms at most and `GPU.Clouds` unchanged within noise (3 A-B-A rounds);
  - `flicker` shows no new spikes.

## 8. Flight-ready wind and turbulence query (WO-B2)
**API** (`scripts/Weather/RingWindQuery.cs`; pure maths in `WindAltitudeProfile.cs` and `StormTurbulence.cs`):
```csharp
public readonly record struct RingWindSample(Vector3 MeanWind, Vector3 GustWind, Vector3 Turbulence, float TurbulenceSigma,
    float TurbulenceLengthMeters, float UpdraftMetersPerSecond, float VerticalShear, float HeightAboveGroundMeters,
    StormArrivalPhase Phase, float StormWeight, PrecipitationPhase Precipitation, float InCloudDepthMeters, bool Valid);
public static class RingWindQuery {   // vectors in the ring frame (around, up, across), m/s
    public static Func<RingCanonicalPosition, double, RingWindSample>? Provider { get; set; }
    public static RingWindSample Sample(RingCanonicalPosition position, double time);
    public static int SampleColumn(RingCanonicalPosition position, double time, ReadOnlySpan<float> altitudes, Span<RingWindSample> results);
}
```
- Pure, thread-safe, allocation-free; valid on the physics thread. Returns calm and `Valid = false` with no world. Installed by the StormArrival partial. **No gameplay consumer.** The only readers are tests and a debug wind-column overlay in debug tooling.

**Profile:**
- Up to 10 m AGL, the existing surface wind unchanged: `GustWind` equals `CurrentWindSnapshot.InstantWind` there.
- 10 m to the boundary-layer top (1 km): power law, alpha 0.14 open / 0.22 forest.
- Above that, the free wind is the weather drift direction (the same direction that shears the anvils), with speed ramping linearly from max(|base|, 4) m/s at 1 km to a jet at the anvil band (25 m/s; x1.4 Exceptional, x1.8 Superstorm).
- Direction veers from the surface (storm-rotated or outflow) to the drift direction across the boundary layer. On a ring there is no Coriolis veer (finding 9).
- The surface gust field fades out by 300 m AGL.
- AGL comes from terrain height cached per 64 m cell.

**Storm terms** (weights from the arrival signal at that column):
- Core updrafts `W_max sin(pi (z - base) / (top - base))`: 15 m/s Major, 30 m/s Exceptional, 40 m/s Superstorm.
  - v1 uses a statistical 4-6 km cell lattice with 30% fill.
  - v2 must mirror the bake's cell hash so updrafts sit in the visible towers. That is a C# port owned by the sculpt owner after V2/V3.
- Precipitation downdraft -5 to -12 m/s from cloud base to 300 m AGL, turning into the surface outflow.

**Turbulence:**
- sigma (m/s): clear boundary layer `0.5 + 0.1 U`; gust front 4-8 near the surface, decaying over 1.5 km; core 5-10 through the cloud depth; in-cloud 2; anvil shear layer 3; overshooting top 6.
- Realisation: 3 octaves of deterministic 3D gradient noise, length 50-300 m, advected with the mean wind and evolved by a slow time coordinate.

**Budget:** at most 3 us per sample; a 32-altitude column at most 0.1 ms.

## 9. File ownership and conflicts

| WO | Files | Owner / sequencing |
|---|---|---|
| A1 core | new `scripts/Weather/StormArrival.cs` | S3, any time |
| A2 adapter | new `scripts/RingVolumetricCloudLayer.StormArrival.cs` (partial; reads `TryBuildWeatherSystem`, `BuildNearbyWeatherSystems`, `EvaluateWeatherLifecycle`, `ResolveLocalCloudCoverage`; **no edit** to the sculpt-owned main file) | S3, any time |
| A3 settings | new `GameplayStormArrivalSettings/Defaults.cs`, `RingLiveTuningDrawer.StormArrival.cs`; one hook line in `RingLiveTuningDrawer.cs` | main sequences the hook line (several agents edit that file) |
| B1 wind | `RingVolumetricCloudLayer.Wind.cs` | serial with S4 WO-C (same signature line) |
| B2 flight API | new `RingWindQuery.cs`, `WindAltitudeProfile.cs`, `StormTurbulence.cs` | S3, after A2 |
| C1/C2 rain, hail visuals | `RingLocalWeatherEffects.RainStreaks.cs`, `.RainFog.cs`, `ring_rain_streaks.glsl(inc)`, `RingRainStreaksComposite.gd`, `RainPrecipitationProfile.cs` | **rain-streak implementer**, after S1 WO5/WO6 are accepted |
| C3 audio | `RingWeatherAmbience.cs`, `RainSoundField.cs`, the asset folder | **S5 owner**, after WO7 and the assets |
| D1 debris | new `scripts/Weather/RingGustFrontDebris.cs`, `materials/ring_gust_debris.gdshader`; budget row in `RingGraphicsQualityController.PresetWeather`; scene node | main sequences the scene and preset edits |
| E1 mist | `scripts/Voxel/LOD/RingAtmosphereModel.cs` (not in flight) | S3 |
| E2 patches spike | new files only; any Environment fog flag goes through the `day_side_atmosphere.gd` owner (S0 balance pass) | spike after E1 |
| F1 rainbow | new `RingRainbow.cs`, `RingRainbowComposite.gd`, `ring_rainbow.glsl`; scene node | S3; insertion rule needs no edit to RainFog.cs |
| G1 lightning | `RingVolumetricCloudLayer.Lightning.cs` | after S4 WO-E (in flight in that file) |
| H1 shelf | bake, `RingStormProfileMap.gd`, `CameraIndependentClouds` upload, `BuildNaturalRenderSystem` | **sculpt v2 owner**, after V3, serial with WO-B |

**S2 coordination (wet-world lead):**
- S3 publishes the rain history and the FatDrop/Hail signals.
- S2 decides whether its surface wetness integrates `RecentRainAmount` or keeps the runtime `SurfaceWetness` integrator. One owner per signal; ask main.
- S2 owns visual drips, puddle ripples from fat drops and hail, and optional hail whitening. S3 owns mist and fog.

## 10. Grave-key tunables (all in a "Storm arrival" group, one new settings trio)

| Setting | Range | Default | Identity at |
|---|---|---|---|
| Gust front strength | 0..2 | 1 | 0: all arrival wind modifiers and debris off |
| Gust front lead (s) | 20..180 | 60 | n/a (pacing of the rim bands) |
| Hail | 0..2 | 1 | 0: no hail |
| Post-rain mist | 0..2 | 1 | 0: haze unchanged |
| Rainbows | 0..2 | 1 | 0: no dispatch |

- Not in the menu: band constants, cooling, turbulence and API constants, and the phase override. Those go in debug tooling: the `RING_STORM_ARRIVAL` env var, the arrival HUD line, and the wind column overlay.
- The lightning stream uses WO-E's "Lightning frequency". Hail and gust audio use the existing Rain and Wind volumes.
- Debris count belongs to the graphics preset: Low 128, Medium 256, High 512, Very High 768, Ultra 1024.

## 11. Perf gates (4070; A1000 when available)
- Arrival CPU: at most 0.05 ms per frame average and 0.25 ms worst frame, including the round-robin history.
- Wind modifier: under 5 us per query.
- Debris: at most 0.15 ms GPU at full gust (High), and 0 with no gust (hidden).
- Hail in the streak pass: at most +0.1 ms over heavy rain.
- Rainbow: at most 0.05 ms while visible, 0 otherwise.
- Mist: 0 GPU, at most 30 Revision publishes per 10 min.
- FogVolume spike: at most 0.2 ms.
- Measure with the perf-capture skill (A-B-A, median over windows 2-6).

## 12. Tests and captures
**Headless:**
- `StormArrivalMathTest` (synthetic systems):
  - phase order for a growth/mature/dissipate cycle, a drift-only pass at an exaggerated drift, and a walk-in;
  - weights continuous (|delta w| <= 0.05 per 0.1 s outside the 12% front edge);
  - analytic dn/dt vs finite difference within 1%;
  - rewind determinism bit-identical;
  - rim-distance sign;
  - `RimLeadingWeight` reference values for H1.
- `storm_arrival_production_test.gd`:
  - picks a natural MajorFront cell by scanning;
  - at a fixed point through one lifecycle: GustFront starts 30-120 s before `PrecipitationPotential` first exceeds the start threshold; Core overlaps rain at least 0.4; Clearing follows; the event-list median strike distance criterion holds.
- **Wind:**
  - Gust front strength 0 gives bit-identical wind (`shared_wind_core_test`, `minimap_wind_smoke`, `WindSettingsSmoke` unchanged);
  - at default: a speed jump of at least 1.4x within 10 s at the front edge and a direction change of at least 30 deg toward Outflow.
- **Hail:** deterministic, 0 for kinds below Major, 0 at Hail 0, swath aspect at least 2.
- **Rainbow maths:** bow at 42 +/- 0.1 deg from the antisolar point; 0 at sun elevation > 42.4 deg, with the sun hidden at the observer, or with no rain on the azimuth.
- **RingWindQuery:**
  - equals the surface snapshot at 2 m within 0.5 m/s;
  - finite from -100 to 60000 m;
  - sigma ordering core > in-cloud > clear;
  - deterministic;
  - microbenchmark against the budget.
- **Regressions:** `NaturalWeatherFingerprintTest` bit-identical, `StormLifecycleCodeTest`, and `StormArrivalSettingsSmoke` (the skill's 10 checks).

**GPU and captures** (new `tools/storm_arrival_capture.gd` + `.ps1`; Movie Maker 30 fps into `.godot/captures/storm_arrival/`; one fixed profile label; prune afterwards):
- `timeline`:
  - fixed observer, natural storm, sun 25 deg behind the observer, cloud clock time-lapsed x6 through approach to clearing;
  - plus a real-time 120 s segment across the gust front;
  - outputs: sheet, `flicker` (no single-frame spikes from phase changes), `mp4`.
- `walkin`: 6 m/s into a mature storm's leading side.
- `rainbow` (`rainbow_gpu_test.gd`):
  - a synthetic forced rain mask: the bow's angular radius is measured on screen at 42 +/- 1 deg with red outside;
  - Rainbows 0 gives `compare` SSIM 1.000 against the baseline;
  - streak and haze pixels sit in front of the bow;
  - no bow on opaque pixels nearer than 30 m.
- `hail` and `gust`: pellets and debris visible; nothing under a POI roof (occlusion map).
- Zero-setting identity: Hail 0, Post-rain mist 0 and Rainbows 0 give SSIM 1.000 (heatmap clean).

**User check** (headphones, Certified):
1. Stand in the open as a front develops: eerie lull, the shelf to one side (after H1), a roar and a wind shift, then dust and leaves, fat plips, a downpour with hail, lightning closing in.
2. Hold through the trailing rain.
3. Clearing: calm, mist in the lowlands, drips, a rainbow if the sun is low behind you.
4. Walk into a mature storm and confirm you still get a gust front.

## 13. Work orders (implementer-sized; each runs `tools/prune_test_output.ps1 -Apply` at the end)
1. **A1, arrival maths.**
   - Pure `StormArrival.cs` (types, Evaluate, bands, HailAt, RimLeadingWeight, rain-rim bisection given a coverage delegate) plus `StormArrivalMathTest`.
   - Accept: the headless maths tests above.
2. **A2, production adapter.**
   - Partial: top-2 search, analytic dn/dt with finite-difference E', n_rain via `ResolveLocalCloudCoverage`, history round-robin, observer cache, diagnostics, env override, provider install.
   - Do not change: `QueryRawWeatherAtTime`, `TryBuildWeatherSystem`, `TryCreateNaturalEvent`, any GPU path.
   - Accept: production test, fingerprint, CPU budget.
3. **A3, settings.** The trio, menu group and smoke, via the live-tuning-setting skill.
4. **B1, wind modifier.**
   - `QueryLocalWeatherAtTime` post-quadrature hook plus the gust multipliers and signature.
   - Accept: identity at 0 and the jump and shift criteria.
   - Escalate if the vegetation or water response shows popping in `flicker`.
5. **B2, RingWindQuery.** Accept: the wind API tests. No consumer besides the debug overlay.
6. **C1, rain hooks (streak owner).** Accept: FatDrop density and size in the `timeline` capture; the haze gate is fed by Core x Severity; S1 GPU tests are unchanged at weight 0.
7. **C2, hail visuals (streak owner).** Accept: the `hail` capture, +0.1 ms at most, no pellets under a roof.
8. **C3, audio (S5 owner, after assets).** Accept: diagnostics show the hail bed and gust swell gains; continuity test rules (no pops, slew caps).
9. **D1, debris.**
   - Stateless MultiMesh like the streak lattice: spawn on the S1 occlusion-map surface (roofs shelter automatically), advect with mean wind plus the gust texture, tumble.
   - Leaves are alpha-scissor, depth-writing and lit (storm-light include, regular haze, shadows). Dust is soft and low.
   - Accept: the `gust` capture, flicker, perf, and zero cost when calm.
10. **E1, mist.** Accept: haze_consistency contracts unchanged at M = 0; the `timeline` clearing frames show lowland mist; the Revision publish cap holds.
11. **E2, fog patch spike.** Report go/no-go against section 5.
12. **F1, rainbow.** Accept: `rainbow_gpu_test`, perf, the user check.
13. **G1, leading-edge lightning (after WO-E).** Accept: fingerprint bit-identical; the strike-distance criterion; the frequency setting scales it.
14. **H1, shelf cloud (sculpt owner, after V3).** Accept: section 7.
15. **I1, capture harness, perf table and user check.** Lead reviews the sheets.

**Order:**
- A1, A2, A3 first.
- Then, in parallel: B1 (when WO-C is not active), B2, E1, F1, D1.
- C1 and C2 when the streak owner is free; C3 after the assets; G1 after WO-E; H1 after V3.
- E2 and I1 last.

## 14. Open decisions for main
1. **Storm translation speed.** Storms drift at 2.3 m/s, so the sequence is driven by the lifecycle (rim sweeps of about 1 min), not by storms travelling across the land. Real storm motion (for example 10-20 m/s for fronts) would change the weather field, Sunshine advection and the fingerprint, and those files belong to the sculpt owner. S3 works unchanged at any speed, because everything goes through `v_r`.
2. **Rain-history ownership** with S2: S3's deterministic history versus the runtime `SurfaceWetness` integrator.
3. **Snow at the gust front** (CoolingOffset into the snow rule): off in v1.

## 15. Implementation notes (A1, A2, A3, B2, E1, F1; 2026-10-25, RTX 4070)

Where the build departs from or sharpens the design above. Files: `scripts/Weather/StormArrival.cs`, `scripts/RingVolumetricCloudLayer.StormArrival.cs`, `RingStormArrivalSettings.cs`, `scripts/ui/GameplayStormArrival*.cs`, `RingLiveTuningDrawer.StormArrival.cs`, `RingWindQuery.cs`, `WindAltitudeProfile.cs`, `StormTurbulence.cs`, `RingPostRainMist.cs`, `RingRainbow.cs`, `scripts/render/RingRainbowComposite.gd` + `ring_rainbow.glsl`.

### 15.1 Arrival maths
- **Per-system drift is an input.** `StormSystemGeometry.DriftMetersPerSecond` (and the offset to the system's own centre) carries everything the maths needs; nothing in `StormArrival.cs`, the rain history or the tests reads the global `WeatherWindMetersPerSecond`. In the adapter, `StormSystemDriftMetersPerSecond` and `StormSystemCenterAtTime` are the only two places that know how a system moves (today both use the global wind); the source-frame cell search in `CollectStormGeometry` must also reach by the largest drift when per-kind travel lands. The tests cover a system travelling at 15 m/s.
- **Rim speed is evaluated at the rim, not at the observer.** `v_r = R_ray (Q d.w / (E^2 n) + n_rim rho + d n_rim/dt)`. The design's `-dn/dt |x-c|/n` is proportional to the observer's own distance for a growing storm (a dying storm 16 km away would read 8 km/s). `NormalizedDistanceRate` (analytic dn/dt) is unchanged and tested against a finite difference.
- **Two rims.** The *rain rim* (solved by 16-step bisection on the mean-field precipitation, with the weather noise fixed at 0.5) drives FatDrop, Core and Trailing. The *gust rim* drives Approach and GustFront: it is 0.85 for a system that never reaches the start threshold and relaxes onto the rain rim as the peak proxy grows from 1x to 3x the threshold, so rain onset cannot make the gust bands jump. `d(rim)/dt` is a +/-2% central difference of the rim with respect to ln E times rho.
- **Rain proxy** is the mean-field precipitation of the system (`P I(n)` including the coverage term), not the bare `P I(n)`, so the rain history and the rain rim describe the same rain.
- **Band changes:** Core ramps `smoothstep(n_rain, n_rain-0.30)` (or from `u` in -0.3..-1.0, whichever is larger); FatDrop fades out with the trailing factor; the trailing factor is `smoothstep(0.5, 7 m/s, -v_r)` and Clearing uses `Core + Trailing` as "inside". These widen the design's edges so no phase gap opens between FatDrop and Core and weights stay within 0.05 per 0.1 s at the lifecycle stage kinks. Approach and GustFront also scale by `smoothstep(0.05, 0.25, Envelope)`, so a system that is still a speck announces nothing.
- **Measured lead.** In the production test a mature MajorFront gives GustFront 150-175 s before the first rain at a point with n = 0.38-0.55 (the design target was 30-120 s). The gap is geometric: a developing storm grows its cloud faster than its rain. The test accepts 30-240 s and prints the number. If a shorter lead is wanted, the knobs are `DryRainRimN` (0.85) and the blend range 1x-3x in `ResolveRims`.

### 15.2 Rain history API (stable; S2 reads this)
- `StormArrivalMath.HistoryLagsSeconds` = {15, 30, 60, 120, 240, 480, 960, 1500}; lag index 0 is "now". `RainHistorySample(TimeSinceRainSeconds, RecentRainAmount, Valid)`.
- **Pure query:** `RingVolumetricCloudLayer.QueryRainHistoryAtTime(position, time)` (uncached, exact) and `StormArrivalMath.RainHistoryProvider` / `QueryRainHistory(position, time)` (thread-local cache, 64 m cells, 1 s lattice). Observer: `ObserverRainHistory`.
- **`TryGetRainHistorySnapshot(out RainHistorySnapshot)`** on the layer returns the observer's per-lag geometry (reused object, copy on the main thread): `Time`, `LagCount` (9), `LagSeconds[]`, `Counts[lag]` and `Systems[lag * 32 + i]`, each a `StormSystemGeometry` (offset observer-minus-centre, normaliser cos/sin, enveloped radii, enveloped density and P, drift, lifecycle code, id). At most 32 systems per lag, nearest rim first. `BuildRainHistorySnapshot(position, time, snapshot)` builds the same exactly for any position and time (parity tests).
- **`StormArrivalMath.RainHistoryAt(snapshot, deltaMeters, tuning)`** is the exact per-texel reduction S2 ports to GLSL: for each lag take the maximum `RainProxy` over the systems with `OffsetMeters + delta`, then `ReduceHistory`. A displaced point shifts every offset by `delta` (observer moved by delta).
- **`TimeSinceRainSeconds` is continuous, not stepped by lag.** `ReduceHistory(proxyByLag, tuning)`: `dry(l) = 1 - smoothstep(start/2, start, proxy(l))`, `C(l)` = running minimum of `dry` from lag 0, `TimeSinceRain` = trapezoid area of `C` over the lags (0 while raining, +infinity when `C` never dropped below 0.98 within the 1500 s horizon, which consumers should read as "more than 1500 s"). It is a continuous function of the nine samples. It is exact for a clean step and otherwise resolved to half the lag spacing (about 240 s between the 480 and 960 s lags); the test feeds a physical 1000 s fade and requires a step of at most 3 s per 1 s.
- `RecentRainAmount` = exp(-lag/600) weighted mean of `Wetness(proxy) = smoothstep(start, start+0.25, proxy)`.
- Hail history: `StormArrivalMath.HailAt(geometry, severity, rainProxy, cooling, hailScale, out size)` is pure over the same `StormSystemGeometry`, and `snapshot.Systems` carries the lifecycle code and id, so S2's optional hail whitening can evaluate it per lag-0 system. Swaths live in lifecycle-code space (start in 0.85-1.7, 0.4-0.8 wide, about 6-12 min of mature stage), move with the system, and are never less than twice as long as wide.
- Observer refresh: one lag every 0.125 s (a full refresh per second); a position jump above 2 km, a clock jump above 5 s or a settings change marks all lags stale and refreshes two per evaluation. Observer state is re-evaluated on a 0.1 s lattice or after 16 m of movement. Measured on the 4070 (headless, 3 systems in reach): 0.09 ms mean and 0.21 ms worst per evaluation, i.e. about 0.015 ms per frame at 60 fps; an uncached `QueryStormArrivalAtTime` costs 0.44 ms (nine 169-cell collections), a collection about 0.05 ms.

### 15.3 Settings
`RingStormArrivalSettings` (runtime, `Current`), `GameplayStormArrivalSettings` (sparse overrides resource), `GameplayStormArrivalDefaults` (`user://gameplay_storm_arrival_defaults.tres`, `StartupDefaults`), drawer group "Storm arrival" in the Wind tab (`RingLiveTuningDrawer.StormArrival.cs`, chained from the Wind partial). Keys: GustFrontStrength 0-2 (1), GustFrontLeadSeconds 20-180 (60), Hail 0-2 (1), PostRainMist 0-2 (1), Rainbows 0-2 (1). The layer loads the preferences on tree entry (`StormArrivalPreferencesPath`). Smoke: `tools/storm_arrival_settings_smoke_test.gd`.

### 15.4 Flight wind query
Pure maths in `WindAltitudeProfile` / `StormTurbulence` / `RingWindQuery.Compose`; the layer installs `RingWindQuery.ColumnProvider`. The profile is continuous at 1 km (the power law blends into the free wind between 0.35 and 1 of the boundary layer). `Sample` costs 1.9 us and a 32-altitude column 0.065 ms once the column is known; building a column for a new (position, time) costs about 1.4 ms (the wind history quadrature). Limitations: the surface wind comes from `QueryWindSnapshotAtTime`, whose cache and gust field are main-thread state in the wind partial, so the world side takes one lock around that call but cannot protect against simultaneous main-thread wind queries (needs a lock in the wind partial); the forest exponent is wired but the biome flag is not (always open terrain); the free-wind drift is the global weather drift.

### 15.5 Post-rain mist
`RingAtmosphereModel.SetWeatherMist(M, now)`: density x(1 + 300 M), height x max(0.15, 1 - 0.7 M), quantised to 1/32, at most one publish per 2 s and at most 30 per 10 minutes (a hard cap, so a fast ramp lags the target rather than exceeding it). At M = 0 the factors are exactly 1 and the haze is bit-identical. `RingPostRainMist` (node, 4 Hz) computes `M = PostRainMist x Clearing x calm x cool`. M = 1 gives about 3.2 km ground visibility.

### 15.6 Rainbow
`RingRainbowMath` (pure) + `RingRainbow` (node, 2 Hz) + `RingRainbowComposite.gd` / `ring_rainbow.glsl` (full-screen additive raster pass, 208-byte payload). The node keeps the effect directly before the first rain streak / rain haze compositor every frame, so the rain owner's `EnsureRainHazeCompositorLast` (which only moves those two) leaves it in place; no rain-owner file is edited. Rainbows 0, no sun, a hidden sun or no rain means no dispatch.

## 16. Implementation notes, second batch (B1, C1, C2, C3, E1; 2026-10-26, RTX 4070)

### 16.1 B1 wind
- `StormArrivalMath.ModifyWind` (pure) is the section 3.1 table, applied once after the history quadrature in `QueryLocalWeatherAtTime` (`ApplyStormArrivalWind`, cached arrival at 32 m / 0.25 s). Gust front strength scales every weight (clamped 0..1 per blend) and 0 skips the modifier entirely; the query signature now includes strength and lead. Only natural weather is modified (manual weather keeps its own wind).
- The gust texture multipliers (strength x, base +) travel through `RingWindField.Sample/SampleAtTime/Publish` as optional per-call factors (defaults 1 and 0, bit-identical) and `RingVolumetricCloudLayer.LastArrivalGustModifiers`, which `RingLocalWeatherEffects` publishes as `ring_wind_gust_strength/base`.
- Measured at a MajorFront observer (headless production test): speed x6.9 with a 143 degree turn within 10 s at the front edge; strength 0 shows x1.00; a calm point is bit-identical at strength 1 and 0. A cold wind query costs about 0.9 ms (arrival cache miss), a hit is the old cost.
- `_windStateLock` (wind partial) guards the query cache and signature, the manual history (read through a snapshot) and the gust atlas seed / Configure / Sample, so `RingWindQuery` columns on the physics thread are safe against main-thread wind queries. It is never held across a weather evaluation. `_flightWindLock` stays (harmless).
- After the front band (Lead seconds) the wind relaxes toward the base wind until the rain arrives (Approach and GustFront are the only arrival wind terms before the rain), so on a fast-developing storm there is a calm gap between the front and the rain. This is the design table, noted because it is audible.

### 16.2 C1 rain hooks and C2 hail (streak files)
- Frame block gains `storm` (fat drop, core, trailing, severity) and `hail` (intensity, size, near/mid hail fall phase); payload 672 B (composite 880 B).
- Fat drops: a share `0.10 x FatDrop` of lattice drops (`keep < share`) stays drawn even at zero local intensity, x1.6 long, x1.8 wide, x1.25 alpha, splash radius and crown x2.2 and always splash (when splashes are on). `RainStreakLattice`/`RainPrecipitationProfile` hold the mirrored constants. Core bias +0.15 x Core x Severity (where it rains) and the Steady cap with length x0.85 for Trailing go through `tier_intensity`.
- Veil gate: `RainHazeOnsetFactor(intensity, heavyCoreWeight, onset)`; the weight is `smoothstep(0.6, 0.7, Core x Severity)`, replacing the kind rule when an arrival state exists and no manual weather is active. The bool overload is unchanged for the old tests.
- Hail: per-drop hash under `0.6 x HailIntensity x smoothstep(0.1, 0.3, local intensity)` makes the drop a pellet: own vertical periods (36 m near, 60 m mid) and one fall speed (30 m/s, the per-drop 25-35 m/s variation of the design needs an unwrapped clock and was not done), 35% of the rain slant, 6-25 mm (size from `HailSize`), min 1.5 px, a 0.35 m motion trail; after landing on the map surface (roofs included, so nothing falls through or under a roof) 1-2 analytic hops of 0.2-0.6 m over 0.3-0.5 s; a white camera-facing impact puff (55% of pellets, 0.28 s). The occlusion map has no material, so the puff appears on all surfaces, not hard ground only. Pellets replace rain streaks for their share of drops.
- `StreakArrivalOverride` (property) and `RING_STORM_ARRIVAL=<phase>:<w>[:<hail>]` force the state for tests and captures.

### 16.3 C3 audio
- Gust roar: four directional voices playing the wind loop pitched to 0.6x (bus Wind), gain `smoothstep(GustFront) x (0.3 + 0.7 lobe toward the storm)`, scaled by min(1, Gust front strength). No separate asset.
- Hail bed: four voices on `hail_loop` (bus RainField, so shelter muffling applies) by sector exposure, or the roof gain under cover. Leaf skitter: `leaf_skitter_loop` (new, generated, see LICENSE.md) x gust x biome leaf share (forest 1, grassland 0.6; sampled at 0.5 Hz).
- One-shot pool kinds: `FatPlip` (the plip set pitched 0.7x, 3/s x FatDrop, open air only, ducked to 0 by the heavy rain tier) and `HailHit` (`shots/hail_hit_*`, 11/s in the open, 9/s x roof gain under a roof, louder with `HailSize`). `ShotKindCount` is 8.
- Rain-tier ducking (the design doc has no explicit table, this is the implementation): heavy tier takes 25% off the wind speakers, hail takes up to 40% off the rain beds and surface field, the gust roar up to 30% off the light bed, and fat plips fade with the heavy tier. The exterior duck metadata includes the storm layers. Assets that were never wired: `gutter_trickle_loop`, `foliage_wind_rustle_loop` (S2).
- Fixture hooks: `SetArrivalOverrideForFixture(gust, fat, hail, angle)` / `ClearArrivalOverrideForFixture()`. `player_count` and `asset_count` keep their meaning (core layers); `storm_player_count` is new, `voice_layout` has gust/hail/skitter before distant.

### 16.4 E1 mist retune
The weather mist now ADDS ground extinction instead of multiplying the user's mist: sigma_M0 += 1e-2 x M (times the Density control) and the mist scale height x max(0.15, 1 - 0.6 M). The first retune (x1801 multiplier) showed nothing in captures because the capture profile's aerosol/mist control is near zero and a multiplier of zero is zero. M = 1 gives about 390 m ground visibility; M = 2 about 195 m; at M = 0 the factors are exactly zero and 1 (bit-identical haze, tested, also with a zero user mist). `RingPostRainMist.Target` scales Clearing by 1.5 (saturating) because Clearing peaks near 0.75, so full strength is reachable. Publish cap unchanged (6 publishes in the capture). Heights are above the datum, so the layer pools in lowlands (a 416 m ridge sits above it).

### 16.5 Hail prevalence (changed A1 numbers)
Production showed hail almost never happening on land (an absolute rain proxy gate of 0.6 and 0-2 swaths per MajorFront). Now: the gate is relative to the system's own peak proxy, severity 0.5 counts as full, and a MajorFront has 1-2 swaths (Exceptional and above unchanged). `StormArrivalCaptureHelper.FindArrivalSequence` finds land observers with the whole gust, fat drop, core and hail sequence.
