extends SceneTree
## NF-7 near-fog review pack (docs/sun-flare-shafts-near-fog-plan.md, N8 NF-7 row): frame sequences and stills of the near ground fog on the player's world.
## Boots the real start menu on the isolated slot 3 copy and presses Continue (tools/tree_visibility_world.gd), as the player does. Windowed Vulkan only.
## Run with a fixed frame time so every saved frame is 1/30 s of game time whatever the grab costs:
##   tools/tree_visibility_run.ps1 -Test near_fog_review -Resolution 1280x720 -EngineArgs "--fixed-fps 30" -World "$env:APPDATA\Godot\app_userdata\RingworldTechDemo\bench_worlds\slot3_2026-11-16\world" [-ScriptArgs "--clips=dawn_floor,walk_on"]
## The poses are those of tools/fixtures/perf_bench_scenarios.json, valid on the perf snapshot world slot3_2026-11-16 (v9, seed 2028030306); without -World the live slot 3 is used,
## which since 2026-11-19 is a different world where they fall in the ocean. The clips do not check the world themselves: check "ground" in report.json.
## Clips (each <out>/<clip>/frame%08d.png, ffmpeg_tools.ps1 reads <out>/<clip>/frame.png):
##   dawn_floor   valley floor (mountain_valley_eye), sun 7 deg rising, slow pan          D = (1, 0.6, 0)
##   dawn_ridge   the highest point 100..280 m from that floor, looking down at it          D = (1, 0.6, 0)
##   forest_rain  forest_edge_eye facing the sun (12 deg), wet map soaked, no rain       D = (0, 1, 0)   (the sunward calibration check)
##   river_steam  the dry bank of a river reach, looking over the water, sun 6 deg rising  D = (0, 0.3, 1)
##   night        mountain_valley_eye at a planet-lit night                               D = (1, 0.6, 0)
##   walk_on / walk_off  forest_clearing_eye, sun 10 deg: walk 4 s, then 180 deg turns at 360 deg/s, fog on and off (flicker reference)
## Every clip also logs the world's own drive at that place and time (before forcing) and saves on / off stills of its first view.
## Clear noon (plains_eye, real drivers): stills with amount 1, 0, 1 and their differing pixel counts (must equal fog off when the drivers are 0).
## dawn_survey (not in the default set): the world's own drive, water texels and fog state at the ground poses at sun 7 deg rising and at a planet-lit night (steam activation check).
## Prints NEAR_FOG_REVIEW=PASS (report.json) or =FAIL <reason>.

const COMMON := preload("res://tools/shadow_harness_common.gd")
const WORLD := preload("res://tools/tree_visibility_world.gd")
const AXES := preload("res://tools/RainPrecipitationTestDriver.cs")
const SWIM := preload("res://tools/InlandSwimProbe.cs")
const MARKER := "NEAR_FOG_REVIEW"
const NO_DRIVE := Vector3(-1.0, -1.0, -1.0)
const ALL_CLIPS := ["noon", "dawn_floor", "dawn_ridge", "forest_rain", "river_steam", "night", "walk_on", "walk_off"]

var args := {}
var out_dir := ""
var report := {"clips": {}, "errors": []}
var game: Node
var explorer: Node
var surface: Node
var nf: Node
var wet: Node
var axes_helper: RefCounted
var poses := {}
var held := {}


func _initialize() -> void:
	for raw in OS.get_cmdline_user_args():
		var text := String(raw)
		if text.begins_with("--"):
			var eq := text.find("=")
			if eq > 0:
				args[text.substr(2, eq - 2)] = text.substr(eq + 1)
			else:
				args[text.substr(2)] = "1"
	out_dir = String(args.get("out", ProjectSettings.globalize_path("res://.godot/captures/near_fog_review")))
	DirAccess.make_dir_recursive_absolute(out_dir)
	call_deferred("_run")


func _log(text: String) -> void:
	print("%s %s" % [MARKER, text])


