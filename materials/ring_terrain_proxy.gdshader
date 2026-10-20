shader_type spatial;
render_mode cull_back, depth_draw_opaque;

uniform vec3 camera_world_position = vec3(0.0);
// CPU clipmap coverage is readiness-gated. Keeping the distance discard off
// lets an installed coarse sector remain as an underlay until its complete
// finer replacement is resident.
uniform bool near_fade_enabled = false;
uniform float fade_start_meters = 64.0;
uniform float fade_end_meters = 104.0;
uniform bool far_fade_enabled = false;
uniform float fade_out_start_meters = 480.0;
uniform float fade_out_end_meters = 640.0;
uniform bool geomorph_enabled = true;
uniform bool runtime_parent_chord_enabled = false;
uniform float morph_start_meters = 480.0;
uniform float morph_end_meters = 640.0;
uniform float roughness_value : hint_range(0.0, 1.0) = 1.0;
uniform vec3 ring_center = vec3(0.0);
uniform vec3 sun_direction = vec3(1.0, 0.0, 0.0);
uniform float day_start_dot = -0.2;
uniform float day_full_dot = 0.35;
uniform vec3 night_tint = vec3(0.30, 0.45, 0.70);
uniform float night_direct_light_factor : hint_range(0.0, 1.0) = 0.0;
uniform float day_direct_light_factor : hint_range(0.0, 2.0) = 1.0;
uniform float shadow_floor : hint_range(0.0, 1.0) = 0.0;
// COLOR is a compact biome-weight payload for dry proxy-sector surfaces:
// desert, forest, tundra, alpine.  The implicit remainder is grassland.
// These values are sampled on the CPU only while a sector mesh is built.
uniform bool biome_texture_weights_enabled = false;
uniform float biome_texture_scale : hint_range(0.01, 2.0) = 0.20;
uniform sampler2D biome_grass_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D biome_dirt_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D biome_desert_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D biome_forest_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D biome_tundra_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D biome_alpine_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
#include "res://materials/ring_color_space.gdshaderinc"
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_cloud_shadow.gdshaderinc"
#include "res://materials/ring_terrain_macro_shadow.gdshaderinc"
#include "res://materials/ring_shadow_compose.gdshaderinc"
#include "res://materials/ring_far_cloud_field.gdshaderinc"
#include "res://materials/ring_terrain_grass_coverage.gdshaderinc"
#include "res://materials/ring_surface_albedo_scale.gdshaderinc"
#include "res://materials/ring_surface_aperture.gdshaderinc"
uniform float analytic_cloud_shadow_strength : hint_range(0.0, 0.8) = 0.60;
uniform bool analytic_cloud_shadow_enabled = true;
uniform float analytic_cloud_shadow_fade_start_meters = 38000.0;
uniform float analytic_cloud_shadow_fade_end_meters = 50000.0;
uniform float ring_width_meters = 20000.0;
uniform bool debug_color_enabled = false;
uniform vec3 debug_color = vec3(1.0, 0.0, 1.0);
// Shared per-LOD atlas.  Each sector supplies its own rect as an instance
// uniform, so coverage changes need no unique materials or mesh rebuilds.
uniform sampler2D coverage_atlas : filter_nearest, repeat_disable;
instance uniform vec4 coverage_atlas_rect = vec4(0.0, 0.0, 0.0, 0.0);

