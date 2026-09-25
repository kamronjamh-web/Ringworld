shader_type spatial;
// Global water is intentionally opaque. Screen-space refraction on the large,
// segmented HLOD is sorted per segment and samples clouds/sky, producing white
// rectangular patches. Close proxy water retains the full transparent shader.
render_mode cull_disabled, depth_draw_opaque, shadows_disabled;

uniform vec3 camera_world_position = vec3(0.0);
uniform float fade_start_meters = 4000.0;
uniform float fade_end_meters = 5500.0;
uniform bool far_fade_enabled = false;
uniform float far_fade_start_meters = 500000.0;
uniform float far_fade_end_meters = 1000000.0;
uniform vec3 ring_center = vec3(0.0);
uniform vec3 sun_direction = vec3(1.0, 0.0, 0.0);
uniform float day_start_dot = -0.2;
uniform float day_full_dot = 0.35;
uniform float night_brightness : hint_range(0.0, 1.0) = 0.72;
uniform vec3 night_tint = vec3(0.30, 0.45, 0.70);
uniform float night_direct_light_factor : hint_range(0.0, 1.0) = 0.0;
uniform float day_direct_light_factor : hint_range(0.0, 2.0) = 1.0;
uniform float shadow_floor : hint_range(0.0, 1.0) = 0.0;
uniform float ocean_roughness : hint_range(0.02, 0.5) = 0.20;
uniform float ocean_specular : hint_range(0.0, 1.0) = 0.88;
uniform float ring_width_meters = 20000.0;
#include "res://materials/ring_color_space.gdshaderinc"
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_water_common.gdshaderinc"
#include "res://materials/ring_cloud_shadow.gdshaderinc"
#include "res://materials/ring_terrain_macro_shadow.gdshaderinc"
#include "res://materials/ring_far_cloud_field.gdshaderinc"
uniform bool analytic_cloud_shadow_enabled = true;
uniform float analytic_cloud_shadow_strength : hint_range(0.0, 0.8) = 0.60;
uniform float analytic_cloud_shadow_fade_start_meters = 38000.0;
uniform float analytic_cloud_shadow_fade_end_meters = 50000.0;

varying vec3 world_position;
varying float ring_day_factor;
varying float water_kind_profile;

float sample_combined_cloud_shadow(vec3 sample_world_position, float day_factor) {
	float distance_to_camera = distance(sample_world_position, camera_world_position);
	float analytic_fade = smoothstep(
		analytic_cloud_shadow_fade_start_meters,
		max(analytic_cloud_shadow_fade_start_meters + 1.0, analytic_cloud_shadow_fade_end_meters),
		distance_to_camera);
	float analytic_shadow = analytic_cloud_shadow_enabled
		? ring_far_cloud_field_world(sample_world_position, ring_center)
			* analytic_cloud_shadow_strength
			* analytic_fade
		: 0.0;
	return min(
		0.80,
		max(analytic_shadow, sample_local_cloud_shadow(sample_world_position))) * day_factor;
}

void vertex() {
	water_kind_profile = ring_water_kind_from_vertex_alpha(COLOR.a);
	vec3 original_world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec3 original_radial = original_world_position - ring_center;
	original_radial.y = 0.0;
	world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec3 radial = world_position - ring_center;
	radial.y = 0.0;
	vec3 surface_up = length(radial) > 0.0001 ? -normalize(radial) : vec3(-1.0, 0.0, 0.0);
	ring_day_factor = ringworld_effective_solar_day(smoothstep(day_start_dot, day_full_dot, dot(surface_up, normalize(sun_direction))));
}

