shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;

// Emitter relay grid link ribbons, far layer (spec G4.1/G4.3): the same links in the scaled far viewport, drawn with a
// RenderPriority below the far cloud layers so far clouds blend over them. Never writes depth (the far depth capture feeds
// Sunshine). Owns each fragment with weight 1 - near weight (see ring_emitter_grid_layers.gdshaderinc).
#define EG_FAR_LAYER
#include "res://materials/ring_emitter_grid_beam_common.gdshaderinc"
