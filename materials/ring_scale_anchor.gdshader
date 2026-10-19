shader_type spatial;
render_mode cull_back, depth_draw_opaque;

uniform vec3 camera_world_position = vec3(0.0);
uniform vec3 ring_center = vec3(0.0);
uniform vec3 sun_direction = vec3(1.0, 0.0, 0.0);
uniform float day_start_dot = -0.2;
uniform float day_full_dot = 0.35;
uniform vec3 anchor_color : source_color = vec3(0.18, 0.23, 0.28);
uniform vec3 anchor_emission_color : source_color = vec3(0.05, 0.70, 0.82);
uniform float anchor_emission_energy : hint_range(0.0, 8.0) = 0.18;
uniform float emission_fade_start_meters = 30000.0;
uniform float emission_fade_end_meters = 80000.0;
uniform float metallic_value : hint_range(0.0, 1.0) = 0.72;
uniform float roughness_value : hint_range(0.0, 1.0) = 0.48;
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_cloud_shadow.gdshaderinc"

varying vec3 world_position;
varying float ring_day_factor;

void vertex() {
	world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec3 radial = world_position - ring_center;
	radial.y = 0.0;
	vec3 surface_up = length(radial) > 0.0001
		? -normalize(radial)
		: vec3(-1.0, 0.0, 0.0);
	ring_day_factor = ringworld_effective_solar_day(smoothstep(
		day_start_dot,
		day_full_dot,
		dot(surface_up, normalize(sun_direction))));
}

void fragment() {
	vec3 lit_color = anchor_color * mix(vec3(0.30, 0.42, 0.62), vec3(1.0), ring_day_factor);
	float fade_start = max(emission_fade_start_meters, 0.0);
	float fade_end = max(emission_fade_end_meters, fade_start + 1.0);
	float emission_visibility = 1.0 - smoothstep(
		fade_start,
		fade_end,
		distance(camera_world_position, world_position));
	RingHaze haze = ring_haze_sample(
		camera_world_position,
		world_position,
		ring_center,
		ring_day_factor);
	ALBEDO = lit_color * haze.T;
	EMISSION = anchor_emission_color * anchor_emission_energy * emission_visibility * (1.0 - (1.0 - ring_haze_visibility(haze)) * 0.72)
		+ ring_haze_inscatter(haze);
	METALLIC = metallic_value;
	ROUGHNESS = roughness_value;
	SPECULAR = 0.68;
	float cloud_occlusion = sample_local_cloud_shadow(world_position) * ring_day_factor;
	AO = 1.0 - cloud_occlusion;
	AO_LIGHT_AFFECT = 1.0;
}
