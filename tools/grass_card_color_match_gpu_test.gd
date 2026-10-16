extends SceneTree
# Synthetic GPU scene + tests for the grass-card shader (grass-cards plan step S4).
# A gently sloped analytic plane in a real Terrain3D window (the S1 regression fixture approach) carries cards
# placed by a stand-in placer (tools/GrassCardShaderFixture.cs). Windowed Vulkan only (do not pass --headless).
#
#   godot --path . --resolution 960x540 --script res://tools/grass_card_color_match_gpu_test.gd -- mode=color out=<dir>
# modes:
#   color    compile check + colour match: CIEDE2000 between grass pixels (neutral colour settings) and the same pose with the
#            grass hidden, per pose (day / dusk / night / haze / biome borders / far), screenshots
#   curve    GLSL curve vs RingGrassCardCurve.cs parity
#   measure  GPU ms of cards (vsync off, >= 400 frames) at 1600x900
#   weather  wind on (globals) screenshot sanity
const SURFACE = preload("res://scripts/Terrain/Surface/RingTerrain3DSurface.cs")
const GEOMETRY = preload("res://scripts/Terrain/Surface/RingTerrain3DCollisionGeometry.cs")
const FIXTURE = preload("res://tools/GrassCardShaderFixture.cs")

const WINDOW := 384
const REGION := 128
const MAX_HEIGHT := 64.0
const SETTLE_FRAMES := 12
const BASE_HEIGHT := 20.0
const SLOPE_X := 0.03
const SLOPE_Z := 0.015
const MASK_THRESHOLD := 10
const BLOCK := 32

# name, cam (local x, z), eye height above ground, look (local x, z, extra height), sun = direction to the sun, delta-E target
var POSES := [
	{"name": "day_near", "cam": Vector2(-120, 0), "eye": 1.7, "look": Vector3(-100, 0, 0), "sun": Vector3(0.30, 0.85, 0.20), "target": 3.0, "block": 64},
	{"name": "day_down", "cam": Vector2(-120, 0), "eye": 1.7, "look": Vector3(-114, 0, 0), "sun": Vector3(0.30, 0.85, 0.20), "target": 3.0, "block": 64},
	{"name": "dusk", "cam": Vector2(-120, 0), "eye": 1.7, "look": Vector3(-104, 0, 0), "sun": Vector3(0.92, 0.14, 0.20), "target": 4.0, "block": 64},
	{"name": "night", "cam": Vector2(-120, 0), "eye": 1.7, "look": Vector3(-104, 0, 0), "sun": Vector3(0.20, -0.95, 0.10), "planet": true, "target": 4.0, "block": 64},
	{"name": "day_haze", "cam": Vector2(-120, 0), "eye": 1.7, "look": Vector3(-60, 0, 0), "sun": Vector3(0.40, 0.75, 0.10), "haze": true, "target": 4.0, "block": 64},
	{"name": "border_forest", "cam": Vector2(30, 0), "eye": 1.7, "look": Vector3(58, 0, 0), "sun": Vector3(0.30, 0.85, 0.20), "target": 3.0, "block": 64},
	{"name": "border_desert", "cam": Vector2(125, 0), "eye": 1.7, "look": Vector3(125, 26, 0), "sun": Vector3(0.30, 0.85, 0.20), "target": 3.0, "block": 64},
	{"name": "far", "cam": Vector2(-240, 0), "eye": 30.0, "look": Vector3(-90, 0, 0), "sun": Vector3(0.30, 0.85, 0.20), "target": 3.0, "block": 16},
	{"name": "far_haze", "cam": Vector2(-240, 0), "eye": 30.0, "look": Vector3(-90, 0, 0), "sun": Vector3(0.40, 0.75, 0.10), "haze": true, "target": 4.0, "block": 16},
]

var failed := false
var args := {}
var out_dir := ""
var fixture
var scene_parent: Node
var viewport: SubViewport
var camera: Camera3D
var sun: DirectionalLight3D
var planet: DirectionalLight3D
var surface: Node3D
var field_root: Node3D
var haze_defaults := {}
var shader_names := {}


