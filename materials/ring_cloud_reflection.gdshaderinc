// Reader for the cloud image the Sunshine compositor publishes for reflections (docs/work-orders/reflections/R3a-design.md, sections 3 and 6.1).
// The globals are bound by RingSunshineCloudBridge (R3a-1). Only main-view materials may include this file: the far and regional far viewports,
// the minimap and ring_ocean_global.gdshader have another world and projection, and `valid` cannot tell.
// Pure: no built-ins, so it compiles in canvas_item (parity test) and spatial shaders.
#ifndef RING_CLOUD_REFLECTION_INC
#define RING_CLOUD_REFLECTION_INC
global uniform sampler2D ringworld_cloud_reflection_color : filter_nearest, repeat_disable;
global uniform sampler2D ringworld_cloud_reflection_data : filter_nearest, repeat_disable;
global uniform mat4 ringworld_cloud_reflection_dir_to_clip;
global uniform vec4 ringworld_cloud_reflection_params;   // x valid weight, y 1/W, z 1/H, w serial
global uniform vec4 ringworld_cloud_reflection_params2;  // x enclosed density, y enclosed weight, z min step m, w divisor

const float RING_CLOUD_FAR = 1.0e9;                  // metres; "infinitely far"
const float RING_CLOUD_FAR_LIMIT = 65000.0;          // .b at or above this, or non-finite, is far
const float RING_CLOUD_MIN_TAP_COVERAGE = 0.1;       // est.
const float RING_CLOUD_MIN_DISTANCE_WEIGHT = 0.05;   // est.
const float RING_CLOUD_EDGE = 0.02;                  // est.; uv fraction faded at the image border

struct RingCloudSample {
	vec3 premul;            // premultiplied cloud colour (already hazed), scene-linear
	float coverage;         // [0, 1], enclosed / altitude fade included
	float distance;         // metres from the march camera; RING_CLOUD_FAR when far or unknown
	float distance_weight;  // coverage that carried a distance; 0 = no distance
	float opaque_limit;     // the camera's opaque depth along this direction (metres) or RING_CLOUD_FAR
	float valid;            // params.x * in front * inside * edge fade; 0 => use nothing above
};

// Cloud image uv of a world direction; z = 1 when in front of the march camera (design 3.1).
vec3 ring_cloud_reflection_uv(vec3 dir_world) {
	vec4 clip = ringworld_cloud_reflection_dir_to_clip * vec4(dir_world, 0.0);
	if (clip.w <= 1e-6) {
		return vec3(-1.0, -1.0, 0.0);
	}
	return vec3((clip.xy / clip.w) * 0.5 + 0.5, 1.0);
}

// Four-tap coverage-weighted filter at a cloud image uv (design 3.2).
RingCloudSample ring_cloud_reflection_sample_uv(vec2 uv, float in_front) {
	RingCloudSample r;
	r.premul = vec3(0.0);
	r.coverage = 0.0;
	r.distance = RING_CLOUD_FAR;
	r.distance_weight = 0.0;
	r.opaque_limit = RING_CLOUD_FAR;
	r.valid = 0.0;
	float inside = step(0.0, uv.x) * step(0.0, uv.y) * step(uv.x, 1.0) * step(uv.y, 1.0);
	float edge = smoothstep(0.0, RING_CLOUD_EDGE, min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y)));
	float valid = ringworld_cloud_reflection_params.x * in_front * inside * edge;
	if (!(valid > 0.0)) {
		return r;
	}
	ivec2 size = textureSize(ringworld_cloud_reflection_color, 0);
	vec2 t = uv * vec2(size) - 0.5;
	vec2 i0f = floor(t);
	vec2 f = t - i0f;
	ivec2 i0 = ivec2(i0f);
	ivec2 hi = size - ivec2(1);
	vec3 premul = vec3(0.0);
	float coverage = 0.0;
	float carry_w = 0.0;
	float carry_inv = 0.0;
	float best_score = -1.0;
	float best_w = -1.0;
	float best_opaque = -1.0;
	for (int k = 0; k < 4; k++) {
		ivec2 off = ivec2(k % 2, k / 2);
		float w = (off.x == 0 ? 1.0 - f.x : f.x) * (off.y == 0 ? 1.0 - f.y : f.y);
		ivec2 p = clamp(i0 + off, ivec2(0), hi);
		vec4 c = texelFetch(ringworld_cloud_reflection_color, p, 0);
		vec4 d = texelFetch(ringworld_cloud_reflection_data, p, 0);
		bool bad = isnan(c.a) || isinf(c.a) || any(isnan(c.rgb)) || any(isinf(c.rgb));
		float b = d.b;
		bool b_finite = !(isnan(b) || isinf(b));
		float b_fog = b_finite ? max(b, 0.0) : RING_CLOUD_FAR;
		float fog = 1.0 - ringworld_cloud_reflection_params2.y * (1.0 - exp(-ringworld_cloud_reflection_params2.x * b_fog));
		float a = bad ? 0.0 : clamp(c.a, 0.0, 1.0) * fog;
		vec3 rgb = bad ? vec3(0.0) : c.rgb;
		premul += w * a * rgb;
		coverage += w * a;
		if (a >= RING_CLOUD_MIN_TAP_COVERAGE) {
			float inv = (b_finite && b > 0.0 && b < RING_CLOUD_FAR_LIMIT) ? 1.0 / b : 0.0;
			carry_w += w * a;
			carry_inv += w * a * inv;
		}
		// Heaviest tap by weight x coverage, then by weight; the first wins a tie.
		float score = w * a;
		if (score > best_score || (score == best_score && w > best_w)) {
			best_score = score;
			best_w = w;
			best_opaque = d.a;
		}
	}
	r.premul = premul;
	r.coverage = clamp(coverage, 0.0, 1.0);
	if (carry_w >= RING_CLOUD_MIN_DISTANCE_WEIGHT) {
		r.distance_weight = carry_w;
		r.distance = carry_inv > 0.0 ? min(carry_w / carry_inv, RING_CLOUD_FAR) : RING_CLOUD_FAR;
	}
	bool opaque_ok = !(isnan(best_opaque) || isinf(best_opaque)) && best_opaque >= 0.0;
	r.opaque_limit = opaque_ok ? min(exp2(best_opaque) - 1.0, RING_CLOUD_FAR) : RING_CLOUD_FAR;
	r.valid = valid;
	return r;
}

