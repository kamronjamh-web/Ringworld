// Mesh vertex colors are authored as ordinary sRGB palette values, while
// Forward+ performs lighting in linear space. Custom shaders do not have the
// source_color hint available for COLOR, so convert explicitly before lighting.
// Existing procedural palettes were authored against the project's linear
// vertex-color path. Leave conversion opt-in for future imported sRGB palettes;
// enabling it on the current palette crushes mountain/space midtones.
uniform bool terrain_vertex_color_is_srgb = false;

float ring_srgb_channel_to_linear(float value) {
	return value <= 0.04045
		? value / 12.92
		: pow((value + 0.055) / 1.055, 2.4);
}

vec3 ring_vertex_color_to_linear(vec3 color) {
	if (!terrain_vertex_color_is_srgb) {
		return color;
	}
	return vec3(
		ring_srgb_channel_to_linear(color.r),
		ring_srgb_channel_to_linear(color.g),
		ring_srgb_channel_to_linear(color.b));
}
