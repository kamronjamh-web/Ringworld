extends SceneTree

## Rendered, Certified Ring-only smoke.  This creates a disposable integration
## scene and settings resource under res://.godot; WorldSaveManager is removed
## before the scene ever enters the tree.
const SCENE: PackedScene = preload("res://scenes/VoxelRingIntegrationTest.tscn")
const LAUNCHER: PackedScene = preload("res://scenes/WorldSizeLauncher.tscn")
const PROBE: Script = preload("res://scripts/Tools/PerformanceDiagnosticsContractTest.cs")
const OUTPUT := "res://.godot/performance_diagnostics/certified-smoke"
const SETTINGS := "res://.godot/performance_diagnostics/certified-smoke/settings.tres"
const LIMIT_SECONDS := 180.0

var _scene: Node
var _monitor: Node
var _probe: Node
var _started := 0
var _failed := false
var _phase_contexts: Array[Dictionary] = []

func _initialize() -> void:
	_started = Time.get_ticks_usec()
	call_deferred("_boot")

func _process(_delta: float) -> bool:
	if float(Time.get_ticks_usec() - _started) / 1000000.0 > LIMIT_SECONDS:
		_fail("timeout")
	return false

func _boot() -> void:
	if RenderingServer.get_rendering_device() == null:
		_fail("Forward+ RenderingDevice required for rendered smoke")
		return
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT))
	DisplayServer.window_set_size(Vector2i(1280, 720))
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
	_scene = SCENE.instantiate()
	var saves: Node = _scene.get_node_or_null("WorldSaveManager")
	if saves != null: saves.free()
	_monitor = _scene.get_node_or_null("RingPerformanceMonitor")
	if _monitor == null:
		_fail("RingPerformanceMonitor missing")
		return
	# Must precede add_child so no production settings load/save is possible.
	_monitor.call("InstallSettingsPathForTests", SETTINGS)
	_monitor.call("InstallReportDirectoryForTests", ProjectSettings.globalize_path(OUTPUT + "/reports"))
	_monitor.set("Enabled", true)
	_monitor.set("DetailedGpuEnabled", false)
	var launcher := LAUNCHER.instantiate()
	root.add_child(launcher)
	launcher.call("_on_certified_preset_pressed")
	launcher.call("_configure_game", _scene, 5000000.0)
	launcher.free()
	root.add_child(_scene)
	_probe = PROBE.new()
	root.add_child(_probe)
	for _frame in 90: await process_frame
	if not await _run_phase(false): return
	if not await _run_phase(true): return
	if not await _run_phase(false): return
	await _exercise_overlay_and_drawer()
	if _failed: return
	_monitor.set("DetailedGpuEnabled", true)
	for _frame in 90: await RenderingServer.frame_post_draw
	var trees := _tree_context()
	print("CERTIFIED_PERFORMANCE_TREES ", JSON.stringify(trees))
	if int(trees.get("resident_roots", 0)) <= 0 or int(trees.get("current_batches", 0)) <= 0:
		_fail("tree streaming did not retain resident GPU coverage: " + JSON.stringify(trees))
		return
	var rendered: Dictionary = _probe.call("ValidateRenderedSnapshot", _monitor)
	if not bool(rendered.get("pass", false)):
		_fail("no meaningful rendered diagnostics: " + JSON.stringify(rendered))
		return
	var gpu: Dictionary = _probe.call("ValidateGpuWaterFft", _monitor)
	print("CERTIFIED_PERFORMANCE_GPU ", JSON.stringify(gpu))
	if bool(gpu.get("gpu_available", false)) and not bool(gpu.get("pass", false)):
		_fail("GPU.WaterFft did not produce a bounded positive interval: " + JSON.stringify(gpu))
		return
	await _capture("overlay_detailed_gpu")
	await _capture("grave_diagnostics_detailed_gpu")
	var report_path: String = str(_monitor.call("CaptureSlowdown"))
	if report_path.begins_with("Export failed") or report_path.begins_with("Export refused"):
		_fail("bounded report export failed: " + report_path)
		return
	var exported: Dictionary = _probe.call("ValidateExport", report_path)
	if not bool(exported.get("pass", false)):
		_fail("report was not valid bounded JSON: " + JSON.stringify(exported))
		return
	print("CERTIFIED_PERFORMANCE_DIAGNOSTICS_SMOKE=PASS report=", report_path)
	# Outside the timed report: make sure budgeting has not merely postponed an
	# ever-growing replacement backlog. The live world must eventually settle.
	var tree_layer := _scene.get_node_or_null("BiomeDecorations")
	var settled := false
	for _frame in 900:
		if int(tree_layer.get("VegetationPendingTiles")) == 0 \
			and not bool(tree_layer.get("TreePreparationPending")) \
			and bool(tree_layer.get("GpuTreeCullingSettled")) \
			and int(tree_layer.get("GpuTreeFallbackBatchCount")) == 0:
			settled = true
			break
		await RenderingServer.frame_post_draw
	print("CERTIFIED_TREE_STREAMING_SETTLED ", JSON.stringify(_tree_context()))
	if not settled:
		_fail("tree streaming did not drain pending builds/replacements")
		return
	_scene.queue_free()
	_probe.queue_free()
	quit(0)

