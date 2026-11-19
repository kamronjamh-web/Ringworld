extends RefCounted

## Shared part bodies of the landscape T0-7 tools (docs/landscape-audit-t0-spec.md WO T0-7): look references, cost baseline and LOD-switch capture. TEST ONLY.
## Wrappers (tools/landscape_look_baseline.gd, landscape_cost_baseline.gd, landscape_lod_switch_capture.gd) call setup() then one run_*() and print the marker. Everything boots
## through tools/landscape_harness_common.gd (pinned v8 world by default, the user's preference files snapshotted, Cards grass checked). The hide cases (near terrain, far terrain,
## trees, grass) are copied and cut down from tools/terrain_render_attribution_test.gd (_capture_original_state, _apply_case, _restore_original_state, _verify_case and the
## draw-call/primitive delta check). Raster passes have no GPU markers (spec fact 6), so terrain/tree/grass cost is an A-B-A hide delta plus exact draw and primitive counts,
## with an optional opaque-pass bracket (tools/landscape_pass_timestamp_effect.gd). Machine rule: numbers are only authoritative on the A1000 at P0 / 35 W; anywhere else
## the output is stamped "non-authoritative" (COMMON.machine_info()).

const H := preload("res://tools/landscape_harness_common.gd")
const BANDS := preload("res://tools/landscape_audit_bands.gd")
const TIMING := preload("res://tools/LandscapeTimingBridge.cs")
const PASS_EFFECT := preload("res://tools/landscape_pass_timestamp_effect.gd")
const TREE_VIS := preload("res://tools/tree_visibility_world.gd")
const FEATURE_AB := preload("res://tools/LandscapeFeatureAbBridge.cs")
const SCATTER_DEBUG := preload("res://tools/ScatterDebugBridge.cs")
const COMMON := H.COMMON
const WARMUP_FRAMES := 60
const SETTLE_S := 1.0
const REFERENCE_SET := ["plains_eye__noon", "hills_eye__sun_8_set", "forest_edge_eye__noon", "coast_eye__noon", "hills_2km__noon"]
const COST_EXTRA := [["hills_eye", "sun_8_set"], ["forest_edge_eye", "sun_8_set"]]
## T5-10: scatter_hidden hides every renderer and shadow caster under the node named "ScatterLayer" (a sibling of the decoration layer). In a world without the layer it changes nothing and reads "(no change)".
const CASES := ["baseline", "near_terrain_hidden", "far_terrain_hidden", "trees_hidden", "grass_hidden", "scatter_hidden", "baseline_repeat"]
const HIDE_CLASSES := ["near_terrain_hidden", "far_terrain_hidden", "trees_hidden", "grass_hidden", "scatter_hidden"]
const MARKERS := ["GPU.TreeCulling", "GPU.GrassCulling", "GPU.Vegetation", "GPU.ProxyHeights", "GPU.HeightShadow", "GPU.WaterFft"]
const FILL_SCOPES := ["Trees build", "Trees disk load", "Grass build", "Scatter build O", "Scatter build B", "Scatter build C", "Habitat grid"]
## TR-1 (docs/work-orders/terrain-vegetation/TR.md): tree LOD slot cases. A slot case hides that slot's main-pass renderers only (its shadow casters stay); tree_shadows_off
## removes the tree shadow casters only. A "@rs50" suffix measures the same case at 50 % 3D render scale (fill test).
const TREE_LOD_CASES := ["baseline", "trees_hidden", "tree_slot0_hidden", "tree_slot1_hidden", "tree_slot2_hidden", "tree_slot3_hidden", "tree_slot4_hidden", "tree_shadows_off", "baseline_repeat"]
const TREE_LOD_POSES := ["forest_interior_eye", "forest_edge_eye", "forest_clearing_eye", "coast_eye"]
const TREE_LOD_EXTRA := [["forest_edge_eye", "sun_8_set"]]
const TREE_FADE_PROPERTIES := ["TreeNearDetailFadeStartMeters", "TreeNearDetailFadeEndMeters", "TreeBillboardFadeStartMeters", "TreeBillboardFadeEndMeters", "TreeMidDetailFadeStartMeters", "TreeMidDetailFadeEndMeters"]
const RS_SUFFIX := "@rs50"
const RS_SCALE := 0.5
const STRIP_SPECIES := ["Maple", "Pine", "Fir"]
## Search sites (around, across in m) for the strip trees, tried in order: places of the pinned v8 world where maple, pine or fir grow thinly enough for a 950 m tree-free
## sight line over flat ground (found with a headless lattice scan through FindStripTrees, 2026-11-14). The fixture poses have no fir, their forests are too dense for a
## clear line, and a tree on a mountainside is seen from far above or below. A species takes the first site whose tree is watched within STRIP_MAX_PITCH_DEG of level.
const STRIP_SITES := [[10422000.0, 240000.0], [18759000.0, 160000.0], [4070000.0, 240000.0], [11216000.0, 80000.0], [894000.0, 160000.0]]
const STRIP_MAX_PITCH_DEG := 8.0
const STRIP_TIGHT_PX := 192
const STRIP_TIGHT_SCALE := 3
const STRIP_WIDE := Vector2i(576, 324)
const LOD_STEPS := 180
const LOD_START_M := 15.0
const LOD_STEP_M := 0.25
const EYE_M := 1.7


# ------------------------------------------------------------------ args

## User arguments plus the words of the LANDSCAPE_HARNESS_ARGS environment variable (space separated). tools/run_tests.ps1 has no per-run argument pass-through, so the
## TR-1 modes are selected with e.g. $env:LANDSCAPE_HARNESS_ARGS = '--tree-lod=slots --strip'. Only the options read through these helpers see the variable.
static func harness_args() -> PackedStringArray:
	var args := OS.get_cmdline_user_args()
	for word in OS.get_environment("LANDSCAPE_HARNESS_ARGS").split(" ", false):
		args.append(word)
	return args


static func harness_arg(prefix: String, fallback: String) -> String:
	for arg in harness_args():
		if arg.begins_with(prefix):
			return arg.trim_prefix(prefix)
	return fallback


static func harness_flag(flag: String) -> bool:
	return flag in harness_args()


static func harness_csv(prefix: String) -> PackedStringArray:
	return harness_arg(prefix, "").split(",", false)


# ------------------------------------------------------------------ setup

## Boots the pinned world and records the run identity. On failure, the result lacks "ok" and the caller runs cleanup.
static func setup(tree: SceneTree, marker: String, weather_pose_report := "t0") -> Dictionary:
	if RenderingServer.get_rendering_device() == null:
		push_error("%s=FAIL RenderingDevice is required" % marker)
		return {}
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	Engine.max_fps = 0
	var seed_error := _seed_pose_error()
	if seed_error != "":
		push_error("%s=FAIL %s" % [marker, seed_error])
		return {}
	var res := COMMON.arg_value("--res=", "1920x1080").split("x")
	if res.size() == 2:
		tree.root.size = Vector2i(int(res[0]), int(res[1]))
	var machine: Dictionary = COMMON.machine_info()
	print("%s %s adapter=%s (%s)" % [marker, machine["label"], machine["adapter"], "4070, non-authoritative" if not bool(machine["authoritative"]) else "A1000, authoritative if P0/35W"])
	# T5-10 debug switches (tools/ScatterDebugBridge.cs), set before the world boots because the scatter streamer is created during boot: per-tile build timing (always on, a few bytes per tile)
	# and `--scatter-a5-density` (the design-A5 boulder weights, for the density comparison stills).
	var scatter_debug: RefCounted = SCATTER_DEBUG.new()
	scatter_debug.call("EnableTileTiming", true)
	var a5_density := harness_flag("--scatter-a5-density")
	scatter_debug.call("UseA5Density", a5_density)
	if a5_density:
		print("%s scatter A5 density: boulder term weights %s" % [marker, str(scatter_debug.call("BoulderWeights"))])
	# T5-10b: `--rock-debug-view=N` sets the rock shader's rock_debug_view (ring_scatter_rock.gdshader) before the world boots; the rock materials are built during boot.
	var rock_debug_view := int(harness_arg("--rock-debug-view=", "0"))
	scatter_debug.call("SetRockDebugView", rock_debug_view)
	var booted: Dictionary = await H.boot(tree, marker)
	if booted.is_empty():
		return {}
	var game: Node = booted["game"]
	var monitor := game.get_node_or_null("RingPerformanceMonitor")
	if monitor != null:
		monitor.process_mode = Node.PROCESS_MODE_DISABLED # it re-configures the timestamp bridge every frame and would drain our events
	COMMON.hold_world_clocks(game)
	# T4-2: a run that selects a dressing pose (--poses=steep_eye,...) pins the weather over the T0 poses plus every dressing pose, so the cloud time does not depend on which
	# dressing poses were picked. river_bank_eye is resolved from the booted world's river graph first (it only exists in a water.rivers review world).
	var weather_poses := H.report_poses(weather_pose_report) if H.seed == H.PINNED_SEED else []
	var river_bank_note := {}
	var explicit := _register_explicit_poses(booted)
	if explicit.has("error"):
		push_error("%s=FAIL %s" % [marker, str(explicit["error"])])
		return {"game": game, "fixture_root": booted["fixture_root"]}
	if weather_pose_report == "t0" and (_selects_dressing() or H.seed != H.PINNED_SEED):
		if harness_csv("--poses=").has(H.RIVER_BANK_POSE):
			river_bank_note = H.resolve_river_bank(booted)
			if river_bank_note.has("error"):
				push_error("%s=FAIL %s" % [marker, str(river_bank_note["error"])])
				return {"game": game, "fixture_root": booted["fixture_root"]}
		weather_poses.append_array(H.dressing_poses())
	var weather := H.pin_weather(game, weather_poses)
	if weather.has("error"):
		push_error("%s=FAIL %s" % [marker, str(weather["error"])])
		return {"game": game, "fixture_root": booted["fixture_root"]}
	print("%s weather=%s cloud_time=%.1f searched=%d" % [marker, weather["mode"], weather["cloud_time"], weather["searched"]])
	var world_environment := game.get_node_or_null("WorldEnvironment") as WorldEnvironment
	var explorer := game.get_node_or_null("Explorer")
	var surface := game.get_node_or_null("Terrain3DGameplay")
	var grass := game.get_node_or_null("StandingGrass")
	var trees := game.get_node_or_null("BiomeDecorations")
	var scaled_far := game.get_node_or_null("ScaledFarViewport")
	var sun := game.get_node_or_null("StarLight") as DirectionalLight3D
	if world_environment == null or explorer == null or surface == null or grass == null or trees == null or scaled_far == null or sun == null:
		push_error("%s=FAIL required gameplay nodes are missing" % marker)
		return {"game": game, "fixture_root": booted["fixture_root"]}
	var preset: Dictionary = await H.apply_preset(tree, game)
	if preset.has("error"):
		push_error("%s=FAIL %s" % [marker, str(preset["error"])])
		return {"game": game, "fixture_root": booted["fixture_root"]}
	var ctx := {"tree": tree, "marker": marker, "booted": booted, "game": game, "fixture_root": booted["fixture_root"], "machine": machine, "world_environment": world_environment,
		"explorer": explorer, "surface": surface, "grass": grass, "trees": trees, "scaled_far": scaled_far, "sun": sun, "preset": preset, "weather": weather, "ok": true}
	ctx["ground_model"] = null if str(booted["world_tag"]).is_empty() else booted["world_bridge"]
	ctx["record"] = _common_record(ctx)
	# T5-10b: `--shadow-debug-mode=N` sets ringworld_shadow_debug_mode (1 sun visibility, 2 geometry, 3 contact, 4 terrain, 5 cloud, 6 storm) for the whole run; with `--rock-debug-view=13` a rock shows the term itself.
	# T5-10b: `--env-off=ssao,ssil,ssr,sdfgi,glow,volumetric_fog,adjustment` clears those WorldEnvironment flags for the whole run (A/B of screen-space terms on the rocks).
	for env_flag in harness_csv("--env-off="):
		if world_environment.environment != null:
			world_environment.environment.set(env_flag + "_enabled", false)
	# T5-10b: `--hs-receivers-off` turns the terrain height-shadow receivers off for the whole run (A/B).
	if harness_flag("--hs-receivers-off"):
		var height_shadow := game.get_node_or_null("TerrainHeightShadow")
		if height_shadow != null:
			height_shadow.set("ReceiversEnabled", false)
	var shadow_debug_mode := int(harness_arg("--shadow-debug-mode=", "0"))
	if shadow_debug_mode != 0:
		RenderingServer.global_shader_parameter_set("ringworld_shadow_debug_mode", shadow_debug_mode)
	if a5_density:
		ctx["record"]["scatter_a5_density"] = true
	if H.seed != H.PINNED_SEED:
		ctx["record"]["seed"] = H.seed
		ctx["record"]["explicit_poses"] = explicit.get("poses", [])
	if _selects_dressing() and H.seed == H.PINNED_SEED:
		ctx["record"]["dressing_fixture"] = {"path": H.DRESSING_FIXTURE, "weather_pinned_over": weather_poses.size(), "river_bank_eye": river_bank_note}
	# `--quiet` hides the clouds, grass and gust debris (tools/tree_visibility_world.gd quiet_scene) so two stills of one pose can be compared pixel for pixel. Off by default:
	# the clouds and grass are part of the cost a normal run measures.
	# `--feature-off=<feature id | bit:N>` (look and cost runs): the whole run happens with that world feature's bit cleared in the shader mask (same world, same boot), so a look
	# capture "feature off" needs no second world. The mask is recorded; the hook is tools-only (tools/LandscapeFeatureAbBridge.cs).
	var off_spec := harness_arg("--feature-off=", "")
	if off_spec != "":
		var off_hook: RefCounted = FEATURE_AB.new()
		var off_resolved: Dictionary = off_hook.call("Resolve", off_spec)
		if not bool(off_resolved.get("ok", false)) or (int(off_hook.call("WorldMask")) >> int(off_resolved["bit"])) & 1 == 0:
			var why := str(off_resolved.get("message", "")) if not bool(off_resolved.get("ok", false)) else "%s is not on in this world (mask 0x%x); boot a review world with the feature" % [off_spec, int(off_hook.call("WorldMask"))]
			push_error("%s=FAIL --feature-off: %s" % [marker, why])
			return {"game": game, "fixture_root": booted["fixture_root"]}
		var off_mask := int(off_hook.call("Apply", int(off_resolved["bit"]), false))
		ctx["feature_off_hook"] = off_hook
		ctx["record"]["feature_off"] = {"spec": off_spec, "bit": int(off_resolved["bit"]), "world_mask": int(off_hook.call("WorldMask")), "published_mask": off_mask, "shader_readback": int(off_hook.call("ShaderMask"))}
		print("%s feature off: %s published mask 0x%x (world mask 0x%x), hook last published 0x%x" % [marker, off_spec, off_mask, int(off_hook.call("WorldMask")), int(off_hook.call("ShaderMask"))])
	# NT-4 study: `--t3d-shadows-off` stops the native Terrain3D clipmap casting sun shadows (RingTerrain3DSurface.CastSunShadows) for the whole run; the Lod1 proxy casters remain.
	if harness_flag("--t3d-shadows-off"):
		var t3d_surface := game.get_node_or_null("Terrain3DGameplay/Terrain3DSurface")
		if t3d_surface == null:
			push_error("%s=FAIL --t3d-shadows-off: Terrain3DGameplay/Terrain3DSurface is missing" % marker)
			return {"game": game, "fixture_root": booted["fixture_root"]}
		t3d_surface.set("CastSunShadows", false)
		ctx["record"]["t3d_shadows_off"] = true
	if harness_flag("--quiet"):
		ctx["quiet"] = TREE_VIS.quiet_scene(game)
		ctx["record"]["quiet_scene"] = ctx["quiet"]
		print("%s quiet scene: %s" % [marker, JSON.stringify(ctx["quiet"])])
	return ctx


## Pose names of the seed-1337 fixtures (T0 and dressing). With another --seed they are meaningless, so naming one fails the run.
static func _fixture_pose_names() -> PackedStringArray:
	var names := PackedStringArray()
	var prior := H.seed
	H.seed = H.PINNED_SEED
	for pose in H.load_poses().get("poses", []):
		names.append(str(pose["name"]))
	for pose in H.dressing_poses():
		if str(pose["name"]) != H.RIVER_BANK_POSE:
			names.append(str(pose["name"]))
	H.seed = prior
	return names


## `--seed=<n>` (n != 1337): the fixtures do not apply, so poses come from `--at=name:around,across,lift_m,yaw,pitch[;name2:...]` (lift is metres above the ground) or river_bank_eye.
## "" when fine, else the reason.
static func _seed_pose_error() -> String:
	var seed_text := harness_arg("--seed=", "")
	if seed_text == "" or int(seed_text) == H.PINNED_SEED:
		return ""
	var fixture := _fixture_pose_names()
	var names := harness_csv("--poses=")
	for name in names:
		if fixture.has(name):
			return "pose %s is a seed 1337 fixture pose and does not apply to --seed=%s; give explicit coordinates with --at=name:around,across,lift_m,yaw,pitch" % [name, seed_text]
	if harness_arg("--at=", "") == "" and not names.has(H.RIVER_BANK_POSE):
		return "--seed=%s has no fixture poses; pass --at=name:around,across,lift_m,yaw,pitch (several with ;) or --poses=river_bank_eye" % seed_text
	return ""


## Parses `--at=` into pose rows (vertical_mode agl) and registers them like river_bank_eye so `--poses=` and the default selection find them. Allowed with any seed.
static func _register_explicit_poses(booted: Dictionary) -> Dictionary:
	var text := harness_arg("--at=", "")
	if text == "":
		return {"poses": []}
	var rows: Array = []
	for item in text.split(";", false):
		var parts := item.split(":")
		var numbers := (parts[1] if parts.size() == 2 else "").split(",")
		if parts.size() != 2 or numbers.size() != 5 or not parts[0].is_valid_identifier():
			return {"error": "--at entry '%s' is not name:around,across,lift_m,yaw,pitch" % item}
		var around := float(numbers[0])
		var across := float(numbers[1])
		var ground := float((booted["world_bridge"] as RefCounted).call("GroundHeight", around, across))
		var pose := {"name": parts[0], "reports": ["at"], "class": "Explicit", "habitat": "None", "around_m": around, "across_m": across, "ground_m": ground, "vertical_mode": "agl",
			"lift_m": float(numbers[2]), "yaw_deg": float(numbers[3]), "pitch_deg": float(numbers[4]), "notes": "explicit --at pose"}
		H._runtime_poses[parts[0]] = pose
		rows.append(pose)
	return {"poses": rows}


static func _selects_dressing() -> bool:
	var wanted := H.dressing_pose_names()
	for name in harness_csv("--poses="):
		if wanted.has(name):
			return true
	return false


static func cleanup(ctx: Dictionary) -> void:
	if ctx.has("feature_off_hook"):
		var restored := int((ctx["feature_off_hook"] as RefCounted).call("Restore"))
		print("%s feature mask restored to 0x%x (world mask 0x%x)" % [ctx.get("marker", "LANDSCAPE"), restored, int((ctx["feature_off_hook"] as RefCounted).call("WorldMask"))])
	var timing: Variant = ctx.get("timing", null)
	if timing != null:
		(timing as RefCounted).call("End")
	var pid := int(ctx.get("power_pid", -1))
	if pid > 0:
		COMMON.stop_power_log(pid)
	if ctx.has("game") and is_instance_valid(ctx["game"]):
		(ctx["game"] as Node).queue_free()
	if ctx.has("fixture_root"):
		H.remove_dir_recursive(ProjectSettings.globalize_path(str(ctx["fixture_root"])))


static func _common_record(ctx: Dictionary) -> Dictionary:
	var trees: Node = ctx["trees"]
	var booted: Dictionary = ctx["booted"]
	var snapshot_dir := ProjectSettings.globalize_path(str(booted["fixture_root"]).path_join("prefs_snapshot"))
	var files := {}
	var dir := DirAccess.open(snapshot_dir)
	if dir != null:
		for file in dir.get_files():
			files[file] = FileAccess.get_sha256(snapshot_dir.path_join(file))
	var prefs: Dictionary = booted["prefs"]
	var atmosphere_hash := str(files.get("atmosphere_visual_defaults.tres", "absent (shipped defaults)"))
	var root_window: Window = (ctx["tree"] as SceneTree).root
	var record := {"machine": ctx["machine"], "resolution": [root_window.size.x, root_window.size.y], "vsync": "off", "max_fps": 0, "godot": Engine.get_version_info().get("string", ""),
		"preset": ctx["preset"], "grass_render_style": ctx["preset"].get("grass_render_style_name", "?"),
		"tree_fade": {"compact_profile": trees.get("UseCompactTreeLodProfile"), "near_start_m": trees.get("TreeNearDetailFadeStartMeters"), "near_end_m": trees.get("TreeNearDetailFadeEndMeters"),
			"billboard_start_m": trees.get("TreeBillboardFadeStartMeters"), "billboard_end_m": trees.get("TreeBillboardFadeEndMeters")},
		"prefs": {"mode": prefs.get("mode", "?"), "used": prefs.get("used", []), "absent": prefs.get("absent", []), "sha256": files, "atmosphere_settings_sha256": atmosphere_hash},
		"world": booted["world"], "clocks_held": true, "weather": ctx["weather"], "auto_exposure_disabled_for_look": true}
	record.merge(booted["world_record"])
	return record


