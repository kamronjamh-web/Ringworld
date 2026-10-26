# Storm revamp S4 design (lead, 2026-10-25)

Work orders for `docs/storm-revamp-plan.md` step S4. Line refs are main at 3110b7e.

## Findings
1. **Anvils** (`SunshineCloudsCompute.glsl:347-379`, duplicated in `SunshineCloudShadowCompute.glsl:205-227`): `mod(warped, cellWidth)` (:356-361) tiles identical pillar+anvil cells every ~28 km (pillar 4.5 km x 2.7), a lattice of identical mushrooms. Anvil is a disc centred on the core (:372), no shear, no upwind lip. Slab is `AnvilStartFraction` 0.70 to 0.955 with a 0.075 ramp at constant density 0.94: flat puck top and bottom, every cell the same height. Pillar top (0.91) sits inside the slab, so no overshooting top. Storm erosion is only multiplicative `stormDensity *= mix(0.62,1,medium.r)` (:556), so edges never fray. Lifecycle only scales the system ellipse (`TryBuildWeatherSystem` :4252-4262); pillar/anvil sizes are global, so newborn and dying storms show full anvils.
2. **Free GPU slots:** per-system `d.w` (upload offset 60, `CameraIndependentClouds.cs:350-375`) and `header1.w` are unread. Suppressor flag bit 8 is reserved (`RingVolumetricCloudLayer.cs:3804`).
3. **Climate spec C3 misses the GPU path:** it patches only `QueryRawWeatherAtTime` (:3314-3319); camera-independent Sunshine reads `CollectRenderWeatherSystems` (:3852). The far atlas is 1024x128 (~30 km/texel), so the spec's splat post-pass is unnecessary.
4. **Far-arch dimming:** the remote shell uses the same params as local air, whose `E_sun` is `ringworld_haze_sun_irradiance` (`ring_atmosphere.gdshaderinc:519,554`), already multiplied by the camera's air sun visibility (`day_side_atmosphere.gd:935,955`).
5. **Reusable hooks:** the lightning scanner's ring-wide Major/Exceptional cache (`.Lightning.cs:160-194`) can feed far impostors; far-layer pattern `EmitterGridBeamLayer.BindFarLayer` (:590) + `RingScaledFarViewport.AddScaledWorldChild`/`ScaledPoint` (:3583-3594); POI bounds `RingLargePoiShadowExtents` (:21).

## Order
WO-0 -> WO-A -> WO-B -> WO-C -> WO-E -> WO-D -> WO-F -> WO-G -> WO-H. A, B, C share files (serial); 0 and E can run in parallel.

## WO-0: far arch dimmed by a storm at the camera
- `day_side_atmosphere.gd` (:1019-1022): also publish `ringworld_haze_sun_irradiance_remote = _haze_sun_irradiance()` (undimmed), same 0.5% change threshold. Declare in `project.godot` (:165) and `ring_atmosphere.gdshaderinc:72`. In `ring_haze_sample` (:554) build `p_remote = p` with `p_remote.E_sun = remote * ring_haze_sun_tint` and pass it as the remote params to `ring_haze_eval_target_split`.
- Keep near-air extinction (a local storm still hides the arch through local air), sky and Sunshine profile paths unchanged.
- Accept: new contract `remote_shell_ignores_camera_visibility` in `haze_consistency_gpu_test --model=physical` (visibility 0.3, storm 0.8: remote terms match clear within 1e-5, near terms still scale); existing 15 contracts pass; `*_night_arch` capture with a manual storm at the camera brightens the arch.

## WO-A: cumulonimbus anvil sculpt (top priority)
- **A1 refactor:** move the storm shape function into `addons/SunshineClouds2/RingStormShapes.glslinc`, included by both computes. Byte-identical output (`compare` SSIM 1.000 at a fixed pose). Reimport Sunshine.
- **A2 shape model** (analytic; per sculpt system evaluate own cell plus upwind neighbour):
  - Cells: hash per cell (`ring_ws_hash01`) for +/-0.3-cell jitter, 0.6-1.3 size, ~70% existence, top `htop` 0.86-0.97, maturity offset +/-0.3. Kills the lattice.
  - Shear frame: downwind axis from `WeatherWindMetersPerSecond`, uploaded as an angle in `header1.w`, rotated into the system frame.
  - Pillar radius grows ~35% with height and tilts downwind.
  - Overshooting dome: hemisphere 0.7x pillar radius above the pillar, up to `htop + 0.05`.
  - Anvil footprint teardrop: downwind radius `Ra*(1+1.5*shear)*growth`, upwind lip `0.35*Ra`, sides widening downwind.
  - Anvil vertical: flat top at `htop`; underside rises with distance from core, 0.22 band thick near the updraft to 0.03 at the edge.
  - Mammatus: lobes `1-length(fract(p/Lm)-.5)*2.2` lower the underside ~0.03 band where anvil distance is 0.2-0.7, scaled by maturity and a setting.
  - Edge feathering: density 0.95 core to 0.35 edge; shape writes global `g_ws_storm_edge`; :556 becomes `stormDensity *= mix(mix(.62,1,m.r), smoothstep(.75e, .75e+.25, m.r), e)`.
