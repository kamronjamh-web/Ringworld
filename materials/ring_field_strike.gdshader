shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never, depth_test_disabled, shadows_disabled, fog_disabled;

// Field strikes beyond the main view's handoff (docs/megastorm-design.md 5.5.5, WO13), far viewport (scaled space). One camera-facing quad per strike, standing on the
// channel axis: MODEL_MATRIX origin = the channel base (dome top), column Y = the axis to the contact point under the containment field (world units, not unit length).
// INSTANCE_CUSTOM = (gain 0..1 incl. pulse and handoff, leader progress 0..1, seed 0..1, fan half width (world units)). The channel never subtends less than
// min_angular_radians (the minimum angular size), a faint remote-day factor suppresses it by day like the other upper-atmosphere layers. The canopy flare is the field's own
// far strike layer; this draws the channel only: white-blue lower leader, deep blue upper trunk, a gigantic-jet fan below the field and a violet-white terminal spray.
#include "res://materials/celestial_lighting.gdshaderinc"

uniform vec3 ring_center = vec3(0.0);
uniform vec3 sun_direction = vec3(1.0, 0.0, 0.0);
uniform float min_angular_radians = 0.005236;
uniform float base_emission = 30.0;
uniform float day_suppression = 0.97;

varying vec2 v_world;       // (x lateral, y along the axis) in world units, origin at the base
varying vec4 v_shape;       // x gain, y progress, z seed, w channel length
varying vec3 v_widths;      // x half width of the quad, y minimum half width (angular), z wander amplitude

float hash11(float p) { p = fract(p * 0.1031); p *= p + 33.33; p *= p + p; return fract(p); }

void vertex() {
	vec3 base = MODEL_MATRIX[3].xyz;
	vec3 axis = MODEL_MATRIX[1].xyz;
	float len = max(length(axis), 1e-6);
	vec3 an = axis / len;
	vec3 mid = base + 0.5 * axis;
	vec3 to_camera = CAMERA_POSITION_WORLD - mid;
	float dist = max(length(to_camera), 1e-4);
	vec3 view_dir = to_camera / dist;
	vec3 right = cross(an, view_dir);
	right = length(right) > 1e-4 ? normalize(right) : normalize(cross(an, vec3(0.0, 0.0, 1.0)));
	float min_half = 0.5 * min_angular_radians * dist;
	float fan = INSTANCE_CUSTOM.a;
	float half_w = max(max(fan, min_half) * 1.15, 1e-6);
	vec3 position = base + axis * (VERTEX.y + 0.5) + right * (VERTEX.x * 2.0 * half_w);
	POSITION = PROJECTION_MATRIX * (VIEW_MATRIX * vec4(position, 1.0));
	vec3 radial = base - ring_center;
	radial.y = 0.0;
	vec3 up = length(radial) > 0.0 ? -normalize(radial) : vec3(-1.0, 0.0, 0.0);
	float day = ringworld_effective_solar_day(smoothstep(-0.2, 0.35, dot(up, normalize(sun_direction))));
	float gain = INSTANCE_CUSTOM.r * (1.0 - day_suppression * day) * base_emission;
	v_world = vec2(VERTEX.x * 2.0 * half_w, (VERTEX.y + 0.5) * len);
	v_shape = vec4(gain, INSTANCE_CUSTOM.g, INSTANCE_CUSTOM.b, len);
	v_widths = vec3(half_w, min_half, min(0.07 * len, 0.45 * half_w));
}

// Distance from p to the segment a-b.
float seg_dist(vec2 p, vec2 a, vec2 b) {
	vec2 pa = p - a, ba = b - a;
	float h = clamp(dot(pa, ba) / max(dot(ba, ba), 1e-12), 0.0, 1.0);
	return length(pa - ba * h);
}

float vnoise(float x) {
	float i = floor(x), f = fract(x);
	f = f * f * (3.0 - 2.0 * f);
	return mix(hash11(i * 1.7 + 3.1), hash11((i + 1.0) * 1.7 + 3.1), f);
}

// The jagged trunk: a fractal random walk (four octaves, each half as rough as the last) pinned to the dome top at the base; offset at height y (world units).
float trunk_x(float y, float len, float amp, float seed) {
	float u = y / max(len, 1e-6);
	float s = seed * 37.0, v = 0.0, a = 1.0;
	for (int k = 0; k < 4; k++) {
		v += a * (vnoise(u * (5.0 * pow(2.1, float(k))) + s + float(k) * 11.0) * 2.0 - 1.0);
		a *= 0.55;
	}
	return amp * v * 0.7 * smoothstep(0.0, 0.08, u);
}

// A jagged filament from a in direction dir (unit, in the quad plane), 4 pieces, each vertex jittered sideways; returns the distance to it.
float filament(vec2 p, vec2 a, vec2 dir, float L, float hid) {
	vec2 perp = vec2(-dir.y, dir.x);
	vec2 prev = a;
	float d = 1e9;
	for (int k = 1; k <= 4; k++) {
		float t = float(k) / 4.0;
		vec2 q = a + dir * L * t + perp * (hash11(hid * 13.0 + float(k) * 2.3) - 0.5) * 0.3 * L * t;
		d = min(d, seg_dist(p, prev, q));
		prev = q;
	}
	return d;
}

