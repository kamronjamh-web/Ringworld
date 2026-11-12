#[compute]
#version 450
// Drainage bake source (docs/t2-river-reset.md 4.2, T2R-3): a lean output shader over the same height model and regional climate as K1
// (ring_proxy_height_k1.glsl), which writes 16 floats per sample and is too wide for a whole-ring readback. Natural height only: no relief
// term of a later generation, no carve, no site operator (the F31(c) contract).
//   mode 0 (grid):   input ints: cols*5 dA values (per column: 4 sample offsets, then the centre), then rows*5 across values (per row: 4 sample
//                    positions, then the centre), both as integer metres. Invocation i -> row = i / cols, col = i % cols. The 16 samples are
//                    ordered j*4+i (j across, i around). Output 3 floats per cell: lowest height, index of the lowest sample (first wins a
//                    tie, as a strict < in index order), regional moisture at the centre.
//   mode 1 (points): input ints, 8 per point: anchor, dA, across, flags, cos0.hi, cos0.lo, sin0.hi, sin0.lo (float bits). Output 1 float.
// Anchor: exact integer metres around the ring, 0 <= anchor < C; |dA| < 2^23 (see ring_height_model.glslinc).
#include "ring_height_model.glslinc"
#include "ring_regional_climate.glslinc"

layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;
layout(set = 0, binding = 1, std430) restrict writeonly buffer Output { float o[]; } outp;
layout(set = 0, binding = 2, std430) restrict readonly buffer Input { int p[]; } inp;

layout(push_constant, std430) uniform Params
{
	int mode;
	int count;
	int cols;
	int rows;
	int anchor;
	int pad0;
	int pad1;
	int pad2;
	vec2 cos0;
	vec2 sin0;
} pc;

void main()
{
	uint idx = gl_GlobalInvocationID.x;
	if (idx >= uint(pc.count))
		return;
	if (pc.mode == 0)
	{
		int row = int(idx) / pc.cols;
		int col = int(idx) - row * pc.cols;
		int acrossBase = pc.cols * 5 + row * 5;
		int aroundBase = col * 5;
		hm_set_anchor(pc.anchor, pc.cos0, pc.sin0);
		float best = 3.0e38;
		int bestIndex = 0;
		for (int j = 0; j < 4; j++)
		{
			int across = inp.p[acrossBase + j];
			for (int i = 0; i < 4; i++)
			{
				HmResult h = hm_sample(inp.p[aroundBase + i], across);
				if (h.height < best)
				{
					best = h.height;
					bestIndex = j * 4 + i;
				}
			}
		}
		RcFields f = rc_sample_fields(inp.p[aroundBase + 4], inp.p[acrossBase + 4]);
		uint ob = idx * 3u;
		outp.o[ob] = best;
		outp.o[ob + 1u] = float(bestIndex);
		outp.o[ob + 2u] = f.moisture;
	}
	else
	{
		uint b = idx * 8u;
		hm_set_anchor(inp.p[b], vec2(intBitsToFloat(inp.p[b + 4u]), intBitsToFloat(inp.p[b + 5u])), vec2(intBitsToFloat(inp.p[b + 6u]), intBitsToFloat(inp.p[b + 7u])));
		HmResult h = hm_sample(inp.p[b + 1u], inp.p[b + 2u]);
		outp.o[idx] = h.height;
	}
}
