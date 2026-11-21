// Copyright © 2023-2026 Cory Petkovsek, Roope Palmroos, and Contributors.
// Derived from Terrain3D extras/shaders/minimum.gdshader (MIT License).
// Curves the bounded, planar Terrain3D window into the local ring frame.

shader_type spatial;
render_mode blend_mix,depth_draw_opaque,cull_back,diffuse_burley,specular_schlick_ggx,skip_vertex_transform;
#define RING_GROUND_SURFACE
// T4 ground surface variant of terrain3d_ring_surface.gdshader (rock, strata, scree), for worlds with ground.surface; picked by RingGroundSurfaceVariants. The shipped terrain3d_ring_surface.gdshader is the
// generation 1 shader and stays exactly as it was. This is a full copy (not an include: Terrain3D reads the override shader text itself).
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_cloud_shadow.gdshaderinc"
#include "res://materials/ring_terrain_macro_shadow.gdshaderinc"
#include "res://materials/ring_terrain_grass_coverage.gdshaderinc"
#include "res://materials/ring_surface_albedo_scale.gdshaderinc"
#include "res://materials/ring_terrain_surface_albedo.gdshaderinc"
#include "res://materials/ring_biome_antitile.gdshaderinc"
#include "res://materials/ring_terrain_lighting.gdshaderinc"
#include "res://materials/ring_wetness.gdshaderinc"
#ifdef RING_GROUND_SURFACE
#include "res://materials/ring_ground_surface.gdshaderinc"
// Rock replaces dirt on steep ground in worlds with ground.surface: the dirt sample is skipped there.
#define GROUND_NOT_ON && !ground_on
// The alpine sample also serves rock and scree there, so it is taken even where the alpine weight is 0.
#define GROUND_OR_ON || ground_on
#else
#define GROUND_NOT_ON
#define GROUND_OR_ON
#endif

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
// Biome samplers and biome_texture_scale: ring_terrain_surface_albedo.gdshaderinc.
// Weather is supplied separately from the packed biome weights. The COLOR map's
// alpha channel remains the alpine biome weight.
// Wetness (darkening, gloss, puddles, ripples) comes from the per-location wet map: ring_wetness.gdshaderinc. Only the dry roughness is a terrain look.
uniform float surface_dry_roughness : hint_range(0.65, 0.98) = 0.92;
uniform bool flat_terrain_normals = false;
// Presentation-only smoothing over the authoritative RF lattice. Disable for
// the former per-cell normal and nearest biome-weight A/B reference.
uniform bool smooth_terrain_shading = true;
uniform vec3 ring_atmosphere_camera_world = vec3(0.0);
uniform vec3 ring_atmosphere_center_world = vec3(0.0);
uniform float ring_atmosphere_day_factor = 1.0;
uniform float ring_width_meters = 20000.0;
// Canonical UV at the local Terrain3D window anchor, supplied in double on
// the CPU before conversion so mega-ring positions retain macro-mask phase.
uniform vec2 ring_terrain_shadow_anchor_uv = vec2(0.0);
// Lighting uniforms (sun_direction, day dots, night tint, direct factors,
// shadow_floor): ring_terrain_lighting.gdshaderinc.
varying float v_vertex_xz_dist;
varying vec3 v_vertex;
varying float ring_day_factor;
varying float combined_cloud_shadow_occlusion;
varying float terrain_specular_f0;
varying float terrain_direct_specular_weight;
varying vec2 terrain_shadow_uv;
#ifdef RING_GROUND_SURFACE
// T4 colour variation (design D5): C64, the 64 m convexity, is computed per vertex from the window heights on the ring-wide 32 m lattice and interpolated; the proxy sectors interpolate the same
// lattice (stored by the builders), so both tiers agree at the hand-off. ring_ground_lattice_offset is the frame anchor modulo 32 m (around, across; RingTerrain3DSurface sets it).
uniform vec2 ring_ground_lattice_offset = vec2(0.0);
varying float v_ground_c64;
// D6 detail normal for rock and scree within 60 m (the rock face normal map, materials/ground/rock_face; the default is set in project.godot).
global uniform sampler2D ringworld_ground_rock_detail_normal : filter_linear_mipmap_anisotropic, repeat_enable;
// T8-R2: the rock face (materials/ground/rock_face), sampled at its real size (GROUND_ROCK_UV_SCALE) through its own anti-tile lattice. The alpine sample keeps serving the biome mix and scree.
uniform sampler2D ground_rock_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
#endif

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

