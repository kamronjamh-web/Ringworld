shader_type canvas_item;
// Start-menu fallback cover: a dark vertical gradient with three hashed-grid star layers, a gentle twinkle and a slow parallax drift.
// Stars are crisp points (mostly 1-2 px); a few brighter ones carry a small glow. `frozen` pins the field (tests and captures).

uniform vec4 top_color : source_color = vec4(0.0078, 0.0157, 0.0353, 1.0);
uniform vec4 bottom_color : source_color = vec4(0.0275, 0.0431, 0.0863, 1.0);
uniform bool frozen = false;
uniform float drift_speed = 6.0; // pixels per second at the nearest layer
uniform float twinkle_speed = 1.3;

float hash21(vec2 p) {
	p = fract(p * vec2(123.34, 456.21));
	p += dot(p, p + 45.32);
	return fract(p.x * p.y);
}

// One layer in pixel space: a jittered star per grid cell. `cell_px` sets the density, `radius_px` the base star radius.
float star_layer(vec2 pixel, float cell_px, float density, float radius_px, float glow_chance, float time) {
	vec2 grid = pixel / cell_px;
	vec2 cell = floor(grid);
	vec2 local = (fract(grid) - 0.5) * cell_px;
	float present = step(1.0 - density, hash21(cell + 7.1));
	vec2 jitter = (vec2(hash21(cell + 1.7), hash21(cell + 9.3)) - 0.5) * (cell_px - 4.0);
	float d = length(local - jitter);
	float radius = radius_px * (0.7 + 0.6 * hash21(cell + 3.9));
	float core = 1.0 - smoothstep(radius - 0.5, radius + 0.5, d);
	float bright = step(1.0 - glow_chance, hash21(cell + 13.3));
	float glow = bright * exp(-d * 0.7) * 0.35;
	float phase = hash21(cell + 5.5) * 6.2831853;
	float twinkle = 0.8 + 0.2 * sin(time * twinkle_speed * (0.6 + hash21(cell + 2.2)) + phase);
	float brightness = 0.3 + 0.7 * hash21(cell + 11.7);
	return present * (core * brightness + glow) * twinkle;
}

void fragment() {
	float time = frozen ? 0.0 : TIME;
	vec2 pixel = FRAGCOORD.xy;
	vec3 color = mix(top_color.rgb, bottom_color.rgb, UV.y);
	float stars = 0.0;
	// Nearer layers are sparser, slightly larger and drift faster.
	stars += star_layer(pixel + vec2(time * drift_speed * 0.3, 0.0), 18.0, 0.22, 0.6, 0.0, time) * 0.55;
	stars += star_layer(pixel + vec2(time * drift_speed * 0.6, 0.0), 34.0, 0.18, 0.8, 0.05, time + 3.0) * 0.8;
	stars += star_layer(pixel + vec2(time * drift_speed, 0.0), 64.0, 0.16, 1.1, 0.22, time + 7.0);
	color += vec3(0.78, 0.86, 1.0) * stars;
	COLOR = vec4(color, 1.0);
}