static func t0_poses() -> Array:
	var names := harness_csv("--poses=")
	var result: Array = []
	if H.seed == H.PINNED_SEED:
		for pose in H.report_poses("t0"):
			if names.is_empty() or str(pose["name"]) in names:
				result.append(pose)
	# T4-2: dressing poses are only ever selected by name, so a run without --poses= is exactly the nine T0 poses as before.
	# With another --seed the list holds only the run-time poses (--at= explicit ones, river_bank_eye); explicit ones are all used when --poses= is absent.
	for pose in H.dressing_poses():
		if str(pose["name"]) in names or (H.seed != H.PINNED_SEED and names.is_empty() and str(pose["reports"][0]) == "at"):
			result.append(pose)
	return result


static func _save_json(path: String, data: Variant) -> void:
	var file := FileAccess.open(path, FileAccess.WRITE)
	if file != null:
		file.store_string(JSON.stringify(data, "\t"))
		file.close()


static func _disable_auto_exposure(ctx: Dictionary) -> void:
	var attributes := (ctx["world_environment"] as WorldEnvironment).camera_attributes as CameraAttributesPractical
	if attributes != null and attributes.auto_exposure_enabled:
		attributes.auto_exposure_enabled = false
		await COMMON.settle(ctx["tree"], 0.5)


# ------------------------------------------------------------------ look part

## Nine t0 poses at noon and sun_8_set: <pose>__<time>.png, _bands.png, look.json, reference_set.json in audit_baseline/<machine>/t0/look/<stamp>/.
static func run_look(ctx: Dictionary) -> Dictionary:
	var tree: SceneTree = ctx["tree"]
	var marker: String = ctx["marker"]
	var machine: Dictionary = ctx["machine"]
	var out_dir := H.baseline_dir(str(machine["tag"]), "t0", "look", ctx["booted"]["world_tag"])
	H.copy_prefs_snapshot(ctx["booted"], out_dir)
	var depth_effect := H.install_depth_effect(ctx["world_environment"])
	var times := harness_csv("--times=")
	if times.is_empty():
		times = PackedStringArray(["noon", "sun_8_set"])
	var views: Array = []
	var failures: Array = []
	var first := true
	var variants: Array = [""]
	if harness_flag("--t3d-shadow-ab"):
		variants = ["on", "off", "on2"]
	var t3d_surface_ab := (ctx["game"] as Node).get_node_or_null("Terrain3DGameplay/Terrain3DSurface")
	for variant in variants:
		if variant != "" and t3d_surface_ab != null:
			t3d_surface_ab.set("CastSunShadows", variant != "off")
			await COMMON.settle(tree, 1.0)
		for pose in t0_poses():
			for time_name in times:
				var info: Dictionary = await H.go_pose(tree, ctx["game"], pose, time_name, ctx["ground_model"])
				# T5-10: the scatter jobs and source rebuild follow the tree tiles; wait for them before the still (no-op without scatter.rocks).
				if not info.has("error") and bool((ctx["trees"] as Node).get("HasScatterStreamer")):
					info["scatter_idle"] = await wait_scatter_idle(ctx, 180.0)
					await COMMON.settle(tree, 2.0)
				# T6-4b: `--understory-hidden` takes every renderer under the "UnderstoryLayer" node out of the still (the hidden half of the hidden / shown pair; no change in a world without the layer).
				if harness_flag("--understory-hidden") and _understory_layer(ctx) != null:
					await COMMON.settle(tree, 2.0)
					for node in _geometry_nodes(_understory_layer(ctx)):
						node.layers = 0
					await COMMON.settle(tree, 0.5)
				if first and not info.has("error"):
					await _disable_auto_exposure(ctx)
					first = false
				var view_name: String = ("%s__%s" % [pose["name"], time_name]) + ("" if variant == "" else "__" + variant)
				var weather := H.weather_at(ctx["game"], float(pose["around_m"]), float(pose["across_m"]))
				var view := {"view": view_name, "pose": pose["name"], "time": time_name, "go_pose": info, "weather": weather}
				if info.has("error"):
					view["failure"] = str(info["error"])
					failures.append("%s: %s" % [view_name, view["failure"]])
					views.append(view)
					continue
				var shot: Dictionary = await H.capture(tree, depth_effect)
				var image: Image = shot["image"]
				var w := image.get_width()
				var h := image.get_height()
				var distance := BANDS.distance_from_depth(shot["depth"], w, h)
				var table := BANDS.compute(image, distance, w, h, 2)
				image.save_png(out_dir.path_join(view_name + ".png"))
				BANDS.band_image(distance, w, h).save_png(out_dir.path_join(view_name + "_bands.png"))
				var total := 0.0
				var weight := 0.0
				for row in table:
					if int(row["pixels"]) > 0:
						total += float(row["mean_luma"]) * float(row["share"])
						weight += float(row["share"])
				var mean_luma := total / maxf(weight, 0.000001)
				if bool((ctx["trees"] as Node).get("HasScatterStreamer")):
					view["scatter"] = await scatter_snapshot(ctx)
				var idle := bool(info.get("streaming_idle", false)) and bool(info.get("streaming_idle_after", false))
				view.merge({"size": [w, h], "depth_available": not (shot["depth"] as Dictionary).is_empty(), "band_table": table, "adjacent_steps": BANDS.adjacent_steps(table),
					"non_empty_bands": BANDS.non_empty_bands(table), "mean_luma": mean_luma, "sun_elevation_deg": info.get("sun_elevation_deg", NAN), "streaming_idle": idle})
				var why := ""
				if weather.has("error"):
					why = str(weather["error"])
				elif ctx["weather"]["mode"] == "clear" and float(weather["precipitation"]) > H.CLEAR_PRECIPITATION_MAX:
					why = "weather not clear"
				elif mean_luma <= 0.01:
					why = "view is black (mean linear luma %.4f)" % mean_luma
				elif time_name == "sun_8_set" and absf(float(info.get("sun_elevation_deg", NAN)) - 8.0) > 0.5:
					why = "sun elevation %.2f is not within 0.5 of 8" % float(info.get("sun_elevation_deg", NAN))
				elif not idle:
					why = "streaming not idle (before=%s after=%s)" % [str(info.get("streaming_idle")), str(info.get("streaming_idle_after"))]
				if why != "":
					view["failure"] = why
					failures.append("%s: %s" % [view_name, why])
				views.append(view)
				print("%s view %s luma=%.3f sun=%.2f bands=%d idle=%s %s" % [marker, view_name, mean_luma, float(info.get("sun_elevation_deg", NAN)), BANDS.non_empty_bands(table), str(idle), why])
	var references: Array = []
	for name in REFERENCE_SET:
		for view in views:
			if str(view["view"]) == name:
				references.append({"view": name, "png": name + ".png", "bands_png": name + "_bands.png", "use": "compare reference (machine-specific): tools/media/ffmpeg_tools.ps1 compare -Path <new>.png -Reference <this>.png"})
	var report: Dictionary = (ctx["record"] as Dictionary).duplicate(true)
	report.merge({"marker": marker, "tool": "landscape_look_baseline", "views": views, "failures": failures, "status": "FAIL" if not failures.is_empty() or views.is_empty() else "PASS",
		"output": out_dir})
	_save_json(out_dir.path_join("look.json"), report)
	var reference_set := {"machine": machine["tag"], "adapter": machine["adapter"], "authoritative": machine["authoritative"], "preset": ctx["preset"].get("preset", "?"),
		"prefs_mode": ctx["booted"]["prefs"].get("mode", "?"), "weather": ctx["weather"], "references": references}
	reference_set.merge(ctx["booted"]["world_record"])
	_save_json(out_dir.path_join("reference_set.json"), reference_set)
	print("%s output: %s" % [marker, out_dir])
	return report


# ------------------------------------------------------------------ hide cases (copied and cut down from terrain_render_attribution_test.gd)

static func _all_nodes(node: Node) -> Array[Node]:
	var result: Array[Node] = [node]
	for child in node.get_children():
		result.append_array(_all_nodes(child))
	return result


## The scatter layer node ("ScatterLayer", a sibling of the decoration layer), or null in a world without scatter.rocks.
static func _scatter_layer(ctx: Dictionary) -> Node:
	var parent: Node = (ctx["trees"] as Node).get_parent()
	return parent.get_node_or_null("ScatterLayer") if parent != null else null


## T6-4b: the understory layer node ("UnderstoryLayer", a sibling of the decoration layer), or null in a world without vegetation.forest_structure (bit 6).
static func _understory_layer(ctx: Dictionary) -> Node:
	var parent: Node = (ctx["trees"] as Node).get_parent()
	return parent.get_node_or_null("UnderstoryLayer") if parent != null else null


static func _geometry_nodes(owner: Node) -> Array:
	var result: Array = []
	for node in _all_nodes(owner):
		if node is GeometryInstance3D:
			result.append(node)
	return result


## Re-scans the hideable nodes (streaming creates and frees geometry, so this runs per pose-time after the fill).
static func capture_original_state(ctx: Dictionary) -> Dictionary:
	var game: Node = ctx["game"]
	var surface: Node3D = ctx["surface"]
	var near_nodes: Array = [surface]
	for node in _all_nodes(game):
		if node is Node3D and node.get_script() != null and "RingTerrainProxySector" in (node.get_script() as Script).resource_path:
			near_nodes.append(node)
	# Far terrain = the far viewport's terrain meshes (ring_terrain_global shader, plus the older ScaledSeededRingSurface / ScaledFarLod* names the attribution test knew) and the main
	# viewport's global terrain (GlobalTerrainLod3, AnalyticGlobalRing). Far ocean, the structural hull and the cloud meshes stay on so clouds and water keep their cost.
	var far_nodes: Array = []
	for node in _all_nodes(ctx["scaled_far"]):
		if node is MeshInstance3D and (node.name == "ScaledSeededRingSurface" or node.name.begins_with("ScaledFarLod") or _shader_name(node) in ["ring_terrain_global.gdshader", "ring_terrain_global_ground.gdshader"]):
			far_nodes.append(node)
	for global_name in ["GlobalTerrainLod3", "AnalyticGlobalRing"]:
		var global_node := game.get_node_or_null(global_name) as Node3D
		if global_node != null:
			far_nodes.append(global_node)
	var far_meshes: Array = []
	for node in _all_nodes(ctx["scaled_far"]):
		if node is MeshInstance3D:
			far_meshes.append("%s (%s, visible=%s, shader=%s)" % [node.name, node.get_parent().name, str((node as Node3D).visible), _shader_name(node)])
	var state := {"far_mesh_names": far_meshes, "near_nodes": near_nodes, "near_visible": _visibilities(near_nodes), "far_nodes": far_nodes, "far_visible": _visibilities(far_nodes),
		"grass_root": ctx["grass"], "grass_visible": (ctx["grass"] as Node3D).visible, "trees_root": ctx["trees"], "trees_visible": (ctx["trees"] as Node3D).visible}
	state["grass_geometry"] = _geometry_nodes(ctx["grass"])
	state["grass_layers"] = _layers(state["grass_geometry"])
	state["tree_geometry"] = _geometry_nodes(ctx["trees"])
	state["tree_layers"] = _layers(state["tree_geometry"])
	# T5-10: the scatter layer is a sibling of the decoration layer (never a descendant, so it is not counted as tree geometry).
	var scatter_layer := _scatter_layer(ctx)
	state["scatter_geometry"] = _geometry_nodes(scatter_layer) if scatter_layer != null else []
	state["scatter_layers"] = _layers(state["scatter_geometry"])
	# T6-4b: the understory layer, the same way (understory_hidden and understory_off are extra cases).
	var understory_layer := _understory_layer(ctx)
	state["understory_geometry"] = _geometry_nodes(understory_layer) if understory_layer != null else []
	state["understory_layers"] = _layers(state["understory_geometry"])
	# TR-1: the GPU tree outputs are named GpuTree_<around>_<across>_<variant>_L<slot>; a shadow proxy is the child <output name>_ShadowLod<n> (RingBiomeDecorationLayer.GpuShadows.cs).
	var slot_pattern := RegEx.create_from_string("GpuTree_.*_L(\\d)")
	# The layer's own map (tools/RingBiomeDecorationLayer.TreeBudgetProbe.cs) wins over the names: an output recreated while its predecessor is still in the tree
	# gets a generated "@MultiMeshInstance3D@N" name.
	var by_id := {}
	if (ctx["trees"] as Node).has_method("TreeBudgetProbeOutputSlots"):
		by_id = (ctx["trees"] as Node).call("TreeBudgetProbeOutputSlots")
	var slots: Array = []
	var proxies: Array = []
	var casts: Array = []
	var unclassified: Array = []
	for node in state["tree_geometry"]:
		var code := int(by_id.get(node.get_instance_id(), -1))
		if code >= 0:
			slots.append(code % 16)
			proxies.append(code >= 16 and code < 32)
		else:
			var found := slot_pattern.search(str(node.name))
			slots.append(int(found.get_string(1)) if found != null else -1)
			proxies.append("_ShadowLod" in str(node.name))
			if found == null and node.visible:
				unclassified.append(str(node.name))
		casts.append(int(node.cast_shadow))
	state["tree_unclassified_visible"] = unclassified
	state["tree_slot"] = slots
	state["tree_proxy"] = proxies
	state["tree_cast"] = casts
	state["tree_cast_touched"] = {}
	state["game"] = ctx["game"]
	state["world_environment"] = ctx["world_environment"]
	state["scaled_far"] = ctx["scaled_far"]
	state["extra_undo"] = []
	state["feature_ab"] = ctx.get("feature_ab", {})
	state["viewport"] = (ctx["tree"] as SceneTree).root
	state["scale"] = (ctx["tree"] as SceneTree).root.scaling_3d_scale
	return state


## [case label without the render-scale suffix, render scale or -1.0].
static func _case_parts(label: String) -> Array:
	if label.ends_with(RS_SUFFIX):
		return [label.trim_suffix(RS_SUFFIX), RS_SCALE]
	return [label, -1.0]


static func _known_case(base: String) -> bool:
	if base in EXTRA_CASES:
		return true
	if base in ["baseline", "baseline_repeat", "near_terrain_hidden", "far_terrain_hidden", "trees_hidden", "grass_hidden", "scatter_hidden", "tree_shadows_off"]:
		return true
	return base.begins_with("tree_slot") and base.ends_with("_hidden") and base.trim_prefix("tree_slot").trim_suffix("_hidden").is_valid_int()


## Expected [layers, cast_shadow] of tree geometry node `index` under case `base`. A slot case removes the slot's renderers from the main pass and keeps their shadows;
## tree_shadows_off removes the shadow casters (the shadows-only proxies, and the cast of any renderer that still casts for itself).
static func _tree_expected(state: Dictionary, index: int, base: String) -> Array:
	var layers := int(state["tree_layers"][index])
	var cast := int(state["tree_cast"][index])
	var slot := int(state["tree_slot"][index])
	var proxy := bool(state["tree_proxy"][index])
	if base == "trees_hidden":
		layers = 0
	elif base == "tree_shadows_off":
		if proxy or cast == GeometryInstance3D.SHADOW_CASTING_SETTING_SHADOWS_ONLY:
			layers = 0
		elif cast == GeometryInstance3D.SHADOW_CASTING_SETTING_ON:
			cast = GeometryInstance3D.SHADOW_CASTING_SETTING_OFF
	elif slot >= 0 and not proxy and base == "tree_slot%d_hidden" % slot:
		if cast == GeometryInstance3D.SHADOW_CASTING_SETTING_ON:
			cast = GeometryInstance3D.SHADOW_CASTING_SETTING_SHADOWS_ONLY
		elif cast != GeometryInstance3D.SHADOW_CASTING_SETTING_SHADOWS_ONLY:
			layers = 0
	return [layers, cast]


static func _shader_name(node: Node) -> String:
	var instance := node as MeshInstance3D
	if instance == null:
		return "-"
	var material: Material = instance.material_override
	if material == null and instance.mesh != null and instance.mesh.get_surface_count() > 0:
		material = instance.mesh.surface_get_material(0)
	if material is ShaderMaterial and (material as ShaderMaterial).shader != null:
		return (material as ShaderMaterial).shader.resource_path.get_file()
	return "-"


static func _visibilities(nodes: Array) -> Array:
	var values: Array = []
	for node in nodes:
		values.append(node.visible)
	return values


static func _layers(nodes: Array) -> Array:
	var values: Array = []
	for node in nodes:
		values.append(node.layers)
	return values


static func restore_state(state: Dictionary) -> void:
	if state.has("extra_undo"):
		var undo_list: Array = state["extra_undo"]
		while not undo_list.is_empty():
			(undo_list.pop_back() as Callable).call()
	for index in state["near_nodes"].size():
		if is_instance_valid(state["near_nodes"][index]):
			state["near_nodes"][index].visible = state["near_visible"][index]
	for index in state["far_nodes"].size():
		if is_instance_valid(state["far_nodes"][index]):
			state["far_nodes"][index].visible = state["far_visible"][index]
	if is_instance_valid(state["grass_root"]):
		state["grass_root"].visible = state["grass_visible"]
	if is_instance_valid(state["trees_root"]):
		state["trees_root"].visible = state["trees_visible"]
	for key in [["grass_geometry", "grass_layers"], ["tree_geometry", "tree_layers"], ["scatter_geometry", "scatter_layers"], ["understory_geometry", "understory_layers"]]:
		var nodes: Array = state.get(key[0], [])
		for index in nodes.size():
			if is_instance_valid(nodes[index]):
				nodes[index].layers = state[key[1]][index]
	if state.has("tree_cast_touched"):
		for index in state["tree_cast_touched"]:
			var node: Variant = state["tree_geometry"][index]
			if is_instance_valid(node):
				node.cast_shadow = int(state["tree_cast"][index])
		state["tree_cast_touched"] = {}
	if state.has("viewport") and is_instance_valid(state["viewport"]) and not is_equal_approx((state["viewport"] as Viewport).scaling_3d_scale, float(state["scale"])):
		(state["viewport"] as Viewport).scaling_3d_scale = float(state["scale"])


static func apply_case(state: Dictionary, label: String) -> bool:
	var parts := _case_parts(label)
	var base: String = parts[0]
	if not _known_case(base):
		return false
	if float(parts[1]) > 0.0:
		if not state.has("viewport"):
			return false
		(state["viewport"] as Viewport).scaling_3d_scale = float(parts[1])
	if base in EXTRA_CASES:
		return _apply_extra(state, base)
	match base:
		"baseline", "baseline_repeat":
			pass
		"near_terrain_hidden":
			for node in state["near_nodes"]:
				node.visible = false
		"far_terrain_hidden":
			for node in state["far_nodes"]:
				node.visible = false
		"grass_hidden":
			for node in state["grass_geometry"]:
				if is_instance_valid(node):
					node.layers = 0
		"scatter_hidden":
			for node in state["scatter_geometry"]:
				if is_instance_valid(node):
					node.layers = 0
		_:
			# trees_hidden, tree_slot<N>_hidden, tree_shadows_off
			for index in state["tree_geometry"].size():
				var node: Variant = state["tree_geometry"][index]
				if not is_instance_valid(node):
					continue
				var expected := _tree_expected(state, index, base)
				node.layers = int(expected[0])
				if int(expected[1]) != int(state["tree_cast"][index]):
					node.cast_shadow = int(expected[1])
					state["tree_cast_touched"][index] = true
	return true


## "" when the case's state is applied exactly (and nothing else is hidden), else the reason. Freed geometry nodes (streaming) are skipped.
static func verify_case(state: Dictionary, full_label: String) -> String:
	var parts := _case_parts(full_label)
	var label: String = parts[0]
	if state.has("viewport"):
		var wanted_scale := float(parts[1]) if float(parts[1]) > 0.0 else float(state["scale"])
		if not is_equal_approx((state["viewport"] as Viewport).scaling_3d_scale, wanted_scale):
			return "render scale is %.3f, not %.3f, for %s" % [(state["viewport"] as Viewport).scaling_3d_scale, wanted_scale, full_label]
	for index in state["near_nodes"].size():
		var node: Node3D = state["near_nodes"][index]
		if is_instance_valid(node) and node.visible != (false if label == "near_terrain_hidden" else bool(state["near_visible"][index])):
			return "near/proxy terrain visibility was not applied for " + label
	for index in state["far_nodes"].size():
		var node: Node3D = state["far_nodes"][index]
		if is_instance_valid(node) and node.visible != (false if label == "far_terrain_hidden" else bool(state["far_visible"][index])):
			return "far terrain visibility was not applied for " + label
	if is_instance_valid(state["grass_root"]) and state["grass_root"].visible != bool(state["grass_visible"]):
		return "grass visibility changed for " + label
	if is_instance_valid(state["trees_root"]) and state["trees_root"].visible != bool(state["trees_visible"]):
		return "tree visibility changed for " + label
	for index in state["grass_geometry"].size():
		var node: Node = state["grass_geometry"][index]
		if is_instance_valid(node) and node.layers != (0 if label == "grass_hidden" else int(state["grass_layers"][index])):
			return "grass render layer was not applied for " + label
	for index in state["scatter_geometry"].size():
		var node: Node = state["scatter_geometry"][index]
		if is_instance_valid(node) and node.layers != (0 if label in ["scatter_hidden", "scatter_off"] else int(state["scatter_layers"][index])):
			return "scatter render layer was not applied for " + label
	var understory_nodes: Array = state.get("understory_geometry", [])
	for index in understory_nodes.size():
		var node: Node = understory_nodes[index]
		if is_instance_valid(node) and node.layers != (0 if label in ["understory_hidden", "understory_off"] else int(state["understory_layers"][index])):
			return "understory render layer was not applied for " + label
	for index in state["tree_geometry"].size():
		var node: Variant = state["tree_geometry"][index]
		if not is_instance_valid(node):
			continue
		var expected := _tree_expected(state, index, label)
		if node.layers != int(expected[0]):
			return "tree render layer was not applied for " + label
		if int(expected[1]) != int(state["tree_cast"][index]) and int(node.cast_shadow) != int(expected[1]):
			return "tree shadow casting was not applied for " + label
	var ab: Dictionary = state.get("feature_ab", {})
	if not ab.is_empty():
		# The shader mask must read back as the world mask, with the feature bit cleared only in the feature_off case.
		var world_mask := int((ab["hook"] as RefCounted).call("WorldMask"))
		var wanted := (world_mask & ~(1 << int(ab["bit"]))) if label == "feature_off" else world_mask
		var seen := int((ab["hook"] as RefCounted).call("ShaderMask"))
		if seen >= 0 and seen != wanted:
			return "shader feature mask reads 0x%x, expected 0x%x for %s" % [seen, wanted, label]
	return ""



