extends SceneTree

## Rendered regression fixture for the production proxy-terrain material. It
## uses an in-memory shader variant only; assets and user saves are untouched.
const TEST_SCENE := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const PROBE := preload("res://scripts/Tools/TerrainLodRenderingProbe.cs")
const OUTPUTS := preload("res://tools/generated_output_paths.gd")
const PROXY_SHADER := "res://materials/ring_terrain_proxy.gdshader"
const FIXED := "proxy_terrain_normals_fixed.png"
const LEGACY := "proxy_terrain_normals_legacy_inverted.png"
const RESTORED := "proxy_terrain_normals_restored.png"
const TIMEOUT := 2400

var _scene: Node
var _probe: RefCounted
var _fixture := {}
var _frame := 0
var _phase := "boot"
var _phase_frame := 0
var _fixed_path := ""
var _legacy_path := ""
var _restored_path := ""
var _replacements: Array = []
var _fixture_shader: Shader
var _capture_started := false
var _legacy_difference := 0.0
var _restore_difference := 0.0
var _placement_frame := -1

func _initialize() -> void:
	_boot.call_deferred()

func _boot() -> void:
	if RenderingServer.get_rendering_device() == null: _fail("Forward+ RenderingDevice required"); return
	_fixed_path = OUTPUTS.smoke_capture_path(FIXED)
	_legacy_path = OUTPUTS.smoke_capture_path(LEGACY)
	_restored_path = OUTPUTS.smoke_capture_path(RESTORED)
	if _fixed_path.is_empty() or _legacy_path.is_empty() or _restored_path.is_empty(): _fail("capture path allocation failed"); return
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
		_placement_frame = _frame; print("PROXY_TERRAIN_NORMALS_PLACED fixture=", _fixture, " camera=", explorer.global_position)
		_phase = "stream"; _phase_frame = _frame
	if _phase == "stream":
		if _frame < _placement_frame + 150 or _frame % 10 != 0: return false
		var status := _normal_status()
		if int(status.nearby_proxies) >= 3 and int(status.proxies) >= 3 and int(status.spacings) >= 3 and int(status.vertices) >= 60 and float(status.normal_fraction) > .92 and float(status.winding_fraction) > .92:
			print("PROXY_TERRAIN_NORMALS_READY fixture=", _fixture, " camera=", _scene.get_node("Explorer").global_position, " status=", status)
			_freeze_scene_callbacks(); _disable_capture_variance(); _phase = "settle"; _phase_frame = _frame
		elif _frame % 120 == 0: print("PROXY_TERRAIN_NORMALS_WAIT ", status)
	if _phase == "settle" and _frame >= _phase_frame + 20 and not _capture_started:
		_capture_started = true; call_deferred("_capture_sequence")
	if _phase == "done":
		if _legacy_difference < 5.0: return _fail("legacy inverted-normal variant did not alter lower-middle terrain ROI: %.3f" % _legacy_difference)
		if _restore_difference > 1.0: return _fail("restored proxy material differs from fixed capture: %.3f" % _restore_difference)
		print("PROXY_TERRAIN_NORMALS_GPU_TEST=PASS fixture=%s normals=%s legacy_roi=%.3f restored_roi=%.3f captures=[%s,%s,%s]" % [_fixture, _normal_status(), _legacy_difference, _restore_difference, _fixed_path, _legacy_path, _restored_path])
		quit(0); return true
	return false

