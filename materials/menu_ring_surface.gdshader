shader_type spatial;
render_mode cull_back, diffuse_burley, specular_schlick_ggx;

#include "res://materials/menu_flyover_terrain_common.gdshaderinc"

uniform sampler2D height_texture : filter_linear, repeat_enable;
uniform sampler2D color_texture : source_color, filter_linear, repeat_enable;

varying vec2 logical_texel;
varying vec3 local_point_km;
varying float ground_height_km;

float height_at(vec2 uv)
{
    return texture(height_texture, uv).r;
}

vec2 physical_uv(vec2 texel)
{
    return vec2((clamp(texel.x, 0.0, columns - 1.0) + 0.5) / columns,
        ring_row_uv(clamp(texel.y, 0.0, rows - 1.0)));
}

void vertex()
{
    float logical_row = UV.y * (rows - 1.0);
    float relative_around_km = window_start_km + logical_row * terrain_info.z;
    float across_km = terrain_info.y + UV.x * (columns - 1.0) * terrain_info.z;
    logical_texel = vec2(UV.x * (columns - 1.0), logical_row);
    vec2 terrain_uv = physical_uv(logical_texel);
    float height_km = height_at(terrain_uv);
    float height_x0 = height_at(physical_uv(logical_texel - vec2(1.0, 0.0)));
    float height_x1 = height_at(physical_uv(logical_texel + vec2(1.0, 0.0)));
    float height_z0 = height_at(physical_uv(logical_texel - vec2(0.0, 1.0)));
    float height_z1 = height_at(physical_uv(logical_texel + vec2(0.0, 1.0)));
    float angle = relative_around_km / radius_km;
    float cosine = cos(angle);
    float sine = sin(angle);
    VERTEX = vec3(across_km, radius_km * (1.0 - cosine) + height_km * cosine, -(radius_km - height_km) * sine);
    local_point_km = VERTEX;
    ground_height_km = height_km;
    float height_across_slope = (height_x1 - height_x0) / (2.0 * terrain_info.z);
    float height_around_slope = (height_z1 - height_z0) / (2.0 * terrain_info.z);
    vec3 across_tangent = vec3(1.0, height_across_slope * cosine, height_across_slope * sine);
    vec3 around_tangent = vec3(0.0, sine + height_around_slope * cosine, -cosine + height_around_slope * sine);
    NORMAL = normalize(cross(across_tangent, around_tangent));
}

void fragment()
{
    // Interpolate logical coordinates, never wrapped physical UVs: a triangle
    // crossing the last/first ring-buffer row must not span the entire atlas.
    vec2 terrain_uv = physical_uv(logical_texel);
    float terrain_available = min(
        texture(availability_texture, vec2(.5, ring_row_uv(floor(logical_texel.y)))).r,
        texture(availability_texture, vec2(.5, ring_row_uv(ceil(logical_texel.y)))).r);
    float weight = near_weight(local_point_km);
    if (terrain_available < 0.5 || weight <= handoff_hash(FRAGCOORD.xy)) discard;
    vec4 atlas_sample = texture(color_texture, terrain_uv);
    float water = atlas_sample.a * terrain_available;
    vec3 coarse_land = vec3(0.05, 0.08, 0.06);
    vec3 land = mix(coarse_land, atlas_sample.rgb, terrain_available);
    vec3 ocean = vec3(0.012, 0.075, 0.16);
    float distance_haze = distant_haze(local_point_km);
    float occlusion = cached_shadow(canonical_anchor_km + local_ground_coordinates(local_point_km), ground_height_km);
    ALBEDO = mix(land, ocean, water) * (1.0 - occlusion * shadow_strength);
    ALBEDO *= 1.0 - distance_haze;
    EMISSION = vec3(0.20, 0.28, 0.36) * distance_haze;
    // Unresolved ocean ripples form a broad, restrained highlight at this
    // altitude. A smooth mirror turns entire lakes white in the angled view.
    METALLIC = 0.0;
    ROUGHNESS = mix(0.82, 0.65, water);
    SPECULAR = mix(0.20, 0.06, water) * (1.0 - occlusion * shadow_strength) * (1.0 - distance_haze);
}
