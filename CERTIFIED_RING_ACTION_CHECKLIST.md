# Certified Ring Implementation Checklist

Target: 5,000 km radius, 318 km width, 22.3 km structural depth, -2 km to +8 km terrain range, and a +10 km structural wall top.

This is the live implementation checklist. Items are checked only after an automated build or runtime validation passes. Visual acceptance items remain unchecked until tested by a person.

## A. Certified dimensions and configuration

- [x] Add a named certified-ring launcher preset.
- [x] Support a 318 km ring width without clamping.
- [x] Support a -2,000 m terrain floor and +8,000 m terrain ceiling.
- [x] Establish sea level as the absolute Y=0 m terrain datum.
- [x] Separate the +8 km build ceiling from generated elevation: routine mountains cap at 5.2 km and one seed-selected summit caps at 7.6 km.
- [x] Carry the 22.3 km structural depth into runtime geometry configuration.
- [x] Derive the circumference from an exact integer count of 32 m chunks.
- [x] Display the effective radius/circumference and certification status in the launcher.

## B. Scale-safe coordinates

- [x] Keep canonical circumference and radius calculations in `double`/integer form.
- [x] Remove avoidable float truncation from LOD geometry snapshots and background mesh construction.
- [x] Add certified-scale wrap, seam, and coordinate round-trip self-tests.
- [x] Verify all ring dimensions fit their storage and serialization types.

## C. Scale-safe global preprocessing

- [x] Select min/max base-cell size from ring dimensions.
- [x] Cap certified-ring min/max memory/cell counts.
- [x] Remove synchronous full-resolution min/max construction from scene startup.
- [x] Build global summary data incrementally or in cancellable background work.
- [x] Cache global summary data by terrain seed and generator version.

## D. Terrain and global representation

- [x] Retune segmented regional HLOD for the certified circumference and width.
- [x] Add an always-present analytic/global ring tier.
- [x] Represent the 22.3 km underside and side edges without voxelizing the structure.
- [x] Keep the structural sidewall 2 km above the +8 km terrain/build ceiling in local collision, regional HLOD, and both global renderers.
- [x] Cross-fade local voxels, clipmaps, regional HLOD, and the global ring.
- [x] Make clipmap inner exclusions altitude-aware instead of applying a flat horizontal hole at every camera height.
- [x] Keep the isolated scaled-far seeded surface visible as a Canvas-background safety net so queued or missing regional/global sectors expose coarse terrain instead of black.
- [x] Replace the build-ceiling clip approximation with per-sector min/max terrain bounds and retire a coarse sector only after its complete finer-LOD footprint is resident or intentionally owned by another finer tier.
- [x] Human-validate that the altitude-selected 64-128 m regional tier remains continuous beneath the player instead of exposing the whole-ring fallback.
- [x] Prevent the scaled whole-ring chord from covering the near-surface sky by retaining a 625 km local cutout below 2 km altitude and smoothly restoring the fallback between 2 and 20 km.
- [ ] Human-confirm the local horizon remains clear near sea level while the fallback returns without a visible ring during ascent through 2-20 km.

## E. Floating origin and precision completion

- [x] Make canonical position the authority for player traversal and streaming.
- [x] Complete origin-shift propagation across terrain, physics, clouds, atmosphere, audio, and debug systems.
- [x] Reconstruct detailed render positions relative to the active origin/camera.
- [x] Add full-circumference and high-speed automated traversal tests.

## F. Performance certification

- [x] Add process working-set, frame-time, and terrain-resource high-water diagnostics.
- [x] Add a command-line benchmark harness, high-speed seam route, and machine-readable JSON results.
- [x] Add standing-vista, maximum-relief, dense-edit, cloud-heavy, and worst-lighting benchmark stages.
- [x] Enforce High-preset streaming/upload/frame-time budgets.
- [x] Add adaptive quality fallbacks and memory-pressure handling.
- [ ] Pass Arc 140V, 16 GB, 1080p High at 60 FPS target / 45 FPS minimum / <=6 GB.
- [ ] Pass RTX 3060 or RX 6600 class, 1440p Very High at 60 FPS.

## G. Human visual acceptance

- [ ] Confirm curvature reads correctly at 10, 50, 100, and 250 km.
- [ ] Confirm distant terrain keeps natural colour instead of becoming uniformly blue.
- [ ] Confirm lower terrain LODs do not receive sunlight through terrain.
- [ ] Confirm no visible seams, cracks, holes, or objectionable LOD pops during traversal.
- [ ] Confirm the global arc, clouds, landmarks, and structure convey the intended scale.

## H. Far-side visibility without a planetary camera frustum

