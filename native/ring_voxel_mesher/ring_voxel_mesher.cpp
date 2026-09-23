#include <algorithm>
#include <array>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <new>
#include <unordered_map>
#include <vector>

#include "third_party/transvoxel/transvoxel_tables_upstream.cpp"

#if defined(_WIN32)
#define RING_EXPORT extern "C" __declspec(dllexport)
#else
#define RING_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace {

constexpr int kChunkX = 32;
constexpr int kChunkY = 32;
constexpr int kChunkZ = 32;
constexpr int kChunkVolume = kChunkX * kChunkY * kChunkZ;
constexpr std::uint8_t kAir = 0;
// Transvoxel requires one sample before and two samples after a block.
constexpr int kSmoothHaloSize = 35;
constexpr int kSmoothCellCount = 33;
constexpr int kCoarseSampleCount = 33;
constexpr std::size_t kMaximumRetainedVertices = 131072;

enum class FaceDirection : std::uint8_t {
    positive_x,
    negative_x,
    positive_y,
    negative_y,
    positive_z,
    negative_z,
};

struct Workspace {
    std::vector<float> positions;
    std::vector<float> normals;
    std::vector<std::uint8_t> block_types;
    std::vector<std::int32_t> indices;
    std::array<std::uint8_t, 1024> mask{};
    std::unordered_map<std::uint32_t, std::int32_t> smooth_vertex_lookup;

    Workspace() {
        positions.reserve(2048 * 3);
        normals.reserve(2048 * 3);
        block_types.reserve(2048);
        indices.reserve(3072);
    }

    void reset() {
        positions.clear();
        normals.clear();
        block_types.clear();
        indices.clear();
        smooth_vertex_lookup.clear();
    }

    void trim_oversized() {
        if (block_types.capacity() <= kMaximumRetainedVertices) {
            return;
        }
        Workspace compact;
        positions.swap(compact.positions);
        normals.swap(compact.normals);
        block_types.swap(compact.block_types);
        indices.swap(compact.indices);
        smooth_vertex_lookup.clear();
    }
};

thread_local Workspace g_workspace;

struct NativeMeshResult {
    float *positions;
    float *normals;
    std::uint8_t *block_types;
    std::int32_t *indices;
    std::int32_t vertex_count;
    std::int32_t index_count;
};

constexpr int linear_index(const int x, const int y, const int z) {
    return x + kChunkX * (y + kChunkY * z);
}

constexpr std::uint8_t visible_block(
    const std::uint8_t current,
    const std::uint8_t neighbor) {
    return current != kAir && neighbor == kAir ? current : kAir;
}

constexpr bool is_natural(const std::uint8_t block) {
    return block >= 1 && block <= 3;
}

constexpr int smooth_linear_index(const int x, const int y, const int z) {
    return x + kSmoothHaloSize * (y + kSmoothHaloSize * z);
}

constexpr int coarse_linear_index(const int x, const int y, const int z) {
    return x + kCoarseSampleCount * (y + kCoarseSampleCount * z);
}

void add_vertex(
    Workspace &workspace,
    const float x,
    const float y,
    const float z,
    const float nx,
    const float ny,
    const float nz,
    const std::uint8_t block_type) {
    workspace.positions.push_back(x);
    workspace.positions.push_back(y);
    workspace.positions.push_back(z);
    workspace.normals.push_back(nx);
    workspace.normals.push_back(ny);
    workspace.normals.push_back(nz);
    workspace.block_types.push_back(block_type);
}

void emit_face(
    Workspace &workspace,
    const FaceDirection direction,
    const int layer,
    const int a,
    const int b,
    const int width,
    const int height,
    const std::uint8_t block_type) {
    float ox = 0.0f;
    float oy = 0.0f;
    float oz = 0.0f;
    float ux = 0.0f;
    float uy = 0.0f;
    float uz = 0.0f;
    float vx = 0.0f;
    float vy = 0.0f;
    float vz = 0.0f;
    float nx = 0.0f;
    float ny = 0.0f;
    float nz = 0.0f;

    switch (direction) {
        case FaceDirection::positive_x:
            ox = static_cast<float>(layer + 1);
            oy = static_cast<float>(b);
            oz = static_cast<float>(a);
            uz = static_cast<float>(width);
            vy = static_cast<float>(height);
            nx = 1.0f;
            break;
        case FaceDirection::negative_x:
            ox = static_cast<float>(layer);
            oy = static_cast<float>(b);
            oz = static_cast<float>(a + width);
            uz = static_cast<float>(-width);
            vy = static_cast<float>(height);
            nx = -1.0f;
            break;
        case FaceDirection::positive_y:
            ox = static_cast<float>(a);
            oy = static_cast<float>(layer + 1);
            oz = static_cast<float>(b);
            ux = static_cast<float>(width);
            vz = static_cast<float>(height);
            ny = 1.0f;
            break;
        case FaceDirection::negative_y:
            ox = static_cast<float>(a);
            oy = static_cast<float>(layer);
            oz = static_cast<float>(b + height);
            ux = static_cast<float>(width);
            vz = static_cast<float>(-height);
            ny = -1.0f;
            break;
        case FaceDirection::positive_z:
            ox = static_cast<float>(a + width);
            oy = static_cast<float>(b);
            oz = static_cast<float>(layer + 1);
            ux = static_cast<float>(-width);
            vy = static_cast<float>(height);
            nz = 1.0f;
            break;
        case FaceDirection::negative_z:
            ox = static_cast<float>(a);
            oy = static_cast<float>(b);
            oz = static_cast<float>(layer);
            ux = static_cast<float>(width);
            vy = static_cast<float>(height);
            nz = -1.0f;
            break;
    }

    const auto start = static_cast<std::int32_t>(workspace.block_types.size());
    add_vertex(workspace, ox, oy, oz, nx, ny, nz, block_type);
    add_vertex(workspace, ox + ux, oy + uy, oz + uz, nx, ny, nz, block_type);
    add_vertex(
        workspace,
        ox + ux + vx,
        oy + uy + vy,
        oz + uz + vz,
        nx,
        ny,
        nz,
        block_type);
    add_vertex(workspace, ox + vx, oy + vy, oz + vz, nx, ny, nz, block_type);
    workspace.indices.push_back(start);
    workspace.indices.push_back(start + 1);
    workspace.indices.push_back(start + 2);
    workspace.indices.push_back(start);
    workspace.indices.push_back(start + 2);
    workspace.indices.push_back(start + 3);
}

