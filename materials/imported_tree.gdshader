shader_type spatial;
render_mode cull_disabled, depth_draw_opaque;

uniform sampler2D albedo_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D normal_texture : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D metallic_texture : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D roughness_texture : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_metallic_texture = false;
uniform bool has_roughness_texture = false;
uniform bool has_normal_texture = false;
uniform float normal_scale : hint_range(0.0, 4.0) = 1.0;
uniform vec4 albedo_tint : source_color = vec4(1.0);
uniform float metallic_factor : hint_range(0.0, 1.0) = 0.0;
uniform float roughness_factor : hint_range(0.0, 1.0) = 1.0;
uniform float alpha_cutoff : hint_range(0.0, 1.0) = 0.25;
uniform bool use_alpha_cutout = false;
uniform bool use_uv2_for_metallic_roughness = false;
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
// GPU tree rows opt into the transition protocol with INSTANCE_CUSTOM.w == 2.
// y is signed coverage and z is a stable per-root seed.  Older CPU rows retain
// their original fully-visible behavior.
uniform bool simplified_tree_shading = false;

#include "res://materials/ring_cloud_shadow.gdshaderinc"
#include "res://materials/ring_shadow_compose.gdshaderinc"
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_vegetation_haze.gdshaderinc"
#include "res://materials/ring_local_wind.gdshaderinc"

varying float tree_cloud_occlusion;
varying float tree_day_factor;
varying float tree_height_term;
varying vec3 tree_world_position;
varying vec3 tree_haze_T;
varying vec3 tree_haze_S;
varying flat float tree_dither_coverage;
varying flat float tree_dither_seed;

float tree_dither_noise(vec2 pixel, float seed) {
	return fract(sin(dot(pixel + vec2(seed * 17.0, seed * 43.0), vec2(12.9898, 78.233))) * 43758.5453);
}

void apply_tree_dither(vec2 fragment_coordinate) {
	if (tree_dither_coverage >= 1.0 || tree_dither_coverage <= -1.0) return;
	float noise = tree_dither_noise(floor(fragment_coordinate), tree_dither_seed);
	// Positive coverage is an incoming LOD. Negative coverage is an outgoing
	// LOD whose complement uses the identical screen/root hash.
	if (tree_dither_coverage >= 0.0) {
		if (noise >= tree_dither_coverage) discard;
	} else if (noise < 1.0 + tree_dither_coverage) discard;
}

void vertex() {
	tree_dither_coverage = abs(INSTANCE_CUSTOM.w - 2.0) <= 0.25 ? INSTANCE_CUSTOM.y : 1.0;
	tree_dither_seed = INSTANCE_CUSTOM.z;
	vec3 root_world = MODEL_MATRIX[3].xyz;
	if (vegetation_wind_enabled) {
		mat3 basis = mat3(MODEL_MATRIX);
		vec3 offset; mat3 jacobian;
		ring_tree_wind_geometry(root_world, VERTEX, basis, CUSTOM0, tree_foliage_mode, tree_foliage_attachments, offset, jacobian);
		VERTEX += offset;
		NORMAL = ring_tree_deformed_normal(jacobian, NORMAL);
		if (length(TANGENT) > .0001) TANGENT = normalize(jacobian * TANGENT);
		if (length(BINORMAL) > .0001) BINORMAL = normalize(jacobian * BINORMAL);
	}
	vec3 world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	tree_cloud_occlusion = sample_local_cloud_shadow(world_position);
	vec3 radial = world_position - ring_center; radial.y = 0.0;
	vec3 surface_up = length(radial) > 0.0001 ? -normalize(radial) : vec3(-1.0, 0.0, 0.0);
	tree_day_factor = ringworld_effective_solar_day(smoothstep(day_start_dot, day_full_dot, dot(surface_up, normalize(sun_direction))));
	tree_world_position = world_position;
	// Terrain height-shadow clipmap (S5), per vertex like the cloud term: 1 when inactive. Applied to the primary sun in light().
	tree_height_term = ring_height_shadow_vertex_term(world_position, root_world, surface_up, ring_atmosphere_camera_world_position);
	RingHaze vertex_haze = ring_vegetation_haze_sample(RING_VEGETATION_HAZE_TREES, INSTANCE_ID,
		ring_atmosphere_camera_world_position, world_position, ring_center, tree_day_factor);
	tree_haze_T = vertex_haze.T;
	tree_haze_S = ring_haze_inscatter(vertex_haze);
}

