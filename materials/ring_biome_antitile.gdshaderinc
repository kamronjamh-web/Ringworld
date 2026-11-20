// Ground albedo anti-tiling: removes the visible 5 m repeat of the biome albedo (biome_texture_scale 0.20) on the top-down
// projection of the near Terrain3D surface, the proxy terrain and the grass-card ground colour.
//
// Method: offset-only hex tiling, after Mikkelsen 2022, "Practical Real-Time Hex-Tiling", JCGT 11(2) (code written for this
// project). The uv plane is covered by a skewed triangle lattice (a hex grid cut into triangles). Each lattice vertex gets a
// random uv OFFSET (no rotation: desert ripples and rock flakes are directional), a pixel takes the three taps of the
// vertices of its triangle at uv + offset(vertex), and blends them with sharpened barycentric weights (pow 7) so most of a
// triangle shows one tap. The taps are weighted by a mild luminance term (CONTRAST) so a blend keeps contrast instead of
// averaging to grey. A tap at a given uv + offset has the same derivatives as the plain tap, so close-up sharpness and the
// anisotropic filtering are unchanged; the repeat length of the result is the lattice, not the texture.
//
// Periodicity: the lattice constants are rational (see below) so the lattice repeats EXACTLY every 200 uv = 1000 m in u
// and in v, and the vertex hash is taken on the periodic representative of the vertex, so the pattern
// is identical for texture positions that differ by 1000 m. Callers must therefore supply uv built from canonical metres
// modulo 1000 (the Terrain3D texture anchor is already modulo 1000; the grass origin uv is, see CardTerrainOriginUv).
//
// ringworld_ground_antitile: 1 = on (shipped), 0 = a plain single tap (debug / test A/B only, RingGroundAntiTiling).
// Far away (a texture tile of 3 px or less) the mips are near uniform, so the plain tap is used.
#ifndef RING_BIOME_ANTITILE_GDSHADERINC
#define RING_BIOME_ANTITILE_GDSHADERINC

global uniform float ringworld_ground_antitile;

const float RING_ANTITILE_EXP = 7.0;          // sharpening of the barycentric weights
const float RING_ANTITILE_CONTRAST = 0.6;     // 0 = plain weights, 1 = fully luminance-proportional
const float RING_ANTITILE_MIN_WEIGHT = 0.02;  // weights below this are dropped (the tap is skipped)
const float RING_ANTITILE_MAX_FOOTPRINT = 0.33; // uv per pixel: a tile of 3 px or less takes the plain tap

// Lattice: G uv-lattice units per uv; N = 346 and M = 200 = round(N / sqrt(3)) make the skewed grid periodic in 200 uv.
const int RING_ANTITILE_N = 346;
const int RING_ANTITILE_M = 200;
const float RING_ANTITILE_G = 1.73;           // = N / 200
const float RING_ANTITILE_R = 0.5780346820809249; // = M / N

struct RingAntiTile {
	bool on;
	vec2 uv0;
	vec2 uv1;
	vec2 uv2;
	vec3 w;
};

int ring_antitile_floor_div(int a, int b) {
	int q = a / b;
	return (a % b != 0 && ((a < 0) != (b < 0))) ? q - 1 : q;
}

uint ring_antitile_pcg(uint v) {
	uint state = v * 747796405u + 2891336453u;
	uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
	return (word >> 22u) ^ word;
}

// Random offset in [0, 1)^2 of a lattice vertex, from its periodic representative (the lattice repeats under
// (N, 0) and (-M, 2M)).
vec2 ring_antitile_offset(ivec2 v) {
	int k = ring_antitile_floor_div(v.y, 2 * RING_ANTITILE_M);
	int y = v.y - 2 * RING_ANTITILE_M * k;
	int x = v.x + RING_ANTITILE_M * k;
	x -= RING_ANTITILE_N * ring_antitile_floor_div(x, RING_ANTITILE_N);
	uint h0 = ring_antitile_pcg(uint(x) ^ ring_antitile_pcg(uint(y) + 2654435769u));
	uint h1 = ring_antitile_pcg(h0 ^ 2246822519u);
	return vec2(float(h0 >> 8u), float(h1 >> 8u)) * (1.0 / 16777216.0);
}

