#[compute]
#version 450

// RingworldTechDemo extension (camera-independent clouds; docs/cloud-camera-independence-plan.md, macro weather map).
//
// Builds a slice of the world-anchored macro weather map. Each texel stores the per-position terms the raymarch used to compute
// at every cloud sample (RingWeatherSystems.glslinc: ring_ws_macro_point, the single source of the formulas):
//   R = coverage before the user bias (base + continuous cluster term + macro bias + density bias, manual systems folded in)
//   G = storm level   clamp(0.65 * systemInfluence + 0.55 * precipitation)
//   B = precipitation potential
//   A = system influence (natural product + strongest manual); the P6 ambient / storm-tint factor
// All values are in [0, 1]. RGBA16F (11-bit significand, relative error <= 4.9e-4, absolute <= 4.9e-4 at 1.0) is far below the
// 0.01-level steps of the smoothstep thresholds the values feed; RGBA32F would double the footprint and fetch bandwidth.
//
// The map is an array of two layers (front/back). Every slice of one build reads the SAME snapshot: the weather-systems block
// copy bound at 1 and the build parameters at 2 (origin, size, layer and the wind frame offset of that snapshot).

#include "./CloudsInc.comp"
// GenericData: 304 floats (16-slot ring atmosphere profile, P2b); touched so Godot reimports against the new CloudsInc.comp layout.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, binding = 0) uniform writeonly image2DArray macro_map;

// Snapshot of the weather-systems block (same layout as RingWeatherSystemsBlock in the cloud shaders).
layout(binding = 1, std140) uniform RingWeatherSystemsBlock {
	vec4 header0;
	vec4 header1;
	vec4 header2;
	vec4 header3;
	RingWeatherSystem systems[64];
} ringWeatherSystems;

// a = (canonical around, canonical across of the texel-grid corner, texel size m, back layer)
// b = (used width, used height in texels, first row of this slice, ring circumference m or 0)
// c = (wind frame offset around, across of the snapshot = cloud_weather_frame.yz, 0, 0)
layout(binding = 2, std140) uniform MacroBuildParams {
	vec4 a;
	vec4 b;
	vec4 c;
} build;

#define RING_WS_MACRO_BUILD
#include "./RingWeatherSystems.glslinc"

void main() {
	ivec2 texel = ivec2(gl_GlobalInvocationID.xy);
	texel.y += int(build.b.z + 0.5);
	ivec2 size = ivec2(build.b.xy + 0.5);
	if (any(greaterThanEqual(texel, size))) return;

	vec3 canonical = vec3(
		build.a.x + (float(texel.x) + 0.5) * build.a.z,
		0.0,
		build.a.y + (float(texel.y) + 0.5) * build.a.z);
	int count = clamp(int(ringWeatherSystems.header0.x + 0.5), 0, RING_WS_MAX);
	uint low = count >= 32 ? 0xFFFFFFFFu : ((1u << uint(count)) - 1u);
	uint high = count >= 64 ? 0xFFFFFFFFu : (count > 32 ? ((1u << uint(count - 32)) - 1u) : 0u);
	vec4 value = ring_ws_macro_point(canonical, uvec2(low, high), build.c.xy, build.b.w);
	imageStore(macro_map, ivec3(texel, int(build.a.w + 0.5)), value);
}
