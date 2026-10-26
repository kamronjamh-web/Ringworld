// Shared lighting and haze of the gust front debris shaders: the same day factor, storm ambient, cloud shadow and per-fragment physical haze every other receiver uses.
// Include AFTER ring_atmosphere.gdshaderinc and ring_cloud_shadow.gdshaderinc.
uniform vec3 ring_center = vec3(0.0);
uniform float deb_day_start_dot = -0.2;
uniform float deb_day_full_dot = 0.35;

float deb_day_factor(vec3 world) {
	vec3 radial = world - ring_center;
	vec3 surface_up = length(radial) > 0.0001 ? -normalize(radial) : vec3(-1.0, 0.0, 0.0);
	return ringworld_effective_solar_day(smoothstep(deb_day_start_dot, deb_day_full_dot, dot(surface_up, normalize(ring_atmosphere_to_sun))));
}

// Lit colour of a particle: albedo through the haze transmittance, haze in-scatter as emission, ambient limited by cloud shadow and the storm ambient.
void deb_shade(vec3 world, float day, vec3 albedo, out vec3 out_albedo, out vec3 out_emission, out float out_ao) {
	RingHaze haze = ring_haze_sample(ring_atmosphere_camera_world_position, world, ring_center, day);
	out_albedo = albedo * haze.T;
	out_emission = ring_haze_inscatter(haze);
	float cloud_occlusion = sample_local_cloud_shadow(world) * day;
	out_ao = min(1.0 - cloud_occlusion, ring_storm_ambient(ring_storm_level(world)));
}
