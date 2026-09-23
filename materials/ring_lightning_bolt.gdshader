shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never, shadows_disabled;

uniform vec4 bolt_color : source_color = vec4(0.72, 0.86, 1.0, 1.0);
uniform float intensity : hint_range(0.0, 2.0) = 0.0;
// Source pulse stays raw; distance ownership belongs in the additive alpha
// exactly once rather than in both radiance and alpha.
uniform float ownership_weight : hint_range(0.0, 1.0) = 1.0;
uniform float emission_energy : hint_range(0.0, 64.0) = 8.0;

void fragment() {
	float visible_intensity = max(0.0, intensity);
	vec3 color = bolt_color.rgb * visible_intensity;
	ALBEDO = color;
	EMISSION = color * emission_energy;
	ALPHA = bolt_color.a * clamp(visible_intensity, 0.0, 1.0)
		* clamp(ownership_weight, 0.0, 1.0);
}