#ifdef RING_GROUND_SURFACE
// Height of the window at a (cell) position; found = false where the window does not hold it.
float ground_tap(vec2 cell_uv, out bool found) {
    ivec3 coord = get_index_coord(cell_uv, VERTEX_PASS);
    found = coord.z >= 0;
    return found ? texelFetch(_height_maps, coord, 0).r : 0.0;
}

// D5: C64 at the lattice node at window position node_uv (cells): the height less the mean of the four taps 64 m away, as 0.5 * (Cx + Cz). Taps outside the window (it reaches 256 m behind its
// anchor and the player may be 128 m behind it) leave an axis incomplete: the node then takes the other axis alone, and 0 when neither is there.
float ground_node_c64(vec2 node_uv, float tap_cells) {
    bool node_found;
    float h = ground_tap(node_uv, node_found);
    if (!node_found) return 0.0;
    bool x0, x1, z0, z1;
    float hx0 = ground_tap(node_uv - vec2(tap_cells, 0.0), x0);
    float hx1 = ground_tap(node_uv + vec2(tap_cells, 0.0), x1);
    float hz0 = ground_tap(node_uv - vec2(0.0, tap_cells), z0);
    float hz1 = ground_tap(node_uv + vec2(0.0, tap_cells), z1);
    bool ok_x = x0 && x1;
    bool ok_z = z0 && z1;
    float cx = h - 0.5 * (hx0 + hx1);
    float cz = h - 0.5 * (hz0 + hz1);
    return ok_x && ok_z ? 0.5 * (cx + cz) : (ok_x ? cx : (ok_z ? cz : 0.0));
}

// D5: the bilinear value of the four nodes of the 32 m cell holding window position uv (cells); nodes of zero weight are not fetched.
float ground_c64_at(vec2 uv) {
    float spacing = _vertex_spacing;
    vec2 lattice = vec2(uv.x * spacing + ring_ground_lattice_offset.x, uv.y * spacing * ring_across_direction + ring_ground_lattice_offset.y) / GROUND_RELIEF_LATTICE_M;
    vec2 base = floor(lattice);
    vec2 f = lattice - base;
    float tap_cells = GROUND_RELIEF_TAP_M / spacing;
    float c = 0.0;
    for (int j = 0; j < 2; j++) {
        for (int i = 0; i < 2; i++) {
            float weight = (i == 0 ? 1.0 - f.x : f.x) * (j == 0 ? 1.0 - f.y : f.y);
            if (weight > 0.0) {
                vec2 node = (base + vec2(float(i), float(j))) * GROUND_RELIEF_LATTICE_M - ring_ground_lattice_offset;
                c += weight * ground_node_c64(vec2(node.x, node.y * ring_across_direction) / spacing, tap_cells);
            }
        }
    }
    return c;
}
#endif

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
#ifdef RING_GROUND_SURFACE
    bool ground_kept = false;
    v_ground_c64 = 0.0;
#endif
    ivec3 v_region = get_index_coord(start_pos, VERTEX_PASS);
    uint control = v_region.z < 0 ? 0u : floatBitsToUint(texelFetch(_control_maps, v_region, 0)).r;
    bool hole = bool(control >> 2u & 1u);
    if (hole || v_region.z < 0) { v_vertex.x = 0.0 / 0.0; }
    else {
        ivec3 uv_a = get_index_coord(start_pos, VERTEX_PASS);
        ivec3 uv_b = get_index_coord(end_pos, VERTEX_PASS);
        float h = mix(texelFetch(_height_maps, uv_a, 0).r, texelFetch(_height_maps, uv_b, 0).r, vertex_lerp);
        v_vertex = frame_to_world(curve_ring(v_vertex, h));
#ifdef RING_GROUND_SURFACE
        ground_kept = true;
#endif
    }
    // fragment() discards everything farther than the handoff (the proxy terrain draws it), in every pass. A vertex
    // farther than the handoff plus one triangle's extent (2.5 cells across, up to 32 m of height step) touches only
    // triangles that are discarded whole, so drop it here: same picture and shadows, no rasterisation or fragment work.
    if (ring_handoff_distance_meters > 0.0
            && distance(ring_viewer_world_position, v_vertex) > ring_handoff_distance_meters + 3.0 * scale + 32.0) {
        v_vertex.x = 0.0 / 0.0;
#ifdef RING_GROUND_SURFACE
        ground_kept = false;
#endif
    }
