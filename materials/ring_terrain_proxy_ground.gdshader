shader_type spatial;
render_mode cull_back, depth_draw_opaque;
// T4 ground surface variant of ring_terrain_proxy.gdshader (rock, strata, scree), for worlds with ground.surface; picked by RingGroundSurfaceVariants. The shipped ring_terrain_proxy.gdshader is the
// generation 1 shader and stays exactly as it was; a ground-variant change never touches it.
#define RING_GROUND_SURFACE

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
// Structural closures (rim walls, hull planes) keep their authored reflectance like ring_hull_global; the terrain albedo retune is terrain-only.
uniform bool structural_surface = false;
uniform float biome_texture_scale : hint_range(0.01, 2.0) = 0.20;
uniform sampler2D biome_grass_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D biome_dirt_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D biome_desert_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D biome_forest_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D biome_tundra_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D biome_alpine_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
// Main-viewport only: ray frame comes from global shader parameters.
#define RING_ATMOSPHERE_FRAME_GLOBAL
#include "res://materials/ring_color_space.gdshaderinc"
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_cloud_shadow.gdshaderinc"
#include "res://materials/ring_terrain_macro_shadow.gdshaderinc"
#include "res://materials/ring_shadow_compose.gdshaderinc"
#include "res://materials/ring_far_weather_shadow.gdshaderinc"
#include "res://materials/ring_terrain_grass_coverage.gdshaderinc"
#include "res://materials/ring_surface_albedo_scale.gdshaderinc"
#include "res://materials/ring_surface_aperture.gdshaderinc"
#include "res://materials/ring_wetness.gdshaderinc"
#ifdef RING_GROUND_SURFACE
#include "res://materials/ring_ground_surface.gdshaderinc"
// Rock replaces dirt on steep ground in worlds with ground.surface: the dirt sample is skipped there.
#define GROUND_NOT_ON && !ground_on
#else
#define GROUND_NOT_ON
#endif
// Enables the whole-ring far weather cloud shadow (ring_far_weather_shadow.gdshaderinc) beyond the Sunshine maps.
uniform bool analytic_cloud_shadow_enabled = true;
uniform float ring_width_meters = 20000.0;
uniform bool debug_color_enabled = false;
uniform vec3 debug_color = vec3(1.0, 0.0, 1.0);
// T2R-12b (S11b): river line coverage. River worlds carry the true area fraction c of channel water in UV2.y of terrain lattice vertices as 0.49 c (water and skirt vertices hold 1, so every reader
// thresholds at 0.5); the owner sets river_line_enabled only on levels that carry it. river_far_color is the River seeded colour, in the vertex-colour space.
uniform bool river_line_enabled = false;
uniform vec3 river_far_color = vec3(0.075, 0.36, 0.43);
// Peak strength and tightness of the sun glint on the river line (constants of the look, as in ring_terrain_global).
const float RIVER_GLINT_STRENGTH = 0.3;
const float RIVER_GLINT_SHININESS = 140.0;
// Shared per-LOD atlas.  Each sector supplies its own rect as an instance
// uniform, so coverage changes need no unique materials or mesh rebuilds.
uniform sampler2D coverage_atlas : filter_nearest, repeat_disable;
// Mirrors EnablePartialCoverageMeshCulling for slot-installed sectors (the vertex-stage covered-quad collapse).
uniform bool proxy_vertex_collapse_enabled = false;
#ifdef RING_GROUND_SURFACE
// T4 ground surface: the distance (m) over which this level's vertex normals are differenced (2 x the lattice spacing; RingTerrainProxySector sets it). The rock rule adds a slope bias for coarse levels.
uniform float ground_slope_baseline_meters = 4.0;
// T5-9 far speckle: the world feature bit of scatter.rocks (the ledger's bit 4) and the pattern's cell size (m).
const int SCATTER_ROCKS_BIT = 4;
const float SPECKLE_CELL_M = 4.0;
#endif
instance uniform vec4 coverage_atlas_rect = vec4(0.0, 0.0, 0.0, 0.0);
// After coverage_atlas_rect on purpose: instance uniforms without an explicit index are numbered in declaration order, and the
// ocean and shadow shaders (which share these instance parameters) declare coverage_atlas_rect first.
#include "res://materials/ring_terrain_proxy_vertex_fetch.gdshaderinc"

