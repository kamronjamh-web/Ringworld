shader_type fog;
#include "res://materials/celestial_lighting.gdshaderinc"

// Camera-local cloud detail. The enclosing FogVolume supplies the finite
// render budget; canonical_origin_km keeps the noise stationary while that
// volume follows the player around the ring.
uniform vec2 canonical_origin_km = vec2(0.0);
uniform vec2 wind_km_per_second = vec2(0.0022, 0.0007);
uniform float coverage : hint_range(0.0, 1.0) = 0.52;
uniform float softness : hint_range(0.01, 0.4) = 0.085;
uniform float density_scale : hint_range(0.0, 0.15) = 0.065;
uniform int quality_level : hint_range(1, 3) = 1;
uniform float world_units_to_km = 0.001;
uniform vec3 surface_up = vec3(-1.0, 0.0, 0.0);
uniform vec3 sun_direction = vec3(1.0, 0.0, 0.0);
uniform vec3 sunlit_color : source_color = vec3(0.91, 0.95, 1.0);
uniform vec3 shadow_color : source_color = vec3(0.31, 0.38, 0.48);
uniform vec3 night_color : source_color = vec3(0.045, 0.065, 0.11);
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

void fog() {
	vec2 local_km = (UVW.xz - vec2(0.5)) * SIZE.xz * world_units_to_km;
	vec2 p = canonical_origin_km + local_km + wind_km_per_second * TIME;
	float height = clamp(UVW.y, 0.0, 1.0);

	float broad = value_noise(p * 0.18);
	float shape = value_noise(p * 0.52 + vec2(height * 0.73, -height * 0.41));
	float field = broad * 0.64 + shape * 0.36;
	if (quality_level >= 2) {
		float detail = value_noise(p * 1.35 + vec2(height * 1.9, height * 1.2));
		field = field * 0.84 + detail * 0.16;
	}
	if (quality_level >= 3) {
		float fine = value_noise(p * 3.1 + vec2(-height * 3.3, height * 2.6));
		field = field * 0.92 + fine * 0.08;
	}

	float vertical_profile = smoothstep(0.03, 0.14, height)
		* (1.0 - smoothstep(0.72, 0.97, height));
	float patch_radius = length((UVW.xz - vec2(0.5)) * 2.0);
	float volume_handoff = 1.0 - smoothstep(0.76, 1.0, patch_radius);
	float cloud = smoothstep(
		coverage - softness * 0.5,
		coverage + softness * 0.5,
		field) * vertical_profile * volume_handoff;

	float day = ringworld_effective_solar_day(smoothstep(day_start_dot, day_full_dot,
		dot(normalize(surface_up), normalize(sun_direction))));
	vec3 day_color = mix(shadow_color, sunlit_color, clamp(height * 0.55 + cloud * 0.65, 0.0, 1.0));
	ALBEDO = mix(night_color, day_color, day);
	DENSITY = cloud * density_scale;
	EMISSION = ALBEDO * cloud * mix(0.025, 0.065, day);
}
