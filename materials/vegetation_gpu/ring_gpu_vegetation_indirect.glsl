#[compute]
#version 450
layout(constant_id = 0) const bool VEGETATION_HAZE = false;
layout(constant_id = 1) const bool VEGETATION_SHADOW = false;

layout(local_size_x = 64) in;
layout(set = 0, binding = 0, std430) buffer Sources { float source[]; }; // written only by the phase 5 expansion (tree sources built from compact roots)
layout(set = 0, binding = 1, std430) readonly buffer Metadata { vec4 metadata[]; };
layout(set = 0, binding = 2, std430) buffer OutputRows { float output_row[]; };
layout(set = 0, binding = 3, std430) buffer Commands { uint command[]; };
layout(set = 0, binding = 4, std430) readonly buffer Parameters { float p[]; };
layout(set = 0, binding = 5, std430) buffer Flags { uint local_offset[]; };
layout(set = 0, binding = 6, std430) buffer Counts { uint group_count[]; };
layout(set = 0, binding = 7, std430) buffer Offsets { uint group_offset[]; };
layout(set = 0, binding = 8, std430) buffer Budgets { uint used[5]; };
// Phase 6 only (tree batches with a camera list): the frustum-filtered copy of the output rows (the camera draws it; the full list stays the shadow casters'),
// and, in verify mode, the rows the filter dropped. Batches without them bind the small budget buffer, which is never touched.
layout(set = 0, binding = 9, std430) buffer CameraRows { float camera_row[]; };
layout(set = 0, binding = 10, std430) buffer CameraCommands { uint camera_command[]; };
layout(set = 0, binding = 11, std430) buffer VerifyRows { float verify_row[]; };
layout(set = 0, binding = 12, std430) buffer VerifyCommands { uint verify_command[]; };
layout(push_constant, std430) uniform Push {
    uint phase, count, capacity, surfaces;
    uint sweep, protected_base_valid, haze_base, haze_tag;
    vec4 haze_anchor; // xyz = anchor in mesh-local row space (trees: mesh AABB centre; grass / shared impostor: 0), w > .5 = write haze
    vec4 shadow_flags; // x > .5 = write the shadow slot (same slot index and tag as the haze slot, anchor = the row root); phase 6: y = the full list's slot base (exact as a float: at most 2^24 slots)
} q;
layout(set = 1, binding = 0, std140) uniform HazeParams {
    vec4 sigma_R_H_R; vec4 aerosol; vec4 geometry; vec4 sides; vec4 outward; vec4 axis; vec4 to_sun; vec4 camera; vec4 storm;
} haze;
layout(set = 1, binding = 8, std140) uniform CameraCull {
    vec4 planes[6]; vec4 settings;
    vec4 shadow_planes[6]; vec4 shadow_dirs[2]; vec4 shadow_settings; // phase 7 only, see shadow_keeps
} cull; // phases 6 and 7, see camera_keeps / shadow_keeps
layout(rgba16f, set = 1, binding = 1) uniform restrict image2D haze_atlas; // phase 6 reads the full list's terms to copy them
layout(rgba16f, set = 1, binding = 3) uniform restrict image2D shadow_atlas;
#include "res://materials/vegetation_gpu/ring_vegetation_shadow_terms.glslinc"
#include "res://materials/ring_atmosphere_shared.gdshaderinc"
#include "res://materials/ring_haze_shared.gdshaderinc"
#include "res://materials/atmosphere/ring_vegetation_haze_terms.gdshaderinc"
shared uint scan[64];
shared uint scan_dropped[64];

void write_vegetation_haze(uint slot, uint tag, vec3 root, vec3 local_x, vec3 local_y, vec3 local_z) {
    if (!VEGETATION_HAZE || q.haze_anchor.w < .5) return;
    vec3 local_anchor = root + local_x * q.haze_anchor.x + local_y * q.haze_anchor.y + local_z * q.haze_anchor.z;
    mat3 basis = mat3(vec3(p[16], p[17], p[18]), vec3(p[19], p[20], p[21]), vec3(p[22], p[23], p[24]));
    vec3 world = basis * local_anchor + vec3(p[25], p[26], p[27]);
    RingHazeParams hp = ring_haze_params_default();
    hp.sigma_R = haze.sigma_R_H_R.xyz; hp.H_R = haze.sigma_R_H_R.w;
    hp.sigma_A = haze.aerosol.x; hp.omega_A = haze.aerosol.y; hp.H_A = haze.aerosol.z; hp.cap_height = haze.aerosol.w;
    hp.g = haze.sides.w;
    hp.quality = int(haze.geometry.w + .5); hp.to_sun = normalize(haze.to_sun.xyz);
    hp = ring_haze_apply_storm(hp, haze.storm);
    RingVegetationHazeTerms t = ring_vegetation_haze_terms(hp, world, haze.camera.xyz, haze.geometry.z, haze.outward.xyz, haze.axis.xyz,
        haze.sides.x, haze.sides.y, haze.sides.z, haze.geometry.x, haze.geometry.y);
    ivec2 texel = ring_vegetation_haze_texel(int(slot));
    imageStore(haze_atlas, texel, ring_vegetation_haze_pack0(t, float(tag)));
    imageStore(haze_atlas, texel + ivec2(1, 0), ring_vegetation_haze_pack1(t));
}

