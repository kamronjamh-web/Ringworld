extends Control

const GAME_SCENE: PackedScene = preload("res://scenes/VoxelRingIntegrationTest.tscn")
const DEFAULT_TERRAIN_SETTINGS: Resource = preload("res://resources/voxel/voxel_terrain_settings_ring_integration.tres")
const DEFAULT_LAUNCH_CONFIG: Resource = preload("res://resources/launcher/world_size_default_config.tres")
const CERTIFIED_RING_CONFIG: Resource = preload("res://resources/launcher/certified_ring_config.tres")
const UI_THEME := preload("res://scripts/ui/ring_ui_theme.gd")
const MENU_BACKDROP := preload("res://scripts/ui/ring_menu_backdrop.gd")
const MIN_RADIUS_KM := 80.0
const MAX_RADIUS_KM := 8000.0
const MIN_RING_WIDTH_KM := 5.0
const MAX_RING_WIDTH_KM := 318.0
const DEFAULT_RING_WIDTH_KM := 20.0
const MIN_TERRAIN_FLOOR_Y := -8192.0
const MAX_TERRAIN_FLOOR_Y := 0.0
const MIN_BUILD_MAX_Y := 512.0
const MAX_BUILD_MAX_Y := 8000.0
const MIN_STRUCTURAL_DEPTH_KM := 1.0
const MAX_STRUCTURAL_DEPTH_KM := 50.0
const CERTIFIED_RADIUS_KM := 5000.0
const CERTIFIED_RING_WIDTH_KM := 318.0
const CERTIFIED_TERRAIN_FLOOR_Y := -8192
const CERTIFIED_PROTECTED_ROOF_THICKNESS_M := 512
const CERTIFIED_LOWER_HULL_THICKNESS_M := 800
const CERTIFIED_BEDROCK_MATERIAL_Y := -2000
const LEGACY_CERTIFIED_TERRAIN_FLOOR_Y := -2000
const VERTICAL_DOMAIN_VERSION := 1
const CERTIFIED_BUILD_MAX_Y := 8000
const RUNTIME_SURFACE_BUILD_MAX_Y := 8400
const CERTIFIED_SEA_LEVEL_Y := 0
const CERTIFIED_ROUTINE_PEAK_CEILING_Y := 5200
const CERTIFIED_EXCEPTIONAL_SUMMIT_Y := 7600
const CERTIFIED_TERRAIN_BUILD_CLEARANCE_M := 400
const CERTIFIED_STRUCTURAL_WALL_CLEARANCE_M := 2000
const CERTIFIED_STRUCTURAL_WALL_TOP_Y := CERTIFIED_BUILD_MAX_Y + CERTIFIED_STRUCTURAL_WALL_CLEARANCE_M
const CERTIFIED_STRUCTURAL_DEPTH_KM := 22.3
const MEGA_RING_RADIUS_METERS := 75000000000.0
const MEGA_RING_AROUND_CHUNK_COUNT := 14726215564
const MEGA_RING_WIDTH_METERS := 1600000000.0
const MEGA_RING_STRUCTURAL_DEPTH_METERS := 500000000.0
const MEGA_INLAND_SEA_HALF_WIDTH_METERS := 90000000.0
const MEGA_RING_SPAWN_ACROSS_METERS := MEGA_RING_WIDTH_METERS * 0.5 + MEGA_INLAND_SEA_HALF_WIDTH_METERS + 35000000.0
const SLIDER_STEPS := 1000.0
const CHUNK_ARC_METERS := 32.0
const BASE_TERRAIN_LOD_WIDTH_METERS := 20000.0
const BASE_GLOBAL_ACROSS_SEGMENTS := 32
const FAR_FIELD_START_METERS := 850000.0
const FAR_FIELD_RENDER_END_METERS := 1050000.0
const FAR_FIELD_CAMERA_LIMIT_METERS := 1200000.0
const SUPPORTED_CAVE_GENERATOR_VERSION := 1
const RUNTIME_SURFACE_BACKEND := "surface_runtime_generation_v1"
const RUNTIME_SURFACE_CONTRACT := "surface_runtime_generation_v1"
const SURFACE_RECIPE := preload("res://scripts/Terrain/Generation/terrain_generation_recipe.gd")
const SURFACE_RECIPE_STORE := preload("res://scripts/Terrain/Generation/terrain_generation_preview_store.gd")
const TERRAIN_GENERATION_SAMPLER := preload("res://scripts/Terrain/Generation/TerrainGenerationSampler.cs")

var size_slider: HSlider
var size_label: Label
var circumference_label: Label
var warning_label: Label
var start_button: Button
var ring_width_spin: SpinBox
var terrain_floor_spin: SpinBox
var build_height_spin: SpinBox
var structural_depth_spin: SpinBox
var terrain_seed_spin: SpinBox
var cave_generator_version_spin: SpinBox
var base_surface_spin: SpinBox
var height_amplitude_spin: SpinBox
var detail_amplitude_spin: SpinBox
var continental_contrast_spin: SpinBox
var regional_detail_spin: SpinBox
var rare_peak_spin: SpinBox
var unified_density_toggle: CheckBox
var designed_caves_toggle: CheckBox
var terrain_backend_selector: OptionButton
var advanced_popup: PopupPanel
var advanced_summary_label: Label
var launch_defaults: Dictionary = {}
var selected_slot_has_saved_settings := false
var terrain_backend_default_applied := false
var terrain_backend_explicitly_chosen := false
var suppress_readout_refresh := false
var exact_radius_override_km := -1.0
var mega_ring_selected := false
var mega_terrain_variation_smoke_passed := true
var active_vertical_domain_version := VERTICAL_DOMAIN_VERSION
var active_protected_roof_thickness_m := CERTIFIED_PROTECTED_ROOF_THICKNESS_M
var active_lower_hull_thickness_m := CERTIFIED_LOWER_HULL_THICKNESS_M
var pending_surface_generation_recipe_json := ""
var pending_surface_generation_contract_id := ""


func _use_smooth_editable_backend() -> bool:
	# New worlds default to the replacement.  The two legacy implementations are
	# intentionally retained behind explicit development-only rollback switches.
	var args := OS.get_cmdline_user_args()
	return not ("--legacy-editable-terrain" in args or "--hybrid-smooth-terrain-pilot" in args)


func _legacy_backend_forced() -> bool:
	var args := OS.get_cmdline_user_args()
	return "--legacy-editable-terrain" in args or "--hybrid-smooth-terrain-pilot" in args


func _selected_terrain_backend() -> String:
	if terrain_backend_selector == null:
		return "smooth_terrain_v1"
	if terrain_backend_selector.selected == 1:
		return "terrain3d_surface_v1"
	if terrain_backend_selector.selected == 2:
		return RUNTIME_SURFACE_BACKEND
	return "smooth_terrain_v1"


func _surface_backend_selected_for_certified(is_certified: bool) -> bool:
	return is_certified and not _legacy_backend_forced() and _selected_terrain_backend() in ["terrain3d_surface_v1", RUNTIME_SURFACE_BACKEND]


func _runtime_surface_backend_selected_for_certified(is_certified: bool) -> bool:
	return is_certified and not _legacy_backend_forced() and _selected_terrain_backend() == RUNTIME_SURFACE_BACKEND


func _configure_editable_backend(game: Node, grid: Node) -> void:
	var smooth := _use_smooth_editable_backend()
	grid.set("DormantEditableBackend", smooth)
	grid.set("BuildOnReady", not smooth)
	grid.set("EnableChunkStreaming", not smooth)
	grid.set("UseSmoothNaturalTerrainPilot", not smooth and "--hybrid-smooth-terrain-pilot" in OS.get_cmdline_user_args())
	var smooth_grid := game.get_node_or_null("SmoothTerrainGrid")
	var construction := game.get_node_or_null("SmoothConstructionOverlay")
	var editor := game.get_node_or_null("GridEditController")
	var explorer := game.get_node_or_null("Explorer")
	if smooth_grid != null:
		var desktop_mid_high := str(ProjectSettings.get_setting(
			"ringworld/runtime/hardware_tier",
			"laptop_140v_baseline")) == "desktop_mid_high"
		# LOD1 is already fully visible by 64-104 m, so the integrated-GPU
		# profile does not need the desktop's 7x7 horizontal detail window.
		smooth_grid.set("RadiusAround", 3 if desktop_mid_high else 2)
		smooth_grid.set("RadiusAcross", 3 if desktop_mid_high else 2)
		smooth_grid.set("RadiusVertical", 1)
		smooth_grid.process_mode = Node.PROCESS_MODE_INHERIT if smooth else Node.PROCESS_MODE_DISABLED
	if construction != null:
		construction.process_mode = Node.PROCESS_MODE_INHERIT if smooth else Node.PROCESS_MODE_DISABLED
	if editor != null:
		editor.set("UseSmoothTerrainBackend", smooth)
	if explorer != null:
		explorer.set("UseSmoothTerrainPhysics", smooth)
		var body_shape := explorer.get_node_or_null("CollisionShape3D") as CollisionShape3D
		if body_shape != null:
			body_shape.disabled = not smooth
	print("Editable terrain backend: %s" % ("smooth replacement" if smooth else "legacy rollback"))


func _ready() -> void:
	launch_defaults = _build_default_values()
	var saved_settings := _get_selected_slot_settings()
	selected_slot_has_saved_settings = not saved_settings.is_empty()
	if not saved_settings.is_empty():
		launch_defaults.merge(saved_settings, true)
		exact_radius_override_km = clampf(float(saved_settings.get("radius_km", MIN_RADIUS_KM)), MIN_RADIUS_KM, MAX_RADIUS_KM)
	# Migrate only the exact pre-cavity Certified profile. Its old -2000 value
	# did double duty as physical cutoff and bedrock material threshold. The new
	# floor moves to -8192 while the material threshold stays fixed, preserving
	# the sampler hash used by runtime state and safe smooth-edit journals.
	if _matches_legacy_certified_dimensions(
		float(launch_defaults.get("radius_km", MIN_RADIUS_KM)),
		float(launch_defaults.get("ring_width_km", DEFAULT_RING_WIDTH_KM)),
		int(round(float(launch_defaults.get("terrain_floor_y", -256.0)))),
		int(round(float(launch_defaults.get("build_height_y", MAX_BUILD_MAX_Y)))),
		float(launch_defaults.get("structural_depth_km", CERTIFIED_STRUCTURAL_DEPTH_KM))):
		launch_defaults["terrain_floor_y"] = CERTIFIED_TERRAIN_FLOOR_Y
		print("Migrated legacy Certified Ring vertical domain: terrain bottom -2000 -> -8192 m; bedrock material threshold retained.")
	_build_ui()
	_apply_default_config_to_controls()
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
	if "--mega-spawn-smoke-test" in OS.get_cmdline_user_args() or "--mega-terrain-variation-smoke-test" in OS.get_cmdline_user_args() or "--mega-ground-capture-preset" in OS.get_cmdline_user_args():
		_on_mega_ring_preset_pressed()
		call_deferred("_start_game")
		return
	if "--smooth-physical-terrain-smoke-test" in OS.get_cmdline_user_args() or "--smooth-terrain-traversal-smoke-test" in OS.get_cmdline_user_args() or "--smooth-deep-shaft-smoke-test" in OS.get_cmdline_user_args() or "--capture-ground-horizon" in OS.get_cmdline_user_args():
		call_deferred("_start_game")
		return
	if _consume_load_world_request():
		call_deferred("_start_game")
		return
	if "--certification-benchmark" in OS.get_cmdline_user_args() or "--certified-smoke-test" in OS.get_cmdline_user_args() or "--terrain3d-certified-smoke-test" in OS.get_cmdline_user_args() or "--certified-walk-precision-probe" in OS.get_cmdline_user_args() or "--capture-cavity-lighting" in OS.get_cmdline_user_args() or "--capture-cloud-transition" in OS.get_cmdline_user_args() or "--capture-certified-cloud-diagnostics" in OS.get_cmdline_user_args() or "--capture-certified-cloud-continuous-transition" in OS.get_cmdline_user_args() or "--capture-certified-cloud-continuous-raw-density" in OS.get_cmdline_user_args() or "--capture-certified-cloud-focused-transition" in OS.get_cmdline_user_args() or "--capture-certified-cloud-focused-raw-density" in OS.get_cmdline_user_args() or "--capture-certified-cloud-focused-history-off" in OS.get_cmdline_user_args() or "--capture-certified-cloud-focused-sunshine-only" in OS.get_cmdline_user_args() or "--capture-certified-cloud-focused-far-only" in OS.get_cmdline_user_args() or "--lod-voxel-handoff-smoke-test" in OS.get_cmdline_user_args() or "--lod-hierarchy-handoff-smoke-test" in OS.get_cmdline_user_args() or "--lod-roof-coverage-smoke-test" in OS.get_cmdline_user_args() or "--capture-band-site" in OS.get_cmdline_user_args() or "--capture-band-site-smooth-only" in OS.get_cmdline_user_args() or "--capture-band-site-proxy-only" in OS.get_cmdline_user_args():
		_on_certified_preset_pressed()
		if "--terrain3d-certified-smoke-test" in OS.get_cmdline_user_args() and terrain_backend_selector != null:
			terrain_backend_selector.select(1)
		call_deferred("_start_game")


