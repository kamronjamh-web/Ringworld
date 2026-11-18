// Forest lighting (docs/forest-lighting-plan.md): leaf translucency and crown self-occlusion shared by imported_tree and imported_tree_impostor.
// Global switches, published by RingForestLighting: x translucency on, y tree sun-shadow distance in metres (0 while tree shadows are suspended), z canopy shade on, w crown occlusion on.
// Crown hull: one ellipsoid per variant in catalog-normalized tree space (root at the origin, +Y up, 1 unit = the tree's height). Packed as two vec4:
//   hull  = (centre y, horizontal semi-axis, vertical semi-axis, kappa per normalized unit)
//   extra = (centre x, centre z, k_species, 1 = valid)
// An unset hull (extra.w < 0.5) means no crown terms and no translucency.
#ifndef RING_FOREST_LIGHTING_INCLUDED
#define RING_FOREST_LIGHTING_INCLUDED

global uniform vec4 ringworld_forest_lighting;

// Spectral shape of the leaf transmittance over reflectance (plan section 2).
const vec3 RING_LEAF_TRANSMISSION_SHAPE = vec3(0.95, 1.10, 0.45);

bool ring_forest_translucency_on() { return ringworld_forest_lighting.x > 0.5; }
bool ring_forest_crown_on() { return ringworld_forest_lighting.w > 0.5; }

// 1 where the sun map has no tree casters (beyond the tree sun-shadow distance, or while tree shadows are suspended), 0 inside it; ramps over the last 20 % of the distance.
float ring_forest_analytic_weight(float camera_distance) {
	float range = ringworld_forest_lighting.y;
	return range <= 0.0 ? 1.0 : smoothstep(0.8 * range, range, camera_distance);
}

bool ring_crown_valid(vec4 extra) { return extra.w > 0.5; }

// Entry and exit parameters of the ray origin + t * dir (dir unit length, model space) with the crown ellipsoid; false when the line misses it.
bool ring_crown_ray(vec3 origin, vec3 dir, vec4 hull, vec4 extra, out float t_entry, out float t_exit) {
	vec3 radii = vec3(hull.y, hull.z, hull.y);
	vec3 q = (origin - vec3(extra.x, hull.x, extra.y)) / radii;
	vec3 d = dir / radii;
	float a = dot(d, d);
	float b = dot(q, d);
	float disc = b * b - a * (dot(q, q) - 1.0);
	t_entry = 0.0; t_exit = 0.0;
	if (disc <= 0.0 || a <= 1e-12) return false;
	float root = sqrt(disc);
	t_entry = (-b - root) / a;
	t_exit = (-b + root) / a;
	return true;
}

// Distance from a point inside the hull to its surface along dir; 0 for a point outside.
float ring_crown_exit_distance(vec3 p, vec3 dir, vec4 hull, vec4 extra) {
	float t0, t1;
	if (!ring_crown_ray(p, dir, hull, extra, t0, t1)) return 0.0;
	return (t0 <= 0.0 && t1 > 0.0) ? t1 : 0.0;
}

// Ambient occlusion of a crown point: exit distance towards the sky, the direction most of a side leaf's sky comes from (up, tilted outward from the crown axis).
float ring_crown_sky_visibility(vec3 p, vec4 hull, vec4 extra) {
	vec2 away = p.xz - extra.xy;
	float away_length = length(away);
	vec3 outward = away_length > 1e-4 ? vec3(away.x, 0.0, away.y) / away_length : vec3(0.0);
	return exp(-hull.w * ring_crown_exit_distance(p, normalize(vec3(0.0, 1.0, 0.0) + 0.7 * outward), hull, extra));
}

// Transmittance of the crown towards the sun for a point in it; light_model is the unit direction to the sun in model space.
float ring_crown_sun_transmittance(vec3 p, vec3 light_model, vec4 hull, vec4 extra) {
	return exp(-hull.w * ring_crown_exit_distance(p, light_model, hull, extra));
}

// Projected-area mean of the mesh's max(-dot(Ng, L), 0) for spherical leaves (Ross): 2 T(beta), cos(beta) = -dot(L, V), L towards the light, V towards the viewer.
float ring_leaf_spherical_facing(float cos_beta) {
	float beta = acos(clamp(cos_beta, -1.0, 1.0));
	return 2.0 * (sin(beta) + (PI - beta) * cos(beta)) / (3.0 * PI);
}

#endif