# ------------------------------------------------------------------ perf pass P0 extra cases (docs/perf-pass-2026-11.md)

## Extra A-B-A cases for run_cost, selected with --extra-cases=a,b (or LANDSCAPE_HARNESS_ARGS). Each uses an existing runtime switch or a node property and pushes its undo
## on state["extra_undo"]; restore_state runs the undos. TEST ONLY.
## T4.md C3: a 4070 feature delta above this at any pose is flagged to the primary (the A1000 numbers are the authoritative ones).
const FEATURE_AB_FLAG_MS := 0.1
## T5-10: scatter_off = scatter_hidden plus the layer's _Process stopped (no cull request, so no compute dispatch); (scatter_hidden delta) - (scatter_off delta) is the layer's per-frame compute and CPU-side cost.
## T6-4b: understory_hidden hides every renderer and shadow caster under the node named "UnderstoryLayer" (a sibling of the decoration layer; "(no change)" in a world without it); understory_off is that plus the
## layer's _Process stopped (no cull request, so no compute dispatch). Select them with --extra-cases=understory_hidden,understory_off.
const EXTRA_CASES := ["feature_off", "scatter_off", "understory_hidden", "understory_off", "clouds_off", "cloud_shadow_gen_off", "sun_map_off", "hs_receivers_off", "water_hidden", "ui_hidden", "clocks_running", "far_viewport_off", "env_post_off", "env_ssao_off", "env_ssr_off", "atlas_4096", "atlas_8192", "atlas_16384"]
const ENV_POST_FLAGS := ["glow_enabled", "ssao_enabled", "ssil_enabled", "ssr_enabled", "sdfgi_enabled", "volumetric_fog_enabled", "adjustment_enabled"]


static func _apply_extra(state: Dictionary, base: String) -> bool:
	var game: Node = state["game"]
	var undo: Array = state["extra_undo"]
	match base:
		"feature_off":
			# T4-2 test-only hook (tools/LandscapeFeatureAbBridge.cs): republish the shader mask with the --feature-ab bit cleared. A placement feature (T5, T6) must also rebuild its
			# tiles and wait for streaming idle here; only the shader-side part exists today.
			var ab: Dictionary = state.get("feature_ab", {})
			if ab.is_empty():
				return false
			var hook: RefCounted = ab["hook"]
			var cleared := int(hook.call("Apply", int(ab["bit"]), false))
			if cleared < 0:
				return false
			state["feature_off_mask"] = cleared
			undo.append(func() -> void:
				hook.call("Restore"))
		"scatter_off":
			var layer := _scatter_layer({"trees": state["trees_root"]})
			if layer == null:
				return false
			for node in state["scatter_geometry"]:
				if is_instance_valid(node):
					node.layers = 0
			var previous_mode: int = layer.process_mode
			layer.process_mode = Node.PROCESS_MODE_DISABLED
			undo.append(func() -> void:
				if is_instance_valid(layer):
					layer.process_mode = previous_mode as Node.ProcessMode)
		"understory_hidden":
			# A world without the layer has nothing to hide: the case applies and changes nothing ("(no change)").
			for node in state.get("understory_geometry", []):
				if is_instance_valid(node):
					node.layers = 0
		"understory_off":
			var understory := _understory_layer({"trees": state["trees_root"]})
			if understory == null:
				return false
			for node in state["understory_geometry"]:
				if is_instance_valid(node):
					node.layers = 0
			var previous_understory_mode: int = understory.process_mode
			understory.process_mode = Node.PROCESS_MODE_DISABLED
			undo.append(func() -> void:
				if is_instance_valid(understory):
					understory.process_mode = previous_understory_mode as Node.ProcessMode)
		"clouds_off", "cloud_shadow_gen_off":
			var bridge := game.get_node_or_null("SunshineCloudBridge")
			if bridge == null:
				return false
			var property := "CloudRenderingEnabled" if base == "clouds_off" else "GpuShadowGenerationEnabled"
			var previous: Variant = bridge.get(property)
			bridge.set(property, false)
			undo.append(func() -> void:
				if is_instance_valid(bridge):
					bridge.set(property, previous))
		"sun_map_off":
			var graphics := game.get_node_or_null("GraphicsQuality")
			if graphics == null:
				return false
			var requested := bool(graphics.get("RequestedSunShadowsEnabled"))
			graphics.call("SetShadowTuningOverride", "Enabled", false)
			undo.append(func() -> void:
				if is_instance_valid(graphics):
					graphics.call("SetShadowTuningOverride", "Enabled", requested))
		"atlas_4096", "atlas_8192", "atlas_16384":
			var atlas_graphics := game.get_node_or_null("GraphicsQuality")
			if atlas_graphics == null:
				return false
			var atlas_before := int(atlas_graphics.get("RequestedDirectionalShadowAtlasSize"))
			atlas_graphics.call("SetShadowTuningOverride", "AtlasSize", int(base.trim_prefix("atlas_")))
			undo.append(func() -> void:
				if is_instance_valid(atlas_graphics):
					atlas_graphics.call("SetShadowTuningOverride", "AtlasSize", atlas_before))
		"hs_receivers_off":
			var height_shadow := game.get_node_or_null("TerrainHeightShadow")
			if height_shadow == null:
				return false
			height_shadow.set("ReceiversEnabled", false)
			undo.append(func() -> void:
				if is_instance_valid(height_shadow):
					height_shadow.set("ReceiversEnabled", true))
		"water_hidden":
			var hidden: Array = []
			for node in _all_nodes(game):
				if node is MeshInstance3D and (node as MeshInstance3D).visible:
					var shader := _shader_name(node)
					if "ocean" in shader or "water" in shader or "river" in shader or "lake" in shader:
						(node as MeshInstance3D).visible = false
						hidden.append(node)
			state["water_hidden_count"] = hidden.size()
			undo.append(func() -> void:
				for node in hidden:
					if is_instance_valid(node):
						node.visible = true)
		"ui_hidden":
			var layers: Array = []
			for node in _all_nodes((state["viewport"] as Node)):
				if node is CanvasLayer and (node as CanvasLayer).visible:
					(node as CanvasLayer).visible = false
					layers.append(node)
			state["ui_hidden_count"] = layers.size()
			undo.append(func() -> void:
				for node in layers:
					if is_instance_valid(node):
						node.visible = true)
		"clocks_running":
			var cycle := game.get_node_or_null("RingDayNightCycle")
			var weather := game.get_node_or_null("RingVolumetricClouds")
			if cycle == null or weather == null:
				return false
			var cloud_time: Variant = weather.get("CloudSimulationTimeSeconds")
			cycle.set("Paused", false)
			weather.set("CloudSimulationPaused", false)
			undo.append(func() -> void:
				if is_instance_valid(cycle):
					cycle.set("Paused", true)
				if is_instance_valid(weather):
					weather.set("CloudSimulationPaused", true)
					weather.set("CloudSimulationTimeSeconds", cloud_time))
		"far_viewport_off":
			var far_owner: Node = state["scaled_far"]
			var outer: Variant = far_owner.get("OuterViewportForDiagnostics")
			if not (outer is SubViewport) or not is_instance_valid(outer):
				return false
			var mode: Variant = (outer as SubViewport).get("render_target_update_mode")
			(outer as SubViewport).set("render_target_update_mode", SubViewport.UPDATE_DISABLED)
			undo.append(func() -> void:
				if is_instance_valid(outer):
					(outer as SubViewport).set("render_target_update_mode", mode))
		"env_post_off", "env_ssao_off", "env_ssr_off":
			var world_environment: WorldEnvironment = state["world_environment"]
			var environment := world_environment.environment
			if environment == null:
				return false
			var before := {}
			var flags: Array = ENV_POST_FLAGS if base == "env_post_off" else (["ssao_enabled"] if base == "env_ssao_off" else ["ssr_enabled"])
			for flag in flags:
				before[flag] = environment.get(flag)
				environment.set(flag, false)
			if base == "env_post_off":
				state["env_post_before"] = before
			undo.append(func() -> void:
				for flag in before:
					environment.set(flag, before[flag]))
		_:
			return false
	return true


## What the extra cases can see of the render state (recorded per window, so a switch that did not take shows in the report).
static func extra_probe(ctx: Dictionary) -> Dictionary:
	var game: Node = ctx["game"]
	var root: Window = (ctx["tree"] as SceneTree).root
	var sun: DirectionalLight3D = ctx["sun"]
	var bridge := game.get_node_or_null("SunshineCloudBridge")
	var environment := (ctx["world_environment"] as WorldEnvironment).environment
	var flags := {}
	if environment != null:
		for flag in ENV_POST_FLAGS:
			flags[flag] = environment.get(flag)
	var cycle := game.get_node_or_null("RingDayNightCycle")
	var feature_mask: Variant = null
	if ctx.has("feature_ab"):
		feature_mask = (ctx["feature_ab"]["hook"] as RefCounted).call("ShaderMask")
	return {"feature_mask": feature_mask, "sun_shadow_enabled": sun.shadow_enabled, "sun_shadow_mode": sun.directional_shadow_mode, "sun_shadow_max_distance": sun.directional_shadow_max_distance,
		"clouds_enabled": bridge.get("CloudRenderingEnabled") if bridge != null else null, "cloud_shadow_gen": bridge.get("GpuShadowGenerationEnabled") if bridge != null else null,
		"use_taa": root.use_taa, "msaa_3d": root.msaa_3d, "screen_space_aa": root.screen_space_aa, "scaling_3d_mode": root.scaling_3d_mode, "scaling_3d_scale": root.scaling_3d_scale,
		"clock_paused": cycle.get("Paused") if cycle != null else null, "environment": flags,
		"shadow_atlas": game.get_node("GraphicsQuality").get("EffectiveDirectionalShadowAtlasSize") if game.get_node_or_null("GraphicsQuality") != null else null}

# ------------------------------------------------------------------ cost part

static func summarize(values: Array) -> Dictionary:
	var clean: Array = []
	for value in values:
		if not is_nan(float(value)):
			clean.append(float(value))
	if clean.is_empty():
		return {"n": 0}
	clean.sort()
	var total := 0.0
	for value in clean:
		total += float(value)
	return {"n": clean.size(), "median": COMMON.percentile(clean, 0.5), "p95": COMMON.percentile(clean, 0.95), "mean": total / clean.size(), "min": clean[0], "max": clean[-1]}


static func _median(values: Array) -> float:
	var clean: Array = []
	for value in values:
		if not is_nan(float(value)):
			clean.append(float(value))
	if clean.is_empty():
		return NAN
	clean.sort()
	return COMMON.percentile(clean, 0.5)


static func _viewports(ctx: Dictionary) -> void:
	var rids: Array = [(ctx["tree"] as SceneTree).root.get_viewport_rid()]
	var far_rid := RID()
	for node in _all_nodes(ctx["game"]):
		if node is SubViewport:
			rids.append((node as SubViewport).get_viewport_rid())
			if str(node.get_path()).ends_with("/FarRingViewport") or (not far_rid.is_valid() and "ScaledFarViewport" in str(node.get_path())):
				far_rid = (node as SubViewport).get_viewport_rid()
	for rid in rids:
		RenderingServer.viewport_set_measure_render_time(rid, true)
	ctx["root_rid"] = rids[0]
	ctx["all_rids"] = rids
	ctx["far_rid"] = far_rid
	ctx["viewport_count"] = rids.size()


## T5-10: waits (up to `timeout_s`) for the scatter streamer to have no job and nothing due, 30 stable frames in a row. True at once in a world without scatter.rocks.
static func wait_scatter_idle(ctx: Dictionary, timeout_s: float) -> bool:
	var trees: Node = ctx["trees"]
	if not bool(trees.get("HasScatterStreamer")):
		return true
	var tree: SceneTree = ctx["tree"]
	var stable := 0
	var deadline := Time.get_ticks_msec() + int(timeout_s * 1000.0)
	while Time.get_ticks_msec() < deadline:
		await tree.process_frame
		stable = stable + 1 if bool(trees.get("ScatterIdle")) else 0
		if stable >= 30:
			return true
	return false


## T5-10: resident record counts per class, the scatter compute's bytes and the layer's drawn instance counts per output (readback of each batch's camera list); {} without scatter.rocks.
static func scatter_snapshot(ctx: Dictionary) -> Dictionary:
	var trees: Node = ctx["trees"]
	if not bool(trees.get("HasScatterStreamer")):
		return {}
	var out := {"resident": JSON.parse_string(str(trees.call("ScatterResidentJson")))}
	var layer := _scatter_layer(ctx)
	if layer != null:
		out["compute"] = JSON.parse_string(str(layer.call("ComputeBytesJson")))
		layer.call("DrawnProbeBegin")
		var text := ""
		for i in 180:
			text = str(layer.call("DrawnProbeTake"))
			if text != "":
				break
			await (ctx["tree"] as SceneTree).process_frame
		out["drawn"] = JSON.parse_string(text) if text != "" else null
	return out


## T5-10: the per-tile build-time pairs of the tiles built since the last reset (tools/RingBiomeDecorationLayer.ScatterProbe.cs).
static func scatter_tile_rows(ctx: Dictionary) -> Array:
	var trees: Node = ctx["trees"]
	if not bool(trees.get("HasScatterStreamer")):
		return []
	var parsed: Variant = JSON.parse_string(str(trees.call("ScatterTileTimingJson")))
	return parsed if parsed is Array else []


static func _percentile(values: Array, fraction: float) -> float:
	if values.is_empty():
		return NAN
	var sorted := values.duplicate()
	sorted.sort()
	return float(sorted[mini(sorted.size() - 1, int(floor(fraction * float(sorted.size()))))])


## Gate numbers of one pose's tile rows (design A3 "Measurement"): over tiles that got stage B and have a real tree build, (Trees + O + B) / Trees; Trees build median and p95 over every tile with a real build;
## O, B and C worker ms; C per 60 m block over cobble jobs. ms include the habitat grid.
static func scatter_build_summary(rows: Array) -> Dictionary:
	var trees: Array = []
	var ratios: Array = []
	var o_all: Array = []
	var b_all: Array = []
	var b_extra: Array = []
	var c_blocks: Array = []
	var c_jobs := 0
	var o_tiles := 0
	for row in rows:
		var tree_ms := float(row["tree_ms"])
		if tree_ms > 0.0:
			trees.append(tree_ms)
		if float(row["o_ms"]) > 0.0:
			o_all.append(float(row["o_ms"]))
			o_tiles += 1
		if float(row["b_ms"]) > 0.0:
			b_all.append(float(row["b_ms"]))
			if tree_ms > 0.0:
				ratios.append((tree_ms + float(row["o_ms"]) + float(row["b_ms"])) / tree_ms)
				b_extra.append(float(row["o_ms"]) + float(row["b_ms"]))
		for job in row.get("c_jobs", []):
			c_jobs += 1
			c_blocks.append(float(job[0]) / maxf(1.0, float(job[1])))
	return {"tiles": rows.size(), "trees_n": trees.size(), "trees_median_ms": _median(trees) if not trees.is_empty() else NAN, "trees_p95_ms": _percentile(trees, 0.95),
		"stage_b_tiles": b_all.size(), "stage_b_with_tree": ratios.size(), "ratio_median": _median(ratios) if not ratios.is_empty() else NAN, "ratio_p95": _percentile(ratios, 0.95),
		"ratio_max": (ratios.max() if not ratios.is_empty() else NAN), "ob_extra_median_ms": _median(b_extra) if not b_extra.is_empty() else NAN, "ob_extra_p95_ms": _percentile(b_extra, 0.95),
		"o_tiles": o_tiles, "o_median_ms": _median(o_all) if not o_all.is_empty() else NAN, "o_p95_ms": _percentile(o_all, 0.95),
		"b_median_ms": _median(b_all) if not b_all.is_empty() else NAN, "b_p95_ms": _percentile(b_all, 0.95),
		"c_jobs": c_jobs, "c_block_median_ms": _median(c_blocks) if not c_blocks.is_empty() else NAN, "c_block_p95_ms": _percentile(c_blocks, 0.95)}


static func _fill_summary(cpu: Dictionary) -> Dictionary:
	var result := {}
	for name in FILL_SCOPES:
		var values: Array = []
		if cpu.has(name):
			values = Array(cpu[name])
		var total := 0.0
		for value in values:
			total += float(value)
		var summary := summarize(values)
		result[name] = {"count": values.size(), "median_ms": summary.get("median", NAN), "p95_ms": summary.get("p95", NAN), "sum_ms": total}
	return result


## One timed window of `seconds` after 60 warm-up frames and a 1 s settle. The case must already be applied.
static func sample_window(ctx: Dictionary, seconds: float) -> Dictionary:
	var tree: SceneTree = ctx["tree"]
	var timing: RefCounted = ctx["timing"]
	for i in WARMUP_FRAMES:
		await RenderingServer.frame_post_draw
	await COMMON.settle(tree, SETTLE_S)
	timing.call("ResetWindow")
	var root_gpu: Array = []
	var root_cpu: Array = []
	var far_gpu: Array = []
	var frame_ms: Array = []
	var draws: Array = []
	var prims: Array = []
	var process_ms: Array = []
	var physics_ms: Array = []
	var setup_ms: Array = []
	var all_gpu: Array = []
	var all_cpu: Array = []
	var all_rids: Array = ctx.get("all_rids", [])
	var root_rid: RID = ctx["root_rid"]
	var far_rid: RID = ctx["far_rid"]
	# perf pass P0: split the frame at the SceneTree process_frame signal (emitted before the nodes' _process) and at frame_pre_draw: process step = pre_draw - process_frame
	# (all _process, timers, deferred calls), draw step = post_draw - pre_draw (render CPU plus any wait for the GPU), other = the rest of the frame (physics, input, OS).
	var marks := {"process": 0, "pre": 0}
	var on_process := func() -> void: marks["process"] = Time.get_ticks_usec()
	var on_pre := func() -> void: marks["pre"] = Time.get_ticks_usec()
	tree.process_frame.connect(on_process)
	RenderingServer.frame_pre_draw.connect(on_pre)
	var process_step: Array = []
	var draw_step: Array = []
	var other_step: Array = []
	var utc0 := Time.get_unix_time_from_system()
	var start := Time.get_ticks_usec()
	var last := start
	while (Time.get_ticks_usec() - start) * 0.000001 < seconds:
		await RenderingServer.frame_post_draw
		var now := Time.get_ticks_usec()
		frame_ms.append((now - last) * 0.001)
		if int(marks["process"]) > last and int(marks["pre"]) >= int(marks["process"]):
			process_step.append((int(marks["pre"]) - int(marks["process"])) * 0.001)
			draw_step.append((now - int(marks["pre"])) * 0.001)
			other_step.append((int(marks["process"]) - last) * 0.001)
		last = now
		root_gpu.append(RenderingServer.viewport_get_measured_render_time_gpu(root_rid))
		root_cpu.append(RenderingServer.viewport_get_measured_render_time_cpu(root_rid))
		far_gpu.append(RenderingServer.viewport_get_measured_render_time_gpu(far_rid) if far_rid.is_valid() else NAN)
		draws.append(Performance.get_monitor(Performance.RENDER_TOTAL_DRAW_CALLS_IN_FRAME))
		prims.append(Performance.get_monitor(Performance.RENDER_TOTAL_PRIMITIVES_IN_FRAME))
		# perf pass P0: main-thread process and physics time, render setup and the sum over every measured viewport (bound analysis)
		process_ms.append(Performance.get_monitor(Performance.TIME_PROCESS) * 1000.0)
		physics_ms.append(Performance.get_monitor(Performance.TIME_PHYSICS_PROCESS) * 1000.0)
		setup_ms.append(RenderingServer.get_frame_setup_time_cpu())
		var sum_gpu := 0.0
		var sum_cpu := 0.0
		for rid in all_rids:
			sum_gpu += RenderingServer.viewport_get_measured_render_time_gpu(rid)
			sum_cpu += RenderingServer.viewport_get_measured_render_time_cpu(rid)
		all_gpu.append(sum_gpu)
		all_cpu.append(sum_cpu)
		timing.call("Sample")
	var utc1 := Time.get_unix_time_from_system()
	tree.process_frame.disconnect(on_process)
	RenderingServer.frame_pre_draw.disconnect(on_pre)
	var markers := {}
	var gpu_window: Dictionary = timing.call("Window")
	for key in gpu_window:
		if str(key) == "_meta":
			continue
		var values: Array = Array(gpu_window[key])
		markers[str(key)] = summarize(values)
	var cpu_scopes := {}
	var cpu_window: Dictionary = timing.call("CpuWindow")
	for key in cpu_window:
		if str(key) == "_meta":
			continue
		var scope_values: Array = Array(cpu_window[key])
		var scope_sum := 0.0
		for value in scope_values:
			scope_sum += float(value)
		cpu_scopes[str(key)] = {"n": scope_values.size(), "sum_ms": scope_sum, "ms_per_frame": scope_sum / maxf(1.0, float(draws.size())), "median": _median(scope_values)}
	return {"samples": draws.size(), "seconds": (Time.get_ticks_usec() - start) * 0.000001, "utc0": utc0, "utc1": utc1, "root_gpu_ms": summarize(root_gpu), "far_gpu_ms": summarize(far_gpu),
		"process_ms": summarize(process_ms), "physics_ms": summarize(physics_ms), "setup_cpu_ms": summarize(setup_ms), "all_gpu_ms": summarize(all_gpu), "all_cpu_ms": summarize(all_cpu), "cpu_scopes": cpu_scopes,
		"process_step_ms": summarize(process_step), "draw_step_ms": summarize(draw_step), "other_step_ms": summarize(other_step),
		"root_cpu_ms": summarize(root_cpu), "frame_ms": summarize(frame_ms), "draw_calls": summarize(draws), "primitives": summarize(prims), "markers": markers, "timing_meta": gpu_window.get("_meta", {})}


