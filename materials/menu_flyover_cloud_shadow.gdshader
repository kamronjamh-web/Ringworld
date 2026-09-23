shader_type canvas_item;

#include "menu_flyover_cloud_common.gdshaderinc"

uniform float seed = 1337.0;
uniform float time_seconds;
uniform float ring_radius_km = 1000.0;
uniform float span_km = 256.0;
uniform vec2 origin_km;

void fragment() {
    vec2 canonical = origin_km + UV * span_km;
    float occlusion = 1.0 - exp(-2.1 * menu_cloud_coverage(menu_weather(canonical, ring_radius_km, seed, time_seconds)));
    // Data texture: alpha must stay one, never attenuating its own samples.
    COLOR = vec4(vec3(occlusion), 1.0);
}
