# Shared wind and radar implementation checklist

Requested 2026-10-05. Target appearance: dramatic grass surges, visible tree bending, aggressive storm waves, and gust patches that travel coherently across the visible landscape. The primary agent reviews all delegated edits and runs functional/rendered checks. The user will review FPS because another program is running; no performance benchmarks are part of this work.

## Wind source and lifecycle

- [x] Separate weather-system transport wind from the resolved local surface wind so storm changes cannot teleport weather systems.
- [x] Derive stronger local wind from natural and spawned storm influence, with bounded direction variation, smooth buildup, and decay using the authoritative simulation clock.
- [x] Give manual weather explicit inherit-versus-calm wind semantics; spawned Sunshine storms inherit wind by default.
- [x] Separate the alternative cloud front's travel speed from its surface wind speed.
- [x] Publish a typed mean/instant wind snapshot usable by vegetation, rain, water, and the map.
- [x] Preserve pause, rewinds, long-running clocks, disabled weather, finite-value guards, and scene-owner cleanup.

## Shared moving gusts and vegetation

- [x] Share one seeded, bounded gust field and its clock/movement between grass and trees.
- [x] Preserve canonical position, chunk adjacency, ring wrapping, and floating-origin continuity without per-blade CPU weather queries.
- [x] Retain existing grass artistic controls and saved overrides as grass response settings.
- [x] Add dramatic but bounded tree response, including slower trunk/canopy response and recovery, with fixed roots.
- [x] Apply the shared field to production imported trees and their packed LODs; keep intentional legacy static billboards supported.
- [x] Expand culling/shadow bounds to cover the configured maximum deformation.

## Water and precipitation

- [x] Feed smoothed mean storm wind to the existing FFT spectrum update path, with bounded regeneration rather than updates for every fast gust.
- [x] Use fast shared gusts for bounded chop/detail/whitecap/foam response, preserving shoreline pinning and swimming-query agreement.
- [x] Connect analytic ocean, lake, river, and distant/fallback water to the shared wind direction and simulation clock while preserving water-kind scales and existing artistic controls.
- [x] Keep precipitation direction/strength consistent with the resolved local wind.

## Live tuning and persistence

- [x] Add relevant wind/storm/gust/tree/water response controls to the existing grave-key drawer.
- [x] Use validated persisted settings, debounced saves, explicit false/zero values, reset, and a Dramatic storm preset.
- [x] Preserve existing grass, cloud, water, and vegetation preferences and use isolated paths in tests.
- [x] Provide concise diagnostics for resolved wind, storm influence, gust state, and water response.

## Radar and map

- [x] Show the existing map by default with only the map presentation visible.
- [x] Make M expand/collapse the additional information without hiding or rebuilding the map.
- [x] Add a small live compass pointing toward the wind's destination relative to character facing, in both map orientations.
- [x] Hide or clearly indicate the arrow for calm/unavailable wind and keep its updates lightweight.
- [x] Preserve zoom, rotation, player/trackable markers, and existing capture behavior.

## Primary review and verification

- [x] Personally review every production and test edit, including existing dirty files, without reverting unrelated work.
- [x] Build the .NET project and complete whitespace checks.
- [x] Verify deterministic storm strength/direction, spatial gust agreement, manual inheritance/calm, pause/rewind, and lifecycle cleanup.
- [x] Run relevant rendered vegetation/water checks for roots, deformation, shadows, bounds, and seam/origin continuity.
- [x] Verify water update discipline, shoreline/swimming behavior, and fallback clocks/direction.
- [x] Verify live control binding, saved override round trips, reset/preset behavior, and popup isolation.
- [x] Verify default map visibility, M details toggle, and wind-arrow orientation/calm handling.
- [x] Inspect representative calm, gust, and storm captures and record evidence/limitations here.

## Implementation record

### Follow-up: moving Perlin gusts, consolidated controls and rain haze

