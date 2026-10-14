# Ringworld — Surface Beam Spire and Base Facility

An original editable alien-megastructure blockout, authored in Blender 4.3.2. The eight-arm surface spire now has a genuine hollow facility around its base: an entry, a complete circulation loop, three interior rooms, and an elevated observation gallery overlooking the beam shaft. Dark faceted graphite/ceramic forms and restrained recessed teal inlays match the earlier underground reactor.

This is a design and scale blockout, with real interior geometry and tested geometric passage routes. It is not a production-ready level, and no collision, navigation, gameplay, final VFX, LODs, or target-device performance testing is included.

## Start here

- Open **`Surface_Spire_with_Facility.blend`** for the complete editable design.
- Import **`Surface_Spire_and_Facility_Godot.glb`** for the assembled machine and facility.
- Use **`Surface_Base_Facility_Godot.glb`** to place the facility separately, or **`Surface_Beam_Spire_Godot.glb`** for the approved spire alone. All three share the same ground-center pivot and scale. Do not instantiate the combined export and both separate exports on top of one another.
- The earlier **`Surface_Beam_Spire.blend`** is included as the unchanged base-scene input for the facility builder.

The standalone spire geometry and its GLB were preserved when adding the facility.

## Dimensions and coordinates

One Blender unit is one meter. The **origin is the surface ground center**, not the reactor core or an individual room.

| Element | Dimensions / level |
|---|---|
| Spire top | Blender Z +1,800 m |
| Main arm count | 8 |
| Arm foot centers | 250 m radius; exactly 45° apart |
| Spire-only AABB | 616 × 616 × 1,800 m, X/Y/Z |
| Continuous reserved beam bore | 150 m clear diameter |
| Ground ring corridor | R150–162 m; floor Z0; 10 m clear ceiling |
| Main entry portal | 8 m wide × 8 m high; axis 292.5° |
| Three room interiors | 30 m tangential × 32 m radial × 12 m clear high |
| Room axes | 22.5°, 112.5°, 202.5° |
| Observation gallery | Floor Z16 m; clear ceiling Z26 m; roof maximum Z30 m |
| Gallery floor extent | R99.8–124 m; outer wall begins at R122 m |
| Gallery approach ramp | 12 m wide, R130–142; 16 m rise over 160.22 m centerline run, approximately 1:10 |
| Combined slab bottom | Z−1 m, below the ground datum |
| Combined AABB | 616 × 616 × 1,801 m, X/Y/Z; bottom −1 m, top +1,800 m |
| Human / cube references | 1.8 m figures and 2 m cube, presentation only |

The foot-center circle is 500 m across; the wider 616 m bounds include the full anchoring shoes. The full structure is still **1.8 km above ground**; the extra meter in the combined AABB is the sub-grade floor slab.

The gallery is deliberately raised over the existing 14 m-high ground-throat rim. At player eye height it looks through open framed windows toward the beam, rather than into the rim's solid side. Its inner parapet is a geometric safety cue; it is not a tested gameplay barrier.

Blender Z-up exports automatically to glTF/Godot Y-up: Blender `(X, Y, Z)` becomes Godot `(X, Z, -Y)`. Keep imported instance scale `(1, 1, 1)`. In Godot, the ground floor is Y0, gallery floor Y16, spire tips Y1800, and the floor slab bottom Y−1.

## Interior circulation

All interior spaces are hollow. Floors, walls, piers, lintels and ceilings are separate meshes, with actual open doorways.

1. Enter along 292.5° from roughly R230 toward R156, at Z0.
2. Walk around the R156 ground-floor ring. Its three outward room branches lie at 22.5°, 112.5° and 202.5°. Each branch leads through an 8 m opening into a room.
3. To reach the observation gallery, leave the ground ring inward at 289.5°, reaching R136 at Z0.
4. Turn onto the curved ramp at 292.5°. Follow R136 counterclockwise to 360°, reaching Z16.
5. Turn across the upper landing toward 363° and go inward to R112. This joins the complete elevated gallery loop.

The rooms contain editable low control plinths, power/recessed wall elements and broad unobstructed center routes. They are named Command, Resonance and Maintenance as organizational labels, not implemented gameplay systems.

`facility_geometry_manifest.json` contains the dimensions, original source hashes and exact route coordinates. `facility_route_samples.json` supplies dense floor-level XYZ samples. Add 1.7 m to floor Z for a typical eye-level camera.

## Collections and removable roofs

