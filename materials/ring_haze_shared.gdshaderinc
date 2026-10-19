// Physical haze math (P1 of docs/atmosphere-haze-revamp-plan.md). Portable and uniform-free: compiles as Godot shader
// language and as raw GLSL 450 (Sunshine compute). Include AFTER ring_atmosphere_shared.gdshaderinc (it reuses
// ring_atmosphere_path_interval_shared for the air interval). Not wired into any production material yet.
//
// Consumer contract:   L = L_surf * T + S_sun * vis + S_amb      (vis = 1 until cloud/shaft visibility lands in P9)
//
// Units. All lengths are metres EXCEPT camera/target positions, which are in coordinate units; coordinate_scale is
// coordinate units per metre (1 in the main viewport, 1/1000 in the far viewport). Altitude h is height above the nominal
// floor: h = R - |radial(p)| with radial = component perpendicular to the ring axis; "up" is toward the axis.
// `outward` is the unit radial direction at the camera (away from the axis), as in the ring ray frame.
//
// Layers (extinction per metre = sigma * exp(-h / H)):
//   Rayleigh  sigma_R (per channel), H_R      scattering = extinction
//   aerosol   sigma_A (grey Mie),    H_A      scattering = omega_A * extinction
//   mist      sigma_M0 (grey Mie),   H_mist   scattering = omega_M * extinction
// Optical depth over the first local air interval [t0, t1] (see ring_haze_air_interval):
//   tau_layer = sigma_layer * Integral exp(-h(t)/H_layer) dt,   h(t) = h0 - (b t + a t^2) / (Rc + sqrt(Rc^2 + b t + a t^2))
//   with Rc = R - h0, b = 2 Rc dot(outward, ray), a = |ray - axis dot(ray, axis)|^2   (cancellation-free form of R - |radial|).
//   Per piece, h is replaced by the line hbar + mu (t - tm) (hbar = Simpson mean of the exact h, mu = chord slope) and the exponential
//   is integrated in closed form (iq): Integral = dt exp(-hbar/H) sinh(x)/x with x = |mu| dt / (2H)  (x -> 0 limit 1 + x^2/6).
//   Short or nearly straight rays (altitude sag < RING_HAZE_SAG_FRACTION * H_mist, e.g. everything under ~20 km) are ONE piece; longer
//   curved rays are cut into N (quality 4/8/16) Chebyshev-spaced pieces, 2N+1 exact h evaluations. (Deviation from "N-point Gauss-Legendre on h(t)": GL cannot
//   resolve a 400 m mist layer over 20-600 km; measured 2.7% tau error at N=8.)
// Altitude below the floor is not clamped (the shell reaches floor_depth below it); the exponent is clamped at 80.
//
// Output:
//   T = exp(-(tau_R + tau_A + tau_M))                    per channel (Rayleigh chromatic, Mie/mist grey)
//   mu = dot(view_ray, to_sun);  P_R = 3/(16 pi) (1 + mu^2);  P_M = Cornette-Shanks(g)
//   w_sun = tau_R P_R + (omega_A tau_A + omega_M tau_M) P_M ;  w_amb = tau_R + omega_A tau_A + omega_M tau_M
//   S_sun = (1 - T) * E_sun * day * sun_vis * T_sun * w_sun / max(tau_total, eps)
//   S_amb = (1 - T) * (ambient * day + night_ambient * (1 - day)) * w_amb / max(tau_total, eps)
//   (constant-lighting single scatter: the light seen by the whole path is that at the path midpoint)
// Sun transmittance T_sun: vertical column optical depth from h_mid to the cap, sum_layer sigma H (e^-h_mid/H - e^-cap/H),
//   times a Chapman-like airmass 1.025 / (max(cos_sun, 0) + 0.025) (about 41 at the horizon, 1 at the zenith, so low sun reddens),
//   where cos_sun = dot(to_sun, local up at the path midpoint). sun_vis = smoothstep(-0.06, 0, cos_sun) kills the sun below the horizon.
//   `day` is supplied by the caller (ringworld_effective_solar_day semantics); this file reads no globals.

// Maximum altitude error (m) tolerated by treating h as a straight line inside one quadrature piece, scaled by the mist height.
const float RING_HAZE_SAG_FRACTION = 0.03;

struct RingHaze {
	vec3 T;
	vec3 S_sun;
	vec3 S_amb;
};

