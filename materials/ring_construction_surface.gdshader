shader_type spatial;
render_mode diffuse_burley, specular_schlick_ggx;
// Player construction concrete (SmoothConstructionOverlay). Replaces the StandardMaterial3D so it can read the per-location storm level (ambient and sun fade like every
// ring receiver) and the wet world (darkening, gloss, puddles and ripples on flat tops). It carries no haze of its own: construction sits next to the player and the
// engine environment fog was never applied to it. Dry, clear weather matches the old StandardMaterial3D look (albedo 0.46 / 0.46 / 0.48, roughness 0.9, specular 0.5).
uniform vec3 concrete_albedo : source_color = vec3(0.46, 0.46, 0.48);
uniform float concrete_roughness : hint_range(0.0, 1.0) = 0.9;
#include "res://materials/ring_wetness.gdshaderinc"

varying vec3 construction_world;
varying vec3 construction_wet_state; // (gloss, roughness, specular reflectance)
varying float construction_storm;

void vertex() {
	construction_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	vec3 albedo = concrete_albedo;
	float roughness = concrete_roughness;
	float specular = 0.5;
	float wet_gloss = 0.0;
	construction_storm = ring_storm_level(construction_world);
	if (ring_wet_params.w > 0.5) {
		vec3 wet_normal = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
		vec3 wet_up = ring_wet_frame[2].xyz;
		RingWet wet_state = ring_wet_sample(construction_world, wet_normal, wet_up);
		ring_wet_apply(wet_state, 0.5, albedo, roughness, specular);
		wet_gloss = ring_wet_gloss(wet_state);
		float wet_distance = distance(construction_world, INV_VIEW_MATRIX[3].xyz);
		float flatness = ring_wet_flatness(wet_normal, wet_up);
		if (wet_state.standing > 0.0 && flatness > 0.0 && wet_distance < 150.0) {
			vec2 wet_plane = ring_wet_plane(construction_world);
			vec2 puddle = ring_wet_puddle(wet_state, wet_plane, flatness, 0.5, wet_distance);
			ring_wet_apply_puddle(puddle, wet_state, wet_plane, wet_up, wet_distance, albedo, roughness, specular, wet_normal);
			if (puddle.x > 0.0) NORMAL = mat3(VIEW_MATRIX) * wet_normal;
			wet_gloss = max(wet_gloss, max(puddle.x, puddle.y));
		}
	}
	construction_wet_state = vec3(wet_gloss, roughness, specular);
	ALBEDO = albedo;
	ROUGHNESS = roughness;
	SPECULAR = specular;
	AO = ring_storm_ambient(construction_storm); // storm light: the ambient follows the storm level under the surface
	AO_LIGHT_AFFECT = 0.0;
}

void light() {
	// The sun fades with the storm level like every receiver (terms.storm in ring_shadow_compose.gdshaderinc); other lights are untouched.
	float visibility = ATTENUATION * (LIGHT_IS_DIRECTIONAL ? 1.0 - ring_storm_shadow_fade(construction_storm) : 1.0);
	float n_dot_l = max(dot(NORMAL, LIGHT), 0.0);
	DIFFUSE_LIGHT += (LIGHT_COLOR / PI) * n_dot_l * visibility;
	// Dry specular as the engine's (roughness 0.9, F0 from specular 0.5) plus the wet highlight.
	vec3 f = ring_wet_direct_specular(NORMAL, LIGHT, VIEW, construction_wet_state.y, construction_wet_state.z, 1.0);
	SPECULAR_LIGHT += f * LIGHT_COLOR * visibility * SPECULAR_AMOUNT;
}
