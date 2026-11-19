// T6-4b (T6 design A7): the understory carrier shader. Saplings (and later shrubs and dead wood) of every species draw in one batch per LOD: the vertices are fetched by (VERTEX_ID, variant row) from data
// textures like imported_tree_impostor.gdshader and the rocks do (RingworldTechDemo.Vegetation.Understory.UnderstoryCatalog), and the body of vertex(), fragment() and light() is the body of imported_tree.gdshader
// statement for statement, on the same includes, so a sapling here is lit exactly like the same mesh drawn through the tree material (proof: tools/UnderstoryRenderMatchGpuTest.cs).
// Differences, all intended: the surface kind (bark or foliage) and the species layer come per vertex (position.w = species + 4 for foliage) instead of per material; textures are Texture2DArrays; roughness and metallic are
// per-layer constants; the crown hull and inclination come from the variant's parameter rows 8 to 10 instead of instance uniforms; per-vertex haze and shadow have no slot (exact per-vertex path); the canopy-shade raster applies to
// ALL surfaces (the understory's own crowns are not in the raster), and its transmitted light has its own varying beside leaf translucency. Varyings are packed because the original is at the 32-location limit.
shader_type spatial;
render_mode cull_disabled, depth_draw_opaque;

uniform sampler2D vertex_data : filter_nearest;
uniform sampler2D attachment_data : filter_nearest;
uniform sampler2D normal_data : filter_nearest;
uniform sampler2D uv_data : filter_nearest;
uniform sampler2D tangent_data : filter_nearest;
// Per variant (column): row 0 bark tint, 1 foliage tint, 2 (bark roughness, bark metallic, foliage roughness, foliage metallic), 3 (bark normal scale, foliage normal scale, foliage alpha cutoff, 0),
// 4 (bark wind mode, bark attachments, foliage wind mode, foliage attachments), 5 (x: 1 = no wind for this variant), 8 crown hull, 9 hull extra, 10 leaf inclination (the impostor's layout, TreeCrownHullTable.Pack).
uniform sampler2D variant_parameters : filter_nearest;
uniform int fetch_width = 1; // texels per data row; a variant that is longer wraps onto the next row
uniform int fetch_rows_per_variant = 1;
uniform sampler2DArray foliage_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable; // DXT5 with the distance-stable leaf mips (TR-7c)
// TR-7c: the cutout alpha is read isotropically (an anisotropic tap set reads a finer level averaged along one axis, which changes the covered area with the view angle): the same array under a sampler without anisotropy.
uniform sampler2DArray foliage_alpha : filter_linear_mipmap, repeat_enable;
uniform sampler2DArray foliage_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2DArray bark_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2DArray bark_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
// The coarsest mip level the isotropic cutout read may use (the 4 x 4 level, as ImportedTreeLeafMipCatalog.MaxLod).
uniform float leaf_alpha_max_lod = 8.0;
uniform vec3 ring_center = vec3(0.0);
uniform vec3 sun_direction = vec3(1.0, 0.0, 0.0);
uniform float day_start_dot = -0.2;
uniform float day_full_dot = 0.35;
uniform vec3 night_tint = vec3(0.30, 0.45, 0.70);
uniform float night_direct_light_factor : hint_range(0.0, 1.0) = 0.0;
uniform float day_direct_light_factor : hint_range(0.0, 2.0) = 1.0;
uniform bool vegetation_wind_enabled = true;
uniform bool simplified_tree_shading = false;

// Main-viewport only: ray frame comes from global shader parameters.
#define RING_ATMOSPHERE_FRAME_GLOBAL
#include "res://materials/ring_cloud_shadow.gdshaderinc"
#include "res://materials/ring_shadow_compose.gdshaderinc"
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_vegetation_haze.gdshaderinc"
#include "res://materials/ring_vegetation_shadow.gdshaderinc"
#include "res://materials/ring_local_wind.gdshaderinc"
#include "res://materials/ring_wetness.gdshaderinc"
#include "res://materials/ring_forest_lighting.gdshaderinc"
#include "res://materials/ring_canopy_shade.gdshaderinc"
#include "res://materials/ring_foliage_tint.gdshaderinc"

