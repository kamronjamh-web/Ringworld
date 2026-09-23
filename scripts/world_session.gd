extends Node

const SLOT_COUNT := 3
const WORLDS_ROOT := "user://worlds"
const METADATA_FILE := "world.json"

var active_slot := 0
var _load_selected_world_requested := false


func select_slot(slot: int) -> bool:
	if slot < 1 or slot > SLOT_COUNT:
		return false
	active_slot = slot
	ProjectSettings.set_setting("ringworld/runtime/active_world_directory", _slot_directory(slot))
	if not slot_exists(slot):
		return _create_slot(slot)
	_touch_slot(slot)
	return true


func request_load_selected_world() -> void:
	_load_selected_world_requested = active_slot > 0


func consume_load_selected_world_request() -> bool:
	var requested := _load_selected_world_requested
	_load_selected_world_requested = false
	return requested


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
	var deleted := _delete_directory_recursive(ProjectSettings.globalize_path(_slot_directory(slot)))
	if deleted and active_slot == slot:
		active_slot = 0
		_load_selected_world_requested = false
		ProjectSettings.set_setting("ringworld/runtime/active_world_directory", "")
	return deleted


func slot_exists(slot: int) -> bool:
	return FileAccess.file_exists(_metadata_path(slot))


func get_slot_metadata(slot: int) -> Dictionary:
	if not slot_exists(slot):
		return {}
	var file := FileAccess.open(_metadata_path(slot), FileAccess.READ)
	if file == null:
		return {}
	var parsed: Variant = JSON.parse_string(file.get_as_text())
	return parsed if parsed is Dictionary else {}


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


func _slot_directory(slot: int) -> String:
	return "%s/slot_%d" % [WORLDS_ROOT, slot]


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
