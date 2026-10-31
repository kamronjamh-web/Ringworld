#[compute]
#version 450
#extension GL_EXT_control_flow_attributes : enable
#define PI 3.141592
#define ABSORPTION_COEFFICIENT 0.9

#include "./CloudsInc.comp"
#include "../../materials/ring_atmosphere_shared.gdshaderinc"
#include "../../materials/ring_haze_shared.gdshaderinc"
#include "../../materials/ring_cloud_weather_shared.gdshaderinc"
#include "../../materials/ring_megastorm_darkness.gdshaderinc"
#include "./RingAtmosphereCompute.glslinc"
#include "./RingCloudHistory.gdshaderinc"
#include "./SunshineCloudsDepth.glslinc"
#include "../../materials/ring_terrain_cloud_sun_shared.gdshaderinc"

// Invocations in the (x, y, z) dimension
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

// CloudInterleave (RingworldTechDemo): 0 = every thread marches its own pixel (legacy; the interleave branches below compile away),
// 2 / 4 = each thread marches one pixel of an n x n block per frame and SunshineCloudsInterleaveFill.glsl reconstructs the rest.
// Set per pipeline variant by the compositor (specialization constant 0).
layout(constant_id = 0) const int RING_INTERLEAVE = 0;
// Storm shape fallback (specialization constant 1): 0 = the default kernel carries NO analytic pillar/anvil shape (storms come from the
// baked storm profile map), 1 = HEAD's pillar/anvil lattice is compiled in. The driver selects the fallback pipeline only while the map is
// invalid (camera-independent clouds off, map not built yet, debug A/B), so the map-on kernel never pays for the legacy code.
layout(constant_id = 1) const int RING_STORM_FALLBACK = 0;
// Megastorm strip path (specialization constant 2; docs/megastorm-design.md 3.2): 0 = the default kernel carries NO megastorm strip code (it
// compiles away, so an untriggered world runs the pre-megastorm kernel), 1 = the strip fetch, moving-frame noise and storm-share rules are
// compiled in. The driver selects the variant only while the strip is valid (the wall is near).
layout(constant_id = 2) const int RING_MEGASTORM = 0;
#define RING_MEGASTORM_ENABLED (RING_MEGASTORM != 0)
// Megastorm light under the overhang (docs/megastorm-design.md 2.3, WO4b; RingStormProfileSample.glslinc ring_megastorm_open_light):
// the open-sky term runs for the visible front of a ray only (accumulated opacity below this), and the storm ambient of a fully buried /
// fully open megastorm sample is scaled by SHADE / OPEN (the horizon band at grazing vs normal incidence, about 4:1).
#define RING_MEGASTORM_OPEN_DEPTH 0.85
#define RING_MEGASTORM_SHADE_AMBIENT 0.45
#define RING_MEGASTORM_OPEN_AMBIENT 1.8
// Erosion relief: +-this share of the light factor between the deepest recesses (medium noise b = 1) and the strongest bulges (b = 0).
#define RING_MEGASTORM_RELIEF 0.35

layout(rgba16f, binding = 0) uniform image2D output_data_image;
layout(rgba16f, binding = 1) uniform image2D output_color_image;

layout(rgba32f, binding = 2) uniform image2D accum_1A_image;
layout(rgba32f, binding = 3) uniform image2D accum_1B_image;

layout(rgba32f, binding = 4) uniform image2D accum_2A_image;
layout(rgba32f, binding = 5) uniform image2D accum_2B_image;

layout(binding = 6) uniform sampler2D depth_image;
layout(binding = 7) uniform sampler2D extra_large_noise;
layout(binding = 8) uniform sampler3D large_noise;
layout(binding = 9) uniform sampler3D noise_medium;
layout(binding = 10) uniform sampler3D noise_small;
layout(binding = 11) uniform sampler3D curl_noise;
layout(binding = 12) uniform sampler3D dither_small;
layout(binding = 13) uniform sampler2D heightmask;

layout(binding = 14) uniform uniformBuffer {
	GenericData data;
} genericData;

layout(binding = 15) uniform LightsBuffer {
	DirectionalLight directionalLights[4];
	PointLight pointLights[128];
	PointEffector pointEffectors[64];
	// Append-only (RingworldTechDemo eclipse beam cloud light): x = segment count (0..8), y = phase weight,
	// z = extinction per metre per unit density, w reserved.
	vec4 segmentHeader;
	SegmentLight segmentLights[8];
};

layout(binding = 16, std430) restrict buffer SamplePointsBuffer {
	vec4 SamplePoints[256];
};


layout(binding = 17, std140) uniform SceneDataBlock {
	SceneData data;
	SceneData prev_data;
} scene_data_block;
layout(binding = 18) uniform sampler2D far_opaque_depth_image;
layout(set = 1, binding = 19) uniform sampler2D terrain_cloud_sun_global_image;
layout(set = 1, binding = 20) uniform sampler2D terrain_cloud_sun_regional_image;
// Camera-independent clouds (RingworldTechDemo): up to 64 render weather systems. Set 0 uses bindings 0-18 (19 and 20 live in
// set 1), so 21 is free. Layout and semantics are documented in RingWeatherSystems.glslinc; header0.y = 0 keeps every legacy path.
layout(binding = 21, std140) uniform RingWeatherSystemsBlock {
	vec4 header0;
	vec4 header1;
	vec4 header2;
	vec4 header3;
	RingWeatherSystem systems[64];
} ringWeatherSystems;
// Macro weather map (RingMacroWeatherMap.glsl): world-anchored RGBA16F array (layer = front/back buffer) holding the per-position
// macro terms, so a cloud sample does one bilinear fetch instead of looping over the ray's systems. Params: see RingWeatherSystems.glslinc.
layout(binding = 22, std140) uniform RingMacroMapBlock {
	vec4 a;
	vec4 b;
} ringMacroMap;
layout(binding = 23) uniform sampler2DArray ring_macro_map;
// Storm profile map (RingStormProfileBake.glsl): world-anchored 2.5D storm columns baked outside the march, one fetch per primary
// sample while valid. Params: see RingStormProfileSample.glslinc.
layout(binding = 24, std140) uniform RingStormMapBlock {
	vec4 a;
	vec4 b;
	vec4 c;
} ringStormMap;
layout(binding = 25) uniform sampler2DArray ring_storm_map;
// CloudInterleave block images (one texel per marched pixel; only written when RING_INTERLEAVE > 0): steady light rgba,
// transient light rgb, and march data (initial distance, traveled distance, cloud body distance, opaque depth metadata).
layout(rgba16f, binding = 26) uniform writeonly image2D interleave_steady_image;
layout(rgba32f, binding = 27) uniform writeonly image2D interleave_transient_image;
layout(rgba32f, binding = 28) uniform writeonly image2D interleave_data_image;
// S6 cloud layers (RingCloudLayers.glslinc): the layer table block (live copy, published next to the weather systems) and the generated
// layer height gradient (8 rows, see RingCloudLayerSample.glslinc). Bindings 29 and 30 of set 0.
layout(binding = 30) uniform sampler2D layer_heightmask;
// Megastorm strip (RingMegastormStripBuild.glsl, docs/megastorm-design.md 3.2): the moving-frame wall columns, one fetch per primary sample
// while a megastorm record is in reach. Params: see RingStormProfileSample.glslinc.
layout(binding = 31, std140) uniform RingMegastormStripBlock {
	vec4 a;
	vec4 b;
	vec4 c;
	vec4 d;
	vec4 e;
} ringMegastormStrip;
layout(binding = 32) uniform sampler2DArray ring_megastorm_strip;
#define RING_WS_MACRO_MAP
// The ray mask also keeps base-lowering systems (not only sculpt ones) so the ray's floor is the weather base wherever weatherness can be > 0.
#define RING_WS_MASK_LOWERS_BASE 1
#include "./RingWeatherSystems.glslinc"
#define RING_STORM_SUN_LIGHT
#include "./RingStormProfileSample.glslinc"
#define RING_CLOUD_LAYERS_BINDING 29
// S6 3.8 deck colour (the ambient chroma of layer-deck samples follows the light through the cloud above); 0 = the blue-sky ambient.
// Runtime A/B: CameraIndependentDebugMask bit 256 skips it (per-sample overhead fraction and the final blend).
#ifndef RING_LAYER_DECK_COLOUR
#define RING_LAYER_DECK_COLOUR 1
#endif
#include "./RingCloudLayers.glslinc"
#include "./RingCloudLayerSample.glslinc"
// S6 WO5: the High layer's analytic cirrus shell (composited after the march; the includer supplies its noise reads).
#define RING_CIRRUS_TEX2D(uv, lod) textureLod(extra_large_noise, (uv), (lod)).a
#define RING_CIRRUS_TEX2D_SIZE float(textureSize(extra_large_noise, 0).x)
#include "../../materials/ring_cirrus_shell.gdshaderinc"

float terrain_cloud_direct_sun_visibility(vec3 worldPosition) {
	if (genericData.data.terrain_cloud_sun_profile[3].y <= 0.5
		&& genericData.data.terrain_cloud_sun_profile[3].z <= 0.5) return 1.0;
	vec3 cart = vec3(dot(genericData.data.terrain_cloud_sun_canonical_frame[0].xyz, worldPosition) + genericData.data.terrain_cloud_sun_canonical_frame[0].w,
		dot(genericData.data.terrain_cloud_sun_canonical_frame[1].xyz, worldPosition) + genericData.data.terrain_cloud_sun_canonical_frame[1].w,
		dot(genericData.data.terrain_cloud_sun_canonical_frame[2].xyz, worldPosition) + genericData.data.terrain_cloud_sun_canonical_frame[2].w);
	vec4 geometry = genericData.data.terrain_cloud_sun_canonical_frame[3];
	if (geometry.x <= 0.0 || geometry.w <= 0.0) return 1.0;
	vec3 canonical = vec3(mod(atan(cart.z, cart.x) * geometry.x + geometry.w, geometry.w),
		geometry.x - length(cart.xz) - geometry.y, cart.y - geometry.z);
	return ring_terrain_cloud_sun_visibility(canonical, terrain_cloud_sun_global_image,
		terrain_cloud_sun_regional_image, genericData.data.terrain_cloud_sun_profile).x;
}

// Our push constant
// layout(push_constant, std430) uniform Params {
// 	vec2 raster_size;
// 	float large_noise_scale;
// 	float medium_noise_scale;

// 	float time;
// 	float cloud_coverage;
// 	float cloud_density;
// 	float small_noise_strength;

// 	float cloud_lighting_power;
// 	float accumilation_decay;
// 	vec2 cameraRotation;
// } params;

//Helpers
const int BayerFilter16[16] =
{
    0, 8, 2, 10,
    12, 4, 14, 6,
    3, 11, 1, 9,
    15, 7, 13, 5
};
const int BayerFilter4[4] =
{
    0, 1,
    3, 2,
};

const mat4 bayer_matrix = mat4(
    vec4(00.0 / 16.0, 12.0 / 16.0, 03.0 / 16.0, 15.0 / 16.0),
    vec4(08.0 / 16.0, 04.0 / 16.0, 11.0 / 16.0, 07.0 / 16.0),
    vec4(02.0 / 16.0, 14.0 / 16.0, 01.0 / 16.0, 13.0 / 16.0),
    vec4(10.0 / 16.0, 06.0 / 16.0, 09.0 / 16.0, 05.0 / 16.0));

float quadraticOut(float t) {
  return -t * (t - 2.0);
}

float quadraticIn(float t) {
  return t * t;
}

float rand(vec2 co){
    return fract(sin(dot(co, vec2(12.9898, 78.233))) * 43758.5453);
}

float get_dither_value(vec2 pixel) {
    int x = int(pixel.x - 4.0 * floor(pixel.x / 4.0));
    int y = int(pixel.y - 4.0 * floor(pixel.y / 4.0));
    return bayer_matrix[x][y];
}

float remap(float value, float min1, float max1, float min2, float max2) {
  return min2 + (value - min1) * (max2 - min2) / (max1 - min1);
}

float BeersLaw (float dist, float absorption) {
  return exp(-dist * absorption);
}

float Powder (float dist, float absorption) {
  return 1.0 - exp(-dist * absorption * 2.0);
}

float HenyeyGreenstein(float g, float costh)
{
    return (1.0 - g * g) / (4.0 * PI * pow(1.0 + g * g - 2.0 * g * costh, 3.0/2.0));
}

bool renderBayer(ivec2 fragCoord, int framecount)
{
	//int BAYER = 16;
    //int index = framecount % BAYER;
    
    return (fragCoord.x + 4 * fragCoord.y) % 16 == BayerFilter16[framecount];
}

//Sample functions

vec3 to_cloud_space(vec3 worldPosition) {
	vec3 delta = worldPosition - genericData.data.cloud_frame_origin.xyz;
	float tangent = dot(delta, genericData.data.cloud_frame_around.xyz);
	float inward = dot(delta, genericData.data.cloud_frame_up.xyz);
	float ringRadius = max(genericData.data.cloud_frame_up.w, 1.0);
	// `tangent` alone is R*sin(theta), not the arc distance R*theta.
	// Reconstruct the ring-local polar coordinates from tangent and inward
	// displacement. The former parabolic approximation assigned distant and
	// opposite-side shell samples to an inverted local altitude, producing a
	// persistent vertically mirrored cloud volume on the Certified ring.
	float outwardRadius = ringRadius - inward;
	float localArc = ringRadius * atan(tangent, outwardRadius);
	float vertical = ringRadius - length(vec2(tangent, outwardRadius));
	return vec3(
		genericData.data.cloud_frame_around.w + localArc,
		vertical,
		dot(delta, genericData.data.cloud_frame_across.xyz) + genericData.data.cloud_frame_across.w);
}

#include "SunshineCloudsEntrySkip.glslinc"

float cloud_altitude(vec3 worldPosition) {
	return to_cloud_space(worldPosition).y;
}

float ring_side_wall_mask(float canonicalAcross) {
	float ringWidth = genericData.data.temporal_controls.y;
	if (ringWidth <= 1.0) return 1.0;
	// A kilometre feather avoids a hard vertical cutoff at the side walls.
	float feather = min(1000.0, max(50.0, ringWidth * 0.02));
	return smoothstep(0.0, feather, canonicalAcross)
		* (1.0 - smoothstep(ringWidth - feather, ringWidth, canonicalAcross));
}

// One localized storm system, laid out like the legacy single-system uniforms:
// system = (centre around, centre across, major radius, minor radius), shape = (orientation cos, sin, influence,
// shader profile), detail = (pillar radius, anvil radius multiplier, anvil start, physical CloudFormationProfile).
struct StormSystemParams {
	vec4 system;
	vec4 shape;
	vec4 detail;
};

float storm_system_density(StormSystemParams stormParams, vec3 cloudPosition, float normalizedHeight) {
	float influence = clamp(stormParams.shape.z, 0.0, 1.0);
	float profile = stormParams.shape.w;
	int physicalProfile = int(round(stormParams.detail.w));
	float majorRadius = stormParams.system.z;
	float minorRadius = stormParams.system.w;
	if (influence <= 0.001 || majorRadius <= 1.0 || minorRadius <= 1.0) {
		return 0.0;
	}

	float aroundDelta = cloudPosition.x - stormParams.system.x;
	float circumference = genericData.data.cloud_frame_up.w * 6.28318530718;
	if (circumference > 1.0) {
		aroundDelta = mod(aroundDelta + circumference * 0.5, circumference)
			- circumference * 0.5;
	}
	float acrossDelta = cloudPosition.z - stormParams.system.y;
	float orientationCosine = stormParams.shape.x;
	float orientationSine = stormParams.shape.y;
	vec2 systemPosition = vec2(
		orientationCosine * aroundDelta + orientationSine * acrossDelta,
		-orientationSine * aroundDelta + orientationCosine * acrossDelta);
	vec2 normalizedPosition = vec2(
		systemPosition.x / max(majorRadius, 1.0),
		systemPosition.y / max(minorRadius, 1.0));
	float normalizedSystemDistance = length(normalizedPosition);
	float systemMask = 1.0 - smoothstep(0.76, 1.0, normalizedSystemDistance);
	if (systemMask <= 0.001) {
		return 0.0;
	}
	float verticalEnvelope = smoothstep(0.015, 0.10, normalizedHeight)
		* (1.0 - smoothstep(0.90, 0.995, normalizedHeight));

	// Physical catalog IDs mirror CloudFormationProfile in
	// CloudMetaballMeshGenerator.cs. These are density-field silhouettes;
	// Sunshine remains the sole renderer and supplies all detail and lighting.
	if (physicalProfile == 4) { // Cirrus
		float streaks = 0.0;
		for (int streakIndex = 0; streakIndex < 4; streakIndex++) {
			float indexOffset = float(streakIndex) - 1.5;
			float center = indexOffset * 0.31
				+ sin(normalizedPosition.x * 5.5 + float(streakIndex) * 1.9) * 0.075;
			float distanceToStreak = abs(normalizedPosition.y - center);
			float streak = 1.0 - smoothstep(0.035, 0.105, distanceToStreak);
			float featherTaper = 1.0 - smoothstep(0.55, 1.0, abs(normalizedPosition.x));
			streaks = max(streaks, streak * featherTaper);
		}
		float thinBand = smoothstep(0.24, 0.42, normalizedHeight)
			* (1.0 - smoothstep(0.58, 0.78, normalizedHeight));
		return streaks * thinBand * systemMask * influence * 0.52;
	}

	if (physicalProfile == 5) { // Cirrostratus
		return systemMask * verticalEnvelope * influence * 0.38;
	}

	if (physicalProfile == 6) { // Cirrocumulus
		vec2 rippleGrid = vec2(
			normalizedPosition.x * 6.0,
			normalizedPosition.y * 7.5 + sin(normalizedPosition.x * 12.0) * 0.55);
		vec2 rippleCell = fract(rippleGrid + 0.5) - 0.5;
		float ripplePuff = 1.0 - smoothstep(0.18, 0.47, length(rippleCell));
		float rippleHeight = smoothstep(0.16, 0.38, normalizedHeight)
			* (1.0 - smoothstep(0.68, 0.90, normalizedHeight));
		return ripplePuff * rippleHeight * systemMask * influence * 0.62;
	}

	if (physicalProfile == 7) { // Altostratus
		float sheetUndulation = 0.86
			+ sin(normalizedPosition.x * 7.0 + normalizedPosition.y * 4.0) * 0.08;
		return systemMask * verticalEnvelope * sheetUndulation * influence * 0.72;
	}

	if (physicalProfile == 8) { // Altocumulus
		vec2 clusterGrid = normalizedPosition * vec2(4.2, 4.8);
		clusterGrid.y += sin(normalizedPosition.x * 8.0) * 0.42;
		vec2 clusterCell = fract(clusterGrid + 0.5) - 0.5;
		float clusteredPuff = 1.0 - smoothstep(0.20, 0.53, length(clusterCell));
		float clusterHeight = smoothstep(0.06, 0.30, normalizedHeight)
			* (1.0 - smoothstep(0.72, 0.96, normalizedHeight));
		return clusteredPuff * clusterHeight * systemMask * influence * 0.82;
	}

	if (physicalProfile == 9) { // Stratus
		float flatLayer = smoothstep(0.04, 0.22, normalizedHeight)
			* (1.0 - smoothstep(0.70, 0.94, normalizedHeight));
		return systemMask * flatLayer * influence * 0.78;
	}

	if (physicalProfile == 1) { // Stratocumulus
		float rollWave = cos(normalizedPosition.y * 13.0
			+ sin(normalizedPosition.x * 6.0) * 1.4) * 0.5 + 0.5;
		float rollingMasses = smoothstep(0.18, 0.66, rollWave);
		float rollHeight = smoothstep(0.03, 0.24, normalizedHeight)
			* (1.0 - smoothstep(0.72, 0.97, normalizedHeight));
		return rollingMasses * rollHeight * systemMask * influence * 0.90;
	}

	if (physicalProfile == 10) { // Nimbostratus
		float raggedBase = 0.08
			+ (sin(normalizedPosition.x * 9.0)
				+ sin(normalizedPosition.y * 11.0 + 1.7)) * 0.025;
		float rainDeck = smoothstep(raggedBase, raggedBase + 0.12, normalizedHeight)
			* (1.0 - smoothstep(0.86, 0.995, normalizedHeight));
		return systemMask * rainDeck * influence;
	}

	if (physicalProfile == 0) { // Cumulus
		float angleFromCore = atan(normalizedPosition.y, normalizedPosition.x);
		float bodyRadius = mix(0.72, 0.30, clamp(normalizedHeight, 0.0, 1.0));
		bodyRadius *= 0.92 + sin(angleFromCore * 7.0 + normalizedHeight * 11.0) * 0.10;
		float coreDistance = length(normalizedPosition);
		float cottonBody = 1.0 - smoothstep(bodyRadius * 0.72, bodyRadius, coreDistance);
		float flatBase = smoothstep(0.015, 0.10, normalizedHeight);
		float domedTop = 1.0 - smoothstep(0.72, 0.99, normalizedHeight);
		return cottonBody * flatBase * domedTop * systemMask * influence * 0.88;
	}

	// StormTower and Cumulonimbus preserve the existing pillar/anvil density
	// path, now selected by the physical profile ID carried by storm_detail.w.
	if (physicalProfile != 2 && physicalProfile != 3 && profile < 1.5) {
		return 0.0;
	}
	if (RING_STORM_FALLBACK == 0) {
		return 0.0;
	}
	float pillarRadius = max(1000.0, stormParams.detail.x);
	float anvilRadiusMultiplier = max(1.2, stormParams.detail.y);
	float anvilRadius = pillarRadius * anvilRadiusMultiplier;
	float cellWidth = max(pillarRadius * 3.5, anvilRadius * 2.35);
	vec2 warpedPosition = systemPosition;
	warpedPosition.x += sin(systemPosition.y / cellWidth * 6.28318530718 + 1.7)
		* pillarRadius * 0.42;
	warpedPosition.y += sin(systemPosition.x / cellWidth * 6.28318530718 + 4.1)
		* pillarRadius * 0.36;
	vec2 cellPosition = mod(warpedPosition + cellWidth * 0.5, cellWidth)
		- cellWidth * 0.5;
	float distanceFromCore = length(cellPosition);

	float pillar = 1.0 - smoothstep(pillarRadius * 0.58, pillarRadius, distanceFromCore);
	float pillarTop = profile >= 2.5 ? 0.91 : 0.84;
	float pillarVertical = smoothstep(0.015, 0.13, normalizedHeight)
		* (1.0 - smoothstep(pillarTop, 0.995, normalizedHeight));
	float sculptedDensity = pillar * pillarVertical;

	if (physicalProfile == 3 || profile >= 2.5) {
		float anvil = 1.0 - smoothstep(anvilRadius * 0.60, anvilRadius, distanceFromCore);
		float anvilStart = clamp(stormParams.detail.z, 0.5, 0.9);
		float anvilVertical = smoothstep(anvilStart, min(0.96, anvilStart + 0.075), normalizedHeight)
			* (1.0 - smoothstep(0.955, 1.0, normalizedHeight));
		sculptedDensity = max(sculptedDensity, anvil * anvilVertical * 0.94);
	}

	return sculptedDensity * systemMask * influence;
}