void fragment() {
	float gain = v_shape.x;
	if (gain <= 0.0001) discard;
	float len = v_shape.w, prog = v_shape.y, stroke = floor(v_shape.z), seed = fract(v_shape.z);
	float half_w = v_widths.x, min_half = v_widths.y, amp = v_widths.z;
	vec2 p = v_world;
	float tip = prog * len;
	if (p.y > tip + 0.002 * len) discard;
	// Thin hot core, soft glow; never thinner than a fraction of the minimum angular size.
	float core_w = max(0.0004 * len, 0.35 * min_half);
	float glow_w = core_w * 3.5;

	float tx = trunk_x(p.y, len, amp, seed);
	float e = 0.004 * len;
	float slope = (trunk_x(p.y + e, len, amp, seed) - tx) / e;
	float d_trunk = abs(p.x - tx) / sqrt(1.0 + slope * slope);
	float height = p.y / len;
	// Irregular fan: 8 forks rooted anywhere in the upper trunk (biased to the top), each with its own heading (acute 20-50 degrees), length and a fork; each re-rolls its flicker per return stroke.
	float fan_core = 0.0, fan_glow = 0.0;
	float fan_w = half_w / 1.15;
	for (int i = 0; i < 8; i++) {
		float fi = float(i);
		float hid = seed * 53.0 + fi * 7.7;
		float y0 = (0.2 + 0.75 * pow(hash11(hid + 1.0), 0.55)) * len;
		if (y0 > tip) continue;
		float side = hash11(hid + 2.0) < 0.5 ? -1.0 : 1.0;
		float ang = radians(20.0 + 30.0 * hash11(hid + 3.0));
		vec2 dir = normalize(vec2(side * sin(ang), cos(ang)));
		float L = min((len - y0) * (0.2 + 0.9 * hash11(hid + 4.0)) / max(cos(ang), 0.5), fan_w / max(sin(ang), 0.2));
		vec2 a = vec2(trunk_x(y0, len, amp, seed), y0);
		float w = fract(sin(fi * 91.7 + stroke * 17.3) * 43758.5453) > 0.4 ? 1.0 : 0.12;
		float d = filament(p, a, dir, L, hid);
		// A finer fork from about half way.
		float ang2 = radians((hash11(hid + 5.0) < 0.5 ? -1.0 : 1.0) * (20.0 + 30.0 * hash11(hid + 6.0)));
		vec2 dir2 = vec2(cos(ang2) * dir.x - sin(ang2) * dir.y, sin(ang2) * dir.x + cos(ang2) * dir.y);
		d = min(d * 0.85, filament(p, a + dir * L * 0.5, dir2, L * 0.45, hid + 31.0) * 1.25);
		fan_core += w * exp(-pow(d / (core_w * 0.55), 2.0)) * 0.7;
		fan_glow += w * exp(-pow(d / (glow_w * 0.8), 2.0)) * 0.03;
	}
	fan_core *= smoothstep(0.15, 0.25, height);
	fan_glow *= smoothstep(0.15, 0.25, height);
	// Terminal spray: jagged filaments along the underside of the field, revealed on attachment.
	float d_spray = 1e9;
	if (prog > 0.999) {
		float sl = 0.04 * len;
		vec2 c = vec2(trunk_x(len, len, amp, seed), len);
		for (int i = 0; i < 5; i++) {
			float fi = float(i);
			float s = (hash11(seed * 29.0 + fi) - 0.5) * 2.0;
			vec2 dir = normalize(vec2(s, -0.15 - 0.25 * hash11(seed * 31.0 + fi)));
			d_spray = min(d_spray, filament(p, c, dir, sl * (0.5 + hash11(seed * 17.0 + fi)), seed * 9.0 + fi));
		}
	}
	float up = smoothstep(0.22, 0.38, height);
	vec3 core_col = mix(vec3(0.92, 0.96, 1.0), vec3(0.55, 0.68, 1.0), up);
	vec3 glow_col = mix(vec3(0.50, 0.65, 1.0), vec3(0.30, 0.25, 1.0), up);

	float trunk_core = exp(-pow(d_trunk / core_w, 2.0));
	float trunk_glow = exp(-pow(d_trunk / glow_w, 2.0)) * 0.06;
	float spray_core = exp(-pow(d_spray / (core_w * 0.9), 2.0));
	float spray_glow = exp(-pow(d_spray / (glow_w * 1.2), 2.0)) * 0.06;
	// The growing head is brighter than the channel behind it.
	float head = prog < 0.999 ? 1.0 + 1.2 * exp(-pow((tip - p.y) / (0.05 * len + core_w), 2.0)) : 1.0;

	vec3 col = core_col * (trunk_core + fan_core) + glow_col * (trunk_glow + fan_glow);
	col += vec3(0.90, 0.88, 1.0) * spray_core + vec3(0.60, 0.35, 1.0) * spray_glow;
	float widen = clamp(0.0004 * len / core_w, 0.35, 1.0);
	ALBEDO = col * head * widen * gain;
	ALPHA = 1.0;
}
