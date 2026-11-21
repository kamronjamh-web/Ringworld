extends RefCounted

## Shared GPU boot and capture module of the landscape (T0) and vastness (V0) tools (docs/landscape-audit-t0-spec.md 2.6). TEST ONLY.
## Reuses tools/shadow_harness_common.gd (COMMON) for machine info, power log, stats, time of day and streaming-idle helpers, but has its own boot: it pins the
## selected world (pinned v8 by default, verified against LandscapeWorldBridge), installs the user's REAL preference files under their real names, and records which ones
## it used. Never calls or modifies COMMON.create_game, never writes to user:// settings.
## Flags understood here (after `--`):  --prefs=user|defaults|<dir>   --preset=medium|keep   --allow-blades   --res=WxH   --weather=clear|natural
## World selection: --generation=N [--review-features=a,b]. Generation 0 selects the pinned v8 world.
## --rock-set=<absolute folder> (test-only, T8-R1): albedo.png + normal.png + rockset.json replace the rock texture on terrain rock, scatter rocks and (with mean_linear) the far rock tone, in memory only. See install_rock_set.
## --rock-ab (test-only, T8-R3): patches terrain, proxy and scatter rock shaders in memory so the global harness_rock_old (0 = the T8-R2 rock face, 1 = the old alpine-based rock) switches the rock path in one boot; used by the cost case rock_old. See install_rock_ab.
## Output layout: see docs/landscape-audit-t0-spec.md 2.7 (transient_dir / baseline_dir).

const COMMON := preload("res://tools/shadow_harness_common.gd")
const GENERATION_PIN := preload("res://tools/world_generation_pin.gd")
const BRIDGE := preload("res://tools/LandscapeWorldBridge.cs")
const BANDS := preload("res://tools/landscape_audit_bands.gd")
const LOADING := preload("res://scripts/Loading/ring_world_loading.gd")
const DEPTH_EFFECT := preload("res://tools/shadow_depth_capture_effect.gd")
const POSE_FIXTURE := "res://tools/fixtures/landscape_poses_v8_s1337.json"
## T4-2: the dressing poses (steep, scree, cliff, breaks, mountain, lowland). `--poses=` and pose_by_name take names from either fixture; the T0 rows keep their meaning.
const DRESSING_FIXTURE := "res://tools/fixtures/landscape_poses_dressing_s1337.json"
const DRESSING_REPORT := "t4"
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
const HIGH_PRESET := 2 # RingTerrainLodCoordinator.QualityPreset.High (T5-10 `--preset=high`, the T4-6 review preset)
const CLEAR_PRECIPITATION_MAX := 0.02
const MAJOR_FRONT := 3 # WeatherSystemKind in scripts/RingVolumetricCloudLayer.cs:32

static var _fixture := {}
static var _dressing := {}
## Poses resolved at run time (river_bank_eye, from the booted world's river graph), by name.
static var _runtime_poses := {}
## T4-2 `--seed=<n>`: the world seed of this run (1337 unless the flag is given). The T0 and dressing fixtures are seed 1337 coordinates and are ignored for any other seed.
const PINNED_SEED := 1337
static var seed := PINNED_SEED
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
	for pose in dressing_poses():
		if str(pose["name"]) == name:
			return pose
	return {}


## The dressing fixture's rows plus the run-time resolved ones (T4-2); [] when the fixture is missing.
static func dressing_poses() -> Array:
	if seed != PINNED_SEED:
		return _runtime_poses.values()
	if _dressing.is_empty():
		var parsed: Variant = JSON.parse_string(FileAccess.get_file_as_string(DRESSING_FIXTURE))
		if parsed is Dictionary:
			_dressing = parsed
	var rows: Array = (_dressing.get("poses", []) as Array).duplicate()
	for name in _runtime_poses:
		rows.append(_runtime_poses[name])
	return rows


## Names of every pose a `--poses=` list can select that is not a T0 pose (dressing fixture rows and run-time poses).
static func dressing_pose_names() -> PackedStringArray:
	var names := PackedStringArray()
	for pose in dressing_poses():
		names.append(str(pose["name"]))
	if not names.has(RIVER_BANK_POSE):
		names.append(RIVER_BANK_POSE)
	return names


const RIVER_BANK_POSE := "river_bank_eye"


