#[compute]
#version 450

// RingworldTechDemo extension (GPU offload GPU-4): the far weather atlases built on the GPU instead of the CPU.
//
// One compute shader, two entry modes selected by the includer (RingFarWeatherAtlasGpu.cs prepends the define):
//   default            whole-ring sample grid 128 x 32: per sample the weather terms (coverage, storm) and the S6 layer terms (low, mid,
//                      high * (1 - storm ownership), weatherness), the exact twin of RingScaledFarViewport.UpdateFarWeatherAtlas' sample loop.
//   RING_FAR_ATLAS_FINE  camera-centred fine layer atlas 128 x 64 RGBA8 (RingScaledFarViewport.CloudLayers.cs StepFarFineLayerAtlasBuild).
//
// Each invocation generates the natural weather systems that reach its position itself (a GLSL port of
// RingVolumetricCloudLayer.TryBuildWeatherSystem / EvaluateWeatherLifecycle: pure hash maths, so the whole ring needs no 64-slot block), then runs the
// SAME per-position formulas the macro weather map uses (ring_ws_macro_point, ring_cloud_layers_point). The CPU stays the authority for lightning,
// audio and wetness; this copy is visual only. The CPU path remains as the fallback and as the parity reference (tools/FarWeatherAtlasGpuParityTest.cs).
// Not covered here (the includer falls back to the CPU build): manual / preview weather and a triggered megastorm (its suppression of the natural systems).

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

// Same layout as RingWeatherSystem in CloudsInc.comp (which this build does not include).
struct RingWeatherSystem {
	vec4 a;
	vec4 b;
	vec4 c;
	vec4 d;
};

// A per-invocation copy of the weather-systems block (no UBO): the shared glslinc reads ringWeatherSystems.header* / .systems[i] exactly as
// it does from the cloud shaders' uniform block. Only the slots 0 .. RING_FAR_SLOTS - 1 are ever filled.
#define RING_FAR_SLOTS 32
struct RingWeatherSystemsLocal {
	vec4 header0;
	vec4 header1;
	vec4 header2;
	vec4 header3;
	RingWeatherSystem systems[RING_FAR_SLOTS];
};
RingWeatherSystemsLocal ringWeatherSystems;

#ifdef RING_FAR_ATLAS_FINE
layout(rgba8, binding = 0) uniform writeonly image2D out_fine;
#else
layout(rgba32f, binding = 0) uniform writeonly image2D out_weather;
layout(rgba32f, binding = 1) uniform writeonly image2D out_layers;
#endif

// p0 = (circumference m, ring width m, seed bits (int as float), natural lifecycles enabled)
// p1 = (macro cell m, maximum system radius m, spawn chance, wind.y * t)
// p2 = (birth s, develop s, mature s, dissipate s)
// p3 = (quiet s, lifecycle variation, cycle s, t remainder within the cycle s)
// p4 = (shift around (wrapped -wind.x * t), shift across (-wind.y * t), wind angle rad, 0)
// p5 = (weather base coverage, layer base coverage, 0, 0)
// p6 = (fine: window origin around m, fine: texel m, fine: across texel m, coarse: the ring width the sample rows span m)
// cluster = header3 of the systems block: (cell m, strength, small + 256 * large cells, seed bits)
// ints = (whole cycles of t, around cell count, search radius in cells, 0)
layout(binding = 2, std140) uniform FarAtlasParams {
	vec4 p0;
	vec4 p1;
	vec4 p2;
	vec4 p3;
	vec4 p4;
	vec4 p5;
	vec4 p6;
	vec4 cluster;
	ivec4 ints;
} params;

// The cloud layer table (RingCloudLayers.glslinc), the same 656 byte block the far layer sky binds.
#define RING_CLOUD_LAYERS_BINDING 3

#define RING_WS_MACRO_BUILD
#define RING_WS_QUANTIZED_BASE_CLUSTER
#include "res://addons/SunshineClouds2/RingWeatherSystems.glslinc"
#include "res://addons/SunshineClouds2/RingCloudLayers.glslinc"