varying vec3 world_position;
varying float ring_day_factor;
varying vec2 proxy_coverage_uv;
varying vec3 surface_up_world;
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
	proxy_coverage_uv = UV;
	vec3 original_world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	if (geomorph_enabled) {
		float morph_distance = distance(original_world_position, camera_world_position);
		// Visibility ownership switches at the middle of the far fade band.  The
		// fine surface must already equal its parent at that point; otherwise the
		// ring-radial height delta becomes a visible cross-ring seam.
		float morph_handoff = (morph_start_meters + max(morph_end_meters, morph_start_meters + 1.0)) * 0.5;
		float morph = smoothstep(morph_start_meters, max(morph_handoff, morph_start_meters + 0.01), morph_distance);
		vec3 original_radial = original_world_position - ring_center;
		original_radial.y = 0.0;
		vec3 morph_surface_up_world = length(original_radial) > 0.0001 ? -normalize(original_radial) : vec3(-1.0, 0.0, 0.0);
		vec3 surface_up_local = normalize(inverse(mat3(MODEL_MATRIX)) * morph_surface_up_world);
		VERTEX += runtime_parent_chord_enabled && UV2.y < .5 ? CUSTOM0.xyz * morph : surface_up_local * UV2.x * morph;
	}
	world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec3 radial = world_position - ring_center;
	radial.y = 0.0;
	vec3 surface_up = length(radial) > 0.0001 ? -normalize(radial) : vec3(-1.0, 0.0, 0.0);
	ring_day_factor = ringworld_effective_solar_day(smoothstep(day_start_dot, day_full_dot, dot(surface_up, normalize(sun_direction))));
	surface_up_world = surface_up;
}

vec3 sample_biome_triplanar(sampler2D texture_map, vec3 position, vec3 normal, vec3 blend) {
	vec2 x_uv = position.zy;
	vec2 y_uv = position.xz;
	vec2 z_uv = position.xy;
	x_uv.x *= normal.x < 0.0 ? -1.0 : 1.0;
	y_uv.x *= normal.y < 0.0 ? -1.0 : 1.0;
	z_uv.x *= normal.z >= 0.0 ? -1.0 : 1.0;
	return texture(texture_map, x_uv).rgb * blend.x
		+ texture(texture_map, y_uv).rgb * blend.y
		+ texture(texture_map, z_uv).rgb * blend.z;
}

