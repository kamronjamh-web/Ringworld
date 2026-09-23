#[compute]
#version 450
#define PI 3.141592
#define ABSORPTION_COEFFICIENT 0.9

#include "./CloudsInc.comp"

// Invocations in the (x, y, z) dimension
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

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
};

layout(binding = 16, std430) restrict buffer SamplePointsBuffer {
	vec4 SamplePoints[256];
};


layout(binding = 17, std140) uniform SceneDataBlock {
	SceneData data;
	SceneData prev_data;
} scene_data_block;

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

float localized_storm_density(vec3 cloudPosition, float normalizedHeight) {
	float influence = clamp(genericData.data.storm_shape.z, 0.0, 1.0);
	float profile = genericData.data.storm_shape.w;
	int physicalProfile = int(round(genericData.data.storm_detail.w));
	float majorRadius = genericData.data.storm_system.z;
	float minorRadius = genericData.data.storm_system.w;
	if (influence <= 0.001 || majorRadius <= 1.0 || minorRadius <= 1.0) {
		return 0.0;
	}

	float aroundDelta = cloudPosition.x - genericData.data.storm_system.x;
	float circumference = genericData.data.cloud_frame_up.w * 6.28318530718;
	if (circumference > 1.0) {
		aroundDelta = mod(aroundDelta + circumference * 0.5, circumference)
			- circumference * 0.5;
	}
	float acrossDelta = cloudPosition.z - genericData.data.storm_system.y;
	float orientationCosine = genericData.data.storm_shape.x;
	float orientationSine = genericData.data.storm_shape.y;
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
	float pillarRadius = max(1000.0, genericData.data.storm_detail.x);
	float anvilRadiusMultiplier = max(1.2, genericData.data.storm_detail.y);
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
		float anvilStart = clamp(genericData.data.storm_detail.z, 0.5, 0.9);
		float anvilVertical = smoothstep(anvilStart, min(0.96, anvilStart + 0.075), normalizedHeight)
			* (1.0 - smoothstep(0.955, 1.0, normalizedHeight));
		sculptedDensity = max(sculptedDensity, anvil * anvilVertical * 0.94);
	}

	return sculptedDensity * systemMask * influence;
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
	worldPosition = to_cloud_space(worldPosition);
	vec3 canonicalCloudPosition = worldPosition;
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
	vec4 gradientSample = texture(heightmask, vec2(clampedWorldHeight, 0.5)).rgba;
	

	float edgeFade = min(smoothstep(0.0, 0.1, clampedWorldHeight), smoothstep(1.0, 0.9, clampedWorldHeight));
	float extraLargeShape = extralargeNoiseValue * gradientSample.b;

	// Only directional sun transmittance may omit fine erosion. AO and point
	// lightning retain their original density field even though both are sampled
	// through this function.
	float smallShape = coarseSunLighting ? 0.0 : texture(noise_small, (worldPosition - smallNoisePos) / smallnoisescale).r;

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

	float largeShape = texture(large_noise, (worldPosition - largeNoisePos) / largenoisescale).r * extraLargeShape;
	largeShape = smoothstep(coverage , coverage - 0.1, 1.0 - (largeShape * gradientSample.r)) + max(effectorAdditive, 0.0);
	vec4 mediumShapes = texture(noise_medium, (worldPosition - mediumNoisePos) / mediumnoisescale).rgba;
	float mediumshape = 1.0 - mediumShapes.b;
	smallShape = smallShape * gradientSample.g * pow((1.0 - mediumshape), smallscalePower);
	

	float shape = mediumshape + max(effectorAdditive, 0.0);
	shape = clamp(remap(shape, 1.0 - largeShape, 1.0, 0.0, 1.0), 0.0, 1.0);
	shape = clamp(remap(shape, smallShape, 1.0, 0.0, 1.0), 0.0, 1.0);
	shape += min(effectorAdditive, 0.0);
	float backgroundDensity = clamp(shape * edgeFade, 0.0, 1.0);
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
	float stormDensity = localized_storm_density(canonicalCloudPosition, stormHeight)
		* stormAltitudeMask;
	stormDensity *= mix(0.62, 1.0, mediumShapes.r);

	return clamp(max(backgroundDensity, stormDensity), 0.0, 1.0)
		* ring_side_wall_mask(canonicalCloudPosition.z);
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
	float density = 0.0;
	float stepCountFloat = max(float(stepCount) * lod, 2.0);
	float actualDistance = mix(stepDistance * 4.0, stepDistance, lod);
	float eachShortStep = actualDistance / (float(stepCount) / stepCountFloat) / stepCountFloat;
	float traveledDistance = 0.0;
	
	float sunUpValue = 1.0 - sunUpWeight;
	float eachStepWeight = 1.0 / stepCountFloat;

	float heightGradient = 0.0;
	float thisDensity = 0.0;
	float count = 0.0;
	vec3 curPos = worldPosition;
	for (float i = 0.0; i < stepCountFloat; i++) {
		traveledDistance = mix(eachShortStep, actualDistance, clamp(quadraticOut(i / stepCountFloat), 0.0, 1.0));
		curPos = worldPosition + sunDirection * traveledDistance;
		vec3 cloudPos = to_cloud_space(curPos);

		if (density < 1.0 && clamp(cloudPos.y, cloudfloor, cloudceiling) == cloudPos.y){
			heightGradient = remap(cloudPos.y, cloudfloor, cloudceiling, 0.0, 1.0);
			
			heightGradient = clamp(smoothstep(sunUpValue - 0.1, sunUpValue, heightGradient), 0.0, 1.0);
			float extraLargeShape = texture(extra_large_noise, (cloudPos.xz - extralargeNoisePos.xz) / extralargenoisescale).a;

			thisDensity = sampleScene(largeNoisePos, mediumNoisePos, smallNoisePos, curPos, cloudceiling, cloudfloor, extraLargeShape, largenoisescale, mediumnoisescale, smallnoisescale, coverage, smallscalePower, curlPower, lod, true, coarseSunLighting) * densityMultiplier * eachStepWeight;
			// if (thisDensity <= 0.0){
			// 	break;
			// }
			density += mix(1.0, thisDensity, heightGradient);
		}
		else{
			break;
		}
	}

	return density;
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

