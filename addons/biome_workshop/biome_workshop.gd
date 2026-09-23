@tool
extends EditorPlugin

const PREVIEW_SCENE := "res://scenes/BiomeAuthoringPreview.tscn"
const BIOME_DIR := "res://resources/biomes"
const META_SECTION := "biome_workshop"
const DRAFT_PATH := "res://.godot/biome_workshop_draft.tres"
var dock: Control
var controls: Control
var status: Label
var source_path := ""
var working: Resource
var dirty := false
var active_dialog: FileDialog
var draft_delay := -1.0
var smoke_mode := false
var preview_screen: Control
var preview_viewport: SubViewport
var preview_root: Node
var preview_container: SubViewportContainer

func _enter_tree() -> void:
	_create_preview_screen()
	dock = _build_dock()
	smoke_mode = "--biome-workshop-editor-smoke" in OS.get_cmdline_user_args()
	add_control_to_dock(DOCK_SLOT_RIGHT_UL, dock)
	if smoke_mode: _new_preset()
	else: _restore_session()
	if smoke_mode: call_deferred("_run_editor_smoke")

func _process(delta: float) -> void:
	if draft_delay >= 0.0:
		draft_delay -= delta
		if draft_delay <= 0.0: _save_draft()
	if preview_root and preview_screen and preview_screen.visible and preview_container.has_focus(): preview_root.call("EditorPreviewTick", delta)

func _exit_tree() -> void:
	if dirty and not smoke_mode: _save_draft()
	if active_dialog and is_instance_valid(active_dialog): active_dialog.queue_free()
	if dock:
		remove_control_from_docks(dock)
		dock.queue_free()
	if preview_root: preview_root.queue_free()
	if preview_screen: preview_screen.queue_free()

func _has_main_screen() -> bool: return true
func _get_plugin_name() -> String: return "Biome Preview"
func _make_visible(visible: bool) -> void:
	if preview_screen: preview_screen.visible = visible
	if visible:
		var was_initialized = preview_root != null and is_instance_valid(preview_root)
		_ensure_owned_preview()
		if not was_initialized: _apply_preview()
	if not visible and preview_root and preview_root.has_method("ReleasePreviewMouse"): preview_root.call("ReleasePreviewMouse")

func _create_preview_screen() -> void:
	preview_screen = Control.new(); preview_screen.name = "BiomePreviewMainScreen"; preview_screen.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT); preview_screen.size_flags_horizontal = Control.SIZE_EXPAND_FILL; preview_screen.size_flags_vertical = Control.SIZE_EXPAND_FILL
	var container = SubViewportContainer.new(); preview_container = container; container.name = "BiomePreviewViewport"; container.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT); container.stretch = true; container.focus_mode = Control.FOCUS_ALL; container.gui_input.connect(_preview_gui_input); container.focus_exited.connect(_release_preview_input); preview_screen.add_child(container)
	preview_viewport = SubViewport.new(); preview_viewport.own_world_3d = true; preview_viewport.size = Vector2i(1280, 720); container.add_child(preview_viewport)
	get_editor_interface().get_editor_main_screen().add_child(preview_screen)

func _ensure_owned_preview() -> void:
	if preview_root and is_instance_valid(preview_root): return
	var packed = load(PREVIEW_SCENE)
	if packed == null: _update_status("Preview scene is not available yet."); return
	preview_root = packed.instantiate()
	preview_viewport.add_child(preview_root)
	preview_root.call_deferred("FrameOverview")

func _preview_gui_input(event: InputEvent) -> void:
	if preview_root == null or not preview_screen.visible: return
	if event is InputEventMouseButton and event.button_index == MOUSE_BUTTON_RIGHT and event.pressed: preview_container.grab_focus()
	preview_root.call("EditorPreviewGuiInput", event)
	preview_container.accept_event()

func _release_preview_input() -> void:
	if preview_root and preview_root.has_method("ReleasePreviewMouse"): preview_root.call("ReleasePreviewMouse")

