// Per-root COLOR stores terrain biome weights: desert, forest, tundra, alpine.
// These are low-frequency terrain texture averages supplied by the CPU; they
// intentionally do not sample terrain rasters or read GPU state per frame.
uniform bool grass_appearance_enabled = false;
uniform vec3 grass_palette_grassland : source_color = vec3(.22, .36, .12);
uniform vec3 grass_palette_forest : source_color = vec3(.14, .28, .10);
uniform vec3 grass_palette_sparse : source_color = vec3(.42, .38, .18);
uniform vec3 grass_terrain_average_grass;
uniform vec3 grass_terrain_average_dirt;
uniform vec3 grass_terrain_average_desert;
uniform vec3 grass_terrain_average_forest;
uniform vec3 grass_terrain_average_tundra;
uniform vec3 grass_terrain_average_alpine;
uniform float grass_terrain_handoff_strength = 1.0;
uniform float grass_terrain_blend_start_fraction = .70;
uniform float grass_terrain_blend_end_fraction = .90;
// Zero retains the LOD cutoff for standalone shader fixtures. Runtime supplies
// the effective draw distance, including non-LOD visibility limits.
uniform float grass_terrain_draw_distance = 0.0;
uniform float grass_terrain_lighting_match = 1.0;
// Standalone fixtures retain their established geometry unless opted in.
uniform bool grass_coordinated_handoff_enabled = false;
#include "res://materials/ring_terrain_grass_coverage.gdshaderinc"
#include "res://materials/ring_surface_albedo_scale.gdshaderinc"

vec3 grass_appearance_albedo_with_coverage(vec3 blade_albedo, vec4 biome_weights, float handoff, float coverage) {
	if (!grass_appearance_enabled) return ring_grass_albedo_scaled(blade_albedo);
	// Match the terrain's normalized biome mixture for planted soil. Keep the
	// authored palette available at partial strength and with matching disabled.
	vec4 terrain_weights = clamp(biome_weights, vec4(0.0), vec4(1.0));
	float explicit_weight = dot(terrain_weights, vec4(1.0));
	float grassland_weight = max(0.0, 1.0 - explicit_weight);
	float total_weight = max(.0001, grassland_weight + explicit_weight);
	float sparse = clamp(biome_weights.x + biome_weights.z + biome_weights.w, 0.0, 1.0);
	vec3 palette = mix(grass_palette_grassland, grass_palette_forest, clamp(biome_weights.y, 0.0, 1.0));
	palette = mix(palette, grass_palette_sparse, sparse);
	vec3 terrain = grass_terrain_average_grass * grassland_weight
		+ grass_terrain_average_desert * terrain_weights.x
		+ grass_terrain_average_forest * terrain_weights.y
		+ grass_terrain_average_tundra * terrain_weights.z
		+ grass_terrain_average_alpine * terrain_weights.w;
	terrain /= total_weight;
	terrain = ring_terrain_albedo_scaled(ring_apply_terrain_grass_coverage(terrain, coverage));
	// source_color values arrive in linear space. Normalize against the fixed
	// default grassland palette so an authored grassland tint remains visible.
	vec3 default_grassland_linear = pow((vec3(.22, .36, .12) + vec3(.055)) / vec3(1.055), vec3(2.4));
	vec3 graded_blade = ring_grass_albedo_scaled(blade_albedo * palette / default_grassland_linear);
	float strength = isnan(grass_terrain_handoff_strength) || isinf(grass_terrain_handoff_strength)
		? 0.0 : clamp(grass_terrain_handoff_strength, 0.0, 1.0);
	// Match hue at every range while retaining restrained root/tip brightness
	// and canonical per-instance variation. The distance band only removes this
	// relief for final terrain convergence; it never gates the color match.
	vec3 luminance_weights = vec3(.2126, .7152, .0722);
	float blade_brightness = dot(blade_albedo, luminance_weights)
		/ max(.0001, dot(default_grassland_linear, luminance_weights));
	float relief = mix(clamp(blade_brightness, .8, 1.2), 1.0, clamp(handoff, 0.0, 1.0));
	return mix(graded_blade, terrain * relief, strength);
}

// Preserve the shared benchmark material's established three-argument API.
vec3 grass_appearance_albedo(vec3 blade_albedo, vec4 biome_weights, float handoff) {
	return grass_appearance_albedo_with_coverage(blade_albedo, biome_weights, handoff, 0.0);
}