#ifdef RING_GROUND_SURFACE
    // C64 only for the vertices the drop kept, and only in worlds with ground.surface (about 20 height fetches, design D5).
    if (ground_kept && ring_ground_on()) v_ground_c64 = ground_c64_at(UV);
#endif
    VERTEX = (VIEW_MATRIX * vec4(v_vertex, 1.0)).xyz;
}

#ifdef RING_GROUND_SURFACE
// D6: the detail normal from the dominant projection of the albedo's triplanar (one tap), with the albedo's flips. Returns the normal in the texture frame (x around, y up, z across times the across direction).
// The map is an OpenGL-convention tangent-space normal map: +x along u, +y toward the top of the image (decreasing v). The offset fades with the dominant blend weight, so it is continuous at a change of axis.
vec3 ground_detail_normal(vec3 texture_position, vec3 texture_normal, vec3 blend, float weight, RingAntiTile ty, vec3 dpdx, vec3 dpdy) {
    float flip_x = texture_normal.x < 0.0 ? -1.0 : 1.0;
    float flip_y = texture_normal.y < 0.0 ? -1.0 : 1.0;
    float flip_z = texture_normal.z >= 0.0 ? -1.0 : 1.0;
    bool dom_x = blend.x >= blend.y && blend.x >= blend.z;
    bool dom_y = !dom_x && blend.y >= blend.z;
    vec2 uv = dom_x ? texture_position.zy : (dom_y ? texture_position.xz : texture_position.xy);
    vec2 dx = dom_x ? dpdx.zy : (dom_y ? dpdx.xz : dpdx.xy);
    vec2 dy = dom_x ? dpdy.zy : (dom_y ? dpdy.xz : dpdy.xy);
    float flip = dom_x ? flip_x : (dom_y ? flip_y : flip_z);
    float dom_blend = dom_x ? blend.x : (dom_y ? blend.y : blend.z);
    uv.x *= flip; dx.x *= flip; dy.x *= flip;
    // The top-down projection goes through the same anti-tiling taps as the albedo (ring_biome_antitile.gdshaderinc).
    vec2 t = (dom_y ? ring_antitile_sample_grad_rg(ringworld_ground_rock_detail_normal, ty, dx, dy) : textureGrad(ringworld_ground_rock_detail_normal, uv, dx, dy).rg) * 2.0 - 1.0;
    t.x *= flip;
    vec3 offset = dom_x ? vec3(0.0, -t.y, t.x) : (dom_y ? vec3(t.x, 0.0, -t.y) : vec3(t.x, -t.y, 0.0));
    return normalize(texture_normal + weight * dom_blend * offset);
}
#endif

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
    vec3 ground = mix(vec3(.16, .13, .075), vec3(.34, .28, .13), clamp(1.0 - slope * 2.0, 0.0, 1.0));
    float grass_coverage_eligibility = 0.0;
#ifdef RING_GROUND_SURFACE
    // T4 ground surface (ring_ground_surface.gdshaderinc): only worlds with ground.surface reach the blocks below. The rock and scree weights feed the wet and grass-coverage terms.
    bool ground_on = biome_texture_weights_enabled && ring_ground_on();
    float ground_rock = 0.0;
    float ground_scree = 0.0;
    vec3 ground_debug = vec3(0.0);
    float ground_relief = ground_on ? ring_ground_relief(v_ground_c64) : 0.0;
    float ground_detail_weight = 0.0;
    float ground_detail_delta = 0.0;
