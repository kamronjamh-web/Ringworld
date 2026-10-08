extends SceneTree

# GPU contract for the production grass direct-light gate. The reference is
# derived from the production source by removing only custom light(), leaving
# the same vertex/fragment code for Godot's built-in renderer path.
const DRIVER := preload("res://tools/VegetationWindTestDriver.cs")
const SIZE := 256
const OUTPUT := "res://.godot/grass_night_lighting"
var camera: Camera3D
var viewport: SubViewport
var grass: MultiMeshInstance3D
var material: ShaderMaterial
var reference_material: ShaderMaterial
var environment: Environment
var sun: DirectionalLight3D
var secondary: DirectionalLight3D
var point: OmniLight3D
var failed := false

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null: fail("RenderingDevice required"); return
	call_deferred("run")

func run() -> void:
	setup()
	configure_gate(true, 1.0); grass.material_override = material
	var day := await capture("day_gated")
	grass.material_override = reference_material
	var day_reference := await capture("day_engine_reference")
	var reference_mask := foreground_mask(day_reference)
	var day_error := masked_relative_difference(day, day_reference, reference_mask)
	print("GRASS_NIGHT_LIGHTING day_relative_rgb_error=%.6f" % day_error)
	grass.material_override = material
	if not require(day_error < .005, "gated daytime BRDF diverged from engine reference"): return
	grass.material_override = material; configure_gate(false, 0.0)
	var disabled := await capture("gate_disabled")
	if not require(masked_relative_difference(disabled, day_reference, reference_mask) < .005, "disabled gate diverged from engine reference"): return
	configure_gate(true, 0.0)
	var night_front := await capture("night_frontlit_primary")
	sun.rotation_degrees = Vector3(55.0, 205.0, 0.0); configure_gate(true, 0.0)
	var night_back := await capture("night_backlit_primary")
	if not require(masked_luminance(night_front, reference_mask) < masked_luminance(day, reference_mask) * .18 and masked_luminance(night_back, reference_mask) < masked_luminance(day, reference_mask) * .18, "night gate retained primary diffuse or backlight"): return
	secondary.visible = true
	var secondary_lit := await capture("night_secondary_directional")
	if not require(masked_luminance(secondary_lit, reference_mask) > masked_luminance(night_back, reference_mask) + .02, "secondary directional was suppressed"): return
	secondary.visible = false; sun.visible = false; point.visible = true
	var point_only := await capture("night_point_only")
	if not require(masked_luminance(point_only, reference_mask) > .01, "point light was suppressed"): return
	grass.material_override = reference_material
	var point_reference := await capture("point_engine_reference")
	if not require(masked_relative_difference(point_only, point_reference, reference_mask) < .005, "point-light BRDF diverged from engine reference"): return
	grass.material_override = material
	point.visible = false; environment.ambient_light_energy = .5
	var ambient_only := await capture("night_ambient_only")
	if not require(masked_luminance(ambient_only, reference_mask) > .002, "ambient was suppressed"): return
	grass.material_override = reference_material
	var ambient_reference := await capture("ambient_engine_reference")
	if not require(masked_relative_difference(ambient_only, ambient_reference, reference_mask) < .005, "ambient changed under custom light"): return
	grass.material_override = material
	environment.ambient_light_energy = 0.0; sun.visible = true; sun.light_energy = 0.0; configure_gate(true, 1.0)
	var eclipse_totality := await capture("eclipse_totality")
	if not require(foreground_luminance(eclipse_totality) < .002, "zero-energy eclipse primary still lit grass"): return
	write_report(day, day_reference, night_front, night_back, secondary_lit, point_only, ambient_only, eclipse_totality)
	await verify_ground_facing_lighting()
	if failed: return
	print("GRASS_NIGHT_LIGHTING=PASS legacy BRDF and default two-face ground lighting"); quit(0)

