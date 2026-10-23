extends Node
## Persistent owner of the warm world: the last-played Standard world that loads in the background behind the start menu boot cover.
##
## States: IDLE -> LOADING (background load in flight) -> WARM (world held: no input, no saves, clocks paused, cruise viewpoint)
## -> TEARING_DOWN -> IDLE. The world and its bridge are siblings of the menu under /root, so they outlive a StartMenu scene change.
## While a world exists WorldSession.warm_world_active is true and select_slot/delete_slot refuse; teardown() must be awaited first.

const WORLD_LOADING := preload("res://scripts/Loading/ring_world_loading.gd")
const MENU_SNAPSHOT := preload("res://scripts/ui/menu_snapshot.gd")
const NODE_NAME := "RingWarmWorld"
## Seconds the revealed world renders behind the opaque boot cover before the quiet-frame test starts (hides cold pipeline cost).
const SETTLE_SECONDS := 2.0
## The crossfade waits for this long without a frame slower than SLOW_FRAME_MS.
const QUIET_SECONDS := 1.0
const SLOW_FRAME_MS := 100.0
const QUIET_TIMEOUT_SECONDS := 45.0
const SHUTDOWN_DRAIN_CAP_MS := 10000
const MAX_LOGGED_FRAMES := 24000

enum State { IDLE, LOADING, WARM, TEARING_DOWN }

signal warm_ready(world: Node)
signal warm_failed(message: String)
## The world renders behind the (still opaque) boot cover and frames are quiet; the menu may capture, settle and crossfade now.
signal reveal_ready
signal torn_down

var state := State.IDLE
var world: Node
var bridge: Node
var slot := 0
var reveal_complete := false

var _previous_disable_3d := false
var _start_usec := 0
var _warm_usec := 0
var _reveal_usec := 0
var _last_frame_usec := 0
var _last_slow_usec := 0
var _phase := "loading"
var _frames: Dictionary = {"loading": PackedFloat32Array(), "reveal": PackedFloat32Array()}
var _slow: Array[Dictionary] = []
var _report: Dictionary = {}
var _generation := 0
var _gpu_threads: Array[Thread] = []

static func ensure(context: Node) -> Node:
	var existing := context.get_tree().root.get_node_or_null(NODE_NAME)
	if existing != null:
		return existing
	var owner: Node = load("res://scripts/Loading/ring_warm_world.gd").new()
	owner.name = NODE_NAME
	context.get_tree().root.add_child(owner)
	return owner

func _ready() -> void:
	process_mode = Node.PROCESS_MODE_ALWAYS

func _exit_tree() -> void:
	for thread in _gpu_threads:
		thread.wait_to_finish()
	_gpu_threads.clear()
	# A menu snapshot write may still be running on the worker pool; join it so the process never exits with a task in flight.
	MENU_SNAPSHOT.wait_pending()

func is_warm() -> bool:
	return state == State.WARM and is_instance_valid(world)

func is_busy() -> bool:
	return state != State.IDLE

## Begins the background load of `slot_id`. `placement_pose` is the pose of a valid menu snapshot (see RingWorldLoadingBridge.SetPlacementPose).
## Returns false when a world exists or the slot cannot be bound.
func start(slot_id: int, placement_pose: Dictionary = {}) -> bool:
	if state != State.IDLE:
		return false
	var session := get_node_or_null("/root/WorldSession")
	if session == null or not session.has_method("select_slot_for_preview") or not session.select_slot_for_preview(slot_id):
		return false
	var loader := WORLD_LOADING.ensure(self)
	if loader.active:
		session.clear_preview_slot()
		return false
	session.warm_world_active = true
	session.request_load_selected_world()
	slot = slot_id
	_generation += 1
	_previous_disable_3d = get_tree().root.disable_3d
	state = State.LOADING
	reveal_complete = false
	_phase = "loading"
	_frames = {"loading": PackedFloat32Array(), "reveal": PackedFloat32Array()}
	_slow.clear()
	_report = {}
	_start_usec = Time.get_ticks_usec()
	_last_frame_usec = _start_usec
	_last_slow_usec = _start_usec
	var metadata: Dictionary = session.get_slot_metadata(slot_id)
	var settings: Dictionary = metadata.get("generation_settings", {})
	if not loader.world_warm.is_connected(_on_world_warm):
		loader.world_warm.connect(_on_world_warm)
		loader.loading_failed.connect(_on_loader_failed)
	loader.open_saved_world_background({
		"world_title": str(metadata.get("name", "World %d" % slot_id)),
		"seed": int(settings.get("terrain_seed", 1337)),
		"radius_m": float(settings.get("radius_km", 5000.0)) * 1000.0,
		"circumference_m": TAU * float(settings.get("radius_km", 5000.0)) * 1000.0,
		"width_m": float(settings.get("ring_width_km", 318.0)) * 1000.0,
		"is_existing": true,
	}, placement_pose)
	return true

