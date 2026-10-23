extends Node
## Owns the loading screen across menu, launcher and gameplay scene lifetimes.

const SCREEN := preload("res://scripts/ui/ring_loading_screen.gd")
const BRIDGE := preload("res://scripts/Loading/RingWorldLoadingBridge.cs")
const RESOURCES := preload("res://scripts/Loading/RingWorldLoadingResources.cs")
const NODE_NAME := "RingWorldLoading"
const START_MENU := "res://scenes/StartMenu.tscn"
const LAUNCHER := "res://scenes/WorldSizeLauncher.tscn"

signal world_ready(world: Node)
signal loading_failed(message: String)
## Background mode only: the world finished loading behind the start menu. The receiver owns the world and the bridge.
signal world_warm(world: Node, bridge: Node)

## Cruise placement for a warm world (metres above ground) and the camera pitch the menu looks along.
const WARM_CRUISE_ALTITUDE_M := 16000.0
const WARM_CRUISE_PITCH_DEGREES := -25.0

var active := false
## True while a background (warm world) load is in flight: no overlay, StartMenu stays current, the world is never released.
var is_background := false
var cancelled := false
var failed := false
var _screen: Control
var _layer: CanvasLayer
var _bridge: Node
var _resources: Node
var _resource_status: Dictionary = {}
var _game: Node
var _launcher: Node
var _generation := 0
var _started_usec := 0
var _last_frame_usec := 0
var _stage_started_usec := 0
var _stage := ""
var _stage_times: Array[Dictionary] = []
var _frame_times: Array[float] = []
var _slow_frames: Array[Dictionary] = []
var _last_pipeline_counts: Dictionary = {}
var _operations: Dictionary = {}
var _initializers: Dictionary = {}
var _attach_last_usec := 0
var _info: Dictionary = {}
var _overall_progress := 0.0
var _last_report: Dictionary = {}
var _previous_disable_3d := false
var _pending_scene_requests: Dictionary = {}
## Menu snapshot pose handed to the bridge of the next background load (see RingWorldLoadingBridge.SetPlacementPose).
var _placement_pose: Dictionary = {}
## Cruise placement {altitude_m, pitch_deg} of the next background load (the menu Live world drawer); empty uses the WARM_CRUISE_* defaults.
var _cruise: Dictionary = {}

static func ensure(context: Node) -> Node:
	var existing := context.get_tree().root.get_node_or_null(NODE_NAME)
	if existing != null:
		return existing
	var owner: Node = load("res://scripts/Loading/ring_world_loading.gd").new()
	owner.name = NODE_NAME
	context.get_tree().root.add_child(owner)
	return owner

func _ready() -> void:
	process_mode = Node.PROCESS_MODE_ALWAYS

func _process(_delta: float) -> void:
	if not active:
		return
	# A failed screen may remain open indefinitely. Retire its requests without
	# accumulating frame samples while waiting for the player to return.
	if failed:
		_drain_scene_requests()
		Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
		return
	var now := Time.get_ticks_usec()
	# The pipeline monitors lock the scene-shader mutex, which background shader compiles hold; do not poll them while warming.
	var pipelines := _last_pipeline_counts if str(_resource_status.get("phase", "")) == "background warmup" else _pipeline_counts()
	if _last_frame_usec > 0:
		var milliseconds := float(now - _last_frame_usec) / 1000.0
		_frame_times.append(milliseconds)
		if milliseconds >= 100:
			var compilations: Dictionary = {}
			for key: String in pipelines:
				compilations[key] = int(pipelines[key]) - int(_last_pipeline_counts.get(key, pipelines[key]))
			_slow_frames.append({"milliseconds": milliseconds, "stage": _stage, "elapsed_ms": float(now - _started_usec) / 1000.0,
				"process_ms": Performance.get_monitor(Performance.TIME_PROCESS) * 1000.0,
				"physics_ms": Performance.get_monitor(Performance.TIME_PHYSICS_PROCESS) * 1000.0,
				"pipeline_compilations": compilations})
			_slow_frames.sort_custom(func(a: Dictionary, b: Dictionary) -> bool: return float(a.milliseconds) > float(b.milliseconds))
			if _slow_frames.size() > 10:
				_slow_frames.resize(10)
	_last_frame_usec = now
	_last_pipeline_counts = pipelines
	# Gameplay _Ready methods may capture the pointer. Loading retains ownership.
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE

func _pipeline_counts() -> Dictionary:
	return {"canvas": int(Performance.get_monitor(Performance.PIPELINE_COMPILATIONS_CANVAS)),
		"mesh": int(Performance.get_monitor(Performance.PIPELINE_COMPILATIONS_MESH)),
		"surface": int(Performance.get_monitor(Performance.PIPELINE_COMPILATIONS_SURFACE)),
		"draw": int(Performance.get_monitor(Performance.PIPELINE_COMPILATIONS_DRAW)),
		"specialization": int(Performance.get_monitor(Performance.PIPELINE_COMPILATIONS_SPECIALIZATION))}

func begin(info: Dictionary) -> bool:
	if active:
		return not cancelled and not failed
	_generation += 1
	active = true
	_previous_disable_3d = get_viewport().disable_3d
	get_viewport().disable_3d = true
	cancelled = false
	failed = false
	_info = info.duplicate(true)
	_started_usec = Time.get_ticks_usec()
	_last_frame_usec = _started_usec
	_stage_started_usec = _started_usec
	_stage = ""
	_stage_times.clear()
	_frame_times.clear()
	_slow_frames.clear()
	_last_pipeline_counts = _pipeline_counts()
	_operations.clear()
	_initializers.clear()
	_resource_status.clear()
	_overall_progress = 0
	if not is_background:
		_layer = CanvasLayer.new()
		_layer.name = "WorldLoadingOverlay"
		_layer.layer = 100
		add_child(_layer)
		_screen = SCREEN.new()
		_layer.add_child(_screen)
		_screen.configure(_info)
		_screen.cancel_requested.connect(_cancel_and_return)
	set_stage("preparing", "Opening your world.", 0)
	return true

## Current loading stage key ("preparing", "terrain", "surroundings", "ready"); empty when idle.
func get_stage() -> String:
	return _stage if active else ""

## Overall load progress 0..1 while a load is in flight (the start menu's survey readout), else 0.
func get_progress() -> float:
	return _overall_progress if active else 0.0

func present_frame() -> void:
	await get_tree().process_frame
	if DisplayServer.get_name() != "headless":
		await RenderingServer.frame_post_draw

func set_stage(key: String, detail: String, fraction: float) -> void:
	if not active or failed or cancelled:
		return
	if key != _stage:
		if not _stage.is_empty():
			_stage_times.append({"stage": _stage, "milliseconds": float(Time.get_ticks_usec() - _stage_started_usec) / 1000.0})
		_stage = key
		_stage_started_usec = Time.get_ticks_usec()
	_overall_progress = maxf(_overall_progress, clampf(fraction, 0, 1))
	if _screen != null:
		_screen.set_progress(key, detail, _overall_progress)

func record_operation(key: String, started_usec: int) -> void:
	_operations[key] = float(Time.get_ticks_usec() - started_usec) / 1000.0

func begin_attachment_metrics(world: Node) -> void:
	_attach_last_usec = Time.get_ticks_usec()
	for child: Node in world.get_children():
		child.ready.connect(_record_initializer.bind(str(child.name)), CONNECT_ONE_SHOT)

func _record_initializer(owner_name: String) -> void:
	var now := Time.get_ticks_usec()
	_initializers[owner_name] = float(now - _attach_last_usec) / 1000.0
	_attach_last_usec = now

