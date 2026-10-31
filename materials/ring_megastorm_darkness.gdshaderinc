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
#endif
