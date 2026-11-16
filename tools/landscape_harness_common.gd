extends RefCounted

## Shared GPU boot and capture module of the landscape (T0) and vastness (V0) tools (docs/landscape-audit-t0-spec.md 2.6). TEST ONLY.
## Reuses tools/shadow_harness_common.gd (COMMON) for machine info, power log, stats, time of day and streaming-idle helpers, but has its own boot: it pins the
## selected world (pinned v8 by default, verified against LandscapeWorldBridge), installs the user's REAL preference files under their real names, and records which ones
## it used. Never calls or modifies COMMON.create_game, never writes to user:// settings.
## Flags understood here (after `--`):  --prefs=user|defaults|<dir>   --preset=medium|keep   --allow-blades   --res=WxH   --weather=clear|natural
## World selection: --generation=N [--review-features=a,b]. Generation 0 selects the pinned v8 world.
## Output layout: see docs/landscape-audit-t0-spec.md 2.7 (transient_dir / baseline_dir).

const COMMON := preload("res://tools/shadow_harness_common.gd")
const GENERATION_PIN := preload("res://tools/world_generation_pin.gd")
const BRIDGE := preload("res://tools/LandscapeWorldBridge.cs")
const BANDS := preload("res://tools/landscape_audit_bands.gd")
const LOADING := preload("res://scripts/Loading/ring_world_loading.gd")
const DEPTH_EFFECT := preload("res://tools/shadow_depth_capture_effect.gd")
const POSE_FIXTURE := "res://tools/fixtures/landscape_poses_v8_s1337.json"
## Drawer Install*Defaults method -> the file name of the matching GameplayXDefaults.UserPath / AtmosphereVisualDefaults.UserPath constant.
const PREF_FILES := [
	["InstallGrassDefaults", "gameplay_grass_defaults.tres"],
	["InstallVegetationDefaults", "gameplay_vegetation_defaults.tres"],
	["InstallWaterDefaults", "gameplay_water_defaults.tres"],
	["InstallAntiAliasingDefaults", "gameplay_anti_aliasing_defaults.tres"],
	["InstallShadowDefaults", "gameplay_shadow_defaults.tres"],
	["InstallSurfaceTerrainDefaults", "gameplay_surface_terrain_defaults.tres"],
	["InstallAtmosphereDefaults", "atmosphere_visual_defaults.tres"],
	["InstallRainHazeDefaults", "gameplay_rain_haze_defaults.tres"],
	["InstallWetWorldDefaults", "gameplay_wet_world_defaults.tres"],
	["InstallWindDefaults", "gameplay_wind_defaults.tres"],
]
const MEDIUM_PRESET := 1 # RingTerrainLodCoordinator.QualityPreset.Medium
const CLEAR_PRECIPITATION_MAX := 0.02
const MAJOR_FRONT := 3 # WeatherSystemKind in scripts/RingVolumetricCloudLayer.cs:32

static var _fixture := {}
static var _stamp := ""


# ------------------------------------------------------------------ output folders

static func stamp() -> String:
	if _stamp == "":
		_stamp = Time.get_datetime_string_from_system(false).replace(":", "-").replace("T", "_")
	return _stamp


static func _make(path: String) -> String:
	DirAccess.make_dir_recursive_absolute(path)
	return path


## <project>/.godot/captures/landscape/t0/<kind>/<stamp> (transient, pruned by the normal policy).
static func transient_dir(kind: String, world_tag := "") -> String:
	return _make(ProjectSettings.globalize_path("res://.godot/captures/landscape/t0").path_join(kind).path_join(_world_stamp(world_tag)))


## <project>/.godot/audit_baseline/<machine>/<phase>/<kind>/<stamp> (durable; machine is "common" for machine-independent headless output).
static func baseline_dir(machine: String, phase: String, kind: String, world_tag := "") -> String:
	return _make(ProjectSettings.globalize_path("res://.godot/audit_baseline").path_join(machine).path_join(phase).path_join(kind).path_join(_world_stamp(world_tag)))


static func _world_stamp(world_tag: String) -> String:
	return stamp() if world_tag.is_empty() else stamp() + "_" + world_tag


static func remove_dir_recursive(absolute_path: String) -> void:
	var dir := DirAccess.open(absolute_path)
	if dir == null:
		return
	for sub in dir.get_directories():
		remove_dir_recursive(absolute_path.path_join(sub))
	for file in dir.get_files():
		DirAccess.remove_absolute(absolute_path.path_join(file))
	DirAccess.remove_absolute(absolute_path)


