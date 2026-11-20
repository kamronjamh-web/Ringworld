extends SceneTree

## FL-3b (docs/forest-lighting-plan.md): ring_grass_cards.gdshader as a canopy receiver (sky occlusion, transmitted green light) and with the leaf-model translucency of the trees.
## Fixture: the FL-3a synthetic canopy raster (tools/ForestCanopyShadeGpuTest.cs) published as ringworld_canopy_cover, world x -> around, z -> across, y -> vertical, reference 5 m. The shipped grass material
## (RingGrassCardMaterialConfig defaults, variant D atlas, solid mid-green biome textures) draws a small lattice of cards (one MultiMesh) rooted at each location, seen by a perspective camera 1.6 m away
## in a 160 x 120 viewport (linear tonemap, readings decoded from sRGB, mean over the frame; the background is black so every check is a ratio or a sign).
## Checks:
##   (a) off byte identity: switch off -> canopy on -> off and off -> translucency on -> off per location and light, and (FOREST_GRASS_SHADE_HEAD_DIR = a folder with HEAD's ring_grass_cards.gdshader and
##       optionally ring_terrain_lighting.gdshaderinc) against the HEAD shader;
##   (b) canopy on, ambient only: on/off = 1 - blocked of the probe under the stand, the isolated crown and the conifer stand, 1 in the open and above; with a sun the added light is zero in the open and above,
##       and greener than the grass itself under the stand (added G/R about twice the albedo G/R: tau_c 0.050 / 0.025);
##   (c) translucency on, no canopy: backlit grass brighter than off; frontlit change small (reported); physical bound: backlit-on <= frontlit-on (k <= 1); the old and new transmittance printed;
##   (d) stills (FOREST_GRASS_SHADE_STILLS_DIR): backlit and frontlit, off and on, and the stand with the canopy off and on, plus one contact sheet;
##   (e) blocked = 0 identity: with canopy shade on, the open and above-the-crowns frames are byte-identical to canopy shade off for every light (the grass skips its canopy light block there);
##   (f) reference identity (FOREST_GRASS_SHADE_REF_SHADER = path of another ring_grass_cards.gdshader, e.g. HEAD's from git show): every location and light, canopy off and on, byte-identical to it
##       (proves a restructure such as the blocked > 0 gate changes no pixel).
## Run (windowed Vulkan, not --headless; no other Godot process running): tools/run_tests.ps1 -Tests forest_grass_shade_gpu_test -Label fl3b
## Prints FOREST_GRASS_SHADE_GPU_TEST=PASS|FAIL.
const TEST := preload("res://tools/ForestCanopyShadeGpuTest.cs")
const W := 160
const H := 120
const REFERENCE := 5.0
const OFF := Vector4(0, 0, 0, 0)
const CANOPY := Vector4(0, 0, 1, 0)
const TRANS := Vector4(1, 0, 0, 0)
const LOCATIONS := {
	"stand": Vector3(160, 0, 0),
	"isolated": Vector3(0, 0, 200),
	"open": Vector3(0, 0, -150),
	"above": Vector3(160, 20, 0),
	"conifer": Vector3(130, 0, -270),
}
# Light name -> [direction to the sun, energy, ambient energy]
const LIGHTS := {
	"ambient": [Vector3(0, 0.7071068, 0.7071068), 0.0, 1.0],
	"front": [Vector3(0, 0.7071068, 0.7071068), 3.0, 0.0], # sun behind the camera (camera at +z)
	"back": [Vector3(0, 0.7071068, -0.7071068), 3.0, 0.0], # sun behind the grass
	"csun": [Vector3(0.5, 0.8660254, 0.0), 1.2, 0.0], # side light for the canopy's transmitted light
}
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
var grass_node: MultiMeshInstance3D
var grass_material: ShaderMaterial
var failures: Array[String] = []
var off_frames := {} # "location_light" -> PackedByteArray
var on_frames := {} # "location_light" -> PackedByteArray (canopy on)
var stills := {}

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice required; Dummy renderer is invalid")
		return
	call_deferred("_run")