struct RingHazeParams {
	vec3 sigma_R;       // Rayleigh extinction at the floor, per channel, 1/m
	float H_R;          // Rayleigh scale height, m
	float sigma_A;      // aerosol (Mie) extinction at the floor, 1/m
	float omega_A;      // aerosol single-scatter albedo
	float H_A;          // aerosol scale height, m
	float sigma_M0;     // ground mist extinction at the floor, 1/m
	float omega_M;      // mist single-scatter albedo
	float H_mist;       // mist scale height, m
	float cap_height;   // top of the integration (upper air shell), m
	float g;            // Mie forward-scattering anisotropy
	vec3 E_sun;         // sun irradiance colour, linear (radiance = E_sun * phase * optical depth terms)
	vec3 ambient;       // ambient / multi-scatter radiance colour at full day
	vec3 night_ambient; // ambient radiance colour at night
	float day;          // caller-supplied day weight in [0, 1]
	vec3 to_sun;        // unit direction toward the sun, same space as the ray
	int quality;        // quadrature pieces for long curved rays: 4, 8 or 16
};

struct RingHazeOptics {
	vec3 tau_R;
	float tau_A;
	float tau_M;
};

// Koschmieder: total green-channel extinction at the floor = 3.912 / V. Returns the aerosol floor extinction that
// makes the (Rayleigh + aerosol + mist) green extinction match the meteorological visibility V (km).
float ring_haze_aerosol_from_visibility(float visibility_km, float sigma_R_green, float sigma_M0) {
	return max(3.912 / (max(visibility_km, 0.001) * 1000.0) - sigma_R_green - sigma_M0, 0.0);
}

RingHazeParams ring_haze_params_default() {
	RingHazeParams p;
	p.sigma_R = vec3(5.802e-6, 13.558e-6, 33.1e-6);
	p.H_R = 8000.0;
	p.sigma_M0 = 4.0e-6;
	p.omega_M = 0.95;
	p.H_mist = 400.0;
	p.sigma_A = ring_haze_aerosol_from_visibility(180.0, 13.558e-6, 4.0e-6);
	p.omega_A = 0.9;
	p.H_A = 1500.0;
	p.cap_height = 20000.0;
	p.g = 0.6;
	p.E_sun = vec3(1.0, 0.96, 0.9);
	p.ambient = vec3(0.1, 0.12, 0.16);
	p.night_ambient = vec3(0.004, 0.006, 0.012);
	p.day = 1.0;
	p.to_sun = vec3(0.0, 1.0, 0.0);
	p.quality = 8;
	return p;
}

float ring_haze_phase_rayleigh(float mu) {
	return 0.0596831037 * (1.0 + mu * mu);
}

// x^1.5 for x >= 0 (callers clamp x to >= 0.0001).
float ring_haze_pow15(float x) {
	return x * sqrt(x);
}

// Cornette-Shanks.
float ring_haze_phase_mie(float mu, float g_in) {
	float g = clamp(g_in, -0.95, 0.95);
	float g2 = g * g;
	return 0.1193662074 * (1.0 - g2) * (1.0 + mu * mu) / ((2.0 + g2) * ring_haze_pow15(max(1.0 + g2 - 2.0 * g * mu, 0.0001)));
}

// 1 - exp(-x) without cancellation for small x.
float ring_haze_one_minus_exp(float x) {
	return x < 0.01 ? x * (1.0 - 0.5 * x * (1.0 - x * 0.3333333)) : 1.0 - exp(-x);
}

// First local air interval along the ray, in metres: (t0, t1), empty (y <= x) when there is none. Same machinery as
// ring_atmosphere_first_air_paths_shared (outer shell minus the vacuum above the cap, remote shell excluded).
vec2 ring_haze_air_interval(vec3 target, vec3 camera, float camera_height, vec3 outward, vec3 axis, float side_negative, float side_positive, float coordinate_scale, float radius_m, float floor_depth_m, float cap_height_m) {
	float scale = max(coordinate_scale, 0.000001);
	float length_m = length(target - camera) / scale;
	// Fast path: a ray shorter than the distance to the floor shell, the cap shell and both side walls cannot reach any of them
	// (radial and axial coordinates change by at most 1 m per metre of ray), so the interval is exactly [0, length].
	if (length_m > 0.0001 && length_m < min(min(cap_height_m - camera_height, camera_height + floor_depth_m), min(side_negative, side_positive))) return vec2(0.0, length_m);
	vec2 outer = ring_atmosphere_path_interval_shared(target, camera, camera_height, outward, axis, side_negative, side_positive, scale, radius_m * scale, -floor_depth_m, length_m);
	vec2 upper_vacuum = ring_atmosphere_path_interval_shared(target, camera, camera_height, outward, axis, side_negative, side_positive, scale, radius_m * scale, cap_height_m, length_m);
	if (outer.y <= outer.x) return vec2(1.0, 0.0);
	vec2 air = outer;
	if (upper_vacuum.y > upper_vacuum.x) {
		if (upper_vacuum.x <= air.x) air.x = max(air.x, upper_vacuum.y);
		else air.y = min(air.y, upper_vacuum.x);
	}
	return air;
}