func _init() -> void:
	for a in OS.get_cmdline_user_args():
		var kv := a.split("=", true, 1)
		if kv.size() == 2:
			args[kv[0]] = kv[1]
	out_dir = args.get("out", "res://.godot/grass_card_color_match")
	call_deferred("_run")
	create_timer(900.0).timeout.connect(func() -> void: _fail("timeout"))


func _run() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("requires Vulkan/Forward+ (do not run headless)")
		return
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(out_dir))
	var mode: String = args.get("mode", "color")
	if mode == "curve":
		await _run_curve()
		return
	if mode == "measure":
		DisplayServer.window_set_mode(DisplayServer.WINDOW_MODE_WINDOWED)
		DisplayServer.window_set_size(Vector2i(1600, 900))
		DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
		Engine.max_fps = 0
	fixture = FIXTURE.new()
	fixture.InitAssets()
	_audit_shaders()
	if failed:
		return
	if mode == "measure":
		scene_parent = root
	else:
		viewport = SubViewport.new()
		viewport.own_world_3d = true
		viewport.size = Vector2i(960, 540)
		viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
		root.add_child(viewport)
		scene_parent = viewport
	_build_scene()
	if not await _prepare_surface():
		return
	fixture.call("SetFrame", 0.0, 0.0, Vector3.ZERO, Basis.IDENTITY, 0.0, 0.0, 1.0)
	_read_frame()
	fixture.RegisterFollowers(surface)
	_audit_followers()
	match mode:
		"color": await _run_color()
		"measure": await _run_measure()
		"weather": await _run_weather()
		_: _fail("unknown mode " + mode)


# ------------------------------------------------------------------ scene

func _build_scene() -> void:
	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color(0.10, 0.12, 0.17)
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.ambient_light_color = Color(0.30, 0.33, 0.38)
	environment.ambient_light_energy = 1.0
	# Isolate direct + ambient diffuse: the terrain's environment specular (SPECULAR .18) has no card counterpart (documented gap).
	environment.reflected_light_source = Environment.REFLECTION_SOURCE_DISABLED
	var world_environment := WorldEnvironment.new()
	world_environment.environment = environment
	scene_parent.add_child(world_environment)
	# The surface resolves a directional light named "Sun" once, so it must exist first.
	sun = DirectionalLight3D.new()
	sun.name = "Sun"
	sun.shadow_enabled = true
	sun.directional_shadow_max_distance = 500.0
	scene_parent.add_child(sun)
	planet = DirectionalLight3D.new()
	planet.name = "PlanetLight"
	planet.light_color = Color(0.55, 0.65, 1.0)
	planet.light_energy = 0.45
	planet.visible = false
	scene_parent.add_child(planet)
	_orient_light(planet, Vector3.UP)
	camera = Camera3D.new()
	camera.current = true
	camera.fov = 70.0
	camera.near = 0.05
	camera.far = 2000.0
	scene_parent.add_child(camera)
	surface = SURFACE.new()
	scene_parent.add_child(surface)


