shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled;
#include "res://materials/celestial_lighting.gdshaderinc"
uniform vec4 rain_tint : source_color;
uniform float opacity = 0.0;
uniform float wind_phase = 0.0;
uniform float cell_phase = 0.0;
uniform float sun_energy = 0.0;
uniform float geometric_day = 0.0;
uniform vec2 wind_meters_per_second = vec2(0.0);
// Across is linear on this cylindrical ring. Clipping in UV space remains
// exact as the sheet billboards; it never spills past either physical edge.
uniform vec2 across_extent = vec2(0.0);
uniform float ring_width = 1e9;
uniform vec2 storm_offset = vec2(0.0);
uniform vec2 storm_right_delta = vec2(0.0);
uniform vec2 storm_radii = vec2(1e9);
uniform vec2 storm_rotation = vec2(1.0, 0.0);
uniform vec2 storm_edges = vec2(0.55, 1.3);

float wisps(float x) {
	float i = floor(x);
	float f = fract(x);
	f = f * f * (3.0 - 2.0 * f);
	return mix(fract(sin(i * 127.1) * 43758.5453),
		fract(sin((i + 1.0) * 127.1) * 43758.5453), f);
}

float storm_weight(vec2 offset) {
	vec2 rotated = vec2(dot(offset, storm_rotation),
		dot(offset, vec2(-storm_rotation.y, storm_rotation.x))) / storm_radii;
	return 1.0 - smoothstep(storm_edges.x, storm_edges.y, length(rotated));
}

void fragment() {
	float across = across_extent.x + (UV.x - 0.5) * across_extent.y;
	if (across < 0.0 || across > ring_width) discard;
	float phase = wind_phase * 6.28318530718 / 4096.0;
	float wind = dot(wind_meters_per_second, vec2(0.013, 0.009));
	// Low-frequency continuous veils, not opaque picket-fence raindrop cards.
	float x = UV.x + UV.y * wind * 0.08 + sin(phase) * wind;
	float body = 0.28 + 0.52 * wisps(x * 9.0 + cell_phase)
		+ 0.20 * wisps(x * 25.0 + cell_phase + sin(phase * 8.0) * 0.4);
	float detail_fade = 1.0 - smoothstep(0.2, 0.6, fwidth(UV.x * 96.0));
	float streak = 1.0 + 0.07 * detail_fade
		* sin((UV.x * 96.0 + UV.y * 2.0) * 6.28318530718 - phase * 1024.0);
	float footprint = clamp(storm_weight(storm_offset + (UV.x - 0.5) * storm_right_delta)
		/ max(0.001, storm_weight(storm_offset)), 0.0, 1.0);
	float effective_day = ringworld_effective_solar_day(geometric_day) * clamp(sun_energy, 0.0, 1.0);
	ALBEDO = rain_tint.rgb * mix(0.035, 0.65, effective_day);
	float edge = smoothstep(0.0, 0.2, UV.x) * smoothstep(0.0, 0.2, 1.0 - UV.x)
		* smoothstep(0.0, 0.16, UV.y) * smoothstep(0.0, 0.12, 1.0 - UV.y);
	ALPHA = clamp(opacity * body * streak * edge * footprint * 0.65, 0.0, 0.8);
}
