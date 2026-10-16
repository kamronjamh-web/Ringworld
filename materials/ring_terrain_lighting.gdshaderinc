// Shared Terrain3D direct-light, night-tint and atmosphere contract. The
// Terrain3D ring surface and the grass-cards materials use these so near ground
// and the grass growing on it respond to the sun, cloud/macro shadow, night
// palette and haze identically. The uniforms are forwarded by
// RingTerrain3DSurface (see RegisterSurfaceFollower for follower materials).
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_cloud_shadow.gdshaderinc"
#include "res://materials/ring_terrain_macro_shadow.gdshaderinc"

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
	vec4 atmosphere = sample_ring_atmosphere(camera_world, world, center_world, day_factor);
	albedo *= 1.0 - atmosphere.a;
	emission = atmosphere.rgb * atmosphere.a * atmosphere_scattering_energy;
}

// Shadow/cloud/macro/hemisphere visibility of one light for a terrain-lit
// surface. Shared by the diffuse term and the terrain's wet-specular term.
float ring_terrain_direct_visibility(
		vec3 light,
		float attenuation,
		bool is_directional,
		mat4 view_matrix,
		float day_factor,
		float cloud_occlusion,
		vec2 shadow_uv,
		vec3 world,
		vec3 viewer) {
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
	float cloud_shadow = is_directional ? cloud_occlusion : 0.0;
	float sun_terrain_shadow = is_directional
		? sample_terrain_macro_shadow_uv(
			shadow_uv,
			world,
			viewer) * primary_sun_weight * day_factor
		: 0.0;
	float planet_terrain_shadow = is_directional
		? sample_terrain_planet_shadow_uv(
			shadow_uv,
			world,
			viewer) * primary_planet_weight
		: 0.0;
	float terrain_shadow = max(sun_terrain_shadow, planet_terrain_shadow);
	float combined_visibility = combine_ring_shadow_visibility(
		readable_shadow,
		cloud_shadow,
		terrain_shadow);
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
		vec3 albedo,
		mat4 view_matrix,
		float day_factor,
		float cloud_occlusion,
		vec2 shadow_uv,
		vec3 world,
		vec3 viewer,
		out float direct_visibility) {
	float n_dot_l = max(dot(normal, light), 0.0);
	direct_visibility = ring_terrain_direct_visibility(
		light,
		attenuation,
		is_directional,
		view_matrix,
		day_factor,
		cloud_occlusion,
		shadow_uv,
		world,
		viewer);
	return albedo * light_color * n_dot_l * direct_visibility;
}
