shader_type canvas_item;
render_mode unshaded;
#include "res://materials/celestial_lighting.gdshaderinc"

// Low-resolution background-only integration of the camera-local cloud field.
// This intentionally mirrors ring_local_volumetric_cloud.gdshader, but only
// calculates the transmittance between the camera and the distant backdrop.
uniform vec3 camera_canonical_km = vec3(0.0);
uniform vec3 camera_right_canonical = vec3(1.0, 0.0, 0.0);
uniform vec3 camera_up_canonical = vec3(0.0, 1.0, 0.0);
uniform vec3 camera_forward_canonical = vec3(0.0, 0.0, -1.0);
uniform float camera_tan_half_fov = 0.5773503;
uniform float viewport_aspect = 1.7777778;

uniform vec3 volume_center_canonical_km = vec3(0.0);
uniform vec3 volume_size_meters = vec3(24000.0, 15900.0, 24000.0);
uniform float ring_circumference_km = 31415.9;
uniform float ring_width_km = 318.0;
uniform float cloud_seed = 1337.0;
uniform vec2 wind_km_per_second = vec2(0.0022, 0.0007);
uniform float weather_time_seconds = 0.0;
uniform float coverage : hint_range(0.0, 1.0) = 0.52;
uniform float precipitation : hint_range(0.0, 1.0) = 0.0;
uniform float storm_influence : hint_range(0.0, 1.0) = 0.0;
uniform int formation_profile : hint_range(0, 3) = 0;
uniform float cloud_base_fraction : hint_range(0.0, 1.0) = 0.08;
uniform float cloud_top_fraction : hint_range(0.0, 1.0) = 0.32;
uniform float density_scale : hint_range(0.001, 0.15) = 0.048;
uniform float softness : hint_range(0.01, 0.3) = 0.075;
uniform float visibility : hint_range(0.0, 1.0) = 1.0;
uniform float handoff_start_meters = 9000.0;
uniform float handoff_end_meters = 12000.0;
uniform int quality_level : hint_range(1, 3) = 1;
uniform int raymarch_steps : hint_range(4, 24) = 12;
uniform float extinction_per_kilometer : hint_range(0.01, 4.0) = 0.72;
uniform float opacity_scale : hint_range(0.0, 2.0) = 1.0;
uniform vec3 sun_direction_canonical = vec3(0.0, 1.0, 0.0);
uniform vec3 sunlit_color : source_color = vec3(0.91, 0.95, 1.0);
uniform vec3 shadow_color : source_color = vec3(0.24, 0.31, 0.42);
uniform vec3 storm_color : source_color = vec3(0.12, 0.16, 0.23);
uniform vec3 night_color : source_color = vec3(0.035, 0.050, 0.085);

float hash31(vec3 p) {
	p = fract(p * 0.1031);
	p += dot(p, p.yzx + 33.33 + cloud_seed * 0.00017);
	return fract((p.x + p.y) * p.z);
}

float value_noise3(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float n000 = hash31(i);
	float n100 = hash31(i + vec3(1.0, 0.0, 0.0));
	float n010 = hash31(i + vec3(0.0, 1.0, 0.0));
	float n110 = hash31(i + vec3(1.0, 1.0, 0.0));
	float n001 = hash31(i + vec3(0.0, 0.0, 1.0));
	float n101 = hash31(i + vec3(1.0, 0.0, 1.0));
	float n011 = hash31(i + vec3(0.0, 1.0, 1.0));
	float n111 = hash31(i + vec3(1.0));
	return mix(
		mix(mix(n000, n100, f.x), mix(n010, n110, f.x), f.y),
		mix(mix(n001, n101, f.x), mix(n011, n111, f.x), f.y),
		f.z);
}

vec3 periodic_ring_position(vec3 canonical_km) {
	float circumference = max(ring_circumference_km, 0.001);
	float angle = canonical_km.x / circumference * 6.28318530718;
	float radius = circumference / 6.28318530718;
	return vec3(cos(angle) * radius, sin(angle) * radius, canonical_km.z);
}