varying vec3 world_position;
varying float ring_day_factor;
varying vec2 proxy_coverage_uv;
varying vec3 surface_up_world;
// Both ambient occlusion and the directional-light path consume the same
// day-gated strongest cloud estimate for this fragment.
varying float combined_cloud_shadow_occlusion;
// Wet gloss for the custom light(): weight, roughness, specular reflectance.
varying vec3 proxy_wet_specular_state;
// The river line coverage c of this fragment (0 off the line).
varying float river_line_c;
#ifdef RING_GROUND_SURFACE
// T4 strata: absolute canonical height of this vertex (m).
varying float ground_height;
// T4 colour variation (D5): the 64 m convexity C64 (m) the sector builders stored for this vertex; 0 on levels coarser than 64 m.
varying float ground_c64;
#endif

float sample_combined_cloud_shadow(vec3 sample_world_position, float day_factor) {
	// The Sunshine projected maps own their footprint; the far weather shell
	// shadow fills the rest through their coverage weight.
	return ring_cloud_shadow_occlusion_with_far_weather(
		sample_world_position, ring_center, sun_direction, analytic_cloud_shadow_enabled) * day_factor;
}

void vertex() {
	vec3 proxy_chord = CUSTOM0.xyz;
	float proxy_relief_m = 0.0;
	if (proxy_vertex_table_enabled) proxy_vertex_table_apply_relief(VERTEX_ID, VERTEX, NORMAL, COLOR, UV, UV2, proxy_chord, proxy_relief_m);
	proxy_coverage_uv = UV;
	river_line_c = river_line_enabled && UV2.y < 0.5 ? UV2.y / 0.49 : 0.0;
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
		VERTEX += runtime_parent_chord_enabled && UV2.y < .5 ? proxy_chord * morph : surface_up_local * UV2.x * morph;
	}
#ifdef RING_GROUND_SURFACE
	if (ring_ground_on()) {
		// Absolute canonical height from model space (T4 design D3): each sector's origin is at canonical height 0 on the ring surface, so the height of a vertex is its model-space
		// coordinate along the origin's ring up, less the curvature drop a^2 / 2R along the ring.
		vec3 ground_origin = MODEL_MATRIX[3].xyz - ring_center;
		ground_origin.y = 0.0;
		float ground_radius = max(length(ground_origin), 1.0);
		mat3 ground_to_model = transpose(mat3(MODEL_MATRIX)); // a rotation (times a uniform scale): the inverse up to a scale the normalisations below remove
		vec3 ground_up_model = normalize(ground_to_model * (-ground_origin / ground_radius));
		vec3 ground_around_model = normalize(cross(normalize(ground_to_model * vec3(0.0, 1.0, 0.0)), ground_up_model));
		float ground_around = dot(VERTEX, ground_around_model);
		ground_height = dot(VERTEX, ground_up_model) - ground_around * ground_around / (2.0 * ground_radius);
		// C64: slot sectors carry it in the vertex table (read with the attribute fetch of proxy_vertex_table_apply_relief), mesh sectors in CUSTOM0.w. Only levels of 64 m and finer store it (the baseline is twice the spacing), so coarser levels read 0.
		ground_c64 = 0.0;
		if (ground_slope_baseline_meters <= 128.5) ground_c64 = proxy_slot >= 0 ? proxy_relief_m : CUSTOM0.w;
	}
