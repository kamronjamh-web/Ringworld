extends SceneTree

## NF-2a GPU test (docs/sun-flare-shafts-near-fog-plan.md, NF-1 design N2 and the NF-2a row): the near-fog terrain fields built by the height-shadow producer.
##   T0   = R32F 1024^2 copy of the level-0 heights (ringworld_height_shadow_t0_l0), same toroidal slots as the lattice (slot = index & 1023, texel centre at index * texel).
##   B    = R32F 64^2 basin floor (ringworld_near_fog_basin_l0): 16x16-block min of T0, 5x5 min, 3x3 box blur, slot = (T0 slot >> 4) & 63.
## Boots the pinned v8 fixture (tools/landscape_harness_common.gd), waits for the producer to go idle and reads both textures back (test-only texture_get_data on the render thread):
##   1. T0 at 9 points around the camera (within 1 km) and at the four texels around the slot-0 seam of both axes against Terrain3DGameplay.SampleHeight, within 0.05 m;
##   2. the same after a teleport that forces a full L0 recentre (hills_eye, ~1 Mm away), proving the toroidal update and that the published bounds follow;
##   3. B <= T0 + 0.01 at every texel, and B at the camera block and at the deepest nearby basin block within 2 m of a CPU oracle (min of SampleHeight on an 8 m grid over the same
##      320 m footprint, then the same 3x3 mean; the 8 m grid can only overestimate the true min by (slope x 4 m) = under 1 m up to 14 degrees, hence the 2 m tolerance);
##   4. the shader include: an unshaded probe quad 0.5 m ahead of a camera 1.5 m above ground writes (ring_near_fog_frame_point.z - ring_near_fog_t0) into a SubViewport pixel
##      (own world, no environment, so no tonemapping); it must read 1.5 +- 0.1 m and agree with the camera height decoded from the published frame minus SampleHeight.
## RUN (windowed Vulkan, no other Godot running): tools/run_tests.ps1 -Label nf2a -Tests near_fog_terrain_fields_gpu_test
##   or Godot_console.exe --path . --resolution 1280x720 --script res://tools/near_fog_terrain_fields_gpu_test.gd
## Prints NEAR_FOG_TERRAIN_FIELDS=PASS (or =FAIL <reason>). Test only; the harness fixture world is deleted at exit.
const HARNESS := preload("res://tools/landscape_harness_common.gd")
const MARKER := "NEAR_FOG_TERRAIN_FIELDS"
const N := 1024
const B := 64
const T0_TOLERANCE := 0.05
const BASIN_TOLERANCE := 2.0

var game: Node
var producer: Node
var surface: Node
var failures: Array[String] = []
var finished := false
var report := {}


func _init() -> void:
	call_deferred("_run")


func _check(ok: bool, message: String) -> void:
	if not ok:
		failures.append(message)
		print("%s check failed: %s" % [MARKER, message])


func _fail(message: String) -> void:
	if finished:
		return
	finished = true
	push_error("%s=FAIL %s" % [MARKER, message])
	_cleanup()
	quit(1)


func _cleanup() -> void:
	var root_path := ProjectSettings.globalize_path("res://.godot/captures/landscape/fixture_world")
	HARNESS.remove_dir_recursive(root_path)


