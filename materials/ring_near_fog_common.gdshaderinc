#ifndef RING_NEAR_FOG_COMMON
#define RING_NEAR_FOG_COMMON
// Near ground fog: the density and lighting functions shared by the fog shader (materials/ring_near_fog.gdshader) and, later, the fog-end compute
// (RingAtmosphereLuts.ExpandIncludes). Pure math only: plain parameters, no globals, no Godot-only built-ins, so this file expands into GLSL unchanged.
// docs/sun-flare-shafts-near-fog-plan.md, "NF-1 design" (N2 density). Design constants below are data, not menu rows.

// Per-metre extinction of the three kinds (x = radiation, y = mist, z = steam), and the lid edge softness.
const vec3 RING_NEAR_FOG_SIGMA = vec3(0.01, 0.002, 0.01);
const float RING_NEAR_FOG_LID_SOFTNESS = 0.15;
// Noise contrast per kind (radiation, mist, steam wisps), the noise lattice period (m) and its two octave cell sizes (m).
const vec3 RING_NEAR_FOG_CONTRAST = vec3(0.5, 0.5, 0.9);
const float RING_NEAR_FOG_NOISE_PERIOD = 4096.0;
const float RING_NEAR_FOG_NOISE_CELL_A = 32.0; // 128 cells per period
const float RING_NEAR_FOG_NOISE_CELL_B = 8.0;  // 512 cells per period
const float RING_NEAR_FOG_STEAM_LAYER_H = 6.0;

// Smooth flat-topped layer profile: 1 below u = 1, 0 above, soft edge about 0.15 wide. u = height above the layer floor / layer height.
float ring_near_fog_lid(float u) {
	return 1.0 / (1.0 + exp((u - 1.0) / RING_NEAR_FOG_LID_SOFTNESS));
}

// Integer-lattice hash in [-1, 1].
float ring_near_fog_hash(ivec3 c) {
	uvec3 u = uvec3(c);
	uint h = u.x * 374761393u + u.y * 668265263u + u.z * 2147483647u;
	h = (h ^ (h >> 13u)) * 1274126177u;
	h = h ^ (h >> 16u);
	return float(h & 16777215u) / 8388607.5 - 1.0;
}

// One octave of value noise, cell metres `cell`, lattice wrapped to the 4096 m period (cells = period / cell, a power of two), smooth interpolation. In [-1, 1].
float ring_near_fog_value_noise(vec3 q, float cell) {
	vec3 x = q / cell;
	vec3 base = floor(x);
	vec3 f = x - base;
	f = f * f * (3.0 - 2.0 * f);
	int mask = int(RING_NEAR_FOG_NOISE_PERIOD / cell) - 1;
	ivec3 i0 = ivec3(base);
	float n000 = ring_near_fog_hash(ivec3(i0.x & mask, i0.y & mask, i0.z & mask));
	float n100 = ring_near_fog_hash(ivec3((i0.x + 1) & mask, i0.y & mask, i0.z & mask));
	float n010 = ring_near_fog_hash(ivec3(i0.x & mask, (i0.y + 1) & mask, i0.z & mask));
	float n110 = ring_near_fog_hash(ivec3((i0.x + 1) & mask, (i0.y + 1) & mask, i0.z & mask));
	float n001 = ring_near_fog_hash(ivec3(i0.x & mask, i0.y & mask, (i0.z + 1) & mask));
	float n101 = ring_near_fog_hash(ivec3((i0.x + 1) & mask, i0.y & mask, (i0.z + 1) & mask));
	float n011 = ring_near_fog_hash(ivec3(i0.x & mask, (i0.y + 1) & mask, (i0.z + 1) & mask));
	float n111 = ring_near_fog_hash(ivec3((i0.x + 1) & mask, (i0.y + 1) & mask, (i0.z + 1) & mask));
	return mix(mix(mix(n000, n100, f.x), mix(n010, n110, f.x), f.y), mix(mix(n001, n101, f.x), mix(n011, n111, f.x), f.y), f.z);
}

