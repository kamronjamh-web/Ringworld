extends "res://tools/shadow_compose_identity_gpu_test.gd"

## Storm light (docs/storm-revamp-s0-storm-light-design.md WO-3): synthetic pixel test for the per-location storm level D read by every receiver.
## Reuses the shadow-compose identity scene (a shadowed StarLight + a box casting onto the receiver, a planet directional light, an omni) and replaces
## the producer with a synthetic 16x16 macro map published through the same globals (layers 0: storm 0.2, 1: storm 1.0, 2: storm 1.0, 3: storm 0; storm-light
## layers (owned, front rain) 4: (1, 0), 5: (0, 0), 6: (0, 1); ringworld_storm_map_frame.w picks the storm-light layer, 0 = none)
## RingSunshineCloudBridge.StormLight.cs writes. Per receiver material:
##   unchanged   D = 0.2 (below the fade start 0.35) with the darkness strength at 0: identical to the pre-storm image (max channel diff 0)
##   gated       the map holds D = 1 but ringworld_storm_light.w (valid) = 0: identical to the pre-storm image (max channel diff 0)
##   no_sun      D = 1, strength 1: sun visibility 1 - strength = 0, so the image equals the same storm with the sun and planet lights switched off (max diff 1/255),
##               and lit and shadowed pixels agree: removing the shadow-casting box changes nothing (max diff 1/255)
##   unowned     storm 1.0 but owned 0, no front rain (a non-storm system): D = 0, identical to the pre-storm image (max channel diff 0)
##   front_rain  storm 0, front rain 1 (a raining non-storm front): D = 1, identical to the owned storm image (max diff 1/255)
##   legacy      storm 1.0 with no storm-light layer (index 0): the old D = max(storm, heavy) = 1, identical to the owned storm image (max diff 1/255)
##   ambient     D = 1, strength 1, ambient only (all lights off): the mean luma falls to at most AMBIENT_RATIO_MAX of the clear-air ambient image
##               (AO = A_min = 0.45 against a clear AO of 0.28 .. 1), and a full-strength storm image is darker than a half-strength one
##   godot --path . --script res://tools/storm_light_gpu_test.gd
## Requires a real Vulkan/Forward+ renderer (do not pass --headless). Output stays under .godot/.

const AMBIENT_RATIO_MAX := 0.8
# Materials without a custom light(): built-in lighting, so the storm reaches them only through AO (AO_LIGHT_AFFECT 1 dims ambient and direct together by A(D)),
# and scale_anchor is dominated by its emission. They are exempt from the no-sun / no-shadow cases; emitter_standin still has to pass the ambient case.
const NO_CUSTOM_LIGHT := ["emitter_standin", "scale_anchor"]
const EMISSION_DOMINATED := ["scale_anchor"]
const MAP_SIZE := 16
const STORM_OUT := "res://.godot/captures/storm_light/storm_light_gpu_test"

var _map_rd: Texture2DArrayRD
var _map_rid := RID()
var _results: Array = []