## Resolves river_bank_eye from the booted world's river graph (LandscapeWorldBridge.ResolveRiverBankPose) and registers it for `--poses=`. {} plus a push-free error string in
## "error" when the world has no rivers.
static func resolve_river_bank(boot_result: Dictionary) -> Dictionary:
	var resolved: Dictionary = (boot_result["world_bridge"] as RefCounted).call("ResolveRiverBankPose", boot_result["game"])
	if not bool(resolved.get("ok", false)):
		return {"error": str(resolved.get("message", "river bank search failed"))}
	_runtime_poses[RIVER_BANK_POSE] = resolved["pose"]
	return resolved["pose"]


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
	if rock_ab_requested() and rock_set_requested():
		push_error("%s=FAIL --rock-ab and --rock-set are mutually exclusive" % marker)
		return {}
	if rock_ab_requested() and install_rock_ab(marker).is_empty():
		return {}
	if rock_set_requested() and install_rock_set(marker).is_empty():
		return {}
	var bridge: RefCounted = BRIDGE.new()
	var selection: Variant = bridge.call("ConfigureFromArgs", OS.get_cmdline_user_args())
	if not selection is Dictionary or not selection.has("world_tag"):
		var error := str(selection.get("error", "world selection failed")) if selection is Dictionary else "world selection unavailable"
		push_error("%s=FAIL %s" % [marker, error])
		return {}
	var world_tag: String = selection["world_tag"]
	seed = int(selection.get("seed", PINNED_SEED))
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
	if seed != PINNED_SEED:
		var seed_spin: SpinBox = launcher.get("terrain_seed_spin")
		if seed_spin == null:
			push_error("%s=FAIL launcher seed control missing" % marker)
			return {}
		seed_spin.value = seed
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
	if not rock_set_record.is_empty() and not _bind_rock_detail_normal(marker, "after boot"):
		return {}
	return {"game": game, "world": world, "prefs": prefs, "fixture_root": fixture_root, "world_bridge": bridge,
		"world_tag": world_tag, "world_record": world_record}


# ------------------------------------------------------------------ rock A-B (T8-R3, test only)

## Strong references: the patched Shader resources must outlive the run.
static var _rock_ab_shaders: Array = []
## True once install_rock_ab patched the shaders; landscape_baseline_common records it as rock_ab and the rock_old case needs it.
static var rock_ab_active := false


static func rock_ab_requested() -> bool:
	for arg in OS.get_cmdline_user_args():
		if arg == "--rock-ab":
			return true
	for word in OS.get_environment("LANDSCAPE_HARNESS_ARGS").split(" ", false):
		if word == "--rock-ab":
			return true
	return false


