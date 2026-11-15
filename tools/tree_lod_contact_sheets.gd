extends SceneTree
## TR-4 contact sheets (windowed GPU). One PNG per variant: authored LOD2 beside decimated levels A, B and C, rendered at the pixel size the
## tree has at 150 m and at 325 m (75 degree vertical FOV, 1920 x 1080), at noon and at sun 8 degrees, then enlarged with nearest filtering.
## Run: tools/gpu_queue.ps1 -Label tr4 -Wrap { tools/use_godot_48.ps1 --script res://tools/tree_lod_contact_sheets.gd -- [--only=3,12,...] }
## Needs the level files from tools/prepare_tree_render_lods.gd (.godot/tr4/levels) and lolipop_render_lods.res.
const DRIVER := preload("res://tools/TreeLodSheetDriver.cs")
const OUT := "res://.godot/tr4/sheets"
const SIZE := Vector2i(1920, 1080)
const FOV := 75.0
const ROOT := Vector3(1000, 0, 0)
const LEVELS := ["S", "A", "B", "C"]
const DISTANCES := [150.0, 325.0]
const SUNS := [{"label": "noon", "elev": 90.0}, {"label": "sun 8 deg", "elev": 8.0}]

var driver: Node
var viewport: SubViewport
var camera: Camera3D
var sun: DirectionalLight3D
var environment: Environment
var nodes: Array = []

func _init() -> void:
	if RenderingServer.get_rendering_device() == null:
		push_error("TREE_LOD_SHEETS=FAIL RenderingDevice required")
		quit(1)
		return
	call_deferred("_run")

func _run() -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUT))
	driver = DRIVER.new()
	root.add_child(driver)
	_make_scene()
	var only: Array = []
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--only="):
			for s in arg.trim_prefix("--only=").split(","): only.append(int(s))
	RenderingServer.global_shader_parameter_set("ringworld_solar_visibility", 1.0)
	RenderingServer.global_shader_parameter_set("ring_local_wind_enabled", 0.0)
	var count: int = driver.call("VariantCount")
	for index in range(count):
		if not only.is_empty() and not only.has(index): continue
		await _sheet(index)
	print("TREE_LOD_SHEETS=PASS out=%s" % OUT)
	quit(0)

func _make_scene() -> void:
	viewport = SubViewport.new()
	viewport.size = SIZE
	viewport.own_world_3d = true
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	var world := WorldEnvironment.new()
	environment = Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color(0.52, 0.62, 0.78)
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.ambient_light_color = Color(0.55, 0.62, 0.75)
	environment.ambient_light_energy = 0.45
	environment.tonemap_mode = Environment.TONE_MAPPER_LINEAR
	world.environment = environment
	viewport.add_child(world)
	camera = Camera3D.new()
	camera.fov = FOV
	camera.near = 1.0
	camera.far = 2000.0
	camera.current = true
	viewport.add_child(camera)
	sun = DirectionalLight3D.new()
	sun.light_energy = 1.3
	sun.shadow_enabled = false
	viewport.add_child(sun)
	for i in range(4):
		var multi := MultiMesh.new()
		multi.transform_format = MultiMesh.TRANSFORM_3D
		multi.instance_count = 1
		var node := MultiMeshInstance3D.new()
		node.multimesh = multi
		viewport.add_child(node)
		nodes.append(node)

func _sheet(index: int) -> void:
	var height: float = driver.call("VariantHeight", index)
	var width: float = driver.call("VariantWidth", index)
	var meshes: Array = []
	var tris: Array = []
	for level in LEVELS:
		meshes.append(driver.call("GetMesh", level, index))
		tris.append(driver.call("Triangles", level, index))
	var cells: Array = []  # rows of 4 Images
	var row_labels: Array = []
	for dist in DISTANCES:
		var d: float = dist
		for s in SUNS:
			_light(s.elev)
			var row: Array = []
			var px_per_m: float = float(SIZE.y) / (2.0 * d * tan(deg_to_rad(FOV) * 0.5))
			var half_w: float = maxf(0.62 * width * px_per_m, 14.0)
			for li in range(4):
				var mesh: Mesh = meshes[li]
				for ni in range(4):
					nodes[ni].visible = (ni == li)
				var multi: MultiMesh = nodes[li].multimesh
				multi.mesh = mesh
				for material in driver.call("Materials", index):
					material.set_shader_parameter("ring_center", Vector3.ZERO)
					material.set_shader_parameter("sun_direction", _sun_dir(s.elev))
					material.set_shader_parameter("night_tint", Vector3.ONE)
					material.set_shader_parameter("night_direct_light_factor", 0.0)
					material.set_shader_parameter("day_direct_light_factor", 1.0)
					material.set_shader_parameter("local_cloud_shadow_enabled", false)
					material.set_shader_parameter("local_cloud_shadow_near_enabled", false)
					material.set_shader_parameter("local_cloud_shadow_far_enabled", false)
					material.set_shader_parameter("local_cloud_shadow_radius_meters", 100000.0)
				multi.set_instance_transform(0, Transform3D(Basis(Quaternion(Vector3.UP, Vector3.LEFT)).scaled(Vector3.ONE * height), ROOT))
				camera.global_transform = Transform3D(Basis.IDENTITY, ROOT + Vector3.LEFT * 1.7 + Vector3(0, 0, d))
				camera.look_at(ROOT + Vector3.LEFT * height * 0.5, Vector3.LEFT)
				for f in range(5): await RenderingServer.frame_post_draw
				var image := viewport.get_texture().get_image()
				var root_px := camera.unproject_position(ROOT)
				var top_px := camera.unproject_position(ROOT + Vector3.LEFT * height * 1.04)
				var crop := Rect2i(int(root_px.x - half_w), int(top_px.y), int(half_w * 2.0), int(root_px.y - top_px.y) + 6)
				crop = crop.intersection(Rect2i(Vector2i.ZERO, SIZE))
				row.append(image.get_region(crop))
			cells.append(row)
			row_labels.append("%d m, %s" % [int(d), s.label])
	await _compose(index, cells, row_labels, tris)
	for ni in range(4): nodes[ni].visible = true

