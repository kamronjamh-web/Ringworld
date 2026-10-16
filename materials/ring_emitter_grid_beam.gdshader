shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;

// Emitter relay grid link ribbons, near layer (docs/emitter-grid-climate-machines-spec.md G2-G5): additive core light and
// halo, no depth. When DepthWriteCore is on, ring_emitter_grid_beam_core.gdshader is its next_pass (RenderPriority + 1).
#include "res://materials/ring_emitter_grid_beam_common.gdshaderinc"