// Altitude along the ray, h(t) = R - |radial(p0 + t ray)|, evaluated without the large-number cancellation.
float ring_haze_height(float t, float h0, float radial0, float b, float a) {
	float n = b * t + a * t * t;
	float root = sqrt(max(radial0 * radial0 + n, 0.0));
	return h0 - n / max(radial0 + root, 0.001);
}

// Integral of exp(-h/H) over a flat-ish segment: h ~ hbar + mu (t - tm).
float ring_haze_flat_layer(float hbar, float mu, float dt, float scale_height) {
	float H = max(scale_height, 1.0);
	float x = abs(mu) * dt / (2.0 * H);
	if (x < 0.001) return dt * exp(min(-hbar / H, 80.0)) * (1.0 + x * x * 0.1666667);
	return dt * exp(min(-hbar / H + x, 80.0)) * (1.0 - exp(-2.0 * x)) / (2.0 * x);
}

// Per-layer Integral exp(-h/H) dt over [t0, t1]: x = Rayleigh, y = aerosol, z = mist.
// Straight rays (altitude sag a dt^2 / (8 Rc) under RING_HAZE_SAG_FRACTION * H_mist, e.g. everything under ~20 km) are ONE piece of the
// closed form above. Longer curved rays are cut into N pieces (N = quality 4/8/16) at Chebyshev-spaced positions, dense at both ends
// where the low altitude (hence the weight) is; each piece uses its chord line plus the Simpson-mean offset (2N+1 exact h evaluations).
vec3 ring_haze_layer_integrals(RingHazeParams p, float h0, float radial0, float b, float a, float t0, float t1) {
	float dt = t1 - t0;
	if (dt <= 0.0) return vec3(0.0);
	int n = p.quality >= 16 ? 16 : (p.quality >= 8 ? 8 : 4);
	float sag = max(a, 0.0) * dt * dt / (8.0 * max(radial0, 1.0));
	float sag_tol = clamp(RING_HAZE_SAG_FRACTION * p.H_mist, 4.0, 40.0);
	// Adaptive count: Chebyshev pieces are at most (pi/2)/k of the span, so the per-piece sag is sag (pi/2)^2 / k^2; k = ceil((pi/2) sqrt(sag / tol)) keeps it at the one-piece tolerance.
	int pieces = sag <= sag_tol ? 1 : min(n, max(2, int(ceil(1.5707963 * sqrt(sag / sag_tol)))));
	vec3 sum = vec3(0.0);
	float t_start = t0;
	float h_start = ring_haze_height(t0, h0, radial0, b, a);
	for (int i = 0; i < 16; i++) {
		if (i >= pieces) break;
		float t_end = i + 1 == pieces ? t1 : t0 + dt * (0.5 - 0.5 * cos(3.14159265 * float(i + 1) / float(pieces)));
		float piece_dt = t_end - t_start;
		float h_mid = ring_haze_height(0.5 * (t_start + t_end), h0, radial0, b, a);
		float h_end = ring_haze_height(t_end, h0, radial0, b, a);
		float hbar = (h_start + 4.0 * h_mid + h_end) / 6.0;
		float mu = (h_end - h_start) / max(piece_dt, 0.000001);
		sum += vec3(ring_haze_flat_layer(hbar, mu, piece_dt, p.H_R), ring_haze_flat_layer(hbar, mu, piece_dt, p.H_A), ring_haze_flat_layer(hbar, mu, piece_dt, p.H_mist));
		h_start = h_end;
		t_start = t_end;
	}
	return sum;
}

