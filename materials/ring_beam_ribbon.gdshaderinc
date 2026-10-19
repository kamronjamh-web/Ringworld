// Generic camera-facing beam ribbon helpers (docs/eclipse-false-sun-spec.md E4a). Extracted from
// ring_emitter_grid_beam_common.gdshaderinc so the eclipse hub beams (E4b) can share them without copying.
// Pure functions: no uniforms, no varyings. Built-ins (VIEW_MATRIX, PROJECTION_MATRIX, VIEWPORT_SIZE) and layer uniforms
// (near_plane, widths) are passed in by the caller. Users: ring_emitter_grid_beam_common.gdshaderinc.
#include "res://materials/ring_atmosphere.gdshaderinc"

// Near-plane sub-segment clip: slides view_here toward view_other (and f_here toward f_other) onto the near plane.
// Returns false when both ends are behind the camera plane (nothing to draw).
bool ribbon_clip_near(inout vec3 view_here, inout float f_here, vec3 view_other, float f_other, float near_plane) {
	if (view_here.z > -near_plane) {
		if (view_other.z > -near_plane) {
			return false;
		}
		float k = (-near_plane * 1.01 - view_here.z) / (view_other.z - view_here.z);
		view_here = mix(view_here, view_other, k);
		f_here = mix(f_here, f_other, k);
	}
	return true;
}

// Unit view-space side vector perpendicular to the beam axis (axis_view = the span direction in view space) and the view ray.
vec3 ribbon_side_vector(vec3 axis_view, vec3 view_here, float distance_here) {
	vec3 dir_view = normalize(axis_view);
	vec3 side_view = cross(dir_view, view_here / max(distance_here, 1.0e-9));
	float side_length = length(side_view);
	return side_length > 0.0001 ? side_view / side_length : vec3(1.0, 0.0, 0.0);
}

// World units per pixel. abs(): the RenderingDevice projection carries a Y flip.
float ribbon_meters_per_pixel(float distance_here, float projection_y, float viewport_height) {
	return distance_here / (abs(projection_y) * viewport_height * 0.5);
}

// (core_eff, halo_eff, core_gain, halo_gain): widths widened to the pixel footprint, with the energy-conserving gains.
// Widening a profile to the footprint scales its amplitude by (true / drawn)^compensation.
vec4 ribbon_profile(float core_sigma, float halo_length, float min_pixels, float meters_per_pixel, float compensation) {
	float footprint = 0.35 * min_pixels * meters_per_pixel;
	float core_eff = max(core_sigma, footprint);
	float halo_eff = max(halo_length, footprint);
	float core_gain = pow(core_sigma / core_eff, compensation);
	float halo_gain = pow(halo_length / halo_eff, compensation);
	return vec4(core_eff, halo_eff, core_gain, halo_gain);
}

// Half of the minimum depth footprint of the depth core.
float ribbon_depth_half(float min_pixels, float meters_per_pixel) {
	return 0.5 * max(min_pixels, 0.0) * meters_per_pixel;
}

// Half width of the depth-core strip; one pixel wider than the footprint so rasterisation never clips it.
float ribbon_half_width_core(float core_eff, float depth_half, float meters_per_pixel) {
	return max(3.5 * core_eff, depth_half + meters_per_pixel);
}

// Half width of the colour (halo) strip.
float ribbon_half_width_halo(float core_eff, float halo_eff) {
	return max(3.5 * core_eff, 4.0 * halo_eff);
}

// Depth-core pattern: true where the depth-only core should write. peak = lit level * core gain; threshold = coverage * I
// above which the core writes; x = signed distance across the strip; depth_half = minimum depth footprint half width.
bool ribbon_depth_core_covers(float peak, float threshold, float core_eff, float x, float depth_half) {
	if (peak <= threshold) {
		return false;
	}
	float natural_half = core_eff * sqrt(2.0 * log(peak / threshold));
	return abs(x) <= max(natural_half, depth_half);
}

// Cross profile: Gaussian core plus exponential halo (profile from ribbon_profile). The parameter is not called halo_amplitude:
// both users declare a uniform of that name, and a parameter that shadows an earlier uniform is a redefinition error.
float ribbon_cross_profile(float x, vec4 profile, float halo_amp) {
	return profile.z * exp(-0.5 * x * x / (profile.x * profile.x))
		+ halo_amp * profile.w * exp(-abs(x) / profile.y);
}

// Side edge fade of the strip (side = -1..1).
float ribbon_edge(float side) {
	return 1.0 - smoothstep(0.8, 1.0, abs(side));
}

// Atmosphere attenuation along the camera -> point path (real metres): transmittance (green channel in the Physical model).
float ribbon_atmosphere_visibility(vec3 camera_real, vec3 point_real, vec3 center_real, float background) {
	return ring_haze_visibility(ring_haze_sample(camera_real, point_real, center_real, background));
}
