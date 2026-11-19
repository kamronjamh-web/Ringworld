extends SceneTree

## FL-2 (docs/forest-lighting-plan.md sections 3 and 4): leaf translucency and crown self-occlusion on the tree shaders, switch off and on, mesh against impostor.
## Fixture: tools/tree_billboard_match_gpu_test.gd (one variant per species, the authored LOD2 mesh, the shared crossed cards at 325 m and the far quad at 900 m with the lens narrowed so the tree
## keeps its pixel size, the shaders as shipped with simplified tree shading, haze off, no clouds). The tree shadow distance is published as 0 (ringworld_forest_lighting.y), so the crown_sun
## term is analytic for the mesh and for the cards alike. Each measurement is the mean linear radiance of the foreground (tree) pixels over four tree yaws.
## Cases (sun 20 degrees above the horizon, camera level): backlit (sun behind the tree), frontlit (sun behind the camera), side (90 degrees), ambient (sun off, white ambient).
## Checks:
##   (a) off -> on -> off gives a byte-identical off frame (every variant, kind, case and yaw);
##   (b) backlit on > off for mesh, cards and quad; frontlit |on - off| / off < 2 %;
##   (c) mesh against cards, per variant: the backlit increase (on - off) agrees within 10 % (FL-2c: mesh and impostor share one statistical crown model, so they agree by construction); ambient only: the crown occlusion ratios (on / off) agree within 10 %;
##   (d) physical bound: backlit-on mean <= frontlit-on mean (mesh, cards, quad).
## The quad is gated for (a), (b) and (d); its (c) numbers are printed only. Also printed: the mean linear foliage albedo per species.
## Environment FOREST_LIGHTING_HEAD_DIR = a folder holding imported_tree.gdshader and imported_tree_impostor.gdshader as they were before FL-2 (git show 5457bb2~1:materials/...): after the checks the
## shaders are swapped for those and every off frame must equal the new shaders' off frame byte for byte (the byte-identity claim of the switch-off path).
## Stills: .godot/audit_baseline/common/forest-lighting/fl2/ (yaw 0, off and on per case, mesh, cards, quad, and one contact sheet).
## Run: tools/run_tests.ps1 -Tests forest_lighting_tree_gpu_test -Label fl2
## Prints FOREST_LIGHTING_TREE_GPU_TEST=PASS|FAIL.
const TEST := preload("res://tools/ForestLightingTreeGpuTest.cs")
const SIZE := 128
const FOV_DEGREES := 8.3
const CARD_DISTANCE := 325.0
const QUAD_DISTANCE := 900.0
const BACKGROUND := Color(1.0, 0.0, 1.0, 1.0)
const GREY := Color(0.30, 0.31, 0.33, 1.0)
const UP := Vector3(0, 0, 1) # the tree's surface-up in the test world (ring_center lies far along +Z)
const VARIANTS := [3, 13, 19] # one per species: maple, fir, pine
const SPECIES_NAMES := ["maple", "fir", "pine"]
const HEIGHTS := [18.0, 11.0, 20.0] # the strip's typical heights per species, as in tree_billboard_match_gpu_test
const KINDS := ["mesh", "card", "quad"]
const SUN_ELEVATION := 20.0
const CASES := ["backlit", "frontlit", "side", "ambient"]
const CASE_AZIMUTH := {"backlit": 180.0, "frontlit": 0.0, "side": 90.0, "ambient": 0.0} # camera azimuth, degrees; the sun is at azimuth 0
const YAWS := [0.0, 90.0, 180.0, 270.0]
const OFF := Vector4(0, 0, 0, 0)
const ON := Vector4(1, 0, 0, 1) # translucency on, tree shadow distance 0 (analytic crown regime), canopy shade off, crown occlusion on
const FRONT_TOLERANCE := 0.02
const INCREASE_TOLERANCE := 0.10
const AMBIENT_TOLERANCE := 0.10
const OUT := "res://.godot/audit_baseline/common/forest-lighting/fl2"
const TILE := 192

