shader_type fog;
// Near ground fog froxel shader (docs/sun-flare-shafts-near-fog-plan.md, NF-1 design N2 density, N4 lighting, NF-5b).
// Owned by scripts/Atmosphere/NearFog/RingNearFog.cs, which sets every uniform (material uniforms, not globals) and registers this material with RingCloudShadowProjector.
// EMISSION = 0: the ambient comes from the fog fill light (RingNearFog.UpdateFill). ALBEDO = omega (V S + A) / (S + A) (ring_near_fog_albedo_eff): V = haze sun transmittance x cloud shadow x terrain height shadow
// (or the planet light's factors at night), S the unshadowed light in-scatter, A the ambient the fill light adds. Density and albedo are stochastically rounded (ring_near_fog_round_*) so the engine's
// fixed-point storage is unbiased on average. The lighting is ring_near_fog_light.gdshaderinc, shared with the fog-end compute.
#include "res://materials/ring_near_fog_fields.gdshaderinc"
#include "res://materials/ring_atmosphere_shared.gdshaderinc"
#include "res://materials/ring_haze_shared.gdshaderinc"
#include "res://materials/ring_cloud_shadow.gdshaderinc"

uniform vec3 drive = vec3(0.0); // smoothed drivers (radiation, mist, steam)
uniform float amount = 0.0;
uniform float gate = 0.0; // owner fade x fast-travel / altitude / enclosure gates
uniform float layer_h = 40.0; // "Fog layer height" H, metres
uniform vec2 noise_offset = vec2(0.0); // metres mod 4096: anchor around/across plus wind advection
uniform float window_length = 220.0; // the Environment's volumetric fog length
uniform vec3 camera_world = vec3(0.0);
uniform float debug_sigma = -1.0; // calibration only (RingNearFog.DebugUniformSigma): >= 0 replaces the density with this constant per metre (no terrain terms, no noise)
// Lighting inputs (see ring_near_fog_light.gdshaderinc); the same numbers feed the fog-end compute's UBO.
uniform vec4 nf_haze0 = vec4(5.802e-6, 13.558e-6, 33.1e-6, 8000.0);
uniform vec4 nf_haze1 = vec4(4.175e-6, 1500.0, 20000.0, 0.11);
uniform vec4 nf_light0 = vec4(0.0, 0.0, 1.0, 1.0);
uniform vec4 nf_light1 = vec4(0.0, 0.0, 1.0, 0.0);
uniform vec4 nf_light2 = vec4(0.0); // rgb sun colour x energy x pi x volumetric fog energy, w the Environment's anisotropy g
uniform vec4 nf_planet_dir = vec4(0.0, 1.0, 0.0, 0.0);
uniform vec4 nf_planet_rad = vec4(0.0); // rgb planet light colour x energy x pi x volumetric fog energy
uniform vec4 nf_ambient = vec4(0.0); // rgb A, the ambient in-scatter radiance (published_fog_ambient; the fog fill light adds exactly this to every froxel)
uniform vec4 nf_t_override = vec4(0.0); // camera-value variant of the haze sun transmittance (RingNearFog.DebugHazeCameraValue); w <= 0.5 = per froxel

#define RING_NEAR_FOG_HAZE0 nf_haze0
#define RING_NEAR_FOG_HAZE1 nf_haze1
#define RING_NEAR_FOG_LIGHT0 nf_light0
#define RING_NEAR_FOG_LIGHT1 nf_light1
#define RING_NEAR_FOG_SUN_RAD nf_light2.rgb
#define RING_NEAR_FOG_G nf_light2.w
#define RING_NEAR_FOG_PLANET_DIR nf_planet_dir.xyz
#define RING_NEAR_FOG_PLANET_RAD nf_planet_rad.rgb
#define RING_NEAR_FOG_TSUN_OVERRIDE nf_t_override

// The ground's cloud shadow (the Sunshine near cascade through the projector), faded by the day factor like combined_cloud_shadow_occlusion.
float ring_near_fog_cloud_visibility(vec3 world, float day) {
	return 1.0 - clamp(sample_local_cloud_shadow(world) * day, 0.0, 1.0);
}
#include "res://materials/ring_near_fog_light.gdshaderinc"

void fog() {
	float rho;
	vec3 fp = vec3(0.0);
	if (debug_sigma >= 0.0) {
		rho = amount * gate * debug_sigma;
		fp = ring_near_fog_frame_point(WORLD_POSITION);
	} else {
		fp = ring_near_fog_frame_point(WORLD_POSITION);
		float fade = 1.0 - smoothstep(0.6 * window_length, window_length, distance(WORLD_POSITION, camera_world));
		rho = amount * gate * ring_near_fog_sample(fp, drive, layer_h, noise_offset, fade);
	}
	vec4 u = ring_near_fog_dither(WORLD_POSITION, TIME);
	float rho_out = ring_near_fog_round_density(rho, u.w);
	DENSITY = rho_out;
	vec3 albedo = vec3(0.0);
	if (rho_out > 0.0) {
		float t0 = ring_near_fog_fields_valid(fp.xy) ? ring_near_fog_t0(fp.xy) : fp.z;
		float cam_dist = distance(WORLD_POSITION, camera_world);
		vec3 view_dir = (WORLD_POSITION - camera_world) / max(cam_dist, 0.001);
		vec3 v = ring_near_fog_visibility_at(WORLD_POSITION, fp.z, t0, cam_dist);
		vec3 s = ring_near_fog_light_s(view_dir);
		albedo = ring_near_fog_albedo_eff(RING_NEAR_FOG_OMEGA, v, s, nf_ambient.rgb);
		albedo = ring_near_fog_round_albedo(albedo, rho_out, u.xyz);
	}
	ALBEDO = albedo;
	EMISSION = vec3(0.0);
}
