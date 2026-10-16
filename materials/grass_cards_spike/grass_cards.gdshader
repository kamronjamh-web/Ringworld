shader_type spatial;
// Grass-cards spike shader. mode 0 = P0 (per-vertex terrain colour). mode 1 = P0b: per-fragment terrain colour
// near the camera (blends to per-vertex far), per-blade variation, root AO / tip gain, bent normals + backlight.
// Alpha-scissor depth prepass reads only the mask; the colour path is only evaluated in the colour pass.
render_mode cull_disabled, diffuse_lambert, specular_disabled, skip_vertex_transform;
#include "res://materials/grass_cards_spike/spike_terrain_albedo.gdshaderinc"
#include "res://materials/grass_cards_spike/spike_grass_color.gdshaderinc"

uniform sampler2D tuft_atlas : filter_linear_mipmap, repeat_disable;
uniform sampler2D terrain_albedo : source_color, filter_linear_mipmap, repeat_enable;
uniform int mode = 0;
uniform float terrain_uv_scale = 0.2;
uniform float reach = 400.0;
uniform float alpha_cutoff = 0.5;
uniform float single_start = 90.0;   // metres where the 3->1 card collapse begins
uniform float single_end = 160.0;    // metres where only one camera-facing card remains
uniform float single_width_gain = 0.6;
uniform float fade_start_pct = 0.85; // scale-out band (fraction of reach)
uniform float converge_start_pct = 0.6;
uniform float converge_end_pct = 0.9;
uniform float root_dark = 0.78;
uniform float tip_gain = 1.25;
uniform vec3 tip_shift = vec3(1.06, 1.0, 0.82);
uniform float color_variation = 0.07;
uniform float lod_bias = 3.0; // finer than footprint so bright/dark patches survive
uniform float tilt_amount = 0.22;
uniform float wind_strength = 0.12;
uniform vec2 wind_dir = vec2(0.8, 0.35);
uniform float edge_on_start = 0.93;

// P0c: thin the cards near the camera where the hero-blade layer provides the density (thin_min = 1 disables).
uniform float thin_min = 1.0;
uniform float thin_start = 3.5;
uniform float thin_end = 7.5;

// P0b per-fragment colour
uniform float frag_color_end = 70.0;   // metres: per-fragment ground sampling fades to per-vertex by here
uniform float frag_lod_bias = -0.5;
// P0b form
uniform float normal_out = 0.0;        // outward (per-card side) normal bend
uniform float normal_face = 0.0;       // card-face normal weight
uniform float backlight = 0.0;         // diffuse translucency

varying vec3 v_base;
varying vec3 v_tip;
varying vec2 v_uv;
varying float v_var;
varying vec3 v_up_view;
varying vec3 v_u_view;
varying vec3 v_face_view;
varying vec2 v_wxz;
varying float v_near;
varying float v_side;
varying float v_low;
varying float v_tuft;