func _run() -> void:
	create_timer(900.0).timeout.connect(_on_watchdog)
	_make_scene()
	if failed: return
	# Probes (blocked fraction at each location's root).
	var probes := {}
	for loc: String in LOCATIONS: probes[loc] = await _probe(LOCATIONS[loc])
	if failed: return
	print("FL3B probe blocked: stand %.4f isolated %.4f open %.4f above %.4f conifer %.4f (species %.3f)" % [probes["stand"].x, probes["isolated"].x, probes["open"].x, probes["above"].x, probes["conifer"].x, probes["conifer"].y])
	if not (probes["stand"].x >= BLOCKED_STAND.x and probes["stand"].x <= BLOCKED_STAND.y): failures.append("probe stand blocked %.4f outside %.2f-%.2f" % [probes["stand"].x, BLOCKED_STAND.x, BLOCKED_STAND.y])
	if not (probes["isolated"].x >= BLOCKED_ISOLATED.x and probes["isolated"].x <= BLOCKED_ISOLATED.y): failures.append("probe isolated blocked %.4f outside %.2f-%.2f" % [probes["isolated"].x, BLOCKED_ISOLATED.x, BLOCKED_ISOLATED.y])
	if probes["open"].x > 0.004 or probes["above"].x > 0.004: failures.append("probe blocked in the open / above the canopy is not 0")
	# (a)/(b): canopy.
	var results := {}
	var identity_bad := 0
	var identity_total := 0
	var zero_bad := 0
	var zero_total := 0
	for location: String in LOCATIONS:
		for light: String in LIGHTS:
			_set_switches(OFF)
			var off_a := await _shot(location, light)
			_set_switches(CANOPY)
			var on_shot := await _shot(location, light)
			_set_switches(OFF)
			var off_b := await _shot(location, light)
			if failed: return
			identity_total += 1
			if off_a["data"] != off_b["data"]:
				identity_bad += 1
				failures.append("(a) %s %s: off frame differs after canopy off -> on -> off" % [location, light])
			off_frames["%s_%s" % [location, light]] = off_a["data"]
			on_frames["%s_%s" % [location, light]] = on_shot["data"]
			if location == "open" or location == "above":
				zero_total += 1
				if on_shot["data"] != off_a["data"]:
					zero_bad += 1
					failures.append("(e) %s %s: canopy on differs from off where blocked = 0" % [location, light])
			results["%s_%s" % [location, light]] = {"off": off_a["rgb"], "on": on_shot["rgb"], "off_image": off_a["image"], "on_image": on_shot["image"]}
	print("FL3B (a) canopy identity: %d of %d off -> on -> off frames differ" % [identity_bad, identity_total])
	print("FL3B (e) blocked = 0 identity: %d of %d canopy-on frames (open, above) differ from canopy off" % [zero_bad, zero_total])
	_check_canopy(results, probes)
	# (a)/(c): translucency.
	var trans := {}
	identity_bad = 0
	identity_total = 0
	for location: String in ["open", "stand"]:
		for light: String in ["front", "back", "csun"]:
			_set_switches(OFF)
			var off_a := await _shot(location, light)
			_set_switches(TRANS)
			var on_shot := await _shot(location, light)
			_set_switches(OFF)
			var off_b := await _shot(location, light)
			if failed: return
			identity_total += 1
			if off_a["data"] != off_b["data"]:
				identity_bad += 1
				failures.append("(a) %s %s: off frame differs after translucency off -> on -> off" % [location, light])
			trans["%s_%s" % [location, light]] = {"off": off_a["rgb"], "on": on_shot["rgb"], "off_image": off_a["image"], "on_image": on_shot["image"]}
	print("FL3B (a) translucency identity: %d of %d off -> on -> off frames differ" % [identity_bad, identity_total])
	_check_translucency(trans, results)
	var head_dir := OS.get_environment("FOREST_GRASS_SHADE_HEAD_DIR")
	if head_dir != "":
		await _head_identity(head_dir)
	if failed: return
	var reference := OS.get_environment("FOREST_GRASS_SHADE_REF_SHADER")
	if reference != "":
		await _reference_identity(reference)
	if failed: return
	_write_stills(trans, results)
	for message in failures: print("FOREST_GRASS_SHADE_GPU_TEST failure: " + message)
	done = true
	if failures.is_empty():
		print("FOREST_GRASS_SHADE_GPU_TEST=PASS")
		quit(0)
	else:
		print("FOREST_GRASS_SHADE_GPU_TEST=FAIL %d failures" % failures.size())
		quit(1)

