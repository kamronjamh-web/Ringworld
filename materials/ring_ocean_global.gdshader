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
#include "res://materials/ring_shadow_compose.gdshaderinc"
#include "res://materials/ring_far_weather_shadow.gdshaderinc"
#include "res://materials/ring_surface_aperture.gdshaderinc"
// Enables the whole-ring far weather cloud shadow (ring_far_weather_shadow.gdshaderinc) beyond the Sunshine maps.
uniform bool analytic_cloud_shadow_enabled = true;

varying vec3 world_position;
// Inward radial at this vertex (the local zenith); the light() helper uses it while the eclipse hub sun is up.
varying vec3 surface_up_world;
varying float ring_day_factor;
varying float water_kind_profile;
// Both ambient occlusion and the directional-light path consume the same day-gated cloud estimate.
varying float combined_cloud_shadow_occlusion;

float sample_combined_cloud_shadow(vec3 sample_world_position, float day_factor) {
	return min(0.80, ring_cloud_shadow_occlusion_with_far_weather(
		sample_world_position, ring_center, sun_direction, analytic_cloud_shadow_enabled)) * day_factor;
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
	surface_up_world = surface_up;
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
	if (ring_surface_aperture_discard(world_position)) discard;
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
		(water_wind_clock_enabled ? 1.0 : TIME),
		water_kind_profile,
		far_coordinate_dx,
		far_coordinate_dy);
    filtered_far_wave.xy *= water_mean_response;
    filtered_far_wave.z *= water_mean_response * water_mean_response;
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
	RingHaze haze = ring_haze_sample(camera_world_position, world_position, ring_center, ring_day_factor);
	ALBEDO = grazing_color * haze.T;
	EMISSION = ring_haze_inscatter(haze);
	ROUGHNESS = clamp(max(ocean_roughness, far_water_roughness)
		+ sqrt(max(filtered_far_wave.z * wave_normal_strength * wave_normal_strength, 0.0)) * 0.10, 0.02, 1.0);
	// Physical haze also attenuates the environment reflection; exactly 1 in Legacy.
	SPECULAR = ring_water_dielectric_specular() * ocean_specular * ring_haze_specular_scale(haze);
	METALLIC = 0.0;
	combined_cloud_shadow_occlusion = sample_combined_cloud_shadow(world_position, ring_day_factor);
	AO = 1.0 - combined_cloud_shadow_occlusion * 0.60;
	AO_LIGHT_AFFECT = 0.0;
	if (ring_shadow_debug_enabled()) {
		// Shadow debug view: only the selected shadow term reaches the screen (see ring_shadow_compose.gdshaderinc).
		ALBEDO = vec3(1.0);
		EMISSION = vec3(0.0);
		AO = 0.0;
		SPECULAR = 0.0;
		METALLIC = 0.0;
		ROUGHNESS = 1.0;
	}
}

void light() {
	float readable_shadow = max(ATTENUATION, shadow_floor);
	vec3 sun_direction_view = normalize((VIEW_MATRIX * vec4(sun_direction, 0.0)).xyz);
	float primary_sun_weight = LIGHT_IS_DIRECTIONAL
		? smoothstep(0.90, 0.995, dot(normalize(LIGHT), sun_direction_view))
		: 0.0;
	// Eclipse hub sun: light every point from its own zenith (identical to LIGHT while the hub weight is 0).
	float n_dot_l = max(dot(NORMAL, ringworld_primary_light(LIGHT, surface_up_world, VIEW_MATRIX, primary_sun_weight)), 0.0);
	float sun_hemisphere_direct = mix(
		night_direct_light_factor,
		day_direct_light_factor,
		ring_day_factor);
	float hemisphere_direct = mix(1.0, sun_hemisphere_direct, primary_sun_weight);
	// Terrain height-shadow clipmap (S5): replaces the sun macro term where it has data; inactive = the previous code path.
	bool height_active = LIGHT_IS_DIRECTIONAL && primary_sun_weight > 0.0 && ring_height_shadow_active();
	// S7a: the same for the planet light when the front layer is a planet layer (weight against the published direction).
	float planet_height_weight = LIGHT_IS_DIRECTIONAL && ring_height_shadow_planet_active()
		? ring_height_shadow_light_weight(LIGHT, VIEW_MATRIX) * (1.0 - clamp(ringworld_hub_light_weight, 0.0, 1.0) * primary_sun_weight)
		: 0.0;
	bool height_planet = planet_height_weight > 0.0;
	RingHeightShadow height_shadow = ring_height_shadow_none();
	if (height_active || height_planet) {
		height_shadow = ring_height_shadow_sample(
			world_position,
			0.0,
			0.0,
			distance(world_position, camera_world_position));
	}
	bool ocean_planet_macro_on = ring_height_shadow_enabled();
	float ocean_planet_macro = LIGHT_IS_DIRECTIONAL && ocean_planet_macro_on
		? sample_terrain_planet_shadow(world_position, ring_center, camera_world_position, ring_width_meters)
		: 0.0;
	float ocean_planet_weight = LIGHT_IS_DIRECTIONAL && ocean_planet_macro_on
		? terrain_planet_directional_light_weight(LIGHT, VIEW_MATRIX) * (1.0 - clamp(ringworld_hub_light_weight, 0.0, 1.0) * primary_sun_weight)
		: 0.0;
	float terrain_shadow = LIGHT_IS_DIRECTIONAL
		? ring_macro_terrain_occlusion(
			sample_terrain_macro_shadow(world_position, ring_center, camera_world_position, ring_width_meters) * (height_active ? ring_height_shadow_macro_scale(height_shadow) : 1.0),
			// S7a: the water now takes the planet macro shadow too, only while the height-shadow feature is on (off passes the old literal 0).
			ocean_planet_macro * (height_planet ? ring_height_shadow_macro_scale(height_shadow) : 1.0),
			primary_sun_weight,
			ocean_planet_weight,
			ring_day_factor,
			ringworld_hub_light_weight)
		: 0.0;
	RingShadowTerms terms = ring_shadow_terms(
		world_position,
		SCREEN_UV,
		readable_shadow,
		LIGHT_IS_DIRECTIONAL ? combined_cloud_shadow_occlusion : 0.0,
		terrain_shadow,
		RING_SHADOW_DEFAULT);
	// No contact term on water (RING_SHADOW_NO_CONTACT): the proxy is blend_mix, so the depth texel under it holds the seabed.
	if (height_active) {
		terms.height = ring_height_shadow_term(height_shadow, primary_sun_weight, ring_day_factor, ringworld_hub_light_weight, shadow_floor);
	} else if (height_planet) {
		terms.height = ring_height_shadow_planet_term(height_shadow, planet_height_weight, shadow_floor);
	}
	float combined_visibility = ring_shadow_sun_visibility(terms, RING_SHADOW_DEFAULT);
	if (ring_shadow_debug_enabled()) {
		if (LIGHT_IS_DIRECTIONAL && (primary_sun_weight > 0.5 || (ring_height_shadow_enabled() && planet_height_weight > 0.5))) {
			DIFFUSE_LIGHT += vec3(ring_shadow_debug_value(terms, combined_visibility));
		}
	} else {
		// Built-in convention (see ring_tree): Godot multiplies DIFFUSE_LIGHT by ALBEDO and LIGHT_COLOR carries the PI factor.
		DIFFUSE_LIGHT += (LIGHT_COLOR / PI)
			* n_dot_l
			* combined_visibility
			* hemisphere_direct;
	}
}
