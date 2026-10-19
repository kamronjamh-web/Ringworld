extends SceneTree

## Render-cost attribution for a settled, normal gameplay scene. It changes one
## render submission control at a time while all gameplay nodes keep processing.
const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const GAME := preload("res://scenes/VoxelRingIntegrationTest.tscn")
const RUNTIME_COUNTERS := preload("res://tools/RuntimeFrameDiagnostics.cs")
const FIXTURE_ROOT := "res://.godot/terrain-render-attribution"
const OUTPUT := "res://.godot/terrain-render-attribution/report.json"
const HAZE_OUTPUT := "res://.godot/terrain-render-attribution/haze-report.json"
const WARMUP_FRAMES := 60
const MIN_SAMPLE_FRAMES := 100
const TARGET_SAMPLE_SECONDS := 5.0
const MAX_SAMPLE_SECONDS := 10.0
const FIXTURE_AROUND := 16443476.0
const FIXTURE_ACROSS := 38692.0

var game: Node
var explorer: Node
var surface: Node3D
var grass: Node3D
var trees: Node3D
var scaled_far: Node
var sun: DirectionalLight3D
var runtime_counters: RefCounted
var measured_viewports: Array[Viewport] = []
var original_state := {}
var baseline_submissions := {}
var failed := false
var original_haze_enabled := true
var partial_report := {"status":"RUNNING", "cases":[]}
var tuning_drawer: Node

class SessionStub extends Node:
	var settings := {}
	func get_active_world_settings() -> Dictionary: return settings.duplicate(true)
	func save_active_world_settings(next: Dictionary) -> void: settings = next.duplicate(true)
	func consume_load_selected_world_request() -> bool: return false


func _init() -> void:
	call_deferred("_run")


func _run() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("RenderingDevice is required")
		return
	original_haze_enabled = bool(ProjectSettings.get_setting("ringworld/runtime/haze_enabled", true))
	# Haze is an optional diagnostic layer; turn it off only in this isolated
	# fixture so it cannot mask terrain/vegetation submission changes.
	ProjectSettings.set_setting("ringworld/runtime/haze_enabled", false)
	var unique_root := FIXTURE_ROOT.path_join(str(Time.get_ticks_usec()))
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", unique_root)
	ProjectSettings.set_setting("ringworld/runtime/terrain_backend", "surface_runtime_generation_v1")
	ProjectSettings.set_setting("ringworld/runtime/surface_landforms_v7", false)
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(unique_root))
	game = await _create_game()
	if failed or game == null:
		return
	explorer = game.get_node_or_null("Explorer")
	surface = game.get_node_or_null("Terrain3DGameplay") as Node3D
	grass = game.get_node_or_null("StandingGrass") as Node3D
	trees = game.get_node_or_null("BiomeDecorations") as Node3D
	scaled_far = game.get_node_or_null("ScaledFarViewport")
	sun = game.get_node_or_null("StarLight") as DirectionalLight3D
	if explorer == null or surface == null or grass == null or trees == null or scaled_far == null or sun == null:
		_fail("required gameplay render nodes are missing")
		return

	var spawn_height := float(surface.call("SampleHeight", FIXTURE_AROUND, FIXTURE_ACROSS)) + 1.1
	explorer.call("SetCanonicalSpawnMeters", FIXTURE_AROUND, spawn_height, FIXTURE_ACROSS)
	explorer.call("SetPresentationView", 0.0, -10.0)
	var day_cycle: Node = game.get_node_or_null("RingDayNightCycle")
	if day_cycle != null:
		day_cycle.call("SetCycleFraction", 0.25)
		day_cycle.set("Paused", true)
	if not await _wait_for_settle():
		return
	_enable_viewport_timing()
	runtime_counters = RUNTIME_COUNTERS.new()
	original_state = _capture_original_state()
	if original_state.near_proxy_paths.is_empty() or original_state.far_terrain_paths.is_empty():
		var meshes: Array[String] = []
		for node in _all_nodes(scaled_far):
			if node is MeshInstance3D:
				meshes.append(str(node.get_path()))
		partial_report.discovered_far_mesh_paths = meshes
		_fail("no far terrain geometry was discovered: " + JSON.stringify(meshes))
		return

	var report := {
		"status": "RUNNING",
		"purpose": "steady full-game render submission attribution",
		"limitation": "Each case is a correlated whole-frame measurement. GPU passes overlap, so case deltas are not additive and do not identify exclusive GPU time.",
		"fixture": {"seed": 1337, "generation": "runtime-v2", "around": FIXTURE_AROUND, "across": FIXTURE_ACROSS, "weather": "scene default with runtime haze forced off", "camera": "Explorer fixed yaw 0 pitch -10", "celestial_time": "cycle fraction .25, paused"},
		"renderer": RenderingServer.get_current_rendering_method(),
		"adapter": RenderingServer.get_video_adapter_name(),
		"quality": _quality_identity(),
		"workload_identity": _workload_identity(),
		"cases": []
	}
	tuning_drawer = game.get_node_or_null("LiveTuningDrawer")
	var labels := ["baseline", "natural_haze", "cinematic_haze", "baseline_repeat"] if "--haze-only" in OS.get_cmdline_user_args() else ["baseline", "near_proxy_terrain_hidden", "far_terrain_hidden", "grass_hidden", "trees_hidden", "directional_shadows_off", "natural_haze", "cinematic_haze", "baseline_repeat"]
	for label in labels:
		_restore_original_state()
		if not _apply_case(label) or not _verify_case(label):
			return
		var case_data := await _sample_case(label)
		if failed:
			return
		if label == "baseline":
			baseline_submissions = {"draw_calls": case_data.draw_calls.mean, "primitives": case_data.primitives.mean}
		report.cases.append(case_data)
		partial_report.cases.append(case_data)
		print("TERRAIN_RENDER_ATTRIBUTION_CASE ", JSON.stringify({"label":label,"root_gpu_p50":case_data.root_gpu_ms.p50,"far_gpu_p50":case_data.far_gpu_ms.p50,"draw_p50":case_data.draw_calls.p50,"primitives_p50":case_data.primitives.p50}))
	_restore_original_state()
	if not _verify_case("baseline_repeat"):
		return
	report.status = "PASS"
	if not _write_report(report, HAZE_OUTPUT if "--haze-only" in OS.get_cmdline_user_args() else OUTPUT):
		return
	for viewport in measured_viewports:
		if is_instance_valid(viewport):
			RenderingServer.viewport_set_measure_render_time(viewport.get_viewport_rid(), false)
	game.queue_free()
	await process_frame
	ProjectSettings.set_setting("ringworld/runtime/haze_enabled", original_haze_enabled)
	print("TERRAIN_RENDER_ATTRIBUTION=PASS")
	quit(0)


