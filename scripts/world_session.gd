extends Node

const SLOT_COUNT := 3
const WORLDS_ROOT := "user://worlds"
const METADATA_FILE := "world.json"
const THUMBNAIL_FILE := "thumbnail.png"
const THUMBNAIL_SIZE := Vector2i(480, 270)
const PLAYTIME_FLUSH_SECONDS := 60.0
const MAX_NAME_LENGTH := 32
const EXIT_POOL_DRAIN_TIMEOUT_MS := 10000
## The menu showcase: a frozen Standard world (seed 1337). Choosing it creates a normal save slot from these generation settings.
## While no saved world is eligible for a warm-load the start menu shows the bundled picture of it instead of a live world.
const SHOWCASE_DEFINITION_PATH := "res://resources/menu/showcase_world.json"
const SHOWCASE_BUNDLED_SNAPSHOT_PNG := "res://resources/menu/showcase_snapshot.png"
const SHOWCASE_BUNDLED_SNAPSHOT_JSON := "res://resources/menu/showcase_snapshot.json"

var active_slot := 0
var worlds_root := WORLDS_ROOT
var _load_selected_world_requested := false
var _pending_new_world := {}
var _play_world: WeakRef
var _unflushed_play_seconds := 0.0
var _since_flush := 0.0
## True while the start menu holds a warm (background-loaded) world. The proxy mesh cache and the vegetation cache read the global
## active_world_directory at runtime, so nothing may change the active slot until RingWarmWorld has torn the world down.
var warm_world_active := false
var _showcase_definition := {}
var _showcase_loaded := false


func select_slot(slot: int) -> bool:
	if slot < 1 or slot > SLOT_COUNT:
		return false
	assert(not warm_world_active, "select_slot while a warm world exists; await RingWarmWorld.teardown() first")
	if warm_world_active:
		push_error("select_slot(%d) refused: a warm world is bound to slot %d." % [slot, active_slot])
		return false
	end_play_session()
	active_slot = slot
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", _slot_directory(slot))
	if not slot_exists(slot):
		return _create_slot(slot)
	_touch_slot(slot)
	return true


## Binds a slot as the active world directory for a background (warm) load. Unlike select_slot it does not touch last_played, does
## not end the play session and never creates a slot.
func select_slot_for_preview(slot: int) -> bool:
	if slot < 1 or slot > SLOT_COUNT or not slot_exists(slot):
		return false
	active_slot = slot
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", _slot_directory(slot))
	return true


## Clears the binding made by select_slot_for_preview once the warm world has been freed.
func clear_preview_slot() -> void:
	active_slot = 0
	_load_selected_world_requested = false
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")


## Slot the start menu may warm-load behind the flyover, or 0. Pure: reads metadata only. Only runtime-generation Standard worlds
## qualify; `--no-warm-world` disables it for tests.
func get_live_menu_slot() -> int:
	if "--no-warm-world" in OS.get_cmdline_user_args() or bool(ProjectSettings.get_setting("ringworld/runtime/no_warm_world", false)):
		return 0
	var slot := get_most_recent_slot()
	if slot <= 0:
		return 0
	var settings: Variant = get_slot_metadata(slot).get("generation_settings", {})
	if not (settings is Dictionary) or not _is_standard_runtime_settings(settings):
		return 0
	return slot


func _is_standard_runtime_settings(settings: Dictionary) -> bool:
	if str(settings.get("terrain_backend", "")) != "surface_runtime_generation_v1":
		return false
	var standard := WorldPresets.describe("standard")
	return absf(float(settings.get("radius_km", 0.0)) - float(standard.radius_km)) <= 0.01 \
		and absf(float(settings.get("ring_width_km", 0.0)) - float(standard.width_km)) <= 0.01


