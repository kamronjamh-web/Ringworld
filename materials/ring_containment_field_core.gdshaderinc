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
// Hexagonal lattice (user decision 2026-11-05, overriding decision 6's "no grid"; cells 100 m, "medium"): pointy-top cells with a centre spacing of
// L / 460 = 100.17 m along around, laid out in the same surface coordinates, so the cells stay regular over the curve and the skirt and never swim.
// Origin from ContainmentFieldNearPatch.LatticeOrigin (no drift).
//
// Glass (user decision 2026-11-05 "a glass looking field with honeycomb pattern etched look", superseding "no fresnel rim, nothing added"): the panes
// keep the refraction shimmer and add a Fresnel (Schlick, n = 1.5) reflection of the real light: the sky / habitat radiance in the reflected
// direction and a small GGX glint of the sun. The etched honeycomb is frosted glass: it scatters the incident sun and ambient light diffusely
// (Lambert over both sides) with a slight sparkle under a low grazing sun. Every light term comes from the published lighting (sun irradiance,
// sky and ambient radiance, the local day, storm ambient and megastorm darkness K; RingContainmentField.UpdateLighting), times the haze
// transmittance and the shared fades: nothing self-glows, and at night only the night sky light remains.

const float CF_NOISE_CELL_1 = 180.0;
const float CF_NOISE_CELL_2 = 45.0;
const int CF_NOISE_PERIOD_1 = 256;
const int CF_NOISE_PERIOD_2 = 1024;
const float CF_LATTICE_SPACING = 100.17391304;  // m, ContainmentFieldNearPatch.LatticeSpacingMeters = 46,080 / 460
const float CF_ETCH_HALF_WIDTH = 0.6;           // m, half width of the etched groove
const float CF_FROST_ALBEDO = 0.3;              // diffuse albedo of the frosted groove (thin, pale milky lines)
const float CF_FROST_TRANSMISSION_LOSS = 0.3;   // share of the background the groove scatters away (it is frosted, not clear)
const float CF_GLASS_F0 = 0.04;                 // Schlick F0 for n = 1.5
const float CF_GLASS_ROUGHNESS = 0.08;          // a clean pane: a small sun glint
const float CF_GLASS_GLINT = 0.3;               // glint share: a thin coated pane, not a mirror
const float CF_GLASS_ENV = 0.4;                 // sky / habitat reflection share (subtle: a clean pane, not a mirror)
const float CF_PI = 3.14159265;

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

// Etched groove mask (0..~1): a soft frosted core with a slight inner bevel (the groove wall catches a little more light), anti-aliased with the
// screen footprint (pixel_m = metres per pixel from fwidth of the lattice coordinates) and energy preserving (a groove narrower than a pixel is
// drawn one pixel wide and dimmer). Before the cells shrink to a few pixels the etch fades out, so it never turns into moire or shimmer.
float cf_etch_mask(vec2 p, float pixel_m) {
	float e = cf_hex_edge_distance(p);
	float fw = max(pixel_m, 0.001);
	float w = CF_ETCH_HALF_WIDTH;
	float drawn = max(w, fw);
	float core = 1.0 - smoothstep(0.45 * drawn, drawn + fw, e);
	float bevel_width = 0.35 * w + fw;
	float bevel = exp(-((e - drawn) / bevel_width) * ((e - drawn) / bevel_width)) * 0.4;
	float energy = w / drawn;
	float cell_fade = 1.0 - smoothstep(CF_LATTICE_SPACING / 16.0, CF_LATTICE_SPACING / 6.0, fw);
	return clamp(core * 0.85 + bevel, 0.0, 1.0) * energy * cell_fade;
}

// GGX normal distribution and height-correlated Smith visibility (alpha = roughness^2).
float cf_ggx_specular(float nh, float nl, float nv, float roughness) {
	float a = roughness * roughness;
	float a2 = a * a;
	float d = nh * nh * (a2 - 1.0) + 1.0;
	float dist = a2 / (CF_PI * d * d);
	float vis = 0.5 / max(nl * sqrt(nv * nv * (1.0 - a2) + a2) + nv * sqrt(nl * nl * (1.0 - a2) + a2), 0.00001);
	return dist * vis;
}

