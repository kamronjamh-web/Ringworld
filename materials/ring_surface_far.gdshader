shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque;

#include "res://materials/celestial_lighting.gdshaderinc"

uniform vec4 base_color : source_color = vec4(0.23, 0.29, 0.37, 1.0);
uniform vec4 day_atmosphere_color : source_color = vec4(0.35, 0.52, 0.74, 1.0);
uniform vec4 night_atmosphere_color : source_color = vec4(0.06, 0.1, 0.16, 1.0);

uniform vec3 ring_center = vec3(0.0, 0.0, 0.0);
uniform vec3 sun_direction = vec3(0.0, 1.0, 0.0);

uniform float day_start_dot = -0.2;
uniform float day_full_dot = 0.35;
uniform float tint_strength : hint_range(0.0, 2.0) = 0.0;
uniform float min_tint_distance = 50000.0;
uniform float max_tint_distance = 260000.0;
uniform float atmospheric_mix : hint_range(0.0, 1.0) = 0.8;
uniform float emission_strength : hint_range(0.0, 1.0) = 0.12;
uniform float roughness_value : hint_range(0.0, 1.0) = 0.98;
// The legacy whole-ring backdrop is intentionally analytic: a single material
// shades every low-cost box segment and does not require per-segment texture
// uploads. Palette values come from the shared TerrainBiomeCatalog at runtime.
uniform float ring_radius_meters = 80000.0;
uniform float ring_width_meters = 20000.0;
uniform float biome_color_strength : hint_range(0.0, 1.0) = 0.92;
uniform vec4 biome_grassland_color : source_color = vec4(0.22, 0.34, 0.14, 1.0);
uniform vec4 biome_tundra_color : source_color = vec4(0.60, 0.64, 0.57, 1.0);
uniform vec4 biome_desert_color : source_color = vec4(0.51, 0.42, 0.22, 1.0);
uniform vec4 biome_forest_color : source_color = vec4(0.10, 0.30, 0.12, 1.0);
uniform vec4 biome_alpine_color : source_color = vec4(0.38, 0.39, 0.40, 1.0);

varying vec3 world_pos;


float biome_hash(vec2 point) {
	return fract(sin(dot(point, vec2(127.1, 311.7))) * 43758.5453123);
}


float biome_value_noise(vec2 point) {
	vec2 cell = floor(point);
	vec2 fraction = fract(point);
	fraction = fraction * fraction * (3.0 - 2.0 * fraction);
	return mix(
		mix(biome_hash(cell), biome_hash(cell + vec2(1.0, 0.0)), fraction.x),
		mix(biome_hash(cell + vec2(0.0, 1.0)), biome_hash(cell + vec2(1.0, 1.0)), fraction.x),
		fraction.y);
}


vec3 resolve_biome_palette_color() {
	vec3 radial = world_pos - ring_center;
	float angle = atan(radial.z, radial.x);
	float around = (angle / (2.0 * PI) + 0.5) * (2.0 * PI * max(ring_radius_meters, 1.0));
	// Width is measured from the ring's centreline so the climate field does
	// not introduce a visible seam at the structural edge.
	float across = clamp(world_pos.y + ring_width_meters * 0.5, 0.0, ring_width_meters);
	vec2 climate_point = vec2(around / 180000.0, across / 42000.0);
	float temperature = biome_value_noise(climate_point + vec2(13.7, 5.1));
	float moisture = biome_value_noise(climate_point * 1.31 + vec2(41.2, 19.6));
	float ridge = biome_value_noise(climate_point * 2.07 + vec2(7.4, 61.9));

	vec3 regional = biome_grassland_color.rgb;
	regional = mix(regional, biome_forest_color.rgb, smoothstep(0.57, 0.78, moisture) * smoothstep(0.22, 0.86, temperature));
	regional = mix(regional, biome_desert_color.rgb, smoothstep(0.68, 0.92, temperature) * (1.0 - smoothstep(0.20, 0.42, moisture)));
	regional = mix(regional, biome_tundra_color.rgb, 1.0 - smoothstep(0.20, 0.36, temperature));
	// Rocky alpine colour is an overlay, exactly as the voxel visual model
	// exposes rock above its climate-driven regional colour.
	return mix(regional, biome_alpine_color.rgb, smoothstep(0.70, 0.90, ridge));
}


void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}


void fragment() {
	vec3 radial = world_pos - ring_center;
	radial.y = 0.0;
	float radial_len = length(radial);
	vec3 outward = radial_len > 0.0001 ? radial / radial_len : vec3(1.0, 0.0, 0.0);
	vec3 surface_up = -outward;

	vec3 to_sun = normalize(sun_direction);
	float ndotl = dot(surface_up, to_sun);
	float day_factor = ringworld_effective_solar_day(smoothstep(day_start_dot, day_full_dot, ndotl));

	float world_distance = distance(CAMERA_POSITION_WORLD, world_pos);
	float distance_factor = smoothstep(min_tint_distance, max_tint_distance, world_distance);

	vec3 atmosphere_color = mix(night_atmosphere_color.rgb, day_atmosphere_color.rgb, day_factor);
	float tint_factor = clamp(distance_factor * tint_strength, 0.0, 1.0);

	vec3 terrain_color = mix(base_color.rgb, resolve_biome_palette_color(), biome_color_strength);
	ALBEDO = mix(terrain_color, atmosphere_color, tint_factor * atmospheric_mix);
	EMISSION = atmosphere_color * (tint_factor * emission_strength);
	ROUGHNESS = roughness_value;
	METALLIC = 0.0;
}
