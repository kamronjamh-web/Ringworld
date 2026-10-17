shader_type spatial;
render_mode cull_disabled, depth_draw_opaque;

uniform vec3 camera_world_position = vec3(0.0);
// The analytic whole-ring tier keeps its true world position for lighting and
// haze, but remaps distant vertex depth into the camera's reserved far band.
// Regional HLOD leaves this disabled and renders at its normal depth.
uniform bool far_space_enabled = false;
uniform float far_space_start_meters = 850000.0;
uniform float far_space_real_end_meters = 10500000.0;
uniform float far_space_render_end_meters = 1150000.0;
uniform float fade_start_meters = 4000.0;
uniform float fade_end_meters = 5500.0;
uniform bool far_fade_enabled = false;
uniform float far_fade_start_meters = 500000.0;
uniform float far_fade_end_meters = 1000000.0;
// Regional scaled-far bands are already non-overlapping annuli about their
// reanchor origin. Camera-distance clipping would shift those boundaries by up
// to half an anchor cell and can open a gap between otherwise welded rings.
uniform bool regional_band_distance_clip = true;
uniform vec3 ring_center = vec3(0.0);
// Camera-relative regional meshes are built near the origin, while their real
// ring center can be billions of metres away. Keep their illumination normal
// in a local uniform instead of recovering it by subtracting large floats.
uniform bool camera_relative_region = false;
uniform vec3 regional_surface_up = vec3(-1.0, 0.0, 0.0);
uniform vec3 sun_direction = vec3(1.0, 0.0, 0.0);
uniform float day_start_dot = -0.2;
uniform float day_full_dot = 0.35;
uniform float night_brightness : hint_range(0.0, 1.0) = 0.72;
uniform vec3 night_tint = vec3(0.30, 0.45, 0.70);
uniform float night_direct_light_factor : hint_range(0.0, 1.0) = 0.0;
uniform float day_direct_light_factor : hint_range(0.0, 2.0) = 1.0;
uniform float shadow_floor : hint_range(0.0, 1.0) = 0.0;
#include "res://materials/ring_color_space.gdshaderinc"
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_cloud_shadow.gdshaderinc"
#include "res://materials/ring_terrain_macro_shadow.gdshaderinc"
#include "res://materials/ring_far_cloud_field.gdshaderinc"
#include "res://materials/ring_surface_aperture.gdshaderinc"
uniform float analytic_cloud_shadow_strength : hint_range(0.0, 0.8) = 0.60;
uniform bool analytic_cloud_shadow_enabled = true;
uniform float analytic_cloud_shadow_fade_start_meters = 38000.0;
uniform float analytic_cloud_shadow_fade_end_meters = 50000.0;
uniform float ring_width_meters = 20000.0;
uniform bool debug_color_enabled = false;
uniform vec3 debug_color = vec3(1.0, 1.0, 0.0);
#include "res://materials/ring_far_surface_tile.gdshaderinc"

varying vec3 world_position;
// Inward radial at this vertex (the local zenith); the light() helper uses it while the eclipse hub sun is up.
varying vec3 surface_up_world;
varying float ring_day_factor;
varying vec2 terrain_macro_shadow_uv;
// Both ambient occlusion and the directional-light path consume the same
// day-gated strongest cloud estimate for this fragment.
varying float combined_cloud_shadow_occlusion;

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
	// The analytic field remains capped at its authored maximum, but the local
	// projected map is authoritative near the camera and can legitimately reach
	// full occlusion under a dense storm. Choose the strongest estimate rather
	// than compounding the two shadow systems.
	return max(
		min(0.80, analytic_shadow),
		sample_local_cloud_shadow(sample_world_position)) * day_factor;
}

