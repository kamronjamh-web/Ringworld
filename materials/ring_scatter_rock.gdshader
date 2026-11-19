shader_type spatial;
// T5 scatter rocks (design B4): boulders, outcrops and cobble clusters. One shader for the three groups; the material sets rock_group and the group's vertex-fetch data textures
// (RingScatterRockMaterial). Rocks are shaded like terrain: the same per-fragment haze, shadow, cloud and macro shadow, wetness and storm light as the Terrain3D surface, so
// they are never a separate look (no per-class switch, no menu row).
//
// Instance contract (MultiMesh, custom data enabled; one carrier mesh per (group, LOD) whose vertex positions are all zero):
//   transform : basis X/Z per yaw, Y = the record's up, uniform scale = Size; origin = root (the embed offset is already applied).
//   CUSTOM    : x = group-local variant (0..31) + 32 * tint666, tint666 = (r6 << 12) | (g6 << 6) | b6 of the record's base tint (an exact integer under 2^24);
//               y = dither coverage (the compute writes it), z = the root's canonical surface height in metres, w = 2 when the row is dithered (tree protocol).
// Vertex data: rock_positions (RGBA32F: xyz unit-space position, w height above the ground contact) and rock_normals (RGBA16F), width = vertex count, height = variant.
// Shadow pass: vertex fetch only; the one fragment-side effect is the dither discard, which runs first and reads nothing but flat varyings.
render_mode diffuse_burley, specular_schlick_ggx, depth_draw_opaque;
#include "res://materials/ring_terrain_lighting.gdshaderinc"
#include "res://materials/ring_terrain_surface_albedo.gdshaderinc"
#include "res://materials/ring_surface_albedo_scale.gdshaderinc"
#include "res://materials/ring_ground_surface.gdshaderinc"
#include "res://materials/ring_wetness.gdshaderinc"

// Names match the Terrain3D surface shader so RegisterSurfaceFollower forwards them.
uniform vec3 ring_atmosphere_camera_world = vec3(0.0);
uniform vec3 ring_atmosphere_center_world = vec3(0.0);
uniform vec3 ring_viewer_world_position = vec3(0.0);
uniform float ring_width_meters = 20000.0;

uniform int rock_group = 0; // 0 boulder, 1 outcrop, 2 cobble
uniform sampler2D rock_positions : filter_nearest, repeat_disable;
uniform sampler2D rock_normals : filter_nearest, repeat_disable;
// 0 normal; 1 haze transmittance T; 2 haze in-scatter; 3 direct visibility (shadow, cloud, macro); 4 albedo after wetness (before night tint and haze);
// 5 strata band index; 6 solid white (coverage); 7 world normal as colour (n * 0.5 + 0.5, unlit); 8 albedo = the record's ground tint v_tint, everything else normal (lit, hazed);
// 9 albedo = constant 0.18, everything else normal; 10 grey of dot(world normal, ring up) * 0.5 + 0.5 (unlit); 11 grey of the AO value (unlit); 12 green where the view-space normal faces the camera, red where it faces away (unlit); 13 direct visibility replacing the diffuse light (no ambient), albedo 0.5; 14 the raw shadow-map ATTENUATION and 15 the raw contact-shadow term the same way. Debug views only: tools/ScatterHazeShadowGpuTest.cs and the T5-10 harness
// (--rock-debug-view=N through tools/ScatterDebugBridge.cs) drive them, the game never sets it.
uniform int rock_debug_view = 0;
// Macro-shadow frame of the batch (set per renderer): canonical around/across metres from rock_frame_origin along the two covectors, plus the macro-shadow uv of that origin.
instance uniform vec3 rock_frame_origin;
instance uniform vec3 rock_frame_around;
instance uniform vec3 rock_frame_across;
instance uniform vec2 rock_macro_uv;

