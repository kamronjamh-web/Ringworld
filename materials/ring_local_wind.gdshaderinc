global uniform float ring_local_wind_enabled;
global uniform vec2 ring_local_wind_vector;
global uniform vec4 ring_local_wind_wave;
global uniform vec3 ring_local_wind_center;
global uniform vec3 ring_local_wind_axis;

// Instances are assumed to have orthogonal (non-sheared) basis columns.  The
// caller converts this world displacement via reciprocal column lengths.
vec3 ring_local_wind_displacement(vec3 root_world_position, float root_weight, float instance_height) {
	if (ring_local_wind_enabled < 0.5) return vec3(0.0);
	if (length(ring_local_wind_axis) < 0.0001) return vec3(0.0);
	vec3 axis = normalize(ring_local_wind_axis);
	vec3 radial = root_world_position - ring_local_wind_center;
	radial -= axis * dot(radial, axis);
	if (length(radial) < 0.0001 || length(ring_local_wind_vector) < 0.0001) return vec3(0.0);
	vec3 up = -normalize(radial);
	vec3 around = normalize(cross(axis, up));
	vec3 across = axis;
	vec3 downwind = normalize(around * ring_local_wind_vector.x + across * ring_local_wind_vector.y);
	float height = clamp(abs(instance_height), 0.05, 20.0);
	float variation = fract(height * 0.6180339);
	float sway = ring_local_wind_wave.x * (0.72 + variation * .28) + ring_local_wind_wave.z * .32;
	float wind_response = clamp(length(ring_local_wind_vector) / 20.0, 0.0, 1.0);
	float bend = min(height * wind_response * 0.10 * (0.34 + sway * .22), height * .16);
	return downwind * max(0.0, bend) * root_weight * root_weight;
}
