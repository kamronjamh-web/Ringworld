#ifndef RING_GROUND_TREMOR_INCLUDED
#define RING_GROUND_TREMOR_INCLUDED
// Megastorm ground tremor (docs/megastorm-design.md 5.2, RingGroundTremor.cs): the floor hum and the edge boom shake the world a little. x: amplitude 0..1 (HumLevel x 0.6 plus the
// boom envelope; exactly 0 with no megastorm, and every reader below returns before touching anything at 0, so an untriggered world renders bit-identically),
// y: visible shiver frequency (Hz), z: shiver phase in radians (accumulated on the CPU, wrapped), w: spare.
global uniform vec4 ring_ground_tremor;

// Height gradient (around, across) of a fine standing-wave shiver at a plane position in metres: two crossing wave pairs of 0.2-0.4 m wavelength that drift in phase.
// Added to a water or puddle ripple gradient (the callers scale it like their own ripple gradients).
vec2 ring_tremor_gradient(vec2 plane_m) {
	float a = ring_ground_tremor.x;
	if (a <= 0.0) return vec2(0.0);
	float ph = ring_ground_tremor.z;
	vec2 g = vec2(cos(plane_m.x * 17.0 + plane_m.y * 5.0 + ph), cos(plane_m.y * 19.0 - plane_m.x * 7.0 - ph * 1.31 + 1.3))
		+ 0.5 * vec2(cos(plane_m.y * 31.0 + ph * 1.7 + 0.4), cos(plane_m.x * 29.0 - ph * 1.9 + 2.1));
	return g * (0.8 * a);
}

// Small high-frequency sway added to the shared gust factor of the vegetation wind path, varying with position so the field does not move as one.
float ring_tremor_sway(vec3 world_position) {
	float a = ring_ground_tremor.x;
	if (a <= 0.0) return 0.0;
	return 0.25 * a * sin(ring_ground_tremor.z + dot(world_position, vec3(2.3, 1.7, 3.1)));
}
#endif
