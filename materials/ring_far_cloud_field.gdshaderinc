uniform float ring_circumference_km = 31415.9;
uniform float ring_width_km = 318.0;
uniform float cloud_seed = 1337.0;
uniform float cloud_world_units_to_km = 0.001;
uniform vec2 cloud_wind_km_per_second = vec2(0.0022, 0.0007);

float ring_cloud_hash21(vec2 p) {
	return fract(
		sin(dot(p, vec2(127.1, 311.7)) + cloud_seed * 0.0174533)
		* 43758.5453);
}

float ring_cloud_value_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(
		mix(ring_cloud_hash21(i), ring_cloud_hash21(i + vec2(1.0, 0.0)), f.x),
		mix(ring_cloud_hash21(i + vec2(0.0, 1.0)), ring_cloud_hash21(i + vec2(1.0, 1.0)), f.x),
		f.y);
}

float ring_far_cloud_field_raw(float angle, float across_km) {
	angle = mod(angle, 6.28318530718);
	if (angle < 0.0) angle += 6.28318530718;
	vec2 loop = vec2(cos(angle), sin(angle));
	float around_noise_radius = max(
		8.0,
		min(1048576.0, ring_circumference_km / 6.28318530718 / 13.0));
	vec2 wind = cloud_wind_km_per_second * TIME;
	vec2 canonical_noise = loop * around_noise_radius
		+ vec2(across_km / 13.0, -across_km / 17.0)
		- wind / 13.0;
	float broad = ring_cloud_value_noise(canonical_noise);
	float shape = ring_cloud_value_noise(
		canonical_noise * 2.35 + vec2(7.1, -4.3));
	float detail = ring_cloud_value_noise(
		canonical_noise * 5.2 + vec2(-13.7, 9.2));
	float macro_bank = ring_cloud_value_noise(
		canonical_noise * 0.22 + vec2(23.9, -17.4));
	float synoptic = ring_cloud_value_noise(
		canonical_noise * 0.075 + vec2(-41.2, 31.8));
	return synoptic * 0.16
		+ macro_bank * 0.27
		+ broad * 0.31
		+ shape * 0.18
		+ detail * 0.08;
}

float ring_far_cloud_field_world(vec3 position, vec3 center) {
	vec3 radial = position - center;
	float angle = atan(radial.z, radial.x);
	float across_km = (position.y - center.y) * cloud_world_units_to_km;
	return smoothstep(0.50, 0.68, ring_far_cloud_field_raw(angle, across_km));
}