// Per-instance terrain height-shadow and cloud terms at the row root (grass: the card root; trees: the trunk base). Layout: ring_vegetation_shadow_terms.glslinc.
void write_vegetation_shadow(uint slot, uint tag, vec3 root, vec3 local_x, vec3 local_y, vec3 local_z) {
    if (!VEGETATION_SHADOW || q.shadow_flags.x < .5) return;
    mat3 basis = mat3(vec3(p[16], p[17], p[18]), vec3(p[19], p[20], p[21]), vec3(p[22], p[23], p[24]));
    vec3 world = basis * root + vec3(p[25], p[26], p[27]);
    float slope_tan = 0.0;
    if (p[10] > .5) {
        // Cards / blades: slope of the ground under the root from the clump axes (the card shader's ground_up = cross(az, ax), oriented along its up axis).
        vec3 wx = basis * local_x, wy = basis * local_y, wz = basis * local_z;
        vec3 ground_normal = cross(wz, wx);
        if (dot(ground_normal, wy) < 0.0) ground_normal = -ground_normal;
        if (dot(ground_normal, ground_normal) > 1e-10) slope_tan = rvs_slope_tan(ground_normal);
    }
    RvsHeight height = rvs_height_sample(world, 0.0, slope_tan, distance(world, shadow.camera.xyz));
    vec2 cloud = rvs_cloud_raw(world);
    ivec2 texel = ring_vegetation_haze_texel(int(slot));
    imageStore(shadow_atlas, texel, rvs_pack0(height, float(tag)));
    imageStore(shadow_atlas, texel + ivec2(1, 0), rvs_pack1(cloud, round(shadow.state.w), height));
}

// slot = the absolute atlas slot, tag = that slot range's allocation tag.
void write_vegetation_terms_at(uint slot, uint tag, vec3 root, vec3 local_x, vec3 local_y, vec3 local_z) {
    write_vegetation_haze(slot, tag, root, local_x, local_y, local_z);
    write_vegetation_shadow(slot, tag, root, local_x, local_y, local_z);
}

void write_vegetation_terms(uint slot_row, vec3 root, vec3 local_x, vec3 local_y, vec3 local_z) {
    write_vegetation_terms_at(q.haze_base + slot_row, q.haze_tag, root, local_x, local_y, local_z);
}

float root_hash(vec2 seed, uint id) {
    return fract(sin(dot(seed, vec2(12.9898, 78.233)) + float(id) * .12345) * 43758.5453);
}

float tree_distance(uint id, uint stride) {
    vec3 root = vec3(source[id * stride + 3u], source[id * stride + 7u], source[id * stride + 11u]);
    mat3 basis = mat3(vec3(p[16], p[17], p[18]), vec3(p[19], p[20], p[21]), vec3(p[22], p[23], p[24]));
    if (p[54] > .5) return distance(metadata[id].zw, vec2(p[52], p[53]));
    return distance(basis * root + vec3(p[25], p[26], p[27]), vec3(p[0], p[1], p[2]));
}

// Card mode: GLSL twin of RingGrassCardCurve.cs (Smooth/FarDensity/Density/Keep/GrowIn/LowKeep). Parity-tested; keep the
// operation order identical. p[64..77] hold the curve inputs, see RingGpuVegetationCompute.WriteParameterWords.
float card_smooth(float a, float b, float x) { float t = clamp((x - a) / (b - a), 0.0, 1.0); return t * t * (3.0 - 2.0 * t); }
float card_density(float d) {
    float far_d = clamp(p[66] * pow(p[69] / max(d, p[69]), p[68]), p[67], p[66]);
    if (p[65] <= p[66]) return far_d;
    return far_d + (p[65] - far_d) * (1.0 - card_smooth(p[70], p[71], d));
}
bool card_keeps(float rank, float d, bool low) {
    float keep = low ? 1.0 - card_smooth(p[72], p[73], d) : min(1.0, card_density(d) / max(0.01, p[65]));
    if (!(rank < keep)) return false;
    return card_smooth(0.0, 1.0, (keep - rank) / max(0.02, 0.2 * keep)) >= p[74];
}

// Steep-view rule (trees, slots 3 and 4; TR-6). p[78] / p[79] = sin of the elevation where a card tree starts / finishes handing over to the far quad
// (p[79] <= p[78] = rule off), p[80] = role (0 none, 1 crossed cards, 2 far quad), p[81] = end of the quad's steep near cross-dither (it starts at p[3]),
// p[82] = rank gate for steep trees on the quad slot (the card gate). Returns 0..1, 1 = fully the quad. Elevation is measured from the tree's own up axis.
float tree_steepness(uint base, vec3 world) {
    if (!(p[79] > p[78])) return 0.0;
    mat3 basis = mat3(vec3(p[16], p[17], p[18]), vec3(p[19], p[20], p[21]), vec3(p[22], p[23], p[24]));
    vec3 up = basis * vec3(source[base + 1u], source[base + 5u], source[base + 9u]);
    vec3 to_camera = vec3(p[0], p[1], p[2]) - world;
    float up_length = length(up), camera_length = length(to_camera);
    if (up_length < 1e-6 || camera_length < 1e-3) return 0.0;
    return smoothstep(p[78], p[79], dot(up, to_camera) / (up_length * camera_length));
}

