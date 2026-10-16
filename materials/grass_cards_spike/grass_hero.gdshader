shader_type spatial;
// P0c hero layer: real tapered, curved solid blades (opaque, early-Z) clumped into tufts, instanced around the
// camera. Colour comes from the SAME per-fragment terrain sampling + spike_near_blade_color() as the cards.
// Mesh contract (GrassCardsSpikeAssets.BuildHeroTuftMesh): VERTEX = blade centreline at unit tuft height,
// NORMAL = blade face normal (horizontal, model space), UV = (side -1..1, t 0..1 along blade),
// UV2 = (half width in metres at unit scale, per-blade random), COLOR.rg = blade root xz offset encoded 0.5 + o/0.8.
// Instance: basis = yaw * (sx, sy, sx), CUSTOM = (wind phase, 0, 0, tuft random).
render_mode cull_disabled, diffuse_lambert, specular_disabled, skip_vertex_transform;
#include "res://materials/grass_cards_spike/spike_terrain_albedo.gdshaderinc"
#include "res://materials/grass_cards_spike/spike_grass_color.gdshaderinc"

uniform sampler2D terrain_albedo : source_color, filter_linear_mipmap, repeat_enable;
uniform float terrain_uv_scale = 0.2;
uniform float frag_lod_bias = -0.5;
uniform float hero_start = 4.0;        // metres where tufts start shrinking out
uniform float hero_end = 6.0;          // metres where they are gone (cards fully take over)
uniform float hero_dither = 1.4;       // per-tuft random distance offset so the fade has no visible ring
uniform float wind_strength = 0.12;    // identical to the card material
uniform vec2 wind_dir = vec2(0.8, 0.35);
uniform float normal_out = 0.45;
uniform float normal_face = 0.25;
uniform float backlight = 0.28;

varying vec2 v_wxz;
varying float v_g;
varying float v_r;
varying float v_tuft;
varying float v_side;
varying vec3 v_up_view;
varying vec3 v_u_view;
varying vec3 v_face_view;

void vertex() {
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
	float dist = length(root - cam);
	float dd = dist + (INSTANCE_CUSTOM.w - 0.5) * 2.0 * hero_dither;
	float hf = 1.0 - smoothstep(hero_start, hero_end, dd);
	float hh = hscale * hf;
	float ww = wscale * mix(0.45, 1.0, hf);

	vec3 nm = NORMAL;                              // model-space face normal (horizontal)
	vec3 um = vec3(nm.z, 0.0, -nm.x);              // model-space width axis
	vec3 uw = r0 * um.x + f0 * um.z;
	vec3 nw = r0 * nm.x + f0 * nm.z;
	vec3 p = root + ax * VERTEX.x + az * VERTEX.z + up * (VERTEX.y * hh) + uw * (UV.x * UV2.x * ww);
	float ph = INSTANCE_CUSTOM.x;
	float sway = sin(TIME * 1.7 + ph * 6.2831 + root.x * 0.07 + root.z * 0.05);
	p += vec3(wind_dir.x, 0.0, wind_dir.y) * (wind_strength * (0.6 + 0.4 * sway) * VERTEX.y * VERTEX.y * hh);

	VERTEX = (VIEW_MATRIX * vec4(p, 1.0)).xyz;
	NORMAL = normalize((VIEW_MATRIX * vec4(up, 0.0)).xyz);
	v_up_view = NORMAL;
	v_u_view = normalize((VIEW_MATRIX * vec4(uw, 0.0)).xyz);
	v_face_view = normalize((VIEW_MATRIX * vec4(nw, 0.0)).xyz);
	v_side = UV.x;
	vec2 ro = (COLOR.rg - 0.5) * 0.8;
	v_wxz = (root + ax * ro.x + az * ro.y).xz;
	v_g = UV.y;
	v_r = UV2.y;
	v_tuft = INSTANCE_CUSTOM.w;
}

void fragment() {
	vec2 ts = vec2(textureSize(terrain_albedo, 0));
	vec2 duv_x = dFdx(v_wxz) * terrain_uv_scale * ts;
	vec2 duv_y = dFdy(v_wxz) * terrain_uv_scale * ts;
	float lod = max(0.5 * log2(max(dot(duv_x, duv_x), dot(duv_y, duv_y)) + 1e-6) + frag_lod_bias, 0.0);
	vec3 ground = spike_grass_tint(textureLod(terrain_albedo, v_wxz * terrain_uv_scale, lod).rgb);
	vec3 col = spike_near_blade_color(ground, v_r, v_g, v_tuft);
	ALBEDO = col;
	ROUGHNESS = 1.0;
	vec3 f = v_face_view * (dot(v_face_view, VIEW) >= 0.0 ? 1.0 : -1.0);
	NORMAL = normalize(v_up_view + v_u_view * (v_side * normal_out) + f * normal_face);
	BACKLIGHT = col * backlight;
}
