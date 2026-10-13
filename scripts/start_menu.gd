extends Control

const GENERATOR_SCENE := "res://scenes/WorldSizeLauncher.tscn"
const UI_THEME := preload("res://scripts/ui/ring_ui_theme.gd")
const SPACE_BACKDROP := preload("res://scripts/ui/RingMenuSpaceBackdrop.cs")
const MENU_TUNING_DRAWER := preload("res://scripts/ui/MenuFlyoverTuningDrawer.cs")
const WORLD_LOADING := preload("res://scripts/Loading/ring_world_loading.gd")

@export var backdrop_seed: int = -1
@export var freeze_backdrop: bool = false
@export var use_saved_menu_defaults: bool = true
@export var menu_defaults_path: String = "res://resources/menu/menu_flyover_defaults.tres"

var main_panel: Control
var worlds_panel: Control
var world_actions_panel: Control
var status_label: Label
var action_title: Label
var action_details: Label
var copy_targets: VBoxContainer
var delete_button: Button
var slot_buttons: Array[Button] = []
var selected_slot := 0
var delete_confirmation_armed := false
var space_backdrop: Control
var _ui_root: Control
var _flyover_footer: Label


func _ready() -> void:
	if "--certification-benchmark" in OS.get_cmdline_user_args() or "--certified-smoke-test" in OS.get_cmdline_user_args() or "--certified-walk-precision-probe" in OS.get_cmdline_user_args() or "--capture-cavity-lighting" in OS.get_cmdline_user_args() or "--underground-mode-roundtrip-smoke-test" in OS.get_cmdline_user_args() or "--capture-cloud-transition" in OS.get_cmdline_user_args() or "--capture-certified-cloud-diagnostics" in OS.get_cmdline_user_args() or "--capture-certified-cloud-continuous-transition" in OS.get_cmdline_user_args() or "--capture-certified-cloud-continuous-raw-density" in OS.get_cmdline_user_args() or "--capture-certified-cloud-continuous-deep-night-along" in OS.get_cmdline_user_args() or "--capture-certified-cloud-focused-transition" in OS.get_cmdline_user_args() or "--capture-certified-cloud-focused-raw-density" in OS.get_cmdline_user_args() or "--capture-certified-cloud-focused-history-off" in OS.get_cmdline_user_args() or "--capture-certified-cloud-focused-sunshine-only" in OS.get_cmdline_user_args() or "--capture-certified-cloud-focused-far-only" in OS.get_cmdline_user_args() or "--lod-voxel-handoff-smoke-test" in OS.get_cmdline_user_args() or "--lod-hierarchy-handoff-smoke-test" in OS.get_cmdline_user_args() or "--lod-roof-coverage-smoke-test" in OS.get_cmdline_user_args() or "--smooth-physical-terrain-smoke-test" in OS.get_cmdline_user_args() or "--smooth-terrain-traversal-smoke-test" in OS.get_cmdline_user_args() or "--smooth-deep-shaft-smoke-test" in OS.get_cmdline_user_args() or "--capture-ground-horizon" in OS.get_cmdline_user_args() or "--capture-band-site" in OS.get_cmdline_user_args() or "--capture-band-site-smooth-only" in OS.get_cmdline_user_args() or "--capture-band-site-proxy-only" in OS.get_cmdline_user_args():
		get_tree().call_deferred("change_scene_to_file", GENERATOR_SCENE)
		return
	_build_ui()
	resized.connect(_update_ui_scale)
	_update_ui_scale()
	_show_main_menu()
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE


