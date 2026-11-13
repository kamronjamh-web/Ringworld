shader_type spatial;
// Shadow-pass material for water surfaces: every vertex goes to one clipped point, so the water draws nothing into the sun shadow map
// and does not shadow its own bed (lakes, rivers, ocean). Terrain surfaces of the same mesh keep the opaque shadow material.
render_mode unshaded, cull_disabled;

void vertex() {
	POSITION = vec4(2.0, 2.0, 2.0, 1.0);
}

void fragment() {
	ALBEDO = vec3(0.0);
}
