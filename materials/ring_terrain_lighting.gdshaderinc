// Shared Terrain3D direct-light, night-tint and atmosphere contract. The
// Terrain3D ring surface and the grass-cards materials use these so near ground
// and the grass growing on it respond to the sun, cloud/macro shadow, night
// palette and haze identically. The uniforms are forwarded by
// RingTerrain3DSurface (see RegisterSurfaceFollower for follower materials).
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_cloud_shadow.gdshaderinc"
#include "res://materials/ring_terrain_macro_shadow.gdshaderinc"
#include "res://materials/ring_shadow_compose.gdshaderinc"

// Keep the Terrain3D window's direct-light response aligned with the proxy
// terrain at its handoff. These defaults match ring_terrain_proxy.gdshader.
uniform vec3 sun_direction = vec3(1.0, 0.0, 0.0);
uniform float day_start_dot = -0.2;
uniform float day_full_dot = 0.35;
uniform vec3 night_tint = vec3(0.30, 0.45, 0.70);
uniform float night_direct_light_factor : hint_range(0.0, 1.0) = 0.0;
uniform float day_direct_light_factor : hint_range(0.0, 2.0) = 1.0;
uniform float shadow_floor : hint_range(0.0, 1.0) = 0.0;

// Retain the proxy terrain's low-light chroma before atmosphere haze is
// composited, so the near and LOD surfaces carry the same night palette.
vec3 ring_terrain_night_tint(vec3 albedo, float day_factor) {
	return albedo * mix(night_tint, vec3(1.0), day_factor);
}

// Composites distance haze: dims the albedo and returns the in-scattered emission.
void ring_terrain_apply_atmosphere(
		inout vec3 albedo,
		out vec3 emission,
		vec3 camera_world,
		vec3 world,
		vec3 center_world,
		float day_factor) {
	RingHaze haze = ring_haze_sample(camera_world, world, center_world, day_factor);
	albedo *= haze.T;
	emission = ring_haze_inscatter(haze);
}

// Shadow/cloud/macro/hemisphere visibility of one light for a terrain-lit
// surface. Shared by the diffuse term and the terrain's wet-specular term.
float ring_terrain_direct_visibility(
		vec3 normal,
		vec3 light,
		float attenuation,
		bool is_directional,
		mat4 view_matrix,
		float day_factor,
		float cloud_occlusion,
		vec2 shadow_uv,
		vec3 world,
		vec3 viewer,
		vec2 screen_uv,
		vec2 frag_coord,
		vec2 viewport_size,
		mat4 inv_projection,
		uint contact_flags) {
	float readable_shadow = max(attenuation, shadow_floor);
	vec3 sun_direction_view = normalize((view_matrix * vec4(sun_direction, 0.0)).xyz);
	float primary_sun_weight = is_directional
		? smoothstep(0.90, 0.995, dot(normalize(light), sun_direction_view))
		: 0.0;
	float primary_planet_weight = is_directional
		? terrain_planet_directional_light_weight(light, view_matrix)
		: 0.0;
	float sun_hemisphere_direct = mix(
		night_direct_light_factor,
		day_direct_light_factor,
		day_factor);
	float hemisphere_direct = mix(1.0, sun_hemisphere_direct, primary_sun_weight);
	// Terrain height-shadow clipmap (S5): while it is active it replaces the sun macro term where it has data and also brings the eclipse-hub term
	// to this path; otherwise the previous behaviour (no hub term here) is unchanged.
	bool height_active = is_directional && primary_sun_weight > 0.0 && ring_height_shadow_active();
	RingHeightShadow height_shadow = ring_height_shadow_none();
	if (height_active) {
		height_shadow = ring_height_shadow_sample(
			world,
			0.0,
			ring_height_shadow_slope_tan(transpose(mat3(view_matrix)) * normal),
			distance(world, viewer));
	}
	float terrain_shadow = is_directional
		? ring_macro_terrain_occlusion(
			sample_terrain_macro_shadow_uv(shadow_uv, world, viewer) * ring_height_shadow_macro_scale(height_shadow),
			sample_terrain_planet_shadow_uv(shadow_uv, world, viewer),
			primary_sun_weight,
			primary_planet_weight,
			day_factor,
			height_active ? ringworld_hub_light_weight : 0.0)
		: 0.0;
	RingShadowTerms terms = ring_shadow_terms(
		world,
		screen_uv,
		readable_shadow,
		is_directional ? cloud_occlusion : 0.0,
		terrain_shadow,
		RING_SHADOW_DEFAULT);
	terms.contact = ring_contact_visibility(frag_coord, viewport_size, inv_projection, primary_sun_weight, contact_flags);
	if (height_active) {
		terms.height = ring_height_shadow_term(height_shadow, primary_sun_weight, day_factor, ringworld_hub_light_weight, shadow_floor);
	}
	float combined_visibility = ring_shadow_sun_visibility(terms, RING_SHADOW_DEFAULT);
	if (ring_shadow_debug_enabled()) {
		return primary_sun_weight > 0.5 ? ring_shadow_debug_value(terms, combined_visibility) : 0.0;
	}
	return combined_visibility * hemisphere_direct;
}

// Diffuse contribution of one light (add to DIFFUSE_LIGHT). `direct_visibility`
// is also returned for callers that add their own specular term.
vec3 ring_terrain_direct_diffuse(
		vec3 normal,
		vec3 light,
		float attenuation,
		bool is_directional,
		vec3 light_color,
		mat4 view_matrix,
		float day_factor,
		float cloud_occlusion,
		vec2 shadow_uv,
		vec3 world,
		vec3 viewer,
		vec2 screen_uv,
		vec2 frag_coord,
		vec2 viewport_size,
		mat4 inv_projection,
		uint contact_flags,
		out float direct_visibility) {
	float n_dot_l = max(dot(normal, light), 0.0);
	direct_visibility = ring_terrain_direct_visibility(
		normal,
		light,
		attenuation,
		is_directional,
		view_matrix,
		day_factor,
		cloud_occlusion,
		shadow_uv,
		world,
		viewer,
		screen_uv,
		frag_coord,
		viewport_size,
		inv_projection,
		contact_flags);
	// Shadow debug view: show the selected term directly (ALBEDO is forced to 1 by the caller's fragment()).
	if (ring_shadow_debug_enabled()) {
		return vec3(direct_visibility);
	}
	// Built-in convention (see ring_tree): Godot applies ALBEDO to DIFFUSE_LIGHT afterwards and LIGHT_COLOR carries the PI factor.
	return (light_color / PI) * n_dot_l * direct_visibility;
}
