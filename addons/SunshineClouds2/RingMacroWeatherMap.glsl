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
// The map is an array of four layers: 0 / 1 are the values above (front / back); 2 / 3 are the S6 cloud layers (front / back) built from the
// same snapshot by RingCloudLayers.glslinc: R low, G mid, B high cloud coverage, A weatherness (the base-lowering share, 0..1).
// The layer values are written for the main-compute-unread array layers 2 / 3 only; nothing reads them until the march is layer-aware (WO3b).
//
// Every slice of one build reads the SAME snapshot: the weather-systems block
// copy bound at 1 and the build parameters at 2 (origin, size, layer and the wind frame offset of that snapshot).

#include "./CloudsInc.comp"
// GenericData: 312 floats (16-slot ring atmosphere profile P2b + enclosed fog P6 + engine jitter controls); touched so Godot reimports against the new CloudsInc.comp layout.

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

// The cloud layer table (RingCloudLayers.glslinc): 592 bytes written by RingSunshineCloudBridge.PublishCloudLayers.
#define RING_CLOUD_LAYERS_BINDING 3

#define RING_WS_MACRO_BUILD
#include "./RingWeatherSystems.glslinc"
#include "./RingCloudLayers.glslinc"

// S0 storm-owned D (docs/storm-revamp-s0-storm-light-design.md "Storm-owned D"; materials/ring_storm_light.gdshaderinc ring_storm_owned_level). Layers 4 / 5:
//   x = storm ownership: the S6 section 3.8 weight over sculpt systems (StormTower / Cumulonimbus): 1 within 1.3 ellipse radii, 0 by RING_STORM_SUPPORT
//       (same geometry as ring_ws_storm_owned_weight / ring_ws_storm_owned_at, which live in the per-ray section this build does not compile)
//   y = front rain level: the strongest own precipitation of a non-sculpt front (RING_WS_FLAG_FRONT: MajorFront / ExceptionalFront), capped by the point's
//       precipitation potential (map B, the rain that actually falls there), through the 0.25 .. 0.65 ramp. Same per-system falloff and coverage gate as
//       ring_ws_macro_point's precipitation. Dry fronts and other kinds give 0.
// CPU twin: RingSunshineCloudBridge.EvaluateStormLightTerms (StormLight.cs).
#define RING_WS_FLAG_FRONT 32u
#define RING_STORM_OWNED_INNER_SQUARED 1.69
#define RING_STORM_FRONT_RAIN_START 0.25
#define RING_STORM_FRONT_RAIN_WIDTH 0.40
vec4 ring_storm_light_point(vec3 canonical, uvec2 mask, vec2 shift, float circumference, float cov, float precip) {
	float owned = 0.0;
	float frontRain = 0.0;
	float coverageRamp = ring_ws_smooth_unit((cov - 0.35) / 0.55);
	for (int word = 0; word < 2; word++) {
		uint bits = word == 0 ? mask.x : mask.y;
		while (bits != 0u) {
			int index = word * 32 + findLSB(bits);
			bits &= bits - 1u;
			uint flags = ring_ws_flags(index);
			bool sculpt = (flags & RING_WS_FLAG_SCULPT) != 0u;
			if (!sculpt && (flags & RING_WS_FLAG_FRONT) == 0u) continue;
			bool isStatic = (flags & RING_WS_FLAG_STATIC) != 0u;
			vec4 a = ringWeatherSystems.systems[index].a;
			vec4 b = ringWeatherSystems.systems[index].b;
			vec2 offset = isStatic ? vec2(0.0) : shift;
			vec2 delta = vec2(ring_ws_wrap(canonical.x + offset.x - a.x, circumference), canonical.z + offset.y - a.y);
			vec2 q = vec2((b.x * delta.x + b.y * delta.y) / max(a.z, 1.0), (-b.y * delta.x + b.x * delta.y) / max(a.w, 1.0));
			float n2 = dot(q, q);
			if (sculpt) {
				owned = max(owned, 1.0 - smoothstep(RING_STORM_OWNED_INNER_SQUARED, RING_STORM_SUPPORT * RING_STORM_SUPPORT, n2));
				continue;
			}
			float fade = ringWeatherSystems.systems[index].d.z;
			float influence = isStatic
				? (1.0 - ring_ws_smooth_unit((sqrt(n2) - 0.55) / 0.75)) * b.z * fade
				: (n2 >= 1.0 ? 0.0 : (1.0 - ring_ws_smooth_unit((sqrt(n2) - 0.32) / 0.68)) * b.z * fade * coverageRamp);
			frontRain = max(frontRain, b.w * influence);
		}
	}
	return vec4(owned, ring_ws_smooth_unit((min(frontRain, precip) - RING_STORM_FRONT_RAIN_START) / RING_STORM_FRONT_RAIN_WIDTH), 0.0, 0.0);
}

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
	int layer = int(build.a.w + 0.5);
	imageStore(macro_map, ivec3(texel, layer), value);
	imageStore(macro_map, ivec3(texel, layer + 2), ring_cloud_layers_point(canonical, uvec2(low, high), build.c.xy, build.b.w, value.x));
	imageStore(macro_map, ivec3(texel, layer + 4), ring_storm_light_point(canonical, uvec2(low, high), build.c.xy, build.b.w, value.x, value.z));
}
