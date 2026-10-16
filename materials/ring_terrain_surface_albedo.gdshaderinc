// Shared Terrain3D surface biome albedo contract. The Terrain3D ring surface and
// the grass-cards materials declare the same samplers and combine them with the
// same weights/tint, so grass colour can match the ground it grows from.
// Texture SAMPLING stays with each caller: the terrain keeps its anisotropic
// triplanar texture() calls (implicit derivatives), grass uses textureLod on the
// y projection. Only the declarations, tint and weight mix live here.
//
// Biome weights are the packed Terrain3D COLOR map: r desert, g forest,
// b tundra, a alpine; grass is the remainder.
uniform float biome_texture_scale = .20;
uniform sampler2D biome_grass_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D biome_dirt_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D biome_desert_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D biome_forest_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D biome_tundra_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D biome_alpine_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;

float ring_terrain_grass_weight(vec4 packed_weights) {
	return max(0.0, 1.0 - packed_weights.r - packed_weights.g - packed_weights.b - packed_weights.a);
}

// Warm, saturated grass response applied to the grass albedo sample only.
vec3 ring_terrain_grass_tint(vec3 grass) {
	return mix(grass, grass * vec3(.72, 1.30, .62), .58);
}

// `grass` must already carry ring_terrain_grass_tint. Returns the normalised
// biome-weighted surface colour (the terrain then blends it with dirt by slope).
vec3 ring_terrain_biome_mix(vec3 grass, vec3 desert, vec3 forest, vec3 tundra, vec3 alpine, vec4 packed_weights) {
	float grass_weight = ring_terrain_grass_weight(packed_weights);
	float total = max(.0001, grass_weight + packed_weights.r + packed_weights.g + packed_weights.b + packed_weights.a);
	vec3 surface = grass * grass_weight;
	surface += desert * packed_weights.r;
	surface += forest * packed_weights.g;
	surface += tundra * packed_weights.b;
	surface += alpine * packed_weights.a;
	return surface / total;
}
