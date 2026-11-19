extends SceneTree

## NF-2b GPU test (docs/sun-flare-shafts-near-fog-plan.md, NF-1 design N6/N8): the near ground fog is byte-identical to the feature absent whenever it is dormant.
## Boots the pinned v8 fixture (tools/landscape_harness_common.gd) and, at two poses at noon, in still-capture mode (tools/tree_visibility_world.gd capture_begin / capture_end),
## compares the root viewport's raw RGBA8 bytes with the same frame after the RingNearFog node is REMOVED from the tree (the feature absent: no owner, no FogVolume, the cloud layer's own
## volumetric-fog writes in force), for each of
##   (a) Amount 0 (the shipped default),
##   (b) Amount 1 with ForcedDrive (0, 0, 0) (a clear dry noon: drivers give 0, so the fog never activates),
##   (c) Amount 1 with forced fog on (ForcedDrive (1, 0.5, 0), ForcedGate 1) until the fog is enabled, then forced off (ForcedDrive 0) and 3 s later the fade has completed,
## each with FXAA and with TAA, as present / absent / present again (a case whose two present frames differ drifted with streaming and is retried, up to 3 times). A case whose two consecutive captures differ (not deterministic even without the feature) is reported and not judged; the test fails when a judged case
## differs, when fewer than half the cases are judged, or when (c) never turned the fog on (vacuous). Also saves stills with forced fog on (ForcedDrive (1, 0.5, 0), Amount 1,
## hills_eye and hills_300m at a setting sun of 10 degrees) under .godot/audit_baseline/common/atmosphere/nf2b/ for review (pass --stills=<dir> to move them).
## RUN (windowed Vulkan, no other Godot running): tools/run_tests.ps1 -Label nf2b -Tests near_fog_identity_gpu_test
## Prints NEAR_FOG_IDENTITY=PASS (or =FAIL <reason>) and a per-case table. Test only; the harness fixture world is deleted at exit.
const HARNESS := preload("res://tools/landscape_harness_common.gd")
const WORLD := preload("res://tools/tree_visibility_world.gd")
const MARKER := "NEAR_FOG_IDENTITY"
const POSES := ["hills_eye", "forest_edge_eye"]
const FOG_ON_DRIVE := Vector3(1.0, 0.5, 0.0)
const FOG_OFF_DRIVE := Vector3(0.0, 0.0, 0.0)
const NO_DRIVE := Vector3(-1.0, -1.0, -1.0)

var game: Node
var nf: Node
var failures: Array[String] = []
var rows: Array = []
var finished := false
var args := {}
var stills_dir := ""


func _init() -> void:
	call_deferred("_run")


func _fail(message: String) -> void:
	if finished:
		return
	finished = true
	push_error("%s=FAIL %s" % [MARKER, message])
	_cleanup()
	quit(1)


func _cleanup() -> void:
	if game != null and is_instance_valid(game):
		WORLD.capture_end(game)
	HARNESS.remove_dir_recursive(ProjectSettings.globalize_path("res://.godot/captures/landscape/fixture_world"))


func _frames(n: int) -> void:
	for i in n:
		WORLD.hold_wind()
		await process_frame


func _stable_grab() -> Dictionary:
	await _frames(20)
	var prev: Image
	for i in 40:
		WORLD.hold_wind()
		await RenderingServer.frame_post_draw
		var img := WORLD.grab(self)
		if prev != null and prev.get_data() == img.get_data():
			return {"image": img, "stable": true}
		prev = img
	return {"image": prev, "stable": false}


func _diff(a: Image, b: Image) -> Dictionary:
	var da := a.get_data()
	var db := b.get_data()
	if da.size() != db.size():
		return {"pixels": -1, "max_abs": -1}
	var pixels := 0
	var max_abs := 0
	for i in range(0, da.size(), 4):
		var d := maxi(maxi(absi(da[i] - db[i]), absi(da[i + 1] - db[i + 1])), maxi(absi(da[i + 2] - db[i + 2]), absi(da[i + 3] - db[i + 3])))
		if d > 0:
			pixels += 1
			max_abs = maxi(max_abs, d)
	return {"pixels": pixels, "max_abs": max_abs}


func _set_aa(taa: bool) -> void:
	root.use_taa = taa
	root.screen_space_aa = Viewport.SCREEN_SPACE_AA_DISABLED if taa else Viewport.SCREEN_SPACE_AA_FXAA
	root.msaa_3d = Viewport.MSAA_DISABLED


