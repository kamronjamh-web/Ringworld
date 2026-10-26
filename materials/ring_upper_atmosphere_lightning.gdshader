shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;

// Red sprites, blue jets and gigantic jets above storm cloud tops (storm revamp S4 WO-E), far viewport (scaled space).
// One camera-facing, up-aligned quad per live discharge. INSTANCE_CUSTOM = (kind 1 sprite / 2 jet / 3 gigantic, level 0..1,
// height (world units), width (world units)); MODEL_MATRIX origin is the discharge base. Intensity is multiplied by
// (1 - day_suppression * remote day) using the same day curve as the haze and emitter layers, and the quad never subtends less
// than min_angular_radians so a distant discharge stays a visible (if dim) smudge.
#include "res://materials/celestial_lighting.gdshaderinc"

uniform vec3 ring_center = vec3(0.0);
uniform vec3 sun_direction = vec3(1.0, 0.0, 0.0);
uniform float min_angular_radians = 0.0087266;
uniform float base_emission = 700.0;
uniform float day_suppression = 0.97;
uniform vec3 sprite_color : source_color = vec3(1.0, 0.10, 0.05);
uniform vec3 jet_color : source_color = vec3(0.22, 0.42, 1.0);

varying vec2 v_uv;
varying vec4 v_data;
varying float v_gain;

void vertex() {
	vec3 base = MODEL_MATRIX[3].xyz;
	float kind = INSTANCE_CUSTOM.r;
	float level = INSTANCE_CUSTOM.g;
	vec3 to_camera = CAMERA_POSITION_WORLD - base;
	float distance_world = max(length(to_camera), 0.0001);
	vec3 view_dir = to_camera / distance_world;
	vec3 radial = base - ring_center;
	radial.y = 0.0;
	vec3 up = length(radial) > 0.0 ? -normalize(radial) : vec3(-1.0, 0.0, 0.0);
	// Seen end-on (a discharge directly above a storm across the arch rises toward the camera) the quad turns to face the camera.
	vec3 camera_up = normalize((INV_VIEW_MATRIX * vec4(0.0, 1.0, 0.0, 0.0)).xyz);
	up = normalize(mix(up, camera_up, smoothstep(0.7, 0.97, abs(dot(up, view_dir)))));
	vec3 right = cross(up, view_dir);
	right = length(right) > 0.0001 ? normalize(right) : vec3(0.0, 1.0, 0.0);
	float min_extent = min_angular_radians * distance_world;
	float height = max(INSTANCE_CUSTOM.b, 2.0 * min_extent);
	float half_width = max(INSTANCE_CUSTOM.a * 0.5, min_extent);
	// The base sits at the discharge origin; sprites hang around it, jets rise from it.
	vec3 position = base + right * (VERTEX.x * 2.0) * half_width + up * ((VERTEX.y + 0.5) * height - (kind < 1.5 ? 0.5 * height : 0.0));
	POSITION = PROJECTION_MATRIX * (VIEW_MATRIX * vec4(position, 1.0));
	float geometric_day = smoothstep(-0.2, 0.35, dot(up, normalize(sun_direction)));
	float day = ringworld_effective_solar_day(geometric_day);
	v_gain = level * (1.0 - day_suppression * day) * base_emission;
	// Dimming for sub-minimum size keeps total energy roughly constant instead of blooming a tiny event into a big one.
	v_gain *= clamp(INSTANCE_CUSTOM.a * 0.5 / half_width, 0.35, 1.0);
	v_uv = vec2(VERTEX.x * 2.0, VERTEX.y + 0.5);
	v_data = vec4(kind, level, 0.0, 0.0);
}

// Soft-edged 1D bump used to keep every shape fully faded out before the quad boundary (no visible rectangle).
float bump(float v, float centre, float half_width) {
	return exp(-pow((v - centre) / half_width, 2.0));
}

void fragment() {
	if (v_gain <= 0.0001) discard;
	float kind = v_data.x;
	float x = v_uv.x;   // -1..1 across the quad
	float y = v_uv.y;   // 0 base .. 1 top
	float edge = (1.0 - smoothstep(0.55, 1.0, abs(x))) * smoothstep(0.0, 0.08, y) * (1.0 - smoothstep(0.80, 0.98, y));
	vec3 color;
	float shape;
	if (kind < 1.5) {
		// Red sprite: a diffuse crown above, a bright head, and a column of vertical tendrils (carrot streamers) hanging below.
		float crown = bump(y, 0.78, 0.115) * bump(x, 0.0, 0.58) * 0.4;
		float head = bump(y, 0.60, 0.13) * bump(x, 0.0, 0.50);
		float strand = 0.0;
		for (int i = 0; i < 7; i++) {
			float fi = float(i);
			float offset = (fi - 3.0) * 0.17 + 0.05 * sin(fi * 2.7 + y * 6.0);
			float reach = 0.42 + 0.14 * sin(fi * 5.1 + 1.0);
			strand += bump(x, offset, 0.032 + 0.02 * y) * smoothstep(0.0, reach, y) * (1.0 - smoothstep(reach, 0.62, y)) * (0.6 + 0.4 * sin(fi * 3.3));
		}
		float column = bump(x, 0.0, 0.36) * smoothstep(0.0, 0.5, y) * (1.0 - smoothstep(0.5, 0.64, y)) * 0.28;
		shape = (head + crown + strand * 0.8 + column) * edge;
		// Red everywhere, warming toward orange-pink only where the head is brightest.
		color = mix(sprite_color, vec3(1.0, 0.38, 0.26), clamp(head * head, 0.0, 1.0) * 0.55);
	} else if (kind < 2.5) {
		// Blue jet: a narrow cone, brightest and thinnest at the cloud top, widening upward with faint streamers.
		float cone_width = mix(0.10, 0.62, pow(y, 0.85));
		float streamer = 0.78 + 0.22 * sin(x * 30.0 / max(cone_width, 0.1) + y * 9.0);
		float cone = bump(x, 0.0, cone_width) * mix(1.0, 0.35, y) * streamer * (1.0 - smoothstep(0.72, 1.0, y));
		float core = bump(x, 0.0, cone_width * 0.35) * (1.0 - y) * 0.9;
		shape = (cone + core) * 1.7 * (1.0 - smoothstep(0.7, 1.0, abs(x))) * smoothstep(0.0, 0.04, y);
		color = jet_color + vec3(0.30, 0.25, 0.10) * core;
	} else {
		// Gigantic jet: blue-white trunk that reddens toward the top and spreads into a sprite-like head.
		float cone_width = mix(0.08, 0.5, y);
		float trunk = bump(x, 0.0, cone_width) * smoothstep(1.0, 0.8, y);
		float head = bump(y, 0.92, 0.09) * bump(x, 0.0, 0.7);
		shape = (trunk * 0.8 + head) * edge;
		color = mix(jet_color + vec3(0.3), sprite_color, smoothstep(0.35, 0.95, y));
	}
	ALBEDO = color * shape * v_gain;
	ALPHA = 1.0;
}
