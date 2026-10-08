shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, depth_test_disabled, cull_disabled, fog_disabled;
#include "res://materials/ring_wind_field.gdshaderinc"
uniform sampler2D scene_depth : hint_depth_texture, repeat_disable, filter_nearest;
uniform float rain_intensity = 0.0;
uniform float haze_density = 0.004;
uniform float near_clear_distance = 8.0;
uniform float maximum_opacity = 0.6;
uniform float variation_strength = 0.25;
// The owner converts the sRGB picker value to linear RGB for both rain paths.
uniform vec3 haze_tint = vec3(0.195994, 0.242867, 0.295700);
uniform float daylight = 1.0;
uniform float sky_distance = 750.0;

void vertex() {
    // Forward+ reverse-Z: the overlay is in front of the scene, while the
    // sampled opaque depth still describes the original terrain/objects.
    POSITION = vec4(VERTEX.xy, 1.0, 1.0);
}

void fragment() {
    float raw_depth = textureLod(scene_depth, SCREEN_UV, 0.0).r;
#if CURRENT_RENDERER == RENDERER_COMPATIBILITY
    vec3 depth_ndc = vec3(SCREEN_UV * 2.0 - 1.0, raw_depth * 2.0 - 1.0);
#else
    vec3 depth_ndc = vec3(SCREEN_UV * 2.0 - 1.0, raw_depth);
#endif
    vec4 scene_h = INV_PROJECTION_MATRIX * vec4(depth_ndc, 1.0);
    vec4 ray_h = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, 1.0, 1.0);
    vec3 view_ray = normalize(ray_h.xyz / max(ray_h.w, 0.000001));
    float scene_distance = sky_distance;
    if (raw_depth > 0.0 && scene_h.w > 0.0
        && !any(isnan(scene_h)) && !any(isinf(scene_h))) {
        scene_distance = min(length(scene_h.xyz / scene_h.w), sky_distance);
    }
    float wet_path = max(0.0, scene_distance - near_clear_distance);
    // One broad shared wind sample is sufficient for veils. No independent
    // TIME animation or screen-space streaks can drift during a paused clock.
    vec3 sample_world = CAMERA_POSITION_WORLD
        + (INV_VIEW_MATRIX * vec4(view_ray * min(scene_distance * 0.5, 250.0), 0.0)).xyz;
    float gust = ring_wind_field_enabled > 0.5 ? ring_wind_gust_at_world(sample_world, 0.0) : 1.0;
    float modulation = mix(1.0, clamp(0.7 + gust * 0.3, 0.7, 1.3), variation_strength);
    float optical_depth = max(0.0, haze_density) * clamp(rain_intensity, 0.0, 1.0) * wet_path * modulation;
    float opacity = min(1.0 - exp(-optical_depth), maximum_opacity * rain_intensity);
    ALBEDO = haze_tint * mix(0.035, 1.0, clamp(daylight, 0.0, 1.0));
    ALPHA = clamp(opacity, 0.0, 0.9);
}
