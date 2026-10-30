// RingworldTechDemo extension (S6 WO5; docs/storm-revamp-s6-cloud-layers-design.md sections 3.4 and 3.6): the High cloud layer
// (cirrus, cirrostratus, cirrocumulus) as a noise-free analytic 2D shell at the layer's base altitude. Plain GLSL, no stage builtins:
// the main Sunshine compute includes it and composites the shell by ray distance after its march; WO6 (the far layer) reuses it.
//
// The includer defines before including:
//   RING_CIRRUS_TEX2D(uv, lod)  -> float: 2D noise with mipmaps, value in alpha (compute: textureLod(extra_large_noise, uv, lod).a)
//   RING_CIRRUS_TEX2D_SIZE      -> float: that texture's width in texels (compute: float(textureSize(extra_large_noise, 0).x))
#ifndef RING_CIRRUS_SHELL_GDSHADERINC
#define RING_CIRRUS_SHELL_GDSHADERINC

// Octave 1 periods across / along the wind (1:8 streaks); octave 2 is finer and rotated by +0.21 rad from the wind.
#ifndef RING_CIRRUS_STREAK_ACROSS
#define RING_CIRRUS_STREAK_ACROSS 6000.0
#endif
#ifndef RING_CIRRUS_STREAK_ALONG
#define RING_CIRRUS_STREAK_ALONG 48000.0
#endif
#ifndef RING_CIRRUS_STREAK2_ACROSS
#define RING_CIRRUS_STREAK2_ACROSS 2200.0
#endif
#ifndef RING_CIRRUS_STREAK2_ALONG
#define RING_CIRRUS_STREAK2_ALONG 17600.0
#endif
// Veil / warp noise period, the lateral bend it gives the streaks (metres), the cirrocumulus ripple wavelength (along the wind; across
// it the cells are RING_CIRRUS_CELL_ASPECT times longer).
#ifndef RING_CIRRUS_VEIL_PERIOD
#define RING_CIRRUS_VEIL_PERIOD 90000.0
#endif
#ifndef RING_CIRRUS_WARP_METERS
#define RING_CIRRUS_WARP_METERS 2500.0
#endif
#ifndef RING_CIRRUS_CELL_PERIOD
#define RING_CIRRUS_CELL_PERIOD 900.0
#endif
#ifndef RING_CIRRUS_CELL_ASPECT
#define RING_CIRRUS_CELL_ASPECT 1.6
#endif
// Optical depth scale (a shell is a sheet, not a volume), sun radiance scale, share of the forward (HG) lobe against an isotropic
// multiple-scattering floor, 22 degree halo strength, grazing-angle floor of |mu|.
#ifndef RING_CIRRUS_TAU_SCALE
#define RING_CIRRUS_TAU_SCALE 3.0
#endif
#ifndef RING_CIRRUS_SUN_SCALE
#define RING_CIRRUS_SUN_SCALE 0.5
#endif
#ifndef RING_CIRRUS_FORWARD
#define RING_CIRRUS_FORWARD 0.5
#endif
#ifndef RING_CIRRUS_HALO
#define RING_CIRRUS_HALO 1.0
#endif
#ifndef RING_CIRRUS_MIN_MU
#define RING_CIRRUS_MIN_MU 0.1
#endif

// Ray distance of the ray's FIRST crossing of the curved-cylinder altitude `shellAltitude`, or -1.0 if none ahead. `origin` is relative to
// the cloud frame origin; same geometry and stable-root form as cloud_entry_skip_steps.
float ring_cirrus_shell_distance(vec3 origin, vec3 direction, vec3 around, vec3 up, float ringRadius, float shellAltitude) {
	float t = dot(origin, around);
	float h = dot(origin, up);
	float da = dot(direction, around);
	float du = dot(direction, up);
	float a = da * da + du * du;
	float b = t * da - (ringRadius - h) * du;
	float c = t * t + (shellAltitude - h) * (2.0 * ringRadius - h - shellAltitude);
	float disc = b * b - a * c;
	if (a < 1e-10 || disc <= 0.0) return -1.0;
	float rootDisc = sqrt(disc);
	float q = -b - (b < 0.0 ? -rootDisc : rootDisc);
	if (abs(q) < 1e-8) return -1.0;
	float r0 = q / a;
	float r1 = c / q;
	float r = -1.0;
	if (c > 0.0) {
		// Below the shell: the roots share a sign (product c / a > 0); the first crossing is the smaller, if both lie ahead.
		if (r0 > 0.0 && r1 > 0.0) r = min(r0, r1);
	} else {
		// At or above the shell: exactly one root is ahead.
		r = r0 > 0.0 ? r0 : (r1 > 0.0 ? r1 : -1.0);
	}
	if (isnan(r) || isinf(r)) return -1.0;
	return r;
}

// Stratiformity of the High layer from its coverage (matches ring_cloud_layer_resolve for layer 2): 0 = cirrus fibres, 1 = cirrostratus veil.
float ring_cirrus_stratiformity(float highCov) {
	float x = clamp((highCov - 0.4) / 0.4, 0.0, 1.0);
	return x * x * (3.0 - 2.0 * x);
}

