// Per-instance vegetation haze terms (Physical model only). Portable: compiles as Godot shader language (materials/ring_vegetation_haze.gdshaderinc,
// the grass-card and tree shaders) and as raw GLSL 450 (materials/vegetation_gpu/ring_gpu_vegetation_indirect.glsl, tools/VegetationHazeGpuTest.cs).
// Include AFTER ring_atmosphere_shared.gdshaderinc and ring_haze_shared.gdshaderinc. Reads no uniforms or globals.
//
// The GPU vegetation compute evaluates ring_haze_eval once per instance anchor (grass: root, trees: mesh AABB centre) and stores the parts
// of the result that do not depend on E_sun, the day weight or the ambient colours, so the vertex shader can assemble T / S_sun / S_amb with
// the LIVE lighting globals (cloud shadowing, storm sun occlusion and day changes never force a recompute):
//   L_R         Rayleigh layer integral, Integral exp(-h/H_R) dt (m)          tau_R = sigma_R * L_R (per channel, sigma_R unstormed uniform)
//   mie_ext     tau_A + tau_M (storm already applied to sigma_A)
//   mie_scatter omega_A tau_A + omega_M tau_M
//   K_sun       t_sun * (tau_R P_R(mu) + mie_scatter P_M(mu, g)) / tau_total  (per channel; mu, g and t_sun at compute time)
// Assembly (identical to ring_haze_eval, and to the ring_haze_sample opacity-multiplier tail for m != 1):
//   tau = m (tau_R + mie_ext);  T = exp(-tau);  S_sun = (1 - T) E_sun day K_sun;  S_amb = (1 - T) mix(night, amb, day) (tau_R + mie_scatter) / (tau_R + mie_ext)
// Not covered: the P10 far-arch remote shell (vegetation rays are far shorter than the 438 km minimum remote ray) and the eclipse hub sun blend
// (callers fall back to ring_haze_sample while ringworld_hub_light_weight > 0).
//
// Atlas layout (RGBA16F, width RING_VEGETATION_HAZE_ATLAS_WIDTH texels, two texels per slot, 1024 slots per row):
//   texel0 = (L_R * 1e-3 [km], mie_ext * 1e3, mie_scatter * 1e3, tag)     texel1 = (K_sun.rgb, 1)
// The per-mille / km scaling keeps the values inside the half-float normal range; tag (2..2047, exact in half) identifies the allocation that
// wrote the slot, so a stale or reallocated slot never matches the reader's tag.

const int RING_VEGETATION_HAZE_ATLAS_WIDTH = 2048;
const int RING_VEGETATION_HAZE_SLOTS_PER_ROW = 1024;
const float RING_VEGETATION_HAZE_HALF_MAX = 65000.0;

struct RingVegetationHazeTerms {
	float L_R;
	float mie_ext;
	float mie_scatter;
	vec3 K_sun;
};

// Texel of the first of the slot's two texels.
ivec2 ring_vegetation_haze_texel(int slot) {
	return ivec2((slot % RING_VEGETATION_HAZE_SLOTS_PER_ROW) * 2, slot / RING_VEGETATION_HAZE_SLOTS_PER_ROW);
}

