uniform sampler2D local_cloud_shadow_texture : filter_linear, repeat_disable;
uniform sampler2D local_cloud_shadow_far_texture : filter_linear, repeat_disable;
uniform bool local_cloud_shadow_enabled = false;
uniform bool local_cloud_shadow_near_enabled = false;
uniform bool local_cloud_shadow_far_enabled = false;
uniform vec3 local_cloud_shadow_center_world = vec3(0.0);
uniform vec3 local_cloud_shadow_axis_around = vec3(0.0, 0.0, 1.0);
uniform vec3 local_cloud_shadow_axis_across = vec3(0.0, 1.0, 0.0);
uniform vec3 local_cloud_shadow_axis_up = vec3(-1.0, 0.0, 0.0);
uniform float local_cloud_shadow_radius_meters = 50000.0;
uniform vec2 local_cloud_shadow_uv_offset = vec2(0.0);
uniform vec3 local_cloud_shadow_far_center_world = vec3(0.0);
uniform vec3 local_cloud_shadow_far_axis_around = vec3(0.0, 0.0, 1.0);
uniform vec3 local_cloud_shadow_far_axis_across = vec3(0.0, 1.0, 0.0);
uniform vec3 local_cloud_shadow_far_axis_up = vec3(-1.0, 0.0, 0.0);
uniform float local_cloud_shadow_far_radius_meters = 96000.0;
uniform vec2 local_cloud_shadow_far_uv_offset = vec2(0.0);
uniform float local_cloud_shadow_ring_radius_meters = 0.0;
uniform vec3 local_cloud_shadow_sun_direction = vec3(0.0);
uniform bool local_cloud_shadow_height_projection_enabled = false;
uniform float local_cloud_shadow_strength : hint_range(0.0, 1.0) = 0.45;
uniform float local_cloud_shadow_far_strength : hint_range(0.0, 1.0) = 0.60;
uniform float local_cloud_shadow_near_start_meters = 1800.0;
uniform float local_cloud_shadow_near_end_meters = 3500.0;
uniform float local_cloud_shadow_far_strength_start_meters = 3500.0;
uniform float local_cloud_shadow_far_strength_end_meters = 5000.0;
// Registered receivers read the projector's camera-following transforms from
// shared globals (published once per change) instead of per-material writes.
// Unregistered fixtures keep the per-material uniforms above.
uniform bool local_cloud_shadow_use_globals = false;
global uniform vec3 ring_cloud_shadow_near_center_world;
global uniform vec3 ring_cloud_shadow_near_axis_around;
global uniform vec3 ring_cloud_shadow_near_axis_across;
global uniform vec3 ring_cloud_shadow_near_axis_up;
global uniform vec2 ring_cloud_shadow_near_uv_offset;
global uniform vec3 ring_cloud_shadow_far_center_world;
global uniform vec3 ring_cloud_shadow_far_axis_around;
global uniform vec3 ring_cloud_shadow_far_axis_across;
global uniform vec3 ring_cloud_shadow_far_axis_up;
global uniform vec2 ring_cloud_shadow_far_uv_offset;
// xy: far planar coordinates minus near planar coordinates (the cascades share one
// ring plane, so far = near + xy); z = 1 when valid, 0 keeps a separate far projection.
uniform vec3 local_cloud_shadow_far_from_near = vec3(0.0);
global uniform vec3 ring_cloud_shadow_far_from_near;
vec3 ring_cloud_shadow_near_center() { return local_cloud_shadow_use_globals ? ring_cloud_shadow_near_center_world : local_cloud_shadow_center_world; }
vec3 ring_cloud_shadow_near_around() { return local_cloud_shadow_use_globals ? ring_cloud_shadow_near_axis_around : local_cloud_shadow_axis_around; }
vec3 ring_cloud_shadow_near_across() { return local_cloud_shadow_use_globals ? ring_cloud_shadow_near_axis_across : local_cloud_shadow_axis_across; }
vec3 ring_cloud_shadow_near_up() { return local_cloud_shadow_use_globals ? ring_cloud_shadow_near_axis_up : local_cloud_shadow_axis_up; }
vec2 ring_cloud_shadow_near_offset() { return local_cloud_shadow_use_globals ? ring_cloud_shadow_near_uv_offset : local_cloud_shadow_uv_offset; }
vec3 ring_cloud_shadow_far_center() { return local_cloud_shadow_use_globals ? ring_cloud_shadow_far_center_world : local_cloud_shadow_far_center_world; }
vec3 ring_cloud_shadow_far_around() { return local_cloud_shadow_use_globals ? ring_cloud_shadow_far_axis_around : local_cloud_shadow_far_axis_around; }
vec3 ring_cloud_shadow_far_across() { return local_cloud_shadow_use_globals ? ring_cloud_shadow_far_axis_across : local_cloud_shadow_far_axis_across; }
vec3 ring_cloud_shadow_far_up() { return local_cloud_shadow_use_globals ? ring_cloud_shadow_far_axis_up : local_cloud_shadow_far_axis_up; }
vec2 ring_cloud_shadow_far_offset() { return local_cloud_shadow_use_globals ? ring_cloud_shadow_far_uv_offset : local_cloud_shadow_far_uv_offset; }
vec3 ring_cloud_shadow_far_link() { return local_cloud_shadow_use_globals ? ring_cloud_shadow_far_from_near : local_cloud_shadow_far_from_near; }
// 0.0 keeps overlapping shadow systems energy-conserving by using the darkest
// single estimate. 1.0 restores fully compounded shadow darkening.
uniform float shadow_overlap_darkening : hint_range(0.0, 1.0, 0.01) = 0.0;

