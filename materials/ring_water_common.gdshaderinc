// The CPU supplies individually bounded phases from the authoritative double
// simulation clock; wrapping a common elapsed-time float would pop these bands.
uniform bool water_wind_clock_enabled = false;
uniform vec2 water_mean_wind_direction = vec2(0.916, 0.401);
uniform vec3 water_wave_phases[12];
uniform float water_mean_response = 1.0;
uniform float water_mean_wind_speed = 0.0;
uniform float water_gust_response_strength = 0.0;
uniform bool water_spatial_gust_enabled = false;
uniform float water_river_advection = 0.0;
uniform vec4 water_far_group_translation = vec4(0.0);
// -1 keeps the per-material clock values above. 0 (unscaled) and 1 (scaled-far) read the shared
// globals below, which RingWaterModel publishes once per wind clock update. Phase vector 3m+c is
// column c of mat3 m (arrays cannot be global).
uniform int water_wind_global_slot = -1;
global uniform mat3 ring_water_wave_phases_0;
global uniform mat3 ring_water_wave_phases_1;
global uniform mat3 ring_water_wave_phases_2;
global uniform mat3 ring_water_wave_phases_3;
global uniform float ring_water_river_advection_main;
global uniform float ring_water_river_advection_far;
global uniform vec4 ring_water_far_group_translation_main;
global uniform vec4 ring_water_far_group_translation_far;
// Wave precision (W1, 2026-11-20; scripts/Voxel/LOD/RingWaterModel.WaveAnchor.cs). An anchor near the observer in the main view's world frame:
// world.xyz its position, world.w its distance from the ring axis; tangent.xyz the around direction there, tangent.w 1 when valid; phase.xy its around
// fraction u_A split at 2^-20 into K and r (zw: the same with the river advection folded in). Only slot-0 materials (the main view, unscaled) use it.
global uniform vec4 ring_water_wave_anchor_world;
global uniform vec4 ring_water_wave_anchor_tangent;
global uniform vec4 ring_water_wave_anchor_phase;

uniform sampler2D water_screen_texture : hint_screen_texture, repeat_disable, filter_linear_mipmap;
uniform sampler2D water_depth_texture : hint_depth_texture, repeat_disable, filter_nearest;

uniform float wave_height_meters : hint_range(0.0, 2.0) = 0.32;
uniform float wave_length_meters : hint_range(2.0, 200.0) = 28.0;
uniform float wave_speed : hint_range(0.0, 10.0) = 1.35;
uniform float wave_detail_strength : hint_range(0.0, 1.0) = 0.42;
uniform float wave_domain_warp_strength : hint_range(0.0, 1.5) = 0.46;
uniform float wave_crest_sharpness : hint_range(0.05, 4.0) = 1.35;
uniform float wave_crest_blend : hint_range(0.0, 1.0) = 0.62;
uniform float wave_normal_strength : hint_range(0.0, 2.0) = 0.90;
uniform float wave_bandlimit_start_radians : hint_range(0.05, 2.0) = 0.35;
uniform float wave_bandlimit_end_radians : hint_range(0.1, 3.0) = 1.10;
uniform float wave_geometry_fade_meters : hint_range(100.0, 50000.0) = 8000.0;
uniform float wave_geometry_strength : hint_range(0.0, 1.0) = 1.0;
uniform float wave_normal_fade_start_meters : hint_range(100.0, 50000.0) = 500.0;
uniform float wave_normal_fade_end_meters : hint_range(200.0, 100000.0) = 3000.0;
// Shared anti-repetition controls. Their zero values deliberately isolate the
// original swell/foam paths for tuning and regression captures.
uniform float swell_variation : hint_range(0.0, 1.0) = 0.65;
uniform float foam_patchiness : hint_range(0.0, 1.0) = 0.65;
uniform float distant_foam_visibility : hint_range(0.0, 1.0) = 0.20;
uniform float detail_filtering_strength : hint_range(0.0, 2.0) = 1.0;
// RingWaterModel supplies this for scaled-far materials. Keep procedural cell
// sizes in physical metres while coordinates are scaled for distant rendering.
uniform float water_coordinate_scale = 1.0;
#include "res://materials/ring_water_appearance.gdshaderinc"

