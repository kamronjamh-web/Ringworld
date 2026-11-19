extends SceneTree

## T6-8 (docs/work-orders/terrain-vegetation/T6.md, design A9): per-instance and per-stand foliage tint on the tree shaders (materials/ring_foliage_tint.gdshaderinc), mesh LOD2 against the LOD3
## impostor (shared crossed cards at 325 m, far quad at 900 m with the lens narrowed so the tree keeps its pixel size). Fixture: tools/forest_lighting_tree_gpu_test.gd (simplified shading, haze off, no
## clouds, ambient light only so a tint multiplies the radiance and the mesh / card shading difference cancels in an on / off ratio). The mesh is a one-instance MultiMesh like the GPU output rows, so
## INSTANCE_CUSTOM.z carries the rank; every kind of one root uses the same root position and rank.
## Globals: ringworld_world_features (bit 6), ringworld_foliage_tint = (strength, anchor around, anchor across, published) and ringworld_foliage_frame (identity, ring radius 1e6 in element [3][3]).
## The "mesh" kind draws the LOD2 mesh with its trunk and branch surfaces (not alpha cutout) black: the tint applies to foliage only, and those surfaces would dilute the on / off ratio
## against the cards (which are tinted over the whole crown height). The full mesh is measured too ("mesh_full") and printed.
## Checks (each variant: maple 3 and pine 19):
##   (a) feature off (bit 6 clear, or tint not published, or strength 0): the frames are byte-identical, kind by kind and rank by rank; with FOLIAGE_TINT_HEAD_DIR (a folder holding the pre-change
##       imported_tree.gdshader and imported_tree_impostor.gdshader, git show HEAD:materials/...) every off frame must equal the pre-change shaders' frame byte for byte (0 differing pixels);
##   (b) feature on, strength 1: the on / off luminance ratio of the same root agrees between the mesh and the cards, and between the mesh and the far quad, within 2 %; the tint is a real change (> 3 % for the strongest rank) and has the predicted sign;
##   (d) two ranks differ (> 1 %), two roots 10 m apart differ < 1 % and the stand term moves the tint across 600 m steps (> 1 % spread over six stands).
## Run: tools/run_tests.ps1 -Tests foliage_tint_gpu_test -Label t6-8
## Prints FOLIAGE_TINT_GPU=PASS|FAIL.
const TEST := preload("res://tools/FoliageTintGpuTest.cs")
const SIZE := 128
const FOV_DEGREES := 8.3
const CARD_DISTANCE := 325.0
const QUAD_DISTANCE := 900.0
const BACKGROUND := Color(1.0, 0.0, 1.0, 1.0)
const UP := Vector3(0, 0, 1)
const VARIANTS := [3, 19] # maple, pine
const HEIGHTS := [18.0, 20.0]
const KINDS := ["mesh", "card", "quad"]
const IDENTITY_KINDS := ["mesh", "mesh_full", "card", "quad"]
const FOLIAGE_BIT := 64 # 1 << 6
const FRAME_RADIUS := 1000000.0
const HAND_OFF_TOLERANCE := 0.02
const STRONG_TINT := 0.03
const RANK_DIFFERENCE := 0.01
const NEAR_TOLERANCE := 0.01
const STAND_SPREAD := 0.01
const STAND_STEP := 600.0

var failed := false
var done := false
var viewport: SubViewport
var environment: Environment
var camera: Camera3D
var sun: DirectionalLight3D
var driver: Node
var helper
var mesh_multi: MultiMesh
var mesh_instance: MultiMeshInstance3D
var card_instance: MultiMeshInstance3D
var quad_instance: MultiMeshInstance3D
var card_multi: MultiMesh
var quad_multi: MultiMesh
var lod2_meshes: Array[Mesh] = []
var trunk_tints := {} # material -> its albedo_tint, for the non-foliage (not alpha cutout) surfaces
var materials: Array[ShaderMaterial] = []
var impostor_materials: Array[ShaderMaterial] = []
var failures: Array[String] = []
var off_frames := {} # "variant_kind_rankindex" -> PackedByteArray (the new shaders, feature off)

