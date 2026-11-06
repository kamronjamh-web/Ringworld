shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;

// Emitter field shots, far layer: the same orbs in the scaled far viewport. RenderPriority below the far cloud layers so far clouds blend over them. Never
// writes depth. Owns each fragment with weight 1 - near weight (see ring_emitter_grid_layers.gdshaderinc).
#define EFS_FAR_LAYER
#include "res://materials/ring_emitter_field_shot_common.gdshaderinc"