# ------------------------------------------------------------------ pose fixture

## The committed pose fixture (tools/fixtures/landscape_poses_v8_s1337.json); {} when missing.
static func load_poses() -> Dictionary:
	if _fixture.is_empty():
		var text := FileAccess.get_file_as_string(POSE_FIXTURE)
		var parsed: Variant = JSON.parse_string(text)
		if parsed is Dictionary:
			_fixture = parsed
	return _fixture


static func pose_by_name(name: String) -> Dictionary:
	for pose in load_poses().get("poses", []):
		if str(pose["name"]) == name:
			return pose
	return {}


static func report_poses(report: String) -> Array:
	var poses: Array = []
	for pose in load_poses().get("poses", []):
		if report in pose.get("reports", []):
			poses.append(pose)
	return poses


# ------------------------------------------------------------------ weather

## Call after hold_world_clocks. Natural mode samples the old fixed time without clearing spawned weather.
static func pin_weather(game: Node, poses: Array) -> Dictionary:
	var mode := COMMON.arg_value("--weather=", "clear")
	if mode == "storm":
		return _pin_storm(game, poses)
	if mode != "clear" and mode != "natural":
		return {"error": "unknown --weather=%s (clear|natural|storm)" % mode}
	if poses.is_empty():
		return {"error": "no fixture poses for weather sampling"}
	var clouds := game.get_node_or_null("RingVolumetricClouds")
	if clouds == null or not clouds.has_method("GetNaturalWeatherDiagnosticAtTime"):
		return {"error": "RingVolumetricClouds weather diagnostic missing"}
	var spawned_cleared := false
	if mode == "clear":
		var sunshine := game.get_node_or_null("SunshineCloudBridge")
		if sunshine != null and sunshine.has_method("ClearSpawnedWeather"):
			sunshine.call("ClearSpawnedWeather")
			spawned_cleared = true

	var best := {}
	var best_failures := poses.size() + 1
	var best_worst_score := INF
	var candidates := 200 if mode == "clear" else 1
	for k in candidates:
		var cloud_time: float = COMMON.FIXED_CLOUD_TIME + 600.0 * k
		var per_pose := {}
		var failures := 0
		var worst_pose := ""
		var worst_score := -1.0
		for pose in poses:
			var name := str(pose["name"])
			var sample := _weather_at_time(game, clouds, float(pose["around_m"]), float(pose["across_m"]), cloud_time)
			if sample.has("error"):
				return {"error": "%s at cloud time %.1f: %s" % [name, cloud_time, sample["error"]]}
			per_pose[name] = sample
			var precipitation := float(sample["precipitation"])
			var storm := int(sample["system_kind"]) >= MAJOR_FRONT
			if storm or precipitation > CLEAR_PRECIPITATION_MAX:
				failures += 1
			# A front outranks rain alone. Break ties on precipitation, then fixture order.
			var score := precipitation + (1.0 if storm else 0.0)
			if score > worst_score:
				worst_score = score
				worst_pose = name
		if mode == "natural" or failures == 0:
			if mode == "clear":
				clouds.set("CloudSimulationTimeSeconds", cloud_time)
			var megastorm := {}
			for name in per_pose:
				megastorm[name] = per_pose[name]["megastorm"]
			return {"mode": mode, "cloud_time": cloud_time, "searched": k + 1 if mode == "clear" else 0, "per_pose": per_pose,
				"spawned_cleared": spawned_cleared, "megastorm": megastorm}
		if failures < best_failures or (failures == best_failures and worst_score < best_worst_score):
			best = {"cloud_time": cloud_time, "worst_pose": worst_pose, "weather": per_pose[worst_pose]}
			best_failures = failures
			best_worst_score = worst_score

	return {"error": "no clear weather time in 200 candidates; best time %.1f, %d/%d poses failed, worst pose %s (kind=%d, precipitation=%.4f)" %
		[best["cloud_time"], best_failures, poses.size(), best["worst_pose"], best["weather"]["system_kind"], best["weather"]["precipitation"]],
		"searched": 200, "best": best, "spawned_cleared": spawned_cleared}