func _prepare_surface() -> bool:
	fixture.SetGround(BASE_HEIGHT, SLOPE_X, SLOPE_Z)
	# grass -> forest (x 40..70) -> desert (x 110..140), the same blend the placer samples.
	fixture.SetBiome(Color(0, 0, 0, 0), Color(0, 1, 0, 0), 40.0, 70.0)
	fixture.SetBiomeSecond(Color(1, 0, 0, 0), 110.0, 140.0)
	var column_colors: Array = []
	for window_x in WINDOW:
		column_colors.append(fixture.BiomeColor((window_x - REGION) * 2.0))
	var heights := PackedFloat32Array()
	heights.resize(WINDOW * WINDOW)
	for z in WINDOW:
		for x in WINDOW:
			heights[z * WINDOW + x] = fixture.Height((x - REGION) * 2.0, (z - REGION) * 2.0)
	var height_images: Array[Image] = []
	var biome_images: Array[Image] = []
	for region in 9:
		var region_x := region % 3
		var region_z := region / 3
		var bytes := PackedByteArray()
		bytes.resize(REGION * REGION * 4)
		var biome := Image.create_empty(REGION, REGION, false, Image.FORMAT_RGBA8)
		for row in REGION:
			for column in REGION:
				var window_x := region_x * REGION + column
				var window_z := region_z * REGION + row
				bytes.encode_float((row * REGION + column) * 4, heights[window_z * WINDOW + window_x])
				biome.set_pixel(column, row, column_colors[window_x])
		height_images.append(Image.create_from_data(REGION, REGION, false, Image.FORMAT_RF, bytes))
		biome_images.append(biome)
	var geometry := GEOMETRY.new()
	if not geometry.call("PrepareGameplay", heights, 0, 159000, 0.0, -1, 0.0, MAX_HEIGHT, 32):
		_fail("prepare collision geometry")
		return false
	if not surface.call("BeginGameplayPreparedWindow", heights, height_images, biome_images, geometry, 0, 159000, 0.0, -1, 1, 0.0, MAX_HEIGHT):
		_fail("begin prepared window")
		return false
	var ready := false
	for frame in 480:
		var state: Dictionary = surface.call("AdvanceBudgetedPreparation", 0.5, 0.5)
		if not bool(state.get("ok", false)):
			_fail("prepared state invalid: %s" % state)
			return false
		if bool(state.get("ready", false)):
			ready = true
			break
		await process_frame
	if not ready:
		_fail("prepared window did not become ready")
		return false
	if not surface.call("ActivateStagedWindow", 1):
		_fail("activate prepared window")
		return false
	await physics_frame
	return true


func _terrain_material() -> Object:
	var terrain: Node = surface.get("TerrainNode")
	return terrain.call("get_material")


func _read_frame() -> void:
	var m := _terrain_material()
	var p := func(name: String): return m.call("get_shader_param", name)
	var basis: Basis = p.call("ring_frame_basis")
	fixture.call("SetFrame", float(p.call("ring_radius_meters")), float(p.call("ring_anchor_height")), p.call("ring_frame_origin"), basis,
		float(p.call("ring_texture_anchor_around_meters")), float(p.call("ring_texture_anchor_across_meters")), float(p.call("ring_across_direction")))
	print("FRAME radius=", p.call("ring_radius_meters"), " anchor_h=", p.call("ring_anchor_height"), " origin=", p.call("ring_frame_origin"),
		" basis=", basis, " anchors=", p.call("ring_texture_anchor_around_meters"), ",", p.call("ring_texture_anchor_across_meters"), " dir=", p.call("ring_across_direction"))


func _orient_light(light: DirectionalLight3D, to_light: Vector3) -> void:
	var direction := to_light.normalized()
	var up_hint := Vector3.RIGHT if absf(direction.dot(Vector3.UP)) > 0.95 else Vector3.UP
	light.transform = Transform3D(Basis.looking_at(-direction, up_hint), Vector3.ZERO)


# ------------------------------------------------------------------ audits

func _count_instance_uniforms(path: String, seen: Dictionary) -> int:
	if seen.has(path):
		return 0
	seen[path] = true
	var text := FileAccess.get_file_as_string(path)
	var count := 0
	for line in text.split("\n"):
		var stripped := line.strip_edges()
		if stripped.begins_with("instance uniform"):
			count += 1
		elif stripped.begins_with("#include"):
			var q := stripped.split("\"")
			if q.size() >= 2:
				count += _count_instance_uniforms(q[1], seen)
	return count


func _audit_shaders() -> void:
	for path in ["res://materials/ring_grass_cards.gdshader"]:
		var shader: Shader = load(path)
		if shader == null:
			_fail("shader failed to load " + path)
			return
		var n := _count_instance_uniforms(path, {})
		print("GRASS_S4_AUDIT %s instance_uniforms=%d/16 uniforms=%d" % [path.get_file(), n, shader.get_shader_uniform_list(false).size()])
		if n > 16:
			_fail("too many instance uniforms in " + path)
			return


