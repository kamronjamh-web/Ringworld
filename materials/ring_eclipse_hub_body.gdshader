shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;

// Eclipse hub sun body (docs/eclipse-false-sun-spec.md E.1, phase E4c, user decision 2026-10-17 item 1): a main-viewport proxy. The far viewport
// is tonemapped twice and tops out near 0.85 (T3), so the hub disc is drawn here, in the main camera, as a camera-facing quad placed along the
// hub direction at 0.5 x camera far and scaled to the hub's true angular size. It is additive, unshaded, depth tested and never writes depth,
// so Sunshine near clouds still composite over it and overhead geometry still hides it.
//
// All lengths below are metres at the proxy plane (the script multiplies the real-km settings by proxy distance / hub distance).
// The profile: a white HDR core disc with mild limb brightening, plus an exponential teal halo that fades to 0 at the quad edge.

uniform float quad_half_m = 1000.0;   // half side of the quad; the vertex stage widens it to hold at least 16 minimum core radii
uniform float core_radius_m = 10.0;   // real core radius (30 km x radius factor) at the proxy plane
uniform float halo_radius_m = 100.0;  // halo e-fold radius
uniform float min_radius_px = 2.0;    // the core is never smaller than this many pixels in radius
uniform float core_emission = 40.0;   // HDR emission of the core, already multiplied by the hub level
uniform float halo_emission = 0.6;    // halo peak emission, already multiplied by the hub level
uniform float limb_brightening = 0.15;
uniform vec3 core_color = vec3(1.0, 1.0, 1.0);
uniform vec3 halo_color = vec3(0.70, 0.92, 1.0);

varying float v_px_m;
varying float v_half_m;

void vertex() {
	vec3 center = (VIEW_MATRIX * vec4(MODEL_MATRIX[3].xyz, 1.0)).xyz;
	float distance_m = max(length(center), 0.001);
	float px_m = distance_m / (abs(PROJECTION_MATRIX[1][1]) * VIEWPORT_SIZE.y * 0.5);
	float half_m = max(quad_half_m, 16.0 * min_radius_px * px_m);
	// Face the camera ray (not the view axis): the disc then keeps its true angular shape off the screen centre.
	vec3 forward = center / distance_m;
	vec3 reference = abs(forward.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
	vec3 side = normalize(cross(reference, forward));
	vec3 up = cross(forward, side);
	vec3 corner = center + (side * VERTEX.x + up * VERTEX.y) * 2.0 * half_m;
	POSITION = PROJECTION_MATRIX * vec4(corner, 1.0);
	v_px_m = px_m;
	v_half_m = half_m;
}

void fragment() {
	float r = length(UV - 0.5) * 2.0 * v_half_m;
	float core_r = max(core_radius_m, min_radius_px * v_px_m);
	float disk = 1.0 - smoothstep(core_r - v_px_m, core_r, r);
	float x = r / core_r;
	float limb = 1.0 - limb_brightening + limb_brightening * x * x;
	float window = 1.0 - smoothstep(0.6 * v_half_m, v_half_m, r);
	vec3 emission = core_color * core_emission * disk * limb
		+ halo_color * halo_emission * exp(-r / max(halo_radius_m, 0.0001)) * window;
	ALBEDO = emission;
	ALPHA = 1.0;
}
