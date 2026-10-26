shader_type canvas_item;
// Probe for tools/storm_light_capture.gd: writes the shader's storm level D (ring_storm_level, RGBA16F target) for 16 world positions, one per pixel column.
#include "res://materials/ring_storm_light.gdshaderinc"
uniform vec3 probe_world[16];
void fragment() {
	int index = clamp(int(FRAGCOORD.x), 0, 15);
	COLOR = vec4(ring_storm_level(probe_world[index]), 0.0, 0.0, 1.0);
}