// Ray-start jitter can flip soft cloud coverage between adjacent moving-menu
// samples. Only retain a bounded floor when reprojection is subpixel and the
// same cloud body/depth-break state remains coherent; all other cases keep the
// legacy alpha rejection, including teleports and silhouette/disocclusion loss.
float menu_coherent_history_decay(vec4 history, vec4 current, float decay,
		bool depthBreakAgrees, bool cloudDistanceAgrees, float pixelMotion) {
	float legacyDecay = temporal_alpha_agreement_decay(history, current, decay);
	if (!depthBreakAgrees || !cloudDistanceAgrees) {
		return legacyDecay;
	}
	float subpixelConfidence = 1.0 - smoothstep(0.5, 2.0, pixelMotion);
	float jitterConfidence = clamp(genericData.data.temporal_controls.x, 0.0, 1.0);
	float retainedFloor = decay * 0.70 * subpixelConfidence * jitterConfidence;
	return max(legacyDecay, retainedFloor);
}

void main() {
	//SETTING UP UVS/RAY DATA
	ivec2 uv = ivec2(gl_GlobalInvocationID.xy);
	ivec2 size = ivec2(genericData.data.raster_size);

	// Prevent reading/writing out of bounds.
	if (uv.x >= size.x || uv.y >= size.y) {
		return;
	}
	
	vec2 depthUV = (uv + 0.5) / vec2(size);
	float depth = texture(depth_image, depthUV).r;

	vec4 view = genericData.data.current_camera_inv_projection * vec4(depthUV*2.0-1.0,depth,1.0);
	view.xyz /= view.w;
	float linear_depth = length(view); //used to calculate depth based on the view angle, idk just works.
	//4.4 doesn't work with this
	if (linear_depth >= scene_data_block.data.z_far){ 
		linear_depth *= 100.0;
	}
	
	// Convert screen coordinates to normalized device coordinates
	vec2 clipUV = vec2(depthUV.x, depthUV.y);
	vec2 ndc = clipUV * 2.0 - 1.0;	
	// Convert NDC to view space coordinates
	vec4 clipPos = vec4(ndc, 0.0, 1.0);
	vec4 viewPos = genericData.data.current_camera_inv_projection * clipPos;
	viewPos.xyz /= viewPos.w;
	
	vec3 rd_world = normalize(viewPos.xyz);
	rd_world = mat3(genericData.data.current_camera_transform) * rd_world;
	// Define the ray properties
	
	vec3 raydirection = normalize(rd_world);
	vec3 rayOrigin = genericData.data.current_camera_transform[3].xyz; //center of camera for the ray origin, not worried about the screen width playing in, as it's for clouds.


	//DITHER

	// expirements with interleved gradient noise.
	// float ditherScale = 40.037;
	// vec3 ditherUV = vec3(depthUV.x * ditherScale , depthUV.y * ditherScale , genericData.data.time);
	// float smallNoise = texture(dither_small, ditherUV).r;
	// vec3 ign_noise_uv = vec3(float(uv.x), fract(genericData.data.time) * 2.0 - 1.0, float(uv.y));
	// float ign_noise = fract(52.9829189 * fract(dot(ign_noise_uv, vec3(0.006711056, 0.00583715, 1.61803398875))));
	// float ditherValue = ign_noise;

	float ditherScale = 40.037;
	vec3 ditherUV = vec3(depthUV.x * ditherScale , depthUV.y * ditherScale , genericData.data.time);
	float smallNoise = texture(dither_small, ditherUV).r;

	float ditherValue = smallNoise;

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

	vec3 extralargeNoisePos = genericData.data.extralargenoiseposition;
	vec3 largeNoisePos = genericData.data.largenoiseposition;
	vec3 mediumNoisePos = genericData.data.mediumnoiseposition;
	vec3 smallNoisePos = genericData.data.smallnoiseposition;

	float extralargenoiseScale = genericData.data.extralargenoisescale;
	float largenoiseScale = genericData.data.large_noise_scale;
	float mediumnoiseScale = genericData.data.medium_noise_scale;
	float smallnoiseScale = genericData.data.small_noise_scale;

	float minstep = genericData.data.min_step_distance;
	float maxstep = genericData.data.max_step_distance;
	

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
	vec3 paintedColor = vec3(0.0);
	float initialdistanceSample = 0.0;

	float lightingSamples = 0.0;
	float atmoSamples = 0.0;

	float density = 0.0;
	float ambient = 0.0;
	float depthFade = 1.0;
	float newdensity = 0.0;
	vec3 curPos = vec3(0.0);
	float previousCloudAltitude = cloud_altitude(rayOrigin);
	bool cameraStartsInsideCloudAltitudeBand = previousCloudAltitude >= cloudfloor
		&& previousCloudAltitude <= cloudceiling;
	bool enteredCloudAltitudeBand = false;
	float cloudBandTraversalDirection = 0.0;
	
	float curLod = 1.0;
	float samplePosCount = clamp(genericData.data.samplePointsCount, 0.0, 256.0);

	if (samplePosCount > 0 && uv == ivec2(0)){
		for (int i = 0; i < samplePosCount; i++){
			curPos = SamplePoints[i].xyz;
			vec3 cloudPos = to_cloud_space(curPos);
			vec4 maskSample = texture(extra_large_noise, (cloudPos.xz - extralargeNoisePos.xz) / extralargenoiseScale);
			//ceilingSample = mix(halfCeiling, cloudceiling, maskSample.a);
			//ceilingSample = cloudceiling;
			
			SamplePoints[i].w = pow(sampleScene(largeNoisePos, mediumNoisePos, smallNoisePos, curPos, cloudceiling, cloudfloor, maskSample.a, largenoiseScale, mediumnoiseScale, smallnoiseScale, coverage, smallNoiseMultiplier, curlPower, 1.0, false, false) * densityMultiplier, sharpness);
		}
	}

	// Skip only empty, whole maxstep lattice intervals before the first local
	// shell entry. The jittered first point and all exit-state bookkeeping stay
	// on their original path.
	vec3 entryOrigin = rayOrigin - genericData.data.cloud_frame_origin.xyz;
	int entrySkip = cloud_entry_skip_steps(
		entryOrigin, raydirection, genericData.data.cloud_frame_around.xyz,
		genericData.data.cloud_frame_up.xyz, genericData.data.cloud_frame_up.w,
		cloudfloor, cloudceiling, newStep, maxstep,
		stepCount, linear_depth);
	if (entrySkip > 0) {
		traveledDistance = newStep + float(entrySkip) * maxstep;
		newStep = maxstep;
	}
	for (int i = entrySkip; i < stepCount; i++) {
		
		if (traveledDistance > linear_depth){
			// depthFade = 1.0 - smoothstep(linear_depth - newStep, linear_depth, traveledDistance);
			depthBreak = true;
		}
		
		curPos = rayOrigin + raydirection * traveledDistance;
		vec3 cloudPos = to_cloud_space(curPos);
		bool inCloudAltitudeBand = clamp(cloudPos.y, cloudfloor, cloudceiling) == cloudPos.y;
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
				vec3 radialDelta = curPos - genericData.data.cloud_frame_origin.xyz;
				float tangent = dot(radialDelta, genericData.data.cloud_frame_around.xyz);
				float inward = dot(radialDelta, genericData.data.cloud_frame_up.xyz);
				float radialLength = max(length(vec2(tangent, genericData.data.cloud_frame_up.w - inward)), 1.0);
				float analyticAltitudeMotion = ((genericData.data.cloud_frame_up.w - inward)
					* dot(raydirection, genericData.data.cloud_frame_up.xyz)
					- tangent * dot(raydirection, genericData.data.cloud_frame_around.xyz)) / radialLength;
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
		vec4 maskSample = texture(extra_large_noise, (cloudPos.xz - extralargeNoisePos.xz) / extralargenoiseScale);
		//ceilingSample = mix(halfCeiling, cloudceiling, maskSample.a);
		//ceilingSample = cloudceiling;
		
		//sampleAtmospherics(curPos, atmosphericHeight, newStep, Rayleighscaleheight, Miescaleheight, RayleighScatteringCoef, MieScatteringCoef, atmosphericDensity, density, totalRlh, totalMie, iOdRlh, iOdMie); 
		atmoSamples += 1.0;
		if (inCloudAltitudeBand){

			curLod = 1.0 - clamp(traveledDistance / lodMaxDistance, 0.0, 1.0);
			// newdensity = sampleSceneCoarse(largeNoisePos, curPos, cloudceiling, cloudfloor, maskSample.a, largenoiseScale, coverage, curLod);
			newdensity = pow(sampleScene(largeNoisePos, mediumNoisePos, smallNoisePos, curPos, cloudceiling, cloudfloor, maskSample.a, largenoiseScale, mediumnoiseScale, smallnoiseScale, coverage, smallNoiseMultiplier, curlPower, curLod, false, false) * densityMultiplier, sharpness) * depthFade;
			// if (newdensity > 0.0) {
			// 	newdensity = pow(sampleScene(largeNoisePos, mediumNoisePos, smallNoisePos, curPos, cloudceiling, cloudfloor, maskSample.a, largenoiseScale, mediumnoiseScale, smallnoiseScale, coverage, smallNoiseMultiplier, curlPower, curLod, false) * densityMultiplier, sharpness) * depthFade;
			// }
			
			
			if (newdensity > 0.0){
				if (initialdistanceSample == 0.0){
					initialdistanceSample = traveledDistance;
				}

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
					densitySample = BeersLaw(lightingStepDistance, densitySample * henyeygreenstein);
					//densitySample = Powder(lightingStepDistance, densitySample);
					float thisStepLightingWeight = (pow(densitySample, lightingSharpness)) * sunUpWeight;
					

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
						//float densitySample = 1.0 - newdensity;
						float densitySample = sampleLighting(3, curPos, extralargeNoisePos, largeNoisePos, mediumNoisePos, smallNoisePos, lightToOriginDelta, densityMultiplier, 1.0, min(maxstep, lightDistanceWeight), cloudceiling, cloudfloor, extralargenoiseScale, largenoiseScale, mediumnoiseScale, smallnoiseScale, coverage, smallNoiseMultiplier, curlPower, curLod, false);
						
						float henyeygreenstein = pow(HenyeyGreenstein(genericData.data.anisotropy, dot(lightToOriginDelta, raydirection)), mix(1.0, 2.0, 1.0 - genericData.data.anisotropy)); 
						densitySample = BeersLaw(lightDistanceWeight, densitySample * henyeygreenstein);
						densitySample = mix(densitySample, newdensity, 0.5) * powderEffect;
						lightDistanceWeight = lightDistanceWeight / pointLights[lightI].position.w;
						lightDistanceWeight = pointLights[lightI].color.a * pow((1.0 - lightDistanceWeight), 2.2) * densitySample;


						lightColor.rgb += pow(pointLights[lightI].color.rgb * lightDistanceWeight, vec3(2.2));
					}
				}
				
				if (aobase.a > 0.0){
				ambient += sampleScene(largeNoisePos, mediumNoisePos, smallNoisePos, curPos + genericData.data.cloud_frame_up.xyz * minstep, cloudceiling, cloudfloor, maskSample.a, largenoiseScale, mediumnoiseScale, smallnoiseScale, coverage, smallNoiseMultiplier, curlPower, curLod, true, false) * densityMultiplier * lightingdensityMultiplier ;
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

			if (i == 0){
				newdensity = mix(newdensity, 0.0, traveledDistance / maxstep);
			}

			density += newdensity;
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
			float nextAltitude = cloud_altitude(curPos + raydirection * maxstep);
			float exitAltitudeDirection = nextAltitude - cloudPos.y;
			bool leavingAbove = cloudPos.y > cloudceiling && exitAltitudeDirection > 0.0;
			bool leavingBelow = cloudPos.y < cloudfloor && exitAltitudeDirection < 0.0;
			if (enteredCloudAltitudeBand || leavingAbove || leavingBelow){
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
		}
		
		traveledDistance += newStep;
		if (depthBreak){
			break;
		}
		
	}

	// GLSL leaves smoothstep undefined when edge0 is greater than edge1.
	// Express the same near-to-far fade with ordered edges so cloud alpha is
	// stable across Vulkan drivers.
	float cloudRangeFade = 1.0 - smoothstep(
		minstep * stepCount,
		maxstep * stepCount,
		traveledDistance);
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
	ambientLight = max(ambientLight, genericData.data.ambientLightColor.rgb * 0.35);
	if (any(isnan(lightColor.rgb)) || any(isinf(lightColor.rgb))) {
		lightColor.rgb = vec3(0.0);
	}
	lightColor.rgb += ambientLight;
	// lightColor.rgb = ambientLight + clamp(lightColor.rgb / lightingSamples, vec3(0.0), vec3(1.0));
	lightColor.a = density;

	vec3 physicalFogColor = lightColor.rgb;
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
	lightColor.rgb = max(lightColor.rgb, ambientLight);
	//lightColor.rgb = physicalFogColor;
	// initialdistanceSample = max(initialdistanceSample, 0.0);


	//accumulation preperation:
	float finalDensityDistance = min(traveledDistance, highestDensityDistance);
	// Reproject the body of the cloud, not the ray's exit point.  The exit can
	// sit kilometres behind the visible density and makes history appear to
	// trail the camera during rotation.
	vec3 worldFinalPos = rayOrigin + raydirection * finalDensityDistance;
	vec3 delta = rayOrigin - genericData.data.previous_camera_origin.xyz;
	
	vec4 reprojectedScreenPos = vec4(0.0);

	// The menu camera stays near local origin while its cloud frame scrolls
	// around the ring. Rebuild the cloud point in the paired previous curved
	// frame from small arc/wind deltas instead of absolute canonical positions.
	vec3 previousWorldFinalPos = worldFinalPos;
	if (genericData.data.menu_optimization_controls.y > 0.5
		&& (abs(genericData.data.menu_optimization_controls.z) > 0.00001
			|| abs(genericData.data.menu_optimization_controls.w) > 0.00001)) {
		vec3 frameDelta = worldFinalPos - genericData.data.cloud_frame_origin.xyz;
		float ringRadius = max(genericData.data.cloud_frame_up.w, 1.0);
		float tangent = dot(frameDelta, genericData.data.cloud_frame_around.xyz);
		float inward = dot(frameDelta, genericData.data.cloud_frame_up.xyz);
		float outward = ringRadius - inward;
		float angleDelta = genericData.data.menu_optimization_controls.z / ringRadius;
		float sinHalf = sin(angleDelta * 0.5);
		float oneMinusCos = 2.0 * sinHalf * sinHalf;
		float sine = sin(angleDelta);
		float tangentDelta = tangent * (-oneMinusCos) + outward * sine;
		float inwardDelta = outward * oneMinusCos + tangent * sine;
		previousWorldFinalPos = worldFinalPos
			+ genericData.data.cloud_frame_around.xyz * tangentDelta
			+ genericData.data.cloud_frame_up.xyz * inwardDelta
			+ genericData.data.cloud_frame_across.xyz * genericData.data.menu_optimization_controls.w;
	}
	vec4 reprojectedClipPos = genericData.data.previous_camera_view * vec4(previousWorldFinalPos, 1.0);
	reprojectedClipPos.z -= 0.01;
	if (reprojectedClipPos.z > 0.0){
		override = true;
	}
	reprojectedScreenPos = genericData.data.previous_camera_projection * reprojectedClipPos;

	// Convert clip space to normalized device coordinates. A camera/origin jump
	// can leave the previous-frame projection behind or at w=0. Do not let that
	// produce an unbounded history address.
	bool invalidReprojection = abs(reprojectedScreenPos.w) < 0.00001
		|| any(isnan(reprojectedScreenPos))
		|| any(isinf(reprojectedScreenPos));
	if (invalidReprojection) {
		override = true;
		ndc = depthUV * 2.0 - 1.0;
	} else {
		ndc = reprojectedScreenPos.xy / reprojectedScreenPos.w;
	}

	// Convert normalized device coordinates to screen space
	vec2 screen_position = ndc * 0.5 + 0.5;
	//screen_position = clamp(screen_position, vec2(0.0), vec2(1.0));
	screen_position = screen_position - depthUV;

	ivec2 adjustedUV = ivec2(int(screen_position.x * size.x), int(screen_position.y * size.y));
	//float change = length(vec2(adjustedUV));
	adjustedUV += uv; //Size is the screen resolution.
	
	ivec2 clampedUV = clamp(adjustedUV, ivec2(0), size - ivec2(1)); //having two lets me check if clamping it changed the reprojected uv, if it did that means it was offscreen, so rebuild data.
	bool historyOutsideImage = any(notEqual(clampedUV, adjustedUV));
	// Menu history is reprojected at fractional-pixel locations. Blend only its
	// color; retain nearest metadata for silhouette/disocclusion decisions.
	vec2 historyPixel = vec2(uv) + screen_position * vec2(size);
	float reprojectedPixelMotion = length(screen_position * vec2(size));
	ivec2 historyBase = ivec2(floor(historyPixel));
	vec2 historyFraction = fract(historyPixel);
	ivec2 history00 = clamp(historyBase, ivec2(0), size - ivec2(1));
	ivec2 history10 = clamp(historyBase + ivec2(1, 0), ivec2(0), size - ivec2(1));
	ivec2 history01 = clamp(historyBase + ivec2(0, 1), ivec2(0), size - ivec2(1));
	ivec2 history11 = clamp(historyBase + ivec2(1), ivec2(0), size - ivec2(1));
	bool menuStaticHistory = genericData.data.menu_optimization_controls.y > 0.5
		&& genericData.data.menu_temporal_controls.z > 0.5;
	bool menuBilinearHistory = genericData.data.menu_optimization_controls.y > 0.5
		&& !menuStaticHistory && genericData.data.accumilation_decay > 0.0
		&& abs(genericData.data.temporal_controls.z) <= 0.5
		&& genericData.data.temporal_controls.w <= 0.5;
	if (genericData.data.menu_optimization_controls.y > 0.5) {
		// Metadata remains nearest, but menu reprojection rounds the true
		// fractional history coordinate instead of legacy integer motion.
		clampedUV = clamp(ivec2(floor(historyPixel + vec2(0.5))), ivec2(0), size - ivec2(1));
	}
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
	float travelspeed = length(delta) + maxstep;
	//bool debugCollisions = false;
	if (usingaccumA > 0.0){
		currentColorAccumilation = menuBilinearHistory
			? mix(mix(imageLoad(accum_1A_image, history00).rgba, imageLoad(accum_1A_image, history10).rgba, historyFraction.x), mix(imageLoad(accum_1A_image, history01).rgba, imageLoad(accum_1A_image, history11).rgba, historyFraction.x), historyFraction.y)
			: imageLoad(accum_1A_image, clampedUV).rgba;
		currentDataAccumilation = imageLoad(accum_2A_image, clampedUV).rgba;
		diagnosticAlphaMismatch = abs(currentColorAccumilation.a - lightColor.a);

		float currentDepthBreak = float(depthBreak);
		bool depthBreakAgrees = currentDepthBreak == currentDataAccumilation.a;
		bool cloudDistanceAgrees = abs(finalDensityDistance - currentDataAccumilation.b)
			<= maxstep + 0.1 * max(finalDensityDistance, currentDataAccumilation.b);

		// bool lastDepthBreak = currentDataAccumilation.a < 0.0;
		float if_break = max(float(override), historyOutsideImage ? 1.0 : 0.0);
		// if_break = max(if_break, lightColor.a - 0.8 - currentColorAccumilation.a); //Lets super high accumilation still look passable, but at the cost of less soft edges.

		if (if_break > 0.0 || (!menuStaticHistory && currentDepthBreak != currentDataAccumilation.a && abs(initialdistanceSample - currentDataAccumilation.r) > travelspeed * 0.5)){
			diagnosticHistoryReason = if_break > 0.0 ? 0.75 : 0.5; // depth-break reject
			currentColorAccumilation = lightColor;
			//debugCollisions = true;
			currentDataAccumilation.r = initialdistanceSample;
			currentDataAccumilation.g = traveledDistance;
			currentDataAccumilation.b = finalDensityDistance;
		}
		else{
			float agreementDecay = menuStaticHistory ? accumdecay : (menuBilinearHistory
				? menu_coherent_history_decay(currentColorAccumilation, lightColor, accumdecay, depthBreakAgrees, cloudDistanceAgrees, reprojectedPixelMotion)
				: temporal_alpha_agreement_decay(currentColorAccumilation, lightColor, accumdecay));
			diagnosticHistoryWeight = agreementDecay;
			diagnosticHistoryReason = agreementDecay < accumdecay ? 0.25 : 0.0; // alpha fade / accepted
			currentColorAccumilation = (currentColorAccumilation * agreementDecay) + lightColor * (1.0 - agreementDecay);

			currentDataAccumilation.r = mix(currentDataAccumilation.r, initialdistanceSample, (1.0 - agreementDecay));
			currentDataAccumilation.g = mix(currentDataAccumilation.g, traveledDistance,  (1.0 - agreementDecay));
			currentDataAccumilation.b = mix(currentDataAccumilation.b, finalDensityDistance,  (1.0 - agreementDecay));
		}

		currentDataAccumilation.a = currentDepthBreak;

		imageStore(accum_1B_image, uv, currentColorAccumilation);
		imageStore(accum_2B_image, uv, currentDataAccumilation);
	}
	else{
		currentColorAccumilation = menuBilinearHistory
			? mix(mix(imageLoad(accum_1B_image, history00).rgba, imageLoad(accum_1B_image, history10).rgba, historyFraction.x), mix(imageLoad(accum_1B_image, history01).rgba, imageLoad(accum_1B_image, history11).rgba, historyFraction.x), historyFraction.y)
			: imageLoad(accum_1B_image, clampedUV).rgba;
		currentDataAccumilation = imageLoad(accum_2B_image, clampedUV).rgba;
		diagnosticAlphaMismatch = abs(currentColorAccumilation.a - lightColor.a);

		float currentDepthBreak = float(depthBreak);
		bool depthBreakAgrees = currentDepthBreak == currentDataAccumilation.a;
		bool cloudDistanceAgrees = abs(finalDensityDistance - currentDataAccumilation.b)
			<= maxstep + 0.1 * max(finalDensityDistance, currentDataAccumilation.b);
		
		// bool lastDepthBreak = currentDataAccumilation.a < 0.0;
		float if_break = max(float(override), historyOutsideImage ? 1.0 : 0.0);
		// if_break = max(if_break, lightColor.a - 0.8 - currentColorAccumilation.a); //Lets super high accumilation still look passable, but at the cost of less soft edges.

		if (if_break > 0.0 || (!menuStaticHistory && currentDepthBreak != currentDataAccumilation.a && abs(initialdistanceSample - currentDataAccumilation.r) > travelspeed * 0.5)){
			diagnosticHistoryReason = if_break > 0.0 ? 0.75 : 0.5; // depth-break reject
			currentColorAccumilation = lightColor;
			//debugCollisions = true;
			currentDataAccumilation.r = initialdistanceSample;
			currentDataAccumilation.g = traveledDistance;
			currentDataAccumilation.b = finalDensityDistance;
		}
		else{
			float agreementDecay = menuStaticHistory ? accumdecay : (menuBilinearHistory
				? menu_coherent_history_decay(currentColorAccumilation, lightColor, accumdecay, depthBreakAgrees, cloudDistanceAgrees, reprojectedPixelMotion)
				: temporal_alpha_agreement_decay(currentColorAccumilation, lightColor, accumdecay));
			diagnosticHistoryWeight = agreementDecay;
			diagnosticHistoryReason = agreementDecay < accumdecay ? 0.25 : 0.0; // alpha fade / accepted
			currentColorAccumilation = (currentColorAccumilation * agreementDecay) + lightColor * (1.0 - agreementDecay);

			currentDataAccumilation.r = mix(currentDataAccumilation.r, initialdistanceSample, (1.0 - agreementDecay));
			currentDataAccumilation.g = mix(currentDataAccumilation.g, traveledDistance,  (1.0 - agreementDecay));
			currentDataAccumilation.b = mix(currentDataAccumilation.b, finalDensityDistance,  (1.0 - agreementDecay));
		}

		currentDataAccumilation.a = currentDepthBreak;

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
	
	// Diagnostic output is intentionally after ping-pong stores: it cannot feed
	// back into accumulation. R=used history weight; G=reason (0 accepted,
	// .25 alpha fade, .5 depth-break, .75 invalid/offscreen); B=mismatch;
	// A=current ray-marched alpha.
	if (genericData.data.menu_temporal_controls.w > 0.5) {
		imageStore(output_color_image, uv, vec4(diagnosticHistoryWeight, diagnosticHistoryReason,
			diagnosticAlphaMismatch, lightColor.a));
	} else {
		imageStore(output_color_image, uv, currentColorAccumilation);
	}
	imageStore(output_data_image, uv, currentDataAccumilation);
	//}
}