void emit_mask(
    Workspace &workspace,
    const FaceDirection direction,
    const int layer,
    const int dim_a,
    const int dim_b,
    const bool greedy) {
    auto &mask = workspace.mask;
    if (!greedy) {
        for (int b = 0; b < dim_b; ++b) {
            for (int a = 0; a < dim_a; ++a) {
                const std::uint8_t value = mask[a + b * dim_a];
                if (value != kAir) {
                    emit_face(workspace, direction, layer, a, b, 1, 1, value);
                }
            }
        }
        return;
    }

    for (int b = 0; b < dim_b; ++b) {
        for (int a = 0; a < dim_a;) {
            const int mask_start = a + b * dim_a;
            const std::uint8_t value = mask[mask_start];
            if (value == kAir) {
                ++a;
                continue;
            }

            int width = 1;
            while (a + width < dim_a && mask[mask_start + width] == value) {
                ++width;
            }

            int height = 1;
            bool blocked = false;
            while (b + height < dim_b && !blocked) {
                const int row_start = a + (b + height) * dim_a;
                for (int k = 0; k < width; ++k) {
                    if (mask[row_start + k] != value) {
                        blocked = true;
                        break;
                    }
                }
                if (!blocked) {
                    ++height;
                }
            }

            emit_face(workspace, direction, layer, a, b, width, height, value);
            for (int dy = 0; dy < height; ++dy) {
                std::fill_n(mask.begin() + a + (b + dy) * dim_a, width, kAir);
            }
            a += width;
        }
    }
}

void build_positive_x(
    Workspace &workspace,
    const std::uint8_t *blocks,
    const std::uint8_t *positive_x,
    const bool greedy) {
    for (int x = 0; x < kChunkX; ++x) {
        int mask_index = 0;
        for (int y = 0; y < kChunkY; ++y) {
            for (int z = 0; z < kChunkZ; ++z) {
                const int index = linear_index(x, y, z);
                const std::uint8_t neighbor = x + 1 < kChunkX
                    ? blocks[index + 1]
                    : positive_x[y + kChunkY * z];
                workspace.mask[mask_index++] = visible_block(blocks[index], neighbor);
            }
        }
        emit_mask(workspace, FaceDirection::positive_x, x, kChunkZ, kChunkY, greedy);
    }
}

void build_negative_x(
    Workspace &workspace,
    const std::uint8_t *blocks,
    const std::uint8_t *negative_x,
    const bool greedy) {
    for (int x = 0; x < kChunkX; ++x) {
        int mask_index = 0;
        for (int y = 0; y < kChunkY; ++y) {
            for (int z = 0; z < kChunkZ; ++z) {
                const int index = linear_index(x, y, z);
                const std::uint8_t neighbor = x > 0
                    ? blocks[index - 1]
                    : negative_x[y + kChunkY * z];
                workspace.mask[mask_index++] = visible_block(blocks[index], neighbor);
            }
        }
        emit_mask(workspace, FaceDirection::negative_x, x, kChunkZ, kChunkY, greedy);
    }
}

void build_positive_y(
    Workspace &workspace,
    const std::uint8_t *blocks,
    const std::uint8_t *positive_y,
    const bool greedy) {
    for (int y = 0; y < kChunkY; ++y) {
        int mask_index = 0;
        for (int z = 0; z < kChunkZ; ++z) {
            for (int x = 0; x < kChunkX; ++x) {
                const int index = linear_index(x, y, z);
                const std::uint8_t neighbor = y + 1 < kChunkY
                    ? blocks[index + kChunkX]
                    : positive_y[x + kChunkX * z];
                workspace.mask[mask_index++] = visible_block(blocks[index], neighbor);
            }
        }
        emit_mask(workspace, FaceDirection::positive_y, y, kChunkX, kChunkZ, greedy);
    }
}

void build_negative_y(
    Workspace &workspace,
    const std::uint8_t *blocks,
    const std::uint8_t *negative_y,
    const bool greedy) {
    for (int y = 0; y < kChunkY; ++y) {
        int mask_index = 0;
        for (int z = 0; z < kChunkZ; ++z) {
            for (int x = 0; x < kChunkX; ++x) {
                const int index = linear_index(x, y, z);
                const std::uint8_t neighbor = y > 0
                    ? blocks[index - kChunkX]
                    : negative_y[x + kChunkX * z];
                workspace.mask[mask_index++] = visible_block(blocks[index], neighbor);
            }
        }
        emit_mask(workspace, FaceDirection::negative_y, y, kChunkX, kChunkZ, greedy);
    }
}

void build_positive_z(
    Workspace &workspace,
    const std::uint8_t *blocks,
    const std::uint8_t *positive_z,
    const bool greedy) {
    constexpr int z_stride = kChunkX * kChunkY;
    for (int z = 0; z < kChunkZ; ++z) {
        int mask_index = 0;
        for (int y = 0; y < kChunkY; ++y) {
            for (int x = 0; x < kChunkX; ++x) {
                const int index = linear_index(x, y, z);
                const std::uint8_t neighbor = z + 1 < kChunkZ
                    ? blocks[index + z_stride]
                    : positive_z[x + kChunkX * y];
                workspace.mask[mask_index++] = visible_block(blocks[index], neighbor);
            }
        }
        emit_mask(workspace, FaceDirection::positive_z, z, kChunkX, kChunkY, greedy);
    }
}

void build_negative_z(
    Workspace &workspace,
    const std::uint8_t *blocks,
    const std::uint8_t *negative_z,
    const bool greedy) {
    constexpr int z_stride = kChunkX * kChunkY;
    for (int z = 0; z < kChunkZ; ++z) {
        int mask_index = 0;
        for (int y = 0; y < kChunkY; ++y) {
            for (int x = 0; x < kChunkX; ++x) {
                const int index = linear_index(x, y, z);
                const std::uint8_t neighbor = z > 0
                    ? blocks[index - z_stride]
                    : negative_z[x + kChunkX * y];
                workspace.mask[mask_index++] = visible_block(blocks[index], neighbor);
            }
        }
        emit_mask(workspace, FaceDirection::negative_z, z, kChunkX, kChunkY, greedy);
    }
}

