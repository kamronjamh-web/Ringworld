shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque;

#include "res://materials/ring_color_space.gdshaderinc"

uniform float center_elevation_meters = 0.0;
uniform float vertical_scale = 0.22;
uniform float contour_interval_meters = 250.0;
uniform float contour_strength : hint_range(0.0, 1.0) = 0.42;
uniform float close_3d_factor : hint_range(0.0, 1.0) = 0.0;

varying float elevation_meters;

void vertex() {
	elevation_meters = VERTEX.y / max(vertical_scale, 0.0001) + center_elevation_meters;
}

void fragment() {
	float interval = max(contour_interval_meters, 1.0);
	float phase = abs(fract(elevation_meters / interval + 0.5) - 0.5);
	float antialias = max(fwidth(elevation_meters / interval) * 1.4, 0.012);
	float contour = 1.0 - smoothstep(0.0, antialias, phase);
	vec3 terrain = ring_vertex_color_to_linear(COLOR.rgb)
		* mix(1.0, 1.18, clamp(NORMAL.y, 0.0, 1.0));
	vec3 map_light = normalize(vec3(-0.35, 0.80, 0.45));
	float relief_light = clamp(dot(normalize(NORMAL), map_light) * 0.5 + 0.5, 0.0, 1.0);
	terrain *= mix(1.0, mix(0.72, 1.24, relief_light), close_3d_factor * 0.58);
	terrain *= mix(1.0, 1.0 - contour_strength, contour);
	ALBEDO = terrain;
	EMISSION = terrain * 0.16;
	ROUGHNESS = 1.0;
}