void fragment() {
	// Form stable tangent-space footprints before distance discards. Deriving the
	// atan-based ring coordinate directly would create a false derivative at the
	// +/-pi seam and can blank a strip through the filtered far swell.
	vec3 footprint_radial = world_position - ring_center;
	footprint_radial.y = 0.0;
	vec3 footprint_around = length(footprint_radial) > 0.0001
		? normalize(vec3(-footprint_radial.z, 0.0, footprint_radial.x))
		: vec3(0.0, 0.0, 1.0);
	vec3 world_dx = dFdx(world_position);
	vec3 world_dy = dFdy(world_position);
	vec2 far_coordinate_dx = vec2(dot(world_dx, footprint_around), world_dx.y);
	vec2 far_coordinate_dy = vec2(dot(world_dy, footprint_around), world_dy.y);
	float distance_to_camera = distance(world_position, camera_world_position);
	float near_handoff = (fade_start_meters + max(fade_end_meters, fade_start_meters + 0.01)) * 0.5;
	if (distance_to_camera < near_handoff) discard;
	if (far_fade_enabled) {
		float far_handoff = (far_fade_start_meters + max(far_fade_end_meters, far_fade_start_meters + 1.0)) * 0.5;
		if (distance_to_camera > far_handoff) discard;
	}

	vec3 radial = world_position - ring_center;
	radial.y = 0.0;
	vec3 smooth_water_normal_world = length(radial) > 0.0001 ? -normalize(radial) : vec3(-1.0, 0.0, 0.0);
	vec3 filtered_far_wave = ring_water_far_wave_gradient_filtered(
		ring_water_coordinates(world_position, ring_center, ring_radius_meters),
		TIME,
		water_kind_profile,
		far_coordinate_dx,
		far_coordinate_dy);
	vec2 far_gradient = filtered_far_wave.xy;
	vec3 animated_water_normal_world = ring_water_normal_world_from_gradient(
		world_position,
		ring_center,
		far_gradient);
	if (!FRONT_FACING) animated_water_normal_world *= -1.0;
	NORMAL = normalize((VIEW_MATRIX * vec4(animated_water_normal_world, 0.0)).xyz);
	float fresnel = ring_water_schlick_fresnel(
		clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0));
	float reflection_weight = clamp(fresnel * fresnel_strength, 0.0, 1.0);
	float hemisphere_brightness = mix(night_brightness, 1.0, ring_day_factor);
	vec3 seeded_tint = ring_vertex_color_to_linear(COLOR.rgb);
	float shallow_hint = clamp(COLOR.g * 2.6, 0.0, 1.0);
	vec3 water_tint = mix(deep_water_tint, shallow_water_tint, shallow_hint);
	water_tint = mix(seeded_tint, water_tint, 0.35)
		* mix(night_tint, vec3(1.0), ring_day_factor)
		* hemisphere_brightness;
	vec3 grazing_color = mix(water_tint, vec3(0.12, 0.28, 0.42), reflection_weight * 0.55);
	vec4 atmosphere = sample_ring_atmosphere(camera_world_position, world_position, ring_center, ring_day_factor);
	ALBEDO = grazing_color * (1.0 - atmosphere.a);
	EMISSION = atmosphere.rgb * atmosphere.a * atmosphere_scattering_energy;
	ROUGHNESS = clamp(max(ocean_roughness, far_water_roughness)
		+ sqrt(max(filtered_far_wave.z * wave_normal_strength * wave_normal_strength, 0.0)) * 0.10, 0.02, 1.0);
	SPECULAR = ring_water_dielectric_specular() * ocean_specular;
	METALLIC = 0.0;
	AO = 1.0 - sample_combined_cloud_shadow(world_position, ring_day_factor) * 0.60;
	AO_LIGHT_AFFECT = 0.0;
}

void light() {
	float n_dot_l = max(dot(NORMAL, LIGHT), 0.0);
	float readable_shadow = max(ATTENUATION, shadow_floor);
	vec3 sun_direction_view = normalize((VIEW_MATRIX * vec4(sun_direction, 0.0)).xyz);
	float primary_sun_weight = LIGHT_IS_DIRECTIONAL
		? smoothstep(0.90, 0.995, dot(normalize(LIGHT), sun_direction_view))
		: 0.0;
	float sun_hemisphere_direct = mix(
		night_direct_light_factor,
		day_direct_light_factor,
		ring_day_factor);
	float hemisphere_direct = mix(1.0, sun_hemisphere_direct, primary_sun_weight);
	float terrain_shadow = LIGHT_IS_DIRECTIONAL
		? sample_terrain_macro_shadow(
			world_position,
			ring_center,
			camera_world_position,
			ring_width_meters) * primary_sun_weight * ring_day_factor
		: 0.0;
	float cloud_shadow = LIGHT_IS_DIRECTIONAL
		? sample_combined_cloud_shadow(world_position, ring_day_factor)
		: 0.0;
	float combined_visibility = combine_ring_shadow_visibility(
		readable_shadow,
		cloud_shadow,
		terrain_shadow);
	DIFFUSE_LIGHT += ALBEDO
		* LIGHT_COLOR
		* n_dot_l
		* combined_visibility
		* hemisphere_direct;
}