bool accepted(uint id) {
    bool grass = p[10] > .5;
    uint stride = grass ? 20u : (p[14] > .5 ? 16u : 12u);
    uint base = id * stride;
    vec3 root = vec3(source[base + 3u], source[base + 7u], source[base + 11u]);
    mat3 basis = mat3(vec3(p[16], p[17], p[18]), vec3(p[19], p[20], p[21]), vec3(p[22], p[23], p[24]));
    vec3 world = basis * root + vec3(p[25], p[26], p[27]);
    float distance_to_camera = distance(world, vec3(p[0], p[1], p[2]));
    if (!grass) {
        if (p[54] > .5) distance_to_camera = distance(metadata[id].zw, vec2(p[52], p[53]));
        bool protected_root = metadata[id].y > .5;
        // Physical obstacles get first claim. The distance profile may retain that
        // priority in each requested LOD, while the ordinary pass still excludes it.
        bool protected_profile = p[55] > .5;
        if (q.sweep == 0u) return protected_root && (protected_profile
            ? distance_to_camera >= p[3] && distance_to_camera < p[4] : int(p[11]) == 0);
        if (protected_root) return false;
        if (p[80] < .5) return metadata[id].x <= p[8] && distance_to_camera >= p[3] && distance_to_camera < p[4];
        if (!(distance_to_camera >= p[3] && distance_to_camera < p[4])) return false;
        float steep = tree_steepness(base, world);
        if (p[80] < 1.5) return metadata[id].x <= p[8] && steep < 1.0; // cards: fully steep trees belong to the quad
        if (metadata[id].x <= p[8] && distance_to_camera >= p[56]) return true; // the quad's own distance band
        return steep > 0.0 && metadata[id].x <= p[82] && distance_to_camera < p[57]; // steep trees in the card band
    }
    if (p[62] > .5) {
        // Conservative card accept: the card vertex shader re-derives scale with the exact camera distance. Keep, grow-in and
        // low keep never rise with distance, so testing at distance - margin yields a superset for any camera within margin.
        float custom_x = source[base + 16u];
        float kind_slot = floor(custom_x);
        float card_rank = (custom_x - kind_slot) / 0.999;
        bool low = floor(kind_slot * 0.25) > 0.5;
        float d = max(0.0, distance_to_camera - p[75]);
        if (!(d < p[64])) return false;
        if (!card_keeps(card_rank, d, low)) return false;
        vec3 wx = basis * vec3(source[base], source[base + 4u], source[base + 8u]);
        vec3 wy = basis * vec3(source[base + 1u], source[base + 5u], source[base + 9u]);
        vec3 wz = basis * vec3(source[base + 2u], source[base + 6u], source[base + 10u]);
        float card_guard = p[9] + p[76] * sqrt(dot(wx, wx) + dot(wz, wz)) + p[77] * length(wy);
        if (isnan(card_guard) || isinf(card_guard) || card_guard < 0.0) return true;
        for (int i = 0; i < int(p[13]); ++i) {
            uint a = 28u + uint(i) * 4u;
            if (dot(vec3(p[a], p[a + 1u], p[a + 2u]), world) - p[a + 3u] > card_guard) return false;
        }
        return true;
    }
    // Godot frustum normals point outwards: positive signed distance is outside.
    vec3 local_x = vec3(source[base], source[base + 4u], source[base + 8u]);
    vec3 local_y = vec3(source[base + 1u], source[base + 5u], source[base + 9u]);
    vec3 local_z = vec3(source[base + 2u], source[base + 6u], source[base + 10u]);
    float raw_height = length(basis * local_y);
    // Match the standing-grass material's MODEL_MATRIX height clamp. Keep the
    // raw extent too: oversized source geometry must still fit its sphere.
    float deformation_height = clamp(raw_height, .05, 20.0);
    // The visual envelope includes the bounded tip wind, rest lean/curve and
    // interaction bend (2.25 heights), then the widest camera-facing blade.
    // p[61] is the root-local mesh radius captured on the main thread.
    vec3 world_x = basis * local_x;
    vec3 world_z = basis * local_z;
    // Frobenius bound covers non-uniform parent scale and shear/rotated clump
    // source axes, unlike max(column length).
    float source_radius = sqrt(dot(world_x, world_x) + dot(world_z, world_z));
    // A tall source retains its raw geometry *and* can bend sideways. Applying
    // the full envelope to the larger height avoids under-bounding tall blades.
    float guard = p[9] + max(raw_height, deformation_height) * 2.25 + p[61] * source_radius * 2.0;
    // Invalid transforms must remain visible: a bad source must not make a
    // blade vanish at a frustum edge.
    if (isnan(guard) || isinf(guard) || guard < 0.0) return true;
    for (int i = 0; i < int(p[13]); ++i) {
        uint a = 28u + uint(i) * 4u;
        if (dot(vec3(p[a], p[a + 1u], p[a + 2u]), world) - p[a + 3u] > guard) return false;
    }
    if (p[12] < .5) return true;
    float half_width = p[5] * .5;
    float handoff = smoothstep(p[3] - half_width, p[3] + half_width, distance_to_camera);
    float final_fade = smoothstep(p[4] - half_width, p[4] + half_width, distance_to_camera);
    // Grouped grass retains the canonical leaf rank and leaf-local hash ID.
    bool canonical_leaf = metadata[id].z > .5;
    float rank = canonical_leaf ? metadata[id].x : (float(id) + .5) / float(q.count);
    uint hash_id = canonical_leaf ? uint(metadata[id].y) : id;
    float hash = root_hash(vec2(source[base + 16u], source[base + 17u]), hash_id);
    if (rank > mix(p[6], p[7], handoff) || hash < final_fade) return false;
    return int(p[11]) == 0 ? hash >= handoff : hash < handoff;
}

