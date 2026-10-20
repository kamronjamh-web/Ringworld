#ifndef RING_HEIGHT_SHADOW
#define RING_HEIGHT_SHADOW
// Receiver side of the terrain height-shadow clipmap (docs/shadow-revamp-plan.md S4/S5; producer: scripts/Shadows/RingTerrainHeightShadow.cs).
// Globals only, no material uniforms. Everything here is dormant until the producer publishes (state.x >= 0) and its receivers are enabled
// (state.w == 1, the sun): ring_height_shadow_active() is false otherwise and every caller keeps its previous code path bit for bit.
//
// Texel contents (RG16F, slot space, 1024^2 toroidal, layer = state.x front / state.y previous): R = shadow-line height above the texel's own terrain
// surface T0, G = blocker distance (0 none, negative unresolved). A receiver h_rel metres above T0 is lit when h_rel + bias >= R.
// Authority rule: the real-time sun map owns every blocker it contains. A texel whose blocker lies inside the map (camera distance + G < shape.w)
// contributes no weight, so the macro term (and the map itself) stay in charge there; shape.w is 0 while the sun map is suspended.
#include "res://materials/celestial_lighting.gdshaderinc" // ringworld_hub_light_weight

global uniform sampler2DArray ringworld_height_shadow_l0;
global uniform sampler2DArray ringworld_height_shadow_l1;
global uniform mat4 ringworld_height_shadow_frame;
global uniform vec4 ringworld_height_shadow_l0_params;
global uniform vec4 ringworld_height_shadow_l1_params;
global uniform vec4 ringworld_height_shadow_l0_bounds;
global uniform vec4 ringworld_height_shadow_l0_prev_bounds;
global uniform vec4 ringworld_height_shadow_l1_bounds;
global uniform vec4 ringworld_height_shadow_l1_prev_bounds;
global uniform vec4 ringworld_height_shadow_state; // front layer, previous layer (-1 none), crossfade t, light (1 sun)
global uniform vec4 ringworld_height_shadow_shape; // tan(penumbra), receiver bias m, floor radius m, authority distance A

struct RingHeightShadow {
	float visibility; // 1 = lit, only meaningful where weight > 0
	float weight; // 0 = the heightfield has no say (macro fallback), 1 = fully authoritative
};

RingHeightShadow ring_height_shadow_none() {
	return RingHeightShadow(1.0, 0.0);
}

bool ring_height_shadow_active() {
	return ringworld_height_shadow_state.x > -0.5
		&& abs(ringworld_height_shadow_state.w - 1.0) < 0.5
		&& (ringworld_height_shadow_l0_params.z > 0.5 || ringworld_height_shadow_l1_params.z > 0.5);
}

// Slope of a surface (tangent of the angle between the normal and ring up) from its WORLD-space normal; up is the frame's third row.
float ring_height_shadow_slope_tan(vec3 world_normal) {
	vec3 up = normalize(vec3(ringworld_height_shadow_frame[0][2], ringworld_height_shadow_frame[1][2], ringworld_height_shadow_frame[2][2]));
	float cos_slope = clamp(dot(normalize(world_normal), up), 0.05, 1.0);
	return sqrt(max(1.0 - cos_slope * cos_slope, 0.0)) / cos_slope;
}

vec4 ring_height_shadow_fetch(int level, ivec2 texel, int layer) {
	ivec3 coordinate = ivec3(texel.x & 1023, texel.y & 1023, layer);
	return level == 0
		? texelFetch(ringworld_height_shadow_l0, coordinate, 0)
		: texelFetch(ringworld_height_shadow_l1, coordinate, 0);
}

// Distance of p (anchor-local around/across metres) inside a bounds rect (around min/max, across min/max); negative outside.
float ring_height_shadow_edge(vec4 bounds, vec2 p) {
	return min(min(p.x - bounds.x, bounds.y - p.x), min(p.y - bounds.z, bounds.w - p.y));
}

// One level, one layer: bilinear over the four texels, unresolved texels and texels whose blocker the map owns carry no weight.
// Returns (weighted visibility sum / weight sum, weight sum in 0..1).
vec2 ring_height_shadow_level(int level, int layer, vec2 p, float texel_size, float h_rel, float slope_tan, float cam_dist) {
	vec2 x = p / texel_size;
	vec2 base = floor(x);
	vec2 f = x - base;
	ivec2 i0 = ivec2(base);
	float bias = ringworld_height_shadow_shape.y + 0.5 * texel_size * min(slope_tan, 4.0);
	float a = ringworld_height_shadow_shape.w;
	float visibility_sum = 0.0;
	float weight_sum = 0.0;
	for (int k = 0; k < 4; k++) {
		ivec2 o = ivec2(k & 1, k >> 1);
		float w = (o.x == 1 ? f.x : 1.0 - f.x) * (o.y == 1 ? f.y : 1.0 - f.y);
		vec4 texel = ring_height_shadow_fetch(level, i0 + o, layer);
		if (texel.g < 0.0 || cam_dist + texel.g < a) {
			continue;
		}
		float penumbra = max(texel.g * ringworld_height_shadow_shape.x, 0.25);
		visibility_sum += w * clamp(0.5 + (h_rel + bias - texel.r) / penumbra, 0.0, 1.0);
		weight_sum += w;
	}
	return weight_sum > 0.0001 ? vec2(visibility_sum / weight_sum, weight_sum) : vec2(1.0, 0.0);
}

