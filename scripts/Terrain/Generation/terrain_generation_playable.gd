class_name TerrainGenerationPlayable
extends Node3D

const PREVIEW_HANDOFF := preload("res://scripts/Terrain/Generation/terrain_generation_preview_handoff.gd")
const STREAM := preload("res://scripts/Terrain/Generation/terrain_generation_stream.gd")
const DEBOUNCE_SECONDS := 0.35
const PITCH := 256
const SAFE_EDGE := 176.0
const RECENTER := 128.0
const CLEARANCE := 1.2
const MIN_TRAVEL_ACROSS := 80.0
const MAX_TRAVEL_ACROSS := TerrainGenerationRecipe.CERTIFIED_WIDTH_M - 336.0

@export var surface_path := NodePath("RingTerrain3DSurface")
@export var player_path := NodePath("Player")
@export var camera_path := NodePath("Player/Head/Camera3D")
@export var head_path := NodePath("Player/Head")
@export var status_path := NodePath("CanvasLayer/Status")

var _recipe: TerrainGenerationRecipe
var _surface: Node
var _player: CharacterBody3D
var _camera: Camera3D
var _head: Node3D
var _status: Label
var _stream: RefCounted
var _anchor_around := 0
var _anchor_across := 159000
var _across_direction := 1
var _revision := 0
var _committed_revision := 0
var _recipe_epoch := 0
var _edit_epoch := 0
var _frame_epoch := 0
var _pending := 0.0
var _request_inflight := false
var _request_sent := false
var _requested: Dictionary = {}
var _staged: Dictionary = {}
var _preparing: Dictionary = {}
var _preview_ready := false
var _generation_failed := false
var _teleport_pending := false
# Canonical positions remain doubles; Vector2 would lose precision around the seam.
var _teleport_around := 0.0
var _teleport_across := 0.0
var _canonical_around := 0.0
var _canonical_across := 159000.0
var _last_measured_around := 0.0
var _last_measured_across := 0.0
var _have_motion_sample := false
var _measured_velocity := Vector2.ZERO
var _flying := false
var _move_speed := 12.0
var _mesh_lods := 4
var _worker_count := 2
var _prefetch_m := 64.0
var _cache_mib := 8
var _upload_budget_ms := 1.0
var _collision_budget_ms := 0.75
var _collider_tile_cells := 32
var _edits: Dictionary = {}
var _test_path := ""
var _save_blocked := false
var _error := ""
var _sampling_ms := 0.0
var _packing_ms := 0.0
var _projection_ms := 0.0
var _latencies: Array[float] = []
var _edge_waits := 0
var _waiting_at_edge := false
var _last_lookahead := 64.0
var _last_readiness_ms := 1500.0
var _last_cache_hits := 0
var _stale_results := 0
# Last published canonical coordinate prevents float round-trip jitter at overlap edges
# from immediately requesting the window we just retired.
var _last_handoff_around := 0.0
var _last_handoff_across := 0.0
var _smooth_terrain_shading := true

func configure_test_fixture(recipe_path: String, around: int = 0, across: int = 159000) -> void:
	assert(recipe_path.begins_with("res://.godot/terrain-generation-tests/"))
	_test_path = recipe_path
	_anchor_around = _lattice(around)
	_anchor_across = _safe_across_anchor(across)

func _enter_tree() -> void:
	var handoff: Dictionary = PREVIEW_HANDOFF.consume()
	if not handoff.is_empty():
		_test_path = handoff.recipe_path
	_initialize_recipe()
	if not handoff.is_empty():
		_recipe = handoff.recipe
		_anchor_around = _lattice(int(handoff.around))
		_anchor_across = _safe_across_anchor(int(handoff.across))
		_save_blocked = _save_blocked or handoff.save_blocked
		if _error.is_empty():
			_error = handoff.error
	# Child controls are built before our _ready; expose the selected coordinate now.
	_canonical_around = fposmod(_anchor_around, TerrainGenerationRecipe.CERTIFIED_CIRCUMFERENCE_M)
	_canonical_across = _anchor_across
	if _test_path.begins_with("res://.godot/terrain-generation-tests/"):
		var drawer := get_node_or_null("RingLiveTuningDrawer")
		if drawer != null:
			drawer.set("SurfaceTerrainDefaultsPathOverride", _test_path.get_base_dir().path_join("playable_preferences.tres"))

