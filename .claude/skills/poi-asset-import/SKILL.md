---
name: poi-asset-import
description: Bring a GLB into Ringworld as a POI (point of interest) or update an existing one - correct .glb.import settings, merge/build pipeline, conversion to the shared ring_poi_architecture shader, collision, streaming registration (RingPoiHaze, PoiBaseAmbientOcclusion, large-shadow extents) and verification that it gets regular haze, cloud shadows and lighting. Use for anything under assets/poi/.
---

# POI asset import

`assets/poi/README.md` is the detailed reference. **Read it first.** This skill is the order of operations plus the traps.

## 0. Layout

- `assets/poi/<poi>/`:
  - source GLBs
  - `<Name>POI.tscn` wrapper
  - `merged/` (generated `*Merged.tscn`, `meshes/*.res`, `materials/*.tres`)
  - `collision/`
- Weather Lung is different: `weather_lung/source/{sectors,chunks,modules}` gives `built/`, plus `WeatherLungPOI.tscn` / `WeatherLungDetail.tscn`.
- Editable sources (.blend) live in `prototypes/Ringworld_*`, which Godot ignores via `.gdignore`.
- Axes: Blender (X,Y,Z) becomes Godot (X,Z,-Y). Metres, scale 1.

## 1. Import settings: set them BEFORE the first import

Godot writes a new `.glb.import` with bad defaults: empty root type, light baking on, animation on. A GLB copied into an open editor imports immediately with those defaults. So close the editor first, or fix the file and reimport.

Every POI `.glb.import` uses these identical `[params]`; copy them from `assets/poi/beam_emitter/Surface_Base_Facility_Godot.glb.import`:

```
nodes/root_type="Node3D"  nodes/apply_root_scale=true  nodes/root_scale=1.0
meshes/ensure_tangents=true  meshes/generate_lods=true  meshes/create_shadow_meshes=true
meshes/light_baking=0  animation/import=false  materials/extract=0
gltf/embedded_image_handling=1  gltf/naming_version=2
array_mesh/deduplicate_surfaces=true  nodes/use_name_suffixes=true  _subresources={}
```

- Collision is **not** done through import subresources (see step 3).
- `.glb.import` files showing `M` in git with no content diff are line-ending phantoms (autocrlf). Ignore them and keep them out of commits.

## 2. Regenerate (headless, in order)

```powershell
./tools/use_godot_48.ps1 -Build
./tools/use_godot_48.ps1 --headless --import
./tools/use_godot_48.ps1 --headless --script res://tools/poi/merge_poi_scenes.gd -- <emitter|reactor|main_spire|main_reactor|all> [sectors=N]
./tools/use_godot_48.ps1 --headless --script res://tools/poi/build_poi_scenes.gd -- <same>
./tools/use_godot_48.ps1 --headless --script res://tools/poi/verify_emitter_collision.gd [-- main_spire]
# Weather Lung instead: tools/poi/build_weather_lung.gd, then verify_weather_lung.gd (WEATHER_LUNG_VERIFY=PASS; -- rig gives WEATHER_LUNG_RIG=PASS)
```

- A merge deletes `merged/meshes/*.res`, including the far proxy, so always re-run build afterwards.
- After `--import`, check that `addons/terrain_3d/bin/~libterrain.ringworld.windows.debug.x86_64.dll` still exists. If not, restore it: `git show HEAD:<path> > <path>`.
- Inspection helpers: `tools/poi/inspect_glb.gd`, `inspect_materials.gd`.
- Follow the README's draw-call merge rules and collision scope.

## 3. Materials: the trap that breaks haze, shadows and lighting

- POIs get haze, cloud shadows and POI lighting **only** if their material is a ShaderMaterial using exactly `res://materials/ring_poi_architecture.gdshader` (cull back) or `ring_poi_architecture_double_sided.gdshader`.
  - `PoiMaterialParams.IsPoiShader` matches by **resource path**.
  - Any leftover StandardMaterial3D or copied shader is silently skipped: no haze, no cloud shadow.
- The merge and build tools convert materials through `tools/poi/poi_material_util.gd` `to_shader_material`. Only 6 parameters survive: albedo_color, metallic, roughness, specular, emission_color, emission_energy.
  - `unsupported_reason` rejects transparency, textures, normal/AO/rim/clearcoat, vertex-colour albedo, UV transforms, non-Burley/GGX shading and odd cull modes.
  - **Fix the source asset**, or extend the shared shader for every POI. Never fork a per-POI shader.
- Emissives are boosted above the glow threshold (`EMISSION_GLOW_TARGET` in `merge_poi_scenes.gd`).
- Do not hand-edit the generated `.tres` files; regenerate them.
- `tools/poi/convert_poi_materials.gd` converts in place and asserts an expected count (`EXPECTED`). Update the count when the set changes.
- No per-POI haze or shadow toggles (AGENTS.md); POIs follow the global systems.

## 4. Streaming and registration

- **Emitters:** `scripts/poi/Emitters/EmitterPoiStreamer.cs`.
  - Scene paths are constants near the top. There are 2 regular slots and 1 main slot, instantiated once.
  - On instantiate it calls `RingPoiHaze.RegisterTree(root, circumference/τ)` and `PoiBaseAmbientOcclusion.Apply(root, root, MeasureHeight(root))`. It ticks `RingPoiHaze.Tick(...)` every frame.
  - The bores (`EmitterBoreLayer.cs`) do the same.
- **Weather Lung:** `WeatherLungStreamer.cs` does the same, and also registers `RingLargePoiShadowExtents` while detail is mounted, so the sun shadow map reaches far enough.
  A big new POI needs that too.
- **A new POI type:** follow the streamer pattern.
  - Threaded load and instantiate once.
  - Register haze and AO on instantiate.
  - Expose `AutoBind`/`LoadSavedDefaults` for test fixtures.
  - Put genuine tunables (distances, budgets) in the grave menu via the `live-tuning-setting` skill.

## 5. Verify (required)

- Headless: `tools/poi_haze_registry_test.gd`. Every POI material registered.
- Collision: `verify_emitter_collision.gd` / `verify_weather_lung.gd`.
- GPU: `tools/poi_haze_gpu_test.gd` (haze identity tolerance 2/255). Capture A/B flags: `--poi-haze=on|off --haze-model=legacy|physical --poi-cloud-shadow=on|off`.
- Visual: view the POI at noon, dusk and long range next to the terrain via `shadow_harness_common` poses (`emitter_rim`, `emitter_dusk_*`, `wl_*`) or `scenes/poi/EmitterReactorTest.tscn` / `WeatherLungTest.tscn`.
  The POI should fade into haze and take cloud shadows exactly like the ground around it. Use the `visual-golden-check` skill for side-by-sides.
- Note: `docs/emitter-poi-streaming-spec.md` U4 (StandardMaterial3D + fog only) is superseded by the README's ShaderMaterial section.
