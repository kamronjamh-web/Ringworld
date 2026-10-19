// Shared analytic atmosphere for the habitable inside of the ring.
// Air occupies two thin cylindrical shells next to the surface; the central
// volume is vacuum and therefore contributes no extinction.
#include "res://materials/celestial_lighting.gdshaderinc"
// Eclipse darkness scale for emission-like night terms (1 outside an eclipse; published by day_side_atmosphere.gd).
global uniform float ringworld_night_light_scale;
uniform bool ring_atmosphere_enabled = true;
// Legacy fallback for scaled-space materials without a dynamic ray frame.
// Bound far-ring materials use the camera-relative air path below.
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
uniform float atmosphere_aerosol : hint_range(0.0, 1.0) = 0.35;
uniform float atmosphere_forward_scattering : hint_range(0.0, 0.85) = 0.22;
uniform float atmosphere_sun_warmth : hint_range(0.0, 1.0) = 0.25;
// Direction from the shaded point toward the star. Facing the star brightens
// the view ray; the opposite direction contributes no warm forward lobe.
uniform vec3 ring_atmosphere_to_sun = vec3(0.0, 1.0, 0.0);
// Defaults preserve existing materials that do not participate in the
// celestial presentation bridge.
uniform vec3 ring_atmosphere_planet_direction = vec3(-1.0, 0.0, 0.0);
uniform float eclipse_solar_transmittance : hint_range(0.0, 1.0) = 1.0;
// The analytic atmosphere is cylindrical in the ring's around/up plane. This
// width guard prevents its haze from continuing beyond the physical side walls.
// A large default preserves materials that are not ring-width-aware.
uniform float ring_atmosphere_half_width_meters = 1000000000.0;
#include "res://materials/ring_atmosphere_shared.gdshaderinc"
#include "res://materials/ring_haze_shared.gdshaderinc"
#include "res://materials/ring_atmosphere_paths.gdshaderinc"

// Physical haze (P2a of docs/atmosphere-haze-revamp-plan.md). A/B toggle until the Legacy model is removed (P8):
// 0 = Legacy (smoothstep distance ramp, sample_ring_atmosphere), 1 = Physical (ring_haze_eval). Default Legacy.
// Every ring_haze_* uniform is in PHYSICAL units (1/m, m) regardless of ring_atmosphere_coordinate_scale; ring_haze_sample
// converts positions/radius itself. Pushed by RingAtmosphereModel.PushHazeParameters.
uniform int atmosphere_haze_model = 0;
uniform vec3 ring_haze_sigma_R = vec3(5.802e-6, 13.558e-6, 33.1e-6);
uniform float ring_haze_H_R = 8000.0;
uniform float ring_haze_sigma_A = 4.175e-6;
uniform float ring_haze_omega_A = 0.9;
uniform float ring_haze_H_A = 1500.0;
uniform float ring_haze_sigma_M0 = 4.0e-6;
uniform float ring_haze_omega_M = 0.95;
uniform float ring_haze_H_mist = 400.0;
uniform float ring_haze_cap_height_meters = 20000.0;
uniform float ring_haze_g = 0.6;
uniform int ring_haze_quality = 8;
// Art-direction tint (sun warmth) on the haze sun colour; neutral at the Natural default.
uniform vec3 ring_haze_sun_tint = vec3(1.0);
// Lighting colours come from the environment, not from per-material uniforms: day_side_atmosphere.gd publishes them as
// globals whenever the sun colour/energy or ambient change (cheaper than rewriting every registered material).
// Units: see the comment on ring_haze_sample.
global uniform vec3 ringworld_haze_sun_irradiance;
global uniform vec3 ringworld_haze_ambient_day;
global uniform vec3 ringworld_haze_ambient_night;

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