// Phase 5 (trees only): expands one compact root record into the 12 (16 with the shared-impostor variant) float source row that the
// culling phases read. Mirrors RingBiomeDecorationLayer.CreateTransform + RingLodGeometrySnapshot.CanonicalToWorld, but relative to a
// per-group canonical anchor so ring-scale (5e6 m) coordinates never enter float maths. Bindings in this phase: 0 = rows out,
// 1 = metadata (xy = rank/protected, zw = canonical around/across offset from the anchor), 2 = compact roots (altitude, yaw, height, variant),
// 4 = frame words. Frame words: [0..2] anchor in layer space, [3] sin(a0), [4] cos(a0), [5] ring radius, [6] radius at altitude 0,
// [7] row stride, [8..16] grid basis columns, [17..25] layer-inverse basis columns, [26..34] layer-inverse * grid basis columns.
float expand_sin(float x) { float x2 = x * x; return x * (1.0 - x2 * (1.0 / 6.0 - x2 * (1.0 / 120.0 - x2 / 5040.0))); }
float expand_cosm1(float x) { float x2 = x * x; return -x2 * (0.5 - x2 * (1.0 / 24.0 - x2 * (1.0 / 720.0 - x2 / 40320.0))); }
mat3 expand_mat(uint a) { return mat3(vec3(p[a], p[a + 1u], p[a + 2u]), vec3(p[a + 3u], p[a + 4u], p[a + 5u]), vec3(p[a + 6u], p[a + 7u], p[a + 8u])); }
vec2 expand_rotate(float angle) {
    // (cos, sin) of a0 + angle; angle is a few metres to kilometres over the ring radius, so the series is exact in float.
    float sn = expand_sin(angle), cm = expand_cosm1(angle);
    return vec2(p[4] * (1.0 + cm) - p[3] * sn, p[3] * (1.0 + cm) + p[4] * sn);
}
void expand_tree_row(uint id) {
    if (id >= q.count) return;
    vec4 root = vec4(output_row[id * 4u], output_row[id * 4u + 1u], output_row[id * 4u + 2u], output_row[id * 4u + 3u]);
    float inv_radius = 1.0 / p[5];
    float da = metadata[id].z * inv_radius;
    float sn = expand_sin(da), cm = expand_cosm1(da);
    float alt = root.x, r = p[6] - alt;
    vec3 delta = vec3(-alt * p[4] + r * (p[4] * cm - p[3] * sn), metadata[id].w, -alt * p[3] + r * (p[3] * cm + p[4] * sn));
    vec3 position = vec3(p[0], p[1], p[2]) + expand_mat(26u) * delta;
    mat3 grid = expand_mat(8u), inverse_layer = expand_mat(17u);
    vec2 radial = expand_rotate(da), tangent = expand_rotate(da + 1.5 * inv_radius);
    vec3 up = normalize(grid * vec3(-radial.x, 0.0, -radial.y));
    vec3 normal = normalize(grid * vec3(-tangent.y, 0.0, tangent.x));
    vec3 right = normalize(cross(up, normal));
    normal = normalize(normal * cos(root.y) + right * sin(root.y));
    right = normalize(cross(up, normal));
    vec3 x = inverse_layer * (right * root.z), y = inverse_layer * (up * root.z), z = inverse_layer * (normal * root.z);
    uint stride = uint(p[7]), b = id * stride;
    source[b] = x.x; source[b + 1u] = y.x; source[b + 2u] = z.x; source[b + 3u] = position.x;
    source[b + 4u] = x.y; source[b + 5u] = y.y; source[b + 6u] = z.y; source[b + 7u] = position.y;
    source[b + 8u] = x.z; source[b + 9u] = y.z; source[b + 10u] = z.z; source[b + 11u] = position.z;
    if (stride > 12u) source[b + 12u] = root.w;
}