varying vec3 tree_world_position;
varying vec3 tree_haze_T;
varying vec3 tree_haze_S;
varying vec2 tree_wet_state; // wet world, per vertex: (darkening amount, gloss)
varying vec3 tree_shading_terms; // per vertex: (cloud occlusion, day factor, height-shadow term)
varying vec3 tree_fragment_terms; // fragment() to light(): (leaf translucency transmit, highlight roughness, canopy transmit): k_species * facing * crown transmittance towards the primary sun (0 = none); the wet world's direct highlight keeps the full wet gloss; blocked * species factor of the canopy raster above (0 = none)
varying vec3 tree_model_position; // forest lighting switches on: the vertex in model (normalized tree) space, after wind
varying flat vec3 tree_model_light; // forest lighting switches on: the unit direction to the sun in model space (vertex(): MODEL_MATRIX in fragment() lacks the MultiMesh instance transform)
varying flat vec3 tree_model_camera; // forest lighting switches on: the camera in model space
varying flat vec4 tree_flat; // (dither coverage, dither seed, foliage tint value factor, foliage tint hue term); the tint is written only while ring_foliage_tint_on()
varying flat vec4 tree_surface; // (variant row, 1 = foliage surface, species layer, 0)

float tree_dither_noise(vec2 pixel, float seed) {
	return fract(sin(dot(pixel + vec2(seed * 17.0, seed * 43.0), vec2(12.9898, 78.233))) * 43758.5453);
}

void apply_tree_dither(vec2 fragment_coordinate) {
	float coverage = tree_flat.x;
	if (coverage >= 1.0 || coverage <= -1.0) return;
	float noise = tree_dither_noise(floor(fragment_coordinate), tree_flat.y);
	// Positive coverage is an incoming LOD. Negative coverage is an outgoing
	// LOD whose complement uses the identical screen/root hash.
	if (coverage >= 0.0) {
		if (noise >= coverage) discard;
	} else if (noise < 1.0 + coverage) discard;
}

void vertex() {
	int variant = clamp(int(round(INSTANCE_CUSTOM.x)), 0, 31);
	float dither_coverage = abs(INSTANCE_CUSTOM.w - 2.0) <= 0.25 ? INSTANCE_CUSTOM.y : 1.0;
	ivec2 texel = ivec2(VERTEX_ID % fetch_width, VERTEX_ID / fetch_width + variant * fetch_rows_per_variant);
	vec4 packed_vertex = texelFetch(vertex_data, texel, 0);
	bool foliage = packed_vertex.w >= 3.5;
	float layer = packed_vertex.w - (foliage ? 4.0 : 0.0);
	tree_surface = vec4(float(variant), foliage ? 1.0 : 0.0, layer, 0.0);
	VERTEX = packed_vertex.xyz;
	NORMAL = normalize(texelFetch(normal_data, texel, 0).xyz);
	vec4 packed_uv = texelFetch(uv_data, texel, 0);
	UV = packed_uv.xy;
	UV2 = packed_uv.zw;
	vec4 packed_tangent = texelFetch(tangent_data, texel, 0);
	TANGENT = normalize(packed_tangent.xyz);
	BINORMAL = normalize(cross(NORMAL, TANGENT)) * packed_tangent.w;
	vec3 root_world = MODEL_MATRIX[3].xyz;
	vec2 foliage_tint = vec2(1.0, 0.0);
	if (foliage && ring_foliage_tint_on()) foliage_tint = ring_foliage_tint_terms(root_world, INSTANCE_CUSTOM.z);
	tree_flat = vec4(dither_coverage, INSTANCE_CUSTOM.z, foliage_tint);
	// Row 5 x = 1: this variant does not sway (T6-6: fallen trees and stumps; the wind bends along model +Y, which is wrong for a lying log). Zero for every other variant.
	if (vegetation_wind_enabled && texelFetch(variant_parameters, ivec2(variant, 5), 0).x < 0.5) {
		mat3 basis = mat3(MODEL_MATRIX);
		vec3 offset; mat3 jacobian;
		vec4 wind_terms = texelFetch(variant_parameters, ivec2(variant, 4), 0);
		ring_tree_wind_geometry(root_world, VERTEX, basis, texelFetch(attachment_data, texel, 0), int(round(foliage ? wind_terms.z : wind_terms.x)), (foliage ? wind_terms.w : wind_terms.y) > 0.5, offset, jacobian);
		VERTEX += offset;
		NORMAL = ring_tree_deformed_normal(jacobian, NORMAL);
		if (length(TANGENT) > .0001) TANGENT = normalize(jacobian * TANGENT);
		if (length(BINORMAL) > .0001) BINORMAL = normalize(jacobian * BINORMAL);
	}
	if (ring_forest_translucency_on() || ring_forest_crown_on() || ring_canopy_shade_on()) {
		tree_model_position = VERTEX; // the wind-deformed vertex in model space (normalized tree space, uniform scale)
		tree_model_camera = ring_crown_model_point(MODEL_MATRIX, ring_atmosphere_camera_world_position);
		tree_model_light = normalize(transpose(mat3(MODEL_MATRIX)) * sun_direction);
	}
	vec3 world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	RingVegetationShadowInstance shadow_instance = ring_vegetation_shadow_instance(RING_VEGETATION_SHADOW_TREES, INSTANCE_ID);
	float cloud_occlusion = ring_vegetation_shadow_cloud_vertex(shadow_instance, world_position);
	vec3 radial = world_position - ring_center; radial.y = 0.0;
	vec3 surface_up = length(radial) > 0.0001 ? -normalize(radial) : vec3(-1.0, 0.0, 0.0);
	float day_factor = ringworld_effective_solar_day(smoothstep(day_start_dot, day_full_dot, dot(surface_up, normalize(sun_direction))));
	tree_world_position = world_position;
	// Terrain height-shadow clipmap (S5), per vertex like the cloud term: 1 when inactive. Applied to the primary sun in light().
	float height_term = ring_vegetation_shadow_height_vertex(shadow_instance, world_position, root_world, surface_up, ring_atmosphere_camera_world_position);
	tree_shading_terms = vec3(cloud_occlusion, day_factor, height_term);
	// No haze slot (the instance uniform stays at -1): the exact per-vertex ring_haze_sample.
	RingHaze vertex_haze = ring_vegetation_haze_sample(RING_VEGETATION_HAZE_TREES, INSTANCE_ID,
		ring_atmosphere_camera_world_position, world_position, ring_center, day_factor);
	tree_haze_T = vertex_haze.T;
	tree_haze_S = ring_haze_inscatter(vertex_haze);
	tree_wet_state = vec2(0.0);
	if (ring_wet_params.w > 0.5) {
		RingWet wet_state = ring_wet_sample_vegetation(world_position);
		tree_wet_state = vec2(smoothstep(0.0, 0.5, wet_state.soak) * ring_wet_params.x, ring_wet_gloss(wet_state));
	}
}