// Two-octave value noise (32 m and 8 m, weights 0.65 / 0.35) in [-1, 1]. q = (around, across, height) in metres, already offset by the caller (anchor + wind advection, mod 4096),
// so the pattern is continuous across anchor recentres. The caller multiplies by its distance fade (1 at the camera, 0 over the last 40 % of the window).
float ring_near_fog_noise(vec3 q) {
	return 0.65 * ring_near_fog_value_noise(q, RING_NEAR_FOG_NOISE_CELL_A) + 0.35 * ring_near_fog_value_noise(q, RING_NEAR_FOG_NOISE_CELL_B);
}

// N2 density, per metre, before amount and gate. z = canonical height of the sample; t0, basin = terrain height and basin floor at its column; water = (mask, surface height);
// drive = (D_rad, D_mist, D_steam); sigma = per-kind extinction (RING_NEAR_FOG_SIGMA); layer_h = the radiation layer height H; noise = raw noise in [-1, 1] (already distance-faded).
// Radiation lies H deep above the basin floor with a flat lid; mist hugs the ground (H_m = clamp(0.35 H, 5, 40), measured from T0, 0 below ground); steam lies 6 m over water.
float ring_near_fog_density(float z, float t0, float basin, vec2 water, vec3 drive, vec3 sigma, float layer_h, float noise) {
	float h_m = clamp(0.35 * layer_h, 5.0, 40.0);
	float rad = sigma.x * drive.x * ring_near_fog_lid((z - basin) / layer_h) * max(0.0, 1.0 + RING_NEAR_FOG_CONTRAST.x * noise);
	float mist = sigma.y * drive.y * ring_near_fog_lid(max(z - t0, 0.0) / h_m) * max(0.0, 1.0 + RING_NEAR_FOG_CONTRAST.y * noise);
	float steam = sigma.z * drive.z * water.x * ring_near_fog_lid((z - water.y) / RING_NEAR_FOG_STEAM_LAYER_H) * max(0.0, 1.0 + RING_NEAR_FOG_CONTRAST.z * noise);
	return rad + mist + steam;
}

// Lighting of the fog (NF-5b), one place for the froxel shader and the fog-end compute. Godot's volumetric fog adds per froxel
//   total_light x scattering + emission,   scattering = ALBEDO x clamp(density, 0, 1),   total_light = sum over lights (colour x energy x HG(cos) x volumetric energy x shadow), the fog fill light among them
// (servers/rendering/renderer_rd/shaders/environment/volumetric_fog_process.glsl). EMISSION is quantised away at production density (its floor is about 0.002 per metre), so the fog uses EMISSION = 0 and
// delivers the ambient as a fog fill light (RingNearFog.UpdateFill) that adds F = A to every froxel's total_light. The visibility V (haze sun transmittance x cloud shadow x height shadow) must scale the
// directional light term but not that ambient, so the albedo is the ratio ring_near_fog_albedo_eff: with S the unshadowed light in-scatter the engine adds and A the fill light's, ALBEDO_eff (S + A) = omega (V S + A).
// Exact where only V shadows a froxel (the engine adds S + A) and where only the engine's shadow atlas does (V = 1: ALBEDO_eff = omega, the engine adds S_atlas + A). Residual where both do (tree shafts under a
// cloud or terrain shadow): the engine adds S_atlas + A, giving omega (V S + A) (S_atlas + A) / (S + A) instead of omega (V S_atlas + A); the error is of the order of the product of both shadow depths times the ambient share.
const float RING_NEAR_FOG_OMEGA = 0.99;

// Henyey-Greenstein phase, the engine's normalisation (1 / 4 pi). cos_theta = dot(view ray direction (camera to the point), direction toward the light), as the engine's froxel pass does.
float ring_near_fog_hg(float cos_theta, float g) {
	return 0.0795774715459 * (1.0 - g * g) / pow(1.0 + g * g - 2.0 * g * cos_theta, 1.5);
}

