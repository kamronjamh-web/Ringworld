#ifndef RING_WIND_FIELD_INCLUDED
#define RING_WIND_FIELD_INCLUDED
global uniform float ring_wind_field_enabled;
global uniform sampler2D ring_wind_gust_texture : filter_linear, repeat_enable;
global uniform vec2 ring_wind_mean_vector;
global uniform float ring_wind_gust_strength;
global uniform float ring_wind_gust_base;
global uniform vec3 ring_wind_anchor_world;
global uniform vec4 ring_wind_around_covector;
global uniform vec4 ring_wind_across_covector;
global uniform vec2 ring_wind_anchor_uv;
global uniform vec2 ring_wind_gust_velocity_uv;
global uniform vec4 ring_wind_phase;
vec2 ring_wind_uv_at_world(vec3 position,float lag_seconds) {
 vec3 delta=position-ring_wind_anchor_world;
 vec2 uv=ring_wind_anchor_uv+vec2(dot(delta,ring_wind_around_covector.xyz)*ring_wind_around_covector.w,dot(delta,ring_wind_across_covector.xyz)*ring_wind_across_covector.w)+ring_wind_gust_velocity_uv*lag_seconds;
 return uv;
}
float ring_wind_noise_at_world(vec3 position,float lag_seconds) {
 if(ring_wind_field_enabled<0.5)return 0.5;
 return textureLod(ring_wind_gust_texture,ring_wind_uv_at_world(position,lag_seconds),0.0).r;
}
float ring_wind_gust_at_world(vec3 position,float lag_seconds) {
 if(ring_wind_field_enabled<0.5)return 1.0;
 return clamp(ring_wind_gust_base+ring_wind_gust_strength*ring_wind_noise_at_world(position,lag_seconds)*1.6,0.0,4.0);
}
vec2 ring_wind_at_world(vec3 position,float lag_seconds) {return ring_wind_mean_vector*ring_wind_gust_at_world(position,lag_seconds);}
#endif