func _ready() -> void:
	_surface = get_node_or_null(surface_path)
	if _surface != null:
		_surface.call("SetSmoothTerrainShading", _smooth_terrain_shading)
	_player = get_node_or_null(player_path) as CharacterBody3D
	_camera = get_node_or_null(camera_path) as Camera3D
	_head = get_node_or_null(head_path) as Node3D
	_status = get_node_or_null(status_path) as Label
	_stream = STREAM.new()
	_stream.configure(_worker_count, _cache_mib, _collider_tile_cells)
	_request_window(_anchor_around, _anchor_across, false, true, "initial")

func _exit_tree() -> void:
	if _stream != null:
		_stream.shutdown()

func _initialize_recipe() -> void:
	if _recipe != null:
		return
	var loaded := TerrainGenerationPreviewStore.load_recipe(_recipe_path())
	_recipe = loaded.recipe if loaded.recipe != null else TerrainGenerationRecipe.create_erosion_default()
	_error = loaded.error
	_save_blocked = not _error.is_empty()

func _recipe_path() -> String:
	return _test_path if not _test_path.is_empty() else TerrainGenerationPreviewStore.RECIPE_PATH

func get_recipe_snapshot() -> Resource:
	_initialize_recipe()
	return _recipe.snapshot()

func apply_recipe_setting(name: String, value: Variant) -> void:
	_initialize_recipe()
	_recipe.set(name, value)
	_recipe_epoch += 1
	_queue_definition_change()

# Immutable absolute overrides exercise the edit epoch contract. Gameplay brush
# persistence will adopt the shared terrain definition in milestone 5.
func apply_preview_edits(edits: Dictionary) -> bool:
	var copy: Dictionary = {}
	for key in edits:
		if not key is Vector2i or typeof(edits[key]) not in [TYPE_INT, TYPE_FLOAT]:
			return false
		var height: float = float(edits[key])
		if key.x % 2 != 0 or key.y % 2 != 0 or key.y < 0 or key.y >= int(TerrainGenerationRecipe.CERTIFIED_WIDTH_M):
			return false
		if not is_finite(height) or height < -8000.0 or height > 8000.0:
			return false
		var canonical := Vector2i(posmod(key.x, int(TerrainGenerationRecipe.CERTIFIED_CIRCUMFERENCE_M)), key.y)
		if copy.has(canonical) and copy[canonical] != height:
			return false
		copy[canonical] = height
	_edits = copy
	_edit_epoch += 1
	_queue_definition_change()
	return true

func regenerate() -> void:
	_recipe_epoch += 1
	_queue_definition_change()

func _queue_definition_change() -> void:
	if _teleport_pending:
		_request_window(_lattice(int(round(_teleport_around))), _safe_across_anchor(int(round(_teleport_across))), true, true, "teleport")
	else:
		_request_window(_anchor_around, _anchor_across, false, true, "recipe")

func _request_window(around: int, across: int, teleport: bool, debounce: bool, kind: String = "recipe") -> void:
	if _stream == null:
		return
	var target := Vector2i(posmod(_lattice(around), int(TerrainGenerationRecipe.CERTIFIED_CIRCUMFERENCE_M)), _safe_across_anchor(across))
	if kind == "movement" and not _requested.is_empty() and _requested.anchor == target and _requested.kind == kind:
		return
	_cancel_request()
	_revision += 1
	_frame_epoch += 1
	_generation_failed = false
	_error = "Saved recipe is protected. Use Reset recipe before saving." if _save_blocked else ""
	_teleport_pending = teleport
	_requested = {
		"anchor": target, "revision": _revision, "recipe_epoch": _recipe_epoch,
		"edit_epoch": _edit_epoch, "frame_epoch": _frame_epoch,
		"teleport": teleport, "kind": kind, "started": Time.get_ticks_usec(),
	}
	_pending = DEBOUNCE_SECONDS if debounce else 0.0

