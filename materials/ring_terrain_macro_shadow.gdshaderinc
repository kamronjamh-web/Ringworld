uniform sampler2D terrain_macro_shadow_texture : filter_linear, repeat_enable;
uniform bool terrain_macro_shadow_enabled = false;
// Camera-relative regional terrain supplies this directly from its mesh. It
// avoids reconstructing a large global cylinder from shader float positions.
uniform bool terrain_macro_shadow_use_uv = false;
uniform float terrain_macro_shadow_strength : hint_range(0.0, 0.8) = 0.34;
uniform float terrain_macro_shadow_fade_start_meters = 3200.0;
uniform float terrain_macro_shadow_fade_end_meters = 6500.0;
// The opposite-side planet has its own projected terrain mask. Unlike the
// sun mask it remains valid on the local night side, where PlanetLight is the
// directional source that supplies the illumination.
uniform sampler2D terrain_planet_shadow_texture : filter_linear, repeat_enable;
uniform bool terrain_planet_shadow_enabled = false;
uniform vec3 terrain_planet_shadow_direction = vec3(0.0, 1.0, 0.0);
uniform float terrain_planet_shadow_strength : hint_range(0.0, 0.8) = 0.34;
uniform float terrain_planet_shadow_fade_start_meters = 3200.0;
uniform float terrain_planet_shadow_fade_end_meters = 6500.0;

float sample_terrain_macro_shadow(
		vec3 sample_world_position,
		vec3 center_world,
		vec3 viewer_world_position,
		float shadow_ring_width_meters) {
	if (!terrain_macro_shadow_enabled) {
		return 0.0;
	}

	vec3 radial = sample_world_position - center_world;
	radial.y = 0.0;
	if (dot(radial, radial) < 0.000001) {
		return 0.0;
	}

	float angle = atan(radial.z, radial.x);
	float around_uv = fract(angle / 6.28318530718);
	float across_uv = (sample_world_position.y - center_world.y)
		/ max(shadow_ring_width_meters, 1.0) + 0.5;
	if (across_uv < 0.0 || across_uv > 1.0) {
		return 0.0;
	}

	// The texture repeats along around-ring U, but its across-ring V must never
	// wrap from one structural-wall edge to the other.
	float across_texel = 1.0 / float(textureSize(terrain_macro_shadow_texture, 0).y);
	across_uv = clamp(across_uv, across_texel * 0.5, 1.0 - across_texel * 0.5);
	float viewer_distance = distance(sample_world_position, viewer_world_position);
	float handoff = smoothstep(
		terrain_macro_shadow_fade_start_meters,
		max(
			terrain_macro_shadow_fade_start_meters + 1.0,
			terrain_macro_shadow_fade_end_meters),
		viewer_distance);
	return texture(
		terrain_macro_shadow_texture,
		vec2(around_uv, across_uv)).r
		* handoff
		* terrain_macro_shadow_strength;
}

float sample_terrain_macro_shadow_uv(
		vec2 canonical_uv,
		vec3 sample_world_position,
		vec3 viewer_world_position) {
	if (!terrain_macro_shadow_enabled) {
		return 0.0;
	}

	if (canonical_uv.y < 0.0 || canonical_uv.y > 1.0) {
		return 0.0;
	}

	// The texture repeats along around-ring U, but its across-ring V must never
	// wrap from one structural-wall edge to the other.
	float across_texel = 1.0 / float(textureSize(terrain_macro_shadow_texture, 0).y);
	float clamped_across_uv = clamp(
		canonical_uv.y,
		across_texel * 0.5,
		1.0 - across_texel * 0.5);
	float viewer_distance = distance(sample_world_position, viewer_world_position);
	float handoff = smoothstep(
		terrain_macro_shadow_fade_start_meters,
		max(
			terrain_macro_shadow_fade_start_meters + 1.0,
			terrain_macro_shadow_fade_end_meters),
		viewer_distance);
	return texture(
		terrain_macro_shadow_texture,
		vec2(fract(canonical_uv.x), clamped_across_uv)).r
		* handoff
		* terrain_macro_shadow_strength;
}

float sample_terrain_planet_shadow(
		vec3 sample_world_position,
		vec3 center_world,
		vec3 viewer_world_position,
		float shadow_ring_width_meters) {
	if (!terrain_planet_shadow_enabled) {
		return 0.0;
	}

	vec3 radial = sample_world_position - center_world;
	radial.y = 0.0;
	if (dot(radial, radial) < 0.000001) {
		return 0.0;
	}

	float angle = atan(radial.z, radial.x);
	float around_uv = fract(angle / 6.28318530718);
	float across_uv = (sample_world_position.y - center_world.y)
		/ max(shadow_ring_width_meters, 1.0) + 0.5;
	if (across_uv < 0.0 || across_uv > 1.0) {
		return 0.0;
	}
	float across_texel = 1.0 / float(textureSize(terrain_planet_shadow_texture, 0).y);
	across_uv = clamp(across_uv, across_texel * 0.5, 1.0 - across_texel * 0.5);
	float viewer_distance = distance(sample_world_position, viewer_world_position);
	float handoff = smoothstep(
		terrain_planet_shadow_fade_start_meters,
		max(terrain_planet_shadow_fade_start_meters + 1.0, terrain_planet_shadow_fade_end_meters),
		viewer_distance);
	return texture(terrain_planet_shadow_texture, vec2(around_uv, across_uv)).r
		* handoff
		* terrain_planet_shadow_strength;
}

float sample_terrain_planet_shadow_uv(
		vec2 canonical_uv,
		vec3 sample_world_position,
		vec3 viewer_world_position) {
	if (!terrain_planet_shadow_enabled || canonical_uv.y < 0.0 || canonical_uv.y > 1.0) {
		return 0.0;
	}

	float across_texel = 1.0 / float(textureSize(terrain_planet_shadow_texture, 0).y);
	float clamped_across_uv = clamp(
		canonical_uv.y,
		across_texel * 0.5,
		1.0 - across_texel * 0.5);
	float viewer_distance = distance(sample_world_position, viewer_world_position);
	float handoff = smoothstep(
		terrain_planet_shadow_fade_start_meters,
		max(terrain_planet_shadow_fade_start_meters + 1.0, terrain_planet_shadow_fade_end_meters),
		viewer_distance);
	return texture(terrain_planet_shadow_texture, vec2(fract(canonical_uv.x), clamped_across_uv)).r
		* handoff
		* terrain_planet_shadow_strength;
}

float terrain_planet_directional_light_weight(vec3 light_direction_view, mat4 view_matrix) {
	vec3 planet_direction_view = normalize((view_matrix * vec4(terrain_planet_shadow_direction, 0.0)).xyz);
	return smoothstep(0.90, 0.995, dot(normalize(light_direction_view), planet_direction_view));
}