float combine_ring_shadow_visibility(
	float realtime_visibility,
	float cloud_occlusion,
	float terrain_occlusion) {
	float clamped_cloud_occlusion = clamp(cloud_occlusion, 0.0, 1.0);
	// Clouds attenuate direct sunlight; they cannot restore direct light behind
	// opaque geometry. Diffuse overcast illumination belongs to ambient lighting,
	// so preserve both real shadow-map and terrain-mask occlusion at every cloud level.
	float close_visibility = clamp(realtime_visibility, 0.0, 1.0);
	float cloud_visibility = 1.0 - clamped_cloud_occlusion;
	float terrain_visibility = 1.0 - clamp(terrain_occlusion, 0.0, 1.0);
	float strongest_visibility = min(close_visibility, min(cloud_visibility, terrain_visibility));
	float compounded_visibility = close_visibility * cloud_visibility * terrain_visibility;
	return mix(
		strongest_visibility,
		compounded_visibility,
		clamp(shadow_overlap_darkening, 0.0, 1.0));
}

// Small-angle series replace atan/sin/cos. Cloud-shadow angles stay below 0.02 rad
// (100 km on the 5000 km ring); the series are accurate to well under a millimetre
// out to 0.05 rad and the exact functions take over beyond that.
float ring_cloud_shadow_arc_angle(float tangent, float outward) {
	float q = tangent / outward;
	if (outward > 0.0 && abs(q) <= 0.05) {
		float q2 = q * q;
		return q * (1.0 - q2 * (1.0 / 3.0 - q2 * (1.0 / 5.0 - q2 * (1.0 / 7.0))));
	}
	return atan(tangent, outward);
}

vec2 ring_cloud_shadow_sin_cos(float angle) {
	if (abs(angle) <= 0.05) {
		float a2 = angle * angle;
		return vec2(
			angle * (1.0 - a2 * (1.0 / 6.0) * (1.0 - a2 * (1.0 / 20.0) * (1.0 - a2 * (1.0 / 42.0)))),
			1.0 - a2 * 0.5 * (1.0 - a2 * (1.0 / 12.0) * (1.0 - a2 * (1.0 / 30.0) * (1.0 - a2 * (1.0 / 56.0)))));
	}
	return vec2(sin(angle), cos(angle));
}

vec3 ring_cloud_shadow_coordinates(
		vec3 relative,
		vec3 axis_around,
		vec3 axis_across,
		vec3 axis_up) {
	float tangent = dot(relative, axis_around);
	float across = dot(relative, axis_across);
	float inward = dot(relative, axis_up);
	float ring_radius = local_cloud_shadow_ring_radius_meters;
	if (ring_radius <= 1.0) {
		return vec3(tangent, across, inward);
	}

	float outward_radius = ring_radius - inward;
	float arc = ring_radius * ring_cloud_shadow_arc_angle(tangent, outward_radius);
	float altitude = ring_radius - length(vec2(tangent, outward_radius));
	vec2 planar = vec2(arc, across);
	if (local_cloud_shadow_height_projection_enabled) {
		float angle = arc / ring_radius;
		vec2 sine_cosine = ring_cloud_shadow_sin_cos(angle);
		float sine = sine_cosine.x;
		float cosine = sine_cosine.y;
		vec3 local_around = axis_around * cosine + axis_up * sine;
		vec3 local_up = axis_up * cosine - axis_around * sine;
		// The bridge supplies a normalized world-space sun ray. Avoid doing this
		// uniform normalization for every terrain/vegetation receiver fragment.
		vec3 cloud_shadow_sun_ray = local_cloud_shadow_sun_direction;
		float sun_up = dot(cloud_shadow_sun_ray, local_up);
		if (sun_up > 0.04) {
			planar -= vec2(
				dot(cloud_shadow_sun_ray, local_around),
				dot(cloud_shadow_sun_ray, axis_across)) * (altitude / sun_up);
		}
	}
	return vec3(planar, altitude);
}

