#[compute]
#version 450
// WO-G2 of docs/gpu-offload-plan.md: GPU sector build for the coarse proxy levels (8, 16 and 32 m; 64 x 64 cells, runtime parent chord).
// One source, four passes picked by a define the host prepends (PASS_S, PASS_B, PASS_A, PASS_K); all passes share one uniform set:
//   S  `sample`: natural presentation height for the 67 x 67 window (the 65 x 65 lattice plus a one-vertex halo) -> heights
//   B  `biome`: biome weights and moisture at the 16 m biome-lattice centres ResolveBiomeTextureWeight uses -> biome
//   A  `assemble`: terrain and water lattice (position, morph, chord, colour, normal) into the slot textures, plus the summary
//      (class counters, ocean cell mask, conservative ocean depth bytes, bounds)
//   K  `skirts`: the 4 x 64 skirt quads into the slot textures and the bounds
// The CPU reference is RingTerrainProxySector.BuildRawSector for runtime-generation worlds with positive-diagonal terrain triangles;
// every comment below names the CPU function it replays. Positions use the sector-centred stable form of docs/gpu-offload-plan.md 4.3.
#extension GL_KHR_shader_subgroup_basic : require
#extension GL_KHR_shader_subgroup_arithmetic : require
#extension GL_KHR_shader_subgroup_vote : require
#extension GL_KHR_shader_subgroup_ballot : require

#if defined(PASS_S) || defined(PASS_B)
#include "ring_height_model.glslinc"
#include "ring_regional_climate.glslinc"
#else
layout(set = 0, binding = 0, std430) restrict readonly buffer RingRecipe { uint r[]; } recipe;
#endif

layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;
layout(set = 0, binding = 1, std430) restrict readonly buffer Batch { uint b[]; } batch;
layout(set = 0, binding = 2, std430) restrict buffer Heights { float h[]; } heights;
layout(set = 0, binding = 3, std430) restrict buffer Biome { float v[]; } biome;
layout(set = 0, binding = 4, std430) restrict buffer Summary { uint s[]; } summary;
layout(set = 0, binding = 5, rgba32f) uniform restrict writeonly image2DArray imgPos;
layout(set = 0, binding = 6, rgba32f) uniform restrict writeonly image2DArray imgChord;
layout(set = 0, binding = 7, rgba32f) uniform restrict writeonly image2DArray imgAttr;

layout(push_constant, std430) uniform Params
{
	int count;
	int sentinel;
	int pad0;
	int pad1;
} pc;

// ------------------------------------------------------------------------------------------------------------ batch layout
// Header words (floats unless noted); the host (ProxyGpuSectorBackend.WriteBatch) writes the same indices.
#define H_SPACING 0       // int
#define H_SIZE 1          // int
#define H_PARENT 2        // int
#define H_UNDERLAY 4
#define H_PUNDER 5
#define H_OWN_WR 6
#define H_PAR_WR 7
#define H_SKIRT 8
#define H_RADIUS 9
#define H_RDA 10
#define H_BED 11
#define H_MAXS 12
#define H_OCEAN_A 13
#define H_SHALLOW 14      // rgb
#define H_DEEP 17         // rgb
#define H_LOW 20          // rgb
#define H_HIGH 23         // rgb
#define H_GRID 28         // 3 rows x 4 words (grid basis rows)
#define H_OWNER 40        // 3 rows x 4 words (owner-inverse basis rows)
#define H_SECTORS 64
#define SECTOR_WORDS 16
// Sector words: 0 anchor (around of the sector origin, int)  1 origin across (int)  2 slot (int)  3 flags
//               4,5 cos(theta0) df64  6,7 sin(theta0) df64 (theta0 = anchor angle, K1 projection)  8 cos(thetaC)  9 sin(thetaC) (sector centre, float)

#define CELLS 64
#define ROW 65
#define LATTICE 4225
#define WIN 67
#define WSTRIDE 4489
#define BIOME_STRIDE 4225
#define BIOME_FLOATS 8

