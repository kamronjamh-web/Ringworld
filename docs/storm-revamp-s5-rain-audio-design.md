# Storm revamp S5 design: rain sound field (lead, 2026-10-25)

User request: replace the "Minecraft-like" localisable rain sound sources with an enveloping sound. Line refs are main at 3110b7e.

## Why it sounds like point sources
- 8 `AudioStreamPlayer3D` around the player, InverseDistance, 12 m out (`scripts/RingWeatherAmbience.cs:575-595`, `:771-790`).
- 24 more pinned to fixed ground columns, UnitSize 7, MaxDistance 42 (`:271-288`, `:368-403`), never moved while audible (`:372-376`): each swells and fades like a lamp-post as you walk past.
- Shelter: one overhead column, binary 0 / .55-1 / .82 (`:353-366`); no walls, no opening direction.

## Open phase-timing failure: test artifact
`tools/rain_surface_continuity_test.gd:134-140` compares `get_playback_position()` against wall time over >= 0.2 s windows with +/-0.1 s tolerance. The Dummy driver mixes in 4096-frame blocks (92.9 ms at 44.1 kHz; probe: 114/120 steps at exactly 0.0929 s), so playback position is a staircase. The recorded failure (0.093 s over 0.203 s) is one block where two were expected: aliasing. Long-run rate is correct (0.997-1.03); reproduces with curtains off. Fix: detect restarts with a per-voice start counter (WO5).

## Design
**Ring-fixed directional speakers:** 4 sectors (+/-around, +/-across in the ring frame). Each directional layer has one `AudioStreamPlayer3D` per sector under a top-level `Node3D` rig that moves to the listener each frame and takes the local ring basis. Players: `AttenuationModel = Disabled`, `AttenuationFilterDb = 0`, Doppler off, `PanningStrength ~0.6`, radius 8 m. Distance carries no cue, only direction; the engine pans camera-relative for stereo/5.1/7.1. (Rejected: `AudioEffectPanner` needs a bus per sector, stereo only, no front/back; plain non-positional players carry no direction.) Loop offsets decorrelated per (sector, layer) by golden-ratio fractions, pitch 0.97/0.99/1.01/1.03 per sector to avoid comb filtering. Voices start once and keep running while weather is audible; stop after 3 s below -60 dB.

| Layer | Players | Gain |
|---|---|---|
| Universal bed (light/heavy) | 2 non-positional, stereo `rain_light_loop.ogg` / `rain_heavy_loop.ogg` | intensity crossfade (`WeatherAmbienceMix.Targets`) x openness |
| Surface field | 4 sectors x {Soil, Foliage, Hard, Water}; material plays only while weight > .01 | intensity x exposure_s x material_s,m, equal-power normalised |
| Roof | 2 voices 3 m up at +/-across, PanningStrength .4 (`rain_surface_hard`) | shelter class |
| Wind | 4 sectors (mono derivative) | W(1-a) + a*max(0, cos(theta_s - theta_upwind)), a = .5*wind/16; seeded gust LFO 0.08-0.25 Hz |
| Distant storm | 4 sectors (mono derivative) | 16 probes (`:610-663`) folded per sector as max(strength*cos lobe), tau 3 s; total stays `distant*(1-rainHeavy)` |
| Near one-shots | pool of 6, InverseDistance, MaxDistance 20 | Poisson scheduler |

**Exposure** (`IRainExposureProvider`): 24 columns (4 sectors x 2 angles +/-22 deg x radii 3/7/14 m) + 1 overhead, round-robin 3 per 0.1 s (sweep 0.8 s), inside the test caps (<= 3 surface, <= 48 density queries per frame, `rain_surface_continuity_test.gd:40`). Each column: landing surface/material from `TryResolveRainColumn` (`:460-519`); Foliage = Soil under Forest/Grassland biome (from the captured `TerrainSample`, `:512`); head-height solid test for walls. Sector exposure = columns landing near listener height x transmission (x0.15 behind a nearer wall) x 1/(1+r/6). Before S1: terrain provider. After S1: `OcclusionMapRainExposureProvider` (map gives landed/covered incl. POIs; material from terrain, or Hard for POI/building occluders), chosen when the map is valid.

