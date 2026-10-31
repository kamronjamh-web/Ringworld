#ifndef RING_MEGASTORM_DARKNESS_GDSHADERINC
#define RING_MEGASTORM_DARKNESS_GDSHADERINC

// Megastorm darkness E (docs/megastorm-design.md 5.6; WO14). Texture-free, in the subset of GLSL that both Godot's shading language and
// GLSL 450 accept, so the macro map build (addons/SunshineClouds2/RingMacroWeatherMap.glsl, the storm-light layer z) and every storm-light
// receiver (materials/ring_storm_light.gdshaderinc, analytic beyond the map) evaluate the same function. C# twin: MegastormModel.Darkness
// (constant for constant; tools/MegastormDarknessTest.cs pins the table).
//
// E(xi), xi = distance behind the wall's actual (bowed, meandering) edge in metres; trailing_end_xi = 50 km + the trailing length:
//   -45 .. -5 km   0 -> 0.55   under the approaching anvil
//    -5 .. +2 km   0.55 -> 1   shelf into the wall
//     2 .. 25 km   1           the wall
//    25 .. 30 km   1 -> 0.9, then 0.9 to 60 km (vacuum wake)
//    60 .. 80 km   0.9 -> 0.7, then 0.7 x (1 - smoothstep(80 km, trailing end, xi))
// Every segment joins with zero slope (smoothstep), so E has no steps.

float ring_msd_smooth(float v) {
	float t = clamp(v, 0.0, 1.0);
	return t * t * (3.0 - 2.0 * t);
}

float ring_megastorm_darkness(float xi, float trailing_end_xi) {
	if (xi < -45000.0) return 0.0;
	if (xi < -5000.0) return 0.55 * ring_msd_smooth((xi + 45000.0) / 40000.0);
	if (xi < 2000.0) return 0.55 + 0.45 * ring_msd_smooth((xi + 5000.0) / 7000.0);
	if (xi < 25000.0) return 1.0;
	if (xi < 30000.0) return 1.0 - 0.1 * ring_msd_smooth((xi - 25000.0) / 5000.0);
	if (xi < 60000.0) return 0.9;
	if (xi < 80000.0) return 0.9 - 0.2 * ring_msd_smooth((xi - 60000.0) / 20000.0);
	if (xi < trailing_end_xi) return 0.7 * (1.0 - ring_msd_smooth((xi - 80000.0) / max(trailing_end_xi - 80000.0, 1.0)));
	return 0.0;
}

// x_lead(across) = -Bow (1 - u^2) - Meander (MegastormModel.LeadOffset; same as ring_megastorm_lead_offset in ring_megastorm_profile.gdshaderinc).
// lead = (W, bow, meander amplitude, meander wavelength), phase = meander phase (rad, wrapped).
float ring_megastorm_darkness_lead(float across, vec4 lead, float phase) {
	float u = 2.0 * across / max(lead.x, 1.0) - 1.0;
	float meander = lead.z * sin(6.28318530718 * across / max(lead.w, 1.0) + phase);
	return -lead.y * (1.0 - u * u) - meander;
}

// Reference-line distance x = dir * wrap(s - around) of a point `arc` metres further around than a point with reference distance x_ref,
// wrapped to (-C / 2, C / 2] (C = circumference; 0 or less = no wrap).
float ring_megastorm_darkness_x(float x_ref, float dir, float arc, float circumference) {
	float x = x_ref - dir * arc;
	if (circumference > 1.0) x -= circumference * floor(x / circumference + 0.5);
	return x;
}

