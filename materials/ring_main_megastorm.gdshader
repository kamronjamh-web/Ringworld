shader_type spatial;
render_mode unshaded, blend_premul_alpha, depth_draw_never, depth_test_disabled, cull_front, shadows_disabled, fog_disabled;
// Megastorm wedge pass, main-view companion (docs/megastorm-design.md 3.3; WO5b). The far viewport is injected only where the main view
// drew nothing, and main terrain reaches 500-750 km, so within that range the far wedge pass is hidden wherever terrain lies behind the
// wall. This runs the same body (ring_far_megastorm_pass.gdshaderinc) in the main view on pixels that HAVE main depth, compositing the
// wall over terrain by depth; sky pixels (depth 0) are left to the far pass. Both passes work in the far frame (far camera position,
// far-frame direction, far ray frame, far lights) so the two sides of the terrain silhouette match; only the depth and the pixel ray come
// from the main view. The node MainMegastorm (RingScaledFarViewport.Megastorm) is the far hull moved into the main world.
// Render priority 127: after the other main transparents (e.g. the ocean proxy beyond the wall); Sunshine is POST_TRANSPARENT.
uniform sampler2D main_megastorm_depth : hint_depth_texture, filter_nearest;
// Main-view world directions -> far (canonical) frame (the world-to-canonical basis) and the far camera (scaled units).
uniform mat3 far_megastorm_main_to_far = mat3(1.0);
uniform vec3 far_megastorm_far_camera = vec3(0.0);
#include "res://materials/ring_far_megastorm_pass.gdshaderinc"

void vertex() {
	POSITION = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0));
	if (POSITION.w > 0.0) POSITION.z = max(POSITION.z, 0.0);
}

void fragment() {
	if (!far_megastorm_enabled) discard;
	float depth = textureLod(main_megastorm_depth, SCREEN_UV, 0.0).r;
	if (depth <= 0.0) discard; // sky: the far pass owns it
	// The main view is physical metres (scale 1).
	vec4 view = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, depth, 1.0);
	float t_far = length(view.xyz / view.w);
	vec3 dir = normalize(far_megastorm_main_to_far * (mat3(INV_VIEW_MATRIX) * -VIEW));
	vec4 result = far_ms_render(far_megastorm_far_camera, dir, t_far);
	if (result.a < 0.0) discard;
	ALBEDO = result.rgb;
	ALPHA = result.a;
}
