extends SceneTree

## WO-S0 flag-off compare. Captures the same pose with (A1, A2) the production proxy shaders as they are now
## (vertex-table include present, proxy_vertex_table_enabled false) and (B) an in-memory "before" variant
## of the same shaders with the include and the guarded vertex branch stripped. A1 against A2 is the cloud-noise
## floor; A1 against B is the flag-off effect. No shader or DLL file is swapped.
const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const PROBE := preload("res://scripts/Tools/TerrainLodRenderingProbe.cs")
const OUTPUTS := preload("res://tools/generated_output_paths.gd")
const SHADERS := [
	"res://materials/ring_terrain_proxy.gdshader",
	"res://materials/ring_terrain_proxy_shadow.gdshader",
	"res://materials/ring_ocean_proxy.gdshader",
]
const TIMEOUT := 2400

var _scene: Node
var _probe: RefCounted
var _fixture := {}
var _frame := 0
var _phase := "boot"
var _phase_frame := 0
var _capture_started := false
var _replacements: Array = []
var _variants := {}
var _paths := {}
var _replaced_count := 0

func _initialize() -> void:
	_boot.call_deferred()

func _boot() -> void:
	if RenderingServer.get_rendering_device() == null: _fail("Forward+ RenderingDevice required"); return
	for key in ["a1", "a2", "b", "d1", "d2"]:
		_paths[key] = OUTPUTS.smoke_capture_path("proxy_vertex_table_flag_off_%s.png" % key)
		if _paths[key].is_empty(): _fail("capture path allocation failed"); return
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
	_scene = TEST_SCENE.instantiate()
	var saves := _scene.get_node_or_null("WorldSaveManager")
	if saves != null: saves.free()
	var launcher := LAUNCHER.instantiate()
	root.add_child(launcher); launcher.call("_on_certified_preset_pressed"); launcher.call("_configure_game", _scene, 5000000.0); launcher.free()
	root.add_child(_scene)

func _process(_delta: float) -> bool:
	_frame += 1
	if _frame > TIMEOUT: return _fail("timed out in " + _phase)
	if _phase == "boot" and _frame > 12:
		_probe = PROBE.new(); _fixture = _probe.call("FindDryGentleGrassland", _scene)
		if not bool(_fixture.get("valid", false)): return _fail("bounded sampler found no dry gentle grassland")
		var explorer := _scene.get_node_or_null("Explorer")
		if explorer == null: return _fail("Explorer missing")
		if not _probe.call("PlaceCamera", _scene, _fixture): return _fail("could not orient grassland camera")
		explorer.set_physics_process(false); explorer.set_process_unhandled_input(false)
		_phase = "noon"; _set_noon.call_deferred()
	if _phase == "stream":
		if _frame < _phase_frame + 600 or _frame % 10 != 0: return false
		_freeze_scene_callbacks(); _disable_capture_variance(); _phase = "settle"; _phase_frame = _frame
	if _phase == "settle" and _frame >= _phase_frame + 20 and not _capture_started:
		_capture_started = true; call_deferred("_capture_sequence")
	if _phase == "done":
		var noise := _difference(_paths["a1"], _paths["a2"])
		var flag_off := _difference(_paths["a1"], _paths["b"])
		var debug_off := _difference(_paths["d1"], _paths["d2"])
		print("PROXY_VERTEX_TABLE_FLAG_OFF_DEBUG_COLOUR before_vs_after_mean_abs=%.5f before_vs_after_max=%.5f" % [debug_off.x, debug_off.y])
		print("PROXY_VERTEX_TABLE_FLAG_OFF_COMPARE=PASS variants=%d replacements=%d noise_mean_abs=%.5f noise_max=%.5f before_vs_after_mean_abs=%.5f before_vs_after_max=%.5f a1=%s a2=%s b=%s" % [
			_variants.size(), _replaced_count, noise.x, noise.y, flag_off.x, flag_off.y, _paths["a1"], _paths["a2"], _paths["b"]])
		quit(0); return true
	return false

# The default capture pose is night; scan the cycle for full daylight so the terrain is actually lit.
func _set_noon() -> void:
	var cycle := _scene.find_child("RingDayNightCycle", true, false)
	if cycle != null:
		var full: Array[float] = []
		for i in 100:
			var f := float(i) / 100.0
			cycle.call("set_cycle_fraction", f)
			await process_frame
			await process_frame
			if float(cycle.call("get_daylight_factor")) >= 0.999: full.append(f)
		var noon := 0.5 if full.is_empty() else full[full.size() / 2]
		cycle.call("set_cycle_fraction", noon)
		cycle.set("Paused", true)
		print("PROXY_VERTEX_TABLE_FLAG_OFF_NOON fraction=%.3f daylight=%.3f full_steps=%d" % [noon, float(cycle.call("get_daylight_factor")), full.size()])
	_phase = "stream"; _phase_frame = _frame

func _is_proxy_material(material: Material) -> bool:
	var shader_material := material as ShaderMaterial
	return shader_material != null and shader_material.shader != null and SHADERS.has(shader_material.shader.resource_path)

