#[compute]
#version 450
layout(constant_id = 0) const bool VEGETATION_HAZE = false;

layout(local_size_x = 64) in;
layout(set = 0, binding = 0, std430) readonly buffer Sources { float source[]; };
layout(set = 0, binding = 1, std430) readonly buffer Metadata { vec4 metadata[]; };
layout(set = 0, binding = 2, std430) buffer OutputRows { float output_row[]; };
layout(set = 0, binding = 3, std430) buffer Commands { uint command[]; };
layout(set = 0, binding = 4, std430) readonly buffer Parameters { float p[]; };
layout(set = 0, binding = 5, std430) buffer Flags { uint local_offset[]; };
layout(set = 0, binding = 6, std430) buffer Counts { uint group_count[]; };
layout(set = 0, binding = 7, std430) buffer Offsets { uint group_offset[]; };
layout(set = 0, binding = 8, std430) buffer Budgets { uint used[5]; };
layout(push_constant, std430) uniform Push {
    uint phase, count, capacity, surfaces;
    uint sweep, protected_base_valid, haze_base, haze_tag;
    vec4 haze_anchor; // xyz = anchor in mesh-local row space (trees: mesh AABB centre; grass / shared impostor: 0), w > .5 = write haze
} q;
layout(set = 1, binding = 0, std140) uniform HazeParams {
    vec4 sigma_R_H_R; vec4 aerosol; vec4 mist; vec4 geometry; vec4 sides; vec4 outward; vec4 axis; vec4 to_sun; vec4 camera; vec4 storm;
} haze;
layout(rgba16f, set = 1, binding = 1) uniform restrict writeonly image2D haze_atlas;
#include "res://materials/ring_atmosphere_shared.gdshaderinc"
#include "res://materials/ring_haze_shared.gdshaderinc"
#include "res://materials/atmosphere/ring_vegetation_haze_terms.gdshaderinc"
shared uint scan[64];

void write_vegetation_haze(uint slot_row, vec3 root, vec3 local_x, vec3 local_y, vec3 local_z) {
    if (!VEGETATION_HAZE || q.haze_anchor.w < .5) return;
    vec3 local_anchor = root + local_x * q.haze_anchor.x + local_y * q.haze_anchor.y + local_z * q.haze_anchor.z;
    mat3 basis = mat3(vec3(p[16], p[17], p[18]), vec3(p[19], p[20], p[21]), vec3(p[22], p[23], p[24]));
    vec3 world = basis * local_anchor + vec3(p[25], p[26], p[27]);
    RingHazeParams hp = ring_haze_params_default();
    hp.sigma_R = haze.sigma_R_H_R.xyz; hp.H_R = haze.sigma_R_H_R.w;
    hp.sigma_A = haze.aerosol.x; hp.omega_A = haze.aerosol.y; hp.H_A = haze.aerosol.z; hp.sigma_M0 = haze.aerosol.w;
    hp.omega_M = haze.mist.x; hp.H_mist = haze.mist.y; hp.cap_height = haze.mist.z; hp.g = haze.mist.w;
    hp.quality = int(haze.geometry.w + .5); hp.to_sun = normalize(haze.to_sun.xyz);
    hp = ring_haze_apply_storm(hp, haze.storm);
    RingVegetationHazeTerms t = ring_vegetation_haze_terms(hp, world, haze.camera.xyz, haze.geometry.z, haze.outward.xyz, haze.axis.xyz,
        haze.sides.x, haze.sides.y, haze.sides.z, haze.geometry.x, haze.geometry.y);
    ivec2 texel = ring_vegetation_haze_texel(int(q.haze_base + slot_row));
    imageStore(haze_atlas, texel, ring_vegetation_haze_pack0(t, float(q.haze_tag)));
    imageStore(haze_atlas, texel + ivec2(1, 0), ring_vegetation_haze_pack1(t));
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
        if (protected_root || metadata[id].x > p[8]) return false;
        return distance_to_camera >= p[3] && distance_to_camera < p[4];
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

void main() {
    uint id = gl_GlobalInvocationID.x;
    uint lane = gl_LocalInvocationID.x;
    uint group = gl_WorkGroupID.x;
    uint groups = (q.count + 63u) / 64u;
    if (q.phase == 4u) {
        if (!VEGETATION_HAZE) return;
        uint visible = min(command[1], q.capacity);
        if (id >= visible) return;
        uint input_stride = p[10] > .5 ? 20u : (p[14] > .5 ? 16u : 12u);
        uint output_stride = p[10] > .5 ? 20u : (p[60] > .5 ? 16u : input_stride);
        uint b = id * output_stride;
        write_vegetation_haze(id, vec3(output_row[b + 3u], output_row[b + 7u], output_row[b + 11u]),
            vec3(output_row[b], output_row[b + 4u], output_row[b + 8u]), vec3(output_row[b + 1u], output_row[b + 5u], output_row[b + 9u]),
            vec3(output_row[b + 2u], output_row[b + 6u], output_row[b + 10u]));
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
        write_vegetation_haze(destination, vec3(source[id * input_stride + 3u], source[id * input_stride + 7u], source[id * input_stride + 11u]),
            vec3(source[id * input_stride], source[id * input_stride + 4u], source[id * input_stride + 8u]),
            vec3(source[id * input_stride + 1u], source[id * input_stride + 5u], source[id * input_stride + 9u]),
            vec3(source[id * input_stride + 2u], source[id * input_stride + 6u], source[id * input_stride + 10u]));
        if (p[10] < .5 && p[60] > .5) {
            output_row[destination * output_stride + 12u] = p[14] > .5 ? source[id * input_stride + 12u] : 0.0;
            float distance_to_camera = tree_distance(id, input_stride);
            float coverage = 1.0;
            if (p[57] > p[56] && distance_to_camera >= p[56] && distance_to_camera < p[57]) coverage = smoothstep(p[56], p[57], distance_to_camera);
            if (p[59] > p[58] && distance_to_camera >= p[58] && distance_to_camera < p[59]) coverage = -(1.0 - smoothstep(p[58], p[59], distance_to_camera));
            output_row[destination * output_stride + 13u] = coverage;
            output_row[destination * output_stride + 14u] = metadata[id].x;
            output_row[destination * output_stride + 15u] = 2.0;
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
    write_vegetation_haze(destination, vec3(source[id * input_stride + 3u], source[id * input_stride + 7u], source[id * input_stride + 11u]),
        vec3(source[id * input_stride], source[id * input_stride + 4u], source[id * input_stride + 8u]),
        vec3(source[id * input_stride + 1u], source[id * input_stride + 5u], source[id * input_stride + 9u]),
        vec3(source[id * input_stride + 2u], source[id * input_stride + 6u], source[id * input_stride + 10u]));
    if (p[10] < .5 && p[60] > .5) {
        output_row[destination * output_stride + 12u] = p[14] > .5 ? source[id * input_stride + 12u] : 0.0;
        float distance_to_camera = tree_distance(id, input_stride);
        float coverage = 1.0;
        if (p[57] > p[56] && distance_to_camera >= p[56] && distance_to_camera < p[57]) coverage = smoothstep(p[56], p[57], distance_to_camera);
        if (p[59] > p[58] && distance_to_camera >= p[58] && distance_to_camera < p[59]) coverage = -(1.0 - smoothstep(p[58], p[59], distance_to_camera));
        output_row[destination * output_stride + 13u] = coverage;
        output_row[destination * output_stride + 14u] = metadata[id].x;
        output_row[destination * output_stride + 15u] = 2.0;
    }
}
