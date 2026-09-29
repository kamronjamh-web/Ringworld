extends SceneTree

# Isolated regression for the visual-only Terrain3D smooth shading preference.
# It deliberately uses the playable streaming fixture so a toggle must not request,
# publish, or collide against a new height window.
const PLAYABLE_SCENE := preload("res://scenes/TerrainGenerationPlayable.tscn")
const FIXTURE_DIRECTORY := "res://.godot/terrain-generation-tests"
const REPORT_PATH := FIXTURE_DIRECTORY + "/terrain-surface-shading-report.json"
const MOUNTAIN_AROUND := 1570796.925
const MOUNTAIN_ACROSS := 159000.125
const WARMUP_FRAMES := 60
const MEASURED_FRAMES := 120

var _checks := 0
var _failures := PackedStringArray()
var _finished := false
var _capture := false
var _captures: Dictionary = {}
var _timings: Dictionary = {}
var _uniform_checks: Array[Dictionary] = []
var _run_fixture_directory := ""
var _recipe_path := ""

func _init() -> void:
	_capture = OS.get_cmdline_user_args().has("--capture") and DisplayServer.get_name() != "headless"
	create_timer(90.0).timeout.connect(func(): _failures.append("90 second watchdog expired"); _finish())
	call_deferred("_run")

func _run() -> void:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(FIXTURE_DIRECTORY))
	_run_fixture_directory = FIXTURE_DIRECTORY + "/terrain-shading-%d" % Time.get_ticks_usec()
	_recipe_path = _run_fixture_directory + "/terrain_surface_shading_fixture.tres"
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(_run_fixture_directory))
	var recipe: TerrainGenerationRecipe = TerrainGenerationRecipe.create_detail_default()
	recipe.mountain_detail_mode = 2
	_expect(TerrainGenerationPreviewStore.save_recipe(recipe, _recipe_path).is_empty(), "fixed detail v3 mode 2 fixture saves")
	var playable: Node = PLAYABLE_SCENE.instantiate()
	# Streaming anchors are 2m lattice values. The queried point retains the requested
	# mountain coordinate, which lies inside this committed window.
	playable.configure_test_fixture(_recipe_path, int(floor(MOUNTAIN_AROUND / 2.0)) * 2, int(floor(MOUNTAIN_ACROSS / 2.0)) * 2)
	root.add_child(playable)
	await _await_ready(playable)
	var diagnostics: Dictionary = playable.get_preview_diagnostics()
	_expect(diagnostics.ready, "fixed v3 mode 2 mountain window commits")
	if not diagnostics.ready:
		playable.queue_free()
		await process_frame
		_finish()
		return
	var saved: TerrainGenerationRecipe = playable.get_recipe_snapshot()
	_expect(saved.generator_id == TerrainGenerationRecipe.DETAIL_GENERATOR_ID and saved.mountain_detail_mode == 2, "fixture uses the requested detail recipe")
	var surface: Node = playable.get_node("RingTerrain3DSurface")
	var drawer: Node = playable.get_node("RingLiveTuningDrawer")
	var toggle := drawer.find_child("SmoothTerrainShading", true, false) as CheckButton
	if toggle == null:
		# Compatibility fallback for a pre-name UI build; the test still requires the
		# visible control rather than calling the production setter directly.
		for candidate in _find_check_buttons(drawer):
			if candidate.text == "Smooth terrain shading": toggle = candidate; break
	_expect(toggle != null, "live tuning drawer exposes smooth terrain shading checkbox")
	if toggle == null:
		playable.queue_free()
		await process_frame
		_finish()
		return
	var before := _snapshot(surface, playable)
	_expect(bool(before.ready), "surface is ready before appearance change")
	_expect(_uniform_matches(surface, true), "native Terrain3D material starts with smooth shading enabled")
	if _capture:
		playable.set_physics_process(false)
		_set_capture_camera(playable, surface)
	# Exercise the same CheckButton signal path that a player uses.
	toggle.set_pressed(false)
	await _settle_appearance()
	var after_false := _snapshot(surface, playable)
	_expect(not toggle.button_pressed, "checkbox applies flat terrain shading")
	_expect(_uniform_matches(surface, false), "native Terrain3D material receives flat shading uniform")
	_expect(_same_terrain_state(before, after_false), "flat shading does not regenerate height, query, or collision state")
	if _capture: await _measure_mode("flat", true)
	toggle.set_pressed(true)
	await _settle_appearance()
	var after_true := _snapshot(surface, playable)
	_expect(toggle.button_pressed, "checkbox restores smooth terrain shading")
	_expect(_uniform_matches(surface, true), "native Terrain3D material receives smooth shading uniform")
	_expect(_same_terrain_state(before, after_true), "smooth shading does not regenerate height, query, or collision state")
	if _capture: await _measure_mode("smooth", true)
	# Repeat flat after smooth so the measured samples include both transition
	# directions. Screenshots remain one per state to keep capture artifacts bounded.
	toggle.set_pressed(false)
	await _settle_appearance()
	var after_false_repeat := _snapshot(surface, playable)
	_expect(not toggle.button_pressed and _uniform_matches(surface, false), "flat shading repeats through the live checkbox")
	_expect(_same_terrain_state(before, after_false_repeat), "repeated flat shading does not regenerate terrain")
	if _capture: await _measure_mode("flat_repeat", false)
	# Let the drawer's deferred persistence write complete, then recreate the playable
	# fixture to prove false/true state has no hidden terrain-side write requirement.
	await create_timer(.7).timeout
	playable.queue_free()
	await process_frame
	var reopened: Node = PLAYABLE_SCENE.instantiate()
	reopened.configure_test_fixture(_recipe_path, int(floor(MOUNTAIN_AROUND / 2.0)) * 2, int(floor(MOUNTAIN_ACROSS / 2.0)) * 2)
	root.add_child(reopened)
	await _await_ready(reopened)
	var reopened_surface: Node = reopened.get_node("RingTerrain3DSurface")
	var reopened_drawer: Node = reopened.get_node("RingLiveTuningDrawer")
	var reopened_toggle := reopened_drawer.find_child("SmoothTerrainShading", true, false) as CheckButton
	_expect(reopened_toggle != null and not reopened_toggle.button_pressed, "reopened isolated preferences preserve flat shading")
	_expect(_uniform_matches(reopened_surface, false), "reopened native material uses persisted flat shading")
	reopened.queue_free()
	await process_frame
	_finish()