// Summary words per sector.
#define SUM_VIS_MIN 0     // 3 x inverted ordered uint (atomicMax)
#define SUM_VIS_MAX 3     // 3 x ordered uint
#define SUM_MESH_MIN 6
#define SUM_MESH_MAX 9
#define SUM_REACH 12      // float bits, non-negative
#define SUM_WET 13
#define SUM_QUADS 14
#define SUM_MASK 16       // 4096 bits = 128 words
#define SUM_DEPTH 144     // 4225 bytes = 1057 words
#define SUM_STRIDE 1208

#define G_F(i) uintBitsToFloat(batch.b[(i)])
#define G_I(i) int(batch.b[(i)])

#define INF uintBitsToFloat(0x7f800000u)

// ------------------------------------------------------------------------------------------------------------ geometry
vec3 g_rows(int off, vec3 p)
{
	precise float x = G_F(off) * p.x + G_F(off + 1) * p.y + G_F(off + 2) * p.z;
	precise float y = G_F(off + 4) * p.x + G_F(off + 5) * p.y + G_F(off + 6) * p.z;
	precise float z = G_F(off + 8) * p.x + G_F(off + 9) * p.y + G_F(off + 10) * p.z;
	return vec3(x, y, z);
}

// ownerInverse * CenteredAt(centre).CanonicalToWorld(around, v, across): da = around - centreAround, dz = across - centreAcross (exact).
vec3 ring_pos(float cosC, float sinC, float da, float v, float dz)
{
	float R = G_F(H_RADIUS);
	float rda = G_F(H_RDA);
	precise float th = da / R;
	precise float th2 = th * th;
	precise float omc = th2 * 0.5 * (1.0 - th2 / 12.0);
	precise float sn = th * (1.0 - th2 / 6.0 * (1.0 - th2 / 20.0));
	precise float cs = 1.0 - omc;
	precise float t1 = -rda * omc - v * cs;
	precise float t2 = sn * (rda - v);
	precise float x = cosC * t1 - sinC * t2;
	precise float z = sinC * t1 + cosC * t2;
	vec3 world = g_rows(H_GRID, vec3(x, dz, z));
	return g_rows(H_OWNER, world);
}

// ------------------------------------------------------------------------------------------------------------ packing helpers
uint enc_color8(float c) { return uint(clamp(int(c * 255.0), 0, 255)); }
uint enc_color(vec4 c) { return enc_color8(c.r) | (enc_color8(c.g) << 8) | (enc_color8(c.b) << 16) | (enc_color8(c.a) << 24); }

int snorm16(float v) { return int(roundEven(clamp(v, -1.0, 1.0) * 32767.0)); }

// EncodeOctNormal16.
uint enc_oct(vec3 n)
{
	precise float l1 = abs(n.x) + abs(n.y) + abs(n.z);
	if (!(l1 > 1.0e-12)) return 0u;
	precise float x = n.x / l1;
	precise float y = n.y / l1;
	if (n.z < 0.0)
	{
		precise float fx = (1.0 - abs(y)) * (x >= 0.0 ? 1.0 : -1.0);
		precise float fy = (1.0 - abs(x)) * (y >= 0.0 ? 1.0 : -1.0);
		x = fx; y = fy;
	}
	return (uint(snorm16(x)) & 0xFFFFu) | ((uint(snorm16(y)) & 0xFFFFu) << 16);
}

uint ordered(float f)
{
	uint u = floatBitsToUint(f);
	return (u & 0x80000000u) != 0u ? ~u : (u | 0x80000000u);
}

#define uvec4ToFloat(a) vec4(uintBitsToFloat((a).x), uintBitsToFloat((a).y), uintBitsToFloat((a).z), uintBitsToFloat((a).w))

// Keeps every binding alive in every pass (a pass that drops a binding would not match the shared uniform set). Never true at run time.
void touch_all()
{
	if (pc.sentinel == 0x7fffffff)
	{
		heights.h[0] = 0.0; biome.v[0] = 0.0; summary.s[0] = recipe.r[0] + batch.b[0];
		imageStore(imgPos, ivec3(0), vec4(0.0)); imageStore(imgChord, ivec3(0), vec4(0.0)); imageStore(imgAttr, ivec3(0), vec4(0.0));
	}
}

// texel index t of the fixed slot layout (65 wide): terrain lattice, water lattice, then the skirt block.
void store_texel(int layer, int t, vec4 pos, vec4 chord, uvec4 attr)
{
	ivec3 c = ivec3(t % ROW, t / ROW, layer);
	imageStore(imgPos, c, pos);
	imageStore(imgChord, c, chord);
	imageStore(imgAttr, c, uvec4ToFloat(attr));
}

