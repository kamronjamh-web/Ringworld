shader_type spatial;
render_mode cull_disabled, depth_draw_opaque;

uniform sampler2D albedo_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
// TR-7c: the cutout alpha of the distance-stable leaf mips is read isotropically (an anisotropic tap set reads a finer level averaged along one axis, which changes the covered area with the view angle). The loader sets both with the texture; off elsewhere.
uniform sampler2D leaf_alpha_texture : filter_linear_mipmap, repeat_enable;
uniform bool leaf_alpha_isotropic = false;
// The coarsest mip level the isotropic cutout read may use. The loader sets the 4 x 4 level: coarser levels cannot carry the covered area, and a card seen at an angle reaches them first.
uniform float leaf_alpha_max_lod = 16.0;
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

// Forest lighting: the crown hull of this renderer's variant (one variant per renderer), see ring_forest_lighting.gdshaderinc. Unset = no crown terms, no translucency.
instance uniform vec4 tree_crown_hull = vec4(0.0);
instance uniform vec4 tree_crown_hull_extra = vec4(0.0);
instance uniform vec4 tree_crown_lad = vec4(0.0); // leaf area fractions of the 4 inclination classes (see the include)

varying float tree_cloud_occlusion;
varying float tree_day_factor;
varying float tree_height_term;
varying vec3 tree_world_position;
varying vec3 tree_haze_T;
varying vec3 tree_haze_S;
varying vec2 tree_wet_state; // wet world, per vertex: (darkening amount, gloss)
varying float tree_highlight_roughness; // wet world: the direct highlight keeps the full wet gloss (ROUGHNESS may be held at the SSR floor)
varying flat float tree_dither_coverage;
varying flat float tree_dither_seed;
varying vec3 tree_model_position; // forest lighting switches on: the vertex in model (normalized tree) space, after wind
varying float tree_leaf_transmit; // written in fragment() for light(): k_species * facing * crown transmittance towards the primary sun (0 = no translucency); one varying because the shader is at the 32-varying limit. Bark below the crown base (no translucency) reuses it for the canopy shade: blocked * species factor (FL-3a)
varying flat vec3 tree_model_light; // forest lighting switches on: the unit direction to the sun in model space (vertex(): MODEL_MATRIX in fragment() lacks the MultiMesh instance transform)
varying flat vec3 tree_model_camera; // forest lighting switches on: the camera in model space

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
	if (ring_forest_translucency_on() || ring_forest_crown_on() || ring_canopy_shade_on()) {
		tree_model_position = VERTEX; // the wind-deformed vertex in model space (normalized tree space, uniform scale)
		tree_model_camera = ring_crown_model_point(MODEL_MATRIX, ring_atmosphere_camera_world_position);
		tree_model_light = normalize(transpose(mat3(MODEL_MATRIX)) * sun_direction);
	}
	vec3 world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	RingVegetationShadowInstance shadow_instance = ring_vegetation_shadow_instance(RING_VEGETATION_SHADOW_TREES, INSTANCE_ID);
	tree_cloud_occlusion = ring_vegetation_shadow_cloud_vertex(shadow_instance, world_position);
	vec3 radial = world_position - ring_center; radial.y = 0.0;
	vec3 surface_up = length(radial) > 0.0001 ? -normalize(radial) : vec3(-1.0, 0.0, 0.0);
	tree_day_factor = ringworld_effective_solar_day(smoothstep(day_start_dot, day_full_dot, dot(surface_up, normalize(sun_direction))));
	tree_world_position = world_position;
	// Terrain height-shadow clipmap (S5), per vertex like the cloud term: 1 when inactive. Applied to the primary sun in light().
	tree_height_term = ring_vegetation_shadow_height_vertex(shadow_instance, world_position, root_world, surface_up, ring_atmosphere_camera_world_position);
	RingHaze vertex_haze = ring_vegetation_haze_sample(RING_VEGETATION_HAZE_TREES, INSTANCE_ID,
		ring_atmosphere_camera_world_position, world_position, ring_center, tree_day_factor);
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
	vec4 albedo = texture(albedo_texture, UV) * albedo_tint;
	if (use_alpha_cutout) {
		if (leaf_alpha_isotropic) {
			float leaf_alpha_lod = min(textureQueryLod(leaf_alpha_texture, UV).y, leaf_alpha_max_lod);
			ALPHA = textureLod(leaf_alpha_texture, UV, leaf_alpha_lod).a * albedo_tint.a;
		} else {
			ALPHA = albedo.a;
		}
		ALPHA_SCISSOR_THRESHOLD = alpha_cutoff;
	}
	ALBEDO = albedo.rgb * mix(night_tint, vec3(1.0), tree_day_factor) * (1.0 - tree_cloud_occlusion * tree_day_factor * 0.34);
	ALBEDO *= tree_haze_T;
	ALBEDO *= 1.0 - 0.55 * 0.5 * min(tree_wet_state.x, 1.0); // wet world: bark and foliage darken half as much as soil
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
	float dry_roughness = ROUGHNESS;
	ROUGHNESS *= mix(1.0, 0.6, tree_wet_state.y); // wet world: glossier when wet
	tree_highlight_roughness = ROUGHNESS;
	// Wet leaves stay above the SSR cut (bark keeps its full wet gloss and its SSR sheen).
	if (use_alpha_cutout) ROUGHNESS = ring_wet_ssr_roughness(ROUGHNESS, dry_roughness);
	AO = min(1.0 - tree_cloud_occlusion * tree_day_factor * .72, ring_storm_ambient(ring_storm_level(tree_world_position))); // storm light
	AO_LIGHT_AFFECT = 0.0;
	if (ring_forest_translucency_on() || ring_forest_crown_on()) {
		// Forest lighting: the statistical crown model shared with the impostor (docs/forest-lighting-plan.md sections 3 and 4): first leaf on the view ray, the variant's leaf inclination distribution.
		// Crown sky occlusion applies to foliage and to bark at or above the crown base (branches inside the crown see the same sky; the impostor's gate); translucency to foliage only.
		tree_leaf_transmit = 0.0; // every fragment while a switch is on (light() reads it; no hull = no translucency)
		if (ring_crown_valid(tree_crown_hull_extra) && (use_alpha_cutout || (ring_forest_crown_on() && tree_model_position.y >= tree_crown_hull.x - tree_crown_hull.z))) {
			float analytic_weight = ring_forest_analytic_weight(distance(tree_world_position, ring_atmosphere_camera_world_position));
			float crown_sky, crown_sun;
			ring_crown_first_leaf_terms(tree_model_camera, tree_model_position, tree_model_light, tree_crown_hull, tree_crown_hull_extra, use_alpha_cutout && ring_forest_translucency_on() && analytic_weight > 0.0, crown_sky, crown_sun);
			if (ring_forest_crown_on()) AO *= crown_sky;
			if (use_alpha_cutout && ring_forest_translucency_on()) {
				tree_leaf_transmit = tree_crown_hull_extra.z * (ring_leaf_lad_facing(tree_model_light, normalize(tree_model_camera - tree_model_position), vec3(0.0, 1.0, 0.0), tree_crown_lad)) * mix(1.0, crown_sun, analytic_weight);
			}
		}
	}
	if (ring_canopy_shade_on() && !use_alpha_cutout) {
		// Forest lighting FL-3a: bark below the crown base sits under the stand's canopy (the raster); at or above the base the tree's own crown occlusion (2c) applies instead and the raster
		// would count that crown twice. Foliage never samples the raster. Without a valid hull the raster applies to the whole trunk. The transmitted factor reaches light() in tree_leaf_transmit.
		tree_leaf_transmit = 0.0;
		if (!ring_crown_valid(tree_crown_hull_extra) || tree_model_position.y < tree_crown_hull.x - tree_crown_hull.z) {
			vec2 canopy = ring_canopy_sample(tree_world_position);
			AO *= ring_canopy_sky_visibility(canopy);
			tree_leaf_transmit = canopy.x * canopy.y;
		}
	}
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
		tree_highlight_roughness = 1.0;
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
	terms.storm = LIGHT_IS_DIRECTIONAL ? terms.storm : 0.0; // storm light fades the sun only, never a point or spot light
	terms.cloud = mix(1.0, cloud, primary_sun);
	terms.geometry = ring_height_shadow_vegetation_geometry(terms.geometry, tree_height_term, LIGHT_IS_DIRECTIONAL, primary_sun, tree_day_factor);
	terms.geometry = ring_height_shadow_vegetation_planet_geometry(terms.geometry, tree_height_term, LIGHT_IS_DIRECTIONAL, LIGHT, VIEW_MATRIX, primary_sun);
	float sun_visibility = ring_shadow_sun_visibility(terms, RING_SHADOW_COMBINE_PRODUCT);
	if (ring_shadow_debug_enabled()) {
		if (LIGHT_IS_DIRECTIONAL && primary_sun > 0.5) {
			DIFFUSE_LIGHT += vec3(ring_shadow_debug_value(terms, sun_visibility));
		}
	} else {
		float visibility = sun_visibility * mix(1.0, direct, primary_sun);
		DIFFUSE_LIGHT += (LIGHT_COLOR / PI) * n_dot_l * visibility;
		if (ring_canopy_shade_on() && !use_alpha_cutout && tree_leaf_transmit > 0.0 && primary_sun > 0.0) {
			// Forest lighting FL-3a: sunlight re-emitted by the canopy above, bark under the stand (tree_leaf_transmit = blocked * species factor); above = cloud and storm only (geometry 1).
			DIFFUSE_LIGHT += ring_canopy_transmitted(LIGHT_COLOR, LIGHT, NORMAL, ring_canopy_up_view(VIEW_MATRIX), vec2(tree_leaf_transmit, 1.0),
				ring_canopy_above(terms, RING_SHADOW_COMBINE_PRODUCT) * mix(1.0, direct, primary_sun) * primary_sun);
		}
		if (ring_forest_translucency_on() && use_alpha_cutout && tree_leaf_transmit > 0.0) {
			// Leaf translucency: sunlight transmitted to the viewer's side of the leaf, primary sun only (docs/forest-lighting-plan.md section 3). Godot multiplies by ALBEDO afterwards.
			DIFFUSE_LIGHT += (LIGHT_COLOR / PI) * (tree_leaf_transmit * RING_LEAF_TRANSMISSION_SHAPE) * visibility * primary_sun;
		}
		if (!simplified_tree_shading) {
			vec3 half_vector = normalize(LIGHT + VIEW);
			float gloss = mix(8.0, 128.0, 1.0 - tree_highlight_roughness);
			float specular = pow(max(dot(NORMAL, half_vector), 0.0), gloss) * mix(mix(.04, .1, tree_wet_state.y), 1.0, METALLIC);
			SPECULAR_LIGHT += (LIGHT_COLOR / PI) * specular * n_dot_l * visibility * tree_haze_T;
		}
	}
}