RingAntiTile ring_antitile_setup(vec2 uv, float footprint_uv) {
	RingAntiTile a;
	a.on = ringworld_ground_antitile > 0.5 && footprint_uv < RING_ANTITILE_MAX_FOOTPRINT;
	a.uv0 = uv;
	a.uv1 = uv;
	a.uv2 = uv;
	a.w = vec3(1.0, 0.0, 0.0);
	if (!a.on) return a;
	vec2 p = uv * RING_ANTITILE_G;
	vec2 skew = vec2(p.x - RING_ANTITILE_R * p.y, 2.0 * RING_ANTITILE_R * p.y);
	vec2 base = floor(skew);
	vec2 f = skew - base;
	float tz = 1.0 - f.x - f.y;
	float s = step(0.0, -tz);
	float s2 = 2.0 * s - 1.0;
	vec3 bary = vec3(-tz * s2, s - f.y * s2, s - f.x * s2);
	ivec2 b = ivec2(base);
	int si = int(s);
	a.uv0 = uv + ring_antitile_offset(b + ivec2(si, si));
	a.uv1 = uv + ring_antitile_offset(b + ivec2(si, 1 - si));
	a.uv2 = uv + ring_antitile_offset(b + ivec2(1 - si, si));
	vec3 w = pow(max(bary, vec3(0.0)), vec3(RING_ANTITILE_EXP));
	w /= max(w.x + w.y + w.z, 1e-8);
	a.w = vec3(w.x < RING_ANTITILE_MIN_WEIGHT ? 0.0 : w.x, w.y < RING_ANTITILE_MIN_WEIGHT ? 0.0 : w.y, w.z < RING_ANTITILE_MIN_WEIGHT ? 0.0 : w.z);
	return a;
}

vec3 ring_antitile_combine(vec3 c0, vec3 c1, vec3 c2, vec3 w) {
	const vec3 LUMA = vec3(0.299, 0.587, 0.114);
	vec3 weights = w * mix(vec3(1.0), vec3(dot(c0, LUMA), dot(c1, LUMA), dot(c2, LUMA)), RING_ANTITILE_CONTRAST);
	weights /= max(weights.x + weights.y + weights.z, 1e-8);
	return c0 * weights.x + c1 * weights.y + c2 * weights.z;
}

// Biome albedo with explicit derivatives (safe in non-uniform control flow).
vec3 ring_antitile_sample_grad(sampler2D t, RingAntiTile a, vec2 dx, vec2 dy) {
	if (!a.on) return textureGrad(t, a.uv0, dx, dy).rgb;
	vec3 c0 = vec3(0.0), c1 = vec3(0.0), c2 = vec3(0.0);
	if (a.w.x > 0.0) c0 = textureGrad(t, a.uv0, dx, dy).rgb;
	if (a.w.y > 0.0) c1 = textureGrad(t, a.uv1, dx, dy).rgb;
	if (a.w.z > 0.0) c2 = textureGrad(t, a.uv2, dx, dy).rgb;
	return ring_antitile_combine(c0, c1, c2, a.w);
}

// Same at an explicit mip level (grass cards).
vec3 ring_antitile_sample_lod(sampler2D t, RingAntiTile a, float lod) {
	if (!a.on) return textureLod(t, a.uv0, lod).rgb;
	vec3 c0 = vec3(0.0), c1 = vec3(0.0), c2 = vec3(0.0);
	if (a.w.x > 0.0) c0 = textureLod(t, a.uv0, lod).rgb;
	if (a.w.y > 0.0) c1 = textureLod(t, a.uv1, lod).rgb;
	if (a.w.z > 0.0) c2 = textureLod(t, a.uv2, lod).rgb;
	return ring_antitile_combine(c0, c1, c2, a.w);
}

// Detail normal map (rg): the taps are blended by the lattice weights only (no luminance term), renormalised over the kept taps.
vec2 ring_antitile_sample_grad_rg(sampler2D t, RingAntiTile a, vec2 dx, vec2 dy) {
	if (!a.on) return textureGrad(t, a.uv0, dx, dy).rg;
	vec2 sum = vec2(0.0);
	if (a.w.x > 0.0) sum += textureGrad(t, a.uv0, dx, dy).rg * a.w.x;
	if (a.w.y > 0.0) sum += textureGrad(t, a.uv1, dx, dy).rg * a.w.y;
	if (a.w.z > 0.0) sum += textureGrad(t, a.uv2, dx, dy).rg * a.w.z;
	return sum / max(a.w.x + a.w.y + a.w.z, 1e-8);
}

RingAntiTile ring_antitile_off(vec2 uv) {
	RingAntiTile a;
	a.on = false;
	a.uv0 = uv;
	a.uv1 = uv;
	a.uv2 = uv;
	a.w = vec3(1.0, 0.0, 0.0);
	return a;
}

// Top-down uv of the triplanar (with its flip) and the lattice for it. Call in uniform control flow: the derivatives are
// implicit. dpdx / dpdy are dFdx / dFdy of the (scaled) texture position.
RingAntiTile ring_biome_antitile(vec3 position, vec3 normal, vec3 dpdx, vec3 dpdy) {
	vec2 y_uv = position.xz;
	y_uv.x *= normal.y < 0.0 ? -1.0 : 1.0;
	return ring_antitile_setup(y_uv, max(length(dpdx.xz), length(dpdy.xz)));
}