func load_scene(path: String, start_progress := 0.02, end_progress := 0.25) -> PackedScene:
	var generation := _generation
	var started := Time.get_ticks_usec()
	# Avoid resurrecting collected C# resources from Godot's cache during scene
	# replacement (godotengine/godot#83762). Engine loading remains threaded.
	var error := ResourceLoader.load_threaded_request(path, "PackedScene", false, ResourceLoader.CACHE_MODE_IGNORE_DEEP)
	if error != OK:
		fail("Could not open the world scene. Error %d." % error)
		return null
	_pending_scene_requests[path] = true
	while active and not cancelled and not failed and generation == _generation:
		var progress := []
		var status := ResourceLoader.load_threaded_get_status(path, progress)
		if status == ResourceLoader.THREAD_LOAD_LOADED:
			record_operation("resources:" + path, started)
			var packed := ResourceLoader.load_threaded_get(path) as PackedScene
			_pending_scene_requests.erase(path)
			return packed
		if status == ResourceLoader.THREAD_LOAD_FAILED or status == ResourceLoader.THREAD_LOAD_INVALID_RESOURCE:
			if status == ResourceLoader.THREAD_LOAD_FAILED:
				ResourceLoader.load_threaded_get(path)
			_pending_scene_requests.erase(path)
			fail("A world resource could not be loaded. Your saved world has been preserved.")
			return null
		var fraction: float = float(progress[0]) if not progress.is_empty() else 0.0
		set_stage("preparing", "Loading world resources.", lerpf(start_progress, end_progress, fraction))
		await get_tree().process_frame
	return null

func _drain_scene_requests() -> bool:
	# Godot has no cancellation API for these requests. Every successful request
	# owns a token until get() consumes it, even when another request shares it.
	for path: String in _pending_scene_requests.keys():
		var status := ResourceLoader.load_threaded_get_status(path)
		if status == ResourceLoader.THREAD_LOAD_IN_PROGRESS:
			continue
		if status == ResourceLoader.THREAD_LOAD_LOADED or status == ResourceLoader.THREAD_LOAD_FAILED:
			ResourceLoader.load_threaded_get(path)
		_pending_scene_requests.erase(path)
	return _pending_scene_requests.is_empty()

func open_saved_world(info: Dictionary) -> void:
	if active or not begin(info):
		return
	var old_scene := get_tree().current_scene
	if old_scene != null:
		old_scene.process_mode = Node.PROCESS_MODE_DISABLED
	await present_frame()
	if cancelled or failed:
		return
	var packed := await load_scene(LAUNCHER, 0.01, 0.08)
	if packed == null or cancelled or failed:
		return
	var started := Time.get_ticks_usec()
	_launcher = packed.instantiate()
	_launcher.set_meta("world_loading_transition", true)
	get_tree().root.add_child(_launcher)
	get_tree().current_scene = _launcher
	if is_instance_valid(old_scene):
		old_scene.queue_free()
	record_operation("launcher", started)

## Loads the saved world behind the current scene without any loading screen. The launcher runs hidden and non-current. On success
## `world_warm(world, bridge)` fires and the caller owns both (see ring_warm_world.gd); on failure `loading_failed`.
func open_saved_world_background(info: Dictionary, placement_pose: Dictionary = {}, cruise: Dictionary = {}) -> void:
	if active:
		return
	_placement_pose = placement_pose.duplicate(true)
	_cruise = cruise.duplicate(true)
	is_background = true
	if not begin(info):
		is_background = false
		return
	await get_tree().process_frame
	if cancelled or failed:
		return
	var packed := await load_scene(LAUNCHER, 0.01, 0.08)
	if packed == null or cancelled or failed:
		return
	var started := Time.get_ticks_usec()
	_launcher = packed.instantiate()
	_launcher.set_meta("world_loading_transition", true)
	_launcher.set_meta("world_loading_background", true)
	get_tree().root.add_child(_launcher)
	record_operation("launcher", started)