func _build_ui() -> void:
	set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	theme = UI_THEME.create_theme()
	space_backdrop = SPACE_BACKDROP.new()
	space_backdrop.name = "SpaceBackdrop"
	space_backdrop.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	space_backdrop.mouse_filter = Control.MOUSE_FILTER_IGNORE
	space_backdrop.Seed = backdrop_seed
	space_backdrop.FreezeMotion = freeze_backdrop
	space_backdrop.UseSavedMenuDefaults = use_saved_menu_defaults
	space_backdrop.MenuSettingsPath = menu_defaults_path
	add_child(space_backdrop)
	_ui_root = Control.new()
	_ui_root.name = "MenuDesignCanvas"
	_ui_root.size = Vector2(1920, 1080)
	add_child(_ui_root)
	var gradient := Gradient.new()
	gradient.offsets = PackedFloat32Array([0.0, 0.28, 0.65])
	gradient.colors = PackedColorArray([Color(0, 0, 0, 0.7), Color(0, 0, 0, 0.45), Color(0, 0, 0, 0)])
	var overlay := TextureRect.new()
	overlay.texture = GradientTexture2D.new()
	overlay.texture.gradient = gradient
	overlay.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	overlay.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(overlay)
	move_child(overlay, get_child_count() - 2)
	_build_screen_chrome()

	main_panel = _make_panel(Vector2(500, 430))
	main_panel.set_anchors_preset(Control.PRESET_TOP_LEFT)
	main_panel.position = Vector2(100, 270)
	main_panel.size = Vector2(550, 550)
	main_panel.add_theme_stylebox_override("panel", StyleBoxEmpty.new())
	var main_column := _make_column(main_panel)
	var main_kicker := _add_kicker(main_column, "RING SURVEY")
	main_kicker.horizontal_alignment = HORIZONTAL_ALIGNMENT_LEFT
	var main_title := _add_title(main_column, "RINGWORLD", 72)
	main_title.horizontal_alignment = HORIZONTAL_ALIGNMENT_LEFT
	main_title.add_theme_color_override("font_color", Color.WHITE)
	var main_subtitle := _add_subtitle(main_column, "A world beyond the horizon.")
	main_subtitle.horizontal_alignment = HORIZONTAL_ALIGNMENT_LEFT
	var divider := HSeparator.new()
	main_column.add_child(divider)
	var worlds_button := Button.new()
	worlds_button.text = "ENTER RINGWORLD   →"
	worlds_button.custom_minimum_size = Vector2(390, 58)
	worlds_button.pressed.connect(_show_worlds)
	worlds_button.alignment = HORIZONTAL_ALIGNMENT_LEFT
	main_column.add_child(worlds_button)
	var main_note := Label.new()
	main_note.text = "LOCAL WORLDS  •  CONTINUOUS TERRAIN  •  PHYSICAL CLOUDS"
	main_note.horizontal_alignment = HORIZONTAL_ALIGNMENT_LEFT
	main_note.add_theme_font_size_override("font_size", 12)
	main_note.modulate = UI_THEME.MUTED
	main_column.add_child(main_note)
	var quit_button := Button.new()
	quit_button.text = "QUIT"
	quit_button.alignment = HORIZONTAL_ALIGNMENT_LEFT
	quit_button.pressed.connect(get_tree().quit)
	main_column.add_child(quit_button)

	worlds_panel = _make_panel(Vector2(700, 570))
	var worlds_column := _make_column(worlds_panel)
	_add_kicker(worlds_column, "LOCAL ARCHIVE  /  THREE SLOTS")
	_add_title(worlds_column, "SELECT A WORLD", 34)
	var description := Label.new()
	description.text = "Open an existing ring or configure a new one. Each slot keeps its terrain settings and voxel edits."
	description.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	description.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	description.modulate = UI_THEME.MUTED
	worlds_column.add_child(description)
	for slot in range(1, 4):
		var button := Button.new()
		button.custom_minimum_size = Vector2(520, 64)
		button.text = _slot_button_text(slot)
		button.pressed.connect(_select_slot.bind(slot))
		worlds_column.add_child(button)
		slot_buttons.append(button)
	status_label = Label.new()
	status_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	status_label.modulate = Color(1.0, 0.55, 0.45)
	worlds_column.add_child(status_label)
	var back := Button.new()
	back.text = "←  BACK"
	back.pressed.connect(_show_main_menu)
	worlds_column.add_child(back)

	world_actions_panel = _make_panel(Vector2(700, 590))
	var action_column := _make_column(world_actions_panel)
	_add_kicker(action_column, "WORLD OPERATIONS")
	action_title = Label.new()
	action_title.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	action_title.add_theme_font_size_override("font_size", 30)
	action_title.add_theme_color_override("font_color", UI_THEME.ACCENT_BRIGHT)
	action_column.add_child(action_title)
	action_details = Label.new()
	action_details.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	action_details.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	action_details.modulate = UI_THEME.MUTED
	action_column.add_child(action_details)
	var load_button := Button.new()
	load_button.text = "LOAD WORLD   →"
	load_button.custom_minimum_size = Vector2(430, 48)
	load_button.pressed.connect(_load_selected_world)
	action_column.add_child(load_button)
	var copy_button := Button.new()
	copy_button.text = "DUPLICATE WORLD"
	copy_button.custom_minimum_size = Vector2(430, 48)
	copy_button.pressed.connect(_show_copy_targets)
	action_column.add_child(copy_button)
	copy_targets = VBoxContainer.new()
	copy_targets.add_theme_constant_override("separation", 8)
	action_column.add_child(copy_targets)
	delete_button = Button.new()
	delete_button.text = "DELETE WORLD"
	delete_button.custom_minimum_size = Vector2(430, 48)
	delete_button.modulate = Color(1.0, 0.55, 0.55)
	delete_button.pressed.connect(_delete_selected_world)
	action_column.add_child(delete_button)
	var action_back := Button.new()
	action_back.text = "←  BACK TO WORLD SLOTS"
	action_back.pressed.connect(_show_worlds)
	action_column.add_child(action_back)

	var tuning_drawer: Control = MENU_TUNING_DRAWER.new()
	tuning_drawer.name = "MenuTuningDrawer"
	tuning_drawer.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	add_child(tuning_drawer)
	space_backdrop.connect("MenuTuningChanged", _update_flyover_footer)
	var tuning_button := Button.new()
	tuning_button.text = "`  LIVE TUNING"
	tuning_button.position = Vector2(1672, 30)
	tuning_button.custom_minimum_size = Vector2(210, 36)
	tuning_button.pressed.connect(tuning_drawer.call.bind("SetDrawerOpen", true))
	_ui_root.add_child(tuning_button)


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