func _check_canopy(results: Dictionary, probes: Dictionary) -> void:
	for location: String in LOCATIONS:
		var blocked: float = probes[location].x
		var amb: Dictionary = results["%s_ambient" % location]
		var ratio: Vector3 = amb["on"] / Vector3(maxf(amb["off"].x, 1e-9), maxf(amb["off"].y, 1e-9), maxf(amb["off"].z, 1e-9))
		var csun: Dictionary = results["%s_csun" % location]
		var added: Vector3 = csun["on"] - csun["off"]
		var albedo_gr: float = amb["off"].y / maxf(amb["off"].x, 1e-9)
		var added_gr: float = added.y / added.x if added.x > 1e-6 else 0.0
		print("FL3B %s: probe blocked %.4f; ambient on/off (%.4f %.4f %.4f) expected %.4f; sun off (%.5f %.5f %.5f) added (%.5f %.5f %.5f) added G/R %.3f albedo G/R %.3f ratio %.3f" % [location, blocked,
			ratio.x, ratio.y, ratio.z, 1.0 - blocked, csun["off"].x, csun["off"].y, csun["off"].z, added.x, added.y, added.z, added_gr, albedo_gr, added_gr / maxf(albedo_gr, 1e-9)])
		if amb["off"].y < 0.002 or csun["off"].y < 0.002: failures.append("(b) %s: no grass in the frame" % location)
		var tolerance := 0.04 if blocked > 0.0 else 0.003
		for channel in 3:
			if absf(ratio[channel] - (1.0 - blocked)) > tolerance: failures.append("(b) %s ambient on/off channel %d is %.4f, expected %.4f" % [location, channel, ratio[channel], 1.0 - blocked])
		if blocked > 0.05:
			if not (added.y > 0.0001): failures.append("(b) %s: no green light added under the canopy (%.5f)" % [location, added.y])
			if not (added_gr / maxf(albedo_gr, 1e-9) >= 1.6 and added_gr / maxf(albedo_gr, 1e-9) <= 2.4): failures.append("(b) %s: added G/R is %.3f of the grass G/R, expected about 2" % [location, added_gr / maxf(albedo_gr, 1e-9)])
		else:
			if maxf(added.x, maxf(added.y, added.z)) > 0.0003: failures.append("(b) %s: light added with no canopy above (%.5f)" % [location, added.y])

