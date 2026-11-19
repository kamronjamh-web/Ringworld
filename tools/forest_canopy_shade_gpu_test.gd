extends SceneTree

## FL-3a (docs/forest-lighting-plan.md section 5): canopy sky occlusion and green transmitted light in the receivers (POI material, imported_tree bark, proxy terrain; Terrain3D is not rendered here).
## Fixture: a synthetic canopy raster (tools/ForestCanopyShadeGpuTest.cs: a closed stand, an isolated crown, a conifer stand) uploaded as ringworld_canopy_cover, a simple frame (world x -> around, z -> across,
## y -> vertical, reference 5 m, ring radius 1e7), params and the switches published as globals. Receivers are small vertical quads facing +Z (the sun is in the XY plane, so n.L = 0: the direct
## light is zero and the sun frames show the transmitted canopy light alone), seen by an orthographic camera in a 16 x 16 viewport (linear tonemap, readings decoded from sRGB).
## Checks:
##   (a) include probe (a test shader writing ring_canopy_sample to the output): blocked under the closed stand 0.70-0.80, under the isolated crown 0.15-0.25, 0 in the open, 0 above the canopy top,
##       fades between 380 and 500 m (half at 440 m, 0 at 505 m), conifer species factor 0.6;
##   (b) per receiver (POI quad, bark below the crown base, bark above the crown base, proxy terrain quad): switch off -> on -> off is byte identical; ambient only (white ambient, no sun): the on/off
##       ratio is 1 - blocked of the probe under the stand, the isolated crown and the conifer stand and 1 in the open and above the canopy (bark above the crown base: always 1, FL-2c owns it); with the sun: the
##       added light is zero in the open and above, green over red about 2 (tau_c 0.050 / 0.025) under the stand, and the conifer's added light per unit blocked is 0.6 of the broadleaf's (POI);
##   (c) with FOREST_CANOPY_SHADE_HEAD_DIR (a folder holding imported_tree.gdshader, ring_poi_architecture.gdshaderinc and ring_terrain_proxy.gdshader as they were before FL-3a) every off frame must equal
##       the HEAD shaders' frame byte for byte.
## Terrain3D needs a real world (regions, clipmap); it is covered by FL-4 in the real world. The proxy quad here renders ring_terrain_proxy.gdshader as shipped.
## Run (windowed Vulkan, not --headless; no other Godot process running): tools/run_tests.ps1 -Tests forest_canopy_shade_gpu_test -Label fl3a
## Prints FOREST_CANOPY_SHADE_GPU_TEST=PASS|FAIL.
const TEST := preload("res://tools/ForestCanopyShadeGpuTest.cs")
const SIZE := 16
const REFERENCE := 5.0 # ForestCanopyShadeGpuTest.Reference
const SUN := Vector3(0.5, 0.8660254, 0.0)
const OFF := Vector4(0, 0, 0, 0)
const ON := Vector4(0, 0, 1, 0) # canopy shade only
const TREE_HEIGHT := 18.0
const QUAD := 0.6
const LOCATIONS := {
	"stand": Vector3(160, 0, 0),
	"isolated": Vector3(0, 0, 200),
	"open": Vector3(0, 0, -150),
	"above": Vector3(160, 20, 0),
	"conifer": Vector3(130, 0, -270),
}
const RECEIVERS := ["poi", "bark_below", "bark_above", "proxy"]
const HEIGHTS := {"poi": 1.0, "bark_below": 1.8, "bark_above": 9.9, "proxy": 1.0} # quad height above the location's root
const MODES := ["ambient", "sun"]
const STAND_SPACING := 6.0
const BLOCKED_STAND := Vector2(0.70, 0.80)
const BLOCKED_ISOLATED := Vector2(0.15, 0.25)
const PROBE_SHADER := """shader_type spatial;
render_mode unshaded, cull_disabled;
#include "res://materials/ring_canopy_shade.gdshaderinc"
uniform vec3 probe_position;
void fragment() {
	vec2 s = ring_canopy_sample(probe_position);
	ALBEDO = vec3(s.x, s.y, 0.0);
}
"""

