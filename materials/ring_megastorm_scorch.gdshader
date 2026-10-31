shader_type spatial;
render_mode unshaded, blend_mix, cull_disabled, depth_draw_never, shadows_disabled;

// Scorch line of a megastorm lightning scythe (docs/megastorm-design.md 5.5.4, WO12): an ember-orange strip that cools to a dark scorch and fades out
// over 30 s. Vertex colour: r = fraction along the path, g = cross-section weight (1 on the centre line).
uniform float age_seconds = 0.0;
uniform float tip_fraction : hint_range(0.0, 1.0) = 1.0;
uniform float fade_seconds = 30.0;
uniform vec3 ember_color : source_color = vec3(1.0, 0.42, 0.12);
uniform vec3 char_color : source_color = vec3(0.025, 0.02, 0.018);

varying vec4 vc;

void vertex() {
	vc = COLOR;
}

void fragment() {
	float reached = 1.0 - smoothstep(tip_fraction - 0.004, tip_fraction + 0.004, vc.r);
	float ember = exp(-age_seconds / 2.5) * smoothstep(0.0, 0.25, age_seconds);
	float cross_fade = smoothstep(0.0, 0.55, vc.g);
	float life = 1.0 - smoothstep(0.25 * fade_seconds, fade_seconds, age_seconds);
	ALBEDO = mix(char_color, ember_color * 3.0, clamp(ember, 0.0, 1.0));
	ALPHA = clamp(reached * cross_fade * life * mix(0.62, 0.95, ember), 0.0, 1.0);
}
