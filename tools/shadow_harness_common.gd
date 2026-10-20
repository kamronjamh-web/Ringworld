extends RefCounted

## Shared helpers for the shadow revamp S0 tools (tools/shadow_attribution_test.gd, tools/shadow_quality_capture.gd).
## Test-only: boots an isolated gameplay world (stub WorldSession, nothing written to user:// settings), identifies the machine, runs the nvidia-smi
## power log and its machine-aware validity rule, and holds the shared pose / time-of-day / label helpers. No production code depends on this file.
##
## MACHINE RULE. The authoritative baseline for docs/shadow-revamp-plan.md is the RTX A1000 laptop at 35 W. Every output produced on another GPU
## is stamped `machine: <tag> (non-authoritative)`. Power-log validity: A1000 windows must be P0 / enforced limit 35 W / memory clock ~5500 MHz
## (5300..5700) for every sample, otherwise the window is `invalid`. On any other GPU the same fields are recorded and summarized but windows are
## never rejected (`valid` stays true, `authoritative` is false).

const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const FIXTURE_AROUND := 16443476.0
const FIXTURE_ACROSS := 38692.0
const PHASE_DARKNESS := 2
const FIXED_CLOUD_TIME := 1234.5
const A1000_POWER_LIMIT_W := 35.0
const A1000_MEM_MHZ_MIN := 5300.0
const A1000_MEM_MHZ_MAX := 5700.0
const POWER_QUERY := "timestamp,pstate,clocks.gr,clocks.mem,power.draw,enforced.power.limit,temperature.gpu,clocks_event_reasons.active"


class SessionStub extends Node:
	var settings := {}
	func get_active_world_settings() -> Dictionary: return settings.duplicate(true)
	func save_active_world_settings(next: Dictionary) -> void: settings = next.duplicate(true)
	func consume_load_selected_world_request() -> bool: return false


# ------------------------------------------------------------------ args

static func arg_value(prefix: String, fallback: String) -> String:
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with(prefix):
			return arg.trim_prefix(prefix)
	return fallback


static func has_flag(flag: String) -> bool:
	return flag in OS.get_cmdline_user_args()


static func csv_arg(prefix: String) -> PackedStringArray:
	return arg_value(prefix, "").split(",", false)


# ------------------------------------------------------------------ machine

## Adapter, nvidia-smi snapshot and the authoritative flag. `tag` is a folder-safe machine name ("a1000", "rtx4070", ...).
static func machine_info() -> Dictionary:
	var adapter := RenderingServer.get_video_adapter_name()
	var smi: Array = []
	var exit_code := OS.execute("nvidia-smi", ["--query-gpu=name,pstate,clocks.gr,clocks.mem,power.draw,enforced.power.limit,driver_version", "--format=csv,noheader"], smi)
	var authoritative := adapter.to_upper().contains("A1000")
	var tag := ""
	if authoritative:
		tag = "a1000"
	else:
		tag = adapter.to_lower().replace("nvidia", "").replace("geforce", "")
		var clean := ""
		for c in tag:
			if (c >= "a" and c <= "z") or (c >= "0" and c <= "9"):
				clean += c
		tag = clean if clean != "" else "unknown"
	return {"adapter": adapter, "tag": tag, "authoritative": authoritative,
		"label": "machine: %s (%s)" % [tag, "authoritative" if authoritative else "non-authoritative"],
		"nvidia_smi": smi, "nvidia_smi_exit": exit_code, "renderer": RenderingServer.get_current_rendering_method(),
		"os": OS.get_name(), "godot": Engine.get_version_info().get("string", "")}


## Absolute output directory: --out=<dir> or <project>/.godot/shadow_baseline/<machine tag> (never committed).
static func output_dir(info: Dictionary, sub: String) -> String:
	var base := arg_value("--out=", "")
	if base == "":
		base = ProjectSettings.globalize_path("res://.godot/shadow_baseline").path_join(str(info["tag"]))
	var path := base.path_join(sub)
	DirAccess.make_dir_recursive_absolute(path)
	return path


# ------------------------------------------------------------------ power log

