// T4 ground surface (design: docs/work-orders/terrain-vegetation/T4.md, "T4 design"): rock on steep ground, strata on rock, scree on the slope foot.
// Terrain-only: the Terrain3D surface and the proxy sectors include it (the far tier and T5 follow). The shared albedo include and the grass cards do not.
// Everything is gated by the world feature ground.surface (bit 2); a world without it never reaches these functions, so generation 1 renders as before.
// C# mirror: scripts/Terrain/Surface/RingGroundSurfaceModel.cs (same names in PascalCase). Keep the two in step; ground_surface_model_test and terrain_rock_material_gpu_test check them.
#ifndef RING_GROUND_SURFACE_INC
#define RING_GROUND_SURFACE_INC
#include "res://materials/ring_world_features.gdshaderinc"

// x strata step m (the recipe's breaks_step_m), y strata seed. Published by RingGroundSurfaceGlobals.PublishWorld.
global uniform vec4 ringworld_ground_world;
// x rock start degrees, y strata contrast, z colour variation (T4-4b), w unused.
global uniform vec4 ringworld_ground_look;

const int GROUND_SURFACE_BIT = 2;
const float GROUND_ROCK_RAMP_DEG = 8.0;
const float GROUND_CONVEX_SHIFT_DEG = 6.0;
const float GROUND_FOREST_SHIFT_DEG = 4.0;
const float GROUND_SLOPE_REF_BASELINE_M = 4.0;
const float GROUND_SLOPE_BIAS_PER_OCTAVE_DEG = 1.5;
const float GROUND_SLOPE_BIAS_MAX_DEG = 12.0;
const vec2 GROUND_SCREE_IN = vec2(26.0, 30.0);
const vec2 GROUND_SCREE_OUT = vec2(36.0, 40.0);
const vec2 GROUND_SCREE_CONVEX_FADE = vec2(0.1, 0.5);
const float GROUND_SCREE_VEGETATED = 0.4;
const float GROUND_STRATA_SUB_DIV = 8.0;
const float GROUND_STRATA_SUB_RATIO = 0.5;
const vec2 GROUND_STRATA_FADE_W = vec2(0.35, 0.9);
const vec3 GROUND_ROCK_TINT = vec3(0.92, 0.93, 0.96);
const vec3 GROUND_SCREE_TINT = vec3(1.04, 1.02, 0.98);
// Mean linear colour of materials/biomes/alpine_rock_albedo.png (T5 and the far rock tone read it).
const vec3 GROUND_ROCK_MEAN_LINEAR = vec3(0.2189, 0.2220, 0.2160);

bool ring_ground_on() {
	return ring_world_has(GROUND_SURFACE_BIT);
}

// Angle between a shading normal's up component (its dot with the ring up) and the zenith.
float ring_ground_slope_deg(float up_dot) {
	return degrees(acos(clamp(up_dot, -1.0, 1.0)));
}

// Coarser slope baselines flatten slopes (T0 8.1, R3); the bias keeps the rock share of the coarse proxy levels close to the 4 m reference.
float ring_ground_slope_bias(float baseline_m) {
	return min(GROUND_SLOPE_BIAS_PER_OCTAVE_DEG * max(log2(baseline_m / GROUND_SLOPE_REF_BASELINE_M), 0.0), GROUND_SLOPE_BIAS_MAX_DEG);
}

float ring_ground_rock_exposure(float slope_deg, float baseline_m, float relief, float forest_weight) {
	float s = slope_deg + ring_ground_slope_bias(baseline_m)
		+ GROUND_CONVEX_SHIFT_DEG * relief - GROUND_FOREST_SHIFT_DEG * forest_weight;
	float t0 = ringworld_ground_look.x;
	return smoothstep(t0, t0 + GROUND_ROCK_RAMP_DEG, s);
}

// vegetated = grass weight + forest weight.
float ring_ground_scree(float slope_deg, float baseline_m, float relief, float vegetated) {
	float s = slope_deg + ring_ground_slope_bias(baseline_m);
	float band = smoothstep(GROUND_SCREE_IN.x, GROUND_SCREE_IN.y, s) * (1.0 - smoothstep(GROUND_SCREE_OUT.x, GROUND_SCREE_OUT.y, s));
	return band * (1.0 - smoothstep(GROUND_SCREE_CONVEX_FADE.x, GROUND_SCREE_CONVEX_FADE.y, relief))
		* mix(1.0, GROUND_SCREE_VEGETATED, clamp(vegetated, 0.0, 1.0));
}

uint ring_ground_pcg(uint v) {
	uint state = v * 747796405u + 2891336453u;
	uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
	return (word >> 22u) ^ word;
}

// Flat tone of one bed, uniform in [-1, 1] (mean 0).
float ring_ground_tone(float bed, uint seed) {
	uint k = ring_ground_pcg(uint(int(bed)) ^ (seed * 2654435761u));
	return float(k >> 8u) / 8388607.5 - 1.0;
}

// Box-filtered bed tone at absolute height h (m). fw is fwidth(h), taken by the caller in uniform control flow; the band period is p metres.
float ring_ground_band(float h, float fw, float p, uint seed) {
	float u = h / p;
	float w = max(fw / p, 1e-5);
	if (w >= 1.0) return 0.0;
	float n0 = floor(u - 0.5 * w);
	float f = clamp((u + 0.5 * w - (n0 + 1.0)) / w, 0.0, 1.0);
	float b = mix(ring_ground_tone(n0, seed), ring_ground_tone(n0 + 1.0, seed), f);
	return b * (1.0 - smoothstep(GROUND_STRATA_FADE_W.x, GROUND_STRATA_FADE_W.y, w));
}

float ring_ground_strata_factor(float h, float fw) {
	float p = ringworld_ground_world.x;
	uint seed = uint(ringworld_ground_world.y);
	float c = ringworld_ground_look.y;
	float bed = ring_ground_band(h, fw, p, seed);
	float sub = ring_ground_band(h, fw, p / GROUND_STRATA_SUB_DIV, seed ^ 40503u);
	return (1.0 + c * bed) * (1.0 + c * GROUND_STRATA_SUB_RATIO * sub);
}
#endif
