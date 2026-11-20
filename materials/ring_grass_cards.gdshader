shader_type spatial;
// Grass clump cards (variant D, grass-cards plan step S4). One 3-card alpha-scissor mesh per instance
// (RingGrassCardMeshFactory), coloured from the Terrain3D surface albedo at the card root and lit with the terrain's own
// direct-light body, so the card base matches the ground in every light condition.
//
// Instance contract (MultiMesh, custom data + colours enabled):
//   transform : basis X/Z lie in the ground tangent plane (yaw * width scale), basis Y is the card's up axis with lean baked
//               in (height scale); origin = root. The ground normal is recovered as normalize(cross(Z, X)).
//   COLOR     : packed biome weights (desert, forest, tundra, alpine); grass is the remainder.
//   CUSTOM    : x = kind * 4 + atlasVariant + rank * 0.999 (kind 0 card, 1 low upright layer, 3 flower, 4 fern, 5 tall grass, 6 reed (reserved); 2 is retired.
//               Kinds 3-6 are the ground-cover cards of worlds with vegetation.ground_cover: main-lattice cards that take the main-card keep / grow-in /
//               compensation path and read grass_cover_atlas layer kind - 3), y = tuft random,
//               z/w = canonical wind phases (windAround, windAcross).
//   instance uniforms (10 of 16): vegetation_haze_slot, grass_gust_origin_world, grass_gust_around_world, grass_gust_across_world,
//               grass_gust_origin_uv, grass_modifier_layer/_origin_world/_around_world/_across_world,
//               grass_terrain_origin_uv.
// Haze: per instance from the GPU haze atlas when available (ring_vegetation_haze.gdshaderinc), else per vertex.
// Alpha-scissor: ALPHA comes from the atlas mask first (then the dissolve drops whole blades, a density fade); everything after
// it only feeds the colour pass, so the depth prepass reads the mask and the dissolve alone. No shadow casting: set cast_shadow = OFF on the renderer.
render_mode cull_disabled, diffuse_lambert, specular_disabled, skip_vertex_transform;
#include "res://materials/ring_grass_card_common.gdshaderinc"
#include "res://materials/ring_grass_card_color.gdshaderinc"
#include "res://materials/ring_vegetation_haze.gdshaderinc"
#include "res://materials/ring_vegetation_shadow.gdshaderinc"
#include "res://materials/ring_wetness.gdshaderinc"
#include "res://materials/ring_canopy_shade.gdshaderinc"

uniform sampler2D grass_card_atlas : filter_linear_mipmap, repeat_disable;
uniform sampler2DArray grass_cover_atlas : filter_linear_mipmap, repeat_disable; // ground-cover cards (flower, fern, tall grass): R coverage, G height, B random, A petal / seed-head mask
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
// Forest lighting FL-3b (translucency switch on): diffuse transmittance of a blade / reflectance, the leaf model of the trees (RING_LEAF_TRANSMISSION_SHAPE = the spectral shape).
// Plan section 9 Q3: thin blades, T/R about 0.8. The off path keeps grass_card_backlight.
const float GRASS_LEAF_TRANSMISSION_K = 0.8;
// Ground-cover colours (T6 design B2): flower petals by atlas variant, fern leaf tint, tall-grass seed-head straw.
// Petal hue (unit luminance) and its luminance relative to the blade it sits on (meadow ratios: white 4.5x, yellow 3x, violet and pink 2x; user 2026-11-19: flowers about twice as bright), so petals follow the grass pipeline rather than an absolute albedo.
const vec3 GRASS_COVER_PETALS[4] = vec3[4](vec3(1.006, 1.006, 0.920), vec3(1.307, 0.998, 0.109), vec3(1.043, 0.869, 2.173), vec3(2.004, 0.656, 1.457));
const vec4 GRASS_COVER_PETAL_RATIO = vec4(4.5, 3.0, 2.0, 2.0);
const vec3 GRASS_COVER_FERN_TINT = vec3(0.85, 1.0, 0.80);
const vec3 GRASS_COVER_STRAW = vec3(1.106, 1.003, 0.664); // seed-head hue (unit luminance) at 2.5x the blade luminance