func _finish(ok: bool, reason: String) -> void:
	_release_keys()
	if nf != null and is_instance_valid(nf):
		nf.set("ForcedDrive", NO_DRIVE)
		nf.set("ForcedGate", -1.0)
		nf.call("DebugSetAmount", 1.0)
	report["status"] = "PASS" if ok else "FAIL"
	report["reason"] = reason
	var f := FileAccess.open(out_dir.path_join("report.json"), FileAccess.WRITE)
	if f != null:
		f.store_string(JSON.stringify(report, "  "))
		f.close()
	print("%s=%s%s" % [MARKER, "PASS" if ok else "FAIL", "" if reason == "" else " " + reason])
	quit(0 if ok else 1)


func _frames(n: int) -> void:
	for i in n:
		await process_frame


func _run() -> void:
	if not WORLD.profile_is_isolated():
		_finish(false, "user:// is not an isolated test profile")
		return
	var err: Array = []
	game = await WORLD.boot_game(self, err)
	if game == null:
		_finish(false, "boot: " + str(err))
		return
	explorer = game.get_node("Explorer")
	surface = game.get_node("Terrain3DGameplay")
	nf = game.get_node_or_null("NearFog")
	wet = game.get_node_or_null("WetWorld")
	if nf == null:
		_finish(false, "no NearFog node")
		return
	axes_helper = AXES.new()
	var scen: Dictionary = JSON.parse_string(FileAccess.get_file_as_string("res://tools/fixtures/perf_bench_scenarios.json"))
	poses = scen.get("poses", {})
	var gq := game.get_node_or_null("GraphicsQuality")
	report["preset"] = str(gq.get("CurrentPresetName")) if gq != null else "none"
	report["resolution"] = str(root.size)
	report["aa"] = {"taa": root.use_taa, "ssaa": root.screen_space_aa, "msaa": root.msaa_3d, "scaling_3d_mode": root.scaling_3d_mode, "scaling_3d_scale": root.scaling_3d_scale}
	report["fixed_fps"] = Engine.physics_ticks_per_second
	_log("preset %s, resolution %s, aa %s" % [report["preset"], report["resolution"], str(report["aa"])])
	WORLD.hide_overlays(game)
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	var chosen: Array = ALL_CLIPS if String(args.get("clips", "")) == "" else Array(String(args["clips"]).split(","))
	for clip in chosen:
		var t0 := Time.get_ticks_msec()
		var rec: Dictionary = {}
		match String(clip):
			"noon": rec = await _clip_noon()
			"dawn_floor": rec = await _clip_dawn_floor()
			"dawn_ridge": rec = await _clip_dawn_ridge()
			"forest_rain": rec = await _clip_forest_rain()
			"river_steam": rec = await _clip_river()
			"night": rec = await _clip_night()
			"walk_on": rec = await _clip_walk(true)
			"walk_off": rec = await _clip_walk(false)
			"dawn_survey": rec = await _clip_dawn_survey()
			_: rec = {"error": "unknown clip"}
		rec["wall_s"] = (Time.get_ticks_msec() - t0) * 0.001
		report["clips"][clip] = rec
		if rec.has("error"):
			report["errors"].append("%s: %s" % [clip, str(rec["error"])])
		_log("clip %s done in %.0f s: %s" % [clip, rec["wall_s"], JSON.stringify(rec)])
		_reset_forcing()
	_finish(report["errors"].is_empty(), "" if report["errors"].is_empty() else "%d clip error(s): %s" % [report["errors"].size(), str(report["errors"])])


# ------------------------------------------------------------------ fog state

func _reset_forcing() -> void:
	_release_keys()
	nf.set("ForcedDrive", NO_DRIVE)
	nf.set("ForcedGate", -1.0)
	nf.call("DebugSetAmount", 1.0)
	if wet != null:
		wet.call("ClearForce")


func _fog_state() -> Dictionary:
	var d: Vector3 = nf.get("CurrentDrive")
	return {"drive": [snappedf(d.x, 0.001), snappedf(d.y, 0.001), snappedf(d.z, 0.001)], "rate_per_m": float(nf.get("LastRate")), "gate": snappedf(float(nf.get("LastWorldGate")), 0.001),
		"fog_enabled": bool(nf.get("FogEnabled")), "fade": snappedf(float(nf.get("Fade")), 0.001), "water_texels": int(nf.call("DebugWaterTexelCount"))}


