global uniform float ring_local_wind_enabled;
global uniform vec2 ring_local_wind_vector;
global uniform vec4 ring_local_wind_wave;
global uniform vec3 ring_local_wind_center;
global uniform vec3 ring_local_wind_axis;
#include "res://materials/ring_wind_field.gdshaderinc"
global uniform vec4 ring_tree_wind_response;
global uniform vec2 ring_tree_wind_trunk;
global uniform vec2 ring_tree_wind_canopy;
global uniform vec2 ring_tree_wind_parts;

// Instances are assumed to have orthogonal (non-sheared) basis columns.  The
// caller converts this world displacement via reciprocal column lengths.
vec3 ring_local_wind_displacement(vec3 root_world_position, float root_weight, float instance_height) {
	if (ring_local_wind_enabled < 0.5) return vec3(0.0);
	if (length(ring_local_wind_axis) < 0.0001) return vec3(0.0);
	vec3 axis = normalize(ring_local_wind_axis);
	vec3 radial = root_world_position - ring_local_wind_center;
	radial -= axis * dot(radial, axis);
	vec2 wind = ring_wind_field_enabled > .5 ? ring_wind_mean_vector : ring_local_wind_vector;
	if (length(radial) < 0.0001 || length(wind) < 0.0001) return vec3(0.0);
	vec3 up = -normalize(radial);
	vec3 around = normalize(cross(axis, up));
	vec3 across = axis;
	vec3 downwind = normalize(around * wind.x + across * wind.y);
	float height = clamp(abs(instance_height), 0.05, 20.0);
	if (ring_wind_field_enabled>.5) {
		// Shared vegetation follows the moving local field. This helper supplies
		// direction and mean response without imposing a uniform sine carrier.
		return downwind*height*.10*(1.0-exp(-length(wind)/20.0))*root_weight*root_weight;
	}
	float variation = fract(height * 0.6180339);
	float sway = ring_local_wind_wave.x * (0.72 + variation * .28) + ring_local_wind_wave.z * .32;
	float wind_response = clamp(length(wind) / 20.0, 0.0, 1.0);
	float bend = min(height * wind_response * 0.10 * (0.34 + sway * .22), height * .16);
	return downwind * max(0.0, bend) * root_weight * root_weight;
}

// A bounded GPU response to the same moving patch as grass. The temporal taps
// represent a short causal filter, so a passing patch builds and recovers more
// slowly in the trunk than the canopy; no shader TIME or per-root CPU state.
float ring_tree_filtered_gust(vec3 root, float response_seconds) {
	float lag = max(.05, response_seconds);
	return ring_wind_gust_at_world(root, 0.0) * .25
		+ ring_wind_gust_at_world(root, lag * .5) * .35
		+ ring_wind_gust_at_world(root, lag * 1.5) * .25
		+ ring_wind_gust_at_world(root, lag * 3.0) * .15;
}

vec3 ring_tree_wind_tip(vec2 wind, vec3 around, vec3 across, float height, float gust) {
	float speed = length(wind);
	if (speed < .0001) return vec3(0.0);
	float limit = clamp(ring_tree_wind_response.y, 0.0, .6);
	float response = (1.0 - exp(-speed / 30.0)) * max(0.0, ring_tree_wind_response.x) * max(0.0, gust);
	return normalize(around * wind.x + across * wind.y) * height * limit * (1.0-exp(-response));
}

vec3 ring_tree_wind_displacement(vec3 root, float weight, float instance_height, out vec3 derivative) {
	derivative = vec3(0.0);
	if (ring_wind_field_enabled < .5) {
		vec3 tip = ring_local_wind_displacement(root, 1.0, instance_height);
		derivative = tip * (2.0 * weight);
		return tip * weight * weight;
	}
	if (ring_local_wind_enabled < .5 || length(ring_local_wind_axis) < .0001) return vec3(0.0);
	vec3 axis = normalize(ring_local_wind_axis);
	vec3 radial = root - ring_local_wind_center;
	radial -= axis * dot(radial, axis);
	if (length(radial) < .0001) return vec3(0.0);
	vec3 around = normalize(cross(axis, -normalize(radial)));
	float height = max(.05, abs(instance_height));
	vec3 trunk = ring_tree_wind_tip(ring_tree_wind_trunk, around, axis, height,
		ring_tree_filtered_gust(root, ring_tree_wind_response.z));
	float w = clamp(weight, 0.0, 1.0);
	// Strength scales the saturated structural response, so even extreme
	// storms cannot turn a stiff trunk back into a freely bending crown.
	vec3 tip = trunk * (.25 * clamp(ring_tree_wind_parts.x, 0.0, 1.0));
	derivative = tip * (2.0 * w);
	return tip * w * w;
}