#endif
    if (biome_texture_weights_enabled) {
        vec4 packed = smooth_terrain_shading ? clamp(smooth_packed, 0.0, 1.0) : clamp(texelFetch(_color_maps, c, 0), 0.0, 1.0);
        float grass_weight = ring_terrain_grass_weight(packed);
        grass_coverage_eligibility = clamp(grass_weight + packed.g * .55, 0.0, 1.0);
        vec3 texture_normal = normalize(vec3(planar_normal.x, planar_normal.y, planar_normal.z * ring_across_direction));
        vec3 blend = pow(abs(texture_normal), vec3(8.0)); blend /= max(blend.x + blend.y + blend.z, .0001);
        vec3 texture_position = vec3(
            UV.x * _vertex_spacing + ring_texture_anchor_around_meters,
            surface_height + ring_anchor_height,
            UV.y * _vertex_spacing * ring_across_direction + ring_texture_anchor_across_meters) * biome_texture_scale;
        float ground_facing = smoothstep(.45, .78, planar_normal.y);
        // Ground anti-tiling (ring_biome_antitile.gdshaderinc): derivatives and the lattice in uniform control flow, every tap inside the branches is explicit-gradient.
        vec3 dpdx = dFdx(texture_position);
        vec3 dpdy = dFdy(texture_position);
        blend = ring_triplanar_prune(blend);
        RingAntiTile ty = ring_biome_antitile(texture_position, texture_normal, dpdx, dpdy);
        vec3 dirt = ground_facing < 1.0 GROUND_NOT_ON ? ring_biome_triplanar(biome_dirt_albedo, texture_position, texture_normal, blend, ty, dpdx, dpdy) : vec3(0.0);
        vec3 grass = vec3(0.0), desert = vec3(0.0), forest = vec3(0.0), tundra = vec3(0.0), alpine = vec3(0.0);
        if (grass_weight > 0.0) grass = ring_terrain_grass_tint(ring_biome_triplanar(biome_grass_albedo, texture_position, texture_normal, blend, ty, dpdx, dpdy));
        if (packed.r > 0.0) desert = ring_biome_triplanar(biome_desert_albedo, texture_position, texture_normal, blend, ty, dpdx, dpdy);
        if (packed.g > 0.0) forest = ring_biome_triplanar(biome_forest_albedo, texture_position, texture_normal, blend, ty, dpdx, dpdy);
        if (packed.b > 0.0) tundra = ring_biome_triplanar(biome_tundra_albedo, texture_position, texture_normal, blend, ty, dpdx, dpdy);
        if (packed.a > 0.0 GROUND_OR_ON) alpine = ring_biome_triplanar(biome_alpine_albedo, texture_position, texture_normal, blend, ty, dpdx, dpdy);
#ifdef RING_GROUND_SURFACE
        vec3 biome_surface = ring_terrain_biome_mix(grass, desert, forest, tundra, alpine, packed);
        ground = mix(dirt, biome_surface, ground_facing);
        if (ground_on) {
            // Rock replaces dirt on steep ground, scree sits on the slope foot (T4 design D2, D4). Relief arrives with T4-4b and is 0 here. fwidth stays outside the per-pixel branches.
            float strata_footprint = fwidth(surface_height + ring_anchor_height);
            float slope_degrees = ring_ground_slope_deg(planar_normal.y);
            ground_rock = ring_ground_rock_exposure(slope_degrees, GROUND_SLOPE_REF_BASELINE_M, ground_relief, packed.g);
            ground_scree = ring_ground_scree(slope_degrees, GROUND_SLOPE_REF_BASELINE_M, ground_relief, grass_weight + packed.g);
            float strata_factor = 1.0;
            ground = biome_surface;
            // T8-R2: the rock face has its own scale and lattice (arithmetic only, no implicit derivatives), so it is set up only on rock and scree pixels.
            vec3 rock_position = texture_position * GROUND_ROCK_UV_SCALE;
            vec3 rock_dpdx = dpdx * GROUND_ROCK_UV_SCALE;
            vec3 rock_dpdy = dpdy * GROUND_ROCK_UV_SCALE;
            RingAntiTile ty_rock = ring_antitile_off(rock_position.xz);
            if (ground_rock + ground_scree > 0.0) {
                ty_rock = ring_biome_antitile(rock_position, texture_normal, rock_dpdx, rock_dpdy);
                // Strata only where there is rock (D10: about 40 ALU on rock pixels only).
                vec3 rock_albedo = vec3(0.0);
                if (ground_rock > 0.0) {
                    strata_factor = ring_ground_strata_factor(surface_height + ring_anchor_height, strata_footprint);
                    rock_albedo = ring_biome_triplanar(ground_rock_albedo, rock_position, texture_normal, blend, ty_rock, rock_dpdx, rock_dpdy) * GROUND_ROCK_TINT * strata_factor;
                }
                ground = mix(mix(biome_surface, alpine * GROUND_SCREE_TINT, ground_scree), rock_albedo, ground_rock);
            }
            // D5 colour variation on the dry albedo (before wetness, grass coverage and the albedo scale), from the interpolated C64.
            float ground_distance = distance(v_vertex, ring_viewer_world_position);
            ground = ring_ground_relief_colour(ground, ground_relief, ground_rock, ground_distance);
            // D6 detail normal on rock and scree within 60 m; the wet functions keep the macro ring_normal.
            if (ground_distance < GROUND_DETAIL_NORMAL_FADE_M.y && ground_rock + ground_scree > 0.0) {
                ground_detail_weight = (ground_rock + 0.6 * ground_scree) * (1.0 - smoothstep(GROUND_DETAIL_NORMAL_FADE_M.x, GROUND_DETAIL_NORMAL_FADE_M.y, ground_distance));
                vec3 detailed = ground_detail_normal(rock_position, texture_normal, blend, ground_detail_weight, ty_rock, rock_dpdx, rock_dpdy);
                vec3 detailed_ring_normal = normalize(tangent * detailed.x + up * detailed.y + across * (detailed.z * ring_across_direction));
                ground_detail_delta = length(detailed_ring_normal - ring_normal);
                NORMAL = mat3(VIEW_MATRIX) * detailed_ring_normal;
            }
            ground_debug = vec3(ground_rock, ground_scree, 0.5 * strata_factor);
            if (ringworld_ground_look.w > 2.5) ground_debug = vec3(ground_detail_weight, clamp(ground_detail_delta, 0.0, 1.0), 0.0);
            else if (ringworld_ground_look.w > 1.5) ground_debug = vec3(0.5 + 0.5 * ground_relief, ring_ground_relief_colour(vec3(0.5), ground_relief, 0.0, ground_distance).g, 0.0);
        }
#else
        ground = mix(dirt, ring_terrain_biome_mix(grass, desert, forest, tundra, alpine, packed), ground_facing);
#endif
    }
    float ground_facing = smoothstep(.45, .78, planar_normal.y);