func _cancel_request() -> void:
	if _stream != null:
		_stream.cancel()
	if _surface != null:
		_surface.call("DiscardStagedWindow")
	_requested.clear()
	_staged.clear()
	_preparing.clear()
	_request_inflight = false
	_request_sent = false
	_teleport_pending = false
	_pending = 0.0

func _fail_request(message: String) -> void:
	_cancel_request()
	_generation_failed = true
	_error = message

func _submit() -> void:
	if _requested.is_empty() or _request_sent:
		return
	var errors := _recipe.validation_errors()
	if not errors.is_empty():
		_fail_request("; ".join(errors))
		return
	var anchor: Vector2i = _requested.anchor
	_request_sent = true
	_request_inflight = _stream.request_window(
		_recipe, anchor.x, anchor.y, _across_direction, _requested.revision,
		_requested.recipe_epoch, _requested.edit_epoch, _requested.frame_epoch, _edits)
	if not _request_inflight:
		_fail_request("Terrain streaming request was rejected.")

func _process(delta: float) -> void:
	if not _requested.is_empty() and not _request_sent:
		_pending = maxf(0.0, _pending - delta)
		if _pending == 0.0:
			_submit()
	# Poll even while debouncing or idle: cancelled tasks still need to be joined.
	if _stream != null:
		var prepared: Dictionary = _stream.poll()
		if not prepared.is_empty():
			_receive(prepared)
	if not _preparing.is_empty():
		_advance_budgeted_preparation()
	if _preview_ready and _surface != null and _camera != null:
		_surface.call("UpdateCamera", _camera.global_position)
		_maybe_stream()
	_update_status()

func _matches_request(payload: Dictionary) -> bool:
	return not _requested.is_empty() and int(payload.get("revision", -1)) == _revision \
		and int(payload.get("recipe_epoch", -1)) == _recipe_epoch \
		and int(payload.get("edit_epoch", -1)) == _edit_epoch \
		and int(payload.get("frame_epoch", -1)) == _frame_epoch

func _receive(prepared: Dictionary) -> void:
	if not _matches_request(prepared):
		_stale_results += 1
		return
	_request_inflight = false
	if not bool(prepared.get("ok", false)):
		_fail_request(str(prepared.get("error", "Terrain streaming failed.")))
		return
	_sampling_ms = float(prepared.sampling_ms)
	_packing_ms = float(prepared.packing_ms)
	_projection_ms = float(prepared.get("projection_ms", 0.0))
	_last_cache_hits = int(prepared.get("cache_hits", 0))
	if not _surface.call("BeginBudgetedPreparedWindow", prepared.absolute_heights, prepared.height_images,
		prepared.geometry, prepared.anchor_around, prepared.anchor_across, prepared.frame_height,
		prepared.across_direction, prepared.revision):
		var diagnostics: Dictionary = _surface.call("GetPreparedDiagnostics")
		_fail_request(str(diagnostics.get("error", "Terrain preparation failed.")))
		return
	# Native owns copied payloads; retain only identity while preparation advances.
	_preparing = {
		"revision": prepared.revision, "recipe_epoch": prepared.recipe_epoch,
		"edit_epoch": prepared.edit_epoch, "frame_epoch": prepared.frame_epoch,
		"anchor_around": prepared.anchor_around, "anchor_across": prepared.anchor_across,
	}

func _advance_budgeted_preparation() -> void:
	if not _matches_request(_preparing):
		_surface.call("DiscardStagedWindow")
		_preparing.clear()
		return
	var state: Dictionary = _surface.call("AdvanceBudgetedPreparation", _upload_budget_ms, _collision_budget_ms)
	if not bool(state.get("ok", false)):
		_fail_request(str(state.get("error", "Terrain preparation failed.")))
		return
	if not bool(state.get("ready", false)):
		return
	if int(state.get("revision", -1)) != int(_preparing.revision):
		_fail_request("Terrain preparation returned a different revision.")
		return
	_staged = _preparing.duplicate()
	_staged["readiness_ms"] = float(Time.get_ticks_usec() - int(_requested.started)) / 1000.0
	_staged["ready_at"] = Time.get_ticks_usec()
	_staged["covered_at_ready"] = _surface.call("CanActivateStagedWindow",
		_teleport_around if _teleport_pending else _canonical_around,
		_teleport_across if _teleport_pending else _canonical_across, 16.0)
	_preparing.clear()

