// Local finite-cylinder air segment. Values are physical metres; only world
// positions are divided by coordinate_scale. The remote shell is excluded.
uniform bool ring_atmosphere_ray_frame_valid = false;
uniform vec3 ring_atmosphere_camera_world_position;
uniform float ring_atmosphere_camera_height_meters;
uniform vec3 ring_atmosphere_outward_world;
uniform vec3 ring_atmosphere_axis_world;
uniform float ring_atmosphere_sidewall_negative_meters;
uniform float ring_atmosphere_sidewall_positive_meters;
uniform float ring_atmosphere_coordinate_scale = 1.0;

vec2 ring_atmosphere_path_interval(vec3 target_world, float layer_height_m, float max_length_m) {
	float scale = max(ring_atmosphere_coordinate_scale, 0.000001);
	vec3 delta = (target_world - ring_atmosphere_camera_world_position) / scale;
	float length_m = length(delta);
	if (length_m <= 0.0001) return vec2(1.0, 0.0);
	vec3 ray = delta / length_m;
	vec3 axis = normalize(ring_atmosphere_axis_world);
	vec3 outward = normalize(ring_atmosphere_outward_world);
	vec3 radial_ray = ray - axis * dot(ray, axis);
	float a = dot(radial_ray, radial_ray);
	float r = ring_radius_meters / scale;
	float h = ring_atmosphere_camera_height_meters;
	float b = 2.0 * (r - h) * dot(outward, ray);
	float c = (layer_height_m - h) * (2.0 * r - h - layer_height_m);
	vec2 result;
	if (a <= 0.000000000001) {
		result = c <= 0.0 ? vec2(0.0, max_length_m) : vec2(1.0, 0.0);
	} else {
		float discriminant = b * b - 4.0 * a * c;
		if (discriminant < 0.0) return vec2(1.0, 0.0);
		float q = -0.5 * (b + (b >= 0.0 ? 1.0 : -1.0) * sqrt(discriminant));
		float t0 = abs(q) > 0.000001 ? q / a : -b / (2.0 * a);
		float t1 = abs(q) > 0.000001 ? c / q : t0;
		result = vec2(min(t0, t1), max(t0, t1));
	}
	float across_direction = dot(ray, axis);
	if (abs(across_direction) > 0.000001) {
		float wall0 = -ring_atmosphere_sidewall_negative_meters / across_direction;
		float wall1 = ring_atmosphere_sidewall_positive_meters / across_direction;
		result = vec2(max(result.x, min(wall0, wall1)), min(result.y, max(wall0, wall1)));
	} else if (ring_atmosphere_sidewall_negative_meters < 0.0 || ring_atmosphere_sidewall_positive_meters < 0.0) {
		return vec2(1.0, 0.0);
	}
	return vec2(max(result.x, 0.0), min(result.y, max_length_m));
}

vec3 ring_atmosphere_first_air_paths(vec3 target_world) {
	if (!ring_atmosphere_ray_frame_valid) return vec3(-1.0);
	float scale = max(ring_atmosphere_coordinate_scale, 0.000001);
	float length_m = length(target_world - ring_atmosphere_camera_world_position) / scale;
	vec2 outer = ring_atmosphere_path_interval(target_world, -atmosphere_floor_depth_meters / scale, length_m);
	vec2 upper_vacuum = ring_atmosphere_path_interval(target_world, atmosphere_upper_height_meters / scale, length_m);
	if (outer.y <= outer.x) return vec3(0.0);
	vec2 air = outer;
	if (upper_vacuum.y > upper_vacuum.x) {
		if (upper_vacuum.x <= air.x) air.x = max(air.x, upper_vacuum.y);
		else air.y = min(air.y, upper_vacuum.x);
	}
	if (air.y <= air.x) return vec3(0.0);
	vec2 boundary_vacuum = ring_atmosphere_path_interval(target_world, atmosphere_boundary_height_meters / scale, length_m);
	float air_m = air.y - air.x;
	float boundary_m = boundary_vacuum.y <= boundary_vacuum.x ? air_m : max(0.0, min(air.y, boundary_vacuum.x) - air.x) + max(0.0, air.y - max(air.x, boundary_vacuum.y));
	return vec3(air_m, boundary_m, max(0.0, air_m - boundary_m));
}
