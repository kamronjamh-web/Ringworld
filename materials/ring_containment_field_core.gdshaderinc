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
// "Glass-like, not actual glass: this is still an energy field" (user decision 2026-11-05, latest): the glass reflection is toned down to a soft sheen,
// and the clear seams carry a faint cool-cyan energy that flows slowly along the honeycomb edges (cf_seam_energy; faint self-light, never lights anything,
// dimmer at night; the WO4 boost energises it strongly). The text below describes the glass layer underneath.
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
const float CF_SEAM_HALF_WIDTH = 3.0;           // m, half width of the clear seam bead
const float CF_SEAM_TILT = 0.55;                // surface tilt at the bead's flanks (about 29 degrees): the lens and the edge highlights
const float CF_SEAM_LENS_PX = 5.0;              // background shift across the bead at full tilt (px at 1080 lines): a thick clear bead bends what is behind
const float CF_SEAM_ENV = 0.5;                  // sky / habitat sheen share on the bead (the flat pane has CF_GLASS_ENV)
const float CF_SEAM_GLINT = 0.25;               // sun sheen share on the bead
const float CF_SEAM_ROUGHNESS = 0.25;
const vec3 CF_SEAM_ENERGY_COLOUR = vec3(0.35, 0.8, 1.0);   // faint cool cyan
const float CF_SEAM_ENERGY = 0.06;              // self-light of a seam crest at etch strength 1 (linear radiance, day); faint
const float CF_SEAM_BOOST_GAIN = 4.0;           // how strongly the WO4 boost (ripples, strikes) energises the seams
const float CF_SEAM_ENERGY_MEAN = 0.044;        // cf_seam_energy averaged over a cell (crest integral x edge length per cell area x mean pulse)
const float CF_RIPPLE_SEAM_REVEAL = 6.0;        // ripple patches only: the unresolved energised honeycomb reads as a glowing band (cf_seam_energy_filtered)
const vec3 CF_SEAM_TINT = vec3(0.985, 0.99, 1.0);   // the faint tint of the thicker glass
const float CF_GLASS_F0 = 0.04;                 // Schlick F0 for n = 1.5
const float CF_GLASS_ROUGHNESS = 0.35;          // a soft sheen, not a mirror glint (an energy field, not real glass)
const float CF_GLASS_GLINT = 0.12;              // sun sheen share
const float CF_GLASS_ENV = 0.3;                 // sky / habitat sheen share
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

// Nearest hex edge from p (lattice frame, m: x along around, y along the arch): x = distance to the edge's centre line (m), yz = the unit direction
// in the lattice frame that points away from that centre line (into the cell), so a bead profile across the seam can tilt the surface and bend the light.
vec3 cf_hex_edge(vec2 p) {
	vec2 q = p / CF_LATTICE_SPACING;
	vec2 s = vec2(1.0, 1.7320508);
	vec2 ca = floor(q / s) + 0.5;
	vec2 cb = floor((q - vec2(0.5, 0.8660254)) / s) + 0.5;
	vec2 ha = q - ca * s;
	vec2 hb = q - (cb * s + vec2(0.5, 0.8660254));
	vec2 h = dot(ha, ha) < dot(hb, hb) ? ha : hb;
	vec2 ah = abs(h);
	float slant = dot(ah, vec2(0.5, 0.8660254));
	vec2 grad = ah.x > slant ? vec2(1.0, 0.0) : vec2(0.5, 0.8660254);
	grad *= vec2(h.x < 0.0 ? -1.0 : 1.0, h.y < 0.0 ? -1.0 : 1.0);
	return vec3(max(0.5 - max(ah.x, slant), 0.0) * CF_LATTICE_SPACING, -grad);
}

// Position (m) along the nearest hex edge from its midpoint, measured with the edge's canonical tangent, so both cells sharing the edge agree;
// -L/2 .. L/2 with L = the edge length (spacing / sqrt 3).
float cf_hex_edge_along(vec2 p) {
	vec2 q = p / CF_LATTICE_SPACING;
	vec2 s = vec2(1.0, 1.7320508);
	vec2 ca = floor(q / s) + 0.5;
	vec2 cb = floor((q - vec2(0.5, 0.8660254)) / s) + 0.5;
	vec2 ha = q - ca * s;
	vec2 hb = q - (cb * s + vec2(0.5, 0.8660254));
	vec2 h = dot(ha, ha) < dot(hb, hb) ? ha : hb;
	vec2 ah = abs(h);
	float slant = dot(ah, vec2(0.5, 0.8660254));
	vec2 grad = ah.x > slant ? vec2(1.0, 0.0) : vec2(0.5, 0.8660254);
	grad *= vec2(h.x < 0.0 ? -1.0 : 1.0, h.y < 0.0 ? -1.0 : 1.0);
	vec2 tangent = vec2(-grad.y, grad.x);
	if (tangent.y < 0.0 || (tangent.y == 0.0 && tangent.x < 0.0)) tangent = -tangent;
	return dot(h - 0.5 * grad, tangent) * CF_LATTICE_SPACING;
}

