shader_type canvas_item;
render_mode unshaded;

uniform sampler2D screen_texture : hint_screen_texture, repeat_disable, filter_linear_mipmap;
uniform float effect_strength : hint_range(0.0, 1.0) = 0.0;
uniform float depth_factor : hint_range(0.0, 1.0) = 0.0;
uniform float focus_blur_lod : hint_range(0.0, 4.0) = 1.15;
uniform float tint_strength : hint_range(0.0, 1.0) = 0.24;
uniform float red_absorption : hint_range(0.0, 1.0) = 0.30;

void fragment() {
	float strength = clamp(effect_strength, 0.0, 1.0);
	float depth = clamp(depth_factor, 0.0, 1.0);
	float blur_lod = focus_blur_lod * mix(0.82, 1.18, depth);
	vec3 scene_color = textureLod(screen_texture, SCREEN_UV, blur_lod).rgb;

	// An unaided eye loses much of the cornea's focusing power in water. The
	// mip-filtered sample supplies a restrained, gameplay-safe defocus rather
	// than an opaque overlay. Red light also falls off before blue-green light.
	vec3 spectral_transmission = vec3(
		1.0 - red_absorption * mix(0.45, 1.0, depth),
		mix(0.99, 0.88, depth),
		mix(1.0, 0.96, depth));
	vec3 filtered_color = scene_color * spectral_transmission;
	vec3 water_cast = mix(vec3(0.72, 1.03, 1.04), vec3(0.48, 0.82, 1.02), depth);
	filtered_color = mix(filtered_color, filtered_color * water_cast, tint_strength);

	COLOR = vec4(filtered_color, strength);
}
