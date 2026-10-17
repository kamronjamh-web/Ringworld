// Eclipse spire to hub beam state math (docs/eclipse-false-sun-spec.md E.2, phase E4b). Pure functions of the ev_* uniforms and the
// per-beam custom data (fire tau, kind). No built-ins, no varyings, so the canvas_item parity shader can include it. The CPU twin is
// EclipseHubBeamMath (scripts/Celestial/Eclipse/EclipseHubBeamMath.cs); keep them in step.
// Users: ring_eclipse_hub_beam_common.gdshaderinc, tools/shaders/eclipse_hub_beam_parity.gdshader.
// Phase E4d adds the Sustain refuel flash, which reads the grid pulse's eg_fronts, so the grid pulse maths is included here.
#include "res://materials/ring_emitter_grid_pulse.gdshaderinc"

// Event time tau: seconds from the eclipse window start (not absolute seconds, so float precision is plenty).
uniform float ev_time = 0.0;
// x handoff start tau, y handoff fade seconds, z cut tau, w retract seconds.
uniform vec4 ev_marks = vec4(1.0e9, 1.0, 1.0e9, 1.0);
// x main climb seconds, y main climb exponent, z regular climb seconds, w regular climb exponent.
uniform vec4 ev_climb = vec4(4.0, 2.0, 1.5, 1.0);
// Global brightness gain (1 until the collapse stage drives it).
uniform float ev_level = 1.0;
// x soft tip length (fraction of the beam), y brightness factor while the hub day is above 0.5.
uniform vec2 ev_shape = vec2(0.005, 0.6);

float ehb_smooth01(float x) {
	x = clamp(x, 0.0, 1.0);
	return x * x * (3.0 - 2.0 * x);
}

// Climb tip fraction 0..1 of a beam fired at fire_tau. kind > 0.5 is the main spire.
float ehb_climb_tip(float fire_tau, float kind) {
	bool is_main = kind > 0.5;
	float climb = max(is_main ? ev_climb.x : ev_climb.z, 1.0e-4);
	float x = clamp((ev_time - fire_tau) / climb, 0.0, 1.0);
	return pow(x, is_main ? ev_climb.y : ev_climb.w);
}

// Top of the part of the beam the collapse retract still lights: 1 until the cut, then falling to 0 (top-down).
float ehb_cut_top() {
	if (ev_time < ev_marks.z) {
		return 1.0;
	}
	return 1.0 - clamp((ev_time - ev_marks.z) / max(ev_marks.w, 1.0e-4), 0.0, 1.0);
}

float ehb_tip(float fire_tau, float kind) {
	return min(ehb_climb_tip(fire_tau, kind), ehb_cut_top());
}

// The regular beams fade out over the handoff; the main beam stays.
float ehb_handoff(float kind) {
	if (kind > 0.5) {
		return 1.0;
	}
	return 1.0 - ehb_smooth01((ev_time - ev_marks.x) / max(ev_marks.y, 1.0e-4));
}

// 1 below the tip, a soft fall to 0 over the soft tip length above it. A complete beam (tip 1) is never cut.
float ehb_tip_mask(float f, float tip) {
	if (tip >= 1.0) {
		return 1.0;
	}
	float soft = max(ev_shape.x, 1.0e-6);
	return 1.0 - smoothstep(tip - soft, tip, f);
}

// 1 while the hub day is low, ev_shape.y once it is above 0.5 (smoothed over 0.45..0.55 so the factor does not pop while the level ramps).
float ehb_day_visibility(float hub_day) {
	return mix(1.0, ev_shape.y, smoothstep(0.45, 0.55, clamp(hub_day, 0.0, 1.0)));
}

// True when any part of a sub-segment starting at f_a can be lit (the vertex collapse rule).
bool ehb_lit(float fire_tau, float kind, float f_a) {
	float tip = ehb_tip(fire_tau, kind);
	return tip > 0.0 && ehb_handoff(kind) > 0.0 && ev_level > 0.0 && (tip >= 1.0 || f_a < tip + max(ev_shape.x, 1.0e-6));
}

// Beam level at chord fraction f, before the emission, the cross profile and the colour. The beams ignore the background B and the day
// factor by design (spec E.2): they are the event.
float ehb_level(float fire_tau, float kind, float f, float hub_day, float atmosphere_visibility, float layer_weight) {
	return ehb_handoff(kind) * ehb_tip_mask(f, ehb_tip(fire_tau, kind)) * ehb_day_visibility(hub_day) * ev_level
		* atmosphere_visibility * layer_weight;
}

