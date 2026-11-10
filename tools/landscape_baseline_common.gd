extends RefCounted

## Shared part bodies of the landscape T0-7 tools (docs/landscape-audit-t0-spec.md WO T0-7): look references, cost baseline and LOD-switch capture. TEST ONLY.
## Wrappers (tools/landscape_look_baseline.gd, landscape_cost_baseline.gd, landscape_lod_switch_capture.gd) call setup() then one run_*() and print the marker. Everything boots
## through tools/landscape_harness_common.gd (pinned generation 1 world, the user's preference files snapshotted, Cards grass checked). The hide cases (near terrain, far terrain,
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
const LOD_STEPS := 180
const LOD_START_M := 15.0
const LOD_STEP_M := 0.25
const EYE_M := 1.7


# ------------------------------------------------------------------ setup

## Boots the pinned world and records the run identity. Returns {} after push_error on failure (the caller then calls cleanup).
static func setup(tree: SceneTree, marker: String) -> Dictionary:
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
		"explorer": explorer, "surface": surface, "grass": grass, "trees": trees, "scaled_far": scaled_far, "sun": sun, "preset": preset, "ok": true}
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
	return {"machine": ctx["machine"], "resolution": [root_window.size.x, root_window.size.y], "vsync": "off", "max_fps": 0, "godot": Engine.get_version_info().get("string", ""),
		"preset": ctx["preset"], "grass_render_style": ctx["preset"].get("grass_render_style_name", "?"),
		"tree_fade": {"compact_profile": trees.get("UseCompactTreeLodProfile"), "near_start_m": trees.get("TreeNearDetailFadeStartMeters"), "near_end_m": trees.get("TreeNearDetailFadeEndMeters"),
			"billboard_start_m": trees.get("TreeBillboardFadeStartMeters"), "billboard_end_m": trees.get("TreeBillboardFadeEndMeters")},
		"prefs": {"mode": prefs.get("mode", "?"), "used": prefs.get("used", []), "absent": prefs.get("absent", []), "sha256": files, "atmosphere_settings_sha256": atmosphere_hash},
		"world": booted["world"], "clocks_held": true, "auto_exposure_disabled_for_look": true}


static func t0_poses() -> Array:
	var names := COMMON.csv_arg("--poses=")
	var result: Array = []
	for pose in H.load_poses().get("poses", []):
		if "t0" in pose.get("reports", []) and (names.is_empty() or str(pose["name"]) in names):
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
	var out_dir := H.baseline_dir(str(machine["tag"]), "t0", "look")
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
			var info: Dictionary = await H.go_pose(tree, ctx["game"], pose, time_name)
			if first and not info.has("error"):
				await _disable_auto_exposure(ctx)
				first = false
			var view_name := "%s__%s" % [pose["name"], time_name]
			var view := {"view": view_name, "pose": pose["name"], "time": time_name, "go_pose": info}
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
			if mean_luma <= 0.01:
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
	_save_json(out_dir.path_join("reference_set.json"), {"machine": machine["tag"], "adapter": machine["adapter"], "authoritative": machine["authoritative"], "preset": ctx["preset"].get("preset", "?"),
		"prefs_mode": ctx["booted"]["prefs"].get("mode", "?"), "references": references})
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
	return state


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


static func apply_case(state: Dictionary, label: String) -> bool:
	match label:
		"baseline", "baseline_repeat":
			pass
		"near_terrain_hidden":
			for node in state["near_nodes"]:
				node.visible = false
		"far_terrain_hidden":
			for node in state["far_nodes"]:
				node.visible = false
		"trees_hidden":
			for node in state["tree_geometry"]:
				if is_instance_valid(node):
					node.layers = 0
		"grass_hidden":
			for node in state["grass_geometry"]:
				if is_instance_valid(node):
					node.layers = 0
		_:
			return false
	return true


## "" when the case's state is applied exactly (and nothing else is hidden), else the reason. Freed geometry nodes (streaming) are skipped.
static func verify_case(state: Dictionary, label: String) -> String:
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
		var node: Node = state["tree_geometry"][index]
		if is_instance_valid(node) and node.layers != (0 if label == "trees_hidden" else int(state["tree_layers"][index])):
			return "tree render layer was not applied for " + label
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
	var rounds := maxi(1, int(COMMON.arg_value("--rounds=", "2")))
	var seconds := maxf(1.0, float(COMMON.arg_value("--seconds=", "8")))
	var out_dir := H.baseline_dir(str(machine["tag"]), "t0", "cost")
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
		var info: Dictionary = await H.go_pose(tree, ctx["game"], pose, time_name)
		var fill_wall_s := (Time.get_ticks_msec() - fill_start) / 1000.0
		tree.process_frame.disconnect(sampler)
		if info.has("error"):
			failures.append("%s: %s" % [key, str(info["error"])])
			results.append({"key": key, "pose": pose["name"], "time": time_name, "go_pose": info, "failure": str(info["error"])})
			continue
		var cpu_window: Dictionary = timing.call("CpuWindow")
		var fill := {"wall_s_incl_settle": fill_wall_s, "scopes": _fill_summary(cpu_window), "dropped": cpu_window.get("_meta", {})}
		var record := {"key": key, "pose": pose["name"], "time": time_name, "sun_elevation_deg": info.get("sun_elevation_deg", NAN), "go_pose": info, "fill": fill, "workload": _workload(ctx), "windows": [], "hide_effect": {}}
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
static func _derive(windows: Array) -> Dictionary:
	var by_label := {}
	for label in CASES:
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
	var classes := {}
	var parts: Array = []
	for label in HIDE_CLASSES:
		var d_root: float = base_root - float(med.call(label, "root_gpu_ms", "median"))
		var d_far: float = base_far - float(med.call(label, "far_gpu_ms", "median"))
		var d_draw: float = base_draw - float(med.call(label, "draw_calls", "mean"))
		var d_prim: float = base_prim - float(med.call(label, "primitives", "mean"))
		var changed := absf(d_draw) >= 0.5 or absf(d_prim) >= 1.0
		classes[label] = {"root_gpu_delta_ms": d_root, "far_gpu_delta_ms": d_far, "draw_delta": d_draw, "primitive_delta": d_prim, "changed_submissions": changed,
			"root_gpu_hidden_median_ms": med.call(label, "root_gpu_ms", "median")}
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
		"baseline_noise_draw_calls": absf(base_draw - float(med.call("baseline_repeat", "draw_calls", "mean"))), "classes": classes, "markers": markers,
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
	var out_dir := H.transient_dir("lod_switch")
	var info: Dictionary = await H.go_pose(tree, ctx["game"], pose, "noon")
	if info.has("error"):
		return {"status": "FAIL", "failure": str(info["error"])}
	await _disable_auto_exposure(ctx)
	var finder: RefCounted = TIMING.new()
	var found: Dictionary = finder.call("NearestTreeAhead", float(pose["around_m"]), float(pose["across_m"]), float(pose["yaw_deg"]), 8.0, 150.0, 8.0, LOD_START_M + LOD_STEPS * LOD_STEP_M + 3.0)
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
				"camera_vertical_m": vertical, "yaw_deg": yaw, "pitch_deg": pitch})
	record["status"] = "PASS"
	_save_json(out_dir.path_join("frames.json"), record)
	print("%s tree: %s" % [marker, JSON.stringify(found)])
	print("%s output: %s" % [marker, out_dir])
	return record
