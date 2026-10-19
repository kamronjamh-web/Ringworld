// Sky-view LUT (P7-1 / P8-1 of docs/atmosphere-haze-revamp-plan.md): a phase-factored cache of ring_haze_eval_sky used ONLY by
// materials/ring_local_sky.gdshader. Include AFTER ring_atmosphere_shared.gdshaderinc and ring_haze_shared.gdshaderinc. The portable part
// (structs, ring_haze_eval_sky_terms, mapping) compiles as Godot shader language and as raw GLSL 450 (the build compute,
// materials/atmosphere/ring_haze_sky_view_build.comp, and the GPU tests). The sampler/lookup part is skipped when RING_HAZE_NO_LUTS is
// defined (compute builders and tools/haze_consistency_gpu_test.gd, which turns uniforms into plain globals).
//
// Factoring. ring_haze_eval_sky returns S_sun = (1 - T) E day t_sun (tau_R P_R(mu) + w_mie P_M(mu, g)) / tau_total and
// S_amb = (1 - T) mix(night, amb, day) (tau_R + w_mie) / tau_total. Everything except the phase terms, E_sun and the day weight depends only
// on the view direction (and the sun direction through t_sun, so the LUT is rebuilt when the sun moves), hence per texel
//   T, A_R = (1-T) t_sun tau_R / tau_total, A_M = (1-T) t_sun w_mie / tau_total, B = (1-T)(tau_R + w_mie) / tau_total
// and the lookup assembles S_sun = E day (A_R P_R(mu) + A_M P_M(mu, g)), S_amb = mix(night, amb, day) B with the live E/day/ambient/g.
//
// Texture layout, 3 x RGBA16F 192 x 108:   lut0 = (T.rgb, B.r)   lut1 = (A_R.rgb, B.g)   lut2 = (A_M.rgb, B.b)
// Mapping (frame: up = -outward, axis orthogonalised against up, around = cross(up, axis)):
//   u = fract(psi / 2 pi), psi = atan(dir . around, dir . axis)                (azimuth from the ring axis, texel boundaries at psi = 0, pi)
//   rows split in two halves of 54 at the graze elevation e_g(psi): the upward ray whose closest approach to the ring axis equals the top of
//   the air (radius - cap). Above it the first air interval ends at the cap entry, below it the ray crosses the whole chord, so T and S jump
//   there and the halves are never filtered together (each half uses its own row range, texel centres at both ends).
//     sin^2 e_g = k sin^2 psi / (1 - k cos^2 psi),   k = 1 - ((R - cap) / (R - h0))^2
//   Within a half, x = sqrt((|e - e_g| - eps) / (span - eps)) in [0, 1], row = base +/- 53 x (the sqrt packs resolution next to the graze).
//   Lower half rows 0..53 (row 53 at the graze), upper half rows 54..107 (row 54 at the graze).
//
// Consumption policy. The visible sky pass reads the LUT only in the upper half (ring_haze_sky_view_covers). In the lower half the ray comes
// back down to the floor or leaves through a sidewall inside the air, and the analytic T / S has a near-step along the curve where the
// sidewall exit coincides with the far-floor arrival; it is a fraction of a degree wide in azimuth against 1.875 degree columns, so no grid
// resolves it. Those directions are always covered by terrain / far ring / sidewall geometry in the visible pass, which uses the analytic
// function there. The radiance cubemap pass (AT_CUBEMAP_PASS) uses the LUT everywhere.

const float RING_HAZE_SKY_LUT_WIDTH = 192.0;
const float RING_HAZE_SKY_LUT_HEIGHT = 108.0;
const float RING_HAZE_SKY_LUT_HALF_ROWS = 54.0;
const float RING_HAZE_SKY_GRAZE_EPSILON = 0.0003; // radians offset of the first row from the graze elevation
const float RING_HAZE_SKY_PI = 3.14159265;

struct RingHazeSkyTerms {
	vec3 T;
	vec3 A_R;
	vec3 A_M;
	vec3 B;
};

struct RingHazeSkyFrame {
	vec3 up;
	vec3 axis;
	vec3 around;
	float k;
};

// Same hub blend as ring_haze_uniform_params / ring_haze_eval_remote (identity at weight 0): the primary light direction leans toward the
// local zenith `up` while the eclipse hub lights the sky. Used to build the LUT with exactly the direction the sky shader's params carry.
vec3 ring_haze_blend_sun(vec3 raw_to_sun, vec3 up, float hub_weight) {
	vec3 blended = mix(normalize(raw_to_sun), up, clamp(hub_weight, 0.0, 1.0));
	return length(blended) > 0.0001 ? normalize(blended) : up;
}

