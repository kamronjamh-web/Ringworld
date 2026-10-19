#[compute]
#version 450
#define PI 3.141592

// CloudInterleave fill pass (RingworldTechDemo). SunshineCloudsCompute.glsl built with RING_INTERLEAVE = n marches one pixel of every
// n x n block per frame (and resolves it against history as usual, writing the raw march result to the block images). This pass runs
// at full cloud resolution over every OTHER pixel:
//   * history reprojection and validation are the shared SunshineCloudsHistory.glslinc code, fed with the depth-aware spatial
//     upsample of this frame's block samples (opaque-depth metadata) as the current estimate;
//   * valid history is clamped by the variance of the surrounding 3 x 3 block samples (genericData ring_perf_controls.z = gamma) and
//     blended with the spatial estimate by the per-frame history weight (ring_perf_controls.w);
//   * invalid history (off-image, behind the camera, or opaque-surface disagreement) falls back to the spatial estimate.
// It writes the same outputs as the march pass for its pixels (ping-pong B, output color / data, haze, transient light).
// Diagnostic reason codes (output color G when the history-weight diagnostic is on): 0.9 spatial fallback, 1.0 history held.

#include "./CloudsInc.comp"
#include "../../materials/ring_atmosphere_shared.gdshaderinc"
#include "../../materials/ring_haze_shared.gdshaderinc"
#include "./RingAtmosphereCompute.glslinc"
#include "./RingCloudHistory.gdshaderinc"

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

// Block size n (2 or 4); must equal the march pass's RING_INTERLEAVE. Specialization constant 0, set per pipeline variant.
layout(constant_id = 0) const int RING_INTERLEAVE = 2;

layout(rgba16f, binding = 0) uniform image2D output_data_image;
layout(rgba16f, binding = 1) uniform image2D output_color_image;

layout(rgba32f, binding = 2) uniform image2D accum_1A_image;
layout(rgba32f, binding = 3) uniform image2D accum_1B_image;

layout(rgba32f, binding = 4) uniform image2D accum_2A_image;
layout(rgba32f, binding = 5) uniform image2D accum_2B_image;

layout(binding = 6) uniform sampler2D depth_image;

layout(binding = 7) uniform uniformBuffer {
	GenericData data;
} genericData;

layout(binding = 8, std140) uniform SceneDataBlock {
	SceneData data;
	SceneData prev_data;
} scene_data_block;
layout(binding = 9) uniform sampler2D far_opaque_depth_image;

layout(rgba16f, binding = 10) uniform readonly image2D interleave_steady_image;
layout(rgba16f, binding = 11) uniform readonly image2D interleave_transient_image;
layout(rgba32f, binding = 12) uniform readonly image2D interleave_data_image;

#include "./SunshineCloudsHistory.glslinc"

vec4 history_color(bool fromA, ivec2 pixel) {
	return fromA ? imageLoad(accum_1A_image, pixel) : imageLoad(accum_1B_image, pixel);
}

vec4 history_data(bool fromA, ivec2 pixel) {
	return fromA ? imageLoad(accum_2A_image, pixel) : imageLoad(accum_2B_image, pixel);
}