RingCloudSample ring_cloud_reflection_sample(vec3 dir_world) {
	vec3 u = ring_cloud_reflection_uv(dir_world);
	return ring_cloud_reflection_sample_uv(u.xy, u.z);
}

// Colour and coverage only: the same four taps, weights, NaN rules and enclosed fog as ring_cloud_reflection_sample_uv, without the distance and the
// opaque limit (distance = RING_CLOUD_FAR, distance_weight = 0, opaque_limit = RING_CLOUD_FAR). The data image is read only while the enclosed fog is on
// (params2.y > 0). For readers that composite without placing the cloud (the model 1 water march, R3a-3a): half the fetches of the full sample.
RingCloudSample ring_cloud_reflection_sample_color_uv(vec2 uv, float in_front) {
	RingCloudSample r;
	r.premul = vec3(0.0);
	r.coverage = 0.0;
	r.distance = RING_CLOUD_FAR;
	r.distance_weight = 0.0;
	r.opaque_limit = RING_CLOUD_FAR;
	r.valid = 0.0;
	float inside = step(0.0, uv.x) * step(0.0, uv.y) * step(uv.x, 1.0) * step(uv.y, 1.0);
	float edge = smoothstep(0.0, RING_CLOUD_EDGE, min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y)));
	float valid = ringworld_cloud_reflection_params.x * in_front * inside * edge;
	if (!(valid > 0.0)) {
		return r;
	}
	bool enclosed_fog = ringworld_cloud_reflection_params2.y > 0.0;
	ivec2 size = textureSize(ringworld_cloud_reflection_color, 0);
	vec2 t = uv * vec2(size) - 0.5;
	vec2 i0f = floor(t);
	vec2 f = t - i0f;
	ivec2 i0 = ivec2(i0f);
	ivec2 hi = size - ivec2(1);
	vec3 premul = vec3(0.0);
	float coverage = 0.0;
	for (int k = 0; k < 4; k++) {
		ivec2 off = ivec2(k % 2, k / 2);
		float w = (off.x == 0 ? 1.0 - f.x : f.x) * (off.y == 0 ? 1.0 - f.y : f.y);
		ivec2 p = clamp(i0 + off, ivec2(0), hi);
		vec4 c = texelFetch(ringworld_cloud_reflection_color, p, 0);
		bool bad = isnan(c.a) || isinf(c.a) || any(isnan(c.rgb)) || any(isinf(c.rgb));
		float fog = 1.0;
		if (enclosed_fog) {
			float b = texelFetch(ringworld_cloud_reflection_data, p, 0).b;
			float b_fog = !(isnan(b) || isinf(b)) ? max(b, 0.0) : RING_CLOUD_FAR;
			fog = 1.0 - ringworld_cloud_reflection_params2.y * (1.0 - exp(-ringworld_cloud_reflection_params2.x * b_fog));
		}
		float a = bad ? 0.0 : clamp(c.a, 0.0, 1.0) * fog;
		premul += w * a * (bad ? vec3(0.0) : c.rgb);
		coverage += w * a;
	}
	r.premul = premul;
	r.coverage = clamp(coverage, 0.0, 1.0);
	r.valid = valid;
	return r;
}

RingCloudSample ring_cloud_reflection_sample_color(vec3 dir_world) {
	vec3 u = ring_cloud_reflection_uv(dir_world);
	return ring_cloud_reflection_sample_color_uv(u.xy, u.z);
}

// One refinement: places the cloud at its distance from the reflecting point.
// origin = reflecting point (world), camera = CAMERA_POSITION_WORLD (passed in, keeps the function pure).
RingCloudSample ring_cloud_reflection_sample_placed(vec3 origin, vec3 dir_world, vec3 camera) {
	RingCloudSample s1 = ring_cloud_reflection_sample(dir_world);
	if (s1.valid > 0.0 && s1.distance_weight > 0.0 && s1.distance < RING_CLOUD_FAR) {
		vec3 d2 = normalize(origin + dir_world * s1.distance - camera);
		return ring_cloud_reflection_sample(d2);
	}
	return s1;
}

// The post pass's near fade (post:438) for a finite hit at hit_distance metres from the camera.
float ring_cloud_reflection_near_fade(float hit_distance) {
	return smoothstep(ringworld_cloud_reflection_params2.z, 2.0 * ringworld_cloud_reflection_params2.z, hit_distance);
}

// out = src * (1 - coverage * valid) + premul * valid
vec3 ring_cloud_reflection_composite(vec3 src, RingCloudSample s) {
	return src * (1.0 - s.coverage * s.valid) + s.premul * s.valid;
}
#endif