## Perf pass P0 (docs/perf-pass-2026-11.md): `--weather=storm [--weather-pose=<name>]` pins the cloud time (same 200 candidates as clear mode) at which the named fixture pose
## (default: the first) has the heaviest natural weather (a front outranks rain alone). Spawned weather is left alone. TEST ONLY.
static func _pin_storm(game: Node, poses: Array) -> Dictionary:
	if poses.is_empty():
		return {"error": "no fixture poses for weather sampling"}
	var clouds := game.get_node_or_null("RingVolumetricClouds")
	if clouds == null or not clouds.has_method("GetNaturalWeatherDiagnosticAtTime"):
		return {"error": "RingVolumetricClouds weather diagnostic missing"}
	var wanted := COMMON.arg_value("--weather-pose=", str(poses[0]["name"]))
	var target := {}
	for pose in poses:
		if str(pose["name"]) == wanted:
			target = pose
	if target.is_empty():
		return {"error": "unknown --weather-pose=%s" % wanted}
	var best_time := -1.0
	var best_score := -1.0
	var best_sample := {}
	for k in 200:
		var cloud_time: float = COMMON.FIXED_CLOUD_TIME + 600.0 * k
		var sample := _weather_at_time(game, clouds, float(target["around_m"]), float(target["across_m"]), cloud_time)
		if sample.has("error"):
			return {"error": "%s at cloud time %.1f: %s" % [wanted, cloud_time, sample["error"]]}
		var score := float(sample["precipitation"]) + (1.0 if int(sample["system_kind"]) >= MAJOR_FRONT else 0.0)
		if score > best_score:
			best_score = score
			best_time = cloud_time
			best_sample = sample
	clouds.set("CloudSimulationTimeSeconds", best_time)
	return {"mode": "storm", "cloud_time": best_time, "searched": 200, "per_pose": {wanted: best_sample}, "spawned_cleared": false, "megastorm": {wanted: best_sample.get("megastorm", null)},
		"storm_pose": wanted, "storm_score": best_score}


static func weather_at(game: Node, around: float, across: float) -> Dictionary:
	var clouds := game.get_node_or_null("RingVolumetricClouds")
	if clouds == null or not clouds.has_method("GetNaturalWeatherDiagnosticAtTime"):
		return {"error": "RingVolumetricClouds weather diagnostic missing"}
	return _weather_at_time(game, clouds, around, across, float(clouds.get("CloudSimulationTimeSeconds")))


static func _weather_at_time(game: Node, clouds: Node, around: float, across: float, cloud_time: float) -> Dictionary:
	var weather: Dictionary = clouds.call("GetNaturalWeatherDiagnosticAtTime", around, across, cloud_time)
	if not weather.has("system_kind") or not weather.has("precipitation") or not is_finite(float(weather["precipitation"])):
		return {"error": "incomplete weather diagnostic"}
	var megastorm := game.get_node_or_null("RingMegastorm")
	weather["megastorm"] = "unknown"
	if megastorm != null and megastorm.has_method("query_megastorm"):
		var state: Dictionary = megastorm.call("query_megastorm", around, across, cloud_time)
		if state.has("active") and state.has("phase_name"):
			# Active describes the whole storm. The phase describes this position.
			weather["megastorm"] = {"active": state["active"], "phase": state["phase_name"]}
	return weather


# ------------------------------------------------------------------ boot