## Starts `nvidia-smi ... -lms 500` redirected to a file (the -f flag buffers until exit, shell redirection does not). Returns the cmd.exe pid, or -1.
static func start_power_log(path: String) -> int:
	if OS.get_name() != "Windows":
		return OS.create_process("sh", ["-c", "nvidia-smi --query-gpu=%s --format=csv -lms 500 > \"%s\"" % [POWER_QUERY, path]])
	return OS.create_process("cmd.exe", ["/c", "nvidia-smi --query-gpu=%s --format=csv -lms 500 > \"%s\"" % [POWER_QUERY, path]])


static func stop_power_log(pid: int) -> void:
	if pid <= 0:
		return
	if OS.get_name() == "Windows":
		OS.execute("taskkill", ["/F", "/T", "/PID", str(pid)], [])
	else:
		OS.kill(pid)


## Offset (seconds) between nvidia-smi's local-time stamps and Time.get_unix_time_from_system() (UTC).
static func local_offset_seconds() -> float:
	var local := Time.get_unix_time_from_datetime_dict(Time.get_datetime_dict_from_system(false))
	var utc := Time.get_unix_time_from_datetime_dict(Time.get_datetime_dict_from_system(true))
	return float(local - utc)


static func _number(text: String) -> float:
	var clean := ""
	for c in text.strip_edges():
		if (c >= "0" and c <= "9") or c == "." or c == "-":
			clean += c
	return float(clean) if clean != "" else NAN


## Rows {t (local-time unix seconds incl. ms), pstate, gr, mem, power, limit, temp, reasons}.
static func parse_power_log(path: String) -> Array:
	var rows: Array = []
	if not FileAccess.file_exists(path):
		return rows
	var lines := FileAccess.get_file_as_string(path).split("\n", false)
	for i in range(1, lines.size()):
		var parts := lines[i].strip_edges().split(",")
		if parts.size() < 8:
			continue
		var stamp := parts[0].strip_edges()  # 2026/10/19 20:16:30.795
		var dt := stamp.split(" ")
		if dt.size() < 2:
			continue
		var d := dt[0].split("/")
		var t := dt[1].split(":")
		if d.size() < 3 or t.size() < 3:
			continue
		var sec := float(t[2])
		var unix := Time.get_unix_time_from_datetime_dict({"year": int(d[0]), "month": int(d[1]), "day": int(d[2]), "hour": int(t[0]), "minute": int(t[1]), "second": int(sec)})
		rows.append({"t": float(unix) + (sec - floorf(sec)), "pstate": parts[1].strip_edges(), "gr": _number(parts[2]), "mem": _number(parts[3]),
			"power": _number(parts[4]), "limit": _number(parts[5]), "temp": _number(parts[6]), "reasons": parts[7].strip_edges()})
	return rows


## Power summary and the machine-aware validity verdict for one window. t0/t1 are Time.get_unix_time_from_system() (UTC) values.
static func power_window(rows: Array, t0_utc: float, t1_utc: float, info: Dictionary, offset: float) -> Dictionary:
	var a := t0_utc + offset
	var b := t1_utc + offset
	var power: Array[float] = []
	var gr: Array[float] = []
	var mem: Array[float] = []
	var limits: Array[float] = []
	var states := {}
	var reasons := {}
	var bad := 0
	var n := 0
	for r in rows:
		var t: float = r["t"]
		if t < a or t > b:
			continue
		n += 1
		power.append(float(r["power"])); gr.append(float(r["gr"])); mem.append(float(r["mem"])); limits.append(float(r["limit"]))
		states[r["pstate"]] = int(states.get(r["pstate"], 0)) + 1
		reasons[r["reasons"]] = int(reasons.get(r["reasons"], 0)) + 1
		var ok: bool = r["pstate"] == "P0" and absf(float(r["limit"]) - A1000_POWER_LIMIT_W) <= 0.6 and float(r["mem"]) >= A1000_MEM_MHZ_MIN and float(r["mem"]) <= A1000_MEM_MHZ_MAX
		if not ok:
			bad += 1
	var authoritative: bool = info["authoritative"]
	var result := {"samples": n, "pstates": states, "event_reasons": reasons, "authoritative": authoritative,
		"power_w": range_stats(power), "gr_mhz": range_stats(gr), "mem_mhz": range_stats(mem), "enforced_limit_w": range_stats(limits),
		"samples_off_rule": bad, "rule": "A1000: P0 / 35 W limit / mem 5300..5700 MHz; other GPUs: recorded, never rejected"}
	if authoritative:
		result["valid"] = n >= 2 and bad == 0
		result["verdict"] = "valid" if result["valid"] else ("invalid: no power samples in window" if n < 2 else "invalid: %d of %d samples off P0/35W/5500MHz" % [bad, n])
	else:
		result["valid"] = true
		result["verdict"] = "recorded, non-authoritative (%s)" % info["label"] + ("" if n >= 2 else "; no power samples in window")
	return result


