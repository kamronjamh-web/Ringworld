// RingworldTechDemo far storm lighting (docs/megastorm-design.md 3.3; WO5): the lighting terms of storm cloud drawn beyond the Sunshine
// handoff, shared by the megastorm wedge pass and (later) the far storm towers of WO-D. Lights are the Sunshine-matched far cloud lights
// (RingFarWeatherShadowGlobals: ring_far_cloud_lights / ring_far_cloud_ambient, declared by the includer).
//  - remote day: the ambient of a cloud far round the ring follows ITS sun, not the camera's (the far arch can be in night while the
//    camera is at noon), as a ratio applied to the camera-local ambient;
//  - storm sun: the sun on a storm sample, Beer through the column's optical depth; megastorm strip samples (near and far) take no S4 storm
//    cut, only the physical umbra (WO5b-C1; decision 2). ring_far_storm_cut_weight now only weights the storm ambient;
//  - the megastorm flash field (3.3): 64 segments of 5 km across in the moving frame, one flash chance per 2 s slot per segment. Its CPU
//    twin is MegastormLightning.FlashesInWindow / GlowAt (scripts/Weather/Megastorm/MegastormLightning.cs): same hashes, same constants.
// Include after ring_megastorm_profile.gdshaderinc (ring_ms_hash01, ring_megastorm_lead_offset).
#ifndef RING_FAR_STORM_LIGHTING_GDSHADERINC
#define RING_FAR_STORM_LIGHTING_GDSHADERINC

// ---- Remote day ----
const float RING_FAR_NIGHT_AMBIENT_SHARE = 0.08;

// Day weight of a light (direction toward it L, present > 0.5) at a point whose local up is `up`: the far cloud layers' light_up ramp.
float ring_far_light_up(vec3 L, vec3 up) {
	return smoothstep(-0.05, 0.12, dot(normalize(L), up));
}

// Ratio that turns the camera-local cloud ambient into the ambient at a point with local up `up_point` (camera up `up_cam`), from the sun
// (light 0). 1 at the camera's own longitude; night ambient is RING_FAR_NIGHT_AMBIENT_SHARE of day.
float ring_far_remote_day_scale(mat4 lights, vec3 up_point, vec3 up_cam) {
	vec4 ld = lights[0];
	if (ld.w < 0.5) return 1.0;
	float d_point = ring_far_light_up(ld.xyz, up_point);
	float d_cam = ring_far_light_up(ld.xyz, up_cam);
	return mix(RING_FAR_NIGHT_AMBIENT_SHARE, 1.0, d_point) / mix(RING_FAR_NIGHT_AMBIENT_SHARE, 1.0, d_cam);
}

// Henyey-Greenstein, half mixed with isotropic (the far cloud layers' phase).
float ring_far_storm_phase(float cos_theta, float g) {
	float hg = (1.0 - g * g) / pow(max(1.0 + g * g - 2.0 * g * cos_theta, 1e-4), 1.5);
	return mix(1.0, hg, 0.5);
}

// Weight of the S4 storm sun cut at view distance t: 1 up to the handoff end, 0 by 2.5 x the handoff end.
float ring_far_storm_cut_weight(float t, float handoff_end) {
	float e = max(handoff_end, 1000.0);
	return 1.0 - smoothstep(e, 2.5 * e, t);
}

// ---- Flash field (docs/megastorm-design.md 3.3) ----
const int RING_MS_FLASH_SEGMENTS = 64;
const float RING_MS_FLASH_SEGMENT_METERS = 5000.0;
const float RING_MS_FLASH_SLOT_SECONDS = 2.0;
const float RING_MS_FLASH_CHANCE = 0.1;          // slot / mean interval (2 s / 20 s)
const float RING_MS_FLASH_RADIUS_METERS = 6000.0;
const float RING_MS_FLASH_LIFETIME = 0.72;       // RingLightningEvent.LifetimeSeconds

float ring_ms_gaussian(float age, float center, float width) {
	float x = (age - center) / width;
	return exp(-0.5 * x * x);
}

// RingLightningEvent.PulseAtAge (reduced = ReducedPulseAtAge, which already carries the reduced peak scale `reduced_peak`).
float ring_ms_flash_pulse(float age, bool reduced, float reduced_peak) {
	if (age < 0.0 || age > RING_MS_FLASH_LIFETIME) return 0.0;
	if (reduced) {
		float swell = ring_ms_gaussian(age, 0.25, 0.08) + ring_ms_gaussian(age, 0.5, 0.09) * 0.4;
		return clamp(swell, 0.0, 1.0) * reduced_peak;
	}
	float primary = ring_ms_gaussian(age, 0.035, 0.018) + ring_ms_gaussian(age, 0.095, 0.035) * 0.38;
	float restrike_a = ring_ms_gaussian(age, 0.265, 0.016) * 0.62;
	float restrike_b = ring_ms_gaussian(age, 0.505, 0.022) * 0.35;
	return clamp(primary + restrike_a + restrike_b, 0.0, 1.0);
}

// Flash light at a point of the moving frame (x reference-line distance, across, altitude above the floor), from the flashes of the
// segments beside it in the current and previous slot. `slot` = floor(t / 2), `slot_time` = t - 2 slot (the CPU splits t in double, so the
// pulse keeps millisecond precision at any weather time). `chance_scale` is the strike-rate multiplier (LightningVisualPolicy.
// NaturalStrikeChanceScale: Lightning frequency x the reduced-flashes share), applied to the same roll as MegastormLightning.Flash(..., chanceScale),
// so a scale below 1 keeps a subset of the cells at 1. Returns the summed pulse x energy x Gaussian falloff (times strength).
float ring_ms_flash_glow(float x, float across, float altitude, int slot, float slot_time, bool reduced, float reduced_peak,
		float chance_scale, RingMegastormParams p) {
	int seed = int(round(p.a.w));
	int center = int(floor(across / RING_MS_FLASH_SEGMENT_METERS));
	float glow = 0.0;
	for (int ds = 0; ds < 2; ds++) {
		int k = slot - ds;
		float slot_age = slot_time + float(ds) * RING_MS_FLASH_SLOT_SECONDS;
		for (int dseg = -1; dseg <= 1; dseg++) {
			int seg = center + dseg;
			if (seg < 0 || seg >= RING_MS_FLASH_SEGMENTS) continue;
			if (ring_ms_hash01(seed, 1000 + seg, k) >= RING_MS_FLASH_CHANCE * max(chance_scale, 0.0)) continue;
			float age = slot_age - RING_MS_FLASH_SLOT_SECONDS * ring_ms_hash01(seed, 2000 + seg, k);
			float pulse = ring_ms_flash_pulse(age, reduced, reduced_peak);
			if (pulse <= 0.0) continue;
			float f_across = (float(seg) + 0.15 + 0.7 * ring_ms_hash01(seed, 3000 + seg, k)) * RING_MS_FLASH_SEGMENT_METERS;
			float f_xi = 2000.0 + 18000.0 * ring_ms_hash01(seed, 4000 + seg, k);
			float f_alt = 4000.0 + 7000.0 * ring_ms_hash01(seed, 5000 + seg, k);
			float energy = 0.6 + 0.4 * ring_ms_hash01(seed, 6000 + seg, k);
			float f_x = f_xi + ring_megastorm_lead_offset(f_across, p);
			vec3 d = vec3(x - f_x, altitude - f_alt, across - f_across) / RING_MS_FLASH_RADIUS_METERS;
			glow += pulse * energy * exp(-dot(d, d));
		}
	}
	return glow * p.a.z;
}

#endif
