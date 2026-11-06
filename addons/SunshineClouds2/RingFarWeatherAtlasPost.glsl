#[compute]
#version 450

// RingworldTechDemo extension (GPU offload GPU-4): second stage of the far weather atlas build (RingFarWeatherAtlas.glsl writes the 128 x 32 sample grids).
//
// default            bilinear upsample of the two sample grids to the 1024 x 128 RGBA8 atlases (the twin of RingScaledFarViewport.SampleWeatherGrid) plus
//                    the two hashed detail channels of the weather atlas (B, A: SeedUnit).
// RING_FAR_ATLAS_MIP one 2 x 2 box-filter step of the weather atlas mip chain (the CPU used Image.GenerateMipmaps).

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

#ifdef RING_FAR_ATLAS_MIP
layout(rgba8, binding = 0) uniform readonly image2D mip_source;
layout(rgba8, binding = 1) uniform writeonly image2D mip_dest;

void main() {
	ivec2 texel = ivec2(gl_GlobalInvocationID.xy);
	ivec2 size = imageSize(mip_dest);
	if (texel.x >= size.x || texel.y >= size.y) return;
	ivec2 sourceSize = imageSize(mip_source);
	ivec2 base = texel * 2;
	ivec2 far = min(base + 1, sourceSize - 1);
	vec4 sum = imageLoad(mip_source, base) + imageLoad(mip_source, ivec2(far.x, base.y))
		+ imageLoad(mip_source, ivec2(base.x, far.y)) + imageLoad(mip_source, far);
	imageStore(mip_dest, texel, sum * 0.25);
}
#else
layout(rgba32f, binding = 0) uniform readonly image2D weather_samples;
layout(rgba32f, binding = 1) uniform readonly image2D layer_samples;
layout(rgba8, binding = 2) uniform writeonly image2D weather_atlas;
layout(rgba8, binding = 3) uniform writeonly image2D layer_atlas;

// x = hashed seed of the detail channels ((uint)terrainSeed ^ 0x9e3779b9, as an int bit pattern)
layout(binding = 4, std140) uniform PostParams {
	ivec4 seed;
} params;

float seed_unit(uint seed, int x, int y) {
	uint h = seed + 0x9e3779b9u;
	h ^= uint(x) * 0x85ebca6bu;
	h ^= uint(y) * 0xc2b2ae35u;
	h ^= h >> 16u;
	h *= 0x7feb352du;
	h ^= h >> 15u;
	h *= 0x846ca68bu;
	h ^= h >> 16u;
	return float(h & 0x00ffffffu) / 16777215.0;
}

void main() {
	ivec2 texel = ivec2(gl_GlobalInvocationID.xy);
	ivec2 size = imageSize(weather_atlas);
	if (texel.x >= size.x || texel.y >= size.y) return;
	const int sampleWidth = 128;
	const int sampleHeight = 32;
	float u = float(texel.x) / float(size.x);
	float v = float(texel.y) / float(max(1, size.y - 1));
	float x = u * float(sampleWidth);
	int x0 = int(floor(x));
	x0 = x0 - sampleWidth * int(floor(float(x0) / float(sampleWidth)));
	int x1 = (x0 + 1) % sampleWidth;
	float y = clamp(v, 0.0, 1.0) * float(sampleHeight - 1);
	int y0 = int(floor(y));
	int y1 = min(y0 + 1, sampleHeight - 1);
	float fx = x - floor(x);
	float fy = y - float(y0);
	vec4 w00 = imageLoad(weather_samples, ivec2(x0, y0));
	vec4 w10 = imageLoad(weather_samples, ivec2(x1, y0));
	vec4 w01 = imageLoad(weather_samples, ivec2(x0, y1));
	vec4 w11 = imageLoad(weather_samples, ivec2(x1, y1));
	vec4 weather = mix(mix(w00, w10, fx), mix(w01, w11, fx), fy);
	vec4 l00 = imageLoad(layer_samples, ivec2(x0, y0));
	vec4 l10 = imageLoad(layer_samples, ivec2(x1, y0));
	vec4 l01 = imageLoad(layer_samples, ivec2(x0, y1));
	vec4 l11 = imageLoad(layer_samples, ivec2(x1, y1));
	vec4 layers = mix(mix(l00, l10, fx), mix(l01, l11, fx), fy);
	uint seed = uint(params.seed.x);
	imageStore(weather_atlas, texel, vec4(clamp(weather.x, 0.0, 1.0), clamp(weather.y, 0.0, 1.0),
		seed_unit(seed ^ 0x27d4eb2du, texel.x / 7, texel.y / 5), seed_unit(seed ^ 0x165667b1u, texel.x / 3, texel.y / 3)));
	imageStore(layer_atlas, texel, layers);
}
#endif
