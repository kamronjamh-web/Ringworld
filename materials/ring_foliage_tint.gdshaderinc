// Foliage colour variation (T6 design A9, docs/work-orders/terrain-vegetation/T6.md): a per-instance and per-stand tint of tree foliage, shared by imported_tree and imported_tree_impostor so the LOD2 to LOD3
// hand-off cannot pop. Dormant unless the world has bit 6 (vegetation.forest_structure) and RingBiomeDecorationLayer publishes ringworld_foliage_tint (w = 1); callers wrap every call in `if (ring_foliage_tint_on())`.
// Globals: ringworld_foliage_tint = (strength, anchor around mod 65 536 m, anchor across mod 65 536 m, 1 = published); ringworld_foliage_frame = the world -> anchor tangent frame (HeightShadowFrameMath.BuildFrame, rebuilt
// every frame like ringworld_canopy_frame) with the floor radius in the free w of its translation column, so (frame * vec4(world, 1)).w = radius. The anchor is the camera's canonical position snapped to 4 096 m.
// Stand value: value noise on 512 m cells over the canonical (around, across), cell indices wrapped every 128 cells (65 536 m), an integer PCG hash per cell. The pattern repeats every 65.5 km.
#ifndef RING_FOLIAGE_TINT_INCLUDED
#define RING_FOLIAGE_TINT_INCLUDED
#include "res://materials/ring_world_features.gdshaderinc"

global uniform mat4 ringworld_foliage_frame;
global uniform vec4 ringworld_foliage_tint;

bool ring_foliage_tint_on() {
	return ring_world_has(6) && ringworld_foliage_tint.w > 0.5;
}

uint ring_foliage_pcg(uint value) {
	uint state = value * 747796405u + 2891336453u;
	uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
	return (word >> 22u) ^ word;
}

// Hash of a wrapped cell in [-1, 1].
float ring_foliage_cell(ivec2 cell) {
	uint x = uint(cell.x & 127);
	uint y = uint(cell.y & 127);
	uint h = ring_foliage_pcg(x ^ ring_foliage_pcg(y + 0x9E3779B9u));
	return float(h >> 8u) * (2.0 / 16777215.0) - 1.0;
}

// Stand value in [-1, 1] at a canonical (around, across) position in metres.
float ring_foliage_stand_value(vec2 canonical) {
	vec2 p = canonical * (1.0 / 512.0);
	vec2 cell = floor(p);
	vec2 f = p - cell;
	f = f * f * (3.0 - 2.0 * f);
	ivec2 i = ivec2(cell);
	return mix(mix(ring_foliage_cell(i), ring_foliage_cell(i + ivec2(1, 0)), f.x), mix(ring_foliage_cell(i + ivec2(0, 1)), ring_foliage_cell(i + ivec2(1, 1)), f.x), f.y);
}

// Canonical (around, across) metres of a world position: the anchor plus the position's offset decoded as in ring_canopy_sample (small-angle series inside 10 km of the anchor, exact beyond).
vec2 ring_foliage_canonical(vec3 world) {
	vec4 f = ringworld_foliage_frame * vec4(world, 1.0);
	float radius = max(f.w, 1.0);
	float q = f.x / max(radius - f.z, 1.0);
	float angle = abs(q) <= 0.002 ? q - q * q * q * 0.333333333 : atan(f.x, radius - f.z);
	return vec2(ringworld_foliage_tint.y + radius * angle, ringworld_foliage_tint.z + f.y);
}

// The tint terms of one root (vertex stage): x = value factor, y = hue term. root_world is MODEL_MATRIX[3].xyz, rank is INSTANCE_CUSTOM.z (identical for the mesh and impostor rows of a root).
vec2 ring_foliage_tint_terms(vec3 root_world, float rank) {
	float s = ringworld_foliage_tint.x;
	float v_i = 2.0 * fract(rank * 4271.0) - 1.0;
	float h_i = 2.0 * fract(rank * 7919.0 + 0.37) - 1.0;
	float v_s = ring_foliage_stand_value(ring_foliage_canonical(root_world));
	return vec2(1.0 + s * (0.08 * v_i + 0.06 * v_s), s * (h_i + v_s));
}

// The albedo multiplier of the terms (at most +-14 % in value and a small yellow-green shift).
vec3 ring_foliage_tint(vec2 terms) {
	return terms.x * vec3(1.0 + 0.03 * terms.y, 1.0, 1.0 - 0.06 * terms.y);
}
#endif