func _configure(case_id: String) -> bool:
	## Applies one case; returns false when the case could not reach its precondition.
	nf.set("ForcedGate", -1.0)
	nf.set("ForcedDrive", NO_DRIVE)
	nf.call("DebugSetAmount", 0.0)
	await _frames(120)
	if case_id == "a":
		return not bool(nf.get("FogEnabled"))
	if case_id == "b":
		nf.call("DebugSetAmount", 1.0)
		nf.set("ForcedDrive", FOG_OFF_DRIVE)
		await _frames(30)
		return not bool(nf.get("FogEnabled"))
	# (c): on until the fog is enabled and fully faded in, then forced off and the fade-out completes. Normal time scale (the fade runs on frame delta).
	nf.call("DebugSetAmount", 1.0)
	nf.set("ForcedGate", 1.0)
	nf.set("ForcedDrive", FOG_ON_DRIVE)
	var deadline := Time.get_ticks_msec() + 8000
	while Time.get_ticks_msec() < deadline and not (bool(nf.get("FogEnabled")) and float(nf.get("Fade")) >= 0.999):
		await process_frame
	var was_on := bool(nf.get("FogEnabled")) and float(nf.get("Fade")) >= 0.999
	nf.set("ForcedDrive", FOG_OFF_DRIVE)
	deadline = Time.get_ticks_msec() + 8000
	while Time.get_ticks_msec() < deadline and bool(nf.get("FogEnabled")):
		await process_frame
	await _frames(30)
	return was_on and not bool(nf.get("FogEnabled"))


func _case(pose_name: String, aa: String, case_id: String) -> void:
	_set_aa(aa == "taa")
	var ok: bool = await _configure(case_id)
	var tag := "%s/%s/%s" % [pose_name, aa, case_id]
	if not ok:
		failures.append("%s: precondition not reached (fog enabled=%s fade=%s)" % [tag, str(nf.get("FogEnabled")), str(nf.get("Fade"))])
		return
	var cap: Dictionary = await WORLD.capture_begin(self, game)
	# A-B-A: present, absent, present again. Streaming that lands between the grabs (the capture hold freezes the clocks, not the workers) shows as present != present again; such
	# an attempt is retried (up to 3), so only a difference the feature itself makes is judged.
	var x: Dictionary = {}
	var r: Dictionary = {}
	var drifted := true
	var parent := nf.get_parent()
	for attempt in 3:
		x = await _stable_grab()
		parent.remove_child(nf)
		await _frames(10)
		r = await _stable_grab()
		parent.add_child(nf)
		await _frames(10)
		var x2: Dictionary = await _stable_grab()
		drifted = int(_diff(x["image"], x2["image"])["pixels"]) != 0
		if not drifted or not bool(x["stable"]):
			break   # (an unstable present frame, TAA, is reported unjudged anyway: no retry)
		print("%s case %s attempt %d: the present frame drifted between grabs (streaming), retrying" % [MARKER, tag, attempt + 1])
	WORLD.capture_end(game)
	var d := _diff(x["image"], r["image"])
	var judged: bool = bool(x["stable"]) and bool(r["stable"]) and not drifted
	var same: bool = int(d["pixels"]) == 0
	rows.append({"tag": tag, "judged": judged, "identical": same, "pixels": int(d["pixels"]), "max": int(d["max_abs"]), "cap_deterministic": bool(cap["deterministic"]),
		"stable_x": bool(x["stable"]), "stable_r": bool(r["stable"])})
	print("%s case %s judged=%s identical=%s differing_px=%d max=%d (capture det=%s, present stable=%s, absent stable=%s)" % [MARKER, tag, str(judged), str(same), int(d["pixels"]), int(d["max_abs"]),
		str(cap["deterministic"]), str(x["stable"]), str(r["stable"])])
	if aa == "fxaa" and not judged:
		failures.append("%s: not deterministic even without the feature, cannot be judged" % tag)
	if judged and not same:
		failures.append("%s differs from the feature absent: %d px, max %d" % [tag, int(d["pixels"]), int(d["max_abs"])])
		(x["image"] as Image).save_png(stills_dir.path_join("fail_" + tag.replace("/", "_") + "_present.png"))
		(r["image"] as Image).save_png(stills_dir.path_join("fail_" + tag.replace("/", "_") + "_absent.png"))


