shader_type spatial;
render_mode diffuse_lambert, specular_disabled;
#include "res://materials/grass_cards_spike/spike_terrain_albedo.gdshaderinc"

uniform sampler2D terrain_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform float terrain_uv_scale = 0.2;

varying vec2 v_world_xz;

void vertex() {
	v_world_xz = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xz;
}

void fragment() {
	ALBEDO = spike_grass_tint(texture(terrain_albedo, v_world_xz * terrain_uv_scale).rgb);
	ROUGHNESS = 1.0;
}
