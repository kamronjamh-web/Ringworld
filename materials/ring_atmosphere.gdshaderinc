// Shared analytic atmosphere for the habitable inside of the ring.
// Air occupies two thin cylindrical shells next to the surface; the central
// volume is vacuum and therefore contributes no extinction.
#include "res://materials/celestial_lighting.gdshaderinc"
uniform bool ring_atmosphere_enabled = true;
// Scaled-space far-ring materials are always beyond the detailed haze range.
// Give them one cheap, uniform shroud instead of evaluating shell intersections
// and elevation-aware visibility for every fragment.
uniform bool ring_atmosphere_far_shroud_enabled = false;
uniform float ring_atmosphere_far_shroud_visibility : hint_range(0.0, 1.0) = 1.0;
uniform float ring_radius_meters = 5000000.0;
uniform float atmosphere_floor_depth_meters = 2500.0;
uniform float atmosphere_boundary_height_meters = 2500.0;
uniform float atmosphere_upper_height_meters = 20000.0;
uniform float atmosphere_boundary_extinction_meters = 60000.0;
uniform float atmosphere_upper_extinction_meters = 300000.0;
uniform float atmosphere_boundary_max_opacity : hint_range(0.0, 1.0) = 0.22;
uniform float atmosphere_upper_max_opacity : hint_range(0.0, 1.0) = 0.08;
uniform float atmosphere_total_max_opacity : hint_range(0.0, 1.0) = 0.80;
uniform float atmosphere_clear_distance_meters = 30000.0;
uniform float atmosphere_full_distance_meters = 220000.0;
uniform float atmosphere_high_altitude_clear_distance_meters = 200000.0;
uniform float atmosphere_high_altitude_full_distance_meters = 600000.0;
uniform float atmosphere_distance_altitude_start_meters = 1500.0;
uniform float atmosphere_distance_altitude_end_meters = 12000.0;
uniform float atmosphere_distance_haze_max_opacity : hint_range(0.0, 1.0) = 0.68;
uniform float atmosphere_opacity_multiplier : hint_range(0.0, 2.0) = 1.0;
uniform float atmosphere_night_density_factor : hint_range(0.0, 1.0) = 0.08;
uniform vec3 atmosphere_day_color : source_color = vec3(0.34, 0.46, 0.60);
uniform vec3 atmosphere_night_color : source_color = vec3(0.026, 0.035, 0.055);
uniform float atmosphere_scattering_energy : hint_range(0.0, 2.0) = 0.26;
// Defaults preserve existing materials that do not participate in the
// celestial presentation bridge.
uniform vec3 ring_atmosphere_planet_direction = vec3(-1.0, 0.0, 0.0);
uniform float eclipse_solar_transmittance : hint_range(0.0, 1.0) = 1.0;
// The analytic atmosphere is cylindrical in the ring's around/up plane. This
// width guard prevents its haze from continuing beyond the physical side walls.
// A large default preserves materials that are not ring-width-aware.
uniform float ring_atmosphere_half_width_meters = 1000000000.0;

float ring_atmosphere_altitude_blend(float camera_altitude) {
	return smoothstep(
		atmosphere_distance_altitude_start_meters,
		max(atmosphere_distance_altitude_start_meters + 1.0, atmosphere_distance_altitude_end_meters),
		max(camera_altitude, 0.0));
}

vec2 ring_atmosphere_visibility_distances(float camera_altitude) {
	float altitude_blend = ring_atmosphere_altitude_blend(camera_altitude);
	return vec2(
		mix(atmosphere_clear_distance_meters, atmosphere_high_altitude_clear_distance_meters, altitude_blend),
		mix(atmosphere_full_distance_meters, atmosphere_high_altitude_full_distance_meters, altitude_blend));
}

float ring_segment_length_inside_cylinder(vec3 start_world, vec3 end_world, vec3 center_world, float radius) {
	vec2 start = start_world.xz - center_world.xz;
	vec2 delta = end_world.xz - start_world.xz;
	float a = dot(delta, delta);
	float segment_length = distance(start_world, end_world);
	if (a < 0.000001 || segment_length < 0.000001) {
		return length(start) <= radius ? segment_length : 0.0;
	}

	float b = 2.0 * dot(start, delta);
	float c = dot(start, start) - radius * radius;
	float discriminant = b * b - 4.0 * a * c;
	if (discriminant <= 0.0) {
		return 0.0;
	}

	float root = sqrt(discriminant);
	float t0 = (-b - root) / (2.0 * a);
	float t1 = (-b + root) / (2.0 * a);
	float enter = clamp(min(t0, t1), 0.0, 1.0);
	float exit = clamp(max(t0, t1), 0.0, 1.0);
	return max(exit - enter, 0.0) * segment_length;
}

