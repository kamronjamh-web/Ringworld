shader_type spatial;
render_mode diffuse_lambert, specular_disabled;
#include "res://materials/ring_terrain_grass_coverage.gdshaderinc"
uniform vec3 terrain_color;
uniform float eligibility = 1.0;
varying vec3 world_position;
void vertex() { world_position = (MODEL_MATRIX * vec4(VERTEX,1.0)).xyz; }
void fragment() {
    float coverage = ring_terrain_grass_coverage(world_position,distance(world_position,CAMERA_POSITION_WORLD),1.0,eligibility);
    ALBEDO = ring_apply_terrain_grass_coverage(terrain_color,coverage);
    ROUGHNESS = 1.0;
}
