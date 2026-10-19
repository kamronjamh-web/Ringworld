shader_type spatial;
render_mode cull_back, depth_draw_opaque;

uniform vec3 ring_center = vec3(0.0);
uniform vec3 camera_world_position = vec3(0.0);
uniform vec3 sun_direction = vec3(1.0, 0.0, 0.0);
uniform float day_start_dot = -0.2;
uniform float day_full_dot = 0.35;
uniform vec3 night_tint = vec3(0.30, 0.45, 0.70);
uniform float night_direct_light_factor : hint_range(0.0, 1.0) = 0.0;
uniform float day_direct_light_factor : hint_range(0.0, 2.0) = 1.0;
uniform float shadow_floor : hint_range(0.0, 1.0) = 0.0;
uniform bool pilot_triplanar_enabled = false;
uniform sampler2D pilot_grass_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D pilot_dirt_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D biome_desert_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D biome_forest_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D biome_tundra_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D biome_alpine_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool biome_texture_weights_enabled = false;
uniform float pilot_texture_scale : hint_range(0.01, 2.0) = 0.20;
#include "res://materials/ring_color_space.gdshaderinc"
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_cloud_shadow.gdshaderinc"
#include "res://materials/ring_terrain_grass_coverage.gdshaderinc"

varying float ring_day_factor;
varying vec3 world_position;
varying vec3 surface_up_world;
// The projected field is view-independent at a surface point.  Cache it once
// per fragment so each directional light (sun and planet) does not repeat the
// cascade projection, ring wrapping, and texture lookup.
varying float local_cloud_shadow_occlusion;

void vertex() {
	world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec3 radial = world_position - ring_center;
	radial.y = 0.0;
	vec3 surface_up = length(radial) > 0.0001 ? -normalize(radial) : vec3(-1.0, 0.0, 0.0);
	surface_up_world = surface_up;
	ring_day_factor = ringworld_effective_solar_day(smoothstep(day_start_dot, day_full_dot, dot(surface_up, normalize(sun_direction))));
}

vec3 sample_pilot_triplanar(sampler2D texture_map, vec3 position, vec3 normal, vec3 blend) {
	// Keep every projection right-side-up when its surface normal points down a
	// negative world axis.  The ring's habitable face points inward, so omitting
	// these handedness corrections mirrors the dominant ground projection over
	// half of the ring.
	vec2 x_uv = position.zy;
	vec2 y_uv = position.xz;
	vec2 z_uv = position.xy;
	x_uv.x *= normal.x < 0.0 ? -1.0 : 1.0;
	y_uv.x *= normal.y < 0.0 ? -1.0 : 1.0;
	z_uv.x *= normal.z >= 0.0 ? -1.0 : 1.0;
	vec3 x_axis = texture(texture_map, x_uv).rgb;
	vec3 y_axis = texture(texture_map, y_uv).rgb;
	vec3 z_axis = texture(texture_map, z_uv).rgb;
	return x_axis * blend.x + y_axis * blend.y + z_axis * blend.z;
}