var failed := false
var done := false
var viewport: SubViewport
var environment: Environment
var camera: Camera3D
var sun: DirectionalLight3D
var driver: Node
var helper
var mesh_instance: MeshInstance3D
var card_instance: MultiMeshInstance3D
var quad_instance: MultiMeshInstance3D
var card_multi: MultiMesh
var quad_multi: MultiMesh
var lod2_meshes: Array[Mesh] = []
var materials: Array[ShaderMaterial] = []
var impostor_materials: Array[ShaderMaterial] = []
var failures: Array[String] = []
var stills := {} # "species_kind_case_state" -> Image (yaw 0)
var off_frames := {} # key -> PackedByteArray (yaw 0..270 off frames of the new shaders)

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice required; Dummy renderer is invalid")
		return
	call_deferred("_run")

func _run() -> void:
	create_timer(900.0).timeout.connect(_on_watchdog)
	_make_scene()
	if failed: return
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT))
	# Foliage albedo per species.
	for s in 3:
		var a: PackedFloat32Array = helper.call("GetFoliageAlbedoMean", VARIANTS[s])
		print("FL2 foliage albedo %s (variant %d): mean linear r %.4f g %.4f b %.4f over alpha-covered texels (%.1f %% of texels), tint (%.3f, %.3f, %.3f)" % [SPECIES_NAMES[s], VARIANTS[s], a[0], a[1], a[2], a[3] * 100.0, a[4], a[5], a[6]])
	for s in 3:
		var lad_row: Vector4 = helper.call("GetHullRows", VARIANTS[s])[2]
		print("FL2 leaf inclination weights %s (variant %d): classes 11.25/33.75/56.25/78.75 deg = (%.4f, %.4f, %.4f, %.4f)" % [SPECIES_NAMES[s], VARIANTS[s], lad_row.x, lad_row.y, lad_row.z, lad_row.w])
	# Measurements: result[species][kind][case] = {"off": lum, "on": lum, "off_rgb", "on_rgb"} (means over the yaws).
	var results := []
	var identity_bad := 0
	var identity_total := 0
	for s in 3:
		var per_kind := {}
		for kind: String in KINDS:
			var per_case := {}
			for case_name: String in CASES:
				var off_sum := Vector3.ZERO
				var on_sum := Vector3.ZERO
				var off_lum := 0.0
				var on_lum := 0.0
				var covered_off := 0.0
				var covered_on := 0.0
				for yaw_index in YAWS.size():
					var yaw: float = YAWS[yaw_index]
					_set_switches(OFF)
					var off_a := await _shot(kind, s, case_name, yaw)
					_set_switches(ON)
					var on_shot := await _shot(kind, s, case_name, yaw)
					_set_switches(OFF)
					var off_b := await _shot(kind, s, case_name, yaw)
					if failed: return
					identity_total += 1
					if off_a["image"].get_data() != off_b["image"].get_data():
						identity_bad += 1
						failures.append("(a) %s %s %s yaw %d: off frame differs after off -> on -> off" % [SPECIES_NAMES[s], kind, case_name, int(yaw)])
					off_frames["%d_%s_%s_%d" % [s, kind, case_name, yaw_index]] = (off_a["image"] as Image).get_data()
					off_sum += Vector3(off_a["rgb"][0], off_a["rgb"][1], off_a["rgb"][2]); on_sum += Vector3(on_shot["rgb"][0], on_shot["rgb"][1], on_shot["rgb"][2])
					off_lum += off_a["lum"]; on_lum += on_shot["lum"]
					covered_off += off_a["covered"]; covered_on += on_shot["covered"]
					if yaw_index == 0:
						stills["%d_%s_%s_off" % [s, kind, case_name]] = off_a["image"]
						stills["%d_%s_%s_on" % [s, kind, case_name]] = on_shot["image"]
				var n := float(YAWS.size())
				per_case[case_name] = {"off": off_lum / n, "on": on_lum / n, "off_rgb": off_sum / n, "on_rgb": on_sum / n, "covered_off": covered_off / n, "covered_on": covered_on / n}
			per_kind[kind] = per_case
		results.append(per_kind)
	print("FL2 (a) identity: %d of %d off -> on -> off frames differ" % [identity_bad, identity_total])
	_print_and_check(results)
	_save_stills()
	if OS.get_environment("FOREST_LIGHTING_HEAD_DIR") != "":
		await _head_identity(OS.get_environment("FOREST_LIGHTING_HEAD_DIR"))
	if failed: return
	for message in failures: print("FOREST_LIGHTING_TREE_GPU_TEST failure: " + message)
	done = true
	if failures.is_empty():
		print("FOREST_LIGHTING_TREE_GPU_TEST=PASS")
		quit(0)
	else:
		print("FOREST_LIGHTING_TREE_GPU_TEST=FAIL %d failures" % failures.size())
		quit(1)