// Optical depth over [t0, t1] of the ray from the camera. ray is a unit vector in the ring-frame space (outward/axis space).
RingHazeOptics ring_haze_optics(RingHazeParams p, vec3 ray, vec2 interval, float camera_height, vec3 outward, vec3 axis, float radius_m) {
	RingHazeOptics o;
	o.tau_R = vec3(0.0);
	o.tau_A = 0.0;
	o.tau_M = 0.0;
	if (interval.y <= interval.x) return o;
	axis = normalize(axis); outward = normalize(outward);
	vec3 radial_ray = ray - axis * dot(ray, axis);
	float radial0 = radius_m - camera_height;
	float b = 2.0 * radial0 * dot(outward, ray);
	float a = dot(radial_ray, radial_ray);
	vec3 layers = ring_haze_layer_integrals(p, camera_height, radial0, b, a, interval.x, interval.y);
	o.tau_R = p.sigma_R * layers.x;
	o.tau_A = p.sigma_A * layers.y;
	o.tau_M = p.sigma_M0 * layers.z;
	return o;
}

// Sun transmittance to the path midpoint (see header). Returns rgb transmittance already multiplied by sun_vis.
vec3 ring_haze_sun_transmittance(RingHazeParams p, vec3 ray, vec2 interval, float camera_height, vec3 outward, vec3 axis, float radius_m) {
	axis = normalize(axis); outward = normalize(outward);
	float t_mid = 0.5 * (interval.x + interval.y);
	float radial0 = radius_m - camera_height;
	vec3 radial_ray = ray - axis * dot(ray, axis);
	vec3 radial_mid = outward * radial0 + radial_ray * t_mid;
	float radial_len = max(length(radial_mid), 0.001);
	float cos_sun = dot(normalize(p.to_sun), -radial_mid / radial_len);
	float h_mid = radius_m - radial_len;
	float cap = p.cap_height;
	float col_R = max(p.H_R * (exp(min(-h_mid / max(p.H_R, 1.0), 80.0)) - exp(-cap / max(p.H_R, 1.0))), 0.0);
	float col_A = max(p.H_A * (exp(min(-h_mid / max(p.H_A, 1.0), 80.0)) - exp(-cap / max(p.H_A, 1.0))), 0.0);
	float col_M = max(p.H_mist * (exp(min(-h_mid / max(p.H_mist, 1.0), 80.0)) - exp(-cap / max(p.H_mist, 1.0))), 0.0);
	vec3 tau_col = p.sigma_R * col_R + vec3(p.sigma_A * col_A + p.sigma_M0 * col_M);
	float airmass = 1.025 / (max(cos_sun, 0.0) + 0.025);
	return exp(-tau_col * airmass) * smoothstep(-0.06, 0.0, cos_sun);
}

// Full evaluation toward `target` (coordinate units). camera_height, radius_m, floor_depth_m in metres.
RingHaze ring_haze_eval(RingHazeParams p, vec3 target, vec3 camera, float camera_height, vec3 outward, vec3 axis, float side_negative, float side_positive, float coordinate_scale, float radius_m, float floor_depth_m) {
	RingHaze result;
	result.T = vec3(1.0);
	result.S_sun = vec3(0.0);
	result.S_amb = vec3(0.0);
	float scale = max(coordinate_scale, 0.000001);
	vec3 delta = target - camera;
	if (length(delta) <= 0.0001 * scale) return result;
	vec3 ray = normalize(delta);
	vec2 interval = ring_haze_air_interval(target, camera, camera_height, outward, axis, side_negative, side_positive, scale, radius_m, floor_depth_m, p.cap_height);
	if (interval.y <= interval.x) return result;
	RingHazeOptics o = ring_haze_optics(p, ray, interval, camera_height, outward, axis, radius_m);
	float mie_extinction = o.tau_A + o.tau_M;
	vec3 tau_total = o.tau_R + vec3(mie_extinction);
	result.T = exp(-tau_total);
	vec3 one_minus_T = vec3(ring_haze_one_minus_exp(tau_total.x), ring_haze_one_minus_exp(tau_total.y), ring_haze_one_minus_exp(tau_total.z));
	vec3 inv_total = vec3(1.0) / max(tau_total, vec3(0.00000001));
	float mie_scatter = p.omega_A * o.tau_A + p.omega_M * o.tau_M;
	float mu = dot(ray, normalize(p.to_sun));
	vec3 weight_sun = o.tau_R * ring_haze_phase_rayleigh(mu) + vec3(mie_scatter * ring_haze_phase_mie(mu, p.g));
	vec3 weight_amb = o.tau_R + vec3(mie_scatter);
	vec3 t_sun = ring_haze_sun_transmittance(p, ray, interval, camera_height, outward, axis, radius_m);
	float day = clamp(p.day, 0.0, 1.0);
	result.S_sun = one_minus_T * p.E_sun * day * t_sun * weight_sun * inv_total;
	result.S_amb = one_minus_T * mix(p.night_ambient, p.ambient, day) * weight_amb * inv_total;
	return result;
}