struct SmoothSample {
    float x;
    float y;
    float z;
    float density;
    std::uint8_t material;
};

constexpr std::array<std::array<int, 3>, 8> kCubeCorners = {{
    {{0, 0, 0}}, {{1, 0, 0}}, {{1, 1, 0}}, {{0, 1, 0}},
    {{0, 0, 1}}, {{1, 0, 1}}, {{1, 1, 1}}, {{0, 1, 1}},
}};
constexpr std::array<std::array<int, 4>, 6> kCubeTetrahedra = {{
    {{0, 5, 1, 6}}, {{0, 1, 2, 6}}, {{0, 2, 3, 6}},
    {{0, 3, 7, 6}}, {{0, 7, 4, 6}}, {{0, 4, 5, 6}},
}};

SmoothSample interpolate_smooth(const SmoothSample &a, const SmoothSample &b) {
    const float denominator = a.density - b.density;
    const float weight = denominator != 0.0f ? a.density / denominator : 0.5f;
    return {
        a.x + (b.x - a.x) * weight,
        a.y + (b.y - a.y) * weight,
        a.z + (b.z - a.z) * weight,
        0.0f,
        a.density < 0.0f ? a.material : b.material,
    };
}

float smooth_density_at(
    const std::uint8_t *halo_blocks,
    const int x,
    const int y,
    const int z) {
    return is_natural(halo_blocks[smooth_linear_index(x + 1, y + 1, z + 1)])
        ? -1.0f
        : 1.0f;
}

std::array<float, 3> smooth_halo_gradient(
    const std::uint8_t *halo_blocks,
    const int x,
    const int y,
    const int z) {
    return {{
        smooth_density_at(halo_blocks, x + 1, y, z) - smooth_density_at(halo_blocks, x - 1, y, z),
        smooth_density_at(halo_blocks, x, y + 1, z) - smooth_density_at(halo_blocks, x, y - 1, z),
        smooth_density_at(halo_blocks, x, y, z + 1) - smooth_density_at(halo_blocks, x, y, z - 1),
    }};
}

std::array<float, 3> smooth_vertex_normal(
    const std::uint8_t *halo_blocks,
    const float x,
    const float y,
    const float z) {
    const int x0 = static_cast<int>(std::floor(x));
    const int y0 = static_cast<int>(std::floor(y));
    const int z0 = static_cast<int>(std::floor(z));
    const float tx = x - static_cast<float>(x0);
    const float ty = y - static_cast<float>(y0);
    const float tz = z - static_cast<float>(z0);
    std::array<float, 3> result{};
    for (int corner = 0; corner < 8; ++corner) {
        const int gx = x0 + (corner & 1);
        const int gy = y0 + ((corner >> 1) & 1);
        const int gz = z0 + ((corner >> 2) & 1);
        const auto gradient = smooth_halo_gradient(halo_blocks, gx, gy, gz);
        const float weight = (gx == x0 ? 1.0f - tx : tx)
            * (gy == y0 ? 1.0f - ty : ty)
            * (gz == z0 ? 1.0f - tz : tz);
        result[0] += gradient[0] * weight;
        result[1] += gradient[1] * weight;
        result[2] += gradient[2] * weight;
    }
    return result;
}

std::int32_t get_smooth_vertex(Workspace &workspace, const SmoothSample &sample) {
    const auto x = static_cast<std::uint32_t>(sample.x * 2.0f + 2.5f);
    const auto y = static_cast<std::uint32_t>(sample.y * 2.0f + 2.5f);
    const auto z = static_cast<std::uint32_t>(sample.z * 2.0f + 2.5f);
    const std::uint32_t key = x | (y << 8u) | (z << 16u);
    const auto found = workspace.smooth_vertex_lookup.find(key);
    if (found != workspace.smooth_vertex_lookup.end()) return found->second;
    const auto index = static_cast<std::int32_t>(workspace.block_types.size());
    add_vertex(workspace, sample.x, sample.y, sample.z, 0.0f, 0.0f, 0.0f, sample.material);
    workspace.smooth_vertex_lookup.emplace(key, index);
    return index;
}

void emit_smooth_triangle(
    Workspace &workspace,
    SmoothSample a,
    SmoothSample b,
    SmoothSample c,
    const float outward_x,
    const float outward_y,
    const float outward_z) {
    float ab_x = b.x - a.x, ab_y = b.y - a.y, ab_z = b.z - a.z;
    float ac_x = c.x - a.x, ac_y = c.y - a.y, ac_z = c.z - a.z;
    float normal_x = ab_y * ac_z - ab_z * ac_y;
    float normal_y = ab_z * ac_x - ab_x * ac_z;
    float normal_z = ab_x * ac_y - ab_y * ac_x;
    if (normal_x * normal_x + normal_y * normal_y + normal_z * normal_z <= 0.0000001f) return;
    if (normal_x * outward_x + normal_y * outward_y + normal_z * outward_z < 0.0f) {
        std::swap(b, c);
        normal_x = -normal_x;
        normal_y = -normal_y;
        normal_z = -normal_z;
    }
    const std::int32_t ia = get_smooth_vertex(workspace, a);
    const std::int32_t ib = get_smooth_vertex(workspace, b);
    const std::int32_t ic = get_smooth_vertex(workspace, c);
    for (const std::int32_t index : {ia, ib, ic}) {
        const auto component = static_cast<std::size_t>(index) * 3;
        workspace.normals[component] += normal_x;
        workspace.normals[component + 1] += normal_y;
        workspace.normals[component + 2] += normal_z;
    }
    // Godot renders clockwise triangle winding as the front face. Keep the
    // outward normal accumulation above while reversing the emitted order.
    workspace.indices.push_back(ia);
    workspace.indices.push_back(ic);
    workspace.indices.push_back(ib);
}