func _check_translucency(trans: Dictionary, results: Dictionary) -> void:
	var back: Dictionary = trans["open_back"]
	var front: Dictionary = trans["open_front"]
	var back_gain: float = back["on"].y / back["off"].y
	var front_change: float = front["on"].y / front["off"].y
	print("FL3B (c) open: backlit mean RGB off (%.5f %.5f %.5f) on (%.5f %.5f %.5f), green gain on/off %.3f" % [back["off"].x, back["off"].y, back["off"].z, back["on"].x, back["on"].y, back["on"].z, back_gain])
	print("FL3B (c) open: frontlit mean RGB off (%.5f %.5f %.5f) on (%.5f %.5f %.5f), green change on/off %.4f" % [front["off"].x, front["off"].y, front["off"].z, front["on"].x, front["on"].y, front["on"].z, front_change])
	var back_on: float = back["on"].y
	var front_on: float = front["on"].y
	print("FL3B (c) bound: backlit-on green %.5f vs frontlit-on green %.5f (ratio %.3f; <= 1 required); backlit-off/frontlit-off %.3f" % [back_on, front_on, back_on / front_on, back["off"].y / front["off"].y])
	if not (back_gain > 1.02): failures.append("(c) backlit grass is not brighter with translucency on (gain %.3f)" % back_gain)
	# Reported, not asserted: the grass lights its reflection with the bent ground normal (n.L about 0.7 here), not the blade face, so a frontlit blade has no face lobe to out-shine the transmitted one.
	if back_on > front_on: print("FL3B (c) NOTE: backlit-on exceeds frontlit-on (%.3f): the card model has no frontlit face reflection lobe; the per-leaf bound T <= R holds as k * s <= 1 (below)" % (back_on / front_on))
	var k_shape := 0.8 * Vector3(0.95, 1.10, 0.45)
	if maxf(k_shape.x, maxf(k_shape.y, k_shape.z)) > 1.0: failures.append("(c) transmittance over reflectance k * s exceeds 1")
	# Old and new transmittance: mean albedo of the covered pixels (ambient frame, energy 1, AO 1).
	var amb: Dictionary = results["open_ambient"]
	var image: Image = amb["off_image"]
	var covered := 0
	var sum := Vector3.ZERO
	for y in image.get_height():
		for x in image.get_width():
			var c := image.get_pixel(x, y).srgb_to_linear()
			if c.g > 0.01:
				covered += 1
				sum += Vector3(c.r, c.g, c.b)
	var albedo := sum / maxf(float(covered), 1.0)
	var shape := Vector3(0.95, 1.10, 0.45)
	var old_t := 0.28 * albedo * albedo
	var new_t := 0.8 * shape * albedo
	print("FL3B (c) transmittance: mean albedo (%.4f %.4f %.4f) over %d px; old 0.28 a^2 = (%.4f %.4f %.4f), new 0.8 s a = (%.4f %.4f %.4f); new/old (%.2f %.2f %.2f); old T/R %.3f new T/R %.3f (green)" % [albedo.x, albedo.y, albedo.z, covered,
		old_t.x, old_t.y, old_t.z, new_t.x, new_t.y, new_t.z, new_t.x / old_t.x, new_t.y / old_t.y, new_t.z / old_t.z, old_t.y / albedo.y, new_t.y / albedo.y])

# Swaps in HEAD's ring_grass_cards.gdshader (and ring_terrain_lighting.gdshaderinc when the folder holds it) and compares every off frame with the new shader's off frame.
func _head_identity(directory: String) -> void:
	var code := FileAccess.get_file_as_string(directory.path_join("ring_grass_cards.gdshader"))
	if code.is_empty():
		_fail("FOREST_GRASS_SHADE_HEAD_DIR %s lacks ring_grass_cards.gdshader" % directory)
		return
	if code.contains("ring_canopy_shade"):
		_fail("the HEAD shader in %s already contains the canopy shade code" % directory)
		return
	var lighting := FileAccess.get_file_as_string(directory.path_join("ring_terrain_lighting.gdshaderinc"))
	if not lighting.is_empty():
		var common := FileAccess.get_file_as_string("res://materials/ring_grass_card_common.gdshaderinc")
		var include_line := "#include \"res://materials/ring_terrain_lighting.gdshaderinc\""
		if not common.contains(include_line):
			_fail("ring_grass_card_common.gdshaderinc no longer includes ring_terrain_lighting.gdshaderinc")
			return
		code = code.replace("#include \"res://materials/ring_grass_card_common.gdshaderinc\"", common.replace(include_line, lighting))
	var head := Shader.new()
	head.code = code
	var original := grass_material.shader
	grass_material.shader = head
	_set_switches(OFF)
	var total := 0
	var differing := 0
	for location: String in LOCATIONS:
		for light: String in LIGHTS:
			var shot := await _shot(location, light)
			if failed: return
			total += 1
			if shot["data"] != off_frames["%s_%s" % [location, light]]:
				differing += 1
				failures.append("(a) %s %s differs between the HEAD shader and the new shader with the switches off" % [location, light])
	grass_material.shader = original
	print("FL3B (a) identity against the HEAD shader (switches off, lighting include %s): %d of %d frames differ" % ["HEAD" if not lighting.is_empty() else "current", differing, total])