func verify_ground_facing_lighting() -> void:
	# A symmetric vertical ribbon exposes both front/back and rotated side
	# shading without clump overlap or changing the planted ring-surface up.
	var ribbon := QuadMesh.new(); ribbon.size = Vector2(.6, .8)
	grass.multimesh.mesh = ribbon
	grass.multimesh.set_instance_transform(0, Transform3D(Basis.IDENTITY, Vector3(0, .5, 0)))
	grass.material_override = material
	material.set_shader_parameter("grass_blade_shading_enabled", false)
	material.set_shader_parameter("grass_base_color", Vector3(.2, .42, .1))
	material.set_shader_parameter("grass_tip_color", Vector3(.2, .42, .1))
	environment.ambient_light_energy = 0.0
	secondary.visible = false; point.visible = false
	sun.visible = true; sun.light_energy = 4.0; sun.rotation_degrees = Vector3(-55, 25, 0)
	configure_gate(true, 1.0)
	camera.position = Vector3(0, .5, 5); camera.look_at(Vector3(0, .5, 0), Vector3.UP)
	var front := await capture("default_ground_front")
	camera.position.z = -5; camera.look_at(Vector3(0, .5, 0), Vector3.UP)
	var back := await capture("default_ground_back")
	grass.multimesh.set_instance_transform(0, Transform3D(Basis(Vector3.UP, .8), Vector3(0, .5, 0)))
	var rotated := await capture("default_ground_rotated")
	var front_level := interior_luminance(front)
	var back_level := interior_luminance(back)
	var rotated_level := interior_luminance(rotated)
	if not require(front_level > .05 and absf(front_level-back_level) / front_level < .03 and absf(front_level-rotated_level) / front_level < .03, "default blade faces differed: front=%s back=%s rotated=%s" % [front_level, back_level, rotated_level]): return
	grass.multimesh.set_instance_transform(0, Transform3D(Basis.IDENTITY, Vector3(0, .5, 0)))
	material.set_shader_parameter("grass_blade_shading_enabled", true)
	camera.position.z = 5; camera.look_at(Vector3(0, .5, 0), Vector3.UP)
	var legacy_front := await capture("legacy_blade_front")
	# Reverse the authored blade normal explicitly, so legacy restoration
	# does not depend on the renderer's handling of cull-disabled backfaces.
	grass.multimesh.set_instance_transform(0, Transform3D(Basis(Vector3.UP, PI), Vector3(0, .5, 0)))
	var legacy_back := await capture("legacy_blade_back")
	grass.multimesh.set_instance_transform(0, Transform3D(Basis.IDENTITY, Vector3(0, .5, 0)))
	var face_mask := foreground_mask(front)
	if not require(absf(masked_luminance(legacy_front, face_mask)-masked_luminance(legacy_back, face_mask)) > front_level * .1, "legacy shading did not restore directional blade sides"): return
	material.set_shader_parameter("grass_blade_shading_enabled", false)
	configure_gate(true, 0.0)
	var default_night := await capture("default_ground_night")
	if not require(masked_luminance(default_night, face_mask) < .002, "default ground-facing grass retained primary light at night"): return
	sun.visible = false; point.visible = true
	var default_point := await capture("default_ground_local_light")
	if not require(masked_luminance(default_point, face_mask) > .02, "default ground-facing grass suppressed local lights"): return
	point.visible = false; sun.visible = true; sun.light_energy = 0.0; configure_gate(true, 1.0)
	var default_eclipse := await capture("default_ground_eclipse")
	if not require(masked_luminance(default_eclipse, face_mask) < .002, "default ground-facing grass emitted light during totality"): return
	print("GRASS_GROUND_LIGHTING front=%s back=%s rotated=%s" % [front_level, back_level, rotated_level])

func interior_luminance(image: Image) -> float:
	var total := 0.0; var count := 0
	for y in range(1, SIZE - 1):
		for x in range(1, SIZE - 1):
			var interior := true
			for dy in range(-1, 2):
				for dx in range(-1, 2):
					if image.get_pixel(x + dx, y + dy).get_luminance() <= .002: interior = false
			if interior: total += image.get_pixel(x, y).get_luminance(); count += 1
	return total / maxi(1, count)

func setup() -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT))
	viewport = SubViewport.new(); viewport.size = Vector2i(SIZE, SIZE); viewport.own_world_3d = true; viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS; root.add_child(viewport)
	var layer := CanvasLayer.new(); var consumer := TextureRect.new(); consumer.texture = viewport.get_texture(); consumer.size = Vector2(SIZE, SIZE); consumer.visible = true; layer.add_child(consumer); root.add_child(layer)
	environment = Environment.new(); environment.background_mode = Environment.BG_COLOR; environment.background_color = Color.BLACK; environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR; environment.ambient_light_color = Color.WHITE; environment.ambient_light_energy = 0.0
	var world := WorldEnvironment.new(); world.environment = environment; viewport.add_child(world)
	camera = Camera3D.new(); camera.current = true; camera.projection = Camera3D.PROJECTION_ORTHOGONAL; camera.size = 2.0; camera.far = 200.0; viewport.add_child(camera); camera.position = Vector3(0.0, .5, 5.0); camera.look_at(Vector3(0.0, .5, 0.0), Vector3.UP)
	grass = MultiMeshInstance3D.new(); grass.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF; viewport.add_child(grass)
	var multi := MultiMesh.new(); multi.transform_format = MultiMesh.TRANSFORM_3D; multi.use_colors = true; multi.use_custom_data = true; multi.mesh = DRIVER.new().call("LoadMeadowMesh"); multi.instance_count = 1; multi.set_instance_transform(0, Transform3D.IDENTITY); multi.set_instance_color(0, Color(0, 0, 0, 0)); multi.set_instance_custom_data(0, Color(.5, .5, .1, .2)); grass.multimesh = multi
	material = make_material(false); reference_material = make_material(true)
	sun = DirectionalLight3D.new(); sun.light_color = Color.WHITE; sun.light_energy = 4.0; sun.light_angular_distance = 0.0; sun.shadow_enabled = false; sun.rotation_degrees = Vector3(-55.0, 25.0, 0.0); viewport.add_child(sun)
	print("GRASS_NIGHT_LIGHTING sun_angular_distance=%s" % str(sun.light_angular_distance))
	secondary = DirectionalLight3D.new(); secondary.light_color = Color(.55, .68, 1.0); secondary.light_energy = 2.5; secondary.rotation_degrees = Vector3(-45.0, -55.0, 0.0); secondary.visible = false; viewport.add_child(secondary)
	point = OmniLight3D.new(); point.light_color = Color(1.0, .82, .58); point.light_energy = 8.0; point.omni_range = 6.0; point.position = Vector3(0.0, 1.2, 1.2); point.visible = false; viewport.add_child(point)