void polygonize_smooth_tetrahedron(Workspace &workspace, const std::array<SmoothSample, 4> &tetra) {
    std::array<int, 4> inside{}, outside{};
    int inside_count = 0, outside_count = 0;
    for (int index = 0; index < 4; ++index) {
        if (tetra[index].density < 0.0f) inside[inside_count++] = index;
        else outside[outside_count++] = index;
    }
    if (inside_count == 0 || inside_count == 4) return;
    float outward_x = 0.0f, outward_y = 0.0f, outward_z = 0.0f;
    for (int index = 0; index < outside_count; ++index) {
        outward_x += tetra[outside[index]].x;
        outward_y += tetra[outside[index]].y;
        outward_z += tetra[outside[index]].z;
    }
    for (int index = 0; index < inside_count; ++index) {
        outward_x -= tetra[inside[index]].x;
        outward_y -= tetra[inside[index]].y;
        outward_z -= tetra[inside[index]].z;
    }
    if (inside_count == 1 || inside_count == 3) {
        const bool one_inside = inside_count == 1;
        const int pivot = one_inside ? inside[0] : outside[0];
        const auto &others = one_inside ? outside : inside;
        emit_smooth_triangle(workspace, interpolate_smooth(tetra[pivot], tetra[others[0]]), interpolate_smooth(tetra[pivot], tetra[others[1]]), interpolate_smooth(tetra[pivot], tetra[others[2]]), outward_x, outward_y, outward_z);
        return;
    }
    const SmoothSample p00 = interpolate_smooth(tetra[inside[0]], tetra[outside[0]]);
    const SmoothSample p01 = interpolate_smooth(tetra[inside[0]], tetra[outside[1]]);
    const SmoothSample p10 = interpolate_smooth(tetra[inside[1]], tetra[outside[0]]);
    const SmoothSample p11 = interpolate_smooth(tetra[inside[1]], tetra[outside[1]]);
    emit_smooth_triangle(workspace, p00, p01, p11, outward_x, outward_y, outward_z);
    emit_smooth_triangle(workspace, p00, p11, p10, outward_x, outward_y, outward_z);
}

void build_smooth_regular(Workspace &workspace, const std::uint8_t *halo_blocks) {
    using namespace zylann::voxel::transvoxel::tables;
    constexpr std::array<std::array<int, 3>, 8> transvoxel_corners = {{
        {{0, 0, 0}}, {{1, 0, 0}}, {{0, 1, 0}}, {{1, 1, 0}},
        {{0, 0, 1}}, {{1, 0, 1}}, {{0, 1, 1}}, {{1, 1, 1}},
    }};
    std::array<SmoothSample, 8> cube{};
    for (int z = 1; z < kSmoothHaloSize - 2; ++z)
    for (int y = 1; y < kSmoothHaloSize - 2; ++y)
    for (int x = 1; x < kSmoothHaloSize - 2; ++x) {
        std::uint8_t case_code = 0;
        for (int corner = 0; corner < 8; ++corner) {
            const int sx = x + transvoxel_corners[corner][0];
            const int sy = y + transvoxel_corners[corner][1];
            const int sz = z + transvoxel_corners[corner][2];
            const std::uint8_t material = halo_blocks[smooth_linear_index(sx, sy, sz)];
            const bool natural = is_natural(material);
            case_code |= static_cast<std::uint8_t>(natural ? 1u << corner : 0u);
            cube[corner] = {static_cast<float>(sx - 1), static_cast<float>(sy - 1), static_cast<float>(sz - 1), natural ? -1.0f : 1.0f, material};
        }
        if (case_code == 0 || case_code == 255) continue;

        const RegularCellData &cell_data = get_regular_cell_data(get_regular_cell_class(case_code));
        const float outward_x = cube[1].density + cube[3].density + cube[5].density + cube[7].density
            - cube[0].density - cube[2].density - cube[4].density - cube[6].density;
        const float outward_y = cube[2].density + cube[3].density + cube[6].density + cube[7].density
            - cube[0].density - cube[1].density - cube[4].density - cube[5].density;
        const float outward_z = cube[4].density + cube[5].density + cube[6].density + cube[7].density
            - cube[0].density - cube[1].density - cube[2].density - cube[3].density;
        std::array<SmoothSample, 12> vertices{};
        const unsigned int vertex_count = static_cast<unsigned int>(cell_data.GetVertexCount());
        for (unsigned int vertex_index = 0; vertex_index < vertex_count; ++vertex_index) {
            const std::uint16_t edge_data = get_regular_vertex_data(case_code, vertex_index);
            const std::uint8_t edge = static_cast<std::uint8_t>(edge_data & 0xffu);
            const std::uint8_t corner_a = static_cast<std::uint8_t>((edge >> 4u) & 0x0fu);
            const std::uint8_t corner_b = static_cast<std::uint8_t>(edge & 0x0fu);
            vertices[vertex_index] = interpolate_smooth(cube[corner_a], cube[corner_b]);
        }
        const unsigned int triangle_count = static_cast<unsigned int>(cell_data.GetTriangleCount());
        for (unsigned int triangle_index = 0; triangle_index < triangle_count; ++triangle_index) {
            const unsigned int offset = triangle_index * 3;
            emit_smooth_triangle(
                workspace,
                vertices[cell_data.get_vertex_index(offset)],
                vertices[cell_data.get_vertex_index(offset + 1)],
                vertices[cell_data.get_vertex_index(offset + 2)],
                outward_x,
                outward_y,
                outward_z);
        }
    }
    for (std::size_t index = 0; index < workspace.block_types.size(); ++index) {
        const std::size_t component = index * 3;
        const auto gradient = smooth_vertex_normal(
            halo_blocks,
            workspace.positions[component],
            workspace.positions[component + 1],
            workspace.positions[component + 2]);
        float nx = gradient[0], ny = gradient[1], nz = gradient[2];
        const float length_squared = nx * nx + ny * ny + nz * nz;
        if (length_squared > 0.000001f) {
            const float inverse_length = 1.0f / std::sqrt(length_squared);
            workspace.normals[component] = nx * inverse_length;
            workspace.normals[component + 1] = ny * inverse_length;
            workspace.normals[component + 2] = nz * inverse_length;
        } else workspace.normals[component + 1] = 1.0f;
    }
}

