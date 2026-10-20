// Reader math of the per-instance vegetation shadow atlas (docs/shadow-revamp-plan.md S7b). Pure functions: no instance uniforms and no atlas
// fetches, so canvas_item probes (tools/VegetationShadowGpuTest.cs) can include it. Include AFTER ring_cloud_shadow.gdshaderinc and
// ring_height_shadow.gdshaderinc (ring_shadow_compose.gdshaderinc brings both). Layout and the writer are in
// materials/vegetation_gpu/ring_vegetation_shadow_terms.glslinc (the GLSL twin of the producer side).
//
//   texel0 = (R_eff, penumbra_eff, heightfield weight, tag)   texel1 = (near cloud raw, far cloud raw, light id, 1 + unknown)
#ifndef RING_VEGETATION_SHADOW_READ
#define RING_VEGETATION_SHADOW_READ

// texel0.w must equal the renderer's tag, texel1.w > 0.5 marks a written slot.
bool ring_vegetation_shadow_written(vec4 texel0, vec4 texel1, float tag) {
	return abs(texel0.w - tag) < 0.5 && texel1.w > 0.5;
}

// The heightfield terms are only valid for the light they were built for (texel1.z, the heightfield state.w at compute time).
bool ring_vegetation_shadow_height_matches(vec4 texel1) {
	return abs(texel1.z - round(ringworld_height_shadow_state.w)) < 0.5;
}

// The two usability tests the renderer wrappers apply (ring_vegetation_shadow.gdshaderinc); everything else falls back to the per-vertex code.
bool ring_vegetation_shadow_cloud_usable(vec4 texel0, vec4 texel1, float tag) {
	return ring_vegetation_shadow_written(texel0, texel1, tag);
}

bool ring_vegetation_shadow_height_usable(vec4 texel0, vec4 texel1, float tag) {
	return ring_vegetation_shadow_written(texel0, texel1, tag) && ring_vegetation_shadow_height_matches(texel1);
}

// h_rel: height of the receiver above the instance root along ring up (0 for terrain-type receivers).
RingHeightShadow ring_vegetation_shadow_height(vec4 texel0, vec4 texel1, float h_rel) {
	RingHeightShadow hs;
	hs.visibility = clamp(0.5 + (h_rel - texel0.x) / max(texel0.y, 0.25), 0.0, 1.0);
	hs.weight = clamp(texel0.z, 0.0, 1.0);
	hs.unknown = clamp(texel1.w - 1.0, 0.0, 1.0);
	return hs;
}

// Per-vertex cloud occlusion from the stored raw near / far values: the hand-off and strength stage of sample_local_cloud_shadow with a cheap planar
// distance (arc ~ tangent over the cloud range). The projection onto the cloud plane and the two map fetches were done once per instance.
float ring_vegetation_shadow_cloud(vec2 raw, vec3 world) {
	if (!local_cloud_shadow_enabled) {
		return 0.0;
	}
	vec3 relative = world - ring_cloud_shadow_near_center();
	vec2 planar_meters = vec2(dot(relative, ring_cloud_shadow_near_around()), dot(relative, ring_cloud_shadow_near_across()));
	float radius = max(local_cloud_shadow_radius_meters, 1.0);
	float planar_distance = length(planar_meters);
	float cascade_handoff = smoothstep(
		local_cloud_shadow_near_start_meters,
		max(local_cloud_shadow_near_start_meters + 1.0, local_cloud_shadow_near_end_meters),
		planar_distance);
	float far_strength_blend = smoothstep(
		local_cloud_shadow_far_strength_start_meters,
		max(
			local_cloud_shadow_far_strength_start_meters + 1.0,
			local_cloud_shadow_far_strength_end_meters),
		planar_distance);
	float distance_strength = mix(
		local_cloud_shadow_strength,
		local_cloud_shadow_far_strength,
		far_strength_blend);
	bool near_covered = local_cloud_shadow_near_enabled && raw.x >= 0.0;
	float near_occlusion = 0.0;
	float near_weight = 0.0;
	if (near_covered) {
		float normalized_radius = length(planar_meters / radius);
		near_weight = 1.0 - smoothstep(0.72, 0.94, normalized_radius);
		if (near_weight > 0.001) {
			near_occlusion = raw.x * cascade_handoff * distance_strength;
		}
	}
	float far_occlusion = local_cloud_shadow_far_enabled ? raw.y * local_cloud_shadow_far_strength : 0.0;
	if (!near_covered) {
		return far_occlusion;
	}
	if (!local_cloud_shadow_far_enabled) {
		return near_occlusion * near_weight;
	}
	return mix(far_occlusion, near_occlusion, near_weight);
}
#endif