func _physics_process(delta: float) -> void:
	_activate()
	if not _preview_ready or _teleport_pending or _player == null or _camera == null:
		return
	var control := not _drawer_owns_mouse() and Input.mouse_mode == Input.MOUSE_MODE_CAPTURED
	var motion_input := Vector2.ZERO
	if control:
		motion_input = Vector2(float(int(Input.is_key_pressed(KEY_D)) - int(Input.is_key_pressed(KEY_A))),
			float(int(Input.is_key_pressed(KEY_W)) - int(Input.is_key_pressed(KEY_S))))
	var forward := -_camera.global_transform.basis.z
	forward.y = 0.0
	forward = forward.normalized()
	var right := _camera.global_transform.basis.x
	right.y = 0.0
	right = right.normalized()
	var wanted := right * motion_input.x + forward * motion_input.y
	if wanted.length() > 1.0:
		wanted = wanted.normalized()
	var speed := _move_speed * (1.8 if control and Input.is_key_pressed(KEY_SHIFT) else 1.0)
	if _flying:
		if control and Input.is_key_pressed(KEY_Q):
			wanted.y -= 1.0
		if control and Input.is_key_pressed(KEY_E):
			wanted.y += 1.0
		_player.velocity = wanted * speed
	else:
		_player.velocity.x = wanted.x * speed
		_player.velocity.z = wanted.z * speed
		if _player.is_on_floor() and control and Input.is_key_pressed(KEY_SPACE):
			_player.velocity.y = 8.5
		else:
			_player.velocity.y = maxf(-30.0, _player.velocity.y - 24.0 * delta)
	_player.move_and_slide()
	_clamp_player()
	_measure_canonical_motion(delta)

func _activate() -> void:
	if _staged.is_empty() or _surface == null or _player == null:
		return
	if not _matches_request(_staged):
		_surface.call("DiscardStagedWindow")
		_staged.clear()
		return
	var had_active := _preview_ready
	var old_height := 0.0
	if had_active:
		var previous: PackedFloat64Array = _surface.call("GetCanonicalPosition", _player.global_position)
		_canonical_around = previous[0]
		_canonical_across = previous[1]
		old_height = previous[2]
	var target_around := _teleport_around if _teleport_pending else _canonical_around
	var target_across := _teleport_across if _teleport_pending else _canonical_across
	if not _surface.call("CanActivateStagedWindow", target_around, target_across, 16.0):
		return
	var new_height: float = _surface.call("SampleStagedHeight", target_around, target_across)
	if not is_finite(new_height):
		_fail_request("Prepared terrain has no usable height at the destination.")
		return
	if not _surface.call("ActivateStagedWindow", _staged.revision):
		_fail_request("Prepared terrain could not be activated.")
		return
	_anchor_around = _staged.anchor_around
	_anchor_across = _staged.anchor_across
	_committed_revision = _staged.revision
	_preview_ready = true
	_canonical_around = target_around
	_canonical_across = target_across
	_last_handoff_around = target_around
	_last_handoff_across = target_across
	var floor_height := new_height + CLEARANCE
	var target_height := floor_height if not had_active or _teleport_pending else maxf(old_height, floor_height)
	_player.global_position = _surface.call("MapCanonicalPosition", target_around, target_across, target_height)
	if not had_active or _teleport_pending:
		_player.velocity = Vector3.ZERO
	elif target_height > old_height and _player.velocity.y < 0.0:
		_player.velocity.y = 0.0
	_surface.call("SetMeshLods", _mesh_lods)
	_last_readiness_ms = float(_staged.readiness_ms)
	# Deliberate waiting for movement into the overlap is not preparation latency.
	if bool(_staged.covered_at_ready):
		_last_readiness_ms += float(Time.get_ticks_usec() - int(_staged.ready_at)) / 1000.0
	_latencies.append(_last_readiness_ms)
	if _latencies.size() > 32:
		_latencies.pop_front()
	_have_motion_sample = false
	_measured_velocity = Vector2.ZERO
	_waiting_at_edge = false
	_teleport_pending = false
	_staged.clear()
	_requested.clear()
	_request_sent = false

