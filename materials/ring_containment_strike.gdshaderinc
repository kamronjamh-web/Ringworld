#ifndef RING_CONTAINMENT_STRIKE_GDSHADERINC
#define RING_CONTAINMENT_STRIKE_GDSHADERINC

// Containment field strike light (docs/containment-field-design.md 3.10.2, WO4): the crawl (filaments spreading across the field from the contact)
// and the contact glow. Pure functions, no uniforms, so the ripple patches (materials/ring_containment_ripple.gdshader), the near patch, the
// post-Sunshine passes (GLSL 450) and the far strike layer (materials/ring_containment_strike_far.gdshader) share them. Written in the subset of
// GLSL that both Godot's shading language and GLSL 450 accept.
//
// Coordinates: d = surface metres from the contact, x along around, y along the arch (increasing arch length, toward the across-W root).
// crawl = (Rc m, crawl front r_f m (CPU: Rc (1 - exp(-t / 0.12 s)), 0.24 s with reduced flashes), seed 0..65535, rim bias: sign = the nearer root's
// arch direction (+1 = increasing arch), magnitude 0..1 = lean strength). Shapes are fixed per seed; only the front and the level animate (TAA-stable).
// Every caller multiplies the result by the strike level E x flash (the source pulse, reduced-flash aware), so nothing glows without a lit pulse.

const vec3 CFS_COLOUR = vec3(0.75, 0.80, 1.0);   // violet-white, the megastorm's terminal spray
const float CFS_FILAMENT_MIN_WIDTH = 25.0;       // m
const float CFS_FRONT_SOFT = 200.0;              // m, the crawl front's soft edge
const float CFS_WIGGLE = 0.35;                   // rad, angular wiggle amplitude at 1 km
const float CFS_LEAN = 0.43633231;               // rad, 25 degrees: the most a branch leans toward the nearer root

uint cfs_hash(uint x) {
	x ^= x >> 16u;
	x *= 0x7FEB352Du;
	x ^= x >> 15u;
	x *= 0x846CA68Bu;
	x ^= x >> 16u;
	return x;
}

float cfs_unit(uint seed, uint i) {
	return float(cfs_hash(seed * 0x9E3779B9u + i * 0x85EBCA6Bu + 0x5F1Eu) & 0xFFFFu) * (1.0 / 65535.0);
}

// 1D gradient noise in about -0.5..0.5 (x >= 0).
float cfs_noise1(float x, uint seed) {
	float i = floor(x);
	float f = x - i;
	uint k = uint(i);
	float g0 = cfs_unit(seed, k) * 2.0 - 1.0;
	float g1 = cfs_unit(seed, k + 1u) * 2.0 - 1.0;
	float u = f * f * (3.0 - 2.0 * f);
	return mix(g0 * f, g1 * (f - 1.0), u);
}

float cfs_wrap_angle(float a) {
	return a - 6.2831853 * floor((a + 3.14159265) / 6.2831853);
}

// One filament segment profile: Gaussian across, pixel-aware width (energy-preserving: the integral across stays 25 m).
float cfs_filament(float lateral_m, float width_m) {
	float x = lateral_m / width_m;
	return exp(-x * x) * (CFS_FILAMENT_MIN_WIDTH / width_m);
}

// x = crawl filament intensity (about 1 on a resolved filament), y = contact glow (core + halo, 1 at the contact).
// pixel_m = the screen footprint of the surface metres (fwidth), so the filaments and the core neither sparkle nor vanish far away.
vec2 cfs_strike_light(vec2 d, float pixel_m, vec4 crawl) {
	float rc = max(crawl.x, 1.0);
	float s = length(d);
	// Contact glow: a small core and a halo; the core is widened to at least a pixel and keeps its energy.
	float r_core = clamp(0.1 * rc, 150.0, 600.0);
	float r_halo = clamp(0.5 * rc, 1000.0, 3000.0);
	float core_eff = max(r_core, pixel_m);
	float halo_eff = max(r_halo, pixel_m);
	float qc = s / core_eff;
	float qh = s / halo_eff;
	float glow = exp(-qc * qc) * (r_core / core_eff) * (r_core / core_eff) + 0.25 * exp(-qh * qh) * (r_halo / halo_eff) * (r_halo / halo_eff);
	// Crawl: only inside the advancing front.
	float front = crawl.y;
	float front_mask = 1.0 - smoothstep(front - CFS_FRONT_SOFT, front, s);
	float crawl_i = 0.0;
	if (front_mask > 0.0 && s < 1.4 * rc) {
		uint seed = uint(max(crawl.z, 0.0));
		float bias = clamp(crawl.w, -1.0, 1.0);
		float root_angle = bias >= 0.0 ? 1.5707963 : -1.5707963;
		float lean = abs(bias) * CFS_LEAN;
		float width = max(CFS_FILAMENT_MIN_WIDTH, 1.25 * pixel_m);
		float theta = atan(d.y, d.x);
		int n = 5 + int(cfs_hash(seed ^ 0xB5297A4Du) % 5u);
		for (int i = 0; i < 9; i++) {
			if (i >= n) break;
			uint bi = uint(i);
			float u0 = cfs_unit(seed, bi * 8u + 1u);
			float u1 = cfs_unit(seed, bi * 8u + 2u);
			float u2 = cfs_unit(seed, bi * 8u + 3u);
			float u3 = cfs_unit(seed, bi * 8u + 4u);
			float th = 6.2831853 * (float(i) + u0) / float(n);
			// Rim bias: lean toward the nearer root's arch direction by up to 25 degrees; branches that face it are up to 1.4x longer.
			th += clamp(cfs_wrap_angle(root_angle - th), -lean, lean);
			float facing = max(cos(th - root_angle), 0.0);
			float len = rc * (0.6 + 0.4 * u1) * (1.0 + 0.4 * facing * abs(bias));
			uint bseed = seed + bi * 7919u;
			// Main branch: angular wiggle that settles with distance; lateral distance as arc length.
			if (s < len) {
				float th_s = th + CFS_WIGGLE * cfs_noise1(s / 600.0, bseed) / max(s / 1000.0, 0.5);
				float lateral = abs(cfs_wrap_angle(theta - th_s)) * s;
				float taper = 1.0 - s / len;
				crawl_i += cfs_filament(lateral, width) * taper * sqrt(taper);
			}
			// One fork at 30-60 % of the branch, 20-35 degrees off, straight, 0.7 x the remaining length.
			float fork_at = (0.3 + 0.3 * u2) * len;
			float th_f = th + CFS_WIGGLE * cfs_noise1(fork_at / 600.0, bseed) / max(fork_at / 1000.0, 0.5);
			vec2 origin = vec2(cos(th_f), sin(th_f)) * fork_at;
			float fork_angle = th_f + (u3 < 0.5 ? -1.0 : 1.0) * (0.34906585 + 0.26179939 * fract(u3 * 2.0));
			vec2 fdir = vec2(cos(fork_angle), sin(fork_angle));
			float fork_len = 0.7 * (len - fork_at);
			vec2 q = d - origin;
			float along = dot(q, fdir);
			if (along > 0.0 && along < fork_len) {
				float lateral_f = abs(q.x * fdir.y - q.y * fdir.x);
				float taper_f = 1.0 - (fork_at + along) / (fork_at + fork_len);
				crawl_i += 0.7 * cfs_filament(lateral_f, width) * taper_f * sqrt(taper_f);
			}
		}
		crawl_i *= front_mask;
	}
	return vec2(crawl_i, glow);
}

#endif