void fragment() {
	apply_tree_dither(FRAGCOORD.xy);
	int variant = int(tree_surface.x);
	bool foliage = tree_surface.y > 0.5;
	vec3 array_uv = vec3(UV, tree_surface.z);
	float cloud_occlusion = tree_shading_terms.x;
	float day_factor = tree_shading_terms.y;
	vec4 albedo;
	vec4 surface_tint = texelFetch(variant_parameters, ivec2(variant, foliage ? 1 : 0), 0);
	vec4 surface_scale = texelFetch(variant_parameters, ivec2(variant, 3), 0);
	if (foliage) {
		albedo = texture(foliage_albedo, array_uv) * surface_tint;
		float leaf_alpha_lod = min(textureQueryLod(foliage_alpha, UV).y, leaf_alpha_max_lod);
		ALPHA = textureLod(foliage_alpha, array_uv, leaf_alpha_lod).a * surface_tint.a;
		ALPHA_SCISSOR_THRESHOLD = surface_scale.z;
	} else {
		albedo = texture(bark_albedo, array_uv) * surface_tint;
	}
	if (foliage && ring_foliage_tint_on()) albedo.rgb *= ring_foliage_tint(tree_flat.zw); // T6-8: per-instance and per-stand foliage colour, before the lighting and haze factors
	ALBEDO = albedo.rgb * mix(night_tint, vec3(1.0), day_factor) * (1.0 - cloud_occlusion * day_factor * 0.34);
	ALBEDO *= tree_haze_T;
	ALBEDO *= 1.0 - 0.55 * 0.5 * min(tree_wet_state.x, 1.0); // wet world: bark and foliage darken half as much as soil
	vec3 haze_emission = tree_haze_S;
	if (atmosphere_haze_model == 0) {
		// Legacy: scattering colour from the fragment position, opacity from the vertex.
		vec4 atmosphere = sample_ring_atmosphere(ring_atmosphere_camera_world_position,
			tree_world_position, ring_center, day_factor);
		haze_emission = atmosphere.rgb * (1.0 - tree_haze_T.g) * atmosphere_scattering_energy;
	}
	EMISSION += haze_emission;
	vec4 material_terms = texelFetch(variant_parameters, ivec2(variant, 2), 0);
	if (simplified_tree_shading) {
		METALLIC = 0.0;
		ROUGHNESS = 0.9;
	} else {
		if (foliage) { NORMAL_MAP = texture(foliage_normal, array_uv).rgb; NORMAL_MAP_DEPTH = surface_scale.y; }
		else { NORMAL_MAP = texture(bark_normal, array_uv).rgb; NORMAL_MAP_DEPTH = surface_scale.x; }
		METALLIC = foliage ? material_terms.w : material_terms.y;
		ROUGHNESS = foliage ? material_terms.z : material_terms.x;
	}
	float dry_roughness = ROUGHNESS;
	ROUGHNESS *= mix(1.0, 0.6, tree_wet_state.y); // wet world: glossier when wet
	float highlight_roughness = ROUGHNESS;
	// Wet leaves stay above the SSR cut (bark keeps its full wet gloss and its SSR sheen).
	if (foliage) ROUGHNESS = ring_wet_ssr_roughness(ROUGHNESS, dry_roughness);
	AO = min(1.0 - cloud_occlusion * day_factor * .72, ring_storm_ambient(ring_storm_level(tree_world_position))); // storm light
	AO_LIGHT_AFFECT = 0.0;
	float leaf_transmit = 0.0;
	float canopy_transmit = 0.0;
	if (ring_forest_translucency_on() || ring_forest_crown_on()) {
		// Forest lighting: the statistical crown model shared with the impostor (docs/forest-lighting-plan.md sections 3 and 4): first leaf on the view ray, the variant's leaf inclination distribution.
		// Crown sky occlusion applies to foliage and to bark at or above the crown base (branches inside the crown see the same sky; the impostor's gate); translucency to foliage only.
		vec4 tree_crown_hull = texelFetch(variant_parameters, ivec2(variant, 8), 0);
		vec4 tree_crown_hull_extra = texelFetch(variant_parameters, ivec2(variant, 9), 0);
		if (ring_crown_valid(tree_crown_hull_extra) && (foliage || (ring_forest_crown_on() && tree_model_position.y >= tree_crown_hull.x - tree_crown_hull.z))) {
			float analytic_weight = ring_forest_analytic_weight(distance(tree_world_position, ring_atmosphere_camera_world_position));
			float crown_sky, crown_sun;
			ring_crown_first_leaf_terms(tree_model_camera, tree_model_position, tree_model_light, tree_crown_hull, tree_crown_hull_extra, foliage && ring_forest_translucency_on() && analytic_weight > 0.0, crown_sky, crown_sun);
			if (ring_forest_crown_on()) AO *= crown_sky;
			if (foliage && ring_forest_translucency_on()) {
				leaf_transmit = tree_crown_hull_extra.z * (ring_leaf_lad_facing(tree_model_light, normalize(tree_model_camera - tree_model_position), vec3(0.0, 1.0, 0.0), texelFetch(variant_parameters, ivec2(variant, 10), 0))) * mix(1.0, crown_sun, analytic_weight);
			}
		}
	}
	if (ring_canopy_shade_on()) {
		// Forest lighting FL-3a on every surface of the understory: its own crowns are not in the canopy raster, so there is no double count (a canopy tree skips the raster at or above its crown base for that reason).
		vec2 canopy = ring_canopy_sample(tree_world_position);
		AO *= ring_canopy_sky_visibility(canopy);
		canopy_transmit = canopy.x * canopy.y;
	}
	tree_fragment_terms = vec3(leaf_transmit, highlight_roughness, canopy_transmit);
	// Reflections R1-1a hold: the sky term stays black as before the sky source; crown interiors and trunks cannot see the sky and there is no crown-visibility term yet (docs/work-orders/reflections/R1-1-design.md). SSR on wet bark is unaffected.
	RADIANCE = vec4(0.0, 0.0, 0.0, 1.0);
	if (ring_shadow_debug_enabled()) {
		// Shadow debug view: only the selected shadow term reaches the screen (see ring_shadow_compose.gdshaderinc).
		ALBEDO = vec3(1.0);
		EMISSION = vec3(0.0);
		AO = 0.0;
		SPECULAR = 0.0;
		METALLIC = 0.0;
		ROUGHNESS = 1.0;
		tree_fragment_terms.y = 1.0;
	}
}