#endif
	world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec3 radial = world_position - ring_center;
	radial.y = 0.0;
	vec3 surface_up = length(radial) > 0.0001 ? -normalize(radial) : vec3(-1.0, 0.0, 0.0);
	ring_day_factor = ringworld_effective_solar_day(smoothstep(day_start_dot, day_full_dot, dot(surface_up, normalize(sun_direction))));
	surface_up_world = surface_up;
	if (proxy_vertex_table_enabled && proxy_vertex_collapse_enabled && proxy_slot >= 0 && coverage_atlas_rect.z > 0.0) {
		// Slot sectors have no CPU triangle filter: a lattice vertex whose (up to four) incident quads are all covered
		// by a finer level collapses onto the sector origin, so triangles whose three vertices all collapse are
		// zero-area and never rasterised. A triangle with a surviving vertex stretches toward the origin, but its
		// varyings (set above, from the uncollapsed vertex) and its quad's covered UVs are unchanged, so the
		// fragment-stage atlas discard removes it exactly as before. Never write POSITION here: any POSITION write
		// switches the whole vertex stage to override mode and draws nothing where it is not assigned.
		int collapse_width = textureSize(proxy_vertex_table_pos, 0).x;
		if (VERTEX_ID < collapse_width * collapse_width) {
			ivec2 lattice_id = ivec2(VERTEX_ID % collapse_width, VERTEX_ID / collapse_width);
			int collapse_cells = collapse_width - 1;
			bool all_covered = true;
			for (int dz = -1; dz <= 0 && all_covered; dz++) {
				for (int dx = -1; dx <= 0 && all_covered; dx++) {
					ivec2 quad = lattice_id + ivec2(dx, dz);
					if (quad.x < 0 || quad.y < 0 || quad.x >= collapse_cells || quad.y >= collapse_cells) continue;
					vec2 quad_uv = (vec2(quad) + 0.5) / float(collapse_cells);
					all_covered = textureLod(coverage_atlas, coverage_atlas_rect.xy + quad_uv * coverage_atlas_rect.zw, 0.0).r > 0.5;
				}
			}
			if (all_covered) VERTEX = vec3(0.0);
		}
	}
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
#ifdef RING_GROUND_SURFACE
	// T4 ground surface: the blocks below run only in worlds with ground.surface. The strata footprint is taken here, before any discard, in uniform control flow.
	bool ground_on = biome_texture_weights_enabled && !debug_color_enabled && !structural_surface && ring_ground_on();
	float strata_footprint = ground_on ? fwidth(ground_height) : 0.0;
	float ground_rock = 0.0;
	vec3 ground_debug = vec3(0.0);
	float ground_relief = ground_on ? ring_ground_relief(ground_c64) : 0.0;
	// T5-9 far speckle (design B8): canonical 4 m cells from the world-anchored wet plane, and their footprint in cells, taken here in uniform control flow (the branch is on global uniforms).
	bool speckle_on = ground_on && ring_world_has(SCATTER_ROCKS_BIT);
	vec2 speckle_cell = vec2(0.0);
	if (speckle_on) speckle_cell = ring_wet_plane(world_position) * (1.0 / SPECKLE_CELL_M);
	vec2 speckle_cell_width = fwidth(speckle_cell);
	float speckle_footprint = max(speckle_cell_width.x, speckle_cell_width.y);
#endif
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
#ifdef RING_GROUND_SURFACE
	vec3 ground_surface_texture = vec3(0.0);
#endif
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
		if (ground_facing < 1.0 GROUND_NOT_ON)
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
#ifdef RING_GROUND_SURFACE
			if (ground_on) ground_surface_texture = surface_texture / total;
#endif
		} else {
			terrain_color = dirt;
		}