vec3 ring_water_global_phases(int index) {
    int matrix_index = index / 3;
    int column = index - matrix_index * 3;
    mat3 packed_phases = matrix_index == 0 ? ring_water_wave_phases_0
        : (matrix_index == 1 ? ring_water_wave_phases_1
        : (matrix_index == 2 ? ring_water_wave_phases_2 : ring_water_wave_phases_3));
    return packed_phases[column];
}
float ring_water_river_advection_value() {
    return water_wind_global_slot == 0 ? ring_water_river_advection_main
        : (water_wind_global_slot == 1 ? ring_water_river_advection_far : water_river_advection);
}
vec4 ring_water_far_group_translation_value() {
    return water_wind_global_slot == 0 ? ring_water_far_group_translation_main
        : (water_wind_global_slot == 1 ? ring_water_far_group_translation_far : water_far_group_translation);
}
float ring_water_phase(int index, float coefficient, float time_seconds) {
    if (!water_wind_clock_enabled) return time_seconds * wave_speed * coefficient;
    vec3 phases = water_wind_global_slot >= 0 ? ring_water_global_phases(index) : water_wave_phases[index];
    return time_seconds > 0.8 ? phases.x : (time_seconds > 0.3 ? phases.y : phases.z);
}
vec2 ring_water_wind_direction(vec2 direction) {
    if (!water_wind_clock_enabled) return direction;
    vec2 base = normalize(vec2(0.916, 0.401));
    vec2 wind = normalize(water_mean_wind_direction);
    float cosine = dot(base, wind);
    float sine = base.x * wind.y - base.y * wind.x;
    return vec2(direction.x * cosine - direction.y * sine,
        direction.x * sine + direction.y * cosine);
}
// Quantize every around-ring phase gradient (including domain warp) so changes
// in wind bearing remain closed at atan's seam, including scaled-far rendering.
// around_cycles is the integer n of cycles per turn (0 without the wind clock): the train's around phase is 2 pi n u for the around fraction u.
vec2 ring_water_wave_direction(vec2 direction, float frequency, float kind_clock_scale, out float around_cycles) {
    vec2 rotated = ring_water_wind_direction(direction);
    around_cycles = 0.0;
    if (!water_wind_clock_enabled) return rotated;
    float circumference = 6.28318530718 * max(ring_radius_meters, 1.0);
    float kind_coordinate_scale = kind_clock_scale > 0.8 ? 1.0 : (kind_clock_scale > 0.3 ? 1.65 : 2.15);
    circumference *= kind_coordinate_scale;
    around_cycles = floor(rotated.x * frequency * circumference / 6.28318530718 + 0.5);
    rotated.x = around_cycles * 6.28318530718 / circumference / max(frequency, 0.000001);
    return rotated;
}

// W1: the anchored exact phase. Off without the wind clock, for scaled-far or per-material (slot -1) bindings and without a published anchor; those
// keep the legacy absolute coordinate.
bool ring_water_anchor_active() {
    return water_wind_clock_enabled && water_wind_global_slot == 0 && ring_water_wave_anchor_tangent.w > 0.5;
}
// Local wave coordinate: x is the around angle from the anchor times the ring radius uniform (the metric of ring_water_coordinates), y the legacy
// across coordinate. The angle comes from small anchor-relative offsets, so it keeps millimetre precision anywhere on the ring.
vec2 ring_water_anchor_coordinates(vec3 sample_world_position, vec3 sample_ring_center) {
    vec3 offset = sample_world_position - ring_water_wave_anchor_world.xyz;
    vec3 tangent = ring_water_wave_anchor_tangent.xyz;
    // The outward radial at the anchor is (t.z, 0, -t.x): the ring axis is world Y.
    float local_angle = atan(dot(offset, tangent), ring_water_wave_anchor_world.w + offset.x * tangent.z - offset.z * tangent.x);
    return vec2(local_angle * max(1.0, ring_radius_meters), sample_world_position.y - sample_ring_center.y);
}
// 2 pi frac(n u_A) with u_A = K 2^-20 + r: (n K) mod 2^20 is exact in 32-bit integer arithmetic (two's complement keeps it exact for n < 0), and
// n r stays below about 12 cycles, so the float remainder keeps ~1e-6 of a cycle.
float ring_water_anchor_phase(float around_cycles, vec2 split) {
    uint whole = (uint(int(around_cycles)) * uint(split.x)) & 0xFFFFFu;
    return 6.28318530718 * fract(float(whole) * (1.0 / 1048576.0) + around_cycles * split.y);
}
vec2 ring_water_anchor_split(float water_kind) {
    return water_kind < 2.5 ? ring_water_wave_anchor_phase.xy : ring_water_wave_anchor_phase.zw;
}

vec2 ring_water_coordinates(vec3 sample_world_position, vec3 sample_ring_center, float radius_meters) {
	vec3 radial = sample_world_position - sample_ring_center;
	float angle = atan(radial.z, radial.x);
	return vec2(angle * max(1.0, radius_meters), radial.y);
}