#ifdef RING_GROUND_SURFACE
    float wet_ground_facing = ground_facing;
    float coverage_upward = ground_facing;
    if (ground_on) {
        wet_ground_facing = 1.0 - ground_rock;
        coverage_upward = ground_facing * (1.0 - max(ground_rock, ground_scree));
    }
#define GROUND_WET_FACING wet_ground_facing
#define GROUND_COVERAGE_UPWARD coverage_upward
#else
#define GROUND_WET_FACING ground_facing
#define GROUND_COVERAGE_UPWARD ground_facing
#endif
    // Keep exposed rock subtly smoother while preserving a matte dry surface.
    float dry_roughness = surface_dry_roughness - slope * .08;
    // Wet world: per-location wetness (map x shelter) darkens the ground (soil 1, rock 0.5), glosses it, and collects puddles with ripples on flat hollows.
    float terrain_roughness = dry_roughness;
    float terrain_specular = .18;
    float wet_gloss = 0.0;
    vec3 dry_albedo = ground;
    if (ring_wet_params.w > 0.5) {
        RingWet wet_state = ring_wet_sample(v_vertex, ring_normal, up);
        ring_wet_apply(wet_state, mix(0.5, 1.0, GROUND_WET_FACING), dry_albedo, terrain_roughness, terrain_specular);
        wet_gloss = ring_wet_gloss(wet_state);
        float wet_distance = distance(v_vertex, ring_viewer_world_position);
        float flatness = ring_wet_flatness(ring_normal, up);
        if (wet_state.standing > 0.0 && ring_wet_params.z > 0.0 && flatness > 0.0 && wet_distance < 150.0) {
            // Cavity from four height taps (8 m): hollows fill first and empty last.
            float cavity = 0.5;
            if (wet_distance < 120.0) {
                float taps = height_or(get_index_coord(index_id + vec2(4.0, 0.0), SKIP_PASS), surface_height)
                    + height_or(get_index_coord(index_id + vec2(-4.0, 0.0), SKIP_PASS), surface_height)
                    + height_or(get_index_coord(index_id + vec2(0.0, 4.0), SKIP_PASS), surface_height)
                    + height_or(get_index_coord(index_id + vec2(0.0, -4.0), SKIP_PASS), surface_height);
                cavity = clamp(0.5 + 0.9 * (0.25 * taps - surface_height), 0.0, 1.0);
            }
            vec2 wet_plane = ring_wet_plane(v_vertex);
            vec2 puddle = ring_wet_puddle(wet_state, wet_plane, flatness, cavity, wet_distance);
            vec3 puddle_normal = ring_normal;
            ring_wet_apply_puddle(puddle, wet_state, wet_plane, up, wet_distance, dry_albedo, terrain_roughness, terrain_specular, puddle_normal);
            if (puddle.x > 0.0) NORMAL = mat3(VIEW_MATRIX) * puddle_normal;
            wet_gloss = max(wet_gloss, max(puddle.x, puddle.y));
        }
        float puddle_far = ring_wet_puddle_average(wet_state, flatness, wet_distance);
        terrain_roughness = mix(terrain_roughness, 0.12, puddle_far);
        wet_gloss = max(wet_gloss, puddle_far);
    }
    float grass_coverage = ring_terrain_grass_coverage(
        v_vertex,
        distance(v_vertex, ring_viewer_world_position),
        GROUND_COVERAGE_UPWARD,
        grass_coverage_eligibility);
    vec3 terrain_color = ring_terrain_albedo_scaled(ring_apply_terrain_grass_coverage(dry_albedo, grass_coverage));
    ring_day_factor = ringworld_effective_solar_day(smoothstep(
        day_start_dot,
        day_full_dot,
        dot(up, normalize(sun_direction))));
    // Retain the proxy terrain's low-light chroma before atmosphere haze is
    // composited, so the near and LOD surfaces carry the same night palette.
    vec3 lit_albedo = ring_terrain_night_tint(terrain_color, ring_day_factor);
    vec3 atmosphere_emission;
    ring_terrain_apply_atmosphere(
        lit_albedo,
        atmosphere_emission,
        ring_atmosphere_camera_world,
        v_vertex,
        ring_atmosphere_center_world,
        ring_day_factor);
    ALBEDO = lit_albedo;
    EMISSION = atmosphere_emission;
    ROUGHNESS = terrain_roughness;
    // Match the proxy's dry environment-reflection coefficient. Wet gloss alone
    // enables the added direct-light highlight below.
    SPECULAR = terrain_specular;
    // A custom light() function has no SPECULAR input, so carry the fragment
    // material's dielectric reflectance into that stage explicitly.
    terrain_specular_f0 = .16 * terrain_specular * terrain_specular;
    terrain_direct_specular_weight = wet_gloss;
    combined_cloud_shadow_occlusion = sample_local_cloud_shadow(v_vertex) * ring_day_factor;
    terrain_shadow_uv = vec2(
        ring_terrain_shadow_anchor_uv.x
            + UV.x / max(_vertex_density, 0.000001) / (6.28318530718 * ring_radius_meters),
        ring_terrain_shadow_anchor_uv.y
            + UV.y / max(_vertex_density, 0.000001) * ring_across_direction / max(ring_width_meters, 1.0));
    AO = min(1.0 - combined_cloud_shadow_occlusion * .72, ring_storm_ambient(ring_storm_level(v_vertex))); // storm light: ambient follows the storm level under the surface
    AO_LIGHT_AFFECT = 0.0;
    if (ring_shadow_debug_enabled()) {
        // Shadow debug view: only the selected shadow term reaches the screen (see ring_shadow_compose.gdshaderinc).
        ALBEDO = vec3(1.0);
        EMISSION = vec3(0.0);
        AO = 0.0;
        SPECULAR = 0.0;
        METALLIC = 0.0;
        ROUGHNESS = 1.0;
        terrain_direct_specular_weight = 0.0;
    }
