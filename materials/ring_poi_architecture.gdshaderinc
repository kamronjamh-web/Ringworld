// POI architecture body shared by ring_poi_architecture.gdshader (cull_back) and ring_poi_architecture_double_sided.gdshader
// (cull_disabled). Replaces StandardMaterial3D on emitter / spire / reactor / Weather Lung architecture so the surface carries the
// ring haze per fragment (POI triangles span km; the haze is strongly non-linear along them).
// Uniform names/typing match StandardMaterial3D so the converter (tools/poi/poi_material_util.gd) maps one to one.
// Consumer contract: L = L_surf * T + S_sun + S_amb. Without a valid ray frame (standalone scenes) the haze is identity,
// which reproduces the old StandardMaterial3D look exactly.
uniform vec4 albedo_color : source_color = vec4(1.0);
uniform float metallic_value : hint_range(0.0, 1.0) = 0.0;
uniform float roughness_value : hint_range(0.0, 1.0) = 1.0;
uniform float specular_value : hint_range(0.0, 1.0) = 0.5;
uniform vec4 emission_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform float emission_energy = 1.0;
uniform bool poi_haze_enabled = true;
uniform bool poi_cloud_shadow_enabled = false;
#include "res://materials/ring_atmosphere.gdshaderinc"
#include "res://materials/ring_cloud_shadow.gdshaderinc"
#include "res://materials/ring_shadow_compose.gdshaderinc"

varying vec3 poi_world_position;

void vertex() {
	poi_world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	RingHaze haze;
	haze.T = vec3(1.0);
	haze.S_sun = vec3(0.0);
	haze.S_amb = vec3(0.0);
	if (poi_haze_enabled && ring_atmosphere_ray_frame_valid) {
		// center/day only feed the frame-less fallback, which this branch never reaches.
		haze = ring_haze_sample(ring_atmosphere_camera_world_position, poi_world_position, vec3(0.0), 1.0);
	}
	ALBEDO = albedo_color.rgb * haze.T;
	METALLIC = metallic_value;
	ROUGHNESS = roughness_value;
	// Dielectric F0 scales with SPECULAR^2, so sqrt(mean T) attenuates it linearly by T.
	SPECULAR = specular_value * sqrt(dot(haze.T, vec3(1.0 / 3.0)));
	EMISSION = emission_color.rgb * emission_energy * haze.T + ring_haze_inscatter(haze);
	if (poi_cloud_shadow_enabled && ring_atmosphere_ray_frame_valid) {
		// Stand-in style: darken ambient and every light by the cloud shadow, scaled by the camera-local day.
		float camera_day = ringworld_effective_solar_day(smoothstep(-0.2, 0.35,
			dot(-normalize(ring_atmosphere_outward_world), normalize(ring_atmosphere_to_sun))));
		float cloud_occlusion = sample_local_cloud_shadow(poi_world_position) * camera_day;
		AO = 1.0 - cloud_occlusion;
		AO_LIGHT_AFFECT = 1.0;
		if (ring_shadow_debug_enabled()) {
			ALBEDO = vec3(0.0);
			SPECULAR = 0.0;
			METALLIC = 0.0;
			AO = 1.0;
			EMISSION = vec3(ringworld_shadow_debug_mode == 1 || ringworld_shadow_debug_mode == 5 ? 1.0 - cloud_occlusion : 1.0);
		}
	}
}