float ring_water_hash_cell(ivec2 cell) {
	// Integer mixing remains stable for the potentially large across-ring cell
	// index. The around-ring index is wrapped before reaching this function.
	uint h = uint(cell.x) * 0x8da6b343u + uint(cell.y) * 0xd8163841u;
	h ^= h >> 13u;
	h *= 0xcb1ab31fu;
	h ^= h >> 16u;
	return float(h) * (1.0 / 4294967295.0);
}

float ring_water_patch_value_gradient(
	vec2 p,
	float radius_meters,
	float cell_size_meters,
	out vec2 value_gradient) {
	float circumference = 6.28318530718 * max(radius_meters, 1.0);
	float scaled_cell_size = max(cell_size_meters, 1.0) * max(water_coordinate_scale, 0.0001);
	float cells_around = max(1.0, floor(circumference / scaled_cell_size + 0.5));
	float around = fract(p.x / circumference + 0.5) * cells_around;
	float across = p.y / scaled_cell_size;
	vec2 cell = floor(vec2(around, across));
	vec2 f = fract(vec2(around, across));
	vec2 s = f * f * (3.0 - 2.0 * f);
	int x0 = int(mod(cell.x, cells_around));
	int x1 = int(mod(cell.x + 1.0, cells_around));
	int y0 = int(cell.y);
	float a = ring_water_hash_cell(ivec2(x0, y0));
	float b = ring_water_hash_cell(ivec2(x1, y0));
	float c = ring_water_hash_cell(ivec2(x0, y0 + 1));
	float d = ring_water_hash_cell(ivec2(x1, y0 + 1));
	float value_x0 = mix(a, b, s.x);
	float value_x1 = mix(c, d, s.x);
	float dvalue_dsx = mix(b - a, d - c, s.y);
	float dvalue_dsy = value_x1 - value_x0;
	vec2 smooth_derivative = 6.0 * f * (1.0 - f);
	value_gradient = vec2(
		dvalue_dsx * smooth_derivative.x * cells_around / circumference,
		dvalue_dsy * smooth_derivative.y / scaled_cell_size);
	return mix(value_x0, value_x1, s.y);
}

float ring_water_patch_value(vec2 p, float radius_meters, float cell_size_meters) {
	vec2 ignored_gradient;
	return ring_water_patch_value_gradient(p, radius_meters, cell_size_meters, ignored_gradient);
}

float ring_water_foam_patch_mask(vec2 p, float radius_meters) {
	if (foam_patchiness <= 0.001) return 1.0;
	float broad = ring_water_patch_value(p, radius_meters, 210.0);
	float medium = ring_water_patch_value(
		p + vec2(37.0, -91.0) * max(water_coordinate_scale, 0.0001),
		radius_meters,
		73.0);
	float irregular = smoothstep(0.30, 0.74, broad * 0.66 + medium * 0.34);
	return mix(1.0, irregular * 1.34, foam_patchiness);
}

// Water kind is packed into vertex alpha by RingWaterModel. The water shader
// computes final transparency itself, leaving this channel available without
// expanding every streamed mesh with another custom attribute.
float ring_water_kind_from_vertex_alpha(float encoded_alpha) {
	return clamp(floor(encoded_alpha * 3.0 + 0.5), 1.0, 3.0);
}

float ring_water_profile_height_scale(float water_kind) {
	return water_kind < 1.5 ? 1.0 : (water_kind < 2.5 ? 0.24 : 0.11);
}

// Far-water roughness at reflection model 1 (docs/reflections-plan.md 10.7, user 2026-11-18): far away every wave is below a pixel, so the roughness is
// that of the whole slope variance (Cox and Munk), matched to the sky reflection of Gaussian slopes at grazing view. Ocean: a constant for about 8 m/s
// (first step; the wind-driven curve comes later). Lakes and rivers: their own calmer wave profile plus the capillary floor. The interpolated kind ramps
// between them at river mouths as the inland optics do (1.05 to 2.0).
const float RING_WATER_FAR_ROUGHNESS_OCEAN = 0.30;
const float RING_WATER_FAR_ROUGHNESS_INLAND = 0.155;
float ring_water_far_roughness_model1(float water_kind) {
	return mix(RING_WATER_FAR_ROUGHNESS_OCEAN, RING_WATER_FAR_ROUGHNESS_INLAND, clamp((water_kind - 1.05) / 0.95, 0.0, 1.0));
}