- [x] Replace the harmonic gust atlas with seeded periodic Perlin noise, retaining shared CPU/GPU sampling, canonical wrapping and downwind advection.
- [x] Make the passing local field drive grass/tree bending; prevent global oscillation and early storm saturation from flattening spatial variation.
- [x] Preserve stiff wood, fixed roots/leaf attachments, corrected normals, bounded geometry, simulation pause and rebasing.
- [x] Prove simultaneous nearby variation and downstream time-lag propagation with the production field and real vegetation shaders.
- [x] Put grass/tree wind controls in Wind, remove overlapping grass gust widgets, and preserve saved values and scoped reset/save behavior.
- [x] Replace rain drops with soft gray, depth-aware haze that thickens with rainfall; keep snowfall, audio, wetness and lightning connected to weather.
- [x] Expose and persist rain-haze settings in the grave-key menu without changing unrelated atmosphere or cloud settings.
- [x] Personally review edits, build and run relevant functional/render checks; inspect captures and leave FPS review to the user.

The gust atlas now uses seeded periodic gradient Perlin noise with three octaves, shared by CPU sampling and the actual GPU texture. At the default 600 m pattern span, its coarsest features are approximately 37.5 m across. Advection already moved downwind correctly; that direction and the simulation clock remain authoritative. Grass now takes its main bend from the passing local field. Trees use a smooth bounded response that retains spatial differences during strong wind, with root/island variation in secondary leaf flutter. Wood stiffness and leaf attachment limits remain intact.

All vegetation wind widgets are in Wind. Grass response retains its original saved keys and owner; the redundant standalone gust multiplier/base-breeze widgets were removed. Grass reset preserves wind overrides, while Wind reset preserves grass density, appearance and other settings. The primary full-scene check caught and resolved a redundant profile reset that discarded retained live values.

Liquid rain now renders as a soft gray depth-aware veil on the main camera, with rainfall-driven density, shared wind variation and a night-adjusted tint. The existing precipitation pool emits snow only. Shelter and underwater state suppress the veil without changing authoritative precipitation, wetness, audio or lightning inputs. Unchanged uniforms are cached, replacement hides the previous veil immediately, and same-path replacement retains unsaved tuning. Distant rain curtains retain broad wisps with their fine streak term removed and safely release/rebind a disposed local effects owner.

The primary agent personally reviewed all follow-up edits and ran these checks with Godot 4.7.2, using Forward+ for rendered evidence:

| Follow-up check | Primary result |
| --- | --- |
| .NET build and whitespace | Pass: zero build warnings/errors; no whitespace errors |
| Perlin core | Pass: same-seed byte determinism, different seeds, nearby spatial variation, downwind travel, pause/rewind, ring seam and long clocks |
| Production atlas integration | Pass: CPU/GPU maximum multiplier error 0.000993252 through rebasing, rotation/nonuniform scale, observer anchors and seam; downwind travel error 0, opposite-direction error 0.324371 |
| Simultaneous vegetation | Pass: 33 identical plants at nearby roots, actual production atlas, differing local bend, downstream lag and frozen clock; imported LOD0–3, packed cards, procedural parts and near grass |
| Tree/grass regression | Pass: root/attachment pinning, independent wood/leaf strengths, lever bounds, corrected normals, live GPU publication, disabled state and owner lifecycle |
| Tree shadows and water | Pass: 81 moving shadow pixels on both voxel/proxy receivers; ocean/lake/river direction, pause, long clock and shared gust detail |
| Scoped controls and persistence | Pass: unique Wind widgets, false/zero preservation, original-owner saves, scoped reset/preset, validation, round trips, save failure/retry and actual gameplay binding |
| Rain state | Pass: no visible liquid-rain particles, snow transitions, unchanged intensity/profile semantics, wetness, enabled/finite/shelter/underwater handling, cached uniforms and owner cleanup/replacement |
| Rain rendering | Pass: haze thickens at 15%, 50% and 100% rain; nearby opaque pixels remain exactly unchanged; distant/sky haze, pause, rebase, night, disabled/sheltered/underwater suppression |
| Grave menu | Pass: real input, folding, wheel guards, six Wind sections, Clouds/Rain haze and existing tabs |

Follow-up logs under `artifacts/shared-wind/`: `perlin-core.log`, `perlin-integration.log`, `perlin-vegetation.log`, `perlin-live-publication.log`, `perlin-shadows.log`, `perlin-water.log`, `consolidated-settings.log`, `consolidated-grass-menu.log`, `consolidated-grave-menu.log`, `rain-haze-state.log` and `rain-haze-gpu.log`. The primary inspected simultaneous vegetation, near-tree and light/heavy/night haze captures. No FPS benchmark was run. Existing optional native-mesher, certificate-store, shader-cache and scene-shutdown diagnostics remain as recorded below.

