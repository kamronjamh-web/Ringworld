shader_type spatial;
// Grass clump cards (variant D, grass-cards plan step S4). One 3-card alpha-scissor mesh per instance
// (RingGrassCardMeshFactory), coloured from the Terrain3D surface albedo at the card root and lit with the terrain's own
// direct-light body, so the card base matches the ground in every light condition.
//
// Instance contract (MultiMesh, custom data + colours enabled):
//   transform : basis X/Z lie in the ground tangent plane (yaw * width scale), basis Y is the card's up axis with lean baked
//               in (height scale); origin = root. The ground normal is recovered as normalize(cross(Z, X)).
//   COLOR     : packed biome weights (desert, forest, tundra, alpine); grass is the remainder.
//   CUSTOM    : x = kind * 4 + atlasVariant + rank * 0.999 (kind 0 card, 1 low upright layer), y = tuft random,
//               z/w = canonical wind phases (windAround, windAcross).
//   instance uniforms (9 of 16): grass_gust_origin_world, grass_gust_around_world, grass_gust_across_world,
//               grass_gust_origin_uv, grass_modifier_layer/_origin_world/_around_world/_across_world,
//               grass_terrain_origin_uv.
// Alpha-scissor: ALPHA comes from the atlas mask first; everything after it only feeds the colour pass, so the depth
// prepass reads the mask alone. No shadow casting: set cast_shadow = OFF on the renderer.
render_mode cull_disabled, diffuse_lambert, specular_disabled, skip_vertex_transform;
#include "res://materials/ring_grass_card_common.gdshaderinc"
#include "res://materials/ring_grass_card_color.gdshaderinc"

uniform sampler2D grass_card_atlas : filter_linear_mipmap, repeat_disable;
uniform float grass_card_alpha_cutoff = 0.5;
uniform float grass_card_single_start = 90.0;   // metres where the 3->1 card collapse begins
uniform float grass_card_single_end = 160.0;    // metres where only one camera-facing card remains
uniform float grass_card_single_width_gain = 0.6;
uniform float grass_card_edge_on_start = 0.93;
uniform float grass_card_tilt = 0.08;
// Far (vertex) colour: root darkening / tip gain converge to 1 over converge_start..end of reach.
uniform float grass_card_converge_start_percent = 60.0;
uniform float grass_card_converge_end_percent = 90.0;
uniform float grass_card_root_dark = 0.78;
uniform float grass_card_tip_gain = 1.25;
uniform vec3 grass_card_tip_shift = vec3(1.06, 1.0, 0.82);
uniform float grass_card_color_variation = 0.07;
uniform float grass_card_vertex_lod_bias = 3.0; // finer than the footprint so bright/dark patches survive
uniform float grass_card_fragment_color_meters = 70.0; // per-fragment ground sampling fades to per-vertex by here
// Form: bent normals + translucency.
uniform float grass_card_normal_out = 0.45;
uniform float grass_card_normal_face = 0.25;
uniform float grass_card_backlight = 0.28;
uniform float grass_card_normal_tip_start = 0.6; // height gradient where the normal reaches the full bend