#ifdef RING_GROUND_SURFACE
		if (ground_on) {
			// Rock replaces dirt on steep ground and scree sits on the slope foot (T4 design D2, D4; relief is 0 until T4-4b). Rock and scree use the alpine texture, which is sampled
			// here when the biome weights did not already (packed.a == 0).
			vec4 ground_weights = clamp(COLOR, 0.0, 1.0);
			float ground_grass = max(0.0, 1.0 - ground_weights.r - ground_weights.g - ground_weights.b - ground_weights.a);
			float slope_degrees = ring_ground_slope_deg(dot(world_normal, normalize(surface_up_world)));
			ground_rock = ring_ground_rock_exposure(slope_degrees, ground_slope_baseline_meters, ground_relief, ground_weights.g);
			float ground_scree = ring_ground_scree(slope_degrees, ground_slope_baseline_meters, ground_relief, ground_grass + ground_weights.g);
			grass_coverage_upward *= 1.0 - max(ground_rock, ground_scree);
			float strata_factor = 1.0;
			terrain_color = ground_surface_texture;
			if (ground_rock + ground_scree > 0.0) {
				if (ground_rock > 0.0) strata_factor = ring_ground_strata_factor(ground_height, strata_footprint);
				vec3 alpine = sample_biome_triplanar(biome_alpine_albedo, texture_position, world_normal, blend);
				terrain_color = mix(mix(ground_surface_texture, alpine * GROUND_SCREE_TINT, ground_scree), alpine * GROUND_ROCK_TINT * strata_factor, ground_rock);
			}
			// D5 colour variation on the dry albedo (before grass coverage, wetness and the albedo scale), from the interpolated C64.
			terrain_color = ring_ground_relief_colour(terrain_color, ground_relief, ground_rock, distance_to_camera);
			if (speckle_on) {
				// T5-9 far speckle (B8): where boulder meshes end (540 m x the scatter distance scale k, ringworld_ground_world.w) the rock and scree weight reads as dark 4 m cells. A cell is a boulder with
				// probability 0.25 w and a boulder cell is 25 % darker. The mean term (0.0625 w) applies everywhere; the deviation from it fades out as the cells shrink below a pixel (F, T4 D3), so the
				// field converges to its mean tone. No time input: the pattern cannot animate. The plane is modulo 16384 m = 4096 cells, so the cell index wraps there and is the same on both sides of it.
				float speckle_k = max(ringworld_ground_world.w, 0.01);
				float speckle_w = max(ground_scree, 0.6 * ground_rock) * clamp(ringworld_ground_world.z, 0.0, 1.0) * smoothstep(540.0 * speckle_k, 600.0 * speckle_k, distance_to_camera);
				if (speckle_w > 0.0) {
					uint cell_x = uint(int(floor(speckle_cell.x))) & 4095u;
					uint cell_y = uint(int(floor(speckle_cell.y))) & 4095u;
					uint cell_hash = ring_ground_pcg(cell_x ^ ring_ground_pcg(cell_y ^ (uint(ringworld_ground_world.y) * 2654435761u)));
					float boulder = float(cell_hash >> 8u) * (1.0 / 16777216.0) < 0.25 * speckle_w ? 1.0 : 0.0;
					float speckle_contrast = 1.0 - smoothstep(0.35, 0.9, speckle_footprint);
					terrain_color *= 1.0 - 0.0625 * speckle_w - 0.25 * (boulder - 0.25 * speckle_w) * speckle_contrast;
				}
			}
			ground_debug = vec3(ground_rock, ground_scree, 0.5 * strata_factor);
			if (ringworld_ground_look.w > 1.5) ground_debug = vec3(0.5 + 0.5 * ground_relief, ring_ground_relief_colour(vec3(0.5), ground_relief, 0.0, distance_to_camera).g, 0.0);
		}
