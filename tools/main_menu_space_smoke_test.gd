extends SceneTree

const MENU := preload("res://scenes/StartMenu.tscn")
const SNAPSHOT := preload("res://scripts/ui/menu_snapshot.gd")
const FIXTURE_PNG := "res://tools/fixtures/menu_snapshot/menu_snapshot.png"
const FIXTURE_JSON := "res://tools/fixtures/menu_snapshot/menu_snapshot.json"
const SNAPSHOT_DIR := "res://.godot/menu_snapshot_smoke"
## The Live world drawer saves here (isolated user://), never to the project defaults.
const LIVE_SETTINGS := "user://generated/menu_live_world_smoke.tres"
var failed: bool = false
var watchdog: Timer

class FakeWorldSession extends Node:
	var mutation_calls: int = 0
	func slot_exists(slot: int) -> bool: return slot == 1
	func get_most_recent_slot() -> int: return 1
	# The fixture slot is never eligible for a warm world.
	func get_live_menu_slot() -> int: return 0
	func get_slot_metadata(_slot: int) -> Dictionary:
		return {"name": "Preview fixture", "generation_settings": {"terrain_seed": 1337, "radius_km": 5000, "ring_width_km": 318}}
	func select_slot(_slot: int) -> bool: mutation_calls += 1; return false
	func copy_slot(_source: int, _target: int) -> bool: mutation_calls += 1; return false
	func delete_slot(_slot: int) -> bool: mutation_calls += 1; return false
	func request_load_selected_world() -> bool: mutation_calls += 1; return false
	func rename_slot(_slot: int, _name: String) -> bool: mutation_calls += 1; return false
	func create_world(_slot: int, _name: String) -> bool: mutation_calls += 1; return false
	func request_new_world(_request: Dictionary) -> void: mutation_calls += 1
	func get_slot_thumbnail_path(_slot: int) -> String: return ""
	func get_menu_snapshot_paths(_target: int) -> Dictionary:
		return {"png": SNAPSHOT_DIR + "/menu_snapshot.png", "json": SNAPSHOT_DIR + "/menu_snapshot.json"}
	func read_world_state_stamp(_directory: String) -> Dictionary:
		return {"saved_unix": 100, "around_m": 5.0, "across_m": 6.0}
	func begin_play_session(_world: Node) -> void: pass
	func end_play_session() -> void: pass

func _initialize() -> void:
	watchdog = Timer.new()
	watchdog.one_shot = true
	watchdog.wait_time = 240.0
	watchdog.timeout.connect(func() -> void: _fail("watchdog timeout"))
	call_deferred("_run")

