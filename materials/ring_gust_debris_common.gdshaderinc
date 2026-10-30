// Gust front debris, shared motion (docs/storm-revamp-s3-storm-arrival-design.md WO-D1; node scripts/Weather/RingGustFrontDebris.cs).
// Stateless like the rain streak lattice: every MultiMesh instance is one hashed particle of a world-anchored square lattice (DEB_DOMAIN metres per side) that is
// carried by the wind (the CPU accumulates the wind into deb_drift, modulo the domain, in double precision) and wraps around the observer, so there is no state and no
// respawn pop. The MultiMesh sits at the camera (MODEL_MATRIX translation = camera world position, identity basis), every position here is relative to it.
// Height: the precipitation occlusion map supplies the surface under each particle (terrain as drawn, roofs, POIs), so debris rides over whatever is below.
#include "res://materials/ring_precip_occlusion.gdshaderinc"

const float DEB_DOMAIN = 96.0;
uniform vec3 deb_around = vec3(0.0, 0.0, 1.0);
uniform vec3 deb_across = vec3(0.0, 1.0, 0.0);
uniform vec3 deb_up = vec3(-1.0, 0.0, 0.0);
uniform vec2 deb_cam_ac = vec2(0.0);       // observer around/across metres modulo DEB_DOMAIN
uniform vec2 deb_drift = vec2(0.0);        // accumulated wind displacement (around, across) modulo DEB_DOMAIN
uniform vec2 deb_wind = vec2(0.0);         // current wind (around, across) m/s
uniform float deb_time = 0.0;              // seconds, wrapped
uniform float deb_level = 0.0;             // 0..1 share of the pool that is alive (gust strength)
uniform float deb_ground_fallback = 1.7;   // placeholder height for dead particles (occlusion map unusable)
uniform vec2 deb_agl_fade = vec2(18.0, 30.0);   // camera height above the surface under a particle (m) where it starts to fade and where it is gone (flight)
uniform vec4 deb_kinds[9];                 // 3x3 biome cells over the domain: x leaf share, y dust share, z spray share, w snow share (dust puffs become white spindrift)

uint deb_hash(uint x) { x ^= x >> 16u; x *= 0x7feb352du; x ^= x >> 15u; x *= 0x846ca68bu; x ^= x >> 16u; return x; }
float deb_u(uint h) { return float(h >> 8u) * (1.0 / 16777216.0); }

// Surface height relative to the camera plane under a point given relative to the camera. ok is false when the occlusion map is unready or the point lies off it:
// those particles are dead (alive 0) instead of being placed at a guessed ground.
float deb_ground(vec3 cam_world, vec3 rel_flat, out bool ok) {
	ok = false;
	if (ring_precip_occlusion_state.x < 0.5) return -deb_ground_fallback;
	vec3 world = cam_world + rel_flat;
	vec3 image_x = ring_precip_occlusion_across.xyz;
	vec3 image_y = ring_precip_occlusion_around.xyz;
	vec3 mup = cross(image_x, image_y);
	vec3 rel = world - ring_precip_occlusion_origin.xyz;
	float half_extent = ring_precip_occlusion_around.w;
	vec2 uv = vec2(dot(rel, image_x) / (2.0 * half_extent) + 0.5, 0.5 - dot(rel, image_y) / (2.0 * half_extent));
	if (uv.x < 0.0 || uv.y < 0.0 || uv.x >= 1.0 || uv.y >= 1.0) return -deb_ground_fallback;
	float surface = textureLod(ring_precip_occlusion, uv, 0.0).r;
	if (surface <= 0.0) return -deb_ground_fallback;
	ok = true;
	return dot(ring_precip_occlusion_origin.xyz - cam_world, mup) - surface;
}

struct DebPlace {
	vec3 rel;        // particle centre relative to the camera
	float alive;     // 0..1 (level, edge fade, biome share), 0 = hidden
	vec4 rnd;        // four hashed randoms for the caller
	float hover;     // 0..1 random lift factor
	float kind_select;   // random used to pick dust against spray
	vec3 kinds;
	float snow;
};

DebPlace deb_place(int id, uint salt, vec3 cam_world) {
	DebPlace d;
	uint h0 = deb_hash(uint(id) * 2654435761u + salt);
	uint h1 = deb_hash(h0 ^ 0x68bc21ebu);
	uint h2 = deb_hash(h1 + 0x02e5be93u);
	uint h3 = deb_hash(h2 ^ 0x9e3779b9u);
	uint h4 = deb_hash(h3 + 0x7f4a7c15u);
	uint h5 = deb_hash(h4 ^ 0x1b873593u);
	d.rnd = vec4(deb_u(h2), deb_u(h3), deb_u(h4), deb_u(h5));
	vec2 lattice = vec2(deb_u(h0), deb_u(h1)) * DEB_DOMAIN;
	float phase = d.rnd.x * 6.2831853;
	float flutter = 0.6 + 1.4 * deb_level;
	vec2 rel = lattice + deb_drift - deb_cam_ac;
	rel = rel - DEB_DOMAIN * floor(rel / DEB_DOMAIN + 0.5);
	rel += vec2(sin(deb_time * 0.9 + phase), cos(deb_time * 1.13 + phase * 1.7)) * flutter;
	float edge = max(abs(rel.x), abs(rel.y)) / (0.5 * DEB_DOMAIN);
	float fade = 1.0 - smoothstep(0.72, 1.0, edge);
	ivec2 cell = ivec2(clamp(floor((rel / DEB_DOMAIN + 0.5) * 3.0), vec2(0.0), vec2(2.0)));
	vec4 cell_kinds = deb_kinds[cell.y * 3 + cell.x];
	d.kinds = cell_kinds.xyz;
	d.snow = cell_kinds.w;
	float vis_hash = deb_u(deb_hash(h5 ^ 0x5bd1e995u));
	float live = smoothstep(vis_hash, vis_hash + 0.12, deb_level);
	d.alive = live * fade;
	d.hover = deb_u(deb_hash(h4 ^ 0x2c1b3c6du));
	d.kind_select = deb_u(deb_hash(h3 ^ 0x3c6ef372u));
	vec3 flat_rel = deb_around * rel.x + deb_across * rel.y;
	bool ground_ok;
	float ground = deb_ground(cam_world, flat_rel, ground_ok);
	// Dead without a valid surface (off the map or map unready, e.g. high above the clouds) and faded out above ~30 m AGL: debris is a ground-level effect.
	d.alive *= ground_ok ? 1.0 - smoothstep(deb_agl_fade.x, deb_agl_fade.y, -ground) : 0.0;
	d.rel = flat_rel + deb_up * ground;   // callers add the hover above the surface
	return d;
}