float ring_water_profile_shore_foam_scale(float water_kind) {
	// T2R-11b: calm inland water (lakes, rivers) has no surf line. Their former 0.30 / 0.46 of the ocean's shore foam, within the 0.5 m foam depth, lay as a
	// white band 2 to 3 m wide along gently sloped river banks (and a rim on lake shores) under the ring sun. The ocean keeps its shore foam.
	return water_kind < 1.5 ? 1.0 : 0.0;
}

// I0(k) is the mean of exp(k * sin(phase)) over one complete cycle. Removing
// it keeps the exponential crest profile at zero mean so sharper peaks do not
// raise the entire water sheet. k is capped at 4, where this series is stable.
float ring_water_i0_approx(float k) {
	float q = k * k * 0.25;
	float q2 = q * q;
	float q3 = q2 * q;
	float q4 = q3 * q;
	float q5 = q4 * q;
	float q6 = q5 * q;
	return 1.0 + q + q2 * 0.25 + q3 / 36.0 + q4 / 576.0
		+ q5 / 14400.0 + q6 / 518400.0;
}

float ring_water_wave_profile(float phase, float exponential_mean, float exponential_scale) {
	float sine_value = sin(phase);
	float exponential_value = (exp(wave_crest_sharpness * sine_value) - exponential_mean)
		* exponential_scale;
	return mix(sine_value, exponential_value, wave_crest_blend);
}

float ring_water_wave_profile_derivative(float phase, float exponential_scale) {
	float sine_value = sin(phase);
	float cosine_value = cos(phase);
	float exponential_derivative = wave_crest_sharpness
		* exp(wave_crest_sharpness * sine_value)
		* cosine_value
		* exponential_scale;
	return mix(cosine_value, exponential_derivative, wave_crest_blend);
}

float ring_water_band_weight(
	vec2 phase_gradient,
	vec2 coordinate_dx,
	vec2 coordinate_dy) {
	// Exact derivatives preserve the wave shape, but they do not make a signal
	// below pixel resolution safe to sample. Estimate each band's phase change
	// across this pixel and retire that band before it crosses Nyquist. The
	// exponential profile contains higher harmonics, so sharpened crests receive
	// a deliberately more conservative footprint.
	float phase_footprint = max(
		abs(dot(phase_gradient, coordinate_dx)),
		abs(dot(phase_gradient, coordinate_dy)));
	float crest_harmonic_scale = mix(
		1.0,
		1.0 + wave_crest_sharpness * 0.72,
		wave_crest_blend);
	float effective_footprint = phase_footprint * crest_harmonic_scale
		* detail_filtering_strength;
	return 1.0 - smoothstep(
		wave_bandlimit_start_radians,
		max(wave_bandlimit_start_radians + 0.01, wave_bandlimit_end_radians),
		effective_footprint);
}

// One wave train of the near stack: its phase (domain warp included) and the phase gradient per unit of p. With the anchor, p is the local coordinate
// and the anchor's exact around phase 2 pi frac(n u_A) is added to the train and to its warp (both are quantised around the ring).
float ring_water_train_phase(
	vec2 p,
	float base_frequency,
	float time_seconds,
	bool anchored,
	vec2 split,
	vec2 direction,
	float ratio,
	int index,
	float coefficient,
	float offset,
	vec2 warp_direction,
	float warp_ratio,
	float warp_coefficient,
	float warp_offset,
	float warp_scale,
	out vec2 phase_gradient) {
	float around_cycles;
	float warp_around_cycles;
	vec2 train_direction = ring_water_wave_direction(direction, base_frequency * ratio, time_seconds, around_cycles);
	vec2 train_warp_direction = ring_water_wave_direction(warp_direction, base_frequency * warp_ratio, time_seconds, warp_around_cycles);
	float warp_argument = dot(p, train_warp_direction) * base_frequency * warp_ratio + ring_water_phase(index + 5, warp_coefficient, time_seconds) + warp_offset;
	float phase = dot(p, train_direction) * base_frequency * ratio;
	if (anchored) {
		warp_argument += ring_water_anchor_phase(warp_around_cycles, split);
		phase += ring_water_anchor_phase(around_cycles, split);
	}
	phase_gradient = train_direction * base_frequency * ratio
		+ train_warp_direction * base_frequency * warp_ratio * cos(warp_argument) * wave_domain_warp_strength * warp_scale;
	return phase + sin(warp_argument) * wave_domain_warp_strength * warp_scale + ring_water_phase(index, coefficient, time_seconds) + offset;
}

