shader_type spatial;
render_mode cull_disabled, depth_draw_opaque;

uniform vec3 camera_world_position = vec3(0.0);
uniform bool far_space_enabled = false;
uniform float far_space_start_meters = 850000.0;
uniform float far_space_real_end_meters = 10500000.0;
uniform float far_space_render_end_meters = 1150000.0;
uniform bool near_space_enabled = false;
uniform float near_space_render_distance = 0.2;
uniform bool near_fade_enabled = true;
uniform float fade_start_meters = 4000.0;
uniform float fade_end_meters = 5500.0;
uniform bool far_fade_enabled = false;
uniform float far_fade_start_meters = 500000.0;
uniform float far_fade_end_meters = 1000000.0;
uniform vec3 ring_center = vec3(0.0);
uniform vec3 sun_direction = vec3(1.0, 0.0, 0.0);
uniform float day_start_dot = -0.2;
uniform float day_full_dot = 0.35;
uniform float night_brightness : hint_range(0.0, 1.0) = 0.75;
uniform vec3 night_tint = vec3(0.30, 0.45, 0.70);
uniform float terrain_light_floor : hint_range(0.0, 0.25) = 0.035;
uniform bool structural_scale_cues_enabled = true;
uniform float ring_width_meters = 318000.0;
uniform float structural_wall_top_meters = 10000.0;
uniform float structural_longitudinal_spacing_meters = 250000.0;
uniform float structural_longitudinal_half_width_meters = 1200.0;
uniform float structural_cross_spacing_meters = 50000.0;
uniform float structural_cross_half_width_meters = 450.0;
uniform float structural_top_rail_half_width_meters = 350.0;
uniform vec3 structural_cue_color : source_color = vec3(0.08, 0.72, 0.82);
uniform float structural_cue_day_energy : hint_range(0.0, 2.0) = 0.16;
uniform float structural_cue_night_energy : hint_range(0.0, 4.0) = 0.85;
#include "res://materials/ring_color_space.gdshaderinc"
#include "res://materials/ring_atmosphere.gdshaderinc"
varying vec3 world_position;
// Inward radial at this vertex (the local zenith); the light() helper uses it while the eclipse hub sun is up.
varying vec3 surface_up_world;
varying float ring_day_factor;

float periodic_structure_line(float coordinate, float spacing, float half_width) {
	float safe_spacing = max(spacing, 1.0);
	float cell_distance = abs(mod(coordinate + safe_spacing * 0.5, safe_spacing) - safe_spacing * 0.5);
	return 1.0 - smoothstep(
		max(half_width, 1.0),
		max(half_width * 2.0, half_width + 1.0),
		cell_distance);
}

float sample_structural_scale_cues(vec3 sample_world_position, float wall_face) {
	if (!structural_scale_cues_enabled) return 0.0;
	vec3 relative = sample_world_position - ring_center;
	float angle = atan(relative.z, relative.x);
	float around = angle * max(ring_radius_meters, 1.0);
	float across = relative.y + ring_width_meters * 0.5;
	float altitude = ring_radius_meters - length(relative.xz);
	float longitudinal = periodic_structure_line(
		around,
		structural_longitudinal_spacing_meters,
		structural_longitudinal_half_width_meters);
	float cross_grid = periodic_structure_line(
		across,
		structural_cross_spacing_meters,
		structural_cross_half_width_meters);
	float top_rail = 1.0 - smoothstep(
		max(structural_top_rail_half_width_meters, 1.0),
		max(structural_top_rail_half_width_meters * 2.0, structural_top_rail_half_width_meters + 1.0),
		abs(altitude - structural_wall_top_meters));
	// The cross grid is placed by the across coordinate, which is constant on a rim wall face: keep it off the walls so the
	// wall at across = 0 is not painted solid (the top rail and longitudinal bars still read there).
	return clamp(max(top_rail, max(longitudinal, cross_grid * 0.48 * (1.0 - wall_face))), 0.0, 1.0);
}