var failed := false
var done := false
var viewport: SubViewport
var environment: Environment
var camera: Camera3D
var sun: DirectionalLight3D
var helper
var probe_material: ShaderMaterial
var probe_node: MeshInstance3D
var nodes := {} # receiver -> MeshInstance3D
var materials := {} # receiver -> ShaderMaterial
var failures: Array[String] = []
var off_frames := {} # "receiver_location_mode" -> PackedByteArray

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice required; Dummy renderer is invalid")
		return
	call_deferred("_run")

func _run() -> void:
	create_timer(600.0).timeout.connect(_on_watchdog)
	_make_scene()
	if failed: return
	# (a) probes.
	var probes := {}
	for loc: String in LOCATIONS:
		for receiver: String in RECEIVERS:
			var position := _receiver_position(receiver, loc)
			if not probes.has(position): probes[position] = await _probe(position)
	var stand: Vector2 = await _probe(Vector3(160, 1, 0))
	var isolated: Vector2 = await _probe(Vector3(0, 1, 200))
	var open: Vector2 = await _probe(Vector3(0, 1, -150))
	var above: Vector2 = await _probe(Vector3(160, 25, 0))
	var conifer: Vector2 = await _probe(Vector3(130, 1, -270))
	var fade_300: Vector2 = await _probe(Vector3(300, 1, 0))
	var fade_440: Vector2 = await _probe(Vector3(440, 1, 0))
	var fade_505: Vector2 = await _probe(Vector3(505, 1, 0))
	if failed: return
	print("FL3A (a) probe: stand blocked %.4f (species %.3f), isolated crown %.4f, open %.4f, above the canopy %.4f, conifer stand %.4f (species %.3f)" % [stand.x, stand.y, isolated.x, open.x, above.x, conifer.x, conifer.y])
	print("FL3A (a) fade: blocked at 300 m %.4f, 440 m %.4f (ratio %.3f, smoothstep predicts 0.500), 505 m %.4f; %d trees, stand spacing %.1f m" % [fade_300.x, fade_440.x, fade_440.x / maxf(fade_300.x, 1e-6), fade_505.x, helper.TreeCount, STAND_SPACING])
	if not (stand.x >= BLOCKED_STAND.x and stand.x <= BLOCKED_STAND.y): failures.append("(a) stand blocked %.4f outside %.2f-%.2f" % [stand.x, BLOCKED_STAND.x, BLOCKED_STAND.y])
	if not (isolated.x >= BLOCKED_ISOLATED.x and isolated.x <= BLOCKED_ISOLATED.y): failures.append("(a) isolated blocked %.4f outside %.2f-%.2f" % [isolated.x, BLOCKED_ISOLATED.x, BLOCKED_ISOLATED.y])
	if open.x > 0.004: failures.append("(a) open blocked %.4f is not 0" % open.x)
	if above.x > 0.004: failures.append("(a) blocked above the canopy %.4f is not 0" % above.x)
	if absf(fade_440.x / maxf(fade_300.x, 1e-6) - 0.5) > 0.06: failures.append("(a) fade at 440 m is %.3f of the interior value, expected 0.5" % (fade_440.x / maxf(fade_300.x, 1e-6)))
	if fade_505.x > 0.004: failures.append("(a) blocked at 505 m %.4f is not 0" % fade_505.x)
	if absf(conifer.y - 0.6) > 0.03: failures.append("(a) conifer species factor %.3f is not 0.6" % conifer.y)
	if absf(stand.y - 1.0) > 0.03: failures.append("(a) broadleaf species factor %.3f is not 1" % stand.y)
	# (b) receivers.
	var results := {}
	var identity_bad := 0
	var identity_total := 0
	for receiver: String in RECEIVERS:
		for location: String in LOCATIONS:
			for mode: String in MODES:
				_set_switches(OFF)
				var off_a := await _shot(receiver, location, mode)
				_set_switches(ON)
				var on_shot := await _shot(receiver, location, mode)
				_set_switches(OFF)
				var off_b := await _shot(receiver, location, mode)
				if failed: return
				identity_total += 1
				if off_a["data"] != off_b["data"]:
					identity_bad += 1
					failures.append("(b) %s %s %s: off frame differs after off -> on -> off" % [receiver, location, mode])
				off_frames["%s_%s_%s" % [receiver, location, mode]] = off_a["data"]
				results["%s_%s_%s" % [receiver, location, mode]] = {"off": off_a["rgb"], "on": on_shot["rgb"]}
	print("FL3A (b) identity: %d of %d off -> on -> off frames differ" % [identity_bad, identity_total])
	_check_receivers(results, probes)
	var head_dir := OS.get_environment("FOREST_CANOPY_SHADE_HEAD_DIR")
	if head_dir != "":
		await _head_identity(head_dir)
	if failed: return
	for message in failures: print("FOREST_CANOPY_SHADE_GPU_TEST failure: " + message)
	done = true
	if failures.is_empty():
		print("FOREST_CANOPY_SHADE_GPU_TEST=PASS")
		quit(0)
	else:
		print("FOREST_CANOPY_SHADE_GPU_TEST=FAIL %d failures" % failures.size())
		quit(1)