- [x] Remove the certified ring's 12,500 km gameplay-camera far plane.
- [x] Bound the normal camera at 1,200 km, beyond the 850-1,000 km regional HLOD handoff.
- [x] Supersede the unstable nonlinear depth-compression experiment with an isolated scaled-space `SubViewport` and `World3D`.
- [x] Render the seeded analytic surface, 22.3 km structural shell, and full-ring clouds at a uniform 1:1,000 scale.
- [x] Synchronize the far camera from canonical position, gameplay-camera orientation/projection, and sun/sky state.
- [x] Composite the far viewport behind normal-depth voxels and regional HLOD without building the legacy full-scale meshes.
- [x] Runtime-validate the scaled renderer, capped gameplay camera, certified dimensions, and disabled legacy meshes.
- [x] Pass clean .NET build, Godot project parse, certified startup, and automated direct-view image capture.
- [ ] Human-test the opposite arc and 500-1,000 km handoff for clipping, holes, depth inversions, or cloud overlay errors.

## I. Seeded terrain parity and far-ring tuning

- [x] Replace the analytic ring's flat sine-coloured placeholder strip.
- [x] Build analytic-ring elevation from the authoritative `RingTerrainSampler`.
- [x] Use one seeded continent/climate/elevation palette across proxy, regional, and analytic terrain tiers.
- [x] Double the certified analytic cross-ring resolution from 8 to 16 cells.
- [x] Include the complete terrain definition in global-summary cache identity and invalidate legacy caches.
- [x] Make the launcher's base-surface-height setting affect terrain generation.
- [x] Runtime-assert and report the selected seed used by the certified terrain stack.
- [x] Regenerate the certified global summary and build all 128 seeded HLOD segments successfully.
- [ ] Human-confirm that local, proxy, regional, and opposite-ring terrain read as the same seeded world.

## J. Cross-ring physical flight handoff

- [x] Move interior flight in ring-centric Cartesian space so speed and direction remain physical near the ring center.
- [x] Rebase the canonical surface chart at the center while preserving camera heading and velocity.
- [x] Preload the opposite seeded voxel sector before arrival.
- [x] Use the scaled-tier near fade and normal regional HLOD instead of restoring the unstable depth-warp renderer.
- [x] Resolve direct arrival against the authoritative edited terrain height and resume normal voxel collision.
- [x] Fade local gravity smoothly from full strength at 10 km to zero at 20 km above terrain.
- [ ] Human-fly from one surface to the other and confirm the center rebase, visual transition, terrain readiness, and landing.

## K. Local 3D terrain minimap

- [x] Add a bottom-right independent 3D minimap viewport without rendering the voxel scene twice.
- [x] Sample seeded terrain and sparse runtime elevation edits on a background worker.
- [x] Display biome/elevation colour, 250 m contours, terrain relief, and player heading.
- [x] Display map span, local elevation, altitude above terrain, and gravity percentage.
- [x] Keep the player fixed at the scanner origin while terrain scrolls continuously in three dimensions.
- [x] Add a vertical landing tether and surface marker, with terrain naturally leaving the scan volume at high altitude.
- [x] Add reusable station, asteroid, ship, waypoint, and ring-proxy trackables.
- [x] Add hide/show, 0.5-128 km zoom, and north-up/heading-up controls.
- [x] Blend from the strategic map into a 34-degree local 3D view between 3 km and 500 m, with slight screen-up framing.
- [x] Adapt close-view terrain overscan, sampling density, 10 m contours, marker scale, and 32 m recentering.
- [x] Cap minimap rendering at 30 FPS and rebuild only after movement or terrain edits.
- [x] Pass clean .NET build, Godot parse, certified GPU launch, and both broad-altitude and 500 m local-3D captures.
- [ ] Human-confirm minimap size, tilt, contour density, heading behavior, and readability during movement.

## L. Seeded surface hydrology

- [x] Add one deterministic hydrology classification shared by voxels, local/regional LODs, the minimap, global HLOD, and the scaled far ring.
- [x] Fix ocean water level at canonical Y=0 while retaining shelves, deep basins, and the -2 km terrain floor.
- [x] Add seeded inland lakes with flat 10 m-quantized water levels, 2.5-9 km radii, and bounded 8-90 m basin depths.
- [x] Add moisture/elevation-gated river channels with locally following water levels and 3-18 m beds.
- [x] Render the water surface elevation and distinct ocean/lake/river colour at every terrain LOD instead of colouring the exposed ocean floor blue.
- [x] Show water kind, level, and depth on the 3D minimap while keeping ground elevation separate.
- [x] Add seam, elevation, water-level, depth, and global distribution audits; version the generator and invalidate prior terrain caches.
- [ ] Human-check shore shapes, lake frequency, river width/continuity, water colour, and LOD transitions in motion.
- [ ] Add swimming, buoyancy, underwater rendering, waves/reflections, and terrain-edit interaction in a later gameplay/material pass.

