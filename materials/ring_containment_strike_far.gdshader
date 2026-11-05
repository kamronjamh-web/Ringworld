shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;

// Containment field far strike layer (docs/containment-field-design.md 3.10.3, WO4): the LIGHT of ripples that reach past the main camera's far fade band and of every
// far-list strike (> 350 km), in the scaled far viewport (scripts/ContainmentField/RingContainmentFarStrikeLayer.cs). Additive, never writes depth; each fragment crossfades with
// the main-view ripple patch (ring_containment_ripple.gdshader), which fades out over cf_far_fade, by the same smoothstep (this layer fades in over it).
// One MultiMesh quad per ripple / strike in the local XY plane, corners +-1. The instance basis columns are (T_around P, T_arch P, N) in scaled units, P = 1.15 max(Rmax, Rc) m x world
// scale, the origin is ScaledPoint(contact), so local xy x P is the surface metres from the contact (x along around, y along the increasing arch), the frame cf_ripple_eval uses.
// INSTANCE_CUSTOM = (strike level, Rc m, crawl front m, seed * 256 + far-list flag * 128 + round((bias + 1) * 63.5)), COLOR = wave (A, front radius rho m, Rmax m, flash); both float32 here (probed: a custom
// value of 16,776,960 round-trips exactly), so the packed seed (up to 65535 * 256 + 255 < 2^24) is exact. The CPU has already multiplied A and the strike level by the air transmittance.

#include "res://materials/ring_containment_strike.gdshaderinc"
#include "res://materials/ring_containment_field_core.gdshaderinc"

uniform vec2 cf_far_fade = vec2(1.0e9, 1.1e9);   // (start, end) metres, ContainmentRipplePatches.FarFade(main camera far)
uniform float world_scale = 0.001;               // RingScaledFarViewport.CoordinateScale

varying vec2 cf_local;
varying flat vec4 cf_custom;
varying flat vec4 cf_wave;

void vertex() {
	cf_local = VERTEX.xy;
	cf_custom = INSTANCE_CUSTOM;
	cf_wave = COLOR;
}

void fragment() {
	float rc = cf_custom.y;
	float patch_m = CF_RIPPLE_PATCH * max(cf_wave.z, rc);
	vec2 d = cf_local * patch_m;
	vec2 fw = fwidth(d);
	float pixel_m = max(fw.x, fw.y);
	float packed_seed = cf_custom.w;
	float seed = floor(packed_seed / 256.0);
	float low = packed_seed - seed * 256.0;
	float far_list = low >= 127.5 ? 1.0 : 0.0;   // far-list strike: the main view never draws it
	float bias = (low - 128.0 * far_list) / 63.5 - 1.0;
	vec2 slope = vec2(0.0);
	float weight = 0.0;
	float boost = 0.0;
	vec3 light = vec3(0.0);
	cf_ripple_eval(d, pixel_m, cf_wave, vec4(rc, cf_custom.z, seed, bias), cf_custom.x, slope, weight, boost, light);
	float metres = length(VERTEX) / max(world_scale, 1.0e-9);
	light *= max(far_list, smoothstep(cf_far_fade.x, cf_far_fade.y, metres));
	if (max(light.x, max(light.y, light.z)) < 1.0 / 4096.0) discard;
	ALBEDO = light;
}