// ---- Port of the natural system generator (RingVolumetricCloudLayer.cs) -------------------------------------------------------------------------
// WeatherSystemKind
#define KIND_CUMULUS_CLUSTER 1
#define KIND_CLOUD_BANK 2
#define KIND_MAJOR_FRONT 3
#define KIND_EXCEPTIONAL_FRONT 4
// CloudFormationProfile ids (the genus row index)
#define PROFILE_CUMULUS 0
#define PROFILE_STRATOCUMULUS 1
#define PROFILE_STORM_TOWER 2
#define PROFILE_CUMULONIMBUS 3
#define PROFILE_CIRRUS 4
#define PROFILE_CIRROSTRATUS 5
#define PROFILE_CIRROCUMULUS 6
#define PROFILE_ALTOSTRATUS 7
#define PROFILE_ALTOCUMULUS 8
#define PROFILE_STRATUS 9
#define PROFILE_NIMBOSTRATUS 10
#define SYSTEM_PROFILE_SALT 213

uint g_seed;

// Lifecycle hash: Hash01(a, z, seed ^ generation * 0x45D9F3B, salt) (the seed is unchanged for generation 0, i.e. with lifecycles off).
float life_hash(int a, int z, int generation, int salt) {
	return ring_ws_hash01(a, z, g_seed ^ (uint(generation) * 0x45D9F3Bu), salt);
}

float smooth_unit(float v) {
	float t = clamp(v, 0.0, 1.0);
	return t * t * (3.0 - 2.0 * t);
}

float varied(float baseSeconds, int a, int z, int generation, int salt, float variation) {
	return max(0.001, baseSeconds * mix(1.0 - variation, 1.0 + variation, life_hash(a, z, generation, salt)));
}

// WeatherSystemId(aroundCell, acrossCell, seed ^ generation * 0x45D9F3B)
int system_id(int a, int z, int generation) {
	uint value = (g_seed ^ (uint(generation) * 0x45D9F3Bu)) ^ 0xA511E9B3u;
	value ^= uint(a) * 0x9E3779B9u;
	value ^= uint(z) * 0x85EBCA6Bu;
	value ^= value >> 16u;
	value *= 0x7FEB352Du;
	value ^= value >> 15u;
	int id = int(value & 0x7FFFFFFFu);
	return id == 0 ? 1 : id;
}

// ResolveSystemFormationProfile (mature density / precipitation).
int resolve_profile(int kind, float roll, float density, float precipPotential) {
	float precipitation = precipPotential * density;
	if (kind == KIND_EXCEPTIONAL_FRONT && density >= 0.24) {
		return roll < 0.28 ? PROFILE_CUMULONIMBUS : roll < 0.62 ? PROFILE_NIMBOSTRATUS : roll < 0.78 ? PROFILE_ALTOSTRATUS
			: roll < 0.90 ? PROFILE_CIRROSTRATUS : PROFILE_STRATOCUMULUS;
	}
	if (kind == KIND_MAJOR_FRONT && density >= 0.22) {
		return (roll < 0.12 && precipitation >= 0.35) ? PROFILE_CUMULONIMBUS : roll < 0.42 ? PROFILE_NIMBOSTRATUS : roll < 0.67 ? PROFILE_ALTOSTRATUS
			: roll < 0.88 ? PROFILE_STRATOCUMULUS : PROFILE_ALTOCUMULUS;
	}
	if (kind == KIND_CLOUD_BANK) {
		return (precipitation >= 0.32 && roll < 0.20) ? PROFILE_NIMBOSTRATUS : roll < 0.42 ? PROFILE_STRATUS : roll < 0.72 ? PROFILE_STRATOCUMULUS
			: roll < 0.90 ? PROFILE_ALTOSTRATUS : PROFILE_CIRROSTRATUS;
	}
	if (kind == KIND_CUMULUS_CLUSTER) {
		return (precipitation >= 0.18 && roll < 0.10) ? PROFILE_CUMULONIMBUS : roll < 0.58 ? PROFILE_CUMULUS : roll < 0.84 ? PROFILE_ALTOCUMULUS
			: PROFILE_CIRROCUMULUS;
	}
	return roll < 0.30 ? PROFILE_CUMULUS : roll < 0.48 ? PROFILE_STRATOCUMULUS : roll < 0.64 ? PROFILE_ALTOCUMULUS : roll < 0.74 ? PROFILE_STRATUS
		: roll < 0.84 ? PROFILE_CIRRUS : roll < 0.90 ? PROFILE_ALTOSTRATUS : roll < 0.94 ? PROFILE_CIRROSTRATUS : roll < 0.97 ? PROFILE_CIRROCUMULUS
		: roll < 0.99 ? PROFILE_NIMBOSTRATUS : PROFILE_CUMULONIMBUS;
}