func _build_ui() -> void:
	set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	theme = UI_THEME.create_theme()
	var background := MENU_BACKDROP.new()
	background.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	add_child(background)
	_build_screen_chrome()

	var panel := PanelContainer.new()
	panel.set_anchors_preset(Control.PRESET_CENTER)
	panel.position = Vector2(-410, -310)
	panel.size = Vector2(820, 620)
	add_child(panel)

	var margin := MarginContainer.new()
	for side in ["margin_left", "margin_right", "margin_top", "margin_bottom"]:
		margin.add_theme_constant_override(side, 30)
	panel.add_child(margin)

	var scroll := ScrollContainer.new()
	scroll.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	margin.add_child(scroll)

	var column := VBoxContainer.new()
	column.custom_minimum_size = Vector2(710, 0)
	column.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	column.add_theme_constant_override("separation", 18)
	scroll.add_child(column)

	var title := Label.new()
	title.text = "CONFIGURE THE RING"
	title.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	title.add_theme_font_size_override("font_size", 36)
	title.add_theme_color_override("font_color", UI_THEME.ACCENT_BRIGHT)
	column.add_child(title)

	var description := Label.new()
	description.text = "Choose the habitat scale. Advanced controls tune terrain, build limits, structure depth, and generation seed."
	description.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	description.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	description.modulate = UI_THEME.MUTED
	column.add_child(description)

	size_slider = HSlider.new()
	size_slider.min_value = 0.0
	size_slider.max_value = SLIDER_STEPS
	size_slider.step = 1.0
	size_slider.value = 0.0
	size_slider.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	size_slider.value_changed.connect(_on_size_slider_changed)
	column.add_child(size_slider)

	size_label = Label.new()
	size_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	size_label.add_theme_font_size_override("font_size", 28)
	size_label.add_theme_color_override("font_color", UI_THEME.ACCENT_BRIGHT)
	column.add_child(size_label)
	circumference_label = Label.new()
	circumference_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	column.add_child(circumference_label)

	var secondary_menu_row := HBoxContainer.new()
	secondary_menu_row.alignment = BoxContainer.ALIGNMENT_CENTER
	secondary_menu_row.add_theme_constant_override("separation", 12)
	column.add_child(secondary_menu_row)

	var advanced_button := Button.new()
	advanced_button.text = "ADVANCED"
	advanced_button.custom_minimum_size = Vector2(160, 38)
	advanced_button.pressed.connect(_open_advanced_knobs)
	secondary_menu_row.add_child(advanced_button)

	var reset_button := Button.new()
	reset_button.text = "RESET"
	reset_button.custom_minimum_size = Vector2(160, 38)
	reset_button.pressed.connect(_on_reset_defaults_pressed)
	secondary_menu_row.add_child(reset_button)

	var certified_button := Button.new()
	certified_button.text = "HALO-SCALE PRESET"
	certified_button.custom_minimum_size = Vector2(160, 38)
	certified_button.pressed.connect(_on_certified_preset_pressed)
	secondary_menu_row.add_child(certified_button)

	var mega_button := Button.new()
	mega_button.text = "MEGA RING PRESET"
	mega_button.custom_minimum_size = Vector2(160, 38)
	mega_button.pressed.connect(_on_mega_ring_preset_pressed)
	secondary_menu_row.add_child(mega_button)

	advanced_summary_label = Label.new()
	advanced_summary_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	advanced_summary_label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	column.add_child(advanced_summary_label)

	_build_advanced_menu()

	warning_label = Label.new()
	warning_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	warning_label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	warning_label.custom_minimum_size = Vector2(0, 52)
	column.add_child(warning_label)

	start_button = Button.new()
	start_button.text = "GENERATE RINGWORLD   →"
	start_button.custom_minimum_size = Vector2(0, 54)
	start_button.pressed.connect(_start_game)
	column.add_child(start_button)


func _build_screen_chrome() -> void:
	var brand := Label.new()
	brand.text = "◉  RINGWORLD / WORLD FOUNDRY"
	brand.position = Vector2(38, 30)
	brand.add_theme_font_size_override("font_size", 14)
	brand.add_theme_color_override("font_color", UI_THEME.ACCENT_BRIGHT)
	brand.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(brand)
	var footer := Label.new()
	footer.text = "RADIUS  •  WIDTH  •  TERRAIN  •  STRUCTURE"
	footer.horizontal_alignment = HORIZONTAL_ALIGNMENT_RIGHT
	footer.set_anchors_preset(Control.PRESET_BOTTOM_RIGHT)
	footer.position = Vector2(-410, -42)
	footer.size = Vector2(370, 20)
	footer.add_theme_font_size_override("font_size", 10)
	footer.modulate = UI_THEME.MUTED
	footer.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(footer)


func _build_advanced_menu() -> void:
	advanced_popup = PopupPanel.new()
	add_child(advanced_popup)

	var margin := MarginContainer.new()
	for side in ["margin_left", "margin_right", "margin_top", "margin_bottom"]:
		margin.add_theme_constant_override(side, 22)
	advanced_popup.add_child(margin)

	var column := VBoxContainer.new()
	column.custom_minimum_size = Vector2(680, 460)
	column.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	column.size_flags_vertical = Control.SIZE_EXPAND_FILL
	column.add_theme_constant_override("separation", 14)
	margin.add_child(column)

	var title := Label.new()
	title.text = "ADVANCED GENERATION"
	title.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	title.add_theme_font_size_override("font_size", 24)
	title.add_theme_color_override("font_color", UI_THEME.ACCENT_BRIGHT)
	column.add_child(title)

	var subtitle := Label.new()
	subtitle.text = "These values are applied before generation. Reset restores the selected baseline."
	subtitle.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	subtitle.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	subtitle.modulate = UI_THEME.MUTED
	column.add_child(subtitle)

	var scroll := ScrollContainer.new()
	scroll.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	column.add_child(scroll)

	var knobs_grid := GridContainer.new()
	knobs_grid.columns = 2
	knobs_grid.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	knobs_grid.add_theme_constant_override("h_separation", 16)
	knobs_grid.add_theme_constant_override("v_separation", 10)
	scroll.add_child(knobs_grid)

	ring_width_spin = _add_spinbox_row(
		knobs_grid,
		"Ring width (km)",
		MIN_RING_WIDTH_KM,
		MAX_RING_WIDTH_KM,
		0.5,
		float(launch_defaults.get("ring_width_km", DEFAULT_RING_WIDTH_KM))
	)
	terrain_floor_spin = _add_spinbox_row(
		knobs_grid,
		"Terrain floor (global Y)",
		MIN_TERRAIN_FLOOR_Y,
		MAX_TERRAIN_FLOOR_Y,
		32.0,
		float(launch_defaults.get("terrain_floor_y", -256.0)),
		true
	)
	build_height_spin = _add_spinbox_row(
		knobs_grid,
		"Build ceiling (global Y)",
		MIN_BUILD_MAX_Y,
		MAX_BUILD_MAX_Y,
		32.0,
		float(launch_defaults.get("build_height_y", MAX_BUILD_MAX_Y)),
		true
	)
	structural_depth_spin = _add_spinbox_row(
		knobs_grid,
		"Structural depth (km)",
		MIN_STRUCTURAL_DEPTH_KM,
		MAX_STRUCTURAL_DEPTH_KM,
		0.1,
		float(launch_defaults.get("structural_depth_km", CERTIFIED_STRUCTURAL_DEPTH_KM))
	)
	terrain_seed_spin = _add_spinbox_row(
		knobs_grid,
		"Terrain seed",
		0.0,
		2147483647.0,
		1.0,
		float(launch_defaults.get("terrain_seed", 1337.0)),
		true
	)
	cave_generator_version_spin = _add_spinbox_row(knobs_grid, "Cave generator version", 1.0, 1024.0, 1.0, float(launch_defaults.get("cave_generator_version", 1.0)), true)
	base_surface_spin = _add_spinbox_row(
		knobs_grid,
		"Base surface height",
		1.0,
		512.0,
		1.0,
		float(launch_defaults.get("base_surface_height", 16.0)),
		true
	)
	height_amplitude_spin = _add_spinbox_row(
		knobs_grid,
		"Height amplitude",
		0.0,
		2048.0,
		1.0,
		float(launch_defaults.get("height_amplitude", 12.0)),
		true
	)
	detail_amplitude_spin = _add_spinbox_row(
		knobs_grid,
		"Detail amplitude",
		0.0,
		512.0,
		1.0,
		float(launch_defaults.get("detail_amplitude", 4.0)),
		true
	)
	continental_contrast_spin = _add_spinbox_row(
		knobs_grid,
		"Continental contrast",
		1.0,
		3.0,
		0.01,
		float(launch_defaults.get("continental_contrast", 1.35))
	)
	regional_detail_spin = _add_spinbox_row(
		knobs_grid,
		"Regional detail strength",
		0.0,
		1.0,
		0.01,
		float(launch_defaults.get("regional_detail_strength", 0.65))
	)
	rare_peak_spin = _add_spinbox_row(
		knobs_grid,
		"Routine mountain ceiling",
		512.0,
		6500.0,
		1.0,
		float(launch_defaults.get("rare_peak_height", 1848.0)),
		true
	)
	unified_density_toggle = _add_checkbox_row(
		knobs_grid,
		"Unified 3D density terrain",
		bool(launch_defaults.get("unified_3d_density_terrain", false)),
		"Experimental smooth natural terrain. Enable before world creation; caves and overhangs will use this shared density field."
	)
	designed_caves_toggle = _add_checkbox_row(
		knobs_grid,
		"Designed caves",
		bool(launch_defaults.get("designed_cave_density_composition", false)),
		"Generates deterministic chambers and corridors in streamed smooth terrain. Existing worlds remain unchanged unless enabled."
	)
	var backend_label := Label.new()
	backend_label.text = "Surface terrain backend\nSurface edits and player state save separately. Caves later."
	backend_label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	backend_label.tooltip_text = "Terrain3D surface editing is available only for the Certified Ring. Surface edits and player state save separately. Caves are planned for a later update."
	knobs_grid.add_child(backend_label)
	terrain_backend_selector = OptionButton.new()
	terrain_backend_selector.add_item("Smooth terrain (existing worlds)", 0)
	terrain_backend_selector.add_item("Terrain3D surface (Certified only)", 1)
	terrain_backend_selector.add_item("Terrain3D runtime generation (Certified only)", 2)
	terrain_backend_selector.tooltip_text = backend_label.tooltip_text
	terrain_backend_selector.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	knobs_grid.add_child(terrain_backend_selector)
	unified_density_toggle.toggled.connect(_on_knob_changed)
	designed_caves_toggle.toggled.connect(_on_knob_changed)
	terrain_backend_selector.item_selected.connect(_on_terrain_backend_selected)

	for spin in [
		ring_width_spin,
		terrain_floor_spin,
		build_height_spin,
		structural_depth_spin,
		terrain_seed_spin,
		cave_generator_version_spin,
		base_surface_spin,
		height_amplitude_spin,
		detail_amplitude_spin,
		continental_contrast_spin,
		regional_detail_spin,
		rare_peak_spin
	]:
		spin.value_changed.connect(_on_knob_changed)

	var close_row := HBoxContainer.new()
	close_row.alignment = BoxContainer.ALIGNMENT_END
	column.add_child(close_row)

	var close_button := Button.new()
	close_button.text = "Done"
	close_button.custom_minimum_size = Vector2(140, 36)
	close_button.pressed.connect(_close_advanced_knobs)
	close_row.add_child(close_button)


