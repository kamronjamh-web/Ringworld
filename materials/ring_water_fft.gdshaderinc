// FFT ocean surface shared by the vertex and fragment paths. Displacement is
// sampled at its undisplaced canonical position: R=around, G=surface-up,
// B=across. Do not feed a displaced position back into these coordinates.
uniform bool fft_ready = false;
uniform sampler2DArray fft_normal_foam : filter_linear_mipmap, repeat_enable;
uniform sampler2DArray fft_displacement : filter_linear_mipmap, repeat_enable;
uniform vec3 fft_anchor_world = vec3(0.0);
uniform vec3 fft_around_axis = vec3(0.0, 0.0, 1.0);
uniform vec3 fft_across_axis = vec3(0.0, 1.0, 0.0);
uniform vec2 fft_period_0 = vec2(12.0);
uniform vec2 fft_period_1 = vec2(79.0);
uniform vec2 fft_period_2 = vec2(316.0);
uniform vec2 fft_period_3 = vec2(1264.0);
uniform vec2 fft_phase_0 = vec2(0.0);
uniform vec2 fft_phase_1 = vec2(0.0);
uniform vec2 fft_phase_2 = vec2(0.0);
uniform vec2 fft_phase_3 = vec2(0.0);
uniform int fft_cascade_count = 0;
uniform float fft_detail_distance_meters = 300.0;
uniform float fft_normal_strength = 0.25;
uniform float fft_foam_strength = 0.35;
uniform float fft_geometry_height_scale : hint_range(0.0, 10.0) = 1.0;
uniform float fft_geometry_choppiness : hint_range(0.0, 3.0) = 0.75;
uniform float fft_geometry_distance_meters : hint_range(25.0, 2000.0) = 240.0;
uniform float fft_geometry_filter_meters : hint_range(1.0, 8.0) = 2.0;
uniform float fft_geometry_spacing_meters : hint_range(0.25, 8.0) = 2.0;
// A fine proxy blends to this explicitly supplied parent lattice only while it
// is handing ownership to that parent. A zero parent spacing means a flat
// parent; a zero sector size keeps standalone materials on the legacy path.
uniform float fft_geometry_parent_spacing_meters = 0.0;
uniform float proxy_sector_size_meters = 0.0;
uniform float fft_crest_lighting_strength : hint_range(0.0, 1.0) = 0.35;
uniform float fft_resolution = 128.0;
// Canonical terrain-contact field. Until a valid field is published, geometry
// is pinned at the authored water base while fine normal detail remains.
// 0 bypasses the field for standalone/legacy FFT tests, 1 pins while a
// production field is unavailable, and 2 samples a valid published field.
uniform int fft_shoreline_field_mode = 0;
uniform sampler2D fft_shoreline_field : filter_linear, repeat_disable;
uniform vec2 fft_shoreline_field_center_canonical = vec2(0.0);
uniform float fft_shoreline_field_cell_meters = 2.0;
uniform float fft_shoreline_fade_distance_meters = 10.0;
uniform float fft_shoreline_chop_fade_distance_meters = 20.0;
uniform float fft_shoreline_shallow_depth_meters = 3.0;
uniform float fft_deep_water_wave_depth_meters = 100.0;