// Dither words of a tree row: x = coverage (output_row[13]), y = marker (output_row[15]). Draw while lo <= noise < hi. Fade-in c in [0,1) = hi, fade-out
// c in (-1,0) = -(1 - lo), 1 = full, marker 2. A window (both lo > 0 and hi < 1, only the crossed-card slot with a steep tree at its near fade) is c = hi with
// marker 2 + 0.2 * lo; the shaders accept any marker within 0.25 of 2 and only imported_tree_impostor.gdshader decodes the offset.
vec2 tree_coverage_words(uint id, uint stride, float d) {
    float hi = 1.0, lo = 0.0;
    bool fade_in = false, fade_out = false;
    if (p[57] > p[56] && d >= p[56] && d < p[57]) { hi = smoothstep(p[56], p[57], d); fade_in = true; }
    if (p[59] > p[58] && d >= p[58] && d < p[59]) { lo = smoothstep(p[58], p[59], d); fade_out = true; }
    if (!(p[80] > .5 && p[79] > p[78])) {
        // No steep rule: the original encoding (a fade-out wins over a fade-in).
        if (fade_out) return vec2(-(1.0 - lo), 2.0);
        return vec2(hi, 2.0);
    }
    {
        mat3 basis = mat3(vec3(p[16], p[17], p[18]), vec3(p[19], p[20], p[21]), vec3(p[22], p[23], p[24]));
        vec3 world = basis * vec3(source[id * stride + 3u], source[id * stride + 7u], source[id * stride + 11u]) + vec3(p[25], p[26], p[27]);
        float steep = tree_steepness(id * stride, world);
        if (p[80] < 1.5) lo = max(lo, steep);
        else {
            float near_t = d < p[3] ? 0.0 : (d >= p[81] ? 1.0 : smoothstep(p[3], p[81], d));
            float f_steep = metadata[id].x <= p[82] ? min(steep, near_t) : 0.0;
            float f_dist = (metadata[id].x <= p[8] && d >= p[56]) ? hi : 0.0;
            hi = max(f_dist, f_steep);
        }
    }
    if (lo <= 0.0 && hi >= 1.0) return vec2(1.0, 2.0);
    if (hi <= lo) return vec2(0.0, 2.0);
    if (lo <= 0.0) return vec2(hi, 2.0);
    if (hi >= 1.0) return vec2(-(1.0 - lo), 2.0);
    return vec2(hi, 2.0 + 0.2 * lo);
}

// Share of a dithered tree row's pixels that its coverage words keep (the dither draws while lo <= noise < hi, see tree_coverage_words): 1 = all, 0 = none.
float tree_kept_share(vec2 words) {
    if (words.x < 0.0) return -words.x;
    float lo = words.y > 2.001 ? (words.y - 2.0) * 5.0 : 0.0;
    return max(words.x - lo, 0.0);
}

// Phase 6 (trees with a camera list): compacts the full output list into the camera list, in order, keeping the rows whose bounding sphere touches the
// padded camera frustum, and copies each kept row's haze / shadow terms to the camera list's slot range with that range's tag.
// Frame state (set 1, binding 8, shared by every batch): the planes (Godot: positive distance is outside) and settings = (mode, wind envelope as a fraction of the
// instance height, fixed guard in metres, plane count); mode 0 keeps every row, 1 filters, 2 filters and writes the dropped rows to the verify list.
// Per batch: p[83] > .5 = the batch has a verify list, p[110] the mesh sphere radius and p[111..113] its centre (mesh space).
// p[84] > 0 (dithered tree batches, camera list only): a row whose dissolve keeps less than that share of its pixels is left out of the camera draw. It is cut after the LOD budget
// claim, so the camera list stays a strict subset of today's (the rows are still in the full list, which only the shadows draw). Off (0): nothing is dropped.
bool faint_row(uint b) {
    if (!(p[84] > 0.0) || p[60] < .5) return false;
    return tree_kept_share(vec2(output_row[b + 13u], output_row[b + 15u])) < p[84];
}

bool camera_keeps(uint b) {
    if (cull.settings.x < .5 || q.sweep != 0u) return true; // q.sweep = 1: this batch was not in the latest frame request, its transform words may be stale
    vec3 local_x = vec3(output_row[b], output_row[b + 4u], output_row[b + 8u]);
    vec3 local_y = vec3(output_row[b + 1u], output_row[b + 5u], output_row[b + 9u]);
    vec3 local_z = vec3(output_row[b + 2u], output_row[b + 6u], output_row[b + 10u]);
    vec3 root = vec3(output_row[b + 3u], output_row[b + 7u], output_row[b + 11u]);
    mat3 basis = mat3(vec3(p[16], p[17], p[18]), vec3(p[19], p[20], p[21]), vec3(p[22], p[23], p[24]));
    float height = length(basis * local_y);
    float scale = max(length(basis * local_x), max(height, length(basis * local_z)));
    vec3 world = basis * (root + local_x * p[111] + local_y * p[112] + local_z * p[113]) + vec3(p[25], p[26], p[27]);
    // The tree vertex shaders bend a vertex by at most 0.6 of the instance height (ring_local_wind.gdshaderinc); nothing else leaves the mesh sphere.
    float radius = p[110] * scale + cull.settings.y * max(.05, height) + cull.settings.z;
    if (isnan(radius) || isinf(radius) || isnan(world.x + world.y + world.z)) return true;
    for (int i = 0; i < int(cull.settings.w); ++i)
        if (dot(cull.planes[i].xyz, world) - cull.planes[i].w > radius) return false;
    return true;
}