func _create_game() -> Node:
	var prior_session := root.get_node_or_null("WorldSession")
	if prior_session != null:
		prior_session.name = "WorldSessionOriginalRenderAttribution"
	var session := SessionStub.new()
	session.name = "WorldSession"
	root.add_child(session)
	var launcher := LAUNCHER.instantiate()
	root.add_child(launcher)
	await process_frame
	launcher.call("_on_certified_preset_pressed")
	var selector: OptionButton = launcher.get("terrain_backend_selector")
	if selector == null:
		_fail("launcher terrain backend selector missing")
		return null
	selector.select(2) # Runtime-v2 terrain surface path.
	selector.emit_signal("item_selected", selector.selected)
	if not bool(launcher.call("_prepare_runtime_surface_generation", true)):
		_fail("runtime-v2 generation preparation failed")
		return null
	launcher.call("_save_selected_slot_settings")
	var next_game := GAME.instantiate()
	launcher.call("_configure_game", next_game, 5000000.0)
	_install_isolated_preferences(next_game)
	if failed:
		launcher.queue_free()
		next_game.queue_free()
		return null
	root.add_child(next_game)
	launcher.queue_free()
	var deadline := Time.get_ticks_msec() + 120000
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var candidate := next_game.get_node_or_null("Terrain3DGameplay")
		if candidate != null and bool(candidate.call("EnsureInitialized")):
			return next_game
	_fail("gameplay startup did not become ready")
	return null


func _install_isolated_preferences(next_game: Node) -> void:
	var drawer: Node = next_game.get_node_or_null("LiveTuningDrawer")
	if drawer == null:
		_fail("LiveTuningDrawer missing for isolated preferences")
		return
	for item in [["InstallGrassDefaults", "gameplay_grass_defaults.tres"], ["InstallVegetationDefaults", "gameplay_vegetation_defaults.tres"], ["InstallWaterDefaults", "gameplay_water_defaults.tres"], ["InstallAntiAliasingDefaults", "gameplay_anti_aliasing_defaults.tres"], ["InstallShadowDefaults", "gameplay_shadow_defaults.tres"], ["InstallSurfaceTerrainDefaults", "surface_tuning.tres"], ["InstallAtmosphereDefaults", "atmosphere_defaults.tres"]]:
		var destination := FIXTURE_ROOT.path_join(str(item[1]))
		var source := "user://" + str(item[1])
		if FileAccess.file_exists(source):
			var copy := FileAccess.open(destination, FileAccess.WRITE)
			if copy == null:
				_fail("could not copy isolated preference " + str(item[1]))
				return
			copy.store_string(FileAccess.get_file_as_string(source))
			copy.close()
		drawer.call(str(item[0]), destination)


