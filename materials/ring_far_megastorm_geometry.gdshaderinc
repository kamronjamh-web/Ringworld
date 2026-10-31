// RingworldTechDemo megastorm far geometry (docs/megastorm-design.md 3.3, 3.4; WO5). Shared by the far wedge pass
// (ring_far_megastorm.gdshader) and the far layer sky (ring_far_cloud_layer_sky.gdshader).
//
// A surface of constant around angle is a half plane containing the ring axis, so in the scaled far viewport (ring axis +Y, far-world
// origin on the axis) the megastorm's moving frame is analytic per pixel at any distance: a sample's reference-line distance is
//   x(t) = x_cam - dir * R * atan(t * dt, rc + t * dr)
// with x_cam the camera's own reference-line distance, computed by the CPU in double (RingScaledFarViewport.Megastorm), so no float ever
// holds an around coordinate of the ring's size. xi = x - lead(across) (ring_megastorm_lead_offset). Everything is physical metres.
//
// Include after ring_atmosphere.gdshaderinc (ring_radius_meters) and ring_megastorm_profile.gdshaderinc, with the global ring_far_cloud_lights
// declared (far_ms_path_shade reads the sun from it).
#ifndef RING_FAR_MEGASTORM_GEOMETRY_GDSHADERINC
#define RING_FAR_MEGASTORM_GEOMETRY_GDSHADERINC

// Off (untriggered, RING_MEGASTORM_FAR_OFF): every consumer takes its pre-megastorm path through this one uniform branch.
uniform bool far_megastorm_enabled = false;
// RingMegastormParams: a = (weather t s, level, strength, seed), b = (W, bow, meander amplitude, meander wavelength),
// c = (meander phase rad, ceiling m, sculpt strength (Sunshine header1.y), 0).
uniform vec4 far_megastorm_a = vec4(0.0);
uniform vec4 far_megastorm_b = vec4(318000.0, 8000.0, 3000.0, 40000.0);
uniform vec4 far_megastorm_c = vec4(0.0, 18000.0, 1.0, 0.0);
// x = x_cam (the camera's reference-line distance, m), y = dir (+1 / -1), z / w = xi range of the coverage table (m).
uniform vec4 far_megastorm_frame = vec4(0.0, 1.0, 0.0, 1.0);
// Trailing (T) + precursor (P) layer coverage over xi (MegastormFarCoverage): (1 - remaining low, mid, high, weatherness).
uniform sampler2D far_megastorm_coverage : hint_default_black, filter_linear, repeat_disable;

const float RING_FAR_MS_NONE = 1e30;
const float RING_FAR_MS_TAU = 6.28318530718;

// The view ray about the ring axis: rc camera radius (m), h camera altitude, dr / dt the horizontal radial-outward / tangential (increasing
// angle) components of the unit direction, dy its across component, a = dr^2 + dt^2, across_c the camera's across (m).
struct RingFarMsRay { float rc; float h; float dr; float dt; float dy; float a; float across_c; };

// The ring radius in physical metres: far materials hold ring_radius_meters in scaled units (RingAtmosphereModel scales it with the
// coordinate scale), and ring_atmosphere_coordinate_scale comes with the per-frame atmosphere ray frame.
float ring_far_ms_radius() {
	return ring_radius_meters / max(ring_atmosphere_coordinate_scale, 0.000001);
}

RingMegastormParams ring_far_ms_params() {
	return RingMegastormParams(far_megastorm_a, far_megastorm_b, far_megastorm_c);
}

float ring_far_ms_x(RingFarMsRay r, float t) {
	return far_megastorm_frame.x - far_megastorm_frame.y * ring_far_ms_radius() * atan(t * r.dt, r.rc + t * r.dr);
}

float ring_far_ms_across(RingFarMsRay r, float t) {
	return r.across_c + t * r.dy;
}

float ring_far_ms_altitude(RingFarMsRay r, float t) {
	float x = r.rc + t * r.dr;
	float y = t * r.dt;
	return r.h - (2.0 * r.rc * t * r.dr + t * t * r.a) / (sqrt(x * x + y * y) + r.rc);
}

