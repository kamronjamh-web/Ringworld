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
const FIXTURE_HELPER := preload("res://tools/AtmosphereVisualFixtureHelper.cs")
# P7-0 measurement gate (`-- --haze-only`): each matrix label is sampled at each pose. `-- --no-matrix` skips it.
const MATRIX_LABELS := ["haze_off", "physical_q4", "physical_q8", "physical_q8_lut_off", "physical_q16", "cloud_mask_off", "cloud_mask_on"]
const MATRIX_POSES := [
	{"name": "ground_horizon", "pitch": 0.0, "lift": 0.0},
	{"name": "sky_up", "pitch": 70.0, "lift": 0.0},
	{"name": "alt_12km", "pitch": -10.0, "lift": 12000.0},
]
const GATE_SKY_MS := 0.2
const GATE_MASK_MS := 0.2
const GATE_NON_SKY_MS := 0.5
# Main-viewport haze attribution (`-- --haze-only --haze-main`, replaces the P7-0 matrix). Every physical_q8_* label is Physical q8 with
# one consumer class forced to identity haze through the `ring_haze_debug_mode` material knob (ring_atmosphere.gdshaderinc), or Sunshine's
# profile cleared (RingSunshineCloudBridge.SetDebugCloudHazeDisabled). haze_off keeps the Physical model and quality (only Enabled
# changes, so the sky/LUT state matches the physical rows); haze_off_legacy is the old P7-0 first-pose state. physical_q8_short_rays
# clamps every spatial haze ray to 12 km (one-piece quadrature; cost probe only). Each pose runs the labels forward, then reversed
# (`--main-rounds=N`, default 2), so haze_off and physical_q8 bracket every block; `--main-seconds=S` (default 8) per case,
# `--main-poses=a,b` filters MATRIX_POSES.
const MAIN_LABELS := ["haze_off", "physical_q8", "physical_q8_no_terrain_haze", "physical_q8_no_grass_haze", "physical_q8_no_tree_haze", "physical_q8_no_ocean_haze", "physical_q8_no_other_spatial_haze", "physical_q8_no_spatial_haze", "physical_q8_no_cloud_haze", "physical_q8_short_rays", "physical_q4", "physical_q16", "haze_off_legacy"]
const MAIN_CLASS_LABELS := {"physical_q8_no_terrain_haze": "terrain", "physical_q8_no_grass_haze": "grass", "physical_q8_no_tree_haze": "tree", "physical_q8_no_ocean_haze": "ocean", "physical_q8_no_other_spatial_haze": "other", "physical_q8_no_spatial_haze": "all", "physical_q8_short_rays": "all"}
const HAZE_DEBUG_PARAM := "ring_haze_debug_mode"
# Shader file basenames per consumer class; any other material whose shader declares HAZE_DEBUG_PARAM is "other". Terrain3D nodes are terrain.
const HAZE_DEBUG_CLASSES := {
	"terrain": ["terrain3d_ring_surface", "ring_terrain_proxy", "ring_terrain_global", "voxel_ring_terrain"],
	"grass": ["ring_grass_cards"],
	"tree": ["ring_tree", "imported_tree", "imported_tree_impostor"],
	"ocean": ["ring_ocean_proxy", "ring_ocean_global"],
}
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
var ground_height := 0.0
var cloud_bridge: Node
var sample_min_frames := MIN_SAMPLE_FRAMES
var sample_target_seconds := TARGET_SAMPLE_SECONDS
var sample_max_seconds := MAX_SAMPLE_SECONDS
# Active ring_haze_debug_mode case: class ("terrain", "grass", "tree", "ocean", "other" or "all"), mode (0 = none), touched materials.
var haze_debug_category := ""
var haze_debug_mode := 0
var haze_debug_touched := {}
var haze_debug_counts := {}
var haze_debug_shader_cache := {}

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
	ground_height = spawn_height
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
	# Haze-only runs never hide far terrain, so they do not need the far-terrain discovery.
	var needs_far_terrain := not ("--haze-only" in OS.get_cmdline_user_args())
	if original_state.near_proxy_paths.is_empty() or (needs_far_terrain and original_state.far_terrain_paths.is_empty()):
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
	cloud_bridge = game.get_node_or_null("SunshineCloudBridge")
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
	if "--haze-only" in OS.get_cmdline_user_args() and "--haze-main" in OS.get_cmdline_user_args():
		report["main_matrix_cases"] = []
		var main_ok: bool = await _run_haze_main_matrix(report)
		if not main_ok or failed:
			return
	elif "--haze-only" in OS.get_cmdline_user_args() and not "--no-matrix" in OS.get_cmdline_user_args():
		report["matrix_cases"] = []
		var matrix_ok: bool = await _run_haze_matrix(report)
		if not matrix_ok or failed:
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


