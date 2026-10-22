extends SceneTree
## Phase C WO C3 step 1 (diagnostics only): boots the start menu, lets it warm-load the last-played world exactly as shipped and
## records a per-frame decomposition with tools/WarmStallProbe.cs (physics / signal handlers / per-owner node process / deferred +
## RS sync / draw, GC and pipeline deltas). Changes no gameplay behaviour; the probe only adds inert marker nodes.
##
## ALWAYS run through tools/menu_warm_stall_probe.ps1 (isolated APPDATA holding COPIES of the saved slots). Refuses to run when
## user:// is not a phase_c_live profile. Output: res://.godot/phase_c_live/stall_probe/{stall_frames.csv,stall_detail.txt,summary.json}.
## User args: --stall-probe-out=<dir name>  --stall-probe-tail=<seconds after reveal_ready, default 8>
##            --stall-probe-hold=<seconds to idle after the report, for dotnet-trace rundown>
## Pass --no-warm-world for the menu-only baseline (the probe then records 30 s of menu frames).

const MENU := preload("res://scenes/StartMenu.tscn")
const PROBE := preload("res://tools/WarmStallProbe.cs")
const OUT_BASE := "res://.godot/phase_c_live"
const WARM_TIMEOUT_MS := 240000

var _probe: Node
var _instrumented_world: Node
var _instrumented_roots := {}
var _out := "stall_probe"
var _tail := 8.0
var _hold := 0.0
var _node_log: Array = []
var _last_node_usec := 0

func _initialize() -> void:
	node_added.connect(_on_node_added)
	call_deferred("_run")

## Logs the gap before each node addition (GDScript handler, so it precedes any C# NodeAdded subscriber for the same node).
func _on_node_added(node: Node) -> void:
	var now := Time.get_ticks_usec()
	if _last_node_usec > 0 and now - _last_node_usec > 50000 and _node_log.size() < 200:
		_node_log.append({"usec": now, "gap_ms": float(now - _last_node_usec) / 1000.0, "node": str(node.get_path()) if node.is_inside_tree() else str(node.name), "class": node.get_class(), "previous": _last_node_name})
	_last_node_usec = now
	_last_node_name = "%s (%s)" % [node.name, node.get_class()]

var _last_node_name := ""
var _boot_marks := {}

func _boot_mark(label: String) -> void:
	_boot_marks[label] = Time.get_ticks_usec()

## A zero-length timer and a one-step tween created after the menu: they fire in this frame's timer / tween pass.
func get_tree_timer_mark() -> void:
	create_timer(0.0).timeout.connect(_boot_mark.bind("timer_pass"))
	var tween := create_tween()
	tween.tween_callback(_boot_mark.bind("tween_pass"))

func _arg(prefix: String, fallback: String) -> String:
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with(prefix):
			return arg.substr(prefix.length())
	return fallback

func _process(_delta: float) -> bool:
	if _probe == null:
		return false
	for child in root.get_children():
		if not _instrumented_roots.has(child.get_instance_id()) and not str(child.name).begins_with("StallProbe"):
			_instrumented_roots[child.get_instance_id()] = true
			_probe.InstrumentRootChild(child)
	var loader := root.get_node_or_null("RingWorldLoading")
	if loader != null:
		var world: Node = loader.get("_game")
		if is_instance_valid(world) and world.is_inside_tree() and world != _instrumented_world:
			_instrumented_world = world
			var added: int = _probe.InstrumentWorld(world)
			print("STALL_PROBE instrumented world %s with %d markers at %d usec" % [world.name, added, Time.get_ticks_usec()])
	return false

