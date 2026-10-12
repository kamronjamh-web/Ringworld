// In-memory biome color and terrain gradient textures. They remain unlit so
// the normal ring lighting, weather shadows, night tint and atmosphere apply.
uniform bool surface_tile_enabled = false;
uniform sampler2D surface_tile_color : filter_linear_mipmap, repeat_disable;
uniform sampler2D surface_tile_normal : filter_linear_mipmap, repeat_disable;
uniform float surface_tile_resolution = 128.0;
uniform float surface_tile_angle_start = 0.0;
uniform float surface_tile_angle_span = 0.0;
uniform float surface_tile_blend = 1.0;

vec2 surface_tile_sample_uv(vec2 tile_uv) {
	// Pixel centers 1.5 .. N+0.5 hold shared boundary endpoint samples;
	// an extrapolated texel on each side supplies the mip/filter gutter.
	return (clamp(tile_uv, vec2(0.0), vec2(1.0)) * (surface_tile_resolution - 1.0) + 1.5)
		/ (surface_tile_resolution + 2.0);
}
