shader_type spatial;
render_mode cull_back, diffuse_burley, specular_schlick_ggx;

#include "res://materials/menu_flyover_terrain_common.gdshaderinc"

uniform sampler2D height_texture : filter_linear, repeat_enable;
uniform sampler2D color_texture : source_color, filter_linear, repeat_enable;
varying vec3 local_point_km;
varying float ground_height_km;

vec2 atlas_uv(vec2 uv) {
    vec2 size = vec2(textureSize(height_texture, 0));
    return vec2(fract(uv.x) + 0.5 / size.x,
        (clamp(uv.y, 0.0, 1.0) * (size.y - 1.0) + 0.5) / size.y);
}

void vertex() {
    float angle = UV.x * TAU - canonical_anchor_km.x / radius_km;
    float height_km = texture(height_texture, atlas_uv(UV)).r;
    float cosine = cos(angle);
    float sine = sin(angle);
    VERTEX = vec3((UV.y - 0.5) * ring_width_km,
        radius_km * (1.0 - cosine) + height_km * cosine,
        -(radius_km - height_km) * sine);
    NORMAL = vec3(0.0, cosine, sine);
    local_point_km = VERTEX;
    ground_height_km = height_km;
}

void fragment() {
    if (handoff_hash(FRAGCOORD.xy) < near_weight(local_point_km)) discard;
    vec4 color = texture(color_texture, atlas_uv(UV));
    float shadow = cached_shadow(canonical_anchor_km + local_ground_coordinates(local_point_km), ground_height_km);
    float haze = distant_haze(local_point_km);
    // Match the fine mesh's restrained ocean tint across the handoff.
    ALBEDO = mix(color.rgb, vec3(0.012, 0.075, 0.16), color.a)
        * (1.0 - shadow * shadow_strength) * (1.0 - haze);
    EMISSION = vec3(0.20, 0.28, 0.36) * haze;
    METALLIC = 0.0;
    ROUGHNESS = mix(0.82, 0.65, color.a);
    SPECULAR = mix(0.20, 0.06, color.a) * (1.0 - shadow * shadow_strength) * (1.0 - haze);
}
