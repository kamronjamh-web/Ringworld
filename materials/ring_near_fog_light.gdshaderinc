#ifndef RING_NEAR_FOG_LIGHT
#define RING_NEAR_FOG_LIGHT
// Near ground fog lighting at one point (docs/sun-flare-shafts-near-fog-plan.md, NF-1 design N4, NF-5b): the one place the froxel shader (materials/ring_near_fog.gdshader) and the fog-end compute
// (materials/ring_near_fog_end.comp) both call, so they cannot drift. Include AFTER ring_near_fog_fields.gdshaderinc (which brings ring_height_shadow.gdshaderinc), ring_haze_shared.gdshaderinc
// and the includer's own `float ring_near_fog_cloud_visibility(vec3 world, float day)` (the shader wraps sample_local_cloud_shadow; the compute has a twin of it), and after the includer defines:
//   RING_NEAR_FOG_HAZE0   vec4  haze sigma_R.xyz, H_R             (storm already applied to sigma_A by the owner, one source for both consumers)
//   RING_NEAR_FOG_HAZE1   vec4  haze sigma_A, H_A, cap height, ground albedo rho_g
//   RING_NEAR_FOG_LIGHT0  vec4  xyz direction toward the sun (StarLight), w the day weight (ringworld_effective_solar_day of the local geometric day)
//   RING_NEAR_FOG_LIGHT1  vec4  xyz ring up (third row of ringworld_height_shadow_frame)
//   RING_NEAR_FOG_SUN_RAD vec3  sun colour x energy x pi x volumetric fog energy (what the engine's light data holds; 0 while off or gated)
//   RING_NEAR_FOG_G       float the Environment's volumetric fog anisotropy
//   RING_NEAR_FOG_PLANET_DIR / RING_NEAR_FOG_PLANET_RAD  vec3  the planet light's direction toward it and its colour x energy x pi x volumetric fog energy
//   RING_NEAR_FOG_TSUN_OVERRIDE vec4  rgb one haze sun transmittance for the whole fog, used when w > 0.5 (the camera-value variant of RingNearFog.DebugHazeCameraValue; the per-froxel evaluation otherwise)
// world: world position (the lattice frame needs metres, so not camera-relative); z: canonical height; t0: terrain height at the column; cam_dist: distance to the camera (authority rule).
// Cloud shadow, height shadow and the day blend are exactly the ground's rules; the containment field lid on the haze column and the hub light's own direction are not modelled (the fog lies within 250 m of the camera).
#include "res://materials/ring_near_fog_common.gdshaderinc"

// V_light at the point (ring_near_fog_visibility).
vec3 ring_near_fog_visibility_at(vec3 world, float z, float t0, float cam_dist) {
	vec4 h0 = RING_NEAR_FOG_HAZE0;
	vec4 h1 = RING_NEAR_FOG_HAZE1;
	vec4 l0 = RING_NEAR_FOG_LIGHT0;
	RingHazeParams p;
	p.sigma_R = h0.xyz;
	p.H_R = h0.w;
	p.sigma_A = h1.x;
	p.H_A = h1.y;
	p.cap_height = h1.z;
	float cos_sun = dot(l0.xyz, RING_NEAR_FOG_LIGHT1.xyz);
	vec3 t_sun = RING_NEAR_FOG_TSUN_OVERRIDE.w > 0.5 ? RING_NEAR_FOG_TSUN_OVERRIDE.rgb : ring_haze_sun_transmittance_point(p, z, cos_sun);
	float v_cloud = ring_near_fog_cloud_visibility(world, l0.w);
	RingHeightShadow hs = ring_height_shadow_sample(world, max(z - t0, 0.0), 0.0, cam_dist);
	float v_height = mix(1.0, hs.visibility, hs.weight);
	bool planet_layer = ring_height_shadow_planet_active();
	float v_height_sun = planet_layer ? 1.0 : v_height;
	float v_height_planet = planet_layer ? v_height : 1.0;
	return ring_near_fog_visibility(l0.w, t_sun, v_cloud, v_height_sun, v_height_planet);
}

// S, the unshadowed in-scatter magnitude of the directional lights the engine adds for a view ray direction (camera to the point): sum of colour x energy x pi x volumetric energy x HG(cos(view, light), g).
vec3 ring_near_fog_light_s(vec3 view_dir) {
	vec3 s = RING_NEAR_FOG_SUN_RAD * ring_near_fog_hg(dot(view_dir, normalize(RING_NEAR_FOG_LIGHT0.xyz)), RING_NEAR_FOG_G);
	if (dot(RING_NEAR_FOG_PLANET_RAD, RING_NEAR_FOG_PLANET_RAD) > 0.0) {
		s += RING_NEAR_FOG_PLANET_RAD * ring_near_fog_hg(dot(view_dir, normalize(RING_NEAR_FOG_PLANET_DIR)), RING_NEAR_FOG_G);
	}
	return s;
}
#endif
