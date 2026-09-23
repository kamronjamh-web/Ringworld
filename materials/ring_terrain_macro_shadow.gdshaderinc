uniform sampler2D terrain_macro_shadow_texture : filter_linear, repeat_enable;
uniform bool terrain_macro_shadow_enabled = false;
uniform float terrain_macro_shadow_strength : hint_range(0.0, 0.8) = 0.34;
uniform float terrain_macro_shadow_fade_start_meters = 3200.0;
uniform float terrain_macro_shadow_fade_end_meters = 6500.0;

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
