shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_always, shadows_disabled, fog_disabled;

// Emitter field shots, near layer depth-writing core (same idea as ring_eclipse_hub_beam_core.gdshader): writes no colour, only depth where the lit orb core
// exceeds CoreDepthThreshold, widened to CoreDepthMinPixels, so clouds behind an orb do not dim it. Must draw after the colour pass (higher RenderPriority).
#define EFS_CORE_PASS
#include "res://materials/ring_emitter_field_shot_common.gdshaderinc"