- **A3 lifecycle:** add `StageProgress` to `WeatherLifecycle` (:3687). `BuildNaturalRenderSystem` (:3938) uploads L in `d.w`: birth 0-0.3, develop 0.3-1, mature 1-2, dissipate 2-3; 1.5 when lifecycles are off, for manual systems and the legacy path. Shader: pillar grows over develop; anvil appears at L > 0.6 and spreads downwind through mature; mammatus at L > 1.4; dissipate collapses the pillar, drifts the anvil downwind by `(L-2)*0.6*Ra`, thins it and raises its underside.
- Settings: "Anvil shear" and "Mammatus" (0..1) beside the pillar/anvil controls (`GameplayCloudDefaults.cs:48-98`; live-tuning-setting skill). Old-shape A/B in debug tooling only.
- Don't change: record layout beyond `d.w`/`header1.w`, non-storm profiles, macro-map math.
- Accept: headless test (L continuous, non-decreasing across stages, deterministic, 1.5 when lifecycles off); both computes compile and shadow/main shapes match; A-B-A on A1000 High in a storm: Sunshine GPU <= +0.3 ms; `flicker` on a 10 s orbit has no new spikes.
- Captures (manual preview storm, clocks paused, sun ~35 deg, Movie Maker 30 fps): side views at 30 km and 60 km from 1.5 km altitude across the shear (wedge downwind >= 2x upwind lip, dome above flat top); downwind looking upwind; from beneath 2 km under the downwind anvil pitched up 60 deg (mammatus); plan view from 25 km (teardrops, varied cells, no lattice); lifecycle strip at L = 0.5/1.0/1.5/2.0/2.5 via debug L override. Contact sheet per set; `compare` with the old shape outside the storm (SSIM ~0.997).

## WO-B: superstorm class
- `WeatherSystemKind.Superstorm = 5`. In `TryBuildWeatherSystem` (:4158-4164), an ExceptionalFront roll with `Hash(salt 120) < SuperstormChance` (default 0.35) becomes Superstorm: radius 100 km to max, aspect 1.0-1.3, density/precip 0.95-1, envelope `smoothstep(0, 0.35, env)`.
- `IsFront` (>= MajorFront) and `IsSevere` (>= ExceptionalFront) helpers; audit every `ExceptionalFront` use (`RingVolumetricCloudLayer.cs` 15, `.Lightning.cs` 3, `.Wind.cs:119`, `RingSunshineCloudBridge.cs` 3, `RingLocalWeatherEffects.cs` 1, `RingDistantRainCurtains.cs:197-206` 1).
- Profile `Supercell` (11), always for this kind in `ResolveSystemFormationProfile`, sculpted, maps to 3 in `ToShaderProfile`; in `ring_ws_shader_profile` and `RingStormShapes.glslinc` work in polar coords rotated by a spin phase in `d.w` (~1 turn / 20 min). Mesocyclone core (r < 0.16) with barber-pole striations, wall cloud below the band floor, two log-spiral rain bands, full WO-A anvil with dome and mammatus. Lower band floor by `WallCloudDropMeters` (350 m) and widen `envelopeFloor` (`CameraIndependentClouds.cs:127`).
- Setting "Superstorm odds". Accept: odds 0 keeps `natural_weather_v5_fingerprint_golden.json` bit-identical; new golden at default; lightning, wind StormFactor and curtains treat Superstorm >= ExceptionalFront; plan and 60 km side captures.