## The validated showcase definition ({showcase_id, name, generation_settings}) from res://resources/menu/showcase_world.json, or {}
## when the file is missing or not a Standard runtime-generation definition.
func get_showcase_definition() -> Dictionary:
	if _showcase_loaded:
		return _showcase_definition
	_showcase_loaded = true
	if not FileAccess.file_exists(SHOWCASE_DEFINITION_PATH):
		return _showcase_definition
	var parsed: Variant = JSON.parse_string(FileAccess.get_file_as_string(SHOWCASE_DEFINITION_PATH))
	if not (parsed is Dictionary):
		push_warning("Showcase definition is not valid JSON.")
		return _showcase_definition
	var id := str(parsed.get("showcase_id", ""))
	var settings: Variant = parsed.get("generation_settings", {})
	if id.is_empty() or not id.is_valid_filename() or not (settings is Dictionary) or not _is_standard_runtime_settings(settings):
		push_warning("Showcase definition is incomplete or not a Standard runtime-generation world.")
		return _showcase_definition
	_showcase_definition = {
		"showcase_id": id,
		"name": str(parsed.get("name", "Showcase")),
		"generation_settings": (settings as Dictionary).duplicate(true),
	}
	return _showcase_definition


## The showcase picture that ships in res:// (hi-res). Regenerate it with tools/showcase_snapshot_capture.ps1 whenever showcase_id changes.
func get_showcase_bundled_snapshot_paths() -> Dictionary:
	return {"png": SHOWCASE_BUNDLED_SNAPSHOT_PNG, "json": SHOWCASE_BUNDLED_SNAPSHOT_JSON}


func get_first_empty_slot() -> int:
	for slot in range(1, SLOT_COUNT + 1):
		if not slot_exists(slot):
			return slot
	return 0


## Creates a normal save slot from the frozen showcase settings and binds it (like create_world). The slot saves like any world;
## its first load spawns at the fresh-spawn viewpoint. Returns false when the definition is invalid or the slot is taken.
func create_showcase_world(slot: int) -> bool:
	var definition := get_showcase_definition()
	if definition.is_empty() or slot < 1 or slot > SLOT_COUNT or slot_exists(slot):
		return false
	if not create_world(slot, str(definition["name"])):
		return false
	return save_active_world_settings(definition["generation_settings"])


func request_load_selected_world() -> void:
	_load_selected_world_requested = active_slot > 0
	_pending_new_world = {}


func consume_load_selected_world_request() -> bool:
	var requested := _load_selected_world_requested
	_load_selected_world_requested = false
	return requested


func request_new_world(request: Dictionary) -> void:
	_pending_new_world = request.duplicate(true)
	_load_selected_world_requested = false


func consume_new_world_request() -> Dictionary:
	var request := _pending_new_world
	_pending_new_world = {}
	return request


func rename_slot(slot: int, new_name: String) -> bool:
	if slot < 1 or slot > SLOT_COUNT or not slot_exists(slot):
		return false
	var cleaned := ""
	for i in new_name.length():
		var code := new_name.unicode_at(i)
		if code >= 32 and code != 127:
			cleaned += new_name[i]
	cleaned = cleaned.strip_edges()
	if cleaned.length() > MAX_NAME_LENGTH:
		cleaned = cleaned.substr(0, MAX_NAME_LENGTH).strip_edges()
	if cleaned.is_empty():
		return false
	var metadata := get_slot_metadata(slot)
	metadata["slot"] = slot
	metadata["name"] = cleaned
	return _write_metadata(slot, metadata)


func create_world(slot: int, world_name: String) -> bool:
	if not select_slot(slot):
		return false
	return rename_slot(slot, world_name)


func get_slot_thumbnail_path(slot: int) -> String:
	if slot < 1 or slot > SLOT_COUNT:
		return ""
	var path := "%s/%s" % [_slot_directory(slot), THUMBNAIL_FILE]
	return path if FileAccess.file_exists(path) else ""


