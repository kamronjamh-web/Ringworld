#[compute]
#version 450

// RingworldTechDemo extension: megastorm strip build (docs/megastorm-design.md 3.2, 3.6; WO4).
//
// Builds a slice of the MOVING-FRAME megastorm strip: texel (i, j) is the wall column at reference-line distance
// x = x_min + (i + 0.5) * x_texel and across = (j + 0.5) * across_texel, from the shared texture-free include
// materials/ring_megastorm_profile.gdshaderinc (the C# twin is MegastormProfile.Column). The strip is indexed in the moving frame, so
// the wall's translation comes from the per-frame ring_megastorm_frame uniform (CloudsInc.comp GenericData), never from a rebuild; a
// rebuild (every 2 s, 8 slices, front/back) only follows cell life, the meander drift and the formation level.
//
// Texture2DArray RGBA16F, 4 layers = buffer * 2 + {0 heights, 1 aux}:
//   heights: (body base, body top, canopy underside, canopy top) in absolute canonical metres (datum + column height), 0 when absent,
//            clamped to the ceiling (18 km, or the containment field's ceiling once it lands)
//   aux:     (canopy coverage, mammatus, tower coverage, body coverage), each x strength
// The sampler (RingStormProfileSample.glslinc, ring_megastorm_strip_fetch) collapses each interval by its coverage after the bilinear
// filter, which is how the footprint edges taper.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, binding = 0) uniform writeonly image2DArray strip_map;

// a = (x min m, x texel m, across texel m, back buffer)
// b = (used width, used height in texels, first row of this slice, altitude datum m)
// c = (t weather-clock s, level, strength, seed)
// d = (W ring width m, bow m, meander amplitude m, meander wavelength m)
// e = (meander phase rad, ceiling m above the floor, 0, 0)
layout(binding = 1, std140) uniform StripBuildParams {
	vec4 a;
	vec4 b;
	vec4 c;
	vec4 d;
	vec4 e;
} build;

#include "../../materials/ring_megastorm_profile.gdshaderinc"

void main() {
	ivec2 texel = ivec2(gl_GlobalInvocationID.xy);
	texel.y += int(build.b.z + 0.5);
	ivec2 size = ivec2(build.b.xy + 0.5);
	if (any(greaterThanEqual(texel, size))) return;

	float x = build.a.x + (float(texel.x) + 0.5) * build.a.y;
	float across = (float(texel.y) + 0.5) * build.a.z;
	RingMegastormParams p = RingMegastormParams(build.c, build.d, build.e);
	vec4 heights;
	vec4 aux;
	ring_megastorm_column(x, across, p, heights, aux);
	float datum = build.b.w;
	// Absolute canonical altitude; an absent interval stays (0, 0) (collapsed). The include already clamps the tops to the ceiling above
	// the floor; the datum shift is clamped again at the hard 18 km cap so nothing in the strip is ever above it.
	if (heights.y > heights.x) heights.xy = min(heights.xy + datum, vec2(18000.0));
	if (heights.w > heights.z) heights.zw = min(heights.zw + datum, vec2(18000.0));
	int layerBase = int(build.a.w + 0.5) * 2;
	imageStore(strip_map, ivec3(texel, layerBase), heights);
	imageStore(strip_map, ivec3(texel, layerBase + 1), aux);
}