// Slanted-ray shade of the noise-free megastorm column (docs/megastorm-design.md 5.6): 1 - the sun transmittance of the air at a ground point along the sun's slanted ray,
// summed over four altitude slabs (centres 2 / 6 / 10.5 / 15 km, thicknesses 4 / 4 / 4.5 / 4.5 km). The ray at height h is displaced by (dx_per_h, dacross_per_h) x h from the
// point (x0 = its reference-line distance x, across0 its across, both metres), so a low sun that reaches the air from under the anvil (the canopy's underside is 9-12 km
// up) is not counted as shaded, and a point behind the wall sees the wall between itself and the sun. At each slab the column is the noise-free twin of the strip's
// (ring_megastorm_column_terms with tower noise 0.5 and strength = level): wall body from 300 m to wallTop, canopy from `under` to canopyTop, each with coverage
// clamp(1.25 cov level, 0, 1) and the wall/canopy coverage of xi = x - lead(across); inside = their sum (clamped to 1); tau += 3.0e-3 thickness inside / max(sun_up, 0.06).
// dx_per_h = -dir * s_around / s_up and dacross_per_h = s_across / s_up (x = dir * wrap(s - around), ring_megastorm_darkness_x: x falls by dir per metre further around,
// so a sun that lies further around displaces the ray's x by -dir s_around / s_up per metre of height); both 0 when s_up < 0.06 (the overhead column).
// lead = (W, bow, meander amplitude, meander wavelength), phase = the meander phase, level = the formation level (0..1).
float ring_megastorm_air_shade_ray(float x0, float across0, float dx_per_h, float dacross_per_h, float sun_up, vec4 lead, float phase, float level) {
	float tau = 0.0;
	for (int k = 0; k < 4; k++) {
		float h = k == 0 ? 2000.0 : (k == 1 ? 6000.0 : (k == 2 ? 10500.0 : 15000.0));
		float thickness = k < 2 ? 4000.0 : 4500.0;
		float xi = x0 + dx_per_h * h - ring_megastorm_darkness_lead(across0 + dacross_per_h * h, lead, phase);
		// Wall body (RING_MS_WALL_END_XI 25 km, RING_MS_TRANSITION_END_XI 50 km): base 300 m, top 15 km + 2 km x tower 0.5, blending to 8.5 km, formation-scaled from 6 km.
		float transition = ring_msd_smooth((xi - 25000.0) / 25000.0);
		float wall_cov = ring_msd_smooth((xi + 300.0) / 800.0) * (1.0 - transition);
		float wall_top = min(6000.0 + (mix(16000.0, 8500.0, transition) - 6000.0) * level, 18000.0);
		// Leading anvil canopy (RING_MS_ANVIL_START_XI -60 km): underside 9 km rising to 12 km ahead, top 6 km + 10.5 km x level.
		float canopy_cov = ring_msd_smooth((xi + 60000.0) / 10000.0) * (1.0 - ring_msd_smooth((xi - 25000.0) / 20000.0));
		float canopy_top = min(6000.0 + 10500.0 * level, 18000.0);
		float under = min(9000.0 + 3000.0 * ring_msd_smooth(xi / -60000.0), canopy_top);
		float inside = (h >= 300.0 && h <= wall_top ? clamp(1.25 * wall_cov * level, 0.0, 1.0) : 0.0)
			+ (h >= under && h <= canopy_top ? clamp(1.25 * canopy_cov * level, 0.0, 1.0) : 0.0);
		tau += 3.0e-3 * thickness * min(inside, 1.0) / max(sun_up, 0.06);
	}
	return 1.0 - exp(-tau);
}

// Haze path pieces (docs/megastorm-design.md 5.6): the view path is cut at FIXED absolute distances, 0 / 5 / 12 / 22 / 40 km and a remainder to the target, so the near
// part of a path that a mountain and the wall behind it share is dimmed identically for both. A piece's storm level is read at its midpoint (2.5 / 8.5 / 17 / 31 km;
// the remainder reuses the 31 km tap). Each piece weighs its share of the cumulative in-scatter of a uniform medium of total optical depth tau over the target length:
// w = (exp(-tau a / D) - exp(-tau b / D)) / (1 - exp(-tau)), a piece [a, b] truncated at D = length_m, dropped when a >= D; (b - a) / D for tau < 1e-4.
// The four weights (the remainder folded into the last) sum to 1.
float ring_msd_piece_weight(float a, float b, float length_m, float tau) {
	if (a >= length_m) return 0.0;
	float c = min(b, length_m);
	if (tau < 1.0e-4) return (c - a) / length_m;
	return (exp(-tau * a / length_m) - exp(-tau * c / length_m)) / (1.0 - exp(-tau));
}

vec4 ring_msd_piece_weights(float length_m, float tau) {
	float d = max(length_m, 1.0);
	return vec4(ring_msd_piece_weight(0.0, 5000.0, d, tau), ring_msd_piece_weight(5000.0, 12000.0, d, tau),
		ring_msd_piece_weight(12000.0, 22000.0, d, tau),
		ring_msd_piece_weight(22000.0, 40000.0, d, tau) + ring_msd_piece_weight(40000.0, d, d, tau));
}
#endif