func _add_spinbox_row(
	parent: GridContainer,
	label_text: String,
	min_value: float,
	max_value: float,
	step_value: float,
	default_value: float,
	rounded: bool = false
) -> SpinBox:
	var label := Label.new()
	label.text = label_text
	label.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	parent.add_child(label)

	var spin := SpinBox.new()
	spin.min_value = min_value
	spin.max_value = max_value
	spin.step = step_value
	spin.rounded = rounded
	spin.value = clampf(default_value, min_value, max_value)
	spin.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	parent.add_child(spin)
	return spin


func _add_checkbox_row(
	parent: GridContainer,
	label_text: String,
	default_value: bool,
	hint_text: String
) -> CheckBox:
	var label := Label.new()
	label.text = label_text
	label.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	label.tooltip_text = hint_text
	parent.add_child(label)

	var toggle := CheckBox.new()
	toggle.text = "Enable (experimental)"
	toggle.button_pressed = default_value
	toggle.tooltip_text = hint_text
	toggle.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	parent.add_child(toggle)
	return toggle


func _build_default_values() -> Dictionary:
	return _build_values_from_config(DEFAULT_LAUNCH_CONFIG)


func _get_selected_slot_settings() -> Dictionary:
	var session := get_node_or_null("/root/WorldSession")
	if session == null:
		return {}
	var settings: Variant = session.get_active_world_settings()
	return settings if settings is Dictionary else {}


func _consume_load_world_request() -> bool:
	var session := get_node_or_null("/root/WorldSession")
	return bool(session.consume_load_selected_world_request()) if session != null else false


func _build_values_from_config(config: Resource) -> Dictionary:
	return {
		"radius_km": clampf(_read_config_float(config, "default_radius_km", MIN_RADIUS_KM), MIN_RADIUS_KM, MAX_RADIUS_KM),
		"ring_width_km": clampf(_read_config_float(config, "ring_width_km", DEFAULT_RING_WIDTH_KM), MIN_RING_WIDTH_KM, MAX_RING_WIDTH_KM),
		"terrain_floor_y": clampf(_read_config_float(config, "terrain_floor_y", -256.0), MIN_TERRAIN_FLOOR_Y, MAX_TERRAIN_FLOOR_Y),
		"build_height_y": clampf(_read_config_float(config, "build_height_y", MAX_BUILD_MAX_Y), MIN_BUILD_MAX_Y, MAX_BUILD_MAX_Y),
		"structural_depth_km": clampf(_read_config_float(config, "structural_depth_km", CERTIFIED_STRUCTURAL_DEPTH_KM), MIN_STRUCTURAL_DEPTH_KM, MAX_STRUCTURAL_DEPTH_KM),
		"vertical_domain_version": int(round(_read_config_float(config, "vertical_domain_version", VERTICAL_DOMAIN_VERSION))),
		"protected_roof_thickness_m": int(round(_read_config_float(config, "protected_roof_thickness_m", CERTIFIED_PROTECTED_ROOF_THICKNESS_M))),
		"lower_hull_thickness_m": int(round(_read_config_float(config, "lower_hull_thickness_m", CERTIFIED_LOWER_HULL_THICKNESS_M))),
		"terrain_seed": clampf(_read_config_float(config, "terrain_seed", _read_terrain_float("Seed", 1337.0)), 0.0, 2147483647.0),
		"cave_generator_version": clampf(_read_config_float(config, "cave_generator_version", 1.0), 1.0, 1024.0),
		"base_surface_height": clampf(_read_config_float(config, "base_surface_height", _read_terrain_float("BaseSurfaceHeightVoxels", 16.0)), 1.0, 512.0),
		"height_amplitude": clampf(_read_config_float(config, "height_amplitude", _read_terrain_float("HeightAmplitudeVoxels", 12.0)), 0.0, 2048.0),
		"detail_amplitude": clampf(_read_config_float(config, "detail_amplitude", _read_terrain_float("DetailAmplitudeVoxels", 4.0)), 0.0, 512.0),
		"continental_contrast": clampf(_read_config_float(config, "continental_contrast", _read_terrain_float("ContinentalContrast", 1.35)), 1.0, 3.0),
		"regional_detail_strength": clampf(_read_config_float(config, "regional_detail_strength", _read_terrain_float("RegionalDetailStrength", 0.65)), 0.0, 1.0),
		"rare_peak_height": clampf(_read_config_float(config, "rare_peak_height", _read_terrain_float("RarePeakY", 1848.0)), 512.0, 6500.0),
		"unified_3d_density_terrain": _read_config_bool(config, "unified_3d_density_terrain", _read_terrain_bool("EnableUnified3dDensityTerrain", false)),
		"designed_cave_density_composition": _read_config_bool(config, "designed_cave_density_composition", false),
		"terrain_backend": "smooth_terrain_v1",
		"auto_raise_peak_to_build_limit": false
	}


func _read_config_float(config: Resource, property_name: String, fallback: float) -> float:
	if config == null:
		return fallback

	var value: Variant = config.get(property_name)
	if typeof(value) == TYPE_INT or typeof(value) == TYPE_FLOAT:
		return float(value)

	return fallback


func _read_config_bool(config: Resource, property_name: String, fallback: bool) -> bool:
	if config == null:
		return fallback

	var value: Variant = config.get(property_name)
	if typeof(value) == TYPE_BOOL:
		return bool(value)

	return fallback


func _read_terrain_float(property_name: String, fallback: float) -> float:
	if DEFAULT_TERRAIN_SETTINGS == null:
		return fallback

	var value: Variant = DEFAULT_TERRAIN_SETTINGS.get(property_name)
	if typeof(value) == TYPE_INT or typeof(value) == TYPE_FLOAT:
		return float(value)

	return fallback


func _read_terrain_bool(property_name: String, fallback: bool) -> bool:
	if DEFAULT_TERRAIN_SETTINGS == null:
		return fallback

	var value: Variant = DEFAULT_TERRAIN_SETTINGS.get(property_name)
	if typeof(value) == TYPE_BOOL:
		return bool(value)

	return fallback


func _radius_km_from_slider(value: float) -> float:
	if exact_radius_override_km > 0.0:
		return exact_radius_override_km
	var normalized := clampf(value / SLIDER_STEPS, 0.0, 1.0)
	return MIN_RADIUS_KM * pow(MAX_RADIUS_KM / MIN_RADIUS_KM, normalized)


func _slider_from_radius_km(radius_km: float) -> float:
	var clamped_radius := clampf(radius_km, MIN_RADIUS_KM, MAX_RADIUS_KM)
	var span := log(MAX_RADIUS_KM / MIN_RADIUS_KM)
	if is_zero_approx(span):
		return 0.0

	var normalized := log(clamped_radius / MIN_RADIUS_KM) / span
	return clampf(normalized, 0.0, 1.0) * SLIDER_STEPS


func _on_size_slider_changed(value: float) -> void:
	if _runtime_surface_slot_locked():
		return
	if suppress_readout_refresh:
		return
	_clear_mega_ring_selection()
	exact_radius_override_km = -1.0 
	_update_readout(value)


func _on_knob_changed(_value: Variant) -> void:
	if _runtime_surface_slot_locked():
		return
	if suppress_readout_refresh:
		return
	_clear_mega_ring_selection()
	_update_readout(size_slider.value)


func _on_knob_toggled(_pressed: bool) -> void:
	if suppress_readout_refresh:
		return
	_clear_mega_ring_selection()
	_update_readout(size_slider.value)


func _on_terrain_backend_selected(_index: int) -> void:
	if _runtime_surface_slot_locked():
		return
	if suppress_readout_refresh:
		return
	terrain_backend_explicitly_chosen = true
	_update_readout(size_slider.value)


func _on_reset_defaults_pressed() -> void:
	if _runtime_surface_slot_locked():
		return
	_clear_mega_ring_selection()
	exact_radius_override_km = -1.0
	_apply_values_to_controls(launch_defaults)


func _on_certified_preset_pressed() -> void:
	if _runtime_surface_slot_locked():
		return
	mega_ring_selected = false
	_apply_values_to_controls(_build_values_from_config(CERTIFIED_RING_CONFIG))
	exact_radius_override_km = CERTIFIED_RADIUS_KM
	_update_readout(size_slider.value)


func _on_mega_ring_preset_pressed() -> void:
	if _runtime_surface_slot_locked():
		return
	mega_ring_selected = true
	exact_radius_override_km = -1.0
	if terrain_backend_selector != null:
		terrain_backend_selector.select(0)
	_refresh_backend_selector(false)
	_update_advanced_summary()
	size_label.text = "MEGA RING / 150 MILLION KM DIAMETER"
	circumference_label.text = "Local playable bubble | 1.6 million km habitat width"
	warning_label.text = "Mega Ring runs procedural local terrain only. Whole-ring legacy LODs are disabled."
	start_button.text = "LAUNCH MEGA RING   →"


func _clear_mega_ring_selection() -> void:
	if not mega_ring_selected:
		return
	mega_ring_selected = false
	start_button.text = "GENERATE RINGWORLD   →"


func _open_advanced_knobs() -> void:
	if advanced_popup == null:
		return
	advanced_popup.popup_centered(Vector2i(760, 560))


func _close_advanced_knobs() -> void:
	if advanced_popup != null:
		advanced_popup.hide()


func _apply_default_config_to_controls() -> void:
	_apply_values_to_controls(launch_defaults)


func _apply_values_to_controls(values: Dictionary) -> void:
	suppress_readout_refresh = true
	active_vertical_domain_version = int(values.get("vertical_domain_version", VERTICAL_DOMAIN_VERSION))
	active_protected_roof_thickness_m = int(values.get("protected_roof_thickness_m", CERTIFIED_PROTECTED_ROOF_THICKNESS_M))
	active_lower_hull_thickness_m = int(values.get("lower_hull_thickness_m", CERTIFIED_LOWER_HULL_THICKNESS_M))
	if size_slider != null:
		size_slider.value = _slider_from_radius_km(float(values.get("radius_km", MIN_RADIUS_KM)))
	if ring_width_spin != null:
		ring_width_spin.value = float(values.get("ring_width_km", DEFAULT_RING_WIDTH_KM))
	if terrain_floor_spin != null:
		terrain_floor_spin.value = float(values.get("terrain_floor_y", -256.0))
	if build_height_spin != null:
		build_height_spin.max_value = RUNTIME_SURFACE_BUILD_MAX_Y if str(values.get("terrain_backend", "")) == RUNTIME_SURFACE_BACKEND else MAX_BUILD_MAX_Y
		build_height_spin.value = float(values.get("build_height_y", MAX_BUILD_MAX_Y))
	if structural_depth_spin != null:
		structural_depth_spin.value = float(values.get("structural_depth_km", CERTIFIED_STRUCTURAL_DEPTH_KM))
	if terrain_seed_spin != null:
		terrain_seed_spin.value = float(values.get("terrain_seed", 1337.0))
	if cave_generator_version_spin != null:
		cave_generator_version_spin.value = float(values.get("cave_generator_version", 1.0))
	if base_surface_spin != null:
		base_surface_spin.value = float(values.get("base_surface_height", 16.0))
	if height_amplitude_spin != null:
		height_amplitude_spin.value = float(values.get("height_amplitude", 12.0))
	if detail_amplitude_spin != null:
		detail_amplitude_spin.value = float(values.get("detail_amplitude", 4.0))
	if continental_contrast_spin != null:
		continental_contrast_spin.value = float(values.get("continental_contrast", 1.35))
	if regional_detail_spin != null:
		regional_detail_spin.value = float(values.get("regional_detail_strength", 0.65))
	if rare_peak_spin != null:
		rare_peak_spin.value = float(values.get("rare_peak_height", 1848.0))
	if unified_density_toggle != null:
		unified_density_toggle.button_pressed = bool(values.get("unified_3d_density_terrain", false))
	if designed_caves_toggle != null:
		designed_caves_toggle.button_pressed = bool(values.get("designed_cave_density_composition", false))
	if terrain_backend_selector != null:
		# Missing backend metadata belongs to an existing world and deliberately
		# retains the pre-Terrain3D smooth backend.
		var backend := str(values.get("terrain_backend", "smooth_terrain_v1"))
		terrain_backend_selector.select(2 if backend == RUNTIME_SURFACE_BACKEND else 1 if backend == "terrain3d_surface_v1" else 0)
	suppress_readout_refresh = false
	_update_readout(size_slider.value if size_slider != null else 0.0)