void main() {
	ivec2 uv = ivec2(gl_GlobalInvocationID.xy);
	ivec2 size = ivec2(genericData.data.raster_size);
	if (uv.x >= size.x || uv.y >= size.y) {
		return;
	}
	const int n = RING_INTERLEAVE;
	ivec2 phaseOffset = ring_interleave_offset(n, int(genericData.data.ring_perf_controls.x + 0.5));
	if ((uv % n) == phaseOffset) {
		return; // marched (and fully resolved) by the main pass this frame
	}

	CloudPixelRay pixelRay = ring_cloud_pixel_ray(uv, size);
	vec2 depthUV = pixelRay.depthUV;
	float linear_depth = pixelRay.linearDepth;
	bool has_opaque_depth = pixelRay.hasOpaqueDepth;
	vec3 raydirection = pixelRay.rayDirection;
	vec3 rayOrigin = pixelRay.rayOrigin;
	float maxstep = ring_cloud_max_step();
	float opaqueDepthMetadata = has_opaque_depth ? linear_depth : -1.0;

	//SPATIAL ESTIMATE from this frame's block samples
	// Block b was marched at pixel b * n + phaseOffset, so pixel p sits at continuous block coordinate (p - phaseOffset) / n.
	ivec2 maxBlock = max(ivec2(0), (size - ivec2(1) - phaseOffset) / n);
	vec2 blockCoord = vec2(uv - phaseOffset) / float(n);
	ivec2 blockBase = ivec2(floor(blockCoord));
	vec2 blockFraction = blockCoord - vec2(blockBase);
	ivec2 blockCenter = blockBase + ivec2(greaterThanEqual(blockFraction, vec2(0.5)));
	// Block samples are n pixels apart, so neighbouring samples legitimately differ more in depth than neighbouring pixels do.
	float spatialDepthTolerance = max(2.0, max(opaqueDepthMetadata, 0.0) * 3.0 / max(float(size.y), 1.0))
		* genericData.data.history_controls.y * float(n);

	vec4 spatialColor = vec4(0.0);
	vec3 spatialTransient = vec3(0.0);
	vec4 spatialData = vec4(0.0);
	float spatialWeight = 0.0;
	vec4 statMean = vec4(0.0);
	vec4 statSquare = vec4(0.0);
	float statCount = 0.0;
	for (int dy = -1; dy <= 1; dy++) {
		for (int dx = -1; dx <= 1; dx++) {
			ivec2 tapUnclamped = blockCenter + ivec2(dx, dy);
			ivec2 tap = clamp(tapUnclamped, ivec2(0), maxBlock);
			vec4 tapColor = imageLoad(interleave_steady_image, tap);
			vec4 tapData = imageLoad(interleave_data_image, tap);
			bool tapAgrees = ring_cloud_history_opaque_matches(opaqueDepthMetadata, tapData.a, spatialDepthTolerance);
			float bilinearX = tapUnclamped.x == blockBase.x ? 1.0 - blockFraction.x
				: (tapUnclamped.x == blockBase.x + 1 ? blockFraction.x : 0.0);
			float bilinearY = tapUnclamped.y == blockBase.y ? 1.0 - blockFraction.y
				: (tapUnclamped.y == blockBase.y + 1 ? blockFraction.y : 0.0);
			float tapWeight = bilinearX * bilinearY * (tapAgrees ? 1.0 : 0.02);
			if (tapWeight > 0.0) {
				spatialColor += tapColor * tapWeight;
				spatialTransient += imageLoad(interleave_transient_image, tap).rgb * tapWeight;
				spatialData += tapData * tapWeight;
				spatialWeight += tapWeight;
			}
			if (tapAgrees) {
				statMean += tapColor;
				statSquare += tapColor * tapColor;
				statCount += 1.0;
			}
		}
	}
	float inverseWeight = 1.0 / max(spatialWeight, 0.000001);
	spatialColor *= inverseWeight;
	spatialTransient *= inverseWeight;
	spatialData *= inverseWeight;
	float initialdistanceSample = spatialData.r;
	float traveledDistance = spatialData.g;
	float finalDensityDistance = spatialData.b;

	//HISTORY (same reprojection and validation code as the march pass)
	CloudHistoryReproject hist = ring_cloud_history_reproject(uv, size, depthUV, rayOrigin, raydirection,
		finalDensityDistance, linear_depth, has_opaque_depth);
	float historyWeight = clamp(genericData.data.ring_perf_controls.w, 0.0, 1.0);
	bool fromA = genericData.data.isAccumulationA > 0.0;
	bool visibilityGate = genericData.data.history_controls.x > 0.5;
	float cameraTranslation = length(rayOrigin - genericData.data.previous_camera_origin.xyz);
	vec4 historyColor = vec4(0.0);
	vec4 historyMetadata = vec4(0.0);
	bool historyValid = historyWeight > 0.0 && !hist.historyOverride && !hist.historyOutsideImage;
	if (historyValid) {
		if (hist.coherentBilinearHistory) {
			vec4 data00 = history_data(fromA, hist.history00);
			vec4 data10 = history_data(fromA, hist.history10);
			vec4 data01 = history_data(fromA, hist.history01);
			vec4 data11 = history_data(fromA, hist.history11);
			float weight00 = (1.0 - hist.historyFraction.x) * (1.0 - hist.historyFraction.y);
			float weight10 = hist.historyFraction.x * (1.0 - hist.historyFraction.y);
			float weight01 = (1.0 - hist.historyFraction.x) * hist.historyFraction.y;
			float weight11 = hist.historyFraction.x * hist.historyFraction.y;
			if (visibilityGate) {
				weight00 *= float(opaque_depth_history_agrees(hist.expectedHistoryOpaqueDepth, opaqueDepthMetadata, data00, hist.opaqueDepthTolerance, traveledDistance, cameraTranslation, maxstep));
				weight10 *= float(opaque_depth_history_agrees(hist.expectedHistoryOpaqueDepth, opaqueDepthMetadata, data10, hist.opaqueDepthTolerance, traveledDistance, cameraTranslation, maxstep));
				weight01 *= float(opaque_depth_history_agrees(hist.expectedHistoryOpaqueDepth, opaqueDepthMetadata, data01, hist.opaqueDepthTolerance, traveledDistance, cameraTranslation, maxstep));
				weight11 *= float(opaque_depth_history_agrees(hist.expectedHistoryOpaqueDepth, opaqueDepthMetadata, data11, hist.opaqueDepthTolerance, traveledDistance, cameraTranslation, maxstep));
			}
			float validWeight = weight00 + weight10 + weight01 + weight11;
			if (validWeight > 0.00001) {
				historyColor = (history_color(fromA, hist.history00) * weight00
					+ history_color(fromA, hist.history10) * weight10
					+ history_color(fromA, hist.history01) * weight01
					+ history_color(fromA, hist.history11) * weight11) / validWeight;
				historyMetadata = (data00 * weight00 + data10 * weight10 + data01 * weight01 + data11 * weight11) / validWeight;
			} else {
				historyValid = false;
			}
		} else {
			historyColor = history_color(fromA, hist.clampedUV);
			historyMetadata = history_data(fromA, hist.clampedUV);
			if (visibilityGate && !opaque_depth_history_agrees(hist.expectedHistoryOpaqueDepth, opaqueDepthMetadata, historyMetadata, hist.opaqueDepthTolerance, traveledDistance, cameraTranslation, maxstep)) {
				historyValid = false;
			}
		}
	}
	if (historyValid && visibilityGate) {
		// Opaque geometry follows its own previous-frame projection, not the cloud body's.
		historyValid = !hist.opaqueHistoryOutside && ring_cloud_history_opaque_matches(hist.expectedHistoryOpaqueDepth,
			history_data(fromA, hist.opaqueHistoryPixel).a, hist.opaqueDepthTolerance);
	}
	if (historyValid) {
		historyValid = !any(isnan(historyColor)) && !any(isinf(historyColor))
			&& !any(isnan(historyMetadata)) && !any(isinf(historyMetadata));
	}
	// Same gameplay acceptance as the march pass (SunshineCloudsCompute.glsl): a validated opaque/sky pair under ray-start jitter
	// keeps the full history weight; otherwise the weight fades with cloud-distance and alpha disagreement instead of a hard reject.
	bool opaquePointAgrees = !hist.opaqueHistoryOutside && ring_cloud_history_opaque_matches(hist.expectedHistoryOpaqueDepth,
		history_data(fromA, hist.opaqueHistoryPixel).a, hist.opaqueDepthTolerance);
	bool validatedJitter = historyValid && ring_cloud_history_validated_jitter(
		visibilityGate && opaquePointAgrees
			&& ((opaqueDepthMetadata < 0.0 && historyMetadata.a == -1.0)
				|| (opaqueDepthMetadata > 0.0 && historyMetadata.a > 0.0)),
		genericData.data.menu_temporal_controls.z > 0.5,
		genericData.data.temporal_controls.x, cameraTranslation, maxstep);
	float effectiveHistoryWeight = historyWeight;
	if (historyValid && !validatedJitter) {
		effectiveHistoryWeight = ring_cloud_history_compatible_weight(historyColor, spatialColor, historyMetadata.b,
			finalDensityDistance, maxstep, historyWeight, genericData.data.history_controls.z > 0.5,
			vec2(genericData.data.cloud_mask_controls.y, genericData.data.cloud_mask_controls.z));
	}

	//RESOLVE
	vec4 resultColor = spatialColor;
	vec4 resultData = spatialData;
	float diagnosticHistoryWeight = 0.0;
	float diagnosticHistoryReason = 0.9; // spatial fallback
	float diagnosticAlphaMismatch = 0.0;
	if (historyValid) {
		vec4 clampedHistory = historyColor;
		if (statCount > 0.5 && !validatedJitter) {
			// Variance clipping against the neighbourhood of this frame's samples removes ghosts of cloud that has since moved.
			// Skipped for validated pairs (baseline gameplay has no neighbourhood clamp). Sigma is floored (rgb 0.02, alpha 0.05)
			// so a degenerate all-miss neighbourhood cannot collapse history onto the noisy raw mean.
			vec4 mean = statMean / statCount;
			vec4 sigma = sqrt(max(statSquare / statCount - mean * mean, vec4(0.0)));
			sigma = max(sigma, vec4(0.02, 0.02, 0.02, 0.05));
			float gamma = max(genericData.data.ring_perf_controls.z, 0.0);
			clampedHistory = clamp(historyColor, mean - sigma * gamma, mean + sigma * gamma);
		}
		resultColor = mix(spatialColor, clampedHistory, effectiveHistoryWeight);
		resultData.rgb = mix(spatialData.rgb, historyMetadata.rgb, effectiveHistoryWeight);
		diagnosticHistoryWeight = effectiveHistoryWeight;
		diagnosticHistoryReason = 1.0; // history held
		diagnosticAlphaMismatch = abs(historyColor.a - spatialColor.a);
	}
	resultData.a = opaqueDepthMetadata;

	// Write the next ping-pong buffer (the march pass writes the same one for its own pixels).
	if (fromA) {
		imageStore(accum_1B_image, uv, resultColor);
		imageStore(accum_2B_image, uv, resultData);
	} else {
		imageStore(accum_1A_image, uv, resultColor);
		imageStore(accum_2A_image, uv, resultData);
	}

	if (genericData.data.menu_temporal_controls.w > 0.5) {
		imageStore(output_color_image, uv, vec4(diagnosticHistoryWeight, diagnosticHistoryReason,
			diagnosticAlphaMismatch, spatialColor.a));
	} else {
		vec4 outputColor = resultColor;
		if (!any(isnan(spatialTransient)) && !any(isinf(spatialTransient))) {
			outputColor.rgb += spatialTransient;
		}
		if (genericData.data.temporal_controls.z <= 0.0 && genericData.data.temporal_controls.w <= 0.0) {
			float haze_distance = clamp(resultData.b, 0.0, max(0.0, linear_depth));
			outputColor.rgb = ring_cloud_atmosphere_apply(outputColor.rgb, rayOrigin, raydirection, haze_distance, genericData.data.ring_atmosphere_profile);
		}
		imageStore(output_color_image, uv, outputColor);
	}
	vec4 outputData = resultData;
	outputData.r = min(outputData.r, initialdistanceSample);
	outputData.a = has_opaque_depth ? log2(1.0 + max(linear_depth, 0.0)) : -8.0;
	imageStore(output_data_image, uv, outputData);
}
