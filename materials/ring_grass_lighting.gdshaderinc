// Direct-light path copied for this fixed grass material from Godot Forward+'s
// Lambert + Schlick-GGX implementation. A custom light() is necessary to
// suppress only the configured ring sun while leaving ambient and local lights
// under engine control. Source: servers/rendering/renderer_rd/shaders/
// scene_forward_lights_inc.glsl (Godot Engine, MIT).
uniform bool grass_primary_sun_gate_enabled = false;
uniform vec3 grass_primary_sun_direction = vec3(1.0, 0.0, 0.0);
uniform float grass_primary_sun_day_factor = 1.0;
uniform float grass_primary_sun_night_direct = 0.0;
uniform float grass_primary_sun_day_direct = 1.0;

float grass_schlick_fresnel(float u) {
	float m = 1.0 - u;
	float m2 = m * m;
	return m2 * m2 * m;
}

float grass_d_ggx(float no_h, float roughness) {
	float a = no_h * roughness;
	float k = roughness / max(1e-6, 1.0 - no_h * no_h + a * a);
	return min(k * k * (1.0 / PI), 65504.0);
}

float grass_v_ggx(float no_l, float no_v, float alpha) {
	return clamp(0.5 / max(1e-6, mix(2.0 * no_l * no_v, no_l + no_v, alpha)), 0.0, 65504.0);
}

float grass_primary_sun_visibility(bool is_directional, vec3 light_vector, vec3 sun_view) {
	if (!grass_primary_sun_gate_enabled || !is_directional) return 1.0;
	float primary_sun = smoothstep(.90, .995, dot(normalize(light_vector), sun_view));
	float direct = mix(grass_primary_sun_night_direct, grass_primary_sun_day_direct,
		clamp(grass_primary_sun_day_factor, 0.0, 1.0));
	return mix(1.0, direct, primary_sun);
}