// Legacy single-system entry point: the uniforms describe the camera's system.
float localized_storm_density(vec3 cloudPosition, float normalizedHeight) {
	StormSystemParams stormParams = StormSystemParams(
		genericData.data.storm_system, genericData.data.storm_shape, genericData.data.storm_detail);
	return storm_system_density(stormParams, cloudPosition, normalizedHeight);
}

float sampleEffectorAdditive(vec3 worldPosition) {
	float effectorAdditive = 0.0;
	for (int i = 0; i < int(genericData.data.pointEffectorCount); i++) {
		float effectorDistance = distance(pointEffectors[i].position, worldPosition);
		if (effectorDistance < pointEffectors[i].radius){
			effectorAdditive += mix(pointEffectors[i].power, 0.0, effectorDistance / pointEffectors[i].radius);
		}
	}
	return effectorAdditive;
}

// Per-sample storm bookkeeping for the per-ray storm ambient: the storm and total density the last sampleScene call returned.
float g_last_storm_density = 0.0;
float g_last_total_density = 0.0;

float sampleScene(
	vec3 largeNoisePos, 
	vec3 mediumNoisePos, 
	vec3 smallNoisePos, 
	vec3 worldPosition, 
	float cloudceiling, 
	float cloudfloor, 
	float extralargeNoiseValue,
	float largenoisescale, 
	float mediumnoisescale, 
	float smallnoisescale, 
	float coverage, 
	float smallscalePower, 
	float curlPower, 
	float lod, 
	bool ambientsample,
	bool coarseSunLighting)
	{
	g_last_storm_density = 0.0;
	g_last_total_density = 0.0;
	worldPosition = to_cloud_space(worldPosition);
	vec3 canonicalCloudPosition = worldPosition;
	float wallMask = ring_side_wall_mask(canonicalCloudPosition.z);
	if (wallMask == 0.0) {
		return 0.0;
	}
	float backgroundFloor = genericData.data.background_altitude.x;
	float backgroundCeiling = genericData.data.background_altitude.y;
	if (backgroundCeiling <= backgroundFloor + 1.0) {
		backgroundFloor = cloudfloor;
		backgroundCeiling = cloudceiling;
	}
	float clampedWorldHeight = remap(
		worldPosition.y,
		backgroundFloor,
		backgroundCeiling,
		0.0,
		1.0);
	float edgeFade = min(smoothstep(0.0, 0.1, clampedWorldHeight), smoothstep(1.0, 0.9, clampedWorldHeight));
	float stormFloor = genericData.data.storm_altitude.x;
	float stormCeiling = genericData.data.storm_altitude.y;
	if (stormCeiling <= stormFloor + 1.0) {
		stormFloor = backgroundFloor;
		stormCeiling = backgroundCeiling;
	}
	float stormHeight = remap(
		canonicalCloudPosition.y,
		stormFloor,
		stormCeiling,
		0.0,
		1.0);
	float stormAltitudeMask = min(
		smoothstep(0.0, 0.06, stormHeight),
		smoothstep(1.0, 0.94, stormHeight));
	// Camera-independent clouds sum every masked sculpt system (each with its own storm band); the legacy
	// single-system uniforms above are then unused.
	bool cameraIndependent = ring_ws_enabled();
	float stormDensity = 0.0;
	if (cameraIndependent) {
		stormDensity = ring_ws_sculpt_density(canonicalCloudPosition, backgroundFloor, backgroundCeiling);
	} else if (stormAltitudeMask != 0.0) {
		stormDensity = localized_storm_density(canonicalCloudPosition, stormHeight)
			* stormAltitudeMask;
	}
	// S6 cloud layers: one call site (the layer function is large; each sampleScene copy inlines it once).
	float layered = 0.0;
	if (g_layers_on) {
		// The storm level lifts the layers only where a real storm owns the column (S6 3.8); non-storm decks close by weatherness instead.
		// With the layers on, ring_ws_storm_level() is the primary's owned storm level (S6 3.8), set after its macro update.
		layered = ring_layers_background(canonicalCloudPosition, max(stormDensity, ring_ws_storm_level()), extralargeNoiseValue,
			largeNoisePos, mediumNoisePos, smallNoisePos, largenoisescale, mediumnoisescale, smallnoisescale,
			smallscalePower, curlPower, lod, ambientsample, coarseSunLighting);
	}
	if (g_layers_on && stormDensity <= 0.0) {
		// No storm sculpt here: the altitude-dispatched low / mid layers are the whole density (no single-band fetches).
		g_last_total_density = clamp(layered, 0.0, 1.0) * wallMask;
		return g_last_total_density;
	}
	if (edgeFade == 0.0 && stormDensity == 0.0) {
		return 0.0;
	}

	vec4 gradientSample = texture(heightmask, vec2(clampedWorldHeight, 0.5)).rgba;
	float extraLargeShape = extralargeNoiseValue * gradientSample.b;

	float curlHeightSample = (1.0 - gradientSample.a);

	float effectorAdditive = 0.0;
	vec2 WindDirection = genericData.data.WindDirection;
	worldPosition += vec3(WindDirection.x, 0.0, WindDirection.y) * genericData.data.windSweptPower * quadraticIn(1.0 - clamp(clampedWorldHeight / genericData.data.windSweptRange, 0.0, 1.0));

	if (lod > 0.0){
		effectorAdditive = sampleEffectorAdditive(worldPosition) * edgeFade;

		if (!ambientsample && curlHeightSample > 0.0 && min(curlPower, lod) > 0.5){
			
			float curlLod = remap(lod, 0.5, 1.0, 0.0, 1.0);
			worldPosition += (((texture(curl_noise, (worldPosition - mediumNoisePos) / mediumnoisescale).xyz * 2.0) - 1.0) * vec3(1.0, 0.2, 1.0) + vec3(WindDirection.x, 0.0, WindDirection.y) * 0.9) * curlPower * curlHeightSample * curlLod;
			worldPosition += (((texture(curl_noise, (worldPosition - mediumNoisePos) / mediumnoisescale).xyz * 2.0) - 1.0) * vec3(1.0, 0.2, 1.0) + vec3(WindDirection.x, 0.0, WindDirection.y) * 0.9) * curlPower * curlHeightSample * curlLod;
			worldPosition += (((texture(curl_noise, (worldPosition - mediumNoisePos) / mediumnoisescale).xyz * 2.0) - 1.0) * vec3(1.0, 0.2, 1.0) + vec3(WindDirection.x, 0.0, WindDirection.y) * 0.9) * curlPower * curlHeightSample * curlLod;
			
			clampedWorldHeight = remap(
				worldPosition.y,
				backgroundFloor,
				backgroundCeiling,
				0.0,
				1.0);
			gradientSample = texture(heightmask, vec2(clampedWorldHeight, 0.5)).rgba;
		}
	}

	// With the S6 layers on (and storm sculpt here) the background comes from `layered` above; this single-band block then only
	// supplies the storm's own warped noise positions.
	float largeShape = 0.0;
	float weatherGate = 1.0;
	if (!g_layers_on) {
	largeShape = texture(large_noise, (worldPosition - largeNoisePos) / largenoisescale).r * extraLargeShape;
	// Background uses shared physical weather. Localized storm volume remains
	// independently dense, so a strong front cannot be erased by a clear gate.
	float weatherStorm = max(stormDensity, genericData.data.cloud_handoff.z);
	vec4 weatherFrame = genericData.data.cloud_weather_frame;
	if (cameraIndependent) {
		// Storm level and base coverage come from the primary sample's macro weather (ring_ws_update_macro).
		weatherStorm = max(stormDensity, ring_ws_storm_level());
		weatherFrame.w = ring_ws_frame_coverage();
	}
	float weatherCoverage = ring_cloud_weather_resolved_coverage(
		weatherFrame.w, weatherStorm, canonicalCloudPosition,
		genericData.data.cloud_weather_params, weatherFrame);
	weatherGate = ring_cloud_weather_clear_gate(weatherCoverage, weatherStorm,
		genericData.data.cloud_weather_params);
	float weatherBackgroundThreshold = genericData.data.cloud_weather_params.y > 0.0
		? mix(mix(0.70, 0.90, weatherCoverage), 1.05, smoothstep(0.35, 0.80, weatherStorm)) : coverage;
	largeShape = smoothstep(1.0 - weatherBackgroundThreshold,
		1.1 - weatherBackgroundThreshold, largeShape * gradientSample.r)
		+ max(effectorAdditive, 0.0);
	if ((largeShape <= 0.0 || weatherGate <= 0.0) && stormDensity <= 0.0) {
		return 0.0;
	}
	}
	// Megastorm strip columns erode with moving-frame noise (docs/megastorm-design.md 3.2): the medium and small detail noise travel
	// with the wall (offsets along around, wrapped by each noise scale), so its shape does not slide over static texture. Without a strip
	// column the coordinates are the original expressions exactly (no zero term enters the arithmetic, so the kernel is bit-identical).
	vec3 mediumCoord = worldPosition - mediumNoisePos;
	vec3 smallCoord = canonicalCloudPosition - smallNoisePos;
	if (RING_MEGASTORM_ENABLED && g_storm_strip) {
		mediumCoord.x -= genericData.data.ring_megastorm_noise.y;
		smallCoord.x -= genericData.data.ring_megastorm_noise.z;
	}
	vec4 mediumShapes = texture(noise_medium, mediumCoord / mediumnoisescale).rgba;
	float mediumshape = 1.0 - mediumShapes.b;
	// Only directional sun transmittance may omit fine erosion. Keep the
	// original pre-warp coordinates when deferring this texture read.
	float smallRaw = coarseSunLighting ? 0.0 : texture(noise_small, smallCoord / smallnoisescale).r;
	float smallShape = smallRaw * gradientSample.g * pow((1.0 - mediumshape), smallscalePower);
	

	float backgroundDensity = 0.0;
	if (g_layers_on) {
		backgroundDensity = layered;
	} else if (largeShape > 0.0) {
		float shape = mediumshape + max(effectorAdditive, 0.0);
		shape = clamp(remap(shape, 1.0 - largeShape, 1.0, 0.0, 1.0), 0.0, 1.0);
		shape = clamp(remap(shape, smallShape, 1.0, 0.0, 1.0), 0.0, 1.0);
		shape += min(effectorAdditive, 0.0);
		backgroundDensity = clamp(shape * edgeFade, 0.0, 1.0) * weatherGate;
	}
	// At exactly zero large shape, the original remap has equal input bounds
	// and can produce NaN. This branch is mathematically empty background.
	if (cameraIndependent && g_storm_map_density > 0.0) {
		// Baked storm columns: profile-driven erosion (billowy tops and sides, smooth underside); the other sculpt profiles keep HEAD's modulation.
		stormDensity = max(g_storm_other * mix(0.62, 1.0, mediumShapes.r), ring_storm_eroded(mediumshape, smallRaw));
	} else {
		stormDensity *= mix(0.62, 1.0, mediumShapes.r);
	}

	g_last_storm_density = clamp(stormDensity, 0.0, 1.0) * wallMask;
	g_last_total_density = clamp(max(backgroundDensity, stormDensity), 0.0, 1.0) * wallMask;
	return g_last_total_density;
}

float sampleSceneCoarse(
	vec3 largeNoisePos, 
	vec3 worldPosition, 
	float cloudceiling, 
	float cloudfloor, 
	float extralargeNoiseValue,
	float largenoisescale, 
	float coverage,
	float lod)
	{
	float clampedWorldHeight = remap(worldPosition.y, cloudfloor, cloudceiling, 0.0, 1.0);
	vec4 gradientSample = texture(heightmask, vec2(clampedWorldHeight, 0.5)).rgba;

	float edgeFade = min(smoothstep(0.0, 0.1, clampedWorldHeight), smoothstep(1.0, 0.9, clampedWorldHeight));
	float extraLargeShape = extralargeNoiseValue * gradientSample.b;

	float effectorAdditive = 0.0;
	vec2 WindDirection = genericData.data.WindDirection;
	worldPosition += vec3(WindDirection.x, 0.0, WindDirection.y) * genericData.data.windSweptPower * quadraticIn(1.0 - clamp(clampedWorldHeight / genericData.data.windSweptRange, 0.0, 1.0));

	if (lod > 0.0){
		effectorAdditive = sampleEffectorAdditive(worldPosition) * edgeFade;
	}

	float largeShape = texture(large_noise, (worldPosition - largeNoisePos) / largenoisescale).r * extraLargeShape;
	largeShape = smoothstep(coverage , coverage - 0.1, 1.0 - (largeShape * gradientSample.r)) + max(effectorAdditive, 0.0);

	float shape = largeShape + effectorAdditive;
	return clamp((shape * edgeFade), 0.0, 1.0);
}

// Camera-independent clouds: per-system sculpt density. Natural systems are evaluated in source space (the sample is shifted
// by the wind frame offsets, like the weather noise), manual systems in world canonical space. Each system keeps its own
// storm band; a system without one (storm ceiling <= floor + 1) sculpts inside the background band.
float ring_ws_sculpt_density(vec3 canonical, float backgroundFloor, float backgroundCeiling) {
	float influence = clamp(ringWeatherSystems.header1.y, 0.0, 1.0);
	if (influence <= 0.001) return 0.0;
	vec3 detail = genericData.data.storm_detail.xyz;
	vec2 shift = vec2(genericData.data.cloud_weather_frame.y, genericData.data.cloud_weather_frame.z);
	float result = 0.0;
	// Storm towers and cumulonimbus come from the baked profile map (fetched once per primary sample); only the other sculpt
	// profiles, or every profile while the map is invalid, run the analytic loop below.
	bool stormMap = ring_storm_map_valid();
	float other = 0.0;
	g_storm_map_density = 0.0;
	// The fetched column (storm map and / or megastorm strip; zero when neither is in reach).
	if (stormMap || ring_megastorm_strip_valid()) result = ring_storm_map_density(canonical.y);
	for (int word = 0; word < 2; word++) {
		// The per-primary sub-mask: only the masked sculpt systems whose ellipse (plus light reach) covers the primary sample.
		uint bits = word == 0 ? g_ws_sub_mask.x : g_ws_sub_mask.y;
		while (bits != 0u) {
			int index = word * 32 + findLSB(bits);
			bits &= bits - 1u;
			if (stormMap && ring_storm_is_storm_profile(index)) continue;
			vec4 c = ringWeatherSystems.systems[index].c;
			float stormFloor = c.x;
			float stormCeiling = c.y;
			if (stormCeiling <= stormFloor + 1.0) {
				stormFloor = backgroundFloor;
				stormCeiling = backgroundCeiling;
			}
			float stormHeight = remap(canonical.y, stormFloor, stormCeiling, 0.0, 1.0);
			float altitudeMask = min(smoothstep(0.0, 0.06, stormHeight), smoothstep(1.0, 0.94, stormHeight));
			if (altitudeMask == 0.0) continue;
			vec4 b = ringWeatherSystems.systems[index].b;
			StormSystemParams systemParams = StormSystemParams(
				ringWeatherSystems.systems[index].a,
				vec4(b.x, b.y, influence, ring_ws_shader_profile(int(round(c.z)))),
				vec4(detail.x, detail.y, detail.z, c.z));
			vec3 position = canonical;
			if ((ring_ws_flags(index) & RING_WS_FLAG_STATIC) == 0u) position.xz += shift;
			float systemDensity = storm_system_density(systemParams, position, stormHeight) * altitudeMask * ringWeatherSystems.systems[index].d.z;
			other = max(other, systemDensity);
			result = max(result, systemDensity);
		}
	}
	g_storm_other = other;
	return result;
}

// Camera-independent clouds: clip the ray to the envelope (reusing the entry-skip quadratic: altitude >= envelope floor),
// then mask the systems whose support reaches the canonical origin -> mid -> end polyline. The ring's curvature moves the
// true canonical path at most L^2 / (32 R) off this polyline (about 120 m for a 50 km ray at the certified radius), far
// inside the margin. Unresolvable geometry keeps the whole ray (conservative).
uvec2 ring_ws_ray_mask(vec3 rayOrigin, vec3 rayDirection, float rayEnd, float margin, float envelopeFloor, float clipSlack, bool sculptOnly) {
	float t0 = 0.0;
	float t1 = max(rayEnd, 0.0);
	float ringRadius = genericData.data.cloud_frame_up.w;
	if (ringRadius > 1.0) {
		vec3 origin = rayOrigin - genericData.data.cloud_frame_origin.xyz;
		float tangent = dot(origin, genericData.data.cloud_frame_around.xyz);
		float height = dot(origin, genericData.data.cloud_frame_up.xyz);
		float da = dot(rayDirection, genericData.data.cloud_frame_around.xyz);
		float du = dot(rayDirection, genericData.data.cloud_frame_up.xyz);
		float a = da * da + du * du;
		if (a > 1e-10) {
			float b = tangent * da - (ringRadius - height) * du;
			float c = tangent * tangent + (envelopeFloor - height) * (2.0 * ringRadius - height - envelopeFloor);
			float disc = b * b - a * c;
			if (disc > 1e-8) {
				float rootDisc = sqrt(disc);
				float enter = (-b - rootDisc) / a;
				float leave = (-b + rootDisc) / a;
				// Samples exist only where altitude >= envelope floor, i.e. between the two roots.
				if (leave + clipSlack < 0.0 || enter - clipSlack > t1) return uvec2(0u);
				t0 = max(0.0, enter - clipSlack);
				t1 = min(t1, leave + clipSlack);
			}
		}
	}
	vec3 p0 = to_cloud_space(rayOrigin + rayDirection * t0);
	vec3 p1 = to_cloud_space(rayOrigin + rayDirection * (0.5 * (t0 + t1)));
	vec3 p2 = to_cloud_space(rayOrigin + rayDirection * t1);
	return ring_ws_mask(p0, p1, p2, margin, sculptOnly);
}