static func _workload(ctx: Dictionary) -> Dictionary:
	var trees: Node = ctx["trees"]
	var grass: Node = ctx["grass"]
	var diagnostics: Dictionary = trees.call("GetImportedForestRuntimeDiagnostics")
	return {"tree_canonical_records": diagnostics.get("canonical_record_count", 0), "tree_gpu_roots": trees.get("GpuTreeResidentRootCount"), "tree_gpu_batches": trees.get("GpuTreeBatchCount"),
		"grass_resident_instances": grass.get("ResidentGrassInstanceCount"), "grass_batches": grass.get("GpuCullingBatchCount")}


static func _install_bracket(ctx: Dictionary) -> Dictionary:
	if COMMON.has_flag("--no-bracket"):
		return {"available": false, "reason": "disabled by --no-bracket"}
	var world_environment: WorldEnvironment = ctx["world_environment"]
	if world_environment.compositor == null:
		world_environment.compositor = Compositor.new()
	var effects := world_environment.compositor.compositor_effects.duplicate()
	var added: Array = []
	for callback_type in [CompositorEffect.EFFECT_CALLBACK_TYPE_PRE_OPAQUE, CompositorEffect.EFFECT_CALLBACK_TYPE_POST_OPAQUE, CompositorEffect.EFFECT_CALLBACK_TYPE_POST_TRANSPARENT]:
		var effect: CompositorEffect = PASS_EFFECT.make(callback_type)
		effects.append(effect)
		added.append(effect)
	world_environment.compositor.compositor_effects = effects
	ctx["bracket_effects"] = added
	var timing: RefCounted = ctx["timing"]
	var wanted := ["lp_pre_opaque", "lp_post_opaque", "lp_post_transparent"]
	var found := false
	for frame in 120:
		await RenderingServer.frame_post_draw
		timing.call("Sample")
		var names: Array = Array(timing.call("ListEngineTimestampNames"))
		found = true
		for name in wanted:
			if not name in names:
				found = false
		if found:
			break
	var names_seen: Array = Array(timing.call("ListEngineTimestampNames"))
	if not found:
		var remaining := world_environment.compositor.compositor_effects.duplicate()
		for effect in added:
			remaining.erase(effect)
		world_environment.compositor.compositor_effects = remaining
		return {"available": false, "reason": "opaque bracket unavailable: names did not appear in ListEngineTimestampNames() within 120 frames", "engine_names_seen": names_seen}
	return {"available": true, "engine_names_seen": names_seen}


static func run_cost(ctx: Dictionary) -> Dictionary:
	var tree: SceneTree = ctx["tree"]
	var marker: String = ctx["marker"]
	var machine: Dictionary = ctx["machine"]
	var rounds := maxi(1, int(harness_arg("--rounds=", "2")))
	var seconds := maxf(1.0, float(harness_arg("--seconds=", "8")))
	# T4-2 `--feature-ab=<feature id | bit:N>`: adds the same-boot A-B-A case feature_off (baseline, feature_off, baseline_repeat). Resolved before anything is measured.
	var feature_spec := harness_arg("--feature-ab=", "")
	if feature_spec != "":
		var hook: RefCounted = FEATURE_AB.new()
		var resolved: Dictionary = hook.call("Resolve", feature_spec)
		if not bool(resolved.get("ok", false)):
			var failure := "--feature-ab: " + str(resolved.get("message", "unresolved"))
			push_error("%s=FAIL %s" % [marker, failure])
			return {"failures": [failure], "status": "FAIL"}
		if (int(hook.call("WorldMask")) >> int(resolved["bit"])) & 1 == 0:
			var missing := "--feature-ab: %s is not on in this world (mask 0x%x); boot a review world with the feature, e.g. --generation=1 --review-features=%s" % [feature_spec, int(hook.call("WorldMask")), feature_spec]
			push_error("%s=FAIL %s" % [marker, missing])
			return {"failures": [missing], "status": "FAIL"}
		ctx["feature_ab"] = {"hook": hook, "spec": feature_spec, "id": str(resolved["id"]), "bit": int(resolved["bit"]), "world_mask": int(hook.call("WorldMask")), "shader_mask_at_start": int(hook.call("ShaderMask"))}
		print("%s feature A-B: %s = bit %d, world mask 0x%x, hook last published 0x%x; feature_off publishes 0x%x" % [marker, feature_spec, int(resolved["bit"]), int(ctx["feature_ab"]["world_mask"]),
			int(ctx["feature_ab"]["shader_mask_at_start"]), int(ctx["feature_ab"]["world_mask"]) & ~(1 << int(resolved["bit"]))])
	var out_dir := H.baseline_dir(str(machine["tag"]), "t0", "cost", ctx["booted"]["world_tag"])
	H.copy_prefs_snapshot(ctx["booted"], out_dir)
	var power_path := out_dir.path_join("power_log.csv")
	ctx["power_pid"] = COMMON.start_power_log(power_path)
	_viewports(ctx)
	var timing: RefCounted = TIMING.new()
	timing.call("Begin", true)
	ctx["timing"] = timing
	var bracket := await _install_bracket(ctx)
	print("%s opaque bracket: %s" % [marker, JSON.stringify(bracket)])
	await COMMON.settle(tree, 1.5)

	# pose-times: every t0 pose at noon, plus the two sun_8_set extras (when the pose is selected)
	var pose_times: Array = []
	var poses := t0_poses()
	for pose in poses:
		pose_times.append([pose, "noon"])
	for extra in COST_EXTRA:
		for pose in poses:
			if str(pose["name"]) == str(extra[0]):
				pose_times.append([pose, str(extra[1])])
	var cases: Array = CASES.duplicate()
	if ctx.has("feature_ab"):
		cases.insert(cases.size() - 1, "feature_off")
	for extra in harness_csv("--extra-cases="):
		if not cases.has(str(extra)):
			cases.insert(cases.size() - 1, str(extra))
	var plan_windows := pose_times.size() * rounds * cases.size()
	print("%s plan: %d pose-times x %d rounds x %d cases = %d windows of %.1f s (+%d frames +%.1f s settle) rounds=%d" % [marker, pose_times.size(), rounds, CASES.size(), plan_windows, seconds, WARMUP_FRAMES, SETTLE_S, rounds])

	var results: Array = []
	var failures: Array = []
	var windows_total := 0
	for entry in pose_times:
		var pose: Dictionary = entry[0]
		var time_name: String = entry[1]
		var key := "%s__%s" % [pose["name"], time_name]
		# 1. fill: CPU scopes from the teleport until builds are idle
		timing.call("ResetWindow")
		var sampler := Callable(timing, "Sample")
		tree.process_frame.connect(sampler)
		var fill_start := Time.get_ticks_msec()
		if bool((ctx["trees"] as Node).get("HasScatterStreamer")):
			(ctx["trees"] as Node).call("ScatterTileTimingReset")
		var info: Dictionary = await H.go_pose(tree, ctx["game"], pose, time_name, ctx["ground_model"])
		var fill_wall_s := (Time.get_ticks_msec() - fill_start) / 1000.0
		# T5-10: scatter jobs run after the tree tiles; keep sampling until they are done (no-op without scatter.rocks).
		var scatter_idle := await wait_scatter_idle(ctx, 240.0)
		tree.process_frame.disconnect(sampler)
		var weather := H.weather_at(ctx["game"], float(pose["around_m"]), float(pose["across_m"]))
		var record := {"key": key, "pose": pose["name"], "time": time_name, "go_pose": info, "weather": weather}
		if info.has("error"):
			failures.append("%s: %s" % [key, str(info["error"])])
			record["failure"] = str(info["error"])
			results.append(record)
			continue
		var cpu_window: Dictionary = timing.call("CpuWindow")
		var fill := {"wall_s_incl_settle": fill_wall_s, "scopes": _fill_summary(cpu_window), "dropped": cpu_window.get("_meta", {})}
		record.merge({"sun_elevation_deg": info.get("sun_elevation_deg", NAN), "fill": fill, "workload": _workload(ctx), "windows": [], "hide_effect": {}})
		if bool((ctx["trees"] as Node).get("HasScatterStreamer")):
			await COMMON.settle(tree, 1.0)
			var tile_rows := scatter_tile_rows(ctx)
			record["scatter_idle_after_fill"] = scatter_idle
			record["scatter_tiles"] = tile_rows
			record["scatter_build"] = scatter_build_summary(tile_rows)
			record["scatter"] = await scatter_snapshot(ctx)
		var state := capture_original_state(ctx)
		if results.is_empty():
			record["far_mesh_names"] = state["far_mesh_names"] # evidence for which meshes count as far terrain (first pose only)
			var children: Array = []
			for child in (ctx["game"] as Node).get_children():
				children.append("%s:%s:%s" % [child.name, child.get_class(), (child.get_script() as Script).resource_path.get_file() if child.get_script() != null else "-"])
			record["game_children"] = children
		record["geometry_nodes"] = {"near_terrain": state["near_nodes"].size(), "far_terrain": state["far_nodes"].size(), "trees": state["tree_geometry"].size(), "grass": state["grass_geometry"].size(), "scatter": state["scatter_geometry"].size(), "understory": state.get("understory_geometry", []).size()}
		print("%s %s fill %.1f s, scopes %s, nodes %s" % [marker, key, fill_wall_s, JSON.stringify(fill["scopes"]), JSON.stringify(record["geometry_nodes"])])
		# 2./3. cases
		for round_index in rounds:
			var order: Array = cases.duplicate()
			if round_index % 2 == 1:
				order.reverse()
			for label in order:
				restore_state(state)
				if not apply_case(state, str(label)):
					failures.append("%s: unknown case %s" % [key, label])
					continue
				var window: Dictionary = await sample_window(ctx, seconds)
				window["probe"] = extra_probe(ctx)
				print("%s %s r%d %s frame %.2f p95 %.2f max %.2f proc_step %.2f draw_step %.2f other %.2f root_gpu %.2f far_gpu %.2f all_gpu %.2f all_cpu %.2f setup %.2f draws %.0f" % [marker, key, round_index, label, float(window["frame_ms"].get("median", NAN)), float(window["frame_ms"].get("p95", NAN)), float(window["frame_ms"].get("max", NAN)), float(window["process_step_ms"].get("median", NAN)), float(window["draw_step_ms"].get("median", NAN)), float(window["other_step_ms"].get("median", NAN)), float(window["root_gpu_ms"].get("median", NAN)), float(window["far_gpu_ms"].get("median", NAN)), float(window["all_gpu_ms"].get("median", NAN)), float(window["all_cpu_ms"].get("median", NAN)), float(window["setup_cpu_ms"].get("median", NAN)), float(window["draw_calls"].get("mean", NAN))])
				var why := verify_case(state, str(label))
				window["label"] = label
				window["round"] = round_index
				window["state_ok"] = why == ""
				if why != "":
					window["failure"] = why
					failures.append("%s %s round %d: %s" % [key, label, round_index, why])
				(record["windows"] as Array).append(window)
				windows_total += 1
		restore_state(state)
		record["derived"] = _derive(record["windows"], cases)
		# T5-10: with no scatter layer in the world there is nothing to hide; primitive-count jitter between windows must not read as a change ("(no change)").
		if (state["scatter_geometry"] as Array).is_empty() and record["derived"]["classes"].has("scatter_hidden"):
			record["derived"]["classes"]["scatter_hidden"]["changed_submissions"] = false
		# T6-4b: the same for understory_hidden in a world without the understory layer.
		if (state.get("understory_geometry", []) as Array).is_empty() and record["derived"]["classes"].has("understory_hidden"):
			record["derived"]["classes"]["understory_hidden"]["changed_submissions"] = false
		record["extra_state"] = {"water_hidden_count": state.get("water_hidden_count", -1), "ui_hidden_count": state.get("ui_hidden_count", -1), "env_post_before": state.get("env_post_before", {})}
		results.append(record)
		print("%s %s derived %s" % [marker, key, JSON.stringify(record["derived"]["summary_line"])])
		_save_json(out_dir.path_join("cost_partial.json"), {"completed": results.size(), "of": pose_times.size(), "pose_times": results})

	COMMON.stop_power_log(int(ctx["power_pid"]))
	ctx["power_pid"] = -1
	# stop the measurement hooks and let the render thread drain them before the world is freed (the 4.8-dev7 exit crash is likelier with timestamp callbacks in flight)
	if ctx.has("bracket_effects"):
		var remaining: Array = (ctx["world_environment"] as WorldEnvironment).compositor.compositor_effects.duplicate()
		for effect in ctx["bracket_effects"]:
			remaining.erase(effect)
		(ctx["world_environment"] as WorldEnvironment).compositor.compositor_effects = remaining
		ctx.erase("bracket_effects")
	timing.call("End")
	ctx.erase("timing")
	for i in 10:
		await RenderingServer.frame_post_draw
	await COMMON.settle(tree, 0.5)
	var rows := COMMON.parse_power_log(power_path)
	var offset := COMMON.local_offset_seconds()
	var valid := 0
	var counted := 0
	for record in results:
		for window in record.get("windows", []):
			window["power"] = COMMON.power_window(rows, float(window["utc0"]), float(window["utc1"]), machine, offset)
			counted += 1
			if bool(window["power"]["valid"]):
				valid += 1
	var valid_share := float(valid) / maxf(1.0, float(counted))
	# a hide class that changed no submissions at ANY pose is a harness/design problem (escalation condition)
	var class_effect := {}
	var scatter_nodes_total := 0
	for record in results:
		scatter_nodes_total += int(record.get("geometry_nodes", {}).get("scatter", 0))
	for label in HIDE_CLASSES:
		class_effect[label] = 0
		for record in results:
			if record.has("derived") and bool(record["derived"]["classes"][label]["changed_submissions"]):
				class_effect[label] = int(class_effect[label]) + 1
		# scatter_hidden has nothing to hide in a world without the scatter layer: that is "(no change)", not a failure.
		if label == "scatter_hidden" and scatter_nodes_total == 0:
			continue
		if int(class_effect[label]) == 0 and not results.is_empty():
			failures.append("hide class %s changed no draw or primitive count at any pose (escalate: hide path unreachable)" % label)
	var status := "PASS"
	if not failures.is_empty() or valid_share < 0.8 or results.is_empty():
		status = "FAIL"
	var report: Dictionary = (ctx["record"] as Dictionary).duplicate(true)
	report.merge({"marker": marker, "tool": "landscape_cost_baseline", "label": "4070, non-authoritative" if not bool(machine["authoritative"]) else "A1000, authoritative only where every window is power-valid",
		"rounds": rounds, "window_seconds": seconds, "warmup_frames": WARMUP_FRAMES, "settle_s": SETTLE_S, "bracket": bracket, "pose_times": results, "failures": failures, "windows": windows_total,
		"power_valid_windows": valid, "power_valid_share": valid_share, "hide_class_effect_poses": class_effect, "status": status, "output": out_dir, "power_log": power_path})
	if ctx.has("feature_ab"):
		var ab: Dictionary = ctx["feature_ab"]
		report["feature_ab"] = {"spec": ab["spec"], "id": ab["id"], "bit": ab["bit"], "world_mask": ab["world_mask"], "shader_mask_at_start": ab["shader_mask_at_start"],
			"feature_off_mask": int(ab["world_mask"]) & ~(1 << int(ab["bit"])), "flag_delta_ms": FEATURE_AB_FLAG_MS}
	_save_json(out_dir.path_join("cost.json"), report)
	var markdown := _cost_markdown(report)
	var file := FileAccess.open(out_dir.path_join("cost.md"), FileAccess.WRITE)
	if file != null:
		file.store_string(markdown)
		file.close()
	DirAccess.remove_absolute(out_dir.path_join("cost_partial.json"))
	print("%s output: %s" % [marker, out_dir])
	return report


## Window medians per case (over rounds), hide deltas, the noise floor and the draw/primitive deltas.
static func _derive(windows: Array, cases: Array = CASES) -> Dictionary:
	var by_label := {}
	for label in cases:
		by_label[label] = []
	for window in windows:
		if by_label.has(window["label"]):
			by_label[window["label"]].append(window)
	var med := func(label: String, metric: String, field: String) -> float:
		var values: Array = []
		for window in by_label[label]:
			values.append(float(window[metric].get(field, NAN)))
		return _median(values)
	var base_root: float = med.call("baseline", "root_gpu_ms", "median")
	var rep_root: float = med.call("baseline_repeat", "root_gpu_ms", "median")
	var base_far: float = med.call("baseline", "far_gpu_ms", "median")
	var rep_far: float = med.call("baseline_repeat", "far_gpu_ms", "median")
	var base_draw: float = med.call("baseline", "draw_calls", "mean")
	var base_prim: float = med.call("baseline", "primitives", "mean")
	var med_marker := func(label: String, name: String) -> float:
		var values: Array = []
		for window in by_label[label]:
			if window["markers"].has(name) and int(window["markers"][name].get("n", 0)) > 0:
				values.append(float(window["markers"][name]["median"]))
		return _median(values) if not values.is_empty() else NAN
	var classes := {}
	var parts: Array = []
	var scales := {}
	for case_label in cases:
		var label := str(case_label)
		var suffix := RS_SUFFIX if label.ends_with(RS_SUFFIX) else ""
		var reference := "baseline" + suffix
		if label.trim_suffix(suffix).begins_with("baseline"):
			if label == reference and by_label.has("baseline_repeat" + suffix):
				var first: float = med.call(reference, "root_gpu_ms", "median")
				var again: float = med.call("baseline_repeat" + suffix, "root_gpu_ms", "median")
				scales[reference] = {"baseline_root_gpu_ms": first, "baseline_repeat_root_gpu_ms": again, "noise_floor_root_gpu_ms": absf(first - again),
					"baseline_opaque_bracket_ms": med_marker.call(reference, "BRACKET:opaque"), "baseline_primitives": med.call(reference, "primitives", "mean"),
					"baseline_draw_calls": med.call(reference, "draw_calls", "mean")}
			continue
		if not by_label.has(reference):
			continue
		var d_root: float = float(med.call(reference, "root_gpu_ms", "median")) - float(med.call(label, "root_gpu_ms", "median"))
		var d_far: float = float(med.call(reference, "far_gpu_ms", "median")) - float(med.call(label, "far_gpu_ms", "median"))
		var d_draw: float = float(med.call(reference, "draw_calls", "mean")) - float(med.call(label, "draw_calls", "mean"))
		var d_prim: float = float(med.call(reference, "primitives", "mean")) - float(med.call(label, "primitives", "mean"))
		var d_opaque: float = float(med_marker.call(reference, "BRACKET:opaque")) - float(med_marker.call(label, "BRACKET:opaque"))
		var changed := absf(d_draw) >= 0.5 or absf(d_prim) >= 1.0
		classes[label] = {"root_gpu_delta_ms": d_root, "far_gpu_delta_ms": d_far, "draw_delta": d_draw, "primitive_delta": d_prim, "changed_submissions": changed,
			"root_gpu_hidden_median_ms": med.call(label, "root_gpu_ms", "median"), "opaque_bracket_delta_ms": d_opaque}
		if suffix == "":
			parts.append("%s root %.2f ms draws %.0f prims %.0f" % [label.trim_suffix("_hidden"), d_root, d_draw, d_prim])
	var markers := {}
	for name in MARKERS:
		var values: Array = []
		for window in by_label["baseline"] + by_label["baseline_repeat"]:
			if window["markers"].has(name) and int(window["markers"][name].get("n", 0)) > 0:
				values.append(float(window["markers"][name]["median"]))
		markers[name] = {"windows_with_samples": values.size(), "median_ms": _median(values) if not values.is_empty() else NAN}
	for name in ["BRACKET:opaque", "BRACKET:post_opaque_to_transparent_end"]:
		var values: Array = []
		for window in by_label["baseline"] + by_label["baseline_repeat"]:
			if window["markers"].has(name) and int(window["markers"][name].get("n", 0)) > 0:
				values.append(float(window["markers"][name]["median"]))
		markers[name] = {"windows_with_samples": values.size(), "median_ms": _median(values) if not values.is_empty() else NAN}
	return {"baseline_root_gpu_ms": base_root, "baseline_repeat_root_gpu_ms": rep_root, "noise_floor_root_gpu_ms": absf(base_root - rep_root), "baseline_far_gpu_ms": base_far,
		"noise_floor_far_gpu_ms": absf(base_far - rep_far), "baseline_p95_root_gpu_ms": med.call("baseline", "root_gpu_ms", "p95"), "baseline_frame_ms": med.call("baseline", "frame_ms", "median"),
		"baseline_root_cpu_ms": med.call("baseline", "root_cpu_ms", "median"), "baseline_draw_calls": base_draw, "baseline_primitives": base_prim,
		"baseline_noise_draw_calls": absf(base_draw - float(med.call("baseline_repeat", "draw_calls", "mean"))), "classes": classes, "markers": markers, "render_scales": scales,
		"summary_line": "base %.2f ms (noise %.2f), %s" % [base_root, absf(base_root - rep_root), "; ".join(parts)]}