## The world's own drive here and now (no forcing), after the smoother has been reset by the teleport and has had a few probe ticks.
func _natural() -> Dictionary:
	nf.set("ForcedDrive", NO_DRIVE)
	nf.set("ForcedGate", -1.0)
	await COMMON.settle(self, 2.5)
	return _fog_state()


## Forces the drive (gate from the world) and waits for the owner fade, then lets temporal reprojection settle.
func _force(drive: Vector3, gate := -1.0) -> bool:
	nf.call("DebugSetAmount", 1.0)
	nf.set("ForcedGate", gate)
	nf.set("ForcedDrive", drive)
	var deadline := Time.get_ticks_msec() + 15000
	while Time.get_ticks_msec() < deadline and not (bool(nf.get("FogEnabled")) and float(nf.get("Fade")) >= 0.999):
		await process_frame
	await _frames(45)
	return bool(nf.get("FogEnabled")) and float(nf.get("Fade")) >= 0.999


func _fog_off() -> void:
	nf.call("DebugSetAmount", 0.0)
	var deadline := Time.get_ticks_msec() + 15000
	while Time.get_ticks_msec() < deadline and bool(nf.get("FogEnabled")):
		await process_frame
	await _frames(30)


# ------------------------------------------------------------------ placement

## Poses of tools/fixtures/perf_bench_scenarios.json (slot 3, v9).
func _pose(name: String) -> Dictionary:
	return poses.get(name, {})


func _await_fields_here() -> bool:
	var producer := game.get_node_or_null("TerrainHeightShadow")
	if producer == null:
		return false
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
		var now: Dictionary = COMMON.pose_now(game)
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


## Grounded teleport (flight false) to around / across at ground + lift, or noclip at an absolute height when vertical is given; time; streaming idle; terrain fields.
func _go(around: float, across: float, time: Dictionary, lift := 1.7, vertical := NAN, flight := false) -> Dictionary:
	var ground := float(surface.call("SampleHeight", around, across))
	var h := vertical if not is_nan(vertical) else ground + lift
	explorer.call("TeleportToCanonicalMeters", around, h, across, 0.0, 0.0, flight)
	await COMMON.settle(self, 0.5)
	var idle: bool = await COMMON.wait_builds_idle(self, game, 240.0)
	var info: Dictionary = COMMON.apply_time(game, time)
	if info.has("error"):
		return {"error": "time: " + str(info["error"])}
	var cycle := game.get_node_or_null("RingDayNightCycle")
	if cycle != null:
		cycle.set("Paused", true)
	await COMMON.settle(self, 3.0)
	idle = idle and await COMMON.wait_builds_idle(self, game, 120.0)
	if not await _await_fields_here():
		return {"error": "terrain fields never covered the camera"}
	# The drivers' smoother (tau 20 s) was reset by the teleport at the time of day before apply_time; a 400 m hop and back resets it at this time, so _natural() reads this place and time.
	explorer.call("TeleportToCanonicalMeters", around + 400.0, h + 50.0, across, 0.0, 0.0, true)
	await _frames(3)
	explorer.call("TeleportToCanonicalMeters", around, h, across, 0.0, 0.0, flight)
	await COMMON.settle(self, 1.0)
	idle = idle and await COMMON.wait_builds_idle(self, game, 60.0)
	if not await _await_fields_here():
		return {"error": "terrain fields never covered the camera after the smoother reset"}
	var now: Dictionary = COMMON.pose_now(game)
	return {"around": around, "across": across, "ground": ground, "camera_vertical": float(now.get("vertical", h)), "builds_idle": idle,
		"sun_elevation_deg": snappedf(float(info.get("sun_elevation_deg", 0.0)), 0.01), "giant_elevation_deg": snappedf(float(info.get("giant_elevation_deg", 0.0)), 0.01)}