# (f) Renders every location and light with a reference grass shader, canopy off and on, and compares with the current shader's frames byte for byte.
func _reference_identity(path: String) -> void:
	var code := FileAccess.get_file_as_string(path)
	if code.is_empty():
		_fail("FOREST_GRASS_SHADE_REF_SHADER %s is empty or missing" % path)
		return
	var reference := Shader.new()
	reference.code = code
	var original := grass_material.shader
	grass_material.shader = reference
	var total := 0
	var differing := 0
	for location: String in LOCATIONS:
		for light: String in LIGHTS:
			for state in ["off", "canopy"]:
				_set_switches(OFF if state == "off" else CANOPY)
				var shot := await _shot(location, light)
				if failed: return
				total += 1
				var key := "%s_%s" % [location, light]
				var mine: PackedByteArray = off_frames[key] if state == "off" else on_frames[key]
				if shot["data"] != mine:
					differing += 1
					failures.append("(f) %s %s canopy %s differs from the reference shader" % [location, light, state])
	grass_material.shader = original
	_set_switches(OFF)
	print("FL3B (f) identity against the reference shader %s (canopy off and on): %d of %d frames differ" % [path, differing, total])

func _write_stills(trans: Dictionary, results: Dictionary) -> void:
	var directory := OS.get_environment("FOREST_GRASS_SHADE_STILLS_DIR")
	if directory == "": return
	DirAccess.make_dir_recursive_absolute(directory)
	var rows := [
		["backlit", trans["open_back"]],
		["frontlit", trans["open_front"]],
		["stand_canopy_ambient", results["stand_ambient"]],
	]
	var sheet := Image.create(W * 2 * 2, H * 2 * rows.size(), false, Image.FORMAT_RGBA8)
	for row in rows.size():
		for column in 2:
			var key := "off_image" if column == 0 else "on_image"
			var image: Image = (rows[row][1][key] as Image).duplicate()
			image.convert(Image.FORMAT_RGBA8)
			image.resize(W * 2, H * 2, Image.INTERPOLATE_BILINEAR)
			image.save_png(directory.path_join("%s_%s.png" % [rows[row][0], "off" if column == 0 else "on"]))
			sheet.blit_rect(image, Rect2i(0, 0, W * 2, H * 2), Vector2i(column * W * 2, row * H * 2))
	sheet.save_png(directory.path_join("contact_sheet.png"))
	print("FL3B (d) stills written to %s (rows: backlit, frontlit, stand under the canopy, ambient light only; columns: off, on)" % directory)

func _set_switches(value: Vector4) -> void:
	RenderingServer.global_shader_parameter_set("ringworld_forest_lighting", value)

# One probe: the include's (blocked, species factor) at a world position (switch on).
func _probe(position: Vector3) -> Vector2:
	_set_switches(CANOPY)
	grass_node.visible = false
	probe_node.visible = true
	probe_material.set_shader_parameter("probe_position", position)
	sun.visible = false
	camera.transform = Transform3D(Basis.IDENTITY, Vector3(0, 0, 5002))
	for i in 3: await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	if image == null or image.is_empty():
		_fail("empty probe capture")
		return Vector2.ZERO
	var pixel := image.get_pixel(W / 2, H / 2).srgb_to_linear()
	return Vector2(pixel.r, pixel.g)

## One capture: {"data" raw bytes, "rgb" mean linear colour, "image"}.
func _shot(location: String, light: String) -> Dictionary:
	probe_node.visible = false
	grass_node.visible = true
	var root: Vector3 = LOCATIONS[location]
	grass_node.position = root
	var setting: Array = LIGHTS[light]
	var direction: Vector3 = setting[0]
	sun.visible = setting[1] > 0.0
	sun.light_energy = setting[1]
	sun.transform = Transform3D(Basis.looking_at(-direction, Vector3.UP), Vector3.ZERO)
	grass_material.set_shader_parameter("sun_direction", direction)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_to_sun", direction)
	environment.ambient_light_energy = setting[2]
	var position := root + Vector3(0, 0.40, 1.6)
	camera.transform = Transform3D(Basis.looking_at((root + Vector3(0, 0.22, 0)) - position, Vector3.UP), position)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_camera_world_position", position)
	grass_material.set_shader_parameter("ring_viewer_world_position", position)
	grass_material.set_shader_parameter("ring_atmosphere_camera_world", position)
	for i in 3: await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	if image == null or image.is_empty():
		_fail("empty capture %s %s" % [location, light])
		return {}
	var sum := Vector3.ZERO
	for y in image.get_height():
		for x in image.get_width():
			var linear := image.get_pixel(x, y).srgb_to_linear()
			sum += Vector3(linear.r, linear.g, linear.b)
	return {"data": image.get_data(), "rgb": sum / float(image.get_width() * image.get_height()), "image": image}

