extends Control

const GENERATOR_SCENE := "res://scenes/WorldSizeLauncher.tscn"
const UI_THEME := preload("res://scripts/ui/ring_ui_theme.gd")
const BOOT_COVER := preload("res://scripts/ui/menu_boot_cover.gd")
const MENU_SNAPSHOT := preload("res://scripts/ui/menu_snapshot.gd")
const WORLD_LOADING := preload("res://scripts/Loading/ring_world_loading.gd")
const MENU_MOTION := preload("res://scripts/ui/menu_motion.gd")
const WORLD_PRESETS := preload("res://scripts/world_presets.gd")
const CARD_THUMBNAIL := preload("res://scripts/ui/world_card_thumbnail.gd")
const WARM_WORLD := preload("res://scripts/Loading/ring_warm_world.gd")
const LIVE_WORLD_DRAWER := preload("res://scripts/ui/MenuLiveWorldTuningDrawer.cs")
const FLIGHT_DIRECTOR := preload("res://scripts/ui/RingMenuFlightDirector.cs")

const INTRO_FADE_SECONDS := 0.8
const INTRO_PANEL_DELAY_SECONDS := 0.35
const TELEMETRY_INTERVAL_SECONDS := 0.25
## Used only if the Live world drawer is unavailable; the real crossfade, drift and cruise values come from that drawer.
const FALLBACK_CROSSFADE_SECONDS := 2.5
const FALLBACK_DRIFT_PERCENT := 8.0
const FALLBACK_LOITER_RADIUS_KM := 30.0
const LEAVE_FADE_SECONDS := 0.2
## Continue: the menu UI fades out over this long, the fly-in and descent get CONTINUE_TIMEOUT_SECONDS in total, and past that the menu
## fades to black over CONTINUE_FALLBACK_FADE_SECONDS and the player is placed directly at the saved pose.
const CONTINUE_UI_FADE_SECONDS := 0.4
const CONTINUE_TIMEOUT_SECONDS := 60.0
const CONTINUE_FALLBACK_FADE_SECONDS := 0.3
## Underground saves: the fly-in stops this far above the surface over the save, looking this far down, before the fade cut.
const UNDERGROUND_STOP_ALTITUDE_M := 400.0
const UNDERGROUND_STOP_PITCH_DEGREES := -20.0
const FALLBACK_RUSH_SPEED_KM_PER_SECOND := 1.2
const FALLBACK_DESCENT_SECONDS := 12.0
## While the world warms, Continue / Enter Ringworld / the world cards stay disabled through the worst early freezes. They unlock once the
## loader is past the terrain stage and the last UNLOCK_QUIET_SECONDS had no frame over UNLOCK_SLOW_FRAME_MS, or after the safety cap.
const UNLOCK_QUIET_SECONDS := 1.0
const UNLOCK_SLOW_FRAME_MS := 100.0
const UNLOCK_CAP_SECONDS := 15.0
const DELETE_HOLD_SECONDS := 1.5
const SLOT_COUNT := 3
const MAX_WORLD_NAME_LENGTH := 32
const CARD_WIDTH := 720.0
const THUMBNAIL_SIZE := Vector2(224, 126)

## False shows the cover only and never warm-loads a world.
@export var live_world: bool = true
## Pins the cover (no warm-load, no drift or twinkle, never crossfaded or retired) for deterministic tests and captures.
@export var freeze_cover: bool = false
## A snapshot PNG (sidecar json beside it) to show as the cover without any world; tests only.
@export var snapshot_override_path: String = ""
## Tests only: redirects the Live world drawer's load and save (a user:// path).
@export var live_world_settings_path: String = ""

var main_panel: Control
var worlds_panel: Control
var new_world_panel: Control
var world_actions_panel: Control
var status_label: Label
var action_details: Label
var copy_targets: HBoxContainer
var rename_row: HBoxContainer
var rename_edit: LineEdit
var delete_button: Button
var delete_modal: Control
var delete_cancel_button: Button
var delete_hold_button: Button
var delete_hold_fill: ColorRect
var new_world_name_edit: LineEdit
var new_world_seed_edit: LineEdit
var slot_buttons: Array[Button] = []
var selected_slot := 0
var boot_cover: Control
## The grave-key "Live world" drawer: owns the crossfade, snapshot drift and warm-world cruise settings.
var live_drawer: Control
## Flies the warm world's camera (slow loiter around the save spot) once the cover is retired; null before that and without a live world.
var flight_director: Node
var _ui_root: Control
var _footer_label: Label
var worlds_button: Button
var continue_button: Button
var continue_subline: Label
## "Explore showcase" on the main menu (only while no saved world exists) and on the worlds screen (always).
var showcase_button: Button
var worlds_showcase_button: Button
var _quit_button: Button
var _continue_slot := 0
var _intro_overlay: ColorRect
var _survey_label: Label
var _survey_done := false
var _telemetry_active := false
var _telemetry_elapsed := 0.0
var _last_survey_percent := -1
var _intro_pending := true
var _cards: Array[Dictionary] = []
var _expanded_slot := 0
var _load_button: Button
var _copy_button: Button
var _rename_button: Button
var _details_button: Button
var _setup_button: Button
var _action_row: HFlowContainer
var _new_world_slot := 0
var _new_world_preset := "standard"
var _preset_buttons: Dictionary = {}
var _preset_line: Label
var _preset_blurb: Label
var _new_world_back: Button
var _new_world_create: Button
var _new_world_advanced: Button
var _new_world_reroll: Button
var _worlds_back: Button
var _delete_title: Label
var _delete_body: Label
var _delete_holding := false
var _delete_hold_elapsed := 0.0
var _delete_target_slot := 0
var _live_slot := 0
var _warm: Node
var _cover_retired := false
var _revealing := false
var _snapshot_valid := false
var _snapshot_stale := false
## True while the cover shows the bundled showcase picture (no eligible world to warm-load); no live world is behind it.
var _showcase_picture := false
var _placement_pose: Dictionary = {}
var _world_name := ""
var _leaving := false
## Continue was pressed before the world was ready: it shows "Preparing NN%" and flies in once the cover is retired and the camera loiters.
var _continue_pending := false
var _continuing := false
var _continue_press_msec := 0
## Tests only: shortens the Continue timeout to exercise the fallback.
var continue_timeout_seconds := CONTINUE_TIMEOUT_SECONDS
## Emitted with "arrived", "failed" or "timeout" when the Continue flight ends.
signal _continue_flight_done(outcome: String)
## Seconds from pressing Continue to the player having control (the hand-off finished); 0 until then. Tests read it.
var continue_report: Dictionary = {}
## True while the warm-load disables the main buttons. unlock_info holds {ms, reason} (ms since the menu was built) once released.
var menu_locked := false
var unlock_info: Dictionary = {}
var _ready_msec := 0
var _last_slow_frame_msec := 0


func _ready() -> void:
	if "--certification-benchmark" in OS.get_cmdline_user_args() or "--certified-smoke-test" in OS.get_cmdline_user_args() or "--certified-walk-precision-probe" in OS.get_cmdline_user_args() or "--capture-cavity-lighting" in OS.get_cmdline_user_args() or "--underground-mode-roundtrip-smoke-test" in OS.get_cmdline_user_args() or "--capture-cloud-transition" in OS.get_cmdline_user_args() or "--capture-certified-cloud-diagnostics" in OS.get_cmdline_user_args() or "--capture-certified-cloud-continuous-transition" in OS.get_cmdline_user_args() or "--capture-certified-cloud-continuous-raw-density" in OS.get_cmdline_user_args() or "--capture-certified-cloud-continuous-deep-night-along" in OS.get_cmdline_user_args() or "--capture-certified-cloud-focused-transition" in OS.get_cmdline_user_args() or "--capture-certified-cloud-focused-raw-density" in OS.get_cmdline_user_args() or "--capture-certified-cloud-focused-history-off" in OS.get_cmdline_user_args() or "--capture-certified-cloud-focused-sunshine-only" in OS.get_cmdline_user_args() or "--capture-certified-cloud-focused-far-only" in OS.get_cmdline_user_args() or "--lod-voxel-handoff-smoke-test" in OS.get_cmdline_user_args() or "--lod-hierarchy-handoff-smoke-test" in OS.get_cmdline_user_args() or "--lod-roof-coverage-smoke-test" in OS.get_cmdline_user_args() or "--smooth-physical-terrain-smoke-test" in OS.get_cmdline_user_args() or "--smooth-terrain-traversal-smoke-test" in OS.get_cmdline_user_args() or "--smooth-deep-shaft-smoke-test" in OS.get_cmdline_user_args() or "--capture-ground-horizon" in OS.get_cmdline_user_args() or "--capture-band-site" in OS.get_cmdline_user_args() or "--capture-band-site-smooth-only" in OS.get_cmdline_user_args() or "--capture-band-site-proxy-only" in OS.get_cmdline_user_args():
		get_tree().call_deferred("change_scene_to_file", GENERATOR_SCENE)
		return
	_live_slot = _eligible_live_slot()
	_build_ui()
	resized.connect(_update_ui_scale)
	_update_ui_scale()
	MENU_MOTION.style_buttons_in(_ui_root)
	_show_main_menu()
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	_ready_msec = Time.get_ticks_msec()
	_last_slow_frame_msec = _ready_msec
	# Deferred: when StartMenu is the main scene, root is still adding its children during _ready, so the warm-world and loader
	# autoload-style nodes cannot be added to root yet ("Parent node is busy setting up children").
	if _live_slot > 0:
		_set_menu_locked(true)
	_begin_live_world.call_deferred()