## P7-0: haze quality / mask cost matrix over three poses. Writes report.matrix_cases and prints a compact table.
func _run_haze_matrix(report: Dictionary) -> bool:
	var helper: RefCounted = FIXTURE_HELPER.new()
	# Hold the canonical pose: presentation setters write the transform directly, so gravity must not move it.
	explorer.process_mode = Node.PROCESS_MODE_DISABLED
	for pose in MATRIX_POSES:
		if not bool(helper.call("Place", explorer, FIXTURE_AROUND, ground_height + float(pose.lift), FIXTURE_ACROSS)):
			_fail("matrix pose placement failed for " + str(pose.name))
			return false
		explorer.call("SetPresentationView", 0.0, float(pose.pitch))
		for label in MATRIX_LABELS:
			_restore_original_state()
			if not _apply_case(label) or not _verify_case(label):
				return false
			var case_data := await _sample_case(label)
			if failed:
				return false
			if label == "physical_q8" or label == "physical_q8_lut_off":
				var key: Vector4 = _sky_lut_host().call("GetPublishedKey")
				print("P7_0_LUT_KEY pose=%s label=%s key=%s" % [pose.name, label, key])
				if label == "physical_q8" and key.w < 0.5:
					_fail("sky-view LUT key was not valid for physical_q8 at pose " + str(pose.name))
					return false
				if label == "physical_q8_lut_off" and key.w > 0.5:
					_fail("sky-view LUT key was still valid for physical_q8_lut_off at pose " + str(pose.name))
					return false
			case_data["pose"] = pose.duplicate()
			report.matrix_cases.append(case_data)
			partial_report.cases.append(case_data)
	_restore_original_state()
	explorer.process_mode = Node.PROCESS_MODE_INHERIT
	explorer.call("SetCanonicalSpawnMeters", FIXTURE_AROUND, ground_height, FIXTURE_ACROSS)
	explorer.call("SetPresentationView", 0.0, -10.0)
	_print_matrix_table(report.matrix_cases)
	return true


func _matrix_main_far(case_data: Dictionary) -> Vector2:
	return Vector2(float(case_data.root_gpu_ms.p50), float(case_data.far_gpu_ms.p50))


func _mask_viewport_ms(case_data: Dictionary) -> float:
	var total := 0.0
	for key in case_data.viewport_gpu_ms:
		if str(key).begins_with("CloudTransmittanceMask"):
			total += float(case_data.viewport_gpu_ms[key].mean)
	return total


