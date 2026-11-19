#[compute]
#version 450
// Sun glare visibility pass (LF-1; docs/sun-flare-shafts-near-fog-plan.md section A, "LF-0 result"). One 64-thread group. Threads 0..47 take Vogel-spiral taps inside the sun disc
// (radius r), threads 48..63 take 16 taps on an annulus at 1.9 r. A disc tap is visible when main depth is the far plane (reversed-Z 0), the scaled far viewport drew nothing there
// (Sunshine far_opaque_depth_texture = 0) and no cloud is in front of it (1 - Sunshine accumulation alpha). Taps that project off-screen count as geometry-free with the CPU
// camera-column cloud transmittance. F = mean(tap) x (1 - eclipse coverage) x storm_hide x enclosure gate (CPU scalars).
// The displayed sun radiance L is measured from the resolved HDR colour (brightest clean disc tap minus the mean clean annulus tap) before the draw pass writes anything,
// and held in a small persistent buffer (L_hold = L / storm_hide) so a covered sun keeps its last clean value. No temporal smoothing. Keep the UBO in sync with RingSunGlareComposite.gd.
layout(local_size_x = 64) in;
layout(std140, set = 0, binding = 0) uniform Frame {
	mat4 proj;
	mat4 inv_proj;
	vec4 viewport;    // xy internal size px
	vec4 sun_view;    // xyz sun direction in view space, w disc radius (rad)
	vec4 fallback;    // rgb sun_color x sun_disk_intensity
	vec4 scalars;     // x eclipse coverage, y storm_hide, z enclosure gate, w camera cloud transmittance
	vec4 draw;        // x strength, y gain, z theta_max (draw pass only)
} F;
layout(set = 0, binding = 1) uniform sampler2D scene_depth;
layout(set = 0, binding = 2) uniform sampler2D cloud_color;   // Sunshine accumulation colour: a = cloud density in front of an infinitely far point
layout(set = 0, binding = 3) uniform sampler2D far_depth;     // far-view opaque distance in metres, 0 = nothing
layout(rgba16f, set = 0, binding = 4) uniform restrict readonly image2D scene_color;
layout(std430, set = 0, binding = 5) restrict buffer State {
	vec4 result;      // x F, y F_taps, z clean disc taps, w clean annulus taps
	vec4 hold;        // rgb L_hold, w 1 once initialised
	vec4 debug;       // x main-depth-empty fraction, y far-empty fraction, z cloud fraction, w luminance of L_meas
	vec4 meas;        // rgb L_meas, w 1 when measured this frame
	vec4 oracle;      // image oracle (tests): x mean luminance of on-screen disc taps, y of on-screen annulus taps, z / w their counts
} S;

shared float s_vis[64];
shared float s_main[64];
shared float s_far[64];
shared float s_cloud[64];
shared float s_clean[64];
shared float s_on[64];
shared vec3 s_color[64];

void main() {
	uint t = gl_LocalInvocationID.x;
	bool disc = t < 48u;
	float r = max(F.sun_view.w, 1e-5);
	vec3 s = normalize(F.sun_view.xyz);
	vec3 up = abs(s.y) < 0.9 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
	vec3 t1 = normalize(cross(s, up));
	vec3 t2 = cross(s, t1);
	float rho, phi;
	if (disc) { rho = r * sqrt((float(t) + 0.5) / 48.0); phi = float(t) * 2.39996323; }
	else { rho = r * 1.9; phi = float(t - 48u) * (6.28318531 / 16.0) + 0.19; }
	vec3 dir = normalize(s + rho * (cos(phi) * t1 + sin(phi) * t2));
	float vis = 0.0, mn = 1.0, fr = 1.0, cl = 1.0, clean = 0.0, on = 0.0;
	vec3 col = vec3(0.0);
	vec4 clip = F.proj * vec4(dir, 1.0);
	bool front = dir.z < 0.0 && clip.w > 1e-6;
	vec2 uv = clip.xy / max(clip.w, 1e-6) * 0.5 + 0.5;
	bool on_screen = front && all(greaterThanEqual(uv, vec2(0.0))) && all(lessThan(uv, vec2(1.0)));
	if (on_screen) {
		on = 1.0;
		ivec2 px = clamp(ivec2(uv * F.viewport.xy), ivec2(0), ivec2(F.viewport.xy) - 1);
		mn = texelFetch(scene_depth, px, 0).r == 0.0 ? 1.0 : 0.0;
		fr = texture(far_depth, uv).r == 0.0 ? 1.0 : 0.0;
		float alpha = clamp(texture(cloud_color, uv).a, 0.0, 1.0);
		cl = 1.0 - alpha;
		vis = mn * fr * cl;
		col = imageLoad(scene_color, px).rgb;
		clean = (mn * fr > 0.5 && alpha <= 0.02) ? 1.0 : 0.0;
	} else if (front) {
		cl = clamp(F.scalars.w, 0.0, 1.0);
		vis = cl;
	}
	s_vis[t] = vis; s_main[t] = mn; s_far[t] = fr; s_cloud[t] = cl; s_clean[t] = clean; s_on[t] = on; s_color[t] = col;
	barrier();
	if (t != 0u) return;
	float sum = 0.0, sm = 0.0, sf = 0.0, sc = 0.0;
	float n_disc = 0.0, n_ann = 0.0;
	float d_lum = 0.0, d_n = 0.0, a_lum = 0.0, a_n = 0.0;
	float best_lum = -1.0; vec3 best = vec3(0.0); vec3 ann = vec3(0.0);
	for (uint i = 0u; i < 48u; i++) {
		sum += s_vis[i]; sm += s_main[i]; sf += s_far[i]; sc += s_cloud[i];
		if (s_on[i] > 0.5) { d_n += 1.0; d_lum += dot(s_color[i], vec3(0.2126, 0.7152, 0.0722)); }
		if (s_clean[i] > 0.5) {
			n_disc += 1.0;
			float lum = dot(s_color[i], vec3(0.2126, 0.7152, 0.0722));
			if (lum > best_lum) { best_lum = lum; best = s_color[i]; }
		}
	}
	for (uint i = 48u; i < 64u; i++) {
		if (s_on[i] > 0.5) { a_n += 1.0; a_lum += dot(s_color[i], vec3(0.2126, 0.7152, 0.0722)); }
		if (s_clean[i] > 0.5) { n_ann += 1.0; ann += s_color[i]; }
	}
	float f_taps = (s.z < 0.0) ? sum / 48.0 : 0.0;
	float hide = clamp(F.scalars.y, 0.0, 1.0);
	float f = f_taps * (1.0 - clamp(F.scalars.x, 0.0, 1.0)) * hide * clamp(F.scalars.z, 0.0, 1.0);
	vec3 hold = S.hold.w > 0.5 ? S.hold.rgb : F.fallback.rgb;
	vec3 l_meas = vec3(0.0);
	float measured = 0.0;
	if (n_disc >= 8.0 && n_ann >= 4.0 && hide >= 0.25) {
		l_meas = max(best - ann / n_ann, vec3(0.0));
		hold = l_meas / hide;
		measured = 1.0;
	}
	S.result = vec4(f, f_taps, n_disc, n_ann);
	S.hold = vec4(hold, 1.0);
	S.debug = vec4(sm / 48.0, sf / 48.0, sc / 48.0, dot(l_meas, vec3(0.2126, 0.7152, 0.0722)));
	S.meas = vec4(l_meas, measured);
	S.oracle = vec4(d_lum / max(d_n, 1.0), a_lum / max(a_n, 1.0), d_n, a_n);
}
