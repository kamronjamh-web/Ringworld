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
const COMMON := H.COMMON
const WARMUP_FRAMES := 60
const SETTLE_S := 1.0
const REFERENCE_SET := ["plains_eye__noon", "hills_eye__sun_8_set", "forest_edge_eye__noon", "coast_eye__noon", "hills_2km__noon"]
const COST_EXTRA := [["hills_eye", "sun_8_set"], ["forest_edge_eye", "sun_8_set"]]
const CASES := ["baseline", "near_terrain_hidden", "far_terrain_hidden", "trees_hidden", "grass_hidden", "baseline_repeat"]
const HIDE_CLASSES := ["near_terrain_hidden", "far_terrain_hidden", "trees_hidden", "grass_hidden"]
const MARKERS := ["GPU.TreeCulling", "GPU.GrassCulling", "GPU.Vegetation", "GPU.ProxyHeights", "GPU.HeightShadow", "GPU.WaterFft"]
const FILL_SCOPES := ["Trees build", "Trees disk load", "Grass build"]
## TR-1 (docs/work-orders/terrain-vegetation/TR.md): tree LOD slot cases. A slot case hides that slot's main-pass renderers only (its shadow casters stay); tree_shadows_off
## removes the tree shadow casters only. A "@rs50" suffix measures the same case at 50 % 3D render scale (fill test).
const TREE_LOD_CASES := ["baseline", "trees_hidden", "tree_slot0_hidden", "tree_slot2_hidden", "tree_slot3_hidden", "tree_slot4_hidden", "tree_shadows_off", "baseline_repeat"]
const TREE_LOD_POSES := ["forest_interior_eye", "forest_edge_eye", "forest_clearing_eye", "coast_eye"]
const TREE_LOD_EXTRA := [["forest_edge_eye", "sun_8_set"]]
const TREE_FADE_PROPERTIES := ["TreeNearDetailFadeStartMeters", "TreeNearDetailFadeEndMeters", "TreeBillboardFadeStartMeters", "TreeBillboardFadeEndMeters"]
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
	var res := COMMON.arg_value("--res=", "1920x1080").split("x")
	if res.size() == 2:
		tree.root.size = Vector2i(int(res[0]), int(res[1]))
	var machine: Dictionary = COMMON.machine_info()
	print("%s %s adapter=%s (%s)" % [marker, machine["label"], machine["adapter"], "4070, non-authoritative" if not bool(machine["authoritative"]) else "A1000, authoritative if P0/35W"])
	var booted: Dictionary = await H.boot(tree, marker)
	if booted.is_empty():
		return {}
	var game: Node = booted["game"]
	var monitor := game.get_node_or_null("RingPerformanceMonitor")
	if monitor != null:
		monitor.process_mode = Node.PROCESS_MODE_DISABLED # it re-configures the timestamp bridge every frame and would drain our events
	COMMON.hold_world_clocks(game)
	var weather := H.pin_weather(game, H.report_poses(weather_pose_report))
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
	return ctx


static func cleanup(ctx: Dictionary) -> void:
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
	for pose in H.report_poses("t0"):
		if names.is_empty() or str(pose["name"]) in names:
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
	var times := COMMON.csv_arg("--times=")
	if times.is_empty():
		times = PackedStringArray(["noon", "sun_8_set"])
	var views: Array = []
	var failures: Array = []
	var first := true
	for pose in t0_poses():
		for time_name in times:
			var info: Dictionary = await H.go_pose(tree, ctx["game"], pose, time_name, ctx["ground_model"])
			if first and not info.has("error"):
				await _disable_auto_exposure(ctx)
				first = false
			var view_name := "%s__%s" % [pose["name"], time_name]
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
		if node is MeshInstance3D and (node.name == "ScaledSeededRingSurface" or node.name.begins_with("ScaledFarLod") or _shader_name(node) == "ring_terrain_global.gdshader"):
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
	state["viewport"] = (ctx["tree"] as SceneTree).root
	state["scale"] = (ctx["tree"] as SceneTree).root.scaling_3d_scale
	return state