static func _f(value: float, digits := 2) -> String:
	return "n/a" if is_nan(value) else ("%." + str(digits) + "f") % value


static func _cost_markdown(report: Dictionary) -> String:
	var machine: Dictionary = report["machine"]
	var lines: Array = []
	lines.append("# Landscape T0-7 cost baseline (%s)" % report["label"])
	lines.append("")
	lines.append("Machine: %s, adapter `%s`, %s, Godot %s. Resolution %s, VSync off, preset `%s` (grass %s). Rounds %d, window %.0f s (+%d warm-up frames, %.0f s settle). Prefs `%s` (atmosphere settings sha256 `%s`)." % [
		machine["label"], machine["adapter"], machine["renderer"], report["godot"], str(report["resolution"]), report["preset"].get("preset", "?"), report["grass_render_style"], int(report["rounds"]),
		float(report["window_seconds"]), int(report["warmup_frames"]), float(report["settle_s"]), report["prefs"]["mode"], str(report["prefs"]["atmosphere_settings_sha256"])])
	lines.append("")
	lines.append("Windows %d, power-valid %d (%.0f %%). Status %s. Opaque bracket: %s." % [int(report["windows"]), int(report["power_valid_windows"]), float(report["power_valid_share"]) * 100.0, report["status"],
		"available" if bool(report["bracket"].get("available", false)) else str(report["bracket"].get("reason", "unavailable"))])
	lines.append("")
	lines.append("## Baseline per pose-time (root viewport GPU ms from viewport timing; frame ms is wall time between frames)")
	lines.append("")
	lines.append("| pose | time | sun el | root GPU med | root GPU p95 | far GPU med | root CPU render | frame ms | draws | primitives | noise floor root GPU | noise floor far GPU | noise draws |")
	lines.append("|---|---|---|---|---|---|---|---|---|---|---|---|---|")
	for record in report["pose_times"]:
		if not record.has("derived"):
			lines.append("| %s | %s | FAILED: %s |" % [record["pose"], record["time"], record.get("failure", "?")])
			continue
		var d: Dictionary = record["derived"]
		lines.append("| %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s |" % [record["pose"], record["time"], _f(float(record["sun_elevation_deg"]), 1), _f(d["baseline_root_gpu_ms"]), _f(d["baseline_p95_root_gpu_ms"]),
			_f(d["baseline_far_gpu_ms"]), _f(d["baseline_root_cpu_ms"]), _f(d["baseline_frame_ms"]), _f(d["baseline_draw_calls"], 0), _f(d["baseline_primitives"], 0),
			_f(d["noise_floor_root_gpu_ms"]), _f(d["noise_floor_far_gpu_ms"]), _f(d["baseline_noise_draw_calls"], 1)])
	lines.append("")
	lines.append("## Hide deltas on the root viewport (ms = baseline minus hidden, median of window medians; compare with the noise floor above)")
	lines.append("")
	lines.append("| pose | time | near terrain | far terrain | trees | grass | scatter | draws: near / far / trees / grass / scatter | primitives: near / far / trees / grass / scatter |")
	lines.append("|---|---|---|---|---|---|---|---|---|")
	for record in report["pose_times"]:
		if not record.has("derived"):
			continue
		var c: Dictionary = record["derived"]["classes"]
		var cells: Array = []
		var draw_cells: Array = []
		var prim_cells: Array = []
		for label in HIDE_CLASSES:
			cells.append(_f(float(c[label]["root_gpu_delta_ms"])) + ("" if bool(c[label]["changed_submissions"]) else " (no change)"))
			draw_cells.append(_f(float(c[label]["draw_delta"]), 0))
			prim_cells.append(_f(float(c[label]["primitive_delta"]), 0))
		lines.append("| %s | %s | %s | %s | %s | %s | %s | %s | %s |" % [record["pose"], record["time"], cells[0], cells[1], cells[2], cells[3], cells[4], " / ".join(draw_cells), " / ".join(prim_cells)])
	lines.append("")
	if report.has("feature_ab"):
		var ab: Dictionary = report["feature_ab"]
		lines.append("## Feature A-B-A: `%s` (bit %d), shader mask 0x%x with the feature, 0x%x in feature_off" % [ab["spec"], int(ab["bit"]), int(ab["world_mask"]), int(ab["feature_off_mask"])])
		lines.append("")
		lines.append("Cost of the feature = baseline minus feature_off, root viewport GPU median ms, same boot, windows ordered baseline / feature_off / baseline_repeat. On the 4070 these are indicative; a delta above %.1f ms is flagged for the primary. A delta inside the noise floor is zero." % float(ab["flag_delta_ms"]))
		lines.append("")
		lines.append("| pose | time | on root GPU | off root GPU | cost (on - off) | far GPU cost | draws on - off | prims on - off | noise floor | flag |")
		lines.append("|---|---|---|---|---|---|---|---|---|---|")
		for record in report["pose_times"]:
			if not record.has("derived") or not record["derived"]["classes"].has("feature_off"):
				continue
			var d3: Dictionary = record["derived"]
			var c3: Dictionary = d3["classes"]["feature_off"]
			var cost := float(c3["root_gpu_delta_ms"])
			var flag := "FLAG" if cost > float(ab["flag_delta_ms"]) else ("within noise" if absf(cost) <= float(d3["noise_floor_root_gpu_ms"]) else "")
			lines.append("| %s | %s | %s | %s | %s | %s | %s | %s | %s | %s |" % [record["pose"], record["time"], _f(float(d3["baseline_root_gpu_ms"])), _f(float(c3["root_gpu_hidden_median_ms"])), _f(cost),
				_f(float(c3["far_gpu_delta_ms"])), _f(float(c3["draw_delta"]), 0), _f(float(c3["primitive_delta"]), 0), _f(float(d3["noise_floor_root_gpu_ms"])), flag])
		lines.append("")
	lines.append("## Far viewport GPU delta (ms)")
	lines.append("")
	lines.append("| pose | time | near terrain | far terrain | trees | grass | scatter |")
	lines.append("|---|---|---|---|---|---|---|")
	for record in report["pose_times"]:
		if not record.has("derived"):
			continue
		var c2: Dictionary = record["derived"]["classes"]
		lines.append("| %s | %s | %s | %s | %s | %s | %s |" % [record["pose"], record["time"], _f(float(c2["near_terrain_hidden"]["far_gpu_delta_ms"])), _f(float(c2["far_terrain_hidden"]["far_gpu_delta_ms"])),
			_f(float(c2["trees_hidden"]["far_gpu_delta_ms"])), _f(float(c2["grass_hidden"]["far_gpu_delta_ms"])), _f(float(c2["scatter_hidden"]["far_gpu_delta_ms"]))])
	lines.append("")
	lines.append("## GPU compute and bracket markers (median ms over baseline windows; n = windows with samples; a marker absent here did not run in the window, which is not zero)")
	lines.append("")
	lines.append("| pose | time | " + " | ".join(MARKERS) + " | opaque bracket |")
	lines.append("|---|---|" + "---|".repeat(MARKERS.size() + 1))
	for record in report["pose_times"]:
		if not record.has("derived"):
			continue
		var m: Dictionary = record["derived"]["markers"]
		var cells2: Array = []
		for name in MARKERS:
			cells2.append("%s (n=%d)" % [_f(float(m[name]["median_ms"])), int(m[name]["windows_with_samples"])])
		cells2.append("%s (n=%d)" % [_f(float(m["BRACKET:opaque"]["median_ms"])), int(m["BRACKET:opaque"]["windows_with_samples"])])
		lines.append("| %s | %s | %s |" % [record["pose"], record["time"], " | ".join(cells2)])
	lines.append("")
	lines.append("## Fill (CPU worker scopes from the teleport until builds were idle; wall time includes the 0.2 s teleport wait and the 2 s settle of go_pose)")
	lines.append("")
	lines.append("| pose | time | wall s | Trees build n / med / p95 / sum ms | Trees disk load n / med / p95 / sum ms | Grass build n / med / p95 / sum ms | Scatter build O | Scatter build B | Scatter build C | Habitat grid |")
	lines.append("|---|---|---|---|---|---|---|---|---|---|")
	for record in report["pose_times"]:
		if not record.has("fill"):
			continue
		var cells3: Array = []
		for name in FILL_SCOPES:
			var s: Dictionary = record["fill"]["scopes"][name]
			cells3.append("%d / %s / %s / %s" % [int(s["count"]), _f(float(s["median_ms"])), _f(float(s["p95_ms"])), _f(float(s["sum_ms"]), 0)])
		lines.append("| %s | %s | %s | %s |" % [record["pose"], record["time"], _f(float(record["fill"]["wall_s_incl_settle"]), 1), " | ".join(cells3)])
	lines.append("")
	var any_scatter := false
	for record in report["pose_times"]:
		if record.has("scatter_build"):
			any_scatter = true
	if any_scatter:
		lines.append("## Scatter build per tile (T5-10; the tile's own Trees build ms against its scatter worker ms, habitat grid included; tree disk cache must be off for real builds)")
		lines.append("")
		lines.append("| pose | time | tiles | Trees n / med / p95 | stage B tiles | (Trees+O+B)/Trees med / p95 / max | O+B extra med / p95 ms | O tiles / med / p95 ms | B med / p95 ms | C jobs | C per block med / p95 ms |")
		lines.append("|---|---|---|---|---|---|---|---|---|---|---|")
		for record in report["pose_times"]:
			if not record.has("scatter_build"):
				continue
			var b: Dictionary = record["scatter_build"]
			lines.append("| %s | %s | %d | %d / %s / %s | %d | %s / %s / %s | %s / %s | %d / %s / %s | %s / %s | %d | %s / %s |" % [record["pose"], record["time"], int(b["tiles"]), int(b["trees_n"]), _f(float(b["trees_median_ms"])), _f(float(b["trees_p95_ms"])),
				int(b["stage_b_with_tree"]), _f(float(b["ratio_median"])), _f(float(b["ratio_p95"])), _f(float(b["ratio_max"])), _f(float(b["ob_extra_median_ms"])), _f(float(b["ob_extra_p95_ms"])),
				int(b["o_tiles"]), _f(float(b["o_median_ms"])), _f(float(b["o_p95_ms"])), _f(float(b["b_median_ms"])), _f(float(b["b_p95_ms"])), int(b["c_jobs"]), _f(float(b["c_block_median_ms"])), _f(float(b["c_block_p95_ms"]))])
		lines.append("")
	lines.append("## Workload at the pose (after the fill)")
	lines.append("")
	lines.append("| pose | time | tree records | GPU tree roots | tree batches | grass instances | grass batches | hideable nodes: near / far / trees / grass |")
	lines.append("|---|---|---|---|---|---|---|---|")
	for record in report["pose_times"]:
		if not record.has("workload"):
			continue
		var w: Dictionary = record["workload"]
		var n: Dictionary = record["geometry_nodes"]
		lines.append("| %s | %s | %s | %s | %s | %s | %s | %d / %d / %d / %d |" % [record["pose"], record["time"], str(w["tree_canonical_records"]), str(w["tree_gpu_roots"]), str(w["tree_gpu_batches"]),
			str(w["grass_resident_instances"]), str(w["grass_batches"]), int(n["near_terrain"]), int(n["far_terrain"]), int(n["trees"]), int(n["grass"])])
	lines.append("")
	lines.append("## Power windows")
	lines.append("")
	var verdicts := {}
	for record in report["pose_times"]:
		for window in record.get("windows", []):
			var verdict := str(window["power"]["verdict"])
			verdicts[verdict] = int(verdicts.get(verdict, 0)) + 1
	for verdict in verdicts:
		lines.append("- %d windows: %s" % [int(verdicts[verdict]), verdict])
	if not (report["failures"] as Array).is_empty():
		lines.append("")
		lines.append("## Failures")
		lines.append("")
		for failure in report["failures"]:
			lines.append("- " + str(failure))
	lines.append("")
	return "\n".join(lines)


# ------------------------------------------------------------------ LOD-switch part

static func _teleport_to(ctx: Dictionary, around: float, across: float, vertical: float, yaw: float, pitch: float) -> void:
	(ctx["explorer"] as Node).call("TeleportToCanonicalMeters", around, vertical, across, yaw, pitch, true)


## Dolly 15 -> 60 m (0.25 m steps) toward the nearest tree ahead of forest_edge_eye, plus a fixed 15 m control. Frames go to .godot/captures/landscape/t0/lod_switch/<stamp>/{dolly,control}/.
static func run_lod(ctx: Dictionary) -> Dictionary:
	var tree: SceneTree = ctx["tree"]
	var marker: String = ctx["marker"]
	var pose := H.pose_by_name(COMMON.arg_value("--pose=", "forest_edge_eye"))
	if pose.is_empty():
		return {"status": "FAIL", "failure": "pose missing from the fixture"}
	var out_dir := H.transient_dir("lod_switch", ctx["booted"]["world_tag"])
	var info: Dictionary = await H.go_pose(tree, ctx["game"], pose, "noon", ctx["ground_model"])
	if info.has("error"):
		return {"status": "FAIL", "failure": str(info["error"])}
	await _disable_auto_exposure(ctx)
	var finder: RefCounted = TIMING.new()
	var found: Dictionary = finder.call("NearestTreeAhead", ctx["booted"]["world_bridge"], float(pose["around_m"]), float(pose["across_m"]), float(pose["yaw_deg"]), 8.0, 150.0, 8.0, LOD_START_M + LOD_STEPS * LOD_STEP_M + 3.0)
	if not bool(found.get("found", false)):
		return {"status": "FAIL", "failure": str(found.get("error", "no tree found")), "tree_search": found}
	var surface: Node = ctx["surface"]
	var tree_a := float(found["tree_around"])
	var tree_z := float(found["tree_across"])
	var tree_ground := float(surface.call("SampleHeight", tree_a, tree_z))
	var target_y := tree_ground + clampf(float(found["height"]) * 0.25, 2.0, 6.0)
	var bearing := deg_to_rad(float(found["bearing_deg"]))
	var dir_a := cos(bearing)
	var dir_z := sin(bearing)
	var record: Dictionary = (ctx["record"] as Dictionary).duplicate(true)
	record.merge({"marker": marker, "tool": "landscape_lod_switch_capture", "pose": pose["name"], "tree": found, "tree_ground_m": tree_ground, "target_height_above_ground_m": target_y - tree_ground,
		"dolly": {"frames": LOD_STEPS, "start_m": LOD_START_M, "step_m": LOD_STEP_M}, "output": out_dir, "frames": {"dolly": [], "control": []}})
	for sequence in ["dolly", "control"]:
		var dir_path := out_dir.path_join(sequence)
		DirAccess.make_dir_recursive_absolute(dir_path)
		# park at the first position and let the streaming and exposure settle before the sequence
		for step in LOD_STEPS:
			var distance := LOD_START_M + (step * LOD_STEP_M if sequence == "dolly" else 0.0)
			var cam_a := tree_a + dir_a * distance
			var cam_z := tree_z + dir_z * distance
			var cam_ground := float(surface.call("SampleHeight", cam_a, cam_z))
			var vertical := cam_ground + EYE_M
			var yaw := rad_to_deg(atan2(-dir_z, -dir_a))
			var pitch := rad_to_deg(atan2(target_y - vertical, distance))
			_teleport_to(ctx, cam_a, cam_z, vertical, yaw, pitch)
			if step == 0:
				await COMMON.wait_builds_idle(tree, ctx["game"], 120.0)
				await COMMON.settle(tree, 2.0)
				_teleport_to(ctx, cam_a, cam_z, vertical, yaw, pitch)
			await RenderingServer.frame_post_draw
			await RenderingServer.frame_post_draw
			var image := tree.root.get_texture().get_image()
			image.save_png(dir_path.path_join("frame%08d.png" % step))
			(record["frames"][sequence] as Array).append({"frame": step, "horizontal_distance_m": distance, "distance_3d_m": sqrt(distance * distance + (target_y - vertical) * (target_y - vertical)),
				"camera_vertical_m": vertical, "yaw_deg": yaw, "pitch_deg": pitch, "weather": H.weather_at(ctx["game"], cam_a, cam_z)})
	record["status"] = "PASS"
	_save_json(out_dir.path_join("frames.json"), record)
	print("%s tree: %s" % [marker, JSON.stringify(found)])
	print("%s output: %s" % [marker, out_dir])
	return record


# ------------------------------------------------------------------ TR-1 tree LOD part (docs/work-orders/terrain-vegetation/TR.md)

static func _tree_fade(trees: Node) -> Dictionary:
	return {"compact_profile": trees.get("UseCompactTreeLodProfile"), "near_start_m": trees.get("TreeNearDetailFadeStartMeters"), "near_end_m": trees.get("TreeNearDetailFadeEndMeters"),
		"billboard_start_m": trees.get("TreeBillboardFadeStartMeters"), "billboard_end_m": trees.get("TreeBillboardFadeEndMeters"), "mid_span": trees.get("MidTreeBatchTileSpan"),
		"sun_shadow_distance_m": trees.get("TreeSunShadowDistanceMeters"), "maximum_gpu_tree_roots": trees.get("MaximumGpuTreeRoots"),
		"maximum_lod1_trees": trees.get("MaximumLod1Trees"), "maximum_lod2_trees": trees.get("MaximumLod2Trees"), "maximum_lod3_trees": trees.get("MaximumLod3Trees"),
		"maximum_lod4_trees": trees.get("MaximumLod4Trees"), "maximum_tree_billboards": trees.get("MaximumTreeBillboards")}


## Sets exported properties of the tree layer for this run only ("Name:value,Name:value"). Returns {name: {from, to}} or {error}.
static func _apply_tree_settings(trees: Node, settings: Dictionary) -> Dictionary:
	var applied := {}
	for name in settings:
		var before: Variant = trees.get(str(name))
		if before == null:
			return {"error": "the tree layer has no property %s" % name}
		trees.set(str(name), type_convert(settings[name], typeof(before)))
		applied[name] = {"from": before, "to": trees.get(str(name))}
	return applied


static func _parse_tree_settings(text: String) -> Dictionary:
	var settings := {}
	for item in text.split(",", false):
		var pair := item.split(":", false)
		if pair.size() == 2:
			settings[pair[0]] = pair[1].to_float()
	return settings


## Streaming idle, the wanted GPU tree outputs installed, no source preparation, no pending shadow proxy and no fallback output, for 30 frames in a row.
static func _wait_trees_settled(ctx: Dictionary, timeout_s: float) -> bool:
	var tree: SceneTree = ctx["tree"]
	var trees: Node = ctx["trees"]
	var deadline := Time.get_ticks_msec() + int(timeout_s * 1000.0)
	var stable := 0
	while Time.get_ticks_msec() < deadline:
		await tree.process_frame
		var settled: bool = COMMON.builds_idle(ctx["game"]) and bool(trees.get("GpuTreeCullingSettled")) and not bool(trees.get("TreePreparationPending")) \
			and int(trees.get("PendingGpuTreeShadowProxyCount")) == 0 and int(trees.get("GpuTreeFallbackBatchCount")) == 0
		stable = stable + 1 if settled else 0
		if stable >= 30:
			return true
	return false


## Budget chain at the current camera (tools/RingBiomeDecorationLayer.TreeBudgetProbe.cs): records -> GPU roots -> band -> rank gate -> cap -> drawn, per slot.
static func _probe(ctx: Dictionary) -> Dictionary:
	var trees: Node = ctx["trees"]
	if not trees.has_method("TreeBudgetProbeBegin") or not bool(trees.call("TreeBudgetProbeBegin")):
		return {"ok": false, "error": "tree budget probe unavailable (GPU tree path not running)"}
	for i in 900:
		await RenderingServer.frame_post_draw
		var result: Dictionary = trees.call("TreeBudgetProbeTake")
		if not result.is_empty():
			return result
	return {"ok": false, "error": "tree budget probe readback did not finish in 900 frames"}


