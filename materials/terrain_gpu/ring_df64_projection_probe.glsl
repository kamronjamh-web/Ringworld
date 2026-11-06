#[compute]
#version 450
// WO-G0 probe: df64 cylindrical projection, one point per invocation (see ring_df64.glslinc). Used by tools/GpuNoiseParityTest.cs.
#include "ring_df64.glslinc"

layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;
// per point: cos0.hi, cos0.lo, sin0.hi, sin0.lo, dA, warp, pad, pad
layout(set = 0, binding = 0, std430) restrict readonly buffer Inputs { float v[]; } inputs;
layout(set = 0, binding = 1, std430) restrict writeonly buffer Outputs { vec2 xy[]; } outputs;
layout(push_constant, std430) uniform Params
{
	vec2 k;      // tau / circumference as df64
	vec2 radius; // circumference / tau as df64
	int count;
	int pad0;
	int pad1;
	int pad2;
} pc;

void main()
{
	uint idx = gl_GlobalInvocationID.x;
	if (idx >= uint(pc.count))
		return;
	uint b = idx * 8u;
	vec2 cos0 = vec2(inputs.v[b], inputs.v[b + 1u]);
	vec2 sin0 = vec2(inputs.v[b + 2u], inputs.v[b + 3u]);
	outputs.xy[idx] = ring_df64_project(cos0, sin0, pc.k, pc.radius, inputs.v[b + 4u], inputs.v[b + 5u]);
}