func _print_matrix_table(cases: Array) -> void:
	print("P7_0_TABLE (GPU ms, p50 over frames; mask_vp = mean of CloudTransmittanceMask* viewports; d_* = vs haze_off)")
	print("%-15s %-15s %8s %8s %8s %8s %8s %6s" % ["pose", "label", "main", "far", "d_main", "d_far", "mask_vp", "maskOn"])
	var by_pose := {}
	for case_data in cases:
		var pose_name := str(case_data.pose.name)
		if not by_pose.has(pose_name):
			by_pose[pose_name] = {}
		by_pose[pose_name][case_data.label] = case_data
	for pose in MATRIX_POSES:
		var rows: Dictionary = by_pose.get(pose.name, {})
		var off: Vector2 = _matrix_main_far(rows["haze_off"]) if rows.has("haze_off") else Vector2.ZERO
		for label in MATRIX_LABELS:
			if not rows.has(label):
				continue
			var mf := _matrix_main_far(rows[label])
			print("%-15s %-15s %8.3f %8.3f %8.3f %8.3f %8.3f %6s" % [pose.name, label, mf.x, mf.y, mf.x - off.x, mf.y - off.y, _mask_viewport_ms(rows[label]), str(rows[label].cloud_mask_active)])
	# Gate readout. Far-viewport deltas vs haze_off at q8 are the haze cost; the sky-up pose is mostly sky pixels,
	# the ground-horizon pose is mostly terrain/backdrop (non-sky) pixels.
	var sky_up: Dictionary = by_pose.get("sky_up", {})
	var ground: Dictionary = by_pose.get("ground_horizon", {})
	if sky_up.has("haze_off") and sky_up.has("physical_q8"):
		var sky_ms := _matrix_main_far(sky_up["physical_q8"]).y - _matrix_main_far(sky_up["haze_off"]).y
		print("P7_0_GATE sky_view_lut: far sky haze (sky_up q8 - off) = %.3f ms -> %s (threshold %.1f)" % [sky_ms, "GO" if sky_ms >= GATE_SKY_MS else "NO-GO", GATE_SKY_MS])
	if ground.has("haze_off") and ground.has("physical_q8"):
		var non_sky := _matrix_main_far(ground["physical_q8"]).y - _matrix_main_far(ground["haze_off"]).y
		print("P7_0_GATE transmittance_lut: far non-sky haze (ground_horizon q8 - off, before any sky LUT) = %.3f ms -> %s (threshold %.1f)" % [non_sky, "GO" if non_sky >= GATE_NON_SKY_MS else "NO-GO", GATE_NON_SKY_MS])
	for pose in MATRIX_POSES:
		var lut_rows: Dictionary = by_pose.get(pose.name, {})
		if lut_rows.has("physical_q8") and lut_rows.has("physical_q8_lut_off"):
			var lut_on := _matrix_main_far(lut_rows["physical_q8"])
			var lut_off := _matrix_main_far(lut_rows["physical_q8_lut_off"])
			print("P7_0_LUT %s: far lut_on=%.3f lut_off=%.3f saving=%.3f ms; main saving=%.3f ms" % [pose.name, lut_on.y, lut_off.y, lut_off.y - lut_on.y, lut_off.x - lut_on.x])
	for pose in MATRIX_POSES:
		var rows: Dictionary = by_pose.get(pose.name, {})
		if rows.has("cloud_mask_on") and rows.has("cloud_mask_off"):
			var on: Vector2 = _matrix_main_far(rows["cloud_mask_on"])
			var off_mask: Vector2 = _matrix_main_far(rows["cloud_mask_off"])
			var mask_ms: float = (on.y - off_mask.y) + (_mask_viewport_ms(rows["cloud_mask_on"]) - _mask_viewport_ms(rows["cloud_mask_off"]))
			print("P7_0_GATE mask_hoist: %s mask cost (on - off, far + mask viewports) = %.3f ms -> %s (threshold %.1f)" % [pose.name, mask_ms, "GO" if mask_ms >= GATE_MASK_MS else "NO-GO", GATE_MASK_MS])


## Main-viewport haze attribution: MAIN_LABELS at each pose, forward then reversed per round. Writes report.main_matrix_cases.
func _run_haze_main_matrix(report: Dictionary) -> bool:
	if cloud_bridge == null or not cloud_bridge.has_method("SetDebugCloudHazeDisabled"):
		_fail("SunshineCloudBridge with SetDebugCloudHazeDisabled is required for --haze-main")
		return false
	var rounds := maxi(1, int(_arg_value("--main-rounds=", "2")))
	var seconds := maxf(2.0, float(_arg_value("--main-seconds=", "8")))
	var pose_filter: PackedStringArray = _arg_value("--main-poses=", "").split(",", false)
	sample_target_seconds = seconds
	sample_max_seconds = seconds * 2.0
	sample_min_frames = 150
	# A/B for per-instance vegetation haze: `--veg-instance-haze=off` runs the whole matrix with the per-vertex path.
	var veg_instance_haze := _arg_value("--veg-instance-haze=", "on") != "off"
	if tuning_drawer != null and tuning_drawer.has_method("SetAtmosphereVegetationInstanceHaze"):
		tuning_drawer.call("SetAtmosphereVegetationInstanceHaze", veg_instance_haze)
	print("P7_0_MAIN_VEG_INSTANCE_HAZE=", "on" if veg_instance_haze else "off")
	var helper: RefCounted = FIXTURE_HELPER.new()
	explorer.process_mode = Node.PROCESS_MODE_DISABLED
	var poses_run: Array = []
	for pose in MATRIX_POSES:
		if not pose_filter.is_empty() and not str(pose.name) in pose_filter:
			continue
		poses_run.append(pose)
		if not bool(helper.call("Place", explorer, FIXTURE_AROUND, ground_height + float(pose.lift), FIXTURE_ACROSS)):
			_fail("main matrix pose placement failed for " + str(pose.name))
			return false
		explorer.call("SetPresentationView", 0.0, float(pose.pitch))
		for round_index in rounds:
			# `--main-labels=a,b,...` limits the matrix to a subset (quick A/B runs); default is every MAIN_LABELS entry.
			var label_filter: PackedStringArray = _arg_value("--main-labels=", "").split(",", false)
			var order: Array = MAIN_LABELS.duplicate() if label_filter.is_empty() else MAIN_LABELS.filter(func(l): return l in label_filter)
			if round_index % 2 == 1:
				order.reverse()
			for label in order:
				_restore_original_state()
				if not _apply_main_case(label) or not _verify_case(label):
					return false
				var case_data := await _sample_case(label)
				if failed:
					return false
				case_data["pose"] = pose.duplicate()
				case_data["round"] = round_index
				case_data["haze_debug"] = {"category": haze_debug_category, "mode": haze_debug_mode, "targets": haze_debug_counts.duplicate(), "missed": _haze_debug_missed(), "cloud_haze_disabled": bool(cloud_bridge.call("IsDebugCloudHazeDisabled"))}
				report.main_matrix_cases.append(case_data)
				partial_report.cases.append(case_data)
				print("P7_0_MAIN_CASE ", JSON.stringify({"pose": pose.name, "round": round_index, "label": label, "main_p50": case_data.root_gpu_ms.p50, "far_p50": case_data.far_gpu_ms.p50, "samples": case_data.samples, "debug": case_data.haze_debug}))
	_restore_original_state()
	sample_target_seconds = TARGET_SAMPLE_SECONDS
	sample_max_seconds = MAX_SAMPLE_SECONDS
	sample_min_frames = MIN_SAMPLE_FRAMES
	explorer.process_mode = Node.PROCESS_MODE_INHERIT
	explorer.call("SetCanonicalSpawnMeters", FIXTURE_AROUND, ground_height, FIXTURE_ACROSS)
	explorer.call("SetPresentationView", 0.0, -10.0)
	_print_main_table(report.main_matrix_cases, poses_run)
	if not veg_instance_haze and tuning_drawer != null and tuning_drawer.has_method("SetAtmosphereVegetationInstanceHaze"):
		tuning_drawer.call("SetAtmosphereVegetationInstanceHaze", true)
	return true


