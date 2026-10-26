#[vertex]
#version 450
// Rainbow (docs/storm-revamp-s3-storm-arrival-design.md section 6, WO-F1): a full-screen additive pass. The CPU (RingRainbow.cs) places the antisolar point and publishes
// five azimuth strengths (how much sunlit rain stands along the visible arc); this shader turns each pixel's view ray into an angle from the antisolar point and paints
// the primary bow (40.6-42.4 deg, red outside), the secondary (50.3-53.5 deg, reversed, 0.43x) and a faint glow inside the primary (Alexander's dark band then reads
// without darkening anything). Drawn by RingRainbowComposite.gd after Sunshine and before the rain streaks and rain haze, so near rain and the heavy-rain veil sit in front.
void main() {
	vec2 p = vec2(float((gl_VertexIndex << 1) & 2), float(gl_VertexIndex & 2));
	gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}

#[fragment]
#version 450
layout(set = 0, binding = 0, std140) uniform Frame {
	mat4 inv_proj;
	vec4 cam_x; vec4 cam_y; vec4 cam_z;   // world-space camera axes
	vec4 viewport;                        // xy: internal size
	vec4 anti;                            // xyz: antisolar direction (world), w: half arc psi_max (rad)
	vec4 top;                             // xyz: u, the direction perpendicular to anti that points up, w: global strength
	vec4 side;                            // xyz: v = anti x u, w: light scale
	vec4 az_a;                            // strengths of azimuth samples 0..3
	vec4 az_b;                            // x: sample 4, y: near depth (m), z: depth range (m)
} F;
layout(set = 0, binding = 1) uniform sampler2D scene_depth;
layout(location = 0) out vec4 out_color;

const float PI = 3.14159265358979;

vec3 spectrum(float t) {
	// Violet (0) to red (1).
	const vec3 stops[7] = vec3[7](vec3(0.45, 0.0, 0.90), vec3(0.05, 0.15, 1.0), vec3(0.0, 0.85, 0.75), vec3(0.15, 1.0, 0.1), vec3(1.0, 0.9, 0.0), vec3(1.0, 0.45, 0.0), vec3(1.0, 0.04, 0.0));
	float x = clamp(t, 0.0, 1.0) * 6.0;
	int i = min(int(floor(x)), 5);
	return mix(stops[i], stops[i + 1], x - float(i));
}

float azimuth_strength(float psi) {
	float half_arc = max(F.anti.w, 0.05);
	float f = clamp((psi + half_arc) / (2.0 * half_arc), 0.0, 1.0) * 4.0;
	float s[5] = float[5](F.az_a.x, F.az_a.y, F.az_a.z, F.az_a.w, F.az_b.x);
	int i = min(int(floor(f)), 3);
	float v = mix(s[i], s[i + 1], smoothstep(0.0, 1.0, f - float(i)));
	// Beyond the sampled arc the bow dies out over 0.25 rad.
	float outside = max(abs(psi) - half_arc, 0.0);
	return v * (1.0 - smoothstep(0.0, 0.25, outside));
}

void main() {
	vec2 ndc = gl_FragCoord.xy / F.viewport.xy * 2.0 - 1.0;
	vec4 near_point = F.inv_proj * vec4(ndc, 1.0, 1.0);
	vec3 view = normalize(near_point.xyz / near_point.w);
	vec3 ray = normalize(view.x * F.cam_x.xyz + view.y * F.cam_y.xyz + view.z * F.cam_z.xyz);
	float theta = degrees(acos(clamp(dot(ray, F.anti.xyz), -1.0, 1.0)));
	if (theta < 20.0 || theta > 57.0) discard;
	float psi = atan(dot(ray, F.side.xyz), dot(ray, F.top.xyz));
	float strength = azimuth_strength(psi) * F.top.w * F.side.w;
	if (strength <= 0.0005) discard;

	// Primary: violet inside, red outside; soft 0.3 deg edges (the sun disc smears the bow by 0.5 deg).
	float t1 = clamp((theta - 40.6) / 1.8, 0.0, 1.0);
	float env1 = smoothstep(40.3, 40.9, theta) * (1.0 - smoothstep(42.1, 42.7, theta));
	vec3 color = spectrum(t1) * env1;
	// Secondary: reversed colours, 0.43x.
	float t2 = clamp((theta - 50.3) / 3.2, 0.0, 1.0);
	float env2 = smoothstep(50.0, 50.8, theta) * (1.0 - smoothstep(53.0, 53.8, theta));
	color += spectrum(1.0 - t2) * env2 * 0.43;
	// Inside the primary: a faint white glow, 0.15x at the bow fading to 0 at 25 deg.
	color += vec3(0.15) * smoothstep(25.0, 40.6, theta) * (1.0 - smoothstep(40.6, 41.2, theta));

	float depth_fade = 1.0;
	float d = texelFetch(scene_depth, ivec2(gl_FragCoord.xy), 0).r;
	if (d > 0.0) {
		vec4 sv = F.inv_proj * vec4(ndc, d, 1.0);
		float dist = length(sv.xyz / sv.w);
		depth_fade = clamp((dist - F.az_b.y) / max(F.az_b.z, 1.0), 0.0, 1.0);
	}
	out_color = vec4(color * strength * depth_fade, 1.0);
}
