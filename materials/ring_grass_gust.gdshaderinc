// Shared texture gust field. The owner supplies canonical covectors per tile;
// this keeps the sample continuous through floating-origin rebases and the ring seam.
uniform sampler2D grass_gust_texture : repeat_enable, filter_linear_mipmap;
uniform bool grass_gust_enabled = false;
uniform float grass_gust_strength = 0.45;
uniform float grass_gust_base_breeze = 0.65;
instance uniform vec3 grass_gust_origin_world;
instance uniform vec4 grass_gust_around_world;
instance uniform vec4 grass_gust_across_world;
instance uniform vec2 grass_gust_origin_uv;

float grass_texture_gust(vec3 root) {
    if (ring_wind_field_enabled > .5) {
        if (!grass_gust_enabled || grass_gust_strength <= 0.0) return 1.0;
        float shared_gust = ring_wind_gust_at_world(root, 0.0);
        // Overall bend and local patch sensitivity are independent controls.
        // Base breeze and the legacy harmonic multiplier are standalone-only.
        return max(0.0, mix(1.0, shared_gust, clamp(grass_gust_strength, 0.0, 2.0)));
    }
    if (!grass_gust_enabled || grass_gust_strength <= 0.0 || grass_gust_around_world.w <= 0.0 || grass_gust_across_world.w <= 0.0) return 1.0;
    vec3 delta = root - grass_gust_origin_world;
    vec2 uv = grass_gust_origin_uv + vec2(dot(delta, grass_gust_around_world.xyz) * grass_gust_around_world.w,
        dot(delta, grass_gust_across_world.xyz) * grass_gust_across_world.w);
    float noise = texture(grass_gust_texture, fract(uv)).r;
    float target = grass_gust_base_breeze + noise * (2.0 - grass_gust_base_breeze);
    return max(0.0, mix(1.0, target, clamp(grass_gust_strength, 0.0, 2.0)));
}