func _arg_value(prefix: String, fallback: String) -> String:
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with(prefix):
			return arg.trim_prefix(prefix)
	return fallback


func _main_label_quality(label: String) -> int:
	if label == "haze_off_legacy":
		return -1
	if label == "physical_q4":
		return 4
	if label == "physical_q16":
		return 16
	return 8


func _apply_main_case(label: String) -> bool:
	if not label in MAIN_LABELS:
		_fail("unknown main matrix case " + label)
		return false
	if label == "haze_off_legacy":
		tuning_drawer.call("SetAtmosphereHazeModel", 0)
		tuning_drawer.call("SetAtmospherePreset", 0)
		tuning_drawer.call("SetAtmosphereHazeQuality", 8)
		tuning_drawer.call("SetAtmosphereEnabled", false)
		return true
	_enable_physical_haze(_main_label_quality(label))
	if label == "haze_off":
		tuning_drawer.call("SetAtmosphereEnabled", false)
	elif label == "physical_q8_no_cloud_haze":
		cloud_bridge.call("SetDebugCloudHazeDisabled", true)
	elif MAIN_CLASS_LABELS.has(label):
		haze_debug_category = str(MAIN_CLASS_LABELS[label])
		haze_debug_mode = 2 if label == "physical_q8_short_rays" else 1
		_apply_haze_debug()
	return true


func _verify_main_case(label: String) -> bool:
	var controller: Node = game.get_node_or_null("GraphicsQuality")
	var haze: Dictionary = controller.call("GetAtmosphereTuningDiagnostics") if controller != null else {}
	if bool(haze.get("enabled", false)) != (not label.begins_with("haze_off")):
		_fail("atmosphere enabled state was wrong for " + label)
		return false
	if int(haze.get("haze_model", -1)) != (0 if label == "haze_off_legacy" else 1):
		_fail("haze model was wrong for " + label + ": " + str(haze.get("haze_model", -1)))
		return false
	var quality := _main_label_quality(label)
	if quality > 0 and int(haze.get("haze_quality", -1)) != quality:
		_fail("haze quality was not applied for " + label + ": " + str(haze.get("haze_quality", -1)))
		return false
	if bool(cloud_bridge.call("IsDebugCloudHazeDisabled")) != (label == "physical_q8_no_cloud_haze"):
		_fail("Sunshine debug haze state was wrong for " + label)
		return false
	var expected_category: String = str(MAIN_CLASS_LABELS.get(label, ""))
	if haze_debug_category != expected_category:
		_fail("haze debug class was wrong for " + label + ": " + haze_debug_category)
		return false
	if not expected_category.is_empty():
		var touched := 0
		for category in haze_debug_counts:
			if expected_category == "all" or category == expected_category:
				touched += int(haze_debug_counts[category])
		if touched == 0:
			# A class with no main-viewport materials at this pose (e.g. ocean) still samples; the table shows targets = 0.
			push_warning("haze debug class %s has no main-viewport materials for %s" % [expected_category, label])
	return true


