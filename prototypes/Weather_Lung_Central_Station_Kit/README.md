# Weather Lung: central climate machine

An editable, macro-scale Blender architecture asset for the Ringworld project, based on the approved crescent-and-open-intake concept. This is the central station; it does not contain a 318 km × 318 km landscape or a working weather simulation.

## Start here

- Open `Weather_Lung_Central_Station.blend` for the editable model, cameras, material assignments and separate presentation context.
- Import `exports/Weather_Lung_Assembled.glb` into Godot 4 for the complete architecture at its correct metre scale.
- For streaming/culling, import the 12 files in `exports/sectors/` instead. Their mesh vertices are sector-local. Apply the exact `origin_godot_m` translations from `docs/asset_manifest.json` to reconstruct the station. Add the vane-bank module at its separately listed origin. Do not also load the assembled GLB, or geometry will be duplicated.
- `exports/Weather_Lung_Far_Proxy.glb` is a separate simplified silhouette model. A runtime LOD/visibility-range controller is not included.
- `exports/modules/Weather_Lung_Intake_Vanes.glb` contains every moving part (5 vane pivots with rams, 2 turbine rotors, 16 gill pivots) in the closed rest pose, local to the intake bank origin recorded in the manifest. Generate `Weather_Lung_Intake_Vanes_Animated.glb` (one sampled 240-frame breath loop) with `--export-anim`.

## Design and dimensions

The nominal footprint is 28 km long × 18 km wide. The broken tips and inset plates make the actual mesh bounds slightly smaller; exact bounds and triangle counts are in the manifest. The arch reaches approximately 4.8 km above the central datum; foundations extend roughly 95 m below it. Most perimeter terraces remain a few hundred metres high.

The original 1.5 km height estimate was increased to 4.8 km to preserve the approved image's dominant tilted, hollow intake. This height is a generator parameter. There is one major intake arch, an open basin, unequal oblique armor tongues, radial exhaust clefts, low condenser fields, three service islands, two access bridges and small galleries projecting from the arch. Graphite structural metal, pale ceramic armor and restrained teal inlays use ordinary PBR material properties.

The basin is deliberately open in the architecture exports. Its preview water and waterfall ribbons are separate presentation objects. Clouds are hidden preview-only volume placeholders; no simulated weather or cloud mesh is exported. An eventual regional relay network remains future work.

## Coordinate and scale contract

All dimensions and mesh vertices use metres. Blender uses +Z up/inward, +X along the circumferential tangent and +Y across the ring. The GLB export maps these to Godot as `(X, Y, Z) = (Blender X, Blender Z, -Blender Y)`.

The assembled GLB uses local vertex buffers for its 12 static sector nodes, with node translations restoring the overall arrangement. The individual sector exports use the same local buffers with identity placement. Authoring objects remain separate and editable in the `.blend`; the game exports batch fixed objects by sector while retaining material surfaces. Five vane pivots remain separate. Rotate those pivots about local Godot Z (the sign maps from Blender Y); the suggested ±22° range is an authoring starting point, not a validated collision envelope.

Do not enlarge the model by 1,000 on import. It is already approximately 28,000 m long.

## Ringworld placement and curvature

The delivered station is tangent-flat. The reference ring radius supplied for this project is 5,000,001.506 m. Across 28 km of circumferential span, the inner surface rises about 19.60 m relative to the centre tangent at either edge; the endpoint local-up tilt is approximately ±0.16043°. This is small for the macro silhouette but matters for near-ground placement and collision.

Conform or subdivide the station before final placement. For a local arc coordinate `s`, cross-ring coordinate `y` and inward height `h`, a suitable Blender-frame mapping around the centre tangent is:

- `theta = s / R`
- `X = (R - h) * sin(theta)`
- `Y = y`
- `Z = R - (R - h) * cos(theta)`

Orient the resulting local frame at the target ring position and use the project's floating-origin/double-precision strategy. Do not bake multi-million-metre global positions into the vertex buffers. Rigidly placing a sector approximates curvature; close-range collision and long decks may need smaller segments or deformed geometry.

## Included versus still to do

Included: closed architectural solids, open intake/basin negative space, editable PBR materials, named hinge pivots, batched GLB exports, sector assembly metadata, a simplified far proxy, Blender cameras/renders and a deterministic Python generator.

Not included: regional relays, vegetation/terrain production assets, detailed traversable interiors, authored UV texture sets, collision shapes, navigation meshes, weather logic, water simulation, gameplay animation/controller wiring or project integration. The galleries and bridges establish architectural access at macro scale; their gameplay traversal and collision still need authoring. The macro asset is not a claim of a finished level.

## Regenerate

Requires Blender 4.3 or later with the bundled glTF exporter. No external Python packages are needed.

`blender -b --python source/build_weather_lung.py -- --out /absolute/output/path --render`