float shortest_ring_delta_km(float value, float reference) {
	float circumference = max(ring_circumference_km, 0.001);
	return mod(value - reference + circumference * 0.5, circumference) - circumference * 0.5;
}

bool intersect_volume_box(vec3 origin_meters, vec3 direction, out float near_t, out float far_t) {
	vec3 safe_direction = direction;
	safe_direction.x = abs(safe_direction.x) < 0.00001 ? (safe_direction.x < 0.0 ? -0.00001 : 0.00001) : safe_direction.x;
	safe_direction.y = abs(safe_direction.y) < 0.00001 ? (safe_direction.y < 0.0 ? -0.00001 : 0.00001) : safe_direction.y;
	safe_direction.z = abs(safe_direction.z) < 0.00001 ? (safe_direction.z < 0.0 ? -0.00001 : 0.00001) : safe_direction.z;
	vec3 half_size = max(volume_size_meters * 0.5, vec3(1.0));
	vec3 a = (-half_size - origin_meters) / safe_direction;
	vec3 b = (half_size - origin_meters) / safe_direction;
	vec3 minimum_t = min(a, b);
	vec3 maximum_t = max(a, b);
	near_t = max(max(minimum_t.x, minimum_t.y), minimum_t.z);
	far_t = min(min(maximum_t.x, maximum_t.y), maximum_t.z);
	return far_t > max(near_t, 0.0);
}

vec4 sample_cloud(vec3 canonical_km, vec3 local_meters) {
	vec3 advected_km = canonical_km;
	advected_km.x -= wind_km_per_second.x * weather_time_seconds;
	advected_km.z -= wind_km_per_second.y * weather_time_seconds;
	vec3 ring_position = periodic_ring_position(advected_km);
	float height = clamp(local_meters.y / max(volume_size_meters.y, 1.0) + 0.5, 0.0, 1.0);
	float cloud_height = clamp(
		(height - cloud_base_fraction) / max(0.015, cloud_top_fraction - cloud_base_fraction),
		0.0,
		1.0);

	vec3 broad_position = ring_position * 0.075
		+ vec3(advected_km.y * 0.11, -advected_km.y * 0.07, advected_km.y * 0.09);
	float broad = value_noise3(broad_position);
	float shape = value_noise3(
		ring_position * 0.22
		+ vec3(advected_km.y * 0.37, advected_km.y * 0.21, -advected_km.y * 0.29)
		+ vec3(17.3, 4.1, 29.7));
	float field = broad * 0.62 + shape * 0.38;
	if (quality_level >= 2) {
		float erosion = value_noise3(
			ring_position * 0.63
			+ vec3(-advected_km.y * 0.83, advected_km.y * 0.71, advected_km.y * 0.58)
			+ vec3(41.7, 13.2, 7.6));
		field = field * 0.88 + erosion * 0.12;
	}
	if (quality_level >= 3) {
		float fine = value_noise3(
			ring_position * 1.45
			+ vec3(advected_km.y * 1.9, -advected_km.y * 1.4, advected_km.y * 1.6)
			+ vec3(9.2, 55.4, 21.8));
		field = field * 0.94 + fine * 0.06;
	}

	float bottom = smoothstep(0.0, mix(0.08, 0.035, storm_influence), cloud_height);
	float top = 1.0 - smoothstep(mix(0.66, 0.80, storm_influence), 1.0, cloud_height);
	float vertical_profile = bottom * top;
	if (formation_profile == 1) {
		vertical_profile *= 1.0 - smoothstep(0.38, 0.88, cloud_height);
	} else if (formation_profile >= 2) {
		vertical_profile *= mix(0.72, 1.0, smoothstep(0.12, 0.58, cloud_height));
	}
	if (formation_profile == 3) {
		float anvil = smoothstep(0.58, 0.78, cloud_height)
			* (1.0 - smoothstep(0.88, 1.0, cloud_height));
		field += anvil * 0.13;
	}

	float threshold = mix(0.70, 0.43, clamp(coverage, 0.0, 1.0));
	threshold -= storm_influence * 0.055;
	float cloud = smoothstep(threshold - softness, threshold + softness, field)
		* vertical_profile;
	float horizontal_distance = length(local_meters.xz);
	float handoff = 1.0 - smoothstep(
		max(0.0, handoff_start_meters),
		max(handoff_start_meters + 1.0, handoff_end_meters),
		horizontal_distance);
	float across_mask = smoothstep(0.0, 0.35, canonical_km.z)
		* (1.0 - smoothstep(max(0.0, ring_width_km - 0.35), ring_width_km, canonical_km.z));
	cloud *= handoff * across_mask * visibility;

	float day = ringworld_effective_solar_day(smoothstep(-0.2, 0.35, normalize(sun_direction_canonical).y));
	float lower_darkening = (1.0 - smoothstep(0.05, 0.62, cloud_height))
		* max(storm_influence, precipitation);
	vec3 weather_shadow = mix(shadow_color, storm_color, clamp(lower_darkening * 0.92, 0.0, 1.0));
	vec3 daylight = mix(weather_shadow, sunlit_color, clamp(cloud_height * 0.72 + field * 0.35, 0.0, 1.0));
	vec3 color = mix(night_color, daylight, day);
	return vec4(color, cloud * mix(0.82, 1.28, storm_influence));
}

