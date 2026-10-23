#ifndef RING_FAR_WEATHER_CLOUD_GDSHADERINC
#define RING_FAR_WEATHER_CLOUD_GDSHADERINC
// A consumer that samples this field coarser than the Sunshine detail noise
// (the far cloud shadow bake) defines this to a band-limited density sampler.
#ifndef RING_FAR_WEATHER_DENSITY_SAMPLE
#define RING_FAR_WEATHER_DENSITY_SAMPLE far_density_sample_coverage
#endif
// The distant shell lays a uniform floor of cloud over storm cells of the coarse weather atlas (one texel is
// about 245 km along the ring). Close up, as an opaque column or a shadow, that floor reads as a flat sheet
// where Sunshine shows sculpted storm towers, so the main-view layer and the shadow bake define it to 0.
#ifndef RING_FAR_WEATHER_STORM_FLOOR
#define RING_FAR_WEATHER_STORM_FLOOR 0.45
#endif
// Cloud amount (0 clear .. 1 opaque body) of the whole-ring far cloud shell at
// canonical metres (x around, z across). One formula for the visible shell
// (ring_cloud_layer.gdshader) and the far cloud shadow
// (ring_far_weather_shadow.gdshaderinc), so far shadows sit under the far
// clouds. Include after ring_far_cloud_density.gdshaderinc and
// ring_cloud_weather_shared.gdshaderinc. weather is the far weather atlas
// texel: r = coverage, g = storm.
// weather_out: x = the weather-resolved coverage (Sunshine's macro "visible" coverage), y = the clear-sky weather gate,
// for consumers that light the clouds or sample the density at more heights.
float ring_far_weather_cloud_amount_resolved(
		vec4 weather, vec3 canonical_meters, float legacy_coverage, float legacy_softness, out vec2 weather_out) {
	float storm_factor = weather.g;
	vec3 weather_meters = vec3(canonical_meters.x, 0.0, canonical_meters.z);
	float resolved_coverage = ring_cloud_weather_resolved_coverage(
		clamp(weather.r + far_density_coverage_bias, 0.0, 1.0), storm_factor, weather_meters,
		cloud_weather_params, cloud_weather_frame);
	float weather_gate = ring_cloud_weather_clear_gate(resolved_coverage, storm_factor, cloud_weather_params);
	float cloud = smoothstep(legacy_coverage, min(1.0, legacy_coverage + legacy_softness), weather.r) * weather_gate;
	float shell_density = RING_FAR_WEATHER_DENSITY_SAMPLE(vec3(
		canonical_meters.x,
		(far_density_background_altitude.x + far_density_background_altitude.y) * 0.5,
		canonical_meters.z), mix(0.70, 0.90, resolved_coverage));
	if (shell_density >= 0.0) cloud = max(mix(cloud, shell_density * weather_gate, far_density_strength), storm_factor * RING_FAR_WEATHER_STORM_FLOOR);
	weather_out = vec2(resolved_coverage, weather_gate);
	return cloud;
}

float ring_far_weather_cloud_amount(
		vec4 weather, vec3 canonical_meters, float legacy_coverage, float legacy_softness) {
	vec2 unused_weather;
	return ring_far_weather_cloud_amount_resolved(weather, canonical_meters, legacy_coverage, legacy_softness, unused_weather);
}
#endif
