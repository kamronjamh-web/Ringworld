// Vertex-table fetch for proxy sectors (docs/gpu-offload-plan.md section 3, WO-S0).
// A sector slot is one layer of three array textures (position+morph, runtime parent chord, packed attributes: oct16 normal, RGBA8 colour, flags, skirt UV).
// A shared index-only grid mesh supplies VERTEX_ID; the slot supplies everything else. The whole path is
// inert unless proxy_vertex_table_enabled is set, so a material that includes this file renders exactly as before.
//
// Layout of one slot, W = cells + 1 texels wide:
//   rows [0, W)        terrain lattice (row z, column x)
//   rows [W, 2W)       water lattice
//   rows [2W, 2W + R)  skirt vertices, index k -> row 2W + k / W, column k % W (4 edges x cells quads x 4 vertices)
// The terrain grid mesh numbers vertices 0..W*W-1 for the lattice and W*W + k for skirts; the water grid mesh
// numbers vertices 0..W*W-1 and defines PROXY_VERTEX_TABLE_WATER before including this file.
// Keep in sync with scripts/Voxel/LOD/RingTerrainProxySector.VertexTable.cs.
//
// Index 3 keeps this uniform clear of coverage_atlas_rect (index 0, must stay first) and the ocean's automatic
// indices, and identical across the terrain, shadow and ocean materials that share one sector node.
uniform bool proxy_vertex_table_enabled = false;
uniform bool proxy_vertex_table_has_chord = false;
uniform sampler2DArray proxy_vertex_table_pos : filter_nearest, repeat_disable;
uniform sampler2DArray proxy_vertex_table_chord : filter_nearest, repeat_disable;
// Texture2DArrayRD cannot wrap integer formats (Godot has no Image format for them), so the attribute block is an
// RGBA32F texture that carries raw uint32 bits; texelFetch is a plain 32-bit load and floatBitsToUint recovers them.
uniform sampler2DArray proxy_vertex_table_attr : filter_nearest, repeat_disable;
// -1 (the default) marks an instance that carries ordinary mesh attributes: the fetch is skipped even when the level's materials are table-enabled.
instance uniform int proxy_slot : instance_index(3) = -1;
// 1 on an instance that draws the water rows of the slot through a shader that has no water define (the shadow caster of a fully-ocean sector).
instance uniform int proxy_water_rows : instance_index(4) = 0;

float proxy_vertex_table_snorm16(uint bits) {
	int value = int(bits & 0xFFFFu);
	if (value >= 32768) value -= 65536;
	return max(float(value) / 32767.0, -1.0);
}

vec3 proxy_vertex_table_decode_normal(uint packed_normal) {
	vec2 e = vec2(proxy_vertex_table_snorm16(packed_normal), proxy_vertex_table_snorm16(packed_normal >> 16u));
	vec3 v = vec3(e.x, e.y, 1.0 - abs(e.x) - abs(e.y));
	if (v.z < 0.0) {
		vec2 folded = (vec2(1.0) - abs(v.yx));
		v.x = folded.x * (v.x >= 0.0 ? 1.0 : -1.0);
		v.y = folded.y * (v.y >= 0.0 ? 1.0 : -1.0);
	}
	float len = length(v);
	return len > 0.0 ? v / len : vec3(0.0, 0.0, 1.0);
}

void proxy_vertex_table_apply(int vertex_id, inout vec3 vertex, inout vec3 normal, inout vec4 color, inout vec2 uv, inout vec2 uv2, inout vec3 chord) {
	if (proxy_slot < 0) return;
	int width = textureSize(proxy_vertex_table_pos, 0).x;
	int lattice = width * width;
#ifdef PROXY_VERTEX_TABLE_WATER
	bool water_rows = true;
#else
	bool water_rows = proxy_water_rows != 0;
#endif
	bool skirt = !water_rows && vertex_id >= lattice;
	int skirt_index = vertex_id - lattice;
	int row = water_rows ? width + vertex_id / width : (skirt ? 2 * width + skirt_index / width : vertex_id / width);
	int column = water_rows ? vertex_id % width : (skirt ? skirt_index % width : vertex_id % width);
	ivec3 texel = ivec3(column, row, proxy_slot);
	vec4 pos_morph = texelFetch(proxy_vertex_table_pos, texel, 0);
#ifdef PROXY_VERTEX_TABLE_SHADOW
	// The shadow caster reads neither normal nor colour; only skirt vertices need the attribute block (flag and skirt UV).
	bool need_attr = skirt;
#else
	bool need_attr = true;
#endif
	uvec4 attr = uvec4(0u);
	if (need_attr) attr = floatBitsToUint(texelFetch(proxy_vertex_table_attr, texel, 0));
	vertex = pos_morph.xyz;
	normal = proxy_vertex_table_decode_normal(attr.x);
	color = vec4(float(attr.y & 0xFFu), float((attr.y >> 8u) & 0xFFu), float((attr.y >> 16u) & 0xFFu), float((attr.y >> 24u) & 0xFFu)) / 255.0;
	uv2 = vec2(pos_morph.w, need_attr ? ((attr.z & 1u) != 0u ? 1.0 : 0.0) : (water_rows ? 1.0 : 0.0));
	if (skirt) {
		// One UV component is a lattice value k / cells (z bits 16..31); the other is stored as float bits in w.
		// z bit 2 set: y is the lattice component.
		float lattice_component = float(attr.z >> 16u) / float(width - 1);
		float other_component = uintBitsToFloat(attr.w);
		uv = (attr.z & 4u) == 0u ? vec2(lattice_component, other_component) : vec2(other_component, lattice_component);
	} else {
		float cells = float(width - 1);
		float lattice_row = float(row - (row >= width ? width : 0));
		uv = vec2(float(column) / cells, lattice_row / cells);
	}
	// Water rows morph along the surface up vector (UV2.y = 1), never along the parent chord, so the ocean skips the third fetch.
	chord = proxy_vertex_table_has_chord && !water_rows ? texelFetch(proxy_vertex_table_chord, texel, 0).xyz : vec3(0.0);
}