- `00_SURFACE_SPIRE_MACHINE_EXPORT`: the unchanged eight-arm spire, feet, crown, inlays and ground throat.
- `10_FACILITY_ARCHITECTURE_EXPORT`: room/ring/gallery floors, walls, piers, open door frames, ramp and parapets.
- `11_FACILITY_REMOVABLE_ROOF_EXPORT`: every facility ceiling and faceted outer roof/hull. Hide this collection for the architectural cutaway.
- `12_FACILITY_INTERIOR_FIXTURES_EXPORT`: editable control surfaces, guidance lights, wall recesses, seams and other interior details.
- `90_PRESENTATION_ONLY_DO_NOT_EXPORT`: simple reference ground, stones, people, cube and an open-ended shaft liner.
- `91_BEAM_PREVIEW_ONLY_DO_NOT_EXPORT`: emissive beam stand-in, hidden by default.
- `92_CAMERAS_LIGHTS`: presentation cameras and lighting.

The combined GLB exports collections 00, 10, 11 and 12. The facility-only GLB exports 10, 11 and 12, including its removable roof objects. Presentation objects, people, beam preview, ground, shaft liner, cameras and lights are excluded from all GLBs.

The cutaway preview hides the removable roofs and tall upper spire pieces so the rooms and passages are visible. The full model retains them. The gallery eye-level preview enables the separate emissive beam stand-in. It is a visual placeholder, not a finished animated beam effect.

## Underground alignment

The surface throat preserves the earlier reactor's nominal 150 m aperture. Align the reactor, future connecting shaft and this surface root on the same horizontal axis.

If the earlier cavern chamber is placed at Godot Y = −4,000 m, its modeled exhaust ends near local +1,039 m, or world Y ≈ −2,961 m. The roughly 2.96 km underground connecting shaft remains a separate terrain/architecture task. The surface exports do not contain that connection; the short open shaft liner in Blender is reference-only.

Cut the game terrain open on the central axis, with at least a 150 m-diameter bore and sufficient extra clearance for the throat lining. Do not leave an opaque terrain cap beneath the beam. The final previews use a dark downward background to make the genuinely empty bore legible.

## Import and geometric verification

The original spire was clean-imported and instantiated in Godot 4.6.3: 60 mesh instances, 108 material surfaces, 5,712 triangles, no reported import warnings/errors.

The combined export contains 937 editable mesh objects and 37,580 triangles; the facility-only export contains 877 objects and 31,868 triangles. This intentionally preserves fine-grained editing. Merge, batch or instance detail objects for production rendering as appropriate to the target hardware.

An independent Blender geometry audit checks every exported triangle against the vertical beam cylinder. The minimum projected surface radius is **75.039949 m**, leaving the entire reserved radius-75 m cylinder unobstructed. It checks faces and edges, not only vertex positions.

All **1,385 sampled positions across ten route segments/loops** passed geometric checks for floor beneath the route, an unobstructed 1.85 m standing-height ray, and 0.35 m body-radius clearance. These checks confirm the modeled connections and do not replace player-controller, collision, navigation or accessibility testing. Exact results are in `facility_validation.json`.

Both the combined and facility-only exports also passed a clean Godot 4.6.3 import and scene instantiation without warnings/errors. The combined export has 1,220 material surfaces; the facility alone has 1,112. See `GODOT_IMPORT_VALIDATION.md` for the exact bounds and final import results.

## Rebuilding and rendering

No external textures, linked libraries or add-ons are required. Keep the companion scripts beside the supplied files. Back up manual edits before rebuilding.

```sh
# Recreate the standalone spire. This overwrites its source .blend and GLB.
blender -b -t 8 -P build_surface_spire.py -- neutral

# Add the hollow facility to that source without changing the original spire files.
blender -b -t 8 -P build_facility.py

# Validate the combined geometry and sampled routes.
blender -b -P verify_facility.py

# Add the presentation rig and render cutaway, eye-level and whole-structure previews.
blender -b -t 8 -P render_facility.py -- cutaway eye hero
```

`verify_geometry.py` checks the standalone spire. `render_previews.py` refreshes its separate presentation views. The `STATIONS` list in `build_surface_spire.py` controls the eight main arms. `build_facility.py` contains the room, ring, gallery and ramp dimensions.

Cycles denoising is disabled because the supplied Blender installation lacks OpenImageDenoise. The editable combined `.blend` is the authoritative artistic source; regeneration is optional.