### Follow-up: stiff wood, independent leaf flex and facing-relative compass

- [x] Classify bark and foliage independently in production near-tree materials; retain a conservative crown approximation for combined distant cards.
- [x] Keep leaf attachment points on the shared structural bend, and limit extra leaf flex by its own attachment lever and the existing tree envelope.
- [x] Expose and persist independent trunk bend and canopy flex strengths, including explicit zero, reset and preset behavior.
- [x] Replace the wind line with a compact shaded 3D compass pointer that turns relative to character facing, independently of map orientation.
- [x] Personally review changes and verify production meshes, attachment/root pinning, normals/shadows, controls, heading wrap, calm and disabled behavior.
- [x] Inspect new tree and compass captures; leave FPS review to the user.

Near imported LOD0–2 and procedural canopies now use separate wood/foliage response. Leaf groups have fixed prototype attachment points, and their extra flex vanishes at those points. Wood strength scales the saturated storm response, so increasing storm speed cannot defeat the stiffness control. Geometry remains instanced; no leaf nodes or per-frame CPU attachment calculations were added. The compass rotates and shades 3D needle vertices inside the existing UI, without an additional rendering viewport. It uses character yaw in both map modes; pitch and camera roll do not rotate it.

The primary agent personally reviewed the follow-up edits and ran these checks with Godot 4.7.2 and Forward+ where rendering was required:

| Follow-up check | Primary result |
| --- | --- |
| .NET build and whitespace | Pass: zero build warnings/errors; no whitespace errors |
| Tree controls and persistence | Pass: old resources without the new keys, defaults, explicit zero, validation, round trip, live slider/entry binding, reset and preset |
| Production attachment metadata | Pass: all 30 variants and LOD0–3, packed classification/channel, 30,794 exact imported attachment vertices |
| Tree/grass GPU deformation | Pass: production near/far/packed meshes and procedural parts, root/attachment pinning, zero controls, nonuniform-scale lever caps, finite-difference normal agreement, shared gusts and frozen/animated leaf phase |
| Live settings publication | Pass: zero and changed strengths reach shaders through the production publisher; finite/range guards, frozen payload, replacement owner, old-owner exit, active-owner cleanup and reactivation |
| Shared tree shadows | Pass: visible/shadow-only agreement on voxel and proxy receivers; storm changes 80 ground-shadow pixels in each fixture |
| Legacy vegetation | Pass: direction, roots, scale, rebasing, ring seam, freeze and calm guards |
| Facing-relative compass | Pass: real character cardinal headings and yaw wrap, both map modes, pitch/roll policy, changed wind, calm/disabled/preference, M toggle and stable map resources |

Follow-up logs: `tree-parts-settings.log`, `tree-parts-vegetation.log`, `tree-parts-live-publication.log`, `tree-parts-shadows.log`, `tree-parts-legacy.log` and `compass-facing.log` under `artifacts/shared-wind/`. No FPS benchmark was run. The pre-existing optional native-mesher, certificate-store, shader-cache and full-scene shutdown diagnostics remain as recorded below.

Implementation and primary review are complete. Work was delegated to the requested Sol 6.1 workers at low reasoning. The primary agent reviewed the changes, built the project (zero warnings/errors), inspected representative captures, and ran the functional checks below. Existing grass appearance and popup fixes remain in the working tree.