// CloudLayerTable.LowersBase through the uploaded genus row (w = CloudLowersBase + 4 * front type).
bool lowers_base(int profile, int kind) {
	int code = int(ringCloudLayers.genus[clamp(profile, 0, 15)].w + 0.5) & 3;
	bool front = kind == KIND_MAJOR_FRONT || kind == KIND_EXCEPTIONAL_FRONT;
	return code == 1 || (code == 2 && (front || kind == KIND_CLOUD_BANK)) || (code == 3 && front);
}

// TryBuildWeatherSystem + EvaluateWeatherLifecycle + BuildNaturalRenderSystem for one macro cell. Fills `slot` and returns true when the system exists.
bool try_build(int cellA, int cellZ, out RingWeatherSystem slot) {
	slot = RingWeatherSystem(vec4(0.0), vec4(0.0), vec4(0.0), vec4(0.0));
	float circumference = params.p0.x;
	float macroCell = params.p1.x;
	float maximumRadius = params.p1.y;
	int generation = 0;
	float envelope = 1.0;
	if (params.p0.w > 0.5) {
		float cycle = params.p3.z;
		float offset = ring_ws_hash01(cellA, cellZ, g_seed, 109) * cycle;
		float shifted = params.p3.w + offset;
		float whole = floor(shifted / cycle);
		generation = params.ints.x + int(whole);
		float phase = shifted - whole * cycle;
		float variation = clamp(params.p3.y, 0.0, 0.45);
		float birth = varied(params.p2.x, cellA, cellZ, generation, 110, variation);
		float develop = varied(params.p2.y, cellA, cellZ, generation, 111, variation);
		float mature = varied(params.p2.z, cellA, cellZ, generation, 112, variation);
		float dissipate = varied(params.p2.w, cellA, cellZ, generation, 113, variation);
		float quiet = varied(params.p3.x, cellA, cellZ, generation, 114, variation);
		float normalization = cycle / max(0.001, birth + develop + mature + dissipate + quiet);
		birth *= normalization;
		develop *= normalization;
		mature *= normalization;
		dissipate *= normalization;
		if (phase < birth) envelope = smooth_unit(phase / max(0.001, birth)) * 0.30;
		else {
			phase -= birth;
			if (phase < develop) envelope = mix(0.30, 1.0, smooth_unit(phase / max(0.001, develop)));
			else {
				phase -= develop;
				if (phase < mature) envelope = 1.0;
				else {
					phase -= mature;
					envelope = phase < dissipate ? 1.0 - smooth_unit(phase / max(0.001, dissipate)) : 0.0;
				}
			}
		}
	}
	if (envelope <= 0.0001 || life_hash(cellA, cellZ, generation, 100) > clamp(params.p1.z, 0.1, 0.8)) return false;

	float typeRoll = life_hash(cellA, cellZ, generation, 101);
	int kind = typeRoll < 0.64 ? KIND_CUMULUS_CLUSTER : typeRoll < 0.89 ? KIND_CLOUD_BANK : typeRoll < 0.988 ? KIND_MAJOR_FRONT : KIND_EXCEPTIONAL_FRONT;
	float radiusRandom = life_hash(cellA, cellZ, generation, 102);
	float majorRadius = kind == KIND_CUMULUS_CLUSTER ? mix(8000.0, 17000.0, radiusRandom)
		: kind == KIND_CLOUD_BANK ? mix(18000.0, 36000.0, radiusRandom)
		: kind == KIND_MAJOR_FRONT ? mix(36000.0, 70000.0, radiusRandom)
		: mix(70000.0, maximumRadius, radiusRandom);
	float aspectRoll = life_hash(cellA, cellZ, generation, 103);
	float aspect = kind == KIND_CUMULUS_CLUSTER ? mix(0.85, 1.45, aspectRoll)
		: kind == KIND_CLOUD_BANK ? mix(1.35, 2.25, aspectRoll)
		: kind == KIND_MAJOR_FRONT ? mix(2.0, 3.4, aspectRoll)
		: mix(2.8, 4.5, aspectRoll);
	float minorRadius = majorRadius / max(0.8, aspect);
	float orientation = (life_hash(cellA, cellZ, generation, 104) - 0.5) * 3.14159265359;
	float centerAround = (float(cellA) + 0.5) * macroCell + (life_hash(cellA, cellZ, generation, 105) - 0.5) * macroCell * 0.82;
	centerAround -= circumference * floor(centerAround / circumference);
	float centerAcross = (float(cellZ) + 0.5) * macroCell + (life_hash(cellA, cellZ, generation, 106) - 0.5) * macroCell * 0.82;
	float advectedAcross = centerAcross + params.p1.w;
	if (advectedAcross < -majorRadius || advectedAcross > params.p0.y + majorRadius) return false;
	float densityRoll = life_hash(cellA, cellZ, generation, 107);
	float density = kind == KIND_CUMULUS_CLUSTER ? mix(0.46, 0.66, densityRoll)
		: kind == KIND_CLOUD_BANK ? mix(0.60, 0.80, densityRoll)
		: kind == KIND_MAJOR_FRONT ? mix(0.75, 0.94, densityRoll)
		: mix(0.88, 1.0, densityRoll);
	float precipRoll = life_hash(cellA, cellZ, generation, 108);
	float precipitation = kind == KIND_CUMULUS_CLUSTER ? mix(0.02, 0.28, precipRoll)
		: kind == KIND_CLOUD_BANK ? mix(0.20, 0.58, precipRoll)
		: kind == KIND_MAJOR_FRONT ? mix(0.52, 0.88, precipRoll)
		: mix(0.78, 1.0, precipRoll);

	// The formation profile is a property of the whole system (rolled from the id and the mature values).
	int id = system_id(cellA, cellZ, generation);
	int profile = resolve_profile(kind, ring_ws_hash01(id, 0, g_seed, SYSTEM_PROFILE_SALT), density, precipitation);
	uint flags = 0u;
	if (profile == PROFILE_STORM_TOWER || profile == PROFILE_CUMULONIMBUS) flags |= RING_WS_FLAG_SCULPT;
	if (lowers_base(profile, kind)) flags |= RING_WS_FLAG_LOWERS_BASE;
	slot.a = vec4(centerAround, centerAcross, majorRadius * envelope, minorRadius * envelope);
	slot.b = vec4(cos(orientation), sin(orientation), density * envelope, precipitation * envelope);
	slot.c = vec4(0.0, 0.0, float(profile), float(flags));
	slot.d = vec4(0.0, 1.0, 1.0, 1.5);
	return true;
}