varying vec3 v_base;
varying vec3 v_tip;
varying vec3 v_atlas_uv; // xy atlas uv, z = ground-cover id ((kind - 3) * 4 + variant) or -1 for grass and the low layer (a constant, so interpolation leaves it intact)
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
varying vec2 v_wet_dissolve; // x: wet world, per vertex darkening amount (cards only darken, they have no specular); y: fraction of blades kept (grow-in x reach fade), constant per instance (packed: the shader is at the 32 varying limit)
varying vec2 v_canopy; // forest lighting: (blocked, species factor) at the card root, per vertex (ring_canopy_shade.gdshaderinc)
varying flat vec4 v_ground_hs; // per-instance heightfield term of the card root: visibility, weight, unknown, valid (S7b)

void vertex() {
	v_wet_dissolve = vec2(0.0);
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
	bool low = abs(kind - 1.0) < 0.5; // kinds 0 and 1 as before; the cover kinds (3-6) take the main-card path
	bool cover = kind > 2.5;
	float scale;
	float near_width;
	float dissolve;
	if (low) {
		scale = grass_card_low_scale(rank, dist);
		near_width = 1.0;
		dissolve = grass_card_low_dissolve(rank, dist);
	} else {
		float keep = grass_card_keep(dist);
		float grow = rank < keep ? grass_card_grow_in(keep, rank) : 0.0;
		scale = grow < grass_card_min_grow_in ? 0.0 : grass_card_compensation(dist);
		near_width = grass_card_near_width_scale(dist);
		// Size is constant with distance; grow-in and the reach fade thin the blades instead (fragment dissolve).
		dissolve = clamp((grow - grass_card_min_grow_in) / (1.0 - grass_card_min_grow_in), 0.0, 1.0) * grass_card_fade(dist);
	}
	v_wet_dissolve.y = dissolve;
	if (scale <= 0.0 || dissolve <= 0.0) {
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

	float hh = hscale * scale;
	float ww = wscale * scale * near_width * wf;
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
	if (ring_canopy_shade_on()) v_canopy = ring_canopy_sample(gp);
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
	v_atlas_uv = vec3((variant + UV.x) * 0.25, UV.y, cover ? (kind - 3.0) * 4.0 + variant : -1.0);

	v_light.x = grass_card_day_factor(ground_up);
	RingVegetationShadowInstance shadow_instance = ring_vegetation_shadow_instance(RING_VEGETATION_SHADOW_GRASS, INSTANCE_ID);
	v_light.y = ring_vegetation_shadow_cloud_vertex(shadow_instance, gp) * v_light.x;
	RingHeightShadow ground_hs;
	bool ground_hs_valid = ring_vegetation_shadow_ground(shadow_instance, ground_hs);
	v_ground_hs = vec4(ground_hs.visibility, ground_hs.weight, ground_hs.unknown, ground_hs_valid ? 1.0 : 0.0);
	RingHaze haze = ring_vegetation_haze_sample(RING_VEGETATION_HAZE_GRASS, INSTANCE_ID, ring_atmosphere_camera_world, gp, ring_atmosphere_center_world, v_light.x);
	v_haze_T = haze.T;
	v_haze_S = ring_haze_inscatter(haze);
	v_modifier = grass_modifier_tint(root);
	if (ring_wet_params.w > 0.5) v_wet_dissolve.x = smoothstep(0.0, 0.5, ring_wet_sample_up(gp, ground_up).soak) * ring_wet_params.x;
	}
}