| Check | Primary result |
| --- | --- |
| Shared wind state | Pass: seed, ring wrap, pause, rewind, transport edits, finite long clocks |
| Spawned storm integration | Pass: rise/decay controls, direction, continuous deletion, manual inherit/calm and revision invalidation |
| Production gust atlas agreement | Pass: CPU/GPU maximum multiplier error 0.000699163 across adjacent samples, rebasing, rotation/nonuniform scale, moved anchor and ring seam |
| Publisher lifecycle | Pass: active owner replacement, old owner exit, active owner cleanup, and equal-payload reactivation against the same weather source |
| Shared vegetation render | Pass: imported LOD0–3, packed LOD3, grass, passing patches, root pinning, trunk/canopy lag and bounded bend |
| Shared tree shadow render | Pass: moving shadows and visible/shadow-only agreement on voxel and proxy terrain |
| Tree preparation | Pass: enlarged deformation bounds and existing sliced preparation contracts |
| Legacy vegetation render | Pass: existing grass/meadow, authored/procedural trees, direction, roots, freeze, rebasing, seam and calm guards |
| Water response | Pass: mean filtering, spectrum cadence, manual/observer priority, long clocks and controller ownership |
| Water render | Pass: proxy/global ocean, lake and river; pause, bearing and enabled/disabled gust detail |
| FFT surface GPU queries | Pass: maximum rendered/query disagreement 0.013405 m against the existing 0.030000 m budget |
| Full-scene shoreline | Pass: real terrain contact pinning, offshore attenuation distance, geometry ownership and live bindings |
| Full-scene FFT swimming | Pass: 0.916 m buoyant rise, 172/180 observations consuming valid queries, 83 advancing accepted samples, and 145 independent comparisons with the same physics-consumed sample serial; maximum height error below 0.00005 m |
| Wind persistence/menu binding | Pass: false/zero, finite validation, atomic round trip, save failure, migration, reset and preset |
| Popup isolation | Pass: tooltips, dropdowns, picker and eyedropper; exact sampled/displayed color |
| Full grave-menu interaction | Pass: section headers, fold persistence, wheel guards, existing tabs and five Wind sections |
| Radar input/render | Pass: default map, M details, retained terrain/viewport, cached wind in both orientations, calm/disabled/preference, and layering beneath the tuning drawer |

Logs are under `artifacts/shared-wind/`. No FPS benchmark was run. Rendering logs still include the pre-existing missing optional native mesher, certificate-store and shader-cache diagnostics, with some full-scene shutdown resource warnings.

The swimming fixture now waits for the real streamed terrain/shoreline footprint to settle before releasing the player. It distinguishes the sample consumed during physics from a newer asynchronous GPU readback. Its query freshness/resource guards, 65% consumed-query requirement and 0.08 m height-error threshold remain unchanged. Rendered FFT/query agreement is also checked independently by the GPU surface fixture.

## Controls and behavior

- Open the grave-key drawer and choose **Wind** for storm buildup/decay, gust strength/size/travel, trunk/canopy response, water response and radar settings. The **Dramatic storm preset** raises storm wind to 38 m/s and increases tree/water response; it configures the response to storms rather than spawning one.
- In **Wind → Tree response**, **Trunk and branch bend strength** defaults to **0.15** and **Leaf and canopy flex strength** defaults to **1.0**. Trunk zero keeps wood static while leaves can flex; canopy zero makes foliage ride the common structural bend. The Dramatic preset also keeps these strengths at 0.15/1.0.
- Natural and spawned inherited storms use the same resolved surface wind. Weather/cloud travel keeps its separate transport speed, while live gust patches integrate the resolved wind continuously.
- **Wind → Grass response** contains **Grass bend strength**, **Enable grass gust response** and **Grass gust response**. Shared pattern size and travel are also in Wind. Grass keeps appearance, density, geometry and interaction controls. Saved wind-response values survive Grass reset; Wind reset clears wind response while preserving the other grass settings.
- **Clouds → Rain haze** provides enable, density, near-clear distance, maximum opacity, variation and tint, with save/reset and persistence. Defaults are density **0.004/m**, near-clear distance **8 m**, maximum opacity **0.6** and variation **0.25**. Tint is RGB; the picker hides alpha because opacity has its own control.
- FFT water follows a smoothed mean wind with spectrum regeneration limited to once per simulation second for wind changes. Fast shared gusts modulate bounded surface detail/foam. Manual ocean wind remains in **Water**.
- The radar starts visible with just the map. **M** expands/collapses details and the wind destination compass. Its forward tick represents character facing; the pointer updates as the character turns even when the map stays north-up. **N** orientation and **+/-** zoom remain available. The drawer renders above the radar while tuning is open.

## Capture evidence and practical limits