# ------------------------------------------------------------------ stats

static func percentile(sorted_values: Array, q: float) -> float:
	if sorted_values.is_empty():
		return NAN
	var pos := clampf(q, 0.0, 1.0) * float(sorted_values.size() - 1)
	var lo := int(floor(pos))
	var hi := mini(lo + 1, sorted_values.size() - 1)
	return lerpf(float(sorted_values[lo]), float(sorted_values[hi]), pos - float(lo))


## {n, median, q1, q3, iqr, mean, min, max} (robust window statistics).
static func stats(values) -> Dictionary:
	var sorted: Array = []
	for v in values:
		if not is_nan(float(v)):
			sorted.append(float(v))
	if sorted.is_empty():
		return {"n": 0}
	sorted.sort()
	var total := 0.0
	for v in sorted:
		total += float(v)
	var q1 := percentile(sorted, 0.25)
	var q3 := percentile(sorted, 0.75)
	return {"n": sorted.size(), "median": percentile(sorted, 0.5), "q1": q1, "q3": q3, "iqr": q3 - q1, "mean": total / sorted.size(), "min": sorted[0], "max": sorted[-1]}


static func range_stats(values: Array) -> Dictionary:
	if values.is_empty():
		return {"n": 0}
	var s := stats(values)
	return {"n": s["n"], "min": s["min"], "median": s["median"], "max": s["max"]}


# ------------------------------------------------------------------ boot

## Boots the isolated gameplay world like terrain_render_attribution_test / eclipse_hub_capture. Returns null (after push_error) on failure.
static func create_game(tree: SceneTree, fixture_root: String) -> Node:
	var root := tree.root
	var prior := root.get_node_or_null("WorldSession")
	if prior != null:
		prior.name = "WorldSessionOriginalShadowHarness"
	var session := SessionStub.new()
	session.name = "WorldSession"
	root.add_child(session)
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture_root)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	ProjectSettings.set_setting("ringworld/runtime/surface_landforms_v7", false)
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture_root))
	var launcher := LAUNCHER.instantiate()
	root.add_child(launcher)
	await tree.process_frame
	launcher.call("_on_certified_preset_pressed")
	var selector: OptionButton = launcher.get("terrain_backend_selector")
	if selector == null:
		push_error("launcher terrain backend selector missing")
		return null
	selector.select(2)
	selector.emit_signal("item_selected", selector.selected)
	if not bool(launcher.call("_prepare_runtime_surface_generation", true)):
		push_error("runtime-v2 generation preparation failed")
		return null
	launcher.call("_save_selected_slot_settings")
	var game := GAME.instantiate()
	launcher.call("_configure_game", game, 5000000.0)
	_install_isolated_preferences(game, fixture_root)
	var mon: Node = game.get_node_or_null("RingPerformanceMonitor")
	if mon != null:
		mon.call("InstallSettingsPathForTests", fixture_root.path_join("telemetry.tres"))
		var reports := fixture_root.path_join("reports")
		DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(reports))
		mon.call("InstallReportDirectoryForTests", reports)
	root.add_child(game)
	launcher.queue_free()
	var deadline := Time.get_ticks_msec() + 600000
	while Time.get_ticks_msec() < deadline:
		await tree.process_frame
		var candidate := game.get_node_or_null("Terrain3DGameplay")
		if candidate != null and bool(candidate.call("EnsureInitialized")):
			return game
	push_error("gameplay startup did not become ready")
	return null


