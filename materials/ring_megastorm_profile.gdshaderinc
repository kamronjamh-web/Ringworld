#ifndef RING_MEGASTORM_PROFILE_GDSHADERINC
#define RING_MEGASTORM_PROFILE_GDSHADERINC

// Megastorm strip column (docs/megastorm-design.md 2.1, 2.2, 3.2; WO4). Texture-free and shared: the Sunshine strip build
// (addons/SunshineClouds2/RingMegastormStripBuild.glsl) evaluates it per texel of the moving-frame strip, the far wedge pass (WO5) per
// sample. Its C# twin is MegastormProfile.Column (scripts/Weather/Megastorm/MegastormModel.cs): the same maths, constant for constant,
// so a change here is a change there (megastorm_strip_gpu_test compares the two at 64 points).
//
// Written in the subset of GLSL that both Godot's shading language and GLSL 450 accept (explicit float literals, no implicit int to
// float conversion, uint hashes with u-suffixed literals).
//
// Inputs, all evaluated by the CPU in double at one weather-clock time and passed as floats:
//   x        reference-line distance (m), dir * wrap(s(t) - around): negative ahead of the line, positive behind it
//   across   canonical across (m), 0 .. W
//   p.a = (t weather-clock seconds, level (formation x stop fade), strength (level x breathing), seed)
//   p.b = (W ring width m, bow m, meander amplitude m, meander wavelength m)
//   p.c = (meander phase rad (2 pi Hash01(seed, 7, 0) + rate t / 3600, wrapped), ceiling m above the floor (18 km or the field), 0, 0)
// Output (heights in metres above the ring floor, 0 when absent; aux 0..1):
//   heights = (body base, body top, canopy underside, canopy top)
//   aux     = (canopy coverage, mammatus, tower coverage, body coverage)   (each x strength)
// The C# record also carries the rain shaft; the strip has no use for it near the camera, so it is not in aux (WO5 calls
// ring_megastorm_rain_shaft directly).

struct RingMegastormParams {
	vec4 a;
	vec4 b;
	vec4 c;
};

// Zone and shape constants (MegastormModel / MegastormProfile).
const float RING_MS_WALL_END_XI = 25000.0;
const float RING_MS_TRANSITION_END_XI = 50000.0;
const float RING_MS_SHELF_END_XI = 1000.0;
const float RING_MS_ANVIL_START_XI = -60000.0;
const float RING_MS_FORMING_TOP = 6000.0;
const float RING_MS_FULL_TOP = 16500.0;
const float RING_MS_TOWER_SCALE = 11500.0;
const float RING_MS_SHAFT_SCALE = 4000.0;
const float RING_MS_MAMMATUS_SCALE = 2000.0;

float ring_ms_smooth(float v) {
	float t = clamp(v, 0.0, 1.0);
	return t * t * (3.0 - 2.0 * t);
}

// MegastormModel.Hash(a, b, c) with c a signed 64-bit integer that fits in 32 bits: (uint)c is the low word, (uint)(c >> 32) the sign word.
uint ring_ms_hash(int a, int b, int c) {
	uint hi = c < 0 ? 0xFFFFFFFFu : 0u;
	uint h = uint(a) * 0x9E3779B1u ^ uint(b) * 0x85EBCA6Bu ^ uint(c) * 0xC2B2AE35u ^ hi * 0x27D4EB2Fu;
	h ^= h >> 16u;
	h *= 0x7FEB352Du;
	h ^= h >> 15u;
	h *= 0x846CA68Bu;
	h ^= h >> 16u;
	return h;
}

float ring_ms_hash01(int a, int b, int c) {
	return float(ring_ms_hash(a, b, c) >> 8u) / 16777216.0;
}

// MegastormProfile.Cell: node i of a 1D value noise whose value re-seeds on a hashed cycle (cMin + cSpan h seconds), crossfading over
// the last 20% of each cycle.
float ring_ms_cell(int seed, int salt, int i, float t, float cMin, float cSpan) {
	float cycle = cMin + cSpan * ring_ms_hash01(seed, salt, i);
	float u = t / cycle + ring_ms_hash01(seed, salt + 100, i);
	float e = floor(u);
	float fade = ring_ms_smooth((u - e - 0.8) / 0.2);
	int ek = int(e);
	int h = i * 31 + salt;
	return ring_ms_hash01(seed, h, ek) * (1.0 - fade) + ring_ms_hash01(seed, h, ek + 1) * fade;
}

// MegastormProfile.Noise.
float ring_ms_noise(int seed, int salt, float coord, float t, float cMin, float cSpan) {
	float fl = floor(coord);
	int i = int(fl);
	float f = ring_ms_smooth(coord - fl);
	return ring_ms_cell(seed, salt, i, t, cMin, cSpan) * (1.0 - f) + ring_ms_cell(seed, salt, i + 1, t, cMin, cSpan) * f;
}