func _maybe_stream() -> void:
	if _teleport_pending or _generation_failed or _surface == null or _player == null:
		return
	# User changes take priority; movement may replace only other movement requests.
	if not _requested.is_empty() and str(_requested.get("kind", "")) != "movement":
		return
	var around_offset := _around_delta(_canonical_around, _anchor_around)
	var across_offset := (_canonical_across - _anchor_across) * _across_direction
	var local_velocity := Vector2(_measured_velocity.x, _measured_velocity.y * _across_direction)
	var readiness_seconds := 1.5
	for latency in _latencies:
		readiness_seconds = maxf(readiness_seconds, latency / 1000.0)
	_last_lookahead = maxf(_prefetch_m, local_velocity.length() * readiness_seconds + 16.0)
	var projected := Vector2(around_offset, across_offset)
	if local_velocity.length() > 0.001:
		projected += local_velocity.normalized() * _last_lookahead
	if not _requested.is_empty():
		var target: Vector2i = _requested.anchor
		var pending_delta := Vector2(_around_delta(target.x, _anchor_around), (target.y - _anchor_across) * _across_direction)
		if local_velocity.dot(pending_delta.normalized()) >= -1.0:
			return
		_cancel_request()
	# Do not rebase solely from a stationary float round-trip at an overlap edge.
	# A player must actually leave the last handoff position or be moving on that axis.
	var around_travel := absf(_around_delta(_canonical_around, _last_handoff_around)) > 0.01 or absf(local_velocity.x) > 0.001
	var across_travel := absf(_canonical_across - _last_handoff_across) > 0.01 or absf(local_velocity.y) > 0.001
	var around_needed := around_travel and (absf(projected.x) >= SAFE_EDGE or (absf(around_offset) >= RECENTER - 0.001 and around_offset * local_velocity.x >= 0.0))
	var across_needed := across_travel and (absf(projected.y) >= SAFE_EDGE or (absf(across_offset) >= RECENTER - 0.001 and across_offset * local_velocity.y >= 0.0))
	var step_around := 0
	var step_across := 0
	if around_needed and (not across_needed or absf(projected.x) >= absf(projected.y)):
		step_around = int(signf(projected.x)) * PITCH
	elif across_needed:
		step_across = int(signf(projected.y)) * PITCH * _across_direction
	var target_across := _safe_across_anchor(_anchor_across + step_across)
	if step_around != 0 or target_across != _anchor_across:
		_request_window(_anchor_around + step_around, target_across, false, false, "movement")

func _measure_canonical_motion(delta: float) -> void:
	var canonical: PackedFloat64Array = _surface.call("GetCanonicalPosition", _player.global_position)
	if _have_motion_sample:
		_measured_velocity = Vector2(_around_delta(canonical[0], _last_measured_around),
			canonical[1] - _last_measured_across) / maxf(delta, 0.001)
	_canonical_around = canonical[0]
	_canonical_across = canonical[1]
	_last_measured_around = canonical[0]
	_last_measured_across = canonical[1]
	_have_motion_sample = true

func request_teleport(around: float, across: float) -> bool:
	if not is_finite(around) or not is_finite(across) or across < MIN_TRAVEL_ACROSS or across > MAX_TRAVEL_ACROSS:
		_error = "Travel requires a finite around coordinate and an across coordinate between 80 m and %d m." % int(MAX_TRAVEL_ACROSS)
		return false
	_teleport_around = fposmod(around, TerrainGenerationRecipe.CERTIFIED_CIRCUMFERENCE_M)
	_teleport_across = across
	_request_window(_lattice(int(round(_teleport_around))), _safe_across_anchor(int(round(across))), true, false, "teleport")
	return true

