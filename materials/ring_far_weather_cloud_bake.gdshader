shader_type canvas_item;
render_mode unshaded, blend_disabled;
// Bakes the far cloud shell's cloud amount over the whole ring (SCREEN_UV.x
// around, SCREEN_UV.y across; the bake draws in vertical strips over frames) for the far weather cloud shadow (ring_far_weather_shadow.gdshaderinc).
// RingScaledFarViewport binds the same weather atlas, wind clock and Sunshine
// density resources as the visible shell, so the baked amount is the shell's
// own field at its mid-band slice. R = cloud amount 0..1.
uniform float ring_circumference_km = 31415.9;
uniform float ring_width_km = 318.0;
uniform float coverage : hint_range(0.0, 1.0) = 0.46;
uniform float softness : hint_range(0.01, 0.4) = 0.20;
uniform sampler2D weather_atlas : hint_default_white, filter_linear, repeat_enable;
uniform bool weather_atlas_enabled = false;
uniform float weather_time_seconds = 0.0;
uniform vec2 weather_wind_uv_per_second = vec2(0.0);
#include "res://materials/ring_far_cloud_density.gdshaderinc"
#include "res://materials/ring_cloud_weather_shared.gdshaderinc"

// Band-limited far_density_core for a ~2 km texel. The medium and small
// Sunshine noises repeat every few km (medium/small_scale), so point samples
// at the bake spacing alias into a regular, grid-aligned pattern. Here they are
// integrated out instead: the large and extra-large shapes (several km
// features) stay exact, and the medium/small terms are replaced by the
// expectation of far_density_core's shaping over a uniformly distributed
// medium value m (shape = clamp((m - (1 - L)) / L) has mean L / 2; the small
// erosion has mean 0.5 * gradient.g / (detail_power + 1)).
float far_density_core_lowpass(vec3 meters, float coverage_threshold) {
	float band = max(far_density_background_altitude.y - far_density_background_altitude.x, 1.0);
	float h = clamp((meters.y - far_density_background_altitude.x) / band, 0.0, 1.0);
	vec4 gradient = texture(far_density_height_gradient, vec2(h, 0.5));
	vec2 extra_uv = (meters.xz - far_density_extra_large_position.xz) / max(far_density_extra_large_scale, 1.0);
	float extra = textureLod(far_density_extra_large_noise_patterns, extra_uv, 0.0).a;
	float swept_height = 1.0 - clamp(h / max(far_density_wind_swept_range, 0.0001), 0.0, 1.0);
	vec3 swept = meters + vec3(far_density_wind_direction.x, 0.0, far_density_wind_direction.z)
		* far_density_wind_swept_strength * swept_height * swept_height;
	float large = textureLod(far_density_large_scale_noise, (swept - far_density_large_position) / max(far_density_large_scale, 1.0), 0.0).r * extra * gradient.b;
	float large_shape = smoothstep(1.0 - coverage_threshold, 1.1 - coverage_threshold, large * gradient.r);
	float shape = 0.5 * large_shape;
	float small_shape = 0.5 * gradient.g / (max(far_density_clouds_detail_power, 0.0) + 1.0);
	shape = clamp((shape - small_shape) / max(1.0 - small_shape, 0.0001), 0.0, 1.0);
	float edge_fade = smoothstep(0.0, 0.1, h) * (1.0 - smoothstep(0.9, 1.0, h));
	return shape * edge_fade * far_density_clouds_density;
}

// far_density_sample_coverage with the band-limited core (same ring-seam feather).
float far_density_sample_coverage_lowpass(vec3 meters, float coverage_threshold) {
	if (!far_density_enabled) return -1.0;
	float circumference = max(far_density_ring_circumference_meters, 1.0);
	float x = mod(meters.x, circumference); if (x < 0.0) x += circumference;
	meters.x = x;
	const float seam_feather = 8000.0;
	if (x < seam_feather)
		return mix(far_density_core_lowpass(vec3(x + circumference, meters.yz), coverage_threshold), far_density_core_lowpass(meters, coverage_threshold), 0.5 + 0.5 * smoothstep(0.0, seam_feather, x));
	if (x > circumference - seam_feather)
		return mix(far_density_core_lowpass(meters, coverage_threshold), far_density_core_lowpass(vec3(x - circumference, meters.yz), coverage_threshold), 0.5 * smoothstep(circumference - seam_feather, circumference, x));
	return far_density_core_lowpass(meters, coverage_threshold);
}
#define RING_FAR_WEATHER_DENSITY_SAMPLE far_density_sample_coverage_lowpass
#define RING_FAR_WEATHER_STORM_FLOOR 0.0
#include "res://materials/ring_far_weather_cloud.gdshaderinc"

void fragment() {
	if (!weather_atlas_enabled) {
		COLOR = vec4(0.0, 0.0, 0.0, 1.0);
	} else {
		// 2x2 box supersample: the texel stores the mean cloud amount over its
		// footprint, so bilinear receivers see soft cloud edges, not texel steps.
		float cloud = 0.0;
		for (int i = 0; i < 4; i++) {
			vec2 uv = SCREEN_UV + (vec2(float(i & 1), float(i >> 1)) - 0.5) * 0.5 * SCREEN_PIXEL_SIZE;
			vec2 weather_uv = vec2(
				fract(uv.x - weather_wind_uv_per_second.x * weather_time_seconds),
				clamp(uv.y - weather_wind_uv_per_second.y * weather_time_seconds, 0.0, 1.0));
			vec4 weather = textureLod(weather_atlas, weather_uv, 0.0);
			vec3 canonical_meters = vec3(fract(uv.x) * ring_circumference_km * 1000.0, 0.0, uv.y * ring_width_km * 1000.0);
			cloud += ring_far_weather_cloud_amount(weather, canonical_meters, coverage, softness);
		}
		COLOR = vec4(clamp(cloud * 0.25, 0.0, 1.0), 0.0, 0.0, 1.0);
	}
}