void vertex() {
	float k = UV2.y;
	float lx = UV2.x;
	float yv = VERTEX.y;
	vec3 root = MODEL_MATRIX[3].xyz;
	vec3 ax = MODEL_MATRIX[0].xyz;
	vec3 ay = MODEL_MATRIX[1].xyz;
	vec3 az = MODEL_MATRIX[2].xyz;
	float wscale = length(ax);
	float hscale = length(ay);
	vec3 up = ay / hscale;
	vec3 r0 = ax / wscale;
	vec3 f0 = az / wscale;
	vec3 cam = INV_VIEW_MATRIX[3].xyz;
	vec3 d = root - cam;
	float dist = length(d);
	vec3 dh = d - up * dot(d, up);
	dh = dh / max(length(dh), 1e-4);

	float c = smoothstep(single_start, single_end, dist);
	float a = k * 1.0471976;
	vec3 u = cos(a) * r0 - sin(a) * f0;
	vec3 n = sin(a) * r0 + cos(a) * f0;
	float s = (k > 0.5 && k < 1.5) ? -1.0 : 1.0;
	vec3 tilt = s * n;
	float wf = 1.0;
	if (k < 0.5) {
		vec3 fr = normalize(cross(up, dh));
		if (dot(fr, u) < 0.0) fr = -fr;
		vec3 tf = dh * (dot(dh, tilt) >= 0.0 ? 1.0 : -1.0);
		u = normalize(mix(u, fr, c));
		tilt = normalize(mix(tilt, tf, c));
		n = normalize(mix(n, cross(up, fr), c));
		wf = 1.0 + single_width_gain * c;
	} else {
		wf = 1.0 - c;
	}
	// Edge-on cards contribute only a sliver; shrink them instead of rasterising it.
	wf *= 1.0 - smoothstep(edge_on_start, 0.995, abs(dot(u, dh)));

	float fade = 1.0 - smoothstep(fade_start_pct * reach, reach, dist);
	float keep = mix(thin_min, 1.0, smoothstep(thin_start, thin_end, dist));
	float thin = keep >= 0.999 ? 1.0 : smoothstep(0.0, 0.15, keep - INSTANCE_CUSTOM.w * 0.999);
	float hh = hscale * fade * thin;
	float ww = wscale * wf * (0.4 + 0.6 * fade) * thin;
	vec3 offset_h = u * (lx * ww);
	vec3 p = root + offset_h + up * (yv * hh) + tilt * (tilt_amount * yv * yv * hh);
	float ph = INSTANCE_CUSTOM.x;
	float sway = sin(TIME * 1.7 + ph * 6.2831 + root.x * 0.07 + root.z * 0.05);
	p += vec3(wind_dir.x, 0.0, wind_dir.y) * (wind_strength * (0.6 + 0.4 * sway) * yv * yv * hh);

	VERTEX = (VIEW_MATRIX * vec4(p, 1.0)).xyz;
	NORMAL = normalize((VIEW_MATRIX * vec4(up, 0.0)).xyz);
	v_up_view = NORMAL;
	v_u_view = normalize((VIEW_MATRIX * vec4(u, 0.0)).xyz);
	v_face_view = normalize((VIEW_MATRIX * vec4(n, 0.0)).xyz);
	v_side = lx * 2.5;
	v_low = INSTANCE_CUSTOM.z;
	v_tuft = INSTANCE_CUSTOM.w;

	// Terrain-derived colour at the ground-projected vertex position, coarse mip (far / fallback path).
	vec2 ts = vec2(textureSize(terrain_albedo, 0));
	float lod = max(log2(max(0.6 * wscale * terrain_uv_scale * ts.x, 1.0)) - lod_bias, 0.0);
	vec2 gxz = (root + offset_h).xz;
	v_wxz = gxz;
	vec3 A = spike_grass_tint(textureLod(terrain_albedo, gxz * terrain_uv_scale, lod).rgb);
	float conv = smoothstep(converge_start_pct * reach, converge_end_pct * reach, dist);
	v_base = A * mix(root_dark, 1.0, conv);
	v_tip = A * mix(tip_gain, 1.0, conv) * mix(tip_shift, vec3(1.0), conv);
	v_var = (INSTANCE_CUSTOM.w - 0.5) * 2.0 * color_variation * (1.0 - conv);
	v_near = 1.0 - smoothstep(frag_color_end * 0.55, frag_color_end, dist);
	v_uv = vec2((INSTANCE_CUSTOM.y + UV.x) * 0.25, UV.y);
}

void fragment() {
	vec4 m = texture(tuft_atlas, v_uv);
	ALPHA = m.r;
	ALPHA_SCISSOR_THRESHOLD = alpha_cutoff;
	if (mode == 0) {
		ALBEDO = mix(v_base, v_tip, m.g) * (1.0 + v_var + (m.b - 0.5) * 2.0 * color_variation * 0.5);
		NORMAL = v_up_view;
		ROUGHNESS = 1.0;
	} else {
	// Far colour (same as P0 mix) for the blend target.
	vec3 far_col = mix(v_base, v_tip, m.g) * (1.0 + v_var + (m.b - 0.5) * 2.0 * color_variation * 0.5);
	vec2 ts = vec2(textureSize(terrain_albedo, 0));
	vec2 duv_x = dFdx(v_wxz) * terrain_uv_scale * ts;
	vec2 duv_y = dFdy(v_wxz) * terrain_uv_scale * ts;
	float lod = max(0.5 * log2(max(dot(duv_x, duv_x), dot(duv_y, duv_y)) + 1e-6) + frag_lod_bias, 0.0);
	vec3 col = far_col;
	if (v_near > 0.002) {
		vec3 ground = spike_grass_tint(textureLod(terrain_albedo, v_wxz * terrain_uv_scale, lod).rgb);
		vec3 c = spike_near_blade_color(ground, m.b, m.g, v_tuft);
		col = mix(far_col, c, v_near);
	}
	ALBEDO = col;
	ROUGHNESS = 1.0;
	vec3 f = v_face_view * (dot(v_face_view, VIEW) >= 0.0 ? 1.0 : -1.0);
	NORMAL = normalize(v_up_view + v_u_view * (v_side * normal_out) + f * normal_face);
	BACKLIGHT = col * backlight;
	}
}
