// Stand-in for the terrain3d_ring_surface y-projection albedo (docs/grass-cards-redesign.md section 2).
// Same texture, 5 m period (uv = world.xz * 0.2) and the same grass multiplier as the real surface shader.
vec3 spike_grass_tint(vec3 c) {
	return mix(c, c * vec3(.72, 1.30, .62), .58);
}
