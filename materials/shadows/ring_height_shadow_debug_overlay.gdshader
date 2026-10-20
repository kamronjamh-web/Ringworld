shader_type canvas_item;
// Shadow revamp S4c: transient top-down view of the terrain height-shadow globals (docs/shadow-revamp-plan.md S4).
// Each pixel is a world-space ground point (view_origin + axis_u * x + axis_v * y); the shader pushes it through ringworld_height_shadow_frame exactly like a
// future receiver (S5) would, decodes anchor-local around/across metres and samples the front layer of the level whose bounds contain it.
// Modes: 0 level-0 surface visibility, 1 level-1 surface visibility, 2 blocker distance, 3 unresolved texels (red: lit so far, yellow: already shadowed by the in-window lower bound), 4 window bounds (L0 cyan, L1 orange, previous yellow).

global uniform sampler2DArray ringworld_height_shadow_l0;
global uniform sampler2DArray ringworld_height_shadow_l1;
global uniform mat4 ringworld_height_shadow_frame;
global uniform vec4 ringworld_height_shadow_l0_params;
global uniform vec4 ringworld_height_shadow_l1_params;
global uniform vec4 ringworld_height_shadow_l0_bounds;
global uniform vec4 ringworld_height_shadow_l0_prev_bounds;
global uniform vec4 ringworld_height_shadow_l1_bounds;
global uniform vec4 ringworld_height_shadow_l1_prev_bounds;
global uniform vec4 ringworld_height_shadow_state;
global uniform vec4 ringworld_height_shadow_shape;

uniform vec3 view_origin = vec3(0.0);
uniform vec3 axis_u = vec3(1.0, 0.0, 0.0);
uniform vec3 axis_v = vec3(0.0, 0.0, 1.0);
uniform float extent_m = 4000.0;
uniform int mode = 0;

bool inside_bounds(vec4 b, vec2 p) {
	return p.x >= b.x && p.x <= b.y && p.y >= b.z && p.y <= b.w;
}

vec2 level_uv(vec4 params, vec2 p) {
	float n = 1024.0;
	return fract((p / params.x + 0.5) / n);
}

vec4 fetch_l0(vec2 p, int layer) {
	ivec2 texel = ivec2(floor(level_uv(ringworld_height_shadow_l0_params, p) * 1024.0));
	return texelFetch(ringworld_height_shadow_l0, ivec3(min(texel, ivec2(1023)), layer), 0);
}

vec4 fetch_l1(vec2 p, int layer) {
	ivec2 texel = ivec2(floor(level_uv(ringworld_height_shadow_l1_params, p) * 1024.0));
	return texelFetch(ringworld_height_shadow_l1, ivec3(min(texel, ivec2(1023)), layer), 0);
}

vec3 visibility_color(vec4 v, float bias) {
	if (v.g < 0.0 && v.r <= bias) {
		return vec3(0.75, 0.2, 0.75);
	}
	return v.r > bias ? vec3(0.06, 0.08, 0.2) : vec3(0.95, 0.93, 0.85);
}

void fragment() {
	vec2 q = (UV - 0.5) * extent_m;
	vec3 world = view_origin + axis_u * q.x + axis_v * q.y;
	vec3 f = (ringworld_height_shadow_frame * vec4(world, 1.0)).xyz;
	float radius = max(ringworld_height_shadow_shape.z, 1.0);
	vec2 p = vec2(radius * atan(f.x, radius - f.z), f.y);
	float bias = ringworld_height_shadow_shape.y;
	int front = int(round(ringworld_height_shadow_state.x));
	bool valid = front >= 0 && ringworld_height_shadow_state.w > 0.5;
	bool in0 = valid && ringworld_height_shadow_l0_params.z > 0.5 && inside_bounds(ringworld_height_shadow_l0_bounds, p);
	bool in1 = valid && ringworld_height_shadow_l1_params.z > 0.5 && inside_bounds(ringworld_height_shadow_l1_bounds, p);
	vec3 color = vec3(0.18);
	if (mode == 4) {
		if (in1) color = mix(color, vec3(0.9, 0.5, 0.15), 0.45);
		if (in0) color = mix(color, vec3(0.1, 0.8, 0.9), 0.5);
		bool prev0 = ringworld_height_shadow_state.y >= 0.0 && inside_bounds(ringworld_height_shadow_l0_prev_bounds, p);
		bool prev1 = ringworld_height_shadow_state.y >= 0.0 && inside_bounds(ringworld_height_shadow_l1_prev_bounds, p);
		if ((prev0 || prev1) && fract((UV.x + UV.y) * 24.0) < 0.5) color = mix(color, vec3(0.95, 0.9, 0.2), 0.6);
	} else if (valid) {
		bool use0 = mode != 1 && in0;
		bool use1 = (mode == 1 && in1) || (mode != 1 && !in0 && in1);
		if (use0 || use1) {
			vec4 v = use0 ? fetch_l0(p, front) : fetch_l1(p, front);
			if (mode == 2) {
				color = abs(v.g) > 0.0 && v.r > 0.0 ? vec3(clamp(abs(v.g) / (extent_m * 0.5), 0.0, 1.0)) * vec3(1.0, 0.7, 0.3) : (v.g < 0.0 ? vec3(0.5, 0.0, 0.5) : vec3(0.0));
			} else if (mode == 3) {
				color = v.g < 0.0 ? (v.r > 0.5 ? vec3(1.0, 0.8, 0.1) : vec3(0.9, 0.1, 0.1)) : vec3(0.05, 0.3, 0.1);
			} else {
				color = visibility_color(v, bias);
			}
			if (use1 && !use0) color *= 0.85;
		}
	}
	float cross = step(abs(q.x), extent_m * 0.004) + step(abs(q.y), extent_m * 0.004);
	if (cross > 0.0 && abs(q.x) + abs(q.y) < extent_m * 0.03) color = vec3(1.0, 0.2, 0.2);
	COLOR = vec4(color, 1.0);
}
