#ifndef RING_CLOUD_WEATHER_SHARED_GDSHADERINC
#define RING_CLOUD_WEATHER_SHARED_GDSHADERINC

// Pure, periodic macro weather field. `meters.x` is canonical around-ring X;
// `meters.z` is canonical across-ring Z.  The caller supplies all state so
// this source can be shared by Godot materials and Sunshine compute shaders.
uint ring_cloud_weather_hash(uvec3 cell, uint seed) {
	uint h = cell.x * 0x8da6b343u ^ cell.y * 0xd8163841u ^ seed * 0xcb1ab31fu;
	h ^= h >> 16u; h *= 0x7feb352du; h ^= h >> 15u; h *= 0x846ca68bu;
	return h ^ (h >> 16u);
}

float ring_cloud_weather_value(ivec2 cell, int around_cells, uint seed) {
	int wrapped_x = cell.x % around_cells;
	if (wrapped_x < 0) wrapped_x += around_cells;
	return float(ring_cloud_weather_hash(uvec3(uint(wrapped_x), uint(cell.y), seed), seed)) / 4294967295.0;
}

float ring_cloud_weather_noise(vec3 meters, vec4 params, vec4 frame) {
	if (params.y <= 0.0 || frame.x <= 0.0) return 0.5;
	float scale = max(params.y, 1.0);
	int around_cells = max(1, int(round(frame.x / scale)));
	// Scale around-ring X by the integer cell count, rather than C/scale,
	// because the latter is generally fractional and would drift at the seam.
	vec2 p = vec2((meters.x + frame.y) / frame.x * float(around_cells),
		(meters.z + frame.z) / scale);
	ivec2 base = ivec2(floor(p)); vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	uint seed = uint(max(params.w, 0.0));
	float a = ring_cloud_weather_value(base, around_cells, seed);
	float b = ring_cloud_weather_value(base + ivec2(1, 0), around_cells, seed);
	float c = ring_cloud_weather_value(base + ivec2(0, 1), around_cells, seed);
	float d = ring_cloud_weather_value(base + ivec2(1, 1), around_cells, seed);
	float primary = mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
	// A restrained second octave breaks rectangular fronts without changing
	// the periodic seam or adding time-dependent randomization.
	vec2 q = p * 2.0 + vec2(19.0, -11.0);
	ivec2 qb = ivec2(floor(q)); vec2 qf = fract(q); qf = qf * qf * (3.0 - 2.0 * qf);
	int qcells = max(1, around_cells * 2);
	float qa = ring_cloud_weather_value(qb, qcells, seed + 0x9e3779b9u);
	float qb1 = ring_cloud_weather_value(qb + ivec2(1, 0), qcells, seed + 0x9e3779b9u);
	float qc = ring_cloud_weather_value(qb + ivec2(0, 1), qcells, seed + 0x9e3779b9u);
	float qd = ring_cloud_weather_value(qb + ivec2(1, 1), qcells, seed + 0x9e3779b9u);
	return mix(primary, mix(mix(qa, qb1, qf.x), mix(qc, qd, qf.x), qf.y), 0.28);
}

float ring_cloud_weather_resolved_coverage(float base_coverage, float storm_strength, vec3 meters, vec4 params, vec4 frame) {
	if (params.y <= 0.0) return clamp(max(base_coverage, storm_strength), 0.0, 1.0);
	float field = ring_cloud_weather_noise(meters, params, frame);
	// Compress the broad middle band into coherent clear and cloudy regions;
	// bilinear value noise keeps transitions continuous rather than per-cell.
	float shaped = mix(0.05, 2.0, smoothstep(0.20, 0.80, field));
	return clamp(max(base_coverage * mix(1.0, shaped, clamp(params.x, 0.0, 1.0)), storm_strength), 0.0, 1.0);
}

float ring_cloud_weather_clear_gate(float resolved_coverage, float storm_strength, vec4 params) {
	if (params.y <= 0.0) return 1.0;
	float gate = smoothstep(params.z, min(1.0, params.z + 0.15), resolved_coverage);
	return max(gate, smoothstep(0.20, 0.55, storm_strength));
}

float ring_cloud_handoff_near_weight(float sample_distance_meters, vec4 handoff) {
	if (handoff.y <= handoff.x || handoff.x <= 0.0) return 1.0;
	return 1.0 - smoothstep(handoff.x, handoff.y, sample_distance_meters);
}

// World positions in the scaled far viewport are converted back to physical
// metres here.  Keep this helper shared with the clipmap regression path: a
// multiply by coordinate_scale makes certified-scale handoffs vanish.
float ring_cloud_physical_distance(vec3 point, vec3 camera, float coordinate_scale) {
	return length(point - camera) / max(abs(coordinate_scale), 0.000001);
}
#endif