func _run() -> void:
	var watchdog := create_timer(1200.0)
	watchdog.timeout.connect(func() -> void: _fail("watchdog timeout (1200 s)"))
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice is required")
		return
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	var booted: Dictionary = await HARNESS.boot(self, MARKER)
	if booted.is_empty():
		_fail("boot failed")
		return
	game = booted["game"]
	var monitor := game.get_node_or_null("RingPerformanceMonitor")
	if monitor != null:
		monitor.process_mode = Node.PROCESS_MODE_DISABLED
	HARNESS.COMMON.hold_world_clocks(game)
	producer = game.get_node_or_null("TerrainHeightShadow")
	surface = game.get_node_or_null("Terrain3DGameplay")
	var graphics := game.get_node_or_null("GraphicsQuality")
	if producer == null or surface == null or graphics == null:
		_fail("TerrainHeightShadow, Terrain3DGameplay or GraphicsQuality missing")
		return
	_check(bool(graphics.call("SetShadowTuningOverride", "HeightShadowL0TexelMeters", 4)) and int(graphics.get("EffectiveHeightShadowL0TexelMeters")) == 4, "pin the L0 texel to 4 m")
	_check(bool(graphics.call("SetShadowTuningOverride", "HeightShadowEnabled", true)), "HeightShadowEnabled accepted")
	var ground_model: RefCounted = null if str(booted["world_tag"]).is_empty() else booted["world_bridge"]

	# ---- stage 1: plains_eye, camera 1.5 m above ground, level heading
	var plains := HARNESS.pose_by_name("plains_eye").duplicate()
	plains["lift_m"] = 1.5
	var level := {"yaw": 0.0, "pitch": 0.0}
	var info: Dictionary = await HARNESS.go_pose(self, game, plains, "noon", ground_model, level)
	if info.has("error"):
		_fail("pose plains_eye: " + str(info["error"]))
		return
	var stage1: Dictionary = await _await_fields(0)
	if stage1.is_empty():
		return
	await _check_fields("plains", stage1)
	await _check_probe(stage1)
	var first_origin: float = float(stage1["abs_min_around"])

	# ---- stage 2: a teleport far outside the window (full recentre), hills_eye
	var hills := HARNESS.pose_by_name("hills_eye")
	info = await HARNESS.go_pose(self, game, hills, "noon", ground_model)
	if info.has("error"):
		_fail("pose hills_eye: " + str(info["error"]))
		return
	var stage2: Dictionary = await _await_fields(int(stage1["publishes"]))
	if stage2.is_empty():
		return
	_check(absf(float(stage2["abs_min_around"]) - first_origin) > 100000.0, "the published T0 bounds moved with the recentre")
	await _check_fields("hills", stage2)

	report["failures"] = failures
	print("%s report %s" % [MARKER, JSON.stringify(report)])
	if not failures.is_empty():
		_fail(" | ".join(failures.slice(0, 12)))
		return
	finished = true
	print("%s=PASS" % MARKER)
	_cleanup()
	quit(0)


# ------------------------------------------------------------------ waiting and reading

func _diag() -> Dictionary:
	return producer.call("GetDiagnostics")


func _global(name: String):
	return producer.call("GetGlobalValues").get(name)


## Waits until the producer published again (publishes > after), is idle, the fields are valid and the camera sits well inside the T0 bounds. Returns the field state, or {} after _fail.
func _await_fields(after_publishes: int) -> Dictionary:
	var deadline := Time.get_ticks_msec() + 600000
	var stable := 0
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var d := _diag()
		var params = _global("ringworld_near_fog_fields_params")
		var bounds = _global("ringworld_near_fog_t0_bounds")
		var rids: Array = d.get("near_fog_rids", [])
		var idle := str(d["phase"]) == "Idle" and int(d["publishes"]) > after_publishes and HARNESS.COMMON.builds_idle(game)
		var valid: bool = params is Vector4 and (params as Vector4).z > 0.5 and bounds is Vector4 and rids.size() == 2
		if idle and valid:
			var now: Dictionary = HARNESS.COMMON.pose_now(game)
			var anchor_around := float(d["anchor_around"])
			var anchor_across := float(d["anchor_across"])
			var b: Vector4 = bounds
			var ca := float(now["around"]) - anchor_around
			var cb := float(now["across"]) - anchor_across
			if ca - b.x > 400.0 and b.y - ca > 400.0 and cb - b.z > 400.0 and b.w - cb > 400.0:
				stable += 1
				if stable >= 30:
					return {"publishes": int(d["publishes"]), "anchor_around": anchor_around, "anchor_across": anchor_across, "bounds": b, "params": params, "rids": rids,
						"abs_min_around": anchor_around + b.x, "abs_min_across": anchor_across + b.z, "around": float(now["around"]), "across": float(now["across"]),
						"texel": int((params as Vector4).x)}
				continue
		stable = 0
	_fail("fields never became valid and idle (phase %s status %s publishes %s)" % [str(_diag()["phase"]), str(_diag()["status"]), str(_diag()["publishes"])])
	return {}


func _read_texture(rid: RID) -> PackedFloat32Array:
	var holder := []
	RenderingServer.call_on_render_thread(func() -> void:
		holder.append(RenderingServer.get_rendering_device().texture_get_data(rid, 0)))
	for i in 6:
		await process_frame
		if not holder.is_empty():
			break
	if holder.is_empty():
		return PackedFloat32Array()
	return (holder[0] as PackedByteArray).to_float32_array()


# ------------------------------------------------------------------ checks 1 to 3

func _sample(around: float, across: float) -> float:
	return float(surface.call("SampleHeight", around, across))