func _run() -> void:
	var user_dir := ProjectSettings.globalize_path("user://").replace("\\", "/").to_lower()
	var real_roaming := OS.get_environment("USERPROFILE").replace("\\", "/").to_lower() + "/appdata/roaming"
	if user_dir.begins_with(real_roaming) or not user_dir.contains("phase_c_live"):
		push_error("Refusing to run: user:// (%s) is not an isolated phase_c_live APPDATA. Use menu_warm_stall_probe.ps1." % user_dir)
		quit(2)
		return
	_out = _arg("--stall-probe-out=", "stall_probe")
	_tail = float(_arg("--stall-probe-tail=", "8"))
	# Keeps the process alive after the report so an attached dotnet-trace session can stop (and write its rundown) first.
	_hold = float(_arg("--stall-probe-hold=", "0"))
	var out_dir := ProjectSettings.globalize_path(OUT_BASE).path_join(_out)
	DirAccess.make_dir_recursive_absolute(out_dir)
	_probe = PROBE.new()
	_probe.name = "StallProbe"
	root.add_child(_probe)
	_probe.Begin()
	await process_frame
	var align_begin: int = _probe.StallProbeAlignmentSpin(60.0)
	await process_frame
	var boot_usec := Time.get_ticks_usec()
	var menu: Control = MENU.instantiate()
	var instantiated_usec := Time.get_ticks_usec()
	# FIFO deferred markers bracket the deferred calls the menu queues during its _ready (boot-frame attribution).
	_boot_mark.call_deferred("deferred_before_menu")
	# Connected before the menu's own _ready connects resized -> _update_ui_scale, so this runs first on every emission.
	menu.resized.connect(func() -> void: _boot_mark("menu_resized_%d" % _boot_marks.size()))
	root.add_child(menu)
	_boot_mark.call_deferred("deferred_after_menu")
	get_tree_timer_mark()
	var added_usec := Time.get_ticks_usec()
	current_scene = menu
	var current_usec := Time.get_ticks_usec()
	var warm: Node = root.get_node_or_null("RingWarmWorld")
	_probe.SetContextSources(root.get_node_or_null("RingWorldLoading"), warm)
	var loader_started := 0
	if root.get_node_or_null("RingWorldLoading") != null:
		loader_started = int(root.get_node("RingWorldLoading").get("_started_usec"))
	print("STALL_PROBE menu_added_usec=%d instantiate_ms=%.1f add_child_ms=%.1f (loader.begin at +%.1f ms) current_scene_ms=%.1f context_ms=%.1f" % [boot_usec,
		(instantiated_usec - boot_usec) / 1000.0, (added_usec - instantiated_usec) / 1000.0, (loader_started - instantiated_usec) / 1000.0,
		(current_usec - added_usec) / 1000.0, (Time.get_ticks_usec() - current_usec) / 1000.0])
	var summary := {"boot_usec": boot_usec, "align_begin_usec": align_begin, "main_os_thread": _probe.get("MainThreadOsId")}
	if warm == null:
		print("STALL_PROBE no warm world (baseline); recording 30 s of menu frames")
		var end := Time.get_ticks_msec() + 30000
		while Time.get_ticks_msec() < end:
			await process_frame
	else:
		var deadline := Time.get_ticks_msec() + WARM_TIMEOUT_MS
		var ready_at := {}
		var next_poll := 0
		while Time.get_ticks_msec() < deadline:
			# Records when each structure owner (and the readiness gates) first reports ready, polled every 250 ms.
			if Time.get_ticks_msec() >= next_poll:
				next_poll = Time.get_ticks_msec() + 250
				var loading := root.get_node_or_null("RingWorldLoading")
				var bridge: Node = loading.get("_bridge") if loading != null else null
				if is_instance_valid(bridge):
					var diagnostics: Dictionary = bridge.call("GetStartupDiagnostics")
					for owner_name in diagnostics:
						if bool((diagnostics[owner_name] as Dictionary).get("ready", false)) and not ready_at.has(owner_name):
							ready_at[owner_name] = (Time.get_ticks_usec() - boot_usec) / 1000.0
					var readiness: Dictionary = bridge.call("CaptureReadiness")
					var key := "stage:" + str(readiness.get("stage", ""))
					if not ready_at.has(key):
						ready_at[key] = (Time.get_ticks_usec() - boot_usec) / 1000.0
					if bool(bridge.get("CruisePlaced")) and not ready_at.has("cruise_placed"):
						ready_at["cruise_placed"] = (Time.get_ticks_usec() - boot_usec) / 1000.0
			summary["owner_ready_ms_after_menu_add"] = ready_at
			if int(warm.state) == 2 and bool(warm.reveal_complete):
				break
			if int(warm.state) == 0 and Time.get_ticks_msec() - boot_usec / 1000 > 5000:
				break
			await process_frame
		summary["warm_state"] = int(warm.state)
		summary["reveal_complete"] = bool(warm.reveal_complete)
		summary["reveal_ready_usec"] = Time.get_ticks_usec()
		var tail_end := Time.get_ticks_msec() + int(_tail * 1000.0)
		while Time.get_ticks_msec() < tail_end:
			await process_frame
		summary["warm_metrics"] = warm.get_metrics()
		summary["warm_start_usec"] = warm.get("_start_usec")
		var loader := root.get_node_or_null("RingWorldLoading")
		if loader != null:
			summary["loader_report"] = loader.get_diagnostics()
		if is_instance_valid(warm.bridge):
			summary["systems_after"] = warm.bridge.call("GetStartupDiagnostics")
		if is_instance_valid(warm.world):
			# Vegetation self-suspension reads the Explorer's cached altitude, which only the (held) Explorer refreshes.
			var explorer: Node = warm.world.get_node_or_null("Explorer")
			var trees: Node = warm.world.get_node_or_null("BiomeDecorations")
			if explorer != null:
				summary["explorer_cached_altitude_m"] = explorer.get("CurrentAltitudeAboveTerrainMeters")
				summary["explorer_live_altitude_m"] = explorer.call("GetCanonicalAtmosphereHeightMeters")
			if trees != null:
				summary["trees_visible"] = trees.get("visible")
				summary["trees_max_viewer_altitude_m"] = trees.get("MaximumViewerAltitudeAboveTerrainMeters")
	summary["node_added_gaps"] = _node_log
	var marks := {}
	for key in _boot_marks:
		marks[key] = (int(_boot_marks[key]) - boot_usec) / 1000.0
	summary["boot_marks_ms_after_menu_add"] = marks
	_probe.StallProbeAlignmentSpin(60.0)
	_probe.WriteReport(out_dir)
	var file := FileAccess.open(out_dir.path_join("summary.json"), FileAccess.WRITE)
	if file != null:
		file.store_string(JSON.stringify(summary, "  "))
		file.close()
	print("STALL_PROBE_DONE out=", out_dir)
	_probe.End()
	if _hold > 0.0:
		var hold_end := Time.get_ticks_msec() + int(_hold * 1000.0)
		while Time.get_ticks_msec() < hold_end:
			await process_frame
	if warm != null and warm.has_method("teardown"):
		await warm.teardown()
	quit(0)