func _run() -> void:
	root.add_child(watchdog)
	watchdog.start()
	var max_fps_before: int = Engine.max_fps
	var quality_keys := ["rendering/anti_aliasing/quality/screen_space_aa", "rendering/environment/screen_space_reflection/half_size", "rendering/lights_and_shadows/directional_shadow/size"]
	var quality_before: Array = []
	for key: String in quality_keys:
		quality_before.append(ProjectSettings.get_setting(key))
	var original_session: Node = root.get_node_or_null("WorldSession")
	if original_session != null:
		root.remove_child(original_session)
	var fake_session := FakeWorldSession.new()
	fake_session.name = "WorldSession"
	root.add_child(fake_session)
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(LIVE_SETTINGS).get_base_dir())
	DirAccess.remove_absolute(ProjectSettings.globalize_path(LIVE_SETTINGS))
	var menu: Control = MENU.instantiate()
	menu.live_world_settings_path = LIVE_SETTINGS
	# The pinned cover never warm-loads a world and never drifts: deterministic, headless-safe.
	menu.freeze_cover = true
	root.add_child(menu)
	_require(menu.main_panel.visible, "main panel is not visible")
	_require(menu.continue_button != null and menu.continue_button.visible and menu.continue_subline.text.begins_with("Preview fixture"), "continue button missing for the most recent slot")
	_require(menu.continue_button.has_focus(), "continue button did not take initial focus")
	var enter_button: Button = _find_button(menu, "ENTER RINGWORLD")
	_require(enter_button != null, "missing enter button")
	if enter_button == null:
		return
	enter_button.pressed.emit()
	_require(menu.slot_buttons.size() == 3, "expected three slot buttons")
	if menu.slot_buttons.size() != 3:
		return
	_require(menu.worlds_panel.visible and not menu.main_panel.visible, "worlds panel not visible")
	_require(menu.slot_buttons[0].has_focus(), "most recent card header did not take focus")
	_require(menu.slot_buttons[0].get_node(menu.slot_buttons[0].focus_neighbor_bottom) == menu.slot_buttons[1], "card focus neighbor does not reach the next card")
	menu.slot_buttons[1].pressed.emit()
	_require(menu.new_world_panel.visible and not menu.worlds_panel.visible, "empty slot did not open the new world screen")
	var first_seed: String = menu.new_world_seed_edit.text
	_require(not first_seed.is_empty() and first_seed.is_valid_int(), "new world seed missing")
	menu.call("_reroll_seed")
	_require(menu.new_world_seed_edit.text != first_seed, "reroll did not change the seed")
	_cancel(menu)
	_require(menu.worlds_panel.visible and not menu.new_world_panel.visible, "cancel did not return from new world to worlds")
	menu.slot_buttons[0].pressed.emit()
	_require(menu.world_actions_panel.visible, "world actions not visible")
	_require(menu.world_actions_panel.get_parent() == menu.slot_buttons[0].get_parent(), "actions are not inside the selected card")
	var duplicate_button: Button = _find_button(menu, "DUPLICATE WORLD")
	_require(duplicate_button != null, "missing duplicate button")
	if duplicate_button == null:
		return
	duplicate_button.pressed.emit()
	_require(menu.copy_targets.visible and menu.copy_targets.get_child_count() == 2, "copy targets missing")
	_cancel(menu)
	_require(not menu.copy_targets.visible and menu.world_actions_panel.visible, "cancel did not close copy targets only")
	_find_button(menu, "RENAME").pressed.emit()
	_require(menu.rename_row.visible, "rename row not shown")
	_cancel(menu)
	_require(not menu.rename_row.visible, "cancel did not hide the rename row")
	_find_button(menu, "DELETE WORLD").pressed.emit()
	_require(menu.delete_modal.visible and menu.delete_cancel_button.has_focus(), "delete modal not shown with cancel focused")
	menu.call("_on_delete_hold_down")
	menu.call("_process", 0.75)
	_require(absf(menu.delete_hold_fill.scale.x - 0.5) < 0.01, "hold fill did not advance")
	menu.call("_on_delete_hold_up")
	_require(menu.delete_hold_fill.scale.x < 0.001, "releasing did not reset the hold")
	_cancel(menu)
	_require(not menu.delete_modal.visible, "cancel did not close the delete modal")
	_cancel(menu)
	_require(menu.worlds_panel.visible and not menu.world_actions_panel.visible, "cancel did not collapse the card")
	_cancel(menu)
	_require(menu.main_panel.visible and not menu.worlds_panel.visible, "cancel did not return to main")
	menu.call("_show_main_menu")
	_require(fake_session.mutation_calls == 0, "navigation mutated a world")

	# --- starfield cover: no snapshot, no world
	_require(menu.boot_cover != null and menu.boot_cover.mode == "starfield", "no-snapshot menu did not show the starfield cover")
	_require(menu.get_node_or_null("SpaceBackdrop") == null and menu.get_node_or_null("MenuTuningDrawer") == null, "menu still builds the flyover or its drawer")
	_require(menu.get("_footer_label").text == "" and menu.get("_survey_label").text == "", "starfield-only menu shows footer or survey text")
	var starfield: ColorRect = menu.boot_cover.get_node_or_null("Starfield") as ColorRect
	_require(starfield != null and starfield.material is ShaderMaterial and bool((starfield.material as ShaderMaterial).get_shader_parameter("frozen")), "starfield is not pinned by freeze_cover")
	_require(menu.boot_cover.get_index() < menu.get("_ui_root").get_index(), "cover is not below the menu UI")
	_require(not _contains_runtime(menu), "menu created world runtime")

	# --- Live world drawer: grave opens only it, with four controls; values persist across a reload; settings reach their consumers
	var live_drawer: Control = menu.live_drawer
	_require(live_drawer != null and menu.get_node_or_null("MenuTuningDrawer") == null and menu.get_node_or_null("LiveTuningDrawer") == null, "menu lacks the Live world drawer, or built another drawer")
	_require(not bool(live_drawer.get("IsDrawerOpen")), "Live world drawer starts open")
	_require(is_equal_approx(live_drawer.call("GetValue", "CruiseAltitudeKm"), 16.0) and is_equal_approx(live_drawer.call("GetValue", "CruisePitchDegrees"), -25.0) \
		and is_equal_approx(live_drawer.call("GetValue", "CrossfadeSeconds"), 2.5) and is_equal_approx(live_drawer.call("GetValue", "SnapshotDriftPercent"), 8.0), "Live world defaults are not 16 km / -25 deg / 2.5 s / 8%")
	_push_key(KEY_QUOTELEFT)
	_require(bool(live_drawer.get("IsDrawerOpen")), "grave did not open the Live world drawer")
	_require(Input.mouse_mode == Input.MOUSE_MODE_VISIBLE, "the drawer hid the mouse")
	var controls: Array = live_drawer.find_children("*", "SpinBox", true, false)
	var control_names: Array = []
	for control: Node in controls:
		control_names.append(String(control.name))
	control_names.sort()
	_require(control_names == ["CrossfadeSeconds", "CruiseAltitudeKm", "CruisePitchDegrees", "SnapshotDriftPercent"] and (live_drawer.call("GetDrawerDiagnostics") as Dictionary)["control_count"] == 4, "Live world drawer does not have exactly the four expected controls: " + str(control_names))
	_require(menu.main_panel.visible and menu.continue_button.focus_mode != Control.FOCUS_NONE, "opening the drawer disturbed the menu buttons")
	_push_key(KEY_ESCAPE)
	_require(not bool(live_drawer.get("IsDrawerOpen")), "Escape did not close the Live world drawer")
	_push_key(KEY_QUOTELEFT)
	_require(bool(live_drawer.get("IsDrawerOpen")), "grave did not reopen the drawer")
	live_drawer.call("SetTuningValue", "CrossfadeSeconds", 4.0)
	live_drawer.call("SetTuningValue", "SnapshotDriftPercent", 12.0)
	live_drawer.call("SetTuningValue", "CruiseAltitudeKm", 99.0)
	live_drawer.call("SetTuningValue", "CruisePitchDegrees", -40.0)
	_require(is_equal_approx(live_drawer.call("GetValue", "CruiseAltitudeKm"), 18.0), "cruise altitude exceeded the 18 km ceiling")
	_require(is_equal_approx(menu.call("_crossfade_seconds"), 4.0), "crossfade change did not reach the menu's crossfade")
	var cruise: Dictionary = menu.call("_live_cruise")
	_require(is_equal_approx(cruise["altitude_m"], 18000.0) and is_equal_approx(cruise["pitch_deg"], -40.0), "cruise change did not reach the warm-world start arguments: " + str(cruise))
	_push_key(KEY_QUOTELEFT)
	_require(not bool(live_drawer.get("IsDrawerOpen")), "second grave did not close the drawer")
	var saved: Dictionary = live_drawer.call("GetDrawerDiagnostics")
	_require(not bool(saved["unsaved"]) and int(saved["save_successes"]) >= 1 and FileAccess.file_exists(LIVE_SETTINGS), "closing the drawer did not persist the settings")

	# --- fixture snapshot cover (no world)
	var snapshot_menu: Control = MENU.instantiate()
	snapshot_menu.freeze_cover = true
	snapshot_menu.snapshot_override_path = FIXTURE_PNG
	# Reloads the settings the first menu saved.
	snapshot_menu.live_world_settings_path = LIVE_SETTINGS
	root.add_child(snapshot_menu)
	var cover: Control = snapshot_menu.boot_cover
	_require(cover.mode == "snapshot", "fixture snapshot was not shown")
	_require(snapshot_menu.get("_footer_label").text.begins_with("LAST SESSION"), "snapshot footer is not LAST SESSION: " + snapshot_menu.get("_footer_label").text)
	var picture: TextureRect = cover.get_node_or_null("SnapshotPicture") as TextureRect
	_require(picture != null and picture.expand_mode == TextureRect.EXPAND_IGNORE_SIZE and picture.stretch_mode == TextureRect.STRETCH_KEEP_ASPECT_COVERED, "snapshot picture is not drawn aspect-covered")
	_require(picture != null and picture.texture.get_width() == 640 and picture.texture.get_height() == 360, "fixture snapshot texture size mismatch")
	_require(is_equal_approx(snapshot_menu.live_drawer.call("GetValue", "CrossfadeSeconds"), 4.0) and is_equal_approx(snapshot_menu.live_drawer.call("GetValue", "CruiseAltitudeKm"), 18.0) and is_equal_approx(snapshot_menu.live_drawer.call("GetValue", "CruisePitchDegrees"), -40.0), "Live world settings did not persist across a reload")
	await process_frame
	_require(picture != null and absf(picture.scale.x - 1.12) < 0.001, "persisted 12%% drift does not start at 1.12x: %s" % str(picture.scale if picture != null else 0))
	_require(picture != null and picture.position.x < 0.0 and picture.position.y > 0.0, "drift start offset is not left/down")
	snapshot_menu.live_drawer.call("SetTuningValue", "SnapshotDriftPercent", 0.0)
	await process_frame
	_require(picture != null and absf(picture.scale.x - 1.0) < 0.001 and picture.position.length() < 0.5, "drift change did not reach the cover")
	snapshot_menu.live_drawer.call("SetTuningValue", "SnapshotDriftPercent", 12.0)
	await process_frame
	await cover.settle()
	_require(picture != null and absf(picture.scale.x - 1.0) < 0.002 and picture.position.length() < 1.0, "settle did not converge to identity")
	await cover.crossfade(0.1)
	_require(cover.modulate.a < 0.01, "crossfade did not reach zero alpha")
	cover.retire()
	_require(cover.is_retired and not cover.visible, "retire did not hide the cover")
	snapshot_menu.queue_free()
	# A sidecar from another render epoch is ignored (starfield).
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(SNAPSHOT_DIR))
	var bad_png := SNAPSHOT_DIR + "/bad_epoch.png"
	DirAccess.copy_absolute(ProjectSettings.globalize_path(FIXTURE_PNG), ProjectSettings.globalize_path(bad_png))
	var bad_sidecar: Dictionary = JSON.parse_string(FileAccess.get_file_as_string(FIXTURE_JSON))
	bad_sidecar["render_epoch"] = 999
	var bad_file := FileAccess.open(bad_png.get_basename() + ".json", FileAccess.WRITE)
	bad_file.store_string(JSON.stringify(bad_sidecar))
	bad_file.close()
	_require(SNAPSHOT.load_files(bad_png, bad_png.get_basename() + ".json").is_empty(), "a snapshot from another render epoch was accepted")
	_require(SNAPSHOT.load_files(SNAPSHOT_DIR + "/missing.png", SNAPSHOT_DIR + "/missing.json").is_empty(), "a missing snapshot was accepted")
	var broken := FileAccess.open(SNAPSHOT_DIR + "/broken.json", FileAccess.WRITE)
	broken.store_string("{ not json")
	broken.close()
	DirAccess.copy_absolute(ProjectSettings.globalize_path(FIXTURE_PNG), ProjectSettings.globalize_path(SNAPSHOT_DIR + "/broken.png"))
	_require(SNAPSHOT.load_files(SNAPSHOT_DIR + "/broken.png", SNAPSHOT_DIR + "/broken.json").is_empty(), "an unparsable sidecar was accepted")

	# --- write_async -> load_valid round trip, stamp match and stale detection (fake session supplies paths and the stamp)
	var big := Image.create_empty(100, 2000, false, Image.FORMAT_RGBA8)
	big.fill(Color(0.2, 0.4, 0.6, 1.0))
	var sidecar: Dictionary = SNAPSHOT.make_sidecar("last_session", {"saved_unix": 100, "around_m": 5.0, "across_m": 6.0}, {"fov_deg": 75.0}, 30.0, 20000.0)
	var task: int = SNAPSHOT.write_async(1, big, sidecar)
	_require(task >= 0, "write_async refused a valid image")
	SNAPSHOT.wait_pending()
	var written: Dictionary = SNAPSHOT.load_valid(1)
	_require(not written.is_empty() and not bool(written.get("stale", true)), "written snapshot is not valid and current")
	if not written.is_empty():
		_require((written["texture"] as Texture2D).get_height() == 1080, "snapshot was not downscaled to 1080 px tall")
		_require(int((written["sidecar"] as Dictionary).get("format_version", 0)) == SNAPSHOT.FORMAT_VERSION and (written["sidecar"] as Dictionary).has("image_size"), "sidecar misses format fields")
	_require(not FileAccess.file_exists(SNAPSHOT_DIR + "/menu_snapshot.png.tmp") and not FileAccess.file_exists(SNAPSHOT_DIR + "/menu_snapshot.json.tmp"), "temporary snapshot files left behind")
	var stale_sidecar: Dictionary = SNAPSHOT.make_sidecar("last_session", {"saved_unix": 99, "around_m": 5.0, "across_m": 6.0}, {}, 0.0, 0.0)
	SNAPSHOT.write_async(1, big, stale_sidecar)
	SNAPSHOT.wait_pending()
	var stale: Dictionary = SNAPSHOT.load_valid(1)
	_require(not stale.is_empty() and bool(stale.get("stale", false)), "a snapshot of an older save was not reported stale (and still returned)")
	_require(SNAPSHOT.capture_world_only(self, []) == null or DisplayServer.get_name() != "headless", "headless capture must be skipped")

	_require(fake_session.mutation_calls == 0, "menu modified the save fixture")
	menu.queue_free()
	DirAccess.remove_absolute(ProjectSettings.globalize_path(LIVE_SETTINGS))
	await process_frame
	root.remove_child(fake_session)
	fake_session.queue_free()
	if original_session != null:
		root.add_child(original_session)
	watchdog.stop()
	if failed:
		quit(1)
	else:
		print("MAIN_MENU_SPACE_SMOKE=PASS")
		quit(0)

