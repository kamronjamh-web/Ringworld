#ifndef RING_RAIN_RIPPLES
#define RING_RAIN_RIPPLES
// Rain ripples (docs/storm-revamp-s2-wet-world-design.md 2.3 / 3.1). Lagarde's baked-atlas technique: ring_wet_ripple_tex (RingWetWorld.Textures.cs, RGBA8, tiling,
// mipped) stores per texel the distance to its cell's drop (R, 0..1 over the maximum ring radius), the outward direction (GB) and the drop's time offset (A). A layer
// evaluates an expanding ring with a fading amplitude at the world-anchored plane position; layers are offset in space and time.
// Active rain layers scale with the rain (1 drizzle .. 4 downpour) capped by the preset (ring_wet_clock.y); fat drops add one sparse layer of large slow rings; hail adds
// a fast layer of sharp short-lived crowns. Zero beyond 40 m. The clock is the weather sim clock modulo 64 s (every rate below divides 64 s), so a paused clock gives identical frames.
global uniform sampler2D ring_wet_ripple_tex : filter_linear_mipmap, repeat_enable;
global uniform vec4 ring_wet_clock; // x: ripple phase (sim clock mod 64 s), y: ripple layers (1..4), z: wet window half extent (m), w: far blend (m)

// One layer: gradient of the ring height in plane axes (around, across). tile_m divides 16384 m, rate (cycles/s) times 64 is an integer, lifetime is the visible share of a cycle.
vec2 ring_rain_ripple_layer(vec2 plane_m, float tile_m, float rate, float lifetime, float seed, float lod) {
	vec2 uv = plane_m / tile_m + vec2(seed * 0.37, seed * 0.61);
	vec4 t = textureLod(ring_wet_ripple_tex, uv, lod);
	float age = fract(ring_wet_clock.x * rate + t.a + seed * 0.29) / lifetime;
	if (age >= 1.0) return vec2(0.0);
	float envelope = (1.0 - age) * (1.0 - age);
	float x = t.r - age;
	float ring = exp(-x * x / 0.0050) * sin(x * 46.0);
	return (t.gb * 2.0 - 1.0) * (ring * envelope * step(t.r, 0.999));
}

// Gradient of the water surface from ripples at a plane position. rain 0..1 (already times exposure), fat and hail 0..1 weights (ring_wet_events), view distance in m.
vec2 ring_rain_ripple_gradient(vec2 plane_m, float rain, float fat, float hail, float view_distance) {
	float fade = 1.0 - smoothstep(25.0, 40.0, view_distance);
	if (fade <= 0.0) return vec2(0.0);
	float lod = clamp(log2(max(view_distance, 1.0) / 8.0), 0.0, 4.0);
	int layers = int(ring_wet_clock.y + 0.5);
	vec2 g = vec2(0.0);
	if (rain > 0.0) {
		float w0 = clamp(rain * 6.0, 0.0, 1.0);
		g += w0 * ring_rain_ripple_layer(plane_m, 4.0, 0.625, 1.0, 0.0, lod);
		if (layers > 1) g += clamp(rain * 3.2 - 0.6, 0.0, 1.0) * ring_rain_ripple_layer(plane_m, 2.0, 0.5, 1.0, 1.0, lod);
		if (layers > 2) g += clamp(rain * 3.2 - 1.4, 0.0, 1.0) * ring_rain_ripple_layer(plane_m, 4.0, 0.75, 1.0, 2.0, lod);
		if (layers > 3) g += clamp(rain * 3.2 - 2.2, 0.0, 1.0) * ring_rain_ripple_layer(plane_m, 2.0, 0.875, 1.0, 3.0, lod);
	}
	if (fat > 0.0) g += fat * ring_rain_ripple_layer(plane_m, 8.0, 0.3125, 1.0, 7.0, lod);
	if (hail > 0.0) g += hail * 1.5 * ring_rain_ripple_layer(plane_m, 2.0, 1.25, 0.6, 9.0, lod);
	return g * fade;
}
#endif