void fragment() {
	if (ring_surface_aperture_discard(world_position)) discard;
	if (coverage_atlas_rect.z > 0.0 && proxy_coverage_uv.x >= 0.0 && proxy_coverage_uv.y >= 0.0
		&& texture(coverage_atlas, coverage_atlas_rect.xy + min(proxy_coverage_uv, vec2(0.99999)) * coverage_atlas_rect.zw).r > 0.5) discard;
	float distance_to_camera = distance(world_position, camera_world_position);
	float near_handoff = (fade_start_meters + max(fade_end_meters, fade_start_meters + 0.01)) * 0.5;
	if (near_fade_enabled && distance_to_camera < near_handoff) {
		discard;
	}
	if (far_fade_enabled) {
		float far_handoff = (fade_out_start_meters + max(fade_out_end_meters, fade_out_start_meters + 0.01)) * 0.5;
		if (distance_to_camera > far_handoff) {
			discard;
		}
	}

	vec3 terrain_color = ring_vertex_color_to_linear(
		debug_color_enabled ? debug_color : COLOR.rgb);
	float grass_coverage_eligibility = 0.0;
	float grass_coverage_upward = 0.0;
	if (biome_texture_weights_enabled && !debug_color_enabled) {
		vec3 world_normal = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
		vec3 blend = pow(abs(world_normal), vec3(8.0));
		blend /= max(blend.x + blend.y + blend.z, 0.0001);
		// Preserve dirt/rock readability on skirts and steep blocky sides. The
		// gradual mask also gives coarse triangle slopes a stable material blend.
		float ground_facing = smoothstep(0.45, 0.78, dot(world_normal, normalize(surface_up_world)));
		grass_coverage_upward = ground_facing;
		vec3 texture_position = world_position * biome_texture_scale;
		// Most LOD fragments carry one dominant biome and (at a boundary) one
		// secondary weight. Branches keep distant terrain from paying for all
		// six maps / eighteen triplanar lookups per fragment.
		vec3 dirt = vec3(0.0);
		if (ground_facing < 1.0)
			dirt = sample_biome_triplanar(biome_dirt_albedo, texture_position, world_normal, blend);
		if (ground_facing > 0.0) {
			vec4 packed = clamp(COLOR, 0.0, 1.0);
			float grass_weight = max(0.0, 1.0 - packed.r - packed.g - packed.b - packed.a);
			grass_coverage_eligibility = clamp(grass_weight + packed.g * .55, 0.0, 1.0);
			float total = max(0.0001, grass_weight + packed.r + packed.g + packed.b + packed.a);
			vec3 surface_texture = vec3(0.0);
			if (grass_weight > 0.0) {
				vec3 grass = sample_biome_triplanar(biome_grass_albedo, texture_position, world_normal, blend);
				grass = mix(grass, grass * vec3(0.72, 1.30, 0.62), 0.58);
				surface_texture += grass * grass_weight;
			}
			if (packed.r > 0.0)
				surface_texture += sample_biome_triplanar(biome_desert_albedo, texture_position, world_normal, blend) * packed.r;
			if (packed.g > 0.0)
				surface_texture += sample_biome_triplanar(biome_forest_albedo, texture_position, world_normal, blend) * packed.g;
			if (packed.b > 0.0)
				surface_texture += sample_biome_triplanar(biome_tundra_albedo, texture_position, world_normal, blend) * packed.b;
			if (packed.a > 0.0)
				surface_texture += sample_biome_triplanar(biome_alpine_albedo, texture_position, world_normal, blend) * packed.a;
			terrain_color = mix(dirt, surface_texture / total, ground_facing);
		} else {
			terrain_color = dirt;
		}
	}
	float grass_coverage = ring_terrain_grass_coverage(
		world_position,
		distance_to_camera,
		grass_coverage_upward,
		grass_coverage_eligibility);
	terrain_color = ring_terrain_albedo_scaled(ring_apply_terrain_grass_coverage(terrain_color, grass_coverage));
	// Keep a spatially varying night tint, but leave brightness to Godot's
	// environment ambient light and directional-light pipeline.
	vec3 lit_color = terrain_color * mix(night_tint, vec3(1.0), ring_day_factor);
	RingHaze haze = ring_haze_sample(camera_world_position, world_position, ring_center, ring_day_factor);
	ALBEDO = lit_color * haze.T;
	EMISSION = ring_haze_inscatter(haze);
	// Every water kind now uses a separate transparent surface, leaving this
	// material responsible only for dry and submerged solid terrain.
	ROUGHNESS = mix(roughness_value, max(roughness_value, .90), grass_coverage);
	SPECULAR = 0.18;
	METALLIC = 0.0;
	combined_cloud_shadow_occlusion = sample_combined_cloud_shadow(world_position, ring_day_factor);
	AO = 1.0 - combined_cloud_shadow_occlusion * 0.72;
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
	// Terrain height-shadow clipmap (S5): replaces the sun macro term where it has data; inactive = the previous code path.
	bool height_active = LIGHT_IS_DIRECTIONAL && primary_sun_weight > 0.0 && ring_height_shadow_active();
	RingHeightShadow height_shadow = ring_height_shadow_none();
	if (height_active) {
		height_shadow = ring_height_shadow_sample(
			world_position,
			0.0,
			ring_height_shadow_slope_tan(transpose(mat3(VIEW_MATRIX)) * NORMAL),
			distance(world_position, camera_world_position));
	}
	float terrain_shadow = LIGHT_IS_DIRECTIONAL
		? ring_macro_terrain_occlusion(
			sample_terrain_macro_shadow(world_position, ring_center, camera_world_position, ring_width_meters) * ring_height_shadow_macro_scale(height_shadow),
			sample_terrain_planet_shadow(world_position, ring_center, camera_world_position, ring_width_meters),
			primary_sun_weight,
			primary_planet_weight,
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
	terms.contact = ring_contact_visibility(FRAGCOORD.xy, VIEWPORT_SIZE, INV_PROJECTION_MATRIX, primary_sun_weight, RING_SHADOW_DEFAULT);
	if (height_active) {
		terms.height = ring_height_shadow_term(height_shadow, primary_sun_weight, ring_day_factor, ringworld_hub_light_weight, shadow_floor);
	}
	float combined_visibility = ring_shadow_sun_visibility(terms, RING_SHADOW_DEFAULT);
	if (ring_shadow_debug_enabled()) {
		if (LIGHT_IS_DIRECTIONAL && primary_sun_weight > 0.5) {
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
