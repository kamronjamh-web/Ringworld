// Portable helpers. Callers supply settings explicitly so fixture shaders can
// exercise the same decisions without compositor descriptors or scene state.
bool ring_cloud_history_opaque_matches(float expected_depth, float previous_depth, float tolerance) {
	if (isnan(expected_depth) || isinf(expected_depth) || isnan(previous_depth) || isinf(previous_depth)) return false;
	if (expected_depth < 0.0) return previous_depth == -1.0;
	return expected_depth > 0.0 && previous_depth > 0.0
		&& abs(expected_depth - previous_depth) <= max(tolerance, 0.0);
}

float ring_cloud_history_compatible_weight(vec4 history_color, vec4 current_color,
		float previous_cloud_distance, float current_cloud_distance,
		float maximum_step, float requested_weight, bool enabled) {
	float retained = clamp(requested_weight, 0.0, 1.0);
	if (!enabled) return retained;
	if (any(isnan(history_color)) || any(isinf(history_color))
			|| any(isnan(current_color)) || any(isinf(current_color))
			|| isnan(current_cloud_distance) || isinf(current_cloud_distance)
			|| isnan(previous_cloud_distance) || isinf(previous_cloud_distance)) return 0.0;
	// A two-percent opacity difference is treated as stochastic sampling noise.
	// Larger visibility changes retire incompatible color without changing the
	// user's coherent stationary/moving history setting.
	float opacity_agreement = 1.0 - smoothstep(0.02, 0.20, abs(history_color.a - current_color.a));
	float distance_allowance = max(maximum_step, 1.0)
		+ 0.10 * max(max(previous_cloud_distance, current_cloud_distance), 0.0);
	float distance_agreement = 1.0 - smoothstep(distance_allowance, distance_allowance * 2.0,
		abs(previous_cloud_distance - current_cloud_distance));
	return retained * opacity_agreement * distance_agreement;
}

// A jittered last interval can alternate between a full density sample and a
// partial terminal sample. Its alpha is not a resolved visibility change.
// Admit this exception only at a verified, continuously reprojected opaque
// endpoint; cloud bodies outside that final interval keep compatibility gates.
bool ring_cloud_history_terminal_jitter(bool opaque_point_agrees, float jitter_strength,
		float pixel_motion, float previous_end, float current_end,
		float previous_body, float current_body, float previous_opaque, float current_opaque,
		float camera_translation, float maximum_step, float geometry_tolerance) {
	if (!opaque_point_agrees || jitter_strength <= 0.0 || pixel_motion > 2.0
			|| previous_opaque <= 0.0 || current_opaque <= 0.0) return false;
	vec4 controls = vec4(jitter_strength, pixel_motion, camera_translation, maximum_step);
	if (any(isnan(controls)) || any(isinf(controls))) return false;
	vec4 endpoints = vec4(previous_end, current_end, previous_body, current_body);
	if (any(isnan(endpoints)) || any(isinf(endpoints))) return false;
	float endpoint_tolerance = max(geometry_tolerance, 0.0) + max(camera_translation, 0.0);
	float interval = max(maximum_step, 1.0);
	if (camera_translation > interval * 0.1) return false;
	// A full sample can saturate just before reaching the opaque endpoint;
	// its paired jitter phase instead evaluates a partial terminal interval.
	return previous_end >= max(0.0, previous_opaque - interval - endpoint_tolerance)
		&& previous_end <= previous_opaque + endpoint_tolerance
		&& current_end >= max(0.0, current_opaque - interval - endpoint_tolerance)
		&& current_end <= current_opaque + endpoint_tolerance
		&& previous_body >= max(0.0, previous_opaque - interval - endpoint_tolerance)
		&& previous_body <= previous_opaque + endpoint_tolerance
		&& current_body >= max(0.0, current_opaque - interval - endpoint_tolerance)
		&& current_body <= current_opaque + endpoint_tolerance
		&& abs(previous_body - current_body) <= interval + endpoint_tolerance;
}

// Only unresolved, continuously sampled sky rays receive this allowance.
// Opaque history validation remains authoritative in the caller.
bool ring_cloud_history_sky_jitter(bool sky_pair, bool continuous_field,
		float jitter_strength,float pixel_motion,float camera_translation,float maximum_step) {
	vec4 controls = vec4(jitter_strength,pixel_motion,camera_translation,maximum_step);
	return sky_pair && continuous_field && !any(isnan(controls)) && !any(isinf(controls))
		&& jitter_strength > 0.0 && pixel_motion <= 2.0
		&& camera_translation <= max(maximum_step,1.0)*0.1;
}