## Boots the isolated gameplay world with the pinned generation 1 recipe and the user's preference files. Returns {game, world, prefs, fixture_root} or {} after push_error
## ("<marker>=FAIL ..."). The caller removes `fixture_root` (remove_dir_recursive on its globalized path) when it finishes.
static func boot(tree: SceneTree, marker: String) -> Dictionary:
	var bridge: RefCounted = BRIDGE.new()
	var selection: Variant = bridge.call("ConfigureFromArgs", OS.get_cmdline_user_args())
	if not selection is Dictionary or not selection.has("world_tag"):
		var error := str(selection.get("error", "world selection failed")) if selection is Dictionary else "world selection unavailable"
		push_error("%s=FAIL %s" % [marker, error])
		return {}
	var world_tag: String = selection["world_tag"]
	var fixture_root := "res://.godot/captures/landscape/fixture_world/%s" % _world_stamp(world_tag)
	var root := tree.root
	var prior := root.get_node_or_null("WorldSession")
	if prior != null:
		prior.name = "WorldSessionOriginalLandscapeHarness"
	var session := COMMON.SessionStub.new()
	session.name = "WorldSession"
	root.add_child(session)
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture_root)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	# Differs from COMMON.create_game: v7 and v8 are always on, and the loaded recipe is verified against the selected one below.
	ProjectSettings.set_setting("ringworld/runtime/surface_landforms_v7", true)
	ProjectSettings.set_setting("ringworld/runtime/surface_weather_lung_v8", true)
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture_root))
	GENERATION_PIN.pin(int(selection["generation"]), ",".join(selection["review_features"]))
	var launcher := COMMON.LAUNCHER.instantiate()
	if not world_tag.is_empty():
		launcher.set("use_preview_recipe", false)
	root.add_child(launcher)
	GENERATION_PIN.unpin()
	await tree.process_frame
	launcher.call("_on_certified_preset_pressed")
	var selector: OptionButton = launcher.get("terrain_backend_selector")
	if selector == null:
		push_error("%s=FAIL launcher terrain backend selector missing" % marker)
		return {}
	selector.select(2)
	selector.emit_signal("item_selected", selector.selected)
	if not bool(launcher.call("_prepare_runtime_surface_generation", true)):
		push_error("%s=FAIL runtime-v2 generation preparation failed" % marker)
		return {}
	launcher.call("_save_selected_slot_settings")
	var game := COMMON.GAME.instantiate()
	launcher.call("_configure_game", game, 5000000.0)
	# A water.rivers world needs its baked river graph before the world is attached (T2R-9); the loading screen does this in the real launch, the harness bakes it blocking.
	var hydrology_error: String = LOADING.bake_hydrology_blocking(game)
	if not hydrology_error.is_empty():
		push_error("%s=FAIL %s" % [marker, hydrology_error])
		return {}
	var prefs := _install_isolated_preferences(game, fixture_root)
	var mon: Node = game.get_node_or_null("RingPerformanceMonitor")
	if mon != null:
		mon.call("InstallSettingsPathForTests", fixture_root.path_join("telemetry.tres"))
		var reports := fixture_root.path_join("reports")
		DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(reports))
		mon.call("InstallReportDirectoryForTests", reports)
	root.add_child(game)
	launcher.queue_free()
	var deadline := Time.get_ticks_msec() + 600000
	var ready := false
	while Time.get_ticks_msec() < deadline:
		await tree.process_frame
		var candidate := game.get_node_or_null("Terrain3DGameplay")
		if candidate != null and bool(candidate.call("EnsureInitialized")):
			ready = true
			break
	if not ready:
		push_error("%s=FAIL gameplay startup did not become ready" % marker)
		return {}
	var world: Dictionary = bridge.call("VerifyGame", game)
	world["pinned_definition_hash"] = str(bridge.call("DefinitionHashHex"))
	if not bool(world.get("ok", false)):
		push_error("%s=FAIL world mismatch: %s" % [marker, str(world.get("message", ""))])
		return {}
	var world_record: Dictionary = {} if world_tag.is_empty() else selection
	return {"game": game, "world": world, "prefs": prefs, "fixture_root": fixture_root, "world_bridge": bridge,
		"world_tag": world_tag, "world_record": world_record}


## Copies the preference files into the fixture under their REAL names (`--prefs=user|defaults|<dir>`), points every drawer at them, and returns what was used.
## Every file used is also copied to <fixture_root>/prefs_snapshot/ so callers can keep the exact inputs next to their results.
static func _install_isolated_preferences(game: Node, fixture_root: String) -> Dictionary:
	var mode := COMMON.arg_value("--prefs=", "user")
	var drawer: Node = game.get_node_or_null("LiveTuningDrawer")
	var used: Array = []
	var absent: Array = []
	if drawer == null:
		return {"mode": mode, "used": used, "absent": absent, "error": "no LiveTuningDrawer"}
	var snapshot := fixture_root.path_join("prefs_snapshot")
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(snapshot))
	for item in PREF_FILES:
		var file_name := str(item[1])
		var destination := fixture_root.path_join(file_name)
		var source := ""
		if mode == "user":
			source = "user://" + file_name
		elif mode != "defaults":
			source = mode.path_join(file_name)
		if source != "" and FileAccess.file_exists(source):
			var text := FileAccess.get_file_as_string(source)
			for target in [destination, snapshot.path_join(file_name)]:
				var copy := FileAccess.open(target, FileAccess.WRITE)
				if copy != null:
					copy.store_string(text)
					copy.close()
			used.append(file_name)
		else:
			absent.append(file_name)
		if drawer.has_method(str(item[0])):
			if str(item[0]) == "InstallWetWorldDefaults":
				drawer.call("InstallWetWorldDefaults", destination, null) # optional second parameter cannot be omitted through call()
			else:
				drawer.call(str(item[0]), destination)
	if drawer.has_method("InstallEmitterSiteDefaults"):
		drawer.call("InstallEmitterSiteDefaults", fixture_root.path_join("emitter_tuning.tres"))
	if drawer.has_method("InstallEclipseDefaults"):
		drawer.call("InstallEclipseDefaults", fixture_root.path_join("eclipse_tuning.tres"))
	return {"mode": mode, "used": used, "absent": absent, "snapshot_dir": ProjectSettings.globalize_path(snapshot)}