#ifdef RING_GROUND_SURFACE
    if (ground_on && ringworld_ground_look.w > 0.5) {
        // Ground debug views (terrain_rock_material_gpu_test), never set by the game. w = 1: EMISSION = rock, scree, strata factor / 2; w = 2: 0.5 + relief / 2, the relief colour of mid grey (g), 0;
        // w = 3: detail normal weight, detail normal change (length of the normal difference), 0.
        ALBEDO = vec3(0.0);
        EMISSION = ground_debug;
        SPECULAR = 0.0;
        terrain_direct_specular_weight = 0.0;
    }
#endif
}

// This is the isotropic Schlick-GGX direct-specular branch from Godot's
// Forward+ renderer (scene_forward_lights_inc.glsl, MIT), expressed with the
// spatial-shader light built-ins. It keeps the existing rain-driven ROUGHNESS
// and SPECULAR values active after this shader takes ownership of lighting.
float terrain_d_ggx(float no_h, float alpha) {
    float a = no_h * alpha;
    float k = alpha / max(1.0 - no_h * no_h + a * a, 0.000001);
    // Godot's saturateHalf clamps only to the representable FP16 range, not
    // the 0..1 color range. Preserve bright narrow wet highlights.
    return min(k * k * (1.0 / PI), 65504.0);
}