**Shelter classes** (hysteresis +/-0.1, 0.4 s dwell; sides open = mean side exposure >= .35):

| Class | When | Bed gain / LPF | Roof | Thunder dB / cutoff | Wind |
|---|---|---|---|---|---|
| Open | nothing overhead | 1 / 18 kHz | 0 | 0 / 18 kHz | 1 |
| Roof (porch) | constructed/POI cover, sides open | .55 / 6 kHz | 1 | -2 / 10 kHz | .8 |
| Indoors | constructed cover, sides closed | .15 / 1.4 kHz | .7 | -8 / 1.5 kHz | .25 |
| Cave | natural cover | .2 / 1.1 kHz, drips | 0 | -10 / 900 Hz | .3 |
| Deep cave | > 24 m cover or `UndergroundSuspended` | 0 | 0 | -16 / 600 Hz | 0 |

Surface field gets a milder LPF than the bed (direct path through an opening stays clear).

**Smoothing:** gains one-pole tau 0.9 s + hard cap 0.8/s; intensity tau 1.5 s; cutoffs slewed in log2 at <= 2.5 oct/s; one column update moves a sector by <= 1/6; teleport (> 96 m) re-seeds targets with a 0.3 s fade.

**Near one-shots:** seeded Poisson ~intensity x 4/s, 1.5-8 m away in exposed columns weighted by material; roof plinks 2/s 2-4 m overhead under a roof; aftermath drips decay tau 90 s near foliage/roof edges; caps 6 voices, 60 ms spacing, no immediate repeat, pitch +/-8%, gain +/-3 dB; silent if the library is empty.

**Kept interfaces:** `IsRainSheltered` (now class != Open with hysteresis; consumer `RingLocalWeatherEffects.RainFog.cs:92`), duck metadata (`:198-199`), `UndergroundBlend` / `UndergroundSuspended` (`UndergroundParticipants.cs:132`).

## Missing assets (resolved 2026-10-25: generated procedurally, see below)
Derive locally (ffmpeg channel average, documented like `resources/audio/weather/LICENSE.md:91-107`): `wind_spatial_loop.ogg`, `storm_distant_spatial_loop.ogg`.
Gaps (mono 44.1 kHz seamless >= 20 s loops unless noted): rain on foliage canopy; rain on sheet metal; interior roof drum (fake first with LPF on the hard loop); hail loop + 8 one-shots; one-shot sets (water plips x10, leaf drips x8, hard/metal ticks x8, roof plinks x8, cave drips into pool x8, eaves dribble x4); gutter/downpipe trickle loop; foliage wind-rustle loop. Source from Freesound/OpenGameArt CC0; record SHA-256 and processing per the LICENSE convention.

## Menu and persistence
New trio after the RainHaze pattern: `scripts/ui/GameplayWeatherAudioSettings.cs`, `GameplayWeatherAudioDefaults.cs` (`user://gameplay_weather_audio_defaults.tres`), `RingLiveTuningDrawer.WeatherAudio.cs`. Move existing rows out of `RingLiveTuningDrawer.cs:382-396`, keeping labels (tests find controls by text: `thunder_behavior_smoke_test.gd:151`, `weather_ambience_runtime_smoke_test.gd:211-224`).

| Field | Range | Default |
|---|---|---|
| Thunder | bool | true |
| Thunder volume | -48..12 | -8 |
| Weather ambience | bool | true |
| Rain volume | -48..6 | -10 |
| Wind volume | -48..6 | -13 |
| Distant storm volume | -48..6 | -18 |
| Spatial width (new) | .2..1 | .6 |
| Near detail volume (new) | -24..6 | 0 |

No debug or sector toggles in the menu.