// Both crossings of altitude H (ascending; (-1, -1) when the ray never reaches it).
vec2 ring_far_ms_crossings(RingFarMsRay r, float H) {
	float R = ring_far_ms_radius();
	float b = r.rc * r.dr;
	float c = (H - r.h) * (2.0 * R - r.h - H);
	float disc = b * b - r.a * c;
	if (r.a < 1e-12 || disc < 0.0) return vec2(-1.0);
	float s = sqrt(disc);
	float q = -(b + (b < 0.0 ? -s : s));
	if (abs(q) < 1e-9) return vec2(-1.0);
	float r0 = q / r.a;
	float r1 = c / q;
	return vec2(min(r0, r1), max(r0, r1));
}

// The ray interval where x(t) lies in [x_lo, x_hi] (empty: x > y). The polar angle of a point moving along a line is monotonic, so
// the wedge between two half planes is one interval; the angle range is wrapped onto the side the ray sweeps.
vec2 ring_far_ms_wedge(RingFarMsRay r, float x_lo, float x_hi) {
	float R = ring_far_ms_radius();
	float dir = far_megastorm_frame.y;
	float p0 = (far_megastorm_frame.x - x_hi) / (dir * R);
	float p1 = (far_megastorm_frame.x - x_lo) / (dir * R);
	float pa = min(p0, p1);
	float pb = max(p0, p1);
	float adt = abs(r.dt);
	if (adt < 1e-7) return (pa <= 0.0 && pb >= 0.0) ? vec2(0.0, RING_FAR_MS_NONE) : vec2(RING_FAR_MS_NONE, -1.0);
	float qa = r.dt >= 0.0 ? pa : -pb;
	float qb = r.dt >= 0.0 ? pb : -pa;
	float k = round(0.5 * (qa + qb) / RING_FAR_MS_TAU);
	qa -= k * RING_FAR_MS_TAU;
	qb -= k * RING_FAR_MS_TAU;
	if (qb < 0.0) { qa += RING_FAR_MS_TAU; qb += RING_FAR_MS_TAU; }
	float psi_inf = atan(adt, r.dr);
	qa = max(qa, 0.0);
	qb = min(qb, psi_inf);
	if (qb <= qa) return vec2(RING_FAR_MS_NONE, -1.0);
	float ta = qa <= 0.0 ? 0.0 : r.rc * sin(qa) / max(adt * cos(qa) - r.dr * sin(qa), 1e-12);
	float tb = qb >= psi_inf - 1e-6 ? RING_FAR_MS_NONE : r.rc * sin(qb) / max(adt * cos(qb) - r.dr * sin(qb), 1e-12);
	return vec2(ta, tb);
}

// The ray interval inside the across slab [0, W].
vec2 ring_far_ms_slab(RingFarMsRay r) {
	float W = far_megastorm_b.x;
	if (abs(r.dy) < 1e-7) return (r.across_c >= 0.0 && r.across_c <= W) ? vec2(0.0, RING_FAR_MS_NONE) : vec2(RING_FAR_MS_NONE, -1.0);
	float t0 = (0.0 - r.across_c) / r.dy;
	float t1 = (W - r.across_c) / r.dy;
	return vec2(min(t0, t1), max(t0, t1));
}

// The altitude band [lo, hi] along the ray: up to two intervals (the chord climbs above hi and comes back down). Empty: x > y.
void ring_far_ms_band(RingFarMsRay r, float lo, float hi, out vec2 i0, out vec2 i1) {
	i0 = vec2(RING_FAR_MS_NONE, -1.0);
	i1 = vec2(RING_FAR_MS_NONE, -1.0);
	vec2 c_lo = ring_far_ms_crossings(r, lo);
	if (c_lo.y <= 0.0) return;
	vec2 c_hi = ring_far_ms_crossings(r, hi);
	if (c_hi.y > 0.0 && c_hi.y > c_hi.x) {
		i0 = vec2(c_lo.x, min(c_lo.y, c_hi.x));
		i1 = vec2(max(c_lo.x, c_hi.y), c_lo.y);
	} else {
		i0 = c_lo;
	}
}