// GPU phase maps store two independently rotated integration phases in R/G.
// Combining their transmittance preserves detail between updates. Legacy CPU
// maps are R8, which read back as G = 0 and therefore retain their old result.
float combine_ring_cloud_shadow_phases(vec2 phases) {
	return 1.0 - (1.0 - phases.r) * (1.0 - phases.g);
}

float sample_local_cloud_shadow(vec3 sample_world_position) {
	if (!local_cloud_shadow_enabled) {
		return 0.0;
	}

	vec3 relative = sample_world_position - ring_cloud_shadow_near_center();
	vec2 planar_meters = ring_cloud_shadow_coordinates(
		relative,
		ring_cloud_shadow_near_around(),
		ring_cloud_shadow_near_across(),
		ring_cloud_shadow_near_up()).xy;
	float radius = max(local_cloud_shadow_radius_meters, 1.0);
	vec2 uv = planar_meters / (radius * 2.0) + vec2(0.5) + ring_cloud_shadow_near_offset();
	float planar_distance = length(planar_meters);
	float cascade_handoff = smoothstep(
		local_cloud_shadow_near_start_meters,
		max(local_cloud_shadow_near_start_meters + 1.0, local_cloud_shadow_near_end_meters),
		planar_distance);
	float far_strength_blend = smoothstep(
		local_cloud_shadow_far_strength_start_meters,
		max(
			local_cloud_shadow_far_strength_start_meters + 1.0,
			local_cloud_shadow_far_strength_end_meters),
		planar_distance);
	float distance_strength = mix(
		local_cloud_shadow_strength,
		local_cloud_shadow_far_strength,
		far_strength_blend);
	float near_occlusion = 0.0;
	float near_weight = 0.0;
	bool near_in_bounds = all(greaterThanEqual(uv, vec2(0.0)))
		&& all(lessThanEqual(uv, vec2(1.0)));
	if (local_cloud_shadow_near_enabled && near_in_bounds) {
		float normalized_radius = length(planar_meters / radius);
		// Keep the detailed cascade authoritative through most of its footprint,
		// then cross-fade to the far map before either texture reaches its edge.
		near_weight = 1.0 - smoothstep(0.72, 0.94, normalized_radius);
		if (near_weight > 0.001) {
			near_occlusion = combine_ring_cloud_shadow_phases(
				textureLod(local_cloud_shadow_texture, uv, 0.0).rg)
				* cascade_handoff
				* distance_strength;
		}
	}
	float far_occlusion = 0.0;
	bool needs_far_cascade = !local_cloud_shadow_near_enabled
		|| !near_in_bounds || near_weight < 0.999;
	if (local_cloud_shadow_far_enabled && needs_far_cascade) {
		// Do not pay for a second curved-ring projection/trig calculation while
		// the detailed near cascade is fully authoritative.
		vec3 far_link = ring_cloud_shadow_far_link();
		vec2 far_planar;
		if (far_link.z > 0.5) {
			// Both cascades lie in one ring plane: far coordinates are the near
			// ones plus a constant, so the blend band needs one projection.
			far_planar = planar_meters + far_link.xy;
		} else {
			vec3 far_relative = sample_world_position - ring_cloud_shadow_far_center();
			far_planar = ring_cloud_shadow_coordinates(
				far_relative,
				ring_cloud_shadow_far_around(),
				ring_cloud_shadow_far_across(),
				ring_cloud_shadow_far_up()).xy;
		}
		float far_radius = max(local_cloud_shadow_far_radius_meters, 1.0);
		vec2 far_uv = far_planar / (far_radius * 2.0) + vec2(0.5) + ring_cloud_shadow_far_offset();
		if (all(greaterThanEqual(far_uv, vec2(0.0)))
				&& all(lessThanEqual(far_uv, vec2(1.0)))) {
			float far_edge = 1.0 - smoothstep(0.88, 0.99, length(far_planar / far_radius));
			far_occlusion = combine_ring_cloud_shadow_phases(
				textureLod(local_cloud_shadow_far_texture, far_uv, 0.0).rg)
				* far_edge * local_cloud_shadow_far_strength;
		}
	}
	if (!local_cloud_shadow_near_enabled || !near_in_bounds) {
		return far_occlusion;
	}
	if (!local_cloud_shadow_far_enabled) {
		return near_occlusion * near_weight;
	}
	return mix(far_occlusion, near_occlusion, near_weight);
}
