uniform int grass_debug_mode = 0;
instance uniform vec3 grass_debug_tile_color = vec3(0.0);
vec3 grass_debug_color(float rank, float wind, float interaction, int pass) {
	if (grass_debug_mode == 1) return pass == 0 ? vec3(1.0, .05, .05) : vec3(.05, .2, 1.0);
	if (grass_debug_mode == 2) return vec3(rank, 1.0-rank, .1);
	if (grass_debug_mode == 3) return vec3(wind, wind * .5, 1.0-wind);
	if (grass_debug_mode == 4) return vec3(interaction, .1, 1.0-interaction);
	if (grass_debug_mode == 5) return grass_debug_tile_color;
	return vec3(-1.0);
}