## Yaw (explorer presentation degrees) facing a canonical horizontal direction (d_around, d_across), measured from the camera's forward at yaw 0 and 90.
func _yaw_toward(d_around: float, d_across: float) -> float:
	var cam := root.get_camera_3d()
	explorer.call("SetPresentationView", 0.0, 0.0)
	await _frames(2)
	var f0 := -cam.global_basis.z
	explorer.call("SetPresentationView", 90.0, 0.0)
	await _frames(2)
	var f90 := -cam.global_basis.z
	var ax: Dictionary = axes_helper.call("FrameAxes", game)
	var dir: Vector3 = Vector3(ax["around"]) * d_around + Vector3(ax["across"]) * d_across
	return rad_to_deg(atan2(dir.dot(f90), dir.dot(f0)))


func _yaw_to_sun() -> float:
	var cam := root.get_camera_3d()
	explorer.call("SetPresentationView", 0.0, 0.0)
	await _frames(2)
	var f0 := -cam.global_basis.z
	explorer.call("SetPresentationView", 90.0, 0.0)
	await _frames(2)
	var f90 := -cam.global_basis.z
	var sun := (game.get_node("StarLight") as Node3D).global_basis.z.normalized()
	return rad_to_deg(atan2(sun.dot(f90), sun.dot(f0)))


# ------------------------------------------------------------------ recording

## Records `count` frames into <out>/<clip>/frame%08d.png; view(i) returns [yaw, pitch] per frame (null keeps the view), keys(i) the held keys.
func _record(clip: String, count: int, view: Callable, keys := Callable()) -> Dictionary:
	var dir := out_dir.path_join(clip)
	DirAccess.make_dir_recursive_absolute(dir)
	var t0 := Time.get_ticks_msec()
	var speeds: Array = []
	if keys.is_valid(): explorer.set("MoveSpeed", 12.0); explorer.set("SprintSpeed", 14.0) # old default walk speed; the shipped default is now 2 m/s walk, 7 sprint
	for i in count:
		var v = view.call(i)
		if v != null:
			explorer.call("SetPresentationView", float(v[0]), float(v[1]))
		if keys.is_valid():
			_key(KEY_W, bool(keys.call(i)))
		await RenderingServer.frame_post_draw
		var img := WORLD.grab(self)
		img.save_png(dir.path_join("frame%08d.png" % i))
		speeds.append(float(explorer.get("ActualTravelSpeedMetersPerSecond")))
	_release_keys()
	var max_speed := 0.0
	for s in speeds:
		max_speed = maxf(max_speed, float(s))
	return {"frames": count, "dir": dir, "record_wall_s": (Time.get_ticks_msec() - t0) * 0.001, "max_speed_mps": snappedf(max_speed, 0.01)}


func _still(name: String) -> Dictionary:
	await _frames(20)
	await RenderingServer.frame_post_draw
	var img := WORLD.grab(self)
	var path := out_dir.path_join("stills").path_join(name + ".png")
	DirAccess.make_dir_recursive_absolute(path.get_base_dir())
	img.save_png(path)
	return {"path": path, "stats": _luma_stats(img)}


## Mean luma, the share of near-white pixels (luma >= 0.95) and the brightest 1 % luma of an image (sunward calibration numbers).
func _luma_stats(img: Image) -> Dictionary:
	var data := img.get_data()
	var w := img.get_width()
	var h := img.get_height()
	var lumas := PackedFloat32Array()
	var sum := 0.0
	var white := 0
	var n := 0
	for y in range(0, h, 4):
		for x in range(0, w, 4):
			var i := (y * w + x) * 4
			var l := (0.2126 * data[i] + 0.7152 * data[i + 1] + 0.0722 * data[i + 2]) / 255.0
			lumas.append(l)
			sum += l
			n += 1
			if l >= 0.95:
				white += 1
	lumas.sort()
	return {"mean_luma": snappedf(sum / maxf(1.0, n), 0.0001), "white_share": snappedf(float(white) / maxf(1.0, n), 0.0001), "p99_luma": snappedf(lumas[int(0.99 * (lumas.size() - 1))], 0.0001)}


func _differing(a: Image, b: Image) -> int:
	var da := a.get_data()
	var db := b.get_data()
	if da.size() != db.size():
		return -1
	var n := 0
	for i in range(0, da.size(), 4):
		if da[i] != db[i] or da[i + 1] != db[i + 1] or da[i + 2] != db[i + 2]:
			n += 1
	return n


