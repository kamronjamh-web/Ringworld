shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;

// Eclipse spire to hub beams, far layer (spec E.2): the same 595 beams in the scaled far viewport, from each crown to the scaled origin.
// RenderPriority below the far cloud layers so far clouds blend over them. Never writes depth. Owns each fragment with weight
// 1 - near weight (see ring_emitter_grid_layers.gdshaderinc).
#define EB_FAR_LAYER
#include "res://materials/ring_eclipse_hub_beam_common.gdshaderinc"