func _initialize() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice required; Dummy renderer is invalid")
		return
	call_deferred("_run")

func _run() -> void:
	create_timer(600.0).timeout.connect(_on_watchdog)
	_make_scene()
	if failed: return
	# Ranks with the strongest predicted tint at the origin stand: the highest and the lowest multiplier of the 199 candidates.
	var best_rank := 0.5
	var worst_rank := 0.5
	var best := -1e9
	var worst := 1e9
	var stand0: float = helper.call("StandValue", 0.0, 0.0)
	for k in range(1, 200):
		var m: Vector3 = helper.call("Multiplier", 1.0, k / 200.0, stand0)
		var lum := 0.2126 * m.x + 0.7152 * m.y + 0.0722 * m.z
		if lum > best: best = lum; best_rank = k / 200.0
		if lum < worst: worst = lum; worst_rank = k / 200.0
	print("FOLIAGE_TINT stand value at the origin %.4f; rank %.3f predicted luminance factor %.4f, rank %.3f predicted %.4f" % [stand0, best_rank, best, worst_rank, worst])
	var ranks := [best_rank, worst_rank]
	# (a) feature off, several ways. Frames of the new shaders first.
	var identity_total := 0
	var identity_bad := 0
	for v in VARIANTS.size():
		for kind: String in IDENTITY_KINDS:
			for r in ranks.size():
				_set_tint(0, Vector4(1, 0, 0, 1))
				var base := await _shot(kind, v, ranks[r], Vector2.ZERO)
				if failed: return
				off_frames["%d_%s_%d" % [v, kind, r]] = base["image"].get_data()
				var variants := {
					"bit set, not published": [FOLIAGE_BIT, Vector4(1, 0, 0, 0)],
					"bit clear, published": [0, Vector4(1, 0, 0, 1)],
					"strength 0": [FOLIAGE_BIT, Vector4(0, 0, 0, 1)],
				}
				for label: String in variants:
					_set_tint(variants[label][0], variants[label][1])
					var other := await _shot(kind, v, ranks[r], Vector2.ZERO)
					if failed: return
					identity_total += 1
					if other["image"].get_data() != off_frames["%d_%s_%d" % [v, kind, r]]:
						identity_bad += 1
						failures.append("(a) variant %d %s rank %.3f: %s differs from the feature-off frame" % [VARIANTS[v], kind, ranks[r], label])
	print("FOLIAGE_TINT (a) feature-off equalities: %d of %d frames differ" % [identity_bad, identity_total])
	# (b) and (d): on / off luminance ratios.
	for v in VARIANTS.size():
		var ratio := {} # kind -> [ratio at rank best, ratio at rank worst]
		for kind: String in KINDS:
			var per_rank := []
			for r in ranks.size():
				_set_tint(0, Vector4(1, 0, 0, 1))
				var off := await _shot(kind, v, ranks[r], Vector2.ZERO)
				_set_tint(FOLIAGE_BIT, Vector4(1, 0, 0, 1))
				var on := await _shot(kind, v, ranks[r], Vector2.ZERO)
				if failed: return
				per_rank.append(on["lum"] / maxf(off["lum"], 1e-9))
			ratio[kind] = per_rank
		var full_mesh := []
		for r in ranks.size():
			_set_tint(0, Vector4(1, 0, 0, 1))
			var full_off := await _shot("mesh_full", v, ranks[r], Vector2.ZERO)
			_set_tint(FOLIAGE_BIT, Vector4(1, 0, 0, 1))
			var full_on := await _shot("mesh_full", v, ranks[r], Vector2.ZERO)
			full_mesh.append(full_on["lum"] / maxf(full_off["lum"], 1e-9))
		print("FOLIAGE_TINT (b) variant %d: full LOD2 mesh (trunk and branches untinted) on/off luminance %.4f at rank %.3f, %.4f at rank %.3f" % [VARIANTS[v], full_mesh[0], ranks[0], full_mesh[1], ranks[1]])
		for r in ranks.size():
			var mesh_ratio: float = ratio["mesh"][r]
			var predicted_m: Vector3 = helper.call("Multiplier", 1.0, ranks[r], stand0)
			var predicted := 0.2126 * predicted_m.x + 0.7152 * predicted_m.y + 0.0722 * predicted_m.z
			print("FOLIAGE_TINT (b) variant %d rank %.3f: on/off luminance mesh %.4f card %.4f quad %.4f (predicted foliage factor %.4f)" % [VARIANTS[v], ranks[r], mesh_ratio, ratio["card"][r], ratio["quad"][r], predicted])
			for other: String in ["card", "quad"]:
				var rel := absf(ratio[other][r] / mesh_ratio - 1.0)
				if rel > HAND_OFF_TOLERANCE: failures.append("(b) variant %d rank %.3f: mesh %.4f against %s %.4f differ by %.2f %% (> %.0f %%)" % [VARIANTS[v], ranks[r], mesh_ratio, other, ratio[other][r], rel * 100.0, HAND_OFF_TOLERANCE * 100.0])
			if absf(predicted - 1.0) > STRONG_TINT and signf(mesh_ratio - 1.0) != signf(predicted - 1.0):
				failures.append("(b) variant %d rank %.3f: mesh ratio %.4f has the opposite sign to the predicted factor %.4f" % [VARIANTS[v], ranks[r], mesh_ratio, predicted])
		var strongest := maxf(absf(ratio["mesh"][0] - 1.0), absf(ratio["mesh"][1] - 1.0))
		if strongest < STRONG_TINT: failures.append("(b) variant %d: the strongest tint changes the mesh by only %.2f %% (< %.0f %%)" % [VARIANTS[v], strongest * 100.0, STRONG_TINT * 100.0])
		# (d) ranks differ.
		for kind: String in KINDS:
			var rank_difference := absf(ratio[kind][0] / ratio[kind][1] - 1.0)
			print("FOLIAGE_TINT (d) variant %d %s: ranks %.3f and %.3f differ by %.2f %%" % [VARIANTS[v], kind, ranks[0], ranks[1], rank_difference * 100.0])
			if rank_difference < RANK_DIFFERENCE: failures.append("(d) variant %d %s: two ranks differ by only %.2f %% (< %.0f %%)" % [VARIANTS[v], kind, rank_difference * 100.0, RANK_DIFFERENCE * 100.0])
		# (d) stand term: one rank, roots 10 m apart against steps of 600 m. Mesh and cards both.
		for kind: String in ["mesh", "card"]:
			var at := {}
			_set_tint(FOLIAGE_BIT, Vector4(1, 0, 0, 1))
			for x: float in [0.0, 10.0, 600.0, 1200.0, 1800.0, 2400.0, 3000.0]:
				var on := await _shot(kind, v, 0.5, Vector2(x, 0.0))
				at[x] = on["lum"]
			var lo := 1e9
			var hi := -1e9
			for x: float in [0.0, 600.0, 1200.0, 1800.0, 2400.0, 3000.0]:
				lo = minf(lo, at[x]); hi = maxf(hi, at[x])
			var near := absf(at[10.0] / at[0.0] - 1.0)
			var spread := hi / lo - 1.0
			print("FOLIAGE_TINT (d) variant %d %s rank 0.5: 10 m apart differ %.3f %%, spread over six stands %.2f %%" % [VARIANTS[v], kind, near * 100.0, spread * 100.0])
			if near >= NEAR_TOLERANCE: failures.append("(d) variant %d %s: roots 10 m apart differ by %.2f %% (>= %.0f %%)" % [VARIANTS[v], kind, near * 100.0, NEAR_TOLERANCE * 100.0])
			if spread < STAND_SPREAD: failures.append("(d) variant %d %s: the stand term moved the tint by only %.2f %% over six stands (< %.0f %%)" % [VARIANTS[v], kind, spread * 100.0, STAND_SPREAD * 100.0])
	if OS.get_environment("FOLIAGE_TINT_HEAD_DIR") != "":
		await _head_identity(OS.get_environment("FOLIAGE_TINT_HEAD_DIR"), ranks)
	else:
		print("FOLIAGE_TINT (a) pre-change identity: skipped (FOLIAGE_TINT_HEAD_DIR not set)")
	if failed: return
	for message in failures: print("FOLIAGE_TINT failure: " + message)
	done = true
	if failures.is_empty():
		print("FOLIAGE_TINT_GPU=PASS")
		quit(0)
	else:
		print("FOLIAGE_TINT_GPU=FAIL %d failures" % failures.size())
		quit(1)