func make_material(engine_reference: bool) -> ShaderMaterial:
	var shader := Shader.new(); var source := FileAccess.get_file_as_string("res://materials/ring_standing_grass.gdshader")
	if engine_reference:
		source = source.replace("#include \"res://materials/ring_grass_lighting.gdshaderinc\"\n", "")
		var light_start := source.find("\nvoid light() {")
		if light_start >= 0: source = source.substr(0, light_start) + "\n"
	shader.code = source
	var output := ShaderMaterial.new(); output.shader = shader; output.set_shader_parameter("local_cloud_shadow_enabled", false); output.set_shader_parameter("ring_local_wind_enabled", false); output.set_shader_parameter("grass_lod_enabled", false); output.set_shader_parameter("grass_appearance_enabled", false); output.set_shader_parameter("grass_blade_shading_enabled", true); output.set_shader_parameter("grass_base_color", Vector3(.20, .42, .10)); output.set_shader_parameter("grass_tip_color", Vector3(.34, .66, .16)); return output

func configure_gate(enabled: bool, day_factor: float) -> void:
	material.set_shader_parameter("grass_primary_sun_gate_enabled", enabled); material.set_shader_parameter("grass_primary_sun_direction", sun.global_transform.basis.z.normalized()); material.set_shader_parameter("grass_primary_sun_day_factor", day_factor); material.set_shader_parameter("grass_primary_sun_night_direct", 0.0); material.set_shader_parameter("grass_primary_sun_day_direct", 1.0)

func capture(label: String) -> Image:
	for i in range(4): await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image(); image.save_png(ProjectSettings.globalize_path(OUTPUT.path_join(label + ".png"))); return image

func foreground_luminance(image: Image) -> float:
	var total := 0.0; var count := 0
	for y in range(SIZE):
		for x in range(SIZE):
			var pixel := image.get_pixel(x, y)
			if pixel.get_luminance() > .002: total += pixel.get_luminance(); count += 1
	return total / max(count, 1)

func masked_luminance(image: Image, mask: PackedByteArray) -> float:
	var total := 0.0
	var count := 0
	for y in range(SIZE):
		for x in range(SIZE):
			if mask[y * SIZE + x] == 0: continue
			total += image.get_pixel(x, y).get_luminance()
			count += 1
	return total / max(count, 1)

func foreground_mask(reference: Image) -> PackedByteArray:
	var mask := PackedByteArray()
	mask.resize(SIZE * SIZE)
	for y in range(SIZE):
		for x in range(SIZE):
			mask[y * SIZE + x] = 1 if reference.get_pixel(x, y).get_luminance() > .002 else 0
	return mask

func masked_relative_difference(actual: Image, reference: Image, mask: PackedByteArray) -> float:
	var difference := 0.0
	var reference_energy := 0.0
	for y in range(SIZE):
		for x in range(SIZE):
			if mask[y * SIZE + x] == 0: continue
			var actual_pixel := actual.get_pixel(x, y)
			var reference_pixel := reference.get_pixel(x, y)
			difference += absf(actual_pixel.r - reference_pixel.r) + absf(actual_pixel.g - reference_pixel.g) + absf(actual_pixel.b - reference_pixel.b)
			reference_energy += absf(reference_pixel.r) + absf(reference_pixel.g) + absf(reference_pixel.b)
	return difference / max(reference_energy, .000001)

func write_report(day: Image, reference: Image, night_front: Image, night_back: Image, secondary_lit: Image, point_only: Image, ambient_only: Image, eclipse_totality: Image) -> void:
	var mask := foreground_mask(reference)
	var report := {"diffuse_mode": "lambert", "day": masked_luminance(day, mask), "engine_reference": masked_luminance(reference, mask), "day_relative_rgb_error": masked_relative_difference(day, reference, mask), "night_front": masked_luminance(night_front, mask), "night_back": masked_luminance(night_back, mask), "secondary_directional": masked_luminance(secondary_lit, mask), "point": masked_luminance(point_only, mask), "ambient": masked_luminance(ambient_only, mask), "eclipse_totality": masked_luminance(eclipse_totality, mask)}
	var file := FileAccess.open(ProjectSettings.globalize_path(OUTPUT.path_join("report.json")), FileAccess.WRITE); file.store_string(JSON.stringify(report, "\t") + "\n")
func require(condition: bool, message: String) -> bool:
	if condition: return true
	fail(message); return false
func fail(message: String) -> void:
	if failed: return
	failed = true; push_error("GRASS_NIGHT_LIGHTING=FAIL " + message); quit(1)