func _receiver_position(receiver: String, location: String) -> Vector3:
	return LOCATIONS[location] + Vector3(0, HEIGHTS[receiver], 0)

func _check_receivers(results: Dictionary, probes: Dictionary) -> void:
	var added_per_blocked := {}
	for receiver: String in RECEIVERS:
		for location: String in LOCATIONS:
			var blocked: float = probes[_receiver_position(receiver, location)].x
			if receiver == "bark_above": blocked = 0.0 # at or above the crown base the raster is not applied (FL-2c owns it)
			var amb: Dictionary = results["%s_%s_ambient" % [receiver, location]]
			var sun_result: Dictionary = results["%s_%s_sun" % [receiver, location]]
			var ratio: Vector3 = amb["on"] / Vector3(maxf(amb["off"].x, 1e-9), maxf(amb["off"].y, 1e-9), maxf(amb["off"].z, 1e-9))
			var added: Vector3 = sun_result["on"] - sun_result["off"]
			var green_red: float = added.y / added.x if added.x > 1e-6 else 0.0
			print("FL3A %s %s: probe blocked %.4f; ambient on/off (%.4f %.4f %.4f) expected %.4f; sun off (%.5f %.5f %.5f) added (%.5f %.5f %.5f) green/red %.3f" % [receiver, location, blocked,
				ratio.x, ratio.y, ratio.z, 1.0 - blocked, sun_result["off"].x, sun_result["off"].y, sun_result["off"].z, added.x, added.y, added.z, green_red])
			var tolerance := 0.03 if blocked > 0.0 else 0.002
			for channel in 3:
				if absf(ratio[channel] - (1.0 - blocked)) > tolerance: failures.append("(b) %s %s ambient on/off channel %d is %.4f, expected %.4f" % [receiver, location, channel, ratio[channel], 1.0 - blocked])
			if sun_result["off"].y > 0.003: failures.append("(b) %s %s sun frame is not dark with the switch off (%.5f): the direct light should be zero here" % [receiver, location, sun_result["off"].y])
			if blocked > 0.05:
				if not (added.y > 0.0005): failures.append("(b) %s %s: no green light added under the canopy (%.5f)" % [receiver, location, added.y])
				if not (green_red >= 1.7 and green_red <= 2.3): failures.append("(b) %s %s: added green/red %.3f is not about 2" % [receiver, location, green_red])
				added_per_blocked["%s_%s" % [receiver, location]] = added.y / blocked
			else:
				if maxf(added.x, maxf(added.y, added.z)) > 0.0005: failures.append("(b) %s %s: light added with no canopy above (%.5f)" % [receiver, location, added.y])
	for receiver: String in ["poi", "bark_below", "proxy"]:
		var species: float = added_per_blocked["%s_conifer" % receiver] / added_per_blocked["%s_stand" % receiver]
		print("FL3A (b) %s: conifer added light per unit blocked / broadleaf = %.3f (expected 0.6)" % [receiver, species])
		if absf(species - 0.6) > 0.08: failures.append("(b) %s conifer species scaling %.3f is not 0.6" % [receiver, species])

