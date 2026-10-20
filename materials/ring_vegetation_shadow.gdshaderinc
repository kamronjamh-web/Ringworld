// Per-instance terrain height-shadow and cloud-shadow terms for GPU-culled grass cards, standing grass and trees (docs/shadow-revamp-plan.md S7b).
// The vegetation compute (RingGpuVegetationCompute, shadow channel Grass or Trees) writes one atlas slot per compacted row next to the haze slot
// (same slot index and tag); the renderer's instance uniform vegetation_haze_slot = (first slot of this MultiMesh, allocation tag) maps INSTANCE_ID to it.
// Include AFTER ring_cloud_shadow.gdshaderinc and ring_shadow_compose.gdshaderinc (and after ring_vegetation_haze.gdshaderinc where the shader has haze).
// Every case the atlas does not cover falls back to the exact per-vertex code, bit for bit: CPU MultiMesh paths (slot -1), the toggle "Per-instance
// vegetation shadows" off, a channel that is not live (compute failed / disposed / frame unusable), a slot whose tag does not match (not yet written,
// reallocated), and, for the heightfield terms only, a slot built for another light than the one published now (state.w).
// Slot to atlas texel: the same mapping as ring_vegetation_haze_texel (two texels per slot, 1024 slots per row), repeated here so shaders without the haze includes can use it.
ivec2 ring_vegetation_shadow_texel(int slot) {
	return ivec2((slot % 1024) * 2, slot / 1024);
}
#include "res://materials/ring_vegetation_shadow_read.gdshaderinc"

// Atlases (Texture2DRD, RGBA16F) published by RingVegetationShadow; x = grass atlas live, y = tree atlas live (1 / 0), z = unified compose (look change, default 0).
global uniform sampler2D ringworld_vegetation_shadow_grass : filter_nearest, repeat_disable;
global uniform sampler2D ringworld_vegetation_shadow_trees : filter_nearest, repeat_disable;
global uniform vec4 ringworld_vegetation_shadow_state;
#ifndef RING_VEGETATION_SLOT_DECLARED
#define RING_VEGETATION_SLOT_DECLARED
instance uniform vec2 vegetation_haze_slot = vec2(-1.0, 0.0);
#endif

const int RING_VEGETATION_SHADOW_GRASS = 0;
const int RING_VEGETATION_SHADOW_TREES = 1;

struct RingVegetationShadowInstance {
	bool cloud_valid; // texel written by the current allocation: the cloud raw values can be used
	bool height_valid; // ... and its heightfield terms were built for the light published now
	vec4 texel0;
	vec4 texel1;
};

RingVegetationShadowInstance ring_vegetation_shadow_instance(int channel, int instance_id) {
	RingVegetationShadowInstance inst = RingVegetationShadowInstance(false, false, vec4(0.0), vec4(0.0));
	float live = channel == RING_VEGETATION_SHADOW_GRASS ? ringworld_vegetation_shadow_state.x : ringworld_vegetation_shadow_state.y;
	if (vegetation_haze_slot.x >= 0.0 && live > 0.5) {
		ivec2 texel = ring_vegetation_shadow_texel(int(vegetation_haze_slot.x) + instance_id);
		if (channel == RING_VEGETATION_SHADOW_GRASS) {
			inst.texel0 = texelFetch(ringworld_vegetation_shadow_grass, texel, 0);
			inst.texel1 = texelFetch(ringworld_vegetation_shadow_grass, texel + ivec2(1, 0), 0);
		} else {
			inst.texel0 = texelFetch(ringworld_vegetation_shadow_trees, texel, 0);
			inst.texel1 = texelFetch(ringworld_vegetation_shadow_trees, texel + ivec2(1, 0), 0);
		}
		inst.cloud_valid = ring_vegetation_shadow_cloud_usable(inst.texel0, inst.texel1, vegetation_haze_slot.y);
		inst.height_valid = ring_vegetation_shadow_height_usable(inst.texel0, inst.texel1, vegetation_haze_slot.y);
	}
	return inst;
}

// Drop-in for sample_local_cloud_shadow(world) at a vegetation vertex.
float ring_vegetation_shadow_cloud_vertex(RingVegetationShadowInstance inst, vec3 world) {
	if (inst.cloud_valid) {
		return ring_vegetation_shadow_cloud(inst.texel1.xy, world);
	}
	return sample_local_cloud_shadow(world);
}

// Drop-in for ring_height_shadow_vertex_term(world, root_world, surface_up, camera_world) (trees; sun and planet layers).
float ring_vegetation_shadow_height_vertex(RingVegetationShadowInstance inst, vec3 world, vec3 root_world, vec3 surface_up, vec3 camera_world) {
	if (inst.height_valid) {
		RingHeightShadow hs = ring_vegetation_shadow_height(inst.texel0, inst.texel1, max(dot(world - root_world, surface_up), 0.0));
		return mix(1.0, hs.visibility, hs.weight);
	}
	return ring_height_shadow_vertex_term(world, root_world, surface_up, camera_world);
}

// Drop-in for ring_height_shadow_planet_point_term(world, camera_world) (standing grass blades: planet layer only, ground level).
float ring_vegetation_shadow_planet_point(RingVegetationShadowInstance inst, vec3 world, vec3 camera_world) {
	if (inst.height_valid) {
		if (!ring_height_shadow_planet_active()) {
			return 1.0;
		}
		RingHeightShadow hs = ring_vegetation_shadow_height(inst.texel0, inst.texel1, 0.0);
		return mix(1.0, hs.visibility, hs.weight);
	}
	return ring_height_shadow_planet_point_term(world, camera_world);
}

// The ground heightfield sample of a card root (h_rel 0) for ring_terrain_direct_visibility; false = no instance data, the caller samples per fragment.
bool ring_vegetation_shadow_ground(RingVegetationShadowInstance inst, out RingHeightShadow hs) {
	hs = ring_height_shadow_none();
	if (!inst.height_valid) {
		return false;
	}
	hs = ring_vegetation_shadow_height(inst.texel0, inst.texel1, 0.0);
	return true;
}