## WO-C: climate anomalies in the weather field
Spec Part C4/C3 (`docs/emitter-grid-climate-machines-spec.md:545-610`) with: parked storms as Superstorm, id -20000-i, centre fixed in canonical space converted at query time (`c - wind*t`), merged into `nearbySystems` before `EvaluateMacroWeather`, fixed L 1.5 plus spec breathing; added as natural non-static records in `CollectRenderWeatherSystems`. Droughts uploaded with suppressor bit 8; `ring_ws_macro_point` (glslinc :115-180) skips them in the natural loop then applies `cov *= 1-0.85w`, `precip *= 1-w`, `influence *= 1-w`, and the sculpt result `*= (1-w)`. No far-atlas splat. Lightning: parked-storm loop at `.Lightning.cs:157`; skip natural strikes in `TryCreateNaturalEvent` (:201) when drought weight > 0.5. Add `ClimateAnomalyRevision` to the cache signature in `Wind.cs:33`. Accept: spec v5 bit-identity gate; CPU vs GPU macro parity at 64 points in/around each anomaly; deterministic across rewind.

## WO-D: far-arch storm towers and rain shafts
`CollectRingStormSystems` (lightning cache + parked storms). New `scripts/Clouds/RingFarStormImpostorLayer.cs` (bound like `BindFarLayer`): MultiMesh of up-axis billboards, 1-6 towers per system from the id hash, advected, refreshed every 2 s. `materials/ring_far_storm_impostor.gdshader`: WO-A side silhouette frayed with fbm, wrap lighting with remote-day factor, regular haze via `ring_haze_sample`, darker rain shaft base to ground, up to 8 `ActiveLightningEvents` as internal glow; weight 1 - near ownership; after far cloud shell, no depth write. Near tier `scripts/RingFarRainShafts.cs` (8-72 km shafts, reuses `ring_distant_rain_curtain.gdshader`). <= 256 instances, cull < 2 px, opposite arch Exceptional+ only. Accept: far viewport <= 0.25 ms; night arch capture shows towers with flashes; no pop in the handoff band (`flicker`).

## WO-E: red sprites and blue jets
`scripts/Lightning/RingUpperAtmosphereLightningLayer.cs` in the far viewport; events derived from `RingLightningEvent` with chance by kind (Major 0.08, Exceptional 0.2, Superstorm 0.45) x frequency setting, `Unit(hash(EventId, salt))`. Sprite 70% (red, ~55 km above cloud top, 0.12-0.3 s), blue jet 25%, gigantic jet 5%. Additive unshaded before far clouds; intensity x (1 - 0.97*remote day) (`ring_haze_shared:291`); min angular size 0.5 deg. Setting "Upper-atmosphere lightning" (0..2), scaled by reduced-flash. Accept: headless determinism/rates/rewind; night across-arch capture, `flicker` shows the spikes.

## WO-F: tornado and waterspout funnels
Pure `RingFunnelSchedule(seed, systemId, slot, t)`: 900 s slots, 120-420 s active; Superstorm always eligible at the hook (r 0.12, rotating with spin), Exceptional p 0.3, Major p 0.05; meandering path; rope-out over the last 20%. `RingStormFunnelLayer.cs` + `ring_storm_funnel.gdshader`: tapered bent twisted tube ~1.5k tris, spiral noise, denser silhouette, regular haze; GPUParticles3D debris (2k) brown over land, white spray over water (`HasSurfaceWater`); within 40 km. Setting "Funnel frequency". Accept: determinism/rewind test; captures at 5 and 20 km; <= 0.2 ms.

## WO-G: haboob dust walls (after spec C4 dust haze)
Pure `HaboobFront(seed, anomaly, t)`: 2400 s cycle, 50% chance, 80-120 km arc at 25 m/s downwind across the drought ellipse. 32-quad billowing ribbon 1.5-2.5 km tall, haze-lit, main and far viewport; dust haze ~3x while passing the camera. Setting "Haboob frequency". Accept: determinism test; captures at 30 km and during passage.

## WO-H: St. Elmo's fire
Trigger: precip >= 0.6, kind >= Major, strike within 8 km in the last 20 s (small strike history). Anchors: POIs within 2 km from `RingLargePoiShadowExtents`, 36 downward raycasts along local up on mount, keep the 6 highest. Additive blue-violet flickering corona particles; no setting, automatic for every POI. Accept: POI capture in a storm preview; zero cost without a storm.

## Risks (A1000)
WO-A/B sculpt cost (0.3 ms hard gate), WO-D impostor overdraw near the horizon, WO-B envelope-floor change lowers the ray clip, Sunshine GLSL changes need reimport + fresh shader cache. Full GPU gates (haze consistency, cloud shadows, storm captures) once per batch.