func _update_readout(value: float) -> void:
	var radius_km := _radius_km_from_slider(value)
	var chunks_around := maxi(1, int(round(TAU * radius_km * 1000.0 / CHUNK_ARC_METERS)))
	var circumference_km := chunks_around * CHUNK_ARC_METERS / 1000.0
	var effective_radius_km := circumference_km / TAU
	var ring_width_km := ring_width_spin.value if ring_width_spin != null else DEFAULT_RING_WIDTH_KM
	var terrain_floor := int(round(terrain_floor_spin.value)) if terrain_floor_spin != null else -256
	var build_ceiling := int(round(build_height_spin.value)) if build_height_spin != null else int(MAX_BUILD_MAX_Y)
	var structural_depth_km := structural_depth_spin.value if structural_depth_spin != null else CERTIFIED_STRUCTURAL_DEPTH_KM
	size_label.text = "Radius: %s km requested | %s km exact topology" % [
		_format_distance(radius_km),
		_format_distance(effective_radius_km)
	]
	circumference_label.text = "Circumference: %s km | Width: %s km | Terrain: %d..%d m | Structure: %.1f km" % [
		_format_distance(circumference_km),
		_format_distance(ring_width_km),
		terrain_floor,
		build_ceiling,
		structural_depth_km
	]
	var certified := _matches_certified_dimensions(radius_km, ring_width_km, terrain_floor, build_ceiling, structural_depth_km, _selected_terrain_backend() == RUNTIME_SURFACE_BACKEND)
	_refresh_backend_selector(certified)
	_update_advanced_summary()
	if certified:
		warning_label.text = "CERTIFIED TARGET DIMENSIONS: High targets Arc 140V at 1080p, 60 FPS / 45 FPS minimum."
		warning_label.modulate = Color(0.45, 0.9, 0.68)
	elif radius_km > 2000.0:
		warning_label.text = "Large-world configuration: certification applies only to the named Certified Ring preset."
		warning_label.modulate = Color(1.0, 0.7, 0.3)
	elif radius_km > 500.0:
		warning_label.text = "Large scale: expect reduced distant curvature precision and longer LOD startup."
		warning_label.modulate = Color(1.0, 0.82, 0.35)
	else:
		warning_label.text = "Recommended precision range for one-meter voxels."
		warning_label.modulate = Color(0.55, 0.9, 0.65)


func _update_advanced_summary() -> void:
	if advanced_summary_label == null:
		return

	var seed_value := int(round(terrain_seed_spin.value)) if terrain_seed_spin != null else 1337
	var cave_version_value := int(round(cave_generator_version_spin.value)) if cave_generator_version_spin != null else 1
	var peak_value := int(round(rare_peak_spin.value)) if rare_peak_spin != null else 1848
	var designed_caves_state := "ON" if designed_caves_toggle != null and designed_caves_toggle.button_pressed else "OFF"
	var backend := "Terrain3D runtime generation" if _selected_terrain_backend() == RUNTIME_SURFACE_BACKEND else "Terrain3D surface" if _selected_terrain_backend() == "terrain3d_surface_v1" else "Smooth terrain"
	if _selected_terrain_backend() == RUNTIME_SURFACE_BACKEND:
		advanced_summary_label.text = "Advanced: %s | immutable canonical recipe and seed | terrain Y=-8000..8000 m | sea 0 m | reserved build ceiling 8400 m | caves disabled | existing worlds cannot change generation." % backend
		return
	advanced_summary_label.text = "Advanced: %s | width %s km | terrain Y=%d..%d | sea 0 m | structure %.1f km | shared world seed %d | cave v%d | designed caves %s | routine peaks <=%d m | one seeded summit <=7600 m" % [
		backend,
		_format_distance(ring_width_spin.value if ring_width_spin != null else DEFAULT_RING_WIDTH_KM),
		int(round(terrain_floor_spin.value)) if terrain_floor_spin != null else -256,
		int(round(build_height_spin.value)) if build_height_spin != null else int(MAX_BUILD_MAX_Y),
		structural_depth_spin.value if structural_depth_spin != null else CERTIFIED_STRUCTURAL_DEPTH_KM,
		seed_value,
		cave_version_value,
		designed_caves_state,
		peak_value
	]


func _refresh_backend_selector(certified: bool) -> void:
	if terrain_backend_selector == null:
		return
	var available := certified and not _legacy_backend_forced()
	if available and not selected_slot_has_saved_settings and not terrain_backend_default_applied and not terrain_backend_explicitly_chosen:
		terrain_backend_selector.select(1)
		terrain_backend_default_applied = true
	if _selected_terrain_backend() in ["terrain3d_surface_v1", RUNTIME_SURFACE_BACKEND] and not available:
		terrain_backend_selector.select(0)
	var runtime_existing := selected_slot_has_saved_settings and _selected_terrain_backend() == RUNTIME_SURFACE_BACKEND
	terrain_backend_selector.disabled = not available or runtime_existing
	if size_slider != null:
		size_slider.editable = not runtime_existing
	for control in [ring_width_spin, terrain_floor_spin, build_height_spin, structural_depth_spin, terrain_seed_spin, cave_generator_version_spin, base_surface_spin, height_amplitude_spin, detail_amplitude_spin, continental_contrast_spin, regional_detail_spin, rare_peak_spin]:
		if control != null:
			control.editable = not runtime_existing
	var surface := _selected_terrain_backend() in ["terrain3d_surface_v1", RUNTIME_SURFACE_BACKEND]
	if designed_caves_toggle != null:
		designed_caves_toggle.disabled = surface or runtime_existing
		if surface:
			designed_caves_toggle.button_pressed = false
	if unified_density_toggle != null:
		unified_density_toggle.disabled = surface or runtime_existing
		if surface:
			unified_density_toggle.button_pressed = false


func _matches_certified_dimensions(radius_km: float, width_km: float, floor_y: int, ceiling_y: int, depth_km: float, allow_runtime_surface_ceiling: bool = false) -> bool:
	return is_equal_approx(radius_km, CERTIFIED_RADIUS_KM) \
		and is_equal_approx(width_km, CERTIFIED_RING_WIDTH_KM) \
		and floor_y == CERTIFIED_TERRAIN_FLOOR_Y \
		and (ceiling_y == CERTIFIED_BUILD_MAX_Y or (allow_runtime_surface_ceiling and ceiling_y == RUNTIME_SURFACE_BUILD_MAX_Y)) \
		and is_equal_approx(depth_km, CERTIFIED_STRUCTURAL_DEPTH_KM)


func _matches_legacy_certified_dimensions(radius_km: float, width_km: float, floor_y: int, ceiling_y: int, depth_km: float) -> bool:
	return is_equal_approx(radius_km, CERTIFIED_RADIUS_KM) \
		and is_equal_approx(width_km, CERTIFIED_RING_WIDTH_KM) \
		and floor_y == LEGACY_CERTIFIED_TERRAIN_FLOOR_Y \
		and ceiling_y == CERTIFIED_BUILD_MAX_Y \
		and is_equal_approx(depth_km, CERTIFIED_STRUCTURAL_DEPTH_KM)


func _format_distance(value: float) -> String:
	var nearest := roundf(value)
	if is_equal_approx(value, nearest):
		return "%.0f" % value

	return "%.1f" % value


func _start_game() -> void:
	var selected_cave_version := int(round(cave_generator_version_spin.value)) if cave_generator_version_spin != null else SUPPORTED_CAVE_GENERATOR_VERSION
	if selected_cave_version != SUPPORTED_CAVE_GENERATOR_VERSION:
		warning_label.text = "Cave generator version %d is not supported by this build (supported: v%d)." % [selected_cave_version, SUPPORTED_CAVE_GENERATOR_VERSION]
		warning_label.modulate = Color(1.0, 0.45, 0.35)
		push_error(warning_label.text)
		return
	var is_certified := _matches_certified_dimensions(
		_radius_km_from_slider(size_slider.value),
		ring_width_spin.value if ring_width_spin != null else DEFAULT_RING_WIDTH_KM,
		int(round(terrain_floor_spin.value)) if terrain_floor_spin != null else -256,
		int(round(build_height_spin.value)) if build_height_spin != null else int(MAX_BUILD_MAX_Y),
		structural_depth_spin.value if structural_depth_spin != null else CERTIFIED_STRUCTURAL_DEPTH_KM,
		_selected_terrain_backend() == RUNTIME_SURFACE_BACKEND)
	if not _prepare_runtime_surface_generation(is_certified):
		return
	start_button.disabled = true
	start_button.text = "Configuring world..."
	_save_selected_slot_settings()
	var game := GAME_SCENE.instantiate()
	if mega_ring_selected:
		_configure_mega_ring_game(game)
	else:
		var radius_m := _radius_km_from_slider(size_slider.value) * 1000.0
		_configure_game(game, radius_m)
	if "--certified-walk-precision-probe" in OS.get_cmdline_user_args():
		_configure_certified_walk_precision_probe(game)
	if "--mega-terrain-variation-smoke-test" in OS.get_cmdline_user_args():
		get_tree().quit(0 if mega_terrain_variation_smoke_passed else 1)
		return
	var host := get_parent()
	host.add_child(game)
	var terrain3d_gameplay := game.get_node_or_null("Terrain3DGameplay")
	if terrain3d_gameplay != null:
		terrain3d_gameplay.call_deferred("EnsureInitialized")
	if host == get_tree().root:
		get_tree().current_scene = game
	queue_free()


func _configure_certified_walk_precision_probe(game: Node) -> void:
	# Keep the real smooth terrain and player physics path while removing systems
	# that cannot affect a 160 m walking traversal.
	for node_name in [
		"GlobalTerrainLod3", "AnalyticGlobalRing", "RingCloudLayer",
		"LocalVolumetricCloudRenderer", "RingVolumetricClouds", "SunshineCloudsDriver",
		"SunshineCloudBridge", "CloudProviderController", "LocalWeatherEffects",
		"WeatherAudio", "WeatherAmbience", "WaterFftController", "ScaledFarViewport",
		"RingDayNightCycle", "TerrainMinimap", "PresentationHud", "LodDebugHud",
		"CelestialDebugHud", "LiveTuningDrawer", "CertificationMonitor"
	]:
		var node := game.get_node_or_null(node_name)
		if node != null:
			node.process_mode = Node.PROCESS_MODE_DISABLED
			if node is CanvasItem:
				node.visible = false
	if "--probe-disable-trees" in OS.get_cmdline_user_args():
		var trees := game.get_node_or_null("BiomeDecorations")
		if trees != null:
			trees.set("Enabled", false)
			trees.process_mode = Node.PROCESS_MODE_DISABLED
			trees.visible = false
	var grid := game.get_node("ChunkGridManager")
	var explorer := game.get_node("Explorer") as Node3D
	var circumference := float(grid.call("GetCanonicalRingCircumferenceMeters64"))
	var width := float(grid.get("RingAcrossWidthMeters"))
	explorer.call("SetCanonicalSpawnMeters", circumference * 0.5, 128.0, width * 0.5)
	print("Certified walk precision probe configured at opposite-side canonical spawn; trees=%s." % ("disabled" if "--probe-disable-trees" in OS.get_cmdline_user_args() else "enabled"))