func _normal_status() -> Dictionary:
	var proxy_ids := {}; var nearby_ids := {}; var vertices := 0; var inward := 0; var triangles := 0; var winding := 0; var spacings := {}
	var explorer := _scene.get_node_or_null("Explorer") as Node3D
	for node in _scene.find_children("*", "MeshInstance3D", true, false):
		var mesh := node as MeshInstance3D
		if mesh == null or mesh.mesh == null or not mesh.is_visible_in_tree(): continue
		var owner := _proxy_owner(mesh)
		if owner == null: continue
		for surface in mesh.mesh.get_surface_count():
			var material := _surface_proxy_material(mesh, surface)
			if material == null: continue
			proxy_ids[mesh.get_instance_id()] = true; spacings[int(owner.get("SampleSpacingMeters"))] = true
			if explorer != null and mesh.global_position.distance_to(explorer.global_position) <= 6000.0: nearby_ids[mesh.get_instance_id()] = true
			var ring_center := material.get_shader_parameter("ring_center") as Vector3
			var arrays := mesh.mesh.surface_get_arrays(surface); var vs: PackedVector3Array = arrays[Mesh.ARRAY_VERTEX]; var ns: PackedVector3Array = arrays[Mesh.ARRAY_NORMAL]; var uv: PackedVector2Array = arrays[Mesh.ARRAY_TEX_UV]; var ix: PackedInt32Array = arrays[Mesh.ARRAY_INDEX]
			for i in mini(vs.size(), ns.size()):
				if i >= uv.size() or uv[i].x < 0.0 or uv[i].y < 0.0: continue
				var world := mesh.global_transform * vs[i]; var relative := world - ring_center; var radial := Vector3(relative.x, 0, relative.z)
				if radial.length_squared() < .01: continue
				var normal := (mesh.global_transform.basis.inverse().transposed() * ns[i]).normalized()
				vertices += 1; if normal.dot(-radial.normalized()) > 0.0: inward += 1
			var examined := 0
			for i in range(0, ix.size() - 2, 3):
				if examined >= 192: break
				var a := ix[i]; var b := ix[i + 1]; var c := ix[i + 2]
				if a >= uv.size() or b >= uv.size() or c >= uv.size() or uv[a].x < 0 or uv[b].x < 0 or uv[c].x < 0: continue
				var wa := mesh.global_transform * vs[a]; var wb := mesh.global_transform * vs[b]; var wc := mesh.global_transform * vs[c]; var center := (wa + wb + wc) / 3.0 - ring_center; var radial := Vector3(center.x, 0, center.z)
				if radial.length_squared() < .01: continue
				examined += 1; triangles += 1; if ((wc - wa).cross(wb - wa)).dot(-radial.normalized()) > 0.0: winding += 1
	return {"proxies": proxy_ids.size(), "nearby_proxies": nearby_ids.size(), "spacings": spacings.size(), "vertices": vertices, "normal_fraction": float(inward) / max(1, vertices), "triangles": triangles, "winding_fraction": float(winding) / max(1, triangles)}

func _proxy_material(mesh: MeshInstance3D) -> ShaderMaterial:
	var override := mesh.material_override as ShaderMaterial
	if _is_biome_proxy_material(override): return override
	for s in mesh.mesh.get_surface_count():
		var material := mesh.get_active_material(s) as ShaderMaterial
		if _is_biome_proxy_material(material): return material
	return null

func _surface_proxy_material(mesh: MeshInstance3D, surface: int) -> ShaderMaterial:
	var override := mesh.material_override as ShaderMaterial
	return override if _is_biome_proxy_material(override) else (mesh.get_active_material(surface) as ShaderMaterial if _is_biome_proxy_material(mesh.get_active_material(surface) as ShaderMaterial) else null)

func _is_biome_proxy_material(material: ShaderMaterial) -> bool:
	return material != null and material.shader != null and material.shader.resource_path == PROXY_SHADER and bool(material.get_shader_parameter("biome_texture_weights_enabled"))

func _proxy_owner(mesh: MeshInstance3D) -> Node:
	var node: Node = mesh
	for depth in 6:
		node = node.get_parent()
		if node == null: return null
		if node.get("SampleSpacingMeters") != null: return node
	return null

