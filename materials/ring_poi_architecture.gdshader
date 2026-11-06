shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, diffuse_burley, specular_schlick_ggx;

// Main-viewport only: ray frame comes from global shader parameters.
#define RING_ATMOSPHERE_FRAME_GLOBAL
#include "res://materials/ring_poi_architecture.gdshaderinc"