func _run() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("requires Vulkan/Forward+ (do not run headless)")
		return
	for name in ["ringworld_hub_light_weight", "ringworld_shadow_debug_mode", "ringworld_storm_map", "ringworld_storm_light", "ringworld_storm_frame", "ringworld_storm_map_frame", "ringworld_storm_map_size"]:
		_globals_present[name] = ProjectSettings.has_setting("shader_globals/" + name)
		if not _globals_present[name]:
			_fail("global %s is not defined in project.godot" % name)
			return
	_build_scene()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(STORM_OUT))
	await _setup_storm_map()
	if failed:
		return
	var box: MeshInstance3D
	for child in _viewport.get_children():
		if child is MeshInstance3D and child.mesh is BoxMesh:
			box = child
	box.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_SHADOWS_ONLY
	var violations := 0
	var poi_entry := {"name": "poi_architecture", "shader": "res://materials/ring_poi_architecture.gdshader", "receiver": "sphere", "params": {}}
	for entry in MATERIALS + [poi_entry]:
		var material := _make_material(entry)
		if failed:
			return
		if entry.name == "standing_grass":
			material.set_shader_parameter("grass_primary_sun_gate_enabled", true)
			material.set_shader_parameter("grass_primary_sun_direction", SUN_DIR.normalized())
		_show_receiver(entry, material)
		_apply_variant(material, 0.0, 0.0)
		RenderingServer.global_shader_parameter_set("ringworld_shadow_debug_mode", 0)
		# clear-air reference: the storm map is off
		_publish(0.0, 0.0, 0.0, false)
		var base := await _grab()
		# unchanged: D = 0.2 below the fade start, strength 0
		_publish(0.2, 0.0, 0, true)
		var below := await _grab()
		var r_unchanged := _diff(below, base)
		# gated: the map says D = 1 but valid = 0
		_publish(1.0, 0.0, 1, false)
		var gated := await _grab()
		var r_gated := _diff(gated, base)
		# no_sun: D = 1, strength 1 (residual sun 1 - strength = 0) against the same storm with both directional lights off
		_publish(1.0, 1.0, 1, true)
		var storm := await _grab()
		_set_directional_lights(false)
		var sun_off := await _grab()
		_set_directional_lights(true)
		_publish(1.0, 1.0, 1, true)
		box.visible = false
		var no_box := await _grab()
		box.visible = true
		var r_sun := _diff(storm, sun_off)
		var r_shadow := _diff(storm, no_box)
		# storm ownership: a non-storm system's storm term is ignored, a raining front darkens through the front term, a map without the layer reads the old D
		_publish(1.0, 1.0, 1, true, 2, 5)
		var r_unowned := _diff(await _grab(), base)
		_publish(1.0, 1.0, 1, true, 3, 6)
		var r_front := _diff(await _grab(), storm)
		_publish(1.0, 1.0, 1, true, 1, 0)
		var r_legacy := _diff(await _grab(), storm)
		# ambient: all lights off
		_set_directional_lights(false)
		_omni.light_energy = 0.0
		_publish(0.0, 0.0, 0.0, false)
		var clear_ambient := await _grab()
		_publish(1.0, 1.0, 1, true)
		var storm_ambient := await _grab()
		_publish(1.0, 0.5, 1, true)
		var half_ambient := await _grab()
		_set_directional_lights(true)
		_omni.light_energy = 2.0
		clear_ambient.save_png(ProjectSettings.globalize_path(STORM_OUT.path_join("%s_ambient_clear.png" % entry.name)))
		storm_ambient.save_png(ProjectSettings.globalize_path(STORM_OUT.path_join("%s_ambient_storm.png" % entry.name)))
		var luma_clear := _mean_luma(clear_ambient)
		var luma_storm := _mean_luma(storm_ambient)
		var luma_half := _mean_luma(half_ambient)
		var ratio := luma_storm / maxf(luma_clear, 0.0001)
		var ok_unchanged: bool = int(r_unchanged.max_diff) == 0
		var ok_gated: bool = int(r_gated.max_diff) == 0
		var ok_owner: bool = int(r_unowned.max_diff) == 0 and int(r_front.max_diff) <= 1 and int(r_legacy.max_diff) <= 1
		var custom_light: bool = not NO_CUSTOM_LIGHT.has(entry.name)
		var ok_sun: bool = not custom_light or int(r_sun.max_diff) <= 1
		var ok_shadow: bool = not custom_light or int(r_shadow.max_diff) <= 1
		var ok_ambient: bool = EMISSION_DOMINATED.has(entry.name) or (ratio <= AMBIENT_RATIO_MAX and luma_storm < luma_half and luma_half < luma_clear + 0.0001)
		var line := "%s unchanged=%d gated=%d unowned=%d front_rain=%d legacy=%d no_sun=%d(%dpx) no_shadow=%d(%dpx) ambient_ratio=%.3f half_ratio=%.3f" % [entry.name,
			r_unchanged.max_diff, r_gated.max_diff, r_unowned.max_diff, r_front.max_diff, r_legacy.max_diff,
			r_sun.max_diff, r_sun.pixels, r_shadow.max_diff, r_shadow.pixels, ratio, luma_half / maxf(luma_clear, 0.0001)]
		var verdict := "PASS" if ok_unchanged and ok_gated and ok_owner and ok_sun and ok_shadow and ok_ambient else "FAIL"
		if verdict == "FAIL":
			violations += 1
			storm.save_png(ProjectSettings.globalize_path(STORM_OUT.path_join("%s_storm.png" % entry.name)))
			sun_off.save_png(ProjectSettings.globalize_path(STORM_OUT.path_join("%s_sun_off.png" % entry.name)))
			base.save_png(ProjectSettings.globalize_path(STORM_OUT.path_join("%s_clear.png" % entry.name)))
		_results.append("%s %s" % [verdict, line])
		if failed:
			return
	for line in _results:
		print("STORM_LIGHT_GPU ", line)
	_publish(0.0, 0.0, 0.0, false)
	if violations > 0:
		_fail("%d material(s) violated the storm light contract" % violations)
		return
	print("STORM_LIGHT_GPU=PASS materials=%d" % (MATERIALS.size() + 1))
	_finish(0)