## Adds the global float harness_rock_old (0 = the T8-R2 rock face, unchanged; 1 = the OLD rock path: alpine * GROUND_ROCK_TINT * strata_factor at the biome scale, no rock lattice, detail normal on the
## biome lattice) and patches the terrain, proxy and scatter rock shaders in memory to branch on it. Call before the world boots. {} (after push_error) when any pattern does not match exactly once.
static func install_rock_ab(marker: String) -> Dictionary:
	var declare := "global uniform float harness_rock_old;\n"
	var rules := {
		"ground": ["res://materials/terrain3d_ring_surface_ground.gdshader", [
			["vec3 rock_position = texture_position * GROUND_ROCK_UV_SCALE;", "float harness_k = harness_rock_old > 0.5 ? 1.0 : GROUND_ROCK_UV_SCALE;\n            vec3 rock_position = texture_position * harness_k;"],
			["vec3 rock_dpdx = dpdx * GROUND_ROCK_UV_SCALE;", "vec3 rock_dpdx = dpdx * harness_k;"],
			["vec3 rock_dpdy = dpdy * GROUND_ROCK_UV_SCALE;", "vec3 rock_dpdy = dpdy * harness_k;"],
			["ty_rock = ring_biome_antitile(rock_position, texture_normal, rock_dpdx, rock_dpdy);", "if (harness_rock_old > 0.5) { ty_rock = ty; } else { ty_rock = ring_biome_antitile(rock_position, texture_normal, rock_dpdx, rock_dpdy); }"],
			["rock_albedo = ring_biome_triplanar(ground_rock_albedo, rock_position, texture_normal, blend, ty_rock, rock_dpdx, rock_dpdy) * GROUND_ROCK_TINT * strata_factor;",
				"if (harness_rock_old > 0.5 && harness_rock_old < 1.5) { rock_albedo = alpine * GROUND_ROCK_TINT * strata_factor; } else { rock_albedo = ring_biome_triplanar(ground_rock_albedo, rock_position, texture_normal, blend, ty_rock, rock_dpdx, rock_dpdy) * GROUND_ROCK_TINT * strata_factor; }"],
		]],
		"proxy": ["res://materials/ring_terrain_proxy_ground.gdshader", [
			["vec3 rock_position = texture_position * GROUND_ROCK_UV_SCALE;", "if (harness_rock_old < 0.5) {\n\t\t\t\t\tvec3 rock_position = texture_position * GROUND_ROCK_UV_SCALE;"],
			["rock_albedo = ring_biome_triplanar_world(ground_rock_albedo, world_normal, blend, tx_rock, ty_rock, tz_rock, rock_dpdx, rock_dpdy) * GROUND_ROCK_TINT * strata_factor;",
				"rock_albedo = ring_biome_triplanar_world(ground_rock_albedo, world_normal, blend, tx_rock, ty_rock, tz_rock, rock_dpdx, rock_dpdy) * GROUND_ROCK_TINT * strata_factor;\n\t\t\t\t}"],
			["terrain_color = mix(mix(ground_surface_texture, alpine * GROUND_SCREE_TINT, ground_scree), rock_albedo, ground_rock);",
				"if (harness_rock_old > 1.5) rock_albedo = ring_biome_triplanar_world(ground_rock_albedo, world_normal, blend, tx, ty, tz, dpdx, dpdy) * GROUND_ROCK_TINT * strata_factor; else if (harness_rock_old > 0.5) rock_albedo = alpine * GROUND_ROCK_TINT * strata_factor;\n\t\t\t\tterrain_color = mix(mix(ground_surface_texture, alpine * GROUND_SCREE_TINT, ground_scree), rock_albedo, ground_rock);"],
		]],
		"scatter": ["res://materials/ring_scatter_rock.gdshader", [
			["vec3 rock = rock_biome_triplanar(ground_rock_albedo, v_world * biome_texture_scale * GROUND_ROCK_UV_SCALE, world_normal, blend) * GROUND_ROCK_TINT;",
				"vec3 rock = (harness_rock_old > 1.5 ? rock_biome_triplanar(ground_rock_albedo, v_world * biome_texture_scale, world_normal, blend) : harness_rock_old > 0.5 ? rock_biome_triplanar(biome_alpine_albedo, v_world * biome_texture_scale, world_normal, blend) : rock_biome_triplanar(ground_rock_albedo, v_world * biome_texture_scale * GROUND_ROCK_UV_SCALE, world_normal, blend)) * GROUND_ROCK_TINT;"],
		]],
	}
	var plans := {}
	for key in rules:
		var path: String = rules[key][0]
		var shader := load(path) as Shader
		if shader == null:
			push_error("%s=FAIL --rock-ab: cannot load shader %s" % [marker, path])
			return {}
		var code := shader.code
		var state := {}
		for pair in rules[key][1]:
			code = _rock_replace(code, pair[0], pair[1], path, state)
		code = _rock_replace(code, "\nvoid fragment()", "\n" + declare + "\nvoid fragment()", path, state)
		if state.has("error"):
			push_error("%s=FAIL --rock-ab: %s" % [marker, str(state["error"])])
			return {}
		plans[key] = [shader, code]
	# Every pattern matched exactly once; now apply. The global exists before any patched shader compiles.
	RenderingServer.global_shader_parameter_add("harness_rock_old", RenderingServer.GLOBAL_VAR_TYPE_FLOAT, 0.0)
	for key in plans:
		var shader: Shader = plans[key][0]
		shader.code = plans[key][1]
		_rock_ab_shaders.append(shader)
	rock_ab_active = true
	print("%s rock A-B: patched %s (global harness_rock_old: 0 = new rock face, 1 = old rock)" % [marker, ", ".join(plans.keys())])
	return {"ok": true}


# ------------------------------------------------------------------ rock set (T8-R1, test only)

const ROCK_SHADERS := {
	"ground": "res://materials/terrain3d_ring_surface_ground.gdshader",
	"proxy": "res://materials/ring_terrain_proxy_ground.gdshader",
	"scatter": "res://materials/ring_scatter_rock.gdshader",
	"far": "res://materials/ring_terrain_global_ground.gdshader",
}
const ROCK_DETAIL_GLOBAL := "ringworld_ground_rock_detail_normal"
## The record of the installed rock set ({} when --rock-set was not given or failed); landscape_baseline_common.setup copies it into look.json.
static var rock_set_record := {}
## Strong references: the patched Shader resources and textures must outlive the run.
static var _rock_shaders: Array = []
static var _rock_textures: Array = []