func _await_ready(playable: Node) -> void:
	for frame in 1800:
		await process_frame
		var diagnostics: Dictionary = playable.get_preview_diagnostics()
		if diagnostics.ready and diagnostics.committed_revision == diagnostics.revision: return
	_failures.append("playable did not commit fixed v3 mode 2 window")

func _settle_appearance() -> void:
	for frame in 4: await process_frame

func _set_capture_camera(playable: Node, surface: Node) -> void:
	var camera := playable.get_node("Player/Head/Camera3D") as Camera3D
	camera.position = Vector3(90.0, 100.0, 120.0)
	camera.look_at(Vector3.ZERO, Vector3.UP)
	surface.call("UpdateCamera", camera.global_position)

func _snapshot(surface: Node, playable: Node) -> Dictionary:
	var preview: Dictionary = playable.get_preview_diagnostics()
	var native: Dictionary = surface.call("GetPreparedDiagnostics")
	var height := float(surface.call("SampleCommittedHeight", MOUNTAIN_AROUND, MOUNTAIN_ACROSS))
	return {
		"ready": preview.ready,
		"revision": preview.committed_revision,
		"query_revision": native.get("query_revision", -1),
		"collision_revision": native.get("collision_revision", -1),
		"render_revision": native.get("render_revision", -1),
		"height": height,
		"height_finite": is_finite(height),
	}

func _same_terrain_state(a: Dictionary, b: Dictionary) -> bool:
	return bool(a.ready) and bool(b.ready) and bool(a.height_finite) and bool(b.height_finite) \
		and a.revision == b.revision and a.query_revision == b.query_revision \
		and a.collision_revision == b.collision_revision and a.render_revision == b.render_revision \
		and a.height == b.height

func _find_check_buttons(node: Node) -> Array[CheckButton]:
	var result: Array[CheckButton] = []
	for child in node.get_children():
		if child is CheckButton: result.append(child)
		result.append_array(_find_check_buttons(child))
	return result