varying vec3 v_base;
varying vec3 v_tip;
varying vec2 v_atlas_uv;
varying vec4 v_shape;  // side, tuft random, near-colour blend, per-tuft colour variation
varying vec4 v_light;  // day factor, cloud occlusion, macro-shadow uv
varying vec3 v_up_view;
varying vec3 v_u_view;
varying vec3 v_face_view;
varying vec2 v_terrain_uv;
varying vec3 v_haze_T;
varying vec3 v_haze_S;
varying vec4 v_modifier;
varying vec3 v_ground_world;
varying flat vec4 v_biome;

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
	vec3 ground_up = normalize(cross(az, ax));
	if (dot(ground_up, up) < 0.0) ground_up = -ground_up;
	vec3 d = root - CAMERA_POSITION_WORLD;
	float dist = length(d);

	// Instance data.
	float custom_x = INSTANCE_CUSTOM.x;
	float kind_slot = floor(custom_x);
	float rank = (custom_x - kind_slot) / 0.999;
	float kind = floor(kind_slot * 0.25);
	float variant = kind_slot - kind * 4.0;
	bool low = kind > 0.5;
	float scale;
	float near_width;
	if (low) {
		scale = grass_card_low_scale(rank, dist);
		near_width = 1.0;
	} else {
		float keep = grass_card_keep(dist);
		float grow = rank < keep ? grass_card_grow_in(keep, rank) : 0.0;
		scale = grow < grass_card_min_grow_in ? 0.0 : grass_card_compensation(dist) * grow;
		near_width = grass_card_near_width_scale(dist);
	}
	float fade = grass_card_fade(dist);
	if (scale <= 0.0 || fade <= 0.0) {
		VERTEX = vec3(0.0); // culled: zero-area triangles
	} else {
	vec3 dh = d - ground_up * dot(d, ground_up);
	dh = dh / max(length(dh), 1e-4);
	float c = smoothstep(grass_card_single_start, grass_card_single_end, dist);
	float a = k * 1.0471976;
	vec3 u = cos(a) * r0 - sin(a) * f0;
	vec3 n = sin(a) * r0 + cos(a) * f0;
	float s = (k > 0.5 && k < 1.5) ? -1.0 : 1.0;
	vec3 tilt = s * n;
	float wf = 1.0;
	if (k < 0.5) {
		vec3 fr = normalize(cross(ground_up, dh));
		if (dot(fr, u) < 0.0) fr = -fr;
		vec3 tf = dh * (dot(dh, tilt) >= 0.0 ? 1.0 : -1.0);
		u = normalize(mix(u, fr, c));
		tilt = normalize(mix(tilt, tf, c));
		n = normalize(mix(n, cross(ground_up, fr), c));
		wf = 1.0 + grass_card_single_width_gain * c;
	} else {
		wf = 1.0 - c;
	}
	// Edge-on cards contribute only a sliver; shrink them instead of rasterising it.
	wf *= 1.0 - smoothstep(grass_card_edge_on_start, 0.995, abs(dot(u, dh)));

	float hh = hscale * scale * fade;
	float ww = wscale * scale * near_width * wf * (0.4 + 0.6 * fade);
	vec3 offset_h = u * (lx * ww);
	vec3 p = root + offset_h + up * (yv * hh) + tilt * (grass_card_tilt * yv * yv * hh);
	float short_coeff;
	vec3 wind = grass_card_tip_displacement(root, up, hh, INSTANCE_CUSTOM.y, INSTANCE_CUSTOM, short_coeff);
	p += wind * (yv * yv) - up * (short_coeff * yv * yv * yv);

	VERTEX = (VIEW_MATRIX * vec4(p, 1.0)).xyz;
	NORMAL = normalize((VIEW_MATRIX * vec4(ground_up, 0.0)).xyz);
	v_up_view = NORMAL;
	v_u_view = normalize((VIEW_MATRIX * vec4(u, 0.0)).xyz);
	v_face_view = normalize((VIEW_MATRIX * vec4(n, 0.0)).xyz);
	v_shape.x = lx * 2.5;
	v_shape.y = INSTANCE_CUSTOM.y;
	v_biome = clamp(COLOR, vec4(0.0), vec4(1.0));

	// Terrain-derived colour at the ground-projected vertex position (coarse mip: far / fallback path).
	vec3 gp = root + offset_h;
	v_ground_world = gp;
	vec2 canonical = grass_card_canonical_offset(gp, grass_gust_origin_world, grass_gust_around_world, grass_gust_across_world);
	v_terrain_uv = grass_card_terrain_uv(canonical, grass_terrain_origin_uv, gp);
	v_light.zw = grass_card_shadow_uv(canonical, grass_terrain_origin_uv);
	float texel_width = float(textureSize(biome_grass_albedo, 0).x);
	float lod = max(log2(max(0.6 * wscale * scale * biome_texture_scale * texel_width, 1.0)) - grass_card_vertex_lod_bias, 0.0);
	vec3 ground = grass_card_ground_two_biomes(v_terrain_uv, lod, v_biome);
	float conv = smoothstep(grass_card_converge_start_percent * 0.01 * grass_card_reach, grass_card_converge_end_percent * 0.01 * grass_card_reach, dist);
	v_base = ground * mix(ringworld_grass_albedo_scale * grass_card_root_dark, 1.0, conv);
	v_tip = ground * mix(ringworld_grass_albedo_scale * grass_card_tip_gain, 1.0, conv) * mix(grass_card_tip_shift, vec3(1.0), conv);
	v_shape.w = (INSTANCE_CUSTOM.y - 0.5) * 2.0 * grass_card_color_variation * (1.0 - conv);
	v_shape.z = 1.0 - smoothstep(grass_card_fragment_color_meters * 0.55, grass_card_fragment_color_meters, dist);
	v_atlas_uv = vec2((variant + UV.x) * 0.25, UV.y);

	v_light.x = grass_card_day_factor(ground_up);
	v_light.y = sample_local_cloud_shadow(gp) * v_light.x;
	RingHaze haze = ring_haze_sample(ring_atmosphere_camera_world, gp, ring_atmosphere_center_world, v_light.x);
	v_haze_T = haze.T;
	v_haze_S = ring_haze_inscatter(haze);
	v_modifier = grass_modifier_tint(root);
	}
}