// Camera-independent clouds: refine the ray's sculpt systems with their ELLIPSES (the sculpt density is exactly zero outside the unit
// ellipse; ring_ws_ray_mask above only knows support circles). Walks an 8-segment cloud-space polyline of the ray [0, rayEnd] (each
// segment is at most a few tens of km, so the ring's curvature sag stays within ~150 m of the true curve at the certified radius) and
//  - drops from g_ws_sculpt_mask every system whose ellipse stays farther than `reachMargin` (light/ambient reach of any primary
//    sample, converted to the ellipse frame as reachMargin / min(major, minor)) from the polyline;
//  - for the kept systems with an own storm band, records the ray-distance interval [t0, t1] where the polyline is within
//    `intervalSlack` metres of the ellipse (a superset of where a primary sample can be inside it) together with that band, in
//    g_ws_ray_band (at most RING_WS_RAY_BANDS slots; extra systems merge into the last slot, i.e. widen it conservatively).
// A primary sample outside the background band and outside every active interval band is exactly empty.
void ring_ws_build_ray_sculpt(vec3 rayOrigin, vec3 rayDirection, float rayEnd, float reachMargin, float intervalSlack,
	vec2 backgroundBand, bool buildIntervals) {
	g_ws_ray_band_count = 0;
	if (g_ws_sculpt_mask == uvec2(0u)) return;
	const int SEGMENTS = 8;
	float dt = max(rayEnd, 1.0) / float(SEGMENTS);
	vec2 points[SEGMENTS + 1];
	for (int k = 0; k <= SEGMENTS; k++) {
		vec3 c = to_cloud_space(rayOrigin + rayDirection * (dt * float(k)));
		points[k] = vec2(c.x, c.z);
	}
	float circumference = ring_ws_circumference();
	vec2 shift = vec2(genericData.data.cloud_weather_frame.y, genericData.data.cloud_weather_frame.z);
	uvec2 kept = uvec2(0u);
	for (int word = 0; word < 2; word++) {
		uint bits = word == 0 ? g_ws_sculpt_mask.x : g_ws_sculpt_mask.y;
		while (bits != 0u) {
			int index = word * 32 + findLSB(bits);
			bits &= bits - 1u;
			vec4 a = ringWeatherSystems.systems[index].a;
			vec4 c = ringWeatherSystems.systems[index].c;
			vec2 offset = (ring_ws_flags(index) & RING_WS_FLAG_STATIC) != 0u ? vec2(0.0) : shift;
			float minRadius = max(1.0, min(a.z, a.w));
			float reachLimit = RING_STORM_SUPPORT + reachMargin / minRadius;
			float sliceLimit = RING_STORM_SUPPORT + intervalSlack / minRadius;
			bool hasBand = buildIntervals && c.y > c.x + 1.0;
			// Unwrapped centre-relative offsets: wrap the first point once, then accumulate wrapped differences.
			float deltaAround = ring_ws_wrap(points[0].x + offset.x - a.x, circumference);
			vec2 previous = ring_ws_ellipse_normalize(index, vec2(deltaAround, points[0].y + offset.y - a.y));
			float nearest2 = 1e30;
			float tLow = 1e30;
			float tHigh = -1e30;
			for (int k = 1; k <= SEGMENTS; k++) {
				deltaAround += ring_ws_wrap(points[k].x - points[k - 1].x, circumference);
				vec2 current = ring_ws_ellipse_normalize(index, vec2(deltaAround, points[k].y + offset.y - a.y));
				nearest2 = min(nearest2, ring_ws_segment_distance2(previous, current));
				if (hasBand) {
					// |previous + (current - previous) u|^2 <= sliceLimit^2 for u in [0, 1].
					vec2 direction = current - previous;
					float qa = dot(direction, direction);
					float qb = dot(previous, direction);
					float qc = dot(previous, previous) - sliceLimit * sliceLimit;
					float uLow = 2.0;
					float uHigh = -1.0;
					if (qa < 1e-12) {
						if (qc <= 0.0) { uLow = 0.0; uHigh = 1.0; }
					} else {
						float disc = qb * qb - qa * qc;
						if (disc >= 0.0) {
							float root = sqrt(disc);
							uLow = max((-qb - root) / qa, 0.0);
							uHigh = min((-qb + root) / qa, 1.0);
						}
					}
					if (uLow <= uHigh) {
						tLow = min(tLow, dt * (float(k - 1) + uLow));
						tHigh = max(tHigh, dt * (float(k - 1) + uHigh));
					}
				}
				previous = current;
			}
			if (nearest2 > reachLimit * reachLimit) continue;
			kept |= ring_ws_bit(index);
			if (hasBand && tLow <= tHigh) {
				vec4 slot = vec4(tLow, tHigh, c.x, c.y);
				if (g_ws_ray_band_count < RING_WS_RAY_BANDS) {
					g_ws_ray_band[g_ws_ray_band_count] = slot;
					g_ws_ray_band_count++;
				} else {
					vec4 last = g_ws_ray_band[RING_WS_RAY_BANDS - 1];
					g_ws_ray_band[RING_WS_RAY_BANDS - 1] = vec4(min(last.x, slot.x), max(last.y, slot.y), min(last.z, slot.z), max(last.w, slot.w));
				}
			}
		}
	}
	g_ws_sculpt_mask = kept;
}

// In-loop gap skip (rule 2): implemented and measured, off by default. It removes only empty iterations, but the extra inlined skip
// (one quadratic) costs +0.09 ms (one call) to +0.2 ms (boundary list) of march registers at 40 km, more than the gap steps cost.
#ifndef RING_WS_GAP_SKIP
#define RING_WS_GAP_SKIP 0
#endif

// Analytic curved-cylinder altitude derivative along a ray at `position` (per metre of ray length). At Certified radii subtracting
// two R-length altitude samples quantizes near a tangent and can falsely reverse a ray.
float cloud_altitude_motion(vec3 position, vec3 direction) {
	vec3 radialDelta = position - genericData.data.cloud_frame_origin.xyz;
	float tangent = dot(radialDelta, genericData.data.cloud_frame_around.xyz);
	float inward = dot(radialDelta, genericData.data.cloud_frame_up.xyz);
	float radialLength = max(length(vec2(tangent, genericData.data.cloud_frame_up.w - inward)), 1.0);
	return ((genericData.data.cloud_frame_up.w - inward)
		* dot(direction, genericData.data.cloud_frame_up.xyz)
		- tangent * dot(direction, genericData.data.cloud_frame_around.xyz)) / radialLength;
}

// Whole maxstep lattice intervals the march may skip from ray distance `fromDistance` (S6 section 3.3, rule 2 gap skip and the
// pre-entry skip): the minimum over every entry of U of its own skip, i.e. the background band plus each sculpt interval that has not
// ended. The result never passes the next boundary of U (an altitude crossing or an interval start), so it only removes empty
// iterations; the jitter lattice (t_k = fromDistance + k * maxStep) is untouched. `origin` is relative to the cloud frame origin.
int ring_ws_boundary_skip(vec3 origin, vec3 direction, vec2 backgroundBand, float fromDistance, float maxStep, int stepCountRemaining, float depthLimit) {
	vec3 around = genericData.data.cloud_frame_around.xyz;
	vec3 up = genericData.data.cloud_frame_up.xyz;
	float radius = genericData.data.cloud_frame_up.w;
	int skip = cloud_entry_skip_steps(origin, direction, around, up, radius, backgroundBand.x, backgroundBand.y,
		fromDistance, maxStep, stepCountRemaining, depthLimit);
	for (int k = 0; k < RING_WS_RAY_BANDS; k++) {
		if (k >= g_ws_ray_band_count || skip <= 0) break;
		vec4 slot = g_ws_ray_band[k];
		if (slot.y < fromDistance) continue; // the interval is behind the ray
		skip = min(skip, cloud_band_skip_steps(origin, direction, around, up, radius, slot.z, slot.w, slot.x,
			fromDistance, maxStep, stepCountRemaining, depthLimit));
	}
	return skip;
}

float sampleLighting(
	int stepCount, 
	vec3 worldPosition,
	vec3 extralargeNoisePos, 
	vec3 largeNoisePos, 
	vec3 mediumNoisePos, 
	vec3 smallNoisePos, 
	vec3 sunDirection,
	float densityMultiplier,
	float sunUpWeight, 
	float stepDistance,  
	float cloudceiling, 
	float cloudfloor, 
	float extralargenoisescale,
	float largenoisescale, 
	float mediumnoisescale, 
	float smallnoisescale, 
	float coverage, 
	float smallscalePower, 
	float curlPower, 
	float lod,
	bool coarseSunLighting)
	{
	// Storm light shortcut: a closed-form optical depth through the storm column the sun ray crosses, plus (for samples that are storm
	// cloud themselves) only two noise steps. Only the directional sun march (coarseSunLighting) takes it.
	float stormTau = 0.0;
	if (ring_ws_enabled() && coarseSunLighting && g_storm_primary_active) {
		stormTau = ring_storm_sun_tau(to_cloud_space(worldPosition), sunDirection);
		if (g_storm_primary_dominant) stepCount = min(stepCount, 2);
	}
	float density = 0.0;
	float stepCountFloat = max(float(stepCount) * lod, 2.0);
	float actualDistance = mix(stepDistance * 4.0, stepDistance, lod);
	// S6 layers: the sun ray's part above the march's reach, through the layers above this sample, as a closed-form transmittance
	// (g_layer_sun, multiplied into the caller's Beer term).
	bool layerLight = ring_ws_enabled() && g_layers_on;
	g_layer_sun = 1.0;
	if (layerLight && coarseSunLighting) {
		float sunElevation = cloud_altitude_motion(worldPosition, sunDirection);
		g_layer_sun = ring_layers_sun_above(to_cloud_space(worldPosition).y, sunElevation, actualDistance);
		g_layer_shade = max(g_layer_shade, 1.0 - g_layer_sun);
	}
	float eachShortStep = actualDistance / (float(stepCount) / stepCountFloat) / stepCountFloat;
	float traveledDistance = 0.0;
	
	float sunUpValue = 1.0 - sunUpWeight;
	// S6: a layer-only primary's sun march takes every lightStride-th step of the same lattice; the steps taken share the weight the
	// full lattice carries (ceil(n) / n), so a stride coarsens the sampling but keeps the expected optical depth.
	float lightStride = coarseSunLighting ? g_layer_light_stride : 1.0;
	float eachStepWeight = ceil(stepCountFloat) / (stepCountFloat * ceil(stepCountFloat / lightStride));

	float heightGradient = 0.0;
	float thisDensity = 0.0;
	float count = 0.0;
	vec3 curPos = worldPosition;
	// Camera-independent clouds march the per-ray band but shade with the background band's height gradient.
	float gradientFloor = cloudfloor;
	float gradientCeiling = cloudceiling;
	float breakFloor = cloudfloor;
	float breakCeiling = cloudceiling;
	bool ciLight = ring_ws_enabled();
	if (ciLight) {
		gradientFloor = genericData.data.background_altitude.x;
		gradientCeiling = genericData.data.background_altitude.y;
		if (gradientCeiling <= gradientFloor + 1.0) {
			gradientFloor = cloudfloor;
			gradientCeiling = cloudceiling;
		}
		// The march breaks on the background band widened only by the sculpt systems that can reach this primary sample's light
		// samples (g_ws_sub_band), not by the ray-wide band.
		breakFloor = g_ws_sub_band.x;
		breakCeiling = g_ws_sub_band.y;
		if ((ring_ws_debug() & 16) != 0) { // debug: break on the background band, not the widened band
			breakFloor = gradientFloor;
			breakCeiling = gradientCeiling;
		}
	}
	// With sculpt systems in reach, samples inside the break band can still lie outside every band that holds density.
	// Map storms are not in the sub-mask; their fetched column (ring_storm_column_contains) is their extent, so the test runs always.
	bool ciLightSkipEmpty = ciLight;
	for (float i = 0.0; i < stepCountFloat; i += lightStride) {
		traveledDistance = mix(eachShortStep, actualDistance, clamp(quadraticOut(i / stepCountFloat), 0.0, 1.0));
		curPos = worldPosition + sunDirection * traveledDistance;
		vec3 cloudPos = to_cloud_space(curPos);

		if (density < 1.0 && clamp(cloudPos.y, breakFloor, breakCeiling) == cloudPos.y){
			bool layerEmpty = false;
			vec2 gradientBand = vec2(gradientFloor, gradientCeiling);
			if (layerLight) {
				// Layers on: a sample inside a layer shades by that layer's own band (the sun-up height hack); storm samples keep the
				// legacy 2-6 km band so the storm look is unchanged; clear air between or above the layers adds nothing.
				vec3 layerBand = ring_layers_light_band(cloudPos.y);
				bool stormHere = ring_ws_density_possible(cloudPos.y, vec2(1e30, -1e30)) || ring_storm_column_contains(cloudPos.y);
				if (layerBand.z > 0.5 && !stormHere) gradientBand = layerBand.xy;
				layerEmpty = layerBand.z < 0.5 && !stormHere;
			}
			heightGradient = remap(cloudPos.y, gradientBand.x, gradientBand.y, 0.0, 1.0);
			
			heightGradient = clamp(smoothstep(sunUpValue - 0.1, sunUpValue, heightGradient), 0.0, 1.0);
			if (layerEmpty) {
				// Exactly empty clear air (no layer, no storm band): adds no density.
				thisDensity = 0.0;
				heightGradient = 1.0;
			} else if (ciLightSkipEmpty && !layerLight && !ring_ws_density_possible(cloudPos.y, vec2(gradientFloor, gradientCeiling)) && !ring_storm_column_contains(cloudPos.y)) {
				// Exactly empty (see ring_ws_density_possible): sampleScene would return 0.
				thisDensity = 0.0;
			} else {
			float extraLargeShape = texture(extra_large_noise, (cloudPos.xz - extralargeNoisePos.xz) / extralargenoisescale).a;

			thisDensity = sampleScene(largeNoisePos, mediumNoisePos, smallNoisePos, curPos, cloudceiling, cloudfloor, extraLargeShape, largenoisescale, mediumnoisescale, smallnoisescale, coverage, smallscalePower, curlPower, lod, true, coarseSunLighting) * densityMultiplier * eachStepWeight;
			}
			// if (thisDensity <= 0.0){
			// 	break;
			// }
			density += mix(1.0, thisDensity, heightGradient);
		}
		else{
			break;
		}
	}

	return density + stormTau / max(stepDistance, 1.0);
}

float sampleAO(
	vec3 extralargeNoisePos,
	vec3 largeNoisePos, 
	vec3 mediumNoisePos, 
	vec3 smallNoisePos, 
	vec3 worldPosition, 
	float lightingSampleRange, 
	float cloudceiling, 
	float cloudfloor,
	float extralargenoisescale,
	float largenoisescale, 
	float mediumnoisescale, 
	float smallnoisescale, 
	float coverage, 
	float smallscalePower, 
	float curlPower, 
	float lod)
	{
	vec3 samplePos = worldPosition;
	samplePos.y += lightingSampleRange * 0.5;
	samplePos.y += lightingSampleRange * (rand(samplePos.xz) * 2.0 - 1.0);
	samplePos.x += lightingSampleRange * (rand(samplePos.zy) * 2.0 - 1.0);
	samplePos.z += lightingSampleRange * (rand(samplePos.yx) * 2.0 - 1.0);

	float extraLargeShape = texture(extra_large_noise, (samplePos.xz - extralargeNoisePos.xz) / extralargenoisescale).a;
	return sampleScene(largeNoisePos, mediumNoisePos, smallNoisePos, samplePos, cloudceiling, cloudfloor, extraLargeShape, largenoisescale, mediumnoisescale, smallnoisescale, coverage, smallscalePower, curlPower, lod, true, false);
}

void sampleAtmospherics(
	vec3 curPos, 
	float atmosphericHeight, 
	float distanceTraveled,
	float Rayleighscaleheight, 
	float Miescaleheight, 
	vec3 RayleighScatteringCoef, 
	float MieScatteringCoef, 
	float atmosphericDensity, 
	float density, 
	inout vec3 totalRlh, 
	inout vec3 totalMie, 
	inout float iOdRlh, 
	inout float iOdMie)
	{
	float iHeight = curPos.y / atmosphericHeight;
	float odStepRlh = exp(-iHeight / Rayleighscaleheight) * distanceTraveled;
	float odStepMie = exp(-iHeight / Miescaleheight) * distanceTraveled;
	iOdRlh += odStepRlh;
	iOdMie += odStepMie;

	vec3 attn = exp(-(MieScatteringCoef * (iOdMie + Miescaleheight) + RayleighScatteringCoef * (iOdRlh + Rayleighscaleheight))) * atmosphericDensity * (1.0 - clamp(iHeight, 0.0, 1.0));
	totalRlh += odStepRlh * attn * (1.0 - density);
	totalMie += odStepMie * attn * (1.0 - density);
}


vec4 sampleAllAtmospherics(
	vec3 worldPos, 
	vec3 rayDirection,
	float linear_depth,
	float highestDensityDistance,
	float density,
	float stepDistance,
	float stepCount,
	float atmosphericDensity, 
	vec3 sunDirection, 
	vec3 sunlightColor, 
	vec3 ambientLight)
	{
	vec3 totalRlh = vec3(0,0,0);
    vec3 totalMie = vec3(0,0,0);
	float iOdRlh = 0.0;
    float iOdMie = 0.0;
	// float odStepRlh = 0.0;
	// float odStepMie = 0.0;

	const float atmosphericHeight = 40000.0;
	const vec3 RayleighScatteringCoef = vec3(5.5e-6, 13.0e-6, 22.4e-6);
	const float Rayleighscaleheight = 8e3;
	const float MieScatteringCoef = 21e-6;
	const float Miescaleheight = 1.2e3;
	const float MieprefferedDirection = 0.758;

	// Calculate the Rayleigh and Mie phases.
    float mu = dot(rayDirection, sunDirection);
    float mumu = mu * mu;
    float gg = MieprefferedDirection * MieprefferedDirection;
    float pRlh = 3.0 / (16.0 * PI) * (1.0 + mumu);
    float pMie = 3.0 / (8.0 * PI) * ((1.0 - gg) * (mumu + 1.0)) / (pow(1.0 + gg - 2.0 * mu * MieprefferedDirection, 1.5) * (2.0 + gg));

	vec3 curPos = vec3(0.0);
	float traveledDistance = 0.0;
	//bool sampledDistanceAtmo = false;
	float currentWeight = 0.0;
	float sampleCount = 0.0;

	for (float i = 0.0; i < stepCount; i++) {
		traveledDistance = stepDistance * (i + 1);
		
		currentWeight = density * (1.0 - (highestDensityDistance - traveledDistance) / stepDistance);

		if (traveledDistance > linear_depth || currentWeight >= 1.0){
			traveledDistance = traveledDistance - stepDistance;
			currentWeight = 1.0 - clamp((linear_depth - traveledDistance) / stepDistance, 0.0, 1.0);
			sampleAtmospherics(curPos, atmosphericHeight, stepDistance, Rayleighscaleheight, Miescaleheight, RayleighScatteringCoef, MieScatteringCoef, atmosphericDensity, currentWeight, totalRlh, totalMie, iOdRlh, iOdMie); 
			break;
		}
		sampleCount += 1.0;
		
		curPos = worldPos + rayDirection * traveledDistance;
		
		sampleAtmospherics(curPos, atmosphericHeight, stepDistance, Rayleighscaleheight, Miescaleheight, RayleighScatteringCoef, MieScatteringCoef, atmosphericDensity, currentWeight, totalRlh, totalMie, iOdRlh, iOdMie); 
	}

	// pRlh *= (1.0 - lightingWeight);
	// pMie *= (1.0 - lightingWeight);

	float AtmosphericsDistancePower = length(vec3(RayleighScatteringCoef * totalRlh + MieScatteringCoef * totalMie));
	vec3 atmospherics = 22.0 * (ambientLight * RayleighScatteringCoef * totalRlh + pMie * MieScatteringCoef * sunlightColor * totalMie) / sampleCount;
	return vec4(atmospherics, AtmosphericsDistancePower);
}
// Keep normal temporal smoothing while reprojected cloud coverage agrees with
// this ray march. Ray-start jitter routinely changes integrated density by
// more than 0.08 at soft cloud edges; treating that as a silhouette change
// defeats the accumulation that is meant to remove that noise. Preserve full
// history through ordinary jitter, then fade it out across genuinely different
// coverage and fully reject a new/vanished cloud silhouette.
float temporal_alpha_agreement_decay(vec4 history, vec4 current, float decay) {
	float alphaMismatch = abs(history.a - current.a);
	return decay * (1.0 - smoothstep(0.20, 0.65, alphaMismatch));
}

