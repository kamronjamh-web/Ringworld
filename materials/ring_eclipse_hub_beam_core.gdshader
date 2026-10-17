shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_always, shadows_disabled, fog_disabled;

// Eclipse spire to hub beams, near layer depth-writing core (spec E.6, grid spec T2). Writes no colour: only depth, only where the
// lit core exceeds CoreDepthThreshold, widened to CoreDepthMinPixels. Sunshine composites after the transparent pass and integrates to
// this depth, so clouds behind a lit beam no longer dim it. Must draw after the halo pass (higher RenderPriority).
#define EB_CORE_PASS
#include "res://materials/ring_eclipse_hub_beam_common.gdshaderinc"