## Copies the user's live preferences into the isolated fixture (so the run measures the real config) without ever writing back to user://.
static func _install_isolated_preferences(game: Node, fixture_root: String) -> void:
	var drawer: Node = game.get_node_or_null("LiveTuningDrawer")
	if drawer == null:
		return
	for item in [["InstallGrassDefaults", "gameplay_grass_defaults.tres"], ["InstallVegetationDefaults", "gameplay_vegetation_defaults.tres"], ["InstallWaterDefaults", "gameplay_water_defaults.tres"], ["InstallAntiAliasingDefaults", "gameplay_anti_aliasing_defaults.tres"], ["InstallShadowDefaults", "gameplay_shadow_defaults.tres"], ["InstallSurfaceTerrainDefaults", "surface_tuning.tres"], ["InstallAtmosphereDefaults", "atmosphere_defaults.tres"]]:
		var destination := fixture_root.path_join(str(item[1]))
		var source := "user://" + str(item[1])
		if FileAccess.file_exists(source):
			var copy := FileAccess.open(destination, FileAccess.WRITE)
			if copy != null:
				copy.store_string(FileAccess.get_file_as_string(source))
				copy.close()
		drawer.call(str(item[0]), destination)
	if drawer.has_method("InstallEmitterSiteDefaults"):
		drawer.call("InstallEmitterSiteDefaults", fixture_root.path_join("emitter_tuning.tres"))
	if drawer.has_method("InstallEclipseDefaults"):
		drawer.call("InstallEclipseDefaults", fixture_root.path_join("eclipse_tuning.tres"))


## True when no terrain / grass / tree / global-LOD build is in flight (residency is not required: altitude poses have no grass).
static func builds_idle(game: Node) -> bool:
	var surface := game.get_node_or_null("Terrain3DGameplay")
	var grass := game.get_node_or_null("StandingGrass")
	var trees := game.get_node_or_null("BiomeDecorations")
	var global_lod := game.get_node_or_null("GlobalTerrainLod3")
	if surface != null:
		var diagnostics: Dictionary = surface.call("GetSurfaceDiagnostics")
		var streaming: Dictionary = diagnostics.get("streaming", {})
		if int(streaming.get("pending_jobs", 0)) != 0 or int(streaming.get("active_jobs", 0)) != 0 or int(streaming.get("packing", 0)) != 0:
			return false
	if grass != null and (int(grass.get("PendingGrassTileBuildCount")) != 0 or int(grass.get("PendingGrassUploadCount")) != 0):
		return false
	if trees != null and int(trees.get("VegetationPendingTiles")) != 0:
		return false
	if global_lod != null and int(global_lod.get("PendingInitialSegmentCount")) != 0:
		return false
	return true


## Waits for `stable_frames` consecutive idle frames (or the timeout). Returns whether idle was reached.
static func wait_builds_idle(tree: SceneTree, game: Node, timeout_s: float, stable_frames := 30) -> bool:
	var stable := 0
	var deadline := Time.get_ticks_msec() + int(timeout_s * 1000.0)
	while Time.get_ticks_msec() < deadline:
		await tree.process_frame
		stable = stable + 1 if builds_idle(game) else 0
		if stable >= stable_frames:
			return true
	return false


static func settle(tree: SceneTree, seconds: float) -> void:
	var end := Time.get_ticks_msec() + int(seconds * 1000.0)
	while Time.get_ticks_msec() < end:
		await tree.process_frame


# ------------------------------------------------------------------ poses / time of day

