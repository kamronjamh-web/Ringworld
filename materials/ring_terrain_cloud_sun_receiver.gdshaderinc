// One lookup implementation for spatial, canvas and fog cloud receivers.
uniform sampler2D terrain_cloud_sun_global_texture : filter_linear, repeat_disable;
uniform sampler2D terrain_cloud_sun_regional_texture : filter_linear, repeat_disable;
uniform vec4 terrain_cloud_sun_profile[5];
#include "res://materials/ring_terrain_cloud_sun_shared.gdshaderinc"

float ring_terrain_cloud_receiver_visibility(vec3 canonical_metres) {
    return ring_terrain_cloud_sun_visibility(canonical_metres,
        terrain_cloud_sun_global_texture, terrain_cloud_sun_regional_texture,
        terrain_cloud_sun_profile).x;
}
