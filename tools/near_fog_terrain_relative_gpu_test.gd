extends SceneTree

## NF-2b GPU test (docs/sun-flare-shafts-near-fog-plan.md, NF-1 design N2 and the NF-2b row): the MIST-1 regression on the full chain. The near-fog density must follow the terrain,
## not sea level: the shared density function (materials/ring_near_fog_common.gdshaderinc) evaluated through the published fields (materials/ring_near_fog_fields.gdshaderinc).
## Boots the pinned v8 fixture (tools/landscape_harness_common.gd). A small unshaded probe material includes the fields and writes ring_near_fog_density (noise 0, no water) as a 16-bit value in two
## colour channels, written as exact sRGB bytes (read back raw: no float readback needed) at the fragment's world position, 1.5 m above the ground unless stated:
##   1. mist-only density (drive (0, 1, 0)) on a high pose (hills_eye, ground >= 1,000 m) equals that on a low pose (coast_eye) within 2 %;
##   2. radiation-only density (drive (1, 0, 0), H 40) at a ridge top at least 60 m above the local basin floor (found from the published T0 and basin textures) is under 5 % of the value
##      at the basin floor (a block whose T0 sits at the basin floor);
##   3. flat lid: radiation density at equal canonical z over the basin floor and over its lower slope (a block 4 to 20 m higher within 200 m) agrees within 5 %.
## The poses are measured from the textures and printed. A calibration probe (known bytes) proves the byte encoding path first.
## RUN (windowed Vulkan, no other Godot running): tools/run_tests.ps1 -Label nf2b -Tests near_fog_terrain_relative_gpu_test
## Prints NEAR_FOG_TERRAIN_RELATIVE=PASS (or =FAIL <reason>). Test only; the harness fixture world is deleted at exit.
const HARNESS := preload("res://tools/landscape_harness_common.gd")
const MARKER := "NEAR_FOG_TERRAIN_RELATIVE"
const N := 1024
const B := 64
const SCALE := 50.0
const RIDGE_GAP_MIN := 60.0
const RIDGE_POSES := ["hills_eye", "v0_ridge_eye"]

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
	HARNESS.remove_dir_recursive(ProjectSettings.globalize_path("res://.godot/captures/landscape/fixture_world"))


