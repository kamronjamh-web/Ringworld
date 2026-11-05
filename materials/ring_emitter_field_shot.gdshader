shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;

// Emitter field shots, near layer: the additive orb and dispersal flare, no depth. When DepthWriteCore is on, ring_emitter_field_shot_core.gdshader is its
// next_pass (RenderPriority + 1).
#include "res://materials/ring_emitter_field_shot_common.gdshaderinc"