// CollectRenderWeatherSystems (radius 0, natural systems): every system whose major-radius circle contains the position. Returns the count.
int gather_systems(float around, float across) {
	float circumference = params.p0.x;
	float macroCell = params.p1.x;
	float maximumRadius = params.p1.y;
	int aroundCells = params.ints.y;
	int searchRadius = params.ints.z;
	float sourceAround = around + params.p4.x;
	sourceAround -= circumference * floor(sourceAround / circumference);
	float sourceAcross = across + params.p4.y;
	int centerCellAround = int(floor(sourceAround / macroCell));
	int centerCellAcross = int(floor(sourceAcross / macroCell));
	float prefilter = maximumRadius + macroCell * 0.6;
	float prefilterSquared = prefilter * prefilter;
	int count = 0;
	for (int dz = -searchRadius; dz <= searchRadius; dz++) {
		int cellZ = centerCellAcross + dz;
		float acrossDelta = (float(cellZ) + 0.5) * macroCell - sourceAcross;
		if (acrossDelta * acrossDelta > prefilterSquared) continue;
		for (int dx = -searchRadius; dx <= searchRadius; dx++) {
			int cellA = centerCellAround + dx;
			cellA = cellA - aroundCells * int(floor(float(cellA) / float(aroundCells)));
			float aroundDelta = ring_ws_wrap((float(cellA) + 0.5) * macroCell - sourceAround, circumference);
			if (aroundDelta * aroundDelta + acrossDelta * acrossDelta > prefilterSquared) continue;
			RingWeatherSystem slot;
			if (!try_build(cellA, cellZ, slot)) continue;
			float deltaAround = ring_ws_wrap(slot.a.x - sourceAround, circumference);
			float deltaAcross = slot.a.y - sourceAcross;
			if (sqrt(deltaAround * deltaAround + deltaAcross * deltaAcross) >= slot.a.z) continue;
			if (count < RING_FAR_SLOTS) {
				ringWeatherSystems.systems[count] = slot;
				count++;
			}
		}
	}
	return count;
}