# Swaps the three shaders for the pre-FL-3a versions and compares every off frame with the new shaders' off frame.
func _head_identity(directory: String) -> void:
	var tree_code := FileAccess.get_file_as_string(directory.path_join("imported_tree.gdshader"))
	var poi_include := FileAccess.get_file_as_string(directory.path_join("ring_poi_architecture.gdshaderinc"))
	var proxy_code := FileAccess.get_file_as_string(directory.path_join("ring_terrain_proxy.gdshader"))
	if tree_code.is_empty() or poi_include.is_empty() or proxy_code.is_empty():
		_fail("FOREST_CANOPY_SHADE_HEAD_DIR %s lacks one of the three HEAD shaders" % directory)
		return
	if tree_code.contains("ring_canopy_shade") or poi_include.contains("ring_canopy_shade") or proxy_code.contains("ring_canopy_shade"):
		_fail("the HEAD shaders in %s already contain the canopy shade code" % directory)
		return
	var poi_code := FileAccess.get_file_as_string("res://materials/ring_poi_architecture.gdshader").replace("#include \"res://materials/ring_poi_architecture.gdshaderinc\"", poi_include)
	var shaders := {"poi": poi_code, "bark_below": tree_code, "bark_above": tree_code, "proxy": proxy_code}
	var original := {}
	for receiver: String in RECEIVERS:
		var shader := Shader.new()
		shader.code = shaders[receiver]
		original[receiver] = materials[receiver].shader
		materials[receiver].shader = shader
	_set_switches(OFF)
	var total := 0
	var differing := 0
	for receiver: String in RECEIVERS:
		for location: String in LOCATIONS:
			for mode: String in MODES:
				var head_shot := await _shot(receiver, location, mode)
				if failed: return
				total += 1
				if head_shot["data"] != off_frames["%s_%s_%s" % [receiver, location, mode]]:
					differing += 1
					failures.append("(c) %s %s %s differs between the HEAD shader and the new shader with the switch off" % [receiver, location, mode])
	for receiver: String in RECEIVERS: materials[receiver].shader = original[receiver]
	print("FL3A (c) identity against the HEAD shaders (switch off): %d of %d frames differ" % [differing, total])

func _set_switches(value: Vector4) -> void:
	RenderingServer.global_shader_parameter_set("ringworld_forest_lighting", value)

# One probe: the include's (blocked, species factor) at a world position, read back from a fullscreen quad (switch on).
func _probe(position: Vector3) -> Vector2:
	_set_switches(ON)
	for key in nodes: (nodes[key] as MeshInstance3D).visible = false
	probe_node.visible = true
	probe_material.set_shader_parameter("probe_position", position)
	sun.visible = false
	camera.transform = Transform3D(Basis.IDENTITY, Vector3(0, 0, 5000) + Vector3(0, 0, 2))
	camera.size = 1.0
	for i in 3: await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	if image == null or image.is_empty():
		_fail("empty probe capture")
		return Vector2.ZERO
	var pixel := image.get_pixel(SIZE / 2, SIZE / 2).srgb_to_linear()
	return Vector2(pixel.r, pixel.g)