// The seams' energy (0..~1.2): a crest along each edge, anti-aliased and faded like the seam itself, whose brightness flows slowly along the edges.
// One wavelength per edge (the edge length), so the pulse is continuous where three edges meet. flow_phase = weather clock / period mod 1.
float cf_seam_energy(vec2 p, float pixel_m, float flow_phase) {
	float e = cf_hex_edge(p).x;
	float fw = max(pixel_m, 0.001);
	float w = 0.5 * CF_SEAM_HALF_WIDTH;
	float drawn = max(w, 0.75 * fw);
	float crest = exp(-(e / drawn) * (e / drawn)) * (w / drawn);
	float cell_fade = 1.0 - smoothstep(CF_LATTICE_SPACING / 16.0, CF_LATTICE_SPACING / 6.0, fw);
	float edge_length = CF_LATTICE_SPACING * 0.57735027;
	float pulse = 0.5 + 0.5 * cos(6.2831853 * (cf_hex_edge_along(p) / edge_length - flow_phase));
	return crest * cell_fade * (0.45 + 0.75 * pulse);
}

// The seams' energy for a ripple patch seen from any distance (WO4): cf_seam_energy while the cells are resolved; as they shrink below a few pixels it
// converges to a band glow (the lattice's mean crest energy x CF_RIPPLE_SEAM_REVEAL), so an energised ring reads from tens of kilometres without sparkle.
// The reveal factor stands in for the glare a lit honeycomb would have at a distance; it only acts where a ripple is (the patch weight is the ring band).
float cf_seam_energy_filtered(vec2 p, float pixel_m, float flow_phase) {
	float fw = max(pixel_m, 0.001);
	float cell_fade = 1.0 - smoothstep(CF_LATTICE_SPACING / 16.0, CF_LATTICE_SPACING / 6.0, fw);
	return cf_seam_energy(p, pixel_m, flow_phase) + (1.0 - cell_fade) * CF_SEAM_ENERGY_MEAN * CF_RIPPLE_SEAM_REVEAL;
}

// ---- Ripples (docs/containment-field-design.md 3.5, 3.10.2; WO4) ----
// Needs materials/ring_containment_strike.gdshaderinc included BEFORE this file (cfs_strike_light, CFS_COLOUR).
// One ripple record (the CPU computes everything time-dependent in double, ContainmentRipplePatches.Evaluate):
//   wave  = (A x ripple strength, front radius rho m, Rmax m, flash: the source pulse x coupling, summed-capped, reduced-flash aware)
//   crawl = (Rc m (0 = no crawl), crawl front m, seed, rim bias) (cfs_strike_light)
//   strike = the strike light level E x flash x ripple strength x tail (0 = no crawl or glow)
// d = surface metres from the ripple centre (x along around, y along increasing arch length), pixel_m = the footprint of d on screen.
// Accumulates: slope (dimensionless refraction slope in the same surface frame, about -1..1 per ripple), weight (the field's reveal weight 0..1: refraction,
// sheen and seams show where the ring is), boost (cf_glass_shade's seam boost), light (additive radiance before haze: the flash-lit reveal ring,
// the strike crawl and the contact glow). All exactly 0 when wave.x, wave.w and strike are 0, and 0 at the patch edge (1.15 max(Rmax, Rc)).
const float CF_RIPPLE_LAMBDA = 0.18;          // x Rmax: crest wavelength
const float CF_RIPPLE_OMEGA = 0.12;           // x Rmax: Gaussian half width of the ring (about 1.5 visible crests)
const float CF_RIPPLE_PATCH = 1.15;           // patch radius / max(Rmax, Rc)
const vec3 CF_RIPPLE_TINT = vec3(0.55, 0.65, 1.0);
const float CF_RIPPLE_REVEAL_GAIN = 1.5;      // linear radiance of the flash-lit ring crest at A = flash = 1
const float CF_STRIKE_CRAWL_GAIN = 6.0;       // linear radiance of a resolved crawl filament at strike level 1
const float CF_STRIKE_GLOW_GAIN = 8.0;        // linear radiance of the contact glow core at strike level 1
const float CF_RIPPLE_MAX_PX = 6.0;           // refraction cap at 1080 lines (the near shimmer has 3)