varying vec3 v_world;
varying vec4 v_light;            // day factor, cloud occlusion, macro-shadow uv
varying float v_height;          // absolute canonical height of the fragment (m), for the strata
varying float v_base;            // height above the ground contact in unit space (base blend over 0..0.15)
varying flat vec3 v_tint;        // record base tint, linear
varying flat vec3 v_up;          // ring up at the root
varying flat vec2 v_dither;      // coverage, seed
varying flat float v_bed;        // flat bed tone of a boulder, in [-1, 1]
varying vec3 rock_wet_state;     // fragment to light(): wet gloss, roughness, specular

float rock_dither_noise(vec2 pixel, float seed) {
	return fract(sin(dot(pixel + vec2(seed * 17.0, seed * 43.0), vec2(12.9898, 78.233))) * 43758.5453);
}

// The tree protocol (imported_tree.gdshader apply_tree_dither): positive coverage is an incoming LOD, negative an outgoing one whose complement uses the identical hash.
void rock_apply_dither(vec2 fragment_coordinate) {
	float coverage = v_dither.x;
	if (coverage >= 1.0 || coverage <= -1.0) return;
	float noise = rock_dither_noise(floor(fragment_coordinate), v_dither.y);
	if (coverage >= 0.0) {
		if (noise >= coverage) discard;
	} else if (noise < 1.0 + coverage) discard;
}

void vertex() {
	uint custom_x = uint(INSTANCE_CUSTOM.x + 0.5);
	int variant = int(custom_x & 31u);
	uint tint666 = custom_x >> 5u;
	v_tint = vec3(float((tint666 >> 12u) & 63u), float((tint666 >> 6u) & 63u), float(tint666 & 63u)) * (1.0 / 63.0);
	vec4 p = texelFetch(rock_positions, ivec2(VERTEX_ID, variant), 0);
	VERTEX = p.xyz;
	NORMAL = normalize(texelFetch(rock_normals, ivec2(VERTEX_ID, variant), 0).xyz);
	v_base = p.w;

	vec3 root_world = MODEL_MATRIX[3].xyz;
	vec3 world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	v_world = world;
	// Ring up at the root as the tree shader computes it (curvature term dropped: over 40 m it is under 0.2 mm).
	vec3 radial = root_world - ring_atmosphere_center_world;
	radial.y = 0.0;
	v_up = length(radial) > 0.0001 ? -normalize(radial) : vec3(-1.0, 0.0, 0.0);
	v_height = INSTANCE_CUSTOM.z + dot(world - root_world, v_up);
	v_light.x = ringworld_effective_solar_day(smoothstep(day_start_dot, day_full_dot, dot(v_up, normalize(sun_direction))));
	v_light.y = sample_local_cloud_shadow(world) * v_light.x;
	vec3 delta = root_world - rock_frame_origin;
	v_light.zw = rock_macro_uv + vec2(dot(delta, rock_frame_around) / (6.28318530718 * ring_radius_meters), dot(delta, rock_frame_across) / max(ring_width_meters, 1.0));

	// Dither words: the coverage, and a per-instance seed identical across the LOD batches of one instance (integer hash of the variant/tint word and the root height).
	v_dither = vec2(abs(INSTANCE_CUSTOM.w - 2.0) <= 0.25 ? INSTANCE_CUSTOM.y : 1.0, 0.0);
	uint h = ring_ground_pcg(custom_x ^ ring_ground_pcg(floatBitsToUint(INSTANCE_CUSTOM.z)));
	v_dither.y = float(h & 0xFFFFu) * (1.0 / 65536.0);
	v_bed = ring_ground_tone(float(h >> 16u), uint(ringworld_ground_world.y));
}

vec3 rock_biome_triplanar(sampler2D texture_map, vec3 position, vec3 normal, vec3 blend) {
	vec2 x_uv = position.zy;
	vec2 y_uv = position.xz;
	vec2 z_uv = position.xy;
	x_uv.x *= normal.x < 0.0 ? -1.0 : 1.0;
	y_uv.x *= normal.y < 0.0 ? -1.0 : 1.0;
	z_uv.x *= normal.z >= 0.0 ? -1.0 : 1.0;
	return texture(texture_map, x_uv).rgb * blend.x + texture(texture_map, y_uv).rgb * blend.y + texture(texture_map, z_uv).rgb * blend.z;
}