// W2 (user, 2026-11-20): lake wind sea. The five trains spread within -37 to +31 degrees of the first (no two near perpendicular), and all travel the
// way the first does (a direction is negated where the phase rate is negative). The ocean and rivers keep the original set.
// Downwind (user, 2026-11-20): the mean wind is a velocity (clouds drift along +wind), and a train with phase dot(p, d) k + rate t moves along -d for a
// positive rate. The original first train (d = the wind, rate +1) therefore travelled upwind; the lake set is negated so every lake train travels with
// the wind, still within -37 to +31 degrees of it.
const vec2 RING_WATER_LAKE_DIRECTION_1 = vec2(-0.916, -0.401);   // the wind reference, negated
const vec2 RING_WATER_LAKE_DIRECTION_2 = vec2(0.7588, 0.6513);   // +17 degrees
const vec2 RING_WATER_LAKE_DIRECTION_3 = vec2(-0.9999, -0.0112); // -23 degrees
const vec2 RING_WATER_LAKE_DIRECTION_4 = vec2(0.5787, 0.8156);   // +31 degrees
const vec2 RING_WATER_LAKE_DIRECTION_5 = vec2(-0.9729, 0.2310);  // -37 degrees

// The five trains of the near stack (ring_water_wave_height_core and ring_water_wave_gradient_core share it).
void ring_water_wave_stack(
	vec2 p,
	float time_seconds,
	float water_kind,
	bool anchored,
	vec2 split,
	out vec4 phases,
	out float fifth_phase,
	out vec2 first_gradient,
	out vec2 second_gradient,
	out vec2 third_gradient,
	out vec2 fourth_gradient,
	out vec2 fifth_gradient) {
	float base_frequency = 6.28318530718 / max(2.0, wave_length_meters);
	bool lake = water_kind > 1.5 && water_kind < 2.5;
	phases.x = ring_water_train_phase(p, base_frequency, time_seconds, anchored, split,
		lake ? RING_WATER_LAKE_DIRECTION_1 : vec2(0.916, 0.401), 1.0, 0, 1.0, 0.0, vec2(-0.511, 0.860), 0.173, 0.071, 0.0, 1.0, first_gradient);
	phases.y = ring_water_train_phase(p, base_frequency, time_seconds, anchored, split,
		lake ? RING_WATER_LAKE_DIRECTION_2 : vec2(-0.349, 0.937), 1.431, 1, -1.173, 1.1, vec2(0.766, 0.643), 0.227, -0.053, 1.7, 0.87, second_gradient);
	phases.z = ring_water_train_phase(p, base_frequency, time_seconds, anchored, split,
		lake ? RING_WATER_LAKE_DIRECTION_3 : vec2(0.625, -0.781), 2.173, 2, 1.619, 2.4, vec2(-0.935, -0.355), 0.319, 0.041, 3.1, 0.74, third_gradient);
	phases.w = ring_water_train_phase(p, base_frequency, time_seconds, anchored, split,
		lake ? RING_WATER_LAKE_DIRECTION_4 : vec2(-0.829, -0.559), 3.117, 3, -2.071, 3.8, vec2(0.252, -0.968), 0.271, -0.063, 4.6, 0.63, fourth_gradient);
	fifth_phase = ring_water_train_phase(p, base_frequency, time_seconds, anchored, split,
		lake ? RING_WATER_LAKE_DIRECTION_5 : vec2(0.187, 0.982), 5.037, 4, 2.513, 5.2, vec2(0.982, -0.188), 0.413, 0.037, 0.8, 0.51, fifth_gradient);
}

float ring_water_wave_height_core(
	vec2 p,
	float time_seconds,
	vec2 coordinate_dx,
	vec2 coordinate_dy,
	float water_kind,
	bool anchored,
	vec2 split) {
	float detail = wave_detail_strength;
	float crest_mean = ring_water_i0_approx(wave_crest_sharpness);
	float crest_scale = 1.0 / max(0.0001, exp(wave_crest_sharpness) - crest_mean);
	vec4 phases;
	float fifth_phase;
	vec2 first_phase_gradient, second_phase_gradient, third_phase_gradient, fourth_phase_gradient, fifth_phase_gradient;
	ring_water_wave_stack(p, time_seconds, water_kind, anchored, split, phases, fifth_phase,
		first_phase_gradient, second_phase_gradient, third_phase_gradient, fourth_phase_gradient, fifth_phase_gradient);
	float first = ring_water_wave_profile(phases.x, crest_mean, crest_scale);
	float second = ring_water_wave_profile(phases.y, crest_mean, crest_scale);
	float third = ring_water_wave_profile(phases.z, crest_mean, crest_scale);
	float fourth = ring_water_wave_profile(phases.w, crest_mean, crest_scale);
	float fifth = ring_water_wave_profile(fifth_phase, crest_mean, crest_scale);
	float first_filter = ring_water_band_weight(first_phase_gradient, coordinate_dx, coordinate_dy);
	float second_filter = ring_water_band_weight(second_phase_gradient, coordinate_dx, coordinate_dy);
	float third_filter = ring_water_band_weight(third_phase_gradient, coordinate_dx, coordinate_dy);
	float fourth_filter = ring_water_band_weight(fourth_phase_gradient, coordinate_dx, coordinate_dy);
	float fifth_filter = ring_water_band_weight(fifth_phase_gradient, coordinate_dx, coordinate_dy);
	return wave_height_meters * (
		first * 0.42 * first_filter
		+ second * 0.30 * second_filter
		+ third * 0.16 * detail * third_filter
		+ fourth * 0.08 * detail * fourth_filter
		+ fifth * 0.04 * detail * fifth_filter);
}

