extends RefCounted
class_name RingUiTheme

const INK := Color(0.82, 0.91, 0.95, 1.0)
const MUTED := Color(0.48, 0.63, 0.69, 1.0)
const ACCENT := Color(0.19, 0.91, 0.87, 1.0)
const ACCENT_BRIGHT := Color(0.55, 1.0, 0.94, 1.0)
const DANGER := Color(1.0, 0.38, 0.36, 1.0)

# Fixed type scale. Use these instead of one-off font sizes.
const TYPE_DISPLAY := 72
const TYPE_H1 := 34
const TYPE_H2 := 24
const TYPE_LEAD := 18
const TYPE_BODY := 16
const TYPE_SMALL := 13
const TYPE_KICKER := 12
const TYPE_MICRO := 11


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
	result.set_stylebox("focus", "Button", _focus_ring())
	result.set_color("font_focus_color", "Button", ACCENT_BRIGHT)
	result.set_color("font_hover_pressed_color", "Button", Color(0.02, 0.08, 0.09, 1.0))
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
	_add_variations(result)
	result.set_color("grabber_area", "HSlider", Color(0.09, 0.55, 0.54, 1.0))
	result.set_color("grabber_area_highlight", "HSlider", ACCENT)
	return result


static func _add_variations(result: Theme) -> void:
	var kicker_font := FontVariation.new()
	kicker_font.base_font = ThemeDB.fallback_font
	kicker_font.spacing_glyph = 2
	result.add_type("KickerLabel")
	result.set_type_variation("KickerLabel", "Label")
	result.set_font("font", "KickerLabel", kicker_font)
	result.set_font_size("font_size", "KickerLabel", TYPE_KICKER)
	result.set_color("font_color", "KickerLabel", ACCENT)
	_label_variation(result, "DisplayLabel", TYPE_DISPLAY, Color.WHITE)
	_label_variation(result, "HeadingLabel", TYPE_H1, ACCENT_BRIGHT)
	_label_variation(result, "CardTitleLabel", TYPE_H2, INK)
	_label_variation(result, "LeadLabel", TYPE_LEAD, MUTED)
	_label_variation(result, "MutedLabel", TYPE_SMALL, MUTED)
	_label_variation(result, "MonoLabel", TYPE_MICRO, MUTED)
	var mono := SystemFont.new()
	mono.font_names = PackedStringArray(["Cascadia Mono", "Consolas", "DejaVu Sans Mono", "monospace"])
	result.set_font("font", "MonoLabel", mono)

	result.add_type("DangerButton")
	result.set_type_variation("DangerButton", "Button")
	result.set_color("font_color", "DangerButton", DANGER)
	result.set_color("font_hover_color", "DangerButton", Color(1.0, 0.62, 0.6, 1.0))
	result.set_color("font_focus_color", "DangerButton", Color(1.0, 0.62, 0.6, 1.0))
	result.set_stylebox("normal", "DangerButton", _button_box(Color(0.06, 0.03, 0.04, 0.92), Color(DANGER, 0.55)))
	result.set_stylebox("hover", "DangerButton", _button_box(Color(0.14, 0.05, 0.06, 0.96), DANGER))
	result.set_stylebox("pressed", "DangerButton", _button_box(DANGER, Color(1.0, 0.62, 0.6, 1.0)))
	result.set_stylebox("focus", "DangerButton", _focus_ring(DANGER))

	result.add_type("GhostButton")
	result.set_type_variation("GhostButton", "Button")
	result.set_stylebox("normal", "GhostButton", _button_box(Color(0, 0, 0, 0), Color(0, 0, 0, 0)))
	result.set_stylebox("hover", "GhostButton", _button_box(Color(0.045, 0.14, 0.16, 0.7), Color(ACCENT, 0.6)))

	result.add_type("CardHeaderButton")
	result.set_type_variation("CardHeaderButton", "Button")
	var empty_box := StyleBoxEmpty.new()
	result.set_stylebox("normal", "CardHeaderButton", empty_box)
	result.set_stylebox("disabled", "CardHeaderButton", empty_box)
	var header_hover := _button_box(Color(ACCENT, 0.06), Color(0, 0, 0, 0))
	header_hover.set_corner_radius_all(10)
	header_hover.content_margin_top = 0.0
	header_hover.content_margin_bottom = 0.0
	result.set_stylebox("hover", "CardHeaderButton", header_hover)
	var header_pressed := _button_box(Color(ACCENT, 0.12), Color(0, 0, 0, 0))
	header_pressed.set_corner_radius_all(10)
	result.set_stylebox("pressed", "CardHeaderButton", header_pressed)
	var header_focus := _focus_ring()
	header_focus.set_corner_radius_all(10)
	result.set_stylebox("focus", "CardHeaderButton", header_focus)
	result.set_color("font_pressed_color", "CardHeaderButton", INK)

	_card_variation(result, "WorldCard", Color(0.018, 0.035, 0.052, 0.82), Color(ACCENT, 0.45), 1)
	_card_variation(result, "WorldCardSelected", Color(0.022, 0.05, 0.068, 0.9), ACCENT, 2)
	_card_variation(result, "WorldCardEmpty", Color(0.018, 0.035, 0.052, 0.45), Color(MUTED, 0.35), 1)


static func _label_variation(result: Theme, type_name: String, size: int, color: Color) -> void:
	result.add_type(type_name)
	result.set_type_variation(type_name, "Label")
	result.set_font_size("font_size", type_name, size)
	result.set_color("font_color", type_name, color)


static func _card_variation(result: Theme, type_name: String, fill: Color, border: Color, border_width: int) -> void:
	var style := StyleBoxFlat.new()
	style.bg_color = fill
	style.border_color = border
	style.set_border_width_all(border_width)
	style.set_corner_radius_all(12)
	style.shadow_color = Color(0.0, 0.0, 0.0, 0.4)
	style.shadow_size = 10
	style.set_content_margin_all(14.0)
	result.add_type(type_name)
	result.set_type_variation(type_name, "PanelContainer")
	result.set_stylebox("panel", type_name, style)


# Outline-only focus ring: it draws over the state style instead of replacing it,
# so a pressed or hovered button never turns dark-on-dark.
static func _focus_ring(color: Color = ACCENT) -> StyleBoxFlat:
	var style := StyleBoxFlat.new()
	style.draw_center = false
	style.border_color = color
	style.set_border_width_all(2)
	style.set_corner_radius_all(9)
	style.set_expand_margin_all(2.0)
	return style


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