func save_active_thumbnail(image: Image) -> bool:
	if image == null or image.is_empty() or active_slot == 0 or not slot_exists(active_slot):
		return false
	var thumb := image.duplicate() as Image
	if thumb.is_compressed():
		return false
	thumb.convert(Image.FORMAT_RGB8)
	var target_aspect := float(THUMBNAIL_SIZE.x) / float(THUMBNAIL_SIZE.y)
	var width := thumb.get_width()
	var height := thumb.get_height()
	var crop_w := width
	var crop_h := height
	if float(width) / float(height) > target_aspect:
		crop_w = int(round(float(height) * target_aspect))
	else:
		crop_h = int(round(float(width) / target_aspect))
	var cropped := thumb.get_region(Rect2i((width - crop_w) / 2, (height - crop_h) / 2, crop_w, crop_h))
	cropped.resize(THUMBNAIL_SIZE.x, THUMBNAIL_SIZE.y, Image.INTERPOLATE_LANCZOS)
	var path := "%s/%s" % [_slot_directory(active_slot), THUMBNAIL_FILE]
	var temporary := path + ".tmp"
	if cropped.save_png(temporary) != OK:
		return false
	if FileAccess.file_exists(path):
		DirAccess.remove_absolute(ProjectSettings.globalize_path(path))
	return DirAccess.rename_absolute(ProjectSettings.globalize_path(temporary), ProjectSettings.globalize_path(path)) == OK


func begin_play_session(world: Node) -> void:
	end_play_session()
	_play_world = weakref(world) if world != null else null
	_since_flush = 0.0


func end_play_session() -> void:
	flush_playtime()
	_play_world = null
	_since_flush = 0.0


func flush_playtime() -> void:
	if active_slot == 0 or not slot_exists(active_slot) or _unflushed_play_seconds < 1.0:
		return
	var whole := int(_unflushed_play_seconds)
	_unflushed_play_seconds -= float(whole)
	_since_flush = 0.0
	var metadata := get_slot_metadata(active_slot)
	metadata["slot"] = active_slot
	metadata["playtime_seconds"] = int(metadata.get("playtime_seconds", 0)) + whole
	metadata["last_played_unix"] = int(Time.get_unix_time_from_system())
	_write_metadata(active_slot, metadata)


func _process(delta: float) -> void:
	if _play_world == null:
		return
	var world := _play_world.get_ref() as Node
	if world == null or not is_instance_valid(world):
		end_play_session()
		return
	if not world.is_inside_tree():
		return
	_unflushed_play_seconds += delta
	_since_flush += delta
	if _since_flush >= PLAYTIME_FLUSH_SECONDS:
		flush_playtime()
		_since_flush = 0.0


func _notification(what: int) -> void:
	if what == NOTIFICATION_WM_CLOSE_REQUEST:
		flush_playtime()


func _exit_tree() -> void:
	flush_playtime()
	_drain_worker_pool_for_exit()


# Godot 4.8-dev7 WorkerThreadPool::exit_languages_threads() can hang forever if
# low-priority tasks are still queued when cleanup starts (idle workers are not
# re-notified). A low-priority sentinel completes only after that queue drains.
# Must run in the first autoload's _exit_tree, i.e. after the world's nodes exited.
func _drain_worker_pool_for_exit() -> void:
	var id := WorkerThreadPool.add_task(func() -> void: pass, false, "exit drain sentinel")
	var start_ms := Time.get_ticks_msec()
	while not WorkerThreadPool.is_task_completed(id):
		if Time.get_ticks_msec() - start_ms >= EXIT_POOL_DRAIN_TIMEOUT_MS:
			push_warning("WorldSession: worker pool did not drain within %d ms; quit may stall." % EXIT_POOL_DRAIN_TIMEOUT_MS)
			return
		OS.delay_usec(1000)
	WorkerThreadPool.wait_for_task_completion(id)