func _contains_runtime(node: Node) -> bool:
	var script: Script = node.get_script()
	var path: String = script.resource_path if script != null else ""
	if node is PhysicsBody3D or path.contains("VoxelChunkGridManager") or path.contains("Explorer") or path.contains("RingSunshineCloudBridge") or path.contains("SunshineCloudsDriver") or path.contains("Vegetation") or path.contains("WorldPhysics"):
		return true
	for child: Node in node.get_children():
		if _contains_runtime(child):
			return true
	return false

## Sends a key press through the viewport input path (what the drawer listens to), then lets it settle.
func _push_key(code: int) -> void:
	var event := InputEventKey.new()
	event.keycode = code
	event.physical_keycode = code
	event.pressed = true
	root.push_input(event)

func _cancel(menu: Control) -> void:
	var event := InputEventAction.new()
	event.action = "ui_cancel"
	event.pressed = true
	menu.call("_unhandled_input", event)

func _find_button(node: Node, prefix: String) -> Button:
	for child: Node in node.find_children("*", "Button", true, false):
		if child.text.begins_with(prefix):
			return child
	return null


func _require(condition: bool, message: String) -> void:
	if not condition:
		_fail(message)

func _fail(message: String) -> void:
	if failed:
		return
	failed = true
	push_error("MAIN_MENU_SPACE_SMOKE=FAIL " + message)
	quit(1)
