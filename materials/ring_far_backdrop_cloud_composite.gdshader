shader_type canvas_item;
render_mode unshaded;

uniform sampler2D previous_cloud_mask : filter_linear, repeat_disable;
uniform sampler2D current_cloud_mask : filter_linear, repeat_disable;
uniform sampler2D sunshine_cloud_texture : filter_linear, repeat_disable;
uniform float temporal_blend : hint_range(0.0, 1.0) = 1.0;
uniform bool cloud_mask_enabled = false;
uniform bool exact_sunshine_clouds_enabled = false;

void fragment() {
	vec4 backdrop = texture(TEXTURE, UV);
	if (!cloud_mask_enabled) {
		COLOR = backdrop;
	} else if (exact_sunshine_clouds_enabled) {
		// Sunshine publishes the same temporally accumulated cloud RGB/density
		// used by its post pass. Sampling it here keeps the canvas-backed ring
		// behind the exact cloud silhouette instead of a second noise model.
		vec4 cloud = texture(sunshine_cloud_texture, UV);
		float cloud_alpha = clamp(cloud.a, 0.0, 1.0);
		COLOR = vec4(mix(backdrop.rgb, cloud.rgb, cloud_alpha), backdrop.a);
	} else {
		float blend_value = smoothstep(0.0, 1.0, temporal_blend);
		vec4 cloud = mix(
			texture(previous_cloud_mask, UV),
			texture(current_cloud_mask, UV),
			blend_value);
		// Cloud RGB is premultiplied by opacity in the mask shader.
		COLOR = vec4(backdrop.rgb * (1.0 - cloud.a) + cloud.rgb, backdrop.a);
	}
}
