shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;

// Pulse head glow sprites, far layer (spec G4.1/G4.3): scaled space, RenderPriority below the far cloud layers.
#define EG_FAR_LAYER
#include "res://materials/ring_emitter_grid_head_common.gdshaderinc"