func _run() -> void:
	var watchdog := create_timer(1500.0)
	watchdog.timeout.connect(func() -> void: _fail("watchdog timeout (1500 s)"))
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
	var nf := game.get_node_or_null("NearFog")
	if nf != null:
		nf.call("DebugSetAmount", 0.0) # the owner stays dormant: the probe is a material of its own
	var ground_model: RefCounted = null

	print("%s booted" % MARKER)
	# calibration of the byte path
	var cal: Dictionary = await _probe_bytes(null, true)
	_check(cal["hi"] == 100 and cal["lo"] == 200 and cal["valid"] == 255, "byte calibration read %s, expected hi 100 lo 200 valid 255 (output encoding assumption broken)" % str(cal))

	# ---- 1. mist at a high and a low pose
	var hills := HARNESS.pose_by_name("hills_eye").duplicate()
	hills["lift_m"] = 1.5
	var info: Dictionary = await HARNESS.go_pose(self, game, hills, "noon", ground_model, {"yaw": 0.0, "pitch": 0.0})
	if info.has("error"):
		_fail("pose hills_eye: " + str(info["error"]))
		return
	var state: Dictionary = await _await_fields(0)
	if state.is_empty():
		return
	var high_mist: Dictionary = await _density_here(Vector3(0, 1, 0), 0.0)
	var coast := HARNESS.pose_by_name("coast_eye").duplicate()
	coast["lift_m"] = 1.5
	info = await HARNESS.go_pose(self, game, coast, "noon", ground_model, {"yaw": 0.0, "pitch": 0.0})
	if info.has("error"):
		_fail("pose coast_eye: " + str(info["error"]))
		return
	state = await _await_fields(int(state["publishes"]))
	if state.is_empty():
		return
	var low_mist: Dictionary = await _density_here(Vector3(0, 1, 0), 0.0)
	var rel := absf(float(high_mist["density"]) - float(low_mist["density"])) / maxf(float(low_mist["density"]), 1e-9)
	report["mist"] = {"high_pose": "hills_eye", "high_ground_m": float(high_mist["ground"]), "high_density": high_mist["density"], "low_pose": "coast_eye",
		"low_ground_m": float(low_mist["ground"]), "low_density": low_mist["density"], "relative_difference": rel}
	print("%s 1: mist 1.5 m above ground: high (ground %.1f m) %.6f, low (ground %.1f m) %.6f, difference %.4f %%" % [MARKER, float(high_mist["ground"]), float(high_mist["density"]),
		float(low_mist["ground"]), float(low_mist["density"]), rel * 100.0])
	_check(float(high_mist["ground"]) >= 1000.0, "the high pose is at %.1f m, not >= 1000" % float(high_mist["ground"]))
	_check(float(low_mist["density"]) > 1e-3, "the low-pose mist density %.6f is not measurable" % float(low_mist["density"]))
	_check(rel <= 0.02, "mist density differs %.2f %% between the high and the low pose" % (rel * 100.0))

	# the same mist check on a second high pose (a forest edge at 1,300 m)
	var edge := HARNESS.pose_by_name("forest_edge_eye").duplicate()
	edge["lift_m"] = 1.5
	info = await HARNESS.go_pose(self, game, edge, "noon", ground_model, {"yaw": 0.0, "pitch": 0.0})
	if info.has("error"):
		_fail("pose forest_edge_eye: " + str(info["error"]))
		return
	state = await _await_fields(int(state["publishes"]))
	if state.is_empty():
		return
	var edge_mist: Dictionary = await _density_here(Vector3(0, 1, 0), 0.0)
	var edge_rel := absf(float(edge_mist["density"]) - float(low_mist["density"])) / maxf(float(low_mist["density"]), 1e-9)
	report["mist_forest_edge"] = {"ground_m": float(edge_mist["ground"]), "density": edge_mist["density"], "relative_difference": edge_rel}
	print("%s 1b: mist at forest_edge_eye (ground %.1f m) %.6f, difference to the low pose %.4f %%" % [MARKER, float(edge_mist["ground"]), float(edge_mist["density"]), edge_rel * 100.0])
	_check(edge_rel <= 0.02, "mist density differs %.2f %% between forest_edge_eye and the low pose" % (edge_rel * 100.0))

	# ---- 2 and 3: poses from the textures
	var found := false
	var prev_publishes: int = int(state["publishes"])
	for pose_name in RIDGE_POSES:
		var p := HARNESS.pose_by_name(pose_name).duplicate()
		p["lift_m"] = 1.5
		info = await HARNESS.go_pose(self, game, p, "noon", ground_model, {"yaw": 0.0, "pitch": 0.0})
		if info.has("error"):
			print("%s pose %s unavailable: %s" % [MARKER, pose_name, str(info["error"])])
			continue
		state = await _await_fields(prev_publishes)
		if state.is_empty():
			return
		prev_publishes = int(state["publishes"])
		var pick: Dictionary = await _pick_blocks(state)
		if pick.is_empty():
			continue
		report["picked_" + pose_name] = pick
		print("%s poses from %s: %s" % [MARKER, pose_name, JSON.stringify(pick)])
		if float(pick["ridge_gap"]) < RIDGE_GAP_MIN:
			print("%s %s: deepest ridge gap %.1f m < %.0f, trying the next pose" % [MARKER, pose_name, float(pick["ridge_gap"]), RIDGE_GAP_MIN])
			continue
		found = true
		await _check_ridge_and_lid(pick)
		break
	_check(found, "no ridge 60 m above its basin floor found in the fixture poses %s" % str(RIDGE_POSES))

	report["failures"] = failures
	print("%s report %s" % [MARKER, JSON.stringify(report)])
	if not failures.is_empty():
		_fail(" | ".join(failures.slice(0, 12)))
		return
	finished = true
	print("%s=PASS" % MARKER)
	_cleanup()
	quit(0)


# ------------------------------------------------------------------ waiting and reading (as near_fog_terrain_fields_gpu_test.gd)

func _diag() -> Dictionary:
	return producer.call("GetDiagnostics")


func _global(name: String):
	return producer.call("GetGlobalValues").get(name)


