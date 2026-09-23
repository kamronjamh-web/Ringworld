shader_type spatial;
#include "res://materials/celestial_lighting.gdshaderinc"
render_mode cull_back, depth_draw_opaque;

uniform vec4 base_color : source_color = vec4(0.44, 0.46, 0.5, 1.0);
uniform vec4 night_tint : source_color = vec4(0.30, 0.40, 0.58, 1.0);
uniform vec3 ring_center = vec3(0.0, 0.0, 0.0);
uniform vec3 sun_direction = vec3(0.0, 1.0, 0.0);
uniform float day_start_dot = -0.2;
uniform float day_full_dot = 0.35;
uniform float night_brightness : hint_range(0.0, 1.0) = 0.45;
uniform float direct_light_night_factor : hint_range(0.0, 1.0) = 0.0;
uniform float roughness_value : hint_range(0.0, 1.0) = 0.95;
uniform float metallic_value : hint_range(0.0, 1.0) = 0.05;
uniform float ring_radius_meters = 80000.0;
uniform float ring_width_meters = 20000.0;
// Terrain and structural geometry use shared material variants. Keeping this
// material-level avoids consuming one renderer instance-uniform slot per ring
// segment when the streaming window is large.
uniform float biome_color_strength : hint_range(0.0, 1.0) = 0.0;
uniform vec4 biome_grassland_color : source_color = vec4(0.22, 0.34, 0.14, 1.0);
uniform vec4 biome_tundra_color : source_color = vec4(0.60, 0.64, 0.57, 1.0);
uniform vec4 biome_desert_color : source_color = vec4(0.51, 0.42, 0.22, 1.0);
uniform vec4 biome_forest_color : source_color = vec4(0.10, 0.30, 0.12, 1.0);
uniform vec4 biome_alpine_color : source_color = vec4(0.38, 0.39, 0.40, 1.0);

varying vec3 world_pos;
varying float ring_day_factor;


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
	float across = clamp(world_pos.y + ring_width_meters * 0.5, 0.0, ring_width_meters);
	vec2 climate_point = vec2(around / 180000.0, across / 42000.0);
	float temperature = biome_value_noise(climate_point + vec2(13.7, 5.1));
	float moisture = biome_value_noise(climate_point * 1.31 + vec2(41.2, 19.6));
	float ridge = biome_value_noise(climate_point * 2.07 + vec2(7.4, 61.9));

	vec3 regional = biome_grassland_color.rgb;
	regional = mix(regional, biome_forest_color.rgb, smoothstep(0.57, 0.78, moisture) * smoothstep(0.22, 0.86, temperature));
	regional = mix(regional, biome_desert_color.rgb, smoothstep(0.68, 0.92, temperature) * (1.0 - smoothstep(0.20, 0.42, moisture)));
	regional = mix(regional, biome_tundra_color.rgb, 1.0 - smoothstep(0.20, 0.36, temperature));
	return mix(regional, biome_alpine_color.rgb, smoothstep(0.70, 0.90, ridge));
}


void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;

	vec3 radial = world_pos - ring_center;
	radial.y = 0.0;
	float radial_len = length(radial);
	vec3 outward = radial_len > 0.0001 ? radial / radial_len : vec3(1.0, 0.0, 0.0);
	vec3 surface_up = -outward;

	float ndotl = dot(surface_up, normalize(sun_direction));
	ring_day_factor = ringworld_effective_solar_day(smoothstep(day_start_dot, day_full_dot, ndotl));
}


void fragment() {
	vec3 radial = world_pos - ring_center;
	radial.y = 0.0;
	float radial_len = length(radial);
	vec3 outward = radial_len > 0.0001 ? radial / radial_len : vec3(1.0, 0.0, 0.0);
	vec3 surface_up = -outward;

	float day_factor = ring_day_factor;
	float side_brightness = mix(night_brightness, 1.0, day_factor);

	vec3 terrain_color = mix(base_color.rgb, resolve_biome_palette_color(), biome_color_strength);
	vec3 day_color = terrain_color;
	vec3 night_color = terrain_color * night_tint.rgb;
	ALBEDO = mix(night_color, day_color, day_factor) * side_brightness;
	ROUGHNESS = roughness_value;
	METALLIC = metallic_value;
}


void light() {
	vec3 sun_direction_view = normalize((VIEW_MATRIX * vec4(sun_direction, 0.0)).xyz);
	float primary_sun_weight = LIGHT_IS_DIRECTIONAL
		? smoothstep(0.90, 0.995, dot(normalize(LIGHT), sun_direction_view))
		: 0.0;
	float sun_hemisphere_direct = mix(direct_light_night_factor, 1.0, ring_day_factor);
	float hemisphere_direct = mix(1.0, sun_hemisphere_direct, primary_sun_weight);
	float n_dot_l = max(dot(NORMAL, LIGHT), 0.0);
	DIFFUSE_LIGHT += ALBEDO * LIGHT_COLOR * n_dot_l * ATTENUATION * hemisphere_direct;
}