void fragment() {
	vec2 screen = vec2(UV.x * 2.0 - 1.0, (1.0 - UV.y) * 2.0 - 1.0);
	vec3 ray_direction = normalize(
		camera_forward_canonical
		+ camera_right_canonical * screen.x * camera_tan_half_fov * viewport_aspect
		+ camera_up_canonical * screen.y * camera_tan_half_fov);

	vec3 camera_relative_meters = (camera_canonical_km - volume_center_canonical_km) * 1000.0;
	camera_relative_meters.x = shortest_ring_delta_km(
		camera_canonical_km.x,
		volume_center_canonical_km.x) * 1000.0;
	float near_t;
	float far_t;
	if (!intersect_volume_box(camera_relative_meters, ray_direction, near_t, far_t)) {
		COLOR = vec4(0.0);
	} else {
		near_t = max(near_t, 0.0);
		int steps = clamp(raymarch_steps, 4, 24);
		float step_meters = max(1.0, (far_t - near_t) / float(steps));
		vec3 accumulated_color = vec3(0.0);
		float accumulated_alpha = 0.0;
		for (int index = 0; index < 24; index++) {
			if (index >= steps || accumulated_alpha > 0.992) {
				break;
			}
			float distance_meters = near_t + (float(index) + 0.5) * step_meters;
			vec3 local_meters = camera_relative_meters + ray_direction * distance_meters;
			vec3 canonical_km = volume_center_canonical_km + local_meters * 0.001;
			canonical_km.x = mod(canonical_km.x, max(ring_circumference_km, 0.001));
			vec4 sample_value = sample_cloud(canonical_km, local_meters);
			float optical_depth = sample_value.a
				* extinction_per_kilometer
				* (density_scale / 0.048)
				* step_meters * 0.001;
			float sample_alpha = (1.0 - exp(-max(0.0, optical_depth))) * opacity_scale;
			sample_alpha = clamp(sample_alpha, 0.0, 1.0);
			float contribution = (1.0 - accumulated_alpha) * sample_alpha;
			accumulated_color += sample_value.rgb * contribution;
			accumulated_alpha += contribution;
		}
		// Canvas blending into the transparent mask target premultiplies this
		// color once. Supply straight RGB here so the sampled texture contains
		// the premultiplied value expected by the backdrop compositor.
		vec3 straight_color = accumulated_alpha > 0.0001
			? accumulated_color / accumulated_alpha
			: vec3(0.0);
		COLOR = vec4(straight_color, clamp(accumulated_alpha, 0.0, 1.0));
	}
}