// Shell density 0..~1.4 at canonical (around, across) metres `xz` (layer noise offset and drift already included). `wind` is the unit
// downwind vector in (around, across); `footprint` the pixel footprint in metres at the shell (0 = full detail, larger fades to the
// mip-averaged value). Exactly 0 where highCov <= 0.
float ring_cirrus_density(vec2 xz, float highCov, float stratiformity, vec2 wind, float footprint) {
	if (highCov <= 0.0) return 0.0;
	float texSize = RING_CIRRUS_TEX2D_SIZE;
	float fp = max(footprint, 1.0);
	vec2 perp = vec2(-wind.y, wind.x);
	float a1 = dot(xz, wind);
	float c1 = dot(xz, perp);
	// Warp / veil noise: bends the streaks laterally and thins the veil in patches.
	float lodV = max(0.0, log2(fp * texSize / RING_CIRRUS_VEIL_PERIOD));
	float vw = RING_CIRRUS_TEX2D(xz / RING_CIRRUS_VEIL_PERIOD, lodV);
	float warp = (vw - 0.5) * 2.0 * RING_CIRRUS_WARP_METERS;
	float lod1 = max(0.0, log2(fp * texSize / RING_CIRRUS_STREAK_ACROSS));
	float n1 = RING_CIRRUS_TEX2D(vec2(a1 / RING_CIRRUS_STREAK_ALONG, (c1 + warp) / RING_CIRRUS_STREAK_ACROSS) + vec2(0.37, 0.11), lod1);
	// Octave 2: the same field in axes rotated by +0.21 rad.
	const float rotCos = 0.97817; // cos(0.21)
	const float rotSin = 0.20846; // sin(0.21)
	vec2 wind2 = vec2(rotCos * wind.x - rotSin * wind.y, rotSin * wind.x + rotCos * wind.y);
	vec2 perp2 = vec2(-wind2.y, wind2.x);
	float lod2 = max(0.0, log2(fp * texSize / RING_CIRRUS_STREAK2_ACROSS));
	// One line per RING_CIRRUS_TEX2D call: Godot's shader preprocessor does not take macro arguments across lines.
	vec2 uv2 = vec2(dot(xz, wind2) / RING_CIRRUS_STREAK2_ALONG, (dot(xz, perp2) + warp) / RING_CIRRUS_STREAK2_ACROSS) + vec2(0.71, 0.53);
	float n2 = RING_CIRRUS_TEX2D(uv2, lod2);
	float streak = 0.62 * n1 + 0.38 * n2;
	float t0 = mix(0.72, 0.38, clamp(highCov, 0.0, 1.0));
	float fibre = smoothstep(t0, t0 + 0.12, streak) * smoothstep(0.25, 0.6, vw + highCov - 0.3);
	float veil = highCov * mix(0.6, 1.0, vw);
	float density = mix(fibre, veil + 0.25 * fibre, stratiformity);
	// Cirrocumulus: rows of small cells (a mackerel sky) at mid coverage where the veil noise is high. Analytic ripples across the wind,
	// broken into cells along it and bent by the streak noise; no texture read. They fade to their mean once a cell drops below a few
	// pixels, so they never alias.
	float cc = smoothstep(0.2, 0.45, highCov) * (1.0 - smoothstep(0.55, 0.8, highCov)) * smoothstep(0.55, 0.75, vw);
	if (cc > 0.0) {
		float rows = 0.5 + 0.5 * sin(6.2831853 * (a1 / RING_CIRRUS_CELL_PERIOD + 1.3 * n1));
		float cols = 0.5 + 0.5 * sin(6.2831853 * ((c1 + warp) / (RING_CIRRUS_CELL_PERIOD * RING_CIRRUS_CELL_ASPECT) + 1.1 * n2));
		float cells = smoothstep(0.35, 0.8, rows * cols);
		cells = mix(cells, 0.3, smoothstep(0.12, 0.35, footprint / RING_CIRRUS_CELL_PERIOD));
		density = max(density, 0.8 * cc * cells);
	}
	return density * smoothstep(0.0, 0.08, highCov);
}

// Opacity of the shell along a ray: the density's vertical optical depth over the layer thickness, lengthened by 1 / |mu| (mu = the
// ray's altitude motion per metre, floored at RING_CIRRUS_MIN_MU).
float ring_cirrus_alpha(float density, float thicknessMeters, float extinctionPerMeter, float mu) {
	float tauV = density * extinctionPerMeter * thicknessMeters * RING_CIRRUS_TAU_SCALE;
	return 1.0 - exp(-tauV / max(abs(mu), RING_CIRRUS_MIN_MU));
}

// Sun phase of the shell: the relative Henyey-Greenstein lobe 4 pi HG(g, cosTheta) (ice: strong forward scattering) blended with an
// isotropic floor for the multiple scattering a single-scatter lobe misses (RING_CIRRUS_FORWARD), plus the 22 degree halo of
// cirrostratus ice crystals (peak at 0.3840 rad, stratiform sheets only).
float ring_cirrus_phase(float cosTheta, float g, float stratiformity, float highCov) {
	float hg = (1.0 - g * g) / pow(max(1.0 + g * g - 2.0 * g * cosTheta, 1e-4), 1.5);
	float halo = acos(clamp(cosTheta, -1.0, 1.0)) - 0.3840;
	return mix(1.0, hg, RING_CIRRUS_FORWARD)
		+ RING_CIRRUS_HALO * stratiformity * clamp(highCov, 0.0, 1.0) * exp(-(halo / 0.015) * (halo / 0.015));
}

#endif