bool ring_fft_ocean_ready(float water_kind) {
	return fft_ready && fft_cascade_count > 0 && abs(water_kind - 1.0) <= 0.1;
}
vec2 ring_fft_canonical(vec3 source_world) {
	vec3 local = source_world - fft_anchor_world;
	return vec2(dot(local, fft_around_axis), dot(local, fft_across_axis));
}
int ring_fft_geometry_layer() { return fft_cascade_count > 1 ? 1 : 0; }
vec2 ring_fft_period(int layer) { return layer == 0 ? fft_period_0 : layer == 1 ? fft_period_1 : layer == 2 ? fft_period_2 : fft_period_3; }
vec2 ring_fft_phase(int layer) { return layer == 0 ? fft_phase_0 : layer == 1 ? fft_phase_1 : layer == 2 ? fft_phase_2 : fft_phase_3; }
vec2 ring_fft_uv(vec2 canonical, int layer) {
	return canonical / ring_fft_period(layer) + ring_fft_phase(layer);
}
// `distant_factors` is supplied by the owning shader. Keeping this include
// independent of the spatial ocean material also allows standalone canvas
// diagnostics to compile it without ocean-mask uniforms/functions.
vec2 ring_fft_shoreline_factors(vec2 canonical, vec2 distant_factors) {
	if (fft_shoreline_field_mode == 0) return vec2(1.0);
	if (fft_shoreline_field_mode != 2 || fft_shoreline_field_cell_meters <= 0.0) return vec2(0.0);
	// Field samples are stored at atlasOrigin + pixel * cell, so add half a
	// texel after mapping canonical source space to texel coordinates.
	vec2 uv = (canonical - fft_shoreline_field_center_canonical) / (fft_shoreline_field_cell_meters * 512.0) + vec2(0.5 + 0.5 / 512.0);
	if (any(lessThan(uv, vec2(0.0))) || any(greaterThanEqual(uv, vec2(1.0))))
		return distant_factors;
	vec4 field = texture(fft_shoreline_field, uv);
	// B certifies this texel. R/G are signed depth/distance in metres.
	if (field.b < 0.999) return vec2(0.0);
	float depth = field.r;
	float shore_distance = field.g;
	if (depth <= 0.0) return vec2(0.0);
	float vertical = smoothstep(max(0.0, fft_shoreline_shallow_depth_meters),
		max(fft_shoreline_shallow_depth_meters + 0.01, fft_deep_water_wave_depth_meters), depth)
		* smoothstep(0.0, max(0.01, fft_shoreline_fade_distance_meters), shore_distance);
	float chop = vertical * smoothstep(0.0, max(0.01, fft_shoreline_chop_fade_distance_meters), shore_distance);
	return vec2(vertical, chop);
}
float ring_fft_geometry_fade(float distance_to_camera) {
	float end_distance = max(1.0, min(fft_geometry_distance_meters, fft_detail_distance_meters));
	return 1.0 - smoothstep(end_distance * 0.65, end_distance, distance_to_camera);
}
float ring_fft_shading_fade(float distance_to_camera) {
	return 1.0 - smoothstep(fft_detail_distance_meters * 0.82, max(1.0, fft_detail_distance_meters), distance_to_camera);
}
float ring_fft_geometry_lod(int layer) {
	vec2 period = ring_fft_period(layer);
	float texel_size = max(period.x, period.y) / max(1.0, fft_resolution);
	float filter_meters = max(fft_geometry_filter_meters, fft_geometry_spacing_meters);
	return max(0.0, log2(max(1.0, filter_meters / texel_size)));
}
float ring_fft_geometry_lod_for_spacing(int layer, float spacing_meters) {
	vec2 period = ring_fft_period(layer);
	float texel_size = max(period.x, period.y) / max(1.0, fft_resolution);
	// Coarser proxy rings must not sample detail they cannot represent. This
	// keeps the 4m/8m handoff stable while preserving the exact near geometry.
	float filter_meters = max(fft_geometry_filter_meters, spacing_meters);
	return max(0.0, log2(max(1.0, filter_meters / texel_size)));
}
vec3 ring_fft_displacement_lod(vec2 canonical, int layer, float lod) {
	return textureLod(fft_displacement, vec3(fract(ring_fft_uv(canonical, layer)), float(layer)), lod).rgb;
}
vec3 ring_fft_geometry_displacement(vec2 canonical) {
	vec3 displacement = vec3(0.0);
	int first_layer = fft_cascade_count > 1 ? 1 : 0;
	for (int layer = 0; layer < 4; ++layer) {
		if (layer < first_layer || layer >= fft_cascade_count) continue;
		displacement += ring_fft_displacement_lod(canonical, layer, ring_fft_geometry_lod(layer));
	}
	return displacement * vec3(fft_geometry_height_scale * fft_geometry_choppiness, fft_geometry_height_scale, fft_geometry_height_scale * fft_geometry_choppiness);
}
vec3 ring_fft_geometry_displacement_for_spacing(vec2 canonical, float spacing_meters) {
	vec3 displacement = vec3(0.0);
	int first_layer = fft_cascade_count > 1 ? 1 : 0;
	for (int layer = 0; layer < 4; ++layer) {
		if (layer < first_layer || layer >= fft_cascade_count) continue;
		displacement += ring_fft_displacement_lod(canonical, layer, ring_fft_geometry_lod_for_spacing(layer, spacing_meters));
	}
	return displacement * vec3(fft_geometry_height_scale * fft_geometry_choppiness, fft_geometry_height_scale, fft_geometry_height_scale * fft_geometry_choppiness);
}
vec3 ring_fft_displacement_grad(vec2 canonical, int layer, vec2 canonical_dx, vec2 canonical_dy) {
	vec2 period = ring_fft_period(layer);
	float footprint_texels = max(length(canonical_dx / period), length(canonical_dy / period)) * fft_resolution;
	float footprint_lod = max(0.0, log2(max(1.0, footprint_texels)));
	return ring_fft_displacement_lod(canonical, layer, max(ring_fft_geometry_lod(layer), footprint_lod));
}
vec3 ring_fft_displacement_grad_for_spacing(vec2 canonical, int layer, vec2 canonical_dx, vec2 canonical_dy, float spacing_meters) {
	// Keep the exact geometry mip nearby, then increase it with the projected
	// source footprint at distance so the displaced normal cannot shimmer.
	vec2 period = ring_fft_period(layer);
	float footprint_texels = max(length(canonical_dx / period), length(canonical_dy / period)) * fft_resolution;
	float footprint_lod = max(0.0, log2(max(1.0, footprint_texels)));
	return ring_fft_displacement_lod(canonical, layer, max(ring_fft_geometry_lod_for_spacing(layer, spacing_meters), footprint_lod));
}
vec3 ring_fft_geometry_displacement_grad(vec2 canonical, vec2 canonical_dx, vec2 canonical_dy) {
	vec3 total = vec3(0.0);
	int first_layer = fft_cascade_count > 1 ? 1 : 0;
	for (int layer = 0; layer < 4; ++layer) {
		if (layer < first_layer || layer >= fft_cascade_count) continue;
		total += ring_fft_displacement_grad(canonical, layer, canonical_dx, canonical_dy);
	}
	return total;
}
vec3 ring_fft_geometry_displacement_grad_for_spacing(vec2 canonical, vec2 canonical_dx, vec2 canonical_dy, float spacing_meters) {
	vec3 total = vec3(0.0);
	int first_layer = fft_cascade_count > 1 ? 1 : 0;
	for (int layer = 0; layer < 4; ++layer) {
		if (layer < first_layer || layer >= fft_cascade_count) continue;
		total += ring_fft_displacement_grad_for_spacing(canonical, layer, canonical_dx, canonical_dy, spacing_meters);
	}
	return total;
}
vec4 ring_fft_normal_foam_grad(vec2 canonical, int layer, vec2 canonical_dx, vec2 canonical_dy) {
	vec2 period = ring_fft_period(layer);
	vec2 uv = ring_fft_uv(canonical, layer);
	return textureGrad(fft_normal_foam, vec3(fract(uv), float(layer)), canonical_dx / period * detail_filtering_strength, canonical_dy / period * detail_filtering_strength);
}