func _clamp_player() -> void:
	var position: PackedFloat64Array = _surface.call("GetCanonicalPosition", _player.global_position)
	var around := _clamp_around(position[0])
	var across := clampf(position[1], _anchor_across - SAFE_EDGE, _anchor_across + SAFE_EDGE)
	var constrained := absf(_around_delta(around, position[0])) > 0.001 or absf(across - position[1]) > 0.001
	if constrained:
		var height: float = _surface.call("SampleCommittedHeight", around, across)
		if is_finite(height):
			_player.global_position = _surface.call("MapCanonicalPosition", around, across, maxf(height + CLEARANCE, position[2]))
		_player.velocity = Vector3.ZERO
		if not _waiting_at_edge:
			_edge_waits += 1
		_waiting_at_edge = true
	elif absf(_around_delta(around, _anchor_around)) < SAFE_EDGE - 1.0 and absf(across - _anchor_across) < SAFE_EDGE - 1.0:
		_waiting_at_edge = false
	_canonical_around = around
	_canonical_across = across

func _around_delta(value: float, anchor: float) -> float:
	var circumference := TerrainGenerationRecipe.CERTIFIED_CIRCUMFERENCE_M
	return fposmod(value - anchor + circumference * 0.5, circumference) - circumference * 0.5

func _clamp_around(value: float) -> float:
	return fposmod(_anchor_around + clampf(_around_delta(value, _anchor_around), -SAFE_EDGE, SAFE_EDGE),
		TerrainGenerationRecipe.CERTIFIED_CIRCUMFERENCE_M)

func _safe_across_anchor(value: int) -> int:
	return clampi(_lattice(value), 256, int(TerrainGenerationRecipe.CERTIFIED_WIDTH_M) - 512)

func _lattice(value: int) -> int:
	return int(round(float(value) / 2.0)) * 2

func _input(event: InputEvent) -> void:
	if event is InputEventKey and event.pressed and not event.echo:
		if event.keycode == KEY_ESCAPE:
			Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
		elif event.keycode == KEY_F and not _drawer_owns_mouse():
			_flying = not _flying
	if event is InputEventMouseButton and event.pressed and event.button_index == MOUSE_BUTTON_LEFT:
		if not _drawer_owns_mouse():
			Input.mouse_mode = Input.MOUSE_MODE_CAPTURED
	if event is InputEventMouseMotion and Input.mouse_mode == Input.MOUSE_MODE_CAPTURED and not _drawer_owns_mouse() and _player != null and _head != null:
		_player.rotate_y(-event.relative.x * 0.002)
		_head.rotate_x(-event.relative.y * 0.002)
		_head.rotation.x = clampf(_head.rotation.x, deg_to_rad(-85.0), deg_to_rad(85.0))

func _drawer_owns_mouse() -> bool:
	return get_tree().root.get_meta("ringworld_live_tuning_mouse_open", false)

func _update_status() -> void:
	if _status == null:
		return
	if not _error.is_empty():
		_status.text = _error
	elif not _preview_ready:
		_status.text = "Generating selected terrain patch..."
	elif _teleport_pending:
		_status.text = "Preparing travel destination..."
	elif _waiting_at_edge:
		_status.text = "At the prepared terrain boundary; waiting for usable coverage."
	elif not _staged.is_empty():
		_status.text = "Terrain prepared ahead; waiting for a safe handoff."
	elif not _requested.is_empty():
		_status.text = "Preparing terrain; the current patch remains active."
	else:
		_status.text = "TERRAIN GENERATION PREVIEW | Grave tune | WASD move | Shift sprint | Space jump | F fly | Q/E down/up"

