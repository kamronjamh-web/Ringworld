#ifndef RING_CONTAINMENT_FIELD_CORE_GDSHADERINC
#define RING_CONTAINMENT_FIELD_CORE_GDSHADERINC

// Containment field near patch: the per-fragment maths shared by the spatial shader (materials/ring_containment_field.gdshader, the
// transparent pass, camera below the field) and the post-Sunshine raster pass (scripts/render/ring_containment_field_post.glsl, camera
// above the field, docs/containment-field-design.md 3.4, 3.7 and the 2026-11-04 decisions 6-7). Both passes draw the same CPU-built patch
// (ContainmentFieldNearPatch.Build) with the same attributes, so a camera crossing the sheet sees the same shimmer from either pass.
//
// Written in the subset of GLSL that both Godot's shading language and GLSL 450 accept (explicit float literals, no implicit int to float
// conversion, uint hashes with u-suffixed literals).
//
// Noise domain: patch UV (metres along around x NoiseAroundScale, metres along the arch, relative to the patch anchor) plus a per-octave origin
// that the CPU computes in double ((anchor + drift t) mod L, ContainmentFieldNearPatch.NoiseOrigins). The lattice is periodic with L = 46,080 m
// (256 cells of 180 m, 1024 cells of 45 m) and the around axis is scaled so the circumference is a whole number of periods: the pattern is
// continuous across patch rebuilds and across around 0.
//
// Hexagonal lattice (user decision 2026-11-05, overriding decision 6's "no grid"): pointy-top cells with a 1,024 m centre spacing along around
// (L = 45 spacings) laid out in the same surface coordinates, so the cells stay regular over the curve and the skirt and never swim. Origin from
// ContainmentFieldNearPatch.LatticeOrigin (no drift). Thin anti-aliased edges plus a faint halo, multiplied into the refracted colour like the sheen.

const float CF_NOISE_CELL_1 = 180.0;
const float CF_NOISE_CELL_2 = 45.0;
const int CF_NOISE_PERIOD_1 = 256;
const int CF_NOISE_PERIOD_2 = 1024;
const vec3 CF_SHEEN_TINT = vec3(0.55, 0.65, 1.0);
const float CF_SHEEN_MAX = 0.035;   // at most about 3.5 % over the background (blue channel) at sheen strength 1
const float CF_LATTICE_SPACING = 1024.0;   // m, ContainmentFieldNearPatch.LatticeSpacingMeters
const float CF_LATTICE_LINE = 8.0;         // m, true edge line width
const float CF_LATTICE_HALO = 90.0;        // m, e-folding of the faint brightening toward an edge
const float CF_LATTICE_MAX = 0.06;         // edge line at lattice strength 1: +6 % of the background (blue; +3.3 % red), about 3 % of the displayed value

uint cf_hash2(int x, int y) {
	uint h = uint(x) * 0x8DA6B343u ^ uint(y) * 0xD8163841u;
	h ^= h >> 16u;
	h *= 0x7FEB352Du;
	h ^= h >> 15u;
	h *= 0x846CA68Bu;
	h ^= h >> 16u;
	return h;
}

int cf_wrap(int c, int period) {
	int m = c % period;
	return m < 0 ? m + period : m;
}

vec2 cf_gradient(int x, int y, int period) {
	uint h = cf_hash2(cf_wrap(x, period), cf_wrap(y, period));
	float a = float(h & 0xFFFFu) * (6.28318530718 / 65536.0);
	return vec2(cos(a), sin(a));
}

// Periodic gradient noise with analytic derivatives: x value, yz d/dp (per cell).
vec3 cf_gradient_noise(vec2 p, int period) {
	vec2 i = floor(p);
	vec2 f = p - i;
	vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
	vec2 du = 30.0 * f * f * (f * (f - 2.0) + 1.0);
	int ix = int(i.x);
	int iy = int(i.y);
	vec2 ga = cf_gradient(ix, iy, period);
	vec2 gb = cf_gradient(ix + 1, iy, period);
	vec2 gc = cf_gradient(ix, iy + 1, period);
	vec2 gd = cf_gradient(ix + 1, iy + 1, period);
	float va = dot(ga, f);
	float vb = dot(gb, f - vec2(1.0, 0.0));
	float vc = dot(gc, f - vec2(0.0, 1.0));
	float vd = dot(gd, f - vec2(1.0, 1.0));
	float k = va - vb - vc + vd;
	float value = va + u.x * (vb - va) + u.y * (vc - va) + u.x * u.y * k;
	vec2 deriv = ga + u.x * (gb - ga) + u.y * (gc - ga) + u.x * u.y * (ga - gb - gc + gd)
		+ du * (vec2(vb - va, vc - va) + u.yx * k);
	return vec3(value, deriv);
}

// Shimmer slope in the surface tangent plane (x along around, y along the arch), dimensionless, length typically 0.3-1.2.
// origins = (octave 1 origin xy, octave 2 origin xy) in metres.
vec2 cf_shimmer_slope(vec2 uv, vec4 origins) {
	vec3 n1 = cf_gradient_noise((uv + origins.xy) / CF_NOISE_CELL_1, CF_NOISE_PERIOD_1);
	vec3 n2 = cf_gradient_noise((uv + origins.zw) / CF_NOISE_CELL_2, CF_NOISE_PERIOD_2);
	return n1.yz * 0.65 + n2.yz * 0.35;
}