# ---- ring_haze_debug_mode plumbing ----

func _in_main_viewport(node: Node) -> bool:
	return node.is_inside_tree() and node.get_viewport() == root


func _node_shader_materials(node) -> Array:
	var candidates: Array = [node.material_override, node.material_overlay]
	if node is MeshInstance3D and node.mesh != null:
		for index in node.get_surface_override_material_count():
			candidates.append(node.get_surface_override_material(index))
		for index in node.mesh.get_surface_count():
			candidates.append(node.mesh.surface_get_material(index))
	elif node is MultiMeshInstance3D and node.multimesh != null and node.multimesh.mesh != null:
		for index in node.multimesh.mesh.get_surface_count():
			candidates.append(node.multimesh.mesh.surface_get_material(index))
	var result: Array = []
	for candidate in candidates:
		var material: Variant = candidate
		var guard := 0
		while material is ShaderMaterial and guard < 4:
			result.append(material)
			material = (material as ShaderMaterial).next_pass
			guard += 1
	return result


func _shader_has_haze_debug(shader: Shader) -> bool:
	if shader == null:
		return false
	var id := shader.get_instance_id()
	if not haze_debug_shader_cache.has(id):
		var found := false
		for uniform in shader.get_shader_uniform_list():
			if str(uniform.get("name", "")) == HAZE_DEBUG_PARAM:
				found = true
				break
		haze_debug_shader_cache[id] = found
	return bool(haze_debug_shader_cache[id])


func _haze_debug_class(shader: Shader) -> String:
	var base := shader.resource_path.get_file().get_basename()
	for category in HAZE_DEBUG_CLASSES:
		if base in HAZE_DEBUG_CLASSES[category]:
			return category
	return "other"


## Haze-capable materials in the main viewport by class: Terrain3D plugin materials plus every ShaderMaterial whose shader declares
## ring_haze_debug_mode. Instances drawn straight through RenderingServer (no node) are not reachable and stay on.
func _haze_debug_scan() -> Dictionary:
	var result := {"terrain": [], "grass": [], "tree": [], "ocean": [], "other": []}
	var seen := {}
	for node in _all_nodes(game):
		if node.is_class("Terrain3D"):
			if not _in_main_viewport(node):
				continue
			var terrain_material: Object = node.call("get_material")
			if terrain_material != null and not seen.has(terrain_material.get_instance_id()):
				seen[terrain_material.get_instance_id()] = true
				result.terrain.append(terrain_material)
			continue
		if not node is GeometryInstance3D or not _in_main_viewport(node):
			continue
		for material in _node_shader_materials(node):
			var id: int = material.get_instance_id()
			if seen.has(id):
				continue
			seen[id] = true
			if not _shader_has_haze_debug(material.shader):
				continue
			result[_haze_debug_class(material.shader)].append(material)
	return result


func _set_haze_debug_value(material: Object, value: int) -> void:
	if material is ShaderMaterial:
		(material as ShaderMaterial).set_shader_parameter(HAZE_DEBUG_PARAM, value)
	elif material.has_method("set_shader_param"):
		material.call("set_shader_param", HAZE_DEBUG_PARAM, value)


func _get_haze_debug_value(material: Object) -> int:
	var value: Variant = null
	if material is ShaderMaterial:
		value = (material as ShaderMaterial).get_shader_parameter(HAZE_DEBUG_PARAM)
	elif material.has_method("get_shader_param"):
		value = material.call("get_shader_param", HAZE_DEBUG_PARAM)
	else:
		return haze_debug_mode # Cannot read back; assume the write landed.
	return int(value) if value != null else 0


func _apply_haze_debug() -> void:
	if haze_debug_mode == 0:
		return
	var scan := _haze_debug_scan()
	haze_debug_counts = {}
	for category in scan:
		haze_debug_counts[category] = scan[category].size()
		if haze_debug_category != "all" and category != haze_debug_category:
			continue
		for material in scan[category]:
			_set_haze_debug_value(material, haze_debug_mode)
			haze_debug_touched[material.get_instance_id()] = material


## Materials of the active class that do not carry the active mode after sampling (created mid-case or reset by their owner).
func _haze_debug_missed() -> int:
	if haze_debug_mode == 0:
		return 0
	var scan := _haze_debug_scan()
	var missed := 0
	for category in scan:
		if haze_debug_category != "all" and category != haze_debug_category:
			continue
		for material in scan[category]:
			if _get_haze_debug_value(material) != haze_debug_mode:
				missed += 1
	return missed