func _await_fields(after_publishes: int) -> Dictionary:
	var deadline := Time.get_ticks_msec() + 600000
	var stable := 0
	var next_note := Time.get_ticks_msec() + 20000
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var d := _diag()
		var note := Time.get_ticks_msec() > next_note
		if note:
			next_note = Time.get_ticks_msec() + 20000
			print("%s waiting for fields: phase %s publishes %s (after %d) status %s builds_idle %s pose %s bounds %s" % [MARKER, str(d["phase"]), str(d["publishes"]), after_publishes,
				str(d["status"]), str(HARNESS.COMMON.builds_idle(game)), str(HARNESS.COMMON.pose_now(game)), str(_global("ringworld_near_fog_t0_bounds"))])
		var params = _global("ringworld_near_fog_fields_params")
		var bounds = _global("ringworld_near_fog_t0_bounds")
		var rids: Array = d.get("near_fog_rids", [])
		if note:
			print("%s   params %s rids %d anchor (%s, %s) texel pinned %s" % [MARKER, str(params), rids.size(), str(d["anchor_around"]), str(d["anchor_across"]), str(d.get("near_fog_rids", []).size())])
		var idle := str(d["phase"]) == "Idle" and int(d["publishes"]) > after_publishes and HARNESS.COMMON.builds_idle(game)
		var valid: bool = params is Vector4 and (params as Vector4).z > 0.5 and bounds is Vector4 and rids.size() == 2
		if idle and valid:
			var now: Dictionary = HARNESS.COMMON.pose_now(game)
			var anchor_around := float(d["anchor_around"])
			var anchor_across := float(d["anchor_across"])
			var b: Vector4 = bounds
			var ca := _wrap(float(now["around"]) - anchor_around)
			var cb := float(now["across"]) - anchor_across
			if ca - b.x > 400.0 and b.y - ca > 400.0 and cb - b.z > 400.0 and b.w - cb > 400.0:
				stable += 1
				if stable >= 30:
					return {"publishes": int(d["publishes"]), "anchor_around": anchor_around, "anchor_across": anchor_across, "bounds": b, "params": params, "rids": rids,
						"p_cam_around": ca, "p_cam_across": cb, "around": float(now["around"]), "across": float(now["across"]), "texel": int((params as Vector4).x)}
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


## Shortest signed ring distance: the producer's anchor is not always in the camera's own turn of the ring.
func _wrap(delta: float) -> float:
	var circumference := float(game.get_node("ChunkGridManager").call("GetCanonicalRingCircumferenceMeters64"))
	return fposmod(delta + circumference * 0.5, circumference) - circumference * 0.5


func _sample(around: float, across: float) -> float:
	return float(surface.call("SampleHeight", around, across))


# ------------------------------------------------------------------ picking poses from the textures

## Scans the published T0 and basin textures over the window (400 m margin): the deepest "ridge" block (largest T0 at the block centre minus the basin floor), the floor block (smallest gap),
## and a lower-slope block 4 to 20 m above the floor block's T0 within 200 m. Returns {} on a readback failure.
func _pick_blocks(state: Dictionary) -> Dictionary:
	var rids: Array = state["rids"]
	var t0 := await _read_texture(rids[0])
	var basin := await _read_texture(rids[1])
	if t0.size() != N * N or basin.size() != B * B:
		_check(false, "readback sizes T0 %d basin %d" % [t0.size(), basin.size()])
		return {}
	var texel := float(state["texel"])
	var bounds: Vector4 = state["bounds"]
	var i_min := int(roundf(bounds.x / texel))
	var j_min := int(roundf(bounds.z / texel))
	var blocks_per_side := N / 16
	var bx0 := int(ceil(float(i_min + 100) / 16.0))
	var by0 := int(ceil(float(j_min + 100) / 16.0))
	var gaps := {}
	var ridge := Vector2i(-1, -1)
	var ridge_gap := -1.0
	var floor_block := Vector2i(-1, -1)
	var floor_gap := 1e9
	for by in range(by0, by0 + blocks_per_side - 12):
		for bx in range(bx0, bx0 + blocks_per_side - 12):
			var centre := t0[((by * 16 + 8) & 1023) * N + ((bx * 16 + 8) & 1023)]
			var gap := centre - basin[(by & 63) * B + (bx & 63)]
			gaps[Vector2i(bx, by)] = [gap, centre]
			if gap > ridge_gap:
				ridge_gap = gap
				ridge = Vector2i(bx, by)
	# the floor block: the lowest gap that still has a 4..20 m higher neighbour block within 3 blocks (192 m)
	var slope := Vector2i(-1, -1)
	for key in gaps:
		var gap: float = gaps[key][0]
		if gap >= floor_gap:
			continue
		var centre: float = gaps[key][1]
		var best := Vector2i(-1, -1)
		var best_rise := 1e9
		for dy in range(-3, 4):
			for dx in range(-3, 4):
				var nk := Vector2i(key.x + dx, key.y + dy)
				if not gaps.has(nk):
					continue
				var rise: float = float(gaps[nk][1]) - centre
				if rise >= 4.0 and rise <= 20.0 and rise < best_rise:
					best_rise = rise
					best = nk
		if best.x != -1:
			floor_gap = gap
			floor_block = key
			slope = best
	if ridge.x == -1 or floor_block.x == -1:
		_check(false, "no ridge or floor block found in the window")
		return {}
	var cam_around: float = state["around"]
	var cam_across: float = state["across"]
	var p_cam_around: float = state["p_cam_around"]
	var p_cam_across: float = state["p_cam_across"]
	var pick := {}
	pick["ridge_block"] = [ridge.x, ridge.y]
	pick["ridge_gap"] = ridge_gap
	pick["ridge_around"] = cam_around + ((ridge.x * 16 + 8) * texel - p_cam_around)
	pick["ridge_across"] = cam_across + ((ridge.y * 16 + 8) * texel - p_cam_across)
	pick["floor_block"] = [floor_block.x, floor_block.y]
	pick["floor_gap"] = floor_gap
	pick["floor_around"] = cam_around + ((floor_block.x * 16 + 8) * texel - p_cam_around)
	pick["floor_across"] = cam_across + ((floor_block.y * 16 + 8) * texel - p_cam_across)
	pick["slope_block"] = [slope.x, slope.y]
	pick["slope_around"] = cam_around + ((slope.x * 16 + 8) * texel - p_cam_around)
	pick["slope_across"] = cam_across + ((slope.y * 16 + 8) * texel - p_cam_across)
	pick["slope_rise_m"] = float(gaps[slope][1]) - float(gaps[floor_block][1])
	return pick