// Ray-start jitter can flip soft cloud coverage between adjacent samples.
// Gameplay gates coherent rays by scene depth; menu also gates by cloud-body
// distance. Other cases keep legacy alpha rejection and disocclusion handling.
float coherent_history_decay(vec4 history, vec4 current, float decay,
		bool depthBreakAgrees, bool cloudDistanceAgrees, float pixelMotion) {
	float legacyDecay = temporal_alpha_agreement_decay(history, current, decay);
	if (genericData.data.menu_optimization_controls.y <= 0.5) {
		if (!depthBreakAgrees) {
			return legacyDecay;
		}
		// On coherent gameplay rays, stochastic cloud-body distance can change
		// with jitter without a disocclusion; scene depth and screen bounds still reject.
		return decay;
	}
	if (!depthBreakAgrees || !cloudDistanceAgrees) {
		return legacyDecay;
	}
	float subpixelConfidence = 1.0 - smoothstep(0.5, 2.0, pixelMotion);
	float jitterConfidence = clamp(genericData.data.temporal_controls.x, 0.0, 1.0);
	float retainedFloor = decay * 0.70 * subpixelConfidence * jitterConfidence;
	return max(legacyDecay, retainedFloor);
}

#include "./SunshineCloudsHistory.glslinc"

// ring_storm_owned_level (materials/ring_storm_light.gdshaderinc), the receivers' D.
float ring_storm_owned_level(float storm, float heavy, float influence, vec2 ownership) {
	return max(max(ownership.x * max(storm, heavy), ownership.y), heavy * (1.0 - ring_ws_smooth_unit(influence / 0.25)));
}

// Storm level of the macro map at a canonical position, L = D + E: the same value ring_storm_level_at_map reads for the receivers (D = ring_storm_owned_level of the
// front layer's coverage / storm / influence and the storm-light layer's ownership (front + 4), E = that layer's z), through the same bilinear fetches. 0 outside
// the map or without it. heavy start: ringWeatherSystems.header1.x = HeavyOvercastStartCoverage, the CPU publishes ringworld_storm_map_frame.z as its clamp to 0.99.
float ring_macro_storm_level_at(vec3 canonical) {
	if (!ring_ws_map_valid()) return 0.0;
	float circumference = ringMacroMap.b.z;
	float texel = max(ringMacroMap.a.z, 1.0);
	float dx = canonical.x - ringMacroMap.a.x;
	if (circumference > 1.0) dx -= circumference * floor(dx / circumference);
	vec2 t = vec2(dx, canonical.z - ringMacroMap.a.y) / texel;
	vec2 size = ringMacroMap.b.xy;
	if (t.x < 0.5 || t.y < 0.5 || t.x > size.x - 0.5 || t.y > size.y - 0.5) return 0.0;
	vec2 uv = t / vec2(textureSize(ring_macro_map, 0).xy);
	vec4 front = textureLod(ring_macro_map, vec3(uv, ringMacroMap.a.w), 0.0);
	vec3 own = textureLod(ring_macro_map, vec3(uv, ringMacroMap.a.w + 4.0), 0.0).xyz;
	float heavyStart = ringWeatherSystems.header1.x;
	float heavy = ring_ws_smooth_unit((front.x - heavyStart) / max(0.01, 1.0 - heavyStart));
	// D' = max(D, E): L = max(D, E) + E (materials/ring_storm_light.gdshaderinc ring_storm_terms_blended_at_map; E = 0 without a megastorm).
	float d_level = ring_storm_owned_level(front.y, heavy, front.w, own.xy);
	return max(d_level, own.z) + own.z;
}

// Megastorm haze path levels (docs/megastorm-design.md 5.6): the march's twin of materials/ring_storm_light.gdshaderinc ring_storm_haze_path_factors, the one rule the
// terrain's haze (and the sky's) takes under a megastorm. The levels are read at FIXED absolute distances along the world ray, the midpoints 2.5 / 8.5 / 17 / 31 km of the
// pieces [0, 5], [5, 12], [12, 22], [22, 40] km (the remainder beyond 40 km reuses the 31 km tap), so every target in a direction shares them; each takes
// max(L, shade), L = D + E from the macro map and shade the slanted sun ray's shade of the air (ring_megastorm_air_shade_ray, the same function the spatial consumers use; no strip fetch). Pieces that
// start beyond boundM are left 0 (their weight is 0 for any target within boundM).
vec4 ring_megastorm_haze_levels(vec3 rayOrigin, vec3 rayDirection, float boundM, vec3 sunWorld) {
	vec4 levels = vec4(0.0);
	// The sun's displacement per metre of height in the strip's (x, across), from the cloud frame axes (sign: ring_megastorm_x = dir * (s - around)); both 0 below 0.06.
	float sunUp = dot(sunWorld, genericData.data.cloud_frame_up.xyz);
	vec2 sunSlope = vec2(0.0);
	if (sunUp >= 0.06) {
		sunSlope = vec2(-genericData.data.ring_megastorm_frame.z * dot(sunWorld, genericData.data.cloud_frame_around.xyz),
			dot(sunWorld, genericData.data.cloud_frame_across.xyz)) / sunUp;
	}
	for (int k = 0; k < 4; k++) {
		float start = k == 0 ? 0.0 : (k == 1 ? 5000.0 : (k == 2 ? 12000.0 : 22000.0));
		if (!(start < boundM)) break;
		float mid = k == 0 ? 2500.0 : (k == 1 ? 8500.0 : (k == 2 ? 17000.0 : 31000.0));
		vec3 tap = to_cloud_space(rayOrigin + rayDirection * mid);
		float shade = ring_megastorm_air_shade_ray(ring_megastorm_x(tap.x), tap.z, sunSlope.x, sunSlope.y, sunUp, ringMegastormStrip.d, ringMegastormStrip.e.x, ringMegastormStrip.e.y);
		levels[k] = max(ring_macro_storm_level_at(tap), shade);
	}
	return levels;
}