func _audit_followers() -> void:
	var terrain_shader: Shader = load("res://materials/terrain3d_ring_surface.gdshader")
	if terrain_shader == null:
		print("GRASS_S4_AUDIT follower audit skipped (no terrain shader)")
		return
	for material in [fixture.CardMaterial]:
		var shared: PackedStringArray = fixture.SharedUniformNames(terrain_shader, material.shader)
		var missing: PackedStringArray = fixture.UnforwardedShared(shared)
		print("GRASS_S4_AUDIT shared_with_terrain(%s)=%d unforwarded=%s" % [material.shader.resource_path.get_file(), shared.size(), missing])


# ------------------------------------------------------------------ poses / capture

func _frames(n: int) -> void:
	for i in n:
		await RenderingServer.frame_post_draw


func _set_all(name: String, value) -> void:
	_terrain_material().call("set_shader_param", name, value)
	fixture.CardMaterial.set_shader_parameter(name, value)


const HAZE := {
	"atmosphere_clear_distance_meters": 8.0, "atmosphere_full_distance_meters": 420.0,
	"atmosphere_high_altitude_clear_distance_meters": 8.0, "atmosphere_high_altitude_full_distance_meters": 420.0,
	"atmosphere_distance_haze_max_opacity": 0.7, "atmosphere_total_max_opacity": 0.8, "atmosphere_scattering_energy": 1.0,
}


func _apply_pose(pose: Dictionary) -> void:
	var cam: Vector2 = pose.cam
	var look: Vector3 = pose.look
	var up: Vector3 = fixture.GroundNormal(cam.x, cam.y)
	var eye: Vector3 = fixture.GroundWorld(cam.x, cam.y) + up * float(pose.eye)
	var target: Vector3 = fixture.GroundWorld(look.x, look.y) + up * look.z
	camera.global_position = eye
	camera.look_at(target, up)
	_orient_light(sun, pose.sun)
	planet.visible = bool(pose.get("planet", false))
	surface.call("SetSurfaceLighting", -0.2, 0.35, Color(0.30, 0.45, 0.70), 0.12, 1.15, 0.10)
	surface.call("SetSurfaceWetness", 0.0)
	if haze_defaults.is_empty():
		var m := _terrain_material()
		for k in HAZE:
			haze_defaults[k] = m.call("get_shader_param", k)
	var haze := bool(pose.get("haze", false))
	for k in HAZE:
		_set_all(k, HAZE[k] if haze else haze_defaults[k])
	for frame in SETTLE_FRAMES:
		surface.call("UpdateCamera", camera.global_position)
		await process_frame


func _rebuild_field(pose: Dictionary, cards: bool) -> void:
	if is_instance_valid(field_root):
		field_root.queue_free()
		field_root = null
	var cam: Vector2 = pose.cam
	field_root = fixture.BuildField(cam.x, cam.y, float(pose.eye), 400.0, cards, 20260)
	scene_parent.add_child(field_root)


func _grab() -> Image:
	await RenderingServer.frame_post_draw
	await RenderingServer.frame_post_draw
	var image := viewport.get_texture().get_image()
	if image == null or image.is_empty():
		_fail("viewport readback failed")
		return Image.new()
	image.convert(Image.FORMAT_RGBA8)
	return image


func _shot(pose: Dictionary, suffix: String, image: Image) -> void:
	image.save_png(ProjectSettings.globalize_path(out_dir.path_join("%s_%s.png" % [pose.name, suffix])))


func _settle(frames := 4) -> void:
	for i in frames:
		surface.call("UpdateCamera", camera.global_position)
		await process_frame
	await _frames(2)