// The proxy terrain textures in WORLD space, where the ring axis is world y and the ground is a vertical surface: its ground is the x or
// z projection (whichever the radial direction favours), so every projection that carries weight gets a lattice. Same flips as the plain triplanar.
RingAntiTile ring_biome_antitile_x(vec3 position, vec3 normal, float blend_x, vec3 dpdx, vec3 dpdy) {
	vec2 uv = position.zy;
	uv.x *= normal.x < 0.0 ? -1.0 : 1.0;
	return blend_x > 0.0 ? ring_antitile_setup(uv, max(length(dpdx.zy), length(dpdy.zy))) : ring_antitile_off(uv);
}

RingAntiTile ring_biome_antitile_y(vec3 position, vec3 normal, float blend_y, vec3 dpdx, vec3 dpdy) {
	return blend_y > 0.0 ? ring_biome_antitile(position, normal, dpdx, dpdy) : ring_antitile_off(position.xz);
}

RingAntiTile ring_biome_antitile_z(vec3 position, vec3 normal, float blend_z, vec3 dpdx, vec3 dpdy) {
	vec2 uv = position.xy;
	uv.x *= normal.z >= 0.0 ? -1.0 : 1.0;
	return blend_z > 0.0 ? ring_antitile_setup(uv, max(length(dpdx.xy), length(dpdy.xy))) : ring_antitile_off(uv);
}

// Drops negligible triplanar weights (their projections are skipped) and renormalises.
vec3 ring_triplanar_prune(vec3 blend) {
	vec3 kept = vec3(blend.x < 0.004 ? 0.0 : blend.x, blend.y < 0.004 ? 0.0 : blend.y, blend.z < 0.004 ? 0.0 : blend.z);
	return kept / max(kept.x + kept.y + kept.z, 0.0001);
}

// Triplanar biome albedo: x and z projections are one tap, the y (top-down) projection goes through the anti-tiling lattice
// `ty` (ring_biome_antitile). blend is expected pruned (ring_triplanar_prune). Explicit derivatives: callable in branches.
vec3 ring_biome_triplanar(sampler2D t, vec3 position, vec3 normal, vec3 blend, RingAntiTile ty, vec3 dpdx, vec3 dpdy) {
	vec3 result = vec3(0.0);
	if (blend.x > 0.0) {
		float flip = normal.x < 0.0 ? -1.0 : 1.0;
		vec2 uv = position.zy;
		vec2 dx = dpdx.zy;
		vec2 dy = dpdy.zy;
		uv.x *= flip; dx.x *= flip; dy.x *= flip;
		result += textureGrad(t, uv, dx, dy).rgb * blend.x;
	}
	if (blend.y > 0.0) {
		float flip = normal.y < 0.0 ? -1.0 : 1.0;
		vec2 dx = dpdx.xz;
		vec2 dy = dpdy.xz;
		dx.x *= flip; dy.x *= flip;
		result += ring_antitile_sample_grad(t, ty, dx, dy) * blend.y;
	}
	if (blend.z > 0.0) {
		float flip = normal.z >= 0.0 ? -1.0 : 1.0;
		vec2 uv = position.xy;
		vec2 dx = dpdx.xy;
		vec2 dy = dpdy.xy;
		uv.x *= flip; dx.x *= flip; dy.x *= flip;
		result += textureGrad(t, uv, dx, dy).rgb * blend.z;
	}
	return result;
}

// Triplanar with a lattice on every projection (proxy terrain, world space). blend is expected pruned; tx / ty / tz from ring_biome_antitile_x / _y / _z.
vec3 ring_biome_triplanar_world(sampler2D t, vec3 normal, vec3 blend, RingAntiTile tx, RingAntiTile ty, RingAntiTile tz, vec3 dpdx, vec3 dpdy) {
	vec3 result = vec3(0.0);
	if (blend.x > 0.0) {
		float flip = normal.x < 0.0 ? -1.0 : 1.0;
		vec2 dx = dpdx.zy;
		vec2 dy = dpdy.zy;
		dx.x *= flip; dy.x *= flip;
		result += ring_antitile_sample_grad(t, tx, dx, dy) * blend.x;
	}
	if (blend.y > 0.0) {
		float flip = normal.y < 0.0 ? -1.0 : 1.0;
		vec2 dx = dpdx.xz;
		vec2 dy = dpdy.xz;
		dx.x *= flip; dy.x *= flip;
		result += ring_antitile_sample_grad(t, ty, dx, dy) * blend.y;
	}
	if (blend.z > 0.0) {
		float flip = normal.z >= 0.0 ? -1.0 : 1.0;
		vec2 dx = dpdx.xy;
		vec2 dy = dpdy.xy;
		dx.x *= flip; dy.x *= flip;
		result += ring_antitile_sample_grad(t, tz, dx, dy) * blend.z;
	}
	return result;
}

#endif