func _print_and_check(results: Array) -> void:
	for s in 3:
		var name: String = SPECIES_NAMES[s]
		for kind: String in KINDS:
			for case_name: String in CASES:
				var r: Dictionary = results[s][kind][case_name]
				var rgb_off: Vector3 = r["off_rgb"]
				var rgb_on: Vector3 = r["on_rgb"]
				print("FL2 %s %s %s: off lum %.5f on lum %.5f (on/off %.4f, diff %.5f); off rgb (%.4f %.4f %.4f) on rgb (%.4f %.4f %.4f); covered off %.0f on %.0f px" % [name, kind, case_name,
					r["off"], r["on"], r["on"] / maxf(r["off"], 1e-9), r["on"] - r["off"], rgb_off.x, rgb_off.y, rgb_off.z, rgb_on.x, rgb_on.y, rgb_on.z, r["covered_off"], r["covered_on"]])
		for kind: String in KINDS:
			var back: Dictionary = results[s][kind]["backlit"]
			var front: Dictionary = results[s][kind]["frontlit"]
			# (b)
			if not (back["on"] > back["off"]): failures.append("(b) %s %s backlit on %.5f is not above off %.5f" % [name, kind, back["on"], back["off"]])
			var front_change := absf(front["on"] - front["off"]) / maxf(front["off"], 1e-9)
			print("FL2 (b) %s %s: backlit increase %.5f (%.2f %%), frontlit change %.3f %%" % [name, kind, back["on"] - back["off"], 100.0 * (back["on"] - back["off"]) / maxf(back["off"], 1e-9), front_change * 100.0])
			if front_change >= FRONT_TOLERANCE: failures.append("(b) %s %s frontlit change %.3f %% reaches %.0f %%" % [name, kind, front_change * 100.0, FRONT_TOLERANCE * 100.0])
			# (d)
			print("FL2 (d) %s %s: backlit-on %.5f frontlit-on %.5f" % [name, kind, back["on"], front["on"]])
			if back["on"] > front["on"]: failures.append("(d) %s %s backlit-on %.5f exceeds frontlit-on %.5f" % [name, kind, back["on"], front["on"]])
		# (c)
		for other: String in ["card", "quad"]:
			var gated: bool = other == "card"
			var increase_mesh: float = results[s]["mesh"]["backlit"]["on"] - results[s]["mesh"]["backlit"]["off"]
			var increase_other: float = results[s][other]["backlit"]["on"] - results[s][other]["backlit"]["off"]
			var increase_ratio: float = increase_mesh / increase_other if absf(increase_other) > 1e-9 else INF
			var ambient_mesh: float = results[s]["mesh"]["ambient"]["on"] / maxf(results[s]["mesh"]["ambient"]["off"], 1e-9)
			var ambient_other: float = results[s][other]["ambient"]["on"] / maxf(results[s][other]["ambient"]["off"], 1e-9)
			var ambient_ratio := ambient_mesh / maxf(ambient_other, 1e-9)
			print("FL2 (c) %s mesh vs %s: backlit increase mesh %.5f %s %.5f (mesh/%s %.3f, gate %.0f %%%s); ambient on/off mesh %.4f %s %.4f (mesh/%s %.3f, gate %.0f %%%s)" % [name, other, increase_mesh, other, increase_other, other, increase_ratio, INCREASE_TOLERANCE * 100.0, "" if gated else ", report only",
				ambient_mesh, other, ambient_other, other, ambient_ratio, AMBIENT_TOLERANCE * 100.0, "" if gated else ", report only"])
			if gated:
				if not (absf(increase_ratio - 1.0) <= INCREASE_TOLERANCE): failures.append("(c) %s mesh/%s backlit increase ratio %.3f outside %.0f %%" % [name, other, increase_ratio, INCREASE_TOLERANCE * 100.0])
				if not (absf(ambient_ratio - 1.0) <= AMBIENT_TOLERANCE): failures.append("(c) %s mesh/%s ambient occlusion ratio %.3f outside %.0f %%" % [name, other, ambient_ratio, AMBIENT_TOLERANCE * 100.0])