void vertex() {
	// Writing POSITION opts out of Godot's automatic projection for every
	// vertex, so establish the normal path before optionally compressing it.
	POSITION = PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
	world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	terrain_macro_shadow_uv = UV;
	if (far_space_enabled) {
		// Compress in view space using Godot's combined model-view transform.
		// This avoids subtracting multi-million-metre world-space floats and uses
		// the real render camera rather than a CPU approximation of its position.
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
	vec3 surface_up = camera_relative_region
		? normalize(regional_surface_up)
		: (length(radial) > 0.0001 ? -normalize(radial) : vec3(-1.0, 0.0, 0.0));
	ring_day_factor = ringworld_effective_solar_day(smoothstep(day_start_dot, day_full_dot, dot(surface_up, normalize(sun_direction))));
	surface_up_world = surface_up;
}

void fragment() {
	if (ring_surface_aperture_discard(world_position)) discard;
	float distance_to_camera = distance(world_position, camera_world_position);
	// Values are scaled world units. Mega Ring uses 1e-7, so a fixed 0.01
	// safety span expands into a 100 km overlap and makes adjacent LOD rings
	// fight each other. A tiny non-zero span avoids a divide-by-zero while
	// preserving the authored physical handoff distance.
        if (regional_band_distance_clip) {
                float near_handoff = (fade_start_meters + max(fade_end_meters, fade_start_meters + 0.000001)) * 0.5;
                if (distance_to_camera < near_handoff) discard;
                if (far_fade_enabled) {
                        float far_handoff = (far_fade_start_meters + max(far_fade_end_meters, far_fade_start_meters + 0.000001)) * 0.5;
                        if (distance_to_camera > far_handoff) discard;
                }
        }

	vec3 terrain_color = ring_vertex_color_to_linear(
		debug_color_enabled ? debug_color : (surface_tile_enabled
			? mix(COLOR.rgb, texture(surface_tile_color, surface_tile_sample_uv(UV2)).rgb, surface_tile_blend) : COLOR.rgb));
	if (surface_tile_enabled) {
		vec3 canonical_normal = normalize(texture(surface_tile_normal, surface_tile_sample_uv(UV2)).xyz * 2.0 - 1.0);
		float angle = surface_tile_angle_start + UV2.x * surface_tile_angle_span;
		vec3 tangent = vec3(-sin(angle), 0.0, cos(angle));
		vec3 up = vec3(-cos(angle), 0.0, -sin(angle));
		vec3 curved_normal = normalize(tangent * canonical_normal.x + up * canonical_normal.y + vec3(0.0, canonical_normal.z, 0.0));
		NORMAL = normalize(mix(NORMAL, (VIEW_MATRIX * MODEL_MATRIX * vec4(curved_normal, 0.0)).xyz, surface_tile_blend));
	}
	float hemisphere_brightness = mix(night_brightness, 1.0, ring_day_factor);
	vec3 lit_color = terrain_color * mix(night_tint, vec3(1.0), ring_day_factor) * hemisphere_brightness;
	vec4 atmosphere = sample_ring_atmosphere(camera_world_position, world_position, ring_center, ring_day_factor);
	ALBEDO = lit_color * (1.0 - atmosphere.a);
	EMISSION = atmosphere.rgb * atmosphere.a * atmosphere_scattering_energy;
	ROUGHNESS = 1.0;
	SPECULAR = 0.18;
	METALLIC = 0.0;
	combined_cloud_shadow_occlusion = sample_combined_cloud_shadow(world_position, ring_day_factor);
	AO = 1.0 - combined_cloud_shadow_occlusion * 0.72;
	AO_LIGHT_AFFECT = 0.0;
}

void light() {
	float readable_shadow = max(ATTENUATION, shadow_floor);
	vec3 sun_direction_view = normalize((VIEW_MATRIX * vec4(sun_direction, 0.0)).xyz);
	float primary_sun_weight = LIGHT_IS_DIRECTIONAL
		? smoothstep(0.90, 0.995, dot(normalize(LIGHT), sun_direction_view))
		: 0.0;
	// Eclipse hub sun: light every point from its own zenith (identical to LIGHT while the hub weight is 0).
	float n_dot_l = max(dot(NORMAL, ringworld_primary_light(LIGHT, surface_up_world, VIEW_MATRIX, primary_sun_weight)), 0.0);
	float primary_planet_weight = LIGHT_IS_DIRECTIONAL
		? terrain_planet_directional_light_weight(LIGHT, VIEW_MATRIX)
		: 0.0;
	// Hub mode aims the StarLight at the zenith, which can fall inside the planet-light cone near real noon: never treat
	// the hub light as planet light (exactly 1 at hub weight 0).
	primary_planet_weight *= 1.0 - clamp(ringworld_hub_light_weight, 0.0, 1.0) * primary_sun_weight;
	float sun_hemisphere_direct = mix(
		night_direct_light_factor,
		day_direct_light_factor,
		ring_day_factor);
	float hemisphere_direct = mix(1.0, sun_hemisphere_direct, primary_sun_weight);
	float sun_terrain_shadow = LIGHT_IS_DIRECTIONAL
		? (terrain_macro_shadow_use_uv
			? sample_terrain_macro_shadow_uv(
				terrain_macro_shadow_uv,
				world_position,
				camera_world_position)
			: sample_terrain_macro_shadow(
				world_position,
				ring_center,
				camera_world_position,
				ring_width_meters)) * ring_day_factor * primary_sun_weight * (1.0 - clamp(ringworld_hub_light_weight, 0.0, 1.0))
		: 0.0;
	float planet_terrain_shadow = LIGHT_IS_DIRECTIONAL
		? (terrain_macro_shadow_use_uv
			? sample_terrain_planet_shadow_uv(
				terrain_macro_shadow_uv,
				world_position,
				camera_world_position)
			: sample_terrain_planet_shadow(
				world_position,
				ring_center,
				camera_world_position,
				ring_width_meters)) * primary_planet_weight
		: 0.0;
	float terrain_shadow = max(sun_terrain_shadow, planet_terrain_shadow);
	float cloud_shadow = LIGHT_IS_DIRECTIONAL
		? combined_cloud_shadow_occlusion
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