func _install_legacy_variant() -> void:
	var source := load(PROXY_SHADER) as Shader
	var code := source.code
	var old := "vec3 world_normal = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);"
	if not code.contains(old): _fail("proxy shader normal conversion anchor changed"); return
	code = code.replace("uniform vec3 camera_world_position = vec3(0.0);", "uniform vec3 camera_world_position = vec3(0.0);\nuniform bool fixture_invert_terrain_normal = false;")
	code = code.replace(old, old + "\n\t\tif (fixture_invert_terrain_normal) world_normal = -world_normal;")
	_fixture_shader = Shader.new(); _fixture_shader.code = code
	for node in _scene.find_children("*", "MeshInstance3D", true, false):
		var mesh := node as MeshInstance3D
		if mesh == null or mesh.mesh == null: continue
		var original_override := mesh.material_override as ShaderMaterial
		if _is_biome_proxy_material(original_override):
			var override_variant := original_override.duplicate() as ShaderMaterial; override_variant.shader = _fixture_shader; override_variant.set_shader_parameter("fixture_invert_terrain_normal", true)
			_replacements.append(["override", mesh, original_override]); mesh.material_override = override_variant
			continue
		for s in mesh.mesh.get_surface_count():
			var original := mesh.get_active_material(s) as ShaderMaterial
			if not _is_biome_proxy_material(original): continue
			var variant := original.duplicate() as ShaderMaterial; variant.shader = _fixture_shader; variant.set_shader_parameter("fixture_invert_terrain_normal", true)
			_replacements.append(["surface", mesh, s, mesh.get_surface_override_material(s)]); mesh.set_surface_override_material(s, variant)

func _restore_materials() -> void:
	for entry in _replacements:
		if entry[0] == "override": entry[1].material_override = entry[2]
		else: entry[1].set_surface_override_material(entry[2], entry[3])
	_replacements.clear()

func _disable_capture_variance() -> void:
	for environment in _scene.find_children("*", "WorldEnvironment", true, false):
		if environment.compositor != null:
			for effect: CompositorEffect in environment.compositor.compositor_effects: effect.enabled = false
		if environment.camera_attributes is CameraAttributesPractical: environment.camera_attributes.auto_exposure_enabled = false
	for camera in _scene.find_children("*", "Camera3D", true, false):
		if camera.attributes is CameraAttributesPractical: camera.attributes.auto_exposure_enabled = false
	for particles in _scene.find_children("*", "GPUParticles3D", true, false): particles.visible = false

func _freeze_scene_callbacks() -> void:
	for node in _scene.find_children("*", "Node", true, false): node.set_process(false); node.set_physics_process(false)

func _capture_sequence() -> void:
	await RenderingServer.frame_post_draw
	if not _capture(_fixed_path): return
	_install_legacy_variant()
	if _replacements.is_empty(): _fail("no proxy material_override replacement installed"); return
	for i in 20: await process_frame
	await RenderingServer.frame_post_draw
	if not _capture(_legacy_path): return
	_restore_materials()
	for i in 20: await process_frame
	await RenderingServer.frame_post_draw
	if not _capture(_restored_path): return
	_legacy_difference = _roi_difference(_fixed_path, _legacy_path)
	_restore_difference = _roi_difference(_fixed_path, _restored_path)
	_phase = "done"

func _capture(path: String) -> bool:
	var image := root.get_texture().get_image()
	if image == null or image.is_empty() or image.save_png(path) != OK:
		_fail("capture failed: " + path)
		return false
	return true

func _roi_difference(a_path: String, b_path: String) -> float:
	var a := Image.load_from_file(a_path); var b := Image.load_from_file(b_path)
	if a == null or b == null or a.get_size() != b.get_size(): return INF
	var x0 := a.get_width() / 4; var x1 := a.get_width() * 3 / 4; var y0 := a.get_height() / 2; var y1 := a.get_height() * 9 / 10
	var total := 0.0; var count := 0
	for y in range(y0, y1, 4): for x in range(x0, x1, 4):
		var left := a.get_pixel(x, y); var right := b.get_pixel(x, y)
		total += absf(left.r - right.r) + absf(left.g - right.g) + absf(left.b - right.b); count += 1
	return total / max(1, count) * 255.0

func _fail(reason: String) -> bool:
	_restore_materials(); push_error("PROXY_TERRAIN_NORMALS_GPU_TEST=FAIL " + reason); quit(1); return true