func prepare_world(world: Node, launcher: Node) -> void:
	_game = world
	_launcher = launcher
	_bridge = BRIDGE.new()
	add_child(_bridge)
	if is_background:
		_bridge.call("EnableBackgroundMode", float(_cruise.get("altitude_m", WARM_CRUISE_ALTITUDE_M)), float(_cruise.get("pitch_deg", WARM_CRUISE_PITCH_DEGREES)))
		_bridge.call("SetPlacementPose", _placement_pose)
		_placement_pose = {}
		_cruise = {}
	_bridge.call("PrepareWorld", world)

func prepare_resources() -> bool:
	var generation := _generation
	var started := Time.get_ticks_usec()
	_resources = RESOURCES.new()
	add_child(_resources)
	while active and not cancelled and not failed and generation == _generation:
		_resource_status = _resources.call("GetLoadingStatus")
		if not str(_resource_status.get("error", "")).is_empty():
			fail(str(_resource_status.error))
			return false
		if bool(_resource_status.get("completed", false)):
			record_operation("resource_warmup", started)
			return bool(_resource_status.get("ready", false))
		var fraction := float(_resource_status.get("completed_resources", 0)) / maxf(1, float(_resource_status.get("total_resources", 1)))
		set_stage("preparing", "Preparing terrain colors and materials.", .26 + fraction * .04)
		await get_tree().process_frame
	return false

func world_attached() -> void:
	# The resources node keeps the shader/prewarm references alive until the world is ready.
	_bridge.call("ConfirmWorldAttached")

func _free_resources() -> void:
	if is_instance_valid(_resources):
		_resources.queue_free()
		_resources = null

func wait_for_world() -> bool:
	if is_background:
		return await _wait_for_world_background()
	var generation := _generation
	while active and not cancelled and not failed and generation == _generation:
		var readiness: Dictionary = _bridge.call("CaptureReadiness")
		var spawn: Dictionary = readiness.get("spawn", {})
		if spawn.has("around_m") and spawn.has("across_m"):
			_screen.set_arrival_position(float(spawn.around_m), float(spawn.across_m))
		var error := str(readiness.get("error", ""))
		if not error.is_empty():
			fail(error)
			return false
		set_stage(str(readiness.get("stage", "terrain")), str(readiness.get("detail", "Preparing your world.")), float(readiness.get("progress", .35)))
		if bool(readiness.get("ready", false)):
			_free_resources()
			# Let an actual world frame render behind the overlay before revealing it.
			var reveal_started := Time.get_ticks_usec()
			_bridge.call("PrepareReveal")
			get_viewport().disable_3d = _previous_disable_3d
			await present_frame()
			record_operation("first_world_frame", reveal_started)
			if cancelled or failed or generation != _generation:
				return false
			get_tree().current_scene = _game
			if is_instance_valid(_launcher):
				_launcher.call("_save_selected_slot_settings")
				var play_session := get_node_or_null("/root/WorldSession")
				if play_session != null and play_session.has_method("begin_play_session"):
					play_session.call("begin_play_session", _game)
				_launcher.queue_free()
			_launcher = null
			_screen.finish()
			var fade := create_tween()
			fade.tween_property(_screen, "modulate:a", 0.0, .3)
			await get_tree().create_timer(.3).timeout
			if cancelled or failed or generation != _generation:
				return false
			_bridge.call("ReleaseWorld")
			_finish_report("ready")
			active = false
			Input.mouse_mode = Input.MOUSE_MODE_CAPTURED
			_layer.queue_free()
			_bridge.queue_free()
			world_ready.emit(_game)
			_game = null
			return true
		await get_tree().process_frame
	return false

func _wait_for_world_background() -> bool:
	var generation := _generation
	while active and not cancelled and not failed and generation == _generation:
		var readiness: Dictionary = _bridge.call("CaptureReadiness")
		var error := str(readiness.get("error", ""))
		if not error.is_empty():
			fail(error)
			return false
		set_stage(str(readiness.get("stage", "terrain")), str(readiness.get("detail", "Preparing your world.")), float(readiness.get("progress", .35)))
		if bool(readiness.get("ready", false)):
			_free_resources()
			if is_instance_valid(_launcher):
				_launcher.call("_save_selected_slot_settings")
				_launcher.queue_free()
			_launcher = null
			_finish_report("warm")
			var world := _game
			var bridge := _bridge
			_game = null
			_bridge = null
			active = false
			is_background = false
			# The world stays held (HoldSavesUntilWorldReady, no input); the receiver keeps the bridge as its holder.
			world_warm.emit(world, bridge)
			return true
		await get_tree().process_frame
	return false

