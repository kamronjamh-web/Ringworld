shader_type spatial;
render_mode cull_disabled, depth_draw_opaque;

uniform sampler2D vertex_data : filter_nearest;
uniform sampler2D normal_data : filter_nearest;
uniform sampler2D uv_data : filter_nearest;
uniform sampler2D tangent_data : filter_nearest;
uniform sampler2D variant_parameters : filter_nearest;
uniform sampler2DArray albedo_layers : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2DArray normal_layers : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2DArray metallic_roughness_layers : filter_linear_mipmap_anisotropic, repeat_enable;
uniform vec3 ring_center = vec3(0.0);
uniform vec3 sun_direction = vec3(1.0, 0.0, 0.0);
uniform float day_start_dot = -0.2;
uniform float day_full_dot = 0.35;
uniform vec3 night_tint = vec3(0.30, 0.45, 0.70);
uniform float night_direct_light_factor : hint_range(0.0, 1.0) = 0.0;
uniform float day_direct_light_factor : hint_range(0.0, 2.0) = 1.0;
uniform bool vegetation_wind_enabled = true;
uniform bool simplified_tree_shading = false;
#include "res://materials/ring_cloud_shadow.gdshaderinc"
#include "res://materials/celestial_lighting.gdshaderinc"
#include "res://materials/ring_local_wind.gdshaderinc"
varying float tree_cloud_occlusion;
varying float tree_day_factor;
varying flat int tree_variant;
varying vec4 tree_parameters;
varying flat float tree_dither_coverage;
varying flat float tree_dither_seed;

float tree_dither_noise(vec2 pixel, float seed) {
	return fract(sin(dot(pixel + vec2(seed * 17.0, seed * 43.0), vec2(12.9898, 78.233))) * 43758.5453);
}

void apply_tree_dither(vec2 fragment_coordinate) {
	if (tree_dither_coverage >= 1.0 || tree_dither_coverage <= -1.0) return;
	float noise = tree_dither_noise(floor(fragment_coordinate), tree_dither_seed);
	if (tree_dither_coverage >= 0.0) {
		if (noise >= tree_dither_coverage) discard;
	} else if (noise < 1.0 + tree_dither_coverage) discard;
}

void vertex() {
	tree_dither_coverage = abs(INSTANCE_CUSTOM.w - 2.0) <= 0.25 ? INSTANCE_CUSTOM.y : 1.0;
	tree_dither_seed = INSTANCE_CUSTOM.z;
	int variant = clamp(int(round(INSTANCE_CUSTOM.x)), 0, 29);
	tree_variant = variant;
	vec4 packed_vertex = texelFetch(vertex_data, ivec2(VERTEX_ID, variant), 0);
	VERTEX = packed_vertex.xyz;
	NORMAL = normalize(texelFetch(normal_data, ivec2(VERTEX_ID, variant), 0).xyz);
	vec4 packed_uv = texelFetch(uv_data, ivec2(VERTEX_ID, variant), 0);
	UV = packed_uv.xy;
	UV2 = packed_uv.zw;
	vec4 packed_tangent = texelFetch(tangent_data, ivec2(VERTEX_ID, variant), 0);
	TANGENT = normalize(packed_tangent.xyz);
	BINORMAL = normalize(cross(NORMAL, TANGENT)) * packed_tangent.w;
	tree_parameters = texelFetch(variant_parameters, ivec2(variant, 1), 0);
	vec3 root_world = MODEL_MATRIX[3].xyz;
	if (vegetation_wind_enabled) {
		float root_weight = clamp(VERTEX.y, 0.0, 1.0);
		float instance_height = length(MODEL_MATRIX[1]);
		vec3 displacement = ring_local_wind_displacement(root_world, root_weight, instance_height);
		mat3 basis = mat3(MODEL_MATRIX);
		vec3 squared_lengths = max(vec3(0.000001), vec3(dot(basis[0], basis[0]), dot(basis[1], basis[1]), dot(basis[2], basis[2])));
		VERTEX += transpose(basis) * displacement / squared_lengths;
	}
	vec3 world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	tree_cloud_occlusion = sample_local_cloud_shadow(world_position);
	vec3 radial = world_position - ring_center;
	radial.y = 0.0;
	vec3 surface_up = length(radial) > 0.0001 ? -normalize(radial) : vec3(-1.0, 0.0, 0.0);
	tree_day_factor = ringworld_effective_solar_day(smoothstep(day_start_dot, day_full_dot, dot(surface_up, normalize(sun_direction))));
}
void fragment() {
	apply_tree_dither(FRAGCOORD.xy);
	vec4 albedo = texture(albedo_layers, vec3(UV, float(tree_variant))) * texelFetch(variant_parameters, ivec2(tree_variant, 0), 0);
	vec4 flags = texelFetch(variant_parameters, ivec2(tree_variant, 2), 0);
	if (flags.r > .5 && albedo.a < tree_parameters.a) discard;
	ALBEDO = albedo.rgb * mix(night_tint, vec3(1.0), tree_day_factor) * (1.0 - tree_cloud_occlusion * tree_day_factor * .34);
	if (simplified_tree_shading) {
		METALLIC = 0.0;
		ROUGHNESS = 0.9;
	} else {
		vec4 mr = texture(metallic_roughness_layers, vec3(UV, float(tree_variant)));
		NORMAL_MAP = texture(normal_layers, vec3(UV, float(tree_variant))).rgb;
		NORMAL_MAP_DEPTH = tree_parameters.r;
		METALLIC = mr.b * tree_parameters.g;
		ROUGHNESS = mr.g * tree_parameters.b;
	}
	AO = 1.0 - tree_cloud_occlusion * tree_day_factor * .72;
	AO_LIGHT_AFFECT = 0.0;
}

void light() {
	float n_dot_l = max(dot(NORMAL, LIGHT), 0.0);
	vec3 sun_view = normalize((VIEW_MATRIX * vec4(sun_direction, 0.0)).xyz);
	float primary_sun = LIGHT_IS_DIRECTIONAL ? smoothstep(.90, .995, dot(normalize(LIGHT), sun_view)) : 0.0;
	float direct = mix(night_direct_light_factor, day_direct_light_factor, tree_day_factor);
	float cloud = LIGHT_IS_DIRECTIONAL ? 1.0 - tree_cloud_occlusion * tree_day_factor : 1.0;
	float visibility = ATTENUATION * mix(1.0, direct, primary_sun) * mix(1.0, cloud, primary_sun);
	DIFFUSE_LIGHT += (LIGHT_COLOR / PI) * n_dot_l * visibility;
	if (!simplified_tree_shading) {
		vec3 half_vector = normalize(LIGHT + VIEW);
		float gloss = mix(8.0, 128.0, 1.0 - ROUGHNESS);
		float specular = pow(max(dot(NORMAL, half_vector), 0.0), gloss) * mix(.04, 1.0, METALLIC);
		SPECULAR_LIGHT += (LIGHT_COLOR / PI) * specular * n_dot_l * visibility;
	}
}
