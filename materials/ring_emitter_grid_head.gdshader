shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;

// Pulse head glow sprites, near layer (spec G3/G4.2). Must draw before the depth-writing core pass (lower RenderPriority).
#include "res://materials/ring_emitter_grid_head_common.gdshaderinc"