float cf_fresnel(float c) {
	float g = 1.0 - clamp(c, 0.0, 1.0);
	float g2 = g * g;
	return CF_GLASS_F0 + (1.0 - CF_GLASS_F0) * g2 * g2 * g;
}

// The lit pane (radiance, not premultiplied). All directions in one space (both passes use view space): n = field normal (either side), v = toward
// the camera, l = toward the sun, up = local zenith. Light inputs (linear, the units of lit surfaces): sun = sun irradiance at the field (day, eclipse
// and K applied), sky = sky radiance (the reflection looking up), ground = habitat radiance (the reflection looking down), ambient = ambient radiance
// for the frosted groove. glass = glass strength x the shared fades, etch_w = etch strength x the shared fades, etch = cf_etch_mask, boost = the WO4
// hook (ripples and strikes brighten the etch near them; the near patch passes 0), haze = the haze transmittance to the fragment.
vec3 cf_glass_shade(vec3 refracted, vec3 n, vec3 v, vec3 l, vec3 up, vec3 sun, vec3 sky, vec3 ground, vec3 ambient,
		float glass, float etch_w, float etch, float boost, float haze) {
	vec3 nf = dot(n, v) < 0.0 ? -n : n;
	float nv = clamp(dot(nf, v), 0.0, 1.0);
	float fresnel = cf_fresnel(nv) * clamp(glass, 0.0, 2.0);
	vec3 r = reflect(-v, nf);
	float down = smoothstep(0.1, -0.3, dot(r, up));
	vec3 reflected = mix(sky, ground, down) * (fresnel * CF_GLASS_ENV);
	float nl = dot(nf, l);
	if (nl > 0.0) {
		vec3 h = normalize(l + v);
		float spec = cf_ggx_specular(max(dot(nf, h), 0.0), nl, max(nv, 0.001), CF_GLASS_ROUGHNESS) * cf_fresnel(dot(h, v));
		reflected += sun * (spec * nl * clamp(glass, 0.0, 2.0) * CF_GLASS_GLINT);
	}
	float frost = clamp(etch * etch_w * (1.0 + max(boost, 0.0)), 0.0, 2.0);
	float anl = abs(nl);
	float sparkle = (1.0 - anl) * (1.0 - anl) * (1.0 - anl) * smoothstep(0.0, 0.08, anl);
	vec3 scattered = frost * CF_FROST_ALBEDO * (sun * (anl + 0.6 * sparkle) / CF_PI + ambient);
	vec3 transmitted = refracted * (1.0 - min(fresnel * CF_GLASS_ENV, 1.0)) * (1.0 - CF_FROST_TRANSMISSION_LOSS * min(frost, 1.0));
	return transmitted + (reflected + scattered) * haze;
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

// Coverage alpha from the weights (premultiplied blending: out = refracted * gain * a + dst * (1 - a)).
float cf_alpha(float refraction_weight, float glass_weight, float etch_weight) {
	return clamp(4.0 * max(max(refraction_weight, glass_weight), etch_weight), 0.0, 1.0);
}

// Refraction offset in pixels from the slope and the view-space tangents (image y runs down, view y up). Its length is at most max_px * weight.
vec2 cf_offset_px(vec2 slope, vec3 tangent_around_view, vec3 tangent_arch_view, float weight, float max_px) {
	vec3 d = slope.x * tangent_around_view + slope.y * tangent_arch_view;
	vec2 o = vec2(d.x, -d.y);
	float len = length(o);
	if (len > 1.0) o /= len;
	return o * (max_px * weight);
}

// Debug colours (cf_debug_mode 1: patch coverage tint, 2: distance heat, 3: etch mask x etch weight).
vec3 cf_debug_colour(int mode, float alpha, float dist, float range_m, float lattice) {
	if (mode == 1) return vec3(1.0, 0.0, 1.0) * alpha;
	if (mode == 3) return vec3(lattice);
	float t = clamp(dist / max(range_m, 1.0), 0.0, 1.0);
	return mix(vec3(1.0, 0.1, 0.0), vec3(0.0, 0.2, 1.0), t);
}

#endif