func _configure_mega_ring_game(game: Node) -> void:
	var desktop_mid_high := str(ProjectSettings.get_setting(
		"ringworld/runtime/hardware_tier",
		"laptop_140v_baseline")) == "desktop_mid_high"
	# The raw integration scene defaults the legacy root shifter off so direct
	# smooth-backend runs cannot move colliders away from cached geometry. Mega
	# mode intentionally uses the bridge, so opt that node back in explicitly.
	var floating_origin := game.get_node_or_null("FloatingOrigin")
	if floating_origin != null:
		floating_origin.set("enabled", true)
		floating_origin.process_mode = Node.PROCESS_MODE_INHERIT
	var grid := game.get_node("ChunkGridManager")
	_configure_editable_backend(game, grid)
	grid.call("ConfigureRuntimeRingTopology", MEGA_RING_AROUND_CHUNK_COUNT, MEGA_RING_RADIUS_METERS)
	grid.set("UseRingCurvedPlacement", true)
	grid.set("UseHabitableRingAxisLayout", true)
	grid.set("UseCameraRelativeTopLevelChunks", true)
	grid.set("EnableFloatingOriginBridge", true)
	grid.set("EnableCanonicalFloatingOriginAnchor", true)
	grid.set("RingAcrossWidthMeters", MEGA_RING_WIDTH_METERS)
	grid.set("RingAcrossBaseOffsetMeters", -MEGA_RING_SPAWN_ACROSS_METERS)
	grid.set("StructuralDepthMeters", MEGA_RING_STRUCTURAL_DEPTH_METERS)
	grid.set("GenerateStructuralBedrockEdgeChunks", false)
	grid.set("GenerateStructuralBedrockUndersideChunks", false)
	grid.set("EnforceRingAcrossBounds", true)
	grid.set("MinExcavationGlobalY", -2000)
	grid.set("MaxBuildGlobalY", 8000)
	grid.set("StreamingVisualRadiusAround", 3)
	grid.set("StreamingVisualRadiusAcross", 3)
	grid.set("StreamingVisualRadiusVertical", 3)
	grid.set("StreamingCollisionRadiusAround", 1)
	grid.set("StreamingCollisionRadiusAcross", 1)
	grid.set("StreamingCollisionRadiusVertical", 1)
	grid.set("EnableAltitudeAwareStreamingRadii", true)
	grid.set("SurfaceResidencyEnterAltitudeMeters", 192.0)
	grid.set("SurfaceResidencyExitAltitudeMeters", 384.0)
	grid.set("SurfaceVisualRadiusVertical", 1)
	grid.set("SurfaceCollisionRadiusVertical", 0)
	grid.set("ReleaseUneditedVisualChunkData", true)
	grid.set("SoftVoxelMemoryBudgetMb", 320 if desktop_mid_high else 192)
	grid.set("MemoryPressureVoxelWorkerLimit", 2 if desktop_mid_high else 1)
	grid.set("MaxChunkLoadsPerFrame", 4)
	grid.set("HideLocalVoxelMeshesDuringFastTravel", false)
	var runtime_terrain_settings := _build_runtime_terrain_settings(grid, -2000, 8000, -2000)
	if runtime_terrain_settings != null:
		_apply_mega_inland_sea_terrain_profile(runtime_terrain_settings)
		grid.set("TerrainSettings", runtime_terrain_settings)
		grid.set("UseSmoothNaturalTerrainPilot", not _use_smooth_editable_backend() and bool(runtime_terrain_settings.get("EnableUnified3dDensityTerrain")))
		grid.call("RefreshRuntimeTerrainDefinition")
		if "--mega-terrain-variation-smoke-test" in OS.get_cmdline_user_args():
			var terrain_audit: Dictionary = grid.call(
				"CaptureTerrainVariationAudit",
				0,
				int(MEGA_RING_SPAWN_ACROSS_METERS),
				25000000,
				4)
			var height_range := int(terrain_audit.get("height_range", 0))
			var water_samples := int(terrain_audit.get("water_samples", 0))
			var total_samples := int(terrain_audit.get("total_samples", 0))
			mega_terrain_variation_smoke_passed = bool(terrain_audit.get("available", false)) and height_range >= 1000 and water_samples > 0
			if not mega_terrain_variation_smoke_passed:
				push_error("Mega terrain variation smoke test failed: %s" % terrain_audit)
			else:
				print("Mega terrain variation smoke test passed: range=%dm, water=%d/%d." % [height_range, water_samples, total_samples])

	var lod_coordinator := game.get_node("TerrainLodCoordinator")
	lod_coordinator.call("ConfigureMegaRegionalExtension")
	lod_coordinator.set("KeepCoarseProxiesStreamingDuringFastTravel", false)
	lod_coordinator.set("HideProxyMeshesDuringFastTravel", false)
	lod_coordinator.set("MaximumStagedMeshMemoryMiB", 160 if desktop_mid_high else 96)
	lod_coordinator.set("MemoryPressureWorkerLimit", 2 if desktop_mid_high else 1)

	var explorer := game.get_node("Explorer") as Node3D
	explorer.position = Vector3.ZERO
	# Spawn inside the surface chunk band so collision terrain is requested on
	# the first streaming pass; the explorer resolves the exact terrain height.
	explorer.call("SetCanonicalSpawnMeters", 0.0, 32.0, MEGA_RING_SPAWN_ACROSS_METERS)
	explorer.set("CrossRingPreloadHeightMeters", 1000000.0)
	# Mega Ring spans hundreds of billions of metres. Its top noclip tier is
	# 1,000,000 km/s (1,000,000,000 m/s); normal-size worlds keep their usual cap.
	explorer.set("NoclipMaximumSpeed", 1000000000.0)
	explorer.set("NoclipWarpSpeed", 1000000000.0)
	explorer.set("NoclipTransRingSpeed", 1000000000.0)
	# Prime the top noclip tier for Mega Ring without enabling flight. Press V
	# to enter noclip, then the initial target is already 1,000,000 km/s.
	explorer.call("SelectNoclipSpeedTier", 4)
	var camera := game.get_node("Explorer/Head/Camera3D") as Camera3D
	# Keep Forward+ light culling in its stable range. The scaled far viewport
	# supplies the whole-ring backdrop beyond this regional presentation bubble.
	camera.far = FAR_FIELD_CAMERA_LIMIT_METERS

	for node_path in [
		"RingWorld",
		"GlobalTerrainLod3",
		"AnalyticGlobalRing",
		"RingCloudLayer",
		"RingVolumetricClouds",
		"SunshineCloudsDriver",
		"SunshineCloudBridge",
		"LocalVolumetricCloudRenderer",
		"LocalWeatherEffects"
	]:
		var legacy_node := game.get_node_or_null(node_path)
		if legacy_node == null:
			continue
		legacy_node.process_mode = Node.PROCESS_MODE_DISABLED
		if legacy_node is Node3D:
			legacy_node.visible = false

	# LOD1-8 remain in the local camera-relative world. LOD9-11 are deliberately
	# excluded: the scaled viewport builds those far tiers after subtracting the
	# current canonical anchor in double precision.
	for node_path in [
		"Lod1Terrain2m",
		"Lod2Terrain4m",
		"Lod3Terrain8m",
		"Lod4Terrain16m",
		"Lod5Terrain32m",
		"Lod6Terrain64m",
		"Lod7Terrain128m",
		"Lod8Terrain256m",
		"TerrainLodCoordinator"
	]:
		var regional_node := game.get_node_or_null(node_path)
		if regional_node == null:
			continue
		regional_node.process_mode = Node.PROCESS_MODE_INHERIT
		if regional_node is Node3D:
			regional_node.visible = true
		if node_path != "TerrainLodCoordinator":
			regional_node.set("RingWidthMeters", int(MEGA_RING_WIDTH_METERS))
			regional_node.set("BuildStructuralEdgeWalls", false)
			regional_node.set("BuildStructuralUnderside", false)
			regional_node.set("RebuildExistingMeshesOnCanonicalAnchorChange", false)
			regional_node.set("MaximumStagedMeshMemoryMiB", 32 if desktop_mid_high else 20)

	for node_path in ["Lod9Terrain2km", "Lod10Terrain8km", "Lod11Terrain32km"]:
		var far_lod_node := game.get_node_or_null(node_path)
		if far_lod_node == null:
			continue
		far_lod_node.process_mode = Node.PROCESS_MODE_DISABLED
		if far_lod_node is Node3D:
			far_lod_node.visible = false

	var scaled_far_viewport := game.get_node("ScaledFarViewport")
	# The graphics controller applies its default preset after this launcher
	# configures the game. Mark Mega so that controller preserves this dedicated
	# whole-ring backdrop target instead of restoring its normal preset mesh.
	game.get_node("GraphicsQuality").set("UseMegaRegionalFarBackdrop", true)
	scaled_far_viewport.set("Enabled", true)
	scaled_far_viewport.set("RegionalFarLodsEnabled", true)
	scaled_far_viewport.set("CoordinateScale", 0.0000001)
	scaled_far_viewport.set("ResolutionScale", 0.5)
	scaled_far_viewport.set("AroundSegments", 2048)
	scaled_far_viewport.set("AcrossSegments", 32)
	scaled_far_viewport.set("RingWidthMeters", int(MEGA_RING_WIDTH_METERS))
	scaled_far_viewport.set("StructuralDepthMeters", MEGA_RING_STRUCTURAL_DEPTH_METERS)
	scaled_far_viewport.set("StructuralWallTopMeters", 10000.0)
	scaled_far_viewport.set("CloudTransmittanceMaskEnabled", false)
	scaled_far_viewport.set("LocalVolumetricCloudsEnabled", false)
	scaled_far_viewport.set("KeepLocalTerrainFallbackVisible", false)
	# LOD9-11 now render in this scaled viewport up to 7.96 Mm. Keep the complete
	# ring mesh outside that regional band so unlike-resolution surfaces never
	# overlap or z-fight.
	scaled_far_viewport.set("FadeStartMeters", 7960000.0)
	scaled_far_viewport.set("FadeEndMeters", 7960000.0)
	# At intercontinental and inter-ring flight speeds no local mesh can be
	# current. Present only the stable whole-ring view until the player slows.
	scaled_far_viewport.set("RegionalTransitSpeedThresholdMetersPerSecond", 100000.0)
	scaled_far_viewport.set("RegionalTransitResumeSpeedMetersPerSecond", 10000.0)

	var instructions := game.get_node_or_null("Instructions") as Label
	if instructions != null:
		instructions.text = "MEGA RING REGIONAL STREAMING\nEditable terrain and sparse clipmaps follow your canonical position."
	print("Mega Ring local bubble configured: chunks=%d, radius=%.0f km, spawnAcross=%.0f km" % [
		MEGA_RING_AROUND_CHUNK_COUNT,
		MEGA_RING_RADIUS_METERS / 1000.0,
		MEGA_RING_SPAWN_ACROSS_METERS / 1000.0
	])


func _apply_mega_inland_sea_terrain_profile(settings: Resource) -> void:
	# This profile lives on the duplicated launch resource, preserving the
	# normal/certified presets and keeping Mega tuning inspector-friendly.
	settings.set("UsePrecisionSafePeriodicNoiseTiles", true)
	settings.set("BaseSurfaceHeightVoxels", 72)
	settings.set("HeightAmplitudeVoxels", 680)
	settings.set("BaseFrequency", 0.00002)
	settings.set("DetailAmplitudeVoxels", 120)
	settings.set("DetailFrequency", 0.00012)
	settings.set("ContinentalFrequency", 0.000001)
	settings.set("ErosionFrequency", 0.000003)
	settings.set("RidgeFrequency", 0.000002)
	settings.set("MountainWarpFrequency", 0.000001)
	settings.set("ValleyFrequency", 0.000003)
	settings.set("ContinentalContrast", 1.22)
	settings.set("RegionalDetailStrength", 0.82)
	settings.set("LowlandHighY", 600)
	settings.set("HighlandHighY", 1900)
	settings.set("MountainFoundationHighY", 3400)
	settings.set("RarePeakY", 6200)
	settings.set("EnableEngineeredMacroTerrain", true)
	settings.set("InlandSeaHalfWidthMeters", MEGA_INLAND_SEA_HALF_WIDTH_METERS)
	settings.set("InlandSeaShoreTransitionMeters", 30000000.0)
	settings.set("CoastalTerraceDistanceMeters", 60000000.0)
	settings.set("CoastalTerraceHalfWidthMeters", 18000000.0)
	settings.set("CoastalTerraceHeightMeters", 520)
	settings.set("ArcEscarpmentHeightMeters", 420)
	settings.set("ArcEscarpmentWavelengthMeters", 850000000.0)


func _save_selected_slot_settings() -> void:
	var session := get_node_or_null("/root/WorldSession")
	if session == null:
		return
	var runtime_surface := _selected_terrain_backend() == RUNTIME_SURFACE_BACKEND
	var settings := {
		"radius_km": _radius_km_from_slider(size_slider.value),
		"ring_width_km": ring_width_spin.value if ring_width_spin != null else DEFAULT_RING_WIDTH_KM,
		"terrain_floor_y": terrain_floor_spin.value if terrain_floor_spin != null else -256.0,
		"build_height_y": RUNTIME_SURFACE_BUILD_MAX_Y if runtime_surface else build_height_spin.value if build_height_spin != null else MAX_BUILD_MAX_Y,
		"structural_depth_km": structural_depth_spin.value if structural_depth_spin != null else CERTIFIED_STRUCTURAL_DEPTH_KM,
		"vertical_domain_version": active_vertical_domain_version,
		"protected_roof_thickness_m": active_protected_roof_thickness_m,
		"lower_hull_thickness_m": active_lower_hull_thickness_m,
		"terrain_seed": terrain_seed_spin.value if terrain_seed_spin != null else 1337.0,
		"cave_generator_version": cave_generator_version_spin.value if cave_generator_version_spin != null else 1.0,
		"base_surface_height": base_surface_spin.value if base_surface_spin != null else 16.0,
		"height_amplitude": height_amplitude_spin.value if height_amplitude_spin != null else 12.0,
		"detail_amplitude": detail_amplitude_spin.value if detail_amplitude_spin != null else 4.0,
		"continental_contrast": continental_contrast_spin.value if continental_contrast_spin != null else 1.35,
		"regional_detail_strength": regional_detail_spin.value if regional_detail_spin != null else 0.65,
		"rare_peak_height": rare_peak_spin.value if rare_peak_spin != null else 1848.0,
		"unified_3d_density_terrain": unified_density_toggle.button_pressed if unified_density_toggle != null else false,
		"designed_cave_density_composition": designed_caves_toggle.button_pressed if designed_caves_toggle != null else false,
		"terrain_backend": _selected_terrain_backend()
	}
	if runtime_surface:
		settings["surface_definition_version"] = 1
		settings["surface_generation_contract_id"] = pending_surface_generation_contract_id
		settings["surface_generation_recipe_json"] = pending_surface_generation_recipe_json
	session.save_active_world_settings(settings)