# Swaps both tree shaders for the pre-FL-2 versions and compares every off frame with the new shaders' off frame.
func _head_identity(directory: String) -> void:
	var mesh_code := FileAccess.get_file_as_string(directory.path_join("imported_tree.gdshader"))
	var card_code := FileAccess.get_file_as_string(directory.path_join("imported_tree_impostor.gdshader"))
	if mesh_code.is_empty() or card_code.is_empty():
		_fail("FOREST_LIGHTING_HEAD_DIR %s lacks the two HEAD shaders" % directory)
		return
	if mesh_code.contains("ring_forest_lighting") or card_code.contains("ring_forest_lighting"):
		_fail("the HEAD shaders in %s already contain the forest lighting code" % directory)
		return
	var mesh_shader := Shader.new()
	mesh_shader.code = mesh_code
	var card_shader := Shader.new()
	card_shader.code = card_code
	var original := {}
	for material in materials: original[material] = material.shader; material.shader = mesh_shader
	for material in impostor_materials: original[material] = material.shader; material.shader = card_shader
	_set_switches(OFF)
	var total := 0
	var differing := 0
	var head_still_index := 0
	for s in 3:
		for kind: String in KINDS:
			for case_name: String in CASES:
				for yaw_index in YAWS.size():
					var head_shot := await _shot(kind, s, case_name, YAWS[yaw_index])
					if failed: return
					total += 1
					if (head_shot["image"] as Image).get_data() != off_frames["%d_%s_%s_%d" % [s, kind, case_name, yaw_index]]:
						differing += 1
						failures.append("identity: %s %s %s yaw %d differs between the HEAD shaders and the new shaders with the switches off" % [SPECIES_NAMES[s], kind, case_name, int(YAWS[yaw_index])])
					if yaw_index == 0 and case_name in ["backlit", "ambient"]:
						(head_shot["image"] as Image).save_png(ProjectSettings.globalize_path(OUT).path_join("head_%s_%s_%s_off.png" % [SPECIES_NAMES[s], kind, case_name]))
						head_still_index += 1
	for material in original: material.shader = original[material]
	print("FL2 identity against the HEAD shaders (switches off): %d of %d frames differ" % [differing, total])

