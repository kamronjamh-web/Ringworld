shader_type spatial;
// Test-only reference receiver for tools/ScatterHazeShadowGpuTest.cs: a flat ground (or wall) built from the same includes and the same calls as the Terrain3D surface and
// materials/ring_scatter_rock.gdshader (ring_terrain_night_tint + ring_terrain_apply_atmosphere per fragment, ring_terrain_direct_diffuse in light()), with the same debug views,
// so the test compares rock and ground through identical code paths. Never used by the game.
render_mode diffuse_burley, specular_schlick_ggx, depth_draw_opaque, cull_disabled;
#include "res://materials/ring_terrain_lighting.gdshaderinc"
#include "res://materials/ring_surface_albedo_scale.gdshaderinc"
#include "res://materials/ring_ground_surface.gdshaderinc"
#include "res://materials/ring_wetness.gdshaderinc"

uniform vec3 ring_atmosphere_camera_world = vec3(0.0);
uniform vec3 ring_atmosphere_center_world = vec3(0.0);
uniform vec3 ring_viewer_world_position = vec3(0.0);
uniform float ring_width_meters = 20000.0;

uniform vec3 ref_albedo = vec3(0.22);
uniform vec3 ref_up = vec3(-1.0, 0.0, 0.0);
uniform float ref_height_offset = 0.0; // absolute canonical height = dot(world, ref_up) + ref_height_offset (strata band reference)
uniform int rock_debug_view = 0;       // same numbering as the rock shader

varying vec3 v_world;
varying float v_day;

void vertex() {
	v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	v_day = ringworld_effective_solar_day(smoothstep(day_start_dot, day_full_dot, dot(ref_up, normalize(sun_direction))));
}

void fragment() {
	vec3 albedo = ring_terrain_albedo_scaled(ref_albedo);
	vec3 wet_albedo = albedo;
	albedo = ring_terrain_night_tint(albedo, v_day);
	vec3 emission;
	ring_terrain_apply_atmosphere(albedo, emission, ring_atmosphere_camera_world, v_world, ring_atmosphere_center_world, v_day);
	ALBEDO = albedo;
	EMISSION = emission;
	ROUGHNESS = 0.92;
	SPECULAR = 0.18;
	AO = 1.0;
	AO_LIGHT_AFFECT = 0.0;
	if (rock_debug_view != 0) {
		vec3 probe = ring_terrain_night_tint(vec3(1.0), v_day);
		vec3 probe_emission;
		ring_terrain_apply_atmosphere(probe, probe_emission, ring_atmosphere_camera_world, v_world, ring_atmosphere_center_world, v_day);
		vec3 shown = vec3(0.0);
		float h = dot(v_world, ref_up) + ref_height_offset;
		if (rock_debug_view == 1) shown = probe;
		else if (rock_debug_view == 2) shown = probe_emission;
		else if (rock_debug_view == 4) shown = wet_albedo;
		else if (rock_debug_view == 5) shown = vec3((float(int(floor(h / max(ringworld_ground_world.x, 0.001))) & 127) + 1.0) / 128.0);
		else if (rock_debug_view == 6) shown = vec3(1.0);
		ALBEDO = rock_debug_view == 3 ? vec3(1.0) : vec3(0.0);
		EMISSION = shown;
		SPECULAR = 0.0;
		METALLIC = 0.0;
		ROUGHNESS = 1.0;
	}
}

void light() {
	float direct_visibility;
	vec3 diffuse = ring_terrain_direct_diffuse(NORMAL, LIGHT, ATTENUATION, LIGHT_IS_DIRECTIONAL, LIGHT_COLOR, VIEW_MATRIX, v_day, sample_local_cloud_shadow(v_world) * v_day, vec2(0.0), v_world,
		ring_viewer_world_position, SCREEN_UV, FRAGCOORD.xy, VIEWPORT_SIZE, INV_PROJECTION_MATRIX, RING_SHADOW_DEFAULT, direct_visibility);
	if (rock_debug_view == 3) DIFFUSE_LIGHT += vec3(direct_visibility);
	else DIFFUSE_LIGHT += diffuse;
}
