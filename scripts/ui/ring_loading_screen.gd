extends Control

signal cancel_requested

const HOLOGRAM := preload("res://scripts/ui/ring_loading_hologram.gd")
const SETTINGS_PATH := "user://gameplay_loading_presentation_defaults.tres"
const ACCENT := Color(0.19, 0.91, 0.87)
const INK := Color(0.82, 0.91, 0.95)
const MUTED := Color(0.48, 0.63, 0.69)
const DEFAULT_STAGES := [
	{"key": "preparing", "label": "Prepare world"},
	{"key": "terrain", "label": "Shape terrain"},
	{"key": "surroundings", "label": "Bring surroundings to life"},
	{"key": "position", "label": "Restore your position"},
	{"key": "ready", "label": "Enter the ring"}
]

var _info: Dictionary = {}
var _stage_definitions: Array = DEFAULT_STAGES.duplicate(true)
var _stage_rows: Dictionary = {}
var _hologram
var _title: Label
var _facts: Label
var _link_status: Label
var _stage: Label
var _detail: Label
var _percent: Label
var _progress_bar: ProgressBar
var _cancel: Button
var _layout: BoxContainer
var _illustration: VBoxContainer
var _stage_panel: VBoxContainer
var _phase_rows: VBoxContainer
var _footer_note: Label
var _margin: MarginContainer
var _failed := false
var _finished := false
var _cancelled := false
var _progress := 0.0
var _current_stage := "preparing"
var _current_detail := "Preparing your journey."
var _built := false

func _ready() -> void:
	process_mode = Node.PROCESS_MODE_ALWAYS
	set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	mouse_filter = Control.MOUSE_FILTER_STOP
	_build()
	_apply_info()
	set_progress(_current_stage, _current_detail, _progress)
	if _finished:
		finish()
	resized.connect(_update_layout)
	_update_layout()
	Input.mouse_mode = Input.MOUSE_MODE_VISIBLE

func configure(info: Dictionary) -> void:
	_info = info.duplicate(true)
	if not _built:
		if info.get("stages") is Array and not info["stages"].is_empty():
			_stage_definitions = info["stages"].duplicate(true)
		return
	_apply_info()

func set_arrival_position(around_m: float, across_m: float) -> void:
	if not is_finite(around_m) or not is_finite(across_m):
		return
	_info["spawn_around_m"] = around_m
	_info["spawn_across_m"] = across_m
	if _built:
		_hologram.set_arrival_position(around_m, across_m)

func set_progress(stage_key: String, detail: String, progress: float) -> void:
	if _failed or _cancelled:
		return
	_current_stage = stage_key
	_current_detail = detail
	_progress = maxf(_progress, clampf(progress, 0.0, 1.0)) if is_finite(progress) else _progress
	if not _built:
		return
	var active := -1
	for index in range(_stage_definitions.size()):
		if String(_stage_definitions[index].get("key", "")) == stage_key:
			active = index
			_stage.text = String(_stage_definitions[index].get("label", stage_key))
			break
	if active < 0:
		_stage.text = "Preparing your world"
	_detail.text = detail
	_progress_bar.value = _progress * 100.0
	_percent.text = "%02d%%" % int(round(_progress * 100))
	_hologram.set_progress(_progress)
	for index in range(_stage_definitions.size()):
		var row: Dictionary = _stage_rows[String(_stage_definitions[index].get("key", ""))]
		var completed := index < active or (_finished and index <= active)
		row["mark"].text = "✓" if completed else ("●" if index == active else "○")
		row["mark"].modulate = ACCENT if completed or index == active else MUTED * Color(1, 1, 1, 0.5)
		row["label"].modulate = INK if index == active else (MUTED if completed else Color(0.31, 0.43, 0.49))
		row["state"].text = "READY" if completed else ("IN PROGRESS" if index == active else "WAITING")
		row["state"].modulate = ACCENT if index == active else MUTED

func show_failure(message: String) -> void:
	_failed = true
	_finished = false
	_current_detail = message
	if not _built:
		return
	_stage.text = "Your world could not be opened"
	_link_status.text = "WORLD LINK  /  INTERRUPTED"
	_stage.modulate = Color(1.0, 0.58, 0.51)
	_detail.text = message
	_cancel.text = "Return to menu"
	_cancel.disabled = false
	_hologram.motion_enabled = false

func finish() -> void:
	if _failed or _cancelled:
		return
	_finished = true
	set_progress(String(_stage_definitions.back().get("key", "ready")), "Your world is ready.", 1.0)
	if _built:
		_cancel.disabled = true
		_link_status.text = "WORLD LINK  /  READY"

