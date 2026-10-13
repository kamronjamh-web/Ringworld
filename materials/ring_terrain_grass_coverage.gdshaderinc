// Zero-root distant grass: stable ground micro-coverage that takes over once
// individual grass geometry is below useful pixel size. The default 200 m
// overlap also covers resident-tile budgets that cannot reach the full 500 m
// geometry target. It has no time input,
// preventing temporal shimmer/afterimages during camera movement.
global uniform float ring_grass_terrain_coverage_enabled;
global uniform float ring_grass_terrain_coverage_start_meters;
global uniform float ring_grass_terrain_coverage_end_meters;
global uniform float ring_grass_terrain_coverage_strength;
global uniform float ring_grass_coordinated_handoff_enabled;
global uniform float ring_grass_coordinated_handoff_start_meters;
global uniform float ring_grass_coordinated_handoff_end_meters;

float ring_terrain_grass_hash(vec2 cell) {
	return fract(sin(dot(cell, vec2(127.1, 311.7))) * 43758.5453123);
}

float ring_terrain_grass_coverage(vec3 position, float camera_distance, float upward, float grass_eligibility) {
	float start = ring_grass_terrain_coverage_start_meters;
	float end = ring_grass_terrain_coverage_end_meters;
	if (ring_grass_coordinated_handoff_enabled > .5) {
		start = min(start, ring_grass_coordinated_handoff_start_meters);
		end = min(end, ring_grass_coordinated_handoff_end_meters);
	}
	// Tiny sustainable windows require sub-meter bands. Manual mode preserves
	// the old one-meter minimum and all existing coverage controls.
	float minimum_band = ring_grass_coordinated_handoff_enabled > .5 ? .001 : 1.0;
	// The field contributes nothing here; avoid both hashes on the near surface
	// and on fragments excluded by the existing terrain masks.
	if (ring_grass_terrain_coverage_enabled < 0.5
		|| camera_distance <= start
		|| upward <= 0.0 || grass_eligibility <= 0.0
		|| ring_grass_terrain_coverage_strength <= 0.0) return 0.0;
	float enabled = step(0.5, ring_grass_terrain_coverage_enabled);
	float distance_fade = smoothstep(
		start,
		max(start + minimum_band, end),
		camera_distance);
	// A world-locked two-scale field gives the terrain a grass-like density
	// breakup without a texture lookup or animated noise.
	float broad = ring_terrain_grass_hash(floor(position.xz * 0.115));
	float fine = ring_terrain_grass_hash(floor(position.xz * 0.47));
	float coverage = mix(.58, 1.0, broad) * mix(.78, 1.0, fine);
	return enabled * distance_fade * upward * clamp(grass_eligibility, 0.0, 1.0)
		* coverage * clamp(ring_grass_terrain_coverage_strength, 0.0, 1.0);
}

vec3 ring_apply_terrain_grass_coverage(vec3 albedo, float coverage) {
	// Subtle green/value variation, not opaque fake blades. Forest participates
	// through its caller-supplied eligibility but remains visually grounded.
	vec3 grass_tint = albedo * vec3(.83, 1.045, .78);
	return mix(albedo, grass_tint, coverage);
}
