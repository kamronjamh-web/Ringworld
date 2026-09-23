shader_type canvas_item;
render_mode unshaded;

#include "res://materials/celestial_lighting.gdshaderinc"

uniform float geometric_day : hint_range(0.0, 1.0) = 1.0;
uniform float expected_value : hint_range(0.0, 1.0) = 1.0;

void fragment() {
	float effective_day = ringworld_effective_solar_day(geometric_day);
	// Same-frame reference defeats renderer-specific display transforms: the left
	// half evaluates the production helper; the right is CPU's expected value.
	float diagnostic_value = UV.x < 0.5 ? effective_day : expected_value;
	COLOR = vec4(diagnostic_value, diagnostic_value, diagnostic_value, 1.0);
}