// ring_haze_eval_sky split into direction-only terms. Same interval / optics / sun transmittance calls as ring_haze_eval_sky (via
// ring_haze_eval). Stormed params are supplied by the caller (ring_haze_apply_storm is the single producer).
RingHazeSkyTerms ring_haze_eval_sky_terms(RingHazeParams p, vec3 dir, vec3 camera, float camera_height, vec3 outward, vec3 axis, float side_negative, float side_positive, float coordinate_scale, float radius_m, float floor_depth_m) {
	RingHazeSkyTerms r;
	r.T = vec3(1.0);
	r.A_R = vec3(0.0);
	r.A_M = vec3(0.0);
	r.B = vec3(0.0);
	float scale = max(coordinate_scale, 0.000001);
	vec3 target = camera + normalize(dir) * (2.0 * radius_m * scale);
	vec3 delta = target - camera;
	if (length(delta) <= 0.0001 * scale) return r;
	vec3 ray = normalize(delta);
	vec2 interval = ring_haze_air_interval(target, camera, camera_height, outward, axis, side_negative, side_positive, scale, radius_m, floor_depth_m, p.cap_height);
	if (interval.y <= interval.x) return r;
	RingHazeOptics o = ring_haze_optics(p, ray, interval, camera_height, outward, axis, radius_m);
	float mie_extinction = o.tau_A + o.tau_M;
	vec3 tau_total = o.tau_R + vec3(mie_extinction);
	r.T = exp(-tau_total);
	vec3 one_minus_T = vec3(ring_haze_one_minus_exp(tau_total.x), ring_haze_one_minus_exp(tau_total.y), ring_haze_one_minus_exp(tau_total.z));
	vec3 inv_total = vec3(1.0) / max(tau_total, vec3(0.00000001));
	float mie_scatter = p.omega_A * o.tau_A + p.omega_M * o.tau_M;
	vec3 t_sun = ring_haze_sun_transmittance(p, ray, interval, camera_height, outward, axis, radius_m);
	vec3 base = one_minus_T * t_sun * inv_total;
	r.A_R = base * o.tau_R;
	r.A_M = base * vec3(mie_scatter);
	r.B = one_minus_T * (o.tau_R + vec3(mie_scatter)) * inv_total;
	return r;
}

// Rebuilds the RingHaze ring_haze_eval_sky would return from the terms, with the live E_sun / day / ambient / g of `p` and the sun phase
// at `ray` (view direction).
RingHaze ring_haze_sky_terms_assemble(RingHazeParams p, RingHazeSkyTerms t, vec3 ray) {
	RingHaze result;
	float mu = dot(normalize(ray), normalize(p.to_sun));
	float day = clamp(p.day, 0.0, 1.0);
	result.T = t.T;
	result.S_sun = p.E_sun * day * (t.A_R * ring_haze_phase_rayleigh(mu) + t.A_M * ring_haze_phase_mie(mu, p.g));
	result.S_amb = mix(p.night_ambient, p.ambient, day) * t.B;
	return result;
}

// Graze constant k (see header). Cancellation-free: 1 - (a/b)^2 = (b - a)(b + a) / b^2 with b = R - h0, a = R - cap.
float ring_haze_sky_graze_constant(float radius_m, float cap_height_m, float camera_height_m) {
	float b = radius_m - camera_height_m;
	return clamp((cap_height_m - camera_height_m) * (2.0 * radius_m - cap_height_m - camera_height_m) / max(b * b, 1.0), 0.0, 0.5);
}

RingHazeSkyFrame ring_haze_sky_frame(vec3 outward, vec3 axis, float k) {
	RingHazeSkyFrame f;
	f.up = -normalize(outward);
	f.axis = normalize(axis - f.up * dot(axis, f.up));
	f.around = cross(f.up, f.axis);
	f.k = k;
	return f;
}

// Elevation (radians above the local horizontal plane) of the graze ray at azimuth psi.
float ring_haze_sky_graze_elevation(float k, float psi) {
	float s = sin(psi);
	float c = cos(psi);
	float sin_g = sqrt(max(k, 0.0)) * abs(s) / sqrt(max(1.0 - k * c * c, 0.000001));
	return asin(min(sin_g, 0.9999));
}

float ring_haze_sky_x_from_offset(float offset, float span) {
	return sqrt(clamp((offset - RING_HAZE_SKY_GRAZE_EPSILON) / max(span - RING_HAZE_SKY_GRAZE_EPSILON, 0.0001), 0.0, 1.0));
}

float ring_haze_sky_offset_from_x(float x, float span) {
	return RING_HAZE_SKY_GRAZE_EPSILON + x * x * max(span - RING_HAZE_SKY_GRAZE_EPSILON, 0.0);
}

// (psi, e) of a direction: azimuth from the ring axis and elevation above the local horizontal plane.
vec2 ring_haze_sky_view_angles(RingHazeSkyFrame f, vec3 dir) {
	vec3 d = normalize(dir);
	float x_axis = dot(d, f.axis);
	float y_around = dot(d, f.around);
	float psi = abs(x_axis) + abs(y_around) > 0.0000001 ? atan(y_around, x_axis) : 0.0;
	float e = asin(clamp(dot(d, f.up), -1.0, 1.0));
	return vec2(psi, e);
}

