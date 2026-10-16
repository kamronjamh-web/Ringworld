shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_always, shadows_disabled, fog_disabled;

// Emitter relay grid, near layer depth-writing core (spec G4.4 / T2). Writes no colour: only depth, only where the lit core
// exceeds CoreDepthThreshold, widened to CoreDepthMinPixels. Sunshine composites after the transparent pass and integrates
// to this depth, so clouds behind a lit beam no longer dim it. Must draw after the halo pass (higher RenderPriority), or the
// halo would depth-fight the core it carries.
#define EG_CORE_PASS
#include "res://materials/ring_emitter_grid_beam_common.gdshaderinc"