## One capture: returns {"data" (raw bytes), "rgb" (mean linear colour)}.
func _shot(receiver: String, location: String, mode: String) -> Dictionary:
	probe_node.visible = false
	for key in nodes: (nodes[key] as MeshInstance3D).visible = key == receiver
	var root: Vector3 = LOCATIONS[location]
	var center := _receiver_position(receiver, location)
	var node: MeshInstance3D = nodes[receiver]
	if receiver.begins_with("bark"):
		node.transform = Transform3D(Basis.IDENTITY.scaled(Vector3.ONE * TREE_HEIGHT), root)
	else:
		node.transform = Transform3D(Basis.IDENTITY, center)
	var ambient := mode == "ambient"
	sun.visible = not ambient
	environment.ambient_light_energy = 1.0 if ambient else 0.0
	camera.size = QUAD * 0.5
	var position := center + Vector3(0, 0, 2)
	camera.transform = Transform3D(Basis.IDENTITY, position)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_camera_world_position", position)
	materials[receiver].set_shader_parameter("camera_world_position", position)
	for i in 3: await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	if image == null or image.is_empty():
		_fail("empty capture %s %s %s" % [receiver, location, mode])
		return {}
	var sum := Vector3.ZERO
	for y in image.get_height():
		for x in image.get_width():
			var linear := image.get_pixel(x, y).srgb_to_linear()
			sum += Vector3(linear.r, linear.g, linear.b)
	return {"data": image.get_data(), "rgb": sum / float(image.get_width() * image.get_height())}

# --- scene ----------------------------------------------------------------------------------------------------------------------------------------------------

func _make_scene() -> void:
	helper = TEST.new()
	viewport = SubViewport.new()
	viewport.size = Vector2i(SIZE, SIZE)
	viewport.own_world_3d = true
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	environment = Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color(0, 0, 0)
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.ambient_light_color = Color(1.0, 1.0, 1.0)
	environment.ambient_light_energy = 0.0
	environment.reflected_light_source = Environment.REFLECTION_SOURCE_DISABLED
	environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	var world := WorldEnvironment.new()
	world.environment = environment
	viewport.add_child(world)
	camera = Camera3D.new()
	camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	camera.size = 1.0
	camera.near = 0.05
	camera.far = 20.0
	camera.current = true
	viewport.add_child(camera)
	sun = DirectionalLight3D.new()
	sun.light_energy = 8.0 # the sun frames hold only the (small) transmitted light: more energy keeps it clear of the 8-bit readout steps; every check is a ratio or a sign
	sun.shadow_enabled = false
	sun.transform = Transform3D(Basis.looking_at(-SUN, Vector3.UP), Vector3.ZERO)
	viewport.add_child(sun)
	_publish_globals()
	# Probe quad (far from the receivers, seen at z = 5000).
	probe_material = ShaderMaterial.new()
	var probe_shader := Shader.new()
	probe_shader.code = PROBE_SHADER
	probe_material.shader = probe_shader
	probe_node = MeshInstance3D.new()
	var probe_quad := QuadMesh.new()
	probe_quad.size = Vector2(4, 4)
	probe_node.mesh = probe_quad
	probe_node.material_override = probe_material
	probe_node.position = Vector3(0, 0, 5000)
	probe_node.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	viewport.add_child(probe_node)
	camera.far = 20.0
	# Receivers.
	for receiver: String in RECEIVERS:
		var material := ShaderMaterial.new()
		var quad := QuadMesh.new()
		quad.size = Vector2(QUAD, QUAD)
		var node := MeshInstance3D.new()
		node.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
		match receiver:
			"poi":
				material.shader = load("res://materials/ring_poi_architecture.gdshader")
				material.set_shader_parameter("albedo_color", Color(0.5, 0.5, 0.5))
				material.set_shader_parameter("specular_value", 0.0)
				material.set_shader_parameter("poi_ring_lighting_valid", true)
				material.set_shader_parameter("poi_ring_center_world", Vector3(1000000.0, 0.0, 0.0))
			"proxy":
				material.shader = load("res://materials/ring_terrain_proxy.gdshader")
				material.set_shader_parameter("ring_center", Vector3(1000000.0, 0.0, 0.0))
				material.set_shader_parameter("sun_direction", SUN)
				material.set_shader_parameter("geomorph_enabled", false)
			_:
				material.shader = load("res://materials/imported_tree.gdshader")
				material.set_shader_parameter("ring_center", Vector3(1000000.0, 0.0, 0.0))
				material.set_shader_parameter("sun_direction", SUN)
				material.set_shader_parameter("albedo_tint", Color(0.5, 0.5, 0.5, 1.0))
				material.set_shader_parameter("vegetation_wind_enabled", false)
				material.set_shader_parameter("simplified_tree_shading", true)
				material.set_shader_parameter("use_alpha_cutout", false)
				# Model space: 1 unit = the tree's height. Crown hull centre 0.7, semi-axes 0.25 and 0.3 (base 0.4 = 7.2 m), valid.
				node.set_instance_shader_parameter("tree_crown_hull", Vector4(0.7, 0.25, 0.3, 0.5))
				node.set_instance_shader_parameter("tree_crown_hull_extra", Vector4(0.0, 0.0, 1.0, 1.0))
				node.set_instance_shader_parameter("tree_crown_lad", Vector4(0.25, 0.25, 0.25, 0.25))
				quad.size = Vector2(QUAD, QUAD) / TREE_HEIGHT
				quad.center_offset = Vector3(0.0, HEIGHTS[receiver] / TREE_HEIGHT, 0.0)
		_configure(material)
		node.mesh = quad
		node.material_override = material
		node.visible = false
		viewport.add_child(node)
		nodes[receiver] = node
		materials[receiver] = material
	# The raster.
	var image: Image = helper.call("BuildRaster", STAND_SPACING)
	var texture := ImageTexture.create_from_image(image)
	RenderingServer.global_shader_parameter_set("ringworld_canopy_cover", texture)
	var frame := Projection(Vector4(1, 0, 0, 0), Vector4(0, 0, 1, 0), Vector4(0, 1, 0, 0), Vector4(0, 0, 0, REFERENCE))
	RenderingServer.global_shader_parameter_set("ringworld_canopy_frame", frame)
	RenderingServer.global_shader_parameter_set("ringworld_canopy_params", Vector4(10000000.0, 2.0, 512.0, 1.0))