func _on_world_warm(warm_world: Node, warm_bridge: Node) -> void:
	if state != State.LOADING:
		return
	world = warm_world
	bridge = warm_bridge
	bridge.reparent(self)
	state = State.WARM
	_warm_usec = Time.get_ticks_usec()
	_report["time_to_warm_ms"] = float(_warm_usec - _start_usec) / 1000.0
	warm_ready.emit(world)

func _on_loader_failed(message: String) -> void:
	if state != State.LOADING:
		return
	push_warning("Warm world failed: " + message)
	await teardown()
	warm_failed.emit(message)

## Presents the world behind the opaque cover: hides the world HUD, releases the render holds, enables root 3D, then waits for
## two presented frames, the settle time and a quiet second. Emits reveal_ready (or returns early if the world went away).
func reveal() -> void:
	if state != State.WARM or not is_instance_valid(bridge):
		return
	var generation := _generation
	bridge.call("HideWorldHud")
	bridge.call("RevealBehindMenu")
	_phase = "reveal"
	_reveal_usec = Time.get_ticks_usec()
	var loader := WORLD_LOADING.ensure(self)
	await loader.present_frame()
	await loader.present_frame()
	if generation != _generation or state != State.WARM:
		return
	await get_tree().create_timer(SETTLE_SECONDS).timeout
	var deadline := Time.get_ticks_usec() + int(QUIET_TIMEOUT_SECONDS * 1000000.0)
	while generation == _generation and state == State.WARM:
		var now := Time.get_ticks_usec()
		if float(now - _last_slow_usec) / 1000000.0 >= QUIET_SECONDS:
			break
		if now > deadline:
			push_warning("Warm world frames did not become quiet within %.0f s; crossfading anyway." % QUIET_TIMEOUT_SECONDS)
			break
		await get_tree().process_frame
	if generation != _generation or state != State.WARM:
		return
	_report["reveal_to_quiet_ms"] = float(Time.get_ticks_usec() - _reveal_usec) / 1000.0
	reveal_complete = true
	reveal_ready.emit()

## Frees the warm world (or cancels a load in flight) and unbinds the slot. Awaitable and idempotent.
func teardown() -> void:
	if state == State.IDLE:
		return
	if state == State.TEARING_DOWN:
		await torn_down
		return
	var previous := state
	state = State.TEARING_DOWN
	_generation += 1
	var started := Time.get_ticks_usec()
	var loader := WORLD_LOADING.ensure(self)
	if previous == State.LOADING:
		await loader.cancel_background()
	else:
		await _teardown_warm_world(loader)
	var session := get_node_or_null("/root/WorldSession")
	if session != null:
		session.warm_world_active = false
		session.clear_preview_slot()
	world = null
	bridge = null
	slot = 0
	reveal_complete = false
	_report["teardown_ms"] = float(Time.get_ticks_usec() - started) / 1000.0
	state = State.IDLE
	torn_down.emit()

func _teardown_warm_world(loader: Node) -> void:
	if is_instance_valid(bridge):
		# CancelWorld makes the world read-only (RejectLoad): its _ExitTree cannot write a save.
		bridge.call("CancelWorld")
		var started := Time.get_ticks_msec()
		while not bool(bridge.call("IsShutdownReady")):
			if Time.get_ticks_msec() - started > SHUTDOWN_DRAIN_CAP_MS:
				push_warning("Warm world shutdown drain exceeded %d ms; freeing anyway." % SHUTDOWN_DRAIN_CAP_MS)
				break
			await get_tree().process_frame
	if is_instance_valid(world):
		world.queue_free()
	await loader.present_frame()
	await loader.present_frame()
	if is_instance_valid(bridge):
		bridge.queue_free()
	get_tree().root.disable_3d = _previous_disable_3d
	# queue_free from a post-draw continuation is flushed at the end of the next processed frame, so wait one presented frame.
	await loader.present_frame()

## Load progress 0..1 for the survey readout: the loader's overall progress while loading, 1 once the world is held.
func get_progress() -> float:
	if state == State.LOADING:
		return WORLD_LOADING.ensure(self).get_progress()
	return 1.0 if state == State.WARM else 0.0