## On / off stills of the current view (fog forced with `drive` for "on").
func _ab_stills(clip: String, drive: Vector3, rec: Dictionary) -> void:
	await _fog_off()
	rec["still_off"] = await _still(clip + "_off")
	if not await _force(drive):
		rec["error"] = "fog did not come on for the stills"
		return
	rec["still_on"] = await _still(clip + "_on")


func _key(code: int, down: bool) -> void:
	if held.get(code, false) == down:
		return
	held[code] = down
	var e := InputEventKey.new()
	e.keycode = code
	e.physical_keycode = code
	e.pressed = down
	Input.parse_input_event(e)


func _release_keys() -> void:
	for code in held.keys():
		_key(code, false)


func _pan(yaw0: float, pitch: float, degrees: float, count: int) -> Callable:
	return func(i: int): return [yaw0 - 0.5 * degrees + degrees * float(i) / float(maxi(1, count - 1)), pitch]


# ------------------------------------------------------------------ clips

func _clip_noon() -> Dictionary:
	var p := _pose("plains_eye")
	if p.is_empty():
		return {"error": "no plains_eye pose"}
	var rec := await _go(float(p["around_m"]), float(p["across_m"]), {"mode": "noon"})
	if rec.has("error"):
		return rec
	explorer.call("SetPresentationView", float(p.get("yaw_deg", 0.0)), float(p.get("pitch_deg", -3.0)))
	rec["natural"] = await _natural()
	var history: Array = []
	for k in 6:
		await COMMON.settle(self, 10.0)
		history.append(_fog_state())
	rec["natural_history_10s"] = history
	WORLD.freeze_world(game)
	var frozen := WORLD.freeze_noise(self, game, PackedStringArray(["clouds", "taa"]))
	var imgs: Array = []
	for amount in [1.0, 0.0, 1.0]:
		nf.call("DebugSetAmount", amount)
		await COMMON.settle(self, 3.0)
		var cap: Dictionary = await WORLD.capture_begin(self, game)
		await RenderingServer.frame_post_draw
		imgs.append(WORLD.grab(self))
		WORLD.capture_end(game)
		rec["capture_amount_%d_%d" % [int(amount), imgs.size()]] = {"deterministic": cap.get("deterministic"), "fog": _fog_state()}
	var sd := out_dir.path_join("stills")
	DirAccess.make_dir_recursive_absolute(sd)
	(imgs[0] as Image).save_png(sd.path_join("noon_amount1.png"))
	(imgs[1] as Image).save_png(sd.path_join("noon_amount0.png"))
	rec["px_amount1_vs_0"] = _differing(imgs[0], imgs[1])
	rec["px_amount1_vs_1"] = _differing(imgs[0], imgs[2])
	root.use_taa = bool(frozen.get("use_taa", root.use_taa))
	nf.call("DebugSetAmount", 1.0)
	return rec


func _clip_dawn_floor() -> Dictionary:
	var p := _pose("mountain_valley_eye")
	if p.is_empty():
		return {"error": "no mountain_valley_eye pose"}
	var rec := await _go(float(p["around_m"]), float(p["across_m"]), {"mode": "elevation", "value": 7.0, "rising": true})
	if rec.has("error"):
		return rec
	var sun_yaw: float = await _yaw_to_sun()
	var yaw := sun_yaw + 60.0   # side-lit: the sun 60 deg to one side, the pan sweeps toward it
	explorer.call("SetPresentationView", yaw, 0.0)
	rec["natural"] = await _natural()
	var drive := Vector3(1.0, 0.6, 0.0)
	await _ab_stills("dawn_floor", drive, rec)
	rec["forced"] = _fog_state()
	rec.merge(await _record("dawn_floor", 300, _pan(yaw, 2.0, 70.0, 300)))
	return rec


