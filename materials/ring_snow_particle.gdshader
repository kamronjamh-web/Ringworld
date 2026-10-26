shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled;
// Camera-local snow flakes (RingLocalWeatherEffects, GPUParticles3D pool). Billboarded unshaded quads like the StandardMaterial3D they replace, plus shelter: a flake whose
// centre lies under a roof, POI overhang or cave ceiling in the precipitation occlusion map is hidden (docs/storm-revamp-s1-design.md WO7). Outside the map or while it is
// not ready ring_precip_exposure is 1, so open-air output is identical to the old material.
#include "res://materials/ring_precip_occlusion.gdshaderinc"
uniform vec4 snow_color : source_color = vec4(0.92, 0.96, 1.0, 0.82);
varying float v_exposure;

void vertex() {
	mat4 mat_world = mat4(normalize(INV_VIEW_MATRIX[0]), normalize(INV_VIEW_MATRIX[1]), normalize(INV_VIEW_MATRIX[2]), MODEL_MATRIX[3]);
	MODELVIEW_MATRIX = VIEW_MATRIX * mat_world;
	MODELVIEW_NORMAL_MATRIX = mat3(MODELVIEW_MATRIX);
	v_exposure = ring_precip_exposure(MODEL_MATRIX[3].xyz);
}

void fragment() {
	ALBEDO = snow_color.rgb;
	ALPHA = snow_color.a * v_exposure;
}
