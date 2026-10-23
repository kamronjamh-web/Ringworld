extends SceneTree
## Regenerates the bundled showcase picture (res://resources/menu/showcase_snapshot.png + .json) at the window's native resolution.
## ALWAYS run through tools/showcase_snapshot_capture.ps1 (windowed GPU, isolated APPDATA). It creates the showcase slot in the isolated
## profile, lets the start menu warm-load it like any saved world, waits until the world is quiet at cruise altitude, then captures the
## world with the menu hidden. Rerun whenever SHOWCASE_ID in tools/showcase_world_definition.gd changes.

const MENU := preload("res://scenes/StartMenu.tscn")
const SNAPSHOT := preload("res://scripts/ui/menu_snapshot.gd")
const WARM_TIMEOUT_MS := 400000
const WATCHDOG_USEC := 900 * 1000000

var _menu: Control


func _initialize() -> void:
	call_deferred("_run")


func _process(_delta: float) -> bool:
	if Time.get_ticks_usec() > WATCHDOG_USEC:
		push_error("watchdog: showcase snapshot capture exceeded its time budget")
		quit(3)
	return false


func _fail(message: String) -> void:
	push_error(message)
	print("SHOWCASE_SNAPSHOT=FAIL ", message)
	quit(1)


func _run() -> void:
	var user_dir := ProjectSettings.globalize_path("user://").replace("\\", "/").to_lower()
	if not user_dir.contains("showcase_tools"):
		push_error("Refusing to run: user:// (%s) is not an isolated showcase_tools APPDATA." % user_dir)
		quit(2)
		return
	if DisplayServer.get_name() == "headless":
		return _fail("needs a window and a GPU (not --headless)")
	# Ask for the requested size explicitly (borderless, at the origin); the OS may still clamp the height to the screen.
	var requested := Vector2i(2560, 1440)
	for arg in OS.get_cmdline_args():
		if arg.contains("x") and arg.substr(0, 1).is_valid_int():
			requested = Vector2i(int(arg.get_slice("x", 0)), int(arg.get_slice("x", 1)))
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_BORDERLESS, true)
	DisplayServer.window_set_position(Vector2i(0, 0))
	DisplayServer.window_set_size(requested)
	for i in 10:
		await process_frame
	var session := root.get_node_or_null("WorldSession")
	if session == null or not session.create_showcase_world(1):
		return _fail("could not create the showcase slot")
	session.clear_preview_slot()
	var id := str(session.get_showcase_definition()["showcase_id"])
	_menu = MENU.instantiate()
	root.add_child(_menu)
	current_scene = _menu
	await process_frame
	var warm := root.get_node_or_null("RingWarmWorld")
	if warm == null:
		return _fail("the menu did not start a warm-load of the showcase slot")
	var deadline := Time.get_ticks_msec() + WARM_TIMEOUT_MS
	while not bool(warm.reveal_complete) and Time.get_ticks_msec() < deadline:
		if int(warm.state) == 0:
			return _fail("the warm-load of the showcase failed")
		await process_frame
	if not bool(warm.reveal_complete):
		return _fail("the showcase world did not become quiet in time")
	var pose: Dictionary = warm.get_camera_pose()
	var image: Image = SNAPSHOT.capture_world_only(self, [_menu])
	if image == null or image.is_empty() or not bool(pose.get("valid", false)):
		return _fail("capture produced no image or pose")
	pose.erase("valid")
	var sun := float(warm.get_telemetry().get("sun_elevation_degrees", 0.0))
	var sidecar: Dictionary = SNAPSHOT.make_sidecar("showcase", {}, pose, sun, warm.get_last_warm_ms())
	sidecar["showcase_id"] = id
	var paths: Dictionary = session.get_showcase_bundled_snapshot_paths()
	if SNAPSHOT.write_to_paths_async(str(paths["png"]), str(paths["json"]), image, sidecar, 0) < 0:
		return _fail("could not queue the snapshot write")
	SNAPSHOT.wait_pending()
	print("SHOWCASE_SNAPSHOT=PASS window=", DisplayServer.window_get_size(), " root=", root.size, " id=%s size=%dx%d png=%s" % [id, image.get_width(), image.get_height(), ProjectSettings.globalize_path(str(paths["png"]))])
	await warm.teardown()
	quit(0)