# Swaps both tree shaders for the pre-change versions and compares every feature-off frame with the new shaders' frame.
func _head_identity(directory: String, ranks: Array) -> void:
	var mesh_code := FileAccess.get_file_as_string(directory.path_join("imported_tree.gdshader"))
	var card_code := FileAccess.get_file_as_string(directory.path_join("imported_tree_impostor.gdshader"))
	if mesh_code.is_empty() or card_code.is_empty():
		_fail("FOLIAGE_TINT_HEAD_DIR %s lacks the two HEAD shaders" % directory)
		return
	if mesh_code.contains("foliage_tint") or card_code.contains("foliage_tint"):
		_fail("the HEAD shaders in %s already contain the foliage tint code" % directory)
		return
	var mesh_shader := Shader.new()
	mesh_shader.code = mesh_code
	var card_shader := Shader.new()
	card_shader.code = card_code
	var original := {}
	for material in materials: original[material] = material.shader; material.shader = mesh_shader
	for material in impostor_materials: original[material] = material.shader; material.shader = card_shader
	_set_tint(0, Vector4(1, 0, 0, 1))
	var total := 0
	var differing := 0
	for v in VARIANTS.size():
		for kind: String in IDENTITY_KINDS:
			for r in ranks.size():
				var head_shot := await _shot(kind, v, ranks[r], Vector2.ZERO)
				if failed: return
				total += 1
				if head_shot["image"].get_data() != off_frames["%d_%s_%d" % [v, kind, r]]:
					differing += 1
					failures.append("identity: variant %d %s rank %.3f differs between the pre-change shaders and the new shaders with the feature off" % [VARIANTS[v], kind, ranks[r]])
	for material in original: material.shader = original[material]
	print("FOLIAGE_TINT (a) identity against the pre-change shaders (feature off): %d of %d frames differ (0 differing pixels required)" % [differing, total])