// ------------------------------------------------------------------------------------------------------------ window access
float hwin(int hb, int x, int z) { return heights.h[hb + (z + 1) * WIN + (x + 1)]; }
float hwc(int hb, int x, int z) { return hwin(hb, min(x, 65), min(z, 65)); }
float disp(float h) { return h < 0.0 ? 0.0 : h; } // ResolveDisplayedHeight for an ocean-or-dry runtime point

// SampleParentTerrainHeight (positive diagonal, runtime chord) / SampleParentTriangleHeight (water, skirts) at lattice (x, z).
float parent_height(int hb, int x, int z, bool positive, bool water)
{
	int x0 = x & ~1, z0 = z & ~1;
	float tx = (x & 1) != 0 ? 0.5 : 0.0;
	float tz = (z & 1) != 0 ? 0.5 : 0.0;
	float h00 = hwc(hb, x0, z0), h10 = hwc(hb, x0 + 2, z0), h01 = hwc(hb, x0, z0 + 2), h11 = hwc(hb, x0 + 2, z0 + 2);
	if (water) { h00 = disp(h00); h10 = disp(h10); h01 = disp(h01); h11 = disp(h11); }
	precise float r;
	if (positive)
		r = tx >= tz ? h00 + (h10 - h00) * tx + (h11 - h10) * tz : h00 + (h11 - h01) * tx + (h01 - h00) * tz;
	else
		r = tx + tz <= 1.0 ? h00 + (h10 - h00) * tx + (h01 - h00) * tz : h11 + (h01 - h11) * (1.0 - tx) + (h10 - h11) * (1.0 - tz);
	return r;
}

#if defined(PASS_S)
void main()
{
	uint gid = gl_GlobalInvocationID.x;
	touch_all();
	int s = int(gid) / WSTRIDE;
	if (s >= pc.count) return;
	int i = int(gid) - s * WSTRIDE;
	int wz = i / WIN, wx = i - wz * WIN;
	int base = H_SECTORS + s * SECTOR_WORDS;
	int spacing = G_I(H_SPACING);
	hm_set_anchor(G_I(base), vec2(G_F(base + 4), G_F(base + 5)), vec2(G_F(base + 6), G_F(base + 7)));
	HmResult r = hm_sample((wx - 1) * spacing, G_I(base + 1) + (wz - 1) * spacing);
	heights.h[gid] = r.height;
}
#elif defined(PASS_B)
void main()
{
	uint gid = gl_GlobalInvocationID.x;
	touch_all();
	int spacing = G_I(H_SPACING);
	// One thread per distinct 16 m centre: 33 x 33 at 8 m (every second vertex), 65 x 65 at 16 and 32 m (every vertex).
	int bw = spacing >= 16 ? ROW : 33;
	int perSector = bw * bw;
	int s = int(gid) / perSector;
	if (s >= pc.count) return;
	int i = int(gid) - s * perSector;
	int zi = i / bw, xi = i - zi * bw;
	int x = spacing >= 16 ? xi : xi * 2, z = spacing >= 16 ? zi : zi * 2;
	int base = H_SECTORS + s * SECTOR_WORDS;
	hm_set_anchor(G_I(base), vec2(G_F(base + 4), G_F(base + 5)), vec2(G_F(base + 6), G_F(base + 7)));
	int dA = x * spacing + 8, across = G_I(base + 1) + z * spacing + 8;
	HmResult r = hm_sample(dA, across);
	RcFields f = rc_sample_fields(dA, across);
	vec4 w = rc_biome_weights(r.height, r.continental, r.ridge, f);
	uint o = uint((s * BIOME_STRIDE + zi * ROW + xi) * BIOME_FLOATS);
	biome.v[o] = w.x; biome.v[o + 1u] = w.y; biome.v[o + 2u] = w.z; biome.v[o + 3u] = w.w;
	biome.v[o + 4u] = f.moisture;
}
#elif defined(PASS_A)