The primary agent inspected calm/storm/passing-gust vegetation, water detail, tree shadow, shoreline, radar and Wind drawer captures. Representative artifacts:

- [Tree calm](../artifacts/shared-wind/vegetation/imported_lod0_calm.png), [passing gust](../artifacts/shared-wind/vegetation/imported_lod0_passing_gust.png), [storm](../artifacts/shared-wind/vegetation/imported_lod0_storm.png).
- [Water gust disabled](../artifacts/shared-wind/water/gust-disabled.png), [gust enabled](../artifacts/shared-wind/water/gust-enabled.png), [shoreline contact](../artifacts/shared-wind/water/shoreline.png).
- [Tree shadow calm](../artifacts/shared-wind/shadows/calm.png), [storm](../artifacts/shared-wind/shadows/storm.png).
- [Default map](../artifacts/shared-wind/radar/collapsed.png), [expanded details/arrow](../artifacts/shared-wind/radar/expanded.png), [Wind controls](../artifacts/shared-wind/wind-controls.png).
- Follow-up: [near tree calm](../artifacts/shared-wind/tree-parts/imported_lod0_calm.png), [stiff wood with leaf flex](../artifacts/shared-wind/tree-parts/imported_lod0_storm.png), [packed distant tree](../artifacts/shared-wind/tree-parts/packed_lod3_storm.png), [updated tree shadows](../artifacts/shared-wind/tree-parts-shadows/voxel_on.png).
- Facing-relative compass: [north-up, facing 0°](../artifacts/shared-wind/compass/minimap_expanded_north_up_facing_0.png), [north-up, facing 90°](../artifacts/shared-wind/compass/minimap_expanded_north_up_facing_90.png), [heading-up, facing 90°](../artifacts/shared-wind/compass/minimap_expanded_heading_up_facing_90.png), [default collapsed map](../artifacts/shared-wind/compass/minimap_collapsed.png).
- Perlin follow-up: [33 nearby trees at t=1](../artifacts/shared-wind/perlin-vegetation/imported_lod0_actual_perlin_33_plants_t1.0.png), [t=2](../artifacts/shared-wind/perlin-vegetation/imported_lod0_actual_perlin_33_plants_t2.0.png), [grass response debug](../artifacts/shared-wind/perlin-vegetation/near_grass_actual_perlin_spatial_response.png), [tree shadow](../artifacts/shared-wind/perlin-shadows/voxel_on.png).
- Rain haze depth fixture: [clear](../artifacts/shared-wind/rain-haze/clear.png), [light rain](../artifacts/shared-wind/rain-haze/rain_15.png), [heavy rain](../artifacts/shared-wind/rain-haze/rain_100.png), [night rain](../artifacts/shared-wind/rain-haze/heavy_night.png). These controlled captures use the actual owner-created effect; the dark foreground rectangle stays clear while the distant object and background fade.

The source LOD3 cards combine bark and foliage in one texture, so distant geometry uses a conservative upper-crown approximation. Near attachments are baked once from connected leaf groups with a bounded nearest-bark search. A group without a bark candidate within 12% of normalized tree height keeps the common structural motion and receives no extra flex. Existing source COLOR/UV channels remain intact. Additional near-leaf motion is capped by 32% of each vertex's world attachment lever and the remaining tree envelope; the conservative scale bound assumes orthogonal, non-sheared instance axes.

These are functional captures; the user's in-game visual and FPS review remains authoritative. Live gust advection integrates changing mean wind continuously. Arbitrary historical gust queries approximate the trajectory using the queried mean wind, and shader lag taps reconstruct past patches using the current velocity during rapid direction changes. Analytic fallback water retains its existing base-height CPU swimming behavior; FFT ocean displacement and swimming queries have the rendered/query agreement recorded above. Distant water uses shared direction/clock/mean response and bounded detail rather than projecting the local tangent gust atlas across the full ring.

Rain haze reconstructs opaque scene depth and treats sky/scaled far background as a capped distant path. It leaves the existing atmosphere/cloud fog owners independent and uses cached rain-cover metadata without additional terrain queries. Shelter suppression is conservative: the local veil disappears under a roof or in deep shelter. Forward+ rendering was verified; the shader includes Compatibility depth reconstruction, but Compatibility was not part of these rendered checks.