func _before_shader(path: String) -> Shader:
	if _variants.has(path): return _variants[path]
	var code := (load(path) as Shader).code.replace("\r\n", "\n")
	var include_line := "#include \"res://materials/ring_terrain_proxy_vertex_fetch.gdshaderinc\""
	if not code.contains(include_line): _fail("vertex fetch include missing from " + path); return null
	code = code.replace("#define PROXY_VERTEX_TABLE_WATER\n", "").replace(include_line + "\n", "")
	var lines := code.split("\n")
	var kept := PackedStringArray()
	for line in lines:
		var trimmed := line.strip_edges()
		if trimmed == "vec3 proxy_chord = CUSTOM0.xyz;" or trimmed == "vec3 proxy_chord = vec3(0.0);": continue
		if trimmed.begins_with("if (proxy_vertex_table_enabled)"): continue
		kept.append(line)
	code = "\n".join(kept).replace("proxy_chord * morph", "CUSTOM0.xyz * morph")
	if code.contains("proxy_"+"vertex_table") or code.contains("proxy_chord"): _fail("before variant still references the vertex table in " + path); return null
	var shader := Shader.new(); shader.code = code
	_variants[path] = shader
	return shader

func _install_before_variant() -> void:
	for node in _scene.find_children("*", "MeshInstance3D", true, false):
		var mesh := node as MeshInstance3D
		if mesh == null or mesh.mesh == null: continue
		var original_override := mesh.material_override as ShaderMaterial
		if _is_proxy_material(original_override):
			var override_variant := original_override.duplicate() as ShaderMaterial
			override_variant.shader = _before_shader(original_override.shader.resource_path)
			_replacements.append(["override", mesh, original_override]); mesh.material_override = override_variant
			continue
		for s in mesh.mesh.get_surface_count():
			var original := mesh.get_active_material(s) as ShaderMaterial
			if not _is_proxy_material(original): continue
			var variant := original.duplicate() as ShaderMaterial
			variant.shader = _before_shader(original.shader.resource_path)
			_replacements.append(["surface", mesh, s, mesh.get_surface_override_material(s)]); mesh.set_surface_override_material(s, variant)

func _set_terrain_debug(enabled: bool) -> void:
	var seen := {}
	for node in _scene.find_children("*", "MeshInstance3D", true, false):
		var mesh := node as MeshInstance3D
		if mesh == null or mesh.mesh == null: continue
		var materials: Array = [mesh.material_override]
		for s in mesh.mesh.get_surface_count(): materials.append(mesh.get_active_material(s))
		for material in materials:
			var shader_material := material as ShaderMaterial
			if shader_material == null or shader_material.shader == null or seen.has(shader_material): continue
			if shader_material.shader.resource_path != SHADERS[0]: continue
			seen[shader_material] = true
			shader_material.set_shader_parameter("debug_color_enabled", enabled)

func _restore_materials() -> void:
	for entry in _replacements:
		if entry[0] == "override": entry[1].material_override = entry[2]
		else: entry[1].set_surface_override_material(entry[2], entry[3])
	_replacements.clear()

func _disable_capture_variance() -> void:
	for environment in _scene.find_children("*", "WorldEnvironment", true, false):
		# Compositor effects stay on: with them off the scene is nearly black and the compare proves nothing.
		if environment.camera_attributes is CameraAttributesPractical: environment.camera_attributes.auto_exposure_enabled = false
	for camera in _scene.find_children("*", "Camera3D", true, false):
		if camera.attributes is CameraAttributesPractical: camera.attributes.auto_exposure_enabled = false
	for particles in _scene.find_children("*", "GPUParticles3D", true, false): particles.visible = false

func _freeze_scene_callbacks() -> void:
	for node in _scene.find_children("*", "Node", true, false): node.set_process(false); node.set_physics_process(false)

func _capture_sequence() -> void:
	await RenderingServer.frame_post_draw
	if not _capture(_paths["a1"]): return
	for i in 20: await process_frame
	await RenderingServer.frame_post_draw
	if not _capture(_paths["a2"]): return
	_install_before_variant()
	if _replacements.is_empty(): _fail("no proxy material replacement installed"); return
	_replaced_count = _replacements.size()
	for i in 20: await process_frame
	await RenderingServer.frame_post_draw
	if not _capture(_paths["b"]): return
	_restore_materials()
	# The natural-light pose is dark on terrain, so repeat with the terrain debug colour (bright, flat colour through the
	# same vertex stage) to make geometry and silhouette differences visible in the compare.
	_set_terrain_debug(true)
	for i in 20: await process_frame
	await RenderingServer.frame_post_draw
	if not _capture(_paths["d1"]): return
	_install_before_variant()
	for i in 20: await process_frame
	await RenderingServer.frame_post_draw
	if not _capture(_paths["d2"]): return
	_restore_materials()
	_set_terrain_debug(false)
	_phase = "done"

func _capture(path: String) -> bool:
	var image := root.get_texture().get_image()
	if image == null or image.is_empty() or image.save_png(path) != OK:
		_fail("capture failed: " + path)
		return false
	return true

func _difference(a_path: String, b_path: String) -> Vector2:
	var a := Image.load_from_file(a_path); var b := Image.load_from_file(b_path)
	if a == null or b == null or a.get_size() != b.get_size(): return Vector2(INF, INF)
	var total := 0.0; var count := 0; var largest := 0.0
	for y in range(0, a.get_height(), 2): for x in range(0, a.get_width(), 2):
		var left := a.get_pixel(x, y); var right := b.get_pixel(x, y)
		var d := absf(left.r - right.r) + absf(left.g - right.g) + absf(left.b - right.b)
		total += d; count += 1; largest = maxf(largest, d)
	return Vector2(total / max(1, count) * 255.0, largest * 255.0)

func _fail(reason: String) -> bool:
	_restore_materials(); push_error("PROXY_VERTEX_TABLE_FLAG_OFF_COMPARE=FAIL " + reason); quit(1); return true