// x_lead(across, t) = -Bow (1 - u^2) - Meander (MegastormModel.LeadOffset).
float ring_megastorm_lead_offset(float across, RingMegastormParams p) {
	float u = 2.0 * across / max(p.b.x, 1.0) - 1.0;
	float meander = p.b.z * sin(6.28318530718 * across / max(p.b.w, 1.0) + p.c.x);
	return -p.b.y * (1.0 - u * u) - meander;
}

// Rain shaft 0..1 (x strength) at the column (MegastormColumn.RainShaft); WO5 reads it, the strip does not store it.
float ring_megastorm_rain_shaft(float x, float across, RingMegastormParams p) {
	float xi = x - ring_megastorm_lead_offset(across, p);
	float wallCov = ring_ms_smooth((xi + 300.0) / 800.0)
		* (1.0 - ring_ms_smooth((xi - RING_MS_WALL_END_XI) / (RING_MS_TRANSITION_END_XI - RING_MS_WALL_END_XI)));
	int seed = int(round(p.a.w));
	return wallCov * (0.6 + 0.4 * ring_ms_noise(seed, 12, across / RING_MS_SHAFT_SCALE, p.a.x, 900.0, 900.0)) * p.a.z;
}

// The strip column at (x, across). MegastormProfile.Column, line for line.
void ring_megastorm_column(float x, float across, RingMegastormParams p, out vec4 heights, out vec4 aux) {
	heights = vec4(0.0);
	aux = vec4(0.0);
	float t = p.a.x;
	float level = p.a.y;
	float strength = p.a.z;
	int seed = int(round(p.a.w));
	float width = p.b.x;
	float xi = x - ring_megastorm_lead_offset(across, p);
	float ceilingH = min(18000.0, p.c.y);

	// Wall body: feathered front edge, full to 25 km, fading out over the transition zone. Towers merge into a buttress near the rims.
	float wallCov = ring_ms_smooth((xi + 300.0) / 800.0)
		* (1.0 - ring_ms_smooth((xi - RING_MS_WALL_END_XI) / (RING_MS_TRANSITION_END_XI - RING_MS_WALL_END_XI)));
	float rim = min(across, width - across);
	float merge = ring_ms_smooth((15000.0 - rim) / 15000.0);
	float tower = max(ring_ms_noise(seed, 11, across / RING_MS_TOWER_SCALE, t, 1200.0, 1200.0), merge);
	float dome = tower > 0.8 ? (tower - 0.8) * 5.0 * 1500.0 * (1.0 - merge) : 0.0;
	float wallTop = 15000.0 + 2000.0 * tower + dome;
	float transition = ring_ms_smooth((xi - RING_MS_WALL_END_XI) / (RING_MS_TRANSITION_END_XI - RING_MS_WALL_END_XI));
	wallTop += (8500.0 - wallTop) * transition;
	wallTop = RING_MS_FORMING_TOP + (wallTop - RING_MS_FORMING_TOP) * level;
	float baseH = xi < RING_MS_SHELF_END_XI ? 300.0 : 300.0 + 200.0 * ring_ms_smooth((xi - RING_MS_SHELF_END_XI) / 2000.0);
	float bodyTop = min(wallTop, ceilingH);

	// Leading anvil canopy: underside 9 km rising to 12 km ahead, top 16.5 km (formation-scaled), fading behind the wall.
	float canopyCov = ring_ms_smooth((xi - RING_MS_ANVIL_START_XI) / 10000.0) * (1.0 - ring_ms_smooth((xi - RING_MS_WALL_END_XI) / 20000.0));
	float under = 9000.0 + 3000.0 * ring_ms_smooth(xi / RING_MS_ANVIL_START_XI);
	float canopyTop = min(RING_MS_FORMING_TOP + (RING_MS_FULL_TOP - RING_MS_FORMING_TOP) * level, ceilingH);
	float mamm = ring_ms_smooth((xi + 25000.0) / 5000.0) * (1.0 - ring_ms_smooth((xi + 8000.0) / 5000.0))
		* (0.5 + 0.5 * ring_ms_noise(seed, 13, across / RING_MS_MAMMATUS_SCALE, t, 600.0, 600.0));

	if (wallCov > 0.0) {
		heights.x = baseH;
		heights.y = bodyTop;
	}
	if (canopyCov > 0.0) {
		heights.z = min(under, canopyTop);
		heights.w = canopyTop;
	}
	aux = vec4(canopyCov, mamm, wallCov * tower, wallCov) * strength;
}

#endif