func _check_fields(tag: String, state: Dictionary) -> void:
	var texel: int = state["texel"]
	_check(texel == 4, "%s: L0 texel is 4 m (got %d)" % [tag, texel])
	var rids: Array = state["rids"]
	var t0 := await _read_texture(rids[0])
	var basin := await _read_texture(rids[1])
	_check(t0.size() == N * N and basin.size() == B * B, "%s: readback sizes T0 %d basin %d" % [tag, t0.size(), basin.size()])
	if t0.size() != N * N or basin.size() != B * B:
		return
	var t := float(texel)
	var i_min := int(roundf(float(state["abs_min_around"]) / t))
	var j_min := int(roundf(float(state["abs_min_across"]) / t))
	var i_max := i_min + N - 1
	var j_max := j_min + N - 1
	var ci := int(roundf(float(state["around"]) / t))
	var cj := int(roundf(float(state["across"]) / t))

	# 1. nine points within 1 km of the camera, plus the slot-0 seam of both axes
	var points: Array = []
	for dy in [-200, 0, 200]:
		for dx in [-200, 0, 200]:
			points.append(Vector2i(ci + dx, cj + dy))
	var iw := int(ceil(float(i_min) / 1024.0)) * 1024
	var jw := int(ceil(float(j_min) / 1024.0)) * 1024
	for di in [-1, 0]:
		for dj in [-1, 0]:
			points.append(Vector2i(iw + di, jw + dj))
	var worst := 0.0
	var checked := 0
	for p in points:
		var ii: int = p.x
		var jj: int = p.y
		if ii < i_min or ii > i_max or jj < j_min or jj > j_max:
			continue
		var gpu_h := t0[(jj & 1023) * N + (ii & 1023)]
		var cpu_h := _sample(ii * t, jj * t)
		worst = maxf(worst, absf(gpu_h - cpu_h))
		checked += 1
		_check(absf(gpu_h - cpu_h) <= T0_TOLERANCE, "%s: T0 at index (%d, %d) slot (%d, %d) is %.4f, SampleHeight %.4f" % [tag, ii, jj, ii & 1023, jj & 1023, gpu_h, cpu_h])
	_check(checked >= 12, "%s: only %d of %d T0 points were inside the window" % [tag, checked, points.size()])
	report[tag + "_t0_points"] = checked
	report[tag + "_t0_worst_m"] = worst

	# 3a. B <= T0 + 0.01 everywhere, and how much lower than T0 it gets
	var violations := 0
	var worst_excess := -1e9
	var deepest := 0.0
	var deepest_slot := Vector2i(-1, -1)
	for y in N:
		var row := (y >> 4) * B
		for x in N:
			var diff := basin[row + (x >> 4)] - t0[y * N + x]
			if diff > 0.01:
				violations += 1
			worst_excess = maxf(worst_excess, diff)
	_check(violations == 0, "%s: B exceeds T0 + 0.01 at %d texels (worst %.3f)" % [tag, violations, worst_excess])
	report[tag + "_basin_max_excess_m"] = worst_excess

	# 3b. CPU oracle at the camera block and at the deepest block (T0 minus B at the block's own min) within 1.5 km of the camera
	var camera_block := Vector2i(ci >> 4, cj >> 4)
	var best_gap := -1.0
	var deep_block := camera_block
	for by in range(camera_block.y - 24, camera_block.y + 25):
		for bx in range(camera_block.x - 24, camera_block.x + 25):
			if bx * 16 < i_min + 160 or bx * 16 + 15 > i_max - 160 or by * 16 < j_min + 160 or by * 16 > j_max - 160:
				continue
			var centre_h := t0[((by * 16 + 8) & 1023) * N + ((bx * 16 + 8) & 1023)]
			var gap := centre_h - basin[(by & 63) * B + (bx & 63)]
			if gap > best_gap:
				best_gap = gap
				deep_block = Vector2i(bx, by)
	report[tag + "_deepest_basin_gap_m"] = best_gap
	_check(best_gap > 5.0, "%s: no basin deeper than 5 m within 1.5 km of the camera (gap %.2f); the oracle would be trivial" % [tag, best_gap])
	for block in [camera_block, deep_block]:
		var oracle := _oracle_basin(block, t)
		var got := basin[(block.y & 63) * B + (block.x & 63)]
		report["%s_basin_block_%d_%d" % [tag, block.x, block.y]] = {"gpu": got, "oracle": oracle}
		_check(absf(got - oracle) <= BASIN_TOLERANCE, "%s: B at block (%d, %d) is %.3f, CPU oracle %.3f" % [tag, block.x, block.y, got, oracle])


## The CPU basin of one block: the 8 m-grid min of every block (16 x 16 texels = 64 m), the 5 x 5 min of those, then the 3 x 3 mean.
func _oracle_basin(block: Vector2i, t: float) -> float:
	var block_min := {}
	for by in range(block.y - 3, block.y + 4):
		for bx in range(block.x - 3, block.x + 4):
			var best := INF
			for sj in 8:
				for si in 8:
					best = minf(best, _sample((bx * 16 + si * 2) * t, (by * 16 + sj * 2) * t))
			block_min[Vector2i(bx, by)] = best
	var sum := 0.0
	for cy in range(block.y - 1, block.y + 2):
		for cx in range(block.x - 1, block.x + 2):
			var footprint := INF
			for dy in range(-2, 3):
				for dx in range(-2, 3):
					footprint = minf(footprint, float(block_min[Vector2i(cx + dx, cy + dy)]))
			sum += footprint
	return sum / 9.0