// True when the direction lies at or above the graze elevation of its azimuth (the upper half of the LUT).
bool ring_haze_sky_view_upper_half(RingHazeSkyFrame f, vec3 dir) {
	vec2 a = ring_haze_sky_view_angles(f, dir);
	return a.y >= ring_haze_sky_graze_elevation(f.k, a.x);
}

// Direction -> texture coordinate (u, v); v addresses texel centres, so the clamp-per-half is implicit in x in [0, 1].
vec2 ring_haze_sky_view_uv(RingHazeSkyFrame f, vec3 dir) {
	vec2 angles = ring_haze_sky_view_angles(f, dir);
	float psi = angles.x;
	float e = angles.y;
	float e_g = ring_haze_sky_graze_elevation(f.k, psi);
	float half_pi = 0.5 * RING_HAZE_SKY_PI;
	float last = RING_HAZE_SKY_LUT_HALF_ROWS - 1.0;
	float row;
	if (e >= e_g) row = RING_HAZE_SKY_LUT_HALF_ROWS + last * ring_haze_sky_x_from_offset(e - e_g, half_pi - e_g);
	else row = last - last * ring_haze_sky_x_from_offset(e_g - e, half_pi + e_g);
	return vec2(fract(psi / (2.0 * RING_HAZE_SKY_PI)), (row + 0.5) / RING_HAZE_SKY_LUT_HEIGHT);
}

// Inverse of the mapping for a texel (build side).
vec3 ring_haze_sky_texel_direction(RingHazeSkyFrame f, ivec2 texel) {
	float psi = 2.0 * RING_HAZE_SKY_PI * (float(texel.x) + 0.5) / RING_HAZE_SKY_LUT_WIDTH;
	float e_g = ring_haze_sky_graze_elevation(f.k, psi);
	float half_pi = 0.5 * RING_HAZE_SKY_PI;
	float last = RING_HAZE_SKY_LUT_HALF_ROWS - 1.0;
	float e;
	if (float(texel.y) >= RING_HAZE_SKY_LUT_HALF_ROWS) e = e_g + ring_haze_sky_offset_from_x((float(texel.y) - RING_HAZE_SKY_LUT_HALF_ROWS) / last, half_pi - e_g);
	else e = e_g - ring_haze_sky_offset_from_x((last - float(texel.y)) / last, half_pi + e_g);
	float ce = cos(e);
	return f.axis * (ce * cos(psi)) + f.around * (ce * sin(psi)) + f.up * sin(e);
}

#ifndef RING_HAZE_NO_LUTS
// Built by scripts/Atmosphere/RingAtmosphereLuts.cs (render-thread compute -> Texture2DRD). The key is published by the same class:
// (camera_h_m, across_m, graze constant k, valid). Consumers use the LUT only when valid > 0.5 and the key matches their own camera
// height / across (0.5 m), otherwise they evaluate the analytic function exactly as before.
global uniform sampler2D ringworld_haze_sky_lut0 : filter_linear, repeat_enable;
global uniform sampler2D ringworld_haze_sky_lut1 : filter_linear, repeat_enable;
global uniform sampler2D ringworld_haze_sky_lut2 : filter_linear, repeat_enable;
global uniform vec4 ringworld_haze_sky_lut_key;

bool ring_haze_sky_view_usable(float camera_height_m, float across_m) {
	vec4 key = ringworld_haze_sky_lut_key;
	return key.w > 0.5 && abs(key.x - camera_height_m) < 0.5 && abs(key.y - across_m) < 0.5;
}

// Consumption policy (see header): the lower half is only read where allow_lower_half is set (radiance cubemap pass).
bool ring_haze_sky_view_covers(vec3 dir, vec3 outward, vec3 axis, bool allow_lower_half) {
	return allow_lower_half || ring_haze_sky_view_upper_half(ring_haze_sky_frame(outward, axis, ringworld_haze_sky_lut_key.z), dir);
}

RingHazeSkyTerms ring_haze_sky_view_sample(vec3 dir, vec3 outward, vec3 axis) {
	RingHazeSkyFrame f = ring_haze_sky_frame(outward, axis, ringworld_haze_sky_lut_key.z);
	vec2 uv = ring_haze_sky_view_uv(f, dir);
	vec4 s0 = textureLod(ringworld_haze_sky_lut0, uv, 0.0);
	vec4 s1 = textureLod(ringworld_haze_sky_lut1, uv, 0.0);
	vec4 s2 = textureLod(ringworld_haze_sky_lut2, uv, 0.0);
	RingHazeSkyTerms t;
	t.T = s0.rgb;
	t.A_R = s1.rgb;
	t.A_M = s2.rgb;
	t.B = vec3(s0.a, s1.a, s2.a);
	return t;
}
#endif