## Copies the used preference snapshot into a result folder.
static func copy_prefs_snapshot(boot_result: Dictionary, target_dir: String) -> void:
	var source := ProjectSettings.globalize_path(str(boot_result["fixture_root"]).path_join("prefs_snapshot"))
	var dir := DirAccess.open(source)
	if dir == null:
		return
	var out := target_dir.path_join("prefs_snapshot")
	DirAccess.make_dir_recursive_absolute(out)
	for file in dir.get_files():
		DirAccess.copy_absolute(source.path_join(file), out.path_join(file))


# ------------------------------------------------------------------ preset

## `--preset=medium` (default) applies Medium and waits for builds; `--preset=keep` leaves the preset alone. Records the effective preset and the grass render style and
## returns {error} unless the style is Cards (`--allow-blades` lifts that).
static func apply_preset(tree: SceneTree, game: Node) -> Dictionary:
	var graphics := game.get_node_or_null("GraphicsQuality")
	var grass := game.get_node_or_null("StandingGrass")
	if graphics == null:
		return {"error": "no GraphicsQuality node"}
	var requested := COMMON.arg_value("--preset=", "medium")
	if requested == "medium":
		graphics.call("ApplyPreset", MEDIUM_PRESET, true, false)
		await COMMON.wait_builds_idle(tree, game, 300.0)
	elif requested != "keep":
		return {"error": "unknown --preset=%s (medium|keep)" % requested}
	var style := int(grass.get("GrassRenderStyle")) if grass != null else -1
	var result := {"requested": requested, "preset": str(graphics.get("CurrentPresetName")), "grass_render_style": style, "grass_render_style_name": "Cards" if style == 1 else ("Blades" if style == 0 else "unknown")}
	if style != 1 and not COMMON.has_flag("--allow-blades"):
		result["error"] = "grass render style is %s, not Cards (pass --allow-blades to measure it anyway)" % result["grass_render_style_name"]
	return result


# ------------------------------------------------------------------ poses