void fragment() {
	vec4 m = texture(grass_card_atlas, v_atlas_uv.xy);
	float cover_id = floor(v_atlas_uv.z + 0.5); // ground-cover id (kind - 3) * 4 + variant, or -1
	float cover_layer = floor(cover_id * 0.25);
	float cover_variant = cover_id - cover_layer * 4.0;
	float cover_mask = 0.0;
	if (cover_id > -0.5) {
		// Ground-cover card: its own atlas layer replaces the grass read (the second sampler is only touched here).
		vec4 mc = texture(grass_cover_atlas, vec3(v_atlas_uv.xy, cover_layer));
		m = vec4(mc.rgb, 1.0);
		cover_mask = mc.a;
	}
	ALPHA = m.r;
	if (v_wet_dissolve.y < 0.999) {
		// Density fade (grow-in, reach fade): atlas B at mip 0 is the per-blade (per-plant for ground cover) random, so near cards gain whole
		// blades as they enter instead of changing size. Where the mip-0 tap misses a blade (far, minified) a screen-space dither does the
		// same job. This is before the scissor threshold so the depth prepass sees it. The instance contract / CUSTOM is unchanged.
		vec4 m0 = cover_id > -0.5 ? textureLod(grass_cover_atlas, vec3(v_atlas_uv.xy, cover_layer), 0.0) : textureLod(grass_card_atlas, v_atlas_uv.xy, 0.0);
		float r = m0.r >= 0.5 ? m0.b : fract(52.9829189 * fract(dot(FRAGCOORD.xy, vec2(0.06711056, 0.00583715))));
		if (r >= v_wet_dissolve.y) ALPHA = 0.0;
	}
	ALPHA_SCISSOR_THRESHOLD = grass_card_alpha_cutoff;
	// Far colour (vertex path) is the blend target.
	vec3 col = mix(v_base, v_tip, m.g) * (1.0 + v_shape.w + (m.b - 0.5) * 2.0 * grass_card_color_variation * 0.5);
	if (v_shape.z > 0.002) {
		float lod = grass_card_derivative_lod(v_terrain_uv, float(textureSize(biome_grass_albedo, 0).x));
		vec3 ground = grass_card_ground_all_biomes(v_terrain_uv, lod, v_biome);
		col = mix(col, grass_card_blade_color(ground, m.b, m.g, v_shape.y), v_shape.z);
	}
	if (cover_id > -0.5) {
		// Leaves and stems keep the terrain-derived blade colour; the per-kind constant and the A mask (petals, seed heads) are the only changes.
		// The result then takes the same modifier, wetness, haze and night path as the blade below.
		if (cover_layer < 0.5) {
			int petal = int(clamp(cover_variant, 0.0, 3.0));
			float blade_luma = dot(col, vec3(0.2126, 0.7152, 0.0722));
			vec3 petal_color = min(GRASS_COVER_PETALS[petal] * (blade_luma * GRASS_COVER_PETAL_RATIO[petal]) * (1.0 + 0.08 * (2.0 * v_shape.y - 1.0)), vec3(0.9));
			col = mix(col, petal_color, cover_mask);
		}
		else if (cover_layer < 1.5) col *= GRASS_COVER_FERN_TINT;
		else col = mix(col, min(GRASS_COVER_STRAW * (dot(col, vec3(0.2126, 0.7152, 0.0722)) * 2.5), vec3(0.9)), cover_mask);
	}
	col = mix(col, v_modifier.rgb, clamp(v_modifier.a, 0.0, 1.0));
	col *= 1.0 - 0.55 * 0.4 * min(v_wet_dissolve.x, 1.0); // wet world: blades darken less than soil
	vec3 emission;
	ALBEDO = grass_card_finish_albedo(col, v_light.x, v_haze_T, v_haze_S, emission);
	EMISSION = emission;
	ROUGHNESS = 1.0;
	// Cards have no specular: the engine's environment term ignores render_mode specular_disabled, so it is zeroed here (R1-1a).
	SPECULAR = 0.0;
	// Ground normal at the root, bending toward the card's outward / face normal at the tip.
	vec3 f = v_face_view * (dot(v_face_view, VIEW) >= 0.0 ? 1.0 : -1.0);
	vec3 bent = v_up_view + v_u_view * (v_shape.x * grass_card_normal_out) + f * grass_card_normal_face;
	NORMAL = normalize(mix(v_up_view, bent, smoothstep(0.0, max(grass_card_normal_tip_start, 0.01), m.g)));
	BACKLIGHT = ring_forest_translucency_on() ? vec3(GRASS_LEAF_TRANSMISSION_K) * RING_LEAF_TRANSMISSION_SHAPE : col * grass_card_backlight;
	AO = min(1.0 - v_light.y * 0.72, ring_storm_ambient(ring_storm_level(v_ground_world))); // storm light
	if (ring_canopy_shade_on()) AO *= ring_canopy_sky_visibility(v_canopy); // forest lighting: sky hidden by the canopy above
	AO_LIGHT_AFFECT = 0.0;
	if (ring_shadow_debug_enabled()) {
		// Shadow debug view: only the selected shadow term reaches the screen (see ring_shadow_compose.gdshaderinc).
		ALBEDO = vec3(1.0);
		EMISSION = vec3(0.0);
		AO = 0.0;
		SPECULAR = 0.0;
		METALLIC = 0.0;
		ROUGHNESS = 1.0;
		BACKLIGHT = vec3(0.0);
	}
}