## M. Terrain-generation performance pass

- [x] Reuse each canonical ring coordinate across all same-position noise fields instead of repeating wrap and trigonometric conversion per field.
- [x] Add a height-only sampler path that skips climate colour work while preserving identical terrain and hydrology output.
- [x] Cache complete terrain samples across each proxy worker batch and reuse them for parent morphing, blocky neighbors, skirts, and adjacent-sector borders.
- [x] Stream completed sectors to the upload queue immediately instead of waiting for the entire 48-96-sector worker batch.
- [x] Replace square streaming windows with visibility-bounded circular windows while retaining current and predicted coverage.
- [x] Add a bounded 512-tile baseline surface cache shared by vertical voxel chunks and empty-chunk rejection.
- [x] Remove duplicate terrain evaluation from minimap mesh and information updates.
- [x] Reuse the global hierarchy's shared corner lattice, reducing its cold baseline from five samples per cell to one center plus shared corners.
- [x] Add `--rebuild-terrain-cache` for repeatable cold-generation measurement without manually deleting cache files.
- [x] Raise High-preset generation concurrency from two workers to three after reducing per-worker sample pressure.
- [x] Pass clean build, certified cold-cache rebuild, cached startup, HLOD completion, and sustained 1080p diagnostic benchmark.
- [ ] Human-traverse multiple LOD boundaries and confirm the circular windows/progressive uploads never expose a hole or objectionable partial ring.

## N. Structural walls at every terrain LOD

- [x] Decouple structural edge walls from terrain-sector coverage so an inland streaming window cannot omit them.
- [x] Give every proxy LOD a dedicated pair of lightweight edge-wall strips at canonical across=0 and across=318 km.
- [x] Track the current and predicted around-ring windows without constructing a full-resolution wall around the circumference.
- [x] Use the terrain LOD's existing near/far dither bands so wall detail hands off with its owning terrain tier.
- [x] Restore the previously disabled regional/global HLOD wall tier as 128 precision-safe segments with the same near and 500-850 km far fades as regional terrain.
- [x] Retain voxel edge walls for close collision and the scaled-space structural shell for the full-ring view.
- [x] Preserve the certified -22.3 km to +10 km wall contract, inward-facing winding, lighting, floating-origin transform, and runtime shadow policy.
- [x] Pass clean .NET build and certified runtime construction checks for every active proxy tier.
- [ ] Human-approach both ring edges and confirm continuous walls through voxel, proxy, regional, and scaled-world transitions without gaps, duplicates, or reversed faces.

## O. Certified-scale local precision

- [x] Keep the explorer's stored transform camera-relative instead of retaining a roughly 5,000 km parent-local coordinate after floating-origin shifts.
- [x] Keep streamed voxel chunks in camera-relative top-level transforms so neighboring chunk origins are not rounded independently at ring scale.
- [x] Refresh precision-safe chunk transforms whenever the floating origin moves the world root.
- [x] Replace the nominally 64-bit world-to-canonical path with component-wise double-precision inverse transform math.
- [x] Replace cancellation-prone ring-curvature subtraction with a stable double-precision half-angle formulation.
- [x] Add certified-scale seam and jump/landing regression switches for repeatable validation.
- [x] Pass clean build, a zero-gap loaded-neighbor seam sample, and a zero-quantization automated jump/landing.
- [ ] Human-confirm smooth jumping and no voxel-sized tears while moving across chunk boundaries.

## P. Localized ring atmosphere and scale haze

- [x] Replace simple world-distance terrain tinting with an analytic atmosphere confined to curved layers next to the ring surface.
- [x] Add a 2.5 km aerosol/boundary layer and a 20 km upper-air layer, including coverage down to the certified -2 km terrain floor.
- [x] Measure only the camera-to-fragment path inside those layers so the central ring volume remains vacuum.
- [x] Share one atmosphere include and one authoritative parameter model across voxel, proxy, regional/global, structural hull, cloud, and scaled-far materials.
- [x] Preserve direct opposite-ring visibility while allowing long near-horizontal terrain views to accumulate substantially stronger haze.
- [x] Blend day haze toward a neutral sky gray-blue, reduce density and emission at night, and eliminate haze above the atmosphere.
- [x] Match the procedural horizon to the neutral daytime haze color so terrain fades into an atmospheric sky instead of an unrelated blue backdrop.
- [x] Replace the conventional global-Y procedural sky with a ring-aware sky shader whose horizon follows the player's radial surface-up direction.
- [x] Hold any uncovered pixels below the local horizon at the same haze color instead of exposing a separate dark planetary ground hemisphere.
- [x] Preserve the local zenith gradient, sun disk/halo, day/night blend, global fog interaction, and 8-20 km transition to black space.
- [x] Fade the entire procedural sky, global fog, and ambient fill from atmospheric at 8 km to true black space at 20 km.
- [x] Give dedicated proxy, regional, analytic, and scaled structural wall materials 1.35x atmospheric extinction so distant walls sit inside the haze instead of drawing through it.
- [x] Scale every physical atmosphere length into the isolated 1:1,000 far viewport so its appearance follows the main renderer.
- [x] Pass clean build, shader compilation, certified runtime, and analytic visibility-contract tests.
- [ ] Human-tune haze color and strength while checking 5 km, 20 km, 80 km, opposite-ring, night, and high-altitude views.