// Night tint, then the terrain's per-fragment atmosphere call (the same one the Terrain3D surface makes), on a surface colour.
void rock_apply_atmosphere(inout vec3 albedo, out vec3 emission) {
	albedo = ring_terrain_night_tint(albedo, v_light.x);
	ring_terrain_apply_atmosphere(albedo, emission, ring_atmosphere_camera_world, v_world, ring_atmosphere_center_world, v_light.x);
}

void fragment() {
	float strata_footprint = fwidth(v_height); // uniform control flow: before the dither discard and every branch
	rock_apply_dither(FRAGCOORD.xy);
	vec3 world_normal = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
	vec3 blend = pow(abs(world_normal), vec3(8.0));
	blend /= max(blend.x + blend.y + blend.z, 0.0001);
	vec3 rock = rock_biome_triplanar(biome_alpine_albedo, v_world * biome_texture_scale, world_normal, blend) * GROUND_ROCK_TINT;
	bool strata_on = ringworld_ground_world.x > 0.5;
	float band_height = v_height;
	if (rock_group == 1 && strata_on) rock *= ring_ground_strata_factor(v_height, strata_footprint);
	else if (rock_group == 0 && strata_on) rock *= 1.0 + ringworld_ground_look.y * v_bed;
	// Base blend: the foot takes the ground's macro colour and darkens slightly.
	float base_weight = smoothstep(0.0, 0.15, v_base);
	rock = mix(rock, v_tint, 0.7 * (1.0 - base_weight)) * mix(0.85, 1.0, base_weight);

	if (rock_debug_view == 8) rock = v_tint;
	else if (rock_debug_view == 9) rock = vec3(0.18);

	// Wet world: rock porosity 0.5 (T4 D9), gloss, no puddles.
	float roughness = 0.92;
	float specular = 0.18;
	float wet_gloss = 0.0;
	vec3 albedo = rock;
	if (ring_wet_params.w > 0.5) {
		RingWet wet_state = ring_wet_sample(v_world, world_normal, v_up);
		ring_wet_apply(wet_state, 0.5, albedo, roughness, specular);
		wet_gloss = ring_wet_gloss(wet_state);
	}
	albedo = ring_terrain_albedo_scaled(albedo);
	vec3 wet_albedo = albedo;
	vec3 emission;
	rock_apply_atmosphere(albedo, emission);
	ALBEDO = albedo;
	EMISSION = emission;
	ROUGHNESS = roughness;
	SPECULAR = specular;
	rock_wet_state = vec3(wet_gloss, roughness, specular);
	AO = min(1.0 - v_light.y * 0.72, ring_storm_ambient(ring_storm_level(v_world)));
	if (rock_debug_view >= 20 && (rock_debug_view - 20 & 16) != 0) AO = 1.0;
	AO_LIGHT_AFFECT = 0.0;
	if (rock_debug_view != 0 && rock_debug_view < 8 || rock_debug_view == 10 || rock_debug_view >= 11 && rock_debug_view < 16 || rock_debug_view >= 17 && rock_debug_view <= 19) {
		vec3 probe = vec3(1.0);
		vec3 probe_emission;
		rock_apply_atmosphere(probe, probe_emission);
		vec3 shown = vec3(0.0);
		if (rock_debug_view == 1) shown = probe;
		else if (rock_debug_view == 2) shown = probe_emission;
		else if (rock_debug_view == 4) shown = wet_albedo;
		else if (rock_debug_view == 5) shown = vec3((float(int(floor(band_height / max(ringworld_ground_world.x, 0.001))) & 127) + 1.0) / 128.0);
		else if (rock_debug_view == 6) shown = vec3(1.0);
		else if (rock_debug_view == 7) shown = world_normal * 0.5 + 0.5;
		else if (rock_debug_view == 10) shown = vec3(dot(world_normal, v_up) * 0.5 + 0.5);
		else if (rock_debug_view == 11) shown = vec3(AO);
		else if (rock_debug_view == 17) shown = vec3(v_light.x);
		else if (rock_debug_view == 18) shown = vec3(dot(v_up, normalize(sun_direction)) * 0.5 + 0.5);
		else if (rock_debug_view == 19) shown = vec3(max(dot(world_normal, normalize(sun_direction)), 0.0) * 0.5);
		else if (rock_debug_view == 12) shown = NORMAL.z > 0.0 ? vec3(0.0, 0.4, 0.0) : vec3(0.6, 0.0, 0.0);
		ALBEDO = rock_debug_view == 3 ? vec3(1.0) : (rock_debug_view >= 13 && rock_debug_view <= 15 ? vec3(0.5) : vec3(0.0));
		EMISSION = shown;
		AO = 1.0;
		SPECULAR = 0.0;
		METALLIC = 0.0;
		ROUGHNESS = 1.0;
		rock_wet_state = vec3(0.0);
	}
}

