uniform bool grass_interaction_enabled = false;
uniform int grass_interaction_count = 0;
uniform vec4 grass_interaction_points[8];
uniform float grass_interaction_radius = 1.1;
uniform float grass_interaction_strength = 1.0;
uniform float grass_wind_strength_multiplier = 1.0;
uniform float grass_wind_gust_multiplier = 1.0;

vec3 grass_interaction_lateral(vec3 root, vec3 up, float height) {
	if (!grass_interaction_enabled) return vec3(0.0);
	vec3 sum = vec3(0.0);
	for (int i = 0; i < 8; i++) {
		if (i >= grass_interaction_count) break;
		vec3 delta = root - grass_interaction_points[i].xyz;
		float vertical = abs(dot(delta, up));
		delta -= up * dot(delta, up);
		float distance_to_sample = length(delta);
		float influence = (1.0 - smoothstep(0.0, max(.01, grass_interaction_radius), distance_to_sample)) * grass_interaction_points[i].w * (1.0 - smoothstep(1.25, 2.0, vertical));
		if (distance_to_sample > .001) sum += normalize(delta) * influence;
	}
	float limit = max(.01, height) * .65;
	sum *= clamp(grass_interaction_strength, 0.0, 2.0);
	if (length(sum) > limit) sum *= limit / length(sum);
	return sum;
}
