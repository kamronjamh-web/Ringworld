// POI architecture body shared by ring_poi_architecture.gdshader (cull_back) and ring_poi_architecture_double_sided.gdshader
// (cull_disabled). Replaces StandardMaterial3D on emitter / spire / reactor / Weather Lung architecture so the surface carries the
// ring haze per fragment (POI triangles span km; the haze is strongly non-linear along them) and the same local daylight as terrain and trees.
// Uniform names/typing match StandardMaterial3D so the converter (tools/poi/poi_material_util.gd) maps one to one.
// Consumer contract: L = L_surf * T + S_sun + S_amb. Without a valid ray frame (standalone scenes) the haze is identity, and without
// poi_ring_lighting_valid the direct light is plain Lambert + GGX with no night tint (see ring_poi_lighting.gdshaderinc).
uniform vec4 albedo_color : source_color = vec4(1.0);
uniform float metallic_value : hint_range(0.0, 1.0) = 0.0;
uniform float roughness_value : hint_range(0.0, 1.0) = 1.0;
uniform float specular_value : hint_range(0.0, 1.0) = 0.5;
uniform vec4 emission_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform float emission_energy = 1.0;
// Base ambient occlusion (set per MeshInstance3D by the POI streamers, PoiBaseAmbientOcclusion): the POI's ground anchor in this mesh's own space (so it follows
// pool reuse and floating-origin rebases) and the height over which the
// occlusion fades out. 0 disables it.
instance uniform vec3 poi_base_local = vec3(0.0);
instance uniform float poi_base_ao_height = 0.0;
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_cloud_shadow.gdshaderinc"
#include "res://materials/ring_shadow_compose.gdshaderinc"
#include "res://materials/ring_poi_lighting.gdshaderinc"

// Ambient floor at the ground contact and the share of the direct sun that survives there (both rise to 1 at poi_base_ao_height).
const float BASE_AO_MIN = 0.45;
const float BASE_DIRECT_MIN = 0.75;

varying vec3 poi_world_position;
varying float poi_day;
varying float poi_cloud_occlusion;
varying float poi_base_direct;
varying float poi_specular;

void vertex() {
	poi_world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	RingHaze haze;
	haze.T = vec3(1.0);
	haze.S_sun = vec3(0.0);
	haze.S_amb = vec3(0.0);
	// Local day at this fragment (not the camera's); 1 without lighting data so standalone scenes keep the plain look.
	poi_day = poi_ring_lighting_valid ? poi_local_day(poi_world_position) : 1.0;
	if (ring_atmosphere_ray_frame_valid) {
		// The regular ring haze (ring_atmosphere_enabled and the haze model decide inside); center/day only feed the legacy model.
		haze = ring_haze_sample(ring_atmosphere_camera_world_position, poi_world_position,
			poi_ring_lighting_valid ? poi_ring_center_world : vec3(0.0), poi_day);
	}
	vec3 night_tint = poi_ring_lighting_valid ? mix(poi_night_tint, vec3(1.0), poi_day) : vec3(1.0);
	ALBEDO = albedo_color.rgb * night_tint * haze.T;
	METALLIC = metallic_value;
	ROUGHNESS = roughness_value;
	// Dielectric F0 scales with SPECULAR^2, so sqrt(mean T) attenuates it linearly by T.
	SPECULAR = specular_value * sqrt(dot(haze.T, vec3(1.0 / 3.0)));
	poi_specular = SPECULAR;
	EMISSION = emission_color.rgb * emission_energy * haze.T + ring_haze_inscatter(haze);
	poi_cloud_occlusion = sample_local_cloud_shadow(poi_world_position) * poi_day;
	float base_visibility = 1.0;
	if (poi_base_ao_height > 0.0) {
		base_visibility = smoothstep(0.0, poi_base_ao_height, dot(poi_world_position - (MODEL_MATRIX * vec4(poi_base_local, 1.0)).xyz, poi_local_up(poi_world_position)));
	}
	poi_base_direct = mix(BASE_DIRECT_MIN, 1.0, base_visibility);
	// Cloud shadow darkens the ambient here and the sun in light(); the base occlusion rides on the ambient too.
	AO = (1.0 - poi_cloud_occlusion * 0.72) * mix(BASE_AO_MIN, 1.0, base_visibility);
	AO_LIGHT_AFFECT = 0.0;
	if (ring_shadow_debug_enabled()) {
		// Shadow debug view: only the selected shadow term reaches the screen (see ring_shadow_compose.gdshaderinc).
		ALBEDO = vec3(1.0);
		EMISSION = vec3(0.0);
		AO = 0.0;
		SPECULAR = 0.0;
		poi_specular = 0.0;
		METALLIC = 0.0;
		ROUGHNESS = 1.0;
	}
}

void light() {
	float n_dot_l = max(dot(NORMAL, LIGHT), 0.0);
	float primary_sun = 0.0;
	float direct = 1.0;
	if (poi_ring_lighting_valid) {
		vec3 sun_view = normalize((VIEW_MATRIX * vec4(ring_atmosphere_to_sun, 0.0)).xyz);
		primary_sun = LIGHT_IS_DIRECTIONAL ? smoothstep(0.90, 0.995, dot(normalize(LIGHT), sun_view)) : 0.0;
		direct = mix(poi_lighting_params.z, poi_lighting_params.w, poi_day);
	}
	RingShadowTerms terms = ring_shadow_terms(poi_world_position, SCREEN_UV, max(ATTENUATION, poi_shadow_floor), 0.0, 0.0, RING_SHADOW_DEFAULT);
	terms.cloud = mix(1.0, 1.0 - poi_cloud_occlusion, primary_sun);
	terms.contact = ring_contact_visibility(FRAGCOORD.xy, VIEWPORT_SIZE, INV_PROJECTION_MATRIX, primary_sun, RING_SHADOW_DEFAULT);
	float sun_visibility = ring_shadow_sun_visibility(terms, RING_SHADOW_DEFAULT);
	if (ring_shadow_debug_enabled()) {
		if (LIGHT_IS_DIRECTIONAL && primary_sun > 0.5) {
			DIFFUSE_LIGHT += vec3(ring_shadow_debug_value(terms, sun_visibility));
		}
	} else {
		// Primary sun only: the local-day gate and the base occlusion; the planet light and every other light stay ungated.
		float visibility = sun_visibility * mix(1.0, direct * poi_base_direct, primary_sun);
		DIFFUSE_LIGHT += (LIGHT_COLOR / PI) * n_dot_l * visibility;
		vec3 f0 = mix(vec3(0.16 * poi_specular * poi_specular), ALBEDO, METALLIC);
		SPECULAR_LIGHT += poi_ggx_specular(NORMAL, LIGHT, VIEW, ROUGHNESS, f0) * LIGHT_COLOR * visibility * SPECULAR_AMOUNT;
	}
}