func _process(delta: float) -> void:
	if delta * 1000.0 > UNLOCK_SLOW_FRAME_MS:
		_last_slow_frame_msec = Time.get_ticks_msec()
	if menu_locked:
		_update_menu_lock()
	if _delete_holding and delete_modal != null and delete_modal.visible:
		_delete_hold_elapsed += delta
		delete_hold_fill.scale.x = clampf(_delete_hold_elapsed / DELETE_HOLD_SECONDS, 0.0, 1.0)
		if _delete_hold_elapsed >= DELETE_HOLD_SECONDS:
			_confirm_delete()
	if _telemetry_active:
		_telemetry_elapsed += delta
		if _telemetry_elapsed >= TELEMETRY_INTERVAL_SECONDS:
			_telemetry_elapsed = 0.0
			_update_telemetry()
	if _continue_pending and is_instance_valid(_warm):
		continue_subline.text = "Preparing %d%%" % int(floor(_warm.get_progress() * 100.0))
	if _survey_done or _survey_label == null or not is_instance_valid(_warm):
		return
	var percent := int(floor(_warm.get_progress() * 100.0))
	if percent != _last_survey_percent:
		_last_survey_percent = percent
		_survey_label.text = "PREPARING %d%%" % percent


func _update_telemetry() -> void:
	# The held world's Explorer is the only source: there is no flyover camera any more.
	var telemetry: Dictionary = _warm.get_telemetry() if is_instance_valid(_warm) else {}
	if bool(telemetry.get("valid", false)):
		_survey_label.text = "ALT %.1f KM · %.1f KM/S · SUN %+.1f°" % [
			float(telemetry.get("altitude_m", 0.0)) / 1000.0,
			float(telemetry.get("speed_mps", 0.0)) / 1000.0,
			float(telemetry.get("sun_elevation_degrees", 0.0)),
		]


func _unhandled_input(event: InputEvent) -> void:
	if not event.is_action_pressed("ui_cancel"):
		return
	if delete_modal != null and delete_modal.visible:
		_close_delete_modal()
	elif rename_row.visible and world_actions_panel.visible:
		_hide_rename()
	elif copy_targets.visible and world_actions_panel.visible:
		copy_targets.visible = false
		_update_world_focus_chain()
		_focus_expanded_default()
	elif new_world_panel.visible:
		_show_worlds()
	elif worlds_panel.visible and _expanded_slot != 0:
		var slot := _expanded_slot
		_collapse_cards()
		slot_buttons[slot - 1].grab_focus()
	elif worlds_panel.visible:
		_show_main_menu()
	else:
		return
	get_viewport().set_input_as_handled()


func _build_ui() -> void:
	set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	theme = UI_THEME.create_theme()
	_build_live_world_drawer()
	_build_cover()
	_ui_root = Control.new()
	_ui_root.name = "MenuDesignCanvas"
	_ui_root.size = Vector2(1920, 1080)
	add_child(_ui_root)
	var gradient := Gradient.new()
	gradient.offsets = PackedFloat32Array([0.0, 0.32, 0.7])
	gradient.colors = PackedColorArray([Color(0, 0, 0, 0.72), Color(0, 0, 0, 0.5), Color(0, 0, 0, 0)])
	var overlay := TextureRect.new()
	overlay.texture = GradientTexture2D.new()
	overlay.texture.gradient = gradient
	overlay.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	overlay.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(overlay)
	move_child(overlay, get_child_count() - 2)
	# Bottom strip keeps the monospace footer readable over bright terrain.
	var footer_gradient := Gradient.new()
	footer_gradient.offsets = PackedFloat32Array([0.0, 1.0])
	footer_gradient.colors = PackedColorArray([Color(0, 0, 0, 0), Color(0, 0, 0, 0.45)])
	var footer_texture := GradientTexture2D.new()
	footer_texture.gradient = footer_gradient
	footer_texture.fill_from = Vector2(0, 0)
	footer_texture.fill_to = Vector2(0, 1)
	var footer_strip := TextureRect.new()
	footer_strip.texture = footer_texture
	footer_strip.stretch_mode = TextureRect.STRETCH_SCALE
	footer_strip.set_anchors_preset(Control.PRESET_BOTTOM_WIDE)
	footer_strip.anchor_top = 0.88
	footer_strip.offset_top = 0.0
	footer_strip.offset_bottom = 0.0
	footer_strip.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(footer_strip)
	move_child(footer_strip, get_child_count() - 2)
	# Black reveal sits above the backdrop and readability gradient, below the UI.
	_intro_overlay = ColorRect.new()
	_intro_overlay.name = "IntroReveal"
	_intro_overlay.color = Color.BLACK
	_intro_overlay.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	_intro_overlay.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(_intro_overlay)
	move_child(_intro_overlay, _ui_root.get_index())
	var reveal := create_tween()
	reveal.tween_property(_intro_overlay, "color:a", 0.0, INTRO_FADE_SECONDS)
	reveal.tween_callback(_intro_overlay.hide)
	_build_screen_chrome()
	_build_main_panel()
	_build_worlds_panel()
	_build_world_actions()
	_build_new_world_panel()

	_build_delete_modal()
	# Last child: above the cover and the menu UI.
	move_child(live_drawer, -1)


## The small grave-key drawer for the live world. Its settings are read live by the cover (drift), the crossfade calls and the warm start (cruise).
func _build_live_world_drawer() -> void:
	live_drawer = LIVE_WORLD_DRAWER.new()
	live_drawer.name = "LiveWorldDrawer"
	live_drawer.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	if not live_world_settings_path.is_empty():
		live_drawer.set("SavePathOverride", live_world_settings_path)
	add_child(live_drawer)
	live_drawer.connect("SettingsChanged", _apply_live_world_settings)


func _live_value(property: String, fallback: float) -> float:
	if live_drawer == null or not is_instance_valid(live_drawer):
		return fallback
	return float(live_drawer.call("GetValue", property))


func _crossfade_seconds() -> float:
	return _live_value("CrossfadeSeconds", FALLBACK_CROSSFADE_SECONDS)


## Cruise placement for the warm-world loader ({altitude_m, pitch_deg}); empty falls back to the loader defaults.
func _live_cruise() -> Dictionary:
	if live_drawer == null or not is_instance_valid(live_drawer):
		return {}
	return live_drawer.call("GetCruise")


func _apply_live_world_settings() -> void:
	if boot_cover != null:
		boot_cover.drift_amount = _live_value("SnapshotDriftPercent", FALLBACK_DRIFT_PERCENT) / 100.0


## The boot cover sits where the flyover backdrop did: below the readability gradients and the UI, above the 3D world. It shows the last
## session's snapshot (or `snapshot_override_path`), else a starfield. A stale snapshot (the save changed since) is still shown.
func _build_cover() -> void:
	boot_cover = BOOT_COVER.new()
	boot_cover.frozen = freeze_cover
	_apply_live_world_settings()
	add_child(boot_cover)
	var snapshot: Dictionary = {}
	if not snapshot_override_path.is_empty():
		snapshot = MENU_SNAPSHOT.load_files(snapshot_override_path, snapshot_override_path.get_basename() + ".json")
	elif _live_slot > 0:
		snapshot = MENU_SNAPSHOT.load_valid(_live_slot)
	elif live_world:
		# Nothing to warm-load (first launch, a Compact/legacy world last played, or warm-loading disabled): the bundled showcase picture.
		snapshot = MENU_SNAPSHOT.load_showcase()
		_showcase_picture = not snapshot.is_empty()
	_snapshot_valid = not snapshot.is_empty()
	_snapshot_stale = bool(snapshot.get("stale", false))
	if _snapshot_valid:
		boot_cover.show_snapshot(snapshot["texture"], snapshot["sidecar"])
		# Only a snapshot of the current save may place the camera; a stale one is shown but the camera goes to the real spot.
		if _live_slot > 0 and not _snapshot_stale and snapshot_override_path.is_empty():
			_placement_pose = (snapshot["sidecar"].get("pose", {}) as Dictionary).duplicate()
	else:
		boot_cover.show_starfield()