Optional: `--arch-height 4800`, `--quick` (one low-sample preview), or `--preview-only` (skip exports). The generator uses a fixed seed. Run into a dedicated output directory; it replaces its own named outputs. Preview materials, lights and cameras are retained only in Blender. The generator does not access or modify the Ringworld repository.

## Validation

See `docs/asset_manifest.json` for measured bounds, counts, exact assembly translations and the export contract. Independent geometry and Godot-import validation accompanies the final package where available. A successful import is not an in-game frame-rate benchmark.

## Design update: art-direction pass

The intake is now a thick layered crescent hood: a deep graphite shell with three stacked armor bands of rising height separated by teal seam lights, an overhanging crest lip, rim light strips, eight stacked teal-edged bore fins following the arch curve, a diagonal truss and wall gantries under the shell. Each arch foot folds into a massive chevron ramp/plinth slab (replacing the thin buttress beams). Rim wings are chamfered, faceted modules with ridge roofs, stepped upper decks, teal edge lights and vent grilles; the inner lip is a three-tier stepped retaining wall with sluice mouths at the preview waterfall positions. The basin has six hex service platforms on single tall stalks with masts and docking arms, plus two small ones hung from the crest. Ceramic is slightly warmer. Hard 45 degree chamfers plus smooth-by-angle (40 degrees) shading keep curved parts smooth. Triangle count grew from 24,872 to about 75,300 (3.0x).

## Breath cycle and moving parts

`Breath_Control` (empty, collection `04_BREATH_RIG`, not exported) holds the custom props every moving pivot is driven from:

| Prop | Default | Meaning |
|---|---|---|
| `breath_phase` | 0..1 | Loop position. Inhale 0..0.5, exhale 0.5..1. Keyed linearly 0 to 1 over frames 1..241 with a cyclic F-modifier (action `Breath_Phase_Loop`). |
| `cycle_seconds` | 180 | Real-time period (also sets turbine revolutions per cycle). Timeline maps one cycle to 240 frames at 24 fps for preview. |
| `vane_amplitude_deg` | 35 | Vane open angle (fin tips swing forward/down). |
| `sun_available` | 1 | 0..1 day/night crossfade: 1 = heliostats track hot spots (beams and hot-spot discs in preview), 0 = heliostats stowed face-down and the floor heat-exchanger rings glow. Also drives the preview world and sun. |
| `gill_amplitude_deg` | 35 | Gill flare angle. |
| `turbine_max_rpm` | 1.5 | Peak rotor speed. Revolutions per cycle = round(max_rpm / 1.5 x cycle_seconds / 60) = 3, so the loop is seamless. |
| `ripple_lag` | 0.012 | Phase offset between neighbouring vanes and gill columns. |

Motion: vanes open with a cosine ease (closed at 0, fully open at 0.5, ripple-lagged by index). Gills flare only during exhale in a `sin^2` window of 0.38 cycle that starts at 0.5 + lag x column, so they are exactly closed at phase 0 and 1. The turbine integrates a speed profile (fast on inhale, coasting on exhale), always positive, so it never reverses. Edit the props in the Custom Properties panel (or scrub the timeline); to change the cycle time, change `cycle_seconds` and, if you want the timeline to match, the scene frame range (`timeline_frames` is informational).

Pivots (all in `02_MOVABLE_VANES_Export`, exported with the vane module; Blender axes, glTF maps Y to up and Z to negative Blender Y):

- `VanePivot_00..04`: hinge local Y, `rotation_euler.y` 0 to +35 degrees (positive opens: tips swing forward and down, away from the turbine). `Vane_RamCyl_XX` (static cylinder, rotation_euler.y, aims at the fin pin) and child `Vane_RamRod_XX` (`location.x` extension) form the actuator, driven from the vane angle.
- `Turbine_Rotor_A/B`: axis `spin_axis_blender` (~+X tilted 7 degrees about Y), `rotation_axis_angle[0]` radians, B counter-rotates (20 and 22 blades). Static collar, spider, grille, nacelle and pylons are in the sector architecture.
- `Gill_Pivot_00..15` (8 columns x 2 outer lanes): `rotation_axis_angle[0]`, axis in the pivot's `hinge_axis_blender` extra (tangent to the arch along the plate front edge), 0 to +35 degrees. Dark glow cavities and teal slits under them are static.

Every pivot also has a baked action `Breath_Cycle_<pivot>` (sampled, cyclic) so glTF can carry animation. `--export-anim` writes `exports/modules/Weather_Lung_Intake_Vanes_Animated.glb` (scene-sampled, frames 1..240). The static GLBs are always written in the closed rest pose with `ripple_lag` temporarily 0. Build flags added: `--export-anim`, `--only 01,05,strip` (render subset).

### Round 2 notes