## Q. Procedural mesh clouds

- [x] Supersede the camera-local and scaled-space FogVolumes with opaque, depth-tested procedural mesh formations.
- [x] Retain the inexpensive seeded curved cloud shell for the horizon, opposite arc, and isolated scaled-far viewport.
- [x] Keep local formations in the canonical 1.4-3.2 km cloud band and deterministically select their cells and archetypes from the terrain seed.
- [x] Generate eight reusable archetypes off-thread by smooth-unioning ellipsoid fields and extracting one continuous outer surface with marching tetrahedra.
- [x] Remove every internal puff surface and derive shared normals from the combined density-field gradient so lighting no longer exposes intersecting ovals.
- [x] Replace inter-formation connector overlays and enlarged replacement archetypes with one true implicit union mesh per touching local group.
- [x] Preserve each union member's original archetype lobes, transform, size, profile, and vertical extent while removing intersecting surfaces and join seams.
- [x] Extract camera-local union groups off-thread with analytic gradients, empty-cell rejection, adaptive cell sizes, capped axes, and atomic fallback replacement.
- [x] Batch union geometry into at most three shadow/detail/transition meshes and defer distant groups to instancing until they enter the 14 km union range.
- [x] Instance each reusable archetype through its own `MultiMeshInstance3D`, retaining bounded draw ownership without one node per formation.
- [x] Add connected formation cores, protruding lobes, subtle surface breakup, controlled underside lighting, atmospheric fading, and optional conventional shadow casting.
- [x] Add High, Very High, and Ultra tiers with 10 km, 18 km, and 28 km requested local radii; select High for the Arc 140V certified preset.
- [x] Limit the local patch by curvature sag on smaller experimental rings while allowing the full tier radius on the certified ring.
- [x] Replace the direct local-to-sheet swap with a three-tier path: full-detail merged formations to 10 km on High, simplified merged formations through a 50 km main-depth-buffer tier, then the seeded scaled-space shell.
- [x] Reuse identical deterministic formation cells, transforms, archetype choices, and scales across the detailed and transition meshes; dither-crossfade them over 6.2-10 km instead of popping representations.
- [x] Break the high-altitude cloud lattice with deterministic ±42% sub-cell position jitter plus blended 5-cell and 13-cell weather-density fields, producing irregular placement and broad clustered clear/cloudy regions without changing the seed contract.
- [x] Fade the scaled-space shell in over the outer 38-50 km of the transition tier, leaving a broad 3D/whole-ring overlap instead of exposing a hard 2D boundary.
- [x] Generate a bounded 512 px camera-local cloud-shadow mask from those same formation cells, project it along the current sun direction, and distribute it to voxel, proxy/global terrain, structural-wall, and ocean materials.
- [x] Retain conventional cascaded mesh shadows nearby, fade a 45%-strength projected mask in over 1.8-3.5 km, ramp it to 60% over 3.5-5 km, and carry formation-matched soft cloud shadows to the 50 km shell handoff without expanding Godot's expensive directional-shadow cascades.
- [x] Filter the 512 px projected mask with a separable five-tap triangular blur, softening the former sharp silhouette by roughly two texels on each side without adding a per-frame GPU blur.
- [x] Crossfade a 60%-strength seeded analytic shadow field in over 38-50 km, keep it enabled on every regional/global terrain and ocean LOD plus the isolated scaled-far ring, and eliminate the former no-shadow region beyond the local mask.
- [x] Share the analytic cloud field between the far visible shell and all far shadow receivers so cloud banks and shadows use the same seed, spatial frequency, wind, and ring wrapping.
- [x] Increase the whole-ring shell's spatial frequency and seed it from the selected terrain seed so its far banks no longer resolve as a handful of giant color bands.
- [x] Keep the far-ring Canvas as a strict background pass so local mesh clouds naturally render over the sky and far ring without duplicating local cloud geometry in scaled space.
- [x] Keep volumetric fog disabled in both the main and scaled far environments, preventing central-vacuum fog, temporal trails, and the former cloud/ring draw-order loop.
- [x] Preserve the K-key local-cloud A/B toggle and measured total-GPU-frame-time reports, now including live formation and archetype counts.
- [x] Pass clean .NET build, Forward+ mesh-cloud shader compilation, direct-view main/far captures, certified topology, and the full automated stress route on the Arc 140V.
- [x] Rebase the top-level cached cloud owner with every suspended fast-travel floating-origin shift, then return it to identity atomically before installing current canonical formation transforms.
- [x] Remove the 10-second geometry cadence; move clouds and the existing projected mask continuously with wind, and rebuild unions only for cell, edit, quality, or explicit coordinate changes.
- [x] Retain each installed seamless union during event-driven supersession so touching clouds never fall back to visibly intersecting individual meshes.
- [x] Classify conventional union shadows by the nearest merged bound, add the full union proxy to the projected mask, and scale footprint/density for massive storms.
- [x] Apply additional lower-body extinction from union optical thickness, storm profile, and precipitation so deep storm bases approach the authored shadow colour.
- [ ] Human-tune formation scale, lobe roundness, coverage, underside darkness, shadow strength, shell handoff, entry/exit, and high-altitude appearance at 1080p High.