## The highest ground 100 to 280 m from the valley floor pose; looks back down at the floor.
func _clip_dawn_ridge() -> Dictionary:
	var p := _pose("mountain_valley_eye")
	if p.is_empty():
		return {"error": "no mountain_valley_eye pose"}
	var a0 := float(p["around_m"])
	var z0 := float(p["across_m"])
	var floor_h := float(surface.call("SampleHeight", a0, z0))
	var best := [a0, z0, floor_h]
	for r in [100.0, 140.0, 180.0, 220.0, 260.0, 280.0]:
		for k in 24:
			var ang := TAU * float(k) / 24.0
			var a: float = a0 + cos(ang) * float(r)
			var z: float = z0 + sin(ang) * float(r)
			var hh := float(surface.call("SampleHeight", a, z))
			if hh > float(best[2]):
				best = [a, z, hh]
	var rec := await _go(float(best[0]), float(best[1]), {"mode": "elevation", "value": 7.0, "rising": true})
	if rec.has("error"):
		return rec
	var da := a0 - float(best[0])
	var dz := z0 - float(best[1])
	var dist := sqrt(da * da + dz * dz)
	rec["floor_ground"] = floor_h
	rec["ridge_rise_m"] = float(best[2]) - floor_h
	rec["floor_distance_m"] = dist
	var yaw := await _yaw_toward(da, dz)
	var pitch := clampf(-rad_to_deg(atan2(float(best[2]) - floor_h, maxf(1.0, dist))) * 0.8, -30.0, 0.0)
	explorer.call("SetPresentationView", yaw, pitch)
	rec["natural"] = await _natural()
	var drive := Vector3(1.0, 0.6, 0.0)
	await _ab_stills("dawn_ridge", drive, rec)
	rec["forced"] = _fog_state()
	rec.merge(await _record("dawn_ridge", 300, _pan(yaw, pitch, 50.0, 300)))
	return rec


func _clip_forest_rain() -> Dictionary:
	var p := _pose("forest_edge_eye")
	if p.is_empty():
		return {"error": "no forest_edge_eye pose"}
	var rec := await _go(float(p["around_m"]), float(p["across_m"]), {"mode": "elevation", "value": 12.0, "rising": true})
	if rec.has("error"):
		return rec
	if wet != null:
		wet.call("ForceUniform", 0.6, 1.0, 0.3, 0.0)   # after rain: soaked ground, film and puddles, rain stopped
	var yaw := await _yaw_to_sun()
	explorer.call("SetPresentationView", yaw, 4.0)
	rec["natural_after_rain"] = await _natural()
	var drive := Vector3(0.0, 1.0, 0.0)
	await _ab_stills("forest_rain_sunward", drive, rec)
	rec["forced"] = _fog_state()
	rec.merge(await _record("forest_rain", 300, _pan(yaw, 4.0, 50.0, 300)))
	return rec


func _clip_river() -> Dictionary:
	var swim: RefCounted = SWIM.new()
	var found: Dictionary = swim.call("FindShallowRiver", game, 0.8, 6.0)
	if not bool(found.get("ok", false)):
		found = swim.call("FindPoints", game)
	if not bool(found.get("ok", false)) or not found.has("river"):
		return {"error": "no river point: " + str(found.get("error", ""))}
	var r: Dictionary = found["river"]
	var level := float(r["level"])
	# Stand on the dry bank next to the reach, 2 m above the higher of the bank and the water, looking over the water toward the centreline.
	var ba := float(r["dry_around"])
	var bz := float(r["dry_across"])
	var bank_ground := float(surface.call("SampleHeight", ba, bz))
	var rec := await _go(ba, bz, {"mode": "elevation", "value": 6.0, "rising": true}, 1.7)
	if rec.has("error"):
		return rec
	rec["river"] = {"around": r["around"], "across": r["across"], "level": level, "bed": r["bed"], "width": r.get("width", 0.0), "bank_ground": bank_ground,
		"bank_distance": r["bank_distance"], "sampled_ground_at_centre": float(surface.call("SampleHeight", float(r["around"]), float(r["across"])))}
	var yaw := await _yaw_toward(float(r["around"]) - ba, float(r["across"]) - bz)
	explorer.call("SetPresentationView", yaw, -6.0)
	rec["natural"] = await _natural()
	var under := float(rec["camera_vertical"]) < level
	rec["camera_below_water_level"] = under
	var drive := Vector3(0.0, 0.3, 1.0)
	await _ab_stills("river_steam", drive, rec)
	rec["forced"] = _fog_state()
	rec.merge(await _record("river_steam", 300, _pan(yaw, -6.0, 60.0, 300)))
	return rec


