extends RefCounted
# Shared start-menu motion helpers. Visibility is always set synchronously by the
# caller; these helpers only animate the visuals (modulate, offset, scale).

const UI_THEME := preload("res://scripts/ui/ring_ui_theme.gd")

const PANEL_FADE_SECONDS := 0.18
const PANEL_SLIDE_PIXELS := 24.0
const PANEL_CHILD_STAGGER_SECONDS := 0.04
const UNDERLINE_SECONDS := 0.16
const UNDERLINE_HEIGHT := 2.0
const TAP_SCALE := 0.97
const TAP_DOWN_SECONDS := 0.05
const TAP_UP_SECONDS := 0.09


# Call after setting panel.visible = true. Fades and slides the panel in from the
# left, then fades its column children in one after another.
static func show_panel(panel: Control, start_delay: float = 0.0) -> void:
	if panel == null:
		return
	if panel.has_meta("mm_tween"):
		var previous: Variant = panel.get_meta("mm_tween")
		if previous is Tween and (previous as Tween).is_valid():
			(previous as Tween).kill()
	if not panel.has_meta("mm_rest_x"):
		panel.set_meta("mm_rest_x", panel.position.x)
	var rest_x: float = panel.get_meta("mm_rest_x")
	panel.position.x = rest_x - PANEL_SLIDE_PIXELS
	panel.modulate.a = 0.0
	var tween := panel.create_tween().set_parallel(true)
	panel.set_meta("mm_tween", tween)
	tween.tween_property(panel, "position:x", rest_x, PANEL_FADE_SECONDS).set_delay(start_delay).set_trans(Tween.TRANS_CUBIC).set_ease(Tween.EASE_OUT)
	tween.tween_property(panel, "modulate:a", 1.0, PANEL_FADE_SECONDS).set_delay(start_delay)
	var columns := panel.find_children("*", "VBoxContainer", true, false)
	if columns.is_empty():
		return
	var index := 0
	for child: Node in columns[0].get_children():
		if not (child is Control) or not (child as Control).visible:
			continue
		var control := child as Control
		if not control.has_meta("mm_alpha"):
			control.set_meta("mm_alpha", control.modulate.a)
		var rest_alpha: float = control.get_meta("mm_alpha")
		control.modulate.a = 0.0
		var delay := start_delay + PANEL_CHILD_STAGGER_SECONDS * (index + 1)
		tween.tween_property(control, "modulate:a", rest_alpha, PANEL_FADE_SECONDS).set_delay(delay)
		index += 1


# Adds an accent underline that grows left to right on hover/focus, and a small
# scale tap while pressed.
static func style_button(button: Button) -> void:
	if button == null or button.has_meta("mm_styled"):
		return
	button.set_meta("mm_styled", true)
	var line := ColorRect.new()
	line.name = "MotionUnderline"
	line.color = UI_THEME.ACCENT
	line.mouse_filter = Control.MOUSE_FILTER_IGNORE
	line.set_anchors_preset(Control.PRESET_BOTTOM_WIDE)
	line.offset_left = 10.0
	line.offset_right = -10.0
	line.offset_top = -(UNDERLINE_HEIGHT + 2.0)
	line.offset_bottom = -2.0
	line.scale = Vector2(0.0, 1.0)
	button.add_child(line)
	button.mouse_entered.connect(_set_state.bind(button, line, "mm_hover", true))
	button.mouse_exited.connect(_set_state.bind(button, line, "mm_hover", false))
	button.focus_entered.connect(_set_state.bind(button, line, "mm_focus", true))
	button.focus_exited.connect(_set_state.bind(button, line, "mm_focus", false))
	button.button_down.connect(_tap.bind(button, true))
	button.button_up.connect(_tap.bind(button, false))


static func style_buttons_in(root: Node) -> void:
	for node: Node in root.find_children("*", "Button", true, false):
		style_button(node as Button)


static func _set_state(button: Button, line: ColorRect, key: String, value: bool) -> void:
	button.set_meta(key, value)
	var active: bool = (bool(button.get_meta("mm_hover", false)) or bool(button.get_meta("mm_focus", false))) and not button.disabled
	if line.has_meta("mm_target") and bool(line.get_meta("mm_target")) == active:
		return
	line.set_meta("mm_target", active)
	if line.has_meta("mm_tween"):
		var previous: Variant = line.get_meta("mm_tween")
		if previous is Tween and (previous as Tween).is_valid():
			(previous as Tween).kill()
	var tween := line.create_tween()
	line.set_meta("mm_tween", tween)
	tween.tween_property(line, "scale:x", 1.0 if active else 0.0, UNDERLINE_SECONDS).set_trans(Tween.TRANS_CUBIC).set_ease(Tween.EASE_OUT)


static func _tap(button: Button, down: bool) -> void:
	if button.disabled:
		return
	button.pivot_offset = button.size * 0.5
	if button.has_meta("mm_tap_tween"):
		var previous: Variant = button.get_meta("mm_tap_tween")
		if previous is Tween and (previous as Tween).is_valid():
			(previous as Tween).kill()
	var tween := button.create_tween()
	button.set_meta("mm_tap_tween", tween)
	if down:
		tween.tween_property(button, "scale", Vector2.ONE * TAP_SCALE, TAP_DOWN_SECONDS)
	else:
		tween.tween_property(button, "scale", Vector2.ONE, TAP_UP_SECONDS).set_trans(Tween.TRANS_BACK).set_ease(Tween.EASE_OUT)
