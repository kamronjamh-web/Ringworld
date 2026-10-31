shader_type canvas_item;
// Test-only: evaluates the real far-megastorm shader functions (ring_megastorm_across_noise / shaft_noise / column_terms in
// materials/ring_megastorm_profile.gdshaderinc, ring_ms_flash_glow in materials/ring_far_storm_lighting.gdshaderinc) for a texture of cases and
// writes each float32 result as 4 bytes (14 pixels per case: tower noise, mammatus noise, shaft noise, body base, body top, canopy underside,
// canopy top, canopy coverage, mammatus, tower coverage, body coverage, glow, reduced glow, unused), so tools/megastorm_far_parity_test.gd can
// compare it against MegastormProfile / MegastormLightning in C#.
render_mode blend_disabled;
#include "res://materials/ring_megastorm_profile.gdshaderinc"
#include "res://materials/ring_far_storm_lighting.gdshaderinc"

// Rows: 0 (x, across, altitude, slot_time), 1 (t, level, strength, seed), 2 (W, bow, meander amplitude, wavelength),
// 3 (meander phase, ceiling, slot, reduced peak), 4 (C# tower noise, C# mammatus noise, 0, 0), 5 (glow strength, glow meander phase, 0, 0).
uniform sampler2D cases : filter_nearest;

void fragment() {
	ivec2 px = ivec2(FRAGCOORD.xy);
	int idx = px.x / 14;
	int field = px.x - idx * 14;
	vec4 r0 = texelFetch(cases, ivec2(idx, 0), 0);
	vec4 r1 = texelFetch(cases, ivec2(idx, 1), 0);
	vec4 r2 = texelFetch(cases, ivec2(idx, 2), 0);
	vec4 r3 = texelFetch(cases, ivec2(idx, 3), 0);
	vec4 r4 = texelFetch(cases, ivec2(idx, 4), 0);
	vec4 r5 = texelFetch(cases, ivec2(idx, 5), 0);
	RingMegastormParams p = RingMegastormParams(r1, r2, vec4(r3.x, r3.y, 0.0, 0.0));
	float v = 0.0;
	if (field < 3) {
		if (field == 2) v = ring_megastorm_shaft_noise(r0.y, p);
		else {
			vec2 n = ring_megastorm_across_noise(r0.y, p);
			v = field == 0 ? n.x : n.y;
		}
	} else if (field < 11) {
		vec4 heights;
		vec4 aux;
		ring_megastorm_column_terms(r0.x, r0.y, r4.xy, p, heights, aux);
		if (field < 7) v = heights[field - 3];
		else v = aux[field - 7];
	} else if (field < 13) {
		// Glow: strength and meander phase at the double time (the CPU splits t into slot and slot_time in double).
		RingMegastormParams pg = RingMegastormParams(vec4(r1.x, r1.y, r5.x, r1.w), r2, vec4(r5.y, r3.y, 0.0, 0.0));
		v = ring_ms_flash_glow(r0.x, r0.y, r0.z, int(r3.z), r0.w, field == 12, r3.w, 1.0, pg);
	}
	uint bits = floatBitsToUint(v);
	COLOR = vec4(float(bits & 255u), float((bits >> 8u) & 255u), float((bits >> 16u) & 255u), float(bits >> 24u)) / 255.0;
}