// ---- Far-arch remote shell (P10) --------------------------------------------------------------------------------------------
// A ray that leaves the local air, crosses the central vacuum and re-enters air near its target passes through the air of the
// OPPOSITE ring section. Remote interval = [upper_vacuum.y, outer.y]; it exists only when the vacuum starts after the camera's
// outer chord does (camera in air). The same cylinder frame (ring_haze_height with the camera's h0/radial0/b/a) covers the whole ray.
// Shortest ray that can leave and re-enter air: tangent to the vacuum core, sqrt(cap (2R - cap)) = 447 km for R = 5000 km, cap 20 km.
vec2 ring_haze_remote_interval(vec3 target, vec3 camera, float camera_height, vec3 outward, vec3 axis, float side_negative, float side_positive, float coordinate_scale, float radius_m, float floor_depth_m, float cap_height_m) {
	float scale = max(coordinate_scale, 0.000001);
	float length_m = length(target - camera) / scale;
	if (length_m < 0.98 * sqrt(max(cap_height_m * (2.0 * radius_m - cap_height_m), 0.0))) return vec2(1.0, 0.0);
	vec2 outer = ring_atmosphere_path_interval_shared(target, camera, camera_height, outward, axis, side_negative, side_positive, scale, radius_m * scale, -floor_depth_m, length_m);
	vec2 upper_vacuum = ring_atmosphere_path_interval_shared(target, camera, camera_height, outward, axis, side_negative, side_positive, scale, radius_m * scale, cap_height_m, length_m);
	if (outer.y <= outer.x || upper_vacuum.y <= upper_vacuum.x) return vec2(1.0, 0.0);
	if (upper_vacuum.x <= outer.x) return vec2(1.0, 0.0);
	return vec2(upper_vacuum.y, outer.y);
}

// T and S of the remote shell alone (T = 1, S = 0 when there is none). Lighting uses the REMOTE point (path midpoint): its own
// local day, smoothstep(-0.2, 0.35, dot(up_remote, to_sun)) with the ringworld_effective_solar_day semantics (solar_visibility is
// the global eclipse term, hub_day the eclipse hub), hub-blended sun direction, and the sun transmittance there. raw_to_sun is the
// unblended direction toward the star; p supplies everything else (p.to_sun / p.day describe the camera's point and are ignored).
RingHaze ring_haze_eval_remote(RingHazeParams p, vec3 raw_to_sun, float hub_weight, float hub_day, float solar_visibility, vec3 target, vec3 camera, float camera_height, vec3 outward, vec3 axis, float side_negative, float side_positive, float coordinate_scale, float radius_m, float floor_depth_m) {
	RingHaze result;
	result.T = vec3(1.0);
	result.S_sun = vec3(0.0);
	result.S_amb = vec3(0.0);
	float scale = max(coordinate_scale, 0.000001);
	vec3 delta = target - camera;
	if (length(delta) <= 0.0001 * scale) return result;
	vec3 ray = normalize(delta);
	vec2 interval = ring_haze_remote_interval(target, camera, camera_height, outward, axis, side_negative, side_positive, scale, radius_m, floor_depth_m, p.cap_height);
	if (interval.y <= interval.x) return result;
	RingHazeOptics o = ring_haze_optics(p, ray, interval, camera_height, outward, axis, radius_m);
	// Remote up at the path midpoint, and the sun direction/day it sees.
	vec3 axis_n = normalize(axis);
	vec3 radial_ray = ray - axis_n * dot(ray, axis_n);
	vec3 radial_mid = normalize(outward) * (radius_m - camera_height) + radial_ray * (0.5 * (interval.x + interval.y));
	vec3 up_remote = -radial_mid / max(length(radial_mid), 0.001);
	vec3 blended_sun = mix(normalize(raw_to_sun), up_remote, clamp(hub_weight, 0.0, 1.0));
	RingHazeParams q = p;
	q.to_sun = length(blended_sun) > 0.0001 ? normalize(blended_sun) : up_remote;
	q.day = max(clamp(smoothstep(-0.2, 0.35, dot(up_remote, q.to_sun)), 0.0, 1.0) * clamp(solar_visibility, 0.0, 1.0), clamp(hub_day, 0.0, 1.0));
	float mie_extinction = o.tau_A + o.tau_M;
	vec3 tau_total = o.tau_R + vec3(mie_extinction);
	result.T = exp(-tau_total);
	vec3 one_minus_T = vec3(ring_haze_one_minus_exp(tau_total.x), ring_haze_one_minus_exp(tau_total.y), ring_haze_one_minus_exp(tau_total.z));
	vec3 inv_total = vec3(1.0) / max(tau_total, vec3(0.00000001));
	float mie_scatter = q.omega_A * o.tau_A + q.omega_M * o.tau_M;
	float mu = dot(ray, q.to_sun);
	vec3 weight_sun = o.tau_R * ring_haze_phase_rayleigh(mu) + vec3(mie_scatter * ring_haze_phase_mie(mu, q.g));
	vec3 weight_amb = o.tau_R + vec3(mie_scatter);
	vec3 t_sun = ring_haze_sun_transmittance(q, ray, interval, camera_height, outward, axis, radius_m);
	float day = clamp(q.day, 0.0, 1.0);
	result.S_sun = one_minus_T * q.E_sun * day * t_sun * weight_sun * inv_total;
	result.S_amb = one_minus_T * mix(q.night_ambient, q.ambient, day) * weight_amb * inv_total;
	return result;
}