# ------------------------------------------------------------------ checks 2 and 3

func _check_ridge_and_lid(pick: Dictionary) -> void:
	var rad := Vector3(1, 0, 0)
	# 2. ridge top vs basin floor, both 1.5 m above ground
	var ridge_d: Dictionary = await _density_at(float(pick["ridge_around"]), float(pick["ridge_across"]), 1.5, rad, NAN)
	var floor_d: Dictionary = await _density_at(float(pick["floor_around"]), float(pick["floor_across"]), 1.5, rad, NAN)
	var ratio := float(ridge_d["density"]) / maxf(float(floor_d["density"]), 1e-9)
	report["ridge"] = {"ridge_ground_m": ridge_d["ground"], "ridge_density": ridge_d["density"], "floor_ground_m": floor_d["ground"], "floor_density": floor_d["density"], "ratio": ratio,
		"ridge_gap_m": pick["ridge_gap"], "floor_gap_m": pick["floor_gap"]}
	print("%s 2: radiation 1.5 m above ground: ridge (gap %.1f m, ground %.1f m) %.6f, basin floor (gap %.1f m, ground %.1f m) %.6f, ratio %.4f" % [MARKER, float(pick["ridge_gap"]),
		float(ridge_d["ground"]), float(ridge_d["density"]), float(pick["floor_gap"]), float(floor_d["ground"]), float(floor_d["density"]), ratio])
	_check(float(floor_d["density"]) > 0.005, "the basin-floor radiation density %.6f is not close to sigma_rad 0.01" % float(floor_d["density"]))
	_check(ratio < 0.05, "ridge-top radiation density is %.2f %% of the basin floor value (limit 5 %%)" % (ratio * 100.0))
	# 3. flat lid: equal canonical z over the floor and over its lower slope
	var z_target := _sample(float(pick["floor_around"]), float(pick["floor_across"])) + 25.0
	var over_floor: Dictionary = await _density_at(float(pick["floor_around"]), float(pick["floor_across"]), 1.5, rad, z_target)
	var over_slope: Dictionary = await _density_at(float(pick["slope_around"]), float(pick["slope_across"]), 1.5, rad, z_target)
	var rel := absf(float(over_floor["density"]) - float(over_slope["density"])) / maxf(float(over_floor["density"]), 1e-9)
	report["lid"] = {"z_m": z_target, "floor_ground_m": over_floor["ground"], "slope_ground_m": over_slope["ground"], "floor_density": over_floor["density"], "slope_density": over_slope["density"],
		"relative_difference": rel, "probe_z_floor": over_floor["z"], "probe_z_slope": over_slope["z"]}
	print("%s 3: radiation at z %.2f m: over the floor (ground %.2f m) %.6f, over the slope (ground %.2f m, %.1f m higher) %.6f, difference %.3f %%" % [MARKER, z_target, float(over_floor["ground"]),
		float(over_floor["density"]), float(over_slope["ground"]), float(pick["slope_rise_m"]), float(over_slope["density"]), rel * 100.0])
	_check(float(over_floor["density"]) > 1e-3, "the lid-test density %.6f is not measurable" % float(over_floor["density"]))
	_check(rel <= 0.05, "radiation density at equal z differs %.2f %% between the basin floor and its lower slope" % (rel * 100.0))


# ------------------------------------------------------------------ the probe