func get_preview_diagnostics() -> Dictionary:
	return {
		"ready": _preview_ready, "updating": not _requested.is_empty(),
		"revision": _revision, "committed_revision": _committed_revision,
		"recipe_epoch": _recipe_epoch, "edit_epoch": _edit_epoch, "frame_epoch": _frame_epoch,
		"error": _error, "save_blocked": _save_blocked, "anchor_around": _anchor_around,
		"anchor_across": _anchor_across, "canonical_around": _canonical_around,
		"canonical_across": _canonical_across, "sampling_ms": _sampling_ms, "packing_ms": _packing_ms,
		"projection_ms": _projection_ms, "upload_budget_ms": _upload_budget_ms,
		"collision_budget_ms": _collision_budget_ms, "collider_tile_cells": _collider_tile_cells,
		"preparing": _preparing.duplicate(),
		"edge_waits": _edge_waits, "waiting_at_edge": _waiting_at_edge,
		"actual_velocity": _measured_velocity, "lookahead": _last_lookahead,
		"readiness_ms": _last_readiness_ms, "cache_hits": _last_cache_hits,
		"request_kind": str(_requested.get("kind", "")), "stale_results": _stale_results,
		"native": _surface.call("GetPreparedDiagnostics") if _surface != null else {},
		"stream": _stream.get_diagnostics() if _stream != null else {},
	}

func save_recipe() -> bool:
	if _save_blocked:
		_error = "Saved recipe is protected. Use Reset recipe before saving."
		return false
	_error = TerrainGenerationPreviewStore.save_recipe(_recipe, _recipe_path())
	return _error.is_empty()

func upgrade_to_enhanced_erosion() -> void:
	_initialize_recipe()
	if _recipe == null or _recipe.generator_id != TerrainGenerationRecipe.DETAIL_GENERATOR_ID:
		return
	var upgraded := _recipe.snapshot()
	upgraded.generator_id = TerrainGenerationRecipe.EROSION_GENERATOR_ID
	upgraded.erosion_ridge_preservation = 0.75
	upgraded.gully_normalization = 0.6
	_recipe = upgraded.snapshot()
	_error = ""
	_save_blocked = false
	regenerate()

func reset_recipe() -> void:
	_recipe = TerrainGenerationRecipe.create_erosion_default()
	_error = ""
	_save_blocked = false
	regenerate()
	save_recipe()

func return_to_overview() -> void:
	PREVIEW_HANDOFF.stage(_recipe, _canonical_around, _canonical_across, _recipe_path(), _save_blocked, _error)
	get_tree().change_scene_to_file("res://scenes/TerrainGenerationPreview.tscn")

func ApplySurfaceSettings(settings: Resource) -> void:
	if settings == null:
		return
	_move_speed = clampf(float(settings.get("MoveSpeedMetersPerSecond")), 2.0, 60.0)
	_mesh_lods = clampi(int(settings.get("MeshLods")), 2, 6)
	_worker_count = clampi(int(settings.get("GenerationWorkerCount")), 1, 4)
	_prefetch_m = clampf(float(settings.get("GenerationPrefetchMeters")), 16.0, 160.0)
	_cache_mib = clampi(int(settings.get("GenerationCacheMiB")), 1, 64)
	_upload_budget_ms = clampf(float(settings.get("GenerationUploadBudgetMs")), .1, 8.0)
	_collision_budget_ms = clampf(float(settings.get("GenerationCollisionBudgetMs")), .1, 8.0)
	var next_tile_cells := 16 if int(settings.get("GenerationColliderTileCells")) == 16 else 32
	var tile_cells_changed := next_tile_cells != _collider_tile_cells
	_collider_tile_cells = next_tile_cells
	if _stream != null:
		_stream.configure(_worker_count, _cache_mib, _collider_tile_cells)
	if _surface != null:
		_surface.call("SetMeshLods", _mesh_lods)
	_smooth_terrain_shading = bool(settings.get("SmoothTerrainShading"))
	if _surface != null:
		_surface.call("SetSmoothTerrainShading", _smooth_terrain_shading)
	if tile_cells_changed and _stream != null and (_preview_ready or not _requested.is_empty()):
		if _teleport_pending:
			_request_window(_lattice(int(round(_teleport_around))), _safe_across_anchor(int(round(_teleport_across))), true, false, "teleport")
		else:
			_request_window(_anchor_around, _anchor_across, false, false, "recipe")
