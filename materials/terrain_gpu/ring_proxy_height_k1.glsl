#[compute]
#version 450
// K1 `sample` (docs/gpu-offload-plan.md 4.2, WO-G1): natural height plus climate and biome weights for a sector lattice (or an explicit
// point list), given an integer anchor and spacing. Runs outside the proxy streaming code; the parity test and the timing run use it.
//   mode 0 (lattice):  invocation i -> sector s = i / (cols*rows), column ix, row iy;
//                      dA = originDA + s*sectorStride + ix*spacing, across = clamp(originAcross + iy*spacing, 0, acrossMax)
//                      (the across clamp is TerrainBuildSampleCache.Get's clamp; acrossMax = ring width - 2, the last presentation lattice row)
//   mode 1 (points):   per point 8 words: anchor, dA, across, flags, cos0.hi, cos0.lo, sin0.hi, sin0.lo (bits), no clamp
// flags bit 0: evaluate climate and biome weights. Output: 16 floats per invocation:
//   0 height  1 continentalness  2 ridge  3 valley  4 erosion  5 temperature (runtime, after the altitude drop)  6 moisture
//   7 flags (bit 0 water: height < 0)  8..11 biome weights desert, forest, tundra, alpine  12 macro  13 landform gate
//   14 gully erosion height  15 landform height
#include "ring_height_model.glslinc"
#include "ring_regional_climate.glslinc"

layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;
layout(set = 0, binding = 1, std430) restrict writeonly buffer Output { float o[]; } outp;
layout(set = 0, binding = 2, std430) restrict readonly buffer Points { uint p[]; } pts;

layout(push_constant, std430) uniform Params
{
	int mode;
	int count;
	int cols;
	int rows;
	int anchor;
	int originDA;
	int originAcross;
	int spacing;
	vec2 cos0;
	vec2 sin0;
	int flags;
	int sectorStride;
	int pad0;
	int pad1;
} pc;

void main()
{
	uint idx = gl_GlobalInvocationID.x;
	if (idx >= uint(pc.count))
		return;
	int anchor, dA, across, flags;
	vec2 cos0, sin0;
	if (pc.mode == 0)
	{
		int perSector = pc.cols * pc.rows;
		int s = int(idx) / perSector;
		int local = int(idx) - s * perSector;
		int iy = local / pc.cols;
		int ix = local - iy * pc.cols;
		anchor = pc.anchor;
		dA = pc.originDA + s * pc.sectorStride + ix * pc.spacing;
		across = min(max(pc.originAcross + iy * pc.spacing, 0), HM_I(HM_P_ACROSS_MAX));
		flags = pc.flags;
		cos0 = pc.cos0;
		sin0 = pc.sin0;
	}
	else
	{
		uint b = idx * 8u;
		anchor = int(pts.p[b]);
		dA = int(pts.p[b + 1u]);
		across = int(pts.p[b + 2u]);
		flags = int(pts.p[b + 3u]);
		cos0 = vec2(uintBitsToFloat(pts.p[b + 4u]), uintBitsToFloat(pts.p[b + 5u]));
		sin0 = vec2(uintBitsToFloat(pts.p[b + 6u]), uintBitsToFloat(pts.p[b + 7u]));
	}

	hm_set_anchor(anchor, cos0, sin0);
	HmResult h = hm_sample(dA, across);
	uint ob = idx * 16u;
	outp.o[ob + 0u] = h.height;
	outp.o[ob + 1u] = h.continental;
	outp.o[ob + 2u] = h.ridge;
	outp.o[ob + 3u] = h.valley;
	outp.o[ob + 12u] = h.macro;
	outp.o[ob + 13u] = h.gate;
	outp.o[ob + 14u] = h.erosion;
	outp.o[ob + 15u] = h.landform;
	outp.o[ob + 7u] = uintBitsToFloat(h.height < 0.0 ? 1u : 0u);
	if ((flags & 1) != 0)
	{
		RcFields f = rc_sample_fields(dA, across);
		vec4 w = rc_biome_weights(h.height, h.continental, h.ridge, f);
		precise float temperature = rc_clamp(f.temperature - max(0.0, h.height) * HM_F(RC_P_ALT_DROP) / 1000.0, 0.0, 1.0);
		outp.o[ob + 4u] = f.erosion;
		outp.o[ob + 5u] = temperature;
		outp.o[ob + 6u] = f.moisture;
		outp.o[ob + 8u] = w.x;
		outp.o[ob + 9u] = w.y;
		outp.o[ob + 10u] = w.z;
		outp.o[ob + 11u] = w.w;
	}
	else
	{
		for (uint i = 4u; i < 7u; i++) outp.o[ob + i] = 0.0;
		for (uint i = 8u; i < 12u; i++) outp.o[ob + i] = 0.0;
	}
}
