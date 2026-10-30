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
#include "res://materials/ring_shadow_compose.gdshaderinc"
#include "res://materials/ring_wetness.gdshaderinc"

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
	float surface_roughness = roughness_value;
	float surface_specular = 0.68;
	// Wet world: darkens and glosses like POI architecture (concrete porosity 0.5, metal 0).
	if (ring_wet_params.w > 0.5) {
		vec3 wet_normal = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
		RingWet wet_state = ring_wet_sample(world_position, wet_normal, ring_wet_frame[2].xyz);
		vec3 wet_albedo = vec3(1.0);
		ring_wet_apply(wet_state, 0.5 * (1.0 - metallic_value), wet_albedo, surface_roughness, surface_specular);
		ALBEDO *= wet_albedo;
	}
	ROUGHNESS = surface_roughness;
	SPECULAR = surface_specular;
	float cloud_occlusion = sample_local_cloud_shadow(world_position) * ring_day_factor;
	AO = min(1.0 - cloud_occlusion, ring_storm_ambient(ring_storm_level(world_position))); // storm light
	AO_LIGHT_AFFECT = 1.0;
	if (ring_shadow_debug_enabled()) {
		// Shadow debug view (no light() here): the only shadow term this material owns is the cloud one, shown for modes 1 and 5.
		ALBEDO = vec3(0.0);
		SPECULAR = 0.0;
		METALLIC = 0.0;
		AO = 1.0;
		EMISSION = vec3(ringworld_shadow_debug_mode == 1 || ringworld_shadow_debug_mode == 5 ? 1.0 - cloud_occlusion : 1.0);
	}
}