func _build_main_panel() -> void:
	main_panel = _make_panel(Vector2(500, 430))
	main_panel.set_anchors_preset(Control.PRESET_TOP_LEFT)
	main_panel.position = Vector2(100, 190)
	main_panel.size = Vector2(550, 700)
	main_panel.add_theme_stylebox_override("panel", StyleBoxEmpty.new())
	var main_column := _make_column(main_panel)
	var main_kicker := _add_kicker(main_column, "RING SURVEY")
	main_kicker.horizontal_alignment = HORIZONTAL_ALIGNMENT_LEFT
	var main_title := _add_title(main_column, "RINGWORLD", "DisplayLabel")
	main_title.horizontal_alignment = HORIZONTAL_ALIGNMENT_LEFT
	var main_subtitle := _add_subtitle(main_column, "A world beyond the horizon.")
	main_subtitle.horizontal_alignment = HORIZONTAL_ALIGNMENT_LEFT
	var divider := HSeparator.new()
	main_column.add_child(divider)
	continue_button = Button.new()
	continue_button.text = "CONTINUE   →"
	continue_button.custom_minimum_size = Vector2(390, 58)
	continue_button.alignment = HORIZONTAL_ALIGNMENT_LEFT
	continue_button.pressed.connect(_continue_most_recent)
	continue_button.visible = false
	main_column.add_child(continue_button)
	continue_subline = Label.new()
	continue_subline.theme_type_variation = "MutedLabel"
	continue_subline.horizontal_alignment = HORIZONTAL_ALIGNMENT_LEFT
	continue_subline.visible = false
	main_column.add_child(continue_subline)
	worlds_button = Button.new()
	worlds_button.text = "ENTER RINGWORLD   →"
	worlds_button.custom_minimum_size = Vector2(390, 58)
	worlds_button.pressed.connect(_show_worlds)
	worlds_button.alignment = HORIZONTAL_ALIGNMENT_LEFT
	main_column.add_child(worlds_button)
	showcase_button = Button.new()
	showcase_button.text = "EXPLORE SHOWCASE   →"
	showcase_button.custom_minimum_size = Vector2(390, 58)
	showcase_button.alignment = HORIZONTAL_ALIGNMENT_LEFT
	showcase_button.pressed.connect(_explore_showcase)
	showcase_button.visible = false
	main_column.add_child(showcase_button)
	var main_note := Label.new()
	main_note.theme_type_variation = "MutedLabel"
	main_note.text = "LOCAL WORLDS  •  CONTINUOUS TERRAIN  •  PHYSICAL CLOUDS"
	main_note.horizontal_alignment = HORIZONTAL_ALIGNMENT_LEFT
	main_column.add_child(main_note)
	_quit_button = Button.new()
	_quit_button.text = "QUIT"
	_quit_button.alignment = HORIZONTAL_ALIGNMENT_LEFT
	_quit_button.pressed.connect(_quit)
	main_column.add_child(_quit_button)


func _build_worlds_panel() -> void:
	worlds_panel = _make_panel(Vector2(CARD_WIDTH, 100))
	worlds_panel.set_anchors_preset(Control.PRESET_TOP_LEFT)
	worlds_panel.position = Vector2(100, 150)
	worlds_panel.size = Vector2(CARD_WIDTH, 100)
	worlds_panel.custom_minimum_size.x = CARD_WIDTH
	worlds_panel.add_theme_stylebox_override("panel", StyleBoxEmpty.new())
	var worlds_column := _make_left_column(worlds_panel, 14)
	_add_kicker(worlds_column, "LOCAL ARCHIVE  /  THREE SLOTS").horizontal_alignment = HORIZONTAL_ALIGNMENT_LEFT
	_add_title(worlds_column, "SELECT A WORLD", "HeadingLabel").horizontal_alignment = HORIZONTAL_ALIGNMENT_LEFT
	var description := Label.new()
	description.theme_type_variation = "MutedLabel"
	description.text = "Open an existing ring or found a new one. Each slot keeps its terrain settings and voxel edits."
	description.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	description.custom_minimum_size.x = CARD_WIDTH - 20.0
	worlds_column.add_child(description)
	for slot in range(1, SLOT_COUNT + 1):
		worlds_column.add_child(_build_world_card(slot))
	worlds_showcase_button = Button.new()
	worlds_showcase_button.text = "SHOWCASE WORLD   →   a fresh Standard ring, seed 1337"
	worlds_showcase_button.alignment = HORIZONTAL_ALIGNMENT_LEFT
	worlds_showcase_button.custom_minimum_size = Vector2(CARD_WIDTH - 28.0, 52)
	worlds_showcase_button.pressed.connect(_explore_showcase)
	worlds_showcase_button.visible = _showcase_available()
	worlds_column.add_child(worlds_showcase_button)
	status_label = Label.new()
	status_label.add_theme_color_override("font_color", Color(1.0, 0.55, 0.45))
	worlds_column.add_child(status_label)
	_worlds_back = Button.new()
	_worlds_back.theme_type_variation = "GhostButton"
	_worlds_back.text = "←  BACK"
	_worlds_back.alignment = HORIZONTAL_ALIGNMENT_LEFT
	_worlds_back.pressed.connect(_show_main_menu)
	worlds_column.add_child(_worlds_back)


func _build_world_card(slot: int) -> Control:
	var card := PanelContainer.new()
	card.name = "WorldCard%d" % slot
	card.theme_type_variation = "WorldCard"
	var vbox := VBoxContainer.new()
	vbox.add_theme_constant_override("separation", 10)
	card.add_child(vbox)
	var header := Button.new()
	header.theme_type_variation = "CardHeaderButton"
	header.custom_minimum_size = Vector2(CARD_WIDTH - 28.0, THUMBNAIL_SIZE.y)
	header.set_meta("mm_styled", true)
	header.pressed.connect(_select_slot.bind(slot))
	vbox.add_child(header)
	slot_buttons.append(header)
	var row := HBoxContainer.new()
	row.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	row.add_theme_constant_override("separation", 16)
	row.mouse_filter = Control.MOUSE_FILTER_IGNORE
	header.add_child(row)
	var thumbnail: Control = CARD_THUMBNAIL.new()
	thumbnail.custom_minimum_size = THUMBNAIL_SIZE
	row.add_child(thumbnail)
	var info := VBoxContainer.new()
	info.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	info.alignment = BoxContainer.ALIGNMENT_CENTER
	info.add_theme_constant_override("separation", 4)
	info.mouse_filter = Control.MOUSE_FILTER_IGNORE
	row.add_child(info)
	var kicker := _add_kicker(info, "SLOT %d" % slot)
	kicker.horizontal_alignment = HORIZONTAL_ALIGNMENT_LEFT
	var title := Label.new()
	title.theme_type_variation = "CardTitleLabel"
	info.add_child(title)
	var played := Label.new()
	played.theme_type_variation = "MutedLabel"
	info.add_child(played)
	var size_line := Label.new()
	size_line.theme_type_variation = "MutedLabel"
	info.add_child(size_line)
	for label: Label in [kicker, title, played, size_line]:
		label.clip_text = true
		label.mouse_filter = Control.MOUSE_FILTER_IGNORE
	var details := Label.new()
	details.theme_type_variation = "MonoLabel"
	details.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	details.custom_minimum_size.x = CARD_WIDTH - 48.0
	details.visible = false
	vbox.add_child(details)
	_cards.append({
		"card": card, "vbox": vbox, "thumbnail": thumbnail, "kicker": kicker, "title": title,
		"played": played, "size": size_line, "details": details,
	})
	return card


func _build_world_actions() -> void:
	world_actions_panel = VBoxContainer.new()
	world_actions_panel.name = "WorldActions"
	world_actions_panel.add_theme_constant_override("separation", 8)
	world_actions_panel.visible = false
	_ui_root.add_child(world_actions_panel)
	_action_row = HFlowContainer.new()
	_action_row.add_theme_constant_override("h_separation", 8)
	_action_row.add_theme_constant_override("v_separation", 8)
	world_actions_panel.add_child(_action_row)
	_load_button = _make_action_button("LOAD WORLD   →", _load_selected_world)
	_setup_button = _make_action_button("CONTINUE SETUP   →", _continue_setup)
	_copy_button = _make_action_button("DUPLICATE WORLD", _show_copy_targets)
	_rename_button = _make_action_button("RENAME", _show_rename)
	_details_button = _make_action_button("DETAILS", _toggle_details)
	delete_button = _make_action_button("DELETE WORLD", _open_delete_modal)
	delete_button.theme_type_variation = "DangerButton"
	copy_targets = HBoxContainer.new()
	copy_targets.add_theme_constant_override("separation", 8)
	copy_targets.visible = false
	world_actions_panel.add_child(copy_targets)
	rename_row = HBoxContainer.new()
	rename_row.add_theme_constant_override("separation", 8)
	rename_row.visible = false
	world_actions_panel.add_child(rename_row)
	rename_edit = LineEdit.new()
	rename_edit.max_length = MAX_WORLD_NAME_LENGTH
	rename_edit.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	rename_edit.text_submitted.connect(_commit_rename)
	rename_row.add_child(rename_edit)
	var rename_save := Button.new()
	rename_save.text = "SAVE"
	rename_save.pressed.connect(func() -> void: _commit_rename(rename_edit.text))
	rename_row.add_child(rename_save)
	var rename_cancel := Button.new()
	rename_cancel.text = "CANCEL"
	rename_cancel.pressed.connect(_hide_rename)
	rename_row.add_child(rename_cancel)
	action_details = Label.new()
	action_details.theme_type_variation = "MutedLabel"
	action_details.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	action_details.custom_minimum_size.x = CARD_WIDTH - 48.0
	world_actions_panel.add_child(action_details)


func _make_action_button(text: String, callback: Callable) -> Button:
	var button := Button.new()
	button.text = text
	button.add_theme_font_size_override("font_size", 14)
	button.pressed.connect(callback)
	_action_row.add_child(button)
	return button