// Storm grey-Mie haze (P9). storm = (s, sigma_storm, omega_storm, g_storm): s in [0, 1] storm strength, sigma_storm the aerosol
// extinction (1/m) added at s = 1 (already scaled by density), then the single-scatter albedo and Mie anisotropy of the storm particles.
// The particles join the aerosol layer (same H_A): extinction adds, omega_A is extinction-weighted, g is scattering-weighted.
// s <= 0 returns p unchanged (bit-identical clear weather). RingAtmosphereModel.ApplyStorm mirrors this with the same op order.
RingHazeParams ring_haze_apply_storm(RingHazeParams p, vec4 storm) {
	float s = clamp(storm.x, 0.0, 1.0);
	if (s <= 0.0) return p;
	float add = s * max(storm.y, 0.0);
	float scatter_clear = p.omega_A * p.sigma_A + p.omega_M * p.sigma_M0;
	float scatter_storm = clamp(storm.z, 0.0, 1.0) * add;
	float sigma = p.sigma_A + add;
	p.omega_A = sigma > 0.0 ? (p.omega_A * p.sigma_A + scatter_storm) / sigma : p.omega_A;
	float st = scatter_clear + scatter_storm;
	p.g = st > 0.0 ? (p.g * scatter_clear + clamp(storm.w, -0.95, 0.95) * scatter_storm) / st : p.g;
	p.sigma_A = sigma;
	return p;
}

// Target evaluation including the far-arch air: L = S_near + T_near * (S_remote + T_remote * L_surf), returned as one RingHaze
// (T = T_near T_remote, S = S_near + T_near S_remote). Identical to ring_haze_eval for targets whose ray never re-enters air.
// p_near drives the local air and p_remote the far-arch shell; they differ only under storm haze (the remote shell stays clear).
RingHaze ring_haze_eval_target_split(RingHazeParams p_near, RingHazeParams p_remote, vec3 raw_to_sun, float hub_weight, float hub_day, float solar_visibility, vec3 target, vec3 camera, float camera_height, vec3 outward, vec3 axis, float side_negative, float side_positive, float coordinate_scale, float radius_m, float floor_depth_m) {
	RingHaze near_air = ring_haze_eval(p_near, target, camera, camera_height, outward, axis, side_negative, side_positive, coordinate_scale, radius_m, floor_depth_m);
	RingHaze remote = ring_haze_eval_remote(p_remote, raw_to_sun, hub_weight, hub_day, solar_visibility, target, camera, camera_height, outward, axis, side_negative, side_positive, coordinate_scale, radius_m, floor_depth_m);
	near_air.S_sun += near_air.T * remote.S_sun;
	near_air.S_amb += near_air.T * remote.S_amb;
	near_air.T *= remote.T;
	return near_air;
}