func _build_dock() -> Control:
	var outer := VBoxContainer.new()
	outer.name = "BiomeWorkshop"
	outer.custom_minimum_size = Vector2(290, 0)
	outer.size_flags_vertical = Control.SIZE_EXPAND_FILL
	var header = VBoxContainer.new(); outer.add_child(header)
	var title := Label.new(); title.text = "BIOME WORKSHOP"; title.theme_type_variation = "HeaderSmall"; header.add_child(title)
	var actions := GridContainer.new(); actions.columns = 2; header.add_child(actions)
	for entry in [["New", _new_preset], ["Duplicate", _duplicate], ["Open", _open_dialog], ["Save", _save], ["Save As", _save_as], ["Open 10km Preview", _open_preview], ["Overview", _frame_overview], ["Detail", _frame_detail]]:
		var button := Button.new(); button.text = entry[0]; button.pressed.connect(entry[1]); actions.add_child(button)
	status = Label.new(); status.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART; header.add_child(status)
	var scroll := ScrollContainer.new(); scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL; outer.add_child(scroll)
	var column := VBoxContainer.new()
	column.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	column.add_theme_constant_override("separation", 8)
	scroll.add_child(column)
	controls = preload("res://scripts/ui/BiomeAuthoringControls.cs").new()
	controls.connect("Changed", _mark_dirty)
	column.add_child(controls)
	return outer

func _configure() -> void:
	controls.call("Configure", working)
	_update_status()
	_apply_preview()

func _restore_session() -> void:
	if get_editor_interface().get_editor_settings().get_project_metadata(META_SECTION, "has_draft", false) and ResourceLoader.exists(DRAFT_PATH):
		var draft = ResourceLoader.load(DRAFT_PATH, "", ResourceLoader.CACHE_MODE_IGNORE)
		var expected = load("res://scripts/Biomes/BiomeAuthoringPreset.cs")
		if draft != null and draft.get_script() == expected:
			working = draft
			source_path = get_editor_interface().get_editor_settings().get_project_metadata(META_SECTION, "draft_source_path", "")
			dirty = true
			_configure()
			_update_status("Recovered unsaved editor draft." if draft.call("ValidateForUi") else "Recovered invalid draft: " + _validation_message(draft))
			return
	var last_path = get_editor_interface().get_editor_settings().get_project_metadata(META_SECTION, "last_preset_path", "")
	if last_path is String and not last_path.is_empty() and ResourceLoader.exists(last_path):
		_load_path(last_path)
	else:
		_new_preset()

func _remember_session() -> void:
	if not source_path.is_empty(): get_editor_interface().get_editor_settings().set_project_metadata(META_SECTION, "last_preset_path", source_path)

func _save_draft() -> void:
	if working == null or smoke_mode: return
	draft_delay = -1.0
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path("res://.godot"))
	if ResourceSaver.save(working, DRAFT_PATH) == OK:
		var settings = get_editor_interface().get_editor_settings()
		settings.set_project_metadata(META_SECTION, "has_draft", true)
		settings.set_project_metadata(META_SECTION, "draft_source_path", source_path)

func _clear_draft() -> void:
	get_editor_interface().get_editor_settings().set_project_metadata(META_SECTION, "has_draft", false)

func _mark_dirty() -> void:
	dirty = true
	if not smoke_mode: draft_delay = .5
	_update_status()
	_apply_preview()

func _update_status(message := "") -> void:
	if not status: return
	var where := source_path if not source_path.is_empty() else "Unsaved working copy"
	status.text = ("Unsaved changes — " if dirty else "Saved — ") + where + ("\n" + message if not message.is_empty() else "")