func _request_cancel() -> void:
	if _cancelled or _finished:
		return
	_cancelled = true
	_cancel.disabled = true
	_stage.text = "Returning to menu"
	_detail.text = "Closing the world safely."
	cancel_requested.emit()

func _unhandled_key_input(event: InputEvent) -> void:
	if event is InputEventKey and event.pressed and not event.echo and event.keycode == KEY_ESCAPE:
		_request_cancel()
		get_viewport().set_input_as_handled()

func _label(text: String, font_size: int, color: Color = INK) -> Label:
	var label := Label.new()
	label.text = text
	label.add_theme_font_size_override("font_size", font_size)
	label.add_theme_color_override("font_color", color)
	return label

func _box(fill: Color, border: Color, radius: int) -> StyleBoxFlat:
	var style := StyleBoxFlat.new()
	style.bg_color = fill
	style.border_color = border
	style.set_border_width_all(1)
	style.set_corner_radius_all(radius)
	return style

func _build() -> void:
	var background := ColorRect.new()
	background.color = Color(0.009, 0.020, 0.033)
	background.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	background.mouse_filter = Control.MOUSE_FILTER_IGNORE
	add_child(background)
	_margin = MarginContainer.new()
	_margin.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	add_child(_margin)
	var column := VBoxContainer.new()
	column.add_theme_constant_override("separation", 20)
	_margin.add_child(column)
	var header := HBoxContainer.new()
	column.add_child(header)
	var brand := _label("RINGWORLD", 16, ACCENT)
	brand.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	header.add_child(brand)
	_link_status = _label("WORLD LINK  /  ESTABLISHING", 12, MUTED)
	header.add_child(_link_status)
	var divider := HSeparator.new()
	divider.modulate = Color(0.15, 0.45, 0.47, 0.45)
	column.add_child(divider)
	var body_scroll := ScrollContainer.new()
	body_scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	body_scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	column.add_child(body_scroll)
	_layout = BoxContainer.new()
	_layout.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_layout.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_layout.add_theme_constant_override("separation", 48)
	body_scroll.add_child(_layout)
	_illustration = VBoxContainer.new()
	_illustration.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_illustration.size_flags_stretch_ratio = 1.3
	_layout.add_child(_illustration)
	_illustration.add_child(_label("YOUR NEXT HORIZON", 12, MUTED))
	_title = _label("Ringworld", 38)
	_title.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_illustration.add_child(_title)
	_facts = _label("", 13, MUTED)
	_facts.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_illustration.add_child(_facts)
	_hologram = HOLOGRAM.new()
	_hologram.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_hologram.custom_minimum_size = Vector2(280, 180)
	_illustration.add_child(_hologram)
	_illustration.add_child(_label("◉  YOUR ARRIVAL POINT", 11, ACCENT))
	_stage_panel = VBoxContainer.new()
	_stage_panel.custom_minimum_size = Vector2(340, 0)
	_stage_panel.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_stage_panel.size_flags_vertical = Control.SIZE_SHRINK_CENTER
	_stage_panel.add_theme_constant_override("separation", 20)
	_layout.add_child(_stage_panel)
	_stage_panel.add_child(_label("OPENING YOUR WORLD", 12, MUTED))
	_stage = _label("Prepare world", 25)
	_stage.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_stage_panel.add_child(_stage)
	_detail = _label("Preparing your journey.", 14, MUTED)
	_detail.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_detail.custom_minimum_size.y = 45
	_stage_panel.add_child(_detail)
	var progress_row := HBoxContainer.new()
	_stage_panel.add_child(progress_row)
	var progress_label := _label("PROGRESS", 11, MUTED)
	progress_label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	progress_row.add_child(progress_label)
	_percent = _label("00%", 13, ACCENT)
	progress_row.add_child(_percent)
	_progress_bar = ProgressBar.new()
	_progress_bar.show_percentage = false
	_progress_bar.custom_minimum_size.y = 5
	_progress_bar.add_theme_stylebox_override("background", _box(Color(0.04, 0.10, 0.13), Color(0, 0, 0, 0), 2))
	_progress_bar.add_theme_stylebox_override("fill", _box(ACCENT, ACCENT, 2))
	_stage_panel.add_child(_progress_bar)
	_phase_rows = VBoxContainer.new()
	_phase_rows.add_theme_constant_override("separation", 14)
	_stage_panel.add_child(_phase_rows)
	for definition: Dictionary in _stage_definitions:
		var row := HBoxContainer.new()
		row.add_theme_constant_override("separation", 10)
		_phase_rows.add_child(row)
		var mark := _label("○", 13, MUTED)
		mark.custom_minimum_size.x = 16
		row.add_child(mark)
		var label := _label(String(definition.get("label", "Prepare world")), 13)
		label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
		row.add_child(label)
		var state := _label("WAITING", 11, MUTED)
		row.add_child(state)
		_stage_rows[String(definition.get("key", ""))] = {"mark": mark, "label": label, "state": state}
	_cancel = Button.new()
	_cancel.text = "Return to menu"
	_cancel.custom_minimum_size.y = 42
	_cancel.add_theme_color_override("font_color", INK)
	_cancel.add_theme_font_size_override("font_size", 13)
	_cancel.add_theme_stylebox_override("normal", _box(Color(0.025, 0.055, 0.073), Color(0.13, 0.34, 0.39), 6))
	_cancel.add_theme_stylebox_override("hover", _box(Color(0.04, 0.11, 0.13), ACCENT, 6))
	_cancel.add_theme_stylebox_override("focus", _box(Color(0.04, 0.11, 0.13), ACCENT, 6))
	_cancel.pressed.connect(_request_cancel)
	# Cancellation stays outside the scrollable body at every window size.
	var footer := HBoxContainer.new()
	footer.add_theme_constant_override("separation", 16)
	_footer_note = _label("A world on the inside of a ring. A horizon that never ends.", 12, MUTED)
	_footer_note.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_footer_note.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	footer.add_child(_footer_note)
	_cancel.custom_minimum_size.x = 180
	footer.add_child(_cancel)
	column.add_child(footer)
	_built = true
	_load_presentation()

