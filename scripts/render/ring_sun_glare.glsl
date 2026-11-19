#[compute]
#version 450
// Sun glare draw pass (LF-1). 8x8 groups over a square window around the sun's pixel (push constant rect, clamped to the viewport by the effect). Adds the CIE 1999 general
// disability glare function (age 25, pigmentation 0.5) of the displayed sun: colour.rgb += strength x F x L_hold x gain x Omega x GSF(max(theta, r)), Omega = pi r^2.
// F and L_hold come from ring_sun_visibility.glsl. Alpha is untouched (FSR2 reads it). No floor, no boost: strength 1 is the eye's physical glare of the sun as displayed.
layout(local_size_x = 8, local_size_y = 8) in;
layout(std140, set = 0, binding = 0) uniform Frame {
	mat4 proj;
	mat4 inv_proj;
	vec4 viewport;
	vec4 sun_view;    // xyz direction (view space), w disc radius (rad)
	vec4 fallback;
	vec4 scalars;
	vec4 draw;        // x strength, y gain, z window half-angle theta_max (rad)
} F;
layout(rgba16f, set = 0, binding = 1) uniform restrict image2D scene_color;
layout(std430, set = 0, binding = 2) restrict readonly buffer State {
	vec4 result;
	vec4 hold;
	vec4 debug;
	vec4 meas;
	vec4 oracle;
} S;
layout(push_constant, std430) uniform Push { ivec4 rect; } pc;   // xy origin, zw size (px)

// CIE 1999 general disability glare function (per sr), theta in degrees, age 25, pigmentation 0.5.
float gsf(float theta) {
	const float A = 25.0, P = 0.5;
	float a4 = pow(A / 70.0, 4.0);
	float t2 = theta * theta;
	float x1 = 1.0 + t2 / (0.0046 * 0.0046);
	float x2 = 1.0 + t2 / (0.045 * 0.045);
	float x3 = 1.0 + t2 / (0.1 * 0.1);
	float a = (1.0 - 0.08 * a4) * (9.2e6 / (x1 * sqrt(x1)) + 1.5e5 / (x2 * sqrt(x2)));
	float b = (1.0 + 1.6 * a4) * (400.0 / x3 + 3e-8 * t2 + P * (1300.0 / (x3 * sqrt(x3)) + 0.8 / sqrt(x3)));
	return a + b + 2.5e-3 * P;
}

void main() {
	ivec2 local = ivec2(gl_GlobalInvocationID.xy);
	if (any(greaterThanEqual(local, pc.rect.zw))) return;
	float f = S.result.x;
	vec3 l = S.hold.rgb * F.draw.y;
	float strength = F.draw.x;
	if (f * strength * dot(l, vec3(0.2126, 0.7152, 0.0722)) < 1e-6) return;
	ivec2 p = pc.rect.xy + local;
	if (any(greaterThanEqual(p, ivec2(F.viewport.xy)))) return;
	vec2 ndc = (vec2(p) + 0.5) / F.viewport.xy * 2.0 - 1.0;
	vec4 near_point = F.inv_proj * vec4(ndc, 1.0, 1.0);
	vec3 dir = normalize(near_point.xyz / near_point.w);
	float r = max(F.sun_view.w, 1e-5);
	float theta = acos(clamp(dot(dir, normalize(F.sun_view.xyz)), -1.0, 1.0));
	float omega = 3.14159265 * r * r;
	float g = gsf(degrees(max(theta, r)));
	vec4 c = imageLoad(scene_color, p);
	// Fade to 0 over the outer quarter of the window so the cut-off tail leaves no square edge on dark ground (visible at high source gain).
	float edge = 1.0 - smoothstep(0.75 * F.draw.z, F.draw.z, theta);
	c.rgb += strength * f * l * omega * g * edge;
	imageStore(scene_color, p, c);
}
