// Copyright © 2023-2026 Cory Petkovsek, Roope Palmroos, and Contributors.
// Derived from Terrain3D extras/shaders/minimum.gdshader (MIT License).
// Curves the bounded, planar Terrain3D window into the local ring frame.

shader_type spatial;
render_mode blend_mix,depth_draw_opaque,cull_back,diffuse_burley,specular_schlick_ggx,skip_vertex_transform;
#include "res://materials/ring_atmosphere.gdshaderinc"

#define SKIP_PASS 0
#define VERTEX_PASS 1
#define FRAGMENT_PASS 2
#if CURRENT_RENDERER == RENDERER_COMPATIBILITY
    #define fma(a,b,c) ((a)*(b)+(c))
    #define dFdxCoarse(a) dFdx(a)
    #define dFdyCoarse(a) dFdy(a)
#endif

uniform vec3 _camera_pos = vec3(0.0);
uniform float _mesh_size = 48.0;
uniform uint _background_mode = 0u;
uniform float _vertex_spacing = 2.0;
uniform float _vertex_density = .5;
uniform float _region_size = 128.0;
uniform float _region_texel_size = .0078125;
uniform int _region_map_size = 32;
uniform int _region_map[1024];
uniform highp sampler2DArray _height_maps : repeat_disable;
uniform highp sampler2DArray _control_maps : repeat_disable;
uniform highp sampler2DArray _color_maps : repeat_disable;

// Set by RingTerrain3DSurface through the Terrain3D material override.
uniform float ring_anchor_height = 0.0;
uniform float ring_cull_margin_meters = 1.0;
uniform mat3 ring_frame_basis = mat3(1.0);
uniform vec3 ring_frame_origin = vec3(0.0);
uniform float ring_handoff_distance_meters = 0.0;
uniform vec3 ring_viewer_world_position = vec3(0.0);
uniform float ring_texture_anchor_around_meters = 0.0;
uniform float ring_texture_anchor_across_meters = 0.0;
uniform float ring_across_direction = 1.0;
uniform bool biome_texture_weights_enabled = false;
uniform float biome_texture_scale = .20;
uniform sampler2D biome_grass_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D biome_dirt_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D biome_desert_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D biome_forest_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D biome_tundra_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D biome_alpine_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
// Weather is supplied separately from the packed biome weights. The COLOR map's
// alpha channel remains the alpine biome weight.
uniform float surface_wetness : hint_range(0.0, 1.0) = 0.0;
uniform float surface_wetness_strength : hint_range(0.0, 1.0) = 1.0;
uniform float surface_dry_roughness : hint_range(0.65, 0.98) = 0.92;
uniform float surface_wet_roughness : hint_range(0.10, 0.75) = 0.35;
uniform bool flat_terrain_normals = false;
// Presentation-only smoothing over the authoritative RF lattice. Disable for
// the former per-cell normal and nearest biome-weight A/B reference.
uniform bool smooth_terrain_shading = true;
uniform vec3 ring_atmosphere_camera_world = vec3(0.0);
uniform vec3 ring_atmosphere_center_world = vec3(0.0);
uniform float ring_atmosphere_day_factor = 1.0;
varying float v_vertex_xz_dist;
varying vec3 v_vertex;

ivec3 get_index_coord(const vec2 uv, const int search) {
    vec2 r_uv = round(uv);
    vec2 o_uv = mod(r_uv, _region_size);
    ivec2 pos;
    int bounds; int layer_index = -1;
    for (int i = -1; i < clamp(search, SKIP_PASS, FRAGMENT_PASS); i++) {
        if ((layer_index == -1 && _background_mode == 0u) || i < 0) {
            r_uv -= i == -1 ? vec2(0.0) : vec2(float(o_uv.x <= o_uv.y), float(o_uv.y <= o_uv.x));
            pos = ivec2(floor(r_uv * _region_texel_size)) + (_region_map_size / 2);
            bounds = int(uint(pos.x | pos.y) < uint(_region_map_size));
            int lookup = pos.y * _region_map_size + pos.x;
            // `bounds` prevents an out-of-range dynamic-array lookup for edge tiles.
            layer_index = bounds == 0 ? -1 : _region_map[lookup] - 1;
        }
    }
    return ivec3(ivec2(mod(r_uv, _region_size)), layer_index);
}

