#[compute]
#version 450

layout(local_size_x = 64) in;
layout(set = 0, binding = 0, std430) readonly buffer Sources { float source[]; };
layout(set = 0, binding = 1, std430) readonly buffer Metadata { vec4 metadata[]; };
layout(set = 0, binding = 2, std430) writeonly buffer OutputRows { float output_row[]; };
layout(set = 0, binding = 3, std430) buffer Commands { uint command[]; };
layout(set = 0, binding = 4, std430) readonly buffer Parameters { float p[]; };
layout(set = 0, binding = 5, std430) buffer Flags { uint local_offset[]; };
layout(set = 0, binding = 6, std430) buffer Counts { uint group_count[]; };
layout(set = 0, binding = 7, std430) buffer Offsets { uint group_offset[]; };
layout(set = 0, binding = 8, std430) buffer Budgets { uint used[5]; };
layout(push_constant, std430) uniform Push {
    uint phase, count, capacity, surfaces;
    uint sweep, protected_base_valid, unused1, unused2;
} q;
shared uint scan[64];

float root_hash(vec2 seed, uint id) {
    return fract(sin(dot(seed, vec2(12.9898, 78.233)) + float(id) * .12345) * 43758.5453);
}

float tree_distance(uint id, uint stride) {
    vec3 root = vec3(source[id * stride + 3u], source[id * stride + 7u], source[id * stride + 11u]);
    mat3 basis = mat3(vec3(p[16], p[17], p[18]), vec3(p[19], p[20], p[21]), vec3(p[22], p[23], p[24]));
    if (p[54] > .5) return distance(metadata[id].zw, vec2(p[52], p[53]));
    return distance(basis * root + vec3(p[25], p[26], p[27]), vec3(p[0], p[1], p[2]));
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