### Automated procedural-mesh-cloud verification — 2026-07-23

- The Arc 140V High profile generated eight detailed archetypes containing 37,073 source triangles and eight transition archetypes containing 9,717 source triangles, then instanced 24 detailed plus 688 transition formations at the starting vista.
- Both archetype sets are extracted off-thread; Godot installs sixteen bounded `MultiMeshInstance3D` owners on the main thread, and the transition tier disables conventional shadow-map casting because its matching projected mask owns that distance.
- Automated main-view capture shows transition cloud geometry in front of the far-ring background; the isolated scaled-space capture contains only the seeded far shell, with no duplicated local FogVolume or canopy.
- A dedicated 26 km straight-down capture verifies that cloud formations no longer sit on visible cell centers; density varies smoothly between clustered and sparse weather regions.
- The same 26 km capture completed while multiple local/regional sectors were still queued and showed continuous seeded terrain beneath the player, with no black clipmap exclusion hole.
- The projected mask updates only when the canonical cloud cell or floating origin changes, covers a 100 km square at 512 px, and avoids its texture sample entirely inside the 1.8 km conventional-shadow region.
- Runtime diagnostics confirmed 27 live terrain/ocean/wall receivers and a populated 512 px mask with 71,040 occupied texels, a 229/255 peak, a two-pixel weighted blur, the 45%-to-60% strength ramp, and the intended 1.8-50 km receiver range.
- The continuous shadow chain is physical cascades through roughly 4-6 km, formation-matched projection through 50 km, and seed-matched analytic shadows thereafter; the transition bands overlap rather than relying on one exact cutoff distance.
- The final 15-second warmup plus 5-second 1,200 m/s stress route with all-distance shadows averaged 52.4 FPS with a 20.6 FPS 1% low, 142.3 ms worst frame, and 1,783.1 MiB working set while the 7/7 terrain window continued route-driven streaming. This is regression evidence, not a 60 FPS certification pass.
- No mesh-cloud shader errors, asynchronous mesh failures, or RID lifecycle errors appeared; the unrelated StartMenu `remove_child()` timing error remains.

## R. Ambient occlusion

- [x] Enable Forward+ SSAO for nearby voxel corners, terrain cavities, object contacts, and structural-wall intersections.
- [x] Retune the profile toward Minecraft-strength corner shading: 1.2 m radius, 2.15 intensity, 1.45 power, 0.80 detail, 0.98 sharpness, and 0.38 direct-light contribution.
- [x] Use Medium half-resolution SSAO with one blur pass and an 80-350 m fade for the Arc 140V High target; the reduced blur keeps the stronger voxel-scale contacts crisp.
- [x] Add a J-key A/B toggle and measured total-GPU-frame-time reports.
- [ ] Human-check voxel creases, terrain contacts, vegetation/building contacts, edge halos, motion stability, and steady-state J-key GPU cost at 1080p High.

## S. Voxel 7/7 residency optimization

