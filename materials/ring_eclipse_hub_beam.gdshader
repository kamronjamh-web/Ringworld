shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;

// Eclipse spire to hub beams, near layer (docs/eclipse-false-sun-spec.md E.2): additive core light and halo, no depth. When
// DepthWriteCore is on, ring_eclipse_hub_beam_core.gdshader is its next_pass (RenderPriority + 1).
#include "res://materials/ring_eclipse_hub_beam_common.gdshaderinc"