void fragment() {
	vec4 m = texture(grass_card_atlas, v_atlas_uv);
	ALPHA = m.r;
	ALPHA_SCISSOR_THRESHOLD = grass_card_alpha_cutoff;
	// Far colour (vertex path) is the blend target.
	vec3 col = mix(v_base, v_tip, m.g) * (1.0 + v_shape.w + (m.b - 0.5) * 2.0 * grass_card_color_variation * 0.5);
	if (v_shape.z > 0.002) {
		float lod = grass_card_derivative_lod(v_terrain_uv, float(textureSize(biome_grass_albedo, 0).x));
		vec3 ground = grass_card_ground_all_biomes(v_terrain_uv, lod, v_biome);
		col = mix(col, grass_card_blade_color(ground, m.b, m.g, v_shape.y), v_shape.z);
	}
	col = mix(col, v_modifier.rgb, clamp(v_modifier.a, 0.0, 1.0));
	vec3 emission;
	ALBEDO = grass_card_finish_albedo(col, v_light.x, v_haze_T, v_haze_S, emission);
	EMISSION = emission;
	ROUGHNESS = 1.0;
	// Ground normal at the root, bending toward the card's outward / face normal at the tip.
	vec3 f = v_face_view * (dot(v_face_view, VIEW) >= 0.0 ? 1.0 : -1.0);
	vec3 bent = v_up_view + v_u_view * (v_shape.x * grass_card_normal_out) + f * grass_card_normal_face;
	NORMAL = normalize(mix(v_up_view, bent, smoothstep(0.0, max(grass_card_normal_tip_start, 0.01), m.g)));
	BACKLIGHT = col * grass_card_backlight;
	AO = 1.0 - v_light.y * 0.72;
	AO_LIGHT_AFFECT = 0.0;
}

void light() {
	float direct_visibility;
	DIFFUSE_LIGHT += ring_terrain_direct_diffuse(NORMAL, LIGHT, ATTENUATION, LIGHT_IS_DIRECTIONAL, LIGHT_COLOR,
		VIEW_MATRIX, v_light.x, v_light.y, v_light.zw, v_ground_world, ring_viewer_world_position, direct_visibility);
	// Diffuse translucency from the far side of the blade, scaled like the terrain's diffuse term.
	DIFFUSE_LIGHT += (LIGHT_COLOR / PI) * BACKLIGHT * max(dot(-NORMAL, LIGHT), 0.0) * direct_visibility;
}
