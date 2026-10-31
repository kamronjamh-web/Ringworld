shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never, shadows_disabled;

// Megastorm lightning scythe (docs/megastorm-design.md 5.5.4, WO12): the sheet box, the centre-line core and the branch filaments of one scythe.
// Vertex colour: r = fraction along the path from the attachment (0) to the end of the sheet (1), g = cross-section glow (1 on the centre line),
// b = per-branch random in [0, 1] (the sheet itself is 1), a = unused.
uniform vec4 bolt_color : source_color = vec4(0.72, 0.86, 1.0, 1.0);
uniform float intensity : hint_range(0.0, 2.0) = 0.0;       // the stroke pulse (reduced-flashes envelope already applied by the caller)
uniform float tip_fraction : hint_range(0.0, 1.0) = 0.0;    // how far along the path the tip has travelled
uniform float emission_energy : hint_range(0.0, 64.0) = 8.0;
uniform float stroke_seed = 0.0;                            // changes per return stroke: re-picks which branches light
uniform float branch_share : hint_range(0.0, 1.0) = 0.55;   // share of branch filaments lit by one stroke
uniform float is_branch : hint_range(0.0, 1.0) = 0.0;
uniform float sheet_opacity : hint_range(0.0, 1.0) = 0.7;

varying vec4 vc;

float hash11(float p) {
	return fract(sin(p * 127.1 + 311.7) * 43758.5453);
}

void vertex() {
	vc = COLOR;
}

void fragment() {
	float along = vc.r;
	float reached = 1.0 - smoothstep(tip_fraction - 0.004, tip_fraction + 0.004, along);
	// The head glows hardest; the attachment end carries the return-stroke root.
	float head = exp(-max(tip_fraction - along, 0.0) / 0.05);
	float root = exp(-along / 0.04);
	float lit = mix(1.0, step(1.0 - branch_share, hash11(vc.b * 91.7 + stroke_seed * 17.3)), is_branch);
	float glow = vc.g * (0.55 + 1.4 * head + 0.6 * root);
	// The shape (cross-section glow, tip mask, branch pick) goes into the additive alpha once; the stroke pulse scales the radiance.
	float shape = clamp(glow * reached * lit, 0.0, 1.0);
	vec3 color = bolt_color.rgb * clamp(intensity, 0.0, 2.0);
	ALBEDO = color;
	EMISSION = color * emission_energy * shape;
	ALPHA = shape * mix(sheet_opacity, 1.0, is_branch) * clamp(intensity * 2.0, 0.0, 1.0);
}