func _uniform_matches(surface: Node, expected: bool) -> bool:
	var terrain: Object = surface.get("TerrainNode")
	if terrain == null:
		_failures.append("active Terrain3D node is unavailable for material validation")
		return false
	var material: Variant = terrain.call("get_material")
	if material == null:
		_failures.append("Terrain3D material is unavailable for uniform validation")
		return false
	var actual: Variant = material.call("get_shader_param", "smooth_terrain_shading")
	if actual == null:
		if DisplayServer.get_name() == "headless":
			_uniform_checks.append({"expected": expected, "available": false, "reason": "headless renderer did not expose Terrain3DMaterial shader parameter"})
			print("SKIP renderer-only smooth terrain shading uniform assertion (headless)")
			return true
		_failures.append("rendered Terrain3D material did not expose smooth_terrain_shading")
		return false
	if typeof(actual) != TYPE_BOOL:
		_failures.append("smooth_terrain_shading shader parameter has unexpected type %s" % typeof(actual))
		return false
	_uniform_checks.append({"expected": expected, "available": true, "actual": actual})
	return actual == expected

func _measure_mode(mode: String, save_screenshot: bool) -> void:
	var viewport_rid: RID = root.get_viewport_rid()
	RenderingServer.viewport_set_measure_render_time(viewport_rid, true)
	for frame in WARMUP_FRAMES: await RenderingServer.frame_post_draw
	var gpu_samples: Array[float] = []
	var cpu_samples: Array[float] = []
	for frame in MEASURED_FRAMES:
		await RenderingServer.frame_post_draw
		var gpu_ms := RenderingServer.viewport_get_measured_render_time_gpu(viewport_rid)
		var cpu_ms := RenderingServer.viewport_get_measured_render_time_cpu(viewport_rid)
		if is_finite(gpu_ms) and gpu_ms > 0.0: gpu_samples.append(gpu_ms)
		if is_finite(cpu_ms) and cpu_ms > 0.0: cpu_samples.append(cpu_ms)
	_timings[mode] = {
		"warmup_frames": WARMUP_FRAMES, "measured_frames": MEASURED_FRAMES,
		"gpu_timing_available": not gpu_samples.is_empty(), "gpu_positive_samples": gpu_samples.size(), "gpu_median_ms": _median(gpu_samples),
		"cpu_timing_available": not cpu_samples.is_empty(), "cpu_positive_samples": cpu_samples.size(), "cpu_median_ms": _median(cpu_samples),
		"note": "Viewport GPU and CPU values are renderer timing samples, not an FPS claim. A zero or unavailable value is recorded as unavailable without failing this visual regression."
	}
	if not save_screenshot: return
	var image := root.get_texture().get_image()
	var image_path := FIXTURE_DIRECTORY + "/terrain-surface-shading-" + mode + ".png"
	_expect(image.save_png(image_path) == OK, mode + " terrain shading screenshot saves")
	_captures[mode] = {"image": image_path, "sha256": _sha256(image.get_data())}

func _sha256(bytes: PackedByteArray) -> String:
	var context := HashingContext.new()
	if context.start(HashingContext.HASH_SHA256) != OK: return ""
	if context.update(bytes) != OK: return ""
	return context.finish().hex_encode()

func _median(samples: Array[float]) -> Variant:
	if samples.is_empty(): return null
	var sorted := samples.duplicate()
	sorted.sort()
	return sorted[sorted.size() / 2]

func _expect(condition: bool, label: String) -> void:
	_checks += 1
	if not condition: _failures.append(label)

func _finish() -> void:
	if _finished: return
	_finished = true
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(FIXTURE_DIRECTORY))
	var output := FileAccess.open(REPORT_PATH, FileAccess.WRITE)
	if output:
		output.store_string(JSON.stringify({"checks": _checks, "failures": _failures, "uniform_checks": _uniform_checks, "capture": _captures, "timings": _timings, "requested_mountain_point": {"around": MOUNTAIN_AROUND, "across": MOUNTAIN_ACROSS}, "recipe": "ringworld_surface_detail_v3 mode 2"}, "\t") + "\n")
		output.close()
	if _failures.is_empty():
		print("TERRAIN_SURFACE_SHADING=PASS checks=", _checks)
		quit(0)
	else:
		push_error("TERRAIN_SURFACE_SHADING=FAIL ", "; ".join(_failures))
		quit(1)