// ring_haze_eval without E_sun / day / ambient (same operations up to the lighting multiply). p must already carry the storm (ring_haze_apply_storm).
RingVegetationHazeTerms ring_vegetation_haze_terms(RingHazeParams p, vec3 target, vec3 camera, float camera_height, vec3 outward, vec3 axis, float side_negative, float side_positive, float coordinate_scale, float radius_m, float floor_depth_m) {
	RingVegetationHazeTerms t;
	t.L_R = 0.0;
	t.mie_ext = 0.0;
	t.mie_scatter = 0.0;
	t.K_sun = vec3(0.0);
	float scale = max(coordinate_scale, 0.000001);
	vec3 delta = target - camera;
	if (length(delta) <= 0.0001 * scale) return t;
	vec3 ray = normalize(delta);
	vec2 interval = ring_haze_air_interval(target, camera, camera_height, outward, axis, side_negative, side_positive, scale, radius_m, floor_depth_m, p.cap_height);
	if (interval.y <= interval.x) return t;
	// ring_haze_optics, keeping the Rayleigh layer integral as a scalar.
	vec3 axis_n = normalize(axis);
	vec3 outward_n = normalize(outward);
	vec3 radial_ray = ray - axis_n * dot(ray, axis_n);
	float radial0 = radius_m - camera_height;
	float b = 2.0 * radial0 * dot(outward_n, ray);
	float a = dot(radial_ray, radial_ray);
	vec3 layers = ring_haze_layer_integrals(p, camera_height, radial0, b, a, interval.x, interval.y);
	float tau_A = p.sigma_A * layers.y;
	float tau_M = p.sigma_M0 * layers.z;
	vec3 tau_R = p.sigma_R * layers.x;
	t.L_R = layers.x;
	t.mie_ext = tau_A + tau_M;
	t.mie_scatter = p.omega_A * tau_A + p.omega_M * tau_M;
	vec3 tau_total = tau_R + vec3(t.mie_ext);
	vec3 inv_total = vec3(1.0) / max(tau_total, vec3(0.00000001));
	float mu = dot(ray, normalize(p.to_sun));
	vec3 weight_sun = tau_R * ring_haze_phase_rayleigh(mu) + vec3(t.mie_scatter * ring_haze_phase_mie(mu, p.g));
	vec3 t_sun = ring_haze_sun_transmittance(p, ray, interval, camera_height, outward, axis, radius_m);
	t.K_sun = t_sun * weight_sun * inv_total;
	return t;
}

vec4 ring_vegetation_haze_pack0(RingVegetationHazeTerms t, float tag) {
	return vec4(
		clamp(t.L_R * 0.001, 0.0, RING_VEGETATION_HAZE_HALF_MAX),
		clamp(t.mie_ext * 1000.0, 0.0, RING_VEGETATION_HAZE_HALF_MAX),
		clamp(t.mie_scatter * 1000.0, 0.0, RING_VEGETATION_HAZE_HALF_MAX),
		tag);
}

vec4 ring_vegetation_haze_pack1(RingVegetationHazeTerms t) {
	return vec4(clamp(t.K_sun, vec3(0.0), vec3(RING_VEGETATION_HAZE_HALF_MAX)), 1.0);
}

RingVegetationHazeTerms ring_vegetation_haze_unpack(vec4 texel0, vec4 texel1) {
	RingVegetationHazeTerms t;
	t.L_R = texel0.x * 1000.0;
	t.mie_ext = texel0.y * 0.001;
	t.mie_scatter = texel0.z * 0.001;
	t.K_sun = texel1.xyz;
	return t;
}

// T / S from stored terms with the caller's live lighting (p.sigma_R, p.E_sun, p.day, p.ambient, p.night_ambient) and the per-material
// opacity multiplier m (tau' = m tau, the ring_haze_sample tail).
RingHaze ring_vegetation_haze_assemble(RingHazeParams p, RingVegetationHazeTerms t, float opacity_multiplier) {
	vec3 tau_R = p.sigma_R * t.L_R;
	vec3 tau_total = tau_R + vec3(t.mie_ext);
	vec3 tau = tau_total * max(opacity_multiplier, 0.0);
	RingHaze haze;
	haze.T = exp(-tau);
	vec3 one_minus_T = vec3(ring_haze_one_minus_exp(tau.x), ring_haze_one_minus_exp(tau.y), ring_haze_one_minus_exp(tau.z));
	float day = clamp(p.day, 0.0, 1.0);
	haze.S_sun = one_minus_T * p.E_sun * day * t.K_sun;
	haze.S_amb = one_minus_T * mix(p.night_ambient, p.ambient, day) * (tau_R + vec3(t.mie_scatter)) / max(tau_total, vec3(0.00000001));
	return haze;
}
