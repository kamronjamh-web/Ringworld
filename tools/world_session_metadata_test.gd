extends SceneTree

# Phase B: WorldSession metadata additions (create/rename/thumbnail/playtime/.tmp recovery/new-world request) plus the launcher
# applying a pending new-world request. Uses a temp worlds_root override; never touches the real user://worlds saves.

const LAUNCHER := preload("res://scenes/WorldSizeLauncher.tscn")
const SESSION_SCRIPT := preload("res://scripts/world_session.gd")
const PRESETS := preload("res://scripts/world_presets.gd")
const TEST_ROOT := "user://world_session_metadata_test"

var failures := 0

func _init() -> void:
	call_deferred("_run")

func _check(condition: bool, message: String) -> void:
	if not condition:
		push_error("WORLD_SESSION_META_FAIL " + message)
		failures += 1

func _run() -> void:
	var absolute := ProjectSettings.globalize_path(TEST_ROOT)
	_remove_tree(absolute)
	var session: Node = SESSION_SCRIPT.new()
	session.worlds_root = TEST_ROOT
	root.add_child(session)

	_check(session.create_world(1, "  Hello\tWorld  "), "create_world failed")
	_check(str(session.get_slot_metadata(1).get("name", "")) == "HelloWorld", "name not sanitized: %s" % session.get_slot_metadata(1).get("name", ""))
	_check(not session.rename_slot(1, "  \n "), "empty rename accepted")
	_check(session.rename_slot(1, "x".repeat(60)) and str(session.get_slot_metadata(1).get("name", "")).length() == 32, "name not capped at 32")
	_check(session.get_slot_metadata(1).get("generation_settings", null) is Dictionary, "generation_settings missing")
	_check(session.create_world(1, "Reused") and str(session.get_slot_metadata(1).get("name", "")) == "Reused", "reuse of existing slot failed")
	_check(not session.rename_slot(2, "Nope"), "rename of missing slot succeeded")

	_check(session.get_slot_thumbnail_path(1) == "", "thumbnail should not exist yet")
	var source := Image.create(1000, 500, false, Image.FORMAT_RGBA8)
	source.fill(Color(0.2, 0.5, 0.8))
	_check(session.save_active_thumbnail(source), "save_active_thumbnail failed")
	var thumb_path: String = session.get_slot_thumbnail_path(1)
	_check(thumb_path != "", "thumbnail path empty after save")
	var loaded := Image.load_from_file(ProjectSettings.globalize_path(thumb_path))
	_check(loaded != null and loaded.get_size() == Vector2i(480, 270), "thumbnail size %s" % (loaded.get_size() if loaded != null else "null"))
	_check(not FileAccess.file_exists(thumb_path + ".tmp"), "thumbnail tmp left behind")

	# Playtime.
	var fake_world := Node.new()
	root.add_child(fake_world)
	session.begin_play_session(fake_world)
	session._process(2.75)
	session.flush_playtime()
	_check(int(session.get_slot_metadata(1).get("playtime_seconds", 0)) == 2, "playtime flush expected 2, got %s" % session.get_slot_metadata(1).get("playtime_seconds", 0))
	session._process(0.5)
	session.end_play_session()
	_check(int(session.get_slot_metadata(1).get("playtime_seconds", 0)) == 3, "fractional remainder not carried: %s" % session.get_slot_metadata(1).get("playtime_seconds", 0))
	session.begin_play_session(fake_world)
	session._process(5.0)
	fake_world.free()
	session._process(0.0)
	_check(int(session.get_slot_metadata(1).get("playtime_seconds", 0)) == 8, "freed world did not end session with flush")
	_check(session._play_world == null, "play session not cleared when world freed")

	# A flush after deletion must not recreate the slot.
	var live_world := Node.new()
	root.add_child(live_world)
	session.begin_play_session(live_world)
	session._process(10.0)
	_check(session.delete_slot(1), "delete_slot failed")
	session.flush_playtime()
	_check(not session.slot_exists(1) and not DirAccess.dir_exists_absolute(absolute.path_join("slot_1")), "flush recreated deleted slot")
	live_world.free()

	# .tmp recovery.
	_check(session.create_world(2, "Recover"), "create slot 2 failed")
	var meta_path := TEST_ROOT + "/slot_2/world.json"
	DirAccess.rename_absolute(ProjectSettings.globalize_path(meta_path), ProjectSettings.globalize_path(meta_path + ".tmp"))
	_check(session.slot_exists(2), "slot_exists did not recover tmp")
	_check(str(session.get_slot_metadata(2).get("name", "")) == "Recover", "recovered metadata wrong")

	# New-world request.
	session.select_slot(2)
	session.request_load_selected_world()
	session.request_new_world({"preset": "standard", "terrain_seed": 4242, "auto_start": false})
	_check(not session.consume_load_selected_world_request(), "request_new_world did not clear load request")
	var request: Dictionary = session.consume_new_world_request()
	_check(int(request.get("terrain_seed", 0)) == 4242, "new world request lost")
	_check(session.consume_new_world_request().is_empty(), "new world request not consumed")

	# Presets.
	var standard: Dictionary = PRESETS.describe("standard")
	var compact: Dictionary = PRESETS.describe("compact")
	_check(is_equal_approx(float(standard.radius_km), 5000.0) and is_equal_approx(float(standard.width_km), 318.0), "standard preset numbers %s" % standard)
	_check(float(compact.radius_km) >= 80.0 and float(compact.radius_km) < 5000.0, "compact preset numbers %s" % compact)

	# Launcher applies a pending standard request (no auto start).
	session.request_new_world({"preset": "standard", "terrain_seed": 4242, "auto_start": false})
	var launcher_session := root.get_node_or_null("WorldSession")
	_check(launcher_session != null, "no autoload WorldSession")
	if launcher_session != null:
		launcher_session.request_new_world({"preset": "standard", "terrain_seed": 4242, "auto_start": false})
		var launcher := LAUNCHER.instantiate()
		root.add_child(launcher)
		await process_frame
		await process_frame
		var selector: OptionButton = launcher.get("terrain_backend_selector")
		_check(selector.selected == 2, "backend selector %d != 2" % selector.selected)
		_check(is_equal_approx(float(launcher.get("exact_radius_override_km")), 5000.0), "exact radius %s" % launcher.get("exact_radius_override_km"))
		var seed_spin: SpinBox = launcher.get("terrain_seed_spin")
		_check(int(seed_spin.value) == 4242, "seed spin %s" % seed_spin.value)
		_check(bool(launcher.call("_prepare_runtime_surface_generation", true)), "_prepare_runtime_surface_generation failed")
		launcher_session.consume_new_world_request()
		launcher.queue_free()

	session.queue_free()
	await process_frame
	_remove_tree(absolute)
	if failures == 0:
		print("WORLD_SESSION_METADATA=PASS")
	else:
		push_error("WORLD_SESSION_METADATA=FAIL failures=%d" % failures)
	quit(failures)

func _remove_tree(path: String) -> void:
	if not DirAccess.dir_exists_absolute(path):
		return
	var dir := DirAccess.open(path)
	for sub in dir.get_directories():
		_remove_tree(path.path_join(sub))
	for f in dir.get_files():
		DirAccess.remove_absolute(path.path_join(f))
	DirAccess.remove_absolute(path)