// ---------------------------------------------------------------- refuel flashes (phase E4d, spec E.4)
// The Sustain refuel flash of a regular beam comes from the grid's normal pulse fronts (eg_fronts, normal fronts only, written by the beam
// layer from the pulse clock at the grid time): the latest front on the beam's column that has passed the spire's coordinate q is age
// (head - q) / v seconds old. The CPU twin is EclipseHubBeamMath.RefuelFlash / EclipseEventTimeline.RefuelFlash.
// x flash climb seconds, y hold seconds, z fade seconds, w front speed (m/s).
uniform vec4 ev_flash = vec4(0.5, 0.6, 1.2, 104719.76);
// x Sustain start tau, y Instability start tau: only a pass inside [x, y) flashes.
uniform vec2 ev_refuel = vec2(1.0e9, 1.0e9);

// vec2(envelope, tip): the flash envelope 0..1 (rise over the climb, hold, fade) and the fraction of the beam the bolt has reached (age / climb).
// (0, 1) when the beam does not flash. kind > 0.5 (the main spire) and dead beams (fire tau never) do not flash.
vec2 ehb_refuel(float fire_tau, float kind, float p_site, float column) {
	if (kind > 0.5 || fire_tau >= 1.0e8 || eg_front_count <= 0) {
		return vec2(0.0, 1.0);
	}
	float q = eg_q_of_p(p_site);
	float age = 1.0e9;
	for (int i = 0; i < eg_front_count; i++) {
		vec4 front = eg_fronts[i];
		if (abs(front.y - column) < 0.5 && front.x >= q) {
			age = min(age, (front.x - q) / max(ev_flash.w, 1.0));
		}
	}
	float pass_tau = ev_time - age;
	if (age >= 1.0e8 || pass_tau < ev_refuel.x || pass_tau >= ev_refuel.y) {
		return vec2(0.0, 1.0);
	}
	float climb = max(ev_flash.x, 1.0e-4);
	float envelope;
	if (age < climb) {
		envelope = ehb_smooth01(age / climb);
	} else if (age < climb + ev_flash.y) {
		envelope = 1.0;
	} else {
		envelope = 1.0 - ehb_smooth01((age - climb - ev_flash.y) / max(ev_flash.z, 1.0e-4));
	}
	return vec2(envelope, clamp(age / climb, 0.0, 1.0));
}

// ehb_lit with a refuel flash (flash from ehb_refuel): the sub-segment starting at f_a is also lit below the bolt's tip.
bool ehb_lit_refuel(float fire_tau, float kind, float f_a, vec2 flash) {
	bool bolt = flash.x > 0.0 && ev_level > 0.0 && (flash.y >= 1.0 || f_a < flash.y + max(ev_shape.x, 1.0e-6));
	return bolt || ehb_lit(fire_tau, kind, f_a);
}

// ehb_level with a refuel flash: the brighter of the beam's own state and the bolt. With flash.x = 0 this is exactly ehb_level.
float ehb_level_refuel(float fire_tau, float kind, float f, float hub_day, float atmosphere_visibility, float layer_weight, vec2 flash) {
	float own = ehb_handoff(kind) * ehb_tip_mask(f, ehb_tip(fire_tau, kind));
	float bolt = flash.x * ehb_tip_mask(f, flash.y);
	return max(own, bolt) * ehb_day_visibility(hub_day) * ev_level * atmosphere_visibility * layer_weight;
}

// ---------------------------------------------------------------- in-cloud diffusion (phase E4e, spec E.6)
// A near beam carries (k, deck low, deck high, flag) in its MultiMesh COLOR: k = the cloud coverage at the spire (one weather query), deck
// low/high = the cloud base and top as metres above the crown. The CPU twin is EclipseHubBeamMath.CloudMask / CloudCoreKeep.
// x widening (halo widens by 1 + x k), y core cut threshold, z halo boost (brightness 1 + z k), w enabled (0 or 1).
uniform vec4 cloud_params = vec4(3.0, 1.5, 1.0, 1.0);

// 1 inside the deck (height h metres above the crown), with 400 m smooth half-width edges (a cloud base is not a plane).
float ehb_cloud_mask(float h, float deck_low, float deck_high) {
	return smoothstep(deck_low - 400.0, deck_low + 400.0, h) * (1.0 - smoothstep(deck_high - 400.0, deck_high + 400.0, h));
}

// In-cloud factor k * mask at height h; 0 for an unqueried beam (flag 0) or when the feature is off.
float ehb_cloud_factor(vec4 cloud, float h) {
	if (cloud.w < 0.5 || cloud.x <= 0.0 || cloud_params.w < 0.5) {
		return 0.0;
	}
	return cloud.x * ehb_cloud_mask(h, cloud.y, cloud.z);
}

// Depth-core keep factor: 1 outside dense cloud, 0 once the in-cloud factor passes the cut (smooth over +-0.1).
float ehb_cloud_core_keep(float factor) {
	return 1.0 - smoothstep(cloud_params.y - 0.1, cloud_params.y + 0.1, factor);
}