// Phase 7 (tree sun-shadow mirrors): the shadow-only copy of the full list keeps the rows whose swept bounding sphere can reach what the sun map is for. A caster matters when the
// ray from its centre along the light's travel direction (the shadow it throws) can enter the padded camera frustum cut off at the shadow distance (shadow_planes; Godot: positive
// signed distance is outside). The ray never enters when the sphere is wholly outside one plane and the ray does not approach that plane (n . d >= 0.02: the margin covers the
// light's drift between re-latches). One ray per shadow-casting light (shadow_dirs, w > .5 = enabled; shadow_settings.x 0 = keep every row). Per batch: push haze_anchor = the mirror
// mesh's sphere (xyz centre in instance space, w radius); q.sweep = 1: the transform words may be stale, keep every row.
bool shadow_keeps(uint b) {
    if (cull.shadow_settings.x < .5 || q.sweep != 0u) return true;
    vec3 local_x = vec3(output_row[b], output_row[b + 4u], output_row[b + 8u]);
    vec3 local_y = vec3(output_row[b + 1u], output_row[b + 5u], output_row[b + 9u]);
    vec3 local_z = vec3(output_row[b + 2u], output_row[b + 6u], output_row[b + 10u]);
    vec3 root = vec3(output_row[b + 3u], output_row[b + 7u], output_row[b + 11u]);
    mat3 basis = mat3(vec3(p[16], p[17], p[18]), vec3(p[19], p[20], p[21]), vec3(p[22], p[23], p[24]));
    float height = length(basis * local_y);
    float scale = max(length(basis * local_x), max(height, length(basis * local_z)));
    vec3 world = basis * (root + local_x * q.haze_anchor.x + local_y * q.haze_anchor.y + local_z * q.haze_anchor.z) + vec3(p[25], p[26], p[27]);
    float radius = q.haze_anchor.w * scale + cull.settings.y * max(.05, height) + cull.settings.z;
    if (isnan(radius) || isinf(radius) || isnan(world.x + world.y + world.z)) return true;
    for (int light = 0; light < 2; ++light) {
        if (cull.shadow_dirs[light].w < .5) continue;
        bool never_enters = false;
        for (int i = 0; i < 6 && !never_enters; ++i)
            never_enters = dot(cull.shadow_planes[i].xyz, world) - cull.shadow_planes[i].w > radius && dot(cull.shadow_planes[i].xyz, cull.shadow_dirs[light].xyz) >= 0.02;
        if (!never_enters) return true;
    }
    return false;
}

// One workgroup per batch: compacts the full list into the mirror's rows (bindings 9 / 10 of the mirror's own set), in order.
void mirror_phase(uint lane) {
    uint visible = min(command[1], q.capacity);
    uint stride = p[60] > .5 || p[14] > .5 ? 16u : 12u;
    uint kept = 0u;
    for (uint start = 0u; start < visible; start += 64u) {
        uint id = start + lane;
        bool live = id < visible;
        bool keep = live && shadow_keeps(id * stride);
        scan[lane] = keep ? 1u : 0u;
        barrier();
        for (uint step = 1u; step < 64u; step <<= 1u) {
            uint previous = lane >= step ? scan[lane - step] : 0u;
            barrier();
            scan[lane] += previous;
            barrier();
        }
        uint kept_here = scan[63u];
        if (keep) {
            uint destination = kept + scan[lane] - 1u;
            for (uint element = 0u; element < stride; ++element)
                camera_row[destination * stride + element] = output_row[id * stride + element];
        }
        kept += kept_here;
        barrier();
    }
    if (lane == 0u)
        for (uint surface = 0u; surface < q.surfaces; ++surface)
            camera_command[surface * 5u + 1u] = kept;
}

void copy_camera_terms(uint source_slot, uint destination_slot) {
    ivec2 from = ring_vegetation_haze_texel(int(source_slot)), to = ring_vegetation_haze_texel(int(destination_slot));
    if (VEGETATION_HAZE && q.haze_anchor.w > .5) {
        vec4 first = imageLoad(haze_atlas, from);
        first.w = float(q.haze_tag);
        imageStore(haze_atlas, to, first);
        imageStore(haze_atlas, to + ivec2(1, 0), imageLoad(haze_atlas, from + ivec2(1, 0)));
    }
    if (VEGETATION_SHADOW && q.shadow_flags.x > .5) {
        vec4 first = imageLoad(shadow_atlas, from);
        first.w = float(q.haze_tag);
        imageStore(shadow_atlas, to, first);
        imageStore(shadow_atlas, to + ivec2(1, 0), imageLoad(shadow_atlas, from + ivec2(1, 0)));
    }
}