## `--rock-set=<folder>` from the real command line or LANDSCAPE_HARNESS_ARGS ("" when absent).
static func _rock_set_arg() -> String:
	var args := OS.get_cmdline_user_args()
	for word in OS.get_environment("LANDSCAPE_HARNESS_ARGS").split(" ", false):
		args.append(word)
	for arg in args:
		if arg.begins_with("--rock-set="):
			return arg.trim_prefix("--rock-set=")
	return ""


static func rock_set_requested() -> bool:
	return _rock_set_arg() != ""


static func _rock_fail(marker: String, reason: String) -> Dictionary:
	push_error("%s=FAIL --rock-set: %s" % [marker, reason])
	return {}


## Replaces `pattern` by `replacement` in `code`; it must occur exactly once, otherwise state["error"] is set and the code is returned unchanged.
static func _rock_replace(code: String, pattern: String, replacement: String, path: String, state: Dictionary) -> String:
	var n := code.count(pattern)
	if n != 1:
		state["error"] = "%s: pattern matched %d times (need 1): %s" % [path, n, pattern]
		return code
	return code.replace(pattern, replacement)


## Replaces the rock albedo (ground variants, scatter rocks), the rock detail normal and optionally the far rock mean by the candidate set in <folder> (albedo.png sRGB, normal.png OpenGL
## linear, rockset.json {id, uv_scale, mean_linear?}) by patching the four shaders' code in memory (T8-R2: the ground variants and the scatter shader already read ground_rock_albedo at GROUND_ROCK_UV_SCALE, so the patch swaps the sampler and the scale) (nothing is written). Call before the world boots. {} (after push_error) on any failure.
static func install_rock_set(marker: String) -> Dictionary:
	var folder := _rock_set_arg()
	if folder == "":
		return {}
	var json_path := folder.path_join("rockset.json")
	var parsed: Variant = JSON.parse_string(FileAccess.get_file_as_string(json_path)) if FileAccess.file_exists(json_path) else null
	if not parsed is Dictionary:
		return _rock_fail(marker, "rockset.json missing or invalid in %s" % folder)
	var uv_scale := float(parsed.get("uv_scale", 1.0))
	if not (uv_scale > 0.0 and is_finite(uv_scale)):
		return _rock_fail(marker, "uv_scale must be > 0")
	var mean: Variant = parsed.get("mean_linear", null)
	if mean != null and not (mean is Array and (mean as Array).size() == 3):
		return _rock_fail(marker, "mean_linear must be [r,g,b]")
	var images := {}
	for name in ["albedo", "normal"]:
		var path := folder.path_join(name + ".png")
		var image := Image.load_from_file(path)
		if image == null or image.is_empty():
			return _rock_fail(marker, "cannot load %s" % path)
		var side := image.get_width()
		if side != image.get_height() or side < 1 or (side & (side - 1)) != 0:
			return _rock_fail(marker, "%s is %dx%d, need square power of two" % [path, side, image.get_height()])
		images[name] = image
	if (images["albedo"] as Image).get_width() != (images["normal"] as Image).get_width():
		return _rock_fail(marker, "albedo and normal sizes differ")
	var textures := {}
	for name in images:
		var image: Image = images[name]
		image.convert(Image.FORMAT_RGBA8)
		image.generate_mipmaps()
		textures[name] = ImageTexture.create_from_image(image)
	_rock_textures = [textures["albedo"], textures["normal"]]

	var k := "%.6f" % uv_scale
	var header_common := "global uniform sampler2D harness_rock_albedo : filter_linear_mipmap_anisotropic, repeat_enable;\nconst float HARNESS_ROCK_UV_SCALE = %s;\n" % k
	var header_srgb := "vec3 harness_srgb_to_linear(vec3 c) {\n\treturn mix(pow((c + vec3(0.055)) / 1.055, vec3(2.4)), c / 12.92, step(c, vec3(0.04045)));\n}\n"
	var plans := {}
	var counts_text: Array = []
	for key in ROCK_SHADERS:
		if key == "far" and mean == null:
			continue
		var path: String = ROCK_SHADERS[key]
		var shader := load(path) as Shader
		if shader == null:
			return _rock_fail(marker, "cannot load shader %s" % path)
		var code := shader.code
		var state := {}
		var header := ""
		var applied := 0
		if key == "ground" or key == "proxy" or key == "scatter":
			# T8-R2: the shaders already sample ground_rock_albedo at GROUND_ROCK_UV_SCALE (with their own lattices); the candidate replaces the sampler (a global, sRGB decoded here) and the scale.
			header = header_common + header_srgb
			var call_old := ""
			if key == "ground":
				call_old = "ring_biome_triplanar(ground_rock_albedo, rock_position, texture_normal, blend, ty_rock, rock_dpdx, rock_dpdy)"
			elif key == "proxy":
				call_old = "ring_biome_triplanar_world(ground_rock_albedo, world_normal, blend, tx_rock, ty_rock, tz_rock, rock_dpdx, rock_dpdy)"
			else:
				call_old = "rock_biome_triplanar(ground_rock_albedo, v_world * biome_texture_scale * GROUND_ROCK_UV_SCALE, world_normal, blend)"
			code = _rock_replace(code, "uniform sampler2D ground_rock_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;", "", path, state)
			code = _rock_replace(code, call_old, "harness_srgb_to_linear(" + call_old.replace("ground_rock_albedo", "harness_rock_albedo") + ")", path, state)
			if not code.contains("GROUND_ROCK_UV_SCALE"):
				state["error"] = "%s: GROUND_ROCK_UV_SCALE not found" % path
			code = code.replace("GROUND_ROCK_UV_SCALE", "HARNESS_ROCK_UV_SCALE")
			applied = 3 if key != "scatter" else 1
		else:
			header = "const vec3 HARNESS_ROCK_MEAN_LINEAR = vec3(%.6f, %.6f, %.6f);\n" % [float(mean[0]), float(mean[1]), float(mean[2])]
			code = _rock_replace(code, "GROUND_ROCK_MEAN_LINEAR * GROUND_ROCK_TINT", "HARNESS_ROCK_MEAN_LINEAR * GROUND_ROCK_TINT", path, state)
			applied = 1
		# The header goes right before fragment(): after every include and helper, at global scope.
		code = _rock_replace(code, "\nvoid fragment()", "\n" + header + "\nvoid fragment()", path, state)
		if state.has("error"):
			return _rock_fail(marker, str(state["error"]))
		plans[key] = [shader, code]
		counts_text.append("%s:%d" % [key, applied + 1])
	# Every substitution matched exactly once; now apply. The global sampler exists before any patched shader compiles.
	RenderingServer.global_shader_parameter_add("harness_rock_albedo", RenderingServer.GLOBAL_VAR_TYPE_SAMPLER2D, textures["albedo"])
	for key in plans:
		var shader: Shader = plans[key][0]
		shader.code = plans[key][1]
		_rock_shaders.append(shader)
	if not _bind_rock_detail_normal(marker, "install"):
		return {}
	rock_set_record = {"folder": folder, "id": str(parsed.get("id", "")), "uv_scale": uv_scale, "mean_linear": mean if mean != null else "unchanged",
		"size": (images["albedo"] as Image).get_width(), "albedo_sha256": FileAccess.get_sha256(folder.path_join("albedo.png")),
		"normal_sha256": FileAccess.get_sha256(folder.path_join("normal.png")), "rockset_sha256": FileAccess.get_sha256(json_path), "patched_shaders": counts_text}
	print("%s rock set: id=%s folder=%s size=%d uv_scale=%s mean=%s patches=%s" % [marker, rock_set_record["id"], folder, rock_set_record["size"], k,
		str(mean) if mean != null else "unchanged", ",".join(counts_text)])
	return rock_set_record


## Sets the rock detail normal global to the set's normal map. No readback: global_shader_parameter_get returns Nil for a sampler and logs an engine error outside the editor.
static func _bind_rock_detail_normal(marker: String, when: String) -> bool:
	RenderingServer.global_shader_parameter_set(ROCK_DETAIL_GLOBAL, _rock_textures[1])
	print("%s rock set: %s bound (%s); nothing else rebinds it (only project.godot sets the default)" % [marker, ROCK_DETAIL_GLOBAL, when])
	return true


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
	elif requested == "high":
		graphics.call("ApplyPreset", HIGH_PRESET, true, false)
		await COMMON.wait_builds_idle(tree, game, 300.0)
	elif requested != "keep":
		return {"error": "unknown --preset=%s (medium|high|keep)" % requested}
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
	# NT-4 study: sun_<n>_set is any setting-sun elevation in degrees (sun_5_set, sun_10_set, ...) without a fixture row each.
	if not times.has(time_name) and time_name.begins_with("sun_") and time_name.ends_with("_set") and time_name.trim_prefix("sun_").trim_suffix("_set").is_valid_float():
		times[time_name] = {"mode": "elevation", "value": float(time_name.trim_prefix("sun_").trim_suffix("_set")), "rising": false}
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