void main() {
	//SETTING UP UVS/RAY DATA
	ivec2 uv = ivec2(gl_GlobalInvocationID.xy);
	ivec2 size = ivec2(genericData.data.raster_size);
	if (RING_INTERLEAVE > 0) {
		// Each thread marches one pixel of its block; the phase picks which one this frame.
		uv = uv * RING_INTERLEAVE + ring_interleave_offset(RING_INTERLEAVE, int(genericData.data.ring_perf_controls.x + 0.5));
	}

	// Prevent reading/writing out of bounds.
	if (uv.x >= size.x || uv.y >= size.y) {
		return;
	}
	
	// Primary ray, opaque depth and (CloudResolutionJitter) sub-pixel jitter; shared with the interleave fill pass.
	CloudPixelRay pixelRay = ring_cloud_pixel_ray(uv, size);
	vec2 depthUV = pixelRay.depthUV;
	float linear_depth = pixelRay.linearDepth;
	bool has_opaque_depth = pixelRay.hasOpaqueDepth;
	vec3 raydirection = pixelRay.rayDirection;
	vec3 rayOrigin = pixelRay.rayOrigin;


	//DITHER

	// expirements with interleved gradient noise.
	// float ditherScale = 40.037;
	// vec3 ditherUV = vec3(depthUV.x * ditherScale , depthUV.y * ditherScale , genericData.data.time);
	// float smallNoise = texture(dither_small, ditherUV).r;
	// vec3 ign_noise_uv = vec3(float(uv.x), fract(genericData.data.time) * 2.0 - 1.0, float(uv.y));
	// float ign_noise = fract(52.9829189 * fract(dot(ign_noise_uv, vec3(0.006711056, 0.00583715, 1.61803398875))));
	// float ditherValue = ign_noise;

	float ditherScale = 40.037;
	float ditherValue;
	if (genericData.data.menu_optimization_controls.y > 0.5) {
		// Preserve the menu renderer's existing time-driven 3D blue-noise slice.
		vec3 ditherUV = vec3(depthUV.x * ditherScale , depthUV.y * ditherScale , genericData.data.time);
		float smallNoise = texture(dither_small, ditherUV).r;
		ditherValue = smallNoise;
	} else {
		// Gameplay advances one scalar low-discrepancy sample instead of selecting
		// per-pixel finite-slice sampling bias that persists in history.
		vec3 ditherUV = vec3(depthUV.x * ditherScale , depthUV.y * ditherScale , 0.5 / 64.0);
		float seed = texture(dither_small, ditherUV).r;
		ditherValue = fract(seed + genericData.data.time);
	}

	//ATMOSPHERICS
	vec3 ambientfogdistancecolor = genericData.data.ambientfogdistancecolor.rgb;
	vec3 totalRlh = vec3(0,0,0);
    vec3 totalMie = vec3(0,0,0);
	float iOdRlh = 0.0;
    float iOdMie = 0.0;
	float atmosphericDensity = genericData.data.atmospheric_density;

	const float atmosphericHeight = 40000.0;
	const vec3 RayleighScatteringCoef = vec3(5.5e-6, 13.0e-6, 22.4e-6);
	const float Rayleighscaleheight = 8e3;
	const float MieScatteringCoef = 21e-6;
	const float Miescaleheight = 1.2e3;
	const float MieprefferedDirection = 0.758;

	//IMPORTED DATA
	int stepCount = int(genericData.data.max_step_count);
	int lightingStepCount = int(genericData.data.max_lighting_step_count);
	int directionalLightCount = int(genericData.data.directionalLightsCount);
	int pointLightCount = int(genericData.data.pointLightsCount);
	int segmentLightCount = clamp(int(segmentHeader.x), 0, 8);

	vec3 extralargeNoisePos = genericData.data.extralargenoiseposition;
	vec3 largeNoisePos = genericData.data.largenoiseposition;
	vec3 mediumNoisePos = genericData.data.mediumnoiseposition;
	vec3 smallNoisePos = genericData.data.smallnoiseposition;

	float extralargenoiseScale = genericData.data.extralargenoisescale;
	float largenoiseScale = genericData.data.large_noise_scale;
	float mediumnoiseScale = genericData.data.medium_noise_scale;
	float smallnoiseScale = genericData.data.small_noise_scale;

	float minstep = genericData.data.min_step_distance;
	float maxstep = ring_cloud_max_step();
	

	float curlPower = genericData.data.curlPower;
	float lightingStepDistance = genericData.data.lighting_step_distance;
	float cloudfloor = genericData.data.cloud_floor;
	float cloudceiling = genericData.data.cloud_ceiling;

	float densityMultiplier = genericData.data.cloud_density;
	float sharpness = clamp(1.0 - genericData.data.cloud_sharpness, 0.001, 1.0) * 2.0;
	float lightingSharpness = genericData.data.cloud_lighting_sharpness;
	float smallNoiseMultiplier = genericData.data.small_noise_strength;

	float coverage = genericData.data.cloud_coverage * 1.01;
	float lightingdensityMultiplier = genericData.data.cloud_lighting_power;
	lightingdensityMultiplier += lightingdensityMultiplier * 3.0 * coverage;

	vec4 aobase = genericData.data.ambientGroundLightColor;
	
	//bool debugCollisions = false;
	//int frameIndex = int(genericData.data.filterIndex);
	
	//REUSABLE VARIABLES
	bool override = false;
	bool densityBreak = false;
	bool depthBreak = false;

	float maxTheoreticalStep = float(stepCount) * maxstep;
	float highestDensity = 0.0;
	float highestDensityDistance = maxTheoreticalStep;
	// Transmittance-weighted depth: each sample weighs by the opacity it adds, so a thin near veil and a dense far cloud average by contribution.
	float depthWeightSum = 0.0;
	float depthWeightedDistanceSum = 0.0;
	//float ceilingSample = cloudceiling;
	float lodMaxDistance = maxstep * float(stepCount) * genericData.data.lod_bias;
	//float halfcloudThickness = (cloudceiling - cloudfloor) * 0.5;
	//float halfCeiling = cloudceiling - halfcloudThickness;
	

	float newStep = maxstep * ditherValue * clamp(genericData.data.temporal_controls.x, 0.0, 1.0);
	float traveledDistance = newStep;

	vec4 currentColorAccumilation = vec4(0.0);
	vec4 currentDataAccumilation = vec4(0.0);




	//Used for interlaced rendering, not currently enabled due to it's long accumilation time, results in a lot of noticable artifacts.
	//Though it does improve performance, so maybe for some people it will be helpful.

				//bool rebuildFrame = renderBayer(uv, frameIndex);
				// bool rebuildFrame = true;
				
				// if (!rebuildFrame){
				// 	//accumulation preperation:
				// 	vec4 niaveDataRetreval = vec4(0.0);
				// 	float usingaccumA = genericData.data.isAccumulationA;
				// 	if (usingaccumA > 0.0){
				// 		niaveDataRetreval = imageLoad(accum_2A_image, uv).rgba;
				// 	}
				// 	else{
				// 		niaveDataRetreval = imageLoad(accum_2B_image, uv).rgba;
				// 	}
				// 	//depthBreak = niaveDataRetreval.r > linear_depth;

				// 	vec3 worldFinalPos = curPos + raydirection * niaveDataRetreval.g;
				// 	worldFinalPos += (rayOrigin - genericData.data.prevview[3].xyz);
				// 	//Prevview is already actually the inv_view (due to the way retrieving the transform works), so inversing it here is making it the equalivant of View_Matrix.
				// 	vec4 reprojectedClipPos = inverse(genericData.data.prevview) * vec4(worldFinalPos, 1.0);
					
					
				// 	if (reprojectedClipPos.z > 0.0){
				// 		override = true;
				// 	}
				// 	else{
				// 		vec4 reprojectedScreenPos = genericData.data.prevproj * reprojectedClipPos;
						
				// 		// Convert clip space to normalized device coordinates
				// 		ndc = (reprojectedScreenPos.xy / reprojectedScreenPos.w);

				// 		// Convert normalized device coordinates to screen space
				// 		vec2 screen_position = ndc * 0.5 + 0.5;
				// 		//screen_position = clamp(screen_position, vec2(0.0), vec2(1.0));
				// 		screen_position = screen_position - depthUV;
				// 		ivec2 adjustedUV = ivec2(int(screen_position.x * size.x), int(screen_position.y * size.y));
				// 		//float change = length(vec2(adjustedUV));
				// 		adjustedUV += uv; //Size is the screen resolution.
						
				// 		ivec2 clampedUV = clamp(adjustedUV, ivec2(0), size - ivec2(1)); //having two lets me check if clamping it changed the reprojected uv, if it did that means it was offscreen, so rebuild data.

				// 		//execute accumilation.
				// 		float accumdecay = genericData.data.accumilation_decay;

				// 		//alternate back and forth to avoid stepping on pixels being written too.
						
				// 		float actualDepth = abs(reprojectedClipPos.z);
						
				// 		if (usingaccumA > 0.0){
				// 			currentDataAccumilation = imageLoad(accum_2A_image, adjustedUV).rgba;
				// 			bool lastDepthBreak = currentDataAccumilation.a < 0.0;
				// 			float sampledDepth = currentDataAccumilation.r;
				// 			depthBreak = actualDepth > sampledDepth;
				// 			if (clampedUV != adjustedUV || depthBreak != lastDepthBreak){
				// 				override = true;
				// 				//debugCollisions = true;
				// 			}
				// 			else{
				// 				imageStore(accum_1B_image, uv, imageLoad(accum_1A_image, adjustedUV));
				// 				imageStore(accum_2B_image, uv, currentDataAccumilation);
				// 			}
							
				// 		}
				// 		else{
				// 			currentDataAccumilation = imageLoad(accum_2B_image, adjustedUV).rgba;
				// 			bool lastDepthBreak = currentDataAccumilation.a < 0.0;
				// 			float sampledDepth = abs(currentDataAccumilation.r);
				// 			depthBreak = actualDepth > sampledDepth;
				// 			if (clampedUV != adjustedUV || depthBreak != lastDepthBreak){
				// 				override = true;
				// 				//debugCollisions = true;
				// 			}
				// 			else{
				// 				imageStore(accum_1A_image, uv, imageLoad(accum_1B_image, adjustedUV));
				// 				imageStore(accum_2A_image, uv, currentDataAccumilation);

				// 			}
				// 		}
				// 	}

				// }
				
	// END INTERLACED RENDERING


	
	//if (rebuildFrame || override){ //Re-enable for interlaced rendering
	//If it is our render, build the data for this pixel
	
	
	// Camera-independent clouds: this ray's system mask and altitude band. The march and its exit tests then use the
	// background band unioned with the storm bands of the masked sculpt systems, instead of the sky-wide envelope.
	// The margin covers the longest light march (4 x the lighting step distance) plus the light-sample offsets.
	bool ciClouds = ring_ws_enabled();
	// S6 volumetric low / mid layers (RingCloudLayerSample.glslinc): on whenever the layer table is published; debug mask bit 64 returns
	// to the single legacy background band.
	g_layers_on = ciClouds && ringCloudLayers.header0.x > 1.0 && (ring_ws_debug() & 64) == 0 && ring_ws_map_valid();
	bool ciLayersRay = g_layers_on;
	bool ciLayersProbe = ciClouds && ringCloudLayers.header0.x > 1.0 && (ring_ws_debug() & 64) == 0;
	// With a valid macro map the per-sample macro terms are one fetch, so the per-ray list only needs the sculpt systems.
	bool ciMap = ciClouds && ring_ws_map_valid();
	bool ciStormMap = ciClouds && ring_storm_map_valid();
	// Column sources: the storm map and / or the megastorm strip (docs/megastorm-design.md 3.2). Without a megastorm ciColumns == ciStormMap.
	bool ciColumns = ciStormMap || (ciClouds && ring_megastorm_strip_valid());
	// Background band (flag on) and the light reach used for the per-primary sculpt sub-mask: the longest light march
	// (4 x the lighting step distance, point lights 4 x min(maxstep, distance)) plus slack for the ambient offset and curvature.
	vec2 ciBackground = vec2(cloudfloor, cloudceiling);
	float ciLightReach = 0.0;
	bool ciWidenBand = false;
	if (ciClouds) {
		float ciRayEnd = maxTheoreticalStep;
		if (genericData.data.cloud_handoff.y > genericData.data.cloud_handoff.x
			&& genericData.data.cloud_handoff.x > 0.0) {
			ciRayEnd = min(ciRayEnd, genericData.data.cloud_handoff.y);
		}
		ciRayEnd = min(ciRayEnd, linear_depth) + maxstep;
		// Longest light sample offset from a primary: 4 x max(lighting step, maxstep), scaled by 1.05 (cloud-space arc length per
		// metre at altitude <= 20 km on a >= 400 km ring), plus 1 km for the ambient offset; the ray margin adds 600 m for the
		// polyline's curvature sag.
		ciLightReach = 4.2 * max(lightingStepDistance, maxstep) + 1000.0;
		float ciMargin = ciLightReach + 600.0;
		int ciDebug = ring_ws_debug();
		if ((ciDebug & 4) != 0) ring_ws_set_mask(uvec2(0u)); // debug: skip the ray mask build
		else ring_ws_set_mask(ring_ws_ray_mask(rayOrigin, raydirection, ciRayEnd, ciMargin,
			g_layers_on ? min(cloudfloor, ring_cloud_layers_hull(true).x) : cloudfloor, 2.0 * maxstep + 500.0, ciMap));
		if ((ciDebug & 2) != 0 || ringWeatherSystems.header1.y <= 0.001) g_ws_sculpt_mask = uvec2(0u); // debug: no sculpt systems; zero sculpt strength has no sculpt density
		ciBackground = vec2(genericData.data.background_altitude.x, genericData.data.background_altitude.y);
		if (ciBackground.y <= ciBackground.x + 1.0) ciBackground = vec2(cloudfloor, cloudceiling);
		if (g_layers_on) {
			// S6: the ray's base band is the volumetric layer hull. Its floor is the weather base only when a base-lowering system is
			// near the ray (the mask margin covers the map's bilinear bleed); otherwise the fair base. The sculpt intervals widen it as before.
			bool ciLowersBase = false;
			for (int word = 0; word < 2; word++) {
				uint bits = word == 0 ? g_ws_mask.x : g_ws_mask.y;
				while (bits != 0u) {
					int index = word * 32 + findLSB(bits);
					bits &= bits - 1u;
					if ((ring_ws_flags(index) & RING_WS_FLAG_LOWERS_BASE) != 0u) ciLowersBase = true;
				}
			}
			ciBackground = ring_cloud_layers_hull(ciLowersBase);
		}
		ciWidenBand = (ciDebug & 1) == 0; // debug bit 1: no band widening
		// Tight per-ray band: the background band, widened by a sculpt system's own band only over the ray interval where the ray
		// is inside that system's ellipse; the sub-mask / sculpt mask are refined by the ellipses as well.
		ring_ws_build_ray_sculpt(rayOrigin, raydirection, ciRayEnd, ciMargin, 1500.0, ciBackground, ciWidenBand);
		g_ws_sub_mask = g_ws_sculpt_mask;
		g_ws_sub_band = ciBackground;
		// cloudfloor/cloudceiling now hold the hull of every band this ray can touch (camera-inside and exit-side tests); the
		// per-step in-band test, the empty-step skip and the entry skip use the per-distance band below.
		vec2 ciHull = ring_ws_ray_band_hull(ciBackground);
		cloudfloor = ciHull.x;
		cloudceiling = ciHull.y;
	}

	// S6 WO5 cirrus shell: ciShellT is the shell's ray distance when it lies in front of the opaque depth (1e30 = none). The march
	// only records the opacity in front of it (one loop-carried float); every shell fetch happens after the loop.
	bool ciShellOn = g_layers_on && ringCloudLayers.layers[11].w > 0.5 && (ring_ws_debug() & 128) == 0;
	float ciShellT = 1e30;
	if (ciShellOn) {
		float s = ring_cirrus_shell_distance(rayOrigin - genericData.data.cloud_frame_origin.xyz, raydirection,
			genericData.data.cloud_frame_around.xyz, genericData.data.cloud_frame_up.xyz, genericData.data.cloud_frame_up.w,
			ringCloudLayers.layers[8].x);
		if (s >= 0.0 && s < linear_depth) ciShellT = s;
	}

	vec3 directionalLightSunUpPower[4] = vec3[4](vec3(0.0), vec3(0.0), vec3(0.0), vec3(0.0));
	float totalLightPower = 0.0;

	for (int lightI = 0; lightI < directionalLightCount; lightI++){
		if (directionalLights[lightI].color.a > 0.0){
			
			directionalLightSunUpPower[lightI].r = smoothstep(-0.03, 0.07, dot(directionalLights[lightI].direction.xyz, genericData.data.cloud_frame_up.xyz));
			totalLightPower += directionalLights[lightI].color.a * directionalLightSunUpPower[lightI].r;

			directionalLightSunUpPower[lightI].b = dot(directionalLights[lightI].direction.xyz, raydirection);
		}
	}
	

	
	
	vec4 lightColor = vec4(0.0);
	vec3 transientLightColor = vec3(0.0);
	vec3 paintedColor = vec3(0.0);
	float initialdistanceSample = 0.0;

	float lightingSamples = 0.0;
	float atmoSamples = 0.0;

	float density = 0.0;
	float ambient = 0.0;
	float depthFade = 1.0;
	float newdensity = 0.0;
	// Storm share of the ray's opacity: sum(storm density * visibility) / sum(density * visibility) over the primary samples.
	float primaryStormShare = 0.0;
	// Share used for the S4 storm sun cut. Equals primaryStormShare except megastorm strip samples, which take the physical sun.
	float sunCutShare = 0.0;
	float stormShareNum = 0.0;
	float stormShareDen = 0.0;
	// Megastorm light under the overhang (WO4b): sum(visibility * open-sky light) / sum(visibility) over the samples inside or under the
	// strip column; the denominator over stormShareDen is the megastorm share of the ray's opacity. Megastorm kernel variant only.
	float megaOpenNum = 0.0;
	float megaOpenDen = 0.0;
	// Layer shade share (S6): sum(visibility * sun share taken by the layers above) / sum(visibility), for the ambient term. The visibility
	// sum is stormShareDen (identical: a sample with no density adds a zero visibility).
	float layerShadeNum = 0.0;
	// Deck light (S6 3.8): sum(visibility * column light of deck samples) over the same weights; 1 for non-deck samples.
	float deckLightNum = 0.0;
	// Deck colour (S6 3.8): sum(visibility * overhead cloud fraction of non-storm layer samples) over the same weights.
	float deckOvercastNum = 0.0;
	vec3 curPos = vec3(0.0);
	float previousCloudAltitude = cloud_altitude(rayOrigin);
	bool cameraStartsInsideCloudAltitudeBand = previousCloudAltitude >= cloudfloor
		&& previousCloudAltitude <= cloudceiling;
	bool enteredCloudAltitudeBand = false;
	float cloudBandTraversalDirection = 0.0;
	
	float curLod = 1.0;
	float samplePosCount = clamp(genericData.data.samplePointsCount, 0.0, 256.0);

	if (samplePosCount > 0 && gl_GlobalInvocationID.xy == uvec2(0)){
		// Point probes are not tied to this pixel's ray, so evaluate them directly against every uploaded system.
		uvec2 ciRayMask = g_ws_mask;
		if (ciClouds) ring_ws_set_mask(ring_ws_all_mask());
		for (int i = 0; i < samplePosCount; i++){
			curPos = SamplePoints[i].xyz;
			vec3 cloudPos = to_cloud_space(curPos);
			if (ciClouds) ring_ws_update_macro(cloudPos, coverage, densityMultiplier, lightingdensityMultiplier);
			if (ciClouds) g_ws_storm_owned = ring_ws_storm_owned_at(cloudPos, g_ws_sculpt_mask);
			g_layers_on = ciLayersProbe;
			if (ciLayersProbe) {
				ring_layers_update_direct(cloudPos);
				g_ws_storm = ring_ws_eval_owned_storm_level();
				ring_layers_set_closure(g_ws_storm);
			}
			if (ciColumns) ring_storm_update_primary(cloudPos, g_ws_sub_mask);
			vec4 maskSample = texture(extra_large_noise, (cloudPos.xz - extralargeNoisePos.xz) / extralargenoiseScale);
			//ceilingSample = mix(halfCeiling, cloudceiling, maskSample.a);
			//ceilingSample = cloudceiling;
			
			SamplePoints[i].w = pow(sampleScene(largeNoisePos, mediumNoisePos, smallNoisePos, curPos, cloudceiling, cloudfloor, maskSample.a, largenoiseScale, mediumnoiseScale, smallnoiseScale, coverage, smallNoiseMultiplier, curlPower, 1.0, false, false) * densityMultiplier, sharpness);
			// A probe exactly at the shell altitude reads the shell's vertical opacity (test hook).
			if (ciLayersProbe && ringCloudLayers.layers[11].w > 0.5 && (ring_ws_debug() & 128) == 0
				&& abs(cloudPos.y - ringCloudLayers.layers[8].x) <= 0.5) {
				float probeCov = g_layer_high_cov;
				vec4 probeHigh = ring_cloud_layer_resolve(2, vec4(0.0, 0.0, probeCov, g_layer_weather));
				vec2 probeWind = vec2(cos(ringWeatherSystems.header1.w), sin(ringWeatherSystems.header1.w));
				float probeTau = ring_cirrus_density(cloudPos.xz + ringCloudLayers.noise[2].xz - genericData.data.mediumnoiseposition.xz,
					probeCov, ring_cirrus_stratiformity(probeCov), probeWind, 0.0)
					* ringCloudLayers.layers[10].w * max(probeHigh.z - probeHigh.y, 1.0) * RING_CIRRUS_TAU_SCALE;
				SamplePoints[i].w = max(SamplePoints[i].w, 1.0 - exp(-probeTau));
			}
		}
		if (ciClouds) ring_ws_set_mask(ciRayMask);
		g_layers_on = ciLayersRay;
	}

	// Skip only empty, whole maxstep lattice intervals before the first local
	// shell entry. The jittered first point and all exit-state bookkeeping stay
	// on their original path.
	vec3 entryOrigin = rayOrigin - genericData.data.cloud_frame_origin.xyz;
	int entrySkip = 0;
	if (ciClouds) {
		// The boundary-list skip over U: the background band and each sculpt interval (a leading step may be skipped only if it is
		// outside the band AND, for each interval, either outside that system's band or before the interval starts; t_k = jitter + k * maxstep).
		entrySkip = ring_ws_boundary_skip(entryOrigin, raydirection, ciBackground, newStep, maxstep, stepCount, linear_depth);
	} else {
		entrySkip = cloud_entry_skip_steps(
			entryOrigin, raydirection, genericData.data.cloud_frame_around.xyz,
			genericData.data.cloud_frame_up.xyz, genericData.data.cloud_frame_up.w,
			cloudfloor, cloudceiling, newStep, maxstep,
			stepCount, linear_depth);
	}
	if (entrySkip > 0) {
		traveledDistance = newStep + float(entrySkip) * maxstep;
		newStep = maxstep;
	}
	for (int i = entrySkip; i < stepCount; i++) {
		if (genericData.data.cloud_handoff.y > genericData.data.cloud_handoff.x
			&& genericData.data.cloud_handoff.x > 0.0
			&& traveledDistance > genericData.data.cloud_handoff.y) {
			break;
		}
		// Cirrus shell: the first step at or past it records the opacity in front of the shell; the value is negative afterwards.
		if (traveledDistance >= ciShellT && ciShellT >= 0.0) ciShellT = -1.0 - density;
		bool terminalDepthInterval = false;
		float terminalDensityWeight = 1.0;
		if (traveledDistance > linear_depth){
			depthBreak = true;
			// Each point represents the interval that led to it. Preserve the visible
			// portion of the terminal interval instead of evaluating the endpoint
			// behind opaque geometry. The first jittered point still represents the
			// nominal first maxstep interval starting at the ray origin.
			float incomingStep = i == 0 ? maxstep : newStep;
			float intervalStart = i == 0 ? 0.0 : traveledDistance - incomingStep;
			float visibleLength = clamp(linear_depth - intervalStart, 0.0, incomingStep);
			if (visibleLength <= 0.0) {
				traveledDistance = linear_depth;
				curPos = rayOrigin + raydirection * traveledDistance;
				break;
			}
			terminalDepthInterval = true;
			terminalDensityWeight = visibleLength / incomingStep;
			traveledDistance = intervalStart + visibleLength * 0.5;
		}
		
		curPos = rayOrigin + raydirection * traveledDistance;
		vec3 cloudPos = to_cloud_space(curPos);
		bool inCloudAltitudeBand;
		// Flag on: the band at this ray distance (background band widened only inside active sculpt intervals); the exact
		// emptiness test then skips the macro-map fetch and sampleScene for steps that cannot hold density.
		bool ciEmptyStep = false;
		// This primary lies inside or under the megastorm strip column, more than one fade below its top (set below; the megastorm kernel
		// variant only).
		bool megaSample = false;
		if (ciClouds) {
			vec2 ciStepBand = ring_ws_ray_band_at(traveledDistance, ciBackground);
			inCloudAltitudeBand = clamp(cloudPos.y, ciStepBand.x, ciStepBand.y) == cloudPos.y;
			ciEmptyStep = inCloudAltitudeBand && !ring_ws_primary_possible(cloudPos.y, traveledDistance, ciBackground);
		} else {
			inCloudAltitudeBand = clamp(cloudPos.y, cloudfloor, cloudceiling) == cloudPos.y;
		}
		// A near-tangent ray can reach its altitude apex while it is still
		// inside the cloud band. Track the first altitude-band traversal rather
		// than density: noise holes must not permit a remote shell re-entry.
		if (inCloudAltitudeBand) {
			if (!enteredCloudAltitudeBand) {
				enteredCloudAltitudeBand = true;
			}
			// Use the analytic curved-cylinder altitude derivative. At Certified
			// radii subtracting two R-length samples quantizes near a tangent and
			// can falsely reverse a ray before its first density sample.
			if (!cameraStartsInsideCloudAltitudeBand) {
				float analyticAltitudeMotion = cloud_altitude_motion(curPos, raydirection);
				if (abs(cloudBandTraversalDirection) <= 0.000001
					&& abs(analyticAltitudeMotion) > 0.000001) {
					cloudBandTraversalDirection = sign(analyticAltitudeMotion);
				}
				if (abs(cloudBandTraversalDirection) > 0.000001
					&& analyticAltitudeMotion * cloudBandTraversalDirection < -0.000001) {
					break;
				}
			}
		}
		// This mask cannot affect density or lighting outside the altitude band.
		// The disabled path retains the original fetch for parity comparisons.
		vec4 maskSample = vec4(0.0);
		// With the layers on the fetch waits until the column is known to hold density (below).
		if ((genericData.data.cloud_mask_controls.x <= 0.5 || inCloudAltitudeBand) && !ciEmptyStep && !g_layers_on) {
			maskSample = texture(extra_large_noise, (cloudPos.xz - extralargeNoisePos.xz) / extralargenoiseScale);
		}
		//ceilingSample = mix(halfCeiling, cloudceiling, maskSample.a);
		//ceilingSample = cloudceiling;
		
		//sampleAtmospherics(curPos, atmosphericHeight, newStep, Rayleighscaleheight, Miescaleheight, RayleighScatteringCoef, MieScatteringCoef, atmosphericDensity, density, totalRlh, totalMie, iOdRlh, iOdMie); 
		atmoSamples += 1.0;
		if (inCloudAltitudeBand){

			curLod = 1.0 - clamp(traveledDistance / lodMaxDistance, 0.0, 1.0);
			if (ciEmptyStep) {
				// Exactly empty: outside the background band and every active sculpt band, edgeFade and the storm altitude
				// masks are 0, so sampleScene would return 0. No map fetch, no sub-mask, no sampleScene; same flow as a zero
				// density sample (newStep = maxstep below).
				newdensity = 0.0;
			} else {
			// Sculpt sub-mask for this primary sample (light and ambient samples reuse it).
			if (ciClouds) ring_ws_update_sub_mask(cloudPos, ciLightReach, ciBackground, ciWidenBand, ciStormMap);
			// One storm profile fetch per primary sample (only when a storm system is in reach); light and ambient samples reuse it.
			bool ciColumnEmpty = false;
			if (ciColumns) {
				if (g_ws_sub_map_storm || g_ws_sub_strip) ring_storm_fetch_columns(cloudPos, g_ws_sub_map_storm, g_ws_sub_strip);
				else { g_storm_heights = vec4(0.0); g_storm_aux = vec4(0.0); g_storm_strip = false; }
				g_storm_primary_active = g_storm_heights.y > g_storm_heights.x || g_storm_heights.w > g_storm_heights.z;
				g_storm_primary_dominant = false;
				// The fetched column is the map storms' exact extent for this primary and its light samples (ring_ws_update_sub_mask).
				if (ciWidenBand) g_ws_sub_band.y = max(g_ws_sub_band.y, ring_storm_column_top());
				// Exactly empty: outside the background band, the column and every other sculpt band in reach (e.g. an anvil interval
				// step beside the canopy), so sampleScene would return 0. Skips the macro fetch and sampleScene.
				ciColumnEmpty = !g_layers_on && !ring_ws_density_possible(cloudPos.y, ciBackground) && !ring_storm_column_contains(cloudPos.y);
			}
			if (g_layers_on) {
				// S6: the column's low / mid layers (one map fetch). A primary outside both layers, every sculpt band in reach and the storm
				// column is exactly empty: no macro fetch, no mask fetch, no sampleScene. The layer hull is wider than the layers themselves.
				ring_layers_update(cloudPos);
				ciColumnEmpty = !ring_layers_contain(cloudPos.y) && !ring_ws_density_possible(cloudPos.y, vec2(1e30, -1e30))
					&& !(ciColumns && ring_storm_column_contains(cloudPos.y));
				if (!ciColumnEmpty) maskSample = texture(extra_large_noise, (cloudPos.xz - extralargeNoisePos.xz) / extralargenoiseScale);
			}
			if (ciColumnEmpty) {
				newdensity = 0.0;
			} else {
			// One macro-weather evaluation per primary sample; its coverage, density and light multipliers (and the
			// g_ws_* storm/base-coverage globals) are reused by every light and ambient sample taken from this point.
			if (ciClouds && (ring_ws_debug() & 8) != 0) ring_ws_apply_macro(vec4(ringWeatherSystems.header0.z, 0.0, 0.0, 0.0), coverage, densityMultiplier, lightingdensityMultiplier); // debug: constant macro
			else if (ciMap) ring_ws_update_macro_map(cloudPos, coverage, densityMultiplier, lightingdensityMultiplier);
			else if (ciClouds) ring_ws_update_macro(cloudPos, coverage, densityMultiplier, lightingdensityMultiplier);
			if (g_layers_on) {
				// S6 3.8: from here on this primary's storm level is the owned one (storms and plain heavy overcast keep theirs).
				g_ws_storm = ring_ws_eval_owned_storm_level();
				ring_layers_set_closure(g_ws_storm);
			}
			// newdensity = sampleSceneCoarse(largeNoisePos, curPos, cloudceiling, cloudfloor, maskSample.a, largenoiseScale, coverage, curLod);
			if (g_layers_on) g_layer_extent = abs(cloud_altitude_motion(curPos, raydirection)) * maxstep;
			newdensity = pow(sampleScene(largeNoisePos, mediumNoisePos, smallNoisePos, curPos, cloudceiling, cloudfloor, maskSample.a, largenoiseScale, mediumnoiseScale, smallnoiseScale, coverage, smallNoiseMultiplier, curlPower, curLod, false, false) * densityMultiplier, sharpness) * depthFade;
			primaryStormShare = g_last_total_density > 0.0001 ? clamp(g_last_storm_density / g_last_total_density, 0.0, 1.0) : 0.0;
			// Storm-lit background (the lifted overcast under a storm) is storm cloud as far as darkness goes.
			// Only a storm-owned column counts (S6 3.8): a non-storm deck keeps its layer lighting. Layers off keep the S4 rule.
			primaryStormShare = max(primaryStormShare, smoothstep(0.35, 0.80, ring_ws_storm_level()) * min(1.0, newdensity * 4.0));
			sunCutShare = primaryStormShare;
			g_storm_primary_dominant = g_last_storm_density > 0.5 * g_last_total_density && g_last_storm_density > 0.0;
			if (RING_MEGASTORM_ENABLED && g_storm_strip && cloudPos.y <= ring_storm_column_top()) {
				// Megastorm wall and anvil (docs/megastorm-design.md 3.2): everything inside the strip column, including the closed layer deck the
				// W/A ownership lifts there, is the storm's own cloud. Its macro storm level only peaks deep behind the face (record W's natural
				// falloff), so without this the face would take the deck's sky-chroma ambient and read as open sky. Below the column's top fade
				// (WO4b) nothing sees the open sky dome: the face under the overhang, the overhang's underside and the fair layer cumulus below it
				// are lit only by the dim light under the storm (ring_megastorm_open_light), however thin the cloud, so they take the storm ambient
				// in full and its open-sky factor (megaSample); the column's top layer, under the open sky, keeps the thin-cloud ramp and the plain
				// storm ambient. Their sun is already gone (ring_storm_sun_tau).
				bool megaInColumn = ring_storm_column_contains(cloudPos.y);
				megaSample = ring_storm_column_top() - cloudPos.y > RING_STORM_FADE_METERS;
				primaryStormShare = megaSample ? 1.0 : min(1.0, newdensity * 4.0);
				sunCutShare = 0.0;
				g_storm_primary_dominant = megaInColumn || g_storm_primary_dominant;
			}
			g_layer_light_stride = ring_layers_light_stride(g_layers_on && g_last_storm_density <= 0.0, cloudPos.y, traveledDistance);
			// A non-storm stratiform layer sample is deck cloud (S6 3.8); a storm-owned column keeps the S4 look.
			g_layer_deck = g_layers_on && g_last_storm_density <= 0.0
				? ring_layers_deck(cloudPos.y) * (1.0 - g_ws_storm_owned) * (1.0 - smoothstep(0.35, 0.80, ring_ws_storm_level())) : 0.0;
			if (genericData.data.cloud_handoff.y > genericData.data.cloud_handoff.x
				&& genericData.data.cloud_handoff.x > 0.0) {
				newdensity *= ring_cloud_handoff_near_weight(traveledDistance,
					genericData.data.cloud_handoff);
			}
			}
			}
			// if (newdensity > 0.0) {
			// 	newdensity = pow(sampleScene(largeNoisePos, mediumNoisePos, smallNoisePos, curPos, cloudceiling, cloudfloor, maskSample.a, largenoiseScale, mediumnoiseScale, smallnoiseScale, coverage, smallNoiseMultiplier, curlPower, curLod, false) * densityMultiplier, sharpness) * depthFade;
			// }
			
			
			float sampleDeckOvercast = 0.0;
			// Storm-ambient light factor of a megastorm sample (megaSample only; RING_MEGASTORM_SHADE_AMBIENT for buried samples).
			float sampleMegaOpen = RING_MEGASTORM_SHADE_AMBIENT;
			if (newdensity > 0.0){
				if (initialdistanceSample == 0.0){
					initialdistanceSample = traveledDistance;
				}
				g_layer_shade = 0.0;

				float powderEffect = pow(newdensity, genericData.data.powderStrength * 2.0);

				paintedColor += maskSample.rgb;
				lightingSamples += 1.0;
				for (int lightI = 0; lightI < directionalLightCount; lightI++){
					vec3 sundir = directionalLights[lightI].direction.xyz;
					float sunUpWeight = directionalLightSunUpPower[lightI].r;
					// A below-horizon or disabled directional light cannot contribute.
					// Skip its expensive transmittance march, while point lights and
					// ambient/cloud density paths below remain unchanged.
					if (directionalLights[lightI].color.a <= 0.0 || sunUpWeight <= 0.0) {
						continue;
					}

					int thislightingStepCount = min(int(directionalLights[lightI].direction.w), lightingStepCount);
					float henyeygreenstein =  pow(HenyeyGreenstein(genericData.data.anisotropy, directionalLightSunUpPower[lightI].b), mix(1.0, 2.0, 1.0 - genericData.data.anisotropy)); 
					float densitySample = sampleLighting(thislightingStepCount, curPos, extralargeNoisePos, largeNoisePos, mediumNoisePos, smallNoisePos, sundir, densityMultiplier * lightingdensityMultiplier, sunUpWeight, lightingStepDistance, cloudceiling, cloudfloor, extralargenoiseScale, largenoiseScale, mediumnoiseScale, smallnoiseScale, coverage, smallNoiseMultiplier, curlPower, curLod, true);
					densitySample = BeersLaw(lightingStepDistance, densitySample * henyeygreenstein) * g_layer_sun;
					//densitySample = Powder(lightingStepDistance, densitySample);
					// Storm cloud (tower, canopy, deck, the storm-lit overcast) takes only part of the direct sun: a dark, dramatic storm even where the sun reaches it.
						// Megastorm strip cloud takes the physical sun only (sunCutShare 0); its umbra comes from ring_storm_sun_tau.
					float thisStepLightingWeight = (pow(densitySample, lightingSharpness)) * sunUpWeight * terrain_cloud_direct_sun_visibility(curPos)
						* (1.0 - 0.85 * genericData.data.ring_storm_ambient.w * sunCutShare);
					

					lightColor.rgb += pow(directionalLights[lightI].color.rgb * directionalLights[lightI].color.a * thisStepLightingWeight, vec3(2.2)) * powderEffect;
					directionalLightSunUpPower[lightI].g += directionalLights[lightI].color.a * thisStepLightingWeight;
					// if (thislightingStepCount > 0){
					// 	float henyeygreenstein =  pow(HenyeyGreenstein(genericData.data.anisotropy, directionalLightSunUpPower[lightI].b), mix(1.0, 2.0, 1.0 - genericData.data.anisotropy)); 
					// 	float densitySample = sampleLighting(thislightingStepCount, curPos, extralargeNoisePos, largeNoisePos, mediumNoisePos, smallNoisePos, sundir, densityMultiplier * lightingdensityMultiplier, sunUpWeight, lightingStepDistance, ceilingSample, cloudfloor, extralargenoiseScale, largenoiseScale, mediumnoiseScale, smallnoiseScale, coverage, smallNoiseMultiplier, curlPower, curLod);
					// 	densitySample = BeersLaw(lightingStepDistance, densitySample * henyeygreenstein);
					// 	//densitySample = Powder(lightingStepDistance, densitySample);
					// 	float thisStepLightingWeight = (clamp(pow(densitySample, lightingSharpness), 0.0, 1.0)) * sunUpWeight;
						

					// 	lightColor.rgb += pow(directionalLights[lightI].color.rgb * directionalLights[lightI].color.a * thisStepLightingWeight, vec3(2.2)) * powderEffect;
					// 	directionalLightSunUpPower[lightI].g += directionalLights[lightI].color.a * thisStepLightingWeight;
					// }
					// else{
					// 	lightColor.rgb += pow(directionalLights[lightI].color.rgb * directionalLights[lightI].color.a * sunUpWeight, vec3(2.2)) * powderEffect;
					// 	directionalLightSunUpPower[lightI].g += directionalLights[lightI].color.a * sunUpWeight;
					// }
					// if (directionalLights[lightI].color.a > 0.0){
						
					// 	vec3 sundir = directionalLights[lightI].direction.xyz;
					// 	float sunUpWeight = directionalLightSunUpPower[lightI].r;

					// 	int thislightingStepCount = min(int(directionalLights[lightI].direction.w), lightingStepCount);
					// 	if (thislightingStepCount > 0){
					// 		float henyeygreenstein =  pow(HenyeyGreenstein(genericData.data.anisotropy, directionalLightSunUpPower[lightI].b), mix(1.0, 2.0, 1.0 - genericData.data.anisotropy)); 
					// 		float densitySample = sampleLighting(thislightingStepCount, curPos, extralargeNoisePos, largeNoisePos, mediumNoisePos, smallNoisePos, sundir, densityMultiplier * lightingdensityMultiplier, sunUpWeight, lightingStepDistance, ceilingSample, cloudfloor, extralargenoiseScale, largenoiseScale, mediumnoiseScale, smallnoiseScale, coverage, smallNoiseMultiplier, curlPower, curLod);
					// 		densitySample = BeersLaw(lightingStepDistance, densitySample * henyeygreenstein);
					// 		//densitySample = Powder(lightingStepDistance, densitySample);
					// 		float thisStepLightingWeight = (clamp(pow(densitySample, lightingSharpness), 0.0, 1.0)) * sunUpWeight;
							

					// 		lightColor.rgb += pow(directionalLights[lightI].color.rgb * directionalLights[lightI].color.a * thisStepLightingWeight, vec3(2.2)) * powderEffect;
					// 		directionalLightSunUpPower[lightI].g += directionalLights[lightI].color.a * thisStepLightingWeight;
					// 	}
					// 	else{
					// 		lightColor.rgb += pow(directionalLights[lightI].color.rgb * directionalLights[lightI].color.a * sunUpWeight, vec3(2.2)) * powderEffect;
					// 		directionalLightSunUpPower[lightI].g += directionalLights[lightI].color.a * sunUpWeight;
					// 	}

						
					// }
				}

				for (int lightI = 0; lightI < pointLightCount; lightI++){
					vec3 lightToOriginDelta = pointLights[lightI].position.xyz - curPos;
					float lightDistanceWeight = length(lightToOriginDelta); 
					if (pointLights[lightI].color.a > 0.0 && lightDistanceWeight < pointLights[lightI].position.w){
						lightToOriginDelta = normalize(lightToOriginDelta);
						vec3 pointContribution;
						if (genericData.data.history_controls.w > 0.5) {
							// History-isolated (lightning) point light: the segment lights' tap-free model. The old 3-tap density exponentiated by the light distance in
							// metres flipped between ~0 and ~1 with the per-frame jitter (raw sparkle, since this branch never reaches history). Transmittance
							// exp(-density x distance x extinction), the same Henyey-Greenstein blend as the segment lights, the (1 - d/r)^2.2 window and the
							// density weight mix(density, 1, 0.35); one sample cannot exceed the light colour x intensity.
							float ptDist = lightDistanceWeight;
							float ptExtinction = segmentHeader.z > 0.0 ? segmentHeader.z : 0.0015;
							float ptPhase = mix(1.0, clamp(HenyeyGreenstein(genericData.data.anisotropy, dot(lightToOriginDelta, raydirection)) * 4.0 * PI, 0.2, 4.0), segmentHeader.y);
							float ptWindow = pow(1.0 - ptDist / pointLights[lightI].position.w, 2.2);
							float ptWeight = pointLights[lightI].color.a * ptWindow * ptPhase * exp(-newdensity * ptDist * ptExtinction) * mix(newdensity, 1.0, 0.35) * powderEffect;
							pointContribution = pow(pointLights[lightI].color.rgb * min(ptWeight, pointLights[lightI].color.a), vec3(2.2));
							transientLightColor += pointContribution;
						} else {
						//float densitySample = 1.0 - newdensity;
						float densitySample = sampleLighting(3, curPos, extralargeNoisePos, largeNoisePos, mediumNoisePos, smallNoisePos, lightToOriginDelta, densityMultiplier, 1.0, min(maxstep, lightDistanceWeight), cloudceiling, cloudfloor, extralargenoiseScale, largenoiseScale, mediumnoiseScale, smallnoiseScale, coverage, smallNoiseMultiplier, curlPower, curLod, false);
						
						float henyeygreenstein = pow(HenyeyGreenstein(genericData.data.anisotropy, dot(lightToOriginDelta, raydirection)), mix(1.0, 2.0, 1.0 - genericData.data.anisotropy)); 
						densitySample = BeersLaw(lightDistanceWeight, densitySample * henyeygreenstein);
						densitySample = mix(densitySample, newdensity, 0.5) * powderEffect;
						lightDistanceWeight = lightDistanceWeight / pointLights[lightI].position.w;
						lightDistanceWeight = pointLights[lightI].color.a * pow((1.0 - lightDistanceWeight), 2.2) * densitySample;


						pointContribution = pow(pointLights[lightI].color.rgb * lightDistanceWeight, vec3(2.2));
						lightColor.rgb += pointContribution;
						}
					}
				}

				// Segment (line) lights: in-scattered emission of a finite line source. The reject is one dot-product closest-point
				// distance squared (no sqrt) against the radius squared. Irradiance is the analytic line integral
				// (atan((L - t) / d) + atan(t / d)) / d, softened inside a core radius and windowed to the light radius; scattering is
				// weighted by the local density, with a cheap density-based Beer term and a blended Henyey-Greenstein phase. No density taps.
				for (int segI = 0; segI < segmentLightCount; segI++){
					vec3 segW = curPos - segmentLights[segI].a.xyz;
					vec3 segDir = segmentLights[segI].b.xyz;
					float segLength = segmentLights[segI].b.w;
					float segRadius = segmentLights[segI].a.w;
					float segT = dot(segW, segDir);
					float segTc = clamp(segT, 0.0, segLength);
					float segW2 = dot(segW, segW);
					float segClosest2 = segW2 - segTc * (2.0 * segT - segTc);
					if (segClosest2 < segRadius * segRadius){
						float segClosest = sqrt(max(segClosest2, 0.0));
						float segPerp = sqrt(max(segW2 - segT * segT, 0.0));
						float segCore = max(segRadius * 0.04, 1.0);
						float segD = max(segPerp, segCore);
						float segIrradiance = (atan((segLength - segT) / segD) + atan(segT / segD)) * (1.0 / PI) * (segCore / segD);
						float segWindow = 1.0 - segClosest / segRadius;
						segWindow *= segWindow;
						vec3 segToClosest = segDir * segTc - segW;
						float segPhase = mix(1.0, clamp(HenyeyGreenstein(genericData.data.anisotropy, dot(segToClosest, raydirection) / max(segClosest, 0.01)) * 4.0 * PI, 0.2, 4.0), segmentHeader.y);
						float segTransmit = exp(-newdensity * segClosest * segmentHeader.z);
						vec3 segContribution = segmentLights[segI].color.rgb * (segIrradiance * segWindow * segPhase * segTransmit * mix(newdensity, 1.0, 0.35));
						if (segmentLights[segI].color.a < 0.5 && genericData.data.history_controls.w > 0.5) transientLightColor += segContribution;
						else lightColor.rgb += segContribution;
					}
				}
				
				if (aobase.a > 0.0){
				// Deck samples take the unsaturated density above (structure: darker patches under denser cloud); the rest is unchanged.
				float deckAbove = sampleScene(largeNoisePos, mediumNoisePos, smallNoisePos, curPos + genericData.data.cloud_frame_up.xyz * minstep, cloudceiling, cloudfloor, maskSample.a, largenoiseScale, mediumnoiseScale, smallnoiseScale, coverage, smallNoiseMultiplier, curlPower, curLod, true, false);
				ambient += deckAbove * mix(densityMultiplier * lightingdensityMultiplier, RING_LAYER_DECK_AO_SCALE, g_layer_deck);
#if RING_LAYER_DECK_COLOUR
				// Not for storm cloud: its share of the primary (storm-owned columns also have no closure and no deck weight).
				if (g_layers_on && (ring_ws_debug() & 256) == 0) sampleDeckOvercast = (1.0 - primaryStormShare) * ring_layers_deck_overcast(cloudPos.y, deckAbove, g_layer_deck);
#endif
				// From here g_layer_deck holds this primary's deck light factor (column light x patch), 1 for non-deck samples.
				g_layer_deck = mix(1.0, ring_layers_column_light(cloudPos.y) * ring_layers_deck_patch(deckAbove), g_layer_deck);
				}

				if (RING_MEGASTORM_ENABLED && megaSample && density < RING_MEGASTORM_OPEN_DEPTH) {
					// Megastorm light under the overhang (docs/megastorm-design.md 2.3, WO4b): this sample's storm-ambient factor, from buried
					// (SHADE) to facing the open sky beyond the anvil's leading edge (OPEN; ring_megastorm_open_light), times the relief of the
					// eroded surface: where the medium erosion noise (b) carves deep the surface is a recess, shaded by the bulges around it; where
					// it barely erodes, the surface bulges into the open light. The noise is read at the primary itself, in the strip's moving frame
					// (sampleScene's wind sweep and curl warp left out, and sampleScene left untouched: a store there cost the default kernel),
					// not by a probe, so it follows the erosion pattern rather than the march's step lattice. Only the visible front of the ray pays
					// (accumulated opacity below RING_MEGASTORM_OPEN_DEPTH); deeper samples count as buried.
					sampleMegaOpen = mix(RING_MEGASTORM_SHADE_AMBIENT, RING_MEGASTORM_OPEN_AMBIENT, ring_megastorm_open_light(cloudPos))
						* (1.0 + RING_MEGASTORM_RELIEF * (1.0 - 2.0 * texture(noise_medium, (cloudPos - mediumNoisePos
							- vec3(genericData.data.ring_megastorm_noise.y, 0.0, 0.0)) / mediumnoiseScale).b));
				}
				
				newStep = mix(mix(maxstep, minstep, pow(newdensity, 0.1)), maxstep, float(i) / float(stepCount));
				if (newdensity > highestDensity){
					highestDensity = newdensity;
					highestDensityDistance = traveledDistance;
				}
			}
			else{
				newStep = maxstep;
			}

			if (i == 0 && !terminalDepthInterval){
				newdensity = mix(newdensity, 0.0, traveledDistance / maxstep);
			}

			ring_cloud_depth_accumulate(depthWeightSum, depthWeightedDistanceSum, density, newdensity * terminalDensityWeight, traveledDistance);
			float stormVisible = newdensity * terminalDensityWeight * (1.0 - density);
			stormShareNum += stormVisible * primaryStormShare;
			stormShareDen += stormVisible;
			if (RING_MEGASTORM_ENABLED && megaSample && newdensity > 0.0) {
				megaOpenNum += stormVisible * sampleMegaOpen;
				megaOpenDen += stormVisible;
			}
			if (newdensity > 0.0) {
				layerShadeNum += stormVisible * g_layer_shade;
				deckLightNum += stormVisible * (aobase.a > 0.0 ? g_layer_deck : 1.0);
#if RING_LAYER_DECK_COLOUR
				deckOvercastNum += stormVisible * sampleDeckOvercast;
#endif
			}
			density += newdensity * terminalDensityWeight;
			if (density >= 1.0){
				densityBreak = true;
				break;
			}
		}
		else{
			// A curved ring cloud layer can leave the ray and then intersect it
			// again on the far/opposite side. Once this ray has entered its first
			// foreground altitude band, any subsequent out-of-layer sample is the
			// terminal exit. Do not use the local altitude derivative for that
			// case: after the ray has curved around, it can point back toward the
			// remote shell and incorrectly admit the inverted night-side layer.
			// Flag on: a sculpt interval that has not started yet can still hold density, so this is not the terminal exit.
			bool ciExitDeferred = ciClouds && ring_ws_band_ahead(traveledDistance);
			// Rule 2 (S6 section 3.3): inside the ray's hull but outside U(t) is a gap step (the gap beside a storm interval, below the
			// background band, between layers). It holds no density and is never terminal; it skips whole lattice intervals to the next
			// boundary of U. Rule 3: a gap step heading away from U with nothing ahead is the terminal exit, and so is a gap step that
			// returns toward U after the ray's apex (the remote ring shell, which the far layer owns).
			bool ciGap = ciClouds && ring_ws_gap_step(cloudPos.y, traveledDistance, vec2(cloudfloor, cloudceiling), ciBackground);
			bool ciGapSkip = false;
			vec2 ciGapBand = vec2(0.0);
			bool ciGapTerminal = false;
			if (ciGap) {
				float gapMotion = cloud_altitude_motion(curPos, raydirection);
				vec2 gapBand = ring_ws_ray_band_at(traveledDistance, ciBackground);
				ciGapBand = gapBand;
				bool gapToward = (cloudPos.y < gapBand.x && gapMotion > 0.0) || (cloudPos.y > gapBand.y && gapMotion < 0.0);
				bool gapPastApex = gapMotion < 0.0 && cloud_altitude_motion(rayOrigin, raydirection) > 0.0; // the ray started ascending
				ciGapTerminal = !ciExitDeferred && (!gapToward || gapPastApex);
				ciGapSkip = gapToward && !gapPastApex;
			}
			float nextAltitude = ciExitDeferred ? cloudPos.y : cloud_altitude(curPos + raydirection * maxstep);
			float exitAltitudeDirection = nextAltitude - cloudPos.y;
			bool leavingAbove = cloudPos.y > cloudceiling && exitAltitudeDirection > 0.0;
			bool leavingBelow = cloudPos.y < cloudfloor && exitAltitudeDirection < 0.0;
			if (ciGapTerminal || (!ciExitDeferred && !ciGap && (enteredCloudAltitudeBand || leavingAbove || leavingBelow))){
				// Preserve density collected while traversing the layer. The stock
				// path jumped to max distance on exit, and the range fade below then
				// erased the completed cloud ray.
				if (!enteredCloudAltitudeBand) {
					traveledDistance = min(maxTheoreticalStep, linear_depth);
					curPos = rayOrigin + raydirection * traveledDistance;
				}
				//debugCollisions = true;
				break;
			}
			
			newStep = maxstep;
#if RING_WS_GAP_SKIP
			// Gap skip: whole lattice intervals up to just before the next boundary of U, i.e. the altitude crossing into U's band at this
			// distance or the next interval start, whichever comes first (never past either, never off the lattice). One skip call only
			// (not the boundary list): a second inlined copy of the quadratic costs registers in the whole march.
			if (ciGapSkip && !terminalDepthInterval) {
				int gapRemaining = stepCount - i - 1;
				float gapFrom = traveledDistance + maxstep;
				int gapSkip = cloud_entry_skip_steps(
					rayOrigin - genericData.data.cloud_frame_origin.xyz, raydirection, genericData.data.cloud_frame_around.xyz,
					genericData.data.cloud_frame_up.xyz, genericData.data.cloud_frame_up.w,
					ciGapBand.x, ciGapBand.y, gapFrom, maxstep, gapRemaining, linear_depth);
				float gapNextStart = min(ring_ws_next_interval_start(traveledDistance), linear_depth);
				gapSkip = min(gapSkip, int(clamp(floor((gapNextStart - gapFrom) / maxstep), 0.0, float(max(gapRemaining - 1, 0)))));
				if (gapSkip > 0) {
					traveledDistance += float(gapSkip) * maxstep;
					i += gapSkip;
				}
			}
#endif
		}

		if (terminalDepthInterval) {
			traveledDistance = linear_depth;
			curPos = rayOrigin + raydirection * traveledDistance;
			break;
		}
		// Adaptive dense-cloud steps can otherwise spend the finite ray budget
		// before the explicit ownership fade. Reserve enough forward distance in
		// each remaining iteration to reach its configured end; this changes
		// interval length only, never the bounded step count or depth weighting.
		if (genericData.data.cloud_handoff.y > genericData.data.cloud_handoff.x
			&& genericData.data.cloud_handoff.x > 0.0
			&& traveledDistance < genericData.data.cloud_handoff.y) {
			int remainingSteps = max(stepCount - i - 1, 1);
			newStep = max(newStep, (genericData.data.cloud_handoff.y - traveledDistance)
				/ float(remainingSteps));
		}
		
		traveledDistance += newStep;

	}
	if (depthBreak) {
		traveledDistance = linear_depth;
		curPos = rayOrigin + raydirection * traveledDistance;
	}

	// GLSL leaves smoothstep undefined when edge0 is greater than edge1.
	// Express the same near-to-far fade with ordered edges so cloud alpha is
	// stable across Vulkan drivers.
	float cloudRangeFade = genericData.data.cloud_handoff.y > genericData.data.cloud_handoff.x
		&& genericData.data.cloud_handoff.x > 0.0 ? 1.0 : 1.0 - smoothstep(
		minstep * stepCount, maxstep * stepCount, traveledDistance);
	density *= clamp(cloudRangeFade, 0.0, 1.0);

	// Empty rays have no lighting samples. Avoid feeding NaNs into temporal
	// accumulation, where they can contaminate neighboring cloud pixels.
	if (lightingSamples > 0.0) {
		ambient = clamp(ambient / lightingSamples, 0.0, 1.0);
		paintedColor = clamp(paintedColor / lightingSamples, 0.0, 1.0);
	} else {
		ambient = 0.0;
		paintedColor = vec3(1.0);
	}


	// A ringworld's night side still receives scattered light from the sky,
	// terrain and the opposite arc. Stock Sunshine multiplies ambient by zero
	// once its tracked sun crosses the local horizon, making valid clouds black.
	vec3 ambientLight = genericData.data.ambientLightColor.rgb * max(totalLightPower, 0.62);
	ambientLight += genericData.data.ambientfogdistancecolor.rgb * 0.08;
	ambientLight = mix(ambientLight, ambientLight * aobase.rgb, ambient * aobase.a) * paintedColor;
	// Keep clouds readable without imposing a fixed daytime-blue floor. The
	// bridge supplies a ring-local day/night ambient color, so this floor now
	// follows the viewer's side of the ring.
	// Per-ray storm ambient: rays whose opacity is mostly storm cloud (tower, canopy, deck) take the dark storm ambient and lose the
	// 0.35 floor for that share, so the underside of a storm is dark however bright the sky ambient is.
	float stormShare = stormShareDen > 0.0001 ? clamp(stormShareNum / stormShareDen, 0.0, 1.0) : 0.0;
	float stormAmbientStrength = genericData.data.ring_storm_ambient.w * stormShare;
	ambientLight = max(ambientLight, genericData.data.ambientLightColor.rgb * 0.35 * (1.0 - stormAmbientStrength));
	vec3 stormAmbientColour = genericData.data.ring_storm_ambient.rgb;
	if (RING_MEGASTORM_ENABLED && megaOpenDen > 0.0001) {
		// Megastorm (WO4b): the storm ambient of the megastorm share of the ray follows the open-sky light its samples see. A face below the
		// overhang lit by the horizon band takes RING_MEGASTORM_OPEN_AMBIENT x the storm ambient, the underside and the interior
		// RING_MEGASTORM_SHADE_AMBIENT x (normal vs grazing incidence of that band, about 4:1).
		float megaShare = clamp(megaOpenDen / max(stormShareDen, 0.0001), 0.0, 1.0);
		stormAmbientColour *= mix(1.0, megaOpenNum / megaOpenDen, megaShare);
	}
	ambientLight = mix(ambientLight, stormAmbientColour, stormAmbientStrength);
	// Layers above the cloud (an As deck over Cu) darken its ambient the way the storm share does, at half strength.
	float layerShade = stormShareDen > 0.0001 ? clamp(layerShadeNum / stormShareDen, 0.0, 1.0) : 0.0;
	ambientLight = mix(ambientLight, genericData.data.ring_storm_ambient.rgb, 0.5 * genericData.data.ring_storm_ambient.w * layerShade);
	// Deck column light (S6 3.8), after the floor: a closed deck's colour is this ambient, so its structure must survive the floor.
	ambientLight *= stormShareDen > 0.0001 ? clamp(deckLightNum / stormShareDen, 0.0, 2.0) : 1.0;
#if RING_LAYER_DECK_COLOUR
	// Deck colour (S6 3.8): the light under a deck is the sun and sky light its cloud has scattered (flat in wavelength), so the overhead
	// cloud fraction of the ray's samples (ring_layers_deck_overcast) moves the ambient's chroma from the sky's toward the sun + sky
	// irradiance on the deck top (the unshadowed sun term of a lit sample x the sun's cosine, plus the sky ambient). Brightness (column
	// light, patches, floor) is kept. Fair cumulus (no closure, no deck weight) and storm-owned cloud keep their ambient exactly.
	float deckOvercast = stormShareDen > 0.0001 ? clamp(deckOvercastNum / stormShareDen, 0.0, 1.0) : 0.0;
	if (deckOvercast > 0.0) {
		vec3 deckSource = genericData.data.ambientLightColor.rgb * max(totalLightPower, 0.62) + genericData.data.ambientfogdistancecolor.rgb * 0.08;
		for (int lightI = 0; lightI < directionalLightCount; lightI++) {
			if (directionalLights[lightI].color.a > 0.0 && directionalLightSunUpPower[lightI].r > 0.0) {
				deckSource += pow(directionalLights[lightI].color.rgb * directionalLights[lightI].color.a, vec3(2.2)) * directionalLightSunUpPower[lightI].r
					* max(dot(directionalLights[lightI].direction.xyz, genericData.data.cloud_frame_up.xyz), 0.0);
			}
		}
		const vec3 deckLuma = vec3(0.2126, 0.7152, 0.0722);
		ambientLight = mix(ambientLight, deckSource * (dot(ambientLight, deckLuma) / max(dot(deckSource, deckLuma), 1e-6)), deckOvercast);
	}
#endif
	if (any(isnan(lightColor.rgb)) || any(isinf(lightColor.rgb))) {
		lightColor.rgb = vec3(0.0);
	}
	lightColor.rgb += ambientLight;
	// Cirrus shell composite (S6 WO5): by ray distance against the volumetric march. The march kept the opacity in front of the shell
	// (ciShellT, negative once recorded) or ended before reaching it. `density` already holds cloudRangeFade.
	float shellDepthEnd = 0.0;
	if (ciShellOn && ciShellT < 1e29) {
		float shellFront = ciShellT < -0.5 ? -1.0 - ciShellT : clamp(density, 0.0, 1.0);
		float shellT = ring_cirrus_shell_distance(rayOrigin - genericData.data.cloud_frame_origin.xyz, raydirection,
			genericData.data.cloud_frame_around.xyz, genericData.data.cloud_frame_up.xyz, genericData.data.cloud_frame_up.w,
			ringCloudLayers.layers[8].x);
		float shellNear = 1.0;
		if (genericData.data.cloud_handoff.y > genericData.data.cloud_handoff.x && genericData.data.cloud_handoff.x > 0.0) {
			shellNear = ring_cloud_handoff_near_weight(shellT, genericData.data.cloud_handoff);
		} else {
			shellNear = 1.0 - smoothstep(minstep * float(stepCount), maxstep * float(stepCount), shellT);
		}
		if (shellT >= 0.0 && shellNear > 0.0 && shellFront < 1.0) {
			vec3 shellPos = rayOrigin + raydirection * shellT;
			vec3 shellCanon = to_cloud_space(shellPos);
			vec4 shellCol = ring_cloud_layer_resolve(2, ring_layers_map_at(shellCanon)); // (coverage, base, top, stratiformity)
			// A real storm owns its anvil (S6 3.8): no cirrus shell inside a sculpt storm's support, so storms look exactly as before.
			if (ciClouds) shellCol.x *= 1.0 - ring_ws_storm_owned_at(shellCanon, g_ws_sculpt_mask);
			if (shellCol.x > 0.0) {
				vec2 shellWind = vec2(cos(ringWeatherSystems.header1.w), sin(ringWeatherSystems.header1.w));
				vec2 shellXZ = shellCanon.xz + ringCloudLayers.noise[2].xz - genericData.data.mediumnoiseposition.xz;
				float shellMu = cloud_altitude_motion(shellPos, raydirection);
				// Pixel footprint at the shell: the angle between the rays of two vertically adjacent pixels.
				vec4 shellP0 = genericData.data.current_camera_inv_projection * vec4(0.0, 0.0, 0.0, 1.0);
				vec4 shellP1 = genericData.data.current_camera_inv_projection * vec4(0.0, 2.0 / float(size.y), 0.0, 1.0);
				float shellAngle = length(normalize(shellP1.xyz / shellP1.w) - normalize(shellP0.xyz / shellP0.w));
				float shellFootprint = shellT * shellAngle / max(abs(shellMu), 0.05);
				float shellDensity = ring_cirrus_density(shellXZ, shellCol.x, shellCol.w, shellWind, shellFootprint);
				float shellAlpha = ring_cirrus_alpha(shellDensity, max(shellCol.z - shellCol.y, 1.0), ringCloudLayers.layers[10].w, shellMu) * shellNear;
				vec3 shellColour = (genericData.data.ambientLightColor.rgb * max(totalLightPower, 0.62)
					+ genericData.data.ambientfogdistancecolor.rgb * 0.08) * ringCloudLayers.layers[11].y;
				for (int shellLight = 0; shellLight < directionalLightCount; shellLight++) {
					if (directionalLights[shellLight].color.a > 0.0 && directionalLightSunUpPower[shellLight].r > 0.0) {
						shellColour += pow(directionalLights[shellLight].color.rgb * directionalLights[shellLight].color.a
								* directionalLightSunUpPower[shellLight].r, vec3(2.2))
							* ring_cirrus_phase(directionalLightSunUpPower[shellLight].b, ringCloudLayers.layers[11].x, shellCol.w, shellCol.x)
							* RING_CIRRUS_SUN_SCALE;
					}
				}
				float shellW = shellAlpha * (1.0 - shellFront);
				float shellVolA = clamp(density, 0.0, 1.0);
				if (shellW > 0.0) {
					lightColor.rgb = (lightColor.rgb * shellVolA + shellColour * shellW) / max(shellVolA + shellW, 1e-5);
					ring_cloud_depth_accumulate(depthWeightSum, depthWeightedDistanceSum, shellFront, shellAlpha, shellT);
					density = min(shellVolA + shellW, 1.0);
					shellDepthEnd = shellT;
				}
			}
		}
	}
	// lightColor.rgb = ambientLight + clamp(lightColor.rgb / lightingSamples, vec3(0.0), vec3(1.0));
	// Keep the temporal opacity input physical; display compositing clamps later.
	lightColor.a = clamp(density, 0.0, 1.0);

	vec3 physicalFogColor = lightColor.rgb;
	vec3 unfoggedColor = lightColor.rgb; // storm cloud keeps its dark colour with distance (look pass)
	float fogweight = 0.0;
	
	if (atmosphericDensity > 0.0 && linear_depth > maxstep && directionalLightCount > 0.0){
		for (float i = 0.0; i < directionalLightCount; i++){
			DirectionalLight light = directionalLights[int(i)];
			vec3 sundir = light.direction.xyz;
			//sampleColor = sundir;
			float sunUpWeight = smoothstep(0.0, 0.4, dot(sundir, genericData.data.cloud_frame_up.xyz));
			float sundensityaffect = 1.0 - clamp(dot(sundir, raydirection) * density, 0.0, 1.0);
			// sundensityaffect = min(1.0 - (sundensityaffect * density), 1.0 - (sundensityaffect * clamp(maxTheoreticalStep - linear_depth, 0.0, 1.0)));
			float lightPower = light.color.a * sunUpWeight * sundensityaffect;
			vec4 atmosphericData = sampleAllAtmospherics(rayOrigin, raydirection, linear_depth, traveledDistance, 0.0, traveledDistance / 10.0, 10.0, atmosphericDensity, sundir, light.color.rgb * lightPower, ambientfogdistancecolor);
			
			physicalFogColor = mix(physicalFogColor, atmosphericData.rgb, atmosphericData.a); //causes jitter in the sky
			fogweight += atmosphericData.a;
		}
	}



	if (atmosphericDensity > 0.0) {
		lightColor.rgb = mix(physicalFogColor, mix(lightColor.rgb, ambientfogdistancecolor, fogweight),  genericData.data.atmosphere_simple_blend);
	}
	if (any(isnan(lightColor.rgb)) || any(isinf(lightColor.rgb))) {
		lightColor.rgb = ambientLight;
	}
	// Storm cloud (tower, canopy, deck) is a dark mass against the hazy horizon: its own colour wins back part of the aerial perspective.
	lightColor.rgb = mix(lightColor.rgb, unfoggedColor, 0.6 * stormShare);
	lightColor.rgb = max(lightColor.rgb, ambientLight);
	//lightColor.rgb = physicalFogColor;
	// initialdistanceSample = max(initialdistanceSample, 0.0);


	//accumulation preperation:
	float finalDensityDistance = ring_cloud_depth_resolve(depthWeightSum, depthWeightedDistanceSum, max(traveledDistance, shellDepthEnd), highestDensityDistance);
	// Megastorm haze path levels (5.6) of this ray (fixed-distance pieces, so independent of the target); with interleave the block's factors travel to the fill
	// in the transient alpha. All 0 in the default kernel, whenever the strip is off and with CameraIndependentDebugMask bit 1024 (plain haze, the A/B).
	bool megaHazeOn = RING_MEGASTORM_ENABLED && ring_megastorm_strip_valid() && (ring_ws_debug() & 1024) == 0;
	vec4 megaLevels = vec4(0.0);
	float megaPackedFactors = 0.0;
	if (megaHazeOn) {
		vec3 shadeSun = directionalLightCount > 0 ? directionalLights[0].direction.xyz : genericData.data.cloud_frame_up.xyz;
		megaLevels = ring_megastorm_haze_levels(rayOrigin, raydirection, max(0.0, linear_depth), shadeSun);
		if (RING_INTERLEAVE > 0) {
			megaPackedFactors = ring_cloud_pack_storm_factors(ring_cloud_atmosphere_storm_factors(rayOrigin, raydirection,
				clamp(finalDensityDistance, 0.0, max(0.0, linear_depth)), genericData.data.ring_atmosphere_profile, megaLevels,
				genericData.data.ring_storm_ambient.w, ringMegastormStrip.c));
		}
	}
	vec3 delta = rayOrigin - genericData.data.previous_camera_origin.xyz;

	// Reprojection of the cloud body and the opaque-surface metadata the history is validated against live in
	// SunshineCloudsHistory.glslinc, shared with the interleave fill pass.
	CloudHistoryReproject hist = ring_cloud_history_reproject(uv, size, depthUV, rayOrigin, raydirection,
		finalDensityDistance, linear_depth, has_opaque_depth);
	override = override || hist.historyOverride;
	bool historyOutsideImage = hist.historyOutsideImage;
	ivec2 clampedUV = hist.clampedUV;
	vec2 historyFraction = hist.historyFraction;
	ivec2 history00 = hist.history00;
	ivec2 history10 = hist.history10;
	ivec2 history01 = hist.history01;
	ivec2 history11 = hist.history11;
	float reprojectedPixelMotion = hist.reprojectedPixelMotion;
	bool menuStaticHistory = hist.menuStaticHistory;
	bool coherentBilinearHistory = hist.coherentBilinearHistory;
	bool gameplayTemporalHistory = hist.gameplayTemporalHistory;
	float opaqueDepthMetadata = hist.opaqueDepthMetadata;
	float expectedHistoryOpaqueDepth = hist.expectedHistoryOpaqueDepth;
	float opaqueDepthTolerance = hist.opaqueDepthTolerance;
	ivec2 opaqueHistoryPixel = hist.opaqueHistoryPixel;
	bool opaqueHistoryOutside = hist.opaqueHistoryOutside;
	float diagnosticHistoryWeight = 0.0;
	float diagnosticHistoryReason = 0.75; // .75 = invalid/offscreen rejection
	float diagnosticAlphaMismatch = 0.0;


        //execute accumilation.
        float accumdecay = genericData.data.accumilation_decay;
        // The density-matte capture must contain this frame's ray-marched
        // alpha, never an earlier temporal sample. Keeping the normal buffer
        // writes makes enabling/disabling the diagnostic safe between frames.
        if (abs(genericData.data.temporal_controls.z) > 0.5 || genericData.data.temporal_controls.w > 0.5) {
                accumdecay = 0.0;
        }

        //alternate back and forth to avoid stepping on pixels being written too.
	float usingaccumA = genericData.data.isAccumulationA;
	
	//float finalDensityDistance = max(traveledDistance, highestDensityDistance);
	//linear_depth = max(linear_depth, traveledDistance);
	float cameraTranslation = length(delta);
	float travelspeed = cameraTranslation + maxstep;
	//bool debugCollisions = false;
	if (usingaccumA > 0.0){
		currentColorAccumilation = coherentBilinearHistory
			? mix(mix(imageLoad(accum_1A_image, history00).rgba, imageLoad(accum_1A_image, history10).rgba, historyFraction.x), mix(imageLoad(accum_1A_image, history01).rgba, imageLoad(accum_1A_image, history11).rgba, historyFraction.x), historyFraction.y)
			: imageLoad(accum_1A_image, clampedUV).rgba;
		currentDataAccumilation = imageLoad(accum_2A_image, clampedUV).rgba;
		float currentDepthBreak = float(depthBreak);
		bool depthBreakAgrees = currentDepthBreak == currentDataAccumilation.a;
		bool cloudDistanceAgrees = abs(finalDensityDistance - currentDataAccumilation.b)
			<= maxstep + 0.1 * max(finalDensityDistance, currentDataAccumilation.b);
		bool opaquePointAgrees = !opaqueHistoryOutside && (ring_cloud_history_opaque_matches(expectedHistoryOpaqueDepth,
			imageLoad(accum_2A_image, opaqueHistoryPixel).a, opaqueDepthTolerance)
			|| ring_jitter_opaque_neighborhood_matches(true, opaqueHistoryPixel, size, expectedHistoryOpaqueDepth, opaqueDepthTolerance));
		bool gameplayVisibilityRejected = gameplayTemporalHistory && genericData.data.history_controls.x > 0.5 && !opaquePointAgrees;
		if (gameplayTemporalHistory && !historyOutsideImage && genericData.data.history_controls.x > 0.5) {
			if (coherentBilinearHistory) {
				vec4 data00 = imageLoad(accum_2A_image, history00).rgba;
				vec4 data10 = imageLoad(accum_2A_image, history10).rgba;
				vec4 data01 = imageLoad(accum_2A_image, history01).rgba;
				vec4 data11 = imageLoad(accum_2A_image, history11).rgba;
				float weight00 = (1.0 - historyFraction.x) * (1.0 - historyFraction.y) * float(opaque_depth_history_agrees(expectedHistoryOpaqueDepth, opaqueDepthMetadata, data00, opaqueDepthTolerance, traveledDistance, cameraTranslation, maxstep));
				float weight10 = historyFraction.x * (1.0 - historyFraction.y) * float(opaque_depth_history_agrees(expectedHistoryOpaqueDepth, opaqueDepthMetadata, data10, opaqueDepthTolerance, traveledDistance, cameraTranslation, maxstep));
				float weight01 = (1.0 - historyFraction.x) * historyFraction.y * float(opaque_depth_history_agrees(expectedHistoryOpaqueDepth, opaqueDepthMetadata, data01, opaqueDepthTolerance, traveledDistance, cameraTranslation, maxstep));
				float weight11 = historyFraction.x * historyFraction.y * float(opaque_depth_history_agrees(expectedHistoryOpaqueDepth, opaqueDepthMetadata, data11, opaqueDepthTolerance, traveledDistance, cameraTranslation, maxstep));
				float validWeight = weight00 + weight10 + weight01 + weight11;
				if (validWeight > 0.00001) {
					currentColorAccumilation = (imageLoad(accum_1A_image, history00).rgba * weight00
						+ imageLoad(accum_1A_image, history10).rgba * weight10
						+ imageLoad(accum_1A_image, history01).rgba * weight01
						+ imageLoad(accum_1A_image, history11).rgba * weight11) / validWeight;
					currentDataAccumilation = (data00 * weight00 + data10 * weight10 + data01 * weight01 + data11 * weight11) / validWeight;
				} else if (ring_jitter_opaque_neighborhood_matches(true, clampedUV, size, expectedHistoryOpaqueDepth, opaqueDepthTolerance)) {
					// Engine jitter: every bilinear tap failed the exact test but the 3x3 neighbourhood agrees, so keep the nearest history texel.
					currentColorAccumilation = imageLoad(accum_1A_image, clampedUV).rgba;
					currentDataAccumilation = imageLoad(accum_2A_image, clampedUV).rgba;
				} else {
					gameplayVisibilityRejected = true;
				}
			} else if (!opaque_depth_history_agrees(expectedHistoryOpaqueDepth, opaqueDepthMetadata, currentDataAccumilation, opaqueDepthTolerance, traveledDistance, cameraTranslation, maxstep)
					&& !ring_jitter_opaque_neighborhood_matches(true, clampedUV, size, expectedHistoryOpaqueDepth, opaqueDepthTolerance)) {
				gameplayVisibilityRejected = true;
			}
		}
		diagnosticAlphaMismatch = abs(currentColorAccumilation.a - lightColor.a);

		// bool lastDepthBreak = currentDataAccumilation.a < 0.0;
		float if_break = max(float(override), historyOutsideImage ? 1.0 : 0.0);
		// if_break = max(if_break, lightColor.a - 0.8 - currentColorAccumilation.a); //Lets super high accumilation still look passable, but at the cost of less soft edges.

		if (if_break > 0.0 || gameplayVisibilityRejected || (!gameplayTemporalHistory && !menuStaticHistory && currentDepthBreak != currentDataAccumilation.a && abs(initialdistanceSample - currentDataAccumilation.r) > travelspeed * 0.5)){
			diagnosticHistoryReason = if_break > 0.0 ? 0.75 : 0.5; // visibility/depth-break reject
			currentColorAccumilation = lightColor;
			//debugCollisions = true;
			currentDataAccumilation.r = initialdistanceSample;
			currentDataAccumilation.g = traveledDistance;
			currentDataAccumilation.b = finalDensityDistance;
		}
		else{
			bool terminalJitterAgrees = ring_cloud_history_terminal_jitter(
				genericData.data.history_controls.x > 0.5 && opaquePointAgrees,
				genericData.data.temporal_controls.x, reprojectedPixelMotion,
				currentDataAccumilation.g, traveledDistance, currentDataAccumilation.b, finalDensityDistance,
				currentDataAccumilation.a, opaqueDepthMetadata, cameraTranslation, maxstep, opaqueDepthTolerance);
			bool validatedJitterAgrees = ring_cloud_history_validated_jitter(
				genericData.data.history_controls.x > 0.5 && opaquePointAgrees
					&& ((opaqueDepthMetadata < 0.0 && currentDataAccumilation.a == -1.0)
						|| (opaqueDepthMetadata > 0.0 && currentDataAccumilation.a > 0.0)),
				genericData.data.menu_temporal_controls.z > 0.5,
				genericData.data.temporal_controls.x, cameraTranslation, maxstep);
			float agreementDecay = gameplayTemporalHistory ? ((terminalJitterAgrees || validatedJitterAgrees) ? accumdecay
				: ring_cloud_history_compatible_weight(currentColorAccumilation, lightColor, currentDataAccumilation.b, finalDensityDistance, maxstep, accumdecay, genericData.data.history_controls.z > 0.5, vec2(genericData.data.cloud_mask_controls.y, genericData.data.cloud_mask_controls.z))) : (menuStaticHistory ? accumdecay : (coherentBilinearHistory
				? coherent_history_decay(currentColorAccumilation, lightColor, accumdecay, depthBreakAgrees, cloudDistanceAgrees, reprojectedPixelMotion)
				: temporal_alpha_agreement_decay(currentColorAccumilation, lightColor, accumdecay)));
			diagnosticHistoryWeight = agreementDecay;
			diagnosticHistoryReason = agreementDecay < accumdecay ? 0.25 : 0.0; // alpha fade / accepted
			currentColorAccumilation = (currentColorAccumilation * agreementDecay) + lightColor * (1.0 - agreementDecay);

			currentDataAccumilation.r = mix(currentDataAccumilation.r, initialdistanceSample, (1.0 - agreementDecay));
			currentDataAccumilation.g = mix(currentDataAccumilation.g, traveledDistance,  (1.0 - agreementDecay));
			currentDataAccumilation.b = mix(currentDataAccumilation.b, finalDensityDistance,  (1.0 - agreementDecay));
		}

		currentDataAccumilation.a = gameplayTemporalHistory ? opaqueDepthMetadata : currentDepthBreak;

		imageStore(accum_1B_image, uv, currentColorAccumilation);
		imageStore(accum_2B_image, uv, currentDataAccumilation);
	}
	else{
		currentColorAccumilation = coherentBilinearHistory
			? mix(mix(imageLoad(accum_1B_image, history00).rgba, imageLoad(accum_1B_image, history10).rgba, historyFraction.x), mix(imageLoad(accum_1B_image, history01).rgba, imageLoad(accum_1B_image, history11).rgba, historyFraction.x), historyFraction.y)
			: imageLoad(accum_1B_image, clampedUV).rgba;
		currentDataAccumilation = imageLoad(accum_2B_image, clampedUV).rgba;
		float currentDepthBreak = float(depthBreak);
		bool depthBreakAgrees = currentDepthBreak == currentDataAccumilation.a;
		bool cloudDistanceAgrees = abs(finalDensityDistance - currentDataAccumilation.b)
			<= maxstep + 0.1 * max(finalDensityDistance, currentDataAccumilation.b);
		bool opaquePointAgrees = !opaqueHistoryOutside && (ring_cloud_history_opaque_matches(expectedHistoryOpaqueDepth,
			imageLoad(accum_2B_image, opaqueHistoryPixel).a, opaqueDepthTolerance)
			|| ring_jitter_opaque_neighborhood_matches(false, opaqueHistoryPixel, size, expectedHistoryOpaqueDepth, opaqueDepthTolerance));
		bool gameplayVisibilityRejected = gameplayTemporalHistory && genericData.data.history_controls.x > 0.5 && !opaquePointAgrees;
		if (gameplayTemporalHistory && !historyOutsideImage && genericData.data.history_controls.x > 0.5) {
			if (coherentBilinearHistory) {
				vec4 data00 = imageLoad(accum_2B_image, history00).rgba;
				vec4 data10 = imageLoad(accum_2B_image, history10).rgba;
				vec4 data01 = imageLoad(accum_2B_image, history01).rgba;
				vec4 data11 = imageLoad(accum_2B_image, history11).rgba;
				float weight00 = (1.0 - historyFraction.x) * (1.0 - historyFraction.y) * float(opaque_depth_history_agrees(expectedHistoryOpaqueDepth, opaqueDepthMetadata, data00, opaqueDepthTolerance, traveledDistance, cameraTranslation, maxstep));
				float weight10 = historyFraction.x * (1.0 - historyFraction.y) * float(opaque_depth_history_agrees(expectedHistoryOpaqueDepth, opaqueDepthMetadata, data10, opaqueDepthTolerance, traveledDistance, cameraTranslation, maxstep));
				float weight01 = (1.0 - historyFraction.x) * historyFraction.y * float(opaque_depth_history_agrees(expectedHistoryOpaqueDepth, opaqueDepthMetadata, data01, opaqueDepthTolerance, traveledDistance, cameraTranslation, maxstep));
				float weight11 = historyFraction.x * historyFraction.y * float(opaque_depth_history_agrees(expectedHistoryOpaqueDepth, opaqueDepthMetadata, data11, opaqueDepthTolerance, traveledDistance, cameraTranslation, maxstep));
				float validWeight = weight00 + weight10 + weight01 + weight11;
				if (validWeight > 0.00001) {
					currentColorAccumilation = (imageLoad(accum_1B_image, history00).rgba * weight00
						+ imageLoad(accum_1B_image, history10).rgba * weight10
						+ imageLoad(accum_1B_image, history01).rgba * weight01
						+ imageLoad(accum_1B_image, history11).rgba * weight11) / validWeight;
					currentDataAccumilation = (data00 * weight00 + data10 * weight10 + data01 * weight01 + data11 * weight11) / validWeight;
				} else if (ring_jitter_opaque_neighborhood_matches(false, clampedUV, size, expectedHistoryOpaqueDepth, opaqueDepthTolerance)) {
					// Engine jitter: every bilinear tap failed the exact test but the 3x3 neighbourhood agrees, so keep the nearest history texel.
					currentColorAccumilation = imageLoad(accum_1B_image, clampedUV).rgba;
					currentDataAccumilation = imageLoad(accum_2B_image, clampedUV).rgba;
				} else {
					gameplayVisibilityRejected = true;
				}
			} else if (!opaque_depth_history_agrees(expectedHistoryOpaqueDepth, opaqueDepthMetadata, currentDataAccumilation, opaqueDepthTolerance, traveledDistance, cameraTranslation, maxstep)
					&& !ring_jitter_opaque_neighborhood_matches(false, clampedUV, size, expectedHistoryOpaqueDepth, opaqueDepthTolerance)) {
				gameplayVisibilityRejected = true;
			}
		}
		diagnosticAlphaMismatch = abs(currentColorAccumilation.a - lightColor.a);
		
		// bool lastDepthBreak = currentDataAccumilation.a < 0.0;
		float if_break = max(float(override), historyOutsideImage ? 1.0 : 0.0);
		// if_break = max(if_break, lightColor.a - 0.8 - currentColorAccumilation.a); //Lets super high accumilation still look passable, but at the cost of less soft edges.

		if (if_break > 0.0 || gameplayVisibilityRejected || (!gameplayTemporalHistory && !menuStaticHistory && currentDepthBreak != currentDataAccumilation.a && abs(initialdistanceSample - currentDataAccumilation.r) > travelspeed * 0.5)){
			diagnosticHistoryReason = if_break > 0.0 ? 0.75 : 0.5; // visibility/depth-break reject
			currentColorAccumilation = lightColor;
			//debugCollisions = true;
			currentDataAccumilation.r = initialdistanceSample;
			currentDataAccumilation.g = traveledDistance;
			currentDataAccumilation.b = finalDensityDistance;
		}
		else{
			bool terminalJitterAgrees = ring_cloud_history_terminal_jitter(
				genericData.data.history_controls.x > 0.5 && opaquePointAgrees,
				genericData.data.temporal_controls.x, reprojectedPixelMotion,
				currentDataAccumilation.g, traveledDistance, currentDataAccumilation.b, finalDensityDistance,
				currentDataAccumilation.a, opaqueDepthMetadata, cameraTranslation, maxstep, opaqueDepthTolerance);
			bool validatedJitterAgrees = ring_cloud_history_validated_jitter(
				genericData.data.history_controls.x > 0.5 && opaquePointAgrees
					&& ((opaqueDepthMetadata < 0.0 && currentDataAccumilation.a == -1.0)
						|| (opaqueDepthMetadata > 0.0 && currentDataAccumilation.a > 0.0)),
				genericData.data.menu_temporal_controls.z > 0.5,
				genericData.data.temporal_controls.x, cameraTranslation, maxstep);
			float agreementDecay = gameplayTemporalHistory ? ((terminalJitterAgrees || validatedJitterAgrees) ? accumdecay
				: ring_cloud_history_compatible_weight(currentColorAccumilation, lightColor, currentDataAccumilation.b, finalDensityDistance, maxstep, accumdecay, genericData.data.history_controls.z > 0.5, vec2(genericData.data.cloud_mask_controls.y, genericData.data.cloud_mask_controls.z))) : (menuStaticHistory ? accumdecay : (coherentBilinearHistory
				? coherent_history_decay(currentColorAccumilation, lightColor, accumdecay, depthBreakAgrees, cloudDistanceAgrees, reprojectedPixelMotion)
				: temporal_alpha_agreement_decay(currentColorAccumilation, lightColor, accumdecay)));
			diagnosticHistoryWeight = agreementDecay;
			diagnosticHistoryReason = agreementDecay < accumdecay ? 0.25 : 0.0; // alpha fade / accepted
			currentColorAccumilation = (currentColorAccumilation * agreementDecay) + lightColor * (1.0 - agreementDecay);

			currentDataAccumilation.r = mix(currentDataAccumilation.r, initialdistanceSample, (1.0 - agreementDecay));
			currentDataAccumilation.g = mix(currentDataAccumilation.g, traveledDistance,  (1.0 - agreementDecay));
			currentDataAccumilation.b = mix(currentDataAccumilation.b, finalDensityDistance,  (1.0 - agreementDecay));
		}

		currentDataAccumilation.a = gameplayTemporalHistory ? opaqueDepthMetadata : currentDepthBreak;

		imageStore(accum_1A_image, uv, currentColorAccumilation);
		imageStore(accum_2A_image, uv, currentDataAccumilation);
	}
	// if (linear_depth < maxTheoreticalStep){
	// 	float nearby_blend = smoothstep(maxstep, minstep, abs(currentDataAccumilation.b - linear_depth));
	// 	depthFade = 1.0 - clamp(linear_depth - maxstep - currentDataAccumilation.b, 0.0, minstep) / minstep;
		
	// 	currentColorAccumilation.a = mix(currentColorAccumilation.a, 0.0, nearby_blend);
	// 	// currentDataAccumilation.g = mix(currentDataAccumilation.g, maxTheoreticalStep, clamp(finalDensityDistance - linear_depth, 0.0, 1.0) * depthFade * nearby_blend);
	// 	//currentColorAccumilation.rgb = mix(currentColorAccumilation.rgb, vec3(1.0, 0.0, 0.0), float(depthFade));
	// }
	// // currentDataAccumilation.g = mix(currentDataAccumilation.g, maxTheoreticalStep, clamp(finalDensityDistance - linear_depth, 0.0, 1.0) * depthFade);
	
	// if (depthBreak){
	// 	currentColorAccumilation.rgb = vec3(1.0, 0.0, 0.0);
	// }

	// currentDataAccumilation.g += maxTheoreticalStep * float(depthBreak);

	currentDataAccumilation.r = min(currentDataAccumilation.r, initialdistanceSample);

	if (RING_INTERLEAVE > 0) {
		// CloudInterleave: publish this frame's raw march result for the fill pass (before the history blend and the haze).
		ivec2 blockUV = ivec2(gl_GlobalInvocationID.xy);
		vec3 blockTransient = (any(isnan(transientLightColor)) || any(isinf(transientLightColor))) ? vec3(0.0) : transientLightColor;
		imageStore(interleave_steady_image, blockUV, lightColor);
		imageStore(interleave_transient_image, blockUV, vec4(blockTransient, megaPackedFactors));
		imageStore(interleave_data_image, blockUV, vec4(initialdistanceSample, traveledDistance, finalDensityDistance, opaqueDepthMetadata));
	}
	
	// Diagnostic output is intentionally after ping-pong stores: it cannot feed
	// back into accumulation. R=used history weight; G=reason (0 accepted,
	// .25 alpha fade, .5 visibility/depth-break, .75 invalid/offscreen); B=mismatch;
	// A=current ray-marched alpha.
	if (genericData.data.menu_temporal_controls.w > 0.5) {
		imageStore(output_color_image, uv, vec4(diagnosticHistoryWeight, diagnosticHistoryReason,
			diagnosticAlphaMismatch, lightColor.a));
	} else {
		// Transient point lights never enter the persistent ping-pong history.
		if (!any(isnan(transientLightColor)) && !any(isinf(transientLightColor))) {
			currentColorAccumilation.rgb += transientLightColor;
		}
		// Apply once after history has been stored. The temporal buffers remain
		// live-profile independent, while the published backdrop matches display.
		if (genericData.data.temporal_controls.z <= 0.0 && genericData.data.temporal_controls.w <= 0.0) {
			float haze_distance = clamp(currentDataAccumilation.b, 0.0, max(0.0, linear_depth));
			if (megaHazeOn) {
				// Every ray of the megastorm variant takes the unified haze path factors (the terrain gets the rule everywhere); all-zero levels give factors 1.
				currentColorAccumilation.rgb = ring_cloud_atmosphere_apply_storm_levels(currentColorAccumilation.rgb, rayOrigin, raydirection, haze_distance,
					genericData.data.ring_atmosphere_profile, megaLevels, genericData.data.ring_storm_ambient.w, ringMegastormStrip.c);
			} else {
				currentColorAccumilation.rgb = ring_cloud_atmosphere_apply(currentColorAccumilation.rgb, rayOrigin, raydirection, haze_distance, genericData.data.ring_atmosphere_profile);
			}
		}
		imageStore(output_color_image, uv, currentColorAccumilation);
	}
	// The private accumulation alpha holds opaque depth for gameplay history.
	// output_data alpha is stable opaque metadata for the post blur: log2(1 +
	// opaque depth), or -8 for sky. Unlike the per-frame depthBreak flag it does
	// not flicker with ray-start jitter over terrain.
	currentDataAccumilation.a = has_opaque_depth ? log2(1.0 + max(linear_depth, 0.0)) : -8.0;
	imageStore(output_data_image, uv, currentDataAccumilation);
	//}
}
