extends RefCounted
class_name RingUiTheme

const INK := Color(0.82, 0.91, 0.95, 1.0)
const MUTED := Color(0.48, 0.63, 0.69, 1.0)
const ACCENT := Color(0.19, 0.91, 0.87, 1.0)
const ACCENT_BRIGHT := Color(0.55, 1.0, 0.94, 1.0)
const DANGER := Color(1.0, 0.38, 0.36, 1.0)


static func create_theme() -> Theme:
	var result := Theme.new()
	result.default_font_size = 16
	result.set_color("font_color", "Label", INK)
	result.set_color("font_shadow_color", "Label", Color(0.0, 0.0, 0.0, 0.72))
	result.set_constant("shadow_offset_x", "Label", 1)
	result.set_constant("shadow_offset_y", "Label", 2)
	result.set_color("font_color", "Button", INK)
	result.set_color("font_hover_color", "Button", ACCENT_BRIGHT)
	result.set_color("font_pressed_color", "Button", Color(0.02, 0.08, 0.09, 1.0))
	result.set_color("font_disabled_color", "Button", Color(0.35, 0.43, 0.46, 0.7))
	result.set_font_size("font_size", "Button", 16)
	result.set_stylebox("normal", "Button", _button_box(Color(0.035, 0.07, 0.095, 0.92), Color(0.16, 0.46, 0.5, 0.7)))
	result.set_stylebox("hover", "Button", _button_box(Color(0.045, 0.14, 0.16, 0.96), ACCENT))
	result.set_stylebox("pressed", "Button", _button_box(ACCENT, ACCENT_BRIGHT))
	result.set_stylebox("focus", "Button", _button_box(Color(0.04, 0.11, 0.13, 0.94), ACCENT))
	result.set_stylebox("disabled", "Button", _button_box(Color(0.025, 0.035, 0.045, 0.72), Color(0.14, 0.19, 0.21, 0.5)))
	result.set_stylebox("panel", "PanelContainer", glass_panel())
	result.set_stylebox("panel", "PopupPanel", glass_panel(0.985))
	result.set_stylebox("normal", "LineEdit", _field_box(Color(0.018, 0.035, 0.05, 0.96), Color(0.13, 0.34, 0.39, 0.9)))
	result.set_stylebox("focus", "LineEdit", _field_box(Color(0.025, 0.055, 0.07, 0.98), ACCENT))
	result.set_color("font_color", "LineEdit", INK)
	result.set_color("caret_color", "LineEdit", ACCENT)
	result.set_color("font_color", "CheckButton", INK)
	result.set_color("font_color", "OptionButton", INK)
	result.set_stylebox("normal", "OptionButton", _button_box(Color(0.025, 0.055, 0.073, 0.96), Color(0.13, 0.34, 0.39, 0.9)))
	result.set_stylebox("hover", "OptionButton", _button_box(Color(0.04, 0.11, 0.13, 0.98), ACCENT))
	result.set_stylebox("pressed", "OptionButton", _button_box(ACCENT, ACCENT_BRIGHT))
	result.set_stylebox("focus", "OptionButton", _button_box(Color(0.04, 0.11, 0.13, 0.98), ACCENT))
	result.set_color("grabber_area", "HSlider", Color(0.09, 0.55, 0.54, 1.0))
	result.set_color("grabber_area_highlight", "HSlider", ACCENT)
	return result


static func glass_panel(alpha: float = 0.94) -> StyleBoxFlat:
	var style := StyleBoxFlat.new()
	style.bg_color = Color(0.018, 0.035, 0.052, alpha)
	style.border_color = Color(0.16, 0.52, 0.55, 0.68)
	style.set_border_width_all(1)
	style.set_corner_radius_all(18)
	style.shadow_color = Color(0.0, 0.0, 0.0, 0.48)
	style.shadow_size = 18
	style.content_margin_left = 1.0
	style.content_margin_right = 1.0
	style.content_margin_top = 1.0
	style.content_margin_bottom = 1.0
	return style


static func _button_box(fill: Color, border: Color) -> StyleBoxFlat:
	var style := StyleBoxFlat.new()
	style.bg_color = fill
	style.border_color = border
	style.set_border_width_all(1)
	style.set_corner_radius_all(9)
	style.content_margin_left = 18.0
	style.content_margin_right = 18.0
	style.content_margin_top = 12.0
	style.content_margin_bottom = 12.0
	return style


static func _field_box(fill: Color, border: Color) -> StyleBoxFlat:
	var style := _button_box(fill, border)
	style.set_corner_radius_all(7)
	style.content_margin_top = 8.0
	style.content_margin_bottom = 8.0
	return style