vec3 ring_atmosphere_scattering_color(vec3 ray_direction, float day_factor) {
	return ring_atmosphere_scattering_color_shared(ray_direction, day_factor, ring_atmosphere_to_sun, atmosphere_day_color, atmosphere_night_color * ringworld_night_light_scale, atmosphere_aerosol, atmosphere_forward_scattering, atmosphere_sun_warmth);
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
		atmosphere_night_color * sky_night_scattering_boost * ringworld_night_light_scale,
		atmosphere_day_color * sky_day_scattering_boost * boundary_solar_transmittance,
		boundary_day);
	vec3 upper_color = mix(
		atmosphere_night_color * sky_night_scattering_boost * ringworld_night_light_scale,
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
	// The legacy cylinder fallback has no camera-local frame, so it retains the
	// caller's target-surface illumination. A valid frame instead integrates
	// camera-local air and must not inherit night/day from a remote target.
	float legacy_day_factor = clamp(day_factor, 0.0, 1.0);
	vec3 legacy_color = ring_atmosphere_scattering_color(target_world - camera_world, legacy_day_factor);
	vec3 camera_relative_paths = ring_atmosphere_first_air_paths(target_world);
	if (camera_relative_paths.x >= 0.0) {
		float local_air_geometric_day = smoothstep(-0.2, 0.35,
			dot(-normalize(ring_atmosphere_outward_world), normalize(ring_atmosphere_to_sun)));
		float local_air_day = ringworld_effective_solar_day(local_air_geometric_day);
		vec3 local_air_color = ring_atmosphere_scattering_color(target_world - camera_world, local_air_day);
		float path_scale = max(ring_atmosphere_coordinate_scale, 0.000001);
		float local_air_boundary_opacity = (1.0 - exp(-(camera_relative_paths.y * path_scale) / max(atmosphere_boundary_extinction_meters, 1.0))) * atmosphere_boundary_max_opacity;
		float local_air_upper_opacity = (1.0 - exp(-(camera_relative_paths.z * path_scale) / max(atmosphere_upper_extinction_meters, 1.0))) * atmosphere_upper_max_opacity;
		float local_air_shell_opacity = 1.0 - (1.0 - local_air_boundary_opacity) * (1.0 - local_air_upper_opacity);
		vec2 local_air_distances = ring_atmosphere_visibility_distances(ring_atmosphere_camera_height_meters * path_scale);
		float local_air_distance_weight = smoothstep(local_air_distances.x, max(local_air_distances.x + 1.0, local_air_distances.y), camera_relative_paths.x * path_scale);
		float local_air_haze = local_air_distance_weight * atmosphere_distance_haze_max_opacity;
		float local_air_opacity = min(1.0 - (1.0 - local_air_shell_opacity * local_air_distance_weight) * (1.0 - local_air_haze), atmosphere_total_max_opacity);
		return vec4(local_air_color, clamp(local_air_opacity * atmosphere_opacity_multiplier, 0.0, 1.0));
	}
	if (target_width_fade <= 0.0001) return vec4(0.0);
	if (ring_atmosphere_far_shroud_enabled) {
		float far_shroud_opacity = min(
			atmosphere_distance_haze_max_opacity,
			atmosphere_total_max_opacity)
			* clamp(ring_atmosphere_far_shroud_visibility, 0.0, 1.0)
			* target_width_fade;
		return vec4(legacy_color, clamp(far_shroud_opacity, 0.0, 1.0));
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
	return vec4(legacy_color, clamp(atmosphere_opacity, 0.0, 1.0));
}

// ---- Unified haze entry point for spatial / forward consumers -------------------------------------------------
// Consumer contract (both models):   L = L_surf * T + S_sun + S_amb
// Legacy:   T = vec3(1 - a), S_sun = rgb * a * atmosphere_scattering_energy, S_amb = 0 (exactly what each consumer composited
//           before P2a, including atmosphere_opacity_multiplier, which sample_ring_atmosphere already folds into a).
// Physical: ring_haze_eval over the local air interval from the ray frame (ring_atmosphere_* uniforms). A material with no valid
//           ray frame falls back to the Legacy result (there is no physical far-shroud); see docs for the audit.
//
// Radiometric units. Godot (non-physical light units) multiplies a directional light's colour*energy by PI internally and
// the built-in Lambert divides by PI again, so a matte surface with albedo A under the sun at NdotL = 1 radiates
// A * sun_colour * sun_energy. The haze model's phase functions are normalised over 4PI (1/sr), i.e. S = E_irr * phase * tau
// with E_irr the sun's irradiance, and a surface radiates (A / PI) * E_irr. Matching the two gives
// E_irr = PI * sun_colour * sun_energy = ringworld_haze_sun_irradiance. Ambient is already "radiance of a white surface" in
// Godot (ambient_light_color * ambient_light_energy multiplies the albedo directly), which is exactly the isotropic
// multi-scatter radiance the model wants, so ringworld_haze_ambient_* is passed through unscaled. Note that the project's custom
// light() functions (terrain, ocean, hull) add ALBEDO * LIGHT_COLOR * NdotL with LIGHT_COLOR already holding the PI factor,
// so terrain radiates PI-fold brighter than a built-in-lit surface of the same albedo; haze is anchored to the built-in convention.
RingHaze ring_haze_from_legacy(vec4 atmosphere) {
	RingHaze haze;
	haze.T = vec3(1.0 - atmosphere.a);
	haze.S_sun = atmosphere.rgb * atmosphere.a * atmosphere_scattering_energy;
	haze.S_amb = vec3(0.0);
	return haze;
}

// Physical-model parameters from the haze uniforms/globals. The day weight is the camera-local ringworld_effective_solar_day of the
// geometric day at the viewer's local up (-outward). Shared by ring_haze_sample (spatial consumers) and the sky shader.
RingHazeParams ring_haze_uniform_params(vec3 to_sun, vec3 outward) {
	RingHazeParams p;
	p.sigma_R = ring_haze_sigma_R;
	p.H_R = ring_haze_H_R;
	p.sigma_A = ring_haze_sigma_A;
	p.omega_A = ring_haze_omega_A;
	p.H_A = ring_haze_H_A;
	p.sigma_M0 = ring_haze_sigma_M0;
	p.omega_M = ring_haze_omega_M;
	p.H_mist = ring_haze_H_mist;
	p.cap_height = ring_haze_cap_height_meters;
	p.g = ring_haze_g;
	p.E_sun = ringworld_haze_sun_irradiance * ring_haze_sun_tint;
	p.ambient = ringworld_haze_ambient_day;
	p.night_ambient = ringworld_haze_ambient_night;
	p.to_sun = normalize(to_sun);
	p.quality = ring_haze_quality;
	p.day = ringworld_effective_solar_day(smoothstep(-0.2, 0.35, dot(-normalize(outward), p.to_sun)));
	return p;
}

RingHaze ring_haze_sample(vec3 camera_world, vec3 target_world, vec3 center_world, float day_factor) {
	if (atmosphere_haze_model == 0 || !ring_atmosphere_enabled || ring_radius_meters <= 1.0 || !ring_atmosphere_ray_frame_valid) {
		return ring_haze_from_legacy(sample_ring_atmosphere(camera_world, target_world, center_world, day_factor));
	}
	float scale = max(ring_atmosphere_coordinate_scale, 0.000001);
	RingHazeParams p = ring_haze_uniform_params(ring_atmosphere_to_sun, ring_atmosphere_outward_world);
	RingHaze haze = ring_haze_eval(p, target_world, ring_atmosphere_camera_world_position, ring_atmosphere_camera_height_meters,
		ring_atmosphere_outward_world, ring_atmosphere_axis_world, ring_atmosphere_sidewall_negative_meters,
		ring_atmosphere_sidewall_positive_meters, scale, ring_radius_meters / scale, atmosphere_floor_depth_meters / scale);
	// Per-material density multiplier (clouds and distant props bind < 1): scales optical depth, tau' = m * tau.
	float m = atmosphere_opacity_multiplier;
	if (abs(m - 1.0) > 0.0001) {
		vec3 t_scaled = exp(m * log(max(haze.T, vec3(0.000001))));
		vec3 gain = (vec3(1.0) - t_scaled) / max(vec3(1.0) - haze.T, vec3(0.000001));
		haze.T = t_scaled;
		haze.S_sun *= gain;
		haze.S_amb *= gain;
	}
	return haze;
}

// Total in-scattered radiance to add to EMISSION.
vec3 ring_haze_inscatter(RingHaze haze) {
	return haze.S_sun + haze.S_amb;
}

// Scalar visibility for effects that carry no colour of their own (emitter beams, glows): green transmittance
// (exactly 1 - a in Legacy, where T is grey).
float ring_haze_visibility(RingHaze haze) {
	return haze.T.g;
}

// Attenuation for specular / reflection terms a shader owns separately from ALBEDO. 1 in Legacy (look unchanged), mean T in Physical.
float ring_haze_specular_scale(RingHaze haze) {
	return atmosphere_haze_model == 0 ? 1.0 : dot(haze.T, vec3(0.3333333));
}