func _prepare_runtime_surface_generation(is_certified: bool) -> bool:
	pending_surface_generation_recipe_json = ""
	pending_surface_generation_contract_id = ""
	var saved := _get_selected_slot_settings()
	var stored_runtime_surface := str(saved.get("terrain_backend", "")) == RUNTIME_SURFACE_BACKEND
	if stored_runtime_surface and _selected_terrain_backend() != RUNTIME_SURFACE_BACKEND:
		return _reject_runtime_surface_launch("This existing runtime surface world has immutable backend and generation settings.")
	if _selected_terrain_backend() != RUNTIME_SURFACE_BACKEND:
		return true
	if not is_certified:
		return _reject_runtime_surface_launch("Runtime surface generation is available only for Certified Ring dimensions.")
	var bridge := TERRAIN_GENERATION_SAMPLER.new()
	if selected_slot_has_saved_settings:
		if not stored_runtime_surface:
			bridge.call("Release")
			return _reject_runtime_surface_launch("An existing non-runtime world cannot be converted to runtime surface generation.")
		if not _runtime_surface_controls_match_saved(saved):
			bridge.call("Release")
			return _reject_runtime_surface_launch("Runtime surface world generation settings differ from its immutable saved definition.")
		if int(saved.get("surface_definition_version", 0)) != 1 \
			or str(saved.get("surface_generation_contract_id", "")) != RUNTIME_SURFACE_CONTRACT:
			bridge.call("Release")
			return _reject_runtime_surface_launch("This runtime surface world is missing required immutable generation metadata.")
		var stored_json := str(saved.get("surface_generation_recipe_json", ""))
		if stored_json.is_empty() or not bool(bridge.call("ConfigureJson", stored_json)):
			var error := str(bridge.get("LastError"))
			bridge.call("Release")
			return _reject_runtime_surface_launch("Stored runtime surface recipe is invalid: %s" % error)
		if str(bridge.get("CanonicalJson")) != stored_json:
			bridge.call("Release")
			return _reject_runtime_surface_launch("Stored runtime surface recipe is not canonical.")
		var parsed_recipe: Variant = JSON.parse_string(stored_json)
		var stored_seed := int((parsed_recipe as Dictionary).get("seed", -1)) if parsed_recipe is Dictionary else -1
		if stored_seed != int(round(float(saved.get("terrain_seed", -1)))):
			bridge.call("Release")
			return _reject_runtime_surface_launch("Stored runtime surface recipe seed does not match the immutable world seed.")
		pending_surface_generation_recipe_json = stored_json
		pending_surface_generation_contract_id = RUNTIME_SURFACE_CONTRACT
		bridge.call("Release")
		return true
	var loaded: Dictionary = SURFACE_RECIPE_STORE.load_recipe()
	if not str(loaded.get("error", "")).is_empty():
		bridge.call("Release")
		return _reject_runtime_surface_launch("Saved preview recipe is invalid: %s" % str(loaded.get("error", "")))
	var recipe := loaded.get("recipe", null) as Resource
	if recipe == null:
		recipe = SURFACE_RECIPE.create_erosion_default()
	recipe.set("seed", int(round(terrain_seed_spin.value)) if terrain_seed_spin != null else 1337)
	if not bool(bridge.call("Configure", recipe.call("to_normalized_dictionary"))):
		var error := str(bridge.get("LastError"))
		bridge.call("Release")
		return _reject_runtime_surface_launch("Preview surface recipe is invalid: %s" % error)
	pending_surface_generation_recipe_json = str(bridge.get("CanonicalJson"))
	pending_surface_generation_contract_id = RUNTIME_SURFACE_CONTRACT
	bridge.call("Release")
	return not pending_surface_generation_recipe_json.is_empty()


func _reject_runtime_surface_launch(message: String) -> bool:
	warning_label.text = message
	warning_label.modulate = Color(1.0, 0.45, 0.35)
	push_error(message)
	return false


func _runtime_surface_slot_locked() -> bool:
	return selected_slot_has_saved_settings and str(_get_selected_slot_settings().get("terrain_backend", "")) == RUNTIME_SURFACE_BACKEND


func _runtime_surface_controls_match_saved(saved: Dictionary) -> bool:
	var expected := {
		"radius_km": _radius_km_from_slider(size_slider.value),
		"ring_width_km": ring_width_spin.value if ring_width_spin != null else DEFAULT_RING_WIDTH_KM,
		"terrain_floor_y": terrain_floor_spin.value if terrain_floor_spin != null else CERTIFIED_TERRAIN_FLOOR_Y,
		"build_height_y": build_height_spin.value if build_height_spin != null else RUNTIME_SURFACE_BUILD_MAX_Y,
		"structural_depth_km": structural_depth_spin.value if structural_depth_spin != null else CERTIFIED_STRUCTURAL_DEPTH_KM,
		"terrain_seed": terrain_seed_spin.value if terrain_seed_spin != null else 1337.0,
		"cave_generator_version": cave_generator_version_spin.value if cave_generator_version_spin != null else 1.0,
		"base_surface_height": base_surface_spin.value if base_surface_spin != null else 16.0,
		"height_amplitude": height_amplitude_spin.value if height_amplitude_spin != null else 12.0,
		"detail_amplitude": detail_amplitude_spin.value if detail_amplitude_spin != null else 4.0,
		"continental_contrast": continental_contrast_spin.value if continental_contrast_spin != null else 1.35,
		"regional_detail_strength": regional_detail_spin.value if regional_detail_spin != null else 0.65,
		"rare_peak_height": rare_peak_spin.value if rare_peak_spin != null else 1848.0
	}
	for key in expected:
		if not is_equal_approx(float(expected[key]), float(saved.get(key, NAN))):
			return false
	return not bool(saved.get("unified_3d_density_terrain", false)) and not bool(saved.get("designed_cave_density_composition", false))