// V_light, the visibility the albedo applies to every light the froxel adds (the sun and the planet light scale by the same factor). By day (day = 1) the sun's factors: haze sun transmittance t_sun x terrain height
// shadow v_height_sun; at night (day = 0) the planet light's: its height shadow v_height_planet (the lattice follows whichever light it was built for, so at most one of the two is below 1). The cloud shadow
// v_cloud scales both (its occlusion is already faded by the day factor, like the ground's), and the Godot shadow atlas adds the tree and POI shafts by itself.
vec3 ring_near_fog_visibility(float day, vec3 t_sun, float v_cloud, float v_height_sun, float v_height_planet) {
	return vec3(v_cloud) * mix(vec3(v_height_planet), t_sun * v_height_sun, clamp(day, 0.0, 1.0));
}

// Froxel ALBEDO, per channel: omega (V S + A) / (S + A), clamped to [0, 1]. s = unshadowed light in-scatter, a = the fill light's ambient in-scatter.
vec3 ring_near_fog_albedo_eff(float omega, vec3 v, vec3 s, vec3 a) {
	return clamp(omega * (v * s + a) / max(s + a, vec3(1e-7)), vec3(0.0), vec3(1.0));
}

// Fog-end in-scatter per unit (1 - transmittance): omega (V S + A), A the ambient.
vec3 ring_near_fog_inscatter(float omega, vec3 v, vec3 s, vec3 a) {
	return omega * (v * s + a);
}

// Unbiased stochastic rounding for the engine's fixed-point storage (volumetric_fog.glsl: density in 1 / 1024 steps, truncated, |density| <= 0.001 dropped; albedo x density in 1 / 2047 (r, g) and
// 1 / 1023 (b) steps, truncated). u in [0, 1) per froxel and frame; temporal reprojection averages the noise. Density: below 1 / 1024 a froxel is either dropped or stored as 1 / 1024 (emitted as 0.00102
// so it passes the drop test) with probability rho x 1024; above it rho + u / 1024 truncates to floor(rho x 1024 + u) steps, whose mean is rho. Albedo: the engine stores ALBEDO x rho_out (the
// emitted density) but divides by the STORED density floor(rho_out x 1024) / 1024 in the integration, so the scattering is targeted at albedo x stored (+ u_c / steps for the truncation) and divided by
// rho_out; otherwise the effective albedo is rho_out / stored too high (about 10 % at 0.004 per metre, the NF-5b composition bias).
const float RING_NEAR_FOG_DENSITY_STEP = 1.0 / 1024.0;

float ring_near_fog_round_density(float rho, float u) {
	if (rho <= 0.0) return 0.0;
	if (rho < RING_NEAR_FOG_DENSITY_STEP) return u < rho * 1024.0 ? 0.00102 : 0.0;
	return max(rho + u * RING_NEAR_FOG_DENSITY_STEP, 0.00102);
}

vec3 ring_near_fog_round_albedo(vec3 albedo, float rho_out, vec3 u) {
	float d = clamp(rho_out, 0.0, 1.0);
	if (d <= 0.0) return albedo;
	float stored = min(floor(rho_out * 1024.0) * RING_NEAR_FOG_DENSITY_STEP, 1.0);
	return (albedo * stored + u / vec3(2047.0, 2047.0, 1023.0)) / d;
}

// Four decorrelated uniforms in [0, 1) from a world position (the froxel centre the engine jitters per frame) and the time.
vec4 ring_near_fog_dither(vec3 world, float time) {
	uvec4 v = uvec4(floatBitsToUint(world.x), floatBitsToUint(world.y), floatBitsToUint(world.z), floatBitsToUint(time));
	v = v * 1664525u + 1013904223u;
	v.x += v.y * v.w; v.y += v.z * v.x; v.z += v.x * v.y; v.w += v.y * v.z;
	v ^= v >> 16u;
	v.x += v.y * v.w; v.y += v.z * v.x; v.z += v.x * v.y; v.w += v.y * v.z;
	return vec4(v >> 8u) * (1.0 / 16777216.0);
}
#endif