void build_smooth_regular_sdf(
    Workspace &workspace,
    const float *densities,
    const std::uint8_t *materials) {
    using namespace zylann::voxel::transvoxel::tables;
    constexpr std::array<std::array<int, 3>, 8> corners = {{
        {{0, 0, 0}}, {{1, 0, 0}}, {{0, 1, 0}}, {{1, 1, 0}},
        {{0, 0, 1}}, {{1, 0, 1}}, {{0, 1, 1}}, {{1, 1, 1}},
    }};
    std::array<SmoothSample, 8> cube{};
    for (int z = 0; z < kCoarseSampleCount - 1; ++z)
    for (int y = 0; y < kCoarseSampleCount - 1; ++y)
    for (int x = 0; x < kCoarseSampleCount - 1; ++x) {
        std::uint8_t case_code = 0;
        for (int corner = 0; corner < 8; ++corner) {
            const int sx = x + corners[corner][0];
            const int sy = y + corners[corner][1];
            const int sz = z + corners[corner][2];
            const int sample_index = coarse_linear_index(sx, sy, sz);
            const float density = densities[sample_index];
            case_code |= static_cast<std::uint8_t>(density < 0.0f ? 1u << corner : 0u);
            cube[corner] = {
                static_cast<float>(sx), static_cast<float>(sy), static_cast<float>(sz),
                density, materials[sample_index]};
        }
        if (case_code == 0 || case_code == 255) continue;

        const RegularCellData &cell_data = get_regular_cell_data(get_regular_cell_class(case_code));
        const float outward_x = cube[1].density + cube[3].density + cube[5].density + cube[7].density
            - cube[0].density - cube[2].density - cube[4].density - cube[6].density;
        const float outward_y = cube[2].density + cube[3].density + cube[6].density + cube[7].density
            - cube[0].density - cube[1].density - cube[4].density - cube[5].density;
        const float outward_z = cube[4].density + cube[5].density + cube[6].density + cube[7].density
            - cube[0].density - cube[1].density - cube[2].density - cube[3].density;
        std::array<SmoothSample, 12> vertices{};
        const unsigned int vertex_count = static_cast<unsigned int>(cell_data.GetVertexCount());
        for (unsigned int vertex_index = 0; vertex_index < vertex_count; ++vertex_index) {
            const std::uint16_t edge_data = get_regular_vertex_data(case_code, vertex_index);
            const std::uint8_t edge = static_cast<std::uint8_t>(edge_data & 0xffu);
            vertices[vertex_index] = interpolate_smooth(
                cube[(edge >> 4u) & 0x0fu], cube[edge & 0x0fu]);
        }
        const unsigned int triangle_count = static_cast<unsigned int>(cell_data.GetTriangleCount());
        for (unsigned int triangle_index = 0; triangle_index < triangle_count; ++triangle_index) {
            const unsigned int offset = triangle_index * 3;
            emit_smooth_triangle(
                workspace,
                vertices[cell_data.get_vertex_index(offset)],
                vertices[cell_data.get_vertex_index(offset + 1)],
                vertices[cell_data.get_vertex_index(offset + 2)],
                outward_x,
                outward_y,
                outward_z);
        }
    }
    for (std::size_t index = 0; index < workspace.block_types.size(); ++index) {
        const std::size_t component = index * 3;
        const float nx = workspace.normals[component];
        const float ny = workspace.normals[component + 1];
        const float nz = workspace.normals[component + 2];
        const float length_squared = nx * nx + ny * ny + nz * nz;
        if (length_squared > 0.000001f) {
            const float inverse_length = 1.0f / std::sqrt(length_squared);
            workspace.normals[component] = nx * inverse_length;
            workspace.normals[component + 1] = ny * inverse_length;
            workspace.normals[component + 2] = nz * inverse_length;
        } else {
            workspace.normals[component + 1] = 1.0f;
        }
    }
}

void build_smooth_regular_fine_sdf(
    Workspace &workspace,
    const float *densities,
    const std::uint8_t *materials) {
    using namespace zylann::voxel::transvoxel::tables;
    constexpr std::array<std::array<int, 3>, 8> corners = {{
        {{0, 0, 0}}, {{1, 0, 0}}, {{0, 1, 0}}, {{1, 1, 0}},
        {{0, 0, 1}}, {{1, 0, 1}}, {{0, 1, 1}}, {{1, 1, 1}},
    }};
    std::array<SmoothSample, 8> cube{};
    for (int z = 1; z < kSmoothHaloSize - 2; ++z)
    for (int y = 1; y < kSmoothHaloSize - 2; ++y)
    for (int x = 1; x < kSmoothHaloSize - 2; ++x) {
        std::uint8_t case_code = 0;
        for (int corner = 0; corner < 8; ++corner) {
            const int sx = x + corners[corner][0];
            const int sy = y + corners[corner][1];
            const int sz = z + corners[corner][2];
            const int sample_index = smooth_linear_index(sx, sy, sz);
            const float density = densities[sample_index];
            case_code |= static_cast<std::uint8_t>(density < 0.0f ? 1u << corner : 0u);
            cube[corner] = {
                static_cast<float>(sx - 1), static_cast<float>(sy - 1), static_cast<float>(sz - 1),
                density, materials[sample_index]};
        }
        if (case_code == 0 || case_code == 255) continue;
        const RegularCellData &cell_data = get_regular_cell_data(get_regular_cell_class(case_code));
        const float outward_x = cube[1].density + cube[3].density + cube[5].density + cube[7].density - cube[0].density - cube[2].density - cube[4].density - cube[6].density;
        const float outward_y = cube[2].density + cube[3].density + cube[6].density + cube[7].density - cube[0].density - cube[1].density - cube[4].density - cube[5].density;
        const float outward_z = cube[4].density + cube[5].density + cube[6].density + cube[7].density - cube[0].density - cube[1].density - cube[2].density - cube[3].density;
        std::array<SmoothSample, 12> vertices{};
        const unsigned int vertex_count = static_cast<unsigned int>(cell_data.GetVertexCount());
        for (unsigned int vertex_index = 0; vertex_index < vertex_count; ++vertex_index) {
            const std::uint16_t edge_data = get_regular_vertex_data(case_code, vertex_index);
            const std::uint8_t edge = static_cast<std::uint8_t>(edge_data & 0xffu);
            vertices[vertex_index] = interpolate_smooth(cube[(edge >> 4u) & 0x0fu], cube[edge & 0x0fu]);
        }
        const unsigned int triangle_count = static_cast<unsigned int>(cell_data.GetTriangleCount());
        for (unsigned int triangle_index = 0; triangle_index < triangle_count; ++triangle_index) {
            const unsigned int offset = triangle_index * 3;
            emit_smooth_triangle(workspace, vertices[cell_data.get_vertex_index(offset)], vertices[cell_data.get_vertex_index(offset + 1)], vertices[cell_data.get_vertex_index(offset + 2)], outward_x, outward_y, outward_z);
        }
    }
    for (std::size_t index = 0; index < workspace.block_types.size(); ++index) {
        const std::size_t component = index * 3;
        const float nx = workspace.normals[component], ny = workspace.normals[component + 1], nz = workspace.normals[component + 2];
        const float length_squared = nx * nx + ny * ny + nz * nz;
        if (length_squared > 0.000001f) {
            const float inverse_length = 1.0f / std::sqrt(length_squared);
            workspace.normals[component] = nx * inverse_length;
            workspace.normals[component + 1] = ny * inverse_length;
            workspace.normals[component + 2] = nz * inverse_length;
        } else workspace.normals[component + 1] = 1.0f;
    }
}