func _load_presentation() -> void:
	if not ResourceLoader.exists(SETTINGS_PATH):
		return
	var settings := ResourceLoader.load(SETTINGS_PATH, "", ResourceLoader.CACHE_MODE_IGNORE)
	if settings == null:
		return
	var speed: Variant = settings.get("AnimationSpeed")
	var motion: Variant = settings.get("MotionEnabled")
	var brightness: Variant = settings.get("HologramBrightness")
	_hologram.set_presentation(float(speed) if speed is float and is_finite(speed) else 0.18,
		bool(motion) if motion is bool else true,
		float(brightness) if brightness is float and is_finite(brightness) else 1.0)

func _apply_info() -> void:
	_title.text = String(_info.get("world_title", "Ringworld"))
	var circumference := float(_info.get("circumference_m", float(_info.get("radius_m", 0.0)) * TAU))
	var width := float(_info.get("width_m", 0.0))
	_facts.text = "SEED %s  ·  %s AROUND  ·  %s WIDE" % [str(_info.get("seed", "—")), _distance(circumference), _distance(width)]
	_hologram.configure(_info)
	if _stage_rows.has("position"):
		var position_label := "Restore your position" if bool(_info.get("is_existing", false)) else "Find your arrival point"
		_stage_rows["position"]["label"].text = position_label
		for definition: Dictionary in _stage_definitions:
			if String(definition.get("key", "")) == "position":
				definition["label"] = position_label
	if _failed:
		show_failure(_current_detail)

func _distance(meters: float) -> String:
	if meters <= 0:
		return "—"
	if meters >= 1.0e9:
		return "%.2f Gm" % (meters / 1.0e9)
	if meters >= 1.0e6:
		return "%.2f Mm" % (meters / 1.0e6)
	return "%.1f km" % (meters / 1000.0)

func _update_layout() -> void:
	if not _built:
		return
	var padding := clampi(int(size.x * 0.055), 24, 84)
	_layout.vertical = size.x < 780
	var compact := size.y < 640
	_stage_panel.add_theme_constant_override("separation", 8 if compact else 20)
	_phase_rows.add_theme_constant_override("separation", 8 if compact else 14)
	_stage.add_theme_font_size_override("font_size", 21 if compact else 25)
	_detail.custom_minimum_size.y = 34 if compact else 45
	_footer_note.visible = size.x >= 780
	_hologram.custom_minimum_size = Vector2(240 if _layout.vertical else 280, 150 if _layout.vertical else 180)
	_margin.add_theme_constant_override("margin_left", padding)
	_margin.add_theme_constant_override("margin_right", padding)
	_margin.add_theme_constant_override("margin_top", clampi(int(size.y * 0.065), 22, 64))
	_margin.add_theme_constant_override("margin_bottom", clampi(int(size.y * 0.055), 18, 52))
	_layout.add_theme_constant_override("separation", clampi(int(size.x * 0.04), 20, 60))
	_title.add_theme_font_size_override("font_size", clampi(int(size.x * 0.032), 25, 42))
	_stage_panel.custom_minimum_size.x = 0 if _layout.vertical else clampf(size.x * 0.31, 270, 440)