void light() {
	float direct_visibility;
	// Contact flags: RING_SHADOW_DEFAULT, the Terrain3D surface's own (rocks are solid ground, not grass: the contact strength is the surface strength).
	// Views 20 + m (m a bit mask, A/B only): 1 shadow-map attenuation forced to 1, 2 contact shadow off, 4 cloud occlusion 0, 8 macro-shadow uv zeroed, 16 AO 1 (fragment()).
	int ab = rock_debug_view >= 20 ? rock_debug_view - 20 : 0;
	vec3 diffuse = ring_terrain_direct_diffuse(NORMAL, LIGHT, (ab & 1) != 0 ? 1.0 : ATTENUATION, LIGHT_IS_DIRECTIONAL, LIGHT_COLOR, VIEW_MATRIX, v_light.x, (ab & 4) != 0 ? 0.0 : v_light.y,
		(ab & 8) != 0 ? vec2(0.0) : v_light.zw, v_world, ring_viewer_world_position, SCREEN_UV, FRAGCOORD.xy, VIEWPORT_SIZE, INV_PROJECTION_MATRIX,
		(ab & 2) != 0 ? RING_SHADOW_NO_CONTACT : RING_SHADOW_DEFAULT, direct_visibility);
	if (rock_debug_view == 16) {
		// no direct light: the lit result is the ambient alone
	} else if (rock_debug_view == 14) {
		DIFFUSE_LIGHT = vec3(ATTENUATION); // the raw shadow-map term
	} else if (rock_debug_view == 15) {
		DIFFUSE_LIGHT = vec3(ring_contact_visibility(FRAGCOORD.xy, VIEWPORT_SIZE, INV_PROJECTION_MATRIX, 1.0, RING_SHADOW_DEFAULT)); // the raw contact-shadow term
	} else if (rock_debug_view == 13) {
		DIFFUSE_LIGHT = vec3(direct_visibility); // replaces the ambient the engine put there: the sun term alone, times the albedo 0.5 the fragment set
	} else if (rock_debug_view == 3) {
		DIFFUSE_LIGHT += vec3(direct_visibility);
	} else {
		DIFFUSE_LIGHT += diffuse;
		// Wet gloss: the same Schlick-GGX direct highlight the terrain adds (ring_wetness.gdshaderinc), under the same visibility.
		if (rock_wet_state.x > 0.0) {
			SPECULAR_LIGHT += ring_wet_direct_specular(NORMAL, LIGHT, VIEW, rock_wet_state.y, rock_wet_state.z, rock_wet_state.x) * LIGHT_COLOR * direct_visibility * SPECULAR_AMOUNT
				* (LIGHT_IS_DIRECTIONAL ? ring_storm_specular_keep(ring_storm_level(v_world)) : 1.0);
		}
	}
}
