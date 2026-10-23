shader_type spatial;
render_mode unshaded, blend_premul_alpha, depth_draw_never, cull_disabled;
// Main-view far cloud layer (RingCloudLayer): drawn over main-viewport ground at altitude, where the
// scaled far viewport (a backdrop behind that ground) cannot show its clouds.
#define RING_CLOUD_LAYER_MAIN_VIEW
#define RING_FAR_WEATHER_STORM_FLOOR 0.0
#include "res://materials/ring_cloud_layer.gdshaderinc"