## Pose table shared by both tools. kind: ground | altitude | emitter. `time` selects the celestial state (see apply_time).
## coast and cave_mouth have no fixture yet (TODO in the tools' README).
const POSES := {
	"ground_horizon": {"kind": "ground", "pitch": 0.0, "lift": 2.0, "time": {"mode": "fraction", "value": 0.25}},
	"forest_noon": {"kind": "ground", "pitch": -12.0, "lift": 2.0, "time": {"mode": "noon"}},
	"emitter_rim": {"kind": "emitter", "viewpoint": 0, "time": {"mode": "noon"}},
	"emitter_bore_noon": {"kind": "emitter", "viewpoint": 1, "time": {"mode": "noon"}},
	"dawn_3deg": {"kind": "ground", "pitch": 0.0, "lift": 2.0, "time": {"mode": "elevation", "value": 3.0, "rising": true}},
	"dawn_5deg": {"kind": "ground", "pitch": 0.0, "lift": 2.0, "time": {"mode": "elevation", "value": 5.0, "rising": true}},
	"dawn_8deg": {"kind": "ground", "pitch": 0.0, "lift": 2.0, "time": {"mode": "elevation", "value": 8.0, "rising": true}},
	"dawn_15deg": {"kind": "ground", "pitch": 0.0, "lift": 2.0, "time": {"mode": "elevation", "value": 15.0, "rising": true}},
	"dusk_3deg": {"kind": "ground", "pitch": 0.0, "lift": 2.0, "time": {"mode": "elevation", "value": 3.0, "rising": false}},
	"dusk_8deg": {"kind": "ground", "pitch": 0.0, "lift": 2.0, "time": {"mode": "elevation", "value": 8.0, "rising": false}},
	"dusk_15deg": {"kind": "ground", "pitch": 0.0, "lift": 2.0, "time": {"mode": "elevation", "value": 15.0, "rising": false}},
	"dusk_m10": {"kind": "ground", "pitch": 0.0, "lift": 2.0, "time": {"mode": "elevation", "value": -10.0, "rising": false}},
	"dusk_m13": {"kind": "ground", "pitch": 0.0, "lift": 2.0, "time": {"mode": "elevation", "value": -13.0, "rising": false}},
	"dusk_m14": {"kind": "ground", "pitch": 0.0, "lift": 2.0, "time": {"mode": "elevation", "value": -14.0, "rising": false}},
	"alt_1km": {"kind": "altitude", "pitch": -25.0, "lift": 1000.0, "time": {"mode": "noon"}},
	"alt_12km": {"kind": "altitude", "pitch": -25.0, "lift": 12000.0, "time": {"mode": "noon"}},
	"night_planet_lit": {"kind": "ground", "pitch": -5.0, "lift": 2.0, "time": {"mode": "night_planet"}},
	"night_planet_low": {"kind": "ground", "pitch": -5.0, "lift": 2.0, "time": {"mode": "night_planet", "giant_min": 7.0, "giant_max": 14.0}},
	"twilight_overlap": {"kind": "ground", "pitch": 0.0, "lift": 2.0, "time": {"mode": "twilight_overlap"}},
	"eclipse_totality": {"kind": "ground", "pitch": 0.0, "lift": 2.0, "time": {"mode": "eclipse", "hub": 0.0}},
	"eclipse_hub": {"kind": "ground", "pitch": 0.0, "lift": 2.0, "time": {"mode": "eclipse", "hub": 1.0}},
}
const ATTRIBUTION_POSES := ["ground_horizon", "forest_noon", "emitter_rim", "dawn_5deg", "alt_1km", "alt_12km", "night_planet_lit", "eclipse_hub"]
const TODO_POSES := ["coast (no coast fixture/teleport yet)", "cave_mouth / overhang (no fixture yet)", "distant hill bands 2/5/10 km (measured as depth bands of the ground poses, no dedicated hill fixture)"]


static func hold_world_clocks(game: Node) -> void:
	var cycle := game.get_node_or_null("RingDayNightCycle")
	if cycle != null:
		cycle.set("Paused", true)
	var weather := game.get_node_or_null("RingVolumetricClouds")
	if weather != null:
		weather.set("CloudSimulationPaused", true)
		weather.set("CloudSimulationTimeSeconds", FIXED_CLOUD_TIME)