// Distance (m) from p (lattice frame, m: x along around, y along the arch) to the nearest hex edge.
float cf_hex_edge_distance(vec2 p) {
	vec2 q = p / CF_LATTICE_SPACING;
	vec2 s = vec2(1.0, 1.7320508);
	vec2 ca = floor(q / s) + 0.5;
	vec2 cb = floor((q - vec2(0.5, 0.8660254)) / s) + 0.5;
	vec2 ha = q - ca * s;
	vec2 hb = q - (cb * s + vec2(0.5, 0.8660254));
	vec2 h = dot(ha, ha) < dot(hb, hb) ? ha : hb;
	vec2 ah = abs(h);
	float hex = max(ah.x, dot(ah, vec2(0.5, 0.8660254)));
	return max(0.5 - hex, 0.0) * CF_LATTICE_SPACING;
}

// Lattice mask: the edge line, anti-aliased with the screen footprint (pixel_m = metres per pixel from fwidth of the lattice coordinates) and
// energy preserving (a line narrower than a pixel is drawn one pixel wide and dimmer, so it neither sparkles nor vanishes at distance), plus a
// faint halo so the edges are slightly brighter than the cell interior. About 0..1.2.
float cf_lattice_mask(vec2 p, float pixel_m) {
	float e = cf_hex_edge_distance(p);
	float fw = max(pixel_m, 0.001);
	float half_w = max(0.5 * CF_LATTICE_LINE, 0.75 * fw);
	float line = (1.0 - smoothstep(half_w, half_w + fw, e)) * clamp(0.5 * CF_LATTICE_LINE / half_w, 0.0, 1.0);
	float halo = exp(-e / CF_LATTICE_HALO) * clamp(CF_LATTICE_HALO / (4.0 * fw), 0.0, 1.0);
	return line + 0.2 * halo;
}

// The lattice as a multiplier on the refracted colour: zero against black, never adds light. lattice_weight = strength x the shared fades.
// boost is the WO4 hook: ripples and field-strike crawls light the cells near them by passing a boost > 0 (the near patch passes 0).
vec3 cf_lattice_gain(float lattice_weight, float mask, float boost) {
	return vec3(1.0) + CF_LATTICE_MAX * lattice_weight * mask * (1.0 + max(boost, 0.0)) * CF_SHEEN_TINT;
}

// Distance weight: 1 near the camera, exactly 0 at and beyond the shimmer range N (so the patch appears and disappears on fully transparent pixels).
float cf_distance_weight(float dist, float range_m) {
	return 1.0 - smoothstep(0.35 * range_m, range_m, dist);
}

// Edge-on sheets never produce a line.
float cf_grazing_fade(float n_dot_v) {
	return smoothstep(0.03, 0.15, n_dot_v);
}

// The field grows out of the wall top instead of starting on a line (arch length from the nearer root, m).
float cf_root_ramp(float root_distance_m) {
	return smoothstep(0.0, 250.0, root_distance_m);
}

// Soft intersection with opaque geometry: metres of clearance between the field fragment and the scene behind it.
float cf_soft_intersection(float clearance_m) {
	return smoothstep(0.0, 30.0, clearance_m);
}

// Sheen angle term: stronger toward grazing, 0 head-on (the grazing fade above still removes the edge-on band).
float cf_sheen_angle(float n_dot_v) {
	float g = 1.0 - clamp(n_dot_v, 0.0, 1.0);
	return g * g;
}

// The sheen as a multiplier on the refracted colour (decision 6): zero against black, never adds light.
vec3 cf_sheen_gain(float sheen) {
	return vec3(1.0) + CF_SHEEN_MAX * sheen * CF_SHEEN_TINT;
}

// Coverage alpha from the weights (premultiplied blending: out = refracted * gain * a + dst * (1 - a)).
float cf_alpha(float refraction_weight, float sheen_weight, float lattice_weight) {
	return clamp(4.0 * max(max(refraction_weight, sheen_weight), lattice_weight), 0.0, 1.0);
}

// Refraction offset in pixels from the slope and the view-space tangents (image y runs down, view y up). Its length is at most max_px * weight.
vec2 cf_offset_px(vec2 slope, vec3 tangent_around_view, vec3 tangent_arch_view, float weight, float max_px) {
	vec3 d = slope.x * tangent_around_view + slope.y * tangent_arch_view;
	vec2 o = vec2(d.x, -d.y);
	float len = length(o);
	if (len > 1.0) o /= len;
	return o * (max_px * weight);
}

// Debug colours (cf_debug_mode 1: patch coverage tint, 2: distance heat, 3: lattice mask x lattice weight).
vec3 cf_debug_colour(int mode, float alpha, float dist, float range_m, float lattice) {
	if (mode == 1) return vec3(1.0, 0.0, 1.0) * alpha;
	if (mode == 3) return vec3(lattice);
	float t = clamp(dist / max(range_m, 1.0), 0.0, 1.0);
	return mix(vec3(1.0, 0.1, 0.0), vec3(0.0, 0.2, 1.0), t);
}

#endif