float ring_water_wave_height_filtered(
	vec2 p,
	float time_seconds,
	vec2 coordinate_dx,
	vec2 coordinate_dy) {
	return ring_water_wave_height_core(p, time_seconds, coordinate_dx, coordinate_dy, 1.0, false, vec2(0.0));
}

float ring_water_wave_height(vec2 p, float time_seconds) {
	return ring_water_wave_height_filtered(p, time_seconds, vec2(0.0), vec2(0.0));
}

vec2 ring_water_wave_gradient_core(
	vec2 p,
	float time_seconds,
	vec2 coordinate_dx,
	vec2 coordinate_dy,
	float water_kind,
	bool anchored,
	vec2 split) {
	float crest_mean = ring_water_i0_approx(wave_crest_sharpness);
	float crest_scale = 1.0 / max(0.0001, exp(wave_crest_sharpness) - crest_mean);
	vec4 phases;
	float fifth_phase;
	vec2 first_phase_gradient, second_phase_gradient, third_phase_gradient, fourth_phase_gradient, fifth_phase_gradient;
	ring_water_wave_stack(p, time_seconds, water_kind, anchored, split, phases, fifth_phase,
		first_phase_gradient, second_phase_gradient, third_phase_gradient, fourth_phase_gradient, fifth_phase_gradient);
	float first_filter = ring_water_band_weight(first_phase_gradient, coordinate_dx, coordinate_dy);
	float second_filter = ring_water_band_weight(second_phase_gradient, coordinate_dx, coordinate_dy);
	float third_filter = ring_water_band_weight(third_phase_gradient, coordinate_dx, coordinate_dy);
	float fourth_filter = ring_water_band_weight(fourth_phase_gradient, coordinate_dx, coordinate_dy);
	float fifth_filter = ring_water_band_weight(fifth_phase_gradient, coordinate_dx, coordinate_dy);
	// These are exact analytic partial derivatives of the same warped,
	// exponentially shaped fBm stack used for displacement. No texture lookup or
	// central-difference resampling is required for the per-pixel normal. Each
	// derivative is then band-limited independently for the current pixel.
	vec2 gradient = ring_water_wave_profile_derivative(phases.x, crest_scale)
		* first_phase_gradient * 0.42 * first_filter;
	gradient += ring_water_wave_profile_derivative(phases.y, crest_scale)
		* second_phase_gradient * 0.30 * second_filter;
	gradient += ring_water_wave_profile_derivative(phases.z, crest_scale)
		* third_phase_gradient * 0.16 * wave_detail_strength * third_filter;
	gradient += ring_water_wave_profile_derivative(phases.w, crest_scale)
		* fourth_phase_gradient * 0.08 * wave_detail_strength * fourth_filter;
	gradient += ring_water_wave_profile_derivative(fifth_phase, crest_scale)
		* fifth_phase_gradient * 0.04 * wave_detail_strength * fifth_filter;
	return gradient * wave_height_meters;
}

vec2 ring_water_wave_gradient_filtered(
	vec2 p,
	float time_seconds,
	vec2 coordinate_dx,
	vec2 coordinate_dy) {
	return ring_water_wave_gradient_core(p, time_seconds, coordinate_dx, coordinate_dy, 1.0, false, vec2(0.0));
}

vec2 ring_water_wave_gradient(vec2 p, float time_seconds) {
	return ring_water_wave_gradient_filtered(p, time_seconds, vec2(0.0), vec2(0.0));
}