- The hood now closes at the back: a quarter-ellipse rear scoop wall (graphite shell, stacked ivory\/pearl plates, teal seams) curves down from the shell's rear edge to the rim, with a circular throat opening. The turbine duct, collar, nacelle and both rotors sit recessed inside that opening (rotor axis normal to the wall, about 13 degrees below +X); nothing protrudes beyond the hood silhouette in the top view.
- Rim: the radial wing tiles are replaced by long low linear buildings in three stepped tiers (decks fall toward the basin), with tangential chevron ridge roofs, faceted block ends, window bands and teal strip lights. Gaps remain only at the exhaust clefts; the outer two tiers stop in the arch zone (150..210 degrees), where the scoop and legs take over.
- Legs are folded L plinths: a steep slab from each arch foot, then a long low slab along the rim with cross seams, inset panels, teal edge lines and a chevron ridge.
- Vane clearance at the 35 degree default is printed at build time as `CLEARANCE` (fin tip to shell underside about 370 m after the fin half-chord; no fin point enters the turbine duct envelope).
- `Camera_07_Breath` (front three-quarter, slightly above) shows vanes, gills and throat; the breath strip is rendered from it. `--anim-preview` renders `previews/breath_cycle.mp4` (48 frames across one cycle, 12 fps, 720x500, H.264, Cycles with denoise).

## Round 3: water-cycle machine

Function: the basin is a sealed, open-topped body of water. Heat (sun by day, floor heat-exchanger rings by night) boils it to vapor; the arch pulls the vapor up and out over the crest.

- Watertight basin: the old crescent gap (310 to 340 degrees) is closed by a massive dam (crest road, spillway gate bays with piers, downstream buttresses, teal lines). Foundations, the continuous inner wall and the dam go down to a sealed floor at z = -800 m (previously -90 m), so architecture bounds now reach about -870. A stepped liner climbs the inner wall to the waterline (-135 m). Exhaust clefts are now raised troughs (floors about 240 m, above the waterline) with side walls; the inner wall below them is continuous. Sluice mouths remain as inflow, with preview-only waterfall ribbons falling from their spill lips. Service stalks run to the new floor.
- Water: `Basin_Water_Surface` lives in collection `05_WATER_Export` and exports as `exports/Weather_Lung_Water_Surface.glb` (transmission 0.93, IOR 1.33, so the floor and rings show through). The old preview-only basin water is gone; the preview ground plane is now a ring with a hole over the basin.
- Day: 8 solar concentrator arrays (3 heliostat panels each, `Heliostat_Pivot_<array>_<k>`, axis-angle) replace the condenser fields. At `sun_available` 1 each panel aims at a hot spot on the water; at 0 it stows face-down (smoothstep crossfade). Preview-only beams and hot-spot discs fade with `sun_available`.
- Night: six concentric heat-exchanger rings plus spokes on the floor (`Heat_Exchanger_Glow`); emission = (1 - sun_available) x breath level (inhale hotter), driven through a material driver.
- Vapor mover: an annular nozzle slit round the hood's inner front lip (12 segments `Nozzle_segment_XX`, each material `Nozzle_Glow_XX` has a phase-lagged driver, so two teal pulses race round on every exhale), two counter-rotating guide-vane rings `Guide_Ring_A/B` (1 revolution per cycle, axis = arch-plane normal), and the compressor rotors recessed behind a dense grille. The rear scoop is slimmer (depth tapers with sin^2.2 across the hood) so it reads less like a dome.
- Breath remap: inhale = heaters ramp, vanes open, compressor spools up; exhale = nozzle fires (pulses plus emission peak), gills flare, turbine coasts.
- Preview-only steam: noisy Principled-volume blobs in `90_PREVIEW_ONLY` boil off the basin and pour over the crest; enabled for the stills, never exported.

### Streaming prep

Every exported object carries custom prop `detail_class` (structure, dressing, moving, water); arch pieces also carry `chunk` (Hood_Shell, Nozzle_Guide, Throat_Compressor, Vane_Bank, Gills). Outputs:

- `exports/sectors/Weather_Lung_Sector_XX_Structure.glb` and `..._Dressing.glb` (same sector origin as the combined `Weather_Lung_Sector_XX.glb`; they exclude arch-chunk pieces).
- `exports/chunks/Weather_Lung_Chunk_<Chunk>.glb`: static pieces batched per class plus moving meshes and pivots, local to the vane-module origin.
- `exports/Weather_Lung_Water_Surface.glb`, the vane/moving module (now also holding guide rings and heliostats), the assembled GLB (everything) and the far proxy (foundations, hood shell, scoop, crown, dam, buttress and rim bases, island cores; no dressing).
- Suggested visibility ranges (also in the manifest): far proxy over 30 km, structure 0 to 30 km, dressing 0 to 2 km (fade 250 m), moving 0 to 8 km, water 0 to 30 km. The manifest records per sector and per chunk triangle counts by class.
- Rams are classed dressing even though they live in the moving module.

New build flags: `--anim-preview` (video), `--only 08,09,strip` (render subset). Stills 08_Day_Solar and 09_Night_Grid render from `Camera_08_Basin_Overview`.