## [case label without the render-scale suffix, render scale or -1.0].
static func _case_parts(label: String) -> Array:
	if label.ends_with(RS_SUFFIX):
		return [label.trim_suffix(RS_SUFFIX), RS_SCALE]
	return [label, -1.0]


static func _known_case(base: String) -> bool:
	if base in ["baseline", "baseline_repeat", "near_terrain_hidden", "far_terrain_hidden", "trees_hidden", "grass_hidden", "tree_shadows_off"]:
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
	for key in [["grass_geometry", "grass_layers"], ["tree_geometry", "tree_layers"]]:
		var nodes: Array = state[key[0]]
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
	for index in state["tree_geometry"].size():
		var node: Variant = state["tree_geometry"][index]
		if not is_instance_valid(node):
			continue
		var expected := _tree_expected(state, index, label)
		if node.layers != int(expected[0]):
			return "tree render layer was not applied for " + label
		if int(expected[1]) != int(state["tree_cast"][index]) and int(node.cast_shadow) != int(expected[1]):
			return "tree shadow casting was not applied for " + label
	return ""


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
	ctx["far_rid"] = far_rid
	ctx["viewport_count"] = rids.size()


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
	var root_rid: RID = ctx["root_rid"]
	var far_rid: RID = ctx["far_rid"]
	var utc0 := Time.get_unix_time_from_system()
	var start := Time.get_ticks_usec()
	var last := start
	while (Time.get_ticks_usec() - start) * 0.000001 < seconds:
		await RenderingServer.frame_post_draw
		var now := Time.get_ticks_usec()
		frame_ms.append((now - last) * 0.001)
		last = now
		root_gpu.append(RenderingServer.viewport_get_measured_render_time_gpu(root_rid))
		root_cpu.append(RenderingServer.viewport_get_measured_render_time_cpu(root_rid))
		far_gpu.append(RenderingServer.viewport_get_measured_render_time_gpu(far_rid) if far_rid.is_valid() else NAN)
		draws.append(Performance.get_monitor(Performance.RENDER_TOTAL_DRAW_CALLS_IN_FRAME))
		prims.append(Performance.get_monitor(Performance.RENDER_TOTAL_PRIMITIVES_IN_FRAME))
		timing.call("Sample")
	var utc1 := Time.get_unix_time_from_system()
	var markers := {}
	var gpu_window: Dictionary = timing.call("Window")
	for key in gpu_window:
		if str(key) == "_meta":
			continue
		var values: Array = Array(gpu_window[key])
		markers[str(key)] = summarize(values)
	return {"samples": draws.size(), "seconds": (Time.get_ticks_usec() - start) * 0.000001, "utc0": utc0, "utc1": utc1, "root_gpu_ms": summarize(root_gpu), "far_gpu_ms": summarize(far_gpu),
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
	var plan_windows := pose_times.size() * rounds * CASES.size()
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
		var info: Dictionary = await H.go_pose(tree, ctx["game"], pose, time_name, ctx["ground_model"])
		var fill_wall_s := (Time.get_ticks_msec() - fill_start) / 1000.0
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
		var state := capture_original_state(ctx)
		if results.is_empty():
			record["far_mesh_names"] = state["far_mesh_names"] # evidence for which meshes count as far terrain (first pose only)
			var children: Array = []
			for child in (ctx["game"] as Node).get_children():
				children.append("%s:%s:%s" % [child.name, child.get_class(), (child.get_script() as Script).resource_path.get_file() if child.get_script() != null else "-"])
			record["game_children"] = children
		record["geometry_nodes"] = {"near_terrain": state["near_nodes"].size(), "far_terrain": state["far_nodes"].size(), "trees": state["tree_geometry"].size(), "grass": state["grass_geometry"].size()}
		print("%s %s fill %.1f s, scopes %s, nodes %s" % [marker, key, fill_wall_s, JSON.stringify(fill["scopes"]), JSON.stringify(record["geometry_nodes"])])
		# 2./3. cases
		for round_index in rounds:
			var order: Array = CASES.duplicate()
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
					failures.append("%s %s round %d: %s" % [key, label, round_index, why])
				(record["windows"] as Array).append(window)
				windows_total += 1
		restore_state(state)
		record["derived"] = _derive(record["windows"])
		results.append(record)
		print("%s %s derived %s" % [marker, key, JSON.stringify(record["derived"]["summary_line"])])
		_save_json(out_dir.path_join("cost_partial.json"), {"completed": results.size(), "of": pose_times.size()})

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
	for label in HIDE_CLASSES:
		class_effect[label] = 0
		for record in results:
			if record.has("derived") and bool(record["derived"]["classes"][label]["changed_submissions"]):
				class_effect[label] = int(class_effect[label]) + 1
		if int(class_effect[label]) == 0 and not results.is_empty():
			failures.append("hide class %s changed no draw or primitive count at any pose (escalate: hide path unreachable)" % label)
	var status := "PASS"
	if not failures.is_empty() or valid_share < 0.8 or results.is_empty():
		status = "FAIL"
	var report: Dictionary = (ctx["record"] as Dictionary).duplicate(true)
	report.merge({"marker": marker, "tool": "landscape_cost_baseline", "label": "4070, non-authoritative" if not bool(machine["authoritative"]) else "A1000, authoritative only where every window is power-valid",
		"rounds": rounds, "window_seconds": seconds, "warmup_frames": WARMUP_FRAMES, "settle_s": SETTLE_S, "bracket": bracket, "pose_times": results, "failures": failures, "windows": windows_total,
		"power_valid_windows": valid, "power_valid_share": valid_share, "hide_class_effect_poses": class_effect, "status": status, "output": out_dir, "power_log": power_path})
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
	lines.append("| pose | time | near terrain | far terrain | trees | grass | draws: near / far / trees / grass | primitives: near / far / trees / grass |")
	lines.append("|---|---|---|---|---|---|---|---|")
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
		lines.append("| %s | %s | %s | %s | %s | %s | %s | %s |" % [record["pose"], record["time"], cells[0], cells[1], cells[2], cells[3], " / ".join(draw_cells), " / ".join(prim_cells)])
	lines.append("")
	lines.append("## Far viewport GPU delta (ms)")
	lines.append("")
	lines.append("| pose | time | near terrain | far terrain | trees | grass |")
	lines.append("|---|---|---|---|---|---|")
	for record in report["pose_times"]:
		if not record.has("derived"):
			continue
		var c2: Dictionary = record["derived"]["classes"]
		lines.append("| %s | %s | %s | %s | %s | %s |" % [record["pose"], record["time"], _f(float(c2["near_terrain_hidden"]["far_gpu_delta_ms"])), _f(float(c2["far_terrain_hidden"]["far_gpu_delta_ms"])),
			_f(float(c2["trees_hidden"]["far_gpu_delta_ms"])), _f(float(c2["grass_hidden"]["far_gpu_delta_ms"]))])
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
	lines.append("| pose | time | wall s | Trees build n / med / p95 / sum ms | Trees disk load n / med / p95 / sum ms | Grass build n / med / p95 / sum ms |")
	lines.append("|---|---|---|---|---|---|")
	for record in report["pose_times"]:
		if not record.has("fill"):
			continue
		var cells3: Array = []
		for name in FILL_SCOPES:
			var s: Dictionary = record["fill"]["scopes"][name]
			cells3.append("%d / %s / %s / %s" % [int(s["count"]), _f(float(s["median_ms"])), _f(float(s["p95_ms"])), _f(float(s["sum_ms"]), 0)])
		lines.append("| %s | %s | %s | %s |" % [record["pose"], record["time"], _f(float(record["fill"]["wall_s_incl_settle"]), 1), " | ".join(cells3)])
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
			fades = {"TreeNearDetailFadeStartMeters": 25.0, "TreeNearDetailFadeEndMeters": 40.0, "TreeBillboardFadeStartMeters": 300.0, "TreeBillboardFadeEndMeters": 350.0}
		elif name != "user":
			return {"status": "FAIL", "failure": "unknown --strip-configs entry %s (user|f1)" % name}
		var distances := _strip_distances(float(fades["TreeBillboardFadeStartMeters"]), float(fades["TreeBillboardFadeEndMeters"]))
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
