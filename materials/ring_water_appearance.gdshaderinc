uniform float refraction_strength : hint_range(0.0, 0.08) = 0.012;
uniform float absorption_density : hint_range(0.001, 2.0) = 0.11;
// RGB Beer-Lambert extinction. Red should remain the largest coefficient and
// blue the smallest for clear coastal water.
uniform vec3 absorption_coefficients : source_color = vec3(0.30, 0.12, 0.035);
uniform float shallow_inscatter_strength : hint_range(0.0, 2.0) = 0.58;
uniform float crest_transmission_strength : hint_range(0.0, 2.0) = 0.95;
uniform float shallow_alpha : hint_range(0.0, 1.0) = 0.38;
uniform float deep_alpha : hint_range(0.0, 1.0) = 0.995;
uniform float shore_foam_depth_meters : hint_range(0.1, 20.0) = 0.5;
uniform float shore_foam_strength : hint_range(0.0, 2.0) = 0.82;
uniform float specular_aa_strength : hint_range(0.0, 2.0) = 0.85;
uniform float breaking_foam_slope_start : hint_range(0.05, 1.5) = 0.28;
uniform float breaking_foam_slope_end : hint_range(0.1, 2.0) = 0.48;
uniform float breaking_foam_strength : hint_range(0.0, 2.0) = 0.55;
uniform vec3 shallow_water_tint : source_color = vec3(0.055, 0.42, 0.50);
uniform vec3 deep_water_tint : source_color = vec3(0.012, 0.075, 0.16);
uniform vec3 foam_color : source_color = vec3(0.72, 0.88, 0.91);

// Water-only SSR is deliberately bounded and has no temporal history.  It is
// composed into the reflection layer only; refraction and volume optics stay
// independent.
uniform bool water_ssr_enabled = true;
uniform int water_ssr_coarse_steps : hint_range(1, 32) = 24;
uniform int water_ssr_refinement_steps : hint_range(0, 8) = 4;
uniform float water_ssr_camera_distance : hint_range(10.0, 1000.0) = 450.0;
uniform float water_ssr_depth_tolerance_near : hint_range(0.01, 2.0) = 0.35;
uniform float water_ssr_ray_bias : hint_range(0.01, 1.0) = 0.12;
uniform float water_ssr_edge_fade : hint_range(0.01, 0.4) = 0.12;
uniform float water_ssr_roughness_cutoff : hint_range(0.02, 1.0) = 0.32;
uniform int water_ssr_debug_mode : hint_range(0, 3) = 0;

const float RING_WATER_F0_PHYSICAL = 0.02;
const float RING_WATER_SPECULAR = 0.353553; // sqrt(0.02 / 0.16): Godot f0 = 0.16 x SPECULAR^2, f90 = clamp(50 f0) = 1
// Karis 2014 fit of the split-sum DFG integral the engine's LUT holds (prefiltered_dfg), with f0 0.02 and f90 1.
float ring_water_env_reflectance(float roughness, float n_dot_v) {
	const vec4 c0 = vec4(-1.0, -0.0275, -0.572, 0.022);
	const vec4 c1 = vec4(1.0, 0.0425, 1.04, -0.04);
	vec4 q = roughness * c0 + c1;
	float a004 = min(q.x * q.x, exp2(-9.28 * n_dot_v)) * q.x + q.y;
	vec2 ab = vec2(-1.04, 1.04) * a004 + q.zw;
	return clamp(RING_WATER_F0_PHYSICAL * ab.x + ab.y, 0.0, 1.0);
}
