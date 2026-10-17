shader_type canvas_item;
// Test-only: evaluates the GLSL pulse/visibility math (materials/ring_emitter_grid_pulse.gdshaderinc) for a texture of samples and
// writes each float32 result as 4 bytes (5 fields per sample: pulse, intensity, background, day, interval lit), so
// tools/emitter_grid_shader_parity_test.gd can compare it against EmitterGridPulseClock / EmitterGridIntensity in C#.
render_mode blend_disabled;
#include "res://materials/ring_emitter_grid_pulse.gdshaderinc"

// Row 0: (p, column, kind, atmosphere alpha). Row 1: (day at camera, day at fragment, view distance, dot(surface up, sun)).
// Row 2: (link P0, link P1, 0, 0).
uniform sampler2D samples : filter_nearest;
uniform float solar_visibility = 1.0;
uniform float hub_day = 0.0;

void fragment() {
	ivec2 px = ivec2(FRAGCOORD.xy);
	int sample_index = px.x / 5;
	int field = px.x - sample_index * 5;
	vec4 a = texelFetch(samples, ivec2(sample_index, 0), 0);
	vec4 b = texelFetch(samples, ivec2(sample_index, 1), 0);
	vec4 l = texelFetch(samples, ivec2(sample_index, 2), 0);
	float pulse = eg_pulse_at_link(a.x, a.y, a.z);
	float background = eg_background(b.x, b.y, b.z);
	float v;
	if (field == 0) v = pulse;
	else if (field == 1) v = eg_intensity(pulse, background, 0.0, a.w);
	else if (field == 2) v = background;
	else if (field == 3) v = eg_day_at_hub(b.w, solar_visibility, hub_day);
	else v = eg_interval_lit(l.x, l.y, a.y, a.z) ? 1.0 : 0.0;
	uint bits = floatBitsToUint(v);
	COLOR = vec4(float(bits & 255u), float((bits >> 8u) & 255u), float((bits >> 16u) & 255u), float(bits >> 24u)) / 255.0;
}