func _run_phase(fast_flight: bool) -> bool:
	var result: Dictionary = _probe.call("SetBenchmarkPhase", _scene, fast_flight)
	if not bool(result.get("pass", false)):
		_fail("benchmark phase setup failed: " + JSON.stringify(result))
		return false
	# Approximately two seconds of genuine rendered time at the capped capture rate.
	for _frame in 120:
		if _failed: return false
		await RenderingServer.frame_post_draw
	_phase_contexts.append({"phase": str(result.get("phase", "unknown")), "context": _probe.call("ReadContext", _monitor), "trees": _tree_context()})
	print("CERTIFIED_PERFORMANCE_PHASE ", JSON.stringify(_phase_contexts[-1]))
	return true

func _tree_context() -> Dictionary:
	var trees := _scene.get_node_or_null("BiomeDecorations")
	if trees == null: return {}
	return {
		"resident_roots": int(trees.get("GpuTreeResidentRootCount")),
		"current_batches": int(trees.get("GpuTreeBatchCount")),
		"fallback_batches": int(trees.get("GpuTreeFallbackBatchCount")),
		"preparation_pending": bool(trees.get("TreePreparationPending")),
		"groups_rebuilt": int(trees.get("TreePreparationGroupsRebuilt")),
		"groups_reused": int(trees.get("TreePreparationGroupsReused")),
		"last_preparation_ms": float(trees.get("LastTreePreparationMilliseconds")),
		"streaming_budget_ms": float(trees.get("TreeStreamingBudgetMilliseconds")),
	}

func _exercise_overlay_and_drawer() -> void:
	var drawer: Node = _scene.get_node_or_null("LiveTuningDrawer")
	var hud: Node = _scene.get_node_or_null("LodDebugHud")
	if drawer == null or hud == null:
		_fail("diagnostics UI nodes missing")
		return
	_send_key(KEY_F6)
	for _frame in 20: await process_frame
	# Synthetic viewport input can be consumed by another control in automated
	# runs. Exercise the public HUD refresh fallback before treating that as a
	# diagnostics failure; real F6 input remains covered by RingLodDebugHud.
	if not _hud_has_diagnostics_text(hud):
		hud.visible = true
		hud.call("RefreshNow")
		await process_frame
	if not _hud_has_diagnostics_text(hud):
		_fail("F6 diagnostics overlay did not populate")
		return
	await _capture("overlay")
	_send_key(KEY_QUOTELEFT)
	for _frame in 20: await process_frame
	drawer.call("RefreshDiagnostics")
	var diagnostics: Node = _tab_named(drawer, "Diagnostics")
	if diagnostics == null:
		_fail("grave menu Diagnostics tab missing")
		return
	for required in ["SectionPerformancemonitoring", "SectionLiveresults", "SectionCaptureandsettings", "DiagnosticsMonitoringToggle", "DiagnosticsHistorySeconds", "DiagnosticsCpuGraph", "DiagnosticsRows", "DiagnosticsCaptureSlowdown"]:
		if diagnostics.find_child(required, true, false) == null:
			_fail("Diagnostics control missing: " + required)
			return
	# Foldables start closed; expand the live-result section so graph/row data is
	# genuinely visible in the capture rather than merely present in the tree.
	var results := diagnostics.find_child("SectionLiveresults", true, false)
	if results != null: results.set("folded", false)
	for _frame in 3: await process_frame
	await _capture("grave_diagnostics")

func _tab_named(drawer: Node, title: String) -> Node:
	var tabs := drawer.find_child("LiveTuningTabs", true, false) as TabContainer
	if tabs == null: tabs = _first_tab_container(drawer)
	if tabs == null: return null
	for index in tabs.get_tab_count():
		if tabs.get_tab_title(index) == title:
			tabs.current_tab = index
			return tabs.get_child(index)
	return null

func _first_tab_container(node: Node) -> TabContainer:
	if node is TabContainer: return node as TabContainer
	for child in node.get_children():
		var found := _first_tab_container(child)
		if found != null: return found
	return null

func _hud_has_diagnostics_text(hud: Node) -> bool:
	for label in hud.find_children("*", "Label", true, false):
		var text := str((label as Label).text)
		if text.contains("FPS") and (text.contains("CPU") or text.contains("PERFORMANCE")): return true
	return false

func _send_key(key: Key) -> void:
	var event := InputEventKey.new()
	event.keycode = key
	event.pressed = true
	root.get_viewport().push_input(event, true)

func _capture(label: String) -> void:
	await RenderingServer.frame_post_draw
	var image := root.get_texture().get_image()
	if image.is_empty() or image.save_png(ProjectSettings.globalize_path(OUTPUT + "/" + label + ".png")) != OK:
		_fail("screenshot failed: " + label)

func _fail(reason: String) -> void:
	if _failed: return
	_failed = true
	push_error("CERTIFIED_PERFORMANCE_DIAGNOSTICS_SMOKE=FAIL " + reason)
	if is_instance_valid(_scene): _scene.queue_free()
	quit(1)
