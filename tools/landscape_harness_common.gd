extends RefCounted

## Shared GPU boot and capture module of the landscape (T0) and vastness (V0) tools (docs/landscape-audit-t0-spec.md 2.6). TEST ONLY.
## Reuses tools/shadow_harness_common.gd (COMMON) for machine info, power log, stats, time of day and streaming-idle helpers, but has its own boot: it pins the
## generation 1 world (v7 + v8 on, verified against LandscapeWorldBridge), installs the user's REAL preference files under their real names, and records which ones
## it used. Never calls or modifies COMMON.create_game, never writes to user:// settings.
## Flags understood here (after `--`):  --prefs=user|defaults|<dir>   --preset=medium|keep   --allow-blades   --res=WxH
## Output layout: see docs/landscape-audit-t0-spec.md 2.7 (transient_dir / baseline_dir).

const COMMON := preload("res://tools/shadow_harness_common.gd")
const BRIDGE := preload("res://tools/LandscapeWorldBridge.cs")
const BANDS := preload("res://tools/landscape_audit_bands.gd")
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
static func transient_dir(kind: String) -> String:
	return _make(ProjectSettings.globalize_path("res://.godot/captures/landscape/t0").path_join(kind).path_join(stamp()))


## <project>/.godot/audit_baseline/<machine>/<phase>/<kind>/<stamp> (durable; machine is "common" for machine-independent headless output).
static func baseline_dir(machine: String, phase: String, kind: String) -> String:
	return _make(ProjectSettings.globalize_path("res://.godot/audit_baseline").path_join(machine).path_join(phase).path_join(kind).path_join(stamp()))


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


# ------------------------------------------------------------------ boot

## Boots the isolated gameplay world with the pinned generation 1 recipe and the user's preference files. Returns {game, world, prefs, fixture_root} or {} after push_error
## ("<marker>=FAIL ..."). The caller removes `fixture_root` (remove_dir_recursive on its globalized path) when it finishes.
static func boot(tree: SceneTree, marker: String) -> Dictionary:
	var fixture_root := "res://.godot/captures/landscape/fixture_world/%s" % stamp()
	var root := tree.root
	var prior := root.get_node_or_null("WorldSession")
	if prior != null:
		prior.name = "WorldSessionOriginalLandscapeHarness"
	var session := COMMON.SessionStub.new()
	session.name = "WorldSession"
	root.add_child(session)
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", fixture_root)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	# Differs from COMMON.create_game: v7 and v8 are always on, and the loaded recipe is verified against the pinned one below.
	ProjectSettings.set_setting("ringworld/runtime/surface_landforms_v7", true)
	ProjectSettings.set_setting("ringworld/runtime/surface_weather_lung_v8", true)
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(fixture_root))
	var launcher := COMMON.LAUNCHER.instantiate()
	root.add_child(launcher)
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
	var bridge: RefCounted = BRIDGE.new()
	var world: Dictionary = bridge.call("VerifyGame", game)
	world["pinned_definition_hash"] = str(bridge.call("DefinitionHashHex"))
	if not bool(world.get("ok", false)):
		push_error("%s=FAIL world mismatch: %s" % [marker, str(world.get("message", ""))])
		return {}
	return {"game": game, "world": world, "prefs": prefs, "fixture_root": fixture_root}


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
static func go_pose(tree: SceneTree, game: Node, pose: Dictionary, time_name: String, heading: Dictionary = {}, settle_s := 2.0, idle_timeout_s := 300.0) -> Dictionary:
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
	var vertical := float(pose["ground_m"]) + lift if str(pose["vertical_mode"]) == "agl" else lift
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
	if str(pose["vertical_mode"]) == "agl":
		var sampled := float(surface.call("SampleHeight", around, across))
		var expected := float(pose["ground_m"])
		info["ground"] = {"sampled_m": sampled, "expected_m": expected, "ok": absf(sampled - expected) < 0.5}
		if not bool(info["ground"]["ok"]):
			info["error"] = "ground check failed: game %.3f m vs fixture %.3f m" % [sampled, expected]
	else:
		info["ground"] = {"ok": true, "note": "absolute pose, not checked"}
	return info


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