## Work orders
1. **WO1 pure core** `scripts/Weather/RainSoundField.cs` (sector aggregation, material normalisation, hysteresis classifier, slew helpers, directional lobe, Poisson scheduler); extend `WeatherAmbienceDiagnosticRunner.cs`. Accept: equal-power within +/-0.5 dB across materials; no flapping at threshold; slew caps hold for random dt; lobe peaks upwind; `weather_ambience_core_smoke_test.gd` passes.
2. **WO2 exposure provider:** extract `:460-519` into `scripts/Weather/TerrainRainExposureProvider.cs` behind `IRainExposureProvider` with foliage + wall test; `GetRainSurfaceDiagnostic` (`:521`) output unchanged. Accept: same results at test columns; budgets unchanged.
3. **WO3 node rewrite** of `RingWeatherAmbience.cs`: delete old 3D groups and detail pool (`:28-39`, `:61-64`, `:271-451`, `:575-595`, `:684-746`, `:771-790`, `:874-929`, `:961-965`); add rig, bed, field, roof, wind, distant layers. `default_bus_layout.tres`: `RainBed`, `RainField` (send to `Rain`), LPF on `Wind`. `VoxelRingIntegrationTest.tscn:66-67`: bed on stereo originals; add mono derivatives. Diagnostics: `sector_exposure`, `sector_material`, `shelter_class`, `voice_start_count`, `max_gain_step`, `cutoff_hz`. Thunder propagation and probe budget unchanged.
4. **WO4 thunder hook:** `RingWeatherAudio.cs` gets `ShelterMuffleAmount`, `ShelterCutoffHz`; at `:200-202` muffle = max(UndergroundBlend, ShelterMuffleAmount), lower cutoff wins; already applied per frame (`:133`). Ambience pushes values via a `WeatherAudioPath` export.
5. **WO5 tests:** replace `rain_spatial_audio_smoke_test.gd`, `rain_surface_behavior_test.gd`, `rain_surface_continuity_test.gd`, `rain_surface_audio_smoke_test.gd` with `rain_sound_field_behavior_test.gd` (Open/porch/room/cave on constructed geometry; exposure toward doorway/cave mouth; shoreline sector materials) and `rain_sound_field_continuity_test.gd` (walk 4 m/s, run 10 m/s, reverse, stop across soil -> water and under a roof; every 50 ms PCM window non-silent and finite; `voice_start_count` constant in motion; per-frame gain step <= 0.8*dt + eps; cutoff slew <= 2.5 oct/s). Update player counts in `weather_ambience_runtime_smoke_test.gd`. Headless `--audio-driver Dummy`, Normal and Certified.
6. **WO6 persistence:** the trio + `WeatherAudioSettingsSmoke.cs` (the skill's 10 checks); add defaults to `scripts/Tools/MinimapWindContractTest.cs:35` (InstallIsolatedPreferences). Live apply and persist across relaunch.
7. **WO7 one-shot pool** (gated on assets). Accept: <= 6 voices; rate tracks intensity; empty library silent.
8. **WO8 S1 adapter** (after S1): `OcclusionMapRainExposureProvider`; POI roofs give the Roof class; A/B vs terrain provider on the same constructed roof gives the same class.

## Listening check (headphones then speakers, Certified, natural storm)
1. Open ground, turn slowly: all around, no speaker you can point at.
2. Walk/run 30 s along a shoreline: water fades in on the water side, no swells or pulses.
3. Under a porch roof: patter overhead, open side stays bright.
4. Enclosed room: muffled, doorway audible in its direction.
5. Cave mouth: rain from the mouth, fades with depth; thunder muffled.
6. Strong wind: louder upwind; distant storm from the storm's direction.
7. After rain: drips taper over 1-2 min.
No audible repetition within 30 s.

## Implementation status (WO1-WO7 done)
Files: `scripts/Weather/RainSoundField.cs` (pure core), `TerrainRainExposureProvider.cs`, `FixtureRainExposureProvider.cs` (test fixture), `scripts/RingWeatherAmbience.cs` (rewrite), `RingWeatherAudio.cs` (shelter hook), `scripts/ui/GameplayWeatherAudio{Settings,Defaults}.cs` + `RingLiveTuningDrawer.WeatherAudio.cs`, bus layout (`RainBed`, `RainField`, LPF on `Wind`), `wind_spatial_loop.ogg` and `storm_distant_spatial_loop.ogg` (derived locally, documented in `resources/audio/weather/LICENSE.md`). Tests: `rain_sound_field_behavior_test`, `rain_sound_field_continuity_test`, `weather_audio_settings_smoke`, `weather_ambience_core_smoke_test`, `weather_ambience_runtime_smoke_test`. The four old rain tests are removed.

Deviations from the design:
- **No constructed cover in this world.** The default scene has `DormantEditableBackend = true`, so voxel roofs and walls cannot be placed (this is why `rain_surface_behavior_test` never passed here). The terrain provider therefore only produces Open and Cave/DeepCave (smooth terrain overhangs). Roof and Indoors need POI or building occluders, i.e. the WO8 occlusion-map provider. Porch, room and doorway cases run on `FixtureRainExposureProvider` through `RingWeatherAmbience.ExposureProvider` (the `IRainExposureProvider` seam); everything else in those tests is real.
- The rig node has an identity basis; the ring axes are applied to each speaker's offset (a handed basis would risk a reflected transform).
- Probe cadence: one probe per 25 ms (never more than one terrain column per frame): overhead every 4th slot (10 Hz), columns 30/s, sweep 0.8 s. Seeding after rain starts, a teleport or a terrain edit probes one column per frame until the field is complete.
- Walls: head-height solid tests along the ray (1.25, 2.25, 3.25 m, then 50% and 80% for far columns).
- `SlewGain` adds a 0.15/s linear floor so one-pole tails reach true silence. A voice below -100 dB stops at once; one between -100 and -60 dB stops after 3 s.
- Foliage has no recording yet; foliage columns fold into the soil voices (an optional `RainFoliageStream` export takes over when set).
- Thunder shelter muffle: `ShelterMuffleAmount` 0..1 maps to 0..16 dB (the underground blend keeps 12 dB), so the class table's -2/-8/-10/-16 dB hold exactly.
- Wind's bus LPF follows the bed cutoff of the class; the distant storm bed is not filtered by shelter (the table lists no filter for it).
- When it is dry or snowing the field does no terrain queries and the class relaxes to Open, so thunder is only muffled while it rains.
- `Near detail volume` is persisted and applied to the node but has no consumer until WO7.
- With a three-wide doorway the mean side exposure is about 0.36, inside the hysteresis band, so a room entered sealed stays Indoors (a porch stays Roof).

### WO7 and assets (2026-10-25)
- **Assets:** every gap above is synthesised by `tools/media/weather_sfx_gen.py`. It is seeded and reproducible, and the provenance and SHA-256 are in `resources/audio/weather/LICENSE.md`. No downloads were needed.
  - Thunder near/far variants process the CC0 thunder clip and layer a synthesised crack or sub-rumble.
  - `hail_loop`, `shots/hail_hit_*`, `gutter_trickle_loop` and `foliage_wind_rustle_loop` are made ahead of S2/S3 and are not wired yet.
- **Surface field:** five materials, with `Metal = 4`.
  - The occlusion-map provider reports POI and building occluders as Metal. Voxel Concrete/Glass stay Hard.
  - Foliage plays `rain_surface_foliage`, Metal plays `rain_surface_metal`, and the roof voices play `rain_roof_drum` (falling back to hard).
  - Diagnostics publish `voice_layout` instead of fixed indices.
- **One-shot pool:** 6 world-fixed `AudioStreamPlayer3D` on `RainField` (InverseDistance, unit 3 m, max 20 m), driven by one `RainShotScheduler` (60 ms spacing, voice cap).
  - Rates come from `RainSoundField.ShotRates`:
    - plips, leaf drips and ticks: 4/s x intensity x exposed material share;
    - roof plinks: 2/s x intensity x roof gain under Roof/Indoors;
    - eaves dribble: 0.35/s x wetness under a roof;
    - cave drips: 0.6/s in Cave;
    - leaf aftermath: 1.5/s x max(0, wetness - intensity) x foliage share.
  - Wetness rises with tau 10 s and falls with tau 90 s.
  - Shot level = rain volume + `Near detail volume` + a per-kind level + jitter of +/-3 dB; pitch +/-8%. Shots never count toward `voice_start_count` or the gain-step checks.
- **Thunder:** `thunder_near_*` for strikes under 1.5 km and `thunder_far_*` over 8 km; the original three stay as the middle pool.
- **Post-rain probing:** exposure and shelter probing keep running while the aftermath wetness is above 0.05 (`RainSoundField.ShouldProbeExposure`), so eave drips under a roof continue after the rain stops. Fully dry (wetness <= 0.05), snowing or underground still means no terrain queries and the class relaxes to Open.