func _configure_game(game: Node, requested_radius_m: float) -> void:
	# Regional HLOD owns normal depth through 850-1000 km. Beyond that, the
	# certified ring is rendered uniformly scaled in an isolated far viewport.
	var width_km := ring_width_spin.value if ring_width_spin != null else DEFAULT_RING_WIDTH_KM
	var width_m := maxf(1000.0, width_km * 1000.0)
	var min_terrain_y := int(round(terrain_floor_spin.value)) if terrain_floor_spin != null else -256
	var max_build_value := build_height_spin.value if build_height_spin != null else float(MAX_BUILD_MAX_Y)
	var max_build_y := int(round(max_build_value))
	var structural_depth_m := (structural_depth_spin.value if structural_depth_spin != null else CERTIFIED_STRUCTURAL_DEPTH_KM) * 1000.0
	var chunks_around := maxi(1, int(round(TAU * requested_radius_m / CHUNK_ARC_METERS)))
	var circumference_m := chunks_around * CHUNK_ARC_METERS
	var radius_m := circumference_m / TAU
	var is_certified := _matches_certified_dimensions(
		requested_radius_m / 1000.0,
		width_km,
		min_terrain_y,
		max_build_y,
		structural_depth_m / 1000.0,
		_selected_terrain_backend() == RUNTIME_SURFACE_BACKEND)
	var bedrock_material_y := CERTIFIED_BEDROCK_MATERIAL_Y if is_certified else min_terrain_y
	var use_surface_terrain := _surface_backend_selected_for_certified(is_certified)
	var use_runtime_surface_generation := _runtime_surface_backend_selected_for_certified(is_certified)
	if use_runtime_surface_generation:
		# Validate Certified dimensions before reserving the surface-only build band.
		max_build_y = RUNTIME_SURFACE_BUILD_MAX_Y
	var desktop_mid_high := str(ProjectSettings.get_setting(
		"ringworld/runtime/hardware_tier",
		"laptop_140v_baseline")) == "desktop_mid_high"

	var ring_world := game.get_node("RingWorld")
	ring_world.set("ring_radius", radius_m)
	ring_world.set("ring_width", width_m)
	ring_world.set("build_far_ring_lod", false)

	var grid := game.get_node("ChunkGridManager")
	_configure_editable_backend(game, grid)
	# Normal and certified worlds use Mega's canonical camera-local frame. The
	# legacy RingWorld/floating-origin pair derives gameplay transforms from the
	# physical radius in floats, so it cannot participate in this frame.
	grid.set("EnableFloatingOriginBridge", false)
	grid.set("EnableCanonicalFloatingOriginAnchor", true)
	grid.set("AroundRingChunkLength", chunks_around)
	grid.set("DeriveRingRadiusFromChunkLength", true)
	grid.set("RingRadiusMeters", radius_m)
	grid.set("RingAcrossWidthMeters", width_m)
	grid.set("RingAcrossBaseOffsetMeters", -width_m * 0.5)
	grid.set("MinExcavationGlobalY", min_terrain_y)
	grid.set("TerrainBottomGlobalY", min_terrain_y)
	grid.set("ProtectedRoofThicknessMeters", active_protected_roof_thickness_m)
	grid.set("LowerHullThicknessMeters", active_lower_hull_thickness_m)
	grid.set("MaxBuildGlobalY", max_build_y)
	grid.set("StructuralDepthMeters", structural_depth_m)
	grid.set("StructuralWallClearanceMeters", CERTIFIED_STRUCTURAL_WALL_CLEARANCE_M)
	var capture_max_quality := "--shorts-max-quality" in OS.get_cmdline_user_args()
	grid.set("StreamingVisualRadiusAround", 4 if desktop_mid_high else 3)
	grid.set("StreamingVisualRadiusAcross", 4 if desktop_mid_high else 3)
	grid.set("StreamingVisualRadiusVertical", 3 if desktop_mid_high else 2)
	grid.set("StreamingCollisionRadiusAround", 1)
	grid.set("StreamingCollisionRadiusAcross", 1)
	grid.set("StreamingCollisionRadiusVertical", 1)
	grid.set("EnableAltitudeAwareStreamingRadii", true)
	grid.set("SurfaceResidencyEnterAltitudeMeters", 192.0)
	grid.set("SurfaceResidencyExitAltitudeMeters", 384.0)
	grid.set("SurfaceVisualRadiusVertical", 1)
	grid.set("SurfaceCollisionRadiusVertical", 0)
	grid.set("ReleaseUneditedVisualChunkData", true)
	grid.set("SoftVoxelMemoryBudgetMb", 384 if desktop_mid_high else 192)
	grid.set("MemoryPressureVoxelWorkerLimit", 2 if desktop_mid_high else 1)
	if capture_max_quality:
		grid.set("MaxConcurrentVoxelMeshWorkers", 4)
		grid.set("MemoryPressureVoxelWorkerLimit", 3)
		grid.set("MaxVoxelMeshUploadsPerFrame", 2)
		grid.set("MaxVoxelMeshUploadMillisecondsPerFrame", 4.0)
	if "--precision-smoke-test" in OS.get_cmdline_user_args():
		grid.set("EnableSeamGapDebugVisualization", true)
		grid.set("LogSeamGapDebugStats", true)
		grid.set("SeamGapDebugUpdateIntervalSeconds", 0.1)
	_configure_terrain_lod_widths(game, width_m)

	var runtime_terrain_settings := _build_runtime_terrain_settings(grid, min_terrain_y, max_build_y, bedrock_material_y, use_surface_terrain)
	if runtime_terrain_settings != null:
		if use_runtime_surface_generation:
			runtime_terrain_settings.set("BedrockY", -8000)
			runtime_terrain_settings.set("SeaLevelY", 0)
			runtime_terrain_settings.set("SurfaceGenerationRecipeJson", pending_surface_generation_recipe_json)
			runtime_terrain_settings.set("SurfaceGenerationContractId", RUNTIME_SURFACE_CONTRACT)
			runtime_terrain_settings.set("EnableProceduralCaves", false)
			runtime_terrain_settings.set("EnableDesignedCaveDensityComposition", false)
			runtime_terrain_settings.set("EnableUnified3dDensityTerrain", false)
			runtime_terrain_settings.set("EnableNaturalOverhangs", false)
		grid.set("TerrainSettings", runtime_terrain_settings)
		grid.set("UseSmoothNaturalTerrainPilot", not use_surface_terrain and not _use_smooth_editable_backend() and bool(runtime_terrain_settings.get("EnableUnified3dDensityTerrain")))
		grid.call("RefreshRuntimeTerrainDefinition")

	var explorer := game.get_node("Explorer") as Node3D
	if use_surface_terrain and runtime_terrain_settings != null:
		_configure_certified_surface_terrain(game, grid, explorer, runtime_terrain_settings)
	explorer.position = Vector3.ZERO
	explorer.call("SetCanonicalSpawnMeters", 0.0, 128.0, width_m * 0.5)
	_disable_legacy_radius_space_nodes(game)
	explorer.set("CrossRingPreloadHeightMeters", minf(1000000.0, radius_m * 0.5))
	explorer.set("NoclipWarpSpeed", clampf(radius_m * 2.0 / 60.0, 1200.0, 250000.0))
	explorer.set("NoclipTransRingSpeed", clampf(radius_m * 2.0 / 15.0, 5000.0, 1000000.0))
	var camera := game.get_node("Explorer/Head/Camera3D") as Camera3D
	camera.far = maxf(220000.0, minf(FAR_FIELD_CAMERA_LIMIT_METERS, radius_m * 2.5))
	var star_light := game.get_node("StarLight")
	# Keep detailed PSSM shadows local enough for an 8 GB RTX 4070 while
	# leaving mountain-scale distance to the much cheaper whole-ring mask.
	# Offline shorts get the wider 16 km bubble; certified realtime play uses
	# 12 km, which is a sustainable 3x increase over the old cutoff.
	var cascade_shadow_distance := 16000.0 if capture_max_quality else 12000.0
	star_light.set("directional_shadow_max_distance", cascade_shadow_distance)
	star_light.set("directional_shadow_split_1", 0.012)
	star_light.set("directional_shadow_split_2", 0.06)
	star_light.set("directional_shadow_split_3", 0.28)
	star_light.set("directional_shadow_fade_start", 0.88)
	star_light.set("directional_shadow_blend_splits", true)
	var world_environment := game.get_node("WorldEnvironment") as WorldEnvironment
	if world_environment != null and world_environment.environment != null:
		world_environment.environment.ssr_max_steps = 128 if desktop_mid_high else 96

	var global_lod := game.get_node("GlobalTerrainLod3")
	if "--certification-benchmark" in OS.get_cmdline_user_args():
		# Automated runs may execute in restricted build sandboxes where user://
		# is intentionally read-only. The benchmark needs deterministic terrain,
		# not a persistent cache write.
		global_lod.set("EnableMinMaxDiskCache", false)
	global_lod.set("StructuralDepthMeters", structural_depth_m)
	# The macro mask takes over before the final cascade fades and continues
	# tracing mountain silhouettes far beyond realtime shadow-map range.
	global_lod.set("MacroTerrainShadowStrength", 0.52 if capture_max_quality else 0.46)
	global_lod.set("MacroTerrainShadowFadeStartMeters", 9000.0 if capture_max_quality else 6500.0)
	global_lod.set("MacroTerrainShadowFadeEndMeters", 15000.0 if capture_max_quality else 11000.0)
	global_lod.set("MacroTerrainShadowTraceSteps", 16 if capture_max_quality else 14)
	global_lod.set("MacroTerrainShadowMaxTraceMeters", 160000.0 if capture_max_quality else 128000.0)
	var analytic_global_lod := game.get_node("AnalyticGlobalRing")
	analytic_global_lod.set("RingWidthMeters", int(round(width_m)))
	analytic_global_lod.set("StructuralDepthMeters", structural_depth_m)
	analytic_global_lod.set("EnableFarSpace", true)
	analytic_global_lod.set("FarSpaceStartMeters", FAR_FIELD_START_METERS)
	analytic_global_lod.set("FarSpaceRenderEndMeters", FAR_FIELD_RENDER_END_METERS)
	var cloud_layer := game.get_node("RingCloudLayer")
	cloud_layer.set("EnableFarSpace", true)
	cloud_layer.set("FarSpaceStartMeters", FAR_FIELD_START_METERS)
	cloud_layer.set("FarSpaceRenderEndMeters", FAR_FIELD_RENDER_END_METERS)
	var mesh_clouds := game.get_node("RingVolumetricClouds")
	# The historically named RingVolumetricClouds node now owns the restored
	# merged-mesh, transition-mesh, projected-shadow, and curved-shell hierarchy.
	mesh_clouds.set("Quality", 3 if capture_max_quality else (2 if desktop_mid_high else 1))
	mesh_clouds.set("Enabled", true)
	mesh_clouds.set("ConventionalShadowRetentionRadiusMeters", 12000.0 if capture_max_quality else 10000.0)
	mesh_clouds.set("ShadowMapResolution", 1024 if capture_max_quality else 768)
	mesh_clouds.set("ProjectedShadowStrength", 0.62 if capture_max_quality else 0.56)
	mesh_clouds.set("ProjectedShadowFarStrength", 0.74 if capture_max_quality else 0.68)
	mesh_clouds.set("ProjectedShadowNearStartMeters", 4500.0)
	mesh_clouds.set("ProjectedShadowNearEndMeters", 10000.0 if capture_max_quality else 8500.0)
	mesh_clouds.set("ProjectedShadowFarStrengthStartMeters", 12000.0 if capture_max_quality else 10000.0)
	mesh_clouds.set("ProjectedShadowFarStrengthEndMeters", 30000.0)
	var sunshine_clouds := game.get_node_or_null("SunshineCloudBridge")
	if sunshine_clouds != null:
		# Startup baseline only. Graphics quality and saved gameplay cloud
		# overrides apply after the scene enters the tree.
		sunshine_clouds.set("CloudResolutionScale", 0)
		sunshine_clouds.set("RaymarchSteps", 128)
		sunshine_clouds.set("DitherSpeed", 0.0)
		sunshine_clouds.set("BlurPower", 0.1)
	var scaled_far_viewport := game.get_node("ScaledFarViewport")
	scaled_far_viewport.set("Enabled", is_certified)
	# Certified uses the complete scaled ring only. Camera-relative LOD9-11 are
	# Mega-specific and would duplicate Certified's primary regional LOD nodes.
	scaled_far_viewport.set("RegionalFarLodsEnabled", false)
	scaled_far_viewport.set("RingWidthMeters", int(round(width_m)))
	scaled_far_viewport.set("StructuralDepthMeters", structural_depth_m)
	scaled_far_viewport.set("StructuralWallTopMeters", max_build_y + CERTIFIED_STRUCTURAL_WALL_CLEARANCE_M)
	scaled_far_viewport.set("ResolutionScale", 1.0 if capture_max_quality or desktop_mid_high else 0.75)
	scaled_far_viewport.set("AroundSegments", 2048 if capture_max_quality else (1536 if desktop_mid_high else 1024))
	scaled_far_viewport.set("AcrossSegments", 32 if capture_max_quality else (24 if desktop_mid_high else 16))
	# Keep the far-backdrop cloud transmittance path disabled. Its screen-space
	# Sunshine texture arrives a frame late and leaves dark cloud afterimages
	# during camera motion.
	scaled_far_viewport.set("CloudTransmittanceMaskEnabled", false)
	scaled_far_viewport.set("CloudMaskResolutionScale", 0.5 if capture_max_quality else (0.35 if desktop_mid_high else 0.25))
	scaled_far_viewport.set("CloudMaskUpdatesPerSecond", 30.0 if capture_max_quality else (20.0 if desktop_mid_high else 15.0))
	scaled_far_viewport.set("CloudMaskRaymarchSteps", 18 if capture_max_quality else (14 if desktop_mid_high else 10))
	scaled_far_viewport.set("CloudMaskMaximumQualityLevel", 3 if capture_max_quality else (2 if desktop_mid_high else 1))
	analytic_global_lod.set("BuildEnabled", not is_certified)
	cloud_layer.set("BuildEnabled", not is_certified)
	var scaled_segments := int(round(512.0 * sqrt(radius_m / 80000.0)))
	global_lod.set("AroundSegments", clampi(scaled_segments, 512, 2048))
	var scaled_occluder_segments := int(round(128.0 * sqrt(radius_m / 80000.0)))
	global_lod.set("OccluderAroundSegments", clampi(scaled_occluder_segments, 128, 512))
	if is_certified:
		# These are the Medium baselines. The coordinator scales High to
		# 4096x128 and Very High to the current 4096x256 safety cap.
		global_lod.set("AroundSegments", 2048)
		global_lod.set("AcrossSegments", 64)
		global_lod.set("GlobalSegmentCount", 128)
		# Regional structural walls bridge the final proxy tier to the scaled
		# whole-ring background. They are segmented with the terrain HLOD so no
		# giant full-ring wall enters one main-camera draw instance.
		global_lod.set("BuildStructuralEdgeWalls", true)
		global_lod.set("EnableFarDistanceFade", true)
		global_lod.set("FarFadeStartMeters", 500000.0)
		global_lod.set("FarFadeEndMeters", 850000.0)
		global_lod.set("RegionalVisibilityDistanceMeters", 1000000.0)
		analytic_global_lod.set("AroundSegments", 1024)
		analytic_global_lod.set("AcrossSegments", 16)
		analytic_global_lod.set("FadeStartMeters", 500000.0)
		analytic_global_lod.set("FadeEndMeters", 750000.0)
	var lod_coordinator := game.get_node("TerrainLodCoordinator")
	# The laptop tier previously started on High, which activates two extra
	# proxy rings and an 8K shadow atlas. Medium is the sustainable default;
	# High remains available from the graphics menu.
	var selected_graphics_preset := 4 if capture_max_quality else (3 if desktop_mid_high else 1)
	lod_coordinator.set("MaximumStagedMeshMemoryMiB", 320 if capture_max_quality else (160 if desktop_mid_high else 96))
	lod_coordinator.set("MemoryPressureWorkerLimit", 3 if capture_max_quality else (2 if desktop_mid_high else 1))
	lod_coordinator.set("TerrainShadowCoverageMeters", cascade_shadow_distance)
	lod_coordinator.set(
		"DefaultPreset",
		selected_graphics_preset)
	var graphics_quality := game.get_node("GraphicsQuality")
	graphics_quality.set("DefaultPreset", selected_graphics_preset)
	var pause_menu := game.get_node("LodPauseMenu")
	pause_menu.set("DefaultChunkRadius", 7)
	pause_menu.set("DefaultVerticalChunkRadius", 7)

	var instructions := game.get_node("Instructions") as Label
	var radius_km := radius_m / 1000.0
	var configured_width_km := width_m / 1000.0
	var min_build_y := int(grid.get("MinExcavationGlobalY"))
	instructions.text = instructions.text.replace("80 km radius", "%s km radius" % _format_distance(radius_km))
	instructions.text = instructions.text.replace("20 km width", "%s km width" % _format_distance(configured_width_km))
	instructions.text = instructions.text.replace("Y=-256..2048", "Y=%d..%d" % [min_build_y, max_build_y])
	instructions.text += "\nK mesh cloud A/B (GPU timing prints to console)"
	instructions.text += " | J ambient occlusion A/B"

	if is_certified:
		_validate_certified_runtime_configuration(
			grid,
			global_lod,
			analytic_global_lod,
			cloud_layer,
			scaled_far_viewport,
			camera,
			int(round(terrain_seed_spin.value)) if terrain_seed_spin != null else 1337,
			int(round(cave_generator_version_spin.value)) if cave_generator_version_spin != null else SUPPORTED_CAVE_GENERATOR_VERSION,
			chunks_around,
			circumference_m,
			radius_m,
			use_runtime_surface_generation)


func _disable_legacy_radius_space_nodes(game: Node) -> void:
	for node_path in ["FloatingOrigin", "RingWorld"]:
		var legacy_node := game.get_node_or_null(node_path)
		if legacy_node == null:
			continue
		legacy_node.process_mode = Node.PROCESS_MODE_DISABLED
		if legacy_node is Node3D:
			legacy_node.visible = false


