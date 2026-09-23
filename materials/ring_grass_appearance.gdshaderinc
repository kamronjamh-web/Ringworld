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
uniform float grass_terrain_handoff_strength = .55;

vec3 grass_appearance_albedo(vec3 blade_albedo, vec4 biome_weights, float handoff) {
	if (!grass_appearance_enabled) return blade_albedo;
	float explicit_weight = clamp(dot(biome_weights, vec4(1.0)), 0.0, 1.0);
	float grassland_weight = 1.0 - explicit_weight;
	float sparse = clamp(biome_weights.x + biome_weights.z + biome_weights.w, 0.0, 1.0);
	vec3 palette = mix(grass_palette_grassland, grass_palette_forest, clamp(biome_weights.y, 0.0, 1.0));
	palette = mix(palette, grass_palette_sparse, sparse);
	vec3 terrain = grass_terrain_average_grass * grassland_weight
		+ grass_terrain_average_desert * biome_weights.x
		+ grass_terrain_average_forest * biome_weights.y
		+ grass_terrain_average_tundra * biome_weights.z
		+ grass_terrain_average_alpine * biome_weights.w;
	terrain = mix(terrain, grass_terrain_average_dirt, sparse * .18);
	// source_color values arrive in linear space. Normalize against the fixed
	// default grassland palette so an authored grassland tint remains visible.
	vec3 default_grassland_linear = pow((vec3(.22, .36, .12) + vec3(.055)) / vec3(1.055), vec3(2.4));
	vec3 graded_blade = blade_albedo * palette / default_grassland_linear;
	return mix(graded_blade, terrain, clamp(grass_terrain_handoff_strength, 0.0, 1.0) * handoff);
}
