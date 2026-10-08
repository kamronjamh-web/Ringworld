shader_type fog;
#include "res://materials/ring_terrain_cloud_sun_receiver.gdshaderinc"
#include "res://materials/celestial_lighting.gdshaderinc"

// Camera-local density tier. Canonical coordinates make the field stationary
// while the FogVolume snaps around the viewer and the world origin rebases.
uniform vec3 canonical_origin_km = vec3(0.0);
uniform float ring_circumference_km = 31415.9;
uniform float ring_width_km = 200.0;
uniform float cloud_seed = 1337.0;
uniform vec2 wind_km_per_second = vec2(0.0022, 0.0007);
uniform float weather_time_seconds = 0.0;
uniform float coverage : hint_range(0.0, 1.0) = 0.52;
uniform float precipitation : hint_range(0.0, 1.0) = 0.0;
uniform float storm_influence : hint_range(0.0, 1.0) = 0.0;
uniform int formation_profile : hint_range(0, 3) = 0;
uniform float cloud_base_fraction : hint_range(0.0, 1.0) = 0.08;
uniform float cloud_top_fraction : hint_range(0.0, 1.0) = 0.32;
uniform float volume_minimum_agl_meters = 100.0;
uniform float volume_maximum_agl_meters = 16000.0;
uniform float density_scale : hint_range(0.001, 0.15) = 0.048;
uniform float softness : hint_range(0.01, 0.3) = 0.075;
uniform float visibility : hint_range(0.0, 1.0) = 1.0;
uniform float handoff_start_meters = 9000.0;
uniform float handoff_end_meters = 12000.0;
uniform int quality_level : hint_range(1, 3) = 1;
uniform vec3 surface_up = vec3(-1.0, 0.0, 0.0);
uniform vec3 sun_direction = vec3(1.0, 0.0, 0.0);
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

void fog() {
	vec3 local_meters = (UVW - vec3(0.5)) * SIZE;
	vec3 canonical_km = canonical_origin_km + local_meters * 0.001;
	vec3 receiver_metres = canonical_km * 1000.0;
	canonical_km.x -= wind_km_per_second.x * weather_time_seconds;
	canonical_km.z -= wind_km_per_second.y * weather_time_seconds;
	vec3 ring_position = periodic_ring_position(canonical_km);
	float height = clamp(UVW.y, 0.0, 1.0);
	float cloud_height = clamp(
		(height - cloud_base_fraction) / max(0.015, cloud_top_fraction - cloud_base_fraction),
		0.0,
		1.0);

	vec3 broad_position = ring_position * 0.075
		+ vec3(canonical_km.y * 0.11, -canonical_km.y * 0.07, canonical_km.y * 0.09);
	float broad = value_noise3(broad_position);
	float shape = value_noise3(
		ring_position * 0.22
		+ vec3(canonical_km.y * 0.37, canonical_km.y * 0.21, -canonical_km.y * 0.29)
		+ vec3(17.3, 4.1, 29.7));
	float field = broad * 0.62 + shape * 0.38;
	if (quality_level >= 2) {
		float erosion = value_noise3(
			ring_position * 0.63
			+ vec3(-canonical_km.y * 0.83, canonical_km.y * 0.71, canonical_km.y * 0.58)
			+ vec3(41.7, 13.2, 7.6));
		field = field * 0.88 + erosion * 0.12;
	}
	if (quality_level >= 3) {
		float fine = value_noise3(
			ring_position * 1.45
			+ vec3(canonical_km.y * 1.9, -canonical_km.y * 1.4, canonical_km.y * 1.6)
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

	float day = ringworld_effective_solar_day(smoothstep(-0.2, 0.35, dot(normalize(surface_up), normalize(sun_direction))));
	float lower_darkening = (1.0 - smoothstep(0.05, 0.62, cloud_height))
		* max(storm_influence, precipitation);
	vec3 weather_shadow = mix(shadow_color, storm_color, clamp(lower_darkening * 0.92, 0.0, 1.0));
	vec3 daylight = mix(weather_shadow, sunlit_color, clamp(cloud_height * 0.72 + field * 0.35, 0.0, 1.0));
	vec3 unshadowed_color = mix(night_color, daylight, day);
	float terrain_sun = ring_terrain_cloud_receiver_visibility(receiver_metres);
	daylight -= (daylight - weather_shadow) * (1.0 - terrain_sun);
	// Native directional fog scattering cannot be intercepted by a fog shader.
	ALBEDO = mix(night_color, daylight, day);
	DENSITY = cloud * density_scale * mix(0.82, 1.28, storm_influence);
	EMISSION = unshadowed_color * cloud * mix(0.012, 0.038, day);
}