static func _memory() -> Dictionary:
	return {"video_mem_mb": Performance.get_monitor(Performance.RENDER_VIDEO_MEM_USED) / 1048576.0, "texture_mem_mb": Performance.get_monitor(Performance.RENDER_TEXTURE_MEM_USED) / 1048576.0,
		"buffer_mem_mb": Performance.get_monitor(Performance.RENDER_BUFFER_MEM_USED) / 1048576.0, "static_mem_mb": Performance.get_monitor(Performance.MEMORY_STATIC) / 1048576.0}


static func _image_mean(image: Image) -> Array:
	var copy := image.duplicate() as Image
	if copy.get_format() != Image.FORMAT_RGBA8:
		copy.convert(Image.FORMAT_RGBA8)
	var total := Vector3.ZERO
	var count := 0
	var step_x := maxi(1, copy.get_width() / 192)
	var step_y := maxi(1, copy.get_height() / 108)
	for y in range(0, copy.get_height(), step_y):
		for x in range(0, copy.get_width(), step_x):
			var c := copy.get_pixel(x, y)
			total += Vector3(c.r, c.g, c.b)
			count += 1
	total /= maxf(1.0, float(count))
	return [total.x, total.y, total.z]


## Normal frame plus Viewport.DEBUG_DRAW_OVERDRAW frames (all, trees hidden, trees only, trees only without slot 0 or slot 2). The engine's overdraw view replaces every material, so
## geometry whose vertices are placed by its shader (the shared far impostor of slots 3 and 4, texelFetch of vertex_data) does not appear in it.
static func _capture_pictures(ctx: Dictionary, state: Dictionary, out_dir: String, key: String) -> Dictionary:
	var root: Window = (ctx["tree"] as SceneTree).root
	var shots := {}
	# "trees only" hides the near terrain, the far terrain and the grass, whose own overdraw otherwise saturates the picture.
	var others := ["near_terrain_hidden", "far_terrain_hidden", "grass_hidden"]
	var plan := [["normal", ["baseline"], Viewport.DEBUG_DRAW_DISABLED], ["overdraw", ["baseline"], Viewport.DEBUG_DRAW_OVERDRAW], ["overdraw_trees_hidden", ["trees_hidden"], Viewport.DEBUG_DRAW_OVERDRAW],
		["overdraw_trees_only", others, Viewport.DEBUG_DRAW_OVERDRAW], ["overdraw_trees_only_slot0_hidden", others + ["tree_slot0_hidden"], Viewport.DEBUG_DRAW_OVERDRAW],
		["overdraw_trees_only_slot2_hidden", others + ["tree_slot2_hidden"], Viewport.DEBUG_DRAW_OVERDRAW]]
	for item in plan:
		restore_state(state)
		for case_label in item[1]:
			apply_case(state, str(case_label))
		root.debug_draw = item[2]
		for i in 45:
			await RenderingServer.frame_post_draw
		var image := root.get_texture().get_image()
		var path := out_dir.path_join("%s__%s.png" % [key, item[0]])
		image.save_png(path)
		shots[item[0]] = {"png": path, "mean_rgb": _image_mean(image)}
	root.debug_draw = Viewport.DEBUG_DRAW_DISABLED
	restore_state(state)
	for i in 10:
		await RenderingServer.frame_post_draw
	return shots


## `--tree-lod=slots`: per-slot hide cases, tree-shadow-off, the 50 % render-scale fill test, the overdraw pictures and the budget probe at the forest and coast poses.
## `--tree-lod=cap`: the same cases and probe at the three forest poses for each root-cap configuration (`--cap-configs=65536,262144,262144+far`; "+far" also raises
## MaximumLod4Trees and MaximumTreeBillboards to their limits). Output: .godot/audit_baseline/<machine>/tr1/tree_lod_<mode>/<stamp>/tree_lod.json and tree_lod.md.
## Other options: --poses=a,b  --rounds=2  --seconds=8  --rs50=<pose__time,...|none>  --overdraw=<pose__time,...|none>  --tree-set=Property:value,...
static func run_tree_lod(ctx: Dictionary) -> Dictionary:
	var tree: SceneTree = ctx["tree"]
	var marker: String = ctx["marker"]
	var machine: Dictionary = ctx["machine"]
	var trees: Node = ctx["trees"]
	var mode := harness_arg("--tree-lod=", "slots")
	if mode != "slots" and mode != "cap":
		return {"status": "FAIL", "failure": "unknown --tree-lod=%s (slots|cap)" % mode}
	var rounds := maxi(1, int(harness_arg("--rounds=", "2")))
	var seconds := maxf(1.0, float(harness_arg("--seconds=", "8")))
	var out_dir := H.baseline_dir(str(machine["tag"]), "tr1", "tree_lod_" + mode, ctx["booted"]["world_tag"])
	H.copy_prefs_snapshot(ctx["booted"], out_dir)
	var overrides := _apply_tree_settings(trees, _parse_tree_settings(harness_arg("--tree-set=", "")))
	if overrides.has("error"):
		return {"status": "FAIL", "failure": str(overrides["error"])}
	if not overrides.is_empty():
		await COMMON.wait_builds_idle(tree, ctx["game"], 300.0)
	var power_path := out_dir.path_join("power_log.csv")
	ctx["power_pid"] = COMMON.start_power_log(power_path)
	_viewports(ctx)
	var timing: RefCounted = TIMING.new()
	timing.call("Begin", true)
	ctx["timing"] = timing
	var bracket := await _install_bracket(ctx)
	print("%s opaque bracket: %s" % [marker, JSON.stringify(bracket)])
	await COMMON.settle(tree, 1.5)

	var failures: Array = []
	var names := harness_csv("--poses=")
	if names.is_empty():
		names = PackedStringArray(TREE_LOD_POSES if mode == "slots" else TREE_LOD_POSES.slice(0, 3))
	var pose_times: Array = []
	for name in names:
		var pose := H.pose_by_name(name)
		if pose.is_empty():
			failures.append("pose %s is not in the fixture" % name)
			continue
		pose_times.append([pose, "noon"])
	if mode == "slots":
		for extra in TREE_LOD_EXTRA:
			if str(extra[0]) in names:
				pose_times.append([H.pose_by_name(str(extra[0])), str(extra[1])])
	var rs50_keys := harness_csv("--rs50=")
	if rs50_keys.is_empty() and mode == "slots":
		rs50_keys = PackedStringArray(["forest_edge_eye__noon"])
	var overdraw_keys := harness_csv("--overdraw=")
	if overdraw_keys.is_empty() and mode == "slots":
		overdraw_keys = PackedStringArray(["forest_edge_eye__noon", "forest_interior_eye__noon"])

	# root-cap configurations: one ("as set") in slots mode
	var original := {"MaximumGpuTreeRoots": trees.get("MaximumGpuTreeRoots"), "MaximumLod4Trees": trees.get("MaximumLod4Trees"), "MaximumTreeBillboards": trees.get("MaximumTreeBillboards")}
	var configs: Array = []
	if mode == "cap":
		var wanted := harness_csv("--cap-configs=")
		if wanted.is_empty():
			wanted = PackedStringArray(["65536", "262144", "262144+far"])
		for item in wanted:
			var settings: Dictionary = original.duplicate()
			settings["MaximumGpuTreeRoots"] = int(item.trim_suffix("+far"))
			if item.ends_with("+far"):
				settings["MaximumLod4Trees"] = 16384
				settings["MaximumTreeBillboards"] = 65536
			configs.append({"name": "roots_" + item, "settings": settings})
	else:
		configs.append({"name": "as_set", "settings": {}})
	print("%s tree-lod mode %s: %d pose-times x %d configs x %d rounds x %d cases, windows of %.1f s, fades %s" % [marker, mode, pose_times.size(), configs.size(), rounds, TREE_LOD_CASES.size(), seconds,
		JSON.stringify(_tree_fade(trees))])

	var results: Array = []
	var windows_total := 0
	for entry in pose_times:
		var pose: Dictionary = entry[0]
		var time_name: String = entry[1]
		var key := "%s__%s" % [pose["name"], time_name]
		timing.call("ResetWindow")
		var sampler := Callable(timing, "Sample")
		tree.process_frame.connect(sampler)
		var fill_start := Time.get_ticks_msec()
		var info: Dictionary = await H.go_pose(tree, ctx["game"], pose, time_name, ctx["ground_model"])
		var fill_wall_s := (Time.get_ticks_msec() - fill_start) / 1000.0
		tree.process_frame.disconnect(sampler)
		var record := {"key": key, "pose": pose["name"], "time": time_name, "go_pose": info, "weather": H.weather_at(ctx["game"], float(pose["around_m"]), float(pose["across_m"])), "configs": []}
		if info.has("error"):
			failures.append("%s: %s" % [key, str(info["error"])])
			record["failure"] = str(info["error"])
			results.append(record)
			continue
		var cpu_window: Dictionary = timing.call("CpuWindow")
		record.merge({"sun_elevation_deg": info.get("sun_elevation_deg", NAN), "fill": {"wall_s_incl_settle": fill_wall_s, "scopes": _fill_summary(cpu_window)}})
		for config_index in configs.size():
			var config: Dictionary = configs[config_index]
			var applied := _apply_tree_settings(trees, config["settings"])
			var settle_start := Time.get_ticks_msec()
			var settled := await _wait_trees_settled(ctx, 420.0)
			var settle_s := (Time.get_ticks_msec() - settle_start) / 1000.0
			var probe: Dictionary = await _probe(ctx)
			if not bool(probe.get("ok", false)):
				failures.append("%s %s: %s" % [key, config["name"], str(probe.get("error", "probe failed"))])
			var state := capture_original_state(ctx)
			var nodes := {"templates_and_other": 0, "shadow_proxies": 0}
			for index in state["tree_geometry"].size():
				var slot := int(state["tree_slot"][index])
				if bool(state["tree_proxy"][index]):
					nodes["shadow_proxies"] = int(nodes["shadow_proxies"]) + 1
				elif slot < 0:
					nodes["templates_and_other"] = int(nodes["templates_and_other"]) + 1
				else:
					nodes["slot%d" % slot] = int(nodes.get("slot%d" % slot, 0)) + 1
			var cases: Array = TREE_LOD_CASES.duplicate()
			if config_index == 0 and key in rs50_keys:
				for label in TREE_LOD_CASES:
					cases.append(str(label) + RS_SUFFIX)
			nodes["unclassified_visible"] = state["tree_unclassified_visible"]
			var config_record := {"name": config["name"], "settings_applied": applied, "settled": settled, "settle_s": settle_s, "tree_settings": _tree_fade(trees), "probe": probe,
				"workload": _workload(ctx), "memory": _memory(), "tree_nodes": nodes, "cases": cases, "windows": [], "viewport": {"scaling_3d_scale": tree.root.scaling_3d_scale,
				"scaling_3d_mode": tree.root.scaling_3d_mode, "msaa_3d": tree.root.msaa_3d, "use_taa": tree.root.use_taa, "screen_space_aa": tree.root.screen_space_aa}}
			print("%s %s %s settled=%s (%.1f s) nodes %s probe slots %s" % [marker, key, config["name"], str(settled), settle_s, JSON.stringify(nodes), _probe_line(probe)])
			var pose_before: Dictionary = COMMON.pose_now(ctx["game"])
			for round_index in rounds:
				var order: Array = cases.duplicate()
				if round_index % 2 == 1:
					order.reverse()
				for label in order:
					restore_state(state)
					if not apply_case(state, str(label)):
						failures.append("%s: unknown case %s" % [key, label])
						continue
					var window: Dictionary = await sample_window(ctx, seconds)
					var why := verify_case(state, str(label))
					window["label"] = label
					window["round"] = round_index
					window["state_ok"] = why == ""
					if why != "":
						window["failure"] = why
						failures.append("%s %s %s round %d: %s" % [key, config["name"], label, round_index, why])
					(config_record["windows"] as Array).append(window)
					windows_total += 1
			restore_state(state)
			# pose guard: the camera must not move or turn during the window set (a drifting pose would turn the hide deltas into noise)
			var pose_after: Dictionary = COMMON.pose_now(ctx["game"])
			var moved := Vector3(float(pose_after["around"]) - float(pose_before["around"]), float(pose_after["vertical"]) - float(pose_before["vertical"]), float(pose_after["across"]) - float(pose_before["across"])).length()
			var turned := maxf(absf(float(pose_after["yaw"]) - float(pose_before["yaw"])), absf(float(pose_after["pitch"]) - float(pose_before["pitch"])))
			config_record["pose_guard"] = {"before": pose_before, "after": pose_after, "moved_m": moved, "turned": turned, "fixture_yaw_deg": pose["yaw_deg"], "fixture_pitch_deg": pose["pitch_deg"]}
			if moved > 0.5 or turned > 0.1:
				failures.append("%s %s: the camera moved %.2f m and turned %.3f during the windows" % [key, config["name"], moved, turned])
			config_record["derived"] = _derive(config_record["windows"], cases)
			if config_index == 0 and key in overdraw_keys:
				config_record["pictures"] = await _capture_pictures(ctx, state, out_dir, key)
			(record["configs"] as Array).append(config_record)
			print("%s %s %s derived %s" % [marker, key, config["name"], JSON.stringify(config_record["derived"]["summary_line"])])
		if mode == "cap":
			_apply_tree_settings(trees, original)
			await _wait_trees_settled(ctx, 420.0)
		results.append(record)
		_save_json(out_dir.path_join("tree_lod_partial.json"), {"completed": results.size(), "of": pose_times.size(), "pose_times": results})

	COMMON.stop_power_log(int(ctx["power_pid"]))
	ctx["power_pid"] = -1
	if ctx.has("bracket_effects"):
		var remaining: Array = (ctx["world_environment"] as WorldEnvironment).compositor.compositor_effects.duplicate()
		for effect in ctx["bracket_effects"]:
			remaining.erase(effect)
		(ctx["world_environment"] as WorldEnvironment).compositor.compositor_effects = remaining
		ctx.erase("bracket_effects")
	timing.call("End")
	ctx.erase("timing")
	for i in 10:
		await RenderingServer.frame_post_draw
	await COMMON.settle(tree, 0.5)
	# a hide case that changed no draw or primitive count at any pose cannot be measured (TR-1 escalation condition)
	var case_effect := {}
	for label in TREE_LOD_CASES:
		if str(label).begins_with("baseline"):
			continue
		case_effect[label] = 0
		# TR-8: slot 1 is only drawn with the render LODs; the old renderer (UseTreeRenderLods:0) has nothing to hide there
		if label == "tree_slot1_hidden" and overrides.has("UseTreeRenderLods") and not bool(trees.get("UseTreeRenderLods")):
			case_effect[label] = 1
			continue
		for record in results:
			for config_record in record.get("configs", []):
				if bool(config_record["derived"]["classes"][label]["changed_submissions"]):
					case_effect[label] = int(case_effect[label]) + 1
		if int(case_effect[label]) == 0 and not results.is_empty():
			failures.append("case %s changed no draw or primitive count at any pose (escalate: the slot cannot be hidden on its own)" % label)
	var report: Dictionary = (ctx["record"] as Dictionary).duplicate(true)
	report.merge({"marker": marker, "tool": "landscape_cost_baseline --tree-lod=" + mode, "mode": mode, "label": "4070, non-authoritative" if not bool(machine["authoritative"]) else "A1000",
		"rounds": rounds, "window_seconds": seconds, "warmup_frames": WARMUP_FRAMES, "settle_s": SETTLE_S, "bracket": bracket, "cases": TREE_LOD_CASES, "tree_overrides": overrides,
		"tree_settings": _tree_fade(trees), "pose_times": results, "failures": failures, "windows": windows_total, "case_effect_poses": case_effect,
		"status": "FAIL" if not failures.is_empty() or results.is_empty() else "PASS", "output": out_dir, "power_log": power_path}, true)
	_save_json(out_dir.path_join("tree_lod.json"), report)
	var file := FileAccess.open(out_dir.path_join("tree_lod.md"), FileAccess.WRITE)
	if file != null:
		file.store_string(_tree_markdown(report))
		file.close()
	DirAccess.remove_absolute(out_dir.path_join("tree_lod_partial.json"))
	print("%s output: %s" % [marker, out_dir])
	return report


static func _probe_line(probe: Dictionary) -> String:
	if not bool(probe.get("ok", false)):
		return str(probe.get("error", "no probe"))
	var parts: Array = []
	for slot in probe.get("slots", []):
		if bool(slot["enabled"]):
			parts.append("s%d %d/%d/%d (cap %d)" % [int(slot["slot"]), int(slot["roots_in_range"]), int(slot["gated_in_range"]), int(slot["drawn"]), int(slot["cap"])])
	return "roots %d of %d visible records; in range/gated/drawn: %s" % [int(probe["gpu_roots"]), int(probe["visible_records"]), ", ".join(parts)]


static func _tree_markdown(report: Dictionary) -> String:
	var machine: Dictionary = report["machine"]
	var lines: Array = []
	lines.append("# TR-1 tree LOD cost (%s, mode %s)" % [report["label"], report["mode"]])
	lines.append("")
	lines.append("Machine: %s, adapter `%s`, %s, Godot %s. Resolution %s, VSync off, preset `%s`. Rounds %d, window %.0f s (+%d warm-up frames, %.0f s settle). Prefs `%s`. Clocks held, weather %s." % [
		machine["label"], machine["adapter"], machine["renderer"], report["godot"], str(report["resolution"]), report["preset"].get("preset", "?"), int(report["rounds"]),
		float(report["window_seconds"]), int(report["warmup_frames"]), float(report["settle_s"]), report["prefs"]["mode"], str(report["weather"].get("mode", "?"))])
	lines.append("")
	lines.append("Tree settings: `%s`. Overrides for this run: `%s`." % [JSON.stringify(report["tree_settings"]), JSON.stringify(report["tree_overrides"])])
	lines.append("")
	lines.append("Status %s, %d windows. A slot case hides the slot's main-pass renderers only; tree_shadows_off removes the tree shadow casters only. Deltas are baseline minus case (median of window medians)." % [report["status"], int(report["windows"])])
	for record in report["pose_times"]:
		for config in record.get("configs", []):
			var d: Dictionary = config["derived"]
			lines.append("")
			lines.append("## %s, %s (sun %s deg), config %s" % [record["pose"], record["time"], _f(float(record.get("sun_elevation_deg", NAN)), 1), config["name"]])
			lines.append("")
			lines.append("Baseline root GPU %s ms (p95 %s, repeat %s, noise floor %s), frame %s ms, opaque bracket %s ms, draws %s, primitives %s. Settled %s in %s s. Video memory %s MB, tree GPU buffers %s MB." % [
				_f(d["baseline_root_gpu_ms"]), _f(d["baseline_p95_root_gpu_ms"]), _f(d["baseline_repeat_root_gpu_ms"]), _f(d["noise_floor_root_gpu_ms"]), _f(d["baseline_frame_ms"]),
				_f(float(d["markers"]["BRACKET:opaque"]["median_ms"])), _f(d["baseline_draw_calls"], 0), _f(d["baseline_primitives"], 0), str(config["settled"]), _f(float(config["settle_s"]), 1),
				_f(float(config["memory"]["video_mem_mb"]), 0), _f(float(config["probe"].get("gpu_buffer_bytes", 0)) / 1048576.0, 1)])
			lines.append("")
			lines.append("| case | root GPU delta ms | opaque bracket delta ms | far GPU delta ms | draws delta | primitives delta |")
			lines.append("|---|---|---|---|---|---|")
			for label in config["cases"]:
				if d["classes"].has(label) and not str(label).ends_with(RS_SUFFIX):
					var c: Dictionary = d["classes"][label]
					lines.append("| %s | %s | %s | %s | %s | %s |" % [label, _f(float(c["root_gpu_delta_ms"])), _f(float(c["opaque_bracket_delta_ms"])), _f(float(c["far_gpu_delta_ms"])), _f(float(c["draw_delta"]), 0), _f(float(c["primitive_delta"]), 0)])
			if d["render_scales"].has("baseline" + RS_SUFFIX):
				var half: Dictionary = d["render_scales"]["baseline" + RS_SUFFIX]
				lines.append("")
				lines.append("Fill test at 50 %% render scale (a quarter of the pixels): baseline %s ms (noise floor %s). Fill part = (delta at 100 %% - delta at 50 %%) / 0.75; the rest does not scale with pixels." % [
					_f(float(half["baseline_root_gpu_ms"])), _f(float(half["noise_floor_root_gpu_ms"]))])
				lines.append("")
				lines.append("| case | delta at 100 % | delta at 50 % | ratio | fill part ms | pixel-independent part ms |")
				lines.append("|---|---|---|---|---|---|")
				for label in TREE_LOD_CASES:
					if d["classes"].has(label) and d["classes"].has(str(label) + RS_SUFFIX):
						var full := float(d["classes"][label]["root_gpu_delta_ms"])
						var scaled := float(d["classes"][str(label) + RS_SUFFIX]["root_gpu_delta_ms"])
						var fill := (full - scaled) / (1.0 - RS_SCALE * RS_SCALE)
						lines.append("| %s | %s | %s | %s | %s | %s |" % [label, _f(full), _f(scaled), _f(scaled / full if absf(full) > 0.0001 else NAN), _f(fill), _f(full - fill)])
			var probe: Dictionary = config["probe"]
			if bool(probe.get("ok", false)):
				var cap: Dictionary = probe["root_cap"]
				lines.append("")
				lines.append("Budget chain: %d records in %d visible tiles (%d inside the far edge %s m), %d GPU roots of a cap of %d (%d inside the far edge). %d visible records are not roots (%d inside the far edge); the nearest of them is at %s m, the farthest root at %s m, and %d roots lie beyond the nearest dropped record." % [
					int(probe["visible_records"]), int(probe["visible_tiles"]), int(probe["visible_records_within_far_edge"]), _f(float(probe["far_edge_m"]), 0), int(probe["gpu_roots"]), int(probe["maximum_gpu_tree_roots"]),
					int(probe["gpu_roots_within_far_edge"]), int(cap["visible_not_root"]), int(cap["visible_not_root_within_far_edge"]), _f(float(cap["nearest_dropped_m"]), 0), _f(float(cap["farthest_root_m"]), 0),
					int(cap["roots_beyond_nearest_dropped"])])
				lines.append("")
				lines.append("| slot | band m | rank gate | cap | visible records in band | GPU roots in band | after rank gate | drawn | partial dither | outputs | triangles per tree | submitted triangles | shadow casters | shadow triangles |")
				lines.append("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|")
				for slot in probe["slots"]:
					if not bool(slot["enabled"]):
						continue
					lines.append("| %d | %s to %s | %s | %d | %d | %d | %d | %d | %d | %d | %d to %d | %d | %d | %d |" % [int(slot["slot"]), _f(float(slot["near_m"]), 0), _f(float(slot["far_m"]), 0), _f(float(slot["rank_gate"])),
						int(slot["cap"]), int(slot["visible_in_range"]), int(slot["roots_in_range"]), int(slot["gated_in_range"]), int(slot["drawn"]), int(slot["drawn_partial_coverage"]), int(slot["outputs"]),
						int(slot["mesh_triangles_min"]), int(slot["mesh_triangles_max"]), int(slot["submitted_triangles"]), int(slot["shadow_proxies"]) + int(slot["self_shadow_outputs"]), int(slot["shadow_triangles"])])
				lines.append("")
				lines.append("Distance histogram (bin start in m; g = GPU roots passing the slot's band and rank gate, d = drawn):")
				lines.append("")
				lines.append("| from m | visible records | GPU roots | dropped by root cap | s0 g / d | s2 g / d | s3 g / d | s4 g / d |")
				lines.append("|---|---|---|---|---|---|---|---|")
				var edges: Array = probe["bin_edges_m"]
				for bin in edges.size():
					var cells: Array = []
					for slot_index in [0, 2, 3, 4]:
						var slot: Dictionary = probe["slots"][slot_index]
						cells.append("%d / %d" % [int(slot["histogram_gated"][bin]), int(slot["histogram_drawn"][bin])])
					lines.append("| %s | %d | %d | %d | %s |" % [_f(float(edges[bin]), 0), int(probe["histogram_visible"][bin]), int(probe["histogram_roots"][bin]), int(probe["histogram_visible_not_root"][bin]), " | ".join(cells)])
			if config.has("pictures"):
				lines.append("")
				for name in config["pictures"]:
					lines.append("- picture %s: `%s` (mean rgb %s)" % [name, config["pictures"][name]["png"], str(config["pictures"][name]["mean_rgb"])])
	if not (report["failures"] as Array).is_empty():
		lines.append("")
		lines.append("## Failures")
		lines.append("")
		for failure in report["failures"]:
			lines.append("- " + str(failure))
	lines.append("")
	return "\n".join(lines)