## Positive control: with forced fog on the frame must DIFFER from the feature absent (proves the shader compiled and the fog is drawn, so the identity rows are not vacuous).
## Waits until the producer's T0 window (published bounds) contains the camera with a 300 m margin, so the fog's fields are valid at the pose.
func _await_fields_here() -> bool:
	var producer := game.get_node_or_null("TerrainHeightShadow")
	var deadline := Time.get_ticks_msec() + 300000
	var stable := 0
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var values: Dictionary = producer.call("GetGlobalValues")
		var d: Dictionary = producer.call("GetDiagnostics")
		var params = values.get("ringworld_near_fog_fields_params")
		var bounds = values.get("ringworld_near_fog_t0_bounds")
		if not (params is Vector4 and (params as Vector4).z > 0.5 and bounds is Vector4) or str(d["phase"]) != "Idle":
			stable = 0
			continue
		var now: Dictionary = HARNESS.COMMON.pose_now(game)
		var circumference := float(game.get_node("ChunkGridManager").call("GetCanonicalRingCircumferenceMeters64"))
		var ca := fposmod(float(now["around"]) - float(d["anchor_around"]) + circumference * 0.5, circumference) - circumference * 0.5
		var cb := float(now["across"]) - float(d["anchor_across"])
		var b: Vector4 = bounds
		if ca - b.x > 300.0 and b.y - ca > 300.0 and cb - b.z > 300.0 and b.w - cb > 300.0:
			stable += 1
			if stable >= 30:
				return true
		else:
			stable = 0
	return false


func _positive_control(pose_name: String) -> void:
	# A strong forced fog (amount 8, all kinds) so that even a pose on a ridge, where the basin lid and the thin mist alone stay under one 8-bit step, shows a difference.
	_set_aa(false)
	nf.set("ForcedGate", 1.0)
	nf.set("ForcedDrive", Vector3(1.0, 1.0, 1.0))
	nf.call("DebugSetAmount", 8.0)
	var deadline := Time.get_ticks_msec() + 10000
	while Time.get_ticks_msec() < deadline and not (bool(nf.get("FogEnabled")) and float(nf.get("Fade")) >= 0.999):
		await process_frame
	var on := bool(nf.get("FogEnabled"))
	var producer := game.get_node_or_null("TerrainHeightShadow")
	var gv: Dictionary = producer.call("GetGlobalValues")
	var dg: Dictionary = producer.call("GetDiagnostics")
	print("%s positive control %s fields: params %s bounds %s anchor (%s, %s) rids %d pose %s rate %.5f gate %.3f" % [MARKER, pose_name, str(gv.get("ringworld_near_fog_fields_params")),
		str(gv.get("ringworld_near_fog_t0_bounds")), str(dg["anchor_around"]), str(dg["anchor_across"]), (dg.get("near_fog_rids", []) as Array).size(), str(HARNESS.COMMON.pose_now(game)),
		float(nf.get("LastRate")), float(nf.get("LastWorldGate"))])
	await WORLD.capture_begin(self, game)
	var x: Dictionary = await _stable_grab()
	var parent := nf.get_parent()
	parent.remove_child(nf)
	await _frames(10)
	var r: Dictionary = await _stable_grab()
	parent.add_child(nf)
	WORLD.capture_end(game)
	var d := _diff(x["image"], r["image"])
	print("%s positive control %s: fog on=%s, differing px vs absent %d max %d" % [MARKER, pose_name, str(on), int(d["pixels"]), int(d["max_abs"])])
	if not on or int(d["pixels"]) == 0:
		failures.append("%s positive control: forced fog on does not change the frame (fog_on=%s, %d px): the fog is not drawn" % [pose_name, str(on), int(d["pixels"])])
	nf.call("DebugSetAmount", 0.0)
	nf.set("ForcedDrive", NO_DRIVE)
	nf.set("ForcedGate", -1.0)
	await _frames(150)