func _validate_certified_runtime_configuration(
	grid: Node,
	global_lod: Node,
	analytic_global_lod: Node,
	cloud_layer: Node,
	scaled_far_viewport: Node,
	camera: Camera3D,
	expected_seed: int,
	expected_cave_version: int,
	chunks_around: int,
	circumference_m: float,
	effective_radius_m: float,
	runtime_surface_generation: bool = false) -> void:
	var expected_chunks := int(round(TAU * CERTIFIED_RADIUS_KM * 1000.0 / CHUNK_ARC_METERS))
	var expected_build_ceiling := RUNTIME_SURFACE_BUILD_MAX_Y if runtime_surface_generation else CERTIFIED_BUILD_MAX_Y
	var expected_wall_top := expected_build_ceiling + CERTIFIED_STRUCTURAL_WALL_CLEARANCE_M
	var cave_settings: Variant = grid.get("TerrainSettings").get("DesignedCaveGenerationContractSettings") if grid.get("TerrainSettings") != null else null
	var valid := chunks_around == expected_chunks \
		and active_vertical_domain_version == VERTICAL_DOMAIN_VERSION \
		and int(grid.get("AroundRingChunkLength")) == expected_chunks \
		and bool(grid.get("EnableCanonicalFloatingOriginAnchor")) \
		and not bool(grid.get("EnableFloatingOriginBridge")) \
		and is_equal_approx(float(grid.get("RingAcrossWidthMeters")), CERTIFIED_RING_WIDTH_KM * 1000.0) \
		and int(grid.get("MinExcavationGlobalY")) == CERTIFIED_TERRAIN_FLOOR_Y \
		and int(grid.get("TerrainBottomGlobalY")) == CERTIFIED_TERRAIN_FLOOR_Y \
		and int(grid.get("ProtectedRoofThicknessMeters")) == CERTIFIED_PROTECTED_ROOF_THICKNESS_M \
		and int(grid.get("LowerHullThicknessMeters")) == CERTIFIED_LOWER_HULL_THICKNESS_M \
		and int(grid.get("MaxBuildGlobalY")) == expected_build_ceiling \
		and is_equal_approx(float(grid.get("StructuralWallClearanceMeters")), CERTIFIED_STRUCTURAL_WALL_CLEARANCE_M) \
		and grid.get("TerrainSettings") != null \
		and int(grid.get("TerrainSettings").get("Seed")) == expected_seed \
		and cave_settings is Resource \
		and int(cave_settings.get("ContractVersion")) == expected_cave_version \
		and int(grid.get("TerrainSettings").get("BedrockY")) == (-8000 if runtime_surface_generation else CERTIFIED_BEDROCK_MATERIAL_Y) \
		and int(grid.get("TerrainSettings").get("SeaLevelY")) == CERTIFIED_SEA_LEVEL_Y \
		and (runtime_surface_generation or int(grid.get("TerrainSettings").get("RarePeakY")) == CERTIFIED_ROUTINE_PEAK_CEILING_Y) \
		and (runtime_surface_generation or not bool(grid.get("TerrainSettings").get("AutoRaiseRarePeakToBuildLimit"))) \
		and (runtime_surface_generation or int(grid.get("TerrainSettings").get("ExceptionalSummitCount")) == 1) \
		and (runtime_surface_generation or int(grid.get("TerrainSettings").get("ExceptionalSummitY")) == CERTIFIED_EXCEPTIONAL_SUMMIT_Y) \
		and (not runtime_surface_generation or (str(grid.get("TerrainSettings").get("SurfaceGenerationContractId")) == RUNTIME_SURFACE_CONTRACT and not bool(grid.get("TerrainSettings").get("EnableNaturalOverhangs")))) \
		and int(grid.get("TerrainSettings").get("TerrainToBuildClearanceMeters")) == CERTIFIED_TERRAIN_BUILD_CLEARANCE_M \
		and is_equal_approx(float(grid.get("StructuralDepthMeters")), CERTIFIED_STRUCTURAL_DEPTH_KM * 1000.0) \
		and is_equal_approx(float(global_lod.get("StructuralDepthMeters")), CERTIFIED_STRUCTURAL_DEPTH_KM * 1000.0) \
		and bool(global_lod.get("BuildStructuralEdgeWalls")) \
		and camera.far <= FAR_FIELD_CAMERA_LIMIT_METERS \
		and bool(scaled_far_viewport.get("Enabled")) \
		and is_equal_approx(float(scaled_far_viewport.get("CoordinateScale")), 0.001) \
		and int(scaled_far_viewport.get("RingWidthMeters")) == int(CERTIFIED_RING_WIDTH_KM * 1000.0) \
		and is_equal_approx(float(scaled_far_viewport.get("StructuralWallTopMeters")), expected_wall_top) \
		and not bool(analytic_global_lod.get("BuildEnabled")) \
		and not bool(cloud_layer.get("BuildEnabled"))
	if not valid:
		push_error("Certified ring runtime configuration self-test failed.")
		return

	if runtime_surface_generation:
		print("Certified runtime surface configuration passed: sharedSeed=%d, chunks=%d, terrain=-8000..8000 m, sea=0 m, buildCeiling=%d m, wallTop=%d m, caves=disabled, immutable recipe contract=%s." % [
			expected_seed, chunks_around, expected_build_ceiling, expected_wall_top, RUNTIME_SURFACE_CONTRACT])
		return
	print("Certified ring runtime self-test passed: sharedSeed=%d, cave=v%d, chunks=%d, circumference=%.3f km, effectiveRadius=%.6f km, width=%.1f km, terrain=%d..%d m, sea=%d m, routinePeak<=%d m, summit=%d m, wallTop=%d m, structure=%.1f km, cameraFar=%.0f km, farViewportScale=1:%.0f" % [
		expected_seed,
		expected_cave_version,
		chunks_around,
		circumference_m / 1000.0,
		effective_radius_m / 1000.0,
		CERTIFIED_RING_WIDTH_KM,
		CERTIFIED_TERRAIN_FLOOR_Y,
		CERTIFIED_BUILD_MAX_Y,
		CERTIFIED_SEA_LEVEL_Y,
		CERTIFIED_ROUTINE_PEAK_CEILING_Y,
		CERTIFIED_EXCEPTIONAL_SUMMIT_Y,
		CERTIFIED_STRUCTURAL_WALL_TOP_Y,
		CERTIFIED_STRUCTURAL_DEPTH_KM,
		camera.far / 1000.0,
		1.0 / float(scaled_far_viewport.get("CoordinateScale"))
	])


func _configure_terrain_lod_widths(game: Node, width_m: float) -> void:
	var width_meters := maxi(1, int(round(width_m)))
	for node_path in [
		"Lod1Terrain2m",
		"Lod2Terrain4m",
		"Lod3Terrain8m",
		"Lod4Terrain16m",
		"Lod5Terrain32m",
		"Lod6Terrain64m",
		"Lod7Terrain128m",
		"Lod8Terrain256m"
	]:
		var proxy_lod := game.get_node_or_null(node_path)
		if proxy_lod != null:
			proxy_lod.set("RingWidthMeters", width_meters)

	var global_lod := game.get_node_or_null("GlobalTerrainLod3")
	var cloud_layer := game.get_node_or_null("RingCloudLayer")
	if cloud_layer != null:
		cloud_layer.set("RingWidthMeters", width_meters)
	if global_lod == null:
		return

	var width_scale := width_m / BASE_TERRAIN_LOD_WIDTH_METERS
	var across_segments := clampi(
		int(round(BASE_GLOBAL_ACROSS_SEGMENTS * width_scale)),
		8,
		128
	)
	global_lod.set("RingWidthMeters", width_meters)
	global_lod.set("AcrossSegments", across_segments)


func _build_runtime_terrain_settings(grid: Node, min_terrain_y: int, max_build_y: int, bedrock_material_y: int, surface_only: bool = false) -> Resource:
	var source_settings: Variant = grid.get("TerrainSettings")
	if source_settings == null:
		source_settings = DEFAULT_TERRAIN_SETTINGS

	if source_settings == null or not (source_settings is Resource):
		return null

	var runtime_settings := (source_settings as Resource).duplicate(true)
	if runtime_settings == null:
		return null

	var terrain_seed_value := terrain_seed_spin.value if terrain_seed_spin != null else float(launch_defaults.get("terrain_seed", 1337.0))
	var cave_generator_version_value := cave_generator_version_spin.value if cave_generator_version_spin != null else float(launch_defaults.get("cave_generator_version", 1.0))
	var base_surface_value := base_surface_spin.value if base_surface_spin != null else float(launch_defaults.get("base_surface_height", 16.0))
	var height_amplitude_value := height_amplitude_spin.value if height_amplitude_spin != null else float(launch_defaults.get("height_amplitude", 12.0))
	var detail_amplitude_value := detail_amplitude_spin.value if detail_amplitude_spin != null else float(launch_defaults.get("detail_amplitude", 4.0))
	var continental_contrast_value := continental_contrast_spin.value if continental_contrast_spin != null else float(launch_defaults.get("continental_contrast", 1.35))
	var regional_detail_value := regional_detail_spin.value if regional_detail_spin != null else float(launch_defaults.get("regional_detail_strength", 0.65))

	runtime_settings.set("Seed", int(round(terrain_seed_value)))
	var cave_settings: Variant = runtime_settings.get("DesignedCaveGenerationContractSettings")
	if not (cave_settings is Resource):
		var default_cave_settings: Variant = DEFAULT_TERRAIN_SETTINGS.get("DesignedCaveGenerationContractSettings")
		if default_cave_settings is Resource:
			cave_settings = (default_cave_settings as Resource).duplicate(true)
			runtime_settings.set("DesignedCaveGenerationContractSettings", cave_settings)
	if not (cave_settings is Resource):
		push_error("Runtime terrain settings do not provide designed cave generation settings.")
		return null
	cave_settings.set("ContractVersion", int(round(cave_generator_version_value)))
	runtime_settings.set("BedrockY", bedrock_material_y)
	runtime_settings.set("SeaLevelY", CERTIFIED_SEA_LEVEL_Y)
	runtime_settings.set("BaseSurfaceHeightVoxels", int(round(base_surface_value)))
	runtime_settings.set("HeightAmplitudeVoxels", int(round(height_amplitude_value)))
	runtime_settings.set("DetailAmplitudeVoxels", int(round(detail_amplitude_value)))
	runtime_settings.set("ContinentalContrast", float(continental_contrast_value))
	runtime_settings.set("RegionalDetailStrength", float(regional_detail_value))
	runtime_settings.set("EnableUnified3dDensityTerrain", false if surface_only else unified_density_toggle.button_pressed if unified_density_toggle != null else false)
	runtime_settings.set("EnableDesignedCaveDensityComposition", false if surface_only else designed_caves_toggle.button_pressed if designed_caves_toggle != null else false)
	if surface_only:
		runtime_settings.set("EnableProceduralCaves", false)

	var rare_peak_value := rare_peak_spin.value if rare_peak_spin != null else float(launch_defaults.get("rare_peak_height", 1848.0))
	var rare_peak := int(round(rare_peak_value))

	runtime_settings.set("RarePeakY", rare_peak)
	runtime_settings.set("AutoRaiseRarePeakToBuildLimit", false)

	return runtime_settings


func _configure_certified_surface_terrain(game: Node, grid: Node, explorer: Node, runtime_settings: Resource) -> void:
	grid.set("UseTerrain3DSurface", true)
	grid.set("DormantEditableBackend", true)
	grid.set("BuildOnReady", false)
	grid.set("EnableChunkStreaming", false)
	var smooth := game.get_node_or_null("SmoothTerrainGrid")
	if smooth != null:
		smooth.free()
	var construction := game.get_node_or_null("SmoothConstructionOverlay")
	if construction != null:
		construction.free()
	var editor := game.get_node_or_null("GridEditController")
	if editor != null:
		editor.set("UseTerrain3DSurfaceBackend", true)
		editor.set("UseSmoothTerrainBackend", false)
	explorer.set("UseTerrain3DSurfacePhysics", true)
	explorer.set("UseSmoothTerrainPhysics", false)
	var gameplay_script := load("res://scripts/Terrain/Surface/RingTerrain3DGameplay.cs")
	if gameplay_script == null:
		push_error("Terrain3D gameplay backend is unavailable.")
		return
	var gameplay: Node = gameplay_script.new()
	gameplay.name = "Terrain3DGameplay"
	game.add_child(gameplay)