## Teleports to a fixture pose (flight, so gravity cannot drift it), waits for streaming to go idle, applies the named time of day, settles and checks the ground.
## `heading` ({yaw, pitch}, e.g. one of the fixture's `headings`) overrides the pose's own heading. Returns {pose, time_name, time, sun_elevation_deg, streaming_idle,
## streaming_idle_after, ground, error?}.
static func go_pose(tree: SceneTree, game: Node, pose: Dictionary, time_name: String, ground_model: RefCounted = null,
		heading: Dictionary = {}, settle_s := 2.0, idle_timeout_s := 300.0) -> Dictionary:
	var info := {"pose": str(pose["name"]), "time_name": time_name}
	var times: Dictionary = load_poses().get("times", {})
	if not times.has(time_name):
		info["error"] = "unknown time '%s'" % time_name
		return info
	var explorer := game.get_node_or_null("Explorer")
	var surface := game.get_node_or_null("Terrain3DGameplay")
	if explorer == null or surface == null:
		info["error"] = "explorer or surface node missing"
		return info
	var around := float(pose["around_m"])
	var across := float(pose["across_m"])
	var lift := float(pose["lift_m"])
	var agl := str(pose["vertical_mode"]) == "agl"
	var fixture_ground := float(pose["ground_m"])
	var expected := fixture_ground
	if agl and ground_model != null:
		var chosen_ground: Variant = ground_model.call("GroundHeight", around, across)
		if not chosen_ground is float or not is_finite(float(chosen_ground)):
			info["error"] = "selected world ground height unavailable"
			return info
		expected = float(chosen_ground)
	var vertical := expected + lift if agl else lift
	var yaw := float(heading.get("yaw", pose["yaw_deg"]))
	var pitch := float(heading.get("pitch", pose["pitch_deg"]))
	explorer.call("TeleportToCanonicalMeters", around, vertical, across, yaw, pitch, true)
	await COMMON.settle(tree, 0.2)
	info["streaming_idle"] = await COMMON.wait_builds_idle(tree, game, idle_timeout_s)
	var time_state: Dictionary = COMMON.apply_time(game, times[time_name])
	info["time"] = time_state
	if time_state.has("error"):
		info["error"] = str(time_state["error"])
		return info
	info["sun_elevation_deg"] = float(time_state.get("sun_elevation_deg", NAN))
	await COMMON.settle(tree, settle_s)
	info["streaming_idle_after"] = COMMON.builds_idle(game)
	if agl:
		var sampled := float(surface.call("SampleHeight", around, across))
		info["ground"] = {"sampled_m": sampled, "expected_m": expected, "ok": absf(sampled - expected) < 0.5}
		if ground_model != null:
			info["ground"]["fixture_m"] = fixture_ground
		if not bool(info["ground"]["ok"]):
			info["error"] = "ground check failed: game %.3f m vs %s %.3f m" % [sampled, "fixture" if ground_model == null else "selected world", expected]
	else:
		info["ground"] = {"ok": true, "note": "absolute pose, not checked"}
	# Mouse look reaching the window rotates the camera during the settle. If the heading or velocity moved, re-issue the teleport (it zeroes velocity and sets yaw/pitch) and settle again, because a teleport restarts the camera-basis alignment; an undisturbed pose is left alone. The pose actually reached is recorded either way.
	var before := _explorer_pose(explorer)
	info["pose_before_reissue"] = before
	var drifted := absf(wrapf(float(before["yaw_deg"]) - yaw, -180.0, 180.0)) > 0.01 or absf(float(before["pitch_deg"]) - pitch) > 0.01 or float(before["velocity_mps"]) > 0.001
	info["pose_reissued"] = drifted
	if drifted:
		explorer.call("TeleportToCanonicalMeters", around, vertical, across, yaw, pitch, true)
		await COMMON.settle(tree, settle_s)
	var reached := _explorer_pose(explorer)
	reached["requested"] = {"around_m": around, "vertical_m": vertical, "across_m": across, "yaw_deg": yaw, "pitch_deg": pitch}
	info["pose_reached"] = reached
	return info


static func _explorer_pose(explorer: Node) -> Dictionary:
	var position: Vector3 = explorer.call("GetCanonicalAtmospherePositionMeters") # x is always 0; only vertical (y) and across (z) are exposed
	var velocity: Vector3 = explorer.get("CanonicalVelocity")
	return {"vertical_m": position.y, "across_m": position.z, "yaw_deg": rad_to_deg(float(explorer.get("SavedYaw"))),
		"pitch_deg": float(explorer.get("SavedPitch")), "velocity_mps": velocity.length()}


# ------------------------------------------------------------------ capture

## Appends the test-only depth CompositorEffect to the world environment and returns it.
static func install_depth_effect(world_environment: WorldEnvironment) -> CompositorEffect:
	var effect: CompositorEffect = DEPTH_EFFECT.new()
	if world_environment.compositor == null:
		world_environment.compositor = Compositor.new()
	var effects := world_environment.compositor.compositor_effects.duplicate()
	effects.append(effect)
	world_environment.compositor.compositor_effects = effects
	return effect


## Arms the depth effect, takes the final frame and waits up to 10 frames for the depth copy. Returns {image (RGBA8), depth ({size, depth} or {})}.
static func capture(tree: SceneTree, depth_effect: CompositorEffect) -> Dictionary:
	depth_effect.arm()
	await RenderingServer.frame_post_draw
	var image := tree.root.get_texture().get_image()
	if image.get_format() != Image.FORMAT_RGBA8:
		image.convert(Image.FORMAT_RGBA8)
	var depth := {}
	for i in 10:
		depth = depth_effect.take()
		if not depth.is_empty():
			break
		await RenderingServer.frame_post_draw
	return {"image": image, "depth": depth}