# ------------------------------------------------------------------ check 4: the shader include

const PROBE_SHADER := """
shader_type spatial;
render_mode unshaded, cull_disabled;
#include "res://materials/ring_near_fog_fields.gdshaderinc"
uniform float calibrate = 0.0;
void fragment() {
	vec3 world = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec3 fp = ring_near_fog_frame_point(world);
	float dz = fp.z - ring_near_fog_t0(fp.xy);
	float valid = ring_near_fog_fields_valid(fp.xy) ? 1.0 : 0.0;
	ALBEDO = calibrate > 0.5 ? vec3(0.5, valid, 0.5) : vec3((dz + 4.0) / 8.0, valid, 0.5);
}
"""


func _probe_pixel(probe_transform: Transform3D, calibrate: bool) -> Color:
	var viewport := SubViewport.new()
	viewport.size = Vector2i(64, 64)
	viewport.own_world_3d = true
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	var probe_camera := Camera3D.new()
	viewport.add_child(probe_camera)
	probe_camera.global_transform = probe_transform
	probe_camera.current = true
	var shader := Shader.new()
	shader.code = PROBE_SHADER
	var material := ShaderMaterial.new()
	material.shader = shader
	material.set_shader_parameter("calibrate", 1.0 if calibrate else 0.0)
	var quad := MeshInstance3D.new()
	var mesh := QuadMesh.new()
	mesh.size = Vector2(0.3, 0.3)
	quad.mesh = mesh
	quad.material_override = material
	probe_camera.add_child(quad)
	quad.position = Vector3(0, 0, -0.5)
	for i in 6:
		await process_frame
	var image := viewport.get_texture().get_image()
	var pixel := image.get_pixel(32, 32)
	viewport.queue_free()
	return pixel


func _probe_height(frame: Projection, camera: Camera3D, world_point: Vector3) -> float:
	# The probe quad sits 0.5 m ahead of the probe camera, level, at world_point; returns z - T0 as the shader sees it (8-bit, so about 0.06 m resolution).
	var up := Vector3(frame.x.z, frame.y.z, frame.z.z).normalized()
	var forward := -camera.global_transform.basis.z
	forward = (forward - up * forward.dot(up)).normalized()
	var probe := await _probe_pixel(Transform3D(Basis.looking_at(forward, up), world_point - forward * 0.5), false)
	if probe.g < 0.5:
		_check(false, "ring_near_fog_fields_valid is false at the probe point")
	return probe.srgb_to_linear().r * 8.0 - 4.0


func _check_probe(state: Dictionary) -> void:
	var camera := root.get_camera_3d()
	var explorer := game.get_node_or_null("Explorer") as Node3D
	if camera == null or explorer == null:
		_check(false, "no main camera or explorer for the probe")
		return
	var frame: Projection = _global("ringworld_height_shadow_frame")
	var up := Vector3(frame.x.z, frame.y.z, frame.z.z).normalized()
	# The player stands (flies) exactly pose.lift = 1.5 m above ground: its canonical vertical is player_height, the ground below it SampleHeight.
	var now: Dictionary = HARNESS.COMMON.pose_now(game)
	var above := float(now["vertical"]) - _sample(float(now["around"]), float(now["across"]))
	var player := explorer.global_position
	var calibration := (await _probe_pixel(Transform3D(Basis.IDENTITY, player), true)).srgb_to_linear()
	var dz0 := await _probe_height(frame, camera, player)
	var dz2 := await _probe_height(frame, camera, player + up * 2.0)
	report["probe"] = {"calibration_linear_r": calibration.r, "dz_at_player_m": dz0, "player_above_ground_m": above, "dz_2m_higher_m": dz2}
	_check(absf(calibration.r - 0.5) < 0.02, "probe calibration: linear %.3f instead of 0.5 (output encoding assumption broken)" % calibration.r)
	_check(absf(above - 1.5) <= 0.1, "player is %.3f m above ground, the pose should give 1.5" % above)
	_check(absf(dz0 - above) <= 0.1, "probe z - T0 at the player is %.3f m, player above ground (vertical - SampleHeight) is %.3f" % [dz0, above])
	_check(absf((dz2 - dz0) - 2.0) <= 0.1, "probe z rises %.3f m for 2 m along ring up" % (dz2 - dz0))