func _sun_dir(elev: float) -> Vector3:
	var horizontal := Vector3(0, -0.5, 0.866).normalized()
	return (horizontal * cos(deg_to_rad(elev)) + Vector3.LEFT * sin(deg_to_rad(elev))).normalized()

func _light(elev: float) -> void:
	var dir := _sun_dir(elev)
	var hint := Vector3(0, 0, -1) if absf(dir.dot(Vector3.LEFT)) > 0.99 else Vector3.LEFT
	sun.global_transform = Transform3D(Basis.looking_at(-dir, hint), ROOT + Vector3.LEFT * 50.0)

func _compose(index: int, cells: Array, row_labels: Array, tris: Array) -> void:
	var name_str: String = driver.call("VariantName", index)
	var size_class: String = driver.call("SizeClass", index)
	var cell_w := 0
	var row_h: Array = []
	var scales: Array = []
	for r in range(cells.size()):
		var h := 0
		for img in cells[r]:
			h = maxi(h, img.get_height())
			cell_w = maxi(cell_w, img.get_width())
		row_h.append(h)
	for r in range(cells.size()):
		scales.append(clampi(int(round(300.0 / float(maxi(row_h[r], 1)))), 2, 10))
	var col_w := 0
	for r in range(cells.size()):
		for img in cells[r]: col_w = maxi(col_w, img.get_width() * scales[r])
	var sheet_viewport := SubViewport.new()
	var total_h := 56
	for r in range(cells.size()): total_h += row_h[r] * scales[r] + 28
	sheet_viewport.size = Vector2i(160 + 4 * (col_w + 8), total_h)
	sheet_viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	sheet_viewport.transparent_bg = false
	root.add_child(sheet_viewport)
	var bg := ColorRect.new()
	bg.color = Color(0.12, 0.12, 0.13)
	bg.size = Vector2(sheet_viewport.size)
	sheet_viewport.add_child(bg)
	var titles := ["authored LOD2 (%d tris)" % tris[0], "A (%d)" % tris[1], "B (%d)" % tris[2], "C (%d)" % tris[3]]
	var header := Label.new()
	header.text = "#%d %s (%s class)   columns: authored LOD2, A = 1.6x target, B = target, C = 0.6x target" % [index, name_str, size_class]
	header.position = Vector2(6, 2)
	sheet_viewport.add_child(header)
	for c in range(4):
		var title := Label.new()
		title.text = titles[c]
		title.position = Vector2(160 + c * (col_w + 8), 28)
		sheet_viewport.add_child(title)
	var y := 56
	for r in range(cells.size()):
		var rl := Label.new()
		rl.text = row_labels[r] + "\nx%d" % scales[r]
		rl.position = Vector2(6, y + 4)
		sheet_viewport.add_child(rl)
		for c in range(4):
			var img: Image = cells[r][c]
			img.resize(img.get_width() * scales[r], img.get_height() * scales[r], Image.INTERPOLATE_NEAREST)
			var rect := TextureRect.new()
			rect.texture = ImageTexture.create_from_image(img)
			rect.position = Vector2(160 + c * (col_w + 8), y)
			rect.texture_filter = CanvasItem.TEXTURE_FILTER_NEAREST
			sheet_viewport.add_child(rect)
		y += row_h[r] * scales[r] + 28
	for f in range(4): await RenderingServer.frame_post_draw
	var out := sheet_viewport.get_texture().get_image()
	out.save_png(ProjectSettings.globalize_path("%s/v%02d_%s.png" % [OUT, index, name_str.replace(" ", "_")]))
	sheet_viewport.queue_free()