## Publishes the synthetic globals: layer selects the storm term (0 -> 0.2, 1 -> 1.0 is chosen by `level`; `front_layer` >= 0 overrides it), `light_layer` the
## storm-light layer (4 = owned, no front rain), strength is the darkness strength.
func _publish(level: float, strength: float, _unused, valid: bool, front_layer := -1, light_layer := 4) -> void:
	var layer := front_layer if front_layer >= 0 else (0 if level < 0.5 else 1)
	RenderingServer.global_shader_parameter_set("ringworld_storm_map_frame", Vector4(1000.0, float(layer), 0.6, float(light_layer)))
	RenderingServer.global_shader_parameter_set("ringworld_storm_map_size", Vector4(MAP_SIZE, MAP_SIZE, MAP_SIZE, MAP_SIZE))
	# ring radius 0: planar frame, camera at map metre (8000, 8000) so every receiver position clamps into the 16 km map
	RenderingServer.global_shader_parameter_set("ringworld_storm_frame", Projection(Vector4(1, 0, 0, 8000.0), Vector4(0, 0, 1, 8000.0), Vector4(0, 1, 0, 0.0), Vector4(0, 0, 0, 0)))
	RenderingServer.global_shader_parameter_set("ringworld_storm_light", Vector4(strength, 0.35, 0.70, 1.0 if valid else 0.0))


func _set_directional_lights(on: bool) -> void:
	_sun.light_energy = 1.0 if on else 0.0
	_planet.light_energy = 0.4 if on else 0.0


func _mean_luma(image: Image) -> float:
	var data := image.get_data()
	var sum := 0.0
	for i in range(0, data.size(), 4):
		sum += 0.2126 * data[i] + 0.7152 * data[i + 1] + 0.0722 * data[i + 2]
	return sum / float(data.size() / 4) / 255.0


func _setup_storm_map() -> void:
	_map_rd = Texture2DArrayRD.new()
	RenderingServer.global_shader_parameter_set("ringworld_storm_map", _map_rd)
	var done := [false]
	var rid_out := [RID()]
	RenderingServer.call_on_render_thread(func() -> void:
		var rd := RenderingServer.get_rendering_device()
		var format := RDTextureFormat.new()
		format.texture_type = RenderingDevice.TEXTURE_TYPE_2D_ARRAY
		format.format = RenderingDevice.DATA_FORMAT_R16G16B16A16_SFLOAT
		format.width = MAP_SIZE
		format.height = MAP_SIZE
		# (r, g) per layer: 0-3 (coverage 0 = heavy term off, storm), 4-6 storm-light (owned, front rain); b, a = 0 (no influence)
		var contents := [[0.0, 0.2], [0.0, 1.0], [0.0, 1.0], [0.0, 0.0], [1.0, 0.0], [0.0, 0.0], [0.0, 1.0]]
		format.array_layers = contents.size()
		format.usage_bits = RenderingDevice.TEXTURE_USAGE_SAMPLING_BIT | RenderingDevice.TEXTURE_USAGE_CAN_UPDATE_BIT
		var layers: Array[PackedByteArray] = []
		for content in contents:
			var bytes := PackedByteArray()
			bytes.resize(MAP_SIZE * MAP_SIZE * 8)
			for texel in MAP_SIZE * MAP_SIZE:
				bytes.encode_half(texel * 8 + 0, content[0])
				bytes.encode_half(texel * 8 + 2, content[1])
				bytes.encode_half(texel * 8 + 4, 0.0)
				bytes.encode_half(texel * 8 + 6, 0.0)
			layers.append(bytes)
		rid_out[0] = rd.texture_create(format, RDTextureView.new(), layers)
		_map_rd.texture_rd_rid = rid_out[0]
		done[0] = true)
	for frame in 30:
		await RenderingServer.frame_post_draw
		if done[0]:
			break
	if not done[0] or not (rid_out[0] as RID).is_valid():
		_fail("could not create the synthetic storm map texture")
		return
	_map_rid = rid_out[0]


func _fail(message: String) -> void:
	if failed:
		return
	failed = true
	for line in _results:
		print("STORM_LIGHT_GPU ", line)
	push_error("STORM_LIGHT_GPU=FAIL " + message)
	if is_instance_valid(_viewport):
		_viewport.queue_free()
	call_deferred("quit", 1)