void cf_ripple_eval(vec2 d, float pixel_m, vec4 wave, vec4 crawl, float strike,
		inout vec2 slope, inout float weight, inout float boost, inout vec3 light) {
	float rmax = max(wave.z, 1.0);
	float patch_r = CF_RIPPLE_PATCH * max(rmax, crawl.x);
	float s = length(d);
	float edge = 1.0 - smoothstep(0.85 * patch_r, patch_r, s);
	if (edge <= 0.0) return;
	float lambda = CF_RIPPLE_LAMBDA * rmax;
	float omega = CF_RIPPLE_OMEGA * rmax;
	float x = s - wave.y;
	float g = exp(-(x / omega) * (x / omega));
	float ph = 6.2831853 * x / lambda;
	float c = cos(ph);
	// f = cos(ph) g, df/ds = (-sin(ph) 2 pi / lambda - 2 x / omega^2 cos(ph)) g; normalised by 2 pi / lambda so a full ring is about +-1.
	float dfn = (-sin(ph) - (2.0 * x * lambda / (6.2831853 * omega * omega)) * c) * g;
	float a = max(wave.x, 0.0) * edge;
	vec2 dir = s > 0.001 ? d / s : vec2(0.0);
	slope += dir * (a * dfn * smoothstep(0.0, 0.1 * lambda, s));
	weight = max(weight, a * g);
	boost += a * g;
	light += CF_RIPPLE_TINT * (CF_RIPPLE_REVEAL_GAIN * a * g * g * (0.6 + 0.4 * c) * clamp(wave.w, 0.0, 1.0));
	if (strike > 0.0 && crawl.x > 0.0) {
		vec2 sl = cfs_strike_light(d, pixel_m, crawl) * (strike * edge);
		light += CFS_COLOUR * (CF_STRIKE_CRAWL_GAIN * sl.x + CF_STRIKE_GLOW_GAIN * sl.y);
		boost += sl.x + sl.y;
	}
}

// Refraction offset (px) of the summed ripple slope: tangent_arch_view must be the INCREASING-arch tangent (cross(around, normal)), so the ring
// bends the background radially. Capped at max_px x weight.
vec2 cf_ripple_offset_px(vec2 slope, vec3 tangent_around_view, vec3 tangent_arch_view, float weight, float max_px) {
	vec3 dv = slope.x * tangent_around_view + slope.y * tangent_arch_view;
	vec2 o = vec2(dv.x, -dv.y);
	float len = length(o);
	if (len > 1.0) o /= len;
	return o * (max_px * clamp(weight, 0.0, 1.0));
}

// The clear seam (user decision 2026-11-05, revised: transparent, a bit thicker): a parabolic bead CF_SEAM_HALF_WIDTH wide each side of the edge,
// read through optics only. Returns x = coverage (0..1, anti-aliased with the screen footprint pixel_m), yz = the surface tilt in the lattice frame
// (0 on the crest, CF_SEAM_TILT at the flanks, pointing away from the crest), w = 1 - roughness widening for the bead's specular (Toksvig-like).
// When the bead shrinks toward a pixel its tilt flattens (no sparkle) and before the cells shrink to a few pixels the seam fades out (no moire).
vec4 cf_seam(vec2 p, float pixel_m) {
	vec3 edge = cf_hex_edge(p);
	float e = edge.x;
	float fw = max(pixel_m, 0.001);
	float w = CF_SEAM_HALF_WIDTH;
	float cell_fade = 1.0 - smoothstep(CF_LATTICE_SPACING / 16.0, CF_LATTICE_SPACING / 6.0, fw);
	float coverage = (1.0 - smoothstep(w - 0.5 * fw, w + 0.5 * fw, e)) * min(1.0, w / fw) * cell_fade;
	float resolve = clamp(w / (2.5 * fw), 0.0, 1.0);
	float t = clamp(e / w, 0.0, 1.0);
	vec2 tilt = edge.yz * (CF_SEAM_TILT * t * resolve * cell_fade);
	return vec4(coverage, tilt, resolve);
}

