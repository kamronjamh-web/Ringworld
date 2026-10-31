shader_type spatial;
render_mode unshaded, blend_premul_alpha, depth_draw_never, depth_test_disabled, cull_front, shadows_disabled, fog_disabled;
// Megastorm far wedge pass (docs/megastorm-design.md 3.1, 3.3; WO5, WO5b): the wall body and the leading anvil at any distance up to the
// antipode, drawn in the scaled far viewport on the sky pixels (the far image is injected only where the main view has no depth). The
// node FarMegastorm (RingScaledFarViewport.Megastorm) is a convex prism hull around the strip's wedge (a screen-space bound; hidden, so
// free, while no megastorm is active). The shading is ring_far_megastorm_pass.gdshaderinc, shared with the main-view companion
// (ring_main_megastorm.gdshader) which draws the same wall over main-view terrain. Here t_far is the far terrain depth.
// Draw order: after the whole-ring shell (10-14), before the far layer sky (16), which ends its integration at megastorm_body_entry.
uniform sampler2D far_megastorm_depth : hint_depth_texture, filter_nearest;
#include "res://materials/ring_far_megastorm_pass.gdshaderinc"

void vertex() {
	// Never far-clipped: the hull spans the whole ring-scale wedge (reverse-Z, far plane at z = 0).
	POSITION = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0));
	if (POSITION.w > 0.0) POSITION.z = max(POSITION.z, 0.0);
}

void fragment() {
	if (!far_megastorm_enabled) discard;
	// Far end: the far terrain (reverse-Z depth), physical metres.
	float t_far = 1e30;
	float depth = textureLod(far_megastorm_depth, SCREEN_UV, 0.0).r;
	if (depth > 0.0) {
		vec4 view = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, depth, 1.0);
		t_far = length(view.xyz / view.w) / max(ring_atmosphere_coordinate_scale, 0.000001);
	}
	vec3 dir = normalize(mat3(INV_VIEW_MATRIX) * -VIEW);
	vec4 result = far_ms_render(CAMERA_POSITION_WORLD, dir, t_far);
	if (result.a < 0.0) discard;
	ALBEDO = result.rgb;
	ALPHA = result.a;
}
