shader_type spatial;
render_mode cull_disabled, depth_draw_opaque;

uniform bool use_albedo_texture = false;
uniform sampler2D tree_albedo_texture : source_color, filter_nearest_mipmap_anisotropic, repeat_disable;
uniform vec3 tree_tint : source_color = vec3(1.0);
uniform float cloud_shadow_albedo_strength : hint_range(0.0, 1.0) = 0.34;
uniform float cloud_shadow_ao_strength : hint_range(0.0, 1.0) = 0.72;
uniform vec3 ring_center = vec3(0.0);
uniform vec3 sun_direction = vec3(1.0, 0.0, 0.0);
uniform float day_start_dot = -0.2;
uniform float day_full_dot = 0.35;
uniform vec3 night_tint = vec3(0.30, 0.45, 0.70);
uniform float night_direct_light_factor : hint_range(0.0, 1.0) = 0.0;
uniform float day_direct_light_factor : hint_range(0.0, 2.0) = 1.0;
uniform bool vegetation_wind_enabled = true;
uniform int tree_foliage_mode = 0;
uniform bool tree_foliage_attachments = false;

#include "res://materials/ring_cloud_shadow.gdshaderinc"
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_vegetation_haze.gdshaderinc"
#include "res://materials/ring_local_wind.gdshaderinc"

varying float tree_cloud_occlusion;
varying float tree_day_factor;
varying vec3 tree_world_position;
varying vec3 tree_haze_T;
varying vec3 tree_haze_S;

void vertex() {
	vec3 tree_root_world = MODEL_MATRIX[3].xyz;
	vec3 world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	if (vegetation_wind_enabled) {
		mat3 basis = mat3(MODEL_MATRIX);
		vec3 offset; mat3 jacobian;
		ring_tree_wind_geometry(tree_root_world, VERTEX, basis, CUSTOM0, tree_foliage_mode, tree_foliage_attachments, offset, jacobian);
		VERTEX += offset;
		NORMAL = ring_tree_deformed_normal(jacobian, NORMAL);
		if (length(TANGENT) > .0001) TANGENT = normalize(jacobian * TANGENT);
		if (length(BINORMAL) > .0001) BINORMAL = normalize(jacobian * BINORMAL);
		world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	}
	// A cloud-map texel covers tens of metres, so evaluating per tree vertex is
	// visually equivalent to per fragment while avoiding foliage overdraw cost.
	tree_cloud_occlusion = sample_local_cloud_shadow(world_position);
	vec3 radial = world_position - ring_center;
	radial.y = 0.0;
	vec3 surface_up = length(radial) > 0.0001 ? -normalize(radial) : vec3(-1.0, 0.0, 0.0);
	tree_day_factor = ringworld_effective_solar_day(smoothstep(
		day_start_dot, day_full_dot, dot(surface_up, normalize(sun_direction))));
	tree_world_position = world_position;
	RingHaze vertex_haze = ring_vegetation_haze_sample(RING_VEGETATION_HAZE_TREES, INSTANCE_ID,
		ring_atmosphere_camera_world_position, world_position, ring_center, tree_day_factor);
	tree_haze_T = vertex_haze.T;
	tree_haze_S = ring_haze_inscatter(vertex_haze);
}

void fragment() {
	vec3 sampled_color = use_albedo_texture
		? texture(tree_albedo_texture, UV).rgb
		: vec3(1.0);
	// Fade the daylight cloud term with solar visibility so a retained cloud
	// map cannot dim nighttime or total-eclipse ambient/planet lighting.
	float cloud_occlusion = tree_cloud_occlusion * tree_day_factor;
	ALBEDO = sampled_color * tree_tint * mix(night_tint, vec3(1.0), tree_day_factor)
		* (1.0 - cloud_occlusion * cloud_shadow_albedo_strength);
	ALBEDO *= tree_haze_T;
	vec3 haze_emission = tree_haze_S;
	if (atmosphere_haze_model == 0) {
		// Legacy: scattering colour from the fragment position, opacity from the vertex.
		vec4 atmosphere = sample_ring_atmosphere(ring_atmosphere_camera_world_position,
			tree_world_position, ring_center, tree_day_factor);
		haze_emission = atmosphere.rgb * (1.0 - tree_haze_T.g) * atmosphere_scattering_energy;
	}
	EMISSION += haze_emission;
	ROUGHNESS = 0.92;
	SPECULAR = 0.04;
	METALLIC = 0.0;
	AO = 1.0 - cloud_occlusion * cloud_shadow_ao_strength;
	// AO handles the daytime ambient term. Direct solar cloud attenuation is
	// handled in light(), avoiding an extra AO multiplier on all direct lights.
	AO_LIGHT_AFFECT = 0.0;
}

void light() {
	float n_dot_l = max(dot(NORMAL, LIGHT), 0.0);
	// Only the star light follows the ring terminator/eclipses. Other directional
	// lights are moon/planet fill and local lights remain available at night.
	vec3 sun_direction_view = normalize((VIEW_MATRIX * vec4(sun_direction, 0.0)).xyz);
	float primary_sun_weight = LIGHT_IS_DIRECTIONAL
		? smoothstep(0.90, 0.995, dot(normalize(LIGHT), sun_direction_view))
		: 0.0;
	float sun_direct = mix(night_direct_light_factor, day_direct_light_factor, tree_day_factor);
	float direct_visibility = mix(1.0, sun_direct, primary_sun_weight);
	float cloud_visibility = LIGHT_IS_DIRECTIONAL
		? 1.0 - tree_cloud_occlusion * tree_day_factor
		: 1.0;
	// LIGHT_COLOR includes Godot's PI scaling. Divide it back out for a matte,
	// bounded Lambert response while retaining engine ambient and non-sun lights.
	DIFFUSE_LIGHT += (LIGHT_COLOR / PI) * n_dot_l * ATTENUATION
		* direct_visibility * mix(1.0, cloud_visibility, primary_sun_weight);
}