func _wait_for_settle() -> bool:
	var stable := 0
	var deadline := Time.get_ticks_msec() + 120000
	while Time.get_ticks_msec() < deadline:
		await process_frame
		var diagnostics: Dictionary = surface.call("GetSurfaceDiagnostics")
		var streaming: Dictionary = diagnostics.get("streaming", {})
		var native: Dictionary = diagnostics.get("native", {})
		var preparation: Dictionary = native.get("preparation", {})
		var grass_ready := int(grass.get("PendingGrassTileBuildCount")) == 0 and int(grass.get("PendingGrassUploadCount")) == 0 and int(grass.get("ResidentGrassInstanceCount")) > 0
		var trees_ready := int(trees.get("VegetationPendingTiles")) == 0 and int(trees.get("GpuTreeResidentRootCount")) > 0
		var streaming_ready := int(streaming.get("pending_jobs", 0)) == 0 and int(streaming.get("active_jobs", 0)) == 0 and int(streaming.get("packing", 0)) == 0 and bool(preparation.get("ready", false))
		stable = stable + 1 if grass_ready and trees_ready and streaming_ready else 0
		if stable >= 12:
			return true
	_fail("steady gameplay workload did not settle")
	return false


func _enable_viewport_timing() -> void:
	measured_viewports = [root]
	for node in _all_nodes(game):
		if node is SubViewport:
			measured_viewports.append(node)
	for viewport in measured_viewports:
		RenderingServer.viewport_set_measure_render_time(viewport.get_viewport_rid(), true)


func _capture_original_state() -> Dictionary:
	var near_proxy_nodes: Array[Node3D] = [surface]
	for node in _all_nodes(game):
		if node is Node3D and node.get_script() != null and "RingTerrainProxySector" in node.get_script().resource_path:
			near_proxy_nodes.append(node)
	var far_terrain_nodes: Array[Node3D] = []
	# RingScaledFarViewport owns private render meshes. Restrict discovery to its
	# terrain-named geometry so clouds and other far background work stay active.
	for node in _all_nodes(scaled_far):
		if node is MeshInstance3D and (node.name == "ScaledSeededRingSurface" or node.name.begins_with("ScaledFarLod")):
			far_terrain_nodes.append(node)
	var far_lights: Array[DirectionalLight3D] = []
	for node in _all_nodes(scaled_far):
		if node is DirectionalLight3D:
			far_lights.append(node)
	var grass_geometry := _geometry_nodes(grass)
	var tree_geometry := _geometry_nodes(trees)
	return {"near_proxy_nodes": near_proxy_nodes, "near_proxy_paths": _node_paths(near_proxy_nodes), "near_proxy_visible": _node_visibilities(near_proxy_nodes), "far_terrain_nodes": far_terrain_nodes, "far_terrain_paths": _node_paths(far_terrain_nodes), "far_terrain_visible": _node_visibilities(far_terrain_nodes), "grass_visible": grass.visible, "trees_visible": trees.visible, "grass_geometry":grass_geometry,"grass_layers":_geometry_layers(grass_geometry),"tree_geometry":tree_geometry,"tree_layers":_geometry_layers(tree_geometry), "sun_shadow_enabled": sun.shadow_enabled, "far_lights": far_lights, "far_light_shadows": _light_shadows(far_lights)}


func _restore_original_state() -> void:
	if tuning_drawer != null:
		tuning_drawer.call("SetAtmosphereEnabled", false)
	for index in original_state.near_proxy_nodes.size():
		var node: Node3D = original_state.near_proxy_nodes[index]
		if is_instance_valid(node):
			node.visible = original_state.near_proxy_visible[index]
	for index in original_state.far_terrain_nodes.size():
		var node: Node3D = original_state.far_terrain_nodes[index]
		if is_instance_valid(node):
			node.visible = original_state.far_terrain_visible[index]
	grass.visible = original_state.grass_visible
	trees.visible = original_state.trees_visible
	_restore_geometry_layers(original_state.grass_geometry, original_state.grass_layers)
	_restore_geometry_layers(original_state.tree_geometry, original_state.tree_layers)
	sun.shadow_enabled = original_state.sun_shadow_enabled
	for index in original_state.far_lights.size():
		original_state.far_lights[index].shadow_enabled = original_state.far_light_shadows[index]