func _build_new_world_panel() -> void:
	new_world_panel = _make_panel(Vector2(550, 700))
	new_world_panel.set_anchors_preset(Control.PRESET_TOP_LEFT)
	new_world_panel.position = Vector2(100, 190)
	new_world_panel.size = Vector2(550, 700)
	new_world_panel.add_theme_stylebox_override("panel", StyleBoxEmpty.new())
	var column := _make_column(new_world_panel)
	_add_kicker(column, "NEW WORLD").horizontal_alignment = HORIZONTAL_ALIGNMENT_LEFT
	_add_title(column, "FOUND A NEW RING", "HeadingLabel").horizontal_alignment = HORIZONTAL_ALIGNMENT_LEFT
	var lead := _add_subtitle(column, "Name it, choose a size, and set the seed.")
	lead.horizontal_alignment = HORIZONTAL_ALIGNMENT_LEFT
	column.add_child(HSeparator.new())
	_add_field_label(column, "NAME")
	new_world_name_edit = LineEdit.new()
	new_world_name_edit.max_length = MAX_WORLD_NAME_LENGTH
	new_world_name_edit.custom_minimum_size = Vector2(390, 48)
	new_world_name_edit.select_all_on_focus = true
	column.add_child(new_world_name_edit)
	_add_field_label(column, "SEED")
	var seed_row := HBoxContainer.new()
	seed_row.add_theme_constant_override("separation", 8)
	column.add_child(seed_row)
	new_world_seed_edit = LineEdit.new()
	new_world_seed_edit.custom_minimum_size = Vector2(250, 48)
	new_world_seed_edit.max_length = 10
	new_world_seed_edit.text_changed.connect(_on_seed_text_changed)
	seed_row.add_child(new_world_seed_edit)
	_new_world_reroll = Button.new()
	_new_world_reroll.text = "REROLL"
	_new_world_reroll.custom_minimum_size = Vector2(120, 48)
	_new_world_reroll.pressed.connect(_reroll_seed)
	seed_row.add_child(_new_world_reroll)
	_add_field_label(column, "SIZE")
	var preset_row := HBoxContainer.new()
	preset_row.add_theme_constant_override("separation", 8)
	column.add_child(preset_row)
	var group := ButtonGroup.new()
	for preset_id: String in WORLD_PRESETS.ORDER:
		var preset_button := Button.new()
		preset_button.text = str(WORLD_PRESETS.describe(preset_id).get("label", preset_id.to_upper()))
		preset_button.toggle_mode = true
		preset_button.button_group = group
		preset_button.custom_minimum_size = Vector2(190, 48)
		preset_button.pressed.connect(_select_preset.bind(preset_id))
		preset_row.add_child(preset_button)
		_preset_buttons[preset_id] = preset_button
	_preset_line = Label.new()
	_preset_line.theme_type_variation = "MonoLabel"
	column.add_child(_preset_line)
	_preset_blurb = Label.new()
	_preset_blurb.theme_type_variation = "MutedLabel"
	_preset_blurb.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	# Wrapped labels need a width floor, or a container can measure them at 1 px and grow tall.
	_preset_blurb.custom_minimum_size.x = 390
	column.add_child(_preset_blurb)
	var create_row := HBoxContainer.new()
	create_row.add_theme_constant_override("separation", 8)
	column.add_child(create_row)
	_new_world_create = Button.new()
	_new_world_create.text = "CREATE WORLD   →"
	_new_world_create.custom_minimum_size = Vector2(250, 58)
	_new_world_create.pressed.connect(_create_new_world.bind(false))
	create_row.add_child(_new_world_create)
	_new_world_advanced = Button.new()
	_new_world_advanced.text = "ADVANCED…"
	_new_world_advanced.custom_minimum_size = Vector2(130, 58)
	_new_world_advanced.pressed.connect(_create_new_world.bind(true))
	create_row.add_child(_new_world_advanced)
	_new_world_back = Button.new()
	_new_world_back.theme_type_variation = "GhostButton"
	_new_world_back.text = "←  BACK"
	_new_world_back.alignment = HORIZONTAL_ALIGNMENT_LEFT
	_new_world_back.pressed.connect(_show_worlds)
	column.add_child(_new_world_back)


func _build_delete_modal() -> void:
	delete_modal = Control.new()
	delete_modal.name = "DeleteModal"
	delete_modal.size = Vector2(1920, 1080)
	delete_modal.visible = false
	_ui_root.add_child(delete_modal)
	var scrim := ColorRect.new()
	scrim.color = Color(0, 0, 0, 0.55)
	scrim.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	scrim.mouse_filter = Control.MOUSE_FILTER_STOP
	delete_modal.add_child(scrim)
	var dialog := PanelContainer.new()
	dialog.position = Vector2(660, 380)
	dialog.size = Vector2(600, 320)
	delete_modal.add_child(dialog)
	var column := _make_column(dialog)
	column.alignment = BoxContainer.ALIGNMENT_BEGIN
	var kicker := _add_kicker(column, "CONFIRM DELETION")
	kicker.horizontal_alignment = HORIZONTAL_ALIGNMENT_LEFT
	kicker.add_theme_color_override("font_color", UI_THEME.DANGER)
	_delete_title = Label.new()
	_delete_title.theme_type_variation = "CardTitleLabel"
	_delete_title.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_delete_title.custom_minimum_size.x = 520
	column.add_child(_delete_title)
	_delete_body = Label.new()
	_delete_body.theme_type_variation = "MutedLabel"
	_delete_body.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_delete_body.custom_minimum_size.x = 520
	column.add_child(_delete_body)
	var buttons := HBoxContainer.new()
	buttons.add_theme_constant_override("separation", 12)
	column.add_child(buttons)
	delete_cancel_button = Button.new()
	delete_cancel_button.text = "CANCEL"
	delete_cancel_button.custom_minimum_size = Vector2(200, 52)
	delete_cancel_button.pressed.connect(_close_delete_modal)
	buttons.add_child(delete_cancel_button)
	delete_hold_button = Button.new()
	delete_hold_button.theme_type_variation = "DangerButton"
	delete_hold_button.text = "HOLD TO DELETE"
	delete_hold_button.custom_minimum_size = Vector2(260, 52)
	delete_hold_button.clip_contents = true
	delete_hold_button.button_down.connect(_on_delete_hold_down)
	delete_hold_button.button_up.connect(_on_delete_hold_up)
	delete_hold_button.mouse_exited.connect(_on_delete_hold_up)
	delete_hold_button.focus_exited.connect(_on_delete_hold_up)
	buttons.add_child(delete_hold_button)
	delete_hold_fill = ColorRect.new()
	delete_hold_fill.name = "DeleteHoldFill"
	delete_hold_fill.color = Color(UI_THEME.DANGER, 0.45)
	delete_hold_fill.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	delete_hold_fill.mouse_filter = Control.MOUSE_FILTER_IGNORE
	delete_hold_fill.pivot_offset = Vector2.ZERO
	delete_hold_fill.scale.x = 0.0
	delete_hold_button.add_child(delete_hold_fill)
	var hint := Label.new()
	hint.theme_type_variation = "MonoLabel"
	hint.text = "Hold %.1f s" % DELETE_HOLD_SECONDS
	column.add_child(hint)


func _update_ui_scale() -> void:
	if _ui_root == null:
		return
	var viewport_size: Vector2 = size
	var scale_factor: float = minf(viewport_size.x / 1920.0, viewport_size.y / 1080.0)
	_ui_root.scale = Vector2.ONE * scale_factor
	_ui_root.position = (viewport_size - Vector2(1920, 1080) * scale_factor) * 0.5


func _make_panel(panel_size: Vector2) -> PanelContainer:
	var panel := PanelContainer.new()
	panel.set_anchors_preset(Control.PRESET_CENTER)
	panel.position = -panel_size * 0.5
	panel.size = panel_size
	_ui_root.add_child(panel)
	return panel


func _make_column(panel: Control) -> VBoxContainer:
	var margin := MarginContainer.new()
	for side in ["margin_left", "margin_right", "margin_top", "margin_bottom"]:
		margin.add_theme_constant_override(side, 34)
	panel.add_child(margin)
	var column := VBoxContainer.new()
	column.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	column.size_flags_vertical = Control.SIZE_EXPAND_FILL
	column.alignment = BoxContainer.ALIGNMENT_CENTER
	column.add_theme_constant_override("separation", 15)
	margin.add_child(column)
	return column


# Top-aligned column with no inner margin, for the card list.
func _make_left_column(panel: Control, separation: int) -> VBoxContainer:
	var column := VBoxContainer.new()
	column.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	column.alignment = BoxContainer.ALIGNMENT_BEGIN
	column.add_theme_constant_override("separation", separation)
	panel.add_child(column)
	return column


func _add_title(parent: Container, text: String, variation: String = "HeadingLabel") -> Label:
	var title := Label.new()
	title.theme_type_variation = variation
	title.text = text
	title.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	parent.add_child(title)
	return title


func _add_kicker(parent: Container, text: String) -> Label:
	var kicker := Label.new()
	kicker.theme_type_variation = "KickerLabel"
	kicker.text = text
	kicker.uppercase = true
	kicker.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	parent.add_child(kicker)
	return kicker


func _add_field_label(parent: Container, text: String) -> Label:
	var label := _add_kicker(parent, text)
	label.horizontal_alignment = HORIZONTAL_ALIGNMENT_LEFT
	return label


func _add_subtitle(parent: Container, text: String) -> Label:
	var subtitle := Label.new()
	subtitle.theme_type_variation = "LeadLabel"
	subtitle.text = text
	subtitle.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	subtitle.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	# Wrapped labels need a width floor, or a container can measure them at 1 px and grow tall.
	subtitle.custom_minimum_size.x = 390
	parent.add_child(subtitle)
	return subtitle


func _build_screen_chrome() -> void:
	var top_left := VBoxContainer.new()
	top_left.position = Vector2(38, 30)
	top_left.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_ui_root.add_child(top_left)
	var brand := Label.new()
	brand.text = "◉  RINGWORLD / TECH DEMO"
	brand.add_theme_font_size_override("font_size", UI_THEME.TYPE_BODY)
	brand.add_theme_color_override("font_color", UI_THEME.ACCENT_BRIGHT)
	top_left.add_child(brand)
	var system := Label.new()
	system.theme_type_variation = "MonoLabel"
	system.text = "PROCEDURAL HABITAT SIMULATION"
	top_left.add_child(system)

	_footer_label = Label.new()
	_footer_label.theme_type_variation = "MonoLabel"
	_footer_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_RIGHT
	_footer_label.position = Vector2(1472, 1036)
	_footer_label.size = Vector2(410, 20)
	_footer_label.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_ui_root.add_child(_footer_label)
	_survey_label = Label.new()
	_survey_label.theme_type_variation = "MonoLabel"
	_survey_label.text = "PREPARING 0%" if _live_slot > 0 else ""
	_survey_label.position = Vector2(38, 1036)
	_survey_label.size = Vector2(400, 20)
	_survey_label.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_ui_root.add_child(_survey_label)
	_update_footer()