// CUSTOM0 stores a lossless prototype attachment and inverse island radius.
// Mode 0 is wood, 1 is attached foliage, 2 is the combined far-card silhouette.
// The phase is constant over an island, hence its spatial derivative is zero.
void ring_tree_wind_geometry(vec3 root, vec3 source, mat3 basis, vec4 attachment,
	int foliage_mode, bool attached, out vec3 local_offset, out mat3 jacobian) {
	vec3 derivative;
	float weight = clamp(source.y, 0.0, 1.0);
	vec3 structure = ring_tree_wind_displacement(root, weight, length(basis[1]), derivative);
	if (source.y<0.0 || source.y>1.0) derivative=vec3(0.0);
	vec3 lengths2 = max(vec3(.000001), vec3(dot(basis[0],basis[0]),dot(basis[1],basis[1]),dot(basis[2],basis[2])));
	vec3 local_structure_derivative = transpose(basis) * derivative / lengths2;
	local_offset = transpose(basis) * structure / lengths2;
	jacobian = mat3(vec3(1.0,0.0,0.0),vec3(0.0,1.0,0.0)+local_structure_derivative,vec3(0.0,0.0,1.0));
	if (ring_wind_field_enabled < .5 || ring_local_wind_enabled < .5 || length(ring_local_wind_axis)<.0001 || foliage_mode == 0 || ring_tree_wind_parts.y <= 0.0) return;
	vec3 anchor = attachment.xyz;
	float inverse_radius = attachment.w;
	float flex;
	vec3 gradient;
	float radius;
	if (foliage_mode == 1 && attached) {
		if (inverse_radius <= .0001) return; // no reliable local bark: structural only.
		vec3 lever = source - anchor;
		float scaled_distance2 = dot(lever,lever) * inverse_radius * inverse_radius;
		flex = min(1.0, scaled_distance2);
		gradient = scaled_distance2 < 1.0 ? 2.0 * lever * inverse_radius * inverse_radius : vec3(0.0);
		radius = 1.0 / inverse_radius;
	} else {
		// Authored far cards contain both bark and leaves in one atlas. Restrict
		// extra flex to the crown, keeping their entire lower stem on structure.
		float crown = clamp((source.y - .35) / .65, 0.0, 1.0);
		flex = crown * crown;
		gradient = vec3(0.0, source.y > .35 && source.y < 1.0 ? 2.0*crown/.65 : 0.0, 0.0);
		anchor = vec3(0.0,.35,0.0);
		radius = .65;
	}
	float height = max(.05,length(basis[1]));
	float minimum_scale = min(length(basis[0]),min(length(basis[1]),length(basis[2])));
	float remaining = height * clamp(ring_tree_wind_response.y,0.0,.6) * (1.0-.25*clamp(ring_tree_wind_parts.x,0.0,1.0));
	// Min basis scale is conservative on every orthogonal nonuniform instance:
	// flex <= .32 of each vertex's actual world attachment-to-tip lever.
	float amplitude = min(remaining, .32 * minimum_scale * radius);
	float phase = dot(anchor,vec3(31.0,17.0,23.0)) + 6.28318530718*ring_wind_noise_at_world(root,0.0)
		+ 5.0*atan(ring_wind_phase.z,ring_wind_phase.w);
	float gust = ring_tree_filtered_gust(root,ring_tree_wind_response.w);
	vec3 axis = normalize(ring_local_wind_axis);
	vec3 radial = root-ring_local_wind_center; radial -= axis*dot(radial,axis);
	if (length(radial)<.0001) return;
	vec3 around = normalize(cross(axis,-normalize(radial)));
	vec3 wind_tip = ring_tree_wind_tip(ring_tree_wind_canopy,around,axis,height,gust*clamp(ring_tree_wind_parts.y,0.0,3.0));
	float full_envelope = height * clamp(ring_tree_wind_response.y,0.0,.6);
	vec3 flex_world = full_envelope > .0001 ? wind_tip/full_envelope * amplitude * (.9+.1*sin(phase)) : vec3(0.0);
	vec3 local_flex = transpose(basis) * flex_world / lengths2;
	local_offset += local_flex * flex;
	jacobian[0] += local_flex * gradient.x;
	jacobian[1] += local_flex * gradient.y;
	jacobian[2] += local_flex * gradient.z;
}

vec3 ring_tree_deformed_normal(mat3 jacobian, vec3 normal) {
	return normalize(cross(jacobian[1],jacobian[2])*normal.x
		+cross(jacobian[2],jacobian[0])*normal.y+cross(jacobian[0],jacobian[1])*normal.z);
}
