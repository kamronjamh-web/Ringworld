shader_type canvas_item;
// Test-only: evaluates the GLSL grass-card curve (materials/ring_grass_card_curve.gdshaderinc) for a texture of
// (rank, distance) samples and writes each float32 result as 4 bytes (4 pixels per sample: scale, keep, density, low scale),
// so tools/grass_card_color_match_gpu_test.gd can compare it bit-for-bit-ish against RingGrassCardCurve.cs.
render_mode blend_disabled;
#include "res://materials/ring_grass_card_curve.gdshaderinc"

uniform sampler2D samples : filter_nearest;

void fragment() {
	ivec2 p = ivec2(FRAGCOORD.xy);
	int sample_index = p.x / 4;
	int field = p.x - sample_index * 4;
	vec4 s = texelFetch(samples, ivec2(sample_index, 0), 0);
	float v;
	if (field == 0) v = grass_card_curve_scale(s.r, s.g);
	else if (field == 1) v = grass_card_keep(s.g);
	else if (field == 2) v = grass_card_density(s.g);
	else v = grass_card_low_scale(s.r, s.g);
	uint b = floatBitsToUint(v);
	COLOR = vec4(float(b & 255u), float((b >> 8u) & 255u), float((b >> 16u) & 255u), float(b >> 24u)) / 255.0;
}
