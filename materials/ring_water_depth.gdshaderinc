// Screen-depth reconstruction for the transparent near-water proxy.  Reverse-Z
// clears the depth texture to zero, which is sky rather than a shallow bottom.
// Return (optical_thickness_meters, has_valid_shore_contact).
vec2 ring_water_depth_from_scene(vec4 scene_h, float raw_scene_depth, float surface_depth) {
	const float OPTICALLY_DEEP_METERS = 5000.0;
	// Do not homogenize an invalid point.  In particular, a reciprocal clamp on
	// w turns distant valid geometry into an artificial, shallow sea floor.
	bool finite_raw = !isnan(raw_scene_depth) && !isinf(raw_scene_depth);
	bool finite_surface = !isnan(surface_depth) && !isinf(surface_depth);
	bool finite_h = !isnan(scene_h.x) && !isnan(scene_h.y) && !isnan(scene_h.z) && !isnan(scene_h.w)
		&& !isinf(scene_h.x) && !isinf(scene_h.y) && !isinf(scene_h.z) && !isinf(scene_h.w);
	if (!finite_raw || raw_scene_depth <= 0.0 || raw_scene_depth > 1.0 || !finite_surface || !finite_h || scene_h.w <= 0.0) {
		return vec2(OPTICALLY_DEEP_METERS, 0.0);
	}
	// This form retains precision when both the terrain and water are far from
	// the camera, instead of subtracting two already-decoded large distances.
	float separation = (-scene_h.z - surface_depth * scene_h.w) / scene_h.w;
	if (isnan(separation) || isinf(separation) || separation < 0.0) {
		// A negative separation is foreground geometry (or corrupt depth), never
		// a shoreline contact for this transparent water surface.
		return vec2(OPTICALLY_DEEP_METERS, 0.0);
	}
	return vec2(clamp(separation, 0.0, OPTICALLY_DEEP_METERS), 1.0);
}