static func _sun_elevation(cycle: Node, fraction: float) -> float:
	cycle.call("SetCycleFraction", fraction)
	return float(cycle.get("SunElevationDegrees"))


## Sets the (paused) celestial state of a pose at the explorer's current location. Returns a description (sun elevation etc.) or {"error": ...}.
static func apply_time(game: Node, time: Dictionary) -> Dictionary:
	var cycle := game.get_node_or_null("RingDayNightCycle")
	var director := game.get_node_or_null("EclipseEvent")
	if cycle == null:
		return {"error": "no RingDayNightCycle"}
	cycle.set("Paused", true)
	if director != null and director.has_method("SetHubLevelOverride"):
		director.call("SetHubLevelOverride", -1.0)
	var mode := str(time.get("mode", "fraction"))
	var result := {"mode": mode}
	match mode:
		"fraction":
			cycle.call("SetCycleFraction", float(time["value"]))
		"noon":
			var best_f := 0.0
			var best_e := -1000.0
			for i in 240:
				var e := _sun_elevation(cycle, float(i) / 240.0)
				if e > best_e:
					best_e = e; best_f = float(i) / 240.0
			for k in range(-12, 13):
				var f := best_f + float(k) / 2400.0
				var e2 := _sun_elevation(cycle, f)
				if e2 > best_e:
					best_e = e2; best_f = f
			cycle.call("SetCycleFraction", best_f)
		"elevation":
			var target := float(time["value"])
			var rising: bool = time["rising"]
			var found := -1.0
			var step := 1.0 / 480.0
			var prev := _sun_elevation(cycle, 0.0)
			for i in range(1, 481):
				var f := float(i) * step
				var e := _sun_elevation(cycle, f)
				var crossing := (prev < target and e >= target) if rising else (prev > target and e <= target)
				if crossing:
					var lo := f - step
					var hi := f
					for _j in 14:
						var mid := 0.5 * (lo + hi)
						var em := _sun_elevation(cycle, mid)
						if (em < target) == rising:
							lo = mid
						else:
							hi = mid
					found = 0.5 * (lo + hi)
					break
				prev = e
			if found < 0.0:
				return {"error": "no sun elevation crossing %.1f deg (rising=%s) at this location" % [target, str(rising)]}
			cycle.call("SetCycleFraction", found)
		"night_planet":
			# Walk simulation time in 450 s steps until the sun is well below the horizon and the gas giant is high and not eclipsed.
			var t := float(cycle.call("GetSimulationSeconds"))
			var matched := false
			var giant_min := float(time.get("giant_min", 20.0))
			var giant_max := float(time.get("giant_max", 1000.0))
			var step_s := 450.0 if giant_max > 100.0 else 60.0 # a low-planet window is narrow: walk it in finer steps
			for i in (1200 if giant_max > 100.0 else 9000):
				t += step_s
				cycle.call("SetSimulationSeconds", t)
				var giant := float(cycle.get("GiantElevationDegrees"))
				if float(cycle.get("SunElevationDegrees")) < -25.0 and giant > giant_min and giant < giant_max and float(cycle.get("RawEclipseCoverage")) <= 0.0:
					matched = true
					break
			if not matched:
				return {"error": "no planet-lit night (giant %.0f..%.0f deg) found within %d x %.0f s" % [giant_min, giant_max, 1200 if giant_max > 100.0 else 9000, step_s]}
		"twilight_overlap":
			# Sun just below the horizon (shadow handoff still on) while the gas giant is up and lit: both directional lights cast shadows.
			var tw := float(cycle.call("GetSimulationSeconds"))
			var overlap := false
			for i in 9000:
				tw += 30.0
				cycle.call("SetSimulationSeconds", tw)
				var sun_elevation := float(cycle.get("SunElevationDegrees"))
				if sun_elevation < -5.0 and sun_elevation > -13.0 and float(cycle.get("GiantElevationDegrees")) > 20.0 and float(cycle.get("RawEclipseCoverage")) <= 0.0:
					overlap = true
					break
			if not overlap:
				return {"error": "no twilight overlap (sun -13..-5 deg, giant > 20 deg) found within 9000 x 30 s"}
		"eclipse":
			if director == null or not director.has_method("JumpToPhase"):
				return {"error": "no EclipseEvent director"}
			configure_eclipse(director, {"EclipseDarknessEnabled": true})
			director.call("JumpToPhase", PHASE_DARKNESS)
			var hub := float(time.get("hub", 0.0))
			director.call("SetHubLevelOverride", hub if hub > 0.0 else -1.0)
	result["sun_elevation_deg"] = float(cycle.get("SunElevationDegrees"))
	result["giant_elevation_deg"] = float(cycle.get("GiantElevationDegrees"))
	result["daylight_factor"] = float(cycle.get("DaylightFactor"))
	result["cycle_fraction"] = float(cycle.call("GetCycleTimeSeconds")) / maxf(1.0, float(cycle.get("FullCycleSeconds")))
	result["simulation_seconds"] = float(cycle.call("GetSimulationSeconds"))
	result["eclipse_coverage"] = float(cycle.get("RawEclipseCoverage"))
	return result


