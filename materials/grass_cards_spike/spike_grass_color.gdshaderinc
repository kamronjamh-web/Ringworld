// Shared near-colour pipeline for the card shader and the hero-blade shader (P0c). Both materials get the same
// uniform names/values from the spike script, so blades and cards colour identically by construction.
// Input is the already grass-tinted ground colour sampled per fragment at the blade/card ground position.
uniform float green_pull = 0.15;       // 0 = raw ground colour, 1 = ground luminance in a grass hue
uniform vec3 grass_hue = vec3(0.62, 1.0, 0.36);
uniform float ao_root = 0.5;           // brightness at the blade root
uniform float tip_light = 0.16;        // extra brightness at the tip
uniform vec3 tip_tint = vec3(1.04, 1.0, 0.74);
uniform float blade_value_jitter = 0.22;
uniform float blade_hue_jitter = 0.10;
uniform float dry_fraction = 0.07;     // fraction of blades that read dry / yellow
uniform vec3 dry_tone = vec3(0.70, 0.58, 0.24);
uniform float tuft_value_jitter = 0.14;

// ground: tinted terrain albedo, r: per-blade random (0..1), g: height gradient (0 root .. 1 tip), tuft: per-tuft random.
vec3 spike_near_blade_color(vec3 ground, float r, float g, float tuft) {
	float lum = dot(ground, vec3(0.299, 0.587, 0.114));
	vec3 hue = grass_hue / dot(grass_hue, vec3(0.299, 0.587, 0.114));
	vec3 c = mix(ground, lum * hue, green_pull);
	float dry = smoothstep(1.0 - dry_fraction - 0.04, 1.0 - dry_fraction + 0.02, fract(r * 7.31));
	c = mix(c, dry_tone * (0.55 + 1.2 * lum), dry * 0.75);
	c *= mix(vec3(1.0 + blade_hue_jitter, 1.0, 1.0 - blade_hue_jitter), vec3(1.0 - blade_hue_jitter, 1.0, 1.0 + blade_hue_jitter), fract(r * 3.71));
	c *= 1.0 + (r - 0.5) * 2.0 * blade_value_jitter;
	c *= 1.0 + (tuft - 0.5) * 2.0 * tuft_value_jitter;
	float ao = mix(ao_root, 1.0, smoothstep(0.0, 0.6, g));
	float tl = 1.0 + tip_light * smoothstep(0.5, 1.0, g);
	vec3 tipt = mix(vec3(1.0), tip_tint, smoothstep(0.6, 1.0, g));
	return c * ao * tl * tipt;
}
