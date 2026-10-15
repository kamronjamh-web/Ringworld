shader_type spatial;
// This mesh exists only for ShadowIgnoresTerrainFade.  It deliberately does
// not run terrain lighting/fades, but shares the exact atlas discard used by
// the visible terrain and ocean surfaces.
render_mode cull_back, depth_draw_opaque;

uniform sampler2D coverage_atlas : filter_nearest, repeat_disable;
instance uniform vec4 coverage_atlas_rect = vec4(0.0, 0.0, 0.0, 0.0);
uniform vec3 camera_world_position = vec3(0.0);
uniform vec3 ring_center = vec3(0.0);
uniform bool geomorph_enabled = true;
uniform bool runtime_parent_chord_enabled = false;
uniform float morph_start_meters = 480.0;
uniform float morph_end_meters = 640.0;
#include "res://materials/ring_surface_aperture.gdshaderinc"
varying vec2 proxy_coverage_uv;
varying vec3 aperture_world_position;

void vertex() {
	proxy_coverage_uv = UV;
	if (geomorph_enabled) {
		vec3 original_world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
		float morph_distance = distance(original_world_position, camera_world_position);
		float morph_handoff = (morph_start_meters + max(morph_end_meters, morph_start_meters + 1.0)) * 0.5;
		float morph = smoothstep(morph_start_meters, max(morph_handoff, morph_start_meters + 0.01), morph_distance);
		vec3 radial = original_world_position - ring_center;
		radial.y = 0.0;
		vec3 surface_up_world = length(radial) > 0.0001 ? -normalize(radial) : vec3(-1.0, 0.0, 0.0);
		VERTEX += runtime_parent_chord_enabled && UV2.y < .5 ? CUSTOM0.xyz * morph : normalize(inverse(mat3(MODEL_MATRIX)) * surface_up_world) * UV2.x * morph;
	}
	aperture_world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	if (ring_surface_aperture_discard(aperture_world_position)) discard;
	if (coverage_atlas_rect.z > 0.0 && proxy_coverage_uv.x >= 0.0 && proxy_coverage_uv.y >= 0.0
		&& texture(coverage_atlas, coverage_atlas_rect.xy + min(proxy_coverage_uv, vec2(0.99999)) * coverage_atlas_rect.zw).r > 0.5) discard;
}