# ------------------------------------------------------------------ TR-1 switch strip

## Five camera distances across a billboard fade band: 50 m before it, its start, its middle, its end and 50 m after it.
static func _strip_distances(start_m: float, end_m: float) -> Array:
	return [start_m - 50.0, start_m, (start_m + end_m) * 0.5, end_m, end_m + 50.0]


## `--strip`: one isolated maple, pine and fir, watched from five distances across the LOD2-to-cards fade at noon and at sun 8 degrees, for each fade configuration
## (`--strip-configs=user,f1`: "user" = the fades as loaded from the preferences and the preset, "f1" = the shipped 300 to 350 m fade of TR.md fact F1).
## Frames: .godot/captures/landscape/t0/lod_switch_strip/<stamp>/<config>/<species>/{tight,wide}/frame%08d.png, index = time * 5 + distance. tight = the centre
## 192 x 192 pixels enlarged 3 x without filtering; wide = the centre 576 x 324 pixels as rendered. strip.json records the tree, its rank and every frame.
static func run_strip(ctx: Dictionary) -> Dictionary:
	var tree: SceneTree = ctx["tree"]
	var marker: String = ctx["marker"]
	var trees: Node = ctx["trees"]
	var game: Node = ctx["game"]
	var world: RefCounted = ctx["booted"]["world_bridge"]
	var out_dir := H.transient_dir("lod_switch_strip", ctx["booted"]["world_tag"])
	var times: Dictionary = H.load_poses().get("times", {})
	var time_names := ["noon", "sun_8_set"]
	var original := {}
	for property in TREE_FADE_PROPERTIES:
		original[property] = trees.get(property)
	var config_names := harness_csv("--strip-configs=")
	if config_names.is_empty():
		config_names = PackedStringArray(["user", "f1"])
	var configs: Array = []
	var all_distances := PackedFloat64Array()
	for name in config_names:
		var fades: Dictionary = original.duplicate()
		if name == "f1":
			fades = {"TreeNearDetailFadeStartMeters": 25.0, "TreeNearDetailFadeEndMeters": 40.0, "TreeBillboardFadeStartMeters": 300.0, "TreeBillboardFadeEndMeters": 350.0,
				"TreeMidDetailFadeStartMeters": 130.0, "TreeMidDetailFadeEndMeters": 150.0}
		elif name != "user":
			return {"status": "FAIL", "failure": "unknown --strip-configs entry %s (user|f1)" % name}
		var distances := _strip_distances(float(fades["TreeBillboardFadeStartMeters"]), float(fades["TreeBillboardFadeEndMeters"]))
		# `--strip-distances=a,b,c,d,e` replaces the five distances (TR-5: 110,130,140,150,170 across the LOD2 to reduced-detail hand-off).
		var distance_override := harness_csv("--strip-distances=")
		if distance_override.size() == 5:
			distances = []
			for entry in distance_override:
				distances.append(float(entry))
		for distance in distances:
			all_distances.append(float(distance))
		configs.append({"name": name, "fades": fades, "distances": distances})
	var finder: RefCounted = TIMING.new()
	var search_start := Time.get_ticks_msec()
	var found := {}
	for site in STRIP_SITES:
		var at_site: Dictionary = finder.call("FindStripTrees", world, PackedFloat64Array([float(site[0])]), PackedFloat64Array([float(site[1])]), all_distances, 0.33, 10.0)
		for species in STRIP_SPECIES:
			var candidate: Dictionary = at_site.get(species, {})
			if found.has(species) or not bool(candidate.get("found", false)):
				continue
			var candidate_bearing := deg_to_rad(float(candidate["bearing_deg"]))
			var candidate_target := float(world.call("GroundHeight", candidate["tree_around"], candidate["tree_across"])) + 0.5 * float(candidate["height"])
			var steepest := 0.0
			for distance in all_distances:
				var eye := float(world.call("GroundHeight", float(candidate["tree_around"]) + cos(candidate_bearing) * distance, float(candidate["tree_across"]) + sin(candidate_bearing) * distance)) + EYE_M
				steepest = maxf(steepest, absf(rad_to_deg(atan2(candidate_target - eye, distance))))
			candidate["max_pitch_deg"] = steepest
			candidate["site"] = site
			if steepest <= STRIP_MAX_PITCH_DEG and float(candidate["clearance_m"]) >= 4.0:
				found[species] = candidate
		if found.size() == STRIP_SPECIES.size():
			break
	print("%s strip trees (%.1f s): %s" % [marker, (Time.get_ticks_msec() - search_start) / 1000.0, JSON.stringify(found)])
	var record: Dictionary = (ctx["record"] as Dictionary).duplicate(true)
	record.merge({"marker": marker, "tool": "landscape_lod_switch_capture --strip", "trees": found, "sites": STRIP_SITES, "configs": configs, "output": out_dir, "frames": [],
		"crop": {"tight_px": STRIP_TIGHT_PX, "tight_scale": STRIP_TIGHT_SCALE, "wide_px": [STRIP_WIDE.x, STRIP_WIDE.y]}, "rank_gates": "slot 3 keeps rank <= 0.35, slot 4 rank <= 0.10"}, true)
	var failures: Array = []
	var exposure_disabled := false
	# the HUD crosshair sits on the screen centre, exactly where the tree is; hide the HUD layers for these captures
	var hidden_layers: Array = []
	for node in _all_nodes(game):
		if node is CanvasLayer and (node.name == "TerrainMinimap" or (node.get_script() != null and "RingPresentationHud" in (node.get_script() as Script).resource_path)):
			(node as CanvasLayer).visible = false
			hidden_layers.append(str(node.name))
	record["hidden_hud_layers"] = hidden_layers
	for config in configs:
		_apply_tree_settings(trees, config["fades"])
		for species in STRIP_SPECIES:
			var info: Dictionary = found.get(species, {})
			if not bool(info.get("found", false)):
				failures.append("no %s with a clear line found" % species)
				continue
			var tree_a := float(info["tree_around"])
			var tree_z := float(info["tree_across"])
			var tree_ground := float(world.call("GroundHeight", tree_a, tree_z))
			var target_y := tree_ground + 0.5 * float(info["height"])
			var bearing := deg_to_rad(float(info["bearing_deg"]))
			var dir_a := cos(bearing)
			var dir_z := sin(bearing)
			for time_index in time_names.size():
				var time_name: String = time_names[time_index]
				var sun := NAN
				var distances: Array = config["distances"]
				for index in distances.size():
					var distance := float(distances[index])
					var cam_a := tree_a + dir_a * distance
					var cam_z := tree_z + dir_z * distance
					var vertical := float(world.call("GroundHeight", cam_a, cam_z)) + EYE_M
					var yaw := rad_to_deg(atan2(-dir_z, -dir_a))
					var pitch := rad_to_deg(atan2(target_y - vertical, distance))
					_teleport_to(ctx, cam_a, cam_z, vertical, yaw, pitch)
					await COMMON.settle(tree, 0.2)
					var idle := await COMMON.wait_builds_idle(tree, game, 240.0)
					if index == 0:
						var time_state: Dictionary = COMMON.apply_time(game, times[time_name])
						if time_state.has("error"):
							failures.append("%s %s %s: %s" % [config["name"], species, time_name, str(time_state["error"])])
						sun = float(time_state.get("sun_elevation_deg", NAN))
						await COMMON.settle(tree, 2.0)
						if not exposure_disabled:
							await _disable_auto_exposure(ctx)
							exposure_disabled = true
					var settled := await _wait_trees_settled(ctx, 90.0)
					_teleport_to(ctx, cam_a, cam_z, vertical, yaw, pitch)
					for i in 6:
						await RenderingServer.frame_post_draw
					var image := tree.root.get_texture().get_image()
					var centre := Vector2i(image.get_width() / 2, image.get_height() / 2)
					var tight := image.get_region(Rect2i(centre.x - STRIP_TIGHT_PX / 2, centre.y - STRIP_TIGHT_PX / 2, STRIP_TIGHT_PX, STRIP_TIGHT_PX))
					tight.resize(STRIP_TIGHT_PX * STRIP_TIGHT_SCALE, STRIP_TIGHT_PX * STRIP_TIGHT_SCALE, Image.INTERPOLATE_NEAREST)
					var wide := image.get_region(Rect2i(centre.x - STRIP_WIDE.x / 2, centre.y - STRIP_WIDE.y / 2, STRIP_WIDE.x, STRIP_WIDE.y))
					var frame := time_index * distances.size() + index
					var base_dir := out_dir.path_join(str(config["name"])).path_join(str(species).to_lower())
					for kind in ["tight", "wide"]:
						DirAccess.make_dir_recursive_absolute(base_dir.path_join(kind))
					tight.save_png(base_dir.path_join("tight").path_join("frame%08d.png" % frame))
					wide.save_png(base_dir.path_join("wide").path_join("frame%08d.png" % frame))
					var camera := tree.root.get_camera_3d()
					(record["frames"] as Array).append({"config": config["name"], "species": species, "time": time_name, "frame": frame, "distance_m": distance, "sun_elevation_deg": sun,
						"streaming_idle": idle, "trees_settled": settled, "camera_vertical_m": vertical, "yaw_deg": yaw, "pitch_deg": pitch, "fov_deg": camera.fov if camera != null else NAN,
						"weather": H.weather_at(game, cam_a, cam_z)})
			print("%s strip %s %s done" % [marker, config["name"], species])
	_apply_tree_settings(trees, original)
	record["failures"] = failures
	record["status"] = "PASS" if failures.is_empty() and not (record["frames"] as Array).is_empty() else "FAIL"
	_save_json(out_dir.path_join("strip.json"), record)
	print("%s output: %s" % [marker, out_dir])
	return record


# ------------------------------------------------------------------ TR-6 far billboard captures (docs/work-orders/terrain-vegetation/TR.md)

const TR6_PINE_MOUNTAIN := {"tree_around": 894206.0, "tree_across": 159468.0, "bearing_deg": 30.0, "height": 29.07}


static func _tr6_shot(ctx: Dictionary, out_dir: String, name: String, record: Array, extra: Dictionary, crop: Vector2i, scale: int) -> void:
	var tree: SceneTree = ctx["tree"]
	var settled := await _wait_trees_settled(ctx, 120.0)
	for i in 8:
		await RenderingServer.frame_post_draw
	var image := tree.root.get_texture().get_image()
	image.save_png(out_dir.path_join("%s.png" % name))
	if crop != Vector2i.ZERO:
		var centre := Vector2i(image.get_width() / 2, image.get_height() / 2)
		var tight := image.get_region(Rect2i(centre.x - crop.x / 2, centre.y - crop.y / 2, crop.x, crop.y))
		tight.resize(crop.x * scale, crop.y * scale, Image.INTERPOLATE_NEAREST)
		tight.save_png(out_dir.path_join("%s_tight.png" % name))
	var entry := {"name": name, "trees_settled": settled}
	entry.merge(extra)
	record.append(entry)


## `--tr6`: far billboard captures, each with the quad tier on and off (UseFarTreeQuads). Output: .godot/captures/landscape/t0/tr6_far_quad/<stamp>/<name>_{quad,cards}.png (+ _tight).
## Cases: ground_2km / ground_5km (an isolated rank <= 0.08 tree seen from that distance, centre crop), forest_ground (forest_edge_eye full frame), altitude_<m>_down<deg>
## (over forest_interior_eye at 300 m and 2 km, looking down 30 and 70 degrees) and mountain_pine_<m> (the TR-1 mountainside pine at 350 and 400 m, shipped fades).
static func run_tr6(ctx: Dictionary) -> Dictionary:
	var tree: SceneTree = ctx["tree"]
	var marker: String = ctx["marker"]
	var trees: Node = ctx["trees"]
	var game: Node = ctx["game"]
	var world: RefCounted = ctx["booted"]["world_bridge"]
	var out_dir := H.transient_dir("tr6_far_quad", ctx["booted"]["world_tag"])
	var times: Dictionary = H.load_poses().get("times", {})
	var original := {}
	for property in TREE_FADE_PROPERTIES + ["UseFarTreeQuads"]:
		original[property] = trees.get(property)
	var f1 := {"TreeNearDetailFadeStartMeters": 25.0, "TreeNearDetailFadeEndMeters": 40.0, "TreeBillboardFadeStartMeters": 300.0, "TreeBillboardFadeEndMeters": 350.0}
	_apply_tree_settings(trees, f1)
	var hidden_layers: Array = []
	for node in _all_nodes(game):
		if node is CanvasLayer and (node.name == "TerrainMinimap" or (node.get_script() != null and "RingPresentationHud" in (node.get_script() as Script).resource_path)):
			(node as CanvasLayer).visible = false
			hidden_layers.append(str(node.name))
	var record: Dictionary = (ctx["record"] as Dictionary).duplicate(true)
	record.merge({"marker": marker, "tool": "landscape_lod_switch_capture --tr6", "output": out_dir, "frames": [], "fades": f1, "hidden_hud_layers": hidden_layers}, true)
	var failures: Array = []
	# --- ground views of one isolated tree
	var finder: RefCounted = TIMING.new()
	var cases: Array = []
	var time_state: Dictionary = {}
	for distance: float in [2000.0, 5000.0]:
		var found := {}
		for site in STRIP_SITES:
			var at_site: Dictionary = finder.call("FindStripTrees", world, PackedFloat64Array([float(site[0])]), PackedFloat64Array([float(site[1])]), PackedFloat64Array([distance]), 0.1, 10.0)
			for species in STRIP_SPECIES:
				var candidate: Dictionary = at_site.get(species, {})
				if bool(candidate.get("found", false)) and float(candidate["clearance_m"]) >= 4.0:
					found = candidate
					break
			if not found.is_empty():
				break
		if found.is_empty():
			failures.append("no isolated rank <= 0.1 tree with a clear %d m line found" % int(distance))
			continue
		var bearing: float = deg_to_rad(float(found["bearing_deg"]))
		var tree_ground: float = float(world.call("GroundHeight", found["tree_around"], found["tree_across"]))
		var target_y: float = tree_ground + 0.5 * float(found["height"])
		var cam_a: float = float(found["tree_around"]) + cos(bearing) * distance
		var cam_z: float = float(found["tree_across"]) + sin(bearing) * distance
		var vertical: float = float(world.call("GroundHeight", cam_a, cam_z)) + EYE_M
		cases.append({"name": "ground_%dkm" % int(distance / 1000.0), "around": cam_a, "across": cam_z, "vertical": vertical, "yaw": rad_to_deg(atan2(-sin(bearing), -cos(bearing))),
			"pitch": rad_to_deg(atan2(target_y - vertical, distance)), "crop": Vector2i(128, 128), "scale": 6, "tree": found})
	# --- forest from the ground, altitude views and the mountainside pine
	var pose := H.pose_by_name("forest_edge_eye")
	var forest_pose := H.pose_by_name("forest_interior_eye")
	if not pose.is_empty():
		cases.append({"name": "forest_ground", "pose": pose})
	if not forest_pose.is_empty():
		for altitude: float in [300.0, 2000.0]:
			for down: float in [30.0, 70.0]:
				cases.append({"name": "altitude_%dm_down%d" % [int(altitude), int(down)], "around": float(forest_pose["around_m"]), "across": float(forest_pose["across_m"]),
					"vertical_above_ground": altitude, "yaw": float(forest_pose["yaw_deg"]), "pitch": -down})
	var mountain := TR6_PINE_MOUNTAIN
	var m_bearing: float = deg_to_rad(float(mountain["bearing_deg"]))
	var m_ground: float = float(world.call("GroundHeight", mountain["tree_around"], mountain["tree_across"]))
	var m_target: float = m_ground + 0.5 * float(mountain["height"])
	for distance: float in [350.0, 400.0]:
		var cam_a: float = float(mountain["tree_around"]) + cos(m_bearing) * distance
		var cam_z: float = float(mountain["tree_across"]) + sin(m_bearing) * distance
		var vertical: float = float(world.call("GroundHeight", cam_a, cam_z)) + EYE_M
		cases.append({"name": "mountain_pine_%d" % int(distance), "around": cam_a, "across": cam_z, "vertical": vertical, "yaw": rad_to_deg(atan2(-sin(m_bearing), -cos(m_bearing))),
			"pitch": rad_to_deg(atan2(m_target - vertical, distance)), "crop": Vector2i(192, 192), "scale": 3})
	var exposure_disabled := false
	for item in cases:
		for quads in [true, false]:
			_apply_tree_settings(trees, {"UseFarTreeQuads": quads})
			var label := "%s_%s" % [item["name"], "quad" if quads else "cards"]
			if item.has("pose"):
				var info: Dictionary = await H.go_pose(tree, game, item["pose"], "noon", ctx["ground_model"])
				if info.has("error"):
					failures.append("%s: %s" % [label, str(info["error"])])
					continue
			else:
				var vertical: float = float(item["vertical"]) if item.has("vertical") else float(world.call("GroundHeight", item["around"], item["across"])) + float(item["vertical_above_ground"])
				_teleport_to(ctx, float(item["around"]), float(item["across"]), vertical, float(item["yaw"]), float(item["pitch"]))
				await COMMON.settle(tree, 0.3)
				await COMMON.wait_builds_idle(tree, game, 240.0)
				if time_state.is_empty():
					time_state = COMMON.apply_time(game, times["noon"])
					await COMMON.settle(tree, 2.0)
				_teleport_to(ctx, float(item["around"]), float(item["across"]), vertical, float(item["yaw"]), float(item["pitch"]))
			if not exposure_disabled:
				await _disable_auto_exposure(ctx)
				exposure_disabled = true
			var crop: Vector2i = item.get("crop", Vector2i.ZERO)
			await _tr6_shot(ctx, out_dir, label, record["frames"], {"case": item["name"], "quads": quads, "yaw_deg": item.get("yaw", NAN), "pitch_deg": item.get("pitch", NAN)}, crop, int(item.get("scale", 1)))
			print("%s tr6 %s done" % [marker, label])
	_apply_tree_settings(trees, original)
	record["failures"] = failures
	record["status"] = "PASS" if failures.is_empty() and not (record["frames"] as Array).is_empty() else "FAIL"
	_save_json(out_dir.path_join("tr6.json"), record)
	print("%s output: %s" % [marker, out_dir])
	return record