// Closed-form atmosphere intersections for sky and celestial rays. These use
// ring-radius-normalized coordinates so the certified 5,000 km ring and the
// 1:1000 scaled far viewport produce the same result without subtracting large
// world-space floats in the sky pass.
vec2 ring_sky_empty_interval() {
	return vec2(1.0, -1.0);
}

bool ring_sky_interval_valid(vec2 interval) {
	return interval.y > interval.x;
}

vec2 ring_sky_intersect_intervals(vec2 first, vec2 second) {
	vec2 result = vec2(max(first.x, second.x), min(first.y, second.y));
	return ring_sky_interval_valid(result) ? result : ring_sky_empty_interval();
}

vec2 ring_sky_ray_cylinder_interval(
		vec3 ray_origin,
		vec3 ray_direction,
		vec3 ring_axis,
		float cylinder_radius) {
	vec3 radial_origin = ray_origin - ring_axis * dot(ray_origin, ring_axis);
	vec3 radial_direction = ray_direction - ring_axis * dot(ray_direction, ring_axis);
	float a = dot(radial_direction, radial_direction);
	float radius = max(cylinder_radius, 0.000001);
	if (a < 0.0000001) {
		return dot(radial_origin, radial_origin) <= radius * radius
			? vec2(-1000000.0, 1000000.0)
			: ring_sky_empty_interval();
	}
	float b = 2.0 * dot(radial_origin, radial_direction);
	float c = dot(radial_origin, radial_origin) - radius * radius;
	float discriminant = b * b - 4.0 * a * c;
	if (discriminant <= 0.0) {
		return ring_sky_empty_interval();
	}
	float root = sqrt(max(discriminant, 0.0));
	float inverse_denominator = 0.5 / a;
	float t0 = (-b - root) * inverse_denominator;
	float t1 = (-b + root) * inverse_denominator;
	return vec2(min(t0, t1), max(t0, t1));
}

vec2 ring_sky_ray_width_interval(
		vec3 ray_origin,
		vec3 ray_direction,
		vec3 ring_axis,
		float half_width_ratio) {
	float origin_across = dot(ray_origin, ring_axis);
	float direction_across = dot(ray_direction, ring_axis);
	float half_width = max(half_width_ratio, 0.000001);
	if (abs(direction_across) < 0.0000001) {
		return abs(origin_across) <= half_width
			? vec2(-1000000.0, 1000000.0)
			: ring_sky_empty_interval();
	}
	float t0 = (-half_width - origin_across) / direction_across;
	float t1 = (half_width - origin_across) / direction_across;
	return vec2(min(t0, t1), max(t0, t1));
}

vec2 ring_sky_clipped_cylinder_interval(
		vec3 ray_origin,
		vec3 ray_direction,
		vec3 ring_axis,
		float cylinder_radius,
		float half_width_ratio) {
	vec2 cylinder = ring_sky_ray_cylinder_interval(
		ray_origin,
		ray_direction,
		ring_axis,
		cylinder_radius);
	vec2 width = ring_sky_ray_width_interval(
		ray_origin,
		ray_direction,
		ring_axis,
		half_width_ratio);
	return ring_sky_intersect_intervals(
		ring_sky_intersect_intervals(cylinder, width),
		vec2(0.0, 1000000.0));
}

float ring_sky_interval_length(vec2 interval) {
	return ring_sky_interval_valid(interval) ? max(interval.y - interval.x, 0.0) : 0.0;
}

vec2 ring_sky_first_shell_segment(vec2 outer_interval, vec2 inner_interval) {
	if (!ring_sky_interval_valid(outer_interval)) {
		return ring_sky_empty_interval();
	}
	if (!ring_sky_interval_valid(inner_interval)) {
		return outer_interval;
	}
	if (inner_interval.x <= outer_interval.x + 0.000001
			&& inner_interval.y > outer_interval.x) {
		vec2 outgoing = vec2(max(inner_interval.y, outer_interval.x), outer_interval.y);
		return ring_sky_interval_valid(outgoing) ? outgoing : ring_sky_empty_interval();
	}
	vec2 incoming = vec2(outer_interval.x, min(inner_interval.x, outer_interval.y));
	return ring_sky_interval_valid(incoming) ? incoming : outer_interval;
}