- [x] Make 7 surface chunks and 7 vertical chunks the default visual window while retaining the 2/2/1 collision window.
- [x] Classify procedural chunks as virtual air or fully enclosed virtual solid; keep surfaces, cliffs, structural walls, edited chunks, and collision-resident solid chunks materialized.
- [x] Resolve unloaded mesh neighbors from the authoritative terrain seed and sparse edits instead of treating them as air.
- [x] Stop rebuilding adjacent meshes merely because a deterministic neighbor unloaded.
- [x] Preserve save/replication compatibility while reducing resident block IDs from 16-bit to 8-bit storage.
- [x] Remove unused voxel UV vertex data and add a bounded 128-node chunk pool with explicit shutdown ownership.
- [x] Add separate classification/load/time budgets and expose virtual-air, virtual-solid, pooled, queued, and block-data metrics.
- [x] Pass clean .NET build and Forward+ 7/7 warmup: 3,375 desired, 374 materialized, 1,555 virtual air, 1,446 virtual solid, zero queued, and 11.7 MiB dense block data.
- [ ] Human-check fast walking/flight, 180-degree turns, chunk seams, cliff faces, mining/placing at chunk boundaries, underground collision, and visual handoff at radius 7.

## T. Asynchronous voxel meshing

- [x] Move greedy voxel geometry construction and optional collision-face expansion to bounded background workers using immutable palette, voxel, neighbor-face, and curvature inputs.
- [x] Keep Godot nodes, resources, mesh RIDs, physics shapes, and scene-tree changes on the main thread.
- [x] Add epoch cancellation, edit-version validation, desired-window validation, stale-result rejection, and deferred scheduling when both worker slots are occupied.
- [x] Limit installation to one completed voxel mesh per frame and retain the existing chunk-node pool.
- [x] Replace 6,144 repeated cross-grid neighbor lookups per chunk with six captured boundary faces that fetch each required procedural height tile once.
- [x] Preserve authoritative loaded-neighbor data and sparse saved edits in worker snapshots.
- [x] Retain managed vertex/index arrays for visual-only chunks so later collision promotion does not require `ArrayMesh.GetFaces()` GPU readback.
- [x] Add prepare, neighbor-capture, worker, upload, queue, active-worker, completed-result, and stale-discard diagnostics to the runtime HUD and certification report.
- [x] Add command-line A/B controls: `--disable-async-voxel-meshing` and `--voxel-mesh-workers=N`.
- [x] Pass clean .NET build with zero warnings/errors and repeated Forward+ certified-route runs without asynchronous build failures or RID leaks.
- [ ] Human-check terrain appearance, chunk-border continuity, collision promotion, mining/placing across chunk borders, fast reversals, and perceived traversal smoothness before closing the 7/7 optimization gate.

### Automated asynchronous-meshing verification — 2026-07-23

- Test hardware/render path: Intel Arc 140V, 16 GB system, 1920×1080 Forward+, certified ring, radius 7/vertical 7, 15-second warmup plus the existing 5-second 1,200 m/s stress route.
- Final async run: 32.0 FPS average, 15.0 FPS 1% low, 136.1 ms worst frame, and 1,808.6 MiB working set.
- Synchronous control on the same route: 31.5 FPS average, 10.0 FPS 1% low, 145.6 ms worst frame, and 1,609.7 MiB working set.
- The final warmup materialized 355 chunks and virtualized 1,322 air plus 1,215 enclosed-solid chunks; the remaining 483 queue entries were unclassified far-window metadata, with no deferred mesh jobs and no completed uploads waiting.
- Maximum measured stages were 11.46 ms main-thread preparation (10.50 ms first-touch neighbor height-tile capture), 9.73 ms background meshing, and 23.29 ms indivisible Godot mesh/collision installation.
- This short stress route validates the async lifecycle and improves the measured low-percentile result, but it does not meet the 60 FPS certification target and does not replace the required human-visible acceptance run.

## U. Fast-travel main-thread offload

- [x] Move voxel classification, deterministic block population, procedural neighbor resolution, greedy meshing, and optional collision-face expansion into the existing bounded worker lifecycle.
- [x] Replace six complete loaded-neighbor chunk clones with six direct boundary-face copies.
- [x] Add a cancellable latest-wins worker planner for desired visual/collision windows, prediction corridors, missing-chunk priorities, and departed chunks.
- [x] Add a 750 m/s speed-aware mode that cancels obsolete local voxel work while preserving an explicit cross-ring destination preload focus.
- [x] Suspend regional proxy and structural-wall rebuilds during fast travel while keeping resident regional and global terrain visible.
- [x] Suspend local cloud placement/union/shadow refreshes during fast travel while keeping the scaled-space cloud shell active.
- [x] Move projected cloud-shadow mask rasterization/blur/packing and structural-wall raw geometry construction to worker jobs.
- [x] Cache one ring-geometry snapshot per floating-origin refresh instead of recapturing it for every resident chunk.
- [x] Keep Godot scene-tree, resource/RID creation, physics state, texture upload, bounded mesh installation, and final node transforms on the main thread.
- [ ] Human-check high-speed visual continuity, rapid reversal, stale-result rejection, destination terrain arrival, and post-travel cloud/proxy refresh in Forward+.