func _save_stills() -> void:
	var directory := ProjectSettings.globalize_path(OUT)
	for key: String in stills:
		var parts := key.split("_")
		var image := (stills[key] as Image).duplicate() as Image
		image.save_png(directory.path_join("%s_%s_%s_%s.png" % [SPECIES_NAMES[int(parts[0])], parts[1], parts[2], parts[3]]))
	# Contact sheet: rows species x case, columns kind x (off, on), grey background in place of the measuring magenta.
	var sheet := Image.create(TILE * 6, TILE * 12, false, Image.FORMAT_RGBA8)
	sheet.fill(GREY)
	var row := 0
	for s in 3:
		for case_name: String in CASES:
			var column := 0
			for kind: String in KINDS:
				for state: String in ["off", "on"]:
					var tile := (stills["%d_%s_%s_%s" % [s, kind, case_name, state]] as Image).duplicate() as Image
					tile.convert(Image.FORMAT_RGBA8)
					for y in tile.get_height():
						for x in tile.get_width():
							if tile.get_pixel(x, y).is_equal_approx(BACKGROUND): tile.set_pixel(x, y, GREY)
					tile.resize(TILE, TILE, Image.INTERPOLATE_NEAREST)
					sheet.blit_rect(tile, Rect2i(0, 0, TILE, TILE), Vector2i(column * TILE, row * TILE))
					column += 1
			row += 1
	var path := directory.path_join("contact_sheet.png")
	sheet.save_png(path)
	print("FL2 contact sheet: %s (rows: maple, fir, pine x backlit, frontlit, side, ambient; columns: mesh off, mesh on, cards off, cards on, quad off, quad on)" % path)

func _set_switches(value: Vector4) -> void:
	RenderingServer.global_shader_parameter_set("ringworld_forest_lighting", value)

## One capture: returns {"image", "rgb", "lum", "covered" (pixels)}.
func _shot(kind: String, species: int, case_name: String, yaw_degrees: float) -> Dictionary:
	var variant: int = VARIANTS[species]
	var ambient := case_name == "ambient"
	var elevation := deg_to_rad(90.0 if ambient else SUN_ELEVATION)
	var sun_direction := Vector3(cos(elevation), 0.0, sin(elevation))
	sun.transform = Transform3D(Basis.looking_at(-sun_direction, Vector3.UP), Vector3.ZERO)
	sun.visible = not ambient
	environment.ambient_light_energy = 1.0 if ambient else 0.0
	for material in materials: material.set_shader_parameter("sun_direction", sun_direction)
	for material in impostor_materials: material.set_shader_parameter("sun_direction", sun_direction)
	var distance := QUAD_DISTANCE if kind == "quad" else CARD_DISTANCE
	camera.fov = rad_to_deg(2.0 * atan(tan(deg_to_rad(FOV_DEGREES * 0.5)) * CARD_DISTANCE / distance))
	var azimuth := deg_to_rad(float(CASE_AZIMUTH[case_name]))
	var height: float = HEIGHTS[species]
	var middle := UP * height * 0.5
	var position := middle + Vector3(cos(azimuth), sin(azimuth), 0.0) * distance
	camera.transform = Transform3D(Basis.looking_at(middle - position, UP), position)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_camera_world_position", position)
	mesh_instance.visible = kind == "mesh"
	card_instance.visible = kind == "card"
	quad_instance.visible = kind == "quad"
	var placement := Transform3D((Basis(Vector3.RIGHT, PI * 0.5) * Basis(Vector3.UP, deg_to_rad(yaw_degrees))).scaled(Vector3.ONE * height), Vector3.ZERO)
	if kind == "mesh":
		mesh_instance.mesh = lod2_meshes[variant]
		mesh_instance.transform = placement
		var rows = helper.call("GetHullRows", variant)
		mesh_instance.set_instance_shader_parameter("tree_crown_hull", rows[0])
		mesh_instance.set_instance_shader_parameter("tree_crown_hull_extra", rows[1])
		mesh_instance.set_instance_shader_parameter("tree_crown_lad", rows[2])
	else:
		var multi: MultiMesh = card_multi if kind == "card" else quad_multi
		multi.set_instance_transform(0, placement)
		multi.set_instance_custom_data(0, Color(float(variant), 1.0, 0.0, 0.0))
	for i in 3: await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	if image == null or image.is_empty():
		_fail("empty capture %s variant %d" % [kind, variant])
		return {}
	var stats: PackedFloat32Array = driver.call("MeasureForeground", image, BACKGROUND)
	return {"image": image, "rgb": [stats[1], stats[2], stats[3]], "lum": stats[4], "covered": stats[0]}

# --- scene (the fixture of tree_billboard_match_gpu_test.gd) ----------------------------------------------------------------------------------------------------