float ring_sky_local_day_factor(
		vec3 normalized_position,
		vec3 ring_axis,
		vec3 to_sun,
		float sky_day_start,
		float sky_day_full) {
	vec3 radial = normalized_position - ring_axis * dot(normalized_position, ring_axis);
	vec3 sky_surface_up = length(radial) > 0.000001 ? -normalize(radial) : vec3(-1.0, 0.0, 0.0);
	return ringworld_effective_solar_day(smoothstep(
		sky_day_start,
		max(sky_day_start + 0.0001, sky_day_full),
		dot(sky_surface_up, normalize(to_sun))));
}

vec4 sample_ring_sky_atmosphere(
		vec3 normalized_camera_position,
		vec3 ray_direction,
		vec3 ring_axis,
		float half_width_ratio,
		vec3 to_sun,
		float sky_day_start,
		float sky_day_full,
		float sky_day_scattering_boost,
		float sky_night_scattering_boost,
		bool sky_planet_haze_enabled,
		vec3 sky_planet_light_color,
		float sky_planet_haze_energy,
		float sky_planet_horizon_start,
		float sky_planet_horizon_full,
		float sky_planet_twilight_end,
		float sky_sun_forward_energy) {
	if (!ring_atmosphere_enabled || ring_radius_meters <= 1.0 || half_width_ratio <= 0.0) {
		return vec4(0.0);
	}

	vec3 axis = normalize(ring_axis);
	vec3 direction = normalize(ray_direction);
	float inverse_radius = 1.0 / max(ring_radius_meters, 1.0);
	// The physical air is inside the habitable surface. FloorDepth remains part
	// of object haze to hide terrain seams, but must not create an exterior halo
	// below the bedrock hull.
	float surface_radius = 1.0;
	float boundary_radius = max(
		0.000001,
		1.0 - max(atmosphere_boundary_height_meters, 1.0) * inverse_radius);
	float upper_radius = max(
		0.000001,
		1.0 - max(atmosphere_upper_height_meters, atmosphere_boundary_height_meters)
			* inverse_radius);

	vec2 surface_interval = ring_sky_clipped_cylinder_interval(
		normalized_camera_position, direction, axis, surface_radius, half_width_ratio);
	vec2 boundary_interval = ring_sky_clipped_cylinder_interval(
		normalized_camera_position, direction, axis, boundary_radius, half_width_ratio);
	vec2 upper_interval = ring_sky_clipped_cylinder_interval(
		normalized_camera_position, direction, axis, upper_radius, half_width_ratio);
	vec2 boundary_segment = ring_sky_first_shell_segment(surface_interval, boundary_interval);
	vec2 upper_segment = ring_sky_first_shell_segment(boundary_interval, upper_interval);
	// Use the same first visible shell segments for both extinction and lighting.
	// Opaque ring terrain normally hides the far crossing; including its length
	// while shading only the near crossing caused day/night hemisphere tinting to
	// flip on exterior and horizon views.
	float boundary_path_ratio = ring_sky_interval_length(boundary_segment);
	float upper_path_ratio = ring_sky_interval_length(upper_segment);
	if (boundary_path_ratio + upper_path_ratio <= 0.0000001) {
		return vec4(0.0);
	}
	float boundary_sample_t = ring_sky_interval_valid(boundary_segment)
		? (boundary_segment.x + boundary_segment.y) * 0.5
		: 0.0;
	float upper_sample_t = ring_sky_interval_valid(upper_segment)
		? (upper_segment.x + upper_segment.y) * 0.5
		: boundary_sample_t;
	vec3 boundary_sample = normalized_camera_position + direction * boundary_sample_t;
	vec3 upper_sample = normalized_camera_position + direction * upper_sample_t;
	float boundary_day = ring_sky_local_day_factor(
		boundary_sample, axis, to_sun, sky_day_start, sky_day_full);
	float upper_day = ring_sky_local_day_factor(
		upper_sample, axis, to_sun, sky_day_start, sky_day_full);

	float boundary_path_meters = boundary_path_ratio * ring_radius_meters;
	float upper_path_meters = upper_path_ratio * ring_radius_meters;
	float boundary_opacity = (1.0 - exp(
		-boundary_path_meters / max(atmosphere_boundary_extinction_meters, 1.0)))
		* atmosphere_boundary_max_opacity;
	float upper_opacity = (1.0 - exp(
		-upper_path_meters / max(atmosphere_upper_extinction_meters, 1.0)))
		* atmosphere_upper_max_opacity;
	boundary_opacity = clamp(boundary_opacity * atmosphere_opacity_multiplier, 0.0, 1.0);
	upper_opacity = clamp(upper_opacity * atmosphere_opacity_multiplier, 0.0, 1.0);
	float sky_total_opacity = 1.0 - (1.0 - boundary_opacity) * (1.0 - upper_opacity);
	float path_meters = boundary_path_meters + upper_path_meters;
	vec3 normalized_camera_radial = normalized_camera_position
		- axis * dot(normalized_camera_position, axis);
	float camera_altitude_meters = max(0.0, 1.0 - length(normalized_camera_radial))
		* ring_radius_meters;
	vec2 visibility_distances = ring_atmosphere_visibility_distances(camera_altitude_meters);
	// This is an air-path weight, not a target-distance guard: celestial bodies
	// can be distant while their ray crosses only the local atmospheric shell.
	// It controls only the artistic extra long-path haze below.
	float distance_weight = smoothstep(
		visibility_distances.x,
		max(visibility_distances.x + 1.0, visibility_distances.y),
		path_meters);
	float camera_air_presence = 1.0 - smoothstep(
		atmosphere_upper_height_meters,
		atmosphere_upper_height_meters + max(1000.0, atmosphere_upper_height_meters * 0.1),
		camera_altitude_meters);
	// Optical depth belongs to the shell geometry, not the solar illumination.
	// At totality this remains a night-coloured veil over the giant and stars.
	float distance_haze_opacity = distance_weight
		* atmosphere_distance_haze_max_opacity * camera_air_presence;
	float physical_distance_opacity = sky_total_opacity;
	// Independent optical-depth composition avoids a visible handoff from the
	// physical shell to distance haze at long range.
	sky_total_opacity = min(
		1.0 - (1.0 - physical_distance_opacity) * (1.0 - distance_haze_opacity),
		atmosphere_total_max_opacity);

	vec3 boundary_up = -normalize(
		boundary_sample - axis * dot(boundary_sample, axis));
	vec3 upper_up = -normalize(upper_sample - axis * dot(upper_sample, axis));
	vec3 planet_direction = normalize(ring_atmosphere_planet_direction);
	float horizon_full = max(sky_planet_horizon_start + 0.0001, sky_planet_horizon_full);
	float boundary_planet_horizon = smoothstep(
		sky_planet_horizon_start, horizon_full, dot(boundary_up, planet_direction));
	float upper_planet_horizon = smoothstep(
		sky_planet_horizon_start, horizon_full, dot(upper_up, planet_direction));
	float twilight_end = max(sky_planet_twilight_end, 0.0001);
	float boundary_planet_night = 1.0 - smoothstep(0.0, twilight_end, boundary_day);
	float upper_planet_night = 1.0 - smoothstep(0.0, twilight_end, upper_day);
	// Rayleigh-like symmetric phase keeps both forward and back-scattered light
	// visible. A one-sided dot(direction, light) made the inner day/night limbs
	// disappear when viewed from the central vacuum.
	float planet_alignment = dot(-direction, planet_direction);
	float sun_alignment = dot(-direction, normalize(to_sun));
	float planet_phase = 0.35 + 0.65 * planet_alignment * planet_alignment;
	float sun_phase = 0.35 + 0.65 * sun_alignment * sun_alignment;
	// Global visibility is already folded into each effective local day.  Do not
	// reapply raw eclipse transmittance or leave a sun-forward halo at totality.
	float boundary_solar_transmittance = 1.0;
	float upper_solar_transmittance = 1.0;
	vec3 boundary_color = mix(
		atmosphere_night_color * sky_night_scattering_boost,
		atmosphere_day_color * sky_day_scattering_boost * boundary_solar_transmittance,
		boundary_day);
	vec3 upper_color = mix(
		atmosphere_night_color * sky_night_scattering_boost,
		atmosphere_day_color * sky_day_scattering_boost * upper_solar_transmittance,
		upper_day);
	boundary_color += atmosphere_day_color
		* sun_phase * sky_sun_forward_energy * boundary_day * boundary_solar_transmittance;
	upper_color += atmosphere_day_color
		* sun_phase * sky_sun_forward_energy * upper_day * upper_solar_transmittance;
	float planet_haze_gate = sky_planet_haze_enabled ? 1.0 : 0.0;
	boundary_color += sky_planet_light_color
		* planet_phase * sky_planet_haze_energy
		* boundary_planet_horizon * boundary_planet_night * planet_haze_gate;
	upper_color += sky_planet_light_color
		* planet_phase * sky_planet_haze_energy
		* upper_planet_horizon * upper_planet_night * planet_haze_gate;

	float boundary_weight = boundary_opacity;
	float upper_weight = upper_opacity * (1.0 - boundary_opacity);
	float combined_weight = max(boundary_weight + upper_weight, 0.000001);
	vec3 scattering_color = (
		boundary_color * boundary_weight + upper_color * upper_weight) / combined_weight;
	return vec4(
		scattering_color * sky_total_opacity * atmosphere_scattering_energy,
		clamp(sky_total_opacity, 0.0, 1.0));
}