func copy_slot(source_slot: int, destination_slot: int) -> bool:
	if source_slot < 1 or source_slot > SLOT_COUNT \
		or destination_slot < 1 or destination_slot > SLOT_COUNT \
		or source_slot == destination_slot \
		or not slot_exists(source_slot) \
		or slot_exists(destination_slot):
		return false
	var source_absolute := ProjectSettings.globalize_path(_slot_directory(source_slot))
	var destination_absolute := ProjectSettings.globalize_path(_slot_directory(destination_slot))
	if not _copy_directory_recursive(source_absolute, destination_absolute):
		_delete_directory_recursive(destination_absolute)
		return false
	var metadata := get_slot_metadata(destination_slot)
	var now := int(Time.get_unix_time_from_system())
	metadata["slot"] = destination_slot
	metadata["name"] = "%s Copy" % str(metadata.get("name", "World %d" % source_slot))
	metadata["created_unix"] = now
	metadata["last_played_unix"] = now
	return _write_metadata(destination_slot, metadata)


func delete_slot(slot: int) -> bool:
	if slot < 1 or slot > SLOT_COUNT or not slot_exists(slot):
		return false
	assert(not (warm_world_active and slot == active_slot), "delete_slot on the warm world's slot; await RingWarmWorld.teardown() first")
	if warm_world_active and slot == active_slot:
		push_error("delete_slot(%d) refused: a warm world is bound to this slot." % slot)
		return false
	end_play_session()
	var deleted := _delete_directory_recursive(ProjectSettings.globalize_path(_slot_directory(slot)))
	if deleted and active_slot == slot:
		active_slot = 0
		_load_selected_world_requested = false
		ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
	return deleted


func slot_exists(slot: int) -> bool:
	_recover_metadata(slot)
	return FileAccess.file_exists(_metadata_path(slot))


func get_slot_metadata(slot: int) -> Dictionary:
	_recover_metadata(slot)
	if not slot_exists(slot):
		return {}
	var file := FileAccess.open(_metadata_path(slot), FileAccess.READ)
	if file == null:
		return {}
	var parsed: Variant = JSON.parse_string(file.get_as_text())
	return parsed if parsed is Dictionary else {}


func get_most_recent_slot() -> int:
	var best_slot := 0
	var best_played := -1
	for slot in range(1, SLOT_COUNT + 1):
		if not slot_exists(slot):
			continue
		var metadata := get_slot_metadata(slot)
		var settings: Variant = metadata.get("generation_settings", {})
		if not (settings is Dictionary) or settings.is_empty():
			continue
		var played := int(metadata.get("last_played_unix", 0))
		if played > best_played:
			best_played = played
			best_slot = slot
	return best_slot


func get_active_world_settings() -> Dictionary:
	if active_slot == 0:
		return {}
	var metadata := get_slot_metadata(active_slot)
	var settings: Variant = metadata.get("generation_settings", {})
	return settings.duplicate(true) if settings is Dictionary else {}


func save_active_world_settings(settings: Dictionary) -> bool:
	if active_slot == 0 or not slot_exists(active_slot):
		return false
	var metadata := get_slot_metadata(active_slot)
	metadata["slot"] = active_slot
	metadata["name"] = str(metadata.get("name", "World %d" % active_slot))
	metadata["generation_settings"] = settings.duplicate(true)
	metadata["last_played_unix"] = int(Time.get_unix_time_from_system())
	return _write_metadata(active_slot, metadata)


## Menu boot snapshot files of a slot (image first, sidecar json last as the commit marker). Empty strings for an invalid target.
func get_menu_snapshot_paths(target: int) -> Dictionary:
	if target < 1 or target > SLOT_COUNT:
		return {"png": "", "json": ""}
	var directory := _slot_directory(target)
	return {"png": directory + "/menu_snapshot.png", "json": directory + "/menu_snapshot.json"}