// One workgroup per batch: 64 rows per step, a running offset keeps the survivors in their original order.
void camera_phase(uint lane) {
    uint visible = min(command[1], q.capacity);
    uint stride = p[60] > .5 || p[14] > .5 ? 16u : 12u;
    uint source_slots = uint(q.shadow_flags.y + .5);
    bool verify = cull.settings.x > 1.5 && p[83] > .5;
    uint kept = 0u, dropped = 0u;
    for (uint start = 0u; start < visible; start += 64u) {
        uint id = start + lane;
        bool live = id < visible;
        bool in_view = live && camera_keeps(id * stride);
        bool keep = in_view && !(cull.settings.x > .5 && q.sweep == 0u && faint_row(id * stride)); // a faint row is not a frustum miss: it never goes to the verify list
        scan[lane] = keep ? 1u : 0u;
        scan_dropped[lane] = verify && live && !in_view ? 1u : 0u;
        barrier();
        for (uint step = 1u; step < 64u; step <<= 1u) {
            uint previous = lane >= step ? scan[lane - step] : 0u;
            uint previous_dropped = lane >= step ? scan_dropped[lane - step] : 0u;
            barrier();
            scan[lane] += previous;
            scan_dropped[lane] += previous_dropped;
            barrier();
        }
        uint kept_here = scan[63u], dropped_here = scan_dropped[63u];
        // The map a terms refresh (phase 4) uses to rewrite the camera slots without cutting the list again: camera position + 1 of full row id, 0 = not in the camera list.
        // The flags buffer (the compaction's scratch) is free by now: phase 0 / 3 write every entry again before they read any.
        if (live) local_offset[id] = keep ? kept + scan[lane] : 0u;
        if (keep) {
            uint destination = kept + scan[lane] - 1u;
            for (uint element = 0u; element < stride; ++element)
                camera_row[destination * stride + element] = output_row[id * stride + element];
            copy_camera_terms(source_slots + id, q.haze_base + destination);
        } else if (verify && live && !in_view) {
            uint destination = dropped + scan_dropped[lane] - 1u;
            for (uint element = 0u; element < stride; ++element)
                verify_row[destination * stride + element] = output_row[id * stride + element];
        }
        kept += kept_here;
        dropped += dropped_here;
        barrier();
    }
    if (lane == 0u)
        for (uint surface = 0u; surface < q.surfaces; ++surface) {
            camera_command[surface * 5u + 1u] = kept;
            if (p[83] > .5) verify_command[surface * 5u + 1u] = dropped; // a verify list that is not being filled must draw nothing
        }
}