void fragment() {
	vec3 base_color = ring_vertex_color_to_linear(COLOR.rgb);
	float grass_coverage_eligibility = 0.0;
	float grass_coverage_upward = 0.0;
	if (pilot_triplanar_enabled) {
		vec3 world_normal = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
		vec3 blend = pow(abs(world_normal), vec3(8.0));
		blend /= max(blend.x + blend.y + blend.z, 0.0001);
		vec3 texture_position = world_position * pilot_texture_scale;
		vec3 grass = sample_pilot_triplanar(pilot_grass_albedo, texture_position, world_normal, blend);
		// Keep the existing texture detail, but restore the saturated living-green
		// read expected of the grassland biome at ground level.
		grass = mix(grass, grass * vec3(0.72, 1.30, 0.62), 0.58);
		vec3 dirt = sample_pilot_triplanar(pilot_dirt_albedo, texture_position, world_normal, blend);
		vec3 surface_texture = grass;
		float soil_surface = 1.0;
		if (biome_texture_weights_enabled) {
			// SmoothTerrainGrid packs the two-biome climate blend into COLOR once
			// while a mesh is built. No climate/noise work occurs per fragment.
			vec4 packed = clamp(COLOR, 0.0, 1.0);
			float grass_weight = max(0.0, 1.0 - packed.r - packed.g - packed.b - packed.a);
			// Grassland is the primary handoff; forest gets a restrained amount so
			// its distant floor does not become a bare, discontinuous patch.
			grass_coverage_eligibility = clamp(grass_weight + packed.g * .55, 0.0, 1.0);
			float total = max(0.0001, grass_weight + packed.r + packed.g + packed.b + packed.a);
			vec3 desert = sample_pilot_triplanar(biome_desert_albedo, texture_position, world_normal, blend);
			vec3 forest = sample_pilot_triplanar(biome_forest_albedo, texture_position, world_normal, blend);
			vec3 tundra = sample_pilot_triplanar(biome_tundra_albedo, texture_position, world_normal, blend);
			vec3 alpine = sample_pilot_triplanar(biome_alpine_albedo, texture_position, world_normal, blend);
			surface_texture = (grass * grass_weight + desert * packed.r + forest * packed.g + tundra * packed.b + alpine * packed.a) / total;
			// UV2.x is authored by SmoothTerrainPresenter from the immutable
			// material lattice: 0 bedrock, 1 rock, 2 soil. COLOR is data here,
			// never an albedo tint. This keeps caves, cliffs and the structural
			// shell on the material-safe dirt/rock path.
			soil_surface = smoothstep(1.5, 1.9, UV2.x);
		}
		float upward = smoothstep(0.45, 0.78, dot(world_normal, normalize(surface_up_world)));
		grass_coverage_upward = upward * soil_surface;
		float biome_surface = upward * soil_surface;
		base_color = mix(dirt, surface_texture, biome_surface);
		if (!biome_texture_weights_enabled) {
			// Legacy cube meshes still supply their BlockType palette through
			// COLOR, so retain the previous colour variation only on that path.
			base_color *= mix(vec3(1.0), ring_vertex_color_to_linear(COLOR.rgb), 0.28);
		}
	}
	float grass_coverage = ring_terrain_grass_coverage(
		world_position,
		distance(world_position, camera_world_position),
		grass_coverage_upward,
		grass_coverage_eligibility);
	base_color = ring_apply_terrain_grass_coverage(base_color, grass_coverage);
	vec3 lit_color = base_color
		* mix(night_tint, vec3(1.0), ring_day_factor);
	RingHaze haze = ring_haze_sample(camera_world_position, world_position, ring_center, ring_day_factor);
	// Extinction belongs in the lit surface term. In-scattered atmosphere is
	// radiance, so add it unlit instead of feeding it back through the sun.
	ALBEDO = lit_color * haze.T;
	EMISSION = ring_haze_inscatter(haze);
	ROUGHNESS = mix(1.0, .90, grass_coverage);
	SPECULAR = 0.18;
	METALLIC = 0.0;
	local_cloud_shadow_occlusion = sample_local_cloud_shadow(world_position);
	AO = 1.0 - local_cloud_shadow_occlusion * ring_day_factor * 0.72;
	AO_LIGHT_AFFECT = 0.0;
}

void light() {
	float n_dot_l = max(dot(NORMAL, LIGHT), 0.0);
	float readable_shadow = max(ATTENUATION, shadow_floor);
	// Only suppress the primary sun on the night hemisphere. The opposite-side
	// planet is also directional and must remain available as moonlight.
	vec3 sun_direction_view = normalize((VIEW_MATRIX * vec4(sun_direction, 0.0)).xyz);
	float primary_sun_weight = LIGHT_IS_DIRECTIONAL
		? smoothstep(0.90, 0.995, dot(normalize(LIGHT), sun_direction_view))
		: 0.0;
	float sun_hemisphere_direct = mix(
		night_direct_light_factor,
		day_direct_light_factor,
		ring_day_factor);
	float hemisphere_direct = mix(1.0, sun_hemisphere_direct, primary_sun_weight);
	float cloud_shadow = LIGHT_IS_DIRECTIONAL
		? local_cloud_shadow_occlusion * ring_day_factor
		: 0.0;
	float combined_visibility = combine_ring_shadow_visibility(
		readable_shadow,
		cloud_shadow,
		0.0);
	DIFFUSE_LIGHT += ALBEDO * LIGHT_COLOR * n_dot_l * combined_visibility * hemisphere_direct;
}