func _still(pose_name: String, file_name: String, force_gate: bool) -> void:
	var pose := HARNESS.pose_by_name(pose_name)
	var ground_model: RefCounted = null
	var info: Dictionary = await HARNESS.go_pose(self, game, pose, "sun_10_set", ground_model)
	if info.has("error"):
		failures.append("still pose %s: %s" % [pose_name, str(info["error"])])
		return
	if not await _await_fields_here():
		failures.append("still pose %s: the terrain fields never covered the camera" % pose_name)
		return
	_set_aa(false)
	nf.set("ForcedGate", 1.0 if force_gate else -1.0)
	nf.set("ForcedDrive", FOG_ON_DRIVE)
	nf.call("DebugSetAmount", 1.0)
	var deadline := Time.get_ticks_msec() + 10000
	while Time.get_ticks_msec() < deadline and not (bool(nf.get("FogEnabled")) and float(nf.get("Fade")) >= 0.999):
		await process_frame
	var on := bool(nf.get("FogEnabled"))
	await WORLD.capture_begin(self, game)
	var img: Dictionary = await _stable_grab()
	WORLD.capture_end(game)
	(img["image"] as Image).save_png(stills_dir.path_join(file_name))
	print("%s still %s fog_on=%s stable=%s gate=%.3f drive=%s" % [MARKER, file_name, str(on), str(img["stable"]), float(nf.get("LastWorldGate")), str(nf.get("CurrentDrive"))])
	nf.call("DebugSetAmount", 0.0)
	nf.set("ForcedDrive", NO_DRIVE)
	nf.set("ForcedGate", -1.0)
	if not on:
		failures.append("still %s: the fog never turned on" % file_name)
	await _frames(150) # fade out in normal time


func _run() -> void:
	for raw in OS.get_cmdline_user_args():
		var text := String(raw)
		if text.begins_with("--"):
			var eq := text.find("=")
			args[text.substr(2, eq - 2) if eq > 0 else text.substr(2)] = text.substr(eq + 1) if eq > 0 else "1"
	var watchdog := create_timer(2400.0)
	watchdog.timeout.connect(func() -> void: _fail("watchdog timeout (2400 s)"))
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice is required")
		return
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	stills_dir = String(args.get("stills", ProjectSettings.globalize_path("res://.godot/audit_baseline/common/atmosphere/nf2b")))
	DirAccess.make_dir_recursive_absolute(stills_dir)
	var booted: Dictionary = await HARNESS.boot(self, MARKER)
	if booted.is_empty():
		_fail("boot failed")
		return
	game = booted["game"]
	var monitor := game.get_node_or_null("RingPerformanceMonitor")
	if monitor != null:
		monitor.process_mode = Node.PROCESS_MODE_DISABLED
	nf = game.get_node_or_null("NearFog")
	if nf == null:
		_fail("no NearFog node in the game scene")
		return
	WORLD.freeze_world(game)
	WORLD.hide_overlays(game)
	WORLD.quiet_scene(game)
	if float(nf.get("Fade")) != 0.0 or bool(nf.get("FogEnabled")):
		_fail("the fog is not dormant at start")
		return
	var ground_model: RefCounted = null
	for pose_name in POSES:
		var pose := HARNESS.pose_by_name(pose_name)
		var info: Dictionary = await HARNESS.go_pose(self, game, pose, "noon", ground_model)
		if info.has("error"):
			failures.append("pose %s: %s" % [pose_name, str(info["error"])])
			continue
		WORLD.freeze_world(game)
		await _frames(60)
		if not await _await_fields_here():
			failures.append("pose %s: the terrain fields never covered the camera" % pose_name)
			continue
		await _positive_control(pose_name)
		for aa in ["fxaa", "taa"]:
			for case_id in ["a", "b", "c"]:
				await _case(pose_name, aa, case_id)
	await _still("hills_eye", "nf2b_forced_fog_hills_eye_sun10.png", false)
	await _still("hills_300m", "nf2b_forced_fog_hills_300m_sun10.png", true)
	var judged := 0
	for r in rows:
		if bool(r["judged"]):
			judged += 1
	print("%s cases %d, judged %d, failures %d" % [MARKER, rows.size(), judged, failures.size()])
	if rows.is_empty():
		_fail("nothing was compared")
		return
	print("%s TAA cases are reported, not judged, when two consecutive TAA frames of the same state differ (the jitter advances every frame and cannot be pinned in this build)" % MARKER)
	if not failures.is_empty():
		_fail(" | ".join(failures.slice(0, 8)))
		return
	finished = true
	print("%s=PASS %d of %d cases judged byte-identical to the feature absent" % [MARKER, judged, rows.size()])
	_cleanup()
	quit(0)