### Automated fast-travel offload verification — 2026-07-25

- Clean .NET build completed with 0 warnings and 0 errors.
- A six-second warmup plus one-second certified headless route measured 0.51 ms maximum voxel main-thread preparation, down from the preceding 8.31 ms whole-chunk snapshot result; background voxel work peaked at 29.53 ms.
- The same diagnostic route averaged 86.8 FPS with a 19.3 FPS 1% low, 51.7 ms worst frame, and 298.8 MiB working set. Headless figures are regression diagnostics, not hardware certification.
- The automated 1,000 km/s cross-ring route logged regional-LOD and local-cloud suspension, crossed the ring center, preloaded 119 destination chunks, landed on authoritative seed-1337 terrain, and resumed both systems.
- During the repeated cross-ring run, the suspended cloud patch reported zero formations and 0.00 ms patch work; no voxel, proxy, cloud-union, shadow, or structural-wall worker failure was reported.
- The existing unrelated StartMenu deferred-removal error and no-save-slot warning remain visible during automated shutdown.

## Automated verification record — 2026-07-21

- Clean .NET build: 0 warnings, 0 errors.
- Godot editor-wide headless parse: passed.
- Certified runtime topology: 981,748 chunks, 31,415.936 km exact circumference, 5,000.001506 km effective radius.
- Certified cold summary: 7,670 x 78 cells at 4,096 m, 799,255 total hierarchy cells, 10.7 MiB; generated and all 128 regional HLOD segments became ready within the 30-second benchmark.
- Certified cached summary: loaded from cache and all 128 regional HLOD segments became ready during the short follow-up run.
- Full-scale stress route: seam crossing, relief teleports, 64 dense edits, cloud-heavy stage, worst-lighting stage, and repeated two-axis grid-aligned origin shifts completed without runtime errors.
- Far-field runtime self-test: certified camera capped at 1,200 km; analytic surface, structural shell, and clouds render in an isolated 1:1,000 scaled-space viewport; legacy compressed meshes are disabled.
- Structural-wall runtime self-test: terrain/building remains capped at +8,000 m, edge collision reaches +10,000 m, interior voxels above the build ceiling remain empty, and the scaled shell uses the same +10,000 m top.
- Automated head-on captures: the root composite and isolated far viewport both show a continuous opposite-ring band without the previous diagonal frustum cutoff.
- Automated cross-ring flight: a 1,000 km/s smoke route crossed the center, rebased to the opposite canonical sector, preloaded destination collision chunks, contacted seed-1337 terrain at its authoritative height, and resumed normal collision mode.
- Seeded terrain runtime: generator v4, seed 1337, and terrain definition `5F46B24E6D7BE18B`; fresh 10.7 MiB global summary generated and all 128 HLOD segments completed.
- Terrain elevation contract: sea level 0 m, a 5,200 m routine-mountain ceiling, one 40 km seed-selected exceptional summit at 7,600 m, and 400 m of terrain-to-build headroom.
- Global elevation audit: of 598,260 base cells at 4,096 m resolution, only 32 exceed 5,200 m and only 9 reach 7,000 m; sampled global range is -1,800 m to 7,579 m, with the exact summit center validated separately at 7,600 m.
- Headless performance numbers are diagnostic only and are explicitly marked invalid for hardware certification in generated JSON reports.

## Automated hydrology verification — 2026-07-22

- Clean .NET build: 0 warnings, 0 errors.
- Generator v7 regenerated the certified 10.7 MiB global summary and completed all 128 global HLOD segments.
- A 32,768-point certified-ring audit found 12,283 ocean, 233 lake, 1,126 river, and 19,126 dry samples for seed 1337.
- Audited maximum depths were 1,791 m ocean, 80 m lake, and 16 m river, all within their configured contracts.
- Certified runtime topology, canonical seam, elevation, Phase C hierarchy, dense-edit, and floating-origin checks passed with hydrology enabled.
- The 22-second diagnostic Arc 140V run averaged 56.1 FPS with a 1,163.9 MiB working set; formal hardware certification still requires the full-duration human-visible benchmark.

## Automated terrain-generation performance verification — 2026-07-22