// First t >= t_min (and < t_max) inside the box xi in [xi_lo, xi_hi], altitude [alt_lo, alt_hi], across [0, W]. The lead offset bows the
// box; it is taken at the ray's across where the entry lands (two fixed-point passes; the bow varies by kilometres over the whole width).
float ring_far_ms_box_entry(RingFarMsRay r, float xi_lo, float xi_hi, float alt_lo, float alt_hi, float t_min, float t_max) {
	RingMegastormParams p = ring_far_ms_params();
	vec2 slab = ring_far_ms_slab(r);
	vec2 b0;
	vec2 b1;
	ring_far_ms_band(r, alt_lo, alt_hi, b0, b1);
	float lo = max(t_min, slab.x);
	float hi = min(t_max, slab.y);
	if (hi <= lo) return RING_FAR_MS_NONE;
	float across = clamp(ring_far_ms_across(r, lo), 0.0, far_megastorm_b.x);
	float entry = RING_FAR_MS_NONE;
	for (int pass = 0; pass < 2; pass++) {
		float lead = ring_megastorm_lead_offset(across, p);
		vec2 w = ring_far_ms_wedge(r, xi_lo + lead, xi_hi + lead);
		float a = max(lo, w.x);
		float b = min(hi, w.y);
		entry = RING_FAR_MS_NONE;
		if (b > a) {
			if (min(b, b0.y) > max(a, b0.x)) entry = max(a, b0.x);
			else if (min(b, b1.y) > max(a, b1.x)) entry = max(a, b1.x);
		}
		if (entry >= RING_FAR_MS_NONE) return entry;
		across = clamp(ring_far_ms_across(r, entry), 0.0, far_megastorm_b.x);
	}
	return entry;
}

// Where the layer sky ends its integration (docs/megastorm-design.md 3.3 ordering): the entry into the wall body (xi 0 .. 25 km below the
// tower tops) or the leading anvil (xi -60 .. 0 km, 9 .. 16.5 km), whichever comes first. Cloud in front composites over the wall; the
// layers behind it are not drawn (the wedge pass draws the wall before the layer sky).
float megastorm_body_entry(RingFarMsRay r, float t_min, float t_max) {
	if (!far_megastorm_enabled) return RING_FAR_MS_NONE;
	float level = far_megastorm_a.y;
	float top = min(RING_MS_FORMING_TOP + (15000.0 - RING_MS_FORMING_TOP) * level, far_megastorm_c.y);
	float body = ring_far_ms_box_entry(r, 0.0, RING_MS_WALL_END_XI, 300.0, top, t_min, t_max);
	float canopy_top = min(RING_MS_FORMING_TOP + (RING_MS_FULL_TOP - RING_MS_FORMING_TOP) * level, far_megastorm_c.y);
	float canopy = canopy_top > 10000.0
		? ring_far_ms_box_entry(r, RING_MS_ANVIL_START_XI + 10000.0, 0.0, 10000.0, canopy_top, t_min, t_max) : RING_FAR_MS_NONE;
	return min(body, canopy);
}

// T + P coverage at xi (0 outside the table).
vec4 ring_far_ms_coverage(float xi) {
	float span = max(far_megastorm_frame.w - far_megastorm_frame.z, 1.0);
	float u = (xi - far_megastorm_frame.z) / span;
	if (u <= 0.0 || u >= 1.0) return vec4(0.0);
	return textureLod(far_megastorm_coverage, vec2(u, 0.5), 0.0);
}

// ---- Column sun shade (shared by the wedge pass and the far layer sky) ----
// Across noise table: (tower, mammatus, shaft) raw value noise over across 0 .. W (MegastormProfile.AcrossTerms).
uniform sampler2D far_megastorm_across : hint_default_black, filter_linear, repeat_disable;
// Sunshine's per-ray storm ambient (rgb, w = Storm darkness strength): the wedge pass's storm light, and the layer sky's ambient for cloud
// in the column's shadow.
uniform vec4 far_megastorm_storm_ambient = vec4(0.0);
// The storm-ambient factor of cloud under the overhang (the near's RING_MEGASTORM_UNDER_OPEN between the shade and open factors:
// mix(0.45, 1.8, 0.6)): ring_megastorm_open_light for fair-layer cumulus below the column.
const float FAR_MS_UNDER_AMBIENT = 1.26;

