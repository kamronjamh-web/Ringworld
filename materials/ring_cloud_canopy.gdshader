shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled;
#include "res://materials/celestial_lighting.gdshaderinc"

uniform vec2 canonical_origin_km = vec2(0.0);
uniform vec2 patch_size_km = vec2(20.0);
uniform vec2 wind_km_per_second = vec2(0.0022, 0.0007);
uniform float coverage : hint_range(0.0, 1.0) = 0.47;
uniform float softness : hint_range(0.01, 0.4) = 0.15;
uniform float opacity : hint_range(0.0, 1.0) = 0.46;
uniform float handoff_start_fraction : hint_range(0.0, 1.0) = 0.76;
uniform float handoff_end_fraction : hint_range(0.0, 1.5) = 1.0;
uniform float viewer_height_meters = 0.0;
uniform float cloud_base_meters = 1400.0;
uniform float cloud_top_meters = 3200.0;
uniform vec3 surface_up = vec3(-1.0, 0.0, 0.0);
uniform vec3 sun_direction = vec3(1.0, 0.0, 0.0);
uniform vec3 sunlit_color : source_color = vec3(0.95, 0.97, 1.0);
uniform vec3 shadow_color : source_color = vec3(0.28, 0.35, 0.44);
uniform vec3 night_color : source_color = vec3(0.055, 0.075, 0.12);
uniform float day_start_dot = -0.2;
uniform float day_full_dot = 0.35;

float hash21(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123);
}

float value_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(
		mix(hash21(i), hash21(i + vec2(1.0, 0.0)), f.x),
		mix(hash21(i + vec2(0.0, 1.0)), hash21(i + vec2(1.0, 1.0)), f.x),
		f.y);
}

void fragment() {
	vec2 p = canonical_origin_km + (UV - vec2(0.5)) * patch_size_km
		+ wind_km_per_second * TIME;
	float broad = value_noise(p * 0.18);
	float shape = value_noise(p * 0.52 + vec2(0.61, -0.37));
	float detail = value_noise(p * 1.35 + vec2(-1.7, 2.1));
	float field = broad * 0.58 + shape * 0.29 + detail * 0.13;
	float cloud = smoothstep(coverage, min(1.0, coverage + softness), field);
	float patch_radius = length((UV - vec2(0.5)) * 2.0);
	float cheap_handoff = smoothstep(handoff_start_fraction, handoff_end_fraction, patch_radius);
	float outer_edge = 1.0 - smoothstep(0.96, 1.0, max(abs(UV.x - 0.5), abs(UV.y - 0.5)) * 2.0);

	// The plane supplies a readable sky silhouette. Fade it while the camera is
	// inside the cloud band so the FogVolume remains the visible representation.
	float inside_band = smoothstep(cloud_base_meters - 250.0, cloud_base_meters + 250.0, viewer_height_meters)
		* (1.0 - smoothstep(cloud_top_meters - 250.0, cloud_top_meters + 250.0, viewer_height_meters));
	float day = ringworld_effective_solar_day(smoothstep(day_start_dot, day_full_dot,
		dot(normalize(surface_up), normalize(sun_direction))));
	vec3 day_color = mix(shadow_color, sunlit_color, clamp(cloud * 1.15, 0.0, 1.0));
	ALBEDO = mix(night_color, day_color, day);
	EMISSION = ALBEDO * mix(0.20, 0.34, day);
	ALPHA = cloud * cheap_handoff * outer_edge * opacity * mix(1.0, 0.16, inside_band);
}