void main() {
    uint id = gl_GlobalInvocationID.x;
    uint lane = gl_LocalInvocationID.x;
    uint group = gl_WorkGroupID.x;
    uint groups = (q.count + 63u) / 64u;
    if (q.phase == 5u) { expand_tree_row(id); return; }
    if (q.phase == 6u) { camera_phase(lane); return; }
    if (q.phase == 7u) { mirror_phase(lane); return; }
    if (q.phase == 4u) {
        if (!VEGETATION_HAZE && !VEGETATION_SHADOW) return;
        uint visible = min(command[1], q.capacity);
        if (id >= visible) return;
        uint input_stride = p[10] > .5 ? 20u : (p[14] > .5 ? 16u : 12u);
        uint output_stride = p[10] > .5 ? 20u : (p[60] > .5 ? 16u : input_stride);
        uint b = id * output_stride;
        vec3 root = vec3(output_row[b + 3u], output_row[b + 7u], output_row[b + 11u]);
        vec3 axis_x = vec3(output_row[b], output_row[b + 4u], output_row[b + 8u]), axis_y = vec3(output_row[b + 1u], output_row[b + 5u], output_row[b + 9u]);
        vec3 axis_z = vec3(output_row[b + 2u], output_row[b + 6u], output_row[b + 10u]);
        write_vegetation_terms(id, root, axis_x, axis_y, axis_z);
        // Tree batches with a camera list (q.sweep - 1 = the camera slot range's base, q.protected_base_valid = its tag): the rows kept by the last phase 6 get the same terms in their
        // camera slots, so the camera list is not cut again for a terms-only refresh (the map is phase 6's, see camera_phase).
        if (q.sweep != 0u) {
            uint camera_position = local_offset[id];
            if (camera_position != 0u) write_vegetation_terms_at(q.sweep - 1u + camera_position - 1u, q.protected_base_valid, root, axis_x, axis_y, axis_z);
        }
        return;
    }
    if (q.phase == 3u) {
        // Small tree batches have one workgroup, so they can scan, claim the
        // shared budget, and compact without the inter-dispatch round trips.
        uint visible = 0u;
        if (id < q.count) visible = accepted(id) ? 1u : 0u;
        scan[lane] = visible;
        barrier();
        for (uint step = 1u; step < 64u; step <<= 1u) {
            uint previous = lane >= step ? scan[lane - step] : 0u;
            barrier();
            scan[lane] += previous;
            barrier();
        }
        if (id < q.count) local_offset[id] = visible == 1u ? scan[lane] : 0u;
        if (lane == 0u) {
            uint total = scan[63u];
            bool grass = p[10] > .5;
            uint base = !grass && q.sweep == 1u && q.protected_base_valid != 0u ? command[1] : 0u;
            uint take = min(total, q.capacity - min(base, q.capacity));
            if (!grass) {
                uint slot = uint(p[11]);
                uint limit = uint(p[15]);
                take = min(take, limit - min(limit, used[slot]));
                used[slot] += take;
            }
            group_offset[0] = take;
            group_offset[1] = base;
            for (uint surface = 0u; surface < q.surfaces; ++surface)
                command[surface * 5u + 1u] = base + take;
        }
        memoryBarrierBuffer();
        barrier();
        if (id >= q.count || local_offset[id] == 0u) return;
        uint destination = local_offset[id] - 1u;
        if (destination >= group_offset[0]) return;
        destination += group_offset[1];
        uint input_stride = p[10] > .5 ? 20u : (p[14] > .5 ? 16u : 12u);
        uint output_stride = p[10] > .5 ? 20u : (p[60] > .5 ? 16u : input_stride);
        for (uint element = 0u; element < input_stride; ++element)
            output_row[destination * output_stride + element] = source[id * input_stride + element];
        write_vegetation_terms(destination, vec3(source[id * input_stride + 3u], source[id * input_stride + 7u], source[id * input_stride + 11u]),
            vec3(source[id * input_stride], source[id * input_stride + 4u], source[id * input_stride + 8u]),
            vec3(source[id * input_stride + 1u], source[id * input_stride + 5u], source[id * input_stride + 9u]),
            vec3(source[id * input_stride + 2u], source[id * input_stride + 6u], source[id * input_stride + 10u]));
        if (p[10] < .5 && p[60] > .5) {
            output_row[destination * output_stride + 12u] = p[14] > .5 ? source[id * input_stride + 12u] : 0.0;
            float distance_to_camera = tree_distance(id, input_stride);
            vec2 dither_words = tree_coverage_words(id, input_stride, distance_to_camera);
            output_row[destination * output_stride + 13u] = dither_words.x;
            output_row[destination * output_stride + 14u] = metadata[id].x;
            output_row[destination * output_stride + 15u] = dither_words.y;
        }
        return;
    }
    if (q.phase == 0u) {
        uint visible = 0u;
        if (id < q.count) visible = accepted(id) ? 1u : 0u;
        scan[lane] = visible;
        barrier();
        for (uint step = 1u; step < 64u; step <<= 1u) {
            uint previous = lane >= step ? scan[lane - step] : 0u;
            barrier();
            scan[lane] += previous;
            barrier();
        }
        // Zero means rejected; accepted roots use a one-based local offset.
        if (id < q.count) local_offset[id] = visible == 1u ? scan[lane] : 0u;
        if (lane == 63u) group_count[group] = scan[lane];
        return;
    }
    if (q.phase == 1u) {
        if (lane != 0u) return;
        uint total = 0u;
        for (uint i = 0u; i < groups; ++i) {
            group_offset[i] = total;
            total += group_count[i];
        }
        bool grass = p[10] > .5;
        // The CPU skips an empty/non-LOD0 protected sweep. Its prior command
        // count must never become the ordinary-pass base on a later frame.
        uint base = !grass && q.sweep == 1u && q.protected_base_valid != 0u ? command[1] : 0u;
        uint take = min(total, q.capacity - min(base, q.capacity));
        if (!grass) {
            uint slot = uint(p[11]);
            uint limit = uint(p[15]);
            take = min(take, limit - min(limit, used[slot]));
            used[slot] += take;
        }
        group_offset[groups] = take;
        group_offset[groups + 1u] = base;
        // Five words per mesh surface. Geometry arguments belong to Godot.
        for (uint surface = 0u; surface < q.surfaces; ++surface)
            command[surface * 5u + 1u] = base + take;
        return;
    }
    if (id >= q.count || local_offset[id] == 0u) return;
    uint destination = group_offset[group] + local_offset[id] - 1u;
    if (destination >= group_offset[groups]) return;
    destination += group_offset[groups + 1u];
    uint input_stride = p[10] > .5 ? 20u : (p[14] > .5 ? 16u : 12u);
    uint output_stride = p[10] > .5 ? 20u : (p[60] > .5 ? 16u : input_stride);
    for (uint element = 0u; element < input_stride; ++element)
        output_row[destination * output_stride + element] = source[id * input_stride + element];
    write_vegetation_terms(destination, vec3(source[id * input_stride + 3u], source[id * input_stride + 7u], source[id * input_stride + 11u]),
        vec3(source[id * input_stride], source[id * input_stride + 4u], source[id * input_stride + 8u]),
        vec3(source[id * input_stride + 1u], source[id * input_stride + 5u], source[id * input_stride + 9u]),
        vec3(source[id * input_stride + 2u], source[id * input_stride + 6u], source[id * input_stride + 10u]));
    if (p[10] < .5 && p[60] > .5) {
        output_row[destination * output_stride + 12u] = p[14] > .5 ? source[id * input_stride + 12u] : 0.0;
        float distance_to_camera = tree_distance(id, input_stride);
        vec2 dither_words = tree_coverage_words(id, input_stride, distance_to_camera);
        output_row[destination * output_stride + 13u] = dither_words.x;
        output_row[destination * output_stride + 14u] = metadata[id].x;
        output_row[destination * output_stride + 15u] = dither_words.y;
    }
}