// Both levels of one published layer. L0 hands over to L1 over the last 256 m of its bounds, L1 hands over to the macro term over the last 1000 m.
vec2 ring_height_shadow_layer(int layer, vec2 p, vec4 b0, vec4 b1, float h_rel, float slope_tan, float cam_dist) {
	float visibility = 1.0;
	float weight = 0.0;
	float w0 = ringworld_height_shadow_l0_params.z > 0.5 ? smoothstep(0.0, 256.0, ring_height_shadow_edge(b0, p)) : 0.0;
	if (w0 > 0.0) {
		vec2 s = ring_height_shadow_level(0, layer, p, ringworld_height_shadow_l0_params.x, h_rel, slope_tan, cam_dist);
		weight = w0 * s.y;
		visibility = s.x;
	}
	float w1 = ringworld_height_shadow_l1_params.z > 0.5 ? smoothstep(0.0, 1000.0, ring_height_shadow_edge(b1, p)) : 0.0;
	if (w1 > 0.0 && weight < 0.999) {
		vec2 s = ring_height_shadow_level(1, layer, p, ringworld_height_shadow_l1_params.x, h_rel, slope_tan, cam_dist);
		float w = (1.0 - weight) * w1 * s.y;
		float total = weight + w;
		if (total > 0.0001) {
			visibility = (visibility * weight + s.x * w) / total;
			weight = total;
		}
	}
	return vec2(visibility, weight);
}

// world: receiver position (world space, any floating-origin state the frame matrix was built for). h_rel: height above the terrain surface (0 for
// terrain-type receivers). slope_tan: ring_height_shadow_slope_tan of the receiver. cam_dist: distance to the camera (authority rule).
RingHeightShadow ring_height_shadow_sample(vec3 world, float h_rel, float slope_tan, float cam_dist) {
	RingHeightShadow result = ring_height_shadow_none();
	if (!ring_height_shadow_active()) {
		return result;
	}
	vec3 f = (ringworld_height_shadow_frame * vec4(world, 1.0)).xyz;
	float radius = max(ringworld_height_shadow_shape.z, 1.0);
	float q = f.x / max(radius - f.z, 1.0);
	// Small-angle series inside 10 km of the anchor (|q| <= 0.002), exact beyond.
	float angle = abs(q) <= 0.002 ? q - q * q * q * 0.333333333 : atan(f.x, radius - f.z);
	vec2 p = vec2(radius * angle, f.y);
	int front = int(round(ringworld_height_shadow_state.x));
	vec2 now = ring_height_shadow_layer(front, p, ringworld_height_shadow_l0_bounds, ringworld_height_shadow_l1_bounds, h_rel, slope_tan, cam_dist);
	float visibility = now.x;
	float weight = now.y;
	float t = ringworld_height_shadow_state.z;
	if (ringworld_height_shadow_state.y > -0.5 && t < 1.0) {
		vec2 before = ring_height_shadow_layer(int(round(ringworld_height_shadow_state.y)), p,
			ringworld_height_shadow_l0_prev_bounds, ringworld_height_shadow_l1_prev_bounds, h_rel, slope_tan, cam_dist);
		// The previous layer only exists where the old and new windows overlap; elsewhere the new layer stands alone.
		float share = (1.0 - t) * clamp(before.y / max(now.y, 0.0001), 0.0, 1.0);
		visibility = mix(now.x, before.x, share);
		weight = mix(now.y, before.y, share);
	}
	result.visibility = visibility;
	result.weight = clamp(weight, 0.0, 1.0);
	return result;
}

// The terms.height value of a heightfield receiver: full strength where the heightfield is authoritative, faded by the sun's presence at the
// receiver (sun_w primary sun weight, day the effective day factor, hub the eclipse-hub light share), floored like the map's ATTENUATION.
float ring_height_shadow_term(RingHeightShadow hs, float sun_w, float day, float hub, float shadow_floor_value) {
	return max(mix(1.0, hs.visibility, hs.weight * sun_w * day * (1.0 - clamp(hub, 0.0, 1.0))), shadow_floor_value);
}

// Per-vertex vegetation form: pre-day term (1 = lit) sampled at a tree vertex; h_rel is the vertex height above its root along ring up.
float ring_height_shadow_vertex_term(vec3 world, vec3 root_world, vec3 surface_up, vec3 camera_world) {
	if (!ring_height_shadow_active()) {
		return 1.0;
	}
	RingHeightShadow hs = ring_height_shadow_sample(world, max(dot(world - root_world, surface_up), 0.0), 0.0, distance(world, camera_world));
	return mix(1.0, hs.visibility, hs.weight);
}

// Applies a vertex_term to a vegetation light's geometry term (product path): min(map, heightfield) for the primary sun only.
float ring_height_shadow_vegetation_geometry(float geometry, float vertex_term, bool directional, float sun_w, float day) {
	if (!directional || vertex_term >= 1.0 || sun_w <= 0.0) {
		return geometry;
	}
	return min(geometry, mix(1.0, vertex_term, sun_w * day * (1.0 - clamp(ringworld_hub_light_weight, 0.0, 1.0))));
}
#endif