func _strip_region(near_m: float, far_m: float, half_width: float, cam_xz: Vector2) -> Rect2i:
	# Screen rect of the ground strip near_m..far_m ahead (+x) of the camera, half_width either side.
	var pts := [Vector2(cam_xz.x + near_m, cam_xz.y - half_width), Vector2(cam_xz.x + near_m, cam_xz.y + half_width),
		Vector2(cam_xz.x + far_m, cam_xz.y - half_width), Vector2(cam_xz.x + far_m, cam_xz.y + half_width)]
	var lo := Vector2(1e9, 1e9)
	var hi := Vector2(-1e9, -1e9)
	for pt in pts:
		var w: Vector3 = fixture.GroundWorld(pt.x, pt.y)
		var s := camera.unproject_position(w)
		lo = lo.min(s)
		hi = hi.max(s)
	var size := viewport.size
	var r := Rect2i(Vector2i(lo.floor()), Vector2i((hi - lo).ceil()))
	return r.intersection(Rect2i(Vector2i.ZERO, size))


func _run_color() -> void:
	var rows: Array = []
	var any_fail := false
	for pose in POSES:
		await _apply_pose(pose)
		await _rebuild_field(pose, true)
		print("POSE %s cards=%d low=%d chunks=%d" % [pose.name, fixture.CardInstances, fixture.LowInstances, fixture.ChunkCount])
		fixture.SetMode("production")
		field_root.visible = false
		await _settle()
		var ground := await _grab()
		_shot(pose, "ground", ground)
		field_root.visible = true
		fixture.SetMode("neutral")
		await _settle()
		var neutral := await _grab()
		_shot(pose, "neutral", neutral)
		fixture.SetMode("mask")
		await _settle()
		var mask := await _grab()
		fixture.SetMode("production")
		await _settle()
		var production := await _grab()
		_shot(pose, "production", production)
		var full := Rect2i(Vector2i.ZERO, viewport.size)
		var px: Dictionary = fixture.CompareMasked(neutral, ground, mask, full, MASK_THRESHOLD)
		var r: Dictionary = fixture.CompareBlocks(neutral, ground, mask, full, MASK_THRESHOLD, int(pose.block), 0.5)
		var r16: Dictionary = fixture.CompareBlocks(neutral, ground, mask, full, MASK_THRESHOLD, 16, 0.5)
		var p: Dictionary = fixture.CompareBlocks(production, ground, mask, full, MASK_THRESHOLD, int(pose.block), 0.5)
		var ok: bool = r.blocks > 8 and float(r.mean) <= float(pose.target)
		any_fail = any_fail or not ok
		var line := "GRASS_S4_COLOR pose=%s blocks%d(n=%d mean=%.2f median=%.2f p95=%.2f max=%.2f dL=%+.2f) target=%.1f %s | blocks16(mean=%.2f p95=%.2f) pixel(n=%d mean=%.2f) | production blocks%d(mean=%.2f dL=%+.2f)" % [
			pose.name, pose.block, r.blocks, r.mean, r.get("median", -1.0), r.p95, r.max, r.mean_dl, pose.target, "PASS" if ok else "FAIL",
			r16.mean, r16.p95, px.count, px.mean, pose.block, p.mean, p.mean_dl]
		print(line)
		rows.append(line)
	var f := FileAccess.open(ProjectSettings.globalize_path(out_dir.path_join("color_results.txt")), FileAccess.WRITE)
	f.store_string("\n".join(rows))
	f.close()
	if any_fail:
		_fail("colour match targets missed (see GRASS_S4_COLOR lines)")
		return
	print("GRASS_S4_COLOR_MATCH=PASS poses=%d" % POSES.size())
	await _finish(0)


# ------------------------------------------------------------------ curve parity