#endif
	}
	float grass_coverage = ring_terrain_grass_coverage(
		world_position,
		distance_to_camera,
		grass_coverage_upward,
		grass_coverage_eligibility);
	terrain_color = ring_apply_terrain_grass_coverage(terrain_color, grass_coverage);
	if (river_line_enabled && !debug_color_enabled) terrain_color = mix(terrain_color, ring_vertex_color_to_linear(river_far_color), river_line_c);
	if (!structural_surface) terrain_color = ring_terrain_albedo_scaled(terrain_color);
	// Wet world: per-location wetness darkens and glosses the proxy ground; puddles are only an averaged roughness drop (no normals, no mask shapes).
	float surface_roughness = mix(roughness_value, max(roughness_value, .90), grass_coverage);
	float surface_specular = 0.18;
	float wet_gloss = 0.0;
	if (ring_wet_params.w > 0.5) {
		vec3 wet_normal = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
		vec3 wet_up = normalize(surface_up_world);
		RingWet wet_state = ring_wet_sample(world_position, wet_normal, wet_up);
		float flatness = ring_wet_flatness(wet_normal, wet_up);
#ifdef RING_GROUND_SURFACE
		float wet_ground_facing = smoothstep(0.45, 0.78, dot(wet_normal, wet_up));
		if (ground_on) wet_ground_facing = 1.0 - ground_rock;
		ring_wet_apply(wet_state, mix(0.5, 1.0, wet_ground_facing), terrain_color, surface_roughness, surface_specular);
#else
		ring_wet_apply(wet_state, mix(0.5, 1.0, smoothstep(0.45, 0.78, dot(wet_normal, wet_up))), terrain_color, surface_roughness, surface_specular);
#endif
		wet_gloss = ring_wet_gloss(wet_state);
		float puddle_far = ring_wet_puddle_average(wet_state, flatness, max(distance_to_camera, 100.0));
		surface_roughness = mix(surface_roughness, 0.12, puddle_far);
		wet_gloss = max(wet_gloss, puddle_far);
	}
	proxy_wet_specular_state = vec3(wet_gloss, surface_roughness, surface_specular);
	if (river_line_enabled) {
		surface_roughness = mix(surface_roughness, 0.06, river_line_c);
		surface_specular = mix(surface_specular, 0.5, river_line_c);
	}
	// Keep a spatially varying night tint, but leave brightness to Godot's
	// environment ambient light and directional-light pipeline.
	vec3 lit_color = terrain_color * mix(night_tint, vec3(1.0), ring_day_factor);
	RingHaze haze = ring_haze_sample(camera_world_position, world_position, ring_center, ring_day_factor);
	ALBEDO = lit_color * haze.T;
	EMISSION = ring_haze_inscatter(haze);
	// Every water kind now uses a separate transparent surface, leaving this
	// material responsible only for dry and submerged solid terrain.
	ROUGHNESS = surface_roughness;
	SPECULAR = surface_specular;
	METALLIC = 0.0;
	combined_cloud_shadow_occlusion = sample_combined_cloud_shadow(world_position, ring_day_factor);
	AO = min(1.0 - combined_cloud_shadow_occlusion * 0.72, ring_storm_ambient(ring_storm_level(world_position))); // storm light
	AO_LIGHT_AFFECT = 0.0;
	if (river_line_enabled && river_line_c > 0.0) {
		// River line glint (S11b): the sun lobe of ring_terrain_global scaled by the real coverage. River water is level, so the normal is the local zenith, not the coarse ground normal.
		vec3 glint_sun = normalize((VIEW_MATRIX * vec4(sun_direction, 0.0)).xyz);
		vec3 glint_up = normalize((VIEW_MATRIX * vec4(surface_up_world, 0.0)).xyz);
		float glint = pow(max(dot(glint_up, normalize(glint_sun + VIEW)), 0.0), RIVER_GLINT_SHININESS) * (RIVER_GLINT_SHININESS + 8.0) / 8.0;
		EMISSION += vec3(1.0, 0.97, 0.9) * glint * RIVER_GLINT_STRENGTH * river_line_c * ring_day_factor * (1.0 - combined_cloud_shadow_occlusion) * haze.T;
	}
	if (ring_shadow_debug_enabled()) {
		// Shadow debug view: only the selected shadow term reaches the screen (see ring_shadow_compose.gdshaderinc).
		ALBEDO = vec3(1.0);
		EMISSION = vec3(0.0);
		AO = 0.0;
		SPECULAR = 0.0;
		METALLIC = 0.0;
		ROUGHNESS = 1.0;
	}