func _set_tint(features: int, tint: Vector4) -> void:
	RenderingServer.global_shader_parameter_set("ringworld_world_features", features)
	RenderingServer.global_shader_parameter_set("ringworld_foliage_tint", tint)

## One capture of a root at (offset.x, offset.y, 0) with the given rank: {"image", "lum"}.
func _shot(kind: String, species: int, rank: float, offset: Vector2) -> Dictionary:
	var variant: int = VARIANTS[species]
	var elevation := deg_to_rad(90.0)
	var sun_direction := Vector3(cos(elevation), 0.0, sin(elevation))
	for material in materials: material.set_shader_parameter("sun_direction", sun_direction)
	for material in impostor_materials: material.set_shader_parameter("sun_direction", sun_direction)
	var distance := QUAD_DISTANCE if kind == "quad" else CARD_DISTANCE
	camera.fov = rad_to_deg(2.0 * atan(tan(deg_to_rad(FOV_DEGREES * 0.5)) * CARD_DISTANCE / distance))
	var root_position := Vector3(offset.x, offset.y, 0.0)
	var height: float = HEIGHTS[species]
	var middle := root_position + UP * height * 0.5
	var position := middle + Vector3(1.0, 0.0, 0.0) * distance
	camera.transform = Transform3D(Basis.looking_at(middle - position, UP), position)
	RenderingServer.global_shader_parameter_set("ring_atmosphere_camera_world_position", position)
	mesh_instance.visible = kind == "mesh" or kind == "mesh_full"
	card_instance.visible = kind == "card"
	quad_instance.visible = kind == "quad"
	var placement := Transform3D((Basis(Vector3.RIGHT, PI * 0.5)).scaled(Vector3.ONE * height), root_position)
	var multi: MultiMesh = mesh_multi if kind == "mesh" or kind == "mesh_full" else (card_multi if kind == "card" else quad_multi)
	if kind == "mesh" or kind == "mesh_full": mesh_multi.mesh = lod2_meshes[variant]
	for material: ShaderMaterial in trunk_tints: material.set_shader_parameter("albedo_tint", Color(0, 0, 0, 1) if kind == "mesh" else trunk_tints[material])
	multi.set_instance_transform(0, placement)
	multi.set_instance_custom_data(0, Color(float(variant), 1.0, rank, 0.0))
	for i in 3: await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	if image == null or image.is_empty():
		_fail("empty capture %s variant %d" % [kind, variant])
		return {}
	var stats: PackedFloat32Array = driver.call("MeasureForeground", image, BACKGROUND)
	return {"image": image, "lum": stats[4]}