func _run_curve() -> void:
	fixture = FIXTURE.new()
	var count := 2048
	var data := PackedFloat32Array()
	data.resize(count * 4)
	var ranks := PackedFloat32Array()
	var dists := PackedFloat32Array()
	var rng := RandomNumberGenerator.new()
	rng.seed = 424242
	for i in count:
		var rank := rng.randf()
		var dist := rng.randf_range(0.3, 430.0)
		if i < 200:
			dist = float(i) * 2.2          # dense sweep over distance
			rank = float(i % 17) / 17.0
		ranks.append(rank)
		dists.append(dist)
		data[i * 4] = rank
		data[i * 4 + 1] = dist
	var image := Image.create_from_data(count, 1, false, Image.FORMAT_RGBAF, data.to_byte_array())
	var texture := ImageTexture.create_from_image(image)
	var vp := SubViewport.new()
	vp.size = Vector2i(count * 4, 1)
	vp.transparent_bg = true
	vp.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	var rect := ColorRect.new()
	rect.size = Vector2(count * 4, 1)
	var material := ShaderMaterial.new()
	material.shader = load("res://tools/shaders/grass_card_curve_parity.gdshader")
	material.set_shader_parameter("samples", texture)
	rect.material = material
	vp.add_child(rect)
	root.add_child(vp)
	await _frames(4)
	var out := vp.get_texture().get_image()
	out.convert(Image.FORMAT_RGBA8)
	var bytes := out.get_data()
	var ref: Array = fixture.CurveReference(ranks, dists, 400.0)
	var names := ["scale", "keep", "density", "low_scale"]
	var worst := [0.0, 0.0, 0.0, 0.0]
	var flips := 0
	var bad := 0
	for i in count:
		for field in 4:
			var o := (i * 4 + field) * 4
			var bits := bytes[o] | (bytes[o + 1] << 8) | (bytes[o + 2] << 16) | (bytes[o + 3] << 24)
			var tmp := PackedByteArray([bytes[o], bytes[o + 1], bytes[o + 2], bytes[o + 3]])
			var gpu := tmp.decode_float(0)
			var expected: float = ref[i][field]
			if is_nan(gpu) or is_inf(gpu):
				bad += 1
				continue
			if (expected == 0.0 or gpu == 0.0) and maxf(absf(expected), absf(gpu)) < 0.25 and (field == 0 or field == 3):
				if expected != gpu:
					flips += 1
				continue
			var err := absf(gpu - expected) / maxf(1.0, absf(expected))
			worst[field] = maxf(worst[field], err)
			if err > 2e-4:
				bad += 1
				if bad < 12:
					print("  mismatch %s rank=%.5f dist=%.3f gpu=%.7f cpu=%.7f" % [names[field], ranks[i], dists[i], gpu, expected])
	print("GRASS_S4_CURVE_PARITY samples=%d worst_rel_err(scale=%.7f keep=%.7f density=%.7f low=%.7f) boundary_flips=%d bad=%d" % [count, worst[0], worst[1], worst[2], worst[3], flips, bad])
	if bad > 0 or flips > 6:
		_fail("curve parity")
		return
	print("GRASS_S4_CURVE_PARITY=PASS")
	await _finish(0)


# ------------------------------------------------------------------ perf

func _smi() -> Dictionary:
	var o := []
	OS.execute("nvidia-smi", ["--query-gpu=pstate,power.draw,clocks.sm,clocks.mem,temperature.gpu", "--format=csv,noheader,nounits"], o)
	var parts: PackedStringArray = (o[0] if o.size() > 0 else "").strip_edges().split(",")
	if parts.size() < 5:
		return {"pstate": "?", "sm": 0, "mem": 0}
	return {"pstate": parts[0].strip_edges(), "power": float(parts[1]), "sm": int(parts[2]), "mem": int(parts[3]), "temp": int(parts[4])}


func _stats(a: Array) -> Dictionary:
	var s := a.duplicate()
	s.sort()
	var sum := 0.0
	for v in s:
		sum += v
	return {"mean": sum / s.size(), "median": s[s.size() / 2], "p95": s[int(s.size() * 0.95)]}


func _window(label: String, samples: int) -> Dictionary:
	var vp := root.get_viewport_rid()
	for attempt in 3:
		await _frames(90)
		var before := _smi()
		var gpu: Array = []
		var cpu: Array = []
		var prims: Array = []
		for i in samples:
			await RenderingServer.frame_post_draw
			gpu.append(RenderingServer.viewport_get_measured_render_time_gpu(vp))
			cpu.append(RenderingServer.viewport_get_measured_render_time_cpu(vp))
			prims.append(Performance.get_monitor(Performance.RENDER_TOTAL_PRIMITIVES_IN_FRAME))
		var after := _smi()
		var healthy: bool = before.pstate == "P0" and after.pstate == "P0" and int(after.mem) > 3000
		if healthy:
			return {"label": label, "gpu": _stats(gpu), "cpu": _stats(cpu), "prims": _stats(prims).median, "smi": after}
		print("THROTTLED window discarded: ", label, " ", before, " ", after)
		OS.delay_msec(15000)
	return {"label": label, "gpu": _stats([0.0]), "cpu": _stats([0.0]), "prims": 0, "smi": {}, "invalid": true}