- Clean .NET build: 0 warnings, 0 errors; generator remains v7 and seed output/cache identity is unchanged.
- At 720p, initial LOD1/2 worker batches fell from approximately 6.2-6.5 seconds to 1.77-1.85 seconds.
- At 720p, initial LOD3/4 worker batches fell from approximately 10.1-10.2 seconds to 1.10-1.31 seconds.
- Circular startup windows reduced the comparable initial sector sets from 79-81 sectors to 48-52 sectors at 720p.
- Progressive output was verified by successful sector uploads occurring before their parent workers reported completion.
- The forced cold certified hierarchy generated 799,255 cells in 3,423.48 ms; cached reconstruction completed in 32.78 ms.
- All 128 global HLOD segments completed after both cold and cached starts.
- The sustained 1920x1080 Arc 140V diagnostic averaged 59.6 FPS with a 1,456.6 MiB working set; formal certification still requires the human-visible acceptance run.

## Automated all-LOD structural-wall verification — 2026-07-22

- Clean .NET build: 0 warnings, 0 errors.
- Certified runtime topology, elevation, hydrology, canonical streaming, min/max hierarchy, HLOD, and floating-origin checks remained green.
- Every proxy tier activated by the certified High profile constructed two dedicated edge strips; the same implementation is shared by all eight authored proxy tiers.
- The formerly missing regional wall tier constructed 128 HLOD segments at 4,096 around-ring samples, with a far fade to the scaled-space shell at 500-850 km.
- Runtime construction reported a 32,300 m wall height, matching -22,300 m structural bottom to +10,000 m wall top.
- Short headless traversal completed repeated canonical teleports and two-axis origin shifts without wall-build exceptions; final visual transition acceptance remains a human check.

## Automated certified-scale precision verification — 2026-07-22

- Clean .NET build: 0 warnings, 0 errors.
- The certified 5,000.001506 km effective-radius scene completed topology, elevation, hydrology, Phase C, streaming, and floating-origin checks with precision-safe transforms enabled.
- Loaded adjacent voxel chunks measured a 0.000 m maximum and average boundary gap across 72 shared-edge samples.
- An automated full jump and landing measured 0.000000 m maximum transform error, 0.000000 m maximum frame-step error, and zero quantized-motion frames.
- The seam diagnostic now safely waits for an eligible neighboring pair instead of submitting an empty debug surface while chunks stream.
- Final visual acceptance requires human confirmation because headless transform metrics cannot judge perceived camera smoothness, temporal aliasing, or material-level seam artifacts.

## Automated localized-atmosphere verification — 2026-07-22

- Clean .NET build: 0 warnings, 0 errors; Godot compiled the shared include in the certified voxel, proxy, global, hull, cloud, and scaled-far material paths without shader errors.
- At the certified 5,000.001506 km effective radius, the daytime contract measured 0.123 opacity at 5 km, 0.341 at 20 km, and 0.520 at 80 km along the surface.
- The direct opposite-ring path measured only 0.162 opacity because it crosses the two thin atmospheric layers nearly perpendicularly with vacuum between them.
- A sightline remaining above the 20 km upper layer measured 0.000 opacity.
- The altitude-sky contract measured a full atmospheric sky at the surface, a 0.500 blend at 14 km, and a fully black space sky at 20 km.
- The custom sky shader compiled successfully and initialized in realtime mode with its horizon driven by the player's current surface-up vector rather than global Y.
- Every active proxy structural-wall tier built with its dedicated stronger-extinction material; regional, analytic, and scaled hull materials use the same 1.35x response.
- Certified topology, canonical streaming, elevation, hydrology, Phase C, floating-origin, and scaled-far startup checks remained green.
- Final color, contrast, LOD-transition, and perceived-scale acceptance remains a human-visible check.

## Automated hybrid-cloud verification — 2026-07-22

- Clean .NET build: 0 warnings, 0 errors.
- The certified Forward+ scene compiled the fog shader on the Intel Arc 140V without shader or renderer errors.
- Runtime selected the High tier with a 10,000 m local radius and the intended 1,400-3,200 m cloud band.
- The environment contract verified zero global volumetric density, preserving the atmosphere-free central ring volume.
- Certified topology, terrain elevation, hydrology, Phase C, floating-origin, localized-atmosphere, and scaled-far startup checks remained green.
- The short automated route was intentionally not treated as a cloud performance result because concurrent terrain construction dominated its frame time; steady-state K-key A/B timing remains the human acceptance gate.

## Automated altitude-aware far-fallback verification — 2026-07-24

- Clean .NET build: 0 warnings, 0 errors.
- Certified Forward+ startup passed topology, terrain hierarchy, regional LOD, scaled-far, atmosphere, cloud, and shader initialization checks on the Intel Arc 140V.
- A dedicated capture placed the player 2 m above the authoritative edited surface with the scaled local terrain excluded through 625 km, leaving the far viewport sky available behind the main regional terrain.
- The paired 26 km straight-down capture restored the scaled terrain safety net beneath still-queued regional uploads, with no return of the black center hole.
