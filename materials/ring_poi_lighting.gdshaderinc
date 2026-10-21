#ifndef RING_POI_LIGHTING
#define RING_POI_LIGHTING
// Local-daylight lighting contract for POI architecture (ring_poi_architecture.gdshaderinc) and the emitter stand-in. The sun DirectionalLight stays at full
// day energy across the whole ring, so surfaces go dark at local sunset only through their own light(): terrain (ring_terrain_lighting.gdshaderinc) and trees
// (imported_tree.gdshader) scale the primary sun by a per-fragment day factor, and POI architecture does the same with the values below.
// Fed by RingPoiHaze (always on, independent of any haze setting). Without poi_ring_lighting_valid (standalone scenes) the day factor is 1 and the
// direct light is plain Lambert + GGX with no night tint.
// Requires ring_atmosphere.gdshaderinc (ring_atmosphere_to_sun, ringworld_effective_solar_day).
uniform bool poi_ring_lighting_valid = false;
uniform vec3 poi_ring_center_world = vec3(0.0);
// x = day start dot, y = day full dot, z = night direct light factor, w = day direct light factor (VoxelChunkGridManager.RingDayStartDot ... RingDayDirectLightFactor).
uniform vec4 poi_lighting_params = vec4(-0.2, 0.35, 0.0, 1.0);
uniform vec3 poi_night_tint = vec3(0.30, 0.45, 0.70);
uniform float poi_shadow_floor : hint_range(0.0, 1.0) = 0.0;

// Local zenith: the inward radial with the ring-axis (Y) component removed, exactly as imported_tree.gdshader and the stand-in compute it.
vec3 poi_local_up(vec3 world) {
	vec3 radial = world - poi_ring_center_world;
	radial.y = 0.0;
	return length(radial) > 0.0001 ? -normalize(radial) : vec3(-1.0, 0.0, 0.0);
}

float poi_local_day(vec3 world) {
	return ringworld_effective_solar_day(smoothstep(
		poi_lighting_params.x,
		poi_lighting_params.y,
		dot(poi_local_up(world), normalize(ring_atmosphere_to_sun))));
}

// Isotropic Schlick-GGX direct specular (Godot Forward+ scene_forward_lights_inc.glsl, MIT), the same port the terrain uses (terrain3d_ring_surface.gdshader).
float poi_d_ggx(float no_h, float alpha) {
	float a = no_h * alpha;
	float k = alpha / max(1.0 - no_h * no_h + a * a, 0.000001);
	return min(k * k * (1.0 / PI), 65504.0);
}

float poi_v_ggx(float no_l, float no_v, float alpha) {
	return min(0.5 / max(mix(2.0 * no_l * no_v, no_l + no_v, alpha), 0.000001), 65504.0);
}

float poi_schlick_fresnel(float u) {
	float m = 1.0 - u;
	float m2 = m * m;
	return m2 * m2 * m;
}

// Specular radiance factor of one light (multiply by LIGHT_COLOR, the visibility and SPECULAR_AMOUNT). f0 = vec3(0.16 * spec^2) for dielectrics, ALBEDO for metals.
vec3 poi_ggx_specular(vec3 normal, vec3 light, vec3 view, float roughness, vec3 f0) {
	float n_dot_l = max(dot(normal, light), 0.0);
	if (roughness <= 0.0 || n_dot_l <= 0.0) return vec3(0.0);
	vec3 half_vector = normalize(view + light);
	float n_dot_v = max(dot(normal, view), 0.0001);
	float n_dot_h = clamp(dot(normal, half_vector), 0.0, 1.0);
	float l_dot_h = clamp(dot(light, half_vector), 0.0, 1.0);
	float alpha_ggx = roughness * roughness;
	float f90 = clamp(dot(f0, vec3(50.0 * 0.33)), 0.0, 1.0);
	vec3 fresnel = f0 + (vec3(f90) - f0) * poi_schlick_fresnel(l_dot_h);
	return n_dot_l * poi_d_ggx(n_dot_h, alpha_ggx) * poi_v_ggx(n_dot_l, n_dot_v, alpha_ggx) * fresnel;
}
#endif
