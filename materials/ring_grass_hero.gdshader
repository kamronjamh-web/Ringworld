shader_type spatial;
// Grass hero blades (variant D, grass-cards plan step S4): real tapered, curved solid blades (opaque, early-Z) clumped into
// 8-blade tufts, placed on a camera-centred ring of 4 m cells. Colour comes from the SAME per-fragment terrain sampling and
// grass_card_blade_color() as the cards (ring_grass_card_color.gdshaderinc), lighting from the terrain's direct-light body.
//
// Mesh contract (RingGrassHeroTuftMeshFactory): VERTEX = blade centreline at unit tuft height with the blade root offset
// included, NORMAL = blade face normal (horizontal, model space), UV = (side -1..1, t 0..1 along the blade),
// UV2 = (half width in metres at unit scale, per-blade random), CUSTOM0.xy = blade root offset in metres (unit scale). No COLOR.
// Instance contract: basis = yaw * (sx, sy, sx) in the ground tangent plane (Y = ground normal, no lean), COLOR = biome
// weights (desert, forest, tundra, alpine), CUSTOM = (unused, tuft random, windAround, windAcross),
// instance uniforms as the card shader (grass_terrain_origin_uv + gust + modifier = 9 of 16).
render_mode cull_disabled, diffuse_lambert, specular_disabled, skip_vertex_transform;
#include "res://materials/ring_grass_card_common.gdshaderinc"
#include "res://materials/ring_grass_card_color.gdshaderinc"

uniform float grass_hero_start = 4.0;        // metres where tufts start shrinking out
uniform float grass_hero_end = 6.0;          // metres where they are gone (cards fully take over)
uniform float grass_hero_dither = 1.4;       // per-tuft random distance offset so the fade has no visible ring
uniform float grass_hero_min_width = 0.45;   // blade width fraction at the end of the fade
uniform float grass_card_normal_out = 0.45;
uniform float grass_card_normal_face = 0.25;
uniform float grass_card_backlight = 0.28;

varying vec2 v_terrain_uv;
varying float v_g;
varying float v_r;
varying float v_tuft;
varying float v_side;
varying vec3 v_up_view;
varying vec3 v_u_view;
varying vec3 v_face_view;
varying vec4 v_atmosphere;
varying vec4 v_modifier;
varying float v_day;
varying float v_cloud;
varying vec2 v_shadow_uv;
varying vec3 v_ground_world;
varying flat vec4 v_biome;

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
	float dist = distance(root, CAMERA_POSITION_WORLD);
	float dd = dist + (INSTANCE_CUSTOM.y - 0.5) * 2.0 * grass_hero_dither;
	float hf = 1.0 - smoothstep(grass_hero_start, grass_hero_end, dd);
	if (hf <= 0.0) {
		VERTEX = vec3(0.0); // gone: zero-area triangles
	} else {
	float hh = hscale * hf;
	float ww = wscale * mix(grass_hero_min_width, 1.0, hf);

	vec3 nm = NORMAL;                              // model-space face normal (horizontal)
	vec3 um = vec3(nm.z, 0.0, -nm.x);              // model-space width axis
	vec3 uw = r0 * um.x + f0 * um.z;
	vec3 nw = r0 * nm.x + f0 * nm.z;
	float t = clamp(VERTEX.y, 0.0, 1.0);
	vec3 p = root + ax * VERTEX.x + az * VERTEX.z + up * (VERTEX.y * hh) + uw * (UV.x * UV2.x * ww);
	float short_coeff;
	vec3 wind = grass_card_tip_displacement(root, up, hh, INSTANCE_CUSTOM.y, INSTANCE_CUSTOM, short_coeff);
	p += wind * (t * t) - up * (short_coeff * t * t * t);

	VERTEX = (VIEW_MATRIX * vec4(p, 1.0)).xyz;
	NORMAL = normalize((VIEW_MATRIX * vec4(up, 0.0)).xyz);
	v_up_view = NORMAL;
	v_u_view = normalize((VIEW_MATRIX * vec4(uw, 0.0)).xyz);
	v_face_view = normalize((VIEW_MATRIX * vec4(nw, 0.0)).xyz);
	v_side = UV.x;
	v_g = UV.y;
	v_r = UV2.y;
	v_tuft = INSTANCE_CUSTOM.y;
	v_biome = clamp(COLOR, vec4(0.0), vec4(1.0));

	vec3 gp = root + ax * CUSTOM0.x + az * CUSTOM0.y;
	v_ground_world = gp;
	vec2 canonical = grass_card_canonical_offset(gp, grass_gust_origin_world, grass_gust_around_world, grass_gust_across_world);
	v_terrain_uv = grass_card_terrain_uv(canonical, grass_terrain_origin_uv);
	v_shadow_uv = grass_card_shadow_uv(canonical, grass_terrain_origin_uv);
	vec3 ground_up = normalize(cross(az, ax));
	if (dot(ground_up, up) < 0.0) ground_up = -ground_up;
	v_day = grass_card_day_factor(ground_up);
	v_cloud = sample_local_cloud_shadow(gp) * v_day;
	v_atmosphere = sample_ring_atmosphere(ring_atmosphere_camera_world, gp, ring_atmosphere_center_world, v_day);
	v_modifier = grass_modifier_tint(root);
	}
}

void fragment() {
	// Ground-plane pixel footprint from the view-space position derivatives (blade uv is constant per blade).
	float footprint = max(length(dFdx(VERTEX)), length(dFdy(VERTEX)));
	float texel_width = float(textureSize(biome_grass_albedo, 0).x);
	float lod = max(log2(max(footprint * biome_texture_scale * texel_width, 1e-6)) + grass_card_frag_lod_bias, 0.0);
	vec3 ground = grass_card_ground_all_biomes(v_terrain_uv, lod, v_biome);
	vec3 col = grass_card_blade_color(ground, v_r, v_g, v_tuft);
	col = mix(col, v_modifier.rgb, clamp(v_modifier.a, 0.0, 1.0));
	vec3 emission;
	ALBEDO = grass_card_finish_albedo(col, v_day, v_atmosphere, emission);
	EMISSION = emission;
	ROUGHNESS = 1.0;
	vec3 f = v_face_view * (dot(v_face_view, VIEW) >= 0.0 ? 1.0 : -1.0);
	vec3 bent = v_up_view + v_u_view * (v_side * grass_card_normal_out) + f * grass_card_normal_face;
	NORMAL = normalize(mix(v_up_view, bent, smoothstep(0.0, 0.6, v_g)));
	BACKLIGHT = col * grass_card_backlight;
	AO = 1.0 - v_cloud * 0.72;
	AO_LIGHT_AFFECT = 0.0;
}

void light() {
	float direct_visibility;
	DIFFUSE_LIGHT += ring_terrain_direct_diffuse(NORMAL, LIGHT, ATTENUATION, LIGHT_IS_DIRECTIONAL, LIGHT_COLOR, ALBEDO,
		VIEW_MATRIX, v_day, v_cloud, v_shadow_uv, v_ground_world, ring_viewer_world_position, direct_visibility);
	DIFFUSE_LIGHT += LIGHT_COLOR * BACKLIGHT * max(dot(-NORMAL, LIGHT), 0.0) * direct_visibility;
}
