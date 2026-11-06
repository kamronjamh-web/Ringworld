#[compute]
#version 450

// RingworldTechDemo extension: storm profile map bake (docs/storm-revamp-s4-sculpt-v2-design.md, WO-V1).
//
// Builds a slice of the world-anchored storm profile map. Each texel is one column of the 2.5D storm shape
// (RingStormShapes.glslinc): a body interval and a canopy interval in absolute canonical altitude, with the footprint encoded by
// collapsing each interval toward its mid-height. The ray-march and shadow computes read one bilinear fetch of it
// (RingStormProfileSample.glslinc) instead of evaluating the storm shape per cloud sample.
//
// The map is a Texture2DArray of 4 layers: buffer * 2 + {0 = heights, 1 = aux}. A build writes the back buffer one slice per render
// callback, every slice reading the SAME snapshot (the weather-systems block copy at binding 1 and the build parameters at 2), and
// the driver swaps buffers after the last slice.
//   heights: (body base, body top, canopy underside, canopy top) in metres; an empty interval is collapsed to a point
//   aux:     (canopy coverage 0..1, mammatus mask 0..1, lifecycle code of the dominant cell, tower coverage 0..1)

#include "./CloudsInc.comp"
// CloudsInc.comp is included only for the RingWeatherSystem struct; touch this file when its layout changes so Godot reimports.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, binding = 0) uniform writeonly image2DArray storm_map;

// Snapshot of the weather-systems block (same layout as RingWeatherSystemsBlock in the cloud shaders).
layout(binding = 1, std140) uniform RingWeatherSystemsBlock {
	vec4 header0;
	vec4 header1;
	vec4 header2;
	vec4 header3;
	RingWeatherSystem systems[64];
} ringWeatherSystems;

// Containment-field cloud ceiling profile for the bake (RingContainmentCeiling.glslinc): the pure profile function only, no genericData here.
#define RING_FIELD_CEILING_PROFILE_ONLY
#include "./RingContainmentCeiling.glslinc"

// a = (canonical around, canonical across of the texel-grid corner, texel size m, back buffer)
// b = (used width, used height in texels, first row of this slice, ring circumference m or 0)
// c = (wind frame offset around, across of the snapshot = cloud_weather_frame.yz, field crown m, field apex m; apex 0 = no field)
// d = (pillar radius m, anvil radius multiplier, anvil start band fraction, anvil shear 0..1)
// e = (mammatus strength 0..1, field RootAcross0, field RootAcrossW, 0)
layout(binding = 2, std140) uniform StormBuildParams {
	vec4 a;
	vec4 b;
	vec4 c;
	vec4 d;
	vec4 e;
} build;

#define RING_WS_MACRO_BUILD
#include "./RingWeatherSystems.glslinc"
#include "./RingStormShapes.glslinc"

void main() {
	ivec2 texel = ivec2(gl_GlobalInvocationID.xy);
	texel.y += int(build.b.z + 0.5);
	ivec2 size = ivec2(build.b.xy + 0.5);
	if (any(greaterThanEqual(texel, size))) return;

	vec3 canonical = vec3(
		build.a.x + (float(texel.x) + 0.5) * build.a.z,
		0.0,
		build.a.y + (float(texel.y) + 0.5) * build.a.z);
	float circumference = build.b.w;
	float influence = clamp(ringWeatherSystems.header1.y, 0.0, 1.0);
	// The field's ceiling at this texel's own canonical across (the weather shift moves storms, not the field).
	float fieldCeiling = ring_containment_ceiling_meters_profile(vec4(build.c.z, build.c.w, build.c.z, build.c.w > build.c.z ? 1.0 : 0.0),
		vec4(build.e.y, build.e.z, 0.0, 0.0), canonical.z);
	StormBakeParams bake = StormBakeParams(vec4(build.d.x, build.d.y, build.d.z, build.d.w), vec4(build.e.x, fieldCeiling, 0.0, 0.0));

	vec3 bodyAcc = vec3(0.0);
	vec3 canopyAcc = vec3(0.0);
	vec4 aux = vec4(0.0);
	int count = clamp(int(ringWeatherSystems.header0.x + 0.5), 0, RING_WS_MAX);
	if (influence > 0.001) {
		for (int index = 0; index < count; index++) {
			uint flags = ring_ws_flags(index);
			if ((flags & RING_WS_FLAG_SCULPT) == 0u) continue;
			int physicalProfile = int(round(ringWeatherSystems.systems[index].c.z));
			if (physicalProfile != 2 && physicalProfile != 3) continue;
			vec3 position = canonical;
			if ((flags & RING_WS_FLAG_STATIC) == 0u) position.xz += build.c.xy;
			ring_storm_system(index, position, circumference, bake, influence, bodyAcc, canopyAcc, aux);
		}
	}
	vec2 body = ring_storm_resolve(bodyAcc);
	vec2 canopy = ring_storm_resolve(canopyAcc);
	int layerBase = int(build.a.w + 0.5) * 2;
	imageStore(storm_map, ivec3(texel, layerBase), vec4(body, canopy));
	imageStore(storm_map, ivec3(texel, layerBase + 1), aux);
}
