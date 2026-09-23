// Canonical coordinates are (around km, across km). The sine/cosine pair wraps
// around the ring continuously; the 64-cubed texture supplies smooth value noise.
const vec2 MENU_WIND_KM_PER_SECOND = vec2(0.09, 0.035);
uniform sampler3D menu_noise_texture : filter_linear, repeat_enable;

float menu_noise(vec3 noise_cell) {
    return texture(menu_noise_texture, fract(noise_cell / 64.0)).r;
}

float menu_weather(vec2 canonical_km, float ring_radius_km, float raw_seed, float time_seconds) {
    vec2 moved = canonical_km + MENU_WIND_KM_PER_SECOND * time_seconds;
    float angle = moved.x / ring_radius_km;
    float seed = mod(raw_seed, 4096.0);
    // Twelve-kilometre weather cells expressed in a seam-free cylinder basis.
    vec3 q = vec3(sin(angle) * ring_radius_km / 12.0, cos(angle) * ring_radius_km / 12.0, moved.y / 12.0);
    q += vec3(seed * 0.017, seed * 0.031, seed * 0.047);
    return 0.52 * menu_noise(q) + 0.30 * menu_noise(q * 2.03 + 11.7) + 0.18 * sin(q.x * 1.7 + q.z * 0.6);
}

float menu_cloud_coverage(float weather) {
    return smoothstep(0.34, 0.64, weather);
}