func fail(message: String) -> void:
	if failed or cancelled:
		return
	failed = true
	if is_instance_valid(_resources):
		_resources.call("CancelPreparation")
	if is_instance_valid(_bridge):
		_bridge.call("CancelWorld")
	if is_instance_valid(_screen):
		_screen.show_failure(message)
	_finish_report("failed")
	if is_background:
		push_warning("Warm world load failed: " + message)
	loading_failed.emit(message)

## Cancels a background load (or clears a failed one) without changing scene. Awaitable.
func cancel_background() -> void:
	if not is_background:
		return
	await _cancel_and_return()

func _cancel_and_return() -> void:
	if cancelled:
		return
	cancelled = true
	_generation += 1
	if is_instance_valid(_resources):
		_resources.call("CancelPreparation")
	if is_instance_valid(_bridge):
		_bridge.call("CancelWorld")
	while true:
		var requests_drained := _drain_scene_requests()
		var resources_drained := not is_instance_valid(_resources) or bool(_resources.call("IsShutdownReady"))
		var world_drained := not is_instance_valid(_bridge) or bool(_bridge.call("IsShutdownReady"))
		if requests_drained and resources_drained and world_drained:
			break
		await get_tree().process_frame
	if is_instance_valid(_resources):
		_resources.queue_free()
		_resources = null
	if is_instance_valid(_game):
		_game.queue_free()
		_game = null
	if is_instance_valid(_launcher):
		_launcher.queue_free()
		_launcher = null
	_finish_report("cancelled")
	# Allow scene-exit render cleanup before introducing a replacement scene.
	await present_frame()
	await present_frame()
	if is_instance_valid(_bridge):
		_bridge.queue_free()
	active = false
	get_viewport().disable_3d = _previous_disable_3d
	if is_instance_valid(_layer):
		_layer.queue_free()
	var session := get_node_or_null("/root/WorldSession")
	if session != null:
		session.consume_load_selected_world_request()
		if session.has_method("consume_new_world_request"):
			session.call("consume_new_world_request")
	if is_background:
		# The menu stays where it is; its owner clears the slot binding.
		is_background = false
		Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
		return
	get_tree().change_scene_to_file(START_MENU)
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE

func get_diagnostics() -> Dictionary:
	return _last_report.duplicate(true)

func _finish_report(outcome: String) -> void:
	var sorted := _frame_times.duplicate()
	sorted.sort()
	var p95: float = float(sorted[mini(sorted.size() - 1, int(sorted.size() * .95))]) if not sorted.is_empty() else 0.0
	var maximum: float = float(sorted[-1]) if not sorted.is_empty() else 0.0
	var stages := _stage_times.duplicate(true)
	stages.append({"stage": _stage, "milliseconds": float(Time.get_ticks_usec() - _stage_started_usec) / 1000.0})
	_last_report = {"outcome": outcome, "elapsed_ms": float(Time.get_ticks_usec() - _started_usec) / 1000.0,
		"frames": _frame_times.size(), "frame_p95_ms": p95, "frame_max_ms": maximum,
		"slowest_frames": _slow_frames.duplicate(true),
		"operations_ms": _operations.duplicate(), "initializers_ms": _initializers.duplicate(), "stages": stages,
		"resource_warmup": _resource_status.duplicate(),
		"note": "Loading frame wall times include scheduling and rendering; these are not gameplay FPS measurements."}
	if is_instance_valid(_bridge):
		_last_report["systems"] = _bridge.call("GetStartupDiagnostics")
	print("WORLD_LOADING_REPORT=" + JSON.stringify(_last_report))