func _apply_case(label: String) -> bool:
	match label:
		"baseline", "baseline_repeat": pass
		"near_proxy_terrain_hidden":
			for node in original_state.near_proxy_nodes:
				node.visible = false
		"far_terrain_hidden":
			for node in original_state.far_terrain_nodes:
				node.visible = false
		"grass_hidden": _set_geometry_layers(original_state.grass_geometry, 0)
		"trees_hidden": _set_geometry_layers(original_state.tree_geometry, 0)
		"directional_shadows_off":
			sun.shadow_enabled = false
			for light in original_state.far_lights: light.shadow_enabled = false
		"natural_haze":
			tuning_drawer.call("SetAtmospherePreset", 0)
			tuning_drawer.call("SetAtmosphereEnabled", true)
		"cinematic_haze":
			tuning_drawer.call("SetAtmospherePreset", 1)
			tuning_drawer.call("SetAtmosphereEnabled", true)
		_: _fail("unknown case " + label); return false
	return true


func _verify_case(label: String) -> bool:
	if label.ends_with("haze"):
		var controller: Node = game.get_node_or_null("GraphicsQuality")
		var haze: Dictionary = controller.call("GetAtmosphereTuningDiagnostics") if controller != null else {}
		if not bool(haze.get("enabled", false)):
			_fail("atmosphere haze was not enabled for " + label)
			return false
	for node in original_state.near_proxy_nodes:
		if node.visible != (false if label == "near_proxy_terrain_hidden" else bool(original_state.near_proxy_visible[original_state.near_proxy_nodes.find(node)])):
			_fail("near/proxy terrain visibility was not applied for " + label)
			return false
	for node in original_state.far_terrain_nodes:
		if node.visible != (false if label == "far_terrain_hidden" else bool(original_state.far_terrain_visible[original_state.far_terrain_nodes.find(node)])):
			_fail("far terrain visibility was not applied for " + label)
			return false
	if grass.visible != bool(original_state.grass_visible):
		_fail("grass visibility was not applied for " + label)
		return false
	for index in original_state.grass_geometry.size():
		var grass_layer: int = 0 if label == "grass_hidden" else original_state.grass_layers[index]
		if original_state.grass_geometry[index].layers != grass_layer:
			_fail("grass render layer was not applied for " + label)
			return false
	if trees.visible != bool(original_state.trees_visible):
		_fail("tree visibility was not applied for " + label)
		return false
	for index in original_state.tree_geometry.size():
		var tree_layer: int = 0 if label == "trees_hidden" else original_state.tree_layers[index]
		if original_state.tree_geometry[index].layers != tree_layer:
			_fail("tree render layer was not applied for " + label)
			return false
	if sun.shadow_enabled != (false if label == "directional_shadows_off" else bool(original_state.sun_shadow_enabled)):
		_fail("directional shadow state was not applied for " + label)
		return false
	for light in original_state.far_lights:
		if light.shadow_enabled != (false if label == "directional_shadows_off" else bool(original_state.far_light_shadows[original_state.far_lights.find(light)])):
			_fail("far directional shadow state was not applied for " + label)
			return false
	return true


