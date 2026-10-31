#ifndef RING_MEGASTORM_BLAST_INCLUDED
#define RING_MEGASTORM_BLAST_INCLUDED
// Megastorm blast (docs/megastorm-design.md 5.5.1, WO11; published by RingMegastormHazardFx.cs). The boom front is a plane that sweeps along the storm's direction of
// travel: s = (front offset) - dot(world - origin, n) is the distance a point lies behind it (positive once the front has passed). Everything is a pure function of s, so
// a static tree or a patch of water shows the whole sequence as the front goes by: it is thrown over within about 60 m, relaxes to a held lean while the bullet-rain wind
// leans on it, and is released over xi 15-25 km. Exactly 0 (every reader returns before touching anything) when x = 0, which is the project default and the state of every
// world without a live megastorm, so an untriggered world renders bit-identically.
global uniform vec4 ring_megastorm_blast;        // x: strength 0..1 (0 = inactive), y: front offset along n from the origin (m), z: full bend angle (rad), w: water slam amplitude 0..1
global uniform vec3 ring_megastorm_blast_n;      // unit direction of travel (world)
global uniform vec3 ring_megastorm_blast_up;     // unit up at the origin (world)
global uniform vec3 ring_megastorm_blast_origin; // world origin the front offset is measured from

float ring_blast_distance_behind(vec3 world) {
	return ring_megastorm_blast.y - dot(world - ring_megastorm_blast_origin, ring_megastorm_blast_n);
}

// Mirrors MegastormHazardFx.BlastProfile (C#): 0 ahead of the front, the throw over 60 m, a held lean of 0.55 from 2.5 km, released over 15-25 km.
float ring_blast_profile(float s) {
	if (s <= 0.0) return 0.0;
	float level = mix(1.0, 0.55, smoothstep(300.0, 2500.0, s)) * (1.0 - smoothstep(15000.0, 25000.0, s));
	return smoothstep(0.0, 60.0, s) * level;
}

// Bend 0..1 at a world point.
float ring_megastorm_blast_bend(vec3 world) {
	if (ring_megastorm_blast.x <= 0.0) return 0.0;
	return ring_megastorm_blast.x * ring_blast_profile(ring_blast_distance_behind(world));
}

// Horizontal unit direction of travel for a given local up.
vec3 ring_megastorm_blast_dir(vec3 up) {
	vec3 d = ring_megastorm_blast_n - up * dot(ring_megastorm_blast_n, up);
	float l = length(d);
	return l > 1.0e-4 ? d / l : vec3(0.0);
}

// Tree structure: displacement of a point at height fraction w (0 root .. 1 tip) of a tree of the given height, and its derivative with respect to w, along a circular arc
// of total angle bend * z. Added to the wind structure offset of ring_tree_wind_geometry.
void ring_megastorm_blast_tree(vec3 root, float w, float height, out vec3 offset, out vec3 derivative) {
	offset = vec3(0.0); derivative = vec3(0.0);
	float bend = ring_megastorm_blast_bend(root);
	if (bend <= 0.0) return;
	vec3 up = ring_megastorm_blast_up;
	vec3 dir = ring_megastorm_blast_dir(up);
	float theta = ring_megastorm_blast.z * bend;
	float a = theta * w;
	// Chord of a constant-curvature arc: horizontal h*sin-ish and the drop h*(1-cos), measured per unit of height.
	float sa = sin(a), ca = cos(a);
	float horizontal = height * w * sa;
	float drop = height * w * (1.0 - ca);
	offset = dir * horizontal - up * drop;
	float dh = height * (sa + a * ca);
	float dd = height * (1.0 - ca + a * sa);
	derivative = dir * dh - up * dd;
}

// Water slam: tilts a water normal along the direction of travel in a ringing packet that follows the front and dies in about 600 m. Exactly the input at x = 0.
vec3 ring_megastorm_blast_water_normal(vec3 normal, vec3 world) {
	if (ring_megastorm_blast.x <= 0.0 || ring_megastorm_blast.w <= 0.0) return normal;
	float s = ring_blast_distance_behind(world);
	if (s < -15.0 || s > 1200.0) return normal;
	float packet = smoothstep(-15.0, 6.0, s) * exp(-s / 260.0);
	float ring = 0.55 + 0.45 * sin(s * 0.75 - 1.1);
	vec3 dir = ring_megastorm_blast_dir(ring_megastorm_blast_up);
	vec3 side = cross(ring_megastorm_blast_up, dir);
	float chop = sin(dot(world, side) * 0.37 + s * 0.21);
	return normalize(normal - dir * (packet * ring * ring_megastorm_blast.w * 0.7) + side * (packet * chop * ring_megastorm_blast.w * 0.25));
}
#endif