## "LAST SESSION" over the snapshot, "LIVE WORLD" once the live view is showing, empty for the starfield.
func _update_footer() -> void:
	if _footer_label == null:
		return
	var suffix := "" if _world_name.is_empty() else " · " + _world_name.to_upper()
	if _cover_retired:
		_footer_label.text = "LIVE WORLD" + suffix
	elif _showcase_picture and boot_cover != null and boot_cover.mode == "snapshot":
		_footer_label.text = "RINGWORLD SHOWCASE"
	elif boot_cover != null and boot_cover.mode == "snapshot":
		_footer_label.text = "LAST SESSION" + suffix
	else:
		_footer_label.text = ""


## Per-screen camera poses came with the flyover; the live world has none until C10.
func _set_camera_pose(_pose: String) -> void:
	pass


# ---------------------------------------------------------------- navigation

func _show_main_menu() -> void:
	main_panel.visible = true
	worlds_panel.visible = false
	new_world_panel.visible = false
	_collapse_cards()
	_close_delete_modal(false)
	_set_camera_pose("main")
	_refresh_continue()
	MENU_MOTION.show_panel(main_panel, INTRO_PANEL_DELAY_SECONDS if _intro_pending else 0.0)
	_intro_pending = false
	var rows: Array = []
	if continue_button.visible:
		rows.append([continue_button])
	rows.append([worlds_button])
	if showcase_button.visible:
		rows.append([showcase_button])
	rows.append([_quit_button])
	_link_rows(rows)
	(continue_button if continue_button.visible else worlds_button).grab_focus()


func _refresh_continue() -> void:
	_continue_slot = 0
	var session := get_node_or_null("/root/WorldSession")
	if session != null and session.has_method("get_most_recent_slot"):
		_continue_slot = int(session.get_most_recent_slot())
	# Continue stays bound to the warm world even when Duplicate stamps a newer last_played on a copy.
	var warm := get_node_or_null("/root/RingWarmWorld")
	if warm != null and warm.is_busy() and int(warm.slot) > 0 and session != null and session.slot_exists(int(warm.slot)):
		_continue_slot = int(warm.slot)
	continue_button.visible = _continue_slot > 0
	continue_subline.visible = _continue_slot > 0
	# With no saved world the showcase is offered right on the main menu, next to its picture.
	showcase_button.visible = _continue_slot <= 0 and _showcase_available()
	if _continue_slot <= 0:
		return
	var metadata: Dictionary = session.get_slot_metadata(_continue_slot)
	var subline := str(metadata.get("name", "World %d" % _continue_slot))
	var played := int(metadata.get("last_played_unix", 0))
	if played > 0:
		subline += " · last played " + _relative_time(played)
	continue_subline.text = subline


func _relative_time(unix_time: int) -> String:
	var seconds := int(Time.get_unix_time_from_system()) - unix_time
	if seconds < 60:
		return "just now"
	if seconds < 3600:
		return "%d min ago" % floori(seconds / 60.0)
	if seconds < 86400:
		return "%d h ago" % floori(seconds / 3600.0)
	var days := floori(seconds / 86400.0)
	return "%d day%s ago" % [days, "" if days == 1 else "s"]


func _format_playtime(seconds: int) -> String:
	if seconds < 60:
		return "<1 min"
	if seconds < 3600:
		return "%d min" % floori(seconds / 60.0)
	return "%d h %d min" % [floori(seconds / 3600.0), floori((seconds % 3600) / 60.0)]


func _continue_most_recent() -> void:
	if not await _load_slot(_continue_slot):
		continue_subline.text = "Could not open this world."


func _show_worlds() -> void:
	main_panel.visible = false
	worlds_panel.visible = true
	new_world_panel.visible = false
	_collapse_cards()
	_close_delete_modal(false)
	_set_camera_pose("worlds")
	MENU_MOTION.show_panel(worlds_panel)
	status_label.text = ""
	_refresh_world_cards()
	_update_world_focus_chain()
	var focus_slot := 1
	var session := get_node_or_null("/root/WorldSession")
	if session != null and session.has_method("get_most_recent_slot") and int(session.get_most_recent_slot()) > 0:
		focus_slot = clampi(int(session.get_most_recent_slot()), 1, SLOT_COUNT)
	slot_buttons[focus_slot - 1].grab_focus()


func _session_has(method: String) -> Node:
	var session := get_node_or_null("/root/WorldSession")
	if session != null and session.has_method(method):
		return session
	return null


# ---------------------------------------------------------------- world cards

func _slot_is_unfinished(metadata: Dictionary) -> bool:
	var settings: Variant = metadata.get("generation_settings", {})
	return not (settings is Dictionary and not settings.is_empty())


func _format_km(value: float) -> String:
	var text := ""
	if absf(value - roundf(value)) < 0.05:
		text = str(int(roundf(value)))
	else:
		text = "%.1f" % value
	var parts := text.split(".")
	var integer := parts[0]
	var out := ""
	var count := 0
	for i in range(integer.length() - 1, -1, -1):
		if count > 0 and count % 3 == 0 and integer[i] != "-":
			out = "," + out
		out = integer[i] + out
		count += 1
	return out + ("." + parts[1] if parts.size() > 1 else "")


func _preset_label_for_radius(radius_km: float) -> String:
	for preset_id: String in WORLD_PRESETS.ORDER:
		var info: Dictionary = WORLD_PRESETS.describe(preset_id)
		if absf(float(info.get("radius_km", -1.0)) - radius_km) < 1.0:
			return str(info.get("label", ""))
	return "CUSTOM"


func _refresh_world_cards() -> void:
	var session := get_node_or_null("/root/WorldSession")
	for index in range(SLOT_COUNT):
		var slot := index + 1
		var entry: Dictionary = _cards[index]
		var card: Control = entry["card"]
		var thumbnail: Control = entry["thumbnail"]
		var kicker: Label = entry["kicker"]
		var title: Label = entry["title"]
		var played: Label = entry["played"]
		var size_line: Label = entry["size"]
		var details: Label = entry["details"]
		details.visible = false
		size_line.visible = true
		if session == null or not session.slot_exists(slot):
			card.theme_type_variation = "WorldCardEmpty"
			thumbnail.set_empty()
			kicker.text = "SLOT %d" % slot
			title.text = "+ NEW WORLD"
			played.text = "Found a new ring in this slot."
			size_line.visible = false
			continue
		var metadata: Dictionary = session.get_slot_metadata(slot)
		var world_name := str(metadata.get("name", "World %d" % slot))
		card.theme_type_variation = "WorldCard"
		if _slot_is_unfinished(metadata):
			thumbnail.set_world(slot, "")
			kicker.text = "SLOT %d · SETUP INCOMPLETE" % slot
			title.text = world_name
			played.text = "Choose a size and seed to finish this world."
			size_line.visible = false
			continue
		var settings: Dictionary = metadata.get("generation_settings", {})
		var thumbnail_path := ""
		if session.has_method("get_slot_thumbnail_path"):
			thumbnail_path = str(session.get_slot_thumbnail_path(slot))
		thumbnail.set_world(int(round(float(settings.get("terrain_seed", slot)))), thumbnail_path)
		var radius_km := float(settings.get("radius_km", 80.0))
		kicker.text = "SLOT %d · %s" % [slot, _preset_label_for_radius(radius_km)]
		title.text = world_name
		var last_played := int(metadata.get("last_played_unix", 0))
		var played_text := "Last played " + _relative_time(last_played) if last_played > 0 else "Never played"
		var playtime := int(metadata.get("playtime_seconds", 0))
		if playtime > 0:
			played_text += " · %s played" % _format_playtime(playtime)
		played.text = played_text
		size_line.text = "R %s km · W %s km" % [_format_km(radius_km), _format_km(float(settings.get("ring_width_km", 20.0)))]
		details.text = _details_text(settings)


func _details_text(settings: Dictionary) -> String:
	var backend := str(settings.get("terrain_backend", "smooth_terrain_v1"))
	var text := "SEED %d · BACKEND %s" % [int(round(float(settings.get("terrain_seed", 1337)))), backend]
	var recipe_json := str(settings.get("surface_generation_recipe_json", ""))
	if not recipe_json.is_empty():
		var recipe: Variant = JSON.parse_string(recipe_json)
		if recipe is Dictionary and recipe.has("generator_id"):
			text += " · GENERATOR %s" % str(recipe["generator_id"])
	else:
		var designed := "ON" if bool(settings.get("designed_cave_density_composition", false)) else "OFF"
		text += " · CAVE V%d · DESIGNED CAVES %s" % [int(round(float(settings.get("cave_generator_version", 1)))), designed]
	return text


func _select_slot(slot: int) -> void:
	var session := get_node_or_null("/root/WorldSession")
	if session == null:
		status_label.text = "World save service is unavailable."
		return
	if not session.slot_exists(slot):
		_show_new_world(slot)
		return
	if _expanded_slot == slot:
		_collapse_cards()
		slot_buttons[slot - 1].grab_focus()
		return
	_expand_card(slot)