vec3 curve_ring(vec3 planar, float h) {
    float theta = planar.x / ring_radius_meters;
    float radius_at_height = ring_radius_meters - (h + ring_anchor_height);
    // Terrain3D's bounded window is normally below |theta| = 0.0002. Some GPU
    // sin implementations lose enough precision near zero to visibly move the
    // local X coordinate. Through |theta| < .01, these series omit less than
    // 1e-10 m at the certified 5,000 km radius.
    if (abs(theta) < .01) {
        float theta2 = theta * theta;
        float sin_theta = theta * (1.0 - theta2 * (1.0 / 6.0 - theta2 / 120.0));
        float one_minus_cos = .5 * theta2 * (1.0 - theta2 * (1.0 / 12.0 - theta2 / 360.0));
        return vec3(radius_at_height * sin_theta,
            h + radius_at_height * one_minus_cos, planar.z);
    }
    return vec3(radius_at_height * sin(theta),
        h + 2.0 * radius_at_height * sin(theta * .5) * sin(theta * .5), planar.z);
}

vec3 frame_to_world(vec3 local_position) { return ring_frame_origin + ring_frame_basis * local_position; }

// Region edges can have no neighbor layer. Never pass layer -1 to texelFetch;
// callers supply the closest valid center or edge height as the fallback.
float height_or(ivec3 coord, float fallback) {
    return coord.z < 0 ? fallback : texelFetch(_height_maps, coord, 0).r;
}

vec4 color_or(ivec3 coord, vec4 fallback) {
    return coord.z < 0 ? fallback : texelFetch(_color_maps, coord, 0);
}

vec3 sample_biome_triplanar(sampler2D texture_map, vec3 position, vec3 normal, vec3 blend) {
    vec2 x_uv = position.zy;
    vec2 y_uv = position.xz;
    vec2 z_uv = position.xy;
    x_uv.x *= normal.x < 0.0 ? -1.0 : 1.0;
    y_uv.x *= normal.y < 0.0 ? -1.0 : 1.0;
    z_uv.x *= normal.z >= 0.0 ? -1.0 : 1.0;
    return texture(texture_map, x_uv).rgb * blend.x + texture(texture_map, y_uv).rgb * blend.y + texture(texture_map, z_uv).rgb * blend.z;
}

void vertex() {
    v_vertex = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    v_vertex_xz_dist = length(v_vertex.xz - _camera_pos.xz);
    float scale = MODEL_MATRIX[0][0];
    float vertex_lerp = smoothstep(.55, .95, (v_vertex_xz_dist / scale - _mesh_size - 4.0) / (_mesh_size - 2.0));
    vec2 v_fract = fract(VERTEX.xz * .5) * 2.0;
    vec2 shift = (scale < _vertex_spacing + 1e-6) ?
        mix(v_fract, vec2(v_fract.x, -v_fract.y), round(fract(round(mod(v_vertex.z * _vertex_density, 4.0)) * round(mod(v_vertex.x * _vertex_density, 4.0)) * .25))) :
        v_fract * round((fract(v_vertex.xz * .25 / scale) - .5) * 4.0);
    vec2 start_pos = v_vertex.xz * _vertex_density;
    vec2 end_pos = (v_vertex.xz - shift * scale) * _vertex_density;
    v_vertex.xz -= shift * scale * vertex_lerp;
    UV = v_vertex.xz * _vertex_density;
    UV2 = fma(UV, vec2(_region_texel_size), vec2(.5 * _region_texel_size));
    ivec3 v_region = get_index_coord(start_pos, VERTEX_PASS);
    uint control = v_region.z < 0 ? 0u : floatBitsToUint(texelFetch(_control_maps, v_region, 0)).r;
    bool hole = bool(control >> 2u & 1u);
    if (hole || v_region.z < 0) { v_vertex.x = 0.0 / 0.0; }
    else {
        ivec3 uv_a = get_index_coord(start_pos, VERTEX_PASS);
        ivec3 uv_b = get_index_coord(end_pos, VERTEX_PASS);
        float h = mix(texelFetch(_height_maps, uv_a, 0).r, texelFetch(_height_maps, uv_b, 0).r, vertex_lerp);
        v_vertex = frame_to_world(curve_ring(v_vertex, h));
    }
    VERTEX = (VIEW_MATRIX * vec4(v_vertex, 1.0)).xyz;
}

