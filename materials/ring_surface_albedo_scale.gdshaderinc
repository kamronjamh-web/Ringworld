// Live-tuned reflectance multipliers (grave menu). After P3b the terrain / grass light() functions follow the built-in
// LIGHT_COLOR / PI * NdotL convention; these scales retune the authored albedo to the pre-P3b noon look. Terrain scale applies to
// the surface reflectance before haze transmittance and night tint; grass scale applies to blade / card colour only.
global uniform float ringworld_terrain_albedo_scale;
global uniform float ringworld_grass_albedo_scale;

vec3 ring_terrain_albedo_scaled(vec3 albedo) {
	return albedo * ringworld_terrain_albedo_scale;
}

vec3 ring_grass_albedo_scaled(vec3 albedo) {
	return albedo * ringworld_grass_albedo_scale;
}