## `--tr6-dolly`: a slow vertical rise (20 to 320 m over 240 frames, pitch fixed 20 degrees down) over forest_interior_eye with the shipped fades, so trees in the card band
## (300 to 350 m and beyond) are seen from 4 to 45 degrees. One sequence with the quad tier on and one with UseFarTreeQuads off. Frames: <out>/{quad,cards}/frame%08d.png.
static func run_tr6_dolly(ctx: Dictionary) -> Dictionary:
	var tree: SceneTree = ctx["tree"]
	var marker: String = ctx["marker"]
	var trees: Node = ctx["trees"]
	var game: Node = ctx["game"]
	var world: RefCounted = ctx["booted"]["world_bridge"]
	var out_dir := H.transient_dir("tr6_dolly", ctx["booted"]["world_tag"])
	var times: Dictionary = H.load_poses().get("times", {})
	var pose := H.pose_by_name("forest_interior_eye")
	if pose.is_empty():
		return {"status": "FAIL", "failure": "forest_interior_eye missing"}
	var original := {}
	for property in TREE_FADE_PROPERTIES + ["UseFarTreeQuads"]:
		original[property] = trees.get(property)
	_apply_tree_settings(trees, {"TreeNearDetailFadeStartMeters": 25.0, "TreeNearDetailFadeEndMeters": 40.0, "TreeBillboardFadeStartMeters": 300.0, "TreeBillboardFadeEndMeters": 350.0})
	for node in _all_nodes(game):
		if node is CanvasLayer and (node.name == "TerrainMinimap" or (node.get_script() != null and "RingPresentationHud" in (node.get_script() as Script).resource_path)):
			(node as CanvasLayer).visible = false
	var around: float = float(pose["around_m"])
	var across: float = float(pose["across_m"])
	var yaw: float = float(pose["yaw_deg"])
	var ground: float = float(world.call("GroundHeight", around, across))
	var info: Dictionary = await H.go_pose(tree, game, pose, "noon", ctx["ground_model"])
	if info.has("error"):
		return {"status": "FAIL", "failure": str(info["error"])}
	await _disable_auto_exposure(ctx)
	var frames := 240
	var record: Dictionary = (ctx["record"] as Dictionary).duplicate(true)
	record.merge({"marker": marker, "tool": "landscape_lod_switch_capture --tr6-dolly", "output": out_dir, "frames": frames, "rise_m": [20.0, 320.0], "pitch_deg": -20.0, "pose": pose["name"]}, true)
	for quads in [true, false]:
		_apply_tree_settings(trees, {"UseFarTreeQuads": quads})
		var dir_path := out_dir.path_join("quad" if quads else "cards")
		DirAccess.make_dir_recursive_absolute(dir_path)
		for step in frames:
			var height: float = 20.0 + 300.0 * float(step) / float(frames - 1)
			_teleport_to(ctx, around, across, ground + height, yaw, -20.0)
			if step == 0:
				await COMMON.wait_builds_idle(tree, game, 240.0)
				await _wait_trees_settled(ctx, 120.0)
				_teleport_to(ctx, around, across, ground + height, yaw, -20.0)
			await RenderingServer.frame_post_draw
			await RenderingServer.frame_post_draw
			tree.root.get_texture().get_image().save_png(dir_path.path_join("frame%08d.png" % step))
		print("%s tr6 dolly %s done" % [marker, "quad" if quads else "cards"])
	_apply_tree_settings(trees, original)
	record["status"] = "PASS"
	_save_json(out_dir.path_join("tr6_dolly.json"), record)
	print("%s output: %s" % [marker, out_dir])
	return record


# ------------------------------------------------------------------ TR-8 review pack captures (docs/work-orders/terrain-vegetation/TR.md)

const TR8_F1 := {"TreeNearDetailFadeStartMeters": 25.0, "TreeNearDetailFadeEndMeters": 40.0, "TreeBillboardFadeStartMeters": 300.0, "TreeBillboardFadeEndMeters": 350.0,
	"TreeMidDetailFadeStartMeters": 130.0, "TreeMidDetailFadeEndMeters": 150.0}
## before = the old renderer (no render LODs, no far quads), after = the shipped one.
const TR8_CASES := [["before", {"UseTreeRenderLods": false, "UseFarTreeQuads": false}], ["after", {"UseTreeRenderLods": true, "UseFarTreeQuads": true}]]


static func _tr8_prepare(ctx: Dictionary) -> Dictionary:
	var trees: Node = ctx["trees"]
	var original := {}
	for property in TREE_FADE_PROPERTIES + ["UseTreeRenderLods", "UseFarTreeQuads"]:
		original[property] = trees.get(property)
	var settings := TR8_F1.duplicate()
	settings.merge(_parse_tree_settings(harness_arg("--tree-set=", "")), true)
	var applied := _apply_tree_settings(trees, settings)
	if applied.has("error"):
		return applied
	for node in _all_nodes(ctx["game"]):
		if node is CanvasLayer and (node.name == "TerrainMinimap" or (node.get_script() != null and "RingPresentationHud" in (node.get_script() as Script).resource_path)):
			(node as CanvasLayer).visible = false
	return {"original": original, "applied": applied}


## `--tr8-dolly`: a pull-back from 100 to 400 m behind the pose (`--pose=`, default forest_edge_eye; one metre per frame, looking along the pose heading, eye height 1.7 m),
## once with the old renderer and once with the new one, the shipped fades forced (`--dolly-lift=<m>` raises the camera above the ground, default 1.7). Frames: <out>/{before,after}/frame%08d.png (run flicker on each).
static func run_tr8_dolly(ctx: Dictionary) -> Dictionary:
	var tree: SceneTree = ctx["tree"]
	var marker: String = ctx["marker"]
	var trees: Node = ctx["trees"]
	var game: Node = ctx["game"]
	var world: RefCounted = ctx["booted"]["world_bridge"]
	var out_dir := H.transient_dir("tr8_dolly", ctx["booted"]["world_tag"])
	var pose := H.pose_by_name(COMMON.arg_value("--pose=", "forest_edge_eye"))
	if pose.is_empty():
		return {"status": "FAIL", "failure": "pose missing from the fixture"}
	var prepared := _tr8_prepare(ctx)
	if prepared.has("error"):
		return {"status": "FAIL", "failure": str(prepared["error"])}
	var info: Dictionary = await H.go_pose(tree, game, pose, "noon", ctx["ground_model"])
	if info.has("error"):
		return {"status": "FAIL", "failure": str(info["error"])}
	await _disable_auto_exposure(ctx)
	var yaw := float(pose["yaw_deg"])
	var pitch := float(pose["pitch_deg"])
	var back_a := -cos(deg_to_rad(yaw))
	var back_z := -sin(deg_to_rad(yaw))
	var frames := 300
	var record: Dictionary = (ctx["record"] as Dictionary).duplicate(true)
	record.merge({"marker": marker, "tool": "landscape_lod_switch_capture --tr8-dolly", "output": out_dir, "frames": frames, "back_m": [100.0, 400.0], "pose": pose["name"], "overrides": prepared["applied"], "settled": {}}, true)
	for item in TR8_CASES:
		_apply_tree_settings(trees, item[1])
		var dir_path := out_dir.path_join(str(item[0]))
		DirAccess.make_dir_recursive_absolute(dir_path)
		for step in frames:
			var cam_a := float(pose["around_m"]) + back_a * (100.0 + float(step))
			var cam_z := float(pose["across_m"]) + back_z * (100.0 + float(step))
			var vertical := float(world.call("GroundHeight", cam_a, cam_z)) + float(harness_arg("--dolly-lift=", str(EYE_M)))
			_teleport_to(ctx, cam_a, cam_z, vertical, yaw, pitch)
			if step == 0:
				await COMMON.wait_builds_idle(tree, game, 240.0)
				record["settled"][item[0]] = await _wait_trees_settled(ctx, 180.0)
				_teleport_to(ctx, cam_a, cam_z, vertical, yaw, pitch)
			await RenderingServer.frame_post_draw
			await RenderingServer.frame_post_draw
			tree.root.get_texture().get_image().save_png(dir_path.path_join("frame%08d.png" % step))
		print("%s tr8 dolly %s done" % [marker, item[0]])
	_apply_tree_settings(trees, prepared["original"])
	record["status"] = "PASS"
	_save_json(out_dir.path_join("tr8_dolly.json"), record)
	print("%s output: %s" % [marker, out_dir])
	return record


## `--tr8-shadow`: the shadow debug view (ShadowDebugMode 1, sun visibility) and the normal frame at `--pose=` (default forest_clearing_eye), noon and sun 8 degrees,
## old renderer then new one. Files: <out>/<time>_<before|after>_{debug,normal}.png.
static func run_tr8_shadow(ctx: Dictionary) -> Dictionary:
	var tree: SceneTree = ctx["tree"]
	var marker: String = ctx["marker"]
	var trees: Node = ctx["trees"]
	var game: Node = ctx["game"]
	var graphics := game.get_node_or_null("GraphicsQuality")
	if graphics == null:
		return {"status": "FAIL", "failure": "no GraphicsQuality node"}
	var out_dir := H.transient_dir("tr8_shadow", ctx["booted"]["world_tag"])
	var pose := H.pose_by_name(COMMON.arg_value("--pose=", "forest_clearing_eye"))
	if pose.is_empty():
		return {"status": "FAIL", "failure": "pose missing from the fixture"}
	var prepared := _tr8_prepare(ctx)
	if prepared.has("error"):
		return {"status": "FAIL", "failure": str(prepared["error"])}
	var record: Dictionary = (ctx["record"] as Dictionary).duplicate(true)
	record.merge({"marker": marker, "tool": "landscape_lod_switch_capture --tr8-shadow", "output": out_dir, "pose": pose["name"], "overrides": prepared["applied"], "frames": []}, true)
	var exposure_disabled := false
	for time_name in ["noon", "sun_8_set"]:
		var info: Dictionary = await H.go_pose(tree, game, pose, time_name, ctx["ground_model"])
		if info.has("error"):
			return {"status": "FAIL", "failure": str(info["error"])}
		if not exposure_disabled:
			await _disable_auto_exposure(ctx)
			exposure_disabled = true
		for item in TR8_CASES:
			_apply_tree_settings(trees, item[1])
			var settled := await _wait_trees_settled(ctx, 240.0)
			for mode in [["normal", 0], ["debug", 1]]:
				graphics.call("SetShadowTuningOverride", "ShadowDebugMode", int(mode[1]))
				await COMMON.settle(tree, 1.0)
				for i in 8:
					await RenderingServer.frame_post_draw
				var name := "%s_%s_%s" % [time_name, item[0], mode[0]]
				tree.root.get_texture().get_image().save_png(out_dir.path_join(name + ".png"))
				(record["frames"] as Array).append({"name": name, "trees_settled": settled, "sun_elevation_deg": info.get("sun_elevation_deg", NAN)})
			graphics.call("SetShadowTuningOverride", "ShadowDebugMode", 0)
		print("%s tr8 shadow %s done" % [marker, time_name])
	_apply_tree_settings(trees, prepared["original"])
	record["status"] = "PASS"
	_save_json(out_dir.path_join("tr8_shadow.json"), record)
	print("%s output: %s" % [marker, out_dir])
	return record


# ------------------------------------------------------------------ T5-9 far speckle fly-out (docs/work-orders/terrain-vegetation/T5.md, T5-9)

## `--t5-flyout`: a camera pull-back and rise from 300 m to 5 km above the ground over 300 frames (altitude on an exponential ramp, 1.5 m back along the heading per metre risen), looking at a
## point 600 m ahead of `--pose=` (default breaks_300m) on the ground. One teleport per captured frame, a short settle after each (streaming keeps up as far as it can); the same path is
## flown with and without `scatter.rocks` by booting the review worlds `--review-features=ground.surface,scatter.rocks` and `--review-features=ground.surface`. `--t5-label=<name>` names the
## frame folder. Frames: <out>/<label>/frame%08d.png (run flicker and sheet on the folder, then delete it). Test-only.
static func run_t5_flyout(ctx: Dictionary) -> Dictionary:
	var tree: SceneTree = ctx["tree"]
	var marker: String = ctx["marker"]
	var game: Node = ctx["game"]
	var world: RefCounted = ctx["booted"]["world_bridge"]
	var pose := H.pose_by_name(COMMON.arg_value("--pose=", "breaks_300m"))
	if pose.is_empty():
		return {"status": "FAIL", "failure": "pose missing from the fixture"}
	var label := harness_arg("--t5-label=", "run")
	var out_dir := H.transient_dir("t5_flyout", ctx["booted"]["world_tag"]).path_join(label)
	DirAccess.make_dir_recursive_absolute(out_dir)
	var info: Dictionary = await H.go_pose(tree, game, pose, "noon", ctx["ground_model"])
	if info.has("error"):
		return {"status": "FAIL", "failure": str(info["error"])}
	await _disable_auto_exposure(ctx)
	for node in _all_nodes(game):
		if node is CanvasLayer and (node.name == "TerrainMinimap" or (node.get_script() != null and "RingPresentationHud" in (node.get_script() as Script).resource_path)):
			(node as CanvasLayer).visible = false
	var yaw := float(pose["yaw_deg"])
	var fwd_a := cos(deg_to_rad(yaw))
	var fwd_z := sin(deg_to_rad(yaw))
	var around := float(pose["around_m"])
	var across := float(pose["across_m"])
	var ground := float(world.call("GroundHeight", around, across))
	var target_a := around + fwd_a * 600.0
	var target_z := across + fwd_z * 600.0
	var target_y := float(world.call("GroundHeight", target_a, target_z))
	var frames := 300
	var ratio := pow(5000.0 / 300.0, 1.0 / float(frames - 1))
	var rows: Array = []
	for step in frames:
		var altitude := 300.0 * pow(ratio, step)
		var back := 1.5 * (altitude - 300.0)
		var cam_a := around - fwd_a * back
		var cam_z := across - fwd_z * back
		var vertical := ground + altitude
		var pitch := rad_to_deg(atan2(target_y - vertical, back + 600.0))
		_teleport_to(ctx, cam_a, cam_z, vertical, yaw, pitch)
		if step == 0:
			await COMMON.wait_builds_idle(tree, game, 240.0)
			await COMMON.settle(tree, 2.0)
			_teleport_to(ctx, cam_a, cam_z, vertical, yaw, pitch)
		await COMMON.settle(tree, 0.15)
		await COMMON.wait_builds_idle(tree, game, 1.5, 4)
		await RenderingServer.frame_post_draw
		await RenderingServer.frame_post_draw
		tree.root.get_texture().get_image().save_png(out_dir.path_join("frame%08d.png" % step))
		rows.append({"frame": step, "altitude_m": altitude, "back_m": back, "pitch_deg": pitch})
	var record: Dictionary = (ctx["record"] as Dictionary).duplicate(true)
	record.merge({"marker": marker, "tool": "landscape_lod_switch_capture --t5-flyout", "output": out_dir, "label": label, "pose": pose["name"], "frames": rows, "status": "PASS"}, true)
	_save_json(out_dir.path_join("t5_flyout.json"), record)
	print("%s output: %s" % [marker, out_dir])
	return record


# ------------------------------------------------------------------ T5-10 scout: tors and outcrops on rocky ground

## `--t5-scout` (landscape_lod_switch_capture.gd), `--poses=` names the search poses: at each, waits for the scatter streamer, then lists outcrops (class 1) within `--t5-radius=` m (default 450) with the habitat
## grid's rock exposure and slope at each (tors = exposure under 0.3, outcrops on rock = exposure over 0.8), the resident counts per class, and for the best few of each kind eight camera spots 60-120 m
## away (ground height there, yaw to the rock, pitch). Writes t5_scout.json and prints the picks. Test-only.
static func run_t5_scout(ctx: Dictionary) -> Dictionary:
	var tree: SceneTree = ctx["tree"]
	var marker: String = ctx["marker"]
	var game: Node = ctx["game"]
	var world: RefCounted = ctx["booted"]["world_bridge"]
	var radius := float(harness_arg("--t5-radius=", "450"))
	var out_dir := H.transient_dir("t5_scout", ctx["booted"]["world_tag"])
	DirAccess.make_dir_recursive_absolute(out_dir)
	var results: Array = []
	for pose in t0_poses():
		var info: Dictionary = await H.go_pose(tree, game, pose, "noon", ctx["ground_model"])
		if info.has("error"):
			return {"status": "FAIL", "failure": str(info["error"])}
		await wait_scatter_idle(ctx, 240.0)
		await COMMON.settle(tree, 1.0)
		var around := float(pose["around_m"])
		var across := float(pose["across_m"])
		var rows: Variant = JSON.parse_string(str((ctx["trees"] as Node).call("ScatterRecordsNearJson", around, across, radius, 1)))
		var resident: Variant = JSON.parse_string(str((ctx["trees"] as Node).call("ScatterResidentJson")))
		var picks := {"tors": [], "rocky": []}
		for kind in ["tors", "rocky"]:
			var candidates: Array = []
			for row in rows:
				if float(row["size"]) < 8.0:
					continue
				var e := float(row["exposure"])
				if (kind == "tors" and e < 0.3) or (kind == "rocky" and e > 0.8):
					candidates.append(row)
			candidates.sort_custom(func(a: Dictionary, b: Dictionary) -> bool: return float(a["size"]) > float(b["size"]))
			for row in candidates.slice(0, 4):
				var spots: Array = []
				for k in 8:
					var heading := deg_to_rad(45.0 * k)
					var cam_a := float(row["a"]) + cos(heading) * 90.0
					var cam_z := float(row["z"]) + sin(heading) * 90.0
					var cam_ground := float(world.call("GroundHeight", cam_a, cam_z))
					var yaw := rad_to_deg(atan2(float(row["z"]) - cam_z, float(row["a"]) - cam_a))
					var rock_mid := float(row["y"]) + float(row["size"]) * 0.4
					var lift := 6.0
					var pitch := rad_to_deg(atan2(rock_mid - (cam_ground + lift), 90.0))
					spots.append({"around": cam_a, "across": cam_z, "ground": cam_ground, "lift": lift, "yaw": yaw, "pitch": pitch})
				picks[kind].append({"rock": row, "spots": spots})
		# boulders at a slope foot (slope under 25 degrees, size 1.5 m or more, most neighbours within 20 m) and cobbles (most neighbours within 8 m): camera spots at eye height
		for kind in ["boulders_foot", "cobbles"]:
			var class_id := 2 if kind == "boulders_foot" else 3
			var near: Variant = JSON.parse_string(str((ctx["trees"] as Node).call("ScatterRecordsNearJson", around, across, 200.0, class_id)))
			var reach := 20.0 if kind == "boulders_foot" else 8.0
			var scored: Array = []
			for row in near:
				if kind == "boulders_foot" and (float(row["slope"]) > 25.0 or float(row["size"]) < 1.5):
					continue
				var neighbours := 0
				for other in near:
					if absf(float(other["a"]) - float(row["a"])) <= reach and absf(float(other["z"]) - float(row["z"])) <= reach:
						neighbours += 1
				scored.append({"row": row, "neighbours": neighbours})
			scored.sort_custom(func(a: Dictionary, b: Dictionary) -> bool: return int(a["neighbours"]) > int(b["neighbours"]))
			var spot_distance := 40.0 if kind == "boulders_foot" else 14.0
			picks[kind] = []
			for entry in scored.slice(0, 3):
				var row: Dictionary = entry["row"]
				var spots: Array = []
				for k in 8:
					var heading := deg_to_rad(45.0 * k)
					var cam_a := float(row["a"]) + cos(heading) * spot_distance
					var cam_z := float(row["z"]) + sin(heading) * spot_distance
					var cam_ground := float(world.call("GroundHeight", cam_a, cam_z))
					var yaw := rad_to_deg(atan2(float(row["z"]) - cam_z, float(row["a"]) - cam_a))
					var lift := 1.7
					var pitch := rad_to_deg(atan2(float(row["y"]) - (cam_ground + lift), spot_distance))
					spots.append({"around": cam_a, "across": cam_z, "ground": cam_ground, "lift": lift, "yaw": yaw, "pitch": pitch})
				picks[kind].append({"rock": row, "neighbours": int(entry["neighbours"]), "spots": spots})
		results.append({"pose": pose["name"], "outcrops_in_radius": rows.size(), "resident": resident, "picks": picks})
		print("%s scout %s: outcrops within %.0f m: %d, resident %s" % [marker, pose["name"], radius, rows.size(), JSON.stringify(resident)])
		for kind in ["tors", "rocky", "boulders_foot", "cobbles"]:
			for pick in picks[kind]:
				var r: Dictionary = pick["rock"]
				print("%s   %s: id %s size %.1f at (%.1f, %.1f) y %.1f exposure %.2f slope %.1f" % [marker, kind, str(r["id"]), float(r["size"]), float(r["a"]), float(r["z"]), float(r["y"]), float(r["exposure"]), float(r["slope"])])
	var record: Dictionary = (ctx["record"] as Dictionary).duplicate(true)
	record.merge({"marker": marker, "tool": "landscape_lod_switch_capture --t5-scout", "output": out_dir, "scout": results, "status": "PASS"}, true)
	_save_json(out_dir.path_join("t5_scout.json"), record)
	print("%s output: %s" % [marker, out_dir])
	return record