func _add_title(parent: Container, text: String, font_size: int = 32) -> Label:
	var title := Label.new()
	title.text = text
	title.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	title.add_theme_font_size_override("font_size", font_size)
	title.add_theme_color_override("font_color", UI_THEME.ACCENT_BRIGHT)
	parent.add_child(title)
	return title


func _add_kicker(parent: Container, text: String) -> Label:
	var kicker := Label.new()
	kicker.text = text
	kicker.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	kicker.add_theme_font_size_override("font_size", 12)
	kicker.add_theme_color_override("font_color", UI_THEME.ACCENT)
	parent.add_child(kicker)
	return kicker


func _add_subtitle(parent: Container, text: String) -> Label:
	var subtitle := Label.new()
	subtitle.text = text
	subtitle.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	subtitle.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	subtitle.add_theme_font_size_override("font_size", 18)
	subtitle.modulate = UI_THEME.MUTED
	parent.add_child(subtitle)
	return subtitle


func _build_screen_chrome() -> void:
	var top_left := VBoxContainer.new()
	top_left.position = Vector2(38, 30)
	top_left.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_ui_root.add_child(top_left)
	var brand := Label.new()
	brand.text = "◉  RINGWORLD / TECH DEMO"
	brand.add_theme_font_size_override("font_size", 14)
	brand.add_theme_color_override("font_color", UI_THEME.ACCENT_BRIGHT)
	top_left.add_child(brand)
	var system := Label.new()
	system.text = "PROCEDURAL HABITAT SIMULATION"
	system.add_theme_font_size_override("font_size", 10)
	system.modulate = UI_THEME.MUTED
	top_left.add_child(system)

	_flyover_footer = Label.new()
	_flyover_footer.horizontal_alignment = HORIZONTAL_ALIGNMENT_RIGHT
	_flyover_footer.position = Vector2(1472, 1036)
	_flyover_footer.size = Vector2(410, 20)
	_flyover_footer.add_theme_font_size_override("font_size", 10)
	_flyover_footer.modulate = UI_THEME.MUTED
	_flyover_footer.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_ui_root.add_child(_flyover_footer)
	_update_flyover_footer()


func _update_flyover_footer() -> void:
	if _flyover_footer != null:
		_flyover_footer.text = str(space_backdrop.call("GetFlyoverCaption"))


func _slot_button_text(slot: int) -> String:
	var session := get_node_or_null("/root/WorldSession")
	if session == null or not session.slot_exists(slot):
		return "Save Slot %d — Create World" % slot
	var metadata: Dictionary = session.get_slot_metadata(slot)
	var world_name := str(metadata.get("name", "World %d" % slot))
	var settings: Variant = metadata.get("generation_settings", {})
	if settings is Dictionary and not settings.is_empty():
		var designed_caves_state := "ON" if bool(settings.get("designed_cave_density_composition", false)) else "OFF"
		return "Save Slot %d — %s\nSeed %d | Caves %s (v%d) | Radius %.0f km" % [
			slot,
			world_name,
			int(round(float(settings.get("terrain_seed", 1337)))),
			designed_caves_state,
			int(round(float(settings.get("cave_generator_version", 1)))),
			float(settings.get("radius_km", 80.0))
		]
	return "Save Slot %d — %s (Configure)" % [slot, world_name]


func _show_main_menu() -> void:
	main_panel.visible = true
	worlds_panel.visible = false
	world_actions_panel.visible = false