func _clear_haze_debug() -> void:
	for id in haze_debug_touched:
		var material: Object = haze_debug_touched[id]
		if is_instance_valid(material):
			_set_haze_debug_value(material, 0)
	haze_debug_touched = {}
	haze_debug_counts = {}
	haze_debug_category = ""
	haze_debug_mode = 0
	if cloud_bridge != null and is_instance_valid(cloud_bridge) and cloud_bridge.has_method("SetDebugCloudHazeDisabled"):
		cloud_bridge.call("SetDebugCloudHazeDisabled", false)


func _round_values(cases: Array, metric: String) -> Array[float]:
	var values: Array[float] = []
	for case_data in cases:
		values.append(float(case_data[metric].p50))
	return values


func _spread(values: Array[float]) -> float:
	if values.is_empty():
		return 0.0
	var low: float = values[0]
	var high: float = values[0]
	for value in values:
		low = minf(low, value)
		high = maxf(high, value)
	return high - low


func _print_main_table(cases: Array, poses: Array) -> void:
	var by_pose := {}
	for case_data in cases:
		var pose_name := str(case_data.pose.name)
		if not by_pose.has(pose_name):
			by_pose[pose_name] = {}
		if not by_pose[pose_name].has(case_data.label):
			by_pose[pose_name][case_data.label] = []
		by_pose[pose_name][case_data.label].append(case_data)
	print("P7_0_MAIN (GPU ms; main/far = mean over rounds of each case's p50; spread = max - min of the round p50s; d_off = label - haze_off; saving = physical_q8 - label; targets = main-viewport materials switched)")
	print("%-15s %-34s %8s %7s %8s %8s %8s %8s %8s" % ["pose", "label", "main", "spread", "far", "d_off", "saving", "d_off_far", "targets"])
	for pose in poses:
		var rows: Dictionary = by_pose.get(pose.name, {})
		if not rows.has("haze_off") or not rows.has("physical_q8"):
			continue
		var means := {}
		for label in MAIN_LABELS:
			if rows.has(label):
				means[label] = Vector2(_mean(_round_values(rows[label], "root_gpu_ms")), _mean(_round_values(rows[label], "far_gpu_ms")))
		var off: Vector2 = means["haze_off"]
		var q8: Vector2 = means["physical_q8"]
		for label in MAIN_LABELS:
			if not means.has(label):
				continue
			var mf: Vector2 = means[label]
			var debug: Dictionary = rows[label][0].get("haze_debug", {})
			var category: String = str(debug.get("category", ""))
			var targets := 0
			for key in debug.get("targets", {}):
				if category == "all" or key == category:
					targets += int(debug.targets[key])
			var saving := "" if label in ["haze_off", "physical_q8", "haze_off_legacy", "physical_q4", "physical_q16"] else "%8.3f" % (q8.x - mf.x)
			print("%-15s %-34s %8.3f %7.3f %8.3f %8.3f %8s %8.3f %8s" % [pose.name, label, mf.x, _spread(_round_values(rows[label], "root_gpu_ms")), mf.y, mf.x - off.x, saving, mf.y - off.y, str(targets) if not category.is_empty() else "-"])
		var saving_of := func(which: String) -> float: return q8.x - means[which].x if means.has(which) else NAN
		var classes: float = 0.0
		for label in ["physical_q8_no_terrain_haze", "physical_q8_no_grass_haze", "physical_q8_no_tree_haze", "physical_q8_no_ocean_haze", "physical_q8_no_other_spatial_haze"]:
			classes += saving_of.call(label)
		var total := q8.x - off.x
		var spatial: float = saving_of.call("physical_q8_no_spatial_haze")
		var cloud: float = saving_of.call("physical_q8_no_cloud_haze")
		print("P7_0_MAIN_SUM %s: total(q8 - off) %.3f = spatial %.3f [terrain %.3f, grass %.3f, tree %.3f, ocean %.3f, other %.3f; class sum %.3f] + sunshine %.3f + residual %.3f" % [pose.name, total, spatial, saving_of.call("physical_q8_no_terrain_haze"), saving_of.call("physical_q8_no_grass_haze"), saving_of.call("physical_q8_no_tree_haze"), saving_of.call("physical_q8_no_ocean_haze"), saving_of.call("physical_q8_no_other_spatial_haze"), classes, cloud, total - spatial - cloud])
		var q4: float = means["physical_q4"].x if means.has("physical_q4") else NAN
		var q16: float = means["physical_q16"].x if means.has("physical_q16") else NAN
		print("P7_0_MAIN_QUAD %s: spatial multi-piece share at q8 (q8 - short_rays) %.3f; per-piece slope (q16 - q4) / 12 = %.4f ms; q4 %.3f q8 %.3f q16 %.3f; legacy_off - off %.3f (sky model/LUT confound of the old P7-0 first pose)" % [pose.name, saving_of.call("physical_q8_short_rays"), (q16 - q4) / 12.0, q4 - off.x, total, q16 - off.x, (means["haze_off_legacy"].x - off.x) if means.has("haze_off_legacy") else NAN])
		print("P7_0_MAIN_NOISE %s: haze_off rounds %s (spread %.3f), physical_q8 rounds %s (spread %.3f)" % [pose.name, str(_round_values(rows["haze_off"], "root_gpu_ms")), _spread(_round_values(rows["haze_off"], "root_gpu_ms")), str(_round_values(rows["physical_q8"], "root_gpu_ms")), _spread(_round_values(rows["physical_q8"], "root_gpu_ms"))])


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
	return {"near_proxy_nodes": near_proxy_nodes, "near_proxy_paths": _node_paths(near_proxy_nodes), "near_proxy_visible": _node_visibilities(near_proxy_nodes), "far_terrain_nodes": far_terrain_nodes, "far_terrain_paths": _node_paths(far_terrain_nodes), "far_terrain_visible": _node_visibilities(far_terrain_nodes), "cloud_mask_enabled": bool(scaled_far.get("CloudTransmittanceMaskEnabled")), "grass_visible": grass.visible, "trees_visible": trees.visible, "grass_geometry":grass_geometry,"grass_layers":_geometry_layers(grass_geometry),"tree_geometry":tree_geometry,"tree_layers":_geometry_layers(tree_geometry), "sun_shadow_enabled": sun.shadow_enabled, "far_lights": far_lights, "far_light_shadows": _light_shadows(far_lights)}


