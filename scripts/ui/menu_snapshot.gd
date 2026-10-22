extends RefCounted
## Last-session menu snapshot: a world-only frame captured at cruise altitude, shown as the boot cover until the live world is ready.
##
## Files (per target slot, see WorldSession.get_menu_snapshot_paths): menu_snapshot.png and menu_snapshot.json. The sidecar is written
## last, as the commit marker, so a PNG without a readable matching sidecar is ignored.

const FORMAT_VERSION := 1
## Bump when the look of the captured frame changes (renderer, lighting, post) so old snapshots stop being shown.
const RENDER_EPOCH := 1
const MAX_HEIGHT := 1080
## Positions in the state stamp may differ by this much (metres) before the snapshot counts as stale.
const STAMP_POSITION_TOLERANCE_M := 0.5

static var _pending_tasks: Array[int] = []


## Validated snapshot for a target slot: {texture, sidecar, stale}, or {} when missing, unreadable or from another format/epoch.
## `stale` is true when the saved state no longer matches the snapshot's state stamp (for example after a window close).
static func load_valid(target: int) -> Dictionary:
	var session := _session()
	if session == null:
		return {}
	var paths: Dictionary = session.get_menu_snapshot_paths(target)
	var loaded := load_files(str(paths.get("png", "")), str(paths.get("json", "")))
	if loaded.is_empty():
		return {}
	var current: Dictionary = session.read_world_state_stamp(str(paths["png"]).get_base_dir())
	loaded["stale"] = not _stamps_match(loaded["sidecar"].get("state_stamp", {}), current)
	return loaded


## Identity of the target's saved state right now, in the same form as a sidecar's state_stamp ({} when there is no readable state file).
static func current_state_stamp(target: int) -> Dictionary:
	var session := _session()
	if session == null:
		return {}
	var paths: Dictionary = session.get_menu_snapshot_paths(target)
	return session.read_world_state_stamp(str(paths.get("png", "")).get_base_dir()) if not str(paths.get("png", "")).is_empty() else {}


## Loads and validates a PNG plus sidecar pair (no state-stamp check; stale is false). Used by fixtures and snapshot_override_path.
static func load_files(png_path: String, json_path: String) -> Dictionary:
	if png_path.is_empty() or json_path.is_empty() or not FileAccess.file_exists(json_path) or not FileAccess.file_exists(png_path):
		return {}
	var file := FileAccess.open(json_path, FileAccess.READ)
	if file == null:
		return {}
	var parser := JSON.new()
	var parse_error := parser.parse(file.get_as_text())
	file.close()
	if parse_error != OK or not (parser.data is Dictionary):
		return {}
	var sidecar: Dictionary = parser.data
	if int(sidecar.get("format_version", -1)) != FORMAT_VERSION or int(sidecar.get("render_epoch", -1)) != RENDER_EPOCH:
		return {}
	# Load from disk, not ResourceLoader, so a re-captured file is never served from cache.
	var image := Image.load_from_file(ProjectSettings.globalize_path(png_path))
	if image == null or image.is_empty():
		return {}
	return {"texture": ImageTexture.create_from_image(image), "sidecar": sidecar, "stale": false}


## Draws one frame of the world with `hide_nodes` (the menu) hidden and returns the root viewport image, or null when headless.
## The caller restores its own focus afterwards: hiding a focused control releases focus.
static func capture_world_only(tree: SceneTree, hide_nodes: Array) -> Image:
	if DisplayServer.get_name() == "headless":
		return null
	var hidden: Array = []
	for node: Variant in hide_nodes:
		if node is CanvasItem and is_instance_valid(node) and (node as CanvasItem).visible:
			(node as CanvasItem).visible = false
			hidden.append(node)
	RenderingServer.force_draw(false, 0.0)
	var image := tree.root.get_texture().get_image()
	for node: CanvasItem in hidden:
		if is_instance_valid(node):
			node.visible = true
	return image


