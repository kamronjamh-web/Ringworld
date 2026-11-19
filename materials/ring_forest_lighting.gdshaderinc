// Forest lighting (docs/forest-lighting-plan.md): leaf translucency and crown self-occlusion shared by imported_tree and imported_tree_impostor.
// Global switches, published by RingForestLighting: x translucency on, y tree sun-shadow distance in metres (0 while tree shadows are suspended), z canopy shade on, w crown occlusion on.
// Crown hull: one ellipsoid per variant in catalog-normalized tree space (root at the origin, +Y up, 1 unit = the tree's height). Packed as three vec4:
//   hull  = (centre y, horizontal semi-axis, vertical semi-axis, kappa per normalized unit)
//   extra = (centre x, centre z, k_species, 1 = valid)
//   lad   = leaf area fractions of 4 inclination classes from vertical (centred 11.25, 33.75, 56.25, 78.75 degrees), sum 1
// Mesh and impostor share one statistical model (ring_crown_first_leaf_terms, ring_leaf_lad_facing), so they agree at the hand-off by construction.
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

// Length of the part of the line {p + t * dir, t > 0} inside the crown ellipsoid (dir unit length); 0 when the line misses it or lies behind p.
float ring_crown_path_length(vec3 p, vec3 dir, vec4 hull, vec4 extra) {
	float t0, t1;
	if (!ring_crown_ray(p, dir, hull, extra, t0, t1)) return 0.0;
	return max(0.0, t1 - max(t0, 0.0));
}

// Ambient occlusion of a crown point: crown path length towards the sky, the direction most of a side leaf's sky comes from (up, tilted outward from the crown axis).
float ring_crown_sky_visibility(vec3 p, vec4 hull, vec4 extra) {
	vec2 away = p.xz - extra.xy;
	float away_length = length(away);
	vec3 outward = away_length > 1e-4 ? vec3(away.x, 0.0, away.y) / away_length : vec3(0.0);
	return exp(-hull.w * ring_crown_path_length(p, normalize(vec3(0.0, 1.0, 0.0) + 0.7 * outward), hull, extra));
}

// Transmittance of the crown towards the sun for a point; light_model is the unit direction to the sun in model space.
float ring_crown_sun_transmittance(vec3 p, vec3 light_model, vec4 hull, vec4 extra) {
	return exp(-hull.w * ring_crown_path_length(p, light_model, hull, extra));
}

// A world position in model space (catalog-normalized tree space) for a model matrix that is a rotation times one uniform scale.
vec3 ring_crown_model_point(mat4 model, vec3 world) {
	vec3 offset = world - model[3].xyz;
	float scale = length(model[1].xyz);
	return vec3(dot(offset, model[0].xyz), dot(offset, model[1].xyz), dot(offset, model[2].xyz)) / max(scale * scale, 0.000001);
}

// Crown sky occlusion and sun transmittance of the first leaf on the view ray (all model space): in a turbid crown the first leaf depth is exponential with rate kappa, truncated to the chord, evaluated
// as three quantile midpoints (an expectation, not one point). A pixel whose ray misses the hull uses the point itself. sun = 1 when !want_sun.
void ring_crown_first_leaf_terms(vec3 camera_model, vec3 point_model, vec3 light_model, vec4 hull, vec4 extra, bool want_sun, out float sky, out float sun) {
	vec3 to_point = point_model - camera_model;
	float t_point = length(to_point);
	vec3 ray = to_point / max(t_point, 0.000001);
	float t_entry, t_exit;
	sun = 1.0;
	if (ring_crown_ray(camera_model, ray, hull, extra, t_entry, t_exit) && t_exit > 0.0) {
		float entry = max(t_entry, 0.0);
		float far_t = t_entry < 0.0 ? min(t_exit, t_point) : t_exit; // camera inside the crown: the chord stops at the fragment
		float chord = max(far_t - entry, 0.0);
		float k = max(hull.w, 0.0001);
		float q = 1.0 - exp(-k * chord);
		sky = 0.0;
		float sun_sum = 0.0;
		for (int i = 0; i < 3; i++) {
			float u = (1.0 + 2.0 * float(i)) / 6.0;
			vec3 p = camera_model + ray * (entry - log(1.0 - u * q) / k);
			sky += ring_crown_sky_visibility(p, hull, extra) / 3.0;
			if (want_sun) sun_sum += ring_crown_sun_transmittance(p, light_model, hull, extra) / 3.0;
		}
		if (want_sun) sun = sun_sum;
	} else {
		sky = ring_crown_sky_visibility(point_model, hull, extra);
		if (want_sun) sun = ring_crown_sun_transmittance(point_model, light_model, hull, extra);
	}
}

// Projected-area mean of the bi-Lambertian translucency facing (max(-n.L, 0) on the viewer-facing leaf side, weighted |n.V|) for leaves whose inclination from `up` falls in 4 classes centred at 11.25, 33.75, 56.25 and 78.75 degrees
// with area weights lad (sum 1), azimuth uniform (8 azimuths). to_light and to_viewer are unit vectors away from the leaf.
float ring_leaf_lad_facing(vec3 to_light, vec3 to_viewer, vec3 up, vec4 lad) {
	vec3 e1 = normalize(cross(up, abs(up.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0)));
	vec3 e2 = cross(up, e1);
	float num = 0.0;
	float den = 0.0;
	for (int c = 0; c < 4; c++) {
		float w = lad[c];
		float incl = radians(11.25 + 22.5 * float(c));
		float ci = cos(incl);
		float si = sin(incl);
		for (int j = 0; j < 8; j++) {
			float a = (float(j) + 0.5) * (PI / 4.0);
			vec3 n = ci * up + si * (cos(a) * e1 + sin(a) * e2);
			float nv = dot(n, to_viewer);
			float nl = dot(n, to_light);
			den += w * abs(nv);
			if (nv * nl < 0.0) num += w * abs(nv) * abs(nl);
		}
	}
	return num / max(den, 0.000001);
}

#endif