func _confirm_discard(next: Callable) -> void:
	if not dirty: next.call(); return
	var dialog := ConfirmationDialog.new(); dialog.title = "Discard unsaved biome changes?"; dialog.dialog_text = "Save your preset before creating or opening another one."; dialog.confirmed.connect(func(): dialog.queue_free(); next.call()); dialog.canceled.connect(func(): dialog.queue_free()); dialog.close_requested.connect(func(): dialog.queue_free()); dock.add_child(dialog); dialog.popup_centered()

func _new_preset() -> void:
	_confirm_discard(func():
		var script := load("res://scripts/Biomes/BiomeAuthoringPreset.cs")
		if script:
			working = script.new()
			working.call("InitializeNewIdentity")
		else:
			_update_status("Biome preset script is not available yet.")
			return
		source_path = ""; dirty = true; _save_draft(); _configure())

func _duplicate() -> void:
	if not working: return
	var duplicate = working.call("CreateDuplicateWithNewIdentity")
	if duplicate: working = duplicate
	else:
		_update_status("Could not duplicate biome preset with a new identity.")
		return
	source_path = ""; dirty = true; _save_draft(); _configure()

func _open_dialog() -> void:
	_confirm_discard(func(): _show_file_dialog(FileDialog.FILE_MODE_OPEN_FILE, _load_path))

func _save_as() -> void: _show_file_dialog(FileDialog.FILE_MODE_SAVE_FILE, _save_path)
func _save() -> void:
	if source_path.is_empty(): _save_as()
	else: _save_path(source_path)

func _show_file_dialog(mode, done: Callable) -> void:
	if active_dialog and is_instance_valid(active_dialog): active_dialog.queue_free()
	var dialog := FileDialog.new(); active_dialog = dialog; dialog.file_mode = mode; dialog.access = FileDialog.ACCESS_RESOURCES; dialog.current_dir = BIOME_DIR; dialog.add_filter("*.tres ; Biome preset"); dialog.file_selected.connect(func(path): active_dialog = null; done.call(path); dialog.queue_free()); dialog.canceled.connect(func(): active_dialog = null; dialog.queue_free()); dialog.close_requested.connect(func(): active_dialog = null; dialog.queue_free()); dock.add_child(dialog); dialog.popup_centered_ratio(.7)

func _load_path(path: String) -> void:
	var loaded = ResourceLoader.load(path, "", ResourceLoader.CACHE_MODE_IGNORE)
	if loaded == null: _update_status("Could not open preset."); return
	var expected = load("res://scripts/Biomes/BiomeAuthoringPreset.cs")
	if loaded.get_script() != expected: _update_status("Selected resource is not a BiomeAuthoringPreset."); return
	if not loaded.call("ValidateForUi"): _update_status("Preset is invalid: " + _validation_message(loaded)); return
	working = loaded.duplicate(true); source_path = path; dirty = false; _clear_draft(); _remember_session(); _configure()

func _save_path(path: String) -> void:
	if not path.ends_with(".tres"): path += ".tres"
	var result = working.call("SavePreset", path)
	if result != OK: _update_status("Save failed. Check the preset validation message."); return
	source_path = path; dirty = false; _clear_draft(); _remember_session(); _update_status("Saved explicitly. World assignment remains a separate stage.")

func _open_preview() -> void:
	_ensure_owned_preview()
	get_editor_interface().set_main_screen_editor("Biome Preview")
	_show_preview_editor_context()
	_apply_preview()

func _show_preview_editor_context() -> void:
	get_editor_interface().set_main_screen_editor("Biome Preview")
	var cursor: Node = dock
	while cursor != null:
		if cursor is TabContainer:
			for index in cursor.get_tab_count():
				var tab = cursor.get_tab_control(index)
				if tab == dock or tab.is_ancestor_of(dock):
					cursor.current_tab = index
					return
		cursor = cursor.get_parent()

func _prepare_preview(root: Node) -> void:
	# Keep the authoring builder active while its scene is open in the editor.
	root.process_mode = Node.PROCESS_MODE_ALWAYS
	root.set_process(true)