func _run_measure() -> void:
	var samples := int(args.get("samples", "400"))
	RenderingServer.viewport_set_measure_render_time(root.get_viewport_rid(), true)
	var results: Array = []
	for pose in [POSES[0], POSES[1], POSES[7]]:
		await _apply_pose(pose)
		fixture.SetMode("production")
		await _rebuild_field(pose, true)
		var counts := "cards=%d low=%d" % [fixture.CardInstances, fixture.LowInstances]
		var card_nodes: Array = field_root.get_children()
		var states := [["hidden", false], ["cards", true]]
		var base := 0.0
		for s in states:
			for n in card_nodes:
				n.visible = s[1]
			var w := await _window("%s/%s" % [pose.name, s[0]], samples)
			if s[0] == "hidden":
				base = w.gpu.median
			var line := "GRASS_S4_GPU pose=%s state=%s gpu_median=%.3f mean=%.3f p95=%.3f delta_vs_hidden=%.3f cpu_median=%.3f prims=%d samples=%d %s smi=%s/%dMHz" % [
				pose.name, s[0], w.gpu.median, w.gpu.mean, w.gpu.p95, w.gpu.median - base, w.cpu.median, int(w.prims), samples, counts, w.smi.get("pstate", "?"), int(w.smi.get("sm", 0))]
			print(line)
			results.append(line)
	var f := FileAccess.open(ProjectSettings.globalize_path(out_dir.path_join("gpu_results.txt")), FileAccess.WRITE)
	f.store_string("\n".join(results))
	f.close()
	await _finish(0)


# ------------------------------------------------------------------ weather (wind) sanity

func _run_weather() -> void:
	var pose: Dictionary = POSES[0]
	await _apply_pose(pose)
	await _rebuild_field(pose, true)
	fixture.SetMode("production")
	await _settle()
	var calm := await _grab()
	_shot(pose, "wind_calm", calm)
	RenderingServer.global_shader_parameter_set("ring_local_wind_enabled", 1.0)
	RenderingServer.global_shader_parameter_set("ring_local_wind_vector", Vector2(14.0, 6.0))
	RenderingServer.global_shader_parameter_set("ring_local_wind_wave", Vector4(0.8, 0.3, 0.5, 0.2))
	RenderingServer.global_shader_parameter_set("ring_local_wind_center", Vector3(0, -25000, 0))
	RenderingServer.global_shader_parameter_set("ring_local_wind_axis", Vector3(0, 0, 1))
	await _settle(8)
	var windy := await _grab()
	_shot(pose, "wind_on", windy)
	var diff := 0
	var a := calm.get_data()
	var b := windy.get_data()
	for i in range(0, a.size(), 4):
		if absi(int(a[i]) - int(b[i])) + absi(int(a[i + 1]) - int(b[i + 1])) > 12:
			diff += 1
	print("GRASS_S4_WEATHER changed_pixels=%d" % diff)
	RenderingServer.global_shader_parameter_set("ring_local_wind_enabled", 0.0)
	await _finish(0 if diff > 100 else 1)


func _finish(code: int) -> void:
	if is_instance_valid(viewport):
		viewport.queue_free()
	for frame in 4:
		await RenderingServer.frame_post_draw
	quit(code)


func _fail(message: String) -> void:
	if failed:
		return
	failed = true
	push_error("GRASS_S4_GPU_TEST=FAIL " + message)
	print("GRASS_S4_GPU_TEST=FAIL ", message)
	call_deferred("quit", 1)