enum class SmoothTransitionFace : int {
    negative_x = 0,
    positive_x = 1,
    negative_y = 2,
    positive_y = 3,
    negative_z = 4,
    positive_z = 5,
};

std::array<int, 3> transition_face_to_halo(
    const int x,
    const int y,
    const int z,
    const SmoothTransitionFace face) {
    switch (face) {
        case SmoothTransitionFace::negative_x: return {z, x, y};
        case SmoothTransitionFace::positive_x: return {kSmoothHaloSize - 1 - z, y, x};
        case SmoothTransitionFace::negative_y: return {y, z, x};
        case SmoothTransitionFace::positive_y: return {x, kSmoothHaloSize - 1 - z, y};
        case SmoothTransitionFace::negative_z: return {x, y, z};
        case SmoothTransitionFace::positive_z: return {y, x, kSmoothHaloSize - 1 - z};
    }
    return {0, 0, 0};
}

void build_smooth_transition(
    Workspace &workspace,
    const float *halo_densities,
    const std::uint8_t *halo_materials,
    const float *coarse_densities,
    const std::uint8_t *coarse_materials,
    const std::array<int, 3> &halo_origin_minus_coarse_origin,
    const int coarse_spacing,
    const SmoothTransitionFace face) {
    using namespace zylann::voxel::transvoxel::tables;
    constexpr int min_face_position = 1;
    constexpr int max_face_position = kSmoothHaloSize - 3;
    std::array<SmoothSample, 13> samples{};
    for (int fy = min_face_position; fy < max_face_position; fy += 2)
    for (int fx = min_face_position; fx < max_face_position; fx += 2) {
        const std::array<std::array<int, 2>, 9> offsets = {{
            {{0, 0}}, {{1, 0}}, {{2, 0}}, {{0, 1}}, {{1, 1}},
            {{2, 1}}, {{0, 2}}, {{1, 2}}, {{2, 2}},
        }};
        for (int index = 0; index < 9; ++index) {
            const auto point = transition_face_to_halo(
                fx + offsets[index][0], fy + offsets[index][1], min_face_position, face);
            const int sample_index = smooth_linear_index(point[0], point[1], point[2]);
            samples[index] = {
                static_cast<float>(point[0] - 1),
                static_cast<float>(point[1] - 1),
                static_cast<float>(point[2] - 1),
                halo_densities[sample_index],
                halo_materials[sample_index],
            };
        }
        constexpr std::array<std::array<int, 2>, 4> coarse_offsets = {{
            {{0, 0}}, {{2, 0}}, {{0, 2}}, {{2, 2}},
        }};
        for (int coarse_index = 0; coarse_index < 4; ++coarse_index) {
            const auto fine_point = transition_face_to_halo(
                fx + coarse_offsets[coarse_index][0],
                fy + coarse_offsets[coarse_index][1],
                min_face_position,
                face);
            const int coarse_x = (halo_origin_minus_coarse_origin[0] + fine_point[0]) / coarse_spacing;
            const int coarse_y = (halo_origin_minus_coarse_origin[1] + fine_point[1]) / coarse_spacing;
            const int coarse_z = (halo_origin_minus_coarse_origin[2] + fine_point[2]) / coarse_spacing;
            const int coarse_sample_index = coarse_linear_index(coarse_x, coarse_y, coarse_z);
            samples[9 + coarse_index].density = coarse_densities[coarse_sample_index];
            samples[9 + coarse_index].material = coarse_materials[coarse_sample_index];
            const auto bridge_point = transition_face_to_halo(
                fx + coarse_offsets[coarse_index][0],
                fy + coarse_offsets[coarse_index][1],
                0,
                face);
            samples[9 + coarse_index].x = static_cast<float>(bridge_point[0] - 1);
            samples[9 + coarse_index].y = static_cast<float>(bridge_point[1] - 1);
            samples[9 + coarse_index].z = static_cast<float>(bridge_point[2] - 1);
        }
        const std::uint16_t case_code =
            (samples[0].density < 0.0f ? 1u : 0u)
            | (samples[1].density < 0.0f ? 2u : 0u)
            | (samples[2].density < 0.0f ? 4u : 0u)
            | (samples[5].density < 0.0f ? 8u : 0u)
            | (samples[8].density < 0.0f ? 16u : 0u)
            | (samples[7].density < 0.0f ? 32u : 0u)
            | (samples[6].density < 0.0f ? 64u : 0u)
            | (samples[3].density < 0.0f ? 128u : 0u)
            | (samples[4].density < 0.0f ? 256u : 0u);
        if (case_code == 0 || case_code == 511) continue;

        const std::uint8_t cell_class = get_transition_cell_class(case_code);
        const TransitionCellData &cell_data = get_transition_cell_data(cell_class & 0x7fu);
        std::array<SmoothSample, 12> vertices{};
        const unsigned int vertex_count = static_cast<unsigned int>(cell_data.GetVertexCount());
        for (unsigned int vertex_index = 0; vertex_index < vertex_count; ++vertex_index) {
            const std::uint16_t edge_data = get_transition_vertex_data(case_code, vertex_index);
            const std::uint8_t a = static_cast<std::uint8_t>((edge_data >> 4u) & 0x0fu);
            const std::uint8_t b = static_cast<std::uint8_t>(edge_data & 0x0fu);
            vertices[vertex_index] = interpolate_smooth(samples[a], samples[b]);
        }
        const unsigned int triangle_count = static_cast<unsigned int>(cell_data.GetTriangleCount());
        const bool flipped = (cell_class & 0x80u) != 0;
        for (unsigned int triangle_index = 0; triangle_index < triangle_count; ++triangle_index) {
            const unsigned int offset = triangle_index * 3;
            const std::uint8_t a = cell_data.get_vertex_index(offset);
            const std::uint8_t b = cell_data.get_vertex_index(offset + 1);
            const std::uint8_t c = cell_data.get_vertex_index(offset + 2);
            emit_smooth_triangle(
                workspace,
                vertices[flipped ? a : c],
                vertices[b],
                vertices[flipped ? c : a],
                0.0f,
                0.0f,
                0.0f);
        }
    }
}

