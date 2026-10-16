// Runtime density / grow-in / scale curve of the grass cards: the GLSL twin of RingGrassCardCurve.cs (C# is the single
// source of truth; tools/grass_card_color_match_gpu_test.gd mode=curve compares the two). Pure math plus the curve
// uniforms, so it can be included from any shader type. Every value is a material uniform fed by RingGrassCardMaterialConfig.
uniform float grass_card_reach = 400.0;
uniform float grass_card_near_density = 14.0;
uniform float grass_card_base_density = 2.5;
uniform float grass_card_floor_density = 0.15;
uniform float grass_card_falloff_exponent = 1.3;
uniform float grass_card_falloff_start = 20.0;
uniform vec2 grass_card_near_boost = vec2(8.0, 45.0);      // metres: boost fully on / fully off
uniform float grass_card_comp_max = 2.5;
uniform float grass_card_near_width = 0.7;
uniform float grass_card_fade_band_percent = 15.0;
uniform vec2 grass_card_low_layer = vec2(8.0, 22.0);       // metres: full / gone
uniform vec3 grass_card_hero_thin = vec3(0.4, 3.0, 7.5);   // minimum keep, thin-start m, thin-end m (min 1 disables)
uniform float grass_card_min_grow_in = 0.08;

float grass_card_smooth(float a, float b, float x) {
	float t = clamp((x - a) / (b - a), 0.0, 1.0);
	return t * t * (3.0 - 2.0 * t);
}

float grass_card_far_density(float d) {
	return clamp(grass_card_base_density * pow(grass_card_falloff_start / max(d, grass_card_falloff_start), grass_card_falloff_exponent),
		grass_card_floor_density, grass_card_base_density);
}

float grass_card_density(float d) {
	float far_d = grass_card_far_density(d);
	if (grass_card_near_density <= grass_card_base_density) return far_d;
	return far_d + (grass_card_near_density - far_d) * (1.0 - grass_card_smooth(grass_card_near_boost.x, grass_card_near_boost.y, d));
}

float grass_card_keep(float d) { return min(1.0, grass_card_density(d) / max(0.01, grass_card_near_density)); }
float grass_card_grow_in(float keep, float rank) { return grass_card_smooth(0.0, 1.0, (keep - rank) / max(0.02, 0.2 * keep)); }
float grass_card_compensation(float d) {
	return min(1.0 / sqrt(min(1.0, grass_card_density(d) / max(0.01, grass_card_base_density))), grass_card_comp_max);
}
float grass_card_near_width_scale(float d) { return mix(grass_card_near_width, 1.0, grass_card_smooth(grass_card_near_boost.x, grass_card_near_boost.y, d)); }
float grass_card_fade(float d) {
	return 1.0 - grass_card_smooth(grass_card_reach * (1.0 - grass_card_fade_band_percent * 0.01), grass_card_reach, d);
}
float grass_card_hero_thin_factor(float rank, float d) {
	float keep = mix(grass_card_hero_thin.x, 1.0, grass_card_smooth(grass_card_hero_thin.y, grass_card_hero_thin.z, d));
	return keep >= 0.999 ? 1.0 : grass_card_smooth(0.0, 0.15, keep - rank * 0.999);
}

// The full card scale product the C# reference calls Scale() (0 = culled).
float grass_card_curve_scale(float rank, float d) {
	float keep = grass_card_keep(d);
	if (rank >= keep) return 0.0;
	float grow = grass_card_grow_in(keep, rank);
	if (grow < grass_card_min_grow_in) return 0.0;
	return grass_card_compensation(d) * grow * grass_card_near_width_scale(d) * grass_card_fade(d) * grass_card_hero_thin_factor(rank, d);
}

float grass_card_low_keep(float d) { return 1.0 - grass_card_smooth(grass_card_low_layer.x, grass_card_low_layer.y, d); }
float grass_card_low_scale(float rank, float d) {
	float keep = grass_card_low_keep(d);
	if (rank >= keep) return 0.0;
	float grow = grass_card_grow_in(keep, rank);
	return grow < grass_card_min_grow_in ? 0.0 : grow;
}

