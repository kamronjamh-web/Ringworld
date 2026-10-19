shader_type spatial;
render_mode unshaded, blend_premul_alpha, depth_draw_never, cull_disabled;
uniform float terrain_cloud_receiver_height_meters = 0.0;
// The camera-local curved mesh is transformed with tangent/inward/up, a
// reflected basis (determinant -1). Its effective winding changes under that
// transform, so directional culling can remove the inhabited-side underside.
// Render both sides; the annular mesh and soft handoff still prevent a flat
// night-time ceiling.
#include "res://materials/ring_terrain_cloud_sun_receiver.gdshaderinc"

// Camera-relative far cloud tier. It deliberately uses a dense procedural
// field rather than the low-resolution whole-ring shell, while canonical
// coordinates keep weather stationary as the player crosses the ring.
uniform vec2 canonical_origin_km = vec2(0.0);
uniform float ring_width_km = 318.0;
uniform float patch_radius_meters = 180000.0;
uniform float patch_across_meters = 20000.0;
uniform float handoff_start_meters = 48000.0;
uniform float handoff_end_meters = 72000.0;
uniform float layer_index = 0.0;
uniform float coverage : hint_range(0.0, 1.0) = 0.55;
uniform float storm_factor : hint_range(0.0, 1.0) = 0.0;
uniform float ring_circumference_km = 31415.0;
uniform sampler2D weather_atlas : hint_default_white, filter_linear_mipmap, repeat_enable;
uniform bool weather_atlas_enabled = false;
uniform float weather_time_seconds = 0.0;
uniform vec2 weather_wind_uv_per_second = vec2(0.0);
uniform vec4 far_lightning_event_a = vec4(0.0);
uniform vec4 far_lightning_event_b = vec4(0.0);
uniform float far_lightning_event_a_id = 0.0;
uniform float far_lightning_event_b_id = 0.0;
// A/B payloads remain authoritative raw pulses; this is renderer ownership.
uniform vec2 far_lightning_event_weights = vec2(0.0);
uniform float opacity : hint_range(0.0, 1.0) = 0.62;
uniform float viewer_height_meters = 0.0;
uniform vec3 surface_up = vec3(0.0, 1.0, 0.0);
uniform vec3 sun_direction = vec3(1.0, 0.0, 0.0);
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_far_cloud_density.gdshaderinc"
#include "res://materials/ring_cloud_weather_shared.gdshaderinc"

varying vec3 world_position;

float hash21(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float noise2(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash21(i), hash21(i + vec2(1.0, 0.0)), f.x),
		mix(hash21(i + vec2(0.0, 1.0)), hash21(i + vec2(1.0)), f.x), f.y);
}
float fbm(vec2 p) {
	float v = 0.0, a = 0.55;
	for (int i = 0; i < 4; i++) { v += noise2(p) * a; p = p * 2.03 + vec2(17.3, -11.7); a *= 0.5; }
	return v;
}

vec2 far_lightning_glow(vec2 canonical_uv, vec4 event_data) {
	if (event_data.w <= 0.0 || event_data.z <= 0.0) return vec2(0.0);
	vec2 delta = vec2(fract(canonical_uv.x - event_data.x + 0.5) - 0.5,
		(canonical_uv.y - event_data.y) * ring_width_km / max(ring_circumference_km, 1.0));
	float distance_normalized = length(delta) / event_data.z;
	// Footprint is derived before ring wrapping, so seam derivatives remain sane.
	vec2 metric_uv = vec2(canonical_uv.x,
		canonical_uv.y * ring_width_km / max(ring_circumference_km, 1.0));
	float pixel_metric = max(length(dFdx(metric_uv)), length(dFdy(metric_uv)));
	float core_radius = max(0.24, min(0.75, 1.5 * pixel_metric / event_data.z));
	float broad = (1.0 - smoothstep(0.35, 1.55, distance_normalized)) * event_data.w;
	float core = (1.0 - smoothstep(0.0, core_radius, distance_normalized)) * event_data.w;
	return vec2(core, broad);
}