// One atlas sample: returns (coverage, storm) in `weather` and the layer value (low, mid, high * (1 - ownership), weatherness).
void evaluate_sample(float around, float across, out vec2 weather, out vec4 layers) {
	float circumference = params.p0.x;
	ringWeatherSystems.header0 = vec4(0.0, 1.0, params.p5.x, 0.0);
	ringWeatherSystems.header1 = vec4(0.0, 0.0, 0.0, params.p4.z);
	ringWeatherSystems.header2 = vec4(0.0);
	ringWeatherSystems.header3 = params.cluster;
	int count = gather_systems(around, across);
	uint mask = count >= 32 ? 0xFFFFFFFFu : ((1u << uint(count)) - 1u);
	vec2 shift = params.p4.xy;
	vec3 canonical = vec3(around, 0.0, across);
	vec4 macro = ring_ws_macro_point(canonical, uvec2(mask, 0u), shift, circumference);
	weather = vec2(clamp(macro.x, 0.0, 1.0), clamp(macro.y, 0.0, 1.0));
	// The layer build reads the base coverage the far layer block carries (header0.z of the live systems block), the weather the layer's own.
	ringWeatherSystems.header0.z = params.p5.y;
	layers = ring_cloud_layers_point(canonical, uvec2(mask, 0u), shift, circumference, macro.x);
	float owned = 0.0;
	for (int i = 0; i < count; i++) {
		if ((ring_ws_flags(i) & RING_WS_FLAG_SCULPT) == 0u) continue;
		vec4 a = ringWeatherSystems.systems[i].a;
		vec4 b = ringWeatherSystems.systems[i].b;
		float dA = ring_ws_wrap(around + shift.x - a.x, circumference);
		float dC = across + shift.y - a.y;
		float qx = (b.x * dA + b.y * dC) / max(a.z, 1.0);
		float qz = (-b.y * dA + b.x * dC) / max(a.w, 1.0);
		float d2 = qx * qx + qz * qz;
		float t = clamp((d2 - 1.69) / (1.45 * 1.45 - 1.69), 0.0, 1.0);
		owned = max(owned, 1.0 - t * t * (3.0 - 2.0 * t));
	}
	layers.z *= 1.0 - owned;
}

void main() {
	ivec2 texel = ivec2(gl_GlobalInvocationID.xy);
	g_seed = uint(floatBitsToInt(params.p0.z));
#ifdef RING_FAR_ATLAS_FINE
	if (texel.x >= 128 || texel.y >= 64) return;
	float around = params.p6.x + (float(texel.x) + 0.5) * params.p6.y;
	around -= params.p0.x * floor(around / params.p0.x);
	float across = (float(texel.y) + 0.5) * params.p6.z;
	vec2 weather;
	vec4 layers;
	evaluate_sample(around, across, weather, layers);
	imageStore(out_fine, texel, layers);
#else
	if (texel.x >= 128 || texel.y >= 32) return;
	float around = float(texel.x) / 128.0 * params.p0.x;
	float across = float(texel.y) / 31.0 * params.p6.w;
	vec2 weather;
	vec4 layers;
	evaluate_sample(around, across, weather, layers);
	imageStore(out_weather, texel, vec4(weather, 0.0, 0.0));
	imageStore(out_layers, texel, layers);
#endif
}