func _publish_globals() -> void:
	RenderingServer.global_shader_parameter_set("ringworld_solar_visibility", 1.0)
	RenderingServer.global_shader_parameter_set("ring_local_wind_enabled", 0.0)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_ray_frame_valid", true)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_camera_world_position", Vector3.ZERO)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_camera_height_meters", 0.0)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_outward_world", Vector3.RIGHT)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_axis_world", Vector3.UP)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_sidewall_negative_meters", 1000000.0)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_sidewall_positive_meters", 1000000.0)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_coordinate_scale", 1.0)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_to_sun", SUN)
	_set_switches(OFF)

func _configure(material: ShaderMaterial) -> void:
	material.set_shader_parameter("ring_atmosphere_enabled", false)
	material.set_shader_parameter("ring_radius_meters", 5000000.0)
	material.set_shader_parameter("atmosphere_haze_model", 1)
	material.set_shader_parameter("ring_haze_sigma_R", Vector3.ZERO)
	material.set_shader_parameter("ring_haze_sigma_A", 0.0)
	material.set_shader_parameter("atmosphere_boundary_max_opacity", 0.0)
	material.set_shader_parameter("atmosphere_upper_max_opacity", 0.0)
	material.set_shader_parameter("atmosphere_distance_haze_max_opacity", 0.0)

func _fail(message: String) -> void:
	if done: return
	failed = true
	done = true
	push_error("FOREST_CANOPY_SHADE_GPU_TEST=FAIL " + message)
	print("FOREST_CANOPY_SHADE_GPU_TEST=FAIL " + message)
	quit(1)

func _on_watchdog() -> void:
	if not done: _fail("watchdog timeout")
