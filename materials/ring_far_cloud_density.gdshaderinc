// Lightweight counterpart to Sunshine's BACKGROUND sampleScene path.  It uses
// the same imported resources and driver-owned origins, but deliberately does
// no curl, effectors, light march, or ray march.
uniform bool far_density_enabled = false;
uniform float far_density_strength = 1.0;
uniform float far_density_opacity_multiplier = 1.0;
uniform sampler2D far_density_extra_large_noise_patterns : filter_linear_mipmap, repeat_enable;
uniform sampler2D far_density_height_gradient : filter_linear_mipmap;
uniform sampler3D far_density_large_scale_noise : filter_linear_mipmap, repeat_enable;
uniform sampler3D far_density_medium_scale_noise : filter_linear_mipmap, repeat_enable;
uniform sampler3D far_density_small_scale_noise : filter_linear_mipmap, repeat_enable;
uniform vec3 far_density_extra_large_position = vec3(0.0);
uniform vec3 far_density_large_position = vec3(0.0);
uniform vec3 far_density_medium_position = vec3(0.0);
uniform vec3 far_density_detail_position = vec3(0.0);
uniform float far_density_extra_large_scale = 320000.0;
uniform float far_density_large_scale = 120000.0;
uniform float far_density_medium_scale = 20000.0;
uniform float far_density_small_scale = 8500.0;
uniform float far_density_clouds_coverage = 0.82;
uniform float far_density_coverage_bias = 0.0;
uniform float far_density_clouds_density = 1.0;
uniform float far_density_clouds_detail_power = 1.0;
uniform float far_density_wind_swept_range = 0.0;
uniform float far_density_wind_swept_strength = 0.0;
uniform vec2 far_density_background_altitude = vec2(2000.0, 6000.0);
uniform vec3 far_density_wind_direction = vec3(0.0);
uniform float far_density_ring_circumference_meters = 31415926.0;
uniform vec4 cloud_weather_params = vec4(0.0);
uniform vec4 cloud_weather_frame = vec4(0.0);

float far_density_remap(float value, float low, float high) {
	return clamp((value - low) / max(high - low, 0.0001), 0.0, 1.0);
}

float far_density_core(vec3 meters, float coverage_threshold) {
	float band = max(far_density_background_altitude.y - far_density_background_altitude.x, 1.0);
	float h = clamp((meters.y - far_density_background_altitude.x) / band, 0.0, 1.0);
	vec4 gradient = texture(far_density_height_gradient, vec2(h, 0.5));
	vec2 extra_uv = (meters.xz - far_density_extra_large_position.xz) / max(far_density_extra_large_scale, 1.0);
	float extra = texture(far_density_extra_large_noise_patterns, extra_uv).a;
	float swept_height = 1.0 - clamp(h / max(far_density_wind_swept_range, 0.0001), 0.0, 1.0);
	vec3 swept = meters + vec3(far_density_wind_direction.x, 0.0, far_density_wind_direction.z)
		* far_density_wind_swept_strength * swept_height * swept_height;
	float large = texture(far_density_large_scale_noise, (swept - far_density_large_position) / max(far_density_large_scale, 1.0)).r * extra * gradient.b;
	float large_shape = smoothstep(1.0 - coverage_threshold, 1.1 - coverage_threshold, large * gradient.r);
	float medium_shape = 1.0 - texture(far_density_medium_scale_noise, (swept - far_density_medium_position) / max(far_density_medium_scale, 1.0)).b;
	float shape = clamp((medium_shape - (1.0 - large_shape)) / max(large_shape, 0.0001), 0.0, 1.0);
	float small_shape = texture(far_density_small_scale_noise, (meters - far_density_detail_position) / max(far_density_small_scale, 1.0)).r
		* gradient.g * pow(max(1.0 - medium_shape, 0.0), max(far_density_clouds_detail_power, 0.0));
	shape = clamp((shape - small_shape) / max(1.0 - small_shape, 0.0001), 0.0, 1.0);
	float edge_fade = smoothstep(0.0, 0.1, h) * (1.0 - smoothstep(0.9, 1.0, h));
	return shape * edge_fade * far_density_clouds_density;
}

float far_density_sample_coverage(vec3 meters, float coverage_threshold) {
	if (!far_density_enabled) return -1.0;
	float circumference = max(far_density_ring_circumference_meters, 1.0);
	float x = mod(meters.x, circumference); if (x < 0.0) x += circumference;
	meters.x = x;
	const float seam_feather = 8000.0;
	if (x < seam_feather)
		return mix(far_density_core(vec3(x + circumference, meters.yz), coverage_threshold), far_density_core(meters, coverage_threshold), 0.5 + 0.5 * smoothstep(0.0, seam_feather, x));
	if (x > circumference - seam_feather)
		return mix(far_density_core(meters, coverage_threshold), far_density_core(vec3(x - circumference, meters.yz), coverage_threshold), 0.5 * smoothstep(circumference - seam_feather, circumference, x));
	return far_density_core(meters, coverage_threshold);
}

float far_density_sample(vec3 meters) { return far_density_sample_coverage(meters, far_density_clouds_coverage); }