func _restore_original_state() -> void:
	_clear_haze_debug()
	_sky_lut_host().call("SetSkyViewEnabled", true)
	if original_state.has("cloud_mask_enabled"):
		scaled_far.set("CloudTransmittanceMaskEnabled", original_state.cloud_mask_enabled)
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
			# P2a A/B: pass `-- --haze-only --model=physical` to measure the physical haze model instead of Legacy.
			tuning_drawer.call("SetAtmosphereHazeModel", 1 if "--model=physical" in OS.get_cmdline_user_args() else 0)
			tuning_drawer.call("SetAtmospherePreset", 0)
			tuning_drawer.call("SetAtmosphereEnabled", true)
		"cinematic_haze":
			tuning_drawer.call("SetAtmosphereHazeModel", 1 if "--model=physical" in OS.get_cmdline_user_args() else 0)
			tuning_drawer.call("SetAtmospherePreset", 1)
			tuning_drawer.call("SetAtmosphereEnabled", true)
		"haze_off": tuning_drawer.call("SetAtmosphereEnabled", false)
		"physical_q4", "physical_q8", "physical_q16":
			_enable_physical_haze(int(label.trim_prefix("physical_q")))
		"physical_q8_lut_off":
			# P7-0 LUT A/B: physical_q8 with the sky-view LUT disabled directly (not persisted; re-enabled in _restore_original_state).
			_enable_physical_haze(8)
			_sky_lut_host().call("SetSkyViewEnabled", false)
		"cloud_mask_off", "cloud_mask_on":
			_enable_physical_haze(8)
			scaled_far.set("CloudTransmittanceMaskEnabled", label == "cloud_mask_on")
		_: _fail("unknown case " + label); return false
	return true


func _sky_lut_host() -> RefCounted:
	return (load("res://scripts/Atmosphere/RingAtmosphereLuts.cs") as Script).new()


func _enable_physical_haze(quality: int) -> void:
	tuning_drawer.call("SetAtmosphereHazeModel", 1)
	tuning_drawer.call("SetAtmospherePreset", 0)
	tuning_drawer.call("SetAtmosphereHazeQuality", quality)
	tuning_drawer.call("SetAtmosphereEnabled", true)


func _is_matrix_label(label: String) -> bool:
	return label in MATRIX_LABELS or label in MAIN_LABELS