void clear_result(NativeMeshResult *result) {
    result->positions = nullptr;
    result->normals = nullptr;
    result->block_types = nullptr;
    result->indices = nullptr;
    result->vertex_count = 0;
    result->index_count = 0;
}

void free_result(NativeMeshResult *result) {
    if (result == nullptr) {
        return;
    }
    std::free(result->positions);
    std::free(result->normals);
    std::free(result->block_types);
    std::free(result->indices);
    clear_result(result);
}

template <typename T>
T *copy_to_malloc(const std::vector<T> &source) {
    if (source.empty()) {
        return nullptr;
    }
    auto *destination = static_cast<T *>(std::malloc(source.size() * sizeof(T)));
    if (destination != nullptr) {
        std::memcpy(destination, source.data(), source.size() * sizeof(T));
    }
    return destination;
}

} // namespace

RING_EXPORT int ring_voxel_mesher_abi_version() {
    return 7;
}

RING_EXPORT int ring_voxel_mesher_build(
    const std::uint8_t *blocks,
    const std::uint8_t *negative_x,
    const std::uint8_t *positive_x,
    const std::uint8_t *negative_y,
    const std::uint8_t *positive_y,
    const std::uint8_t *negative_z,
    const std::uint8_t *positive_z,
    const int use_greedy_merging,
    NativeMeshResult *result) {
    if (blocks == nullptr
        || negative_x == nullptr
        || positive_x == nullptr
        || negative_y == nullptr
        || positive_y == nullptr
        || negative_z == nullptr
        || positive_z == nullptr
        || result == nullptr) {
        return 1;
    }

    clear_result(result);
    try {
        Workspace &workspace = g_workspace;
        workspace.reset();
        const bool greedy = use_greedy_merging != 0;
        build_positive_x(workspace, blocks, positive_x, greedy);
        build_negative_x(workspace, blocks, negative_x, greedy);
        build_positive_y(workspace, blocks, positive_y, greedy);
        build_negative_y(workspace, blocks, negative_y, greedy);
        build_positive_z(workspace, blocks, positive_z, greedy);
        build_negative_z(workspace, blocks, negative_z, greedy);

        result->vertex_count = static_cast<std::int32_t>(workspace.block_types.size());
        result->index_count = static_cast<std::int32_t>(workspace.indices.size());
        result->positions = copy_to_malloc(workspace.positions);
        result->normals = copy_to_malloc(workspace.normals);
        result->block_types = copy_to_malloc(workspace.block_types);
        result->indices = copy_to_malloc(workspace.indices);
        const bool allocation_failed =
            (result->vertex_count > 0
                && (result->positions == nullptr
                    || result->normals == nullptr
                    || result->block_types == nullptr))
            || (result->index_count > 0 && result->indices == nullptr);
        workspace.trim_oversized();
        if (allocation_failed) {
            free_result(result);
            return 2;
        }
        return 0;
    } catch (const std::bad_alloc &) {
        free_result(result);
        return 2;
    } catch (...) {
        free_result(result);
        return 3;
    }
}

RING_EXPORT int ring_voxel_mesher_build_smooth_regular(
    const std::uint8_t *halo_blocks,
    NativeMeshResult *result) {
    if (halo_blocks == nullptr || result == nullptr) return 1;
    clear_result(result);
    try {
        Workspace &workspace = g_workspace;
        workspace.reset();
        build_smooth_regular(workspace, halo_blocks);
        result->vertex_count = static_cast<std::int32_t>(workspace.block_types.size());
        result->index_count = static_cast<std::int32_t>(workspace.indices.size());
        result->positions = copy_to_malloc(workspace.positions);
        result->normals = copy_to_malloc(workspace.normals);
        result->block_types = copy_to_malloc(workspace.block_types);
        result->indices = copy_to_malloc(workspace.indices);
        const bool allocation_failed =
            (result->vertex_count > 0 && (result->positions == nullptr || result->normals == nullptr || result->block_types == nullptr))
            || (result->index_count > 0 && result->indices == nullptr);
        workspace.trim_oversized();
        if (allocation_failed) {
            free_result(result);
            return 2;
        }
        return 0;
    } catch (const std::bad_alloc &) {
        free_result(result);
        return 2;
    } catch (...) {
        free_result(result);
        return 3;
    }
}

