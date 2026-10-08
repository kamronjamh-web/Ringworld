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

float ring_water_phase(int index, float coefficient, float time_seconds) {
    if (!water_wind_clock_enabled) return time_seconds * wave_speed * coefficient;
    vec3 phases = water_wave_phases[index];
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
vec2 ring_water_wave_direction(vec2 direction, float frequency, float kind_clock_scale) {
    vec2 rotated = ring_water_wind_direction(direction);
    if (!water_wind_clock_enabled) return rotated;
    float circumference = 6.28318530718 * max(ring_radius_meters, 1.0);
    float kind_coordinate_scale = kind_clock_scale > 0.8 ? 1.0 : (kind_clock_scale > 0.3 ? 1.65 : 2.15);
    circumference *= kind_coordinate_scale;
    rotated.x = floor(rotated.x * frequency * circumference / 6.28318530718 + 0.5)
        * 6.28318530718 / circumference / max(frequency, 0.000001);
    return rotated;
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

float ring_water_profile_shore_foam_scale(float water_kind) {
	return water_kind < 1.5 ? 1.0 : (water_kind < 2.5 ? 0.30 : 0.46);
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

float ring_water_wave_height_filtered(
	vec2 p,
	float time_seconds,
	vec2 coordinate_dx,
	vec2 coordinate_dy) {
	float base_frequency = 6.28318530718 / max(2.0, wave_length_meters);
	float detail = wave_detail_strength;
	float crest_mean = ring_water_i0_approx(wave_crest_sharpness);
	float crest_scale = 1.0 / max(0.0001, exp(wave_crest_sharpness) - crest_mean);
	vec2 first_direction = ring_water_wave_direction(vec2(0.916, 0.401), base_frequency * 1.0, time_seconds);
	vec2 second_direction = ring_water_wave_direction(vec2(-0.349, 0.937), base_frequency * 1.431, time_seconds);
	vec2 third_direction = ring_water_wave_direction(vec2(0.625, -0.781), base_frequency * 2.173, time_seconds);
	vec2 fourth_direction = ring_water_wave_direction(vec2(-0.829, -0.559), base_frequency * 3.117, time_seconds);
	vec2 fifth_direction = ring_water_wave_direction(vec2(0.187, 0.982), base_frequency * 5.037, time_seconds);
	vec2 first_warp_direction = ring_water_wave_direction(vec2(-0.511, 0.860), base_frequency * 0.173, time_seconds);
	vec2 second_warp_direction = ring_water_wave_direction(vec2(0.766, 0.643), base_frequency * 0.227, time_seconds);
	vec2 third_warp_direction = ring_water_wave_direction(vec2(-0.935, -0.355), base_frequency * 0.319, time_seconds);
	vec2 fourth_warp_direction = ring_water_wave_direction(vec2(0.252, -0.968), base_frequency * 0.271, time_seconds);
	vec2 fifth_warp_direction = ring_water_wave_direction(vec2(0.982, -0.188), base_frequency * 0.413, time_seconds);
	float first_warp_argument = dot(p, first_warp_direction) * base_frequency * 0.173 + ring_water_phase(5, 0.071, time_seconds);
	float second_warp_argument = dot(p, second_warp_direction) * base_frequency * 0.227 + ring_water_phase(6, -0.053, time_seconds) + 1.7;
	float third_warp_argument = dot(p, third_warp_direction) * base_frequency * 0.319 + ring_water_phase(7, 0.041, time_seconds) + 3.1;
	float fourth_warp_argument = dot(p, fourth_warp_direction) * base_frequency * 0.271 + ring_water_phase(8, -0.063, time_seconds) + 4.6;
	float fifth_warp_argument = dot(p, fifth_warp_direction) * base_frequency * 0.413 + ring_water_phase(9, 0.037, time_seconds) + 0.8;
	float first_warp = sin(first_warp_argument);
	float second_warp = sin(second_warp_argument);
	float third_warp = sin(third_warp_argument);
	float fourth_warp = sin(fourth_warp_argument);
	float fifth_warp = sin(fifth_warp_argument);
	float first_phase = dot(p, first_direction) * base_frequency
		+ first_warp * wave_domain_warp_strength + ring_water_phase(0, 1.0, time_seconds);
	float second_phase = dot(p, second_direction) * base_frequency * 1.431
		+ second_warp * wave_domain_warp_strength * 0.87 + ring_water_phase(1, -1.173, time_seconds) + 1.1;
	float third_phase = dot(p, third_direction) * base_frequency * 2.173
		+ third_warp * wave_domain_warp_strength * 0.74 + ring_water_phase(2, 1.619, time_seconds) + 2.4;
	float fourth_phase = dot(p, fourth_direction) * base_frequency * 3.117
		+ fourth_warp * wave_domain_warp_strength * 0.63 + ring_water_phase(3, -2.071, time_seconds) + 3.8;
	float fifth_phase = dot(p, fifth_direction) * base_frequency * 5.037
		+ fifth_warp * wave_domain_warp_strength * 0.51 + ring_water_phase(4, 2.513, time_seconds) + 5.2;
	float first = ring_water_wave_profile(
		first_phase,
		crest_mean,
		crest_scale);
	float second = ring_water_wave_profile(
		second_phase,
		crest_mean,
		crest_scale);
	float third = ring_water_wave_profile(
		third_phase,
		crest_mean,
		crest_scale);
	float fourth = ring_water_wave_profile(
		fourth_phase,
		crest_mean,
		crest_scale);
	float fifth = ring_water_wave_profile(
		fifth_phase,
		crest_mean,
		crest_scale);
	vec2 first_phase_gradient = first_direction * base_frequency
		+ first_warp_direction * base_frequency * 0.173 * cos(first_warp_argument) * wave_domain_warp_strength;
	vec2 second_phase_gradient = second_direction * base_frequency * 1.431
		+ second_warp_direction * base_frequency * 0.227 * cos(second_warp_argument) * wave_domain_warp_strength * 0.87;
	vec2 third_phase_gradient = third_direction * base_frequency * 2.173
		+ third_warp_direction * base_frequency * 0.319 * cos(third_warp_argument) * wave_domain_warp_strength * 0.74;
	vec2 fourth_phase_gradient = fourth_direction * base_frequency * 3.117
		+ fourth_warp_direction * base_frequency * 0.271 * cos(fourth_warp_argument) * wave_domain_warp_strength * 0.63;
	vec2 fifth_phase_gradient = fifth_direction * base_frequency * 5.037
		+ fifth_warp_direction * base_frequency * 0.413 * cos(fifth_warp_argument) * wave_domain_warp_strength * 0.51;
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

float ring_water_wave_height(vec2 p, float time_seconds) {
	return ring_water_wave_height_filtered(p, time_seconds, vec2(0.0), vec2(0.0));
}

vec2 ring_water_wave_gradient_filtered(
	vec2 p,
	float time_seconds,
	vec2 coordinate_dx,
	vec2 coordinate_dy) {
	float base_frequency = 6.28318530718 / max(2.0, wave_length_meters);
	float crest_mean = ring_water_i0_approx(wave_crest_sharpness);
	float crest_scale = 1.0 / max(0.0001, exp(wave_crest_sharpness) - crest_mean);
	vec2 first_direction = ring_water_wave_direction(vec2(0.916, 0.401), base_frequency * 1.0, time_seconds);
	vec2 second_direction = ring_water_wave_direction(vec2(-0.349, 0.937), base_frequency * 1.431, time_seconds);
	vec2 third_direction = ring_water_wave_direction(vec2(0.625, -0.781), base_frequency * 2.173, time_seconds);
	vec2 fourth_direction = ring_water_wave_direction(vec2(-0.829, -0.559), base_frequency * 3.117, time_seconds);
	vec2 fifth_direction = ring_water_wave_direction(vec2(0.187, 0.982), base_frequency * 5.037, time_seconds);
	vec2 first_warp_direction = ring_water_wave_direction(vec2(-0.511, 0.860), base_frequency * 0.173, time_seconds);
	vec2 second_warp_direction = ring_water_wave_direction(vec2(0.766, 0.643), base_frequency * 0.227, time_seconds);
	vec2 third_warp_direction = ring_water_wave_direction(vec2(-0.935, -0.355), base_frequency * 0.319, time_seconds);
	vec2 fourth_warp_direction = ring_water_wave_direction(vec2(0.252, -0.968), base_frequency * 0.271, time_seconds);
	vec2 fifth_warp_direction = ring_water_wave_direction(vec2(0.982, -0.188), base_frequency * 0.413, time_seconds);

	float first_warp_argument = dot(p, first_warp_direction) * base_frequency * 0.173 + ring_water_phase(5, 0.071, time_seconds);
	float second_warp_argument = dot(p, second_warp_direction) * base_frequency * 0.227 + ring_water_phase(6, -0.053, time_seconds) + 1.7;
	float third_warp_argument = dot(p, third_warp_direction) * base_frequency * 0.319 + ring_water_phase(7, 0.041, time_seconds) + 3.1;
	float fourth_warp_argument = dot(p, fourth_warp_direction) * base_frequency * 0.271 + ring_water_phase(8, -0.063, time_seconds) + 4.6;
	float fifth_warp_argument = dot(p, fifth_warp_direction) * base_frequency * 0.413 + ring_water_phase(9, 0.037, time_seconds) + 0.8;
	float first_phase = dot(p, first_direction) * base_frequency
		+ sin(first_warp_argument) * wave_domain_warp_strength + ring_water_phase(0, 1.0, time_seconds);
	float second_phase = dot(p, second_direction) * base_frequency * 1.431
		+ sin(second_warp_argument) * wave_domain_warp_strength * 0.87 + ring_water_phase(1, -1.173, time_seconds) + 1.1;
	float third_phase = dot(p, third_direction) * base_frequency * 2.173
		+ sin(third_warp_argument) * wave_domain_warp_strength * 0.74 + ring_water_phase(2, 1.619, time_seconds) + 2.4;
	float fourth_phase = dot(p, fourth_direction) * base_frequency * 3.117
		+ sin(fourth_warp_argument) * wave_domain_warp_strength * 0.63 + ring_water_phase(3, -2.071, time_seconds) + 3.8;
	float fifth_phase = dot(p, fifth_direction) * base_frequency * 5.037
		+ sin(fifth_warp_argument) * wave_domain_warp_strength * 0.51 + ring_water_phase(4, 2.513, time_seconds) + 5.2;

	vec2 first_phase_gradient = first_direction * base_frequency
		+ first_warp_direction * base_frequency * 0.173 * cos(first_warp_argument) * wave_domain_warp_strength;
	vec2 second_phase_gradient = second_direction * base_frequency * 1.431
		+ second_warp_direction * base_frequency * 0.227 * cos(second_warp_argument) * wave_domain_warp_strength * 0.87;
	vec2 third_phase_gradient = third_direction * base_frequency * 2.173
		+ third_warp_direction * base_frequency * 0.319 * cos(third_warp_argument) * wave_domain_warp_strength * 0.74;
	vec2 fourth_phase_gradient = fourth_direction * base_frequency * 3.117
		+ fourth_warp_direction * base_frequency * 0.271 * cos(fourth_warp_argument) * wave_domain_warp_strength * 0.63;
	vec2 fifth_phase_gradient = fifth_direction * base_frequency * 5.037
		+ fifth_warp_direction * base_frequency * 0.413 * cos(fifth_warp_argument) * wave_domain_warp_strength * 0.51;
	float first_filter = ring_water_band_weight(first_phase_gradient, coordinate_dx, coordinate_dy);
	float second_filter = ring_water_band_weight(second_phase_gradient, coordinate_dx, coordinate_dy);
	float third_filter = ring_water_band_weight(third_phase_gradient, coordinate_dx, coordinate_dy);
	float fourth_filter = ring_water_band_weight(fourth_phase_gradient, coordinate_dx, coordinate_dy);
	float fifth_filter = ring_water_band_weight(fifth_phase_gradient, coordinate_dx, coordinate_dy);
	// These are exact analytic partial derivatives of the same warped,
	// exponentially shaped fBm stack used for displacement. No texture lookup or
	// central-difference resampling is required for the per-pixel normal. Each
	// derivative is then band-limited independently for the current pixel.
	vec2 gradient = ring_water_wave_profile_derivative(first_phase, crest_scale)
		* first_phase_gradient * 0.42 * first_filter;
	gradient += ring_water_wave_profile_derivative(second_phase, crest_scale)
		* second_phase_gradient * 0.30 * second_filter;
	gradient += ring_water_wave_profile_derivative(third_phase, crest_scale)
		* third_phase_gradient * 0.16 * wave_detail_strength * third_filter;
	gradient += ring_water_wave_profile_derivative(fourth_phase, crest_scale)
		* fourth_phase_gradient * 0.08 * wave_detail_strength * fourth_filter;
	gradient += ring_water_wave_profile_derivative(fifth_phase, crest_scale)
		* fifth_phase_gradient * 0.04 * wave_detail_strength * fifth_filter;
	return gradient * wave_height_meters;
}

vec2 ring_water_wave_gradient(vec2 p, float time_seconds) {
	return ring_water_wave_gradient_filtered(p, time_seconds, vec2(0.0), vec2(0.0));
}

float ring_water_profile_height_filtered(
	vec2 p,
	float time_seconds,
	vec2 coordinate_dx,
	vec2 coordinate_dy,
	float water_kind) {
	if (water_kind < 1.5) {
		return ring_water_wave_height_filtered(p, time_seconds, coordinate_dx, coordinate_dy);
	}
	if (water_kind < 2.5) {
		const float lake_scale = 1.65;
		return ring_water_wave_height_filtered(
			p * lake_scale,
			water_wind_clock_enabled ? 0.42 : time_seconds * 0.42,
			coordinate_dx * lake_scale,
			coordinate_dy * lake_scale) * 0.24;
	}
	const float river_scale = 2.15;
	vec2 advected = p * river_scale + vec2(water_wind_clock_enabled ? water_river_advection : time_seconds * wave_speed * 1.8, 0.0);
	return ring_water_wave_height_filtered(
		advected,
		water_wind_clock_enabled ? 0.18 : time_seconds * 0.18,
		coordinate_dx * river_scale,
		coordinate_dy * river_scale) * 0.11;
}

float ring_water_profile_height(vec2 p, float time_seconds, float water_kind) {
	return ring_water_profile_height_filtered(
		p,
		time_seconds,
		vec2(0.0),
		vec2(0.0),
		water_kind);
}

vec2 ring_water_profile_gradient_filtered(
	vec2 p,
	float time_seconds,
	vec2 coordinate_dx,
	vec2 coordinate_dy,
	float water_kind) {
	if (water_kind < 1.5) {
		return ring_water_wave_gradient_filtered(p, time_seconds, coordinate_dx, coordinate_dy);
	}
	if (water_kind < 2.5) {
		const float lake_scale = 1.65;
		return ring_water_wave_gradient_filtered(
			p * lake_scale,
			water_wind_clock_enabled ? 0.42 : time_seconds * 0.42,
			coordinate_dx * lake_scale,
			coordinate_dy * lake_scale) * (0.24 * lake_scale);
	}
	const float river_scale = 2.15;
	vec2 advected = p * river_scale + vec2(water_wind_clock_enabled ? water_river_advection : time_seconds * wave_speed * 1.8, 0.0);
	return ring_water_wave_gradient_filtered(
		advected,
		water_wind_clock_enabled ? 0.18 : time_seconds * 0.18,
		coordinate_dx * river_scale,
		coordinate_dy * river_scale) * (0.11 * river_scale);
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
		p + (water_wind_clock_enabled ? water_far_group_translation.xy : vec2(time_seconds * 0.47, -time_seconds * 0.23) * coordinate_scale),
		ring_radius_meters, 610.0, first_group_gradient);
	vec2 second_group_gradient;
	float second_group = ring_water_patch_value_gradient(
		p + vec2(193.0, -317.0) * coordinate_scale
			+ (water_wind_clock_enabled ? water_far_group_translation.zw : vec2(-time_seconds * 0.29, time_seconds * 0.41) * coordinate_scale),
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