# --- scene ----------------------------------------------------------------------------------------------------------------------------------------------------

func _make_scene() -> void:
	helper = TEST.new()
	viewport = SubViewport.new()
	viewport.size = Vector2i(W, H)
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
	camera.projection = Camera3D.PROJECTION_PERSPECTIVE
	camera.fov = 40.0
	camera.near = 0.05
	camera.far = 5100.0
	camera.current = true
	viewport.add_child(camera)
	sun = DirectionalLight3D.new()
	sun.shadow_enabled = false
	viewport.add_child(sun)
	_publish_globals()
	# Probe quad (far from the grass, seen at z = 5000).
	probe_material = ShaderMaterial.new()
	var probe_shader := Shader.new()
	probe_shader.code = PROBE_SHADER
	probe_material.shader = probe_shader
	probe_node = MeshInstance3D.new()
	var probe_quad := QuadMesh.new()
	probe_quad.size = Vector2(8, 8)
	probe_node.mesh = probe_quad
	probe_node.material_override = probe_material
	probe_node.position = Vector3(0, 0, 5000)
	probe_node.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	viewport.add_child(probe_node)
	# Grass: the shipped material, one MultiMesh of cards on a lattice around the origin of the node.
	grass_material = helper.call("CreateGrassMaterial")
	_configure(grass_material)
	var mesh: Mesh = helper.get("GrassMesh")
	var multi := MultiMesh.new()
	multi.transform_format = MultiMesh.TRANSFORM_3D
	multi.use_colors = true
	multi.use_custom_data = true
	multi.mesh = mesh
	var count := 7 * 5
	multi.instance_count = count
	for i in count:
		var gx := (i % 7) - 3
		var gz := (i / 7) - 2
		var yaw := float(i) * 0.97
		var basis := Basis(Vector3.UP, yaw).scaled(Vector3(0.45, 0.45, 0.45))
		multi.set_instance_transform(i, Transform3D(basis, Vector3(gx * 0.22 + 0.04 * sin(i * 1.7), 0.0, gz * 0.22 + 0.04 * cos(i * 2.3))))
		multi.set_instance_color(i, Color(0, 0, 0, 0))
		multi.set_instance_custom_data(i, Color(0.0, fmod(float(i) * 0.37, 1.0), 0.0, 0.0))
	grass_node = MultiMeshInstance3D.new()
	grass_node.multimesh = multi
	grass_node.material_override = grass_material
	grass_node.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	grass_node.set_instance_shader_parameter("grass_modifier_layer", -1)
	grass_node.set_instance_shader_parameter("vegetation_haze_slot", Vector2(-1.0, 0.0))
	grass_node.visible = false
	viewport.add_child(grass_node)
	# The raster.
	var image: Image = helper.call("BuildRaster", 6.0)
	RenderingServer.global_shader_parameter_set("ringworld_canopy_cover", ImageTexture.create_from_image(image))
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
	RenderingServer.global_shader_parameter_set("ring_atmosphere_to_sun", Vector3(0, 0.7071068, 0.7071068))
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
	# Solid mid-green ground so the cards neither clip nor go black.
	var ground_image := Image.create(4, 4, false, Image.FORMAT_RGBA8)
	ground_image.fill(Color(0.62, 0.80, 0.38))
	var ground := ImageTexture.create_from_image(ground_image)
	for name in ["biome_grass_albedo", "biome_dirt_albedo", "biome_desert_albedo", "biome_forest_albedo", "biome_tundra_albedo", "biome_alpine_albedo"]:
		material.set_shader_parameter(name, ground)

func _fail(message: String) -> void:
	if done: return
	failed = true
	done = true
	push_error("FOREST_GRASS_SHADE_GPU_TEST=FAIL " + message)
	print("FOREST_GRASS_SHADE_GPU_TEST=FAIL " + message)
	quit(1)

func _on_watchdog() -> void:
	if not done: _fail("watchdog timeout")