func _show_worlds() -> void:
	main_panel.visible = false
	worlds_panel.visible = true
	world_actions_panel.visible = false
	status_label.text = ""
	_refresh_slot_buttons()


func _select_slot(slot: int) -> void:
	var session := get_node_or_null("/root/WorldSession")
	if session == null:
		status_label.text = "World save service is unavailable."
		return
	if session.slot_exists(slot):
		_show_world_actions(slot)
		return
	if not session.select_slot(slot):
		status_label.text = "Could not create or open save slot %d." % slot
		return
	get_tree().change_scene_to_file(GENERATOR_SCENE)


func _show_world_actions(slot: int) -> void:
	var session := get_node_or_null("/root/WorldSession")
	if session == null:
		return
	selected_slot = slot
	delete_confirmation_armed = false
	delete_button.text = "Delete World"
	copy_targets.visible = false
	var metadata: Dictionary = session.get_slot_metadata(slot)
	var settings: Variant = metadata.get("generation_settings", {})
	action_title.text = "SAVE SLOT %d — %s" % [slot, str(metadata.get("name", "World %d" % slot))]
	if settings is Dictionary and not settings.is_empty():
		var designed_caves_state := "ON" if bool(settings.get("designed_cave_density_composition", false)) else "OFF"
		action_details.text = "Shared world seed %d | Cave v%d | Designed caves %s | Radius %.0f km | Width %.1f km" % [
			int(round(float(settings.get("terrain_seed", 1337)))),
			int(round(float(settings.get("cave_generator_version", 1)))),
			designed_caves_state,
			float(settings.get("radius_km", 80.0)),
			float(settings.get("ring_width_km", 20.0))
		]
	else:
		action_details.text = "This world still needs ring-generation settings."
	worlds_panel.visible = false
	world_actions_panel.visible = true


func _load_selected_world() -> void:
	var session := get_node_or_null("/root/WorldSession")
	if session == null or selected_slot == 0 or not session.select_slot(selected_slot):
		action_details.text = "Could not open this world."
		return
	session.request_load_selected_world()
	var metadata: Dictionary = session.get_slot_metadata(selected_slot)
	var settings: Dictionary = metadata.get("generation_settings", {})
	WORLD_LOADING.ensure(self).open_saved_world({
		"world_title": str(metadata.get("name", "World %d" % selected_slot)),
		"seed": int(settings.get("terrain_seed", 1337)),
		"radius_m": float(settings.get("radius_km", 80.0)) * 1000.0,
		"circumference_m": TAU * float(settings.get("radius_km", 80.0)) * 1000.0,
		"width_m": float(settings.get("ring_width_km", 20.0)) * 1000.0,
		"is_existing": true,
	})


func _show_copy_targets() -> void:
	for child in copy_targets.get_children():
		child.queue_free()
	var session := get_node_or_null("/root/WorldSession")
	if session == null:
		return
	var has_empty_target := false
	for slot in range(1, 4):
		if slot == selected_slot:
			continue
		var target := Button.new()
		if session.slot_exists(slot):
			target.text = "Save Slot %d — Occupied" % slot
			target.disabled = true
		else:
			has_empty_target = true
			target.text = "Copy to Save Slot %d" % slot
			target.pressed.connect(_copy_selected_world.bind(slot))
		copy_targets.add_child(target)
	if not has_empty_target:
		var full := Label.new()
		full.text = "All other save slots are occupied."
		full.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
		copy_targets.add_child(full)
	copy_targets.visible = true


func _copy_selected_world(destination_slot: int) -> void:
	var session := get_node_or_null("/root/WorldSession")
	if session == null or not session.copy_slot(selected_slot, destination_slot):
		action_details.text = "Could not copy this world to save slot %d." % destination_slot
		return
	action_details.text = "World copied to save slot %d." % destination_slot
	copy_targets.visible = false
	_refresh_slot_buttons()


func _delete_selected_world() -> void:
	if not delete_confirmation_armed:
		delete_confirmation_armed = true
		delete_button.text = "Confirm Delete World"
		action_details.text = "This permanently removes the world from save slot %d. Click again to confirm." % selected_slot
		return
	var session := get_node_or_null("/root/WorldSession")
	if session == null or not session.delete_slot(selected_slot):
		action_details.text = "Could not delete this world."
		return
	selected_slot = 0
	_show_worlds()


func _refresh_slot_buttons() -> void:
	for index in range(slot_buttons.size()):
		slot_buttons[index].text = _slot_button_text(index + 1)
