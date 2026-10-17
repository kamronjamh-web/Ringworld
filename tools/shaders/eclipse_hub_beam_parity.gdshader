shader_type canvas_item;
// Test-only: evaluates the GLSL hub beam state math (materials/ring_eclipse_hub_beam_math.gdshaderinc) for a texture of samples and writes
// each float32 result as 4 bytes (12 pixels per sample: tip, handoff, tip mask, refuel-aware level, refuel flash envelope, flash tip, deck mask, in-cloud factor, core keep, orb envelope, orb fraction, orb level), so tools/eclipse_hub_beam_shader_parity_test.gd can
// compare it against EclipseHubBeamMath in C#.
render_mode blend_disabled;
#include "res://materials/ring_eclipse_hub_beam_math.gdshaderinc"

// Row 0: (fire tau, kind, chord fraction f, hub day). Row 1: (atmosphere visibility, layer weight, cloud k, deck low). Row 2: (p_site, column, deck high (negative = unqueried), height above the crown).
uniform sampler2D samples : filter_nearest;

void fragment() {
	ivec2 px = ivec2(FRAGCOORD.xy);
	int sample_index = px.x / 12;
	int field = px.x - sample_index * 12;
	vec4 a = texelFetch(samples, ivec2(sample_index, 0), 0);
	vec4 b = texelFetch(samples, ivec2(sample_index, 1), 0);
	vec4 s = texelFetch(samples, ivec2(sample_index, 2), 0);
	vec2 flash = ehb_refuel(a.x, a.y, s.x, s.y);
	float tip = ehb_tip(a.x, a.y);
	float v;
	if (field == 0) v = tip;
	else if (field == 1) v = ehb_handoff(a.y);
	else if (field == 2) v = ehb_tip_mask(a.z, tip);
	else if (field == 3) v = ehb_level_refuel(a.x, a.y, a.z, a.w, b.x, b.y, flash);
	else if (field == 4) v = flash.x;
	else if (field == 5) v = flash.y;
	else if (field >= 9) {
		vec2 orb = ehb_orb(a.x, a.y, s.x, s.y);
		if (field == 9) v = orb.x;
		else if (field == 10) v = orb.y;
		else v = ehb_orb_level(orb.x, a.w, b.x, b.y);
	} else {
		vec4 cloud = vec4(b.z, b.w, abs(s.z), step(0.0, s.z));
		float factor = ehb_cloud_factor(cloud, s.w);
		if (field == 6) v = ehb_cloud_mask(s.w, cloud.y, cloud.z);
		else if (field == 7) v = factor;
		else v = ehb_cloud_core_keep(factor);
	}
	uint bits = floatBitsToUint(v);
	COLOR = vec4(float(bits & 255u), float((bits >> 8u) & 255u), float((bits >> 16u) & 255u), float(bits >> 24u)) / 255.0;
}