func _clip_night() -> Dictionary:
	var p := _pose("mountain_valley_eye")
	if p.is_empty():
		return {"error": "no mountain_valley_eye pose"}
	var rec := await _go(float(p["around_m"]), float(p["across_m"]), {"mode": "night_planet"})
	if rec.has("error"):
		return rec
	var yaw := float(p.get("yaw_deg", 0.0))
	explorer.call("SetPresentationView", yaw, 2.0)
	rec["natural"] = await _natural()
	var drive := Vector3(1.0, 0.6, 0.0)
	await _ab_stills("night", drive, rec)
	rec["forced"] = _fog_state()
	rec.merge(await _record("night", 300, _pan(yaw, 2.0, 70.0, 300)))
	return rec


## Walk 4 s, stop, then two 180 deg turns at 360 deg/s with holds (fog on, or amount 0 as the flicker reference). 270 frames.
func _clip_walk(fog_on: bool) -> Dictionary:
	var p := _pose("forest_clearing_eye")
	if p.is_empty():
		return {"error": "no forest_clearing_eye pose"}
	var rec := await _go(float(p["around_m"]), float(p["across_m"]), {"mode": "elevation", "value": 10.0, "rising": true})
	if rec.has("error"):
		return rec
	var yaw0 := float(p.get("yaw_deg", 0.0))
	explorer.call("SetPresentationView", yaw0, 0.0)
	rec["natural"] = await _natural()
	if fog_on:
		if not await _force(Vector3(1.0, 0.6, 0.0)):
			return {"error": "fog did not come on"}
	else:
		await _fog_off()
	rec["fog"] = _fog_state()
	var name := "walk_on" if fog_on else "walk_off"
	# 0..119 walk, 120..134 stop, 135..149 turn +180 (12 deg / frame = 360 deg/s), 150..194 hold, 195..209 turn back, 210..269 hold.
	var view: Callable = func(i: int):
		var yaw := yaw0
		if i >= 135 and i < 150:
			yaw = yaw0 + 12.0 * float(i - 134)
		elif i >= 150 and i < 195:
			yaw = yaw0 + 180.0
		elif i >= 195 and i < 210:
			yaw = yaw0 + 180.0 - 12.0 * float(i - 194)
		return [yaw, 0.0]
	var keys: Callable = func(i: int): return i < 120
	rec.merge(await _record(name, 270, view, keys))
	rec["segments"] = {"walk": [0, 119], "stop": [120, 134], "turn_out": [135, 149], "hold_out": [150, 194], "turn_back": [195, 209], "hold": [210, 269]}
	return rec


## The natural state at several ground poses, dawn and night: steam alone (no water texels) must not switch the fog on.
func _clip_dawn_survey() -> Dictionary:
	var rec := {}
	for name in ["plains_eye", "mountain_eye", "mountain_valley_eye", "forest_clearing_eye", "hill_forest_valley_eye", "coast_eye"]:
		var p := _pose(name)
		if p.is_empty():
			continue
		for time in [{"mode": "elevation", "value": 7.0, "rising": true}, {"mode": "night_planet"}]:
			var at := await _go(float(p["around_m"]), float(p["across_m"]), time)
			if at.has("error"):
				rec["%s_%s" % [name, time["mode"]]] = at
				continue
			var st := await _natural()
			await COMMON.settle(self, 3.0)   # the water field (worker) and the 2 s fade
			st = _fog_state()
			var d: Array = st["drive"]
			st["steam_only"] = float(d[2]) > 0.0 and 0.01 * float(d[0]) < 1e-4 and 0.002 * float(d[1]) < 1e-4
			st["sun_elevation_deg"] = at.get("sun_elevation_deg")
			rec["%s_%s" % [name, time["mode"]]] = st
			_log("survey %s %s: %s" % [name, time["mode"], JSON.stringify(st)])
	return rec