// Water vertex colour: RingWaterModel.EncodeWaterKind(ResolveSeededColor(ocean vertex)). Moisture comes from the 16 m centre.
vec4 water_color(float h, float moisture)
{
	int depth = max(0, 0 - int(floor(h)));
	vec3 baseColor = depth > 500 ? vec3(G_F(H_DEEP), G_F(H_DEEP + 1), G_F(H_DEEP + 2)) : vec3(G_F(H_SHALLOW), G_F(H_SHALLOW + 1), G_F(H_SHALLOW + 2));
	precise float variation = (moisture - 0.5) * 0.06;
	precise float r = clamp(baseColor.r + variation * 0.35, 0.0, 1.0);
	precise float g = clamp(baseColor.g + variation, 0.0, 1.0);
	precise float bl = clamp(baseColor.b + variation, 0.0, 1.0);
	return vec4(r, g, bl, G_F(H_OCEAN_A));
}

void main()
{
	uint gid = gl_GlobalInvocationID.x;
	touch_all();
	int s = int(gid) / LATTICE;
	bool live = s < pc.count;
	int i = int(gid) - s * LATTICE;
	int z = i / ROW, x = i - z * ROW;
	vec3 vmin = vec3(INF), vmax = vec3(-INF), mmin = vec3(INF), mmax = vec3(-INF);
	float reach = 0.0;
	uint wet = 0u, quads = 0u;
	int base = H_SECTORS + max(s, 0) * SECTOR_WORDS;
	if (live)
	{
		int hb = s * WSTRIDE;
		int spacing = G_I(H_SPACING), size = G_I(H_SIZE), parentSpacing = G_I(H_PARENT);
		float underlay = G_F(H_UNDERLAY), punder = G_F(H_PUNDER);
		int slot = G_I(base + 2);
		float cosC = G_F(base + 8), sinC = G_F(base + 9);
		float half_ = float(size) * 0.5;
		float da = float(x * spacing) - half_, dz = float(z * spacing) - half_;
		float h = hwin(hb, x, z);
		float wh = disp(h);

		// -------- terrain vertex
		vec3 pos = ring_pos(cosC, sinC, da, h - underlay, dz);
		precise float morphT = parent_height(hb, x, z, true, false) - h + underlay - punder;
		// runtime parent chord: SampleRuntimeParentChordOffset
		int x0 = x & ~1, z0 = z & ~1;
		float tx = (x & 1) != 0 ? 0.5 : 0.0, tz = (z & 1) != 0 ? 0.5 : 0.0;
		int x1 = x0 + 2, z1 = z0 + 2;
		vec3 p00 = ring_pos(cosC, sinC, float(x0 * spacing) - half_, hwc(hb, x0, z0) - punder, float(z0 * spacing) - half_);
		vec3 p10 = ring_pos(cosC, sinC, float(x1 * spacing) - half_, hwc(hb, x1, z0) - punder, float(z0 * spacing) - half_);
		vec3 p01 = ring_pos(cosC, sinC, float(x0 * spacing) - half_, hwc(hb, x0, z1) - punder, float(z1 * spacing) - half_);
		vec3 p11 = ring_pos(cosC, sinC, float(x1 * spacing) - half_, hwc(hb, x1, z1) - punder, float(z1 * spacing) - half_);
		precise vec3 parent = tx >= tz ? p00 * (1.0 - tx) + p10 * (tx - tz) + p11 * tz : p00 * (1.0 - tz) + p01 * (tz - tx) + p11 * tx;
		precise vec3 chord = parent - pos;

		// -------- normals (CalculateCanonicalSurfaceNormal): central difference one lattice step out, terrain then water heights
		float hl = hwin(hb, x - 1, z), hr = hwin(hb, x + 1, z), hd = hwin(hb, x, z - 1), hu = hwin(hb, x, z + 1);
		vec3 nt;
		{
			vec3 aM = ring_pos(cosC, sinC, da - float(spacing), hl - underlay, dz);
			vec3 aP = ring_pos(cosC, sinC, da + float(spacing), hr - underlay, dz);
			vec3 zM = ring_pos(cosC, sinC, da, hd - underlay, dz - float(spacing));
			vec3 zP = ring_pos(cosC, sinC, da, hu - underlay, dz + float(spacing));
			vec3 a = aP - aM, bb = zP - zM;
			precise vec3 cr = vec3(a.y * bb.z - a.z * bb.y, a.z * bb.x - a.x * bb.z, a.x * bb.y - a.y * bb.x);
			float l2 = dot(cr, cr);
			nt = l2 > 0.0000001 ? cr / sqrt(l2) : vec3(0.0, -1.0, 0.0);
		}
		vec3 nw;
		{
			vec3 aM = ring_pos(cosC, sinC, da - float(spacing), disp(hl) - underlay, dz);
			vec3 aP = ring_pos(cosC, sinC, da + float(spacing), disp(hr) - underlay, dz);
			vec3 zM = ring_pos(cosC, sinC, da, disp(hd) - underlay, dz - float(spacing));
			vec3 zP = ring_pos(cosC, sinC, da, disp(hu) - underlay, dz + float(spacing));
			vec3 a = aP - aM, bb = zP - zM;
			precise vec3 cr = vec3(a.y * bb.z - a.z * bb.y, a.z * bb.x - a.x * bb.z, a.x * bb.y - a.y * bb.x);
			float l2 = dot(cr, cr);
			nw = l2 > 0.0000001 ? cr / sqrt(l2) : vec3(0.0, -1.0, 0.0);
		}

		// -------- colours
		int xi = spacing >= 16 ? x : (x >> 1), zi = spacing >= 16 ? z : (z >> 1);
		uint bo = uint((s * BIOME_STRIDE + zi * ROW + xi) * BIOME_FLOATS);
		vec4 weights = vec4(biome.v[bo], biome.v[bo + 1u], biome.v[bo + 2u], biome.v[bo + 3u]);
		float moisture = biome.v[bo + 4u];
		uint terrainColor = enc_color(weights);
		uint waterColor = enc_color(water_color(h, moisture));

		// -------- water vertex
		vec3 wpos = ring_pos(cosC, sinC, da, wh - underlay, dz);
		precise float morphW = parent_height(hb, x, z, false, true) - wh + underlay - G_F(H_OWN_WR) - punder + G_F(H_PAR_WR);

		int t = i;
		store_texel(slot, t, vec4(pos, morphT), vec4(chord, 0.0), uvec4(enc_oct(nt), terrainColor, 0u, 0u));
		store_texel(slot, LATTICE + t, vec4(wpos, morphW), vec4(0.0), uvec4(enc_oct(nw), waterColor, 1u, 0u));

		// -------- summary inputs: CalculateCompleteVisualBounds over terrain and water lattice (chord endpoint for terrain, radial reach for water)
		vmin = min(pos, min(wpos, pos + chord));
		vmax = max(pos, max(wpos, pos + chord));
		mmin = min(pos, wpos);
		mmax = max(pos, wpos);
		reach = abs(morphW);

		// -------- class counters, ocean cells and conservative ocean depth (IsDisplayedOcean = presentation height < 0)
		wet = h < 0.0 ? 1u : 0u;
		if (x < CELLS && z < CELLS)
		{
			bool allWet = h < 0.0 && hr < 0.0 && hu < 0.0 && hwin(hb, x + 1, z + 1) < 0.0;
			if (allWet)
			{
				quads = 1u;
				int cell = z * CELLS + x;
				atomicOr(summary.s[s * SUM_STRIDE + SUM_MASK + (cell >> 5)], 1u << uint(cell & 31));
			}
		}
		bool ring9 = true;
		float maxH = -INF;
		for (int dzz = -1; dzz <= 1; dzz++)
		for (int dxx = -1; dxx <= 1; dxx++)
		{
			float q = hwin(hb, x + dxx, z + dzz);
			maxH = max(maxH, q);
			ring9 = ring9 && q < 0.0;
		}
		if (ring9)
		{
			uint d = uint(roundEven(clamp(0.0 - maxH, 0.0, 510.0) * 0.5));
			if (d != 0u) atomicOr(summary.s[s * SUM_STRIDE + SUM_DEPTH + (i >> 2)], d << uint((i & 3) * 8));
		}
	}

	// Per-subgroup reductions, then one atomic set per subgroup.
	vec3 rvmin = vec3(subgroupMin(vmin.x), subgroupMin(vmin.y), subgroupMin(vmin.z));
	vec3 rvmax = vec3(subgroupMax(vmax.x), subgroupMax(vmax.y), subgroupMax(vmax.z));
	vec3 rmmin = vec3(subgroupMin(mmin.x), subgroupMin(mmin.y), subgroupMin(mmin.z));
	vec3 rmmax = vec3(subgroupMax(mmax.x), subgroupMax(mmax.y), subgroupMax(mmax.z));
	float rreach = subgroupMax(reach);
	uint rwet = subgroupAdd(wet), rquads = subgroupAdd(quads);
	// A subgroup can straddle two sectors when 4225 is not a multiple of its size: write per active sector via the lane that owns the sector.
	int firstSector = subgroupBroadcastFirst(live ? s : 0x7fffffff);
	bool uniform_ = subgroupAll(!live || s == firstSector);
	if (uniform_)
	{
		if (subgroupElect() && firstSector != 0x7fffffff)
		{
			int sb = firstSector * SUM_STRIDE;
			atomicMax(summary.s[sb + SUM_VIS_MIN], ~ordered(rvmin.x)); atomicMax(summary.s[sb + SUM_VIS_MIN + 1], ~ordered(rvmin.y)); atomicMax(summary.s[sb + SUM_VIS_MIN + 2], ~ordered(rvmin.z));
			atomicMax(summary.s[sb + SUM_VIS_MAX], ordered(rvmax.x)); atomicMax(summary.s[sb + SUM_VIS_MAX + 1], ordered(rvmax.y)); atomicMax(summary.s[sb + SUM_VIS_MAX + 2], ordered(rvmax.z));
			atomicMax(summary.s[sb + SUM_MESH_MIN], ~ordered(rmmin.x)); atomicMax(summary.s[sb + SUM_MESH_MIN + 1], ~ordered(rmmin.y)); atomicMax(summary.s[sb + SUM_MESH_MIN + 2], ~ordered(rmmin.z));
			atomicMax(summary.s[sb + SUM_MESH_MAX], ordered(rmmax.x)); atomicMax(summary.s[sb + SUM_MESH_MAX + 1], ordered(rmmax.y)); atomicMax(summary.s[sb + SUM_MESH_MAX + 2], ordered(rmmax.z));
			atomicMax(summary.s[sb + SUM_REACH], floatBitsToUint(rreach));
			atomicAdd(summary.s[sb + SUM_WET], rwet);
			atomicAdd(summary.s[sb + SUM_QUADS], rquads);
		}
	}
	else if (live)
	{
		// Rare straddling subgroup: every lane pays its own atomics (correct, a few hundred in total).
		int sb = s * SUM_STRIDE;
		atomicMax(summary.s[sb + SUM_VIS_MIN], ~ordered(vmin.x)); atomicMax(summary.s[sb + SUM_VIS_MIN + 1], ~ordered(vmin.y)); atomicMax(summary.s[sb + SUM_VIS_MIN + 2], ~ordered(vmin.z));
		atomicMax(summary.s[sb + SUM_VIS_MAX], ordered(vmax.x)); atomicMax(summary.s[sb + SUM_VIS_MAX + 1], ordered(vmax.y)); atomicMax(summary.s[sb + SUM_VIS_MAX + 2], ordered(vmax.z));
		atomicMax(summary.s[sb + SUM_MESH_MIN], ~ordered(mmin.x)); atomicMax(summary.s[sb + SUM_MESH_MIN + 1], ~ordered(mmin.y)); atomicMax(summary.s[sb + SUM_MESH_MIN + 2], ~ordered(mmin.z));
		atomicMax(summary.s[sb + SUM_MESH_MAX], ordered(mmax.x)); atomicMax(summary.s[sb + SUM_MESH_MAX + 1], ordered(mmax.y)); atomicMax(summary.s[sb + SUM_MESH_MAX + 2], ordered(mmax.z));
		atomicMax(summary.s[sb + SUM_REACH], floatBitsToUint(reach));
		atomicAdd(summary.s[sb + SUM_WET], wet);
		atomicAdd(summary.s[sb + SUM_QUADS], quads);
	}
}
#elif defined(PASS_K)