func _expand_card(slot: int) -> void:
	var session := get_node_or_null("/root/WorldSession")
	if session == null:
		return
	_collapse_cards(false)
	selected_slot = slot
	_expanded_slot = slot
	var metadata: Dictionary = session.get_slot_metadata(slot)
	var unfinished := _slot_is_unfinished(metadata)
	_load_button.visible = not unfinished
	_copy_button.visible = not unfinished
	_rename_button.visible = not unfinished
	_details_button.visible = not unfinished
	_setup_button.visible = unfinished
	copy_targets.visible = false
	rename_row.visible = false
	action_details.text = "This world still needs its size and seed." if unfinished else ""
	var entry: Dictionary = _cards[slot - 1]
	(entry["card"] as Control).theme_type_variation = "WorldCardSelected"
	world_actions_panel.reparent(entry["vbox"], false)
	world_actions_panel.visible = true
	_set_camera_pose("detail")
	_update_world_focus_chain()
	_focus_expanded_default()


func _collapse_cards(refocus_pose := true) -> void:
	if _expanded_slot != 0:
		var entry: Dictionary = _cards[_expanded_slot - 1]
		var card: Control = entry["card"]
		if card.theme_type_variation == "WorldCardSelected":
			card.theme_type_variation = "WorldCard"
		(entry["details"] as Control).visible = false
	_expanded_slot = 0
	copy_targets.visible = false
	rename_row.visible = false
	if world_actions_panel.get_parent() != _ui_root:
		world_actions_panel.reparent(_ui_root, false)
	world_actions_panel.visible = false
	if refocus_pose and worlds_panel.visible:
		_set_camera_pose("worlds")
	if worlds_panel.visible:
		_update_world_focus_chain()


func _focus_expanded_default() -> void:
	if not world_actions_panel.visible:
		return
	(_setup_button if _setup_button.visible else _load_button).grab_focus()


func _toggle_details() -> void:
	if _expanded_slot == 0:
		return
	var details: Control = _cards[_expanded_slot - 1]["details"]
	details.visible = not details.visible


func _continue_setup() -> void:
	if _expanded_slot != 0:
		_show_new_world(_expanded_slot)


func _load_selected_world() -> void:
	if not await _load_slot(selected_slot):
		action_details.text = "Could not open this world."


func _load_slot(slot: int) -> bool:
	var session := get_node_or_null("/root/WorldSession")
	if session == null or slot == 0 or _leaving:
		return false
	if _continue_pending:
		return true
	# The warm world is this very slot: fly into it instead of going through the loading screen.
	if _is_warm_target(slot):
		_continue_live()
		return true
	_leaving = true
	# The warm world binds the global active world directory; free it before selecting a slot.
	await _release_warm_world()
	if not session.select_slot(slot):
		_leaving = false
		return false
	_open_selected_slot_loading(session, slot)
	_leaving = false
	return true


## Starts the foreground loading screen for the slot just bound with select_slot / create_showcase_world.
func _open_selected_slot_loading(session: Node, slot: int) -> void:
	session.request_load_selected_world()
	var metadata: Dictionary = session.get_slot_metadata(slot)
	var settings: Dictionary = metadata.get("generation_settings", {})
	WORLD_LOADING.ensure(self).open_saved_world({
		"world_title": str(metadata.get("name", "World %d" % slot)),
		"seed": int(settings.get("terrain_seed", 1337)),
		"radius_m": float(settings.get("radius_km", 80.0)) * 1000.0,
		"circumference_m": TAU * float(settings.get("radius_km", 80.0)) * 1000.0,
		"width_m": float(settings.get("ring_width_km", 20.0)) * 1000.0,
		"is_existing": true,
	})


func _showcase_available() -> bool:
	var session := get_node_or_null("/root/WorldSession")
	return session != null and session.has_method("create_showcase_world") and not session.get_showcase_definition().is_empty()


## Creates a fresh normal slot from the frozen showcase settings (seed 1337) and loads it through the loading screen, like New World.
## A second pick makes another slot; an existing showcase slot is never overwritten.
func _explore_showcase() -> void:
	var session := get_node_or_null("/root/WorldSession")
	if session == null or _leaving or not _showcase_available():
		return
	var slot := int(session.get_first_empty_slot())
	if slot == 0:
		status_label.text = "All save slots are occupied. Delete a world to make room."
		continue_subline.text = status_label.text
		continue_subline.visible = showcase_button.visible
		return
	_leaving = true
	# The warm world binds the global active world directory; free it before the slot is created and selected.
	await _release_warm_world()
	if not session.create_showcase_world(slot):
		_leaving = false
		status_label.text = "Could not create the showcase world."
		return
	_open_selected_slot_loading(session, slot)
	_leaving = false


func _show_copy_targets() -> void:
	rename_row.visible = false
	for child in copy_targets.get_children():
		copy_targets.remove_child(child)
		child.queue_free()
	var session := get_node_or_null("/root/WorldSession")
	if session == null:
		return
	var has_empty_target := false
	for slot in range(1, SLOT_COUNT + 1):
		if slot == selected_slot:
			continue
		var target := Button.new()
		target.add_theme_font_size_override("font_size", 14)
		if session.slot_exists(slot):
			target.text = "SLOT %d — OCCUPIED" % slot
			target.disabled = true
		else:
			has_empty_target = true
			target.text = "COPY TO SLOT %d" % slot
			target.pressed.connect(_copy_selected_world.bind(slot))
		copy_targets.add_child(target)
		MENU_MOTION.style_button(target)
	if not has_empty_target:
		var full := Label.new()
		full.theme_type_variation = "MutedLabel"
		full.text = "All other save slots are occupied."
		copy_targets.add_child(full)
	copy_targets.visible = true
	_update_world_focus_chain()
	for child in copy_targets.get_children():
		if child is Button and not (child as Button).disabled:
			(child as Button).grab_focus()
			break


func _copy_selected_world(destination_slot: int) -> void:
	var session := get_node_or_null("/root/WorldSession")
	if session == null or not session.copy_slot(selected_slot, destination_slot):
		action_details.text = "Could not copy this world to save slot %d." % destination_slot
		return
	action_details.text = "World copied to save slot %d." % destination_slot
	copy_targets.visible = false
	_refresh_world_cards()
	_update_world_focus_chain()
	_focus_expanded_default()


func _show_rename() -> void:
	var session := get_node_or_null("/root/WorldSession")
	if session == null or selected_slot == 0:
		return
	copy_targets.visible = false
	rename_edit.text = str(session.get_slot_metadata(selected_slot).get("name", "World %d" % selected_slot))
	rename_row.visible = true
	_update_world_focus_chain()
	rename_edit.grab_focus()
	rename_edit.select_all()


func _hide_rename() -> void:
	rename_row.visible = false
	_update_world_focus_chain()
	_focus_expanded_default()


func _commit_rename(new_name: String) -> void:
	var session := _session_has("rename_slot")
	if session == null or not session.rename_slot(selected_slot, new_name):
		action_details.text = "Could not rename this world."
		return
	rename_row.visible = false
	action_details.text = "World renamed."
	_refresh_world_cards()
	_update_world_focus_chain()
	_focus_expanded_default()


# ---------------------------------------------------------------- delete modal

func _open_delete_modal() -> void:
	var session := get_node_or_null("/root/WorldSession")
	if session == null or selected_slot == 0:
		return
	_delete_target_slot = selected_slot
	var world_name := str(session.get_slot_metadata(selected_slot).get("name", "World %d" % selected_slot))
	_delete_title.text = "Delete \"%s\"?" % world_name
	_delete_body.text = "Permanently removes slot %d, its terrain edits and saved position." % selected_slot
	_delete_holding = false
	_delete_hold_elapsed = 0.0
	delete_hold_fill.scale.x = 0.0
	delete_modal.visible = true
	_link_rows([[delete_cancel_button, delete_hold_button]])
	delete_cancel_button.grab_focus()


func _close_delete_modal(restore_focus := true) -> void:
	if delete_modal == null or not delete_modal.visible:
		return
	_on_delete_hold_up()
	delete_modal.visible = false
	if restore_focus and delete_button.is_visible_in_tree():
		delete_button.grab_focus()


func _on_delete_hold_down() -> void:
	_delete_holding = true
	_delete_hold_elapsed = 0.0


func _on_delete_hold_up() -> void:
	_delete_holding = false
	_delete_hold_elapsed = 0.0
	if delete_hold_fill != null:
		delete_hold_fill.scale.x = 0.0


func _confirm_delete() -> void:
	var slot := _delete_target_slot
	_close_delete_modal(false)
	var session := get_node_or_null("/root/WorldSession")
	var warm := get_node_or_null("/root/RingWarmWorld")
	if session != null and warm != null and warm.is_busy() and int(warm.slot) == slot:
		_leaving = true
		await _release_warm_world()
		_leaving = false
	if session == null or not session.delete_slot(slot):
		action_details.text = "Could not delete this world."
		return
	selected_slot = 0
	_show_worlds()


# ---------------------------------------------------------------- new world