func _apply_preview() -> void:
	if not working: return
	var root = preview_root
	if _is_preview_root(root):
		_prepare_preview(root)
		if not working.call("ValidateForUi"):
			_update_status("Preview retains the last valid build: " + _validation_message(working))
			return
		root.call("SetPreset", working)

func _validation_message(preset: Resource) -> String:
	return str(preset.call("GetValidationError")) if preset.has_method("GetValidationError") else "validation failed"

func _frame_overview() -> void: _frame_preview("GetOverviewCameraTransform")
func _frame_detail() -> void: _frame_preview("GetDetailCameraTransform")
func _frame_preview(transform_method: String) -> void:
	if not _is_preview_root(preview_root): _open_preview()
	else: _show_preview_editor_context()
	var root = preview_root
	if not _is_preview_root(root):
		_update_status("Open the 10 km preview before framing it.")
		return
	if transform_method == "GetOverviewCameraTransform": root.call("FrameOverview")
	else: root.call("FrameDetail")

func _run_editor_smoke() -> void:
	# Kept entirely under .godot: it verifies real editor-plugin construction without touching authored assets.
	var test_dir := "res://.godot/biome_workshop_tests"
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(test_dir))
	var preset_script := load("res://scripts/Biomes/BiomeAuthoringPreset.cs")
	if dock == null or preset_script == null:
		push_error("BIOME_WORKSHOP_EDITOR=FAIL dock or preset script missing")
		get_editor_interface().get_base_control().get_tree().quit(1)
		return
	var fresh: Resource = preset_script.new()
	fresh.call("InitializeNewIdentity")
	fresh.set("DisplayName", "Editor smoke")
	var copy = fresh.call("DuplicateWithNewIdentity")
	if copy == null:
		push_error("BIOME_WORKSHOP_EDITOR=FAIL duplicate unavailable")
		get_editor_interface().get_base_control().get_tree().quit(1)
		return
	working = copy
	controls.call("Configure", working)
	var material = controls.find_child("GroundMaterial", true, false)
	if material == null or not material is OptionButton:
		push_error("BIOME_WORKSHOP_EDITOR=FAIL ground material control missing")
		get_editor_interface().get_base_control().get_tree().quit(1)
		return
	material.emit_signal("item_selected", 1)
	if working.get("GroundMaterial") != 1:
		push_error("BIOME_WORKSHOP_EDITOR=FAIL ground material binding")
		get_editor_interface().get_base_control().get_tree().quit(1)
		return
	var path := test_dir.path_join("biome_workshop_editor_smoke.tres")
	var result = copy.call("SavePreset", path)
	var reopened = ResourceLoader.load(path)
	if result != OK or reopened == null or not reopened.call("ValidateForUi"):
		push_error("BIOME_WORKSHOP_EDITOR=FAIL save/reopen validation")
		get_editor_interface().get_base_control().get_tree().quit(1)
		return
	var startup_deadline = Time.get_ticks_msec() + 30000
	var filesystem = get_editor_interface().get_resource_filesystem()
	while filesystem != null and filesystem.is_scanning() and Time.get_ticks_msec() < startup_deadline:
		await get_tree().process_frame
	_open_preview()
	for frame in 3: await get_tree().process_frame
	if not preview_screen.visible or not preview_container.visible or preview_container.size.x <= 100 or preview_container.size.y <= 100:
		push_error("BIOME_WORKSHOP_EDITOR=FAIL owned preview view is not visible/sized")
		get_editor_interface().get_base_control().get_tree().quit(1)
		return
	var deadline = Time.get_ticks_msec() + 30000
	var next_open_retry = Time.get_ticks_msec() + 500
	var root: Node = null
	while Time.get_ticks_msec() < deadline:
		await get_tree().process_frame
		root = preview_root
		if _is_preview_root(root):
			_prepare_preview(root)
			break
		if Time.get_ticks_msec() >= next_open_retry:
			_open_preview()
			next_open_retry = Time.get_ticks_msec() + 500
	if not _is_preview_root(root):
		push_error("BIOME_WORKSHOP_EDITOR=FAIL preview scene open " + _preview_diagnostics(root))
		get_editor_interface().get_base_control().get_tree().quit(1)
		return
	deadline = Time.get_ticks_msec() + 30000
	next_open_retry = Time.get_ticks_msec() + 500
	while Time.get_ticks_msec() < deadline:
		await get_tree().process_frame
		root = preview_root
		if _is_preview_root(root):
			_prepare_preview(root)
			if _preview_ready(root): break
		elif Time.get_ticks_msec() >= next_open_retry:
			_open_preview()
			next_open_retry = Time.get_ticks_msec() + 500
	if not _preview_ready(root):
		push_error("BIOME_WORKSHOP_EDITOR=FAIL initial preview build " + _preview_diagnostics(root))
		get_editor_interface().get_base_control().get_tree().quit(1)
		return
	_apply_preview()
	deadline = Time.get_ticks_msec() + 30000
	next_open_retry = Time.get_ticks_msec() + 500
	while Time.get_ticks_msec() < deadline:
		await get_tree().process_frame
		root = preview_root
		if _is_preview_root(root):
			_prepare_preview(root)
			if _preview_ready(root) and root.get("Preset").get("Identity") == working.get("Identity"): break
		elif Time.get_ticks_msec() >= next_open_retry:
			_open_preview()
			next_open_retry = Time.get_ticks_msec() + 500
	if not _preview_ready(root) or root.get("Preset").get("Identity") != working.get("Identity"):
		push_error("BIOME_WORKSHOP_EDITOR=FAIL final preview apply " + _preview_diagnostics(root))
		get_editor_interface().get_base_control().get_tree().quit(1)
		return
	# Startup layout restoration can hide a main screen after the preview has built; restore it before input checks.
	_show_preview_editor_context()
	deadline = Time.get_ticks_msec() + 5000
	while Time.get_ticks_msec() < deadline and (not preview_screen.visible or not preview_screen.is_visible_in_tree()):
		await get_tree().process_frame
		_show_preview_editor_context()
	if not preview_screen.visible or not preview_screen.is_visible_in_tree():
		push_error("BIOME_WORKSHOP_EDITOR=FAIL owned preview could not be restored after startup")
		get_editor_interface().get_base_control().get_tree().quit(1)
		return
	# Exercise the dock actions and the plugin-owned preview camera, isolated from the native editor cursor.
	var target_overview: Transform3D = root.call("GetOverviewCameraTransform")
	_frame_overview()
	for frame in 3: await get_tree().process_frame
	var preview_camera = root.get_node_or_null("Camera3D")
	if preview_camera == null or preview_camera.global_transform.origin.distance_to(target_overview.origin) > .25:
		push_error("BIOME_WORKSHOP_EDITOR=FAIL overview frame")
		get_editor_interface().get_base_control().get_tree().quit(1)
		return
	var target_detail: Transform3D = root.call("GetDetailCameraTransform")
	_frame_detail()
	for frame in 3: await get_tree().process_frame
	var frame_error: float = preview_camera.global_transform.origin.distance_to(target_detail.origin)
	if frame_error > .25:
		push_error("BIOME_WORKSHOP_EDITOR=FAIL detail frame reset error=%.3f" % frame_error)
		get_editor_interface().get_base_control().get_tree().quit(1)
		return
	if preview_camera.far < 20000.0:
		push_error("BIOME_WORKSHOP_EDITOR=FAIL preview camera far plane is below 20 km")
		get_editor_interface().get_base_control().get_tree().quit(1)
		return
	if not preview_screen.visible or not preview_screen.is_visible_in_tree() or not preview_container.is_visible_in_tree():
		push_error("BIOME_WORKSHOP_EDITOR=FAIL owned preview hid before navigation")
		get_editor_interface().get_base_control().get_tree().quit(1)
		return
	var before_drag = preview_camera.global_transform
	var press = InputEventMouseButton.new(); press.button_index = MOUSE_BUTTON_RIGHT; press.pressed = true; preview_container.emit_signal("gui_input", press)
	var motion = InputEventMouseMotion.new(); motion.relative = Vector2(12, 0); motion.button_mask = MOUSE_BUTTON_MASK_RIGHT; preview_container.emit_signal("gui_input", motion)
	var release = InputEventMouseButton.new(); release.button_index = MOUSE_BUTTON_RIGHT; release.pressed = false; preview_container.emit_signal("gui_input", release)
	for frame in 3: await get_tree().process_frame
	var after_drag = preview_camera.global_transform
	var orientation_delta: float = 1.0 - absf(before_drag.basis.z.dot(after_drag.basis.z))
	var position_drift: float = after_drag.origin.distance_to(target_detail.origin)
	if orientation_delta < .00001 or position_drift > 5.0:
		push_error("BIOME_WORKSHOP_EDITOR=FAIL preview RMB navigation orientation=%.6f drift=%.3f visible=%s in_tree=%s focus=%s nav=%s" % [orientation_delta, position_drift, preview_screen.visible, preview_screen.is_visible_in_tree(), preview_container.has_focus(), root.call("GetEditorNavigationDiagnostics")])
		get_editor_interface().get_base_control().get_tree().quit(1)
		return
	var before_fly = preview_camera.global_position
	var forward = InputEventKey.new(); forward.keycode = KEY_W; forward.pressed = true; _preview_gui_input(forward)
	for frame in 4: await get_tree().process_frame
	if preview_camera.global_position.distance_to(before_fly) <= .01:
		push_error("BIOME_WORKSHOP_EDITOR=FAIL preview keyboard fly did not move")
		get_editor_interface().get_base_control().get_tree().quit(1)
		return
	var name_entry = controls.find_child("DisplayName", true, false)
	if name_entry == null or not name_entry is LineEdit:
		push_error("BIOME_WORKSHOP_EDITOR=FAIL dock name field missing")
		get_editor_interface().get_base_control().get_tree().quit(1)
		return
	name_entry.grab_focus()
	var stopped_at = preview_camera.global_position
	for frame in 4: await get_tree().process_frame
	if preview_camera.global_position.distance_to(stopped_at) > .001:
		push_error("BIOME_WORKSHOP_EDITOR=FAIL dock text focus did not stop preview movement")
		get_editor_interface().get_base_control().get_tree().quit(1)
		return
	if "--biome-workshop-editor-capture" in OS.get_cmdline_user_args():
		DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path("res://artifacts"))
		for frame in 3: await RenderingServer.frame_post_draw
		get_editor_interface().get_base_control().get_viewport().get_texture().get_image().save_png(ProjectSettings.globalize_path("res://artifacts/biome-workshop-editor.png"))
	print("BIOME_WORKSHOP_EDITOR=PASS")
	get_editor_interface().get_base_control().get_tree().quit()

func _is_preview_root(root: Node) -> bool:
	return root != null and root == preview_root and root.has_method("SetPreset") and root.has_method("GetPreviewDiagnostics")

func _preview_diagnostics(root: Node) -> String:
	if root == null: return "root=<null>"
	var script_path: String = root.get_script().resource_path if root.get_script() else "<none>"
	var diagnostics = root.call("GetPreviewDiagnostics") if root.has_method("GetPreviewDiagnostics") else "<none>"
	return "scene=%s script=%s id=%s inside=%s processing=%s set=%s diagnostics=%s" % [root.scene_file_path, script_path, root.get_instance_id(), root.is_inside_tree(), root.is_processing(), root.has_method("SetPreset"), diagnostics]

func _preview_ready(root: Node) -> bool:
	if root == null or not root.has_method("GetPreviewDiagnostics"): return false
	var diagnostics = root.call("GetPreviewDiagnostics")
	return diagnostics is Dictionary and diagnostics.get("ready", false)