void fragment() {
	apply_tree_dither(FRAGCOORD.xy);
	vec4 albedo = texture(albedo_texture, UV) * albedo_tint;
	if (use_alpha_cutout && albedo.a < alpha_cutoff) discard;
	ALBEDO = albedo.rgb * mix(night_tint, vec3(1.0), tree_day_factor) * (1.0 - tree_cloud_occlusion * tree_day_factor * 0.34);
	ALBEDO *= tree_haze_T;
	vec3 haze_emission = tree_haze_S;
	if (atmosphere_haze_model == 0) {
		// Legacy: scattering colour from the fragment position, opacity from the vertex.
		vec4 atmosphere = sample_ring_atmosphere(ring_atmosphere_camera_world_position,
			tree_world_position, ring_center, tree_day_factor);
		haze_emission = atmosphere.rgb * (1.0 - tree_haze_T.g) * atmosphere_scattering_energy;
	}
	EMISSION += haze_emission;
	if (simplified_tree_shading) {
		METALLIC = 0.0;
		ROUGHNESS = 0.9;
	} else {
		vec2 material_uv = use_uv2_for_metallic_roughness ? UV2 : UV;
		float metallic = has_metallic_texture ? texture(metallic_texture, material_uv).b : 1.0;
		float roughness = has_roughness_texture ? texture(roughness_texture, material_uv).g : 1.0;
		if (has_normal_texture) { NORMAL_MAP = texture(normal_texture, UV).rgb; NORMAL_MAP_DEPTH = normal_scale; }
		METALLIC = metallic * metallic_factor;
		ROUGHNESS = roughness * roughness_factor;
	}
	AO = 1.0 - tree_cloud_occlusion * tree_day_factor * .72;
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
	float n_dot_l = max(dot(NORMAL, LIGHT), 0.0);
	vec3 sun_view = normalize((VIEW_MATRIX * vec4(sun_direction, 0.0)).xyz);
	float primary_sun = LIGHT_IS_DIRECTIONAL ? smoothstep(.90, .995, dot(normalize(LIGHT), sun_view)) : 0.0;
	float direct = mix(night_direct_light_factor, day_direct_light_factor, tree_day_factor);
	float cloud = LIGHT_IS_DIRECTIONAL ? 1.0 - tree_cloud_occlusion * tree_day_factor : 1.0;
	// Vegetation keeps its legacy product (geometry * cloud, no macro/terrain term): COMBINE_PRODUCT.
	RingShadowTerms terms = ring_shadow_terms(tree_world_position, SCREEN_UV, ATTENUATION, 0.0, 0.0, RING_SHADOW_COMBINE_PRODUCT);
	terms.cloud = mix(1.0, cloud, primary_sun);
	terms.geometry = ring_height_shadow_vegetation_geometry(terms.geometry, tree_height_term, LIGHT_IS_DIRECTIONAL, primary_sun, tree_day_factor);
	float sun_visibility = ring_shadow_sun_visibility(terms, RING_SHADOW_COMBINE_PRODUCT);
	if (ring_shadow_debug_enabled()) {
		if (LIGHT_IS_DIRECTIONAL && primary_sun > 0.5) {
			DIFFUSE_LIGHT += vec3(ring_shadow_debug_value(terms, sun_visibility));
		}
	} else {
		float visibility = sun_visibility * mix(1.0, direct, primary_sun);
		DIFFUSE_LIGHT += (LIGHT_COLOR / PI) * n_dot_l * visibility;
		if (!simplified_tree_shading) {
			vec3 half_vector = normalize(LIGHT + VIEW);
			float gloss = mix(8.0, 128.0, 1.0 - ROUGHNESS);
			float specular = pow(max(dot(NORMAL, half_vector), 0.0), gloss) * mix(.04, 1.0, METALLIC);
			SPECULAR_LIGHT += (LIGHT_COLOR / PI) * specular * n_dot_l * visibility * tree_haze_T;
		}
	}
}