// Refraction offset of the bead (px): a thick clear bead bends the background across the seam, like a cylindrical lens.
vec2 cf_seam_offset_px(vec4 seam, vec3 tangent_around_view, vec3 tangent_arch_view, float weight, float px_scale) {
	vec3 d = seam.y * tangent_around_view + seam.z * tangent_arch_view;
	return vec2(d.x, -d.y) * (CF_SEAM_LENS_PX / CF_SEAM_TILT * px_scale * clamp(weight, 0.0, 1.0) * seam.x);
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

// Specular reflection of one surface orientation: Fresnel x (sky looking up | lit habitat looking down) x env share, plus the GGX sun glint.
vec3 cf_glass_reflect(vec3 nf, vec3 v, vec3 l, vec3 up, vec3 sun, vec3 sky, vec3 ground, float env_share, float glint_share, float roughness) {
	float nv = clamp(dot(nf, v), 0.0, 1.0);
	vec3 r = reflect(-v, nf);
	float down = smoothstep(0.1, -0.3, dot(r, up));
	vec3 reflected = mix(sky, ground, down) * (cf_fresnel(nv) * env_share);
	float nl = dot(nf, l);
	if (nl > 0.0) {
		vec3 h = normalize(l + v);
		reflected += sun * (cf_ggx_specular(max(dot(nf, h), 0.0), nl, max(nv, 0.001), roughness) * cf_fresnel(dot(h, v)) * nl * glint_share);
	}
	return reflected;
}

// The lit pane (radiance, not premultiplied). All directions in one space (both passes use view space): n = field normal (either side), ta / tb = the
// surface tangents along around / the arch (the lattice frame's axes), v = toward the camera, l = toward the sun, up = local zenith. Light inputs
// (linear, the units of lit surfaces): sun = sun irradiance at the field (day, eclipse and K applied), sky = sky radiance (the reflection looking up),
// ground = habitat radiance (the reflection looking down). glass = glass strength x the shared fades, seam_w = seam strength x the shared fades,
// seam = cf_seam, boost = the WO4 hook (ripples and strikes brighten the seams near them; the near patch passes 0), haze = the haze transmittance.
// The panes are clear glass: a Fresnel reflection near zero head-on. The seams are clear, thicker beads: they bend the background (the caller
// applies cf_seam_offset_px before sampling `refracted`) and their curved flanks catch the sky and the sun, so the honeycomb reads through distortion
// and edge highlights, with at most a very faint tint. energy = cf_seam_energy, energy_scale = the day / night scale of the seams' faint self-light.
vec3 cf_glass_shade(vec3 refracted, vec3 n, vec3 ta, vec3 tb, vec3 v, vec3 l, vec3 up, vec3 sun, vec3 sky, vec3 ground,
		float glass, float seam_w, vec4 seam, float energy, float energy_scale, float boost, float haze) {
	vec3 nf = dot(n, v) < 0.0 ? -n : n;
	float g = clamp(glass, 0.0, 2.0);
	float pane_f = cf_fresnel(clamp(dot(nf, v), 0.0, 1.0)) * g * CF_GLASS_ENV;
	vec3 pane = cf_glass_reflect(nf, v, l, up, sun, sky, ground, CF_GLASS_ENV, CF_GLASS_GLINT, CF_GLASS_ROUGHNESS) * g;
	float m = clamp(seam.x * seam_w, 0.0, 1.0);
	vec3 bead = vec3(0.0);
	if (m > 0.0) {
		vec3 nb = normalize(nf + (seam.y * ta + seam.z * tb) * (dot(nf, n) < 0.0 ? -1.0 : 1.0));
		if (dot(nb, v) < 0.02) nb = nf;
		float rough = mix(0.45, CF_SEAM_ROUGHNESS, seam.w);
		bead = cf_glass_reflect(nb, v, l, up, sun, sky, ground, CF_SEAM_ENV, CF_SEAM_GLINT, rough) * (max(g, 0.5) * (1.0 + max(boost, 0.0)));
	}
	vec3 transmitted = refracted * (1.0 - min(pane_f, 1.0)) * mix(vec3(1.0), CF_SEAM_TINT, m);
	vec3 glow = CF_SEAM_ENERGY_COLOUR * (CF_SEAM_ENERGY * energy * clamp(seam_w, 0.0, 2.0) * energy_scale * (1.0 + CF_SEAM_BOOST_GAIN * max(boost, 0.0)));
	return transmitted + (mix(pane, bead, m) + glow) * haze;
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

// Debug colours (cf_debug_mode 1: patch coverage tint, 2: distance heat, 3: seam coverage x seam weight).
vec3 cf_debug_colour(int mode, float alpha, float dist, float range_m, float lattice) {
	if (mode == 1) return vec3(1.0, 0.0, 1.0) * alpha;
	if (mode == 3) return vec3(lattice);
	float t = clamp(dist / max(range_m, 1.0), 0.0, 1.0);
	return mix(vec3(1.0, 0.1, 0.0), vec3(0.0, 0.2, 1.0), t);
}

#endif