// ResolveElevationColor(RoundToInt(max(h0, h1))).
vec4 skirt_color(float hmax)
{
	float t = clamp((roundEven(hmax) - G_F(H_BED)) / (G_F(H_MAXS) - G_F(H_BED)), 0.0, 1.0);
	precise float r = G_F(H_LOW) + (G_F(H_HIGH) - G_F(H_LOW)) * t;
	precise float g = G_F(H_LOW + 1) + (G_F(H_HIGH + 1) - G_F(H_LOW + 1)) * t;
	precise float bl = G_F(H_LOW + 2) + (G_F(H_HIGH + 2) - G_F(H_LOW + 2)) * t;
	return vec4(r, g, bl, 1.0);
}

// TryEncodeSkirtUv: one component is a lattice value k / 64 (bit exact), the other is carried as float bits.
bool lattice_index(float value, out uint k)
{
	float scaled = value * float(CELLS);
	k = 0u;
	if (!(scaled >= 0.0 && scaled <= float(CELLS))) return false;
	k = uint(roundEven(scaled));
	return k <= uint(CELLS) && floatBitsToUint(float(k) / float(CELLS)) == floatBitsToUint(value);
}

void encode_skirt_uv(vec2 uv, out uint zBits, out uint wBits)
{
	uint k;
	if (lattice_index(uv.x, k)) { zBits = k << 16; wBits = floatBitsToUint(uv.y); return; }
	if (lattice_index(uv.y, k)) { zBits = 4u | (k << 16); wBits = floatBitsToUint(uv.x); return; }
	zBits = 0u; wBits = 0u;
}