func _sample_case(label: String) -> Dictionary:
	var warmup_start := Time.get_ticks_usec()
	for frame in WARMUP_FRAMES:
		await RenderingServer.frame_post_draw
		if failed:
			return {}
		if (Time.get_ticks_usec() - warmup_start) * .000001 >= 2.0:
			break
	if not _verify_case(label):
		return {}
	var root_gpu: Array[float] = []
	var root_cpu: Array[float] = []
	var far_gpu: Array[float] = []
	var far_cpu: Array[float] = []
	var draw_calls: Array[float] = []
	var primitives: Array[float] = []
	var video_bytes: Array[float] = []
	var frames: Array[Dictionary] = []
	var sample_start := Time.get_ticks_usec()
	while draw_calls.size() < MIN_SAMPLE_FRAMES or (Time.get_ticks_usec() - sample_start) * .000001 < TARGET_SAMPLE_SECONDS:
		if (Time.get_ticks_usec() - sample_start) * .000001 >= MAX_SAMPLE_SECONDS:
			break
		var start := Time.get_ticks_usec()
		await RenderingServer.frame_post_draw
		if failed:
			return {}
		var viewport_timings := _viewport_timings()
		var root_timing: Dictionary = viewport_timings[0]
		root_gpu.append(root_timing.gpu_ms)
		root_cpu.append(root_timing.cpu_ms)
		var far_timing := _far_viewport_timing(viewport_timings)
		far_gpu.append(far_timing.gpu_ms)
		far_cpu.append(far_timing.cpu_ms)
		draw_calls.append(Performance.get_monitor(Performance.Monitor.RENDER_TOTAL_DRAW_CALLS_IN_FRAME))
		primitives.append(Performance.get_monitor(Performance.Monitor.RENDER_TOTAL_PRIMITIVES_IN_FRAME))
		video_bytes.append(Performance.get_monitor(Performance.Monitor.RENDER_VIDEO_MEM_USED))
		frames.append({"wall_ms": (Time.get_ticks_usec() - start) * .001, "runtime_counters": runtime_counters.call("CaptureCounters")})
	var submissions_changed := label.begins_with("baseline") or label.ends_with("haze") or absf(_mean(draw_calls) - _baseline_submission_mean("draw_calls")) > 0.01 or absf(_mean(primitives) - _baseline_submission_mean("primitives")) > 0.01
	if not submissions_changed:
		_fail("%s did not change draw-call or primitive submissions" % label)
		return {}
	if label == "baseline_repeat":
		var draw_delta := absf(_mean(draw_calls) - _baseline_submission_mean("draw_calls"))
		var primitive_delta := absf(_mean(primitives) - _baseline_submission_mean("primitives"))
		var draw_tolerance := maxf(1.0, absf(_baseline_submission_mean("draw_calls")) * .05)
		var primitive_tolerance := maxf(1.0, absf(_baseline_submission_mean("primitives")) * .05)
		_expect(draw_delta <= draw_tolerance and primitive_delta <= primitive_tolerance,
			"baseline restoration changed submissions (draw %.3f, primitives %.3f)" % [draw_delta, primitive_delta])
		if failed:
			return {}
	return {"label": label, "warmup_frames": WARMUP_FRAMES, "samples": draw_calls.size(), "sample_seconds": (Time.get_ticks_usec() - sample_start) * .000001, "hidden": _hidden_state(), "root_gpu_ms": _stats(root_gpu), "root_cpu_ms": _stats(root_cpu), "far_gpu_ms": _stats(far_gpu), "far_cpu_ms": _stats(far_cpu), "draw_calls": _stats(draw_calls), "primitives": _stats(primitives), "video_memory_bytes": _stats(video_bytes), "runtime_counters": frames, "workload_identity": _workload_identity()}


func _baseline_submission_mean(metric: String) -> float:
	return float(baseline_submissions.get(metric, NAN))


func _viewport_timings() -> Array[Dictionary]:
	var result: Array[Dictionary] = []
	for viewport in measured_viewports:
		if not is_instance_valid(viewport):
			continue
		var rid := viewport.get_viewport_rid()
		result.append({"path": str(viewport.get_path()), "gpu_ms": RenderingServer.viewport_get_measured_render_time_gpu(rid), "cpu_ms": RenderingServer.viewport_get_measured_render_time_cpu(rid)})
	return result


func _far_viewport_timing(timings: Array[Dictionary]) -> Dictionary:
	for timing in timings:
		if "ScaledFarViewport" in str(timing.path):
			return timing
	return {"gpu_ms": 0.0, "cpu_ms": 0.0}


func _hidden_state() -> Dictionary:
	var hidden_paths: Array[String] = []
	for node in original_state.near_proxy_nodes:
		if not node.visible:
			hidden_paths.append(str(node.get_path()))
	for node in original_state.far_terrain_nodes:
		if not node.visible:
			hidden_paths.append(str(node.get_path()))
	var grass_hidden := _hidden_geometry_count(original_state.grass_geometry)
	var trees_hidden := _hidden_geometry_count(original_state.tree_geometry)
	return {"terrain_paths_hidden": hidden_paths, "grass_hidden": grass_hidden == original_state.grass_geometry.size(), "grass_hidden_geometry_count":grass_hidden, "trees_hidden": trees_hidden == original_state.tree_geometry.size(), "tree_hidden_geometry_count":trees_hidden, "directional_shadows_disabled": not sun.shadow_enabled, "far_directional_shadows_disabled": _all_far_shadows_disabled()}