void vertex() {
	world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	float radius_km = patch_radius_meters * 0.001;
	vec2 patch_extent_km = vec2(radius_km * 2.0, patch_across_meters * 0.001);
	vec2 p = canonical_origin_km + (UV - vec2(0.5)) * patch_extent_km;
	vec2 weather_uv = vec2(fract(p.x / max(ring_circumference_km, 1.0)), clamp(p.y / max(ring_width_km, 1.0), 0.0, 1.0));
	weather_uv.x = fract(weather_uv.x - weather_wind_uv_per_second.x * weather_time_seconds);
	weather_uv.y = clamp(weather_uv.y - weather_wind_uv_per_second.y * weather_time_seconds, 0.0, 1.0);
	vec4 weather = texture(weather_atlas, weather_uv);
	float sampled_coverage = weather_atlas_enabled ? weather.r : 0.0;
	float sampled_storm = weather_atlas_enabled ? weather.g : 0.0;
	float broad = weather_atlas_enabled ? sampled_coverage : fbm(p * (0.018 + layer_index * 0.004));
	float detail = weather_atlas_enabled ? (weather.a * 0.72 + 0.14) : fbm(p * (0.070 + layer_index * 0.012) + 9.1);
	float field = weather_atlas_enabled ? broad * 0.82 + detail * 0.18 : broad * 0.72 + detail * 0.28;
	float near_distance_meters = ring_cloud_physical_distance(world_position,
		ring_atmosphere_camera_world_position, ring_atmosphere_coordinate_scale);
	float handoff_blend = smoothstep(handoff_start_meters,
		max(handoff_start_meters + 1.0, handoff_end_meters), near_distance_meters);
	// Local storm ownership is needed at the near edge, but must yield to the
	// regional atlas after the same physical handoff or it blankets clear cells.
	float resolved_storm_factor = weather_atlas_enabled
		? mix(storm_factor, sampled_storm, handoff_blend)
		: max(storm_factor, sampled_storm);
	// The overlap begins with the near resource's weather sample and arrives at
	// atlas coverage only after the same physical handoff distance.
	float atlas_coverage = weather_atlas_enabled
		? clamp(sampled_coverage + far_density_coverage_bias, 0.0, 1.0) : coverage;
	atlas_coverage = mix(cloud_weather_frame.w, atlas_coverage, handoff_blend);
	float resolved_coverage = ring_cloud_weather_resolved_coverage(atlas_coverage,
		resolved_storm_factor, vec3(p.x * 1000.0, 0.0, p.y * 1000.0),
		cloud_weather_params, cloud_weather_frame);
	float weather_gate = ring_cloud_weather_clear_gate(resolved_coverage,
		resolved_storm_factor, cloud_weather_params);
	float shared_atlas_coverage = mix(0.70, 0.90, resolved_coverage);
	// The fallback field retains sparse cloud detail where weather permits it;
	// the shared clear gate removes genuinely cloud-free weather regions.
	float threshold = mix(0.62, 0.34, resolved_coverage) - resolved_storm_factor * 0.10;
	float cloud = smoothstep(threshold - 0.10, threshold + 0.12, field) * weather_gate;
	float near_fade = handoff_blend;
	// p is canonical km; the local y component is the physical cloud altitude.
	float shared_coverage = cloud_weather_params.y > 0.0
		? shared_atlas_coverage
		: mix(far_density_clouds_coverage, shared_atlas_coverage, near_fade);
	float shared_density = far_density_sample_coverage(vec3(p.x * 1000.0, mix(far_density_background_altitude.x, far_density_background_altitude.y, layer_index * 0.3 + 0.2), p.y * 1000.0), shared_coverage);
	if (shared_density >= 0.0) cloud = max(mix(cloud, shared_density * weather_gate, far_density_strength), resolved_storm_factor * 0.45);
	float edge = 1.0 - smoothstep(0.72, 1.0, length((UV - vec2(0.5)) * 2.0));
	float ring_edge = smoothstep(0.0, 1.5, p.y)
		* (1.0 - smoothstep(ring_width_km - 1.5, ring_width_km, p.y));
	// The curved disk fades through the same physical camera-distance interval
	// as Sunshine, including the camera's altitude above or below the canopy.
	float day = ringworld_effective_solar_day(smoothstep(-0.2, 0.35, dot(normalize(surface_up), normalize(sun_direction))));
	vec3 bright = mix(vec3(0.30, 0.36, 0.46), vec3(0.90, 0.94, 1.0), day);
	vec3 storm = mix(vec3(0.045, 0.065, 0.10), vec3(0.17, 0.21, 0.29), day);
	float slice_height = layer_index * 0.3 + 0.2;
	float sun_rim = pow(clamp(dot(normalize(surface_up), normalize(sun_direction)), 0.0, 1.0), 2.0) * (0.18 + 0.16 * slice_height);
	float underside = mix(0.64, 1.0, clamp(cloud + slice_height * 0.25 + sun_rim, 0.0, 1.0));
	vec3 color = mix(bright, storm, clamp(resolved_storm_factor * 0.88 + resolved_coverage * 0.18, 0.0, 1.0)) * underside;
	float ambient_underside = mix(0.64, 1.0, clamp(cloud + slice_height * 0.25, 0.0, 1.0));
	vec3 ambient_color = mix(vec3(0.30, 0.36, 0.46), vec3(0.045, 0.065, 0.10), clamp(resolved_storm_factor * 0.88 + resolved_coverage * 0.18, 0.0, 1.0)) * mix(underside, ambient_underside, day);
	float terrain_sun = ring_terrain_cloud_receiver_visibility(vec3(p.x * 1000.0, terrain_cloud_receiver_height_meters, p.y * 1000.0));
	color -= max(color - ambient_color, vec3(0.0)) * (1.0 - terrain_sun);
	RingHaze haze = ring_haze_sample(
		ring_atmosphere_camera_world_position,
		world_position,
		vec3(0.0),
		day);
	vec3 atmosphere_transmittance = haze.T;
	vec2 event_uv = vec2(
		p.x / max(ring_circumference_km, 1.0),
		p.y / max(ring_width_km, 1.0));
	vec2 flash_a = far_lightning_glow(event_uv, far_lightning_event_a);
	vec2 flash_b = far_lightning_glow(event_uv, far_lightning_event_b);
	vec2 raw_flash = min(flash_a + flash_b, vec2(1.15, 1.35));
	vec2 far_flash = min(
		flash_a * far_lightning_event_weights.x + flash_b * far_lightning_event_weights.y,
		vec2(1.15, 1.35));
	// The canonical ownership weight is already the 48-72 km fade. Keep the
	// cloud-body near fade out of this carrier so it cannot be squared.
	float flash_carrier = far_flash.y * ring_edge;
	// CPU shared events are storm-validated; coarse atlas sampling must not
	// suppress their bounded scattering carrier.
	float storm_gate = 1.0;
	vec3 far_glow = (vec3(0.18, 0.28, 0.46) * far_flash.y * storm_gate * 0.55
		+ vec3(0.82, 0.91, 1.0) * far_flash.x * storm_gate * 2.4)
		* far_density_opacity_multiplier;
	float path = clamp(1.0 / max(0.25, abs(dot(normalize(surface_up), normalize(world_position - ring_atmosphere_camera_world_position)))), 1.0, 3.5);
	float detail_alpha = (1.0 - exp(-cloud * 1.4 * path));
	float legacy_alpha = cloud;
	float body_alpha = mix(legacy_alpha, detail_alpha, far_density_enabled ? far_density_strength : 0.0);
	float base_alpha = min(1.0, body_alpha * edge * ring_edge * near_fade * opacity * far_density_opacity_multiplier * 1.5);
	float flash_mask = ring_edge * opacity * 1.5;
	float reference_alpha = min(1.0, max(body_alpha * edge * ring_edge * near_fade * opacity * 1.5, raw_flash.y * 0.22 * flash_mask));
	// At full ownership this is the old glow*alpha response over clear carrier
	// or opaque body; partial ownership remains linear through far_glow.
	vec3 flash_premultiplied = far_glow * reference_alpha;
	// Keep physical cloud alpha separate from the bounded validated flash.
	// Premultiplied alpha makes a clear-carrier flash additive, never absorbing
	// the bright ring it is meant to illuminate.
	ALBEDO = (color * base_alpha + flash_premultiplied) * atmosphere_transmittance;
	EMISSION = color * mix(0.025, 0.12, day) * base_alpha * atmosphere_transmittance
		+ ring_haze_inscatter(haze) * base_alpha;
	ALPHA = base_alpha;
}