// p is ring_water_coordinates (legacy) or ring_water_anchor_coordinates when `anchored` (ring_water_anchor_active()); the derivatives come from a
// local coordinate either way. With the anchor the river advection is in the anchor's river split, not in p.
float ring_water_profile_height_filtered(
	vec2 p,
	float time_seconds,
	vec2 coordinate_dx,
	vec2 coordinate_dy,
	float water_kind,
	bool anchored) {
	vec2 split = ring_water_anchor_split(water_kind);
	if (water_kind < 1.5) {
		return ring_water_wave_height_core(p, time_seconds, coordinate_dx, coordinate_dy, water_kind, anchored, split);
	}
	if (water_kind < 2.5) {
		const float lake_scale = 1.65;
		return ring_water_wave_height_core(
			p * lake_scale,
			water_wind_clock_enabled ? 0.42 : time_seconds * 0.42,
			coordinate_dx * lake_scale,
			coordinate_dy * lake_scale,
			water_kind, anchored, split) * 0.24;
	}
	const float river_scale = 2.15;
	vec2 advected = p * river_scale + vec2(anchored ? 0.0 : (water_wind_clock_enabled ? ring_water_river_advection_value() : time_seconds * wave_speed * 1.8), 0.0);
	return ring_water_wave_height_core(
		advected,
		water_wind_clock_enabled ? 0.18 : time_seconds * 0.18,
		coordinate_dx * river_scale,
		coordinate_dy * river_scale,
		water_kind, anchored, split) * 0.11;
}

float ring_water_profile_height(vec2 p, float time_seconds, float water_kind, bool anchored) {
	return ring_water_profile_height_filtered(
		p,
		time_seconds,
		vec2(0.0),
		vec2(0.0),
		water_kind,
		anchored);
}

vec2 ring_water_profile_gradient_filtered(
	vec2 p,
	float time_seconds,
	vec2 coordinate_dx,
	vec2 coordinate_dy,
	float water_kind,
	bool anchored) {
	vec2 split = ring_water_anchor_split(water_kind);
	if (water_kind < 1.5) {
		return ring_water_wave_gradient_core(p, time_seconds, coordinate_dx, coordinate_dy, water_kind, anchored, split);
	}
	if (water_kind < 2.5) {
		const float lake_scale = 1.65;
		return ring_water_wave_gradient_core(
			p * lake_scale,
			water_wind_clock_enabled ? 0.42 : time_seconds * 0.42,
			coordinate_dx * lake_scale,
			coordinate_dy * lake_scale,
			water_kind, anchored, split) * (0.24 * lake_scale);
	}
	const float river_scale = 2.15;
	vec2 advected = p * river_scale + vec2(anchored ? 0.0 : (water_wind_clock_enabled ? ring_water_river_advection_value() : time_seconds * wave_speed * 1.8), 0.0);
	return ring_water_wave_gradient_core(
		advected,
		water_wind_clock_enabled ? 0.18 : time_seconds * 0.18,
		coordinate_dx * river_scale,
		coordinate_dy * river_scale,
		water_kind, anchored, split) * (0.11 * river_scale);
}

