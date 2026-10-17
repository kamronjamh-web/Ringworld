// Emitter relay grid pulse + visibility math (docs/emitter-grid-climate-machines-spec.md G2/G3).
// Pure functions of the eg_* uniforms; the CPU twin is EmitterGridPulseClock / EmitterGridIntensity
// (scripts/Terrain/Sites/EmitterGridPulseClock.cs). Used by the beam and head-sprite shaders and by the
// parity test shader (tools/shaders/emitter_grid_intensity_parity.gdshader).
uniform float eg_circumference = 31415936.0;
// +1 prograde (q = p), -1 retrograde (q = C - p).
uniform float eg_direction = 1.0;
// x leading edge, y cut-off behind the head, z head-streak length (v * frame dt), w trail e-fold length (v * trail s) (metres).
uniform vec4 eg_pulse_shape = vec4(3000.0, 4190000.0, 1745.0, 261800.0);
// x wake e-fold length (v * wake s, metres), y wake weight.
uniform vec2 eg_wake = vec2(1047200.0, 0.05);
uniform int eg_front_count = 0;
// x head position q (metres, direction-adjusted), y column (0..5).
uniform vec4 eg_fronts[24];
// Relaunch flare envelope 0..1 (applies to knot links).
uniform float eg_flare = 0.0;
// x night peak, y day peak, z night residual, w day residual.
uniform vec4 eg_visibility = vec4(1.0, 0.2, 0.03, 0.0);
// x/y smoothstep range (metres) of the distance-driven day factor of a remote fragment.
uniform vec2 eg_day_distance = vec2(50000.0, 500000.0);

float eg_q_of_p(float p) {
	return eg_direction > 0.0 ? p : eg_circumference - p;
}

// One front's profile at distance b behind its head (b < 0 is ahead). 0..1, no wrap.
float eg_profile(float b) {
	float edge = eg_pulse_shape.x;
	if (b <= -edge) return 0.0;
	if (b <= 0.0) {
		float x = (b + edge) / edge;
		return x * x * (3.0 - 2.0 * x);
	}
	if (b > eg_pulse_shape.y) return 0.0;
	float trail = exp(-max(0.0, b - eg_pulse_shape.z) / eg_pulse_shape.w);
	float wake = eg_wake.y * exp(-b / eg_wake.x);
	return min(1.0, trail + wake);
}

float eg_pulse_at_q(float q, float column) {
	if (q < 0.0 || q > eg_circumference) return 0.0;
	float sum = 0.0;
	for (int i = 0; i < eg_front_count; i++) {
		vec4 front = eg_fronts[i];
		if (abs(front.y - column) < 0.5) sum += eg_profile(front.x - q);
	}
	return min(1.0, sum);
}

// Pulse brightness at link coordinate p; kind 0 = chain, > 0.5 = knot link (lit by the relaunch flare).
float eg_pulse_at_link(float p, float column, float kind) {
	float pulse = eg_pulse_at_q(eg_q_of_p(p), column);
	if (kind > 0.5) pulse = max(pulse, eg_flare);
	return pulse;
}

// True when the pulse is non-zero anywhere on the link-coordinate interval [p_a, p_b] (the vertex collapse rule).
bool eg_interval_lit(float p_a, float p_b, float column, float kind) {
	if (kind > 0.5 && eg_flare > 0.0) return true;
	float qa = eg_q_of_p(p_a);
	float qb = eg_q_of_p(p_b);
	float lo = min(qa, qb);
	float hi = max(qa, qb);
	for (int i = 0; i < eg_front_count; i++) {
		vec4 front = eg_fronts[i];
		if (abs(front.y - column) >= 0.5) continue;
		float w_lo = max(0.0, front.x - eg_pulse_shape.y);
		float w_hi = min(eg_circumference, front.x + eg_pulse_shape.x);
		if (w_hi > w_lo && hi > w_lo && lo < w_hi) return true;
	}
	return false;
}

// smoothstep(-0.2, 0.35, dot(surface_up, sun)) times the published solar visibility (0 during an eclipse).
float eg_day_at(float dot_up_sun, float solar_visibility) {
	return smoothstep(-0.2, 0.35, dot_up_sun) * clamp(solar_visibility, 0.0, 1.0);
}

// eg_day_at with the eclipse hub sun's day (ringworld_hub_day): max(eg_day_at, hub_day). Identical to eg_day_at when hub_day is 0.
float eg_day_at_hub(float dot_up_sun, float solar_visibility, float hub_day) {
	return max(eg_day_at(dot_up_sun, solar_visibility), clamp(hub_day, 0.0, 1.0));
}

// B = max(day(camera), day(fragment) * smoothstep(near, far, view distance)).
float eg_background(float day_camera, float day_fragment, float view_distance) {
	return max(day_camera, day_fragment * smoothstep(eg_day_distance.x, eg_day_distance.y, view_distance));
}

float eg_peak(float b) {
	return mix(eg_visibility.x, eg_visibility.y, b);
}

float eg_residual(float b) {
	return mix(eg_visibility.z, eg_visibility.w, b);
}

// I = (Peak(B) * pulse + Residual(B) + crawl) * (1 - atmosphere alpha). Multiply by the base emission for HDR.
float eg_intensity(float pulse, float b, float crawl, float atmosphere_alpha) {
	return (eg_peak(b) * pulse + eg_residual(b) + crawl) * (1.0 - clamp(atmosphere_alpha, 0.0, 1.0));
}