## Identity of the saved player state in `directory` (a user:// or absolute slot directory): {saved_unix, around_m, across_m}.
## Empty when the state file is missing or unreadable. Used to tell whether a menu snapshot still matches the save.
func read_world_state_stamp(directory: String) -> Dictionary:
	var path := directory.path_join("world_state_surface_runtime_v1.json")
	if not FileAccess.file_exists(path):
		return {}
	var file := FileAccess.open(path, FileAccess.READ)
	if file == null:
		return {}
	var parsed: Variant = JSON.parse_string(file.get_as_text())
	file.close()
	if not (parsed is Dictionary) or not (parsed.get("Player") is Dictionary):
		return {}
	var player: Dictionary = parsed["Player"]
	return {
		"saved_unix": int(parsed.get("SavedUnix", 0)),
		"around_m": float(player.get("AroundMeters", 0.0)),
		"across_m": float(player.get("AcrossMeters", 0.0)),
	}


func get_active_world_directory() -> String:
	return _slot_directory(active_slot) if active_slot > 0 else ""


func _create_slot(slot: int) -> bool:
	var directory := _slot_directory(slot)
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(directory))
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(directory + "/regions"))
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(directory + "/lod_cache"))
	var now := int(Time.get_unix_time_from_system())
	return _write_metadata(slot, {
		"format_version": 1,
		"slot": slot,
		"name": "World %d" % slot,
		"created_unix": now,
		"last_played_unix": now,
		"generation_settings": {}
	})


func _touch_slot(slot: int) -> void:
	var metadata := get_slot_metadata(slot)
	metadata["last_played_unix"] = int(Time.get_unix_time_from_system())
	_write_metadata(slot, metadata)


func _write_metadata(slot: int, metadata: Dictionary) -> bool:
	var directory := _slot_directory(slot)
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(directory))
	var path := _metadata_path(slot)
	var temporary := path + ".tmp"
	var file := FileAccess.open(temporary, FileAccess.WRITE)
	if file == null:
		return false
	file.store_string(JSON.stringify(metadata, "  "))
	file.close()
	var absolute_temp := ProjectSettings.globalize_path(temporary)
	var absolute_path := ProjectSettings.globalize_path(path)
	if FileAccess.file_exists(path):
		DirAccess.remove_absolute(absolute_path)
	return DirAccess.rename_absolute(absolute_temp, absolute_path) == OK


func _recover_metadata(slot: int) -> void:
	var path := _metadata_path(slot)
	var temporary := path + ".tmp"
	if not FileAccess.file_exists(path) and FileAccess.file_exists(temporary):
		DirAccess.rename_absolute(ProjectSettings.globalize_path(temporary), ProjectSettings.globalize_path(path))


func _slot_directory(slot: int) -> String:
	return "%s/slot_%d" % [worlds_root, slot]


func _metadata_path(slot: int) -> String:
	return "%s/%s" % [_slot_directory(slot), METADATA_FILE]


func _copy_directory_recursive(source: String, destination: String) -> bool:
	var source_dir := DirAccess.open(source)
	if source_dir == null:
		return false
	if DirAccess.make_dir_recursive_absolute(destination) != OK:
		return false
	for directory_name in source_dir.get_directories():
		if not _copy_directory_recursive(source.path_join(directory_name), destination.path_join(directory_name)):
			return false
	for file_name in source_dir.get_files():
		if DirAccess.copy_absolute(source.path_join(file_name), destination.path_join(file_name)) != OK:
			return false
	return true


func _delete_directory_recursive(path: String) -> bool:
	if not DirAccess.dir_exists_absolute(path):
		return true
	var directory := DirAccess.open(path)
	if directory == null:
		return false
	for directory_name in directory.get_directories():
		if not _delete_directory_recursive(path.path_join(directory_name)):
			return false
	for file_name in directory.get_files():
		if DirAccess.remove_absolute(path.path_join(file_name)) != OK:
			return false
	return DirAccess.remove_absolute(path) == OK