func _show_new_world(slot: int) -> void:
	var session := get_node_or_null("/root/WorldSession")
	if session == null:
		return
	_new_world_slot = slot
	selected_slot = slot
	var world_name := "World %d" % slot
	if session.slot_exists(slot):
		world_name = str(session.get_slot_metadata(slot).get("name", world_name))
	_collapse_cards(false)
	main_panel.visible = false
	worlds_panel.visible = false
	new_world_panel.visible = true
	new_world_name_edit.text = world_name
	_reroll_seed()
	_select_preset("standard")
	_set_camera_pose("detail")
	MENU_MOTION.show_panel(new_world_panel)
	_link_rows([
		[new_world_name_edit],
		[new_world_seed_edit, _new_world_reroll],
		[_preset_buttons["standard"], _preset_buttons["compact"]],
		[_new_world_create, _new_world_advanced],
		[_new_world_back],
	])
	_new_world_create.grab_focus()


func _reroll_seed() -> void:
	new_world_seed_edit.text = str(randi_range(0, 2147483646))


func _on_seed_text_changed(text: String) -> void:
	var digits := ""
	for i in text.length():
		if text[i] >= "0" and text[i] <= "9":
			digits += text[i]
	if digits != text:
		var caret := new_world_seed_edit.caret_column
		new_world_seed_edit.text = digits
		new_world_seed_edit.caret_column = mini(caret, digits.length())


func _select_preset(preset_id: String) -> void:
	_new_world_preset = preset_id
	for key: String in _preset_buttons:
		(_preset_buttons[key] as Button).set_pressed_no_signal(key == preset_id)
	var info: Dictionary = WORLD_PRESETS.describe(preset_id)
	var backend := str(info.get("backend", "")).replace("_", " ")
	_preset_line.text = "R %s km · W %s km · %s" % [_format_km(float(info.get("radius_km", 0.0))), _format_km(float(info.get("width_km", 0.0))), backend]
	_preset_blurb.text = str(info.get("blurb", ""))


func _create_new_world(advanced: bool) -> void:
	var session := get_node_or_null("/root/WorldSession")
	if session == null or not session.has_method("create_world") or not session.has_method("request_new_world") or _leaving:
		status_label.text = "World save service is unavailable."
		return
	var world_name := new_world_name_edit.text.strip_edges()
	if world_name.is_empty():
		world_name = "World %d" % _new_world_slot
	if new_world_seed_edit.text.is_empty():
		_reroll_seed()
	# Match the launcher's seed range so the loading screen shows the seed actually used.
	var terrain_seed := clampi(new_world_seed_edit.text.to_int(), 0, 2147483647)
	_leaving = true
	await _release_warm_world()
	_leaving = false
	if not session.create_world(_new_world_slot, world_name):
		status_label.text = "Could not create save slot %d." % _new_world_slot
		return
	session.request_new_world({"preset": _new_world_preset, "terrain_seed": terrain_seed, "auto_start": not advanced})
	if advanced:
		get_tree().change_scene_to_file(GENERATOR_SCENE)
		return
	var info: Dictionary = WORLD_PRESETS.describe(_new_world_preset)
	var radius_m := float(info.get("radius_km", 80.0)) * 1000.0
	WORLD_LOADING.ensure(self).open_saved_world({
		"world_title": world_name,
		"seed": terrain_seed,
		"radius_m": radius_m,
		"circumference_m": TAU * radius_m,
		"width_m": float(info.get("width_km", 20.0)) * 1000.0,
		"is_existing": false,
	})


# ---------------------------------------------------------------- live world

func _eligible_live_slot() -> int:
	# Frozen covers (tests, captures) and live_world=false never warm-load a world.
	if freeze_cover or not live_world:
		return 0
	var warm := get_node_or_null("/root/RingWarmWorld")
	if warm != null and warm.is_busy() and int(warm.slot) > 0:
		return int(warm.slot)
	var session := get_node_or_null("/root/WorldSession")
	if session == null or not session.has_method("get_live_menu_slot"):
		return 0
	return int(session.get_live_menu_slot())


func _begin_live_world() -> void:
	if _live_slot <= 0:
		return
	var session := get_node_or_null("/root/WorldSession")
	if session != null and session.has_method("get_slot_metadata"):
		_world_name = str(session.get_slot_metadata(_live_slot).get("name", ""))
	_update_footer()
	_warm = WARM_WORLD.ensure(self)
	if _warm.state != WARM_WORLD.State.WARM:
		_set_menu_locked(true)
	_warm.warm_ready.connect(_on_warm_ready)
	_warm.reveal_ready.connect(_on_reveal_ready)
	_warm.warm_failed.connect(_on_warm_failed)
	if _warm.state == WARM_WORLD.State.IDLE:
		_warm.start(_live_slot, _placement_pose, _live_cruise())
	elif _warm.state == WARM_WORLD.State.WARM:
		_on_warm_ready(_warm.world)


func _on_warm_ready(_world: Node) -> void:
	if _warm.reveal_complete:
		_reveal_cover()
	else:
		_warm.reveal()


func _on_reveal_ready() -> void:
	_reveal_cover()


func _on_warm_failed(_message: String) -> void:
	if _continue_pending:
		# Continue was waiting for this world: take the loading screen instead.
		_continue_pending = false
		_leaving = false
		continue_button.disabled = false
		_load_selected_after_failure.call_deferred(_continue_slot)
	_survey_done = true
	_survey_label.text = ""
	_set_menu_locked(false, "failed")
	# The slot could not warm-load: dissolve the cover to the showcase picture (no live world, no starfield unless the picture is missing).
	if boot_cover == null or _cover_retired or freeze_cover or not snapshot_override_path.is_empty():
		return
	var showcase := MENU_SNAPSHOT.load_showcase()
	if showcase.is_empty():
		return
	_showcase_picture = true
	# The cover switches to snapshot mode synchronously; the footer follows it while the dissolve runs.
	boot_cover.fade_to_snapshot(showcase["texture"], showcase["sidecar"], _crossfade_seconds())
	_update_footer()


## Disables (or restores) Continue, Enter Ringworld and the world cards. Quit stays enabled so a slow warm-load can always be left.
func _set_menu_locked(locked: bool, reason := "") -> void:
	if locked == menu_locked:
		return
	menu_locked = locked
	var controls: Array[Button] = [continue_button, worlds_button, showcase_button, worlds_showcase_button]
	controls.append_array(slot_buttons)
	for button in controls:
		button.disabled = locked
	for card_info in _cards:
		(card_info["card"] as Control).modulate = Color(1, 1, 1, 0.55) if locked else Color.WHITE
	if locked:
		var focused := get_viewport().gui_get_focus_owner()
		if focused != null and focused in controls:
			focused.release_focus()
		return
	unlock_info = {"ms": Time.get_ticks_msec() - _ready_msec, "reason": reason}
	print("MENU_UNLOCK ms=%d reason=%s" % [unlock_info["ms"], reason])
	if main_panel.visible:
		(continue_button if continue_button.visible else worlds_button).grab_focus()
	elif worlds_panel.visible:
		slot_buttons[maxi(0, _continue_slot - 1) if _continue_slot > 0 else 0].grab_focus()


func _update_menu_lock() -> void:
	var now := Time.get_ticks_msec()
	if not is_instance_valid(_warm) or _warm.state == WARM_WORLD.State.IDLE:
		_set_menu_locked(false, "no_world")
	elif _warm.state == WARM_WORLD.State.WARM or _warm.is_past_terrain_stage():
		if float(now - _last_slow_frame_msec) / 1000.0 >= UNLOCK_QUIET_SECONDS:
			_set_menu_locked(false, "quiet")
	if menu_locked and float(now - _ready_msec) / 1000.0 >= UNLOCK_CAP_SECONDS:
		_set_menu_locked(false, "cap")


## The world is quiet behind the opaque cover. Capture the next boot snapshot when this boot did not use a current one, ease the
## remaining drift to rest, dissolve the cover to the live view and free it.
func _reveal_cover() -> void:
	if _cover_retired or _revealing or _leaving or freeze_cover or boot_cover == null or not is_instance_valid(_warm):
		return
	_revealing = true
	if _needs_capture():
		_capture_snapshot()
	await boot_cover.settle()
	if not _cover_still_wanted():
		return
	_warm.log_gpu_memory("before_crossfade")
	await boot_cover.crossfade(_crossfade_seconds())
	if not _cover_still_wanted():
		return
	_retire_cover()


func _cover_still_wanted() -> bool:
	return not _leaving and not _cover_retired and is_instance_valid(_warm) and _warm.is_warm()


## No current snapshot was used this boot: none, a stale one, or the placement fell back to the default cruise.
func _needs_capture() -> bool:
	return _live_slot > 0 and snapshot_override_path.is_empty() \
		and not (_snapshot_valid and not _snapshot_stale and _warm.placement_used_snapshot())


func _capture_snapshot() -> void:
	var pose: Dictionary = _warm.get_camera_pose()
	if not bool(pose.get("valid", false)):
		return
	var focus := get_viewport().gui_get_focus_owner()
	var image: Image = MENU_SNAPSHOT.capture_world_only(get_tree(), [self])
	# Hiding the menu released its keyboard focus.
	if focus != null and is_instance_valid(focus) and focus.is_visible_in_tree():
		focus.grab_focus()
	if image == null:
		return
	pose.erase("valid")
	var sun := float(_warm.get_telemetry().get("sun_elevation_degrees", 0.0))
	var sidecar: Dictionary = MENU_SNAPSHOT.make_sidecar("last_session", MENU_SNAPSHOT.current_state_stamp(_live_slot), pose, sun, _warm.get_last_warm_ms())
	MENU_SNAPSHOT.write_async(_live_slot, image, sidecar)