void vertex() {
	POSITION = PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
	world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	if (near_space_enabled) {
		vec3 view_position = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
		float real_distance = length(view_position);
		if (real_distance > 0.000001 && real_distance < near_space_render_distance) {
			view_position *= near_space_render_distance / real_distance;
			POSITION = PROJECTION_MATRIX * vec4(view_position, 1.0);
		}
	}
	if (far_space_enabled) {
		vec3 view_position = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
		float real_distance = length(view_position);
		if (real_distance > far_space_start_meters) {
			float distance_fraction = clamp(
				(real_distance - far_space_start_meters)
				/ max(far_space_real_end_meters - far_space_start_meters, 1.0),
				0.0,
				1.0);
			float render_distance = mix(far_space_start_meters, far_space_render_end_meters, distance_fraction);
			view_position *= render_distance / max(real_distance, 1.0);
			POSITION = PROJECTION_MATRIX * vec4(view_position, 1.0);
		}
	}
	vec3 radial = world_position - ring_center;
	radial.y = 0.0;
	vec3 surface_up = length(radial) > 0.0001 ? -normalize(radial) : vec3(-1.0, 0.0, 0.0);
	ring_day_factor = ringworld_effective_solar_day(smoothstep(day_start_dot, day_full_dot, dot(surface_up, normalize(sun_direction))));
	surface_up_world = surface_up;
}

void fragment() {
	float distance_to_camera = distance(world_position, camera_world_position);
	float near_handoff = (fade_start_meters + max(fade_end_meters, fade_start_meters + 0.01)) * 0.5;
	if (near_fade_enabled && distance_to_camera < near_handoff) discard;
	if (far_fade_enabled) {
		float far_handoff = (far_fade_start_meters + max(far_fade_end_meters, far_fade_start_meters + 1.0)) * 0.5;
		if (distance_to_camera > far_handoff) discard;
	}
	float hemisphere_brightness = mix(night_brightness, 1.0, ring_day_factor);
	vec3 lit_color = ring_vertex_color_to_linear(COLOR.rgb)
		* mix(night_tint, vec3(1.0), ring_day_factor)
		* hemisphere_brightness;
	// World Y is the ring axis: a rim wall face has a normal along it.
	float wall_face = smoothstep(0.7, 0.9, abs(normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz).y));
	float structural_cue = sample_structural_scale_cues(world_position, wall_face);
	lit_color = mix(lit_color, structural_cue_color, structural_cue * 0.34);
	RingHaze haze = ring_haze_sample(camera_world_position, world_position, ring_center, ring_day_factor);
	ALBEDO = lit_color * haze.T;
	EMISSION = ALBEDO * terrain_light_floor
		+ ring_haze_inscatter(haze)
		+ structural_cue_color * structural_cue
			// Eclipse darkness (spec D.1): the self-lit night cue scales with the night light; exactly 1 outside an eclipse.
			* mix(structural_cue_night_energy * ringworld_night_light_scale, structural_cue_day_energy, ring_day_factor)
			* (1.0 - (1.0 - ring_haze_visibility(haze)) * 0.65);
	ROUGHNESS = 0.92;
	SPECULAR = 0.25;
	METALLIC = 0.18;
}

void light() {
	vec3 sun_direction_view = normalize((VIEW_MATRIX * vec4(sun_direction, 0.0)).xyz);
	float primary_sun_weight = LIGHT_IS_DIRECTIONAL
		? smoothstep(0.90, 0.995, dot(normalize(LIGHT), sun_direction_view))
		: 0.0;
	// Eclipse hub sun: light every point from its own zenith (identical to LIGHT while the hub weight is 0).
	float n_dot_l = max(dot(NORMAL, ringworld_primary_light(LIGHT, surface_up_world, VIEW_MATRIX, primary_sun_weight)), 0.0);
	float directional_visibility = mix(1.0, ring_day_factor, primary_sun_weight);
	DIFFUSE_LIGHT += ALBEDO * LIGHT_COLOR * n_dot_l * ATTENUATION * directional_visibility;
}