// The opaque global water only needs moving highlights. Two broad analytic
// bands avoid transparent sorting, texture fetches, and high-frequency shimmer.
vec3 ring_water_far_wave_gradient_filtered(
	vec2 p,
	float time_seconds,
	float water_kind,
	vec2 coordinate_dx,
	vec2 coordinate_dy) {
	float kind_frequency = water_kind < 1.5 ? 1.0 : (water_kind < 2.5 ? 1.45 : 1.85);
	float kind_amplitude = water_kind < 1.5 ? 3.6 : (water_kind < 2.5 ? 0.85 : 0.52);
	float base_frequency = 6.28318530718 / max(16.0 * max(water_coordinate_scale, 0.0001), wave_length_meters * 8.0);
	vec2 first_direction = ring_water_wind_direction(vec2(0.916, 0.401));
	vec2 second_direction = ring_water_wind_direction(vec2(-0.349, 0.937));
	float first_frequency = base_frequency * kind_frequency;
	float second_frequency = base_frequency * 0.57 * kind_frequency;
	float circumference = 6.28318530718 * max(ring_radius_meters, 1.0);
	// Quantizing the around-ring component makes the wave itself continuous at
	// atan's +/-pi seam, rather than merely making its variation field tile.
	float first_around_frequency = 6.28318530718
		* max(1.0, floor(abs(first_direction.x * first_frequency) * circumference / 6.28318530718 + 0.5))
		/ circumference * sign(first_direction.x);
	float second_around_frequency = 6.28318530718
		* max(1.0, floor(abs(second_direction.x * second_frequency) * circumference / 6.28318530718 + 0.5))
		/ circumference * sign(second_direction.x);
	vec2 first_phase_gradient = vec2(first_around_frequency, first_direction.y * first_frequency);
	vec2 second_phase_gradient = vec2(second_around_frequency, second_direction.y * second_frequency);
	float first_phase = dot(p, first_phase_gradient) + ring_water_phase(10, 0.23, time_seconds);
	float second_phase = dot(p, second_phase_gradient) + ring_water_phase(11, -0.13, time_seconds) + 1.7;
	// A smooth seam-periodic value field changes both phase and energy over
	// hundreds of metres, so the two broad bands do not stamp a crossing lattice.
	float coordinate_scale = max(water_coordinate_scale, 0.0001);
	// Independent, broad group fields bend each train differently and vary its
	// energy enough to form swell groups. They remain seam-periodic and their
	// physical sizes survive scaled-far rendering.
	vec2 first_group_gradient;
	float first_group = ring_water_patch_value_gradient(
		p + (water_wind_clock_enabled ? ring_water_far_group_translation_value().xy : vec2(time_seconds * 0.47, -time_seconds * 0.23) * coordinate_scale),
		ring_radius_meters, 610.0, first_group_gradient);
	vec2 second_group_gradient;
	float second_group = ring_water_patch_value_gradient(
		p + vec2(193.0, -317.0) * coordinate_scale
			+ (water_wind_clock_enabled ? ring_water_far_group_translation_value().zw : vec2(-time_seconds * 0.29, time_seconds * 0.41) * coordinate_scale),
		ring_radius_meters, 430.0, second_group_gradient);
	first_phase += (first_group - 0.5) * swell_variation * 5.2;
	second_phase += (second_group - 0.5) * swell_variation * 5.8;
	first_phase_gradient += first_group_gradient * swell_variation * 5.2;
	second_phase_gradient += second_group_gradient * swell_variation * 5.8;
	float first_amplitude = wave_height_meters * kind_amplitude
		* mix(1.0, mix(0.22, 1.42, first_group), swell_variation);
	float second_amplitude = wave_height_meters * kind_amplitude
		* mix(1.0, mix(0.30, 1.30, second_group), swell_variation);
	float first_filter = ring_water_band_weight(first_phase_gradient, coordinate_dx, coordinate_dy);
	float second_filter = ring_water_band_weight(second_phase_gradient, coordinate_dx, coordinate_dy);
	vec2 first_gradient = first_phase_gradient * cos(first_phase) * 0.66 * first_amplitude;
	vec2 second_gradient = second_phase_gradient * cos(second_phase) * 0.34 * second_amplitude;
	// The bands omitted below pixel resolution cannot move the normal, but their
	// squared slope remains as microfacet variance for specular roughness.
	// Use each unresolved sinusoid's expected energy, not cos(phase)^2. The
	// latter is itself sub-pixel once a band is retired and would alias through
	// the roughness path.
	float removed_slope_energy = 0.5 * (
		first_amplitude * first_amplitude * 0.66 * 0.66
			* dot(first_phase_gradient, first_phase_gradient) * (1.0 - first_filter * first_filter)
		+ second_amplitude * second_amplitude * 0.34 * 0.34
			* dot(second_phase_gradient, second_phase_gradient) * (1.0 - second_filter * second_filter));
	return vec3(first_gradient * first_filter + second_gradient * second_filter, removed_slope_energy);
}

// Retained for tests and callers that intentionally need the exact unfiltered
// analytic comparison path.
vec2 ring_water_far_wave_gradient(vec2 p, float time_seconds, float water_kind) {
	return ring_water_far_wave_gradient_filtered(
		p, time_seconds, water_kind, vec2(0.0), vec2(0.0)).xy;
}

vec3 ring_water_normal_world_from_gradient(
	vec3 sample_world_position,
	vec3 sample_ring_center,
	vec2 gradient) {
	vec3 radial = sample_world_position - sample_ring_center;
	radial.y = 0.0;
	vec3 surface_up = length(radial) > 0.0001 ? -normalize(radial) : vec3(-1.0, 0.0, 0.0);
	vec3 around_tangent = normalize(vec3(-radial.z, 0.0, radial.x));
	return normalize(surface_up
		- around_tangent * gradient.x * wave_normal_strength
		- vec3(0.0, 1.0, 0.0) * gradient.y * wave_normal_strength);
}

vec3 ring_water_normal_world(
	vec3 sample_world_position,
	vec3 sample_ring_center,
	float radius_meters,
	float time_seconds) {
	vec2 gradient = ring_water_wave_gradient(
		ring_water_coordinates(sample_world_position, sample_ring_center, radius_meters),
		time_seconds);
	return ring_water_normal_world_from_gradient(sample_world_position, sample_ring_center, gradient);
}
