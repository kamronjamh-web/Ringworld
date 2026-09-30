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
	return ring_atmosphere_path_interval_shared(target_world, ring_atmosphere_camera_world_position, ring_atmosphere_camera_height_meters, ring_atmosphere_outward_world, ring_atmosphere_axis_world, ring_atmosphere_sidewall_negative_meters, ring_atmosphere_sidewall_positive_meters, ring_atmosphere_coordinate_scale, ring_radius_meters, layer_height_m, max_length_m);
}

vec3 ring_atmosphere_first_air_paths(vec3 target_world) {
	if (!ring_atmosphere_ray_frame_valid) return vec3(-1.0);
	return ring_atmosphere_first_air_paths_shared(target_world, ring_atmosphere_camera_world_position, ring_atmosphere_camera_height_meters, ring_atmosphere_outward_world, ring_atmosphere_axis_world, ring_atmosphere_sidewall_negative_meters, ring_atmosphere_sidewall_positive_meters, ring_atmosphere_coordinate_scale, ring_radius_meters, atmosphere_floor_depth_meters, atmosphere_boundary_height_meters, atmosphere_upper_height_meters);
}