float terrain_v_ggx(float no_l, float no_v, float alpha) {
    return min(0.5 / max(mix(2.0 * no_l * no_v, no_l + no_v, alpha), 0.000001), 65504.0);
}

float terrain_schlick_fresnel(float u) {
    float m = 1.0 - u;
    float m2 = m * m;
    return m2 * m2 * m;
}

void light() {
    float direct_visibility;
    DIFFUSE_LIGHT += ring_terrain_direct_diffuse(
        NORMAL,
        LIGHT,
        ATTENUATION,
        LIGHT_IS_DIRECTIONAL,
        LIGHT_COLOR,
        VIEW_MATRIX,
        ring_day_factor,
        combined_cloud_shadow_occlusion,
        terrain_shadow_uv,
        v_vertex,
        ring_viewer_world_position,
        SCREEN_UV,
        FRAGCOORD.xy,
        VIEWPORT_SIZE,
        INV_PROJECTION_MATRIX,
        RING_SHADOW_DEFAULT,
        direct_visibility);
    float n_dot_l = max(dot(NORMAL, LIGHT), 0.0);

    if (terrain_direct_specular_weight > 0.0 && ROUGHNESS > 0.0 && n_dot_l > 0.0) {
        vec3 half_vector = normalize(VIEW + LIGHT);
        float n_dot_v = max(dot(NORMAL, VIEW), 0.0001);
        float n_dot_h = clamp(dot(NORMAL, half_vector), 0.0, 1.0);
        float l_dot_h = clamp(dot(LIGHT, half_vector), 0.0, 1.0);
        float alpha_ggx = ROUGHNESS * ROUGHNESS;
        float distribution = terrain_d_ggx(n_dot_h, alpha_ggx);
        float visibility = terrain_v_ggx(n_dot_l, n_dot_v, alpha_ggx);
        float f90 = clamp(terrain_specular_f0 * (50.0 * 0.33 * 3.0), 0.0, 1.0);
        float fresnel = terrain_specular_f0
            + (f90 - terrain_specular_f0) * terrain_schlick_fresnel(l_dot_h);
        SPECULAR_LIGHT += vec3(n_dot_l * distribution * visibility * fresnel)
            * LIGHT_COLOR
            * direct_visibility
            * terrain_direct_specular_weight
            * SPECULAR_AMOUNT
            * (LIGHT_IS_DIRECTIONAL ? ring_storm_specular_keep(ring_storm_level(v_vertex)) : 1.0); // storm light: no sun glint under an overcast
    }
}