RingHaze ring_haze_eval_target(RingHazeParams p, vec3 raw_to_sun, float hub_weight, float hub_day, float solar_visibility, vec3 target, vec3 camera, float camera_height, vec3 outward, vec3 axis, float side_negative, float side_positive, float coordinate_scale, float radius_m, float floor_depth_m) {
	return ring_haze_eval_target_split(p, p, raw_to_sun, hub_weight, hub_day, solar_visibility, target, camera, camera_height, outward, axis, side_negative, side_positive, coordinate_scale, radius_m, floor_depth_m);
}

// Sky variant: infinite ray in direction `dir`, integrated to the shell exit (the cap, or the shell floor for rays that
// go down). Same arguments as ring_haze_eval, with a direction instead of a target.
RingHaze ring_haze_eval_sky(RingHazeParams p, vec3 dir, vec3 camera, float camera_height, vec3 outward, vec3 axis, float side_negative, float side_positive, float coordinate_scale, float radius_m, float floor_depth_m) {
	float scale = max(coordinate_scale, 0.000001);
	vec3 far_target = camera + normalize(dir) * (2.0 * radius_m * scale);
	return ring_haze_eval(p, far_target, camera, camera_height, outward, axis, side_negative, side_positive, scale, radius_m, floor_depth_m);
}

// ---- Profile-driven evaluation (P2b): Sunshine compute and the far cloud transmittance mask ----------------------------------
// These consumers cannot read Godot uniforms/globals, so the CPU packs the physical model into slots 10-15 of the shared 16-vec4
// ring atmosphere profile (slots 0-9 keep their Legacy meaning; RingAtmosphereModel.PackHazeProfile is the single packer):
//   [10] (model_flag, opacity_multiplier, sigma_R.r, sigma_R.g)      model_flag: 0 = Legacy, 1 = Physical
//   [11] (sigma_R.b, H_R, sigma_A, omega_A)
//   [12] (H_A, sigma_M0, omega_M, H_mist)
//   [13] (g, quality, day, E_sun.r)                                  E_sun already includes the sun tint
//   [14] (E_sun.g, E_sun.b, ambient_day.r, ambient_day.g)
//   [15] (ambient_day.b, ambient_night.r, ambient_night.g, ambient_night.b)
// The cap height is slot 1.x (UpperHeightMeters), radius 0.y, floor depth 0.z, outward 7.xyz, camera height 7.w, axis 8.xyz,
// sidewalls 8.w / 9.w, to_sun 9.xyz. Positions are in metres (scale 1). Requires ring_atmosphere_shared.gdshaderinc first.
bool ring_haze_profile_physical(vec4 p[16]) {
	return p[10].x > 0.5;
}

// Day weight packed by the CPU (max(geo * solar_visibility, hub_day), the ringworld_effective_solar_day formula).
float ring_haze_profile_day(vec4 p[16]) {
	return clamp(p[13].z, 0.0, 1.0);
}

RingHaze ring_haze_from_profile(vec3 target, vec3 camera, vec4 p[16], float day) {
	RingHazeParams hp;
	hp.sigma_R = vec3(p[10].z, p[10].w, p[11].x);
	hp.H_R = p[11].y;
	hp.sigma_A = p[11].z;
	hp.omega_A = p[11].w;
	hp.H_A = p[12].x;
	hp.sigma_M0 = p[12].y;
	hp.omega_M = p[12].z;
	hp.H_mist = p[12].w;
	hp.cap_height = p[1].x;
	hp.g = p[13].x;
	hp.quality = int(p[13].y + 0.5);
	hp.E_sun = vec3(p[13].w, p[14].x, p[14].y);
	hp.ambient = vec3(p[14].z, p[14].w, p[15].x);
	hp.night_ambient = p[15].yzw;
	hp.to_sun = normalize(p[9].xyz);
	hp.day = day;
	RingHaze haze = ring_haze_eval(hp, target, camera, p[7].w, p[7].xyz, p[8].xyz, p[8].w, p[9].w, 1.0, p[0].y, p[0].z);
	// Same per-consumer density multiplier as ring_haze_sample: tau' = m * tau.
	float m = p[10].y;
	if (abs(m - 1.0) > 0.0001) {
		vec3 t_scaled = exp(m * log(max(haze.T, vec3(0.000001))));
		vec3 gain = (vec3(1.0) - t_scaled) / max(vec3(1.0) - haze.T, vec3(0.000001));
		haze.T = t_scaled;
		haze.S_sun *= gain;
		haze.S_amb *= gain;
	}
	return haze;
}