static func configure_eclipse(director: Node, extra: Dictionary) -> void:
	var script: Script = load("res://scripts/ui/GameplayEclipseEventSettings.cs")
	var gameplay: Resource = script.new()
	for key in extra:
		gameplay.set(key, extra[key])
	director.call("Configure", gameplay)


## Current player pose, for the relative poses above.
static func pose_now(game: Node) -> Dictionary:
	var surface := game.get_node_or_null("Terrain3DGameplay")
	var explorer := game.get_node_or_null("Explorer")
	var diag: Dictionary = surface.call("GetSurfaceDiagnostics") if surface != null else {}
	return {"around": float(diag.get("player_around", 0.0)), "vertical": float(diag.get("player_height", 0.0)),
		"across": float(diag.get("player_across", 0.0)), "yaw": rad_to_deg(float(explorer.get("SavedYaw"))),
		"pitch": float(explorer.get("SavedPitch")), "flight": bool(explorer.get("NoclipEnabled"))}


## Moves to a pose (flight so gravity cannot drift it), waits for streaming to go idle, applies the pose's time of day. Returns a description dict
## with "error" set when the pose is unavailable (callers skip it and list it as TODO).
static func go_pose(tree: SceneTree, game: Node, base: Dictionary, name: String, settle_s: float, idle_timeout_s: float) -> Dictionary:
	var pose: Dictionary = POSES[name]
	var explorer := game.get_node_or_null("Explorer")
	var drawer := game.get_node_or_null("LiveTuningDrawer")
	var info := {"pose": name}
	if str(pose["kind"]) == "emitter":
		var site := int(drawer.call("FindEmitterSite", "main")) if drawer != null else -1
		if site < 0 or not bool(drawer.call("RequestEmitterTeleport", site, int(pose["viewpoint"]))):
			info["error"] = "emitter teleport refused (site %d)" % site
			return info
		var start := Time.get_ticks_msec()
		while Time.get_ticks_msec() - start < 180000:
			await tree.physics_frame
			var state := str(drawer.get("EmitterTeleportState"))
			if state == "Arrived":
				break
			if state == "Failed" or state == "TimedOut":
				info["error"] = "emitter teleport %s: %s" % [state, str(drawer.get("EmitterTeleportMessage"))]
				return info
		var landing := 0
		while landing < 180 and not bool(explorer.get("NoclipEnabled")) and not bool(explorer.call("is_on_floor")):
			landing += 1
			await tree.physics_frame
	else:
		explorer.call("TeleportToCanonicalMeters", float(base["around"]), float(base["vertical"]) + float(pose["lift"]), float(base["across"]), float(base["yaw"]), float(pose["pitch"]), true)
		await settle(tree, 0.2)
	info["streaming_idle"] = await wait_builds_idle(tree, game, idle_timeout_s)
	info["time"] = apply_time(game, pose["time"])
	if info["time"].has("error"):
		info["error"] = str(info["time"]["error"])
		return info
	await settle(tree, settle_s)
	info["streaming_idle_after"] = builds_idle(game)
	return info