# --- scene (the fixture of forest_lighting_tree_gpu_test.gd) ----------------------------------------------------------------------------------------------------

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
	environment.ambient_light_energy = 1.0
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
	sun.visible = false
	viewport.add_child(sun)
	driver = load("res://scripts/Vegetation/ImportedTreeCatalogDriver.cs").new()
	root.add_child(driver)
	helper = TEST.new()
	var card_mesh := driver.call("LoadSharedImpostorMesh") as Mesh
	var quad_mesh := driver.call("LoadFarQuadMesh") as Mesh
	if card_mesh == null or quad_mesh == null:
		_fail("shared impostor assets unavailable")
		return
	for variant in 30:
		var mesh := driver.call("LoadVariantMesh", variant, 2) as Mesh
		if mesh == null:
			_fail("LOD2 mesh missing for variant %d" % variant)
			return
		lod2_meshes.append(mesh)
		for surface in mesh.get_surface_count():
			var material := mesh.surface_get_material(surface) as ShaderMaterial
			if material != null and not materials.has(material): materials.append(material)
			if material != null and not material.get_shader_parameter("use_alpha_cutout"): trunk_tints[material] = material.get_shader_parameter("albedo_tint")
	mesh_multi = _multi(lod2_meshes[VARIANTS[0]])
	card_multi = _multi(card_mesh)
	quad_multi = _multi(quad_mesh)
	mesh_instance = _multi_instance(mesh_multi)
	card_instance = _multi_instance(card_multi)
	quad_instance = _multi_instance(quad_multi)
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
	instance.custom_aabb = AABB(Vector3(-5000, -5000, -5000), Vector3(10000, 10000, 10000))
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
	RenderingServer.global_shader_parameter_set("ringworld_forest_lighting", Vector4(0, 0, 0, 0))
	# World -> anchor frame of the test world: identity, with the ring radius in the free w of the translation column.
	RenderingServer.global_shader_parameter_set("ringworld_foliage_frame", Projection(Vector4(1, 0, 0, 0), Vector4(0, 1, 0, 0), Vector4(0, 0, 1, 0), Vector4(0, 0, 0, FRAME_RADIUS)))
	_set_tint(0, Vector4(1, 0, 0, 1))

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
	push_error("FOLIAGE_TINT_GPU=FAIL " + message)
	print("FOLIAGE_TINT_GPU=FAIL " + message)
	quit(1)

func _on_watchdog() -> void:
	if not done: _fail("watchdog timeout")