## True once the load has left the preparing and terrain stages (the worst early freezes are behind it).
func is_past_terrain_stage() -> bool:
	if state == State.WARM:
		return true
	return state == State.LOADING and WORLD_LOADING.ensure(self).get_stage() in ["surroundings", "ready"]

## Milliseconds from start() to the world being quiet behind the cover; the boot snapshot's drift is paced to this on the next boot.
func get_last_warm_ms() -> float:
	return float(_report.get("time_to_warm_ms", 0.0)) + float(_report.get("reveal_to_quiet_ms", 0.0))

## Explorer pose of the held world ({valid, around_m, vertical_m, across_m, yaw_deg, pitch_deg, fov_deg}) for the menu snapshot sidecar.
func get_camera_pose() -> Dictionary:
	if state != State.WARM or not is_instance_valid(bridge):
		return {}
	return bridge.call("GetCameraPose")

func placement_used_snapshot() -> bool:
	return is_instance_valid(bridge) and bool(bridge.get("PlacementUsedSnapshot"))

## Altitude, speed and sun elevation of the held world, for the survey readout once the boot cover is retired.
func get_telemetry() -> Dictionary:
	if state != State.WARM or not is_instance_valid(bridge):
		return {}
	return bridge.call("GetExplorerTelemetry")

## Logs this process's dedicated GPU memory when run with --warm-world-metrics (Windows). Runs on a worker thread because the
## query spawns PowerShell.
func log_gpu_memory(label: String) -> void:
	if OS.get_name() != "Windows" or not "--warm-world-metrics" in OS.get_cmdline_user_args():
		return
	var thread := Thread.new()
	thread.start(_query_gpu_memory.bind(label))
	_gpu_threads.append(thread)

func _query_gpu_memory(label: String) -> void:
	var output := []
	var command := "(Get-Counter '\\GPU Process Memory(pid_%d*)\\Dedicated Usage' -ErrorAction SilentlyContinue).CounterSamples | ForEach-Object { [int]($_.CookedValue/1MB) }" % OS.get_process_id()
	var megabytes := -1.0
	if OS.execute("powershell", ["-NoProfile", "-Command", command], output, true) == 0 and output.size() > 0:
		megabytes = float(String(output[0]).strip_edges().get_slice("\n", 0))
	call_deferred("_record_gpu_memory", label, megabytes)

func _record_gpu_memory(label: String, megabytes: float) -> void:
	_report["gpu_process_dedicated_mb_" + label] = megabytes
	print("WARM_WORLD_GPU_MB %s=%.0f" % [label, megabytes])

func get_visible_hud_layers() -> int:
	return int(bridge.call("CountVisibleHudLayers")) if is_instance_valid(bridge) else 0

## Frame statistics while the menu was up: per phase frame count, max, p95, count over SLOW_FRAME_MS and the slowest frames.
func get_metrics() -> Dictionary:
	var result := _report.duplicate(true)
	for phase: String in _frames:
		var values: PackedFloat32Array = _frames[phase]
		var sorted := values.duplicate()
		sorted.sort()
		var over := 0
		for value in values:
			if value > SLOW_FRAME_MS:
				over += 1
		result[phase] = {
			"frames": values.size(),
			"max_ms": sorted[sorted.size() - 1] if not sorted.is_empty() else 0.0,
			"p95_ms": sorted[mini(sorted.size() - 1, int(sorted.size() * 0.95))] if not sorted.is_empty() else 0.0,
			"over_100ms": over,
		}
	result["slowest_frames"] = _slow.duplicate(true)
	result["state"] = State.keys()[state]
	return result

func _process(_delta: float) -> void:
	for thread in _gpu_threads.duplicate():
		if not thread.is_alive():
			thread.wait_to_finish()
			_gpu_threads.erase(thread)
	if state == State.IDLE:
		return
	var now := Time.get_ticks_usec()
	var milliseconds := float(now - _last_frame_usec) / 1000.0
	_last_frame_usec = now
	if state == State.LOADING or state == State.WARM:
		var values: PackedFloat32Array = _frames[_phase]
		if values.size() < MAX_LOGGED_FRAMES:
			values.append(milliseconds)
			_frames[_phase] = values
		if milliseconds > SLOW_FRAME_MS:
			_last_slow_usec = now
			_slow.append({"ms": milliseconds, "phase": _phase, "at_ms": float(now - _start_usec) / 1000.0})
			_slow.sort_custom(func(a: Dictionary, b: Dictionary) -> bool: return float(a.ms) > float(b.ms))
			if _slow.size() > 12:
				_slow.resize(12)
		# World _Ready methods may capture the pointer; the menu owns it.
		Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