RING_EXPORT int ring_voxel_mesher_build_smooth_regular_sdf(
    const float *densities,
    const std::uint8_t *materials,
    NativeMeshResult *result) {
    if (densities == nullptr || materials == nullptr || result == nullptr) return 1;
    clear_result(result);
    try {
        Workspace &workspace = g_workspace;
        workspace.reset();
        build_smooth_regular_sdf(workspace, densities, materials);
        result->vertex_count = static_cast<std::int32_t>(workspace.block_types.size());
        result->index_count = static_cast<std::int32_t>(workspace.indices.size());
        result->positions = copy_to_malloc(workspace.positions);
        result->normals = copy_to_malloc(workspace.normals);
        result->block_types = copy_to_malloc(workspace.block_types);
        result->indices = copy_to_malloc(workspace.indices);
        const bool allocation_failed =
            (result->vertex_count > 0 && (result->positions == nullptr || result->normals == nullptr || result->block_types == nullptr))
            || (result->index_count > 0 && result->indices == nullptr);
        workspace.trim_oversized();
        if (allocation_failed) {
            free_result(result);
            return 2;
        }
        return 0;
    } catch (const std::bad_alloc &) {
        free_result(result);
        return 2;
    } catch (...) {
        free_result(result);
        return 3;
    }
}

RING_EXPORT int ring_voxel_mesher_build_smooth_regular_fine_sdf(
    const float *densities,
    const std::uint8_t *materials,
    NativeMeshResult *result) {
    if (densities == nullptr || materials == nullptr || result == nullptr) return 1;
    clear_result(result);
    try {
        Workspace &workspace = g_workspace;
        workspace.reset();
        build_smooth_regular_fine_sdf(workspace, densities, materials);
        result->vertex_count = static_cast<std::int32_t>(workspace.block_types.size());
        result->index_count = static_cast<std::int32_t>(workspace.indices.size());
        result->positions = copy_to_malloc(workspace.positions);
        result->normals = copy_to_malloc(workspace.normals);
        result->block_types = copy_to_malloc(workspace.block_types);
        result->indices = copy_to_malloc(workspace.indices);
        const bool allocation_failed =
            (result->vertex_count > 0 && (result->positions == nullptr || result->normals == nullptr || result->block_types == nullptr))
            || (result->index_count > 0 && result->indices == nullptr);
        workspace.trim_oversized();
        if (allocation_failed) { free_result(result); return 2; }
        return 0;
    } catch (const std::bad_alloc &) {
        free_result(result);
        return 2;
    } catch (...) {
        free_result(result);
        return 3;
    }
}

RING_EXPORT int ring_voxel_mesher_build_smooth_transition(
    const float *halo_densities,
    const std::uint8_t *halo_materials,
    const float *coarse_densities,
    const std::uint8_t *coarse_materials,
    const int halo_origin_minus_coarse_x,
    const int halo_origin_minus_coarse_y,
    const int halo_origin_minus_coarse_z,
    const int coarse_spacing,
    const int face,
    NativeMeshResult *result) {
    if (halo_densities == nullptr
        || halo_materials == nullptr
        || coarse_densities == nullptr
        || coarse_materials == nullptr
        || result == nullptr
        || coarse_spacing != 2
        || face < static_cast<int>(SmoothTransitionFace::negative_x)
        || face > static_cast<int>(SmoothTransitionFace::positive_z)) {
        return 1;
    }
    clear_result(result);
    try {
        Workspace &workspace = g_workspace;
        workspace.reset();
        build_smooth_transition(
            workspace,
            halo_densities,
            halo_materials,
            coarse_densities,
            coarse_materials,
            {{halo_origin_minus_coarse_x, halo_origin_minus_coarse_y, halo_origin_minus_coarse_z}},
            coarse_spacing,
            static_cast<SmoothTransitionFace>(face));
        result->vertex_count = static_cast<std::int32_t>(workspace.block_types.size());
        result->index_count = static_cast<std::int32_t>(workspace.indices.size());
        result->positions = copy_to_malloc(workspace.positions);
        result->normals = copy_to_malloc(workspace.normals);
        result->block_types = copy_to_malloc(workspace.block_types);
        result->indices = copy_to_malloc(workspace.indices);
        const bool allocation_failed =
            (result->vertex_count > 0 && (result->positions == nullptr || result->normals == nullptr || result->block_types == nullptr))
            || (result->index_count > 0 && result->indices == nullptr);
        workspace.trim_oversized();
        if (allocation_failed) {
            free_result(result);
            return 2;
        }
        return 0;
    } catch (const std::bad_alloc &) {
        free_result(result);
        return 2;
    } catch (...) {
        free_result(result);
        return 3;
    }
}

RING_EXPORT void ring_voxel_mesher_free(NativeMeshResult *result) {
    free_result(result);
}

// Minimal GDExtension entry point. The hot mesher API deliberately uses the
// C ABI above so worker threads do not enter Godot's Object/Variant API.
using GDExtensionBool = std::uint8_t;
using GDExtensionClassLibraryPtr = void *;
using GDExtensionInterfaceFunctionPtr = void (*)();
using GDExtensionInterfaceGetProcAddress = GDExtensionInterfaceFunctionPtr (*)(const char *);
enum GDExtensionInitializationLevel {
    GDEXTENSION_INITIALIZATION_CORE = 0,
    GDEXTENSION_INITIALIZATION_SERVERS = 1,
    GDEXTENSION_INITIALIZATION_SCENE = 2,
    GDEXTENSION_INITIALIZATION_EDITOR = 3,
};
using GDExtensionInitializationFunction = void (*)(void *, GDExtensionInitializationLevel);
struct GDExtensionInitialization {
    GDExtensionInitializationLevel minimum_initialization_level;
    void *userdata;
    GDExtensionInitializationFunction initialize;
    GDExtensionInitializationFunction deinitialize;
};

namespace {
void gdextension_noop(void *, GDExtensionInitializationLevel) {}
}

RING_EXPORT GDExtensionBool ring_voxel_mesher_library_init(
    GDExtensionInterfaceGetProcAddress,
    GDExtensionClassLibraryPtr,
    GDExtensionInitialization *initialization) {
    if (initialization == nullptr) {
        return 0;
    }
    initialization->minimum_initialization_level = GDEXTENSION_INITIALIZATION_SCENE;
    initialization->userdata = nullptr;
    initialization->initialize = gdextension_noop;
    initialization->deinitialize = gdextension_noop;
    return 1;
}