void light() {
	float direct_visibility;
	// The off path is the pre-forest-lighting call verbatim (FL-4: a call inside an if/else compiled to 1-2 LSB different pixels in the real world).
	DIFFUSE_LIGHT += ring_terrain_direct_diffuse(NORMAL, LIGHT, ATTENUATION, LIGHT_IS_DIRECTIONAL, LIGHT_COLOR,
		VIEW_MATRIX, v_light.x, v_light.y, v_light.zw, v_ground_world, ring_viewer_world_position, SCREEN_UV,
		FRAGCOORD.xy, VIEWPORT_SIZE, INV_PROJECTION_MATRIX, RING_SHADOW_CONTACT_GRASS, v_ground_hs, direct_visibility);
	if (ring_canopy_shade_on()) {
		// Forest lighting: the same terms again through the overload that also returns the sun visibility above the canopy (result discarded), then the canopy's transmitted sunlight.
		float canopy_above;
		float direct_visibility_canopy;
		ring_terrain_direct_diffuse(NORMAL, LIGHT, ATTENUATION, LIGHT_IS_DIRECTIONAL, LIGHT_COLOR,
			VIEW_MATRIX, v_light.x, v_light.y, v_light.zw, v_ground_world, ring_viewer_world_position, SCREEN_UV,
			FRAGCOORD.xy, VIEWPORT_SIZE, INV_PROJECTION_MATRIX, RING_SHADOW_CONTACT_GRASS, v_ground_hs, direct_visibility_canopy, canopy_above);
		if (!ring_shadow_debug_enabled()) {
			DIFFUSE_LIGHT += ring_canopy_transmitted(LIGHT_COLOR, LIGHT, NORMAL, ring_canopy_up_view(VIEW_MATRIX), v_canopy, canopy_above);
		}
	}
	if (ring_forest_translucency_on()) {
		// Leaf model of the trees (bi-Lambertian): the lobe follows the blade's face side toward the viewer, so the transmittance is k * s * albedo. Primary sun only, as the trees
		// (user, 2026-11-19: no translucency glow from the night light).
		vec3 face = v_face_view * (dot(v_face_view, VIEW) >= 0.0 ? 1.0 : -1.0);
		float translucency_sun = LIGHT_IS_DIRECTIONAL ? smoothstep(0.90, 0.995, dot(normalize(LIGHT), normalize((VIEW_MATRIX * vec4(sun_direction, 0.0)).xyz))) : 0.0;
		DIFFUSE_LIGHT += (LIGHT_COLOR / PI) * BACKLIGHT * max(-dot(face, LIGHT), 0.0) * direct_visibility * translucency_sun;
	} else {
		// Diffuse translucency from the far side of the blade, scaled like the terrain's diffuse term.
		DIFFUSE_LIGHT += (LIGHT_COLOR / PI) * BACKLIGHT * max(dot(-NORMAL, LIGHT), 0.0) * direct_visibility;
	}
}