const float FAR_MS_SUN_EXTINCTION = 3.0e-3; // RING_STORM_SUN_EXTINCTION
// The strip extent (MegastormProfile.StripMinX / StripMaxX).
const float FAR_MS_X_MIN = -72000.0;
const float FAR_MS_X_MAX = 56000.0;

// The column at (x, across) in the strip sampler's convention (ring_megastorm_strip_fetch): each interval collapsed onto its base by its
// coverage x sculpt strength. heights = (body base, body top, canopy underside, canopy top), aux = (canopy cov, mammatus, tower, body cov).
void far_ms_column(float x, float across, RingMegastormParams p, out vec4 heights, out vec4 aux) {
	float W = max(p.b.x, 1.0);
	vec2 noise = textureLod(far_megastorm_across, vec2(clamp(across / W, 0.0, 1.0), 0.5), 0.0).xy;
	vec4 raw;
	ring_megastorm_column_terms(x, across, noise, p, raw, aux);
	float sculpt = clamp(p.c.z * 1.2, 0.0, 1.0);
	heights = vec4(raw.x, raw.x + max(raw.y - raw.x, 0.0) * clamp(1.25 * aux.w * sculpt, 0.0, 1.0),
		raw.z, raw.z + max(raw.w - raw.z, 0.0) * clamp(1.25 * aux.x * sculpt, 0.0, 1.0));
}

// ring_megastorm_shade_at (RingStormProfileSample.glslinc): how much of the sun the megastorm column shades at the ray point t (the column
// 2.5 km up the sun ray, overhead when the sun is low). 0 outside the strip x range and when the sun is absent or below the local horizon.
float far_ms_shade_at(RingFarMsRay ray, float t, vec3 L, vec3 up_at_t, RingMegastormParams p) {
	float sun_up = dot(L, up_at_t);
	if (smoothstep(-0.05, 0.12, dot(normalize(L), up_at_t)) <= 0.0) return 0.0;
	vec3 tangent = vec3(up_at_t.z, 0.0, -up_at_t.x);
	float x = ring_far_ms_x(ray, t);
	float across = ring_far_ms_across(ray, t);
	if (sun_up >= 0.06) {
		float k = 2500.0 / sun_up;
		x += -far_megastorm_frame.y * dot(L, tangent) * k;
		across += L.y * k;
	}
	if (x < FAR_MS_X_MIN || x > FAR_MS_X_MAX) return 0.0;
	vec4 hts;
	vec4 unused_aux;
	far_ms_column(x, across, p, hts, unused_aux);
	float h = ring_far_ms_altitude(ray, t);
	float overlap = max(hts.y - max(hts.x, h), 0.0) + max(hts.w - max(hts.z, h), 0.0);
	return 1.0 - exp(-FAR_MS_SUN_EXTINCTION * overlap / max(sun_up, 0.06));
}


// ring_megastorm_path_shade: the mean of far_ms_shade_at at the centres of four equal pieces of the path to t_mean (from the camera).
float far_ms_path_shade(RingFarMsRay ray, vec2 u, float t_mean, RingMegastormParams p) {
	vec4 ld = ring_far_cloud_lights[0];
	if (ld.w < 0.5) return 0.0;
	vec3 L = normalize(ld.xyz);
	float shade = 0.0;
	for (int k = 0; k < 4; k++) {
		float t = t_mean * (0.125 + 0.25 * float(k));
		vec2 radial = (ray.rc + t * ray.dr) * u + (t * ray.dt) * vec2(-u.y, u.x);
		vec2 n = normalize(radial);
		shade += far_ms_shade_at(ray, t, L, vec3(-n.x, 0.0, -n.y), p);
	}
	return 0.25 * shade;
}

#endif
