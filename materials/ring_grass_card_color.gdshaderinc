// Shared grass-card / hero-blade colour pipeline (grass-cards plan step S4). Both materials include this file and are fed
// the same uniform values from one C# config (RingGrassCardMaterialConfig), so blades and cards colour identically by
// construction. The ground colour is the Terrain3D surface albedo (ring_terrain_surface_albedo.gdshaderinc) sampled with
// textureLod on the top-down projection, at a terrain uv derived exactly like the terrain's own y-projection uv:
//   uv(p) = grass_terrain_origin_uv.xy + vec2(dot(p - o, around.xyz), dot(p - o, across.xyz)) * biome_texture_scale
// with o = grass_gust_origin_world and the canonical ground covectors the gust field already uses. COLOR carries the
// packed biome weights (r desert, g forest, b tundra, a alpine; grass is the remainder).
#include "res://materials/ring_terrain_surface_albedo.gdshaderinc"

// Per-tile canonical terrain uv. xy = texture uv of the tile's gust origin (canonical around/across metres mod 5, times
// 0.2); zw = macro-shadow uv of the same origin (around / circumference, across / ring width). Written next to the gust
// mapping, so it is canonical (floating-origin rebases leave it unchanged).
instance uniform vec4 grass_terrain_origin_uv;

uniform float grass_card_green_pull = 0.15;       // 0 = raw ground colour, 1 = ground luminance in a grass hue
uniform vec3 grass_card_hue = vec3(0.62, 1.0, 0.36);
uniform float grass_card_root_ao = 0.5;           // brightness at the blade root
uniform float grass_card_tip_light = 0.16;        // extra brightness at the tip
uniform vec3 grass_card_tip_tint = vec3(1.04, 1.0, 0.74);
uniform float grass_card_blade_value_jitter = 0.22;
uniform float grass_card_blade_hue_jitter = 0.10;
uniform float grass_card_dry_fraction = 0.07;     // fraction of blades that read dry / yellow
uniform vec3 grass_card_dry_tone = vec3(0.70, 0.58, 0.24);
uniform float grass_card_tuft_value_jitter = 0.14;
uniform float grass_card_frag_lod_bias = -0.5;    // per-fragment ground mip bias (derivative lod)

// Canonical metres from the tile origin along the ground covectors (x around, y across).
vec2 grass_card_canonical_offset(vec3 world, vec3 origin_world, vec4 around_covector, vec4 across_covector) {
	vec3 delta = world - origin_world;
	return vec2(dot(delta, around_covector.xyz), dot(delta, across_covector.xyz));
}

// Terrain top-down texture uv of a world position.
vec2 grass_card_terrain_uv(vec2 canonical_offset, vec4 origin_uv) {
	return origin_uv.xy + canonical_offset * biome_texture_scale;
}

// Biome albedo at one uv/mip. Index: 0 grass (already carries the terrain grass tint), 1 desert, 2 forest, 3 tundra, 4 alpine.
vec3 grass_card_biome_sample(int index, vec2 uv, float lod) {
	if (index == 0) return ring_terrain_grass_tint(textureLod(biome_grass_albedo, uv, lod).rgb);
	if (index == 1) return textureLod(biome_desert_albedo, uv, lod).rgb;
	if (index == 2) return textureLod(biome_forest_albedo, uv, lod).rgb;
	if (index == 3) return textureLod(biome_tundra_albedo, uv, lod).rgb;
	return textureLod(biome_alpine_albedo, uv, lod).rgb;
}

float grass_card_biome_weight(int index, vec4 weights) {
	if (index == 0) return ring_terrain_grass_weight(weights);
	if (index == 1) return weights.r;
	if (index == 2) return weights.g;
	if (index == 3) return weights.b;
	return weights.a;
}