void light() {
	float cloud_occlusion = tree_shading_terms.x;
	float day_factor = tree_shading_terms.y;
	float height_term = tree_shading_terms.z;
	bool foliage = tree_surface.y > 0.5;
	float n_dot_l = max(dot(NORMAL, LIGHT), 0.0);
	vec3 sun_view = normalize((VIEW_MATRIX * vec4(sun_direction, 0.0)).xyz);
	float primary_sun = LIGHT_IS_DIRECTIONAL ? smoothstep(.90, .995, dot(normalize(LIGHT), sun_view)) : 0.0;
	float direct = mix(night_direct_light_factor, day_direct_light_factor, day_factor);
	float cloud = LIGHT_IS_DIRECTIONAL ? 1.0 - cloud_occlusion * day_factor : 1.0;
	// Vegetation keeps its legacy product (geometry * cloud, no macro/terrain term): COMBINE_PRODUCT.
	RingShadowTerms terms = ring_shadow_terms(tree_world_position, SCREEN_UV, ATTENUATION, 0.0, 0.0, RING_SHADOW_COMBINE_PRODUCT);
	terms.storm = LIGHT_IS_DIRECTIONAL ? terms.storm : 0.0; // storm light fades the sun only, never a point or spot light
	terms.cloud = mix(1.0, cloud, primary_sun);
	terms.geometry = ring_height_shadow_vegetation_geometry(terms.geometry, height_term, LIGHT_IS_DIRECTIONAL, primary_sun, day_factor);
	terms.geometry = ring_height_shadow_vegetation_planet_geometry(terms.geometry, height_term, LIGHT_IS_DIRECTIONAL, LIGHT, VIEW_MATRIX, primary_sun);
	float sun_visibility = ring_shadow_sun_visibility(terms, RING_SHADOW_COMBINE_PRODUCT);
	if (ring_shadow_debug_enabled()) {
		if (LIGHT_IS_DIRECTIONAL && primary_sun > 0.5) {
			DIFFUSE_LIGHT += vec3(ring_shadow_debug_value(terms, sun_visibility));
		}
	} else {
		float visibility = sun_visibility * mix(1.0, direct, primary_sun);
		DIFFUSE_LIGHT += (LIGHT_COLOR / PI) * n_dot_l * visibility;
		if (ring_canopy_shade_on() && tree_fragment_terms.z > 0.0 && primary_sun > 0.0) {
			// Forest lighting FL-3a: sunlight re-emitted by the canopy above (tree_fragment_terms.z = blocked * species factor); above = cloud and storm only (geometry 1).
			DIFFUSE_LIGHT += ring_canopy_transmitted(LIGHT_COLOR, LIGHT, NORMAL, ring_canopy_up_view(VIEW_MATRIX), vec2(tree_fragment_terms.z, 1.0),
				ring_canopy_above(terms, RING_SHADOW_COMBINE_PRODUCT) * mix(1.0, direct, primary_sun) * primary_sun);
		}
		if (ring_forest_translucency_on() && foliage && tree_fragment_terms.x > 0.0) {
			// Leaf translucency: sunlight transmitted to the viewer's side of the leaf, primary sun only (docs/forest-lighting-plan.md section 3). Godot multiplies by ALBEDO afterwards.
			DIFFUSE_LIGHT += (LIGHT_COLOR / PI) * (tree_fragment_terms.x * RING_LEAF_TRANSMISSION_SHAPE) * visibility * primary_sun;
		}
		if (!simplified_tree_shading) {
			vec3 half_vector = normalize(LIGHT + VIEW);
			float gloss = mix(8.0, 128.0, 1.0 - tree_fragment_terms.y);
			float specular = pow(max(dot(NORMAL, half_vector), 0.0), gloss) * mix(mix(.04, .1, tree_wet_state.y), 1.0, METALLIC);
			SPECULAR_LIGHT += (LIGHT_COLOR / PI) * specular * n_dot_l * visibility * tree_haze_T;
		}
	}
}