void main()
{
	uint gid = gl_GlobalInvocationID.x;
	touch_all();
	int s = int(gid) / (4 * CELLS);
	bool live = s < pc.count;
	vec3 vmin = vec3(INF), vmax = vec3(-INF);
	float reach = 0.0;
	if (live)
	{
		int q = int(gid) - s * 4 * CELLS;
		int edge = q / CELLS, quad = q - edge * CELLS;
		int base = H_SECTORS + s * SECTOR_WORDS;
		int hb = s * WSTRIDE;
		int spacing = G_I(H_SPACING), size = G_I(H_SIZE);
		float underlay = G_F(H_UNDERLAY), punder = G_F(H_PUNDER), depth = G_F(H_SKIRT);
		int slot = G_I(base + 2);
		float cosC = G_F(base + 8), sinC = G_F(base + 9);
		float half_ = float(size) * 0.5;
		// Lattice endpoints of this skirt quad: edge 0 = first around edge, 1 = last around edge, 2 = first across edge, 3 = last across edge.
		int xa, za, xb, zb;
		if (edge == 0) { xa = 0; xb = 0; za = quad; zb = quad + 1; }
		else if (edge == 1) { xa = CELLS; xb = CELLS; za = quad; zb = quad + 1; }
		else if (edge == 2) { za = 0; zb = 0; xa = quad; xb = quad + 1; }
		else { za = CELLS; zb = CELLS; xa = quad; xb = quad + 1; }
		float h0 = hwin(hb, xa, za), h1 = hwin(hb, xb, zb);
		float top0 = h0 - underlay, top1 = h1 - underlay;
		float da0 = float(xa * spacing) - half_, dz0 = float(za * spacing) - half_;
		float da1 = float(xb * spacing) - half_, dz1 = float(zb * spacing) - half_;
		vec3 p0 = ring_pos(cosC, sinC, da0, top0 - depth, dz0);
		vec3 p1 = ring_pos(cosC, sinC, da1, top1 - depth, dz1);
		vec3 p2 = ring_pos(cosC, sinC, da1, top1, dz1);
		vec3 p3 = ring_pos(cosC, sinC, da0, top0, dz0);
		precise float m0 = parent_height(hb, xa, za, false, false) - h0 + underlay - punder;
		precise float m1 = parent_height(hb, xb, zb, false, false) - h1 + underlay - punder;
		uint color = enc_color(skirt_color(max(h0, h1)));
		float fs = float(size);
		vec2 uv0 = vec2(float(xa * spacing) / fs, float(za * spacing) / fs);
		vec2 uv1 = vec2(float(xb * spacing) / fs, float(zb * spacing) / fs);
		uint z0b, w0b, z1b, w1b;
		encode_skirt_uv(uv0, z0b, w0b);
		encode_skirt_uv(uv1, z1b, w1b);
		int t = 2 * LATTICE + edge * CELLS * 4 + quad * 4;
		uint f = 1u | 2u;
		store_texel(slot, t, vec4(p0, m0), vec4(0.0), uvec4(0u, color, f | z0b, w0b));
		store_texel(slot, t + 1, vec4(p1, m1), vec4(0.0), uvec4(0u, color, f | z1b, w1b));
		store_texel(slot, t + 2, vec4(p2, m1), vec4(0.0), uvec4(0u, color, f | z1b, w1b));
		store_texel(slot, t + 3, vec4(p3, m0), vec4(0.0), uvec4(0u, color, f | z0b, w0b));
		vmin = min(min(p0, p1), min(p2, p3));
		vmax = max(max(p0, p1), max(p2, p3));
		reach = max(abs(m0), abs(m1));
	}
	vec3 rvmin = vec3(subgroupMin(vmin.x), subgroupMin(vmin.y), subgroupMin(vmin.z));
	vec3 rvmax = vec3(subgroupMax(vmax.x), subgroupMax(vmax.y), subgroupMax(vmax.z));
	float rreach = subgroupMax(reach);
	int firstSector = subgroupBroadcastFirst(live ? s : 0x7fffffff);
	bool uniform_ = subgroupAll(!live || s == firstSector);
	if (uniform_)
	{
		if (subgroupElect() && firstSector != 0x7fffffff)
		{
			int sb = firstSector * SUM_STRIDE;
			atomicMax(summary.s[sb + SUM_VIS_MIN], ~ordered(rvmin.x)); atomicMax(summary.s[sb + SUM_VIS_MIN + 1], ~ordered(rvmin.y)); atomicMax(summary.s[sb + SUM_VIS_MIN + 2], ~ordered(rvmin.z));
			atomicMax(summary.s[sb + SUM_VIS_MAX], ordered(rvmax.x)); atomicMax(summary.s[sb + SUM_VIS_MAX + 1], ordered(rvmax.y)); atomicMax(summary.s[sb + SUM_VIS_MAX + 2], ordered(rvmax.z));
			atomicMax(summary.s[sb + SUM_MESH_MIN], ~ordered(rvmin.x)); atomicMax(summary.s[sb + SUM_MESH_MIN + 1], ~ordered(rvmin.y)); atomicMax(summary.s[sb + SUM_MESH_MIN + 2], ~ordered(rvmin.z));
			atomicMax(summary.s[sb + SUM_MESH_MAX], ordered(rvmax.x)); atomicMax(summary.s[sb + SUM_MESH_MAX + 1], ordered(rvmax.y)); atomicMax(summary.s[sb + SUM_MESH_MAX + 2], ordered(rvmax.z));
			atomicMax(summary.s[sb + SUM_REACH], floatBitsToUint(rreach));
		}
	}
	else if (live)
	{
		int sb = s * SUM_STRIDE;
		atomicMax(summary.s[sb + SUM_VIS_MIN], ~ordered(vmin.x)); atomicMax(summary.s[sb + SUM_VIS_MIN + 1], ~ordered(vmin.y)); atomicMax(summary.s[sb + SUM_VIS_MIN + 2], ~ordered(vmin.z));
		atomicMax(summary.s[sb + SUM_VIS_MAX], ordered(vmax.x)); atomicMax(summary.s[sb + SUM_VIS_MAX + 1], ordered(vmax.y)); atomicMax(summary.s[sb + SUM_VIS_MAX + 2], ordered(vmax.z));
		atomicMax(summary.s[sb + SUM_MESH_MIN], ~ordered(vmin.x)); atomicMax(summary.s[sb + SUM_MESH_MIN + 1], ~ordered(vmin.y)); atomicMax(summary.s[sb + SUM_MESH_MIN + 2], ~ordered(vmin.z));
		atomicMax(summary.s[sb + SUM_MESH_MAX], ordered(vmax.x)); atomicMax(summary.s[sb + SUM_MESH_MAX + 1], ordered(vmax.y)); atomicMax(summary.s[sb + SUM_MESH_MAX + 2], ordered(vmax.z));
		atomicMax(summary.s[sb + SUM_REACH], floatBitsToUint(reach));
	}
}
#endif