// Far (vertex) path: only the two dominant biomes are sampled, renormalised. Uses a coarse mip (the caller passes the
// footprint mip minus its bias) so light and dark ground patches survive.
vec3 grass_card_ground_two_biomes(vec2 uv, float lod, vec4 weights) {
	int first = 0;
	float first_weight = grass_card_biome_weight(0, weights);
	for (int i = 1; i < 5; i++) {
		float w = grass_card_biome_weight(i, weights);
		if (w > first_weight) { first = i; first_weight = w; }
	}
	int second = first == 0 ? 1 : 0;
	float second_weight = grass_card_biome_weight(second, weights);
	for (int i = 0; i < 5; i++) {
		if (i == first) continue;
		float w = grass_card_biome_weight(i, weights);
		if (w > second_weight) { second = i; second_weight = w; }
	}
	float total = first_weight + second_weight;
	if (second_weight <= 0.001) return grass_card_biome_sample(first, uv, lod);
	return (grass_card_biome_sample(first, uv, lod) * first_weight + grass_card_biome_sample(second, uv, lod) * second_weight) / total;
}

// Near (fragment) path: every biome with weight > 0, combined by the terrain's own normalised mix.
vec3 grass_card_ground_all_biomes(vec2 uv, float lod, vec4 weights) {
	vec3 grass = vec3(0.0), desert = vec3(0.0), forest = vec3(0.0), tundra = vec3(0.0), alpine = vec3(0.0);
	if (ring_terrain_grass_weight(weights) > 0.001) grass = grass_card_biome_sample(0, uv, lod);
	if (weights.r > 0.001) desert = grass_card_biome_sample(1, uv, lod);
	if (weights.g > 0.001) forest = grass_card_biome_sample(2, uv, lod);
	if (weights.b > 0.001) tundra = grass_card_biome_sample(3, uv, lod);
	if (weights.a > 0.001) alpine = grass_card_biome_sample(4, uv, lod);
	return ring_terrain_biome_mix(grass, desert, forest, tundra, alpine, weights);
}

// Mip level from the screen-space uv derivatives (call from fragment()).
float grass_card_derivative_lod(vec2 uv, float texel_width) {
	vec2 dx = dFdx(uv) * texel_width;
	vec2 dy = dFdy(uv) * texel_width;
	return max(0.5 * log2(max(dot(dx, dx), dot(dy, dy)) + 1e-6) + grass_card_frag_lod_bias, 0.0);
}

// ground: biome ground colour at the blade/card, r: per-blade random (0..1), g: height gradient (0 root .. 1 tip),
// tuft: per-tuft random. Promoted verbatim from the P0c spike (spike_near_blade_color).
vec3 grass_card_blade_color(vec3 ground, float r, float g, float tuft) {
	float lum = dot(ground, vec3(0.299, 0.587, 0.114));
	vec3 hue = grass_card_hue / dot(grass_card_hue, vec3(0.299, 0.587, 0.114));
	vec3 c = mix(ground, lum * hue, grass_card_green_pull);
	float dry = smoothstep(1.0 - grass_card_dry_fraction - 0.04, 1.0 - grass_card_dry_fraction + 0.02, fract(r * 7.31));
	c = mix(c, grass_card_dry_tone * (0.55 + 1.2 * lum), dry * 0.75);
	c *= mix(vec3(1.0 + grass_card_blade_hue_jitter, 1.0, 1.0 - grass_card_blade_hue_jitter), vec3(1.0 - grass_card_blade_hue_jitter, 1.0, 1.0 + grass_card_blade_hue_jitter), fract(r * 3.71));
	c *= 1.0 + (r - 0.5) * 2.0 * grass_card_blade_value_jitter;
	c *= 1.0 + (tuft - 0.5) * 2.0 * grass_card_tuft_value_jitter;
	float ao = mix(grass_card_root_ao, 1.0, smoothstep(0.0, 0.6, g));
	float tl = 1.0 + grass_card_tip_light * smoothstep(0.5, 1.0, g);
	vec3 tipt = mix(vec3(1.0), grass_card_tip_tint, smoothstep(0.6, 1.0, g));
	return c * ao * tl * tipt;
}