func _verify_case(label: String) -> bool:
	if label in MAIN_LABELS:
		if not _verify_main_case(label):
			return false
	elif _is_matrix_label(label):
		var controller: Node = game.get_node_or_null("GraphicsQuality")
		var haze: Dictionary = controller.call("GetAtmosphereTuningDiagnostics") if controller != null else {}
		if bool(haze.get("enabled", false)) != (label != "haze_off"):
			_fail("atmosphere enabled state was wrong for " + label)
			return false
		if label.begins_with("physical_q") and int(haze.get("haze_quality", -1)) != int(label.get_slice("_lut_", 0).trim_prefix("physical_q")):
			_fail("haze quality was not applied for " + label + ": " + str(haze.get("haze_quality", -1)))
			return false
		if label.begins_with("cloud_mask_") and bool(scaled_far.get("CloudTransmittanceMaskEnabled")) != (label == "cloud_mask_on"):
			_fail("cloud transmittance mask state was not applied for " + label)
			return false
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
		# Pose moves in the haze matrix can stream geometry out; freed nodes are skipped.
		if is_instance_valid(original_state.grass_geometry[index]) and original_state.grass_geometry[index].layers != grass_layer:
			_fail("grass render layer was not applied for " + label)
			return false
	if trees.visible != bool(original_state.trees_visible):
		_fail("tree visibility was not applied for " + label)
		return false
	for index in original_state.tree_geometry.size():
		var tree_layer: int = 0 if label == "trees_hidden" else original_state.tree_layers[index]
		if is_instance_valid(original_state.tree_geometry[index]) and original_state.tree_geometry[index].layers != tree_layer:
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
	if haze_debug_mode != 0:
		# Re-apply to materials created during the warmup (streaming), then let the parameter writes land.
		_apply_haze_debug()
		for frame in 3:
			await RenderingServer.frame_post_draw
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
	var per_viewport := {}
	var sample_start := Time.get_ticks_usec()
	while draw_calls.size() < sample_min_frames or (Time.get_ticks_usec() - sample_start) * .000001 < sample_target_seconds:
		if (Time.get_ticks_usec() - sample_start) * .000001 >= sample_max_seconds:
			break
		var start := Time.get_ticks_usec()
		await RenderingServer.frame_post_draw
		if failed:
			return {}
		var viewport_timings := _viewport_timings()
		var root_timing: Dictionary = viewport_timings[0]
		root_gpu.append(root_timing.gpu_ms)
		root_cpu.append(root_timing.cpu_ms)
		for timing in viewport_timings:
			var key := str(timing.path).get_file()
			if not per_viewport.has(key):
				per_viewport[key] = [] as Array[float]
			per_viewport[key].append(timing.gpu_ms)
		var far_timing := _far_viewport_timing(viewport_timings)
		far_gpu.append(far_timing.gpu_ms)
		far_cpu.append(far_timing.cpu_ms)
		draw_calls.append(Performance.get_monitor(Performance.Monitor.RENDER_TOTAL_DRAW_CALLS_IN_FRAME))
		primitives.append(Performance.get_monitor(Performance.Monitor.RENDER_TOTAL_PRIMITIVES_IN_FRAME))
		video_bytes.append(Performance.get_monitor(Performance.Monitor.RENDER_VIDEO_MEM_USED))
		frames.append({"wall_ms": (Time.get_ticks_usec() - start) * .001, "runtime_counters": runtime_counters.call("CaptureCounters")})
	var submissions_changed := label.begins_with("baseline") or label.ends_with("haze") or _is_matrix_label(label) or absf(_mean(draw_calls) - _baseline_submission_mean("draw_calls")) > 0.01 or absf(_mean(primitives) - _baseline_submission_mean("primitives")) > 0.01
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
	return {"label": label, "warmup_frames": WARMUP_FRAMES, "samples": draw_calls.size(), "sample_seconds": (Time.get_ticks_usec() - sample_start) * .000001, "hidden": _hidden_state(), "root_gpu_ms": _stats(root_gpu), "root_cpu_ms": _stats(root_cpu), "far_gpu_ms": _stats(far_gpu), "far_cpu_ms": _stats(far_cpu), "viewport_gpu_ms": _viewport_stats(per_viewport), "cloud_mask_active": bool(scaled_far.get("CloudMaskActive")), "draw_calls": _stats(draw_calls), "primitives": _stats(primitives), "video_memory_bytes": _stats(video_bytes), "runtime_counters": frames, "workload_identity": _workload_identity()}


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


func _viewport_stats(per_viewport: Dictionary) -> Dictionary:
	var result := {}
	for key in per_viewport:
		var values: Array[float] = []
		values.assign(per_viewport[key])
		result[key] = _stats(values)
	return result


func _far_viewport_timing(timings: Array[Dictionary]) -> Dictionary:
	# Prefer the far ring SubViewport itself; the generic match below also hits mask/compose children.
	for timing in timings:
		if str(timing.path).ends_with("/FarRingViewport"):
			return timing
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
		if is_instance_valid(node) and node.layers == 0: count += 1
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
	for node in nodes:
		if is_instance_valid(node): node.layers = layers
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
