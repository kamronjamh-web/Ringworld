#[compute]
#version 450
// WO-G0 probe: one FastNoiseLite OpenSimplex2S evaluation per invocation. Used by tools/GpuNoiseParityTest.cs (which expands
// the include itself because it compiles from source on a local RenderingDevice).
#include "fnl_opensimplex2s.glslinc"

layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;
layout(set = 0, binding = 0, std430) restrict readonly buffer Inputs { float xyz[]; } inputs;
layout(set = 0, binding = 1, std430) restrict writeonly buffer Outputs { float v[]; } outputs;
layout(push_constant, std430) uniform Params
{
	int seed;
	int fractal;
	int octaves;
	int count;
	float frequency;
	float lacunarity;
	float gain;
	int repeat; // evaluate this many times (timing); the stored value is the last one
} pc;

void main()
{
	uint idx = gl_GlobalInvocationID.x;
	if (idx >= uint(pc.count))
		return;
	vec3 p = vec3(inputs.xyz[idx * 3u], inputs.xyz[idx * 3u + 1u], inputs.xyz[idx * 3u + 2u]);
	float bounding = fnl_fractal_bounding(pc.octaves, pc.gain);
	float r = 0.0;
	float acc = 0.0;
	for (int n = 0; n < pc.repeat; n++)
	{
		// vary the coordinate negligibly per repeat so the loop cannot be hoisted; the last repeat uses the exact input
		vec3 q = p;
		if (n + 1 < pc.repeat) q.z += float(n) * 1e-3;
		r = fnl_sample(pc.seed, pc.fractal, pc.octaves, pc.frequency, pc.lacunarity, pc.gain, bounding, q);
		acc += r;
	}
	// never true (|acc| is bounded by repeat); keeps the repeat loop from being eliminated as dead code
	if (acc == 1234567.0) r += 1.0;
	outputs.v[idx] = r;
}