#ifdef RING_GROUND_SURFACE
	if (ground_on && ringworld_ground_look.w > 0.5) {
		// Ground debug views (terrain_rock_material_gpu_test), never set by the game. w = 1: EMISSION = rock, scree, strata factor / 2; w = 2: 0.5 + relief / 2, the relief colour of mid grey (g), 0.
		ALBEDO = vec3(0.0);
		EMISSION = ground_debug;
		SPECULAR = 0.0;
	}
#endif
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
	// S7a: the same for the planet light when the front layer is a planet layer (weight against the published direction).
	float planet_height_weight = LIGHT_IS_DIRECTIONAL && ring_height_shadow_planet_active()
		? ring_height_shadow_light_weight(LIGHT, VIEW_MATRIX) * (1.0 - clamp(ringworld_hub_light_weight, 0.0, 1.0) * primary_sun_weight)
		: 0.0;
	bool height_planet = planet_height_weight > 0.0;
	RingHeightShadow height_shadow = ring_height_shadow_none();
	if (height_active || height_planet) {
		height_shadow = ring_height_shadow_sample(
			world_position,
			0.0,
			ring_height_shadow_slope_tan(transpose(mat3(VIEW_MATRIX)) * NORMAL),
			distance(world_position, camera_world_position));
	}
	float terrain_shadow = LIGHT_IS_DIRECTIONAL
		? ring_macro_terrain_occlusion(
			sample_terrain_macro_shadow(world_position, ring_center, camera_world_position, ring_width_meters) * (height_active ? ring_height_shadow_macro_scale(height_shadow) : 1.0),
			sample_terrain_planet_shadow(world_position, ring_center, camera_world_position, ring_width_meters) * (height_planet ? ring_height_shadow_macro_scale(height_shadow) : 1.0),
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
	terms.storm = LIGHT_IS_DIRECTIONAL ? terms.storm : 0.0; // storm light fades the sun only, never a point or spot light
	terms.contact = ring_contact_visibility(FRAGCOORD.xy, VIEWPORT_SIZE, INV_PROJECTION_MATRIX, primary_sun_weight, RING_SHADOW_DEFAULT);
	if (height_active) {
		terms.height = ring_height_shadow_term(height_shadow, primary_sun_weight, ring_day_factor, ringworld_hub_light_weight, shadow_floor);
	} else if (height_planet) {
		terms.height = ring_height_shadow_planet_term(height_shadow, planet_height_weight, shadow_floor);
	}
	float combined_visibility = ring_shadow_sun_visibility(terms, RING_SHADOW_DEFAULT);
	if (ring_shadow_debug_enabled()) {
		if (LIGHT_IS_DIRECTIONAL && (primary_sun_weight > 0.5 || (ring_height_shadow_enabled() && planet_height_weight > 0.5))) {
			DIFFUSE_LIGHT += vec3(ring_shadow_debug_value(terms, combined_visibility));
		}
	} else {
		// Built-in convention (see ring_tree): Godot multiplies DIFFUSE_LIGHT by ALBEDO and LIGHT_COLOR carries the PI factor.
		DIFFUSE_LIGHT += (LIGHT_COLOR / PI)
			* n_dot_l
			* combined_visibility
			* hemisphere_direct;
		if (proxy_wet_specular_state.x > 0.0) {
			SPECULAR_LIGHT += ring_wet_direct_specular(NORMAL, LIGHT, VIEW, proxy_wet_specular_state.y, proxy_wet_specular_state.z, proxy_wet_specular_state.x)
				* LIGHT_COLOR * combined_visibility * hemisphere_direct * SPECULAR_AMOUNT * ring_storm_specular_keep(terms.storm); // storm light: no sun glint under an overcast
		}
	}
}