const PROBE_SHADER := """
shader_type spatial;
render_mode unshaded, cull_disabled;
#include "res://materials/ring_near_fog_fields.gdshaderinc"
uniform vec3 drive = vec3(0.0);
uniform float layer_h = 40.0;
uniform float scale = 50.0;
uniform float calibrate = 0.0;
float lin(float c) {
	return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4);
}
void fragment() {
	vec3 world = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec3 fp = ring_near_fog_frame_point(world);
	float valid = ring_near_fog_fields_valid(fp.xy) ? 1.0 : 0.0;
	float d = ring_near_fog_density(fp.z, ring_near_fog_t0(fp.xy), ring_near_fog_basin(fp.xy), vec2(0.0), drive, RING_NEAR_FOG_SIGMA, layer_h, 0.0);
	float v = floor(clamp(d * scale, 0.0, 0.99998) * 65535.0 + 0.5);
	float hi = floor(v / 256.0);
	float lo = v - hi * 256.0;
	if (calibrate > 0.5) {
		hi = 100.0;
		lo = 200.0;
		valid = 1.0;
	}
	ALBEDO = vec3(lin(hi / 255.0), lin(lo / 255.0), lin(valid));
}
"""


## Renders the probe quad 0.5 m in front of a probe camera placed so that the quad sits at world_point; returns the raw bytes {hi, lo, valid}.
func _probe_bytes(world_point, calibrate: bool, drive := Vector3.ZERO, layer_h := 40.0) -> Dictionary:
	var viewport := SubViewport.new()
	viewport.size = Vector2i(64, 64)
	viewport.own_world_3d = true
	viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	root.add_child(viewport)
	var probe_camera := Camera3D.new()
	viewport.add_child(probe_camera)
	var xform := Transform3D.IDENTITY
	if world_point != null:
		var frame: Projection = _global("ringworld_height_shadow_frame")
		var up := Vector3(frame.x.z, frame.y.z, frame.z.z).normalized()
		var main_camera := root.get_camera_3d()
		var forward := -main_camera.global_transform.basis.z
		forward = (forward - up * forward.dot(up)).normalized()
		xform = Transform3D(Basis.looking_at(forward, up), (world_point as Vector3) - forward * 0.5)
	probe_camera.global_transform = xform
	probe_camera.current = true
	var shader := Shader.new()
	shader.code = PROBE_SHADER
	var material := ShaderMaterial.new()
	material.shader = shader
	material.set_shader_parameter("calibrate", 1.0 if calibrate else 0.0)
	material.set_shader_parameter("drive", drive)
	material.set_shader_parameter("layer_h", layer_h)
	material.set_shader_parameter("scale", SCALE)
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
	var c := image.get_pixel(32, 32)
	viewport.queue_free()
	return {"hi": int(roundf(c.r * 255.0)), "lo": int(roundf(c.g * 255.0)), "valid": int(roundf(c.b * 255.0))}


## Density at the player's position plus an offset along ring-up so that the probe's canonical z equals z_target (NAN = no offset: the player's own point, 1.5 m above ground).
func _density_here(drive: Vector3, z_target: float, layer_h := 40.0) -> Dictionary:
	var explorer := game.get_node("Explorer") as Node3D
	var frame: Projection = _global("ringworld_height_shadow_frame")
	var up := Vector3(frame.x.z, frame.y.z, frame.z.z).normalized()
	var now: Dictionary = HARNESS.COMMON.pose_now(game)
	var lift := 0.0 if is_nan(z_target) or z_target == 0.0 else z_target - float(now["vertical"])
	var ground := _sample(float(now["around"]), float(now["across"]))
	var bytes: Dictionary = await _probe_bytes(explorer.global_position + up * lift, false, drive, layer_h)
	if int(bytes["valid"]) < 128:
		_check(false, "ring_near_fog_fields_valid is false at the probe point (ground %.1f m)" % ground)
	var s := (float(bytes["hi"]) * 256.0 + float(bytes["lo"])) / 65535.0
	return {"density": s / SCALE, "ground": ground, "z": float(now["vertical"]) + lift, "above_ground": float(now["vertical"]) - ground}


## Teleports the player to (around, across) at ground + lift_above_ground, then reads the density at canonical z_target (NAN = at the player).
func _density_at(around: float, across: float, lift_above_ground: float, drive: Vector3, z_target: float) -> Dictionary:
	var explorer: Node = game.get_node("Explorer")
	var ground := _sample(around, across)
	explorer.call("TeleportToCanonicalMeters", around, ground + lift_above_ground, across, 0.0, 0.0, true)
	await HARNESS.COMMON.settle(self, 0.5)
	var result: Dictionary = await _density_here(drive, 0.0 if is_nan(z_target) else z_target)
	result["ground"] = ground
	return result
