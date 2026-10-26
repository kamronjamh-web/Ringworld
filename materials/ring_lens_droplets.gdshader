shader_type canvas_item;
render_mode unshaded;
// Rain on the camera lens (docs/storm-revamp-s2-wet-world-design.md 3, lens droplets). A full-screen CanvasLayer 0 ColorRect drawn after the whole 3D frame (compositor
// chain, FSR / TAA) and before the HUD, like the underwater view. Two procedural layers refract a mip-blurred copy of the final image: small static beads and larger runners that
// slide down with clear trails; a soft overall blur grows with the lens wetness. Drops brighten with the lightning frame automatically (they show the frame). The rect is hidden at
// zero wetness (RingLensDroplets.cs), so this costs nothing when unused. All motion follows the weather sim clock, so a paused clock gives identical frames.
uniform sampler2D screen_texture : hint_screen_texture, repeat_disable, filter_linear_mipmap;
uniform float lens_wetness : hint_range(0.0, 1.0) = 0.0;   // L from LensWetnessModel
uniform float lens_amount : hint_range(0.0, 1.0) = 0.7;    // menu strength
uniform float clock_seconds = 0.0;                          // sim clock modulo 512 s
uniform float screen_aspect = 1.7778;

float h21(vec2 p) {
	p = fract(p * vec2(123.34, 456.21));
	p += dot(p, p + 45.32);
	return fract(p.x * p.y);
}

vec3 h23(vec2 p) { return vec3(h21(p), h21(p + 17.1), h21(p + 41.7)); }

// Small static beads: one candidate per cell; returns (offset inside the bead in cell units, mask).
vec4 bead_layer(vec2 uv, float cells_y, float density, float radius_scale, float seed) {
	vec2 g = uv * vec2(cells_y * screen_aspect, cells_y);
	vec2 id = floor(g);
	vec2 f = fract(g) - 0.5;
	vec3 n = h23(id + seed);
	if (n.x > density) return vec4(0.0);
	vec2 centre = (n.yz - 0.5) * 0.45;
	float r = (0.10 + 0.20 * h21(id + seed * 3.1)) * radius_scale;
	vec2 d = f - centre;
	float m = smoothstep(r, r * 0.8, length(d));
	return vec4(d / r * m, m, r);
}

// Runners: a few columns, each with drops that slide down in stop-go motion and leave a clear trail. Returns (offset, drop mask, trail mask).
vec4 runner_layer(vec2 uv, float density, float seed) {
	float columns = 9.0 * screen_aspect;
	float column_id = floor(uv.x * columns);
	float column_f = fract(uv.x * columns) - 0.5;
	vec3 n = h23(vec2(column_id, seed));
	if (n.x > density) return vec4(0.0);
	float rate = (40.0 + floor(n.y * 61.0)) / 512.0;                  // cycles per second; times 512 s is an integer: seamless wrap
	float phase = fract(n.z * 7.31 + clock_seconds * rate);
	// Stop-go: the drop pauses twice per run.
	float y = phase + 0.035 * sin(phase * 25.13) - 0.02;
	float x_off = (h21(vec2(column_id, seed + 5.0)) - 0.5) * 0.5;
	vec2 d = vec2((column_f - x_off) / screen_aspect * 3.2, uv.y - y);
	float rx = 0.0125, ry = 0.0195;
	float drop = smoothstep(1.0, 0.8, length(d / vec2(rx, ry)));
	float behind = uv.y - y;                                            // negative above the drop (it has already travelled there)
	float trail_len = 0.22;
	float trail = step(behind, 0.0) * smoothstep(-trail_len, -0.01, behind) * smoothstep(rx * 0.55, rx * 0.25, abs(d.x));
	return vec4(d / vec2(rx, ry) * drop, drop, trail);
}

void fragment() {
	float amount = clamp(lens_wetness * lens_amount, 0.0, 1.0);
	vec2 uv = SCREEN_UV;
	float blur_lod = amount * amount * 2.4;
	vec3 sharp = textureLod(screen_texture, uv, 0.0).rgb;
	vec3 color = amount > 0.02 ? textureLod(screen_texture, uv, blur_lod).rgb : sharp;
	float drop_mask = 0.0;
	float trail_mask = 0.0;
	// Small beads: two scales, density follows the wetness.
	vec4 bead_a = bead_layer(uv, 14.0, 0.10 + 0.55 * amount, 0.9, 1.0);
	vec4 bead_b = bead_layer(uv + 0.37, 26.0, 0.05 + 0.45 * amount, 0.8, 2.0);
	vec2 ratio = vec2(1.0 / screen_aspect, 1.0);
	vec3 refracted = color;
	float edge = 0.0;
	if (bead_a.z > 0.0) {
		vec2 sample_uv = uv - bead_a.xy * (1.0 / 14.0) * ratio * 1.1;
		refracted = mix(refracted, textureLod(screen_texture, sample_uv, 0.5).rgb * 1.08, bead_a.z);
		edge = max(edge, bead_a.z * smoothstep(0.7, 1.0, length(bead_a.xy)));
		drop_mask = max(drop_mask, bead_a.z);
	}
	if (bead_b.z > 0.0) {
		vec2 sample_uv = uv - bead_b.xy * (1.0 / 26.0) * ratio * 1.1;
		refracted = mix(refracted, textureLod(screen_texture, sample_uv, 0.5).rgb * 1.08, bead_b.z);
		edge = max(edge, bead_b.z * smoothstep(0.7, 1.0, length(bead_b.xy)));
		drop_mask = max(drop_mask, bead_b.z);
	}
	// Runners: more of them as the lens gets wetter (up to one in two columns per slot).
	float runner_density = smoothstep(0.15, 0.9, amount) * 0.55;
	for (int slot = 0; slot < 2; slot++) {
		vec4 runner = runner_layer(uv, runner_density, 11.0 + float(slot) * 13.0);
		if (runner.z > 0.0) {
			vec2 sample_uv = uv - runner.xy * vec2(0.0125 / screen_aspect * 3.2, 0.0195) * 1.3;
			refracted = mix(refracted, textureLod(screen_texture, sample_uv, 0.3).rgb * 1.1, runner.z);
			edge = max(edge, runner.z * smoothstep(0.7, 1.0, length(runner.xy)));
			drop_mask = max(drop_mask, runner.z);
		}
		trail_mask = max(trail_mask, runner.w * 0.85);
	}
	// Clear trails and drops show the sharp image; the rest of the lens carries the soft blur. Drop rims darken a little and catch a highlight.
	vec3 result = mix(color, sharp, trail_mask);
	result = mix(result, refracted, drop_mask);
	result *= 1.0 - edge * 0.18;
	result += edge * 0.04;
	COLOR = vec4(result, 1.0);
}