void fragment() {
    // Terrain3D appends editor/debug overlays that reference this local name.
    vec2 uv = UV;
    vec2 index_id = floor(uv);
    ivec3 c = get_index_coord(index_id, FRAGMENT_PASS);
    ivec3 x = get_index_coord(index_id + vec2(1.0, 0.0), FRAGMENT_PASS);
    ivec3 z = get_index_coord(index_id + vec2(0.0, 1.0), FRAGMENT_PASS);
    if (ring_handoff_distance_meters > 0.0 && distance(ring_viewer_world_position, v_vertex) > ring_handoff_distance_meters) discard;
    vec3 planar_normal;
    float surface_height;
    vec4 smooth_packed = vec4(0.0);
    if (!smooth_terrain_shading) {
        // Preserve the former per-cell h/hx/hz shading path for A/B checks.
        float h = texelFetch(_height_maps, c, 0).r;
        float hx = texelFetch(_height_maps, x, 0).r;
        float hz = texelFetch(_height_maps, z, 0).r;
        planar_normal = normalize(vec3(h - hx, _vertex_spacing, h - hz));
        surface_height = h;
    } else {
        // Twelve unique samples cover four central vertices and the one-cell
        // ring needed for their central differences. The four center heights
        // also provide the fragment-only height interpolation below.
        ivec3 h00_coord = c;
        ivec3 h10_coord = x;
        ivec3 h01_coord = z;
        ivec3 h11_coord = get_index_coord(index_id + vec2(1.0, 1.0), FRAGMENT_PASS);
        ivec3 left0_coord = get_index_coord(index_id + vec2(-1.0, 0.0), FRAGMENT_PASS);
        ivec3 left1_coord = get_index_coord(index_id + vec2(-1.0, 1.0), FRAGMENT_PASS);
        ivec3 right0_coord = get_index_coord(index_id + vec2(2.0, 0.0), FRAGMENT_PASS);
        ivec3 right1_coord = get_index_coord(index_id + vec2(2.0, 1.0), FRAGMENT_PASS);
        ivec3 down0_coord = get_index_coord(index_id + vec2(0.0, -1.0), FRAGMENT_PASS);
        ivec3 down1_coord = get_index_coord(index_id + vec2(1.0, -1.0), FRAGMENT_PASS);
        ivec3 up0_coord = get_index_coord(index_id + vec2(0.0, 2.0), FRAGMENT_PASS);
        ivec3 up1_coord = get_index_coord(index_id + vec2(1.0, 2.0), FRAGMENT_PASS);
        float h00 = height_or(h00_coord, 0.0);
        float h10 = height_or(h10_coord, h00);
        float h01 = height_or(h01_coord, h00);
        float h11 = height_or(h11_coord, h10);
        // Visible fragments always have c, but retain a valid central fallback
        // for a transient edge tile while the plugin changes its region map.
        if (h00_coord.z < 0) h00 = h10_coord.z >= 0 ? h10 : (h01_coord.z >= 0 ? h01 : h11);
        float left0 = height_or(left0_coord, h00);
        float left1 = height_or(left1_coord, h01);
        float right0 = height_or(right0_coord, h10);
        float right1 = height_or(right1_coord, h11);
        float down0 = height_or(down0_coord, h00);
        float down1 = height_or(down1_coord, h10);
        float up0 = height_or(up0_coord, h01);
        float up1 = height_or(up1_coord, h11);
        vec2 cell = fract(uv);
        float dx0 = mix(h10 - left0, right0 - h00, cell.x);
        float dx1 = mix(h11 - left1, right1 - h01, cell.x);
        float dz0 = mix(h01 - down0, h11 - down1, cell.x);
        float dz1 = mix(up0 - h00, up1 - h10, cell.x);
        float dx = mix(dx0, dx1, cell.y);
        float dz = mix(dz0, dz1, cell.y);
        planar_normal = normalize(vec3(-dx, 2.0 * _vertex_spacing, -dz));
        surface_height = mix(mix(h00, h10, cell.x), mix(h01, h11, cell.x), cell.y);
        if (biome_texture_weights_enabled) {
            vec4 p00 = color_or(h00_coord, vec4(0.0));
            vec4 p10 = color_or(h10_coord, p00);
            vec4 p01 = color_or(h01_coord, p00);
            vec4 p11 = color_or(h11_coord, p10);
            if (h00_coord.z < 0) p00 = h10_coord.z >= 0 ? p10 : (h01_coord.z >= 0 ? p01 : p11);
            smooth_packed = mix(mix(p00, p10, cell.x), mix(p01, p11, cell.x), cell.y);
        }
    }
    float theta = UV.x * _vertex_spacing / ring_radius_meters;
    vec3 tangent = normalize(ring_frame_basis * vec3(cos(theta), sin(theta), 0.0));
    vec3 up = normalize(ring_frame_basis * vec3(-sin(theta), cos(theta), 0.0));
    vec3 across = normalize(ring_frame_basis * vec3(0.0, 0.0, 1.0));
    vec3 ring_normal = normalize(tangent * planar_normal.x + up * planar_normal.y + across * planar_normal.z);
    NORMAL = mat3(VIEW_MATRIX) * ring_normal;
    TANGENT = mat3(VIEW_MATRIX) * tangent;
    BINORMAL = mat3(VIEW_MATRIX) * cross(ring_normal, tangent);
    float slope = 1.0 - clamp(planar_normal.y, 0.0, 1.0);
    float snow = smoothstep(3500.0, 5200.0, surface_height + ring_anchor_height) * (1.0 - slope);
    vec3 ground = mix(vec3(.16, .13, .075), vec3(.34, .28, .13), clamp(1.0 - slope * 2.0, 0.0, 1.0));
    if (biome_texture_weights_enabled) {
        vec4 packed = smooth_terrain_shading ? clamp(smooth_packed, 0.0, 1.0) : clamp(texelFetch(_color_maps, c, 0), 0.0, 1.0);
        float grass_weight = max(0.0, 1.0 - packed.r - packed.g - packed.b - packed.a);
        float total = max(.0001, grass_weight + packed.r + packed.g + packed.b + packed.a);
        vec3 texture_normal = normalize(vec3(planar_normal.x, planar_normal.y, planar_normal.z * ring_across_direction));
        vec3 blend = pow(abs(texture_normal), vec3(8.0)); blend /= max(blend.x + blend.y + blend.z, .0001);
        vec3 texture_position = vec3(
            UV.x * _vertex_spacing + ring_texture_anchor_around_meters,
            surface_height + ring_anchor_height,
            UV.y * _vertex_spacing * ring_across_direction + ring_texture_anchor_across_meters) * biome_texture_scale;
        float ground_facing = smoothstep(.45, .78, planar_normal.y);
        vec3 dirt = ground_facing < 1.0 ? sample_biome_triplanar(biome_dirt_albedo, texture_position, texture_normal, blend) : vec3(0.0);
        vec3 grass = sample_biome_triplanar(biome_grass_albedo, texture_position, texture_normal, blend);
        grass = mix(grass, grass * vec3(.72, 1.30, .62), .58);
        vec3 surface = grass * grass_weight;
        surface += sample_biome_triplanar(biome_desert_albedo, texture_position, texture_normal, blend) * packed.r;
        surface += sample_biome_triplanar(biome_forest_albedo, texture_position, texture_normal, blend) * packed.g;
        surface += sample_biome_triplanar(biome_tundra_albedo, texture_position, texture_normal, blend) * packed.b;
        surface += sample_biome_triplanar(biome_alpine_albedo, texture_position, texture_normal, blend) * packed.a;
        ground = mix(dirt, surface / total, ground_facing);
    }
    float ground_facing = smoothstep(.45, .78, planar_normal.y);
    float wet = clamp(surface_wetness * surface_wetness_strength, 0.0, 1.0) * ground_facing * (1.0 - snow);
    // Keep exposed rock subtly smoother while preserving a matte dry surface.
    float dry_roughness = surface_dry_roughness - slope * .08;
    vec3 dry_albedo = ground * (1.0 - wet * .15);
    ALBEDO = mix(dry_albedo, vec3(.78, .82, .86), snow);
	vec4 atmosphere = sample_ring_atmosphere(
		ring_atmosphere_camera_world,
		v_vertex,
		ring_atmosphere_center_world,
		ringworld_effective_solar_day(ring_atmosphere_day_factor));
	ALBEDO *= 1.0 - atmosphere.a;
	EMISSION = atmosphere.rgb * atmosphere.a * atmosphere_scattering_energy;
    ROUGHNESS = mix(dry_roughness, surface_wet_roughness, wet);
    SPECULAR = mix(.20, .50, wet);
}