func _make_scene() -> void:
	viewport = SubViewport.new()
	viewport.size = Vector2i(SIZE, SIZE)
	viewport.own_world_3d = true
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	environment = Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = BACKGROUND
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.ambient_light_color = Color(1.0, 1.0, 1.0)
	environment.ambient_light_energy = 0.0
	environment.reflected_light_source = Environment.REFLECTION_SOURCE_DISABLED
	environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	var world := WorldEnvironment.new()
	world.environment = environment
	viewport.add_child(world)
	camera = Camera3D.new()
	camera.fov = FOV_DEGREES
	camera.near = 1.0
	camera.far = 5000.0
	camera.current = true
	viewport.add_child(camera)
	sun = DirectionalLight3D.new()
	sun.light_energy = 1.0
	sun.shadow_enabled = false
	viewport.add_child(sun)
	driver = load("res://scripts/Vegetation/ImportedTreeCatalogDriver.cs").new()
	root.add_child(driver)
	helper = TEST.new()
	var tree_basis := Basis(Vector3.RIGHT, PI * 0.5)
	mesh_instance = MeshInstance3D.new()
	mesh_instance.transform = Transform3D(tree_basis, Vector3.ZERO)
	mesh_instance.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	viewport.add_child(mesh_instance)
	var card_mesh := driver.call("LoadSharedImpostorMesh") as Mesh
	var quad_mesh := driver.call("LoadFarQuadMesh") as Mesh
	if card_mesh == null or quad_mesh == null:
		_fail("shared impostor assets unavailable")
		return
	card_multi = _multi(card_mesh)
	quad_multi = _multi(quad_mesh)
	card_instance = _multi_instance(card_multi)
	quad_instance = _multi_instance(quad_multi)
	for variant in 30:
		var mesh := driver.call("LoadVariantMesh", variant, 2) as Mesh
		if mesh == null:
			_fail("LOD2 mesh missing for variant %d" % variant)
			return
		lod2_meshes.append(mesh)
		for surface in mesh.get_surface_count():
			var material := mesh.surface_get_material(surface) as ShaderMaterial
			if material != null and not materials.has(material): materials.append(material)
	impostor_materials = [driver.call("LoadSharedImpostorMaterial"), driver.call("LoadFarQuadMaterial")]
	_publish_frame_globals()
	for material in materials + impostor_materials: _configure(material)

func _multi(mesh: Mesh) -> MultiMesh:
	var multi := MultiMesh.new()
	multi.transform_format = MultiMesh.TRANSFORM_3D
	multi.use_custom_data = true
	multi.mesh = mesh
	multi.instance_count = 1
	multi.set_instance_transform(0, Transform3D.IDENTITY)
	multi.set_instance_custom_data(0, Color(0, 1, 0, 0))
	return multi

func _multi_instance(multi: MultiMesh) -> MultiMeshInstance3D:
	var instance := MultiMeshInstance3D.new()
	instance.multimesh = multi
	instance.cast_shadow = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	instance.custom_aabb = AABB(Vector3(-2, -1, -2), Vector3(4, 4, 4))
	viewport.add_child(instance)
	return instance

func _publish_frame_globals() -> void:
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
	RenderingServer.global_shader_parameter_set("ring_atmosphere_to_sun", Vector3.LEFT)
	_set_switches(OFF)

func _configure(material: ShaderMaterial) -> void:
	material.set_shader_parameter("ring_center", Vector3(0.0, 0.0, 1000000.0)) # surface_up = +Z at the tree
	material.set_shader_parameter("sun_direction", Vector3(1, 0, 0))
	material.set_shader_parameter("vegetation_wind_enabled", false)
	material.set_shader_parameter("simplified_tree_shading", true)
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
	push_error("FOREST_LIGHTING_TREE_GPU_TEST=FAIL " + message)
	print("FOREST_LIGHTING_TREE_GPU_TEST=FAIL " + message)
	quit(1)

func _on_watchdog() -> void:
	if not done: _fail("watchdog timeout")