vec4 sample_ring_atmosphere(vec3 camera_world, vec3 target_world, vec3 center_world, float day_factor) {
	if (!ring_atmosphere_enabled || ring_radius_meters <= 1.0) {
		return vec4(0.0);
	}

	float half_width = max(1.0, ring_atmosphere_half_width_meters);
	float width_feather = max(1.0, half_width * 0.015);
	float target_width_fade = 1.0 - smoothstep(
		half_width - width_feather,
		half_width + width_feather,
		abs(target_world.y - center_world.y));
	if (target_width_fade <= 0.0001) return vec4(0.0);
	float clamped_day_factor = clamp(day_factor, 0.0, 1.0);
	vec3 color = mix(atmosphere_night_color, atmosphere_day_color, clamped_day_factor);
	if (ring_atmosphere_far_shroud_enabled) {
		float far_shroud_opacity = min(
			atmosphere_distance_haze_max_opacity,
			atmosphere_total_max_opacity)
			* clamp(ring_atmosphere_far_shroud_visibility, 0.0, 1.0)
			* target_width_fade;
		return vec4(color, clamp(far_shroud_opacity, 0.0, 1.0));
	}
	float outer_radius = ring_radius_meters + max(atmosphere_floor_depth_meters, 0.0);
	float boundary_inner_radius = max(1.0, ring_radius_meters - max(atmosphere_boundary_height_meters, 1.0));
	float upper_inner_radius = max(1.0, ring_radius_meters - max(atmosphere_upper_height_meters, atmosphere_boundary_height_meters));
	float inside_outer = ring_segment_length_inside_cylinder(camera_world, target_world, center_world, outer_radius);
	float inside_boundary_vacuum = ring_segment_length_inside_cylinder(camera_world, target_world, center_world, boundary_inner_radius);
	float inside_upper_vacuum = ring_segment_length_inside_cylinder(camera_world, target_world, center_world, upper_inner_radius);
	float boundary_path = max(inside_outer - inside_boundary_vacuum, 0.0);
	// Keep the lower and upper shells disjoint. Counting the boundary layer a
	// second time in the upper shell was the main source of far-ring washout.
	float upper_path = max(inside_boundary_vacuum - inside_upper_vacuum, 0.0);

	float boundary_opacity = (1.0 - exp(-boundary_path / max(atmosphere_boundary_extinction_meters, 1.0)))
		* atmosphere_boundary_max_opacity;
	float upper_opacity = (1.0 - exp(-upper_path / max(atmosphere_upper_extinction_meters, 1.0)))
		* atmosphere_upper_max_opacity;
	float atmosphere_opacity = 1.0 - (1.0 - boundary_opacity) * (1.0 - upper_opacity);
	float view_distance = distance(camera_world, target_world);
	float camera_radial_distance = length((camera_world - center_world).xz);
	float camera_altitude = max(0.0, ring_radius_meters - camera_radial_distance);
	vec2 visibility_distances = ring_atmosphere_visibility_distances(camera_altitude);
	float distance_weight = smoothstep(
		visibility_distances.x,
		max(visibility_distances.x + 1.0, visibility_distances.y),
		view_distance);
	float camera_air_presence = (1.0 - smoothstep(
		atmosphere_upper_height_meters,
		atmosphere_upper_height_meters + max(1000.0, atmosphere_upper_height_meters * 0.1),
		camera_altitude))
		* (1.0 - step(ring_radius_meters + atmosphere_floor_depth_meters, camera_radial_distance));
	float physical_distance_opacity = atmosphere_opacity * distance_weight;
	float distance_haze_opacity = distance_weight
		* atmosphere_distance_haze_max_opacity * camera_air_presence;
	// Match RingAtmosphereModel: compose rather than select the dominant term.
	atmosphere_opacity = min(
		1.0 - (1.0 - physical_distance_opacity) * (1.0 - distance_haze_opacity),
		atmosphere_total_max_opacity);
	atmosphere_opacity *= atmosphere_opacity_multiplier * target_width_fade;
	return vec4(color, clamp(atmosphere_opacity, 0.0, 1.0));
}