## Sidecar body for a capture; write_async adds the format fields and image_size.
static func make_sidecar(kind: String, state_stamp: Dictionary, pose: Dictionary, sun_elevation_deg: float, last_warm_ms: float) -> Dictionary:
	return {
		"kind": kind,
		"state_stamp": state_stamp.duplicate(true),
		"pose": pose.duplicate(true),
		"sun_elevation_deg": sun_elevation_deg,
		"last_warm_ms": last_warm_ms,
		"captured_unix": int(Time.get_unix_time_from_system()),
	}


## Downscales (Lanczos, at most MAX_HEIGHT tall, RGB8) and writes the PNG then the sidecar on a worker thread. Returns the task id
## (or -1 when nothing was queued). The previous sidecar is removed first so a half-written pair is never trusted.
static func write_async(target: int, image: Image, sidecar: Dictionary) -> int:
	var session := _session()
	if session == null or image == null or image.is_empty():
		return -1
	var paths: Dictionary = session.get_menu_snapshot_paths(target)
	if str(paths.get("png", "")).is_empty():
		return -1
	var png := ProjectSettings.globalize_path(str(paths["png"]))
	var json := ProjectSettings.globalize_path(str(paths["json"]))
	if FileAccess.file_exists(json):
		DirAccess.remove_absolute(json)
	var data := sidecar.duplicate(true)
	data["format_version"] = FORMAT_VERSION
	data["render_epoch"] = RENDER_EPOCH
	var task := WorkerThreadPool.add_task(_write_files.bind(image.duplicate() as Image, png, json, data), false, "menu snapshot write")
	_pending_tasks.append(task)
	return task


## Blocks until every queued snapshot write has finished (tests and shutdown).
static func wait_pending() -> void:
	for task in _pending_tasks:
		WorkerThreadPool.wait_for_task_completion(task)
	_pending_tasks.clear()


static func has_pending() -> bool:
	for task in _pending_tasks.duplicate():
		if WorkerThreadPool.is_task_completed(task):
			WorkerThreadPool.wait_for_task_completion(task)
			_pending_tasks.erase(task)
	return not _pending_tasks.is_empty()


static func _write_files(image: Image, png: String, json: String, data: Dictionary) -> void:
	image.convert(Image.FORMAT_RGB8)
	if image.get_height() > MAX_HEIGHT:
		var width := maxi(1, roundi(float(image.get_width()) * float(MAX_HEIGHT) / float(image.get_height())))
		image.resize(width, MAX_HEIGHT, Image.INTERPOLATE_LANCZOS)
	data["image_size"] = [image.get_width(), image.get_height()]
	if image.save_png(png + ".tmp") != OK or not _replace(png + ".tmp", png):
		push_warning("Menu snapshot: could not write " + png)
		return
	var file := FileAccess.open(json + ".tmp", FileAccess.WRITE)
	if file == null:
		push_warning("Menu snapshot: could not write " + json)
		return
	file.store_string(JSON.stringify(data, "\t"))
	file.close()
	_replace(json + ".tmp", json)


static func _replace(temporary: String, final: String) -> bool:
	if FileAccess.file_exists(final):
		DirAccess.remove_absolute(final)
	return DirAccess.rename_absolute(temporary, final) == OK


static func _stamps_match(a: Variant, b: Variant) -> bool:
	if not (a is Dictionary) or not (b is Dictionary) or a.is_empty() or b.is_empty():
		return false
	return int(a.get("saved_unix", -1)) == int(b.get("saved_unix", -2)) \
		and absf(float(a.get("around_m", 0.0)) - float(b.get("around_m", 1.0e12))) <= STAMP_POSITION_TOLERANCE_M \
		and absf(float(a.get("across_m", 0.0)) - float(b.get("across_m", 1.0e12))) <= STAMP_POSITION_TOLERANCE_M


static func _session() -> Node:
	var loop := Engine.get_main_loop()
	return (loop as SceneTree).root.get_node_or_null("WorldSession") if loop is SceneTree else null