func _hidden_geometry_count(nodes: Array) -> int:
	var count := 0
	for node in nodes:
		if node.layers == 0: count += 1
	return count


func _node_paths(nodes: Array) -> Array[String]:
	var paths: Array[String] = []
	for node in nodes:
		paths.append(str(node.get_path()))
	return paths


func _node_visibilities(nodes: Array) -> Array[bool]:
	var values: Array[bool] = []
	for node in nodes:
		values.append(node.visible)
	return values

func _geometry_nodes(owner: Node) -> Array:
	var result: Array = []
	for node in _all_nodes(owner):
		if node is GeometryInstance3D:
			result.append(node)
	return result
func _geometry_layers(nodes: Array) -> Array[int]:
	var result: Array[int] = []
	for node in nodes: result.append(node.layers)
	return result
func _set_geometry_layers(nodes: Array, layers: int) -> void:
	for node in nodes: node.layers = layers
func _restore_geometry_layers(nodes: Array, layers: Array) -> void:
	for index in nodes.size():
		if is_instance_valid(nodes[index]): nodes[index].layers = layers[index]


func _light_shadows(lights: Array) -> Array[bool]:
	var values: Array[bool] = []
	for light in lights:
		values.append(light.shadow_enabled)
	return values


func _all_far_shadows_disabled() -> bool:
	for light in original_state.far_lights:
		if light.shadow_enabled:
			return false
	return true


func _workload_identity() -> Dictionary:
	return {"surface": surface.call("GetSurfaceDiagnostics"), "grass_roots": grass.get("ResidentGrassInstanceCount"), "grass_batches": grass.get("GpuCullingBatchCount"), "tree_roots": trees.get("GpuTreeResidentRootCount"), "tree_batches": trees.get("GpuTreeBatchCount")}


func _quality_identity() -> Dictionary:
	var graphics: Node = game.get_node_or_null("GraphicsQuality")
	var cycle: Node = game.get_node_or_null("RingDayNightCycle")
	return {"preset": graphics.get("CurrentPresetName") if graphics != null else "unknown", "resolution":root.size,"render_scale": root.get("scaling_3d_scale"), "msaa": root.get("msaa_3d"), "taa": root.get("use_taa"), "shadow_atlas":graphics.get("EffectiveDirectionalShadowAtlasSize") if graphics != null else 0, "sun_elevation":cycle.get("SunElevationDegrees") if cycle != null else NAN, "daylight_factor":cycle.get("DaylightFactor") if cycle != null else NAN}


func _all_nodes(node: Node) -> Array[Node]:
	var result: Array[Node] = [node]
	for child in node.get_children():
		result.append_array(_all_nodes(child))
	return result


func _stats(values: Array[float]) -> Dictionary:
	var sorted := values.duplicate()
	sorted.sort()
	return {"p50": sorted[int(sorted.size() * .5)], "p95": sorted[mini(sorted.size() - 1, int(ceil(sorted.size() * .95)) - 1)], "mean": _mean(sorted), "min": sorted[0], "max": sorted[-1]}


func _mean(values: Array[float]) -> float:
	var total := 0.0
	for value in values:
		total += value
	return total / values.size() if not values.is_empty() else 0.0


func _write_report(report: Dictionary, path: String = OUTPUT) -> bool:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(path.get_base_dir()))
	var file := FileAccess.open(path, FileAccess.WRITE)
	if file == null:
		_fail("could not write report")
		return false
	file.store_string(JSON.stringify(report, "\t"))
	file.close()
	return true


func _fail(message: String) -> void:
	if failed:
		return
	failed = true
	partial_report.status = "FAIL"
	partial_report.failure = message
	partial_report.cases = partial_report.get("cases", [])
	var output_path: String = HAZE_OUTPUT if "--haze-only" in OS.get_cmdline_user_args() else OUTPUT
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(output_path.get_base_dir()))
	var file := FileAccess.open(output_path, FileAccess.WRITE)
	if file != null:
		file.store_string(JSON.stringify(partial_report, "\t"))
		file.close()
	push_error("TERRAIN_RENDER_ATTRIBUTION=FAIL " + message)
	if is_instance_valid(game):
		game.queue_free()
	ProjectSettings.set_setting("ringworld/runtime/haze_enabled", original_haze_enabled)
	quit(1)


func _expect(condition: bool, message: String) -> void:
	if not condition:
		_fail(message)
