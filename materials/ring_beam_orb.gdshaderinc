// Shared orb maths (a camera-facing billboard with a Gaussian white core and an exponential teal halo), used by:
//   ring_eclipse_hub_beam_common.gdshaderinc        the false sun's Sustain refuel orbs (EB_CORE_PASS = the depth core)
//   ring_emitter_field_shot_common.gdshaderinc      the emitters' field shots (EFS_CORE_PASS = the depth core)
// Pure functions of their arguments (plus the ribbon helpers): no uniforms, no varyings, no built-ins. The expressions are the orb's original ones, moved
// here unchanged, so the false sun looks exactly as before.
#include "res://materials/ring_beam_ribbon.gdshaderinc"

const float ORB_HALO_AMPLITUDE = 0.25;

// Half size (world units) of the billboard quad: wide enough for eight halo e-folds, five core sigmas and the depth footprint.
// profile = ribbon_profile(...); depth_half = ribbon_depth_half(...); mpp = metres per pixel at the orb.
float orb_half_size(vec4 profile, float depth_half, float mpp) {
	return max(8.0 * profile.y, max(5.0 * profile.x, depth_half + mpp));
}

// Colour fragment at distance orb_r from the centre: rgb = the orb radiance (white core mixed into color), a = the scalar value (callers discard below 0.0001).
// orb_level = the light level before the profile (envelope x day x atmosphere x layer weight), emission = HDR gain, half_size = orb_half_size().
vec4 orb_radiance(float orb_r, float orb_level, vec4 profile, float half_size, float emission, vec3 color) {
	float orb_total = ribbon_cross_profile(orb_r, profile, ORB_HALO_AMPLITUDE);
	float orb_core_only = ribbon_cross_profile(orb_r, profile, 0.0);
	float orb_edge = 1.0 - smoothstep(0.7, 1.0, orb_r / max(half_size, 1.0e-6));
	float orb_value = orb_level * orb_total * orb_edge * emission;
	return vec4(mix(color, vec3(1.0), clamp(orb_core_only / max(orb_total, 1.0e-6), 0.0, 1.0)) * orb_value, orb_value);
}

// Depth core: true where the lit orb core is bright (coverage * level above core_depth.x), widened to the minimum depth footprint (depth_half).
bool orb_depth_core_covers(float orb_level, vec4 profile, vec2 core_depth, float orb_r, float depth_half) {
	return ribbon_depth_core_covers(orb_level * profile.z, max(core_depth.x, 1.0e-4), profile.x, orb_r, depth_half);
}
