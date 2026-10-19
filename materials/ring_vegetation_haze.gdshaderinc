// Per-instance Physical haze for grass cards and trees. Include AFTER res://materials/ring_atmosphere.gdshaderinc.
// The GPU vegetation compute (RingGpuVegetationCompute, haze channel Grass or Trees) writes one atlas slot per output row; the
// renderer's instance uniform vegetation_haze_slot = (first slot of this MultiMesh, allocation tag) maps INSTANCE_ID to its slot.
// Layout, packing and the assembly are in materials/atmosphere/ring_vegetation_haze_terms.gdshaderinc.
// Every case the atlas does not cover falls back to the exact per-vertex ring_haze_sample: CPU MultiMesh paths (slot -1), Legacy model,
// haze disabled / no ray frame, the ring_haze_debug_mode attribution knob, the eclipse hub event (hub-blended sun), a channel that is not
// live (toggle "Per-instance vegetation haze" off, compute failed / disposed) and any slot whose tag does not match (not yet written,
// reallocated to another batch).
#include "res://materials/atmosphere/ring_vegetation_haze_terms.gdshaderinc"

// Atlases (Texture2DRD, RGBA16F) published by RingVegetationHaze; x = grass atlas live, y = tree atlas live (1 / 0).
global uniform sampler2D ringworld_vegetation_haze_grass : filter_nearest, repeat_disable;
global uniform sampler2D ringworld_vegetation_haze_trees : filter_nearest, repeat_disable;
global uniform vec4 ringworld_vegetation_haze_state;
// (first slot, tag); x < 0 = no per-instance data for this renderer.
instance uniform vec2 vegetation_haze_slot = vec2(-1.0, 0.0);

const int RING_VEGETATION_HAZE_GRASS = 0;
const int RING_VEGETATION_HAZE_TREES = 1;

// Drop-in replacement for ring_haze_sample(camera_world, vertex_world, center_world, day_factor) in a vegetation vertex shader.
RingHaze ring_vegetation_haze_sample(int channel, int instance_id, vec3 camera_world, vec3 vertex_world, vec3 center_world, float day_factor) {
	float live = channel == RING_VEGETATION_HAZE_GRASS ? ringworld_vegetation_haze_state.x : ringworld_vegetation_haze_state.y;
	if (vegetation_haze_slot.x >= 0.0 && live > 0.5 && atmosphere_haze_model == 1 && ring_atmosphere_enabled && ring_radius_meters > 1.0
		&& ring_atmosphere_ray_frame_valid && ring_haze_debug_mode == 0 && ringworld_hub_light_weight <= 0.0
		&& abs(ring_atmosphere_coordinate_scale - 1.0) < 0.000001) {
		ivec2 texel = ring_vegetation_haze_texel(int(vegetation_haze_slot.x) + instance_id);
		vec4 texel0;
		vec4 texel1;
		if (channel == RING_VEGETATION_HAZE_GRASS) {
			texel0 = texelFetch(ringworld_vegetation_haze_grass, texel, 0);
			texel1 = texelFetch(ringworld_vegetation_haze_grass, texel + ivec2(1, 0), 0);
		} else {
			texel0 = texelFetch(ringworld_vegetation_haze_trees, texel, 0);
			texel1 = texelFetch(ringworld_vegetation_haze_trees, texel + ivec2(1, 0), 0);
		}
		if (abs(texel0.w - vegetation_haze_slot.y) < 0.5 && texel1.w > 0.5) {
			RingHazeParams p = ring_haze_uniform_params(ring_atmosphere_to_sun, ring_atmosphere_outward_world);
			return ring_vegetation_haze_assemble(p, ring_vegetation_haze_unpack(texel0, texel1), atmosphere_opacity_multiplier);
		}
	}
	return ring_haze_sample(camera_world, vertex_world, center_world, day_factor);
}
