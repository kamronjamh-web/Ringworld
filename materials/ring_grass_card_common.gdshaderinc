// Shared pieces of the grass-card and hero-blade materials (grass-cards plan step S4): the runtime density curve (GLSL
// twin of RingGrassCardCurve.cs, parity-tested), wind/interaction bending, and the terrain-lit light() body. Lighting,
// atmosphere and macro/cloud shadow uniforms come from ring_terrain_lighting.gdshaderinc (do not redeclare them); the
// Terrain3D surface forwards them to followers. This file adds only the surface-frame values that live in the terrain
// shader itself.
#include "res://materials/ring_terrain_lighting.gdshaderinc"
#include "res://materials/ring_local_wind.gdshaderinc"
#include "res://materials/ring_grass_gust.gdshaderinc"
#include "res://materials/ring_grass_modifiers.gdshaderinc"
#include "res://materials/ring_grass_interaction.gdshaderinc"
#include "res://materials/ring_grass_card_curve.gdshaderinc"

// Names match the Terrain3D surface shader so RegisterSurfaceFollower forwards them.
uniform vec3 ring_atmosphere_camera_world = vec3(0.0);
uniform vec3 ring_atmosphere_center_world = vec3(0.0);
uniform vec3 ring_viewer_world_position = vec3(0.0);
uniform float ring_width_meters = 20000.0;

// ---- Wind ----
float grass_card_phased_wave(vec2 clock, float phase) {
	float a = phase * 6.28318530718;
	return clock.x * cos(a) + clock.y * sin(a);
}

// Legacy (shared-field disabled) gust: z/w are the canonical periodic around/across phases carried in CUSTOM.zw.
float grass_card_legacy_gust(vec4 custom_data) {
	float direction_length = length(ring_local_wind_vector);
	if (ring_local_wind_enabled < .5 || direction_length < .0001) return 0.0;
	vec2 signed_direction = ring_local_wind_vector / direction_length;
	vec2 direction = abs(signed_direction);
	float broad = direction.x * grass_card_phased_wave(ring_local_wind_wave.xy, -sign(signed_direction.x) * custom_data.z)
		+ direction.y * grass_card_phased_wave(ring_local_wind_wave.xy, -sign(signed_direction.y) * custom_data.w);
	float ripple = direction.x * grass_card_phased_wave(ring_local_wind_wave.zw, -sign(signed_direction.x) * fract(custom_data.z * 4.0))
		+ direction.y * grass_card_phased_wave(ring_local_wind_wave.zw, -sign(signed_direction.y) * fract(custom_data.w * 3.0));
	return broad * .28 + ripple * .10;
}

// Tip displacement in world metres (shared field + gust texture + interaction), the same response law as the blade
// shader, bounded below 0.58 of the height. Apply as tip * t^2 - up * short_coeff * t^3 (t = 0 at the root, 1 at the tip).
vec3 grass_card_tip_displacement(vec3 root, vec3 up, float height, float flexibility, vec4 custom_data, out float short_coeff) {
	float wind_strength = clamp(length(ring_wind_field_enabled > .5 ? ring_wind_mean_vector : ring_local_wind_vector) / 20.0, 0.0, 1.0);
	float flexible = mix(.82, 1.22, flexibility);
	vec3 tip_wind = ring_local_wind_displacement(root, 1.0, height);
	vec3 lateral;
	float lateral_limit = height * mix(.20, .58, wind_strength);
	if (ring_wind_field_enabled > .5) {
		float response = length(ring_wind_mean_vector) / 20.0 * max(0.0, grass_wind_strength_multiplier)
			* flexible * grass_texture_gust(root);
		lateral = length(tip_wind) > .0001 ? normalize(tip_wind) * lateral_limit * (1.0 - exp(-response)) : vec3(0.0);
	} else {
		lateral = tip_wind * (4.8 + grass_card_legacy_gust(custom_data) * 3.0 * grass_wind_gust_multiplier)
			* grass_wind_strength_multiplier * flexible;
		if (length(lateral) > lateral_limit) lateral *= lateral_limit / length(lateral);
		lateral *= grass_texture_gust(root);
	}
	if (length(lateral) > lateral_limit) lateral *= lateral_limit / length(lateral);
	vec3 total = lateral + grass_interaction_lateral(root, up, height);
	short_coeff = min(height * .20, dot(total, total) / max(height * 1.45, .05));
	return total;
}

// ---- Lighting (the terrain's own direct-light body; card/hero bases therefore match the ground) ----
// Ground-frame day factor, identical to the terrain vertex stage.
float grass_card_day_factor(vec3 ground_up) {
	return ringworld_effective_solar_day(smoothstep(day_start_dot, day_full_dot, dot(ground_up, normalize(sun_direction))));
}

// Macro-shadow uv of a canonical offset from the tile origin (zw of grass_terrain_origin_uv is the origin's uv).
vec2 grass_card_shadow_uv(vec2 canonical_offset, vec4 origin_uv) {
	return vec2(origin_uv.z + canonical_offset.x / (6.28318530718 * ring_radius_meters),
		origin_uv.w + canonical_offset.y / max(ring_width_meters, 1.0));
}

// Terrain night tint and per-vertex atmosphere: returns the final albedo and the in-scattered emission.
vec3 grass_card_finish_albedo(vec3 colour, float day_factor, vec4 atmosphere, out vec3 emission) {
	vec3 lit = ring_terrain_night_tint(colour, day_factor);
	emission = atmosphere.rgb * atmosphere.a * atmosphere_scattering_energy;
	return lit * (1.0 - atmosphere.a);
}