func _retire_cover() -> void:
	if _cover_retired:
		return
	boot_cover.retire()
	_cover_retired = true
	_survey_done = true
	_telemetry_active = true
	_start_loiter()
	_update_footer()
	_update_telemetry()
	if _continue_pending:
		_continue_pending = false
		_run_continue()
	# Let the freed cover settle before sampling the process GPU memory again.
	get_tree().create_timer(3.0).timeout.connect(func() -> void:
		if is_instance_valid(_warm):
			_warm.log_gpu_memory("after_crossfade"))



## The cover is gone and the world is live: the menu camera loiters around the save spot. Saves stay held while it flies.
func _start_loiter() -> void:
	if flight_director != null or _leaving or not is_instance_valid(_warm) or not _warm.is_warm():
		return
	var director: Node = FLIGHT_DIRECTOR.new()
	director.name = "FlightDirector"
	add_child(director)
	if not director.Bind(_warm.bridge):
		director.queue_free()
		return
	var cruise := _live_cruise()
	if not cruise.is_empty():
		director.CruiseAltitudeMeters = float(cruise["altitude_m"])
		director.CruisePitchDegrees = float(cruise["pitch_deg"])
	director.LoiterRadiusMeters = _live_value("LoiterRadiusKm", FALLBACK_LOITER_RADIUS_KM) * 1000.0
	director.connect("Failed", func(message: String) -> void: push_warning("Menu flight director: " + message))
	flight_director = director
	director.StartLoiter()
	# Water sleeps and both terrain streamers hold the saved spot while the camera loiters kilometres up.
	_warm.bridge.ApplyMenuSuspension()


## Returns the explorer to its held state before the world is torn down.
func _stop_flight() -> void:
	if flight_director == null:
		return
	if is_instance_valid(flight_director):
		flight_director.Release()
		flight_director.queue_free()
	flight_director = null


## True when `slot` is the world the menu is warming or holding, so Continue flies into it instead of using the loading screen. False for
## the showcase picture, an ineligible or failed slot, a different slot, or when the camera director could not take the explorer.
func _is_warm_target(slot: int) -> bool:
	if freeze_cover or _showcase_picture or _live_slot <= 0 or not is_instance_valid(_warm):
		return false
	if _cover_retired and flight_director == null:
		return false
	return (_warm.state == WARM_WORLD.State.LOADING or _warm.state == WARM_WORLD.State.WARM) and int(_warm.slot) == slot


## Continue on the warm world. Once the cover is gone and the camera loiters it flies in straight away; before that the button shows
## "Preparing NN%" and the fly-in starts when the world is ready.
func _continue_live() -> void:
	if _continuing or _continue_pending:
		return
	_continue_press_msec = Time.get_ticks_msec()
	if _warm.state == WARM_WORLD.State.WARM and _cover_retired and flight_director != null:
		_run_continue()
		return
	_continue_pending = true
	continue_button.disabled = true
	continue_subline.visible = true


func _load_selected_after_failure(slot: int) -> void:
	if not await _load_slot(slot):
		continue_subline.text = "Could not open this world."


## The Continue transition: fade the UI, rush to the saved spot, descend onto the saved pose (the director holds the descent at about 2 km
## while Terrain3D coverage is missing), then RingWarmWorld hands the world to the player. After CONTINUE_TIMEOUT_SECONDS (or a director
## failure) the menu fades to black over CONTINUE_FALLBACK_FADE_SECONDS and the player is placed directly at the saved pose.
func _run_continue() -> void:
	if _continuing or not is_instance_valid(_warm) or not _warm.is_warm() or flight_director == null:
		return
	_continuing = true
	_leaving = true
	_telemetry_active = false
	continue_button.disabled = true
	var bridge: Node = _warm.bridge
	var director: Node = flight_director
	var flight_start := Time.get_ticks_msec()
	if _continue_press_msec == 0:
		_continue_press_msec = flight_start
	# Fade every menu layer except the black reveal overlay (the timeout fallback fades that one in).
	var fade := create_tween().set_parallel(true)
	for child in get_children():
		if child is Control and child != _intro_overlay and child != boot_cover:
			fade.tween_property(child, "modulate:a", 0.0, CONTINUE_UI_FADE_SECONDS)
	var home: Dictionary = bridge.GetHomePose()
	var outcome := "failed"
	var underground := false
	if bool(home.get("valid", false)):
		director.RushSpeedMetersPerSecond = _live_value("RushSpeedKmPerSecond", FALLBACK_RUSH_SPEED_KM_PER_SECOND) * 1000.0
		director.DescentSeconds = _live_value("DescentSeconds", FALLBACK_DESCENT_SECONDS)
		director.connect("Arrived", func() -> void: _continue_flight_done.emit("arrived"), CONNECT_ONE_SHOT)
		director.connect("Failed", func(_message: String) -> void: _continue_flight_done.emit("failed"), CONNECT_ONE_SHOT)
		get_tree().create_timer(continue_timeout_seconds).timeout.connect(func() -> void: _continue_flight_done.emit("timeout"))
		var plan: Dictionary = bridge.GetLandingPlan()
		underground = bool(plan.get("underground", false))
		# An underground (cave, shaft, rock) save is never descended into: the fly-in ends above the surface and the 0.3 s fade cuts to the pose.
		var landing_vertical := float(plan["surface_vertical_m"]) + UNDERGROUND_STOP_ALTITUDE_M if underground else float(home["vertical_m"])
		var landing_pitch := UNDERGROUND_STOP_PITCH_DEGREES if underground else float(home["pitch_deg"])
		director.FlyInTo(float(home["around_m"]), landing_vertical, float(home["across_m"]), float(home["yaw_deg"]), landing_pitch)
		outcome = await _continue_flight_done
	var use_veil := outcome != "arrived"
	if underground and outcome == "arrived":
		outcome = "underground"
		use_veil = true
	if use_veil:
		director.Hold()
		_intro_overlay.show()
		_intro_overlay.color = Color(0, 0, 0, 0)
		var cover := create_tween()
		cover.tween_property(_intro_overlay, "color:a", 1.0, CONTINUE_FALLBACK_FADE_SECONDS)
		await cover.finished
	var info := {"use_veil": use_veil, "press_msec": _continue_press_msec, "flight_msec": Time.get_ticks_msec() - flight_start, "outcome": outcome}
	continue_report = info
	# The menu is freed inside the hand-off; everything after this await only runs if the hand-off was refused.
	var handed_over: bool = await _warm.hand_off_to_play(self, director, info)
	if not handed_over:
		_continuing = false
		_leaving = false


## Fades to black and frees the warm world (or cancels its load). Awaiting it makes select_slot and the loading screen safe.
func _release_warm_world() -> void:
	_continue_pending = false
	_stop_flight()
	if is_instance_valid(_warm) and _warm.bridge != null and is_instance_valid(_warm.bridge):
		_warm.bridge.ReleaseMenuSuspension()
	if boot_cover != null:
		boot_cover.halt()
	var warm := get_node_or_null("/root/RingWarmWorld")
	if warm == null or not warm.is_busy():
		return
	if _intro_overlay != null:
		_intro_overlay.show()
		var cover := create_tween()
		cover.tween_property(_intro_overlay, "color:a", 1.0, LEAVE_FADE_SECONDS)
		await cover.finished
	await warm.teardown()


func _quit() -> void:
	if _leaving:
		return
	_leaving = true
	# Quitting from the live menu writes nothing: tear the held world down (read-only) before the tree exits.
	await _release_warm_world()
	get_tree().quit()


# ---------------------------------------------------------------- focus

# Wires explicit focus neighbors for a list of rows of controls: up/down move
# between rows (wrapping), left/right move within a row (wrapping), and Tab
# follows reading order.
func _link_rows(rows: Array) -> void:
	var flat: Array[Control] = []
	for row: Array in rows:
		for control: Control in row:
			flat.append(control)
	for row_index in range(rows.size()):
		var row: Array = rows[row_index]
		var previous_row: Array = rows[(row_index - 1 + rows.size()) % rows.size()]
		var next_row: Array = rows[(row_index + 1) % rows.size()]
		for column in range(row.size()):
			var control: Control = row[column]
			control.focus_neighbor_top = control.get_path_to(previous_row[mini(column, previous_row.size() - 1)])
			control.focus_neighbor_bottom = control.get_path_to(next_row[mini(column, next_row.size() - 1)])
			control.focus_neighbor_left = control.get_path_to(row[(column - 1 + row.size()) % row.size()])
			control.focus_neighbor_right = control.get_path_to(row[(column + 1) % row.size()])
			var flat_index := flat.find(control)
			control.focus_previous = control.get_path_to(flat[(flat_index - 1 + flat.size()) % flat.size()])
			control.focus_next = control.get_path_to(flat[(flat_index + 1) % flat.size()])


func _focusable_in(controls: Array) -> Array:
	var result: Array = []
	for control: Control in controls:
		if control.is_visible_in_tree() and not (control is BaseButton and (control as BaseButton).disabled):
			result.append(control)
	return result


func _update_world_focus_chain() -> void:
	var rows: Array = []
	for slot in range(1, SLOT_COUNT + 1):
		rows.append([slot_buttons[slot - 1]])
		if slot != _expanded_slot:
			continue
		var actions := _focusable_in(_action_row.get_children())
		if not actions.is_empty():
			rows.append(actions)
		var copies := _focusable_in(copy_targets.get_children())
		if not copies.is_empty():
			rows.append(copies)
		if rename_row.visible:
			rows.append(_focusable_in(rename_row.get_children()))
	if worlds_showcase_button.visible:
		rows.append([worlds_showcase_button])
	rows.append([_worlds_back])
	_link_rows(rows)
