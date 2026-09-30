// Portable local-air math. No uniforms or Godot-only annotations: terrain and
// Sunshine compute supply their respective frame/profile values explicitly.
vec2 ring_atmosphere_path_interval_shared(vec3 target, vec3 camera, float camera_height, vec3 outward, vec3 axis, float side_negative, float side_positive, float coordinate_scale, float radius_m, float layer_height_m, float max_length_m) {
	float scale = max(coordinate_scale, 0.000001);
	vec3 delta = (target - camera) / scale;
	float length_m = length(delta);
	if (length_m <= 0.0001) return vec2(1.0, 0.0);
	vec3 ray = delta / length_m;
	axis = normalize(axis); outward = normalize(outward);
	vec3 radial_ray = ray - axis * dot(ray, axis);
	float a = dot(radial_ray, radial_ray);
	float r = radius_m / scale;
	float h = camera_height;
	float b = 2.0 * (r - h) * dot(outward, ray);
	float c = (layer_height_m - h) * (2.0 * r - h - layer_height_m);
	vec2 result;
	if (a <= 0.000000000001) result = c <= 0.0 ? vec2(0.0, max_length_m) : vec2(1.0, 0.0);
	else {
		float discriminant = b * b - 4.0 * a * c;
		if (discriminant < 0.0) return vec2(1.0, 0.0);
		float q = -0.5 * (b + (b >= 0.0 ? 1.0 : -1.0) * sqrt(discriminant));
		float t0 = abs(q) > 0.000001 ? q / a : -b / (2.0 * a);
		float t1 = abs(q) > 0.000001 ? c / q : t0;
		result = vec2(min(t0, t1), max(t0, t1));
	}
	float across_direction = dot(ray, axis);
	if (abs(across_direction) > 0.000001) {
		float wall0 = -side_negative / across_direction;
		float wall1 = side_positive / across_direction;
		result = vec2(max(result.x, min(wall0, wall1)), min(result.y, max(wall0, wall1)));
	} else if (side_negative < 0.0 || side_positive < 0.0) return vec2(1.0, 0.0);
	return vec2(max(result.x, 0.0), min(result.y, max_length_m));
}

vec3 ring_atmosphere_first_air_paths_shared(vec3 target, vec3 camera, float camera_height, vec3 outward, vec3 axis, float side_negative, float side_positive, float coordinate_scale, float radius_m, float floor_depth_m, float boundary_height_m, float upper_height_m) {
	float scale = max(coordinate_scale, 0.000001);
	float length_m = length(target - camera) / scale;
	vec2 outer = ring_atmosphere_path_interval_shared(target, camera, camera_height, outward, axis, side_negative, side_positive, scale, radius_m, -floor_depth_m / scale, length_m);
	vec2 upper_vacuum = ring_atmosphere_path_interval_shared(target, camera, camera_height, outward, axis, side_negative, side_positive, scale, radius_m, upper_height_m / scale, length_m);
	if (outer.y <= outer.x) return vec3(0.0);
	// Keep only the first local shell, excluding the remote shell beyond vacuum.
	vec2 air = outer;
	if (upper_vacuum.y > upper_vacuum.x) {
		if (upper_vacuum.x <= air.x) air.x = max(air.x, upper_vacuum.y);
		else air.y = min(air.y, upper_vacuum.x);
	}
	if (air.y <= air.x) return vec3(0.0);
	vec2 boundary_vacuum = ring_atmosphere_path_interval_shared(target, camera, camera_height, outward, axis, side_negative, side_positive, scale, radius_m, boundary_height_m / scale, length_m);
	float air_m = air.y - air.x;
	float boundary_m = boundary_vacuum.y <= boundary_vacuum.x ? air_m : max(0.0, min(air.y, boundary_vacuum.x) - air.x) + max(0.0, air.y - max(air.x, boundary_vacuum.y));
	return vec3(air_m, boundary_m, max(0.0, air_m - boundary_m));
}

vec3 ring_atmosphere_scattering_color_shared(vec3 ray_direction, float day_factor, vec3 to_sun, vec3 profile_day_color, vec3 profile_night_color, float aerosol, float forward_scattering, float sun_warmth) {
	float daylight = clamp(day_factor, 0.0, 1.0);
	float sun_alignment = clamp(dot(normalize(ray_direction), normalize(to_sun)), -1.0, 1.0);
	float g = forward_scattering;
	float forward_lobe = min(3.0, (1.0 - g * g) / max(0.08, pow(1.0 + g * g - 2.0 * g * sun_alignment, 1.5)));
	vec3 base = mix(profile_night_color, profile_day_color, daylight);
	float excess = max(forward_lobe - 1.0, 0.0) * daylight;
	return base + vec3(1.0, 0.55, 0.26) * sun_warmth * excess + base * aerosol * excess;
}

vec4 ring_atmosphere_local_haze_shared(vec3 target, vec3 camera, vec3 outward, vec3 to_sun, float solar_visibility, vec4 p[10]) {
	vec3 paths = ring_atmosphere_first_air_paths_shared(target, camera, p[7].w, p[7].xyz, p[8].xyz, p[8].w, p[9].w, 1.0, p[0].y, p[0].z, p[0].w, p[1].x);
	float boundary = (1.0 - exp(-paths.y / max(p[1].y, 1.0))) * p[2].x;
	float upper = (1.0 - exp(-paths.z / max(p[1].z, 1.0))) * p[2].y;
	float shell = 1.0 - (1.0 - boundary) * (1.0 - upper);
	float altitude = smoothstep(p[4].x, max(p[4].x + 1.0, p[4].y), max(p[7].w, 0.0));
	vec2 visibility = vec2(mix(p[3].x, p[3].z, altitude), mix(p[3].y, p[3].w, altitude));
	float weight = smoothstep(visibility.x, max(visibility.x + 1.0, visibility.y), paths.x);
	float haze = weight * p[2].z;
	float shared_haze_opacity = min(1.0 - (1.0 - shell * weight) * (1.0 - haze), p[1].w);
	float day = smoothstep(-0.2, 0.35, dot(-normalize(outward), normalize(to_sun))) * clamp(solar_visibility, 0.0, 1.0);
	vec3 color = ring_atmosphere_scattering_color_shared(target - camera, day, to_sun, p[5].xyz, p[6].xyz, p[4].z, p[4].w, p[5].w);
	return vec4(color * p[2].w, clamp(shared_haze_opacity, 0.0, 1.0));
}
